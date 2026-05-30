// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.CodeDom.Compiler;

namespace BindingsGeneration
{
    /// <summary>
    /// Bridge for tuple-return helpers that live on <see cref="WrapperEmitter"/> but are also
    /// consumed by the async harness. Implemented by WrapperEmitter; passed into
    /// <see cref="AsyncHarnessEmitter"/> via its constructor so the harness can emit
    /// async wrappers for tuple-returning methods without owning the tuple projection code.
    /// </summary>
    internal interface IAsyncTupleHelpers
    {
        string GetPInvokeTypeForTupleElement(TypeSpec element);
        string GetCSharpTypeForTupleElement(TypeSpec element, bool applyIdiomaticConversion = true);
        string? GetTupleElementMarshalCode(TypeSpec element, string itemName, string resultName, string csharpType);
    }

    /// <summary>
    /// Emits the C# async callback plumbing (TCS, GCHandle, UnmanagedCallersOnly callbacks, error
    /// callbacks) and the Swift @_cdecl/@_silgen_name wrapper body for async methods.
    ///
    /// Originally lived as private helpers on <see cref="WrapperEmitter"/>. Extracted so the
    /// concrete-protocol specialization (CSM) path can emit the same harness for specialized
    /// async overloads. All inputs are explicit: method environment, wrapper+P/Invoke
    /// signatures, typed-throws error model, emission context, and tuple-return bridge.
    /// </summary>
    internal sealed class AsyncHarnessEmitter
    {
        private static readonly TypeProjectionFactory s_projectionFactory = new();

        private readonly MethodEnvironment _env;
        private readonly Signature _wrapperSignature;
        private readonly Signature _pInvokeSignature;
        private readonly bool _useTypedErrorCallback;
        private readonly string? _typedThrowsSwiftErrorType;
        private readonly string? _typedThrowsCSharpErrorType;
        private readonly bool _typedErrorTransfersOwnershipAsync;
        // Frozen-with-memory parity flag: true when the typed error type is
        // `IsFrozenStructProjectedAsClass`. The cleanup needs VWT `Destroy` + `SBW_Free`
        // because the frozen-struct `NewFromPayload` copies into a fresh `NativeMemory`
        // buffer and leaves the wire carrier holding +1 retains. Mutually exclusive with
        // `_typedErrorTransfersOwnershipAsync` — the other ownership-transfer shapes
        // hand the wire buffer to the SafeHandle directly.
        private readonly bool _typedErrorRequiresVwtDestroyAsync;
        // Class-direct parity flag: true when the typed error type is a Swift class.
        // Wire is a +1 retained class pointer (no carrier buffer); on success C#'s
        // `MarshalFromSwift<T>` constructs the SwiftObject taking ownership of the
        // retain, on marshal failure C# calls `Arc.Release(errorPtr)` to balance it.
        // Mutually exclusive with `_typedErrorTransfersOwnershipAsync` and
        // `_typedErrorRequiresVwtDestroyAsync` — there's no buffer to `SBW_Free`.
        private readonly bool _typedErrorIsClassDirectAsync;
        private readonly ModuleEmissionContext _emissionContext;
        private readonly IAsyncTupleHelpers _tupleHelpers;

        // Async callback hoisting for generic types: when PInvokeHelperContext is present,
        // [UnmanagedCallersOnly] callbacks are written to a helper StringWriter and flushed
        // to PInvokeHelperContext.RawCodeBlocks. Null when not in a generic parent type.
        private System.IO.StringWriter? _asyncHelperWriter;
        private CSharpWriter? _asyncHelperCsWriter;

        public AsyncHarnessEmitter(
            MethodEnvironment env,
            Signature wrapperSignature,
            Signature pInvokeSignature,
            bool useTypedErrorCallback,
            string? typedThrowsSwiftErrorType,
            string? typedThrowsCSharpErrorType,
            bool typedErrorTransfersOwnershipAsync,
            bool typedErrorRequiresVwtDestroyAsync,
            bool typedErrorIsClassDirectAsync,
            ModuleEmissionContext emissionContext,
            IAsyncTupleHelpers tupleHelpers)
        {
            _env = env;
            _wrapperSignature = wrapperSignature;
            _pInvokeSignature = pInvokeSignature;
            _useTypedErrorCallback = useTypedErrorCallback;
            _typedThrowsSwiftErrorType = typedThrowsSwiftErrorType;
            _typedThrowsCSharpErrorType = typedThrowsCSharpErrorType;
            _typedErrorTransfersOwnershipAsync = typedErrorTransfersOwnershipAsync;
            _typedErrorRequiresVwtDestroyAsync = typedErrorRequiresVwtDestroyAsync;
            _typedErrorIsClassDirectAsync = typedErrorIsClassDirectAsync;
            _emissionContext = emissionContext;
            _tupleHelpers = tupleHelpers;

            // Phase 4 plain-throws cascade gate — mirrors the resolver in WrapperEmitter.cs.
            // A plain `async throws` method routes through the cascade-dispatch path when the
            // module has registered Error-conforming types; otherwise the existing 3-param
            // stringification fallback applies.
            _useCascadeErrorCallback = !useTypedErrorCallback
                && env.MethodDecl.IsAsync
                && env.MethodDecl.Throws
                && emissionContext.ErrorTypeOrder.Count > 0;
        }

        private readonly bool _useCascadeErrorCallback;

        /// <summary>
        /// Returns the helper class name prefix for referencing hoisted async callbacks.
        /// When async callbacks are hoisted to the PInvokeHelper class (generic parent types),
        /// field/method references must be prefixed with the helper class name.
        /// </summary>
        public string AsyncCallbackPrefix =>
            _env.PInvokeHelperContext != null ? $"{_env.PInvokeHelperContext.HelperClassName}." : "";

        /// <summary>
        /// Visibility for async fields/P/Invokes hoisted to the helper class.
        /// Members accessed from outside the helper class need <c>internal</c>;
        /// inline members (emitted inside the same generic class) use <c>private</c>.
        /// </summary>
        public string AsyncFieldVisibility =>
            _env.PInvokeHelperContext != null ? "internal" : "private";

        /// <summary>
        /// Flushes the async helper writer to PInvokeHelperContext.RawCodeBlocks.
        /// Called at each exit point of EmitAsyncWrapper when callbacks were redirected.
        /// </summary>
        private void FlushAsyncHelperWriter()
        {
            if (_asyncHelperWriter != null && _env.PInvokeHelperContext != null)
            {
                _asyncHelperCsWriter!.Flush();
                var content = _asyncHelperWriter.ToString();
                if (!string.IsNullOrWhiteSpace(content))
                    _env.PInvokeHelperContext.RawCodeBlocks.Add(content);
                _asyncHelperWriter = null;
                _asyncHelperCsWriter = null;
            }
        }

        /// <summary>
        /// Returns generic params string containing only method-own generics (excluding parent-type generics).
        /// Used for async extension wrappers where parent-type generics come from the extension scope.
        /// </summary>
        public static string BuildMethodOwnGenericParams(MethodDecl methodDecl)
        {
            var parentParams = methodDecl.ParentDecl is TypeDecl td && td.IsGeneric
                ? new HashSet<string>(td.GenericParameters.Select(p => p.TypeName))
                : new HashSet<string>();
            var ownParams = methodDecl.GenericParameters
                .Where(p => !parentParams.Contains(p.TypeName))
                .Select(p => p.SugaredTypeName)
                .ToList();
            return ownParams.Count > 0 ? $"<{string.Join(", ", ownParams)}>" : "";
        }

        /// <summary>
        /// Emits a wrapper for Swift async method.
        /// </summary>
        /// <param name="csWriter">The IndentedTextWriter instance.</param>
        public void EmitAsyncWrapper(CSharpWriter csWriter)
        {
            if (!_env.MethodDecl.IsAsync)
                return;

            // For generic parent types, [UnmanagedCallersOnly] callbacks must be hoisted to
            // the non-generic PInvokeHelper class to avoid CS7042. Redirect callback output
            // to a StringWriter that gets added to PInvokeHelperContext.RawCodeBlocks.
            var callbackWriter = csWriter;
            if (_env.PInvokeHelperContext != null)
            {
                var helperStringWriter = new System.IO.StringWriter();
                callbackWriter = new CSharpWriter(helperStringWriter) { Indent = 0 };
                // Store the helper writer so we can flush it at the end
                _asyncHelperWriter = helperStringWriter;
                _asyncHelperCsWriter = callbackWriter;
            }

            // Emit SBW_CancelTask P/Invoke once per C# type (for CancellationToken support)
            var moduleDecl = _env.MethodDecl.ModuleDecl ?? throw new ArgumentNullException(nameof(_env.MethodDecl.ModuleDecl));
            var moduleLibPath = _env.TypeDatabase.GetLibraryPath(moduleDecl.Name);
            var wrapperLibPath = _env.TypeDatabase.AsyncLibraryName ?? moduleLibPath;
            var cancelSymbolName = CancellationTaskEmitter.GetCancelSymbolName(moduleDecl.Name);
            var typeKey = (_env.ParentDecl as TypeDecl)?.SwiftTypeName.ModuleQualifiedName ?? moduleDecl.Name;
            if (!CancellationTaskEmitter.HasCancelPInvokeForType(typeKey, _emissionContext))
            {
                CancellationTaskEmitter.MarkCancelPInvokeEmittedForType(typeKey, _emissionContext);
                // SBW_CancelTask P/Invoke: hoist to helper for generic types, emit inline otherwise
                var cancelWriter = _env.PInvokeHelperContext != null ? callbackWriter : csWriter;
                cancelWriter.WriteLine("[global::System.Runtime.InteropServices.UnmanagedCallConv(CallConvs = new global::System.Type[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]");
                cancelWriter.WriteLines($"""
                    [global::System.Runtime.InteropServices.LibraryImport("{wrapperLibPath}", EntryPoint = "{cancelSymbolName}")]
                    {AsyncFieldVisibility} static partial void SBW_CancelTask(long taskId);

                    """);
            }

            var returnType = _env.MethodDecl.CSSignature.First();
            var voidReturn = returnType.SwiftTypeSpec.IsEmptyTuple;
            var genericContext = _env.ParentDecl is TypeDecl parentType
                ? GenericContext.FromMethodInType(_env.MethodDecl, parentType)
                : GenericContext.FromMethod(_env.MethodDecl);
            var isTupleReturn = _env.TupleHandler.IsTuple(returnType.SwiftTypeSpec) &&
                                (_env.TupleHandler.IsSupportedTuple((TupleTypeSpec)returnType.SwiftTypeSpec) ||
                                 _env.TupleHandler.IsSupportedTuple((TupleTypeSpec)returnType.SwiftTypeSpec, genericContext));

            var callbackFieldName = NameProvider.GetAsyncCallbackFieldName(_env.MethodDecl);
            var callbackMethodName = NameProvider.GetAsyncCallbackMethodName(_env.MethodDecl);
            var errorCallbackFieldName = NameProvider.GetAsyncErrorCallbackFieldName(_env.MethodDecl);
            var errorCallbackMethodName = NameProvider.GetAsyncErrorCallbackMethodName(_env.MethodDecl);

            // For tuple returns, we need to marshal each element individually
            if (isTupleReturn)
            {
                EmitAsyncWrapperForTuple(callbackWriter, returnType, callbackFieldName, callbackMethodName, errorCallbackFieldName, errorCallbackMethodName);
                FlushAsyncHelperWriter();
                return;
            }

            // Detect String return - requires UTF-8 unmarshalling from SBW_Utf8Slice
            bool isStringReturn = !voidReturn && returnType.SwiftTypeSpec.ToString() == "Swift.String";
            if (isStringReturn)
            {
                EmitAsyncWrapperForString(callbackWriter, callbackFieldName, callbackMethodName, errorCallbackFieldName, errorCallbackMethodName);
                FlushAsyncHelperWriter();
                return;
            }

            // Detect Array<String> return - requires flat buffer unmarshalling
            bool isArrayStringReturn = !voidReturn && IsArrayOfString(returnType.SwiftTypeSpec);
            if (isArrayStringReturn)
            {
                EmitAsyncWrapperForArrayString(callbackWriter, callbackFieldName, callbackMethodName, errorCallbackFieldName, errorCallbackMethodName);
                FlushAsyncHelperWriter();
                return;
            }

            // Detect collection returns (Array, Dictionary, Set) — these pass through OpaquePointer
            // on the Swift side (same as complex types) but need MarshalFromSwift with the runtime
            // container type (e.g., SwiftArray<int>), not the public type (IReadOnlyList<int>).
            //
            // ObjC-container-bridge variant (e.g., `[URL]`, `Set<URL>`, `[String: URL]`): the Swift
            // wrapper uses the same nullable-pointer carrier as the optional path — store a +1
            // retained NSArray / NSDictionary / NSSet pointer (via `as AnyObject`) and let C# read
            // it as an IntPtr. Without this branch, `MarshalFromSwift<SwiftArray<NSUrl>>(...)` would
            // try to revive Swift's `_ContiguousArrayStorage<URL>` bits as a managed handle, and
            // the conversion expression (`ArrayFromHandleFunc<NSUrl>(_collection, ...)`) would mismatch
            // (CS1503: SwiftArray<NSUrl> vs IntPtr).
            if (!voidReturn && TryGetCollectionAsyncInfo(returnType.SwiftTypeSpec,
                out var runtimeType, out var conversionExpr, out var collectionUsesObjCBridge))
            {
                EmitAsyncWrapperForCollection(callbackWriter, callbackFieldName, callbackMethodName,
                    errorCallbackFieldName, errorCallbackMethodName, runtimeType, conversionExpr,
                    collectionUsesObjCBridge);
                FlushAsyncHelperWriter();
                return;
            }

            // Detect complex type returns (classes, enums, structs) that need OpaquePointer marshalling
            // These types can't be passed directly through @convention(c) callbacks
            var returnTypeName = returnType.SwiftTypeSpec.ToString();
            bool isComplexTypeReturn = !voidReturn && !returnType.IsGeneric && !IsSwiftPrimitive(returnTypeName);
            if (isComplexTypeReturn)
            {
                _env.TypeDatabase.TryGetTypeRecord(returnType.SwiftTypeSpec, out var complexTypeRecord);
                bool isClassType = complexTypeRecord?.Kind == TypeRecordKind.Class;
                // ObjC-bridgeable value types (Foundation.URL → NSUrl, URLRequest → NSUrlRequest,
                // Decimal → NSDecimalNumber): Swift wrapper passes a +1 retained NS pointer via
                // `as AnyObject`, mirroring the class-type ABI. Without this branch the dispatcher
                // falls into newFromPayloadTakesOwnership and emits SwiftObjectHelper<NSUrl> — but
                // NSUrl does not implement ISwiftObject (CS0311). Pairs with the matching gate in
                // EmitAsync that emits the class-style passRetained Swift code.
                bool isObjCBridgeableValue = !isClassType && complexTypeRecord != null
                    && MarshallingHelpers.IsObjCBridgeable(complexTypeRecord)
                    && (complexTypeRecord.Kind == TypeRecordKind.Struct
                        || complexTypeRecord.Kind == TypeRecordKind.Enum);
                // Optional<ClassType>: uses nullable pointer ABI — same buffer layout as class
                // (retained pointer or zero for nil) but needs null check on C# side.
                bool isOptionalClassType = !isClassType && !isObjCBridgeableValue &&
                    CdeclParamMapper.IsOptionalWithReferenceInner(returnType.SwiftTypeSpec, _env.TypeDatabase);
                // Optional<Container<ObjCBridgeable>>: Swift wrapper stores +1 retained NS-collection
                // pointer or 0 for nil via `as AnyObject` — pointer-bit carrier, no initializeMemory,
                // so no VWT Destroy needed. Mirrors the Swift-side branch selection in EmitAsync.
                bool isOptionalObjCContainer = !isClassType && !isObjCBridgeableValue && !isOptionalClassType &&
                    IsOptionalObjCBridgeContainerReturn(returnType.SwiftTypeSpec);
                // ObjCBridged requires class type — the GetNSObject path reads _retainedObjPtr
                // which is only declared when isClassType is true.
                // ObjCBridgeable value types also use the GetNSObject path — extend the gate.
                bool isComplexObjCBridged = (isClassType && complexTypeRecord != null && MarshallingHelpers.IsObjCBridged(complexTypeRecord))
                    || isObjCBridgeableValue;
                // Types projected as C# class with opaque payload (SwiftSafeHandle) must VWT-copy
                // the Swift-allocated carrier into a C#-allocated buffer before NewFromPayload wraps
                // it — otherwise the later NativeMemory.Free in SwiftSafeHandle.ReleaseHandle would
                // run against a Swift UnsafeMutableRawPointer.allocate, mismatching allocators.
                // Matches the sync path's "ownership transferred" predicate in MethodMarshalPlanBuilder
                // (isNonFrozenStruct || isComplexEnum). RequiresMemoryManagement is not set on
                // non-frozen structs by the parser (only on frozen structs containing ref types),
                // so we classify purely by kind + frozen/simple flags here.
                //
                // `carrierNeedsDestroy` is the broader set: Swift always initializes the carrier
                // with +1 on internal refs via `initializeMemory(as:repeating:)`, so any type with
                // non-trivial value witnesses (frozen-with-memory, non-frozen struct, complex enum,
                // or Optional wrapping any of those) must VWT-Destroy the carrier before SBW_Free —
                // otherwise the carrier's internal refs leak.
                bool cbTakesOwnership = false;
                bool carrierNeedsDestroy = false;
                if (!isClassType && !isObjCBridgeableValue && !isOptionalClassType && !isOptionalObjCContainer && complexTypeRecord != null)
                {
                    bool isNonFrozenStruct = complexTypeRecord.Kind == TypeRecordKind.Struct
                        && !MarshallingHelpers.IsTypeFrozen(complexTypeRecord);
                    bool isComplexEnum = complexTypeRecord.Kind == TypeRecordKind.Enum
                        && !complexTypeRecord.Flags.HasFlag(TypeRecordFlags.SimpleEnum);
                    bool isFrozenAsClass = MarshallingHelpers.IsFrozenStructProjectedAsClass(complexTypeRecord);
                    cbTakesOwnership = isNonFrozenStruct || isComplexEnum;
                    carrierNeedsDestroy = isNonFrozenStruct || isComplexEnum || isFrozenAsClass;
                }
                // Optional<value-type> plain path (SwiftOptional<T>.ToNullable): Swift-side
                // initializeMemory runs Optional<T>'s copy witness, so for .some the embedded
                // non-trivial payload holds its own +1. Widen carrierNeedsDestroy when the inner
                // type's VWT is non-trivial — SwiftOptional<T>'s NewFromPayload performs its own
                // InitializeWithCopy into a managed buffer, so the carrier's +1 must be released.
                if (!carrierNeedsDestroy && !isClassType && !isObjCBridgeableValue && !isOptionalClassType && !isOptionalObjCContainer
                    && WrapperValidation.IsOptionalType(returnType.SwiftTypeSpec))
                {
                    var innerSpec = MarshallingHelpers.UnwrapOptionalTypeSpec(returnType.SwiftTypeSpec);
                    if (innerSpec != null && _env.TypeDatabase.TryGetTypeRecord(innerSpec, out var innerRecord))
                    {
                        bool innerIsFrozenAsClass = MarshallingHelpers.IsFrozenStructProjectedAsClass(innerRecord);
                        bool innerIsNonFrozenStruct = innerRecord.Kind == TypeRecordKind.Struct
                            && !MarshallingHelpers.IsTypeFrozen(innerRecord);
                        bool innerIsComplexEnum = innerRecord.Kind == TypeRecordKind.Enum
                            && !innerRecord.Flags.HasFlag(TypeRecordFlags.SimpleEnum);
                        carrierNeedsDestroy = innerIsFrozenAsClass || innerIsNonFrozenStruct || innerIsComplexEnum;
                    }
                }
                // Optional<ObjC-reference-inner>: Swift writes a +1 retained ObjC pointer (or 0 for nil)
                // into the 8-byte carrier — but the C# read for Optional<UIImage>, Optional<URL>, etc.
                // can't go through MarshalFromSwift<T?>: NSObject subclasses don't implement ISwiftObject.
                // Pre-compute the GetNSObject bridge call here so EmitAsyncWrapperForComplexType can
                // emit the correct null-check + GetNSObject + DangerousRelease pattern, mirroring the
                // non-optional ObjCBridged/ObjCBridgeableValue path.
                string? optionalRefBridgeCall = null;
                if (isOptionalClassType)
                {
                    var innerSpec = MarshallingHelpers.UnwrapOptionalTypeSpec(returnType.SwiftTypeSpec);
                    if (innerSpec != null && _env.TypeDatabase.TryGetTypeRecord(innerSpec, out var innerOptRecord))
                    {
                        bool innerIsObjCBridgedClass = innerOptRecord.Kind == TypeRecordKind.Class
                            && MarshallingHelpers.IsObjCBridged(innerOptRecord);
                        bool innerIsObjCBridgeableValue = (innerOptRecord.Kind == TypeRecordKind.Struct
                                || innerOptRecord.Kind == TypeRecordKind.Enum)
                            && MarshallingHelpers.IsObjCBridgeable(innerOptRecord);
                        if (innerIsObjCBridgedClass)
                        {
                            optionalRefBridgeCall = MarshallingHelpers.FormatObjCBridgeCall(
                                innerOptRecord.CSharpTypeName.FullyQualifiedName, "_retainedObjPtr");
                        }
                        else if (innerIsObjCBridgeableValue && innerOptRecord.NativeTypeName != null)
                        {
                            optionalRefBridgeCall = MarshallingHelpers.FormatObjCBridgeCall(
                                innerOptRecord.NativeTypeName.FullyQualifiedName, "_retainedObjPtr");
                        }
                    }
                }
                // ObjCBridgeable value types use the class-style ABI in EmitAsyncWrapperForComplexType:
                // _retainedObjPtr is read from the carrier, then the isObjCBridged branch emits
                // GetNSObject<T>(...). Pass `isClassType || isObjCBridgeableValue` so the readObjPtr
                // gate fires; isComplexObjCBridged (already set above) routes to the GetNSObject branch.
                EmitAsyncWrapperForComplexType(callbackWriter, callbackFieldName, callbackMethodName, errorCallbackFieldName, errorCallbackMethodName, isClassType || isObjCBridgeableValue, isComplexObjCBridged, cbTakesOwnership, isOptionalClassType, carrierNeedsDestroy, optionalRefBridgeCall);
                FlushAsyncHelperWriter();
                return;
            }

            // Non-tuple return handling
            TypeRecord returnTypeRecord = _env.TypeDatabase.GetTypeRecordOrThrow(returnType.SwiftTypeSpec);
            var isObjCBridged = !voidReturn && MarshallingHelpers.IsObjCBridged(returnTypeRecord);

            // Convertible types (SwiftString -> string, SwiftArray -> IReadOnlyList, etc.) are already
            // properly marshalled and don't need InitWithCopy. Using SwiftObjectHelper with their projected
            // types (string, IReadOnlyList<T>) would fail since those types don't implement ISwiftObject.
            var isConvertibleType = MarshallingHelpers.IsConvertibleType(returnType.SwiftTypeSpec);

            // ObjC bridged types and convertible types don't need InitWithCopy
            var requiresInitWithCopy = !voidReturn && !isObjCBridged && !isConvertibleType && (MarshallingHelpers.RequiresMemoryManagement(returnTypeRecord) || returnType.IsGeneric);

            // For ObjC bridged types, the rawResult is the ObjC object pointer directly
            // For Swift types, we need to marshal from Swift memory layout
            // For class types, rawResult is IntPtr (the raw object pointer) — pass directly
            // For non-class types, rawResult is a value type — take its address
            bool isClassReturn = !voidReturn && returnTypeRecord.Kind == TypeRecordKind.Class;
            string marshalResultCode;
            if (isObjCBridged)
            {
                // ObjC types: rawResult is the ObjC object pointer, wrap with appropriate bridge call.
                // Swift passed +1 via passRetained or calling convention. GetNSObject adds +1.
                // DangerousRelease() balances the extra retain.
                var bridgeCall = MarshallingHelpers.FormatObjCBridgeCall(_wrapperSignature.ReturnType, "rawResult");
                if (MarshallingHelpers.IsCoreFoundationType(_wrapperSignature.ReturnType))
                    marshalResultCode = $"var result = {MarshallingHelpers.FormatObjCBridgeCall(_wrapperSignature.ReturnType, "rawResult", ownsReference: true)};";
                else
                    marshalResultCode = $"var result = {bridgeCall};\n                                result?.DangerousRelease();";
            }
            else if (isClassReturn)
            {
                // Class types: rawResult is IntPtr (the raw Swift object pointer), pass directly
                marshalResultCode = $"var result = SwiftMarshal.MarshalFromSwift<{_wrapperSignature.ReturnType}>(rawResult);";
            }
            else
            {
                marshalResultCode = $"var result = SwiftMarshal.MarshalFromSwift<{_wrapperSignature.ReturnType}>(new IntPtr(&rawResult));";
            }

            var text = $$"""
                        {{AsyncFieldVisibility}} static unsafe delegate* unmanaged[Cdecl]<{{(voidReturn ? "" : $"{_pInvokeSignature.ReturnType}, ")}}IntPtr, void> {{callbackFieldName}} = &{{callbackMethodName}};
                        [UnmanagedCallersOnly(CallConvs = new[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]
                        private static unsafe void {{callbackMethodName}}({{(voidReturn ? "" : $"{_pInvokeSignature.ReturnType} rawResult, ")}}IntPtr task)
                        {
                            GCHandle handle = GCHandle.FromIntPtr(task);
                            try
                            {
                                {{(voidReturn ? "" : marshalResultCode)}}
                                {{(requiresInitWithCopy ? $"var metadata = SwiftObjectHelper<{_wrapperSignature.ReturnType}>.GetTypeMetadata();" : "")}}
                                {{(requiresInitWithCopy ? $"Span<byte> payloadSpan = stackalloc byte[(int)metadata.Size];" : "")}}
                                {{(requiresInitWithCopy ? $"IntPtr payload = (IntPtr)Unsafe.AsPointer(ref MemoryMarshal.GetReference(payloadSpan));" : "")}}
                                {{(requiresInitWithCopy ? $"SwiftMarshal.MarshalToSwift(result, ref payloadSpan);" : "")}}
                                // Handle both cases: direct TCS or object[] holder (with copy buffer pointers)
                                if (handle.Target is object[] holder && holder[0] is TaskCompletionSource{{(voidReturn ? "" : $"<{_wrapperSignature.ReturnType}>")}} holderTcs)
                                {
                                    // Free copy buffer memory for non-frozen params and release retained self
                {{BuildHolderCleanupCode("holder", "                    ")}}
                                    holderTcs.TrySetResult({{(voidReturn ? "" : "result")}});
                                }
                                else if (handle.Target is TaskCompletionSource{{(voidReturn ? "" : $"<{_wrapperSignature.ReturnType}>")}} directTcs)
                                {
                                    directTcs.TrySetResult({{(voidReturn ? "" : "result")}});
                                }
                            }
                            finally
                            {
                                handle.Free();
                            }
                        }

                        {{BuildErrorCallbackBlock(errorCallbackFieldName, errorCallbackMethodName, voidReturn ? "" : $"<{_wrapperSignature.ReturnType}>")}}
                """;
            callbackWriter.WriteLine(text);
            FlushAsyncHelperWriter();
        }

        /// <summary>
        /// Emits async wrapper for methods returning tuples.
        /// Handles marshalling each tuple element individually.
        /// For @convention(c) compatibility, tuple elements are flattened into separate callback parameters.
        /// </summary>
        private void EmitAsyncWrapperForTuple(CSharpWriter csWriter, ArgumentDecl returnType, string callbackFieldName, string callbackMethodName, string errorCallbackFieldName, string errorCallbackMethodName)
        {
            var tupleTypeSpec = (TupleTypeSpec)returnType.SwiftTypeSpec;
            var elements = tupleTypeSpec.Elements;

            // Build flattened callback parameter lists
            var delegateParams = new List<string>();  // For delegate* signature
            var methodParams = new List<string>();    // For method signature
            var marshalLines = new List<string>();
            var resultElements = new List<string>();

            for (int i = 0; i < elements.Count; i++)
            {
                var element = elements[i];
                var rawParamName = $"rawItem{i}";
                var resultName = $"item{i}";
                var pInvokeType = _tupleHelpers.GetPInvokeTypeForTupleElement(element);
                var csharpType = _tupleHelpers.GetCSharpTypeForTupleElement(element);

                delegateParams.Add(pInvokeType);
                methodParams.Add($"{pInvokeType} {rawParamName}");

                // Determine how to marshal this element
                var marshalCode = _tupleHelpers.GetTupleElementMarshalCode(element, rawParamName, resultName, csharpType);
                if (marshalCode != null)
                {
                    marshalLines.Add(marshalCode);
                }

                // Build the result element (with label if present)
                if (!string.IsNullOrEmpty(element.TypeLabel))
                {
                    resultElements.Add($"{element.TypeLabel}: {resultName}");
                }
                else
                {
                    resultElements.Add(resultName);
                }
            }

            var delegateTypeParams = string.Join(", ", delegateParams) + ", IntPtr, void";
            var methodParamList = string.Join(", ", methodParams) + ", IntPtr task";
            var marshalResultCode = string.Join("\n                    ", marshalLines);
            var tupleConstruction = $"var result = ({string.Join(", ", resultElements)});";

            var text = $$"""
                        {{AsyncFieldVisibility}} static unsafe delegate* unmanaged[Cdecl]<{{delegateTypeParams}}> {{callbackFieldName}} = &{{callbackMethodName}};
                        [UnmanagedCallersOnly(CallConvs = new[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]
                        private static unsafe void {{callbackMethodName}}({{methodParamList}})
                        {
                            GCHandle handle = GCHandle.FromIntPtr(task);
                            try
                            {
                                {{marshalResultCode}}
                                {{tupleConstruction}}
                                // Handle both cases: direct TCS or object[] holder (with copy buffer pointers)
                                if (handle.Target is object[] holder && holder[0] is TaskCompletionSource<{{_wrapperSignature.ReturnType}}> holderTcs)
                                {
                                    // Free copy buffer memory for non-frozen params and release retained self
                {{BuildHolderCleanupCode("holder", "                    ")}}
                                    holderTcs.TrySetResult(result);
                                }
                                else if (handle.Target is TaskCompletionSource<{{_wrapperSignature.ReturnType}}> directTcs)
                                {
                                    directTcs.TrySetResult(result);
                                }
                            }
                            finally
                            {
                                handle.Free();
                            }
                        }

                        {{BuildErrorCallbackBlock(errorCallbackFieldName, errorCallbackMethodName, $"<{_wrapperSignature.ReturnType}>")}}
                """;
            csWriter.WriteLine(text);
        }

        /// <summary>
        /// Emits async wrapper for methods returning String.
        /// Uses SBW_Utf8Slice for @convention(c) compatibility.
        /// Swift allocates UTF-8 buffer, C# copies via Marshal.PtrToStringUTF8 and frees via SBW_Free.
        /// </summary>
        private void EmitAsyncWrapperForString(CSharpWriter csWriter, string callbackFieldName, string callbackMethodName, string errorCallbackFieldName, string errorCallbackMethodName)
        {
            var freePInvokeDecl = GetFreePInvokeDeclIfNeeded();

            var text = $$"""
                        {{freePInvokeDecl}}{{AsyncFieldVisibility}} static unsafe delegate* unmanaged[Cdecl]<IntPtr, nint, IntPtr, void> {{callbackFieldName}} = &{{callbackMethodName}};
                        [UnmanagedCallersOnly(CallConvs = new[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]
                        private static unsafe void {{callbackMethodName}}(IntPtr slicePtr, nint sliceLen, IntPtr task)
                        {
                            GCHandle handle = GCHandle.FromIntPtr(task);
                            try
                            {
                                // Unmarshal UTF-8 to string
                                string result;
                                if (sliceLen == 0)
                                {
                                    result = string.Empty;
                                }
                                else
                                {
                                    result = global::System.Runtime.InteropServices.Marshal.PtrToStringUTF8(slicePtr, (int)sliceLen)!;
                                }

                                // Handle both cases: direct TCS or object[] holder (with copy buffer pointers)
                                if (handle.Target is object[] holder && holder[0] is TaskCompletionSource<{{_wrapperSignature.ReturnType}}> holderTcs)
                                {
                                    // Free copy buffer memory for non-frozen params and release retained self
                {{BuildHolderCleanupCode("holder", "                    ")}}
                                    holderTcs.TrySetResult(result);
                                }
                                else if (handle.Target is TaskCompletionSource<{{_wrapperSignature.ReturnType}}> directTcs)
                                {
                                    directTcs.TrySetResult(result);
                                }
                            }
                            finally
                            {
                                // Always free Swift-allocated memory (even empty strings allocate 1 byte)
                                SBW_Free(slicePtr);
                                handle.Free();
                            }
                        }

                        {{BuildErrorCallbackBlock(errorCallbackFieldName, errorCallbackMethodName, $"<{_wrapperSignature.ReturnType}>")}}
                """;
            csWriter.WriteLine(text);
        }

        /// <summary>
        /// Emits async wrapper for methods returning Array&lt;String&gt;.
        /// Deserializes from flat buffer format: [count][lengths...][data...].
        /// Returns IReadOnlyList&lt;string&gt; for idiomatic C# usage.
        /// </summary>
        private void EmitAsyncWrapperForArrayString(CSharpWriter csWriter, string callbackFieldName, string callbackMethodName, string errorCallbackFieldName, string errorCallbackMethodName)
        {
            var freePInvokeDecl = GetFreePInvokeDeclIfNeeded();

            // The wrapper return type is IReadOnlyList<string> (matches non-async Array<String> return type with WU2 element conversion)
            var text = $$"""
                        {{freePInvokeDecl}}{{AsyncFieldVisibility}} static unsafe delegate* unmanaged[Cdecl]<IntPtr, nint, IntPtr, void> {{callbackFieldName}} = &{{callbackMethodName}};
                        [UnmanagedCallersOnly(CallConvs = new[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]
                        private static unsafe void {{callbackMethodName}}(IntPtr bufferPtr, nint bufferLen, IntPtr task)
                        {
                            GCHandle handle = GCHandle.FromIntPtr(task);
                            System.Exception? deserializationError = null;
                            System.Collections.Generic.List<string>? result = null;

                            try
                            {
                                // Deserialize Array<String> from flat buffer into string instances
                                // Buffer format: [count: Int64][len0: Int64]...[lenN-1: Int64][str0 bytes]...[strN-1 bytes]
                                if (bufferLen <= sizeof(long))
                                {
                                    // Empty array or just count field
                                    result = new System.Collections.Generic.List<string>();
                                }
                                else
                                {
                                    long count = *(long*)bufferPtr;

                                    // Validate count is in valid range for int cast
                                    if (count < 0 || count > int.MaxValue)
                                        throw new InvalidOperationException($"Invalid array count in async callback buffer: {count}");

                                    if (count == 0)
                                    {
                                        result = new System.Collections.Generic.List<string>();
                                    }
                                    else
                                    {
                                        // Calculate and validate header size (count already validated <= int.MaxValue)
                                        int headerSize = sizeof(long) * (1 + (int)count);
                                        if (headerSize > bufferLen)
                                            throw new InvalidOperationException($"Buffer too small for array header: need {headerSize}, have {bufferLen}");

                                        // Read lengths from header
                                        long* lengthsPtr = (long*)bufferPtr + 1;

                                        // Validate all lengths are in valid range and total doesn't exceed buffer
                                        long totalDataLen = 0;
                                        for (int i = 0; i < count; i++)
                                        {
                                            long len = lengthsPtr[i];
                                            if (len < 0 || len > int.MaxValue)
                                                throw new InvalidOperationException($"Invalid string length at index {i}: {len}");
                                            totalDataLen += len;
                                        }
                                        if (headerSize + totalDataLen > bufferLen)
                                            throw new InvalidOperationException($"Buffer too small for array data: need {headerSize + totalDataLen}, have {bufferLen}");

                                        // Read strings from buffer (casts are safe after validation)
                                        result = new System.Collections.Generic.List<string>((int)count);
                                        int dataOffset = headerSize;
                                        for (int i = 0; i < count; i++)
                                        {
                                            int strLen = (int)lengthsPtr[i];
                                            string s = strLen == 0
                                                ? string.Empty
                                                : global::System.Runtime.InteropServices.Marshal.PtrToStringUTF8(bufferPtr + dataOffset, strLen)!;
                                            result.Add(s);
                                            dataOffset += strLen;
                                        }
                                    }
                                }
                            }
                            catch (System.Exception ex)
                            {
                                // Capture deserialization errors to report via TCS (can't throw from UnmanagedCallersOnly)
                                deserializationError = ex;
                            }

                            try
                            {
                                // Handle both cases: direct TCS or object[] holder (with copy buffer pointers)
                                if (handle.Target is object[] holder && holder[0] is TaskCompletionSource<{{_wrapperSignature.ReturnType}}> holderTcs)
                                {
                                    // Free copy buffer memory for non-frozen params and release retained self
                {{BuildHolderCleanupCode("holder", "                    ")}}
                                    if (deserializationError != null)
                                        holderTcs.TrySetException(deserializationError);
                                    else
                                        holderTcs.TrySetResult(result!);
                                }
                                else if (handle.Target is TaskCompletionSource<{{_wrapperSignature.ReturnType}}> directTcs)
                                {
                                    if (deserializationError != null)
                                        directTcs.TrySetException(deserializationError);
                                    else
                                        directTcs.TrySetResult(result!);
                                }
                            }
                            finally
                            {
                                // Always free Swift-allocated memory (even empty arrays allocate 1 byte)
                                SBW_Free(bufferPtr);
                                handle.Free();
                            }
                        }

                        {{BuildErrorCallbackBlock(errorCallbackFieldName, errorCallbackMethodName, $"<{_wrapperSignature.ReturnType}>")}}
                """;
            csWriter.WriteLine(text);
        }

        /// <summary>
        /// Emits async wrapper for methods returning complex types (classes, enums, structs).
        /// These types can't be passed directly through @convention(c) callbacks, so Swift
        /// allocates memory, stores the result, and passes an OpaquePointer.
        /// C# receives IntPtr, reads the value, and frees the memory.
        /// </summary>
        private void EmitAsyncWrapperForComplexType(CSharpWriter csWriter, string callbackFieldName, string callbackMethodName, string errorCallbackFieldName, string errorCallbackMethodName, bool isClassType, bool isObjCBridged = false, bool newFromPayloadTakesOwnership = false, bool isOptionalClass = false, bool carrierNeedsDestroy = false, string? optionalRefBridgeCall = null)
        {
            var freePInvokeDecl = GetFreePInvokeDeclIfNeeded();

            // For class types (including optional class), Swift retained the object before passing through callback.
            // We must read the object pointer from the buffer (resultPtr points to buffer containing the pointer).
            // SwiftClassHandle takes ownership of the +1 retain — no Arc.Release needed here.
            // Optional class: buffer contains retained pointer (non-nil) or zero (nil).
            var readObjPtrCode = (isClassType || isOptionalClass)
                ? "\n                            // Read object pointer from buffer (for class types, buffer contains the object reference)\n                            IntPtr _retainedObjPtr = *(IntPtr*)resultPtr;"
                : "";

            // For ObjC-bridged types, read the object pointer from the buffer and wrap with GetNSObject<T>
            // For class types, use the dereferenced object pointer (NewFromPayload expects raw pointer, not buffer)
            // For optional class types, same dereference but with null check (IntPtr.Zero = Swift nil)
            // For non-class types, marshal from Swift memory layout (resultPtr is the buffer)
            // For optional (nullable) types, use SwiftOptional<T> to read the discriminator byte correctly.
            // The inner type must be the runtime/marshal type (e.g., SwiftString not string, SwiftArray<T>
            // not IReadOnlyList<T>) — resolved via TypeProjectionFactory.
            string marshalResultCode;
            var asyncReturnSpec = _env.MethodDecl.CSSignature.First().SwiftTypeSpec;
            if (_env.ExistentialHandler.IsExistential(asyncReturnSpec))
            {
                // Existential / opaque-protocol return (`some P` is boxed to `any P` by the async
                // harness, which `initializeMemory(as: P.self, ...)` into the carrier). The carrier
                // holds an existential container, NOT a value of the public interface type, so read
                // the blittable container struct and wrap it in the proxy / union — mirroring the
                // synchronous @_cdecl existential-return path in WrapperEmitter.Return.cs. Without
                // this, the generic catch-all below emits `MarshalFromSwift<I{Protocol}>(resultPtr)`,
                // which has no marshalling support for a plain interface target and throws
                // NotSupportedException inside the [UnmanagedCallersOnly] callback — escaping a native
                // Swift caller, which crashes the process (SIGSEGV). Swift wrote the existential
                // into the carrier at +1, so the proxy ADOPTS the container (ownsContainer) and
                // releases the payload's value-witness retains on Dispose/finalize — same owned-return
                // contract as the sync proxy branch. The carrier free stays a plain dealloc (no VWT
                // Destroy): the proxy holds an independent bitwise copy and owns its own release.
                var asyncProtocolList = _env.ExistentialHandler.ToProtocolListTypeSpec(asyncReturnSpec)!;
                var asyncContainerType = _env.ExistentialHandler.GetCSharpExistentialType(asyncProtocolList);
                var asyncPublicType = _env.ExistentialHandler.GetPublicExistentialType(asyncProtocolList);
                // Owned-return ctor arg (`, ownsContainer: true`) for EC1/EC2+ proxies, mirroring
                // WrapperEmitter.OwnedExistentialCtorArg; gated on the container TYPE via the shared
                // ExistentialHandler predicate (not protocol count — ObjC filtering can drop protocols).
                var asyncOwnedArg = ExistentialHandler.IsOwnedExistentialContainerType(asyncContainerType)
                    ? ", ownsContainer: true"
                    : string.Empty;
                string asyncWrapExpr;
                if (asyncProtocolList.Protocols.Count == 0 || asyncPublicType == "object")
                    asyncWrapExpr = "__existentialResult";
                else if (asyncPublicType == "Swift.Runtime.ExistentialUnion")
                    asyncWrapExpr = "new Swift.Runtime.ExistentialUnion(__existentialResult)";
                else if (_env.ExistentialHandler.TryGetWellKnownProtocolType(asyncProtocolList, out var asyncWkIR))
                    asyncWrapExpr = $"new {asyncWkIR}(__existentialResult)";
                else
                    asyncWrapExpr = $"new {_env.ExistentialHandler.GetQualifiedProxyClassName(asyncProtocolList)}(__existentialResult{asyncOwnedArg})";
                // A class-bound (single AnyObject-/superclass-constrained) existential is a compact
                // 2-word [classRef][witnessTable] heap cell (16 bytes), not the 5-word opaque container
                // (40 bytes); reading the wider type over-reads 24 bytes past the allocation. The +1
                // still transfers via the bitwise copy (carrier free is a plain dealloc, no VWT Destroy),
                // so the proxy's ownsContainer adoption is unchanged — only the read width differs.
                var asyncExistentialRead = _env.ExistentialHandler.IsClassBoundArity1Existential(asyncProtocolList)
                    ? "Swift.Runtime.ClassExistentialContainer1.ReadHeapCell(resultPtr)"
                    : $"SwiftMarshal.MarshalFromSwift<{asyncContainerType}>(resultPtr)";
                marshalResultCode =
                    $"var __existentialResult = {asyncExistentialRead};\n" +
                    $"                                var result = {asyncWrapExpr};";
            }
            else if (isObjCBridged)
            {
                // Swift passed +1 via passRetained. GetNSObject/GetINativeObject adds its own +1 retain
                // (NSObject(handle, false) → DangerousRetain). DangerousRelease() balances passRetained,
                // matching the SwiftHandle constructor pattern for ObjC-rooted classes.
                var bridgeCall = MarshallingHelpers.FormatObjCBridgeCall(_wrapperSignature.ReturnType, "_retainedObjPtr");
                if (MarshallingHelpers.IsCoreFoundationType(_wrapperSignature.ReturnType))
                {
                    // CoreFoundation: change owns=false to owns=true to take ownership of passRetained
                    bridgeCall = MarshallingHelpers.FormatObjCBridgeCall(_wrapperSignature.ReturnType, "_retainedObjPtr", ownsReference: true);
                    marshalResultCode = $"var result = {bridgeCall};";
                }
                else
                {
                    marshalResultCode = $"var result = {bridgeCall};\n                                // Balance passRetained: GetNSObject added its own retain via DangerousRetain\n                                result?.DangerousRelease();";
                }
            }
            else if (isOptionalClass)
            {
                // Optional<ClassType>: buffer contains retained pointer or zero (nil).
                // Check for nil before marshalling — IntPtr.Zero means Swift returned .none.
                if (optionalRefBridgeCall != null)
                {
                    // Optional<ObjC reference> (Optional<UIImage>, Optional<Foundation.URL>, etc.):
                    // NSObject-rooted inner doesn't implement ISwiftObject, so MarshalFromSwift<T?>
                    // would fail CS0311. Use GetNSObject and balance the +1 from passRetained with
                    // DangerousRelease, matching the non-optional ObjCBridged/ObjCBridgeableValue path.
                    marshalResultCode = $"var result = _retainedObjPtr != IntPtr.Zero ? {optionalRefBridgeCall} : null;\n                                // Balance passRetained: GetNSObject added its own retain via DangerousRetain\n                                result?.DangerousRelease();";
                }
                else
                {
                    marshalResultCode = $"var result = _retainedObjPtr != IntPtr.Zero ? SwiftMarshal.MarshalFromSwift<{_wrapperSignature.ReturnType}>(_retainedObjPtr) : null;";
                }
            }
            else if (isClassType)
                marshalResultCode = $"var result = SwiftMarshal.MarshalFromSwift<{_wrapperSignature.ReturnType}>(_retainedObjPtr);";
            else if (TryGetOptionalMarshalType(out var optionalMarshalType, out var objcBridgeConversion, out var containerBridgeConversion, out var valueContainerInnerConversion))
            {
                if (containerBridgeConversion != null)
                {
                    // Optional<Array/Set/Dictionary<ObjCBridgeable>>: paired with the Swift-side
                    // `isOptionalObjCContainer` branch in EmitAsync. The Swift wrapper unwraps the
                    // Optional and calls `_unwrapped as AnyObject`, which dispatches through
                    // `_ObjectiveCBridgeable` to produce a real NSArray/NSDictionary/NSSet (NOT the
                    // raw `_ContiguousArrayStorage<T>` / `Foundation._SwiftURL` storage class — those
                    // are NOT toll-free bridged, and feeding their pointers into ArrayFromHandle /
                    // GetNSObject crashes in Class.Lookup). The carrier buffer holds the +1 retained
                    // NS collection pointer (or 0 for nil, via Optional's extra-inhabitant encoding).
                    // We bypass SwiftOptional<SwiftArray<>> here because its .Some would be a
                    // SwiftArray<IntPtr>, which is the wrong logical shape for the TCS<IReadOnlyList<NSUrl>?>.
                    // DO NOT remove the Swift-side half of this fix — both sides are load-bearing.
                    marshalResultCode = $"IntPtr _ptr = *(IntPtr*)resultPtr;\n                                var result = _ptr == IntPtr.Zero ? null : {containerBridgeConversion};";
                }
                else if (objcBridgeConversion != null)
                    marshalResultCode = $"var _rawResult = SwiftMarshal.MarshalFromSwift<{optionalMarshalType}>(resultPtr);\n                                var result = _rawResult.Case == SwiftOptionalCases.Some ? {objcBridgeConversion} : null;";
                else if (valueContainerInnerConversion != null)
                {
                    // Optional<Array/Set/Dictionary<value-type>>: ToNullable() yields the raw Swift
                    // container (SwiftArray<SwiftString>?, SwiftSet<int>?, etc.) but the public TCS
                    // expects the projected public type (IReadOnlyList<string>?, IReadOnlySet<int>?).
                    // Apply the inner container projection's element conversion to the unwrapped
                    // value before assigning to result. The outer Optional is a complex enum, so the
                    // carrier was initialized via initializeMemory(as: Optional<SwiftArray<…>>.self)
                    // and holds +1 on the embedded class storage — VWT-Destroy the carrier before
                    // SBW_Free reclaims the raw allocation, otherwise the storage refcount leaks.
                    marshalResultCode =
                        $"var _rawResult = SwiftMarshal.MarshalFromSwift<{optionalMarshalType}>(resultPtr).ToNullable();\n" +
                        $"                                var result = _rawResult is {{ }} _rawCol ? {valueContainerInnerConversion} : null;\n" +
                        $"                                var _vwtMetadata = SwiftObjectHelper<{optionalMarshalType}>.GetTypeMetadata();\n" +
                        $"                                _vwtMetadata.ValueWitnessTable->Destroy((void*)resultPtr, _vwtMetadata);";
                }
                else if (carrierNeedsDestroy)
                {
                    // Optional<value-type-with-non-trivial-VWT> (e.g. Optional<@frozen struct with
                    // String>, Optional<non-frozen struct>, Optional<complex enum>). Swift
                    // initialized the carrier via `initializeMemory(as: Optional<T>.self, ...)`
                    // so .some holds its own +1 on internal refs. SwiftOptional<T>'s NewFromPayload
                    // performs its own InitializeWithCopy into a managed buffer, so we must
                    // VWT-Destroy the carrier (using Optional<T>'s metadata) before SBW_Free —
                    // otherwise the carrier's +1 leaks each call.
                    //
                    // We DO NOT call SwiftOptional<T>.ToNullable() here: for unconstrained generic T,
                    // the C# language specifies `T?` as a nullable annotation (NOT Nullable<T>), so
                    // the method's runtime return type collapses to T. When T is a value type and the
                    // Optional is None, ToNullable returns `default(T)` (e.g. 0 for int) which silently
                    // converts to `int?` HasValue=true at the callsite — the None case is lost. The
                    // explicit HasValue branch with a cast to the public return type forces the
                    // conditional's common type to the proper Nullable<T>, preserving null for None.
                    marshalResultCode =
                        $"var _swiftOpt = SwiftMarshal.MarshalFromSwift<{optionalMarshalType}>(resultPtr);\n" +
                        $"                                var result = _swiftOpt.HasValue ? ({_wrapperSignature.ReturnType})_swiftOpt.Some : default;\n" +
                        $"                                var _vwtMetadata = SwiftObjectHelper<{optionalMarshalType}>.GetTypeMetadata();\n" +
                        $"                                _vwtMetadata.ValueWitnessTable->Destroy((void*)resultPtr, _vwtMetadata);";
                }
                else
                {
                    // See HasValue-vs-ToNullable rationale on the carrierNeedsDestroy branch above.
                    marshalResultCode =
                        $"var _swiftOpt = SwiftMarshal.MarshalFromSwift<{optionalMarshalType}>(resultPtr);\n" +
                        $"                                var result = _swiftOpt.HasValue ? ({_wrapperSignature.ReturnType})_swiftOpt.Some : default;";
                }
            }
            else if (newFromPayloadTakesOwnership)
            {
                // Non-frozen struct/enum return projected as a C# class with SwiftSafeHandle.
                // The Swift carrier was initialized via `initializeMemory(as:repeating:)`,
                // so it holds its own +1 on internal references. InitializeWithCopy into a
                // NativeMemory-allocated buffer performs an additional +1 retain for the
                // managed wrapper, which then owns its own memory (SwiftSafeHandle.ReleaseHandle
                // runs VWT Destroy + NativeMemory.Free on dispose). We must VWT-Destroy the
                // carrier here to release its +1 before SBW_Free reclaims the raw allocation.
                marshalResultCode =
                    $"var _vwtMetadata = SwiftObjectHelper<{_wrapperSignature.ReturnType}>.GetTypeMetadata();\n" +
                    $"                                IntPtr _vwtBuf = (IntPtr)NativeMemory.Alloc(_vwtMetadata.Size);\n" +
                    $"                                _vwtMetadata.ValueWitnessTable->InitializeWithCopy((void*)_vwtBuf, (void*)resultPtr, _vwtMetadata);\n" +
                    $"                                var result = SwiftMarshal.MarshalFromSwift<{_wrapperSignature.ReturnType}>(_vwtBuf);\n" +
                    $"                                _vwtMetadata.ValueWitnessTable->Destroy((void*)resultPtr, _vwtMetadata);";
            }
            else if (carrierNeedsDestroy)
            {
                // Frozen-with-memory struct (ClassWithBufferStruct, e.g. @frozen with String field).
                // NewFromPayload runs its own InitializeWithCopy into a managed buffer — the returned
                // C# object holds its own +1 independent of the carrier. We only need to release the
                // carrier's +1 (from the Swift-side initializeMemory) before SBW_Free.
                marshalResultCode =
                    $"var result = SwiftMarshal.MarshalFromSwift<{_wrapperSignature.ReturnType}>(resultPtr);\n" +
                    $"                                var _vwtMetadata = SwiftObjectHelper<{_wrapperSignature.ReturnType}>.GetTypeMetadata();\n" +
                    $"                                _vwtMetadata.ValueWitnessTable->Destroy((void*)resultPtr, _vwtMetadata);";
            }
            else
                marshalResultCode = $"var result = SwiftMarshal.MarshalFromSwift<{_wrapperSignature.ReturnType}>(resultPtr);";

            // Always free the Swift-allocated carrier. For non-frozen struct/enum returns the
            // carrier's +1 was released above via VWT Destroy; SBW_Free then reclaims the raw
            // memory. For all other complex-type returns the carrier is POD / class pointer
            // bits, so a raw free is sufficient.
            var freeCode = "\n                                // Free Swift-allocated memory\n                                SBW_Free(resultPtr);";

            var text = $$"""
                        {{freePInvokeDecl}}{{AsyncFieldVisibility}} static unsafe delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void> {{callbackFieldName}} = &{{callbackMethodName}};
                        [UnmanagedCallersOnly(CallConvs = new[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]
                        private static unsafe void {{callbackMethodName}}(IntPtr resultPtr, IntPtr task)
                        {
                            GCHandle handle = GCHandle.FromIntPtr(task);{{readObjPtrCode}}
                            try
                            {
                                // Read result from pointer (Swift allocated memory and stored the value)
                                {{marshalResultCode}}

                                // Handle both cases: direct TCS or object[] holder (with copy buffer pointers)
                                if (handle.Target is object[] holder && holder[0] is TaskCompletionSource<{{_wrapperSignature.ReturnType}}> holderTcs)
                                {
                                    // Free copy buffer memory for non-frozen params and release retained self
                {{BuildHolderCleanupCode("holder", "                    ")}}
                                    holderTcs.TrySetResult(result);
                                }
                                else if (handle.Target is TaskCompletionSource<{{_wrapperSignature.ReturnType}}> directTcs)
                                {
                                    directTcs.TrySetResult(result);
                                }
                            }
                            finally
                            {{{freeCode}}
                                handle.Free();
                            }
                        }

                        {{BuildErrorCallbackBlock(errorCallbackFieldName, errorCallbackMethodName, $"<{_wrapperSignature.ReturnType}>")}}
                """;
            csWriter.WriteLine(text);
        }

        /// <summary>
        /// Tries to detect if the return TypeSpec is a collection type (Array, Dictionary, Set)
        /// and extracts the runtime container type name and conversion expression needed for
        /// async callback marshalling.
        ///
        /// When the projection's <see cref="ITypeProjection.UsesObjCContainerBridge"/> is true
        /// (e.g. <c>[URL]</c>, <c>Set&lt;URL&gt;</c>, <c>[String: URL]</c> whose elements bridge
        /// through NSObject), the returned <paramref name="conversionExpr"/> is keyed off
        /// <c>_ptr</c> (an <c>IntPtr</c> read from the carrier buffer) instead of <c>_collection</c>
        /// (a managed <c>SwiftArray&lt;T&gt;</c> / <c>SwiftSet&lt;T&gt;</c> / <c>SwiftDictionary&lt;…&gt;</c>).
        /// The Swift @_cdecl wrapper for this branch stores a +1 retained NSArray / NSDictionary /
        /// NSSet pointer (via <c>as AnyObject</c>) into the carrier. <paramref name="usesObjCContainerBridge"/>
        /// signals which carrier shape <see cref="EmitAsyncWrapperForCollection"/> must emit.
        /// </summary>
        /// <returns>True if the type is a collection with extractable async info.</returns>
        private bool TryGetCollectionAsyncInfo(TypeSpec returnTypeSpec,
            [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? runtimeType,
            [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? conversionExpr,
            out bool usesObjCContainerBridge)
        {
            runtimeType = null;
            conversionExpr = null;
            usesObjCContainerBridge = false;

            var ctx = new ProjectionContext
            {
                TypeDatabase = _env.TypeDatabase,
                IsParameter = false,
                IsAsync = false
            };

            var projection = s_projectionFactory.Project(returnTypeSpec, ctx);
            if (projection is ArrayProjection ap)
            {
                usesObjCContainerBridge = ap.UsesObjCContainerBridge;
                runtimeType = ap.ContainerTypeName;
                conversionExpr = usesObjCContainerBridge
                    ? ap.GetReturnContainerConversion("_ptr")!
                    : ap.GetReturnContainerConversion("_collection")!;
                return true;
            }
            if (projection is DictionaryProjection dp)
            {
                usesObjCContainerBridge = dp.UsesObjCContainerBridge;
                runtimeType = dp.ContainerTypeName;
                conversionExpr = usesObjCContainerBridge
                    ? dp.GetReturnContainerConversion("_ptr")!
                    : dp.GetReturnContainerConversion("_collection")!;
                return true;
            }
            if (projection is SetProjection sp)
            {
                usesObjCContainerBridge = sp.UsesObjCContainerBridge;
                runtimeType = sp.ContainerTypeName;
                // SetProjection returns null when no element conversion is needed
                // (SwiftSet<T> already implements IReadOnlySet<T>). Use identity.
                conversionExpr = usesObjCContainerBridge
                    ? sp.GetReturnContainerConversion("_ptr")!
                    : (sp.GetReturnContainerConversion("_collection") ?? "_collection");
                return true;
            }

            return false;
        }

        /// <summary>
        /// Returns true when the method's async return is a top-level non-optional
        /// <c>Array/Set/Dictionary</c> whose element projection bridges through NSObject
        /// (e.g. <c>[URL]</c>, <c>Set&lt;URL&gt;</c>, <c>[String: URL]</c>). Used by the
        /// Swift @_cdecl wrapper emitter to pick the single-pointer carrier shape (store
        /// a +1 retained NSArray / NSDictionary / NSSet pointer via <c>as AnyObject</c>)
        /// instead of the raw <c>initializeMemory(as:repeating:)</c> path, which would
        /// stamp Swift's <c>_ContiguousArrayStorage&lt;T&gt;</c> / <c>Foundation._SwiftURL</c>
        /// bits into the buffer — those are NOT toll-free bridged, and feeding their
        /// pointers into <c>ArrayFromHandle</c> / <c>GetINativeObject</c> crashes the
        /// ObjC registrar.
        /// </summary>
        public bool IsTopLevelObjCBridgeContainerReturn(TypeSpec returnSpec)
        {
            var projection = ProjectReturn(returnSpec);
            return projection switch
            {
                ArrayProjection ap => ap.UsesObjCContainerBridge,
                DictionaryProjection dp => dp.UsesObjCContainerBridge,
                SetProjection sp => sp.UsesObjCContainerBridge,
                _ => false
            };
        }

        /// <summary>
        /// Checks if the method's return type is Optional and resolves the correct runtime/marshal
        /// type for MarshalFromSwift. Uses TypeProjectionFactory to get the projection-resolved
        /// container type (e.g., SwiftOptional&lt;SwiftString&gt; not SwiftOptional&lt;string&gt;).
        ///
        /// Three result shapes (mutually exclusive, the marshalResultCode caller picks the branch):
        ///   1. <paramref name="containerBridgeConversion"/> set: inner is Array/Set/Dict whose elements
        ///      use ObjC container bridge (e.g., <c>Optional&lt;Array&lt;URL&gt;&gt;</c>). Caller reads the
        ///      buffer as a nullable IntPtr (the Swift wrapper stores a +1 retained NSArray /
        ///      NSDictionary / NSSet pointer via <c>as AnyObject</c>) and applies the bridge conversion.
        ///   2. <paramref name="objcBridgeConversion"/> set: inner is an ObjC-bridgeable scalar
        ///      (e.g., <c>Optional&lt;URLRequest&gt;</c>). Caller reads via SwiftOptional&lt;IntPtr&gt;
        ///      then bridges the Some payload through GetNSObject.
        ///   3. <paramref name="valueContainerInnerConversion"/> set: inner is Array/Set/Dict whose
        ///      elements are value-type projected (e.g., <c>Optional&lt;Array&lt;String&gt;&gt;</c>).
        ///      The caller reads via SwiftOptional&lt;SwiftArray&lt;…&gt;&gt;.ToNullable() and applies
        ///      this conversion to project the unwrapped Swift container into the public type
        ///      (IReadOnlyList&lt;string&gt;?, IReadOnlySet&lt;int&gt;?).
        ///   4. None set: ordinary value-type optional. Caller reads via SwiftOptional&lt;T&gt;.ToNullable().
        /// </summary>
        private bool TryGetOptionalMarshalType(
            [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? marshalType,
            out string? objcBridgeConversion,
            out string? containerBridgeConversion,
            out string? valueContainerInnerConversion)
        {
            marshalType = null;
            objcBridgeConversion = null;
            containerBridgeConversion = null;
            valueContainerInnerConversion = null;
            var returnSpec = _env.MethodDecl.CSSignature.First().SwiftTypeSpec;
            if (returnSpec is not NamedTypeSpec { Name: "Swift.Optional", GenericParameters.Count: 1 } optionalSpec)
                return false;

            // Class and ObjC-bridged optionals use nullable pointer ABI (nil = IntPtr.Zero),
            // handled by the isClassType/isObjCBridged paths above. Only value-type optionals
            // need SwiftOptional<T> with discriminator byte reading.
            var innerSpec = optionalSpec.GenericParameters[0];
            if (_env.TypeDatabase.TryGetTypeRecord(innerSpec, out var innerRecord))
            {
                if (innerRecord.Kind == TypeRecordKind.Class)
                    return false;
                if (MarshallingHelpers.IsObjCBridged(innerRecord))
                    return false;
                // ObjC-bridgeable types (e.g., URLRequest) marshal as SwiftOptional<IntPtr>.
                // ToNullable() returns nint (not nint?) due to unconstrained T? semantics,
                // so we work with SwiftOptional<IntPtr> directly: check .Case, then wrap .Some.
                if (MarshallingHelpers.IsObjCBridgeable(innerRecord) && innerRecord.NativeTypeName != null)
                {
                    objcBridgeConversion = MarshallingHelpers.FormatObjCBridgeCall(
                        innerRecord.NativeTypeName.FullyQualifiedName, "_rawResult.Some", nonNull: true);
                }
            }

            var projection = ProjectReturn(returnSpec);
            if (projection is OptionalProjection op)
            {
                marshalType = op.ContainerTypeName;

                // Optional<Container<ObjCBridgeable>>: the inner container projection bridges to
                // NSArray / NSDictionary / NSSet. The Swift @_cdecl wrapper coerces the unwrapped
                // value via `as AnyObject` (which dispatches through _ObjectiveCBridgeable to
                // produce a real NSArray/NSDictionary/NSSet, NOT the raw Swift storage class —
                // Foundation._SwiftURL is not an NSObject subclass and would crash the ObjC
                // registrar) and stores the resulting +1 retained pointer in the carrier buffer.
                // The C# side reads the IntPtr and hands it to the container projection's
                // GetReturnContainerConversion which expects an IntPtr-typed variable name.
                if (op.InnerProjection.UsesObjCContainerBridge)
                {
                    containerBridgeConversion = op.InnerProjection.GetReturnContainerConversion("_ptr");
                    // Drop the no-longer-used objcBridgeConversion guard — we're switching strategies.
                    objcBridgeConversion = null;
                }
                else if (objcBridgeConversion == null
                    && op.InnerProjection is ArrayProjection or SetProjection or DictionaryProjection)
                {
                    // Optional<Array/Set/Dictionary<value-type>>: the carrier holds a real
                    // SwiftOptional<SwiftArray<…>> value (no ObjC bridge). ToNullable() yields
                    // SwiftArray<rawElem>?, but the public TCS expects IReadOnlyList<publicElem>?.
                    // Capture the inner container's element-conversion expression (e.g.,
                    // `_rawCol.AsProjected(e => e.ToString())`) to apply at the call site.
                    valueContainerInnerConversion = op.InnerProjection.GetReturnContainerConversion("_rawCol");
                }

                return true;
            }

            return false;
        }

        /// <summary>
        /// Returns true when the method's async return is <c>Optional&lt;Array/Set/Dictionary&lt;ObjCBridgeable&gt;&gt;</c>.
        /// Used by the Swift @_cdecl wrapper emitter to pick the nullable-pointer ABI shape
        /// (bridge to NS collection via <c>as AnyObject</c>) instead of the raw <c>copyMemory</c>
        /// path, which would store a Swift storage class pointer that the C# side cannot use
        /// as an NSArray handle (Foundation._SwiftURL crashes ObjC registrar lookup).
        /// </summary>
        public bool IsOptionalObjCBridgeContainerReturn(TypeSpec returnSpec)
        {
            if (returnSpec is not NamedTypeSpec { Name: "Swift.Optional", GenericParameters.Count: 1 })
                return false;
            return ProjectReturn(returnSpec) is OptionalProjection op
                && op.InnerProjection.UsesObjCContainerBridge;
        }

        /// <summary>
        /// Builds the standard return-projection context for the current async method.
        /// Centralizes the <c>IsParameter=false, IsAsync=false</c> setup so callers don't have
        /// to duplicate it (and so the projection-cache key stays consistent across uses).
        /// </summary>
        private ITypeProjection? ProjectReturn(TypeSpec returnSpec)
        {
            var ctx = new ProjectionContext
            {
                TypeDatabase = _env.TypeDatabase,
                IsParameter = false,
                IsAsync = false
            };
            return s_projectionFactory.Project(returnSpec, ctx);
        }

        /// <summary>
        /// Emits async wrapper for methods returning collection types (Array, Dictionary, Set).
        /// These use the same OpaquePointer pattern as complex types on the Swift side,
        /// but require MarshalFromSwift with the runtime container type (e.g., SwiftArray&lt;int&gt;)
        /// instead of the public type (e.g., IReadOnlyList&lt;int&gt;).
        ///
        /// When <paramref name="usesObjCContainerBridge"/> is true (e.g. <c>[URL]</c>,
        /// <c>Set&lt;URL&gt;</c>, <c>[String: URL]</c>), the Swift wrapper stores a +1 retained
        /// NSArray / NSDictionary / NSSet pointer (via <c>as AnyObject</c>) into the carrier
        /// instead of the raw Swift collection bits. C# reads that as an <c>IntPtr</c> and feeds
        /// it to the projection-supplied conversion (which expects a pointer, not a managed
        /// container). This mirrors the optional-collection ObjC-bridge branch in
        /// <see cref="EmitAsyncWrapperForComplexType"/>.
        /// </summary>
        private void EmitAsyncWrapperForCollection(CSharpWriter csWriter,
            string callbackFieldName, string callbackMethodName,
            string errorCallbackFieldName, string errorCallbackMethodName,
            string runtimeType, string conversionExpr,
            bool usesObjCContainerBridge)
        {
            var freePInvokeDecl = GetFreePInvokeDeclIfNeeded();

            // ObjC-container bridge: read the retained pointer the Swift wrapper stored, then
            // bridge through the projection's IntPtr-shaped conversion. Plain Swift collection:
            // revive via MarshalFromSwift on the runtime container type and let the projection
            // convert from the managed container.
            string marshalLines = usesObjCContainerBridge
                ? $"// ObjC-bridge collection: read +1 retained NS-collection pointer from carrier\n" +
                  $"                                IntPtr _ptr = *(IntPtr*)resultPtr;\n" +
                  $"                                var result = {conversionExpr};"
                : $"// Marshal collection from Swift-allocated memory using runtime container type\n" +
                  $"                                var _collection = SwiftMarshal.MarshalFromSwift<{runtimeType}>(resultPtr);\n" +
                  $"                                var result = {conversionExpr};";

            var text = $$"""
                        {{freePInvokeDecl}}{{AsyncFieldVisibility}} static unsafe delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void> {{callbackFieldName}} = &{{callbackMethodName}};
                        [UnmanagedCallersOnly(CallConvs = new[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]
                        private static unsafe void {{callbackMethodName}}(IntPtr resultPtr, IntPtr task)
                        {
                            GCHandle handle = GCHandle.FromIntPtr(task);
                            try
                            {
                                {{marshalLines}}

                                // Handle both cases: direct TCS or object[] holder (with copy buffer pointers)
                                if (handle.Target is object[] holder && holder[0] is TaskCompletionSource<{{_wrapperSignature.ReturnType}}> holderTcs)
                                {
                                    // Free copy buffer memory for non-frozen params and release retained self
                {{BuildHolderCleanupCode("holder", "                    ")}}
                                    holderTcs.TrySetResult(result);
                                }
                                else if (handle.Target is TaskCompletionSource<{{_wrapperSignature.ReturnType}}> directTcs)
                                {
                                    directTcs.TrySetResult(result);
                                }
                            }
                            finally
                            {
                                // Free Swift-allocated memory
                                SBW_Free(resultPtr);
                                handle.Free();
                            }
                        }

                        {{BuildErrorCallbackBlock(errorCallbackFieldName, errorCallbackMethodName, $"<{_wrapperSignature.ReturnType}>")}}
                """;
            csWriter.WriteLine(text);
        }

        /// <summary>
        /// Builds the C# error callback code block (delegate + method) for async wrappers.
        /// Phase 4 unified wire format: all branches (typed throws, plain-throws cascade,
        /// untyped throws) emit the same 6-param callback
        /// (errorPtr, errorSize, errorMessagePtr, isCancellation, task, errorTypeId).
        /// Body branches differ — typed marshals a static error type from <c>errorPtr</c>;
        /// cascade dispatches via the per-module helper using <c>errorTypeId</c>; untyped
        /// reads only <c>errorMessagePtr</c>, with the payload fields nil/0.
        /// <c>isCancellation</c> (Int32) is 1 when the Swift error is CancellationError.
        /// </summary>
        private string BuildErrorCallbackBlock(
            string errorCallbackFieldName,
            string errorCallbackMethodName,
            string tcsType)
        {
            // Common cancellation handling code — used by typed, cascade, and untyped paths.
            // When isCancellation is set, find the CancellationToken from the holder and call TrySetCanceled.
            // For typed throws, the Swift-allocated error buffer must also be freed; for cascade,
            // SBW_Free runs inside the dispatcher helper's `finally` so no extra free here.
            // Class-direct typed throws emit nil errorPtr on cancellation (no buffer / no
            // retain) so there's nothing to clean up — skip the SBW_Free call.
            var freeErrorInCancellation = (_useTypedErrorCallback && !_typedErrorIsClassDirectAsync)
                ? "\n                                        SBW_Free(errorPtr);"
                : "";
            var cancellationBlock = $$"""
                                    if (isCancellation != 0)
                                    {
                                        // Swift reported CancellationError — find token and cancel the Task.
                                        // Loop body shared via BuildCancellationCleanupLoop so this hand-rolled
                                        // block cannot drift from WrapperEmitter.Async's equivalent block.
                                        global::System.Threading.CancellationToken cancelToken = default;
                {{BuildCancellationCleanupLoop("holder", "i", "                                        ")}}{{freeErrorInCancellation}}
                                        holderTcs.TrySetCanceled(cancelToken);
                                    }
                """;

            // Build the error creation and TCS dispatch code.
            // Typed throws: unmarshal typed error from Swift memory, free error buffer, create SwiftException<T>.
            // Untyped throws: parse error message string, create SwiftException.
            string holderErrorBody;
            string directErrorBody;

            if (_useTypedErrorCallback)
            {
                // Per-shape cleanup mirrors the cascade dispatcher's `CascadePayloadShape`
                // selector in <see cref="ErrorRegistryHelperEmitter"/>:
                //
                // - frozen-with-memory struct (`_typedErrorRequiresVwtDestroyAsync`):
                //   `NewFromPayload` copies into a fresh buffer, so the wire carrier still
                //   holds +1 retains. `finally { VWT-destroy + SBW_Free }` to release both
                //   the retains and the carrier on every successful marshal.
                //
                // - class error (`_typedErrorIsClassDirectAsync`): wire is a +1 retained
                //   class pointer (no carrier buffer). `MarshalFromSwift<T>` constructs a
                //   SwiftObject taking ownership of the retain on success. On marshal
                //   failure C# calls `Arc.Release` to balance the retain — never `SBW_Free`
                //   (no allocation to free, and the symbol isn't even imported for this
                //   shape; see freePInvokeDecl below).
                //
                // - complex enum / non-frozen struct (`_typedErrorTransfersOwnershipAsync`):
                //   `NewFromPayload` wraps the wire buffer directly into the SafeHandle, so
                //   the SafeHandle's release path runs the destroy. Free only on marshal
                //   failure to avoid double-free with the SafeHandle's finalizer.
                //
                // - simple enum / plain frozen struct: marshal copies bytes by value; the
                //   buffer is owned by us. Free in `finally`.
                string asyncErrorFreeBlock;
                if (_typedErrorRequiresVwtDestroyAsync)
                    asyncErrorFreeBlock = $"finally {{ if (errorPtr != IntPtr.Zero) {{ global::Swift.Runtime.InteropServices.SwiftMarshal.DestroyWireBufferRetains<{_typedThrowsCSharpErrorType}>(errorPtr); SBW_Free(errorPtr); }} }}";
                else if (_typedErrorIsClassDirectAsync)
                    asyncErrorFreeBlock = "catch { if (errorPtr != IntPtr.Zero) global::Swift.Runtime.Arc.Release(errorPtr); throw; }";
                else if (_typedErrorTransfersOwnershipAsync)
                    asyncErrorFreeBlock = "catch { SBW_Free(errorPtr); throw; }";
                else
                    asyncErrorFreeBlock = "finally { SBW_Free(errorPtr); }";
                holderErrorBody = $$"""
                                        var errorMessage = Marshal.PtrToStringUTF8(errorMessagePtr) ?? "Unknown Swift error";
                                        {{_typedThrowsCSharpErrorType}} typedError;
                                        try
                                        {
                                            typedError = ({{_typedThrowsCSharpErrorType}})SwiftMarshal.MarshalFromSwift<{{_typedThrowsCSharpErrorType}}>(errorPtr);
                                        }
                                        {{asyncErrorFreeBlock}}
                                        var exception = new SwiftException<{{_typedThrowsCSharpErrorType}}>(typedError, errorMessage);
                                        // Free copy buffer memory for non-frozen params and release retained self
                {{BuildHolderCleanupCode("holder", "                        ", cancelRegVarName: "cancelReg2")}}
                                        holderTcs.TrySetException(exception);
                """;
                directErrorBody = $$"""
                                    var errorMessage = Marshal.PtrToStringUTF8(errorMessagePtr) ?? "Unknown Swift error";
                                    {{_typedThrowsCSharpErrorType}} typedError;
                                    try
                                    {
                                        typedError = ({{_typedThrowsCSharpErrorType}})SwiftMarshal.MarshalFromSwift<{{_typedThrowsCSharpErrorType}}>(errorPtr);
                                    }
                                    {{asyncErrorFreeBlock}}
                                    directTcs.TrySetException(new SwiftException<{{_typedThrowsCSharpErrorType}}>(typedError, errorMessage));
                """;
            }
            else if (_useCascadeErrorCallback)
            {
                // Phase 4 plain-throws cascade: 6-param wire format. The Swift cascade
                // dispatcher (_SBW_dispatchSwiftError_{Module}) hands us errorTypeId +
                // a typed buffer for registered error types, or id 0 for untyped fallthrough.
                // The per-module C# helper class (_SbwModuleErrorRegistry_{Module}) consumes
                // the wire fields and returns the appropriate SwiftException / SwiftException<TError>.
                // The helper handles SBW_Free in its own finally — no extra free here.
                var moduleName = _env.MethodDecl.ModuleDecl?.Name
                    ?? throw new InvalidOperationException("MethodDecl.ModuleDecl required for cascade callback");
                // Helper-class cross-reference shares the central resolver with
                // WrapperEmitter.Async so the two sites cannot diverge on NamespacePattern
                // remaps. See ErrorRegistryHelperEmitter.GetFullyQualifiedHelperReference.
                var helperRef = ErrorRegistryHelperEmitter.GetFullyQualifiedHelperReference(
                    moduleName, _emissionContext.ResolvedNamespace);
                holderErrorBody = $$"""
                                        var errorMessage = Marshal.PtrToStringUTF8(errorMessagePtr) ?? "Unknown Swift error";
                                        var exception = {{helperRef}}.CreateException(errorTypeId, errorPtr, errorSize, errorMessage);
                                        // Free copy buffer memory for non-frozen params and release retained self
                {{BuildHolderCleanupCode("holder", "                        ", cancelRegVarName: "cancelReg2")}}
                                        holderTcs.TrySetException(exception);
                """;
                directErrorBody = $$"""
                                    var errorMessage = Marshal.PtrToStringUTF8(errorMessagePtr) ?? "Unknown Swift error";
                                    directTcs.TrySetException({{helperRef}}.CreateException(errorTypeId, errorPtr, errorSize, errorMessage));
                """;
            }
            else
            {
                holderErrorBody = $$"""
                                        var errorMessage = Marshal.PtrToStringUTF8(errorMessagePtr) ?? "Unknown Swift error";
                                        var exception = new SwiftException(errorMessage);
                                        // Free copy buffer memory for non-frozen params and release retained self
                {{BuildHolderCleanupCode("holder", "                        ", cancelRegVarName: "cancelReg2")}}
                                        holderTcs.TrySetException(exception);
                """;
                directErrorBody = $$"""
                                    var errorMessage = Marshal.PtrToStringUTF8(errorMessagePtr) ?? "Unknown Swift error";
                                    directTcs.TrySetException(new SwiftException(errorMessage));
                """;
            }

            // Phase 4 unified wire format: typed-throws, plain-throws cascade, and untyped
            // throws all emit the same 6-param C# delegate. Body branches above still differ
            // (typed marshals a static error type, cascade dispatches via per-module helper,
            // untyped reads only the message), but the wire and delegate type are uniform.
            // SBW_Free is only declared for the typed-throws branch — cascade frees inside
            // the per-module helper; untyped never allocates payload memory.
            // Class-direct typed throws never call SBW_Free (no buffer); skip the P/Invoke
            // declaration so the helper class doesn't import an unused symbol. Other
            // typed-throws shapes still need it. Cascade frees inside the per-module helper;
            // untyped never allocates payload memory.
            var freePInvokeDecl = (_useTypedErrorCallback && !_typedErrorIsClassDirectAsync)
                ? GetFreePInvokeDeclIfNeeded()
                : "";
            const string delegateParams = "IntPtr, nint, IntPtr, int, IntPtr, int, void";
            const string methodParams = "IntPtr errorPtr, nint errorSize, IntPtr errorMessagePtr, int isCancellation, IntPtr task, int errorTypeId";

            return $$"""
                        {{freePInvokeDecl}}{{AsyncFieldVisibility}} static unsafe delegate* unmanaged[Cdecl]<{{delegateParams}}> {{errorCallbackFieldName}} = &{{errorCallbackMethodName}};
                        [UnmanagedCallersOnly(CallConvs = new[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]
                        private static unsafe void {{errorCallbackMethodName}}({{methodParams}})
                        {
                            GCHandle handle = GCHandle.FromIntPtr(task);
                            try
                            {
                                // Handle both cases: direct TCS or object[] holder (with copy buffer pointers)
                                if (handle.Target is object[] holder && holder[0] is TaskCompletionSource{{tcsType}} holderTcs)
                                {
                {{cancellationBlock}}
                                    else
                                    {
                {{holderErrorBody}}
                                    }
                                }
                                else if (handle.Target is TaskCompletionSource{{tcsType}} directTcs)
                                {
                {{directErrorBody}}
                                }
                            }
                            finally
                            {
                                handle.Free();
                            }
                        }
                """;
        }

        /// <summary>
        /// Gets the SBW_Free P/Invoke declaration string if not already emitted for the current type.
        /// Handles deduplication — types with multiple async string/complex methods only emit once.
        /// </summary>
        private string GetFreePInvokeDeclIfNeeded()
        {
            var moduleDecl = _env.MethodDecl.ModuleDecl ?? throw new ArgumentNullException(nameof(_env.MethodDecl.ModuleDecl));
            var moduleLibPath = _env.TypeDatabase.GetLibraryPath(moduleDecl.Name);
            var wrapperLibPath = _env.TypeDatabase.AsyncLibraryName ?? moduleLibPath;
            var freeSymbolName = Utf8SliceEmitter.GetFreeSymbolName(moduleDecl.Name);
            var typeKey = (_env.ParentDecl as TypeDecl)?.SwiftTypeName.ModuleQualifiedName ?? moduleDecl.Name;
            var needsFreePInvoke = !Utf8SliceEmitter.HasFreePInvokeForType(typeKey, _emissionContext);
            if (needsFreePInvoke)
            {
                Utf8SliceEmitter.MarkFreePInvokeEmittedForType(typeKey, _emissionContext);
            }
            return needsFreePInvoke
                ? $$"""
                        [global::System.Runtime.InteropServices.UnmanagedCallConv(CallConvs = new global::System.Type[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]
                        [global::System.Runtime.InteropServices.LibraryImport("{{wrapperLibPath}}", EntryPoint = "{{freeSymbolName}}")]
                        private static partial void SBW_Free(IntPtr ptr);

                """
                : "";
        }

        /// <summary>
        /// Builds the Swift catch block body for typed/cascade/untyped throws, parameterized by indent.
        /// </summary>
        public string BuildSwiftCatchBody(string indent)
        {
            if (_useTypedErrorCallback)
            {
                if (_typedErrorIsClassDirectAsync)
                {
                    // Class-shaped typed throws — mirror the cascade dispatcher's
                    // `ClassPointerDirect` shape. Wire is a +1 retained class pointer (no
                    // carrier buffer); cancellation passes nil errorPtr.
                    return
                        $"let _isCancelled: Int32 = (error is CancellationError) ? 1 : 0\n" +
                        $"{indent}let errorMessage = String(describing: error)\n" +
                        $"{indent}if _isCancelled != 0 {{\n" +
                        $"{indent}    errorMessage.withCString {{ _msgPtr in\n" +
                        $"{indent}        errorCallback(nil, 0, _msgPtr, _isCancelled, _sbwTask, 0)\n" +
                        $"{indent}    }}\n" +
                        $"{indent}}} else {{\n" +
                        $"{indent}    let _ptr = Unmanaged.passRetained(error as! {_typedThrowsSwiftErrorType} as AnyObject).toOpaque()\n" +
                        $"{indent}    errorMessage.withCString {{ _msgPtr in\n" +
                        $"{indent}        errorCallback(UnsafeRawPointer(_ptr), 0, _msgPtr, 0, _sbwTask, 0)\n" +
                        $"{indent}    }}\n" +
                        $"{indent}}}";
                }
                // Typed-throws path. Cancellation must be handled before the force-cast to
                // the typed error — CancellationError is not the typed error type, so
                // `error as! T` would trap. Cancellation: allocate a zeroed buffer (C# only
                // reads _isCancelled flag). Typed errors: cast and copy into the buffer.
                // Wire format is the unified 6-param shape; errorTypeId is 0 because the
                // typed-throws C# body uses the static error type (never consults the id).
                return
                    $"let _isCancelled: Int32 = (error is CancellationError) ? 1 : 0\n" +
                    $"{indent}let _errSize = MemoryLayout<{_typedThrowsSwiftErrorType}>.size\n" +
                    $"{indent}let _errPtr = UnsafeMutableRawPointer.allocate(\n" +
                    $"{indent}    byteCount: _errSize, alignment: MemoryLayout<{_typedThrowsSwiftErrorType}>.alignment)\n" +
                    $"{indent}if _isCancelled == 0 {{\n" +
                    $"{indent}    _errPtr.initializeMemory(as: {_typedThrowsSwiftErrorType}.self, repeating: error as! {_typedThrowsSwiftErrorType}, count: 1)\n" +
                    $"{indent}}}\n" +
                    $"{indent}let errorMessage = String(describing: error)\n" +
                    $"{indent}errorMessage.withCString {{ _msgPtr in\n" +
                    $"{indent}    errorCallback(UnsafeRawPointer(_errPtr), Int(Int64(_errSize)), _msgPtr, _isCancelled, _sbwTask, 0)\n" +
                    $"{indent}}}";
            }
            else if (_useCascadeErrorCallback)
            {
                // Phase 4 plain-throws cascade: delegate to the per-module dispatcher helper
                // (_SBW_dispatchSwiftError_{Module}) which handles cancellation, the
                // alphabetical `as?` cascade against registered error types, typed-buffer
                // allocation, and the unified 6-param callback invocation. The helper falls
                // through to errorTypeId 0 when no registered type matches; C# treats id 0
                // as untyped SwiftException fallback.
                var moduleName = _env.MethodDecl.ModuleDecl?.Name
                    ?? throw new InvalidOperationException("MethodDecl.ModuleDecl required for cascade catch body");
                var dispatchSymbol = ErrorRegistryHelperEmitter.GetSwiftDispatchSymbolName(moduleName);
                return $"{dispatchSymbol}(error, _sbwTask, errorCallback)";
            }
            else
            {
                // Untyped fallback: the module has no registered Error-conforming types so
                // the cascade has nothing to dispatch against. Pass the unified shape with
                // nil payload pointer, zero size, and errorTypeId 0 — the C# body reads only
                // the message field, but the wire matches the typed/cascade delegate type.
                return
                    $"let _isCancelled: Int32 = (error is CancellationError) ? 1 : 0\n" +
                    $"{indent}let errorMessage = String(describing: error)\n" +
                    $"{indent}errorMessage.withCString {{ _msgPtr in\n" +
                    $"{indent}    errorCallback(nil, 0, _msgPtr, _isCancelled, _sbwTask, 0)\n" +
                    $"{indent}}}";
            }
        }

        /// <summary>
        /// Builds the Swift async wrapper code for all 3 scope variants (free function, extension, top-level).
        /// Parameterized by isExtension and hasReadCode to collapse 6 templates into 1.
        /// </summary>
        public string BuildSwiftAsyncWrapperCode(
            bool isExtension,
            SwiftTypeName? parentTypeName,
            string staticModifier,
            string genericParams,
            string parameters,
            string whereClause,
            bool hasReadCode,
            string readCode,
            string selfConversion,
            string selfComment,
            bool isEmptyTuple,
            string methodCallArgs,
            string methodCallPrefix,
            string stringMarshalCode,
            string callbackResultArgs,
            string catchBody,
            bool needsMainActor = false)
        {
            var mangledName = NameProvider.GetMangledName(_env.MethodDecl);
            var pInvokeName = NameProvider.GetPInvokeName(_env.MethodDecl);
            var resultAssign = isEmptyTuple ? "" : $"let result{_env.MethodDecl.Name} = ";

            // Extension adds 4-space indent to everything inside the extension { } block
            var i = isExtension ? "    " : "";

            // Build the function body lines
            var readCodeBlock = hasReadCode
                ? $"""
                {i}    // Read non-frozen parameters via .pointee (bitwise copy)
                {i}    // C# created copies using InitializeWithCopy (owns a proper reference)
                {i}    {readCode}
                {i}    {selfConversion}
                {i}    {selfComment}

                """
                : (selfConversion != "" || selfComment != ""
                    ? $"""
                {i}    {selfConversion}
                {i}    {selfComment}
                """
                    : "");

            // Ensure readCodeBlock ends with a newline so _entry starts on its own line
            if (readCodeBlock.Length > 0 && !readCodeBlock.EndsWith("\n"))
                readCodeBlock += "\n";

            var mainActorLine = needsMainActor ? $"{i}@MainActor\n" : "";
            // @MainActor functions: Task { } doesn't inherit actor context, so we need
            // Task { @MainActor in } to access actor-isolated members within the task body.
            var taskOpen = needsMainActor ? "Task { @MainActor in" : "Task {";
            var annotation = _env.MethodDecl.UsesCdeclMethodWrapper ? "@_cdecl" : "@_silgen_name";

            // Async @_cdecl wrappers don't inherit the enclosing type's availability,
            // so emit @available lines explicitly when the method or any ancestor type
            // is gated behind an OS version (e.g., StoreKit Product APIs gated to iOS 15+).
            var availability = WrapperEmitterHelpers.MergeAvailabilityFromAncestors(
                _env.MethodDecl.AvailabilityAnnotations, _env.ParentDecl);
            var availabilityLines = BuildAvailabilityLines(availability, i);

            // Async property getters use property access syntax (no parens), not method call syntax.
            var asyncPropertyName = _env.MethodDecl.AsyncPropertyName;
            var callExpression = asyncPropertyName != null
                ? $"{methodCallPrefix}{asyncPropertyName}"
                : $"{methodCallPrefix}{_env.MethodDecl.Name}(\n{i}                {methodCallArgs}\n{i}            )";

            // Non-throwing async: use plain `await` without do/catch wrapper
            // to avoid "no calls to throwing functions occur within 'try' expression" warning.
            bool throws = _env.MethodDecl.Throws;
            var awaitKeyword = throws ? "try await" : "await";

            string funcBody;
            if (throws)
            {
                funcBody = $$"""
            {{availabilityLines}}{{mainActorLine}}{{i}}{{annotation}}("{{mangledName}}")
            {{i}}public {{staticModifier}}func {{pInvokeName}}{{genericParams}}({{parameters}}){{whereClause}}{
            {{readCodeBlock}}{{i}}    let _entry = _SBWTaskEntry()
            {{i}}    _sbwRegisterTask(_sbwTask, _entry)
            {{i}}    _entry.task = {{taskOpen}}
            {{i}}        defer {
            {{i}}            _sbwUnregisterTask(_sbwTask)
            {{i}}        }
            {{i}}        do {
            {{i}}            {{resultAssign}}{{awaitKeyword}} {{callExpression}}
            {{i}}            {{stringMarshalCode}}
            {{i}}            callback({{callbackResultArgs}}_sbwTask)
            {{i}}        } catch {
            {{i}}            {{catchBody}}
            {{i}}        }
            {{i}}    }
            {{i}}}
            """;
            }
            else
            {
                funcBody = $$"""
            {{availabilityLines}}{{mainActorLine}}{{i}}{{annotation}}("{{mangledName}}")
            {{i}}public {{staticModifier}}func {{pInvokeName}}{{genericParams}}({{parameters}}){{whereClause}}{
            {{readCodeBlock}}{{i}}    let _entry = _SBWTaskEntry()
            {{i}}    _sbwRegisterTask(_sbwTask, _entry)
            {{i}}    _entry.task = {{taskOpen}}
            {{i}}        defer {
            {{i}}            _sbwUnregisterTask(_sbwTask)
            {{i}}        }
            {{i}}        {{resultAssign}}{{awaitKeyword}} {{callExpression}}
            {{i}}        {{stringMarshalCode}}
            {{i}}        callback({{callbackResultArgs}}_sbwTask)
            {{i}}    }
            {{i}}}
            """;
            }

            if (isExtension)
            {
                // Extension declarations on nested types inside OS-gated outer types
                // (e.g. `extension TipKit.Tips.Event`, where `TipKit.Tips` is iOS 17+)
                // must themselves carry availability — Swift treats a bare
                // `extension Foo.Bar { @available(...) func ... }` as using `Foo.Bar`
                // outside its window and emits `'Foo' is only available in …`.
                // Use ONLY ancestor annotations (not the method's own) so a method
                // introduced later than its containing type doesn't produce a more-restrictive
                // extension than the inner method claims, which Swift rejects as
                // "instance method cannot be more available than enclosing scope".
                var ancestorAvailability = WrapperEmitterHelpers.MergeAvailabilityFromAncestors(
                    null, _env.ParentDecl);
                var extensionAvailabilityLines = BuildAvailabilityLines(ancestorAvailability, "");
                return $$"""
            {{extensionAvailabilityLines}}extension {{parentTypeName!.ModuleQualifiedName}} {
            {{funcBody}}
            }
            """;
            }
            return funcBody;
        }

        /// <summary>
        /// Builds Swift @available annotation lines for the async wrapper template.
        /// Returns a string with each annotation on its own line (terminated by a newline) so it can
        /// be inlined directly in front of the @MainActor / @_cdecl line in the async wrapper template.
        /// Returns an empty string when there are no annotations.
        /// </summary>
        private static string BuildAvailabilityLines(IReadOnlyList<AvailabilityAnnotation>? annotations, string indent)
        {
            // Route through the shared helper so per-platform deduplication and the
            // macCatalyst-tracks-iOS lift apply uniformly to every wrapper variant.
            var keys = WrapperEmitterHelpers.CollectStrictestAvailabilityKeys(annotations);
            if (keys.Count == 0)
                return "";

            var sb = new System.Text.StringBuilder();
            foreach (var key in keys)
                sb.Append(indent).Append("@available(").Append(key).Append(", *)\n");
            return sb.ToString();
        }

        /// <summary>
        /// Builds the holder cleanup loop code for freeing async call resources.
        /// Handles RetainedSelfPtr, DeferredSafeHandleRelease, CopyBufferWithType, and CancellationRegistrationHolder.
        /// </summary>
        /// <param name="holderVar">The variable name for the holder array (e.g., "holder" or "_asyncCallHolder").</param>
        /// <param name="indent">The whitespace indent prefix for each line.</param>
        /// <param name="includeCancellationReg">Whether to include CancellationRegistrationHolder cleanup.</param>
        /// <param name="cancelRegVarName">Variable name for the CancellationRegistrationHolder (to avoid shadowing).</param>
        // MIRROR with WrapperEmitter.Async.BuildHolderCleanupCode. Any new holder
        // slot type (RetainedSelfPtr, ExistentialContainerHeap, ...) must be added
        // here AND in WrapperEmitter.Async.BuildHolderCleanupCode AND in
        // BuildCancellationCleanupLoop (used by both BuildErrorCallbackBlock helpers)
        // or the slot will leak on success / exception / cancellation paths.
        public static string BuildHolderCleanupCode(string holderVar, string indent, bool includeCancellationReg = true, string cancelRegVarName = "cancelReg")
        {
            var cancelRegLine = includeCancellationReg
                ? $"\n{indent}    else if ({holderVar}[i] is CancellationRegistrationHolder {cancelRegVarName})\n{indent}        {cancelRegVarName}.Registration.Dispose();"
                : "";
            // AsyncDeferredDisposeList holds SwiftArray/Set/Dictionary containers whose
            // 'using var' was hoisted into the holder by EmitAsync. Disposed here on every
            // callback path (success / exception / cancellation) so the buffer is freed
            // exactly once after the Swift continuation has read it.
            return $$"""
                {{indent}}for (int i = 1; i < {{holderVar}}.Length; i++)
                {{indent}}{
                {{indent}}    if ({{holderVar}}[i] is RetainedSelfPtr retained && retained.Ptr != IntPtr.Zero)
                {{indent}}        Arc.Release(retained.Ptr);
                {{indent}}    else if ({{holderVar}}[i] is DeferredSafeHandleRelease deferred)
                {{indent}}        deferred.Handle.DangerousRelease();
                {{indent}}    else if ({{holderVar}}[i] is CopyBufferWithType copyBuffer && copyBuffer.Buffer != IntPtr.Zero)
                {{indent}}    {
                {{indent}}        copyBuffer.Metadata.ValueWitnessTable->Destroy((void*)copyBuffer.Buffer, copyBuffer.Metadata);
                {{indent}}        NativeMemory.Free((void*)copyBuffer.Buffer);
                {{indent}}    }
                {{indent}}    else if ({{holderVar}}[i] is ExistentialContainerHeap existentialHeap && existentialHeap.Ptr != IntPtr.Zero)
                {{indent}}        NativeMemory.Free((void*)existentialHeap.Ptr);
                {{indent}}    else if ({{holderVar}}[i] is AsyncDeferredDisposeList __deferredList)
                {{indent}}    {
                {{indent}}        foreach (var __d in __deferredList.Items) __d.Dispose();
                {{indent}}    }{{cancelRegLine}}
                {{indent}}}
                """;
        }

        /// <summary>
        /// Builds the cancellation-path holder cleanup loop emitted inside
        /// <c>BuildErrorCallbackBlock</c> when Swift reports CancellationError.
        /// Identical slot-walk to <see cref="BuildHolderCleanupCode"/>, but with
        /// <c>CancellationRegistrationHolder</c> as the FIRST branch so the loop
        /// can capture <c>cancelToken</c> before disposing the registration.
        /// Shared between this file and <c>WrapperEmitter.Async</c> so the two
        /// hand-rolled cancellation blocks cannot drift apart.
        /// </summary>
        // MIRROR with BuildHolderCleanupCode and WrapperEmitter.Async.BuildHolderCleanupCode.
        // Any new holder slot type must also appear in those two helpers.
        internal static string BuildCancellationCleanupLoop(string holderVar, string loopVarName, string indent)
        {
            return $$"""
                {{indent}}for (int {{loopVarName}} = 1; {{loopVarName}} < {{holderVar}}.Length; {{loopVarName}}++)
                {{indent}}{
                {{indent}}    if ({{holderVar}}[{{loopVarName}}] is CancellationRegistrationHolder cancelReg)
                {{indent}}    {
                {{indent}}        cancelToken = cancelReg.Token;
                {{indent}}        cancelReg.Registration.Dispose();
                {{indent}}    }
                {{indent}}    else if ({{holderVar}}[{{loopVarName}}] is RetainedSelfPtr retained && retained.Ptr != IntPtr.Zero)
                {{indent}}        Arc.Release(retained.Ptr);
                {{indent}}    else if ({{holderVar}}[{{loopVarName}}] is DeferredSafeHandleRelease deferred)
                {{indent}}        deferred.Handle.DangerousRelease();
                {{indent}}    else if ({{holderVar}}[{{loopVarName}}] is CopyBufferWithType copyBuffer && copyBuffer.Buffer != IntPtr.Zero)
                {{indent}}    {
                {{indent}}        copyBuffer.Metadata.ValueWitnessTable->Destroy((void*)copyBuffer.Buffer, copyBuffer.Metadata);
                {{indent}}        NativeMemory.Free((void*)copyBuffer.Buffer);
                {{indent}}    }
                {{indent}}    else if ({{holderVar}}[{{loopVarName}}] is ExistentialContainerHeap existentialHeap && existentialHeap.Ptr != IntPtr.Zero)
                {{indent}}        NativeMemory.Free((void*)existentialHeap.Ptr);
                {{indent}}    else if ({{holderVar}}[{{loopVarName}}] is AsyncDeferredDisposeList __deferredList)
                {{indent}}    {
                {{indent}}        foreach (var __d in __deferredList.Items) __d.Dispose();
                {{indent}}    }
                {{indent}}}
                """;
        }

        /// <summary>
        /// Determines if a TypeSpec represents Swift.Array&lt;Swift.String&gt;.
        /// Used to detect array-of-string returns that need flat buffer serialization in async callbacks.
        /// </summary>
        public bool IsArrayOfString(TypeSpec typeSpec)
        {
            if (typeSpec is not NamedTypeSpec namedType)
                return false;

            // Guard against DynamicSelf or unqualified names
            if (!namedType.Name.Contains('.'))
                return false;

            // Check if it's Swift.Array
            var typeName = SwiftTypeName.FromTypeSpec(namedType);
            if (typeName.ModuleQualifiedName != "Swift.Array")
                return false;

            // Check if it has exactly one generic parameter
            if (namedType.GenericParameters.Count != 1)
                return false;

            // Check if the generic parameter is Swift.String
            var elementType = namedType.GenericParameters[0];
            return elementType.ToString() == "Swift.String";
        }

        /// <summary>
        /// Checks if a Swift type name is a primitive type that can be passed directly
        /// through @convention(c) callbacks without pointer indirection.
        /// Delegates to the canonical implementation in ClosureEmitter.
        /// </summary>
        private static bool IsSwiftPrimitive(string swiftTypeName)
            => ClosureEmitter.IsSwiftPrimitive(swiftTypeName);
    }
}
