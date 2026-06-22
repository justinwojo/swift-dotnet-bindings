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
    /// Emits the C# async callback plumbing (TCS, GCHandle, UnmanagedCallersOnly callbacks,
    /// error callbacks) for async methods. This is the sole live C# async emitter.
    /// The matching Swift <c>@_cdecl</c>/<c>@_silgen_name</c> wrapper body is emitted by the
    /// live Swift-side path <see cref="WrapperEmitter"/>.<c>Async.EmitAsync</c>; this class no
    /// longer owns any Swift emission (the former <c>BuildSwift*</c> duplicate was deleted).
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

            // Plain-throws cascade gate — mirrors the resolver in WrapperEmitter.cs.
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
            var unregisterSymbolName = CancellationTaskEmitter.GetUnregisterSymbolName(moduleDecl.Name);
            var typeKey = (_env.ParentDecl as TypeDecl)?.SwiftTypeName.ModuleQualifiedName ?? moduleDecl.Name;
            if (!CancellationTaskEmitter.HasCancelPInvokeForType(typeKey, _emissionContext))
            {
                CancellationTaskEmitter.MarkCancelPInvokeEmittedForType(typeKey, _emissionContext);
                // SBW_CancelTask / SBW_UnregisterTask P/Invokes: hoist to helper for generic types, emit inline otherwise
                var cancelWriter = _env.PInvokeHelperContext != null ? callbackWriter : csWriter;
                cancelWriter.WriteLine("[global::System.Runtime.InteropServices.UnmanagedCallConv(CallConvs = new global::System.Type[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]");
                cancelWriter.WriteLines($"""
                    [global::System.Runtime.InteropServices.LibraryImport("{wrapperLibPath}", EntryPoint = "{cancelSymbolName}")]
                    {AsyncFieldVisibility} static partial void SBW_CancelTask(long taskId);

                    """);
                cancelWriter.WriteLine("[global::System.Runtime.InteropServices.UnmanagedCallConv(CallConvs = new global::System.Type[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]");
                cancelWriter.WriteLines($"""
                    [global::System.Runtime.InteropServices.LibraryImport("{wrapperLibPath}", EntryPoint = "{unregisterSymbolName}")]
                    {AsyncFieldVisibility} static partial void SBW_UnregisterTask(long taskId);

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

            var callbackFieldName = NameProvider.GetAsyncCallbackFieldName(_env.EmissionSymbol, _env.MethodDecl);
            var callbackMethodName = NameProvider.GetAsyncCallbackMethodName(_env.EmissionSymbol, _env.MethodDecl);
            var errorCallbackFieldName = NameProvider.GetAsyncErrorCallbackFieldName(_env.EmissionSymbol, _env.MethodDecl);
            var errorCallbackMethodName = NameProvider.GetAsyncErrorCallbackMethodName(_env.EmissionSymbol, _env.MethodDecl);

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
                out var runtimeType, out var conversionExpr, out var collectionUsesObjCBridge,
                out var collectionProxySuppressed))
            {
                EmitAsyncWrapperForCollection(callbackWriter, callbackFieldName, callbackMethodName,
                    errorCallbackFieldName, errorCallbackMethodName, runtimeType, conversionExpr,
                    collectionUsesObjCBridge, collectionProxySuppressed);
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
                //
                // The ownership algebra (cbTakesOwnership / carrierNeedsDestroy, plus the
                // Optional<value-type> widening) is the S13 Pillar A single source in
                // AsyncResultPlanner — see AsyncResultPlan.cs for the full rationale.
                bool cbTakesOwnership = false;
                bool carrierNeedsDestroy = false;
                if (!isClassType && !isObjCBridgeableValue && !isOptionalClassType && !isOptionalObjCContainer && complexTypeRecord != null)
                {
                    var ownership = AsyncResultPlanner.ClassifyCarrierOwnership(complexTypeRecord);
                    cbTakesOwnership = ownership.CallbackTakesOwnership;
                    carrierNeedsDestroy = ownership.CarrierNeedsDestroy;
                }
                if (!carrierNeedsDestroy && !isClassType && !isObjCBridgeableValue && !isOptionalClassType && !isOptionalObjCContainer)
                {
                    carrierNeedsDestroy = AsyncResultPlanner.WidenDestroyForOptionalPayload(returnType.SwiftTypeSpec, _env.TypeDatabase);
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

            // Callback-ownership contract: the emitted Swift wrapper (WrapperEmitter.Async.cs) is a
            // single `Task { do { … callback(_sbwTask) } catch { errorCallback(_sbwTask) } }`, so exactly
            // ONE of {success, error} fires exactly ONCE per `task` GCHandle cookie — the success call is
            // the last `do` statement, a @convention(c) callback cannot throw a Swift error into the `do`,
            // and cancellation is cooperative (task.cancel() sets a flag, it does not re-invoke a callback).
            // The GCHandle therefore has a SINGLE freer (this finally), so handle.Free() is intentionally
            // NOT idempotent — unlike the TCS, which genuinely has two writers (the C# token registration's
            // TrySetCanceled and this callback's TrySetResult) and so uses the Try* idempotent setters.
            // Do not add speculative resume-once guarding to the free: it would mask a genuine future
            // double-callback regression instead of surfacing it through the fault catch.
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
                                // Handle both cases: direct TCS or typed SwiftAsyncCallHolder (with copy buffer pointers etc.)
                                if (handle.Target is global::Swift.Runtime.SwiftAsyncCallHolder holder && holder.Tcs is TaskCompletionSource{{(voidReturn ? "" : $"<{_wrapperSignature.ReturnType}>")}} holderTcs)
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
                {{BuildAsyncCallbackFaultCatch(voidReturn ? "" : $"<{_wrapperSignature.ReturnType}>", "            ")}}
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
                                // Handle both cases: direct TCS or typed SwiftAsyncCallHolder (with copy buffer pointers etc.)
                                if (handle.Target is global::Swift.Runtime.SwiftAsyncCallHolder holder && holder.Tcs is TaskCompletionSource<{{_wrapperSignature.ReturnType}}> holderTcs)
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
                {{BuildAsyncCallbackFaultCatch($"<{_wrapperSignature.ReturnType}>", "            ")}}
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

                                // Handle both cases: direct TCS or typed SwiftAsyncCallHolder (with copy buffer pointers etc.)
                                if (handle.Target is global::Swift.Runtime.SwiftAsyncCallHolder holder && holder.Tcs is TaskCompletionSource<{{_wrapperSignature.ReturnType}}> holderTcs)
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
                {{BuildAsyncCallbackFaultCatch($"<{_wrapperSignature.ReturnType}>", "            ")}}
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
                                // Handle both cases: direct TCS or typed SwiftAsyncCallHolder (with copy buffer pointers etc.)
                                if (handle.Target is global::Swift.Runtime.SwiftAsyncCallHolder holder && holder.Tcs is TaskCompletionSource<{{_wrapperSignature.ReturnType}}> holderTcs)
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
                {{BuildAsyncCallbackFaultCatch($"<{_wrapperSignature.ReturnType}>", "            ")}}
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
                // Finding 53: an async existential that collapses to bare `object` returns the
                // untyped __existentialResult below — record the loud SWIFTBIND026 degradation.
                if (asyncPublicType == "object")
                    ReportCollector.RecordObjectDegradation(asyncProtocolList.ToString() ?? "<unknown>");
                // Owned-return ctor arg (`, ownsContainer: true`) for EC1/EC2+ proxies, mirroring
                // WrapperEmitter.OwnedExistentialCtorArg; gated on the container TYPE via the shared
                // ExistentialHandler predicate (not protocol count — ObjC filtering can drop protocols).
                var asyncOwnedArg = ExistentialHandler.IsOwnedExistentialContainerType(asyncContainerType)
                    ? ", ownsContainer: true"
                    : string.Empty;
                string asyncWrapExpr;
                bool asyncProxySuppressed = false;
                if (asyncProtocolList.Protocols.Count == 0 || asyncPublicType == "object")
                    asyncWrapExpr = "__existentialResult";
                else if (asyncPublicType == "Swift.Runtime.ExistentialUnion")
                    asyncWrapExpr = "new Swift.Runtime.ExistentialUnion(__existentialResult)";
                else if (_env.ExistentialHandler.TryGetWellKnownProtocolType(asyncProtocolList, out var asyncWkIR))
                    asyncWrapExpr = $"new {asyncWkIR}(__existentialResult)";
                else if (_env.ExistentialHandler.IsProxyReferenceSuppressed(asyncProtocolList, _emissionContext))
                {
                    // The protocol proxy that would marshal this async existential result was
                    // suppressed (its EveryProtocol conformance could not be emitted — e.g. an
                    // `init()` requirement). There is no concrete type to construct, so fault the
                    // awaiting Task instead of referencing the absent proxy class. The fault is
                    // emitted as the `result` initializer below, so it throws inside the completion
                    // callback's try — routing to TrySetException (observable to the awaiter) and
                    // freeing the carrier + GCHandle in finally. This preserves the async lifecycle,
                    // unlike a silent no-op callback that would leave the awaiting Task hanging.
                    asyncProxySuppressed = true;
                    asyncWrapExpr = "default";
                }
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
                // Suppressed proxy: fault the awaiting Task. A bare `T result = throw …;` is not a
                // legal throw-expression context (CS8115), and the downstream TrySetResult(result)
                // requires `result` to be definitely assigned, so route the throw through a `?:`
                // whose value-typed arm keeps `result`'s type while the throw always fires.
                //
                // The carrier still holds the Swift-vended +1 even on this fault path: the @_cdecl
                // wrapper ran to completion and `initializeMemory(as: (any P).self, repeating:)`'d the
                // existential into the carrier (it cannot know the C# proxy was suppressed), so the
                // carrier holds the existential's value-witness +1. Release it BEFORE throwing, or it
                // leaks every call. The correction is per existential SHAPE — the same class-bound vs
                // opaque split the read above uses:
                //  • class-bound `any P` — a compact 2-word [classRef][witnessTable] cell whose +1 is
                //    a retained class reference at word 0. Balance it with an unknown-object release
                //    (the conformer may be an ObjC class, so route through the kind-dispatching entry
                //    point, not native-only swift_release). No VWT Destroy: there is no opaque
                //    value-witness table, and the 16-byte cell has no registered container metadata.
                //  • opaque `any P` — a 5-word container; release via the arity-based existential
                //    metadata's value-witness Destroy. The destroy is structural — it follows the
                //    payload's embedded metadata/witness-table words, so it does not depend on the
                //    protocol's identity (the runtime resolves marker-protocol existential metadata
                //    keyed only on the witness-table slot count). This is the same per-element destroy
                //    the shipped optional/collection container arms already rely on.
                // The opaque carrier's metadata is the arity-based existential metadata, NOT a
                // SwiftObjectHelper<T> lookup: ExistentialContainer{N} implements IExistentialContainer,
                // not ISwiftObject, so it cannot satisfy SwiftObjectHelper's constraint. The arity is the
                // non-marker protocol count GetCSharpExistentialType embeds in asyncContainerType
                // (ExistentialContainer{N}); GetExistentialTypeMetadata(N) yields container metadata whose
                // structural value-witness Destroy releases the payload's +1 — the same opaque-existential
                // metadata path WrapperEmitter and the proxy emitters already use. The per-shape release
                // is built by BuildSuppressedExistentialCarrierRelease (unit-locked there).
                var asyncExistentialArity = ExistentialHandler.GetNonMarkerProtocols(asyncProtocolList).Count;
                var asyncSuppressedRelease = BuildSuppressedExistentialCarrierRelease(
                    _env.ExistentialHandler.IsClassBoundArity1Existential(asyncProtocolList),
                    asyncExistentialArity,
                    "                                ");
                marshalResultCode = asyncProxySuppressed
                    ? $"{asyncSuppressedRelease}{asyncPublicType} result = true ? throw new global::System.NotSupportedException(\"Protocol proxy for '{asyncPublicType}' was not emitted (its EveryProtocol conformance is unavailable); cannot marshal the async existential result.\") : default;"
                    : $"var __existentialResult = {asyncExistentialRead};\n" +
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
            else if (TryGetOptionalMarshalType(out var optionalMarshalType, out var objcBridgeConversion, out var containerBridgeConversion, out var valueContainerInnerConversion, out var optionalProxySuppressed))
            {
                if (optionalProxySuppressed)
                {
                    // Optional<container<existential>> whose element proxy was suppressed (EveryProtocol
                    // conformance unavailable). Fault the awaiting Task — same contract as the
                    // non-optional existential suppressed branch above: the throw fires inside the
                    // completion callback's try → TrySetException (observable to the awaiter), and the
                    // finally releases the GCHandle. A bare `T result = throw …;` is not a legal
                    // throw-expression context (CS8115) and TrySetResult(result) requires definite
                    // assignment, so route the throw through a `?:` whose value-typed arm keeps result's
                    // type while the throw always fires.
                    //
                    // The carrier still holds the Swift-vended +1 even on the fault path: the Swift
                    // @_cdecl wrapper ran to completion and `initializeMemory(as: Optional<SwiftArray<…>>.self)`'d
                    // the .some payload (it cannot know the C# proxy was suppressed), so the carrier holds
                    // +1 on the embedded container storage exactly as the non-suppressed
                    // valueContainerInnerConversion branch below — which VWT-Destroys the carrier before
                    // SBW_Free. Mirror that release here BEFORE throwing, or the .some([…]) storage's +1
                    // leaks every call. optionalMarshalType (op.ContainerTypeName) is set before the
                    // suppression catch, so it names the raw SwiftOptional<SwiftArray<…>> carrier type and
                    // never references the absent proxy. Destroy runs first; the throw then fires (result
                    // is never read — the catch faults the Task), and the shared finally's SBW_Free reclaims
                    // the now-released raw allocation.
                    var optPublicType = _wrapperSignature.ReturnType;
                    marshalResultCode =
                        $"var _vwtMetadata = SwiftObjectHelper<{optionalMarshalType}>.GetTypeMetadata();\n" +
                        $"                                _vwtMetadata.ValueWitnessTable->Destroy((void*)resultPtr, _vwtMetadata);\n" +
                        $"                                {optPublicType} result = true ? throw new global::System.NotSupportedException(\"Protocol proxy for '{optPublicType}' was not emitted (its EveryProtocol conformance is unavailable); cannot marshal the async optional existential result.\") : default;";
                }
                else if (containerBridgeConversion != null)
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
                    // Resolve the metadata first, then release in a finally so a marshal/conversion
                    // throw cannot orphan the carrier's +1.
                    marshalResultCode =
                        $"var _vwtMetadata = SwiftObjectHelper<{optionalMarshalType}>.GetTypeMetadata();\n" +
                        $"                                {_wrapperSignature.ReturnType} result;\n" +
                        $"                                try\n" +
                        $"                                {{\n" +
                        $"                                    var _rawResult = SwiftMarshal.MarshalFromSwift<{optionalMarshalType}>(resultPtr).ToNullable();\n" +
                        $"                                    result = _rawResult is {{ }} _rawCol ? {valueContainerInnerConversion} : null;\n" +
                        $"                                }}\n" +
                        $"                                finally\n" +
                        $"                                {{\n" +
                        $"                                    _vwtMetadata.ValueWitnessTable->Destroy((void*)resultPtr, _vwtMetadata);\n" +
                        $"                                }}";
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
                    // Resolve the metadata first, then release in a finally so a marshal-throw cannot
                    // orphan the carrier's +1.
                    marshalResultCode =
                        $"var _vwtMetadata = SwiftObjectHelper<{optionalMarshalType}>.GetTypeMetadata();\n" +
                        $"                                {_wrapperSignature.ReturnType} result;\n" +
                        $"                                try\n" +
                        $"                                {{\n" +
                        $"                                    var _swiftOpt = SwiftMarshal.MarshalFromSwift<{optionalMarshalType}>(resultPtr);\n" +
                        $"                                    result = _swiftOpt.HasValue ? ({_wrapperSignature.ReturnType})_swiftOpt.Some : default;\n" +
                        $"                                }}\n" +
                        $"                                finally\n" +
                        $"                                {{\n" +
                        $"                                    _vwtMetadata.ValueWitnessTable->Destroy((void*)resultPtr, _vwtMetadata);\n" +
                        $"                                }}";
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
                //
                // The marshal runs in a try so a throw cannot orphan a +1: finally releases the
                // carrier's +1 (success and throw alike); on success the managed result owns _vwtBuf
                // (freed by its SafeHandle), but if MarshalFromSwift throws before that adoption the
                // catch releases the copy's +1 and frees _vwtBuf so it does not leak alongside.
                marshalResultCode =
                    $"var _vwtMetadata = SwiftObjectHelper<{_wrapperSignature.ReturnType}>.GetTypeMetadata();\n" +
                    $"                                IntPtr _vwtBuf = (IntPtr)NativeMemory.Alloc(_vwtMetadata.Size);\n" +
                    $"                                _vwtMetadata.ValueWitnessTable->InitializeWithCopy((void*)_vwtBuf, (void*)resultPtr, _vwtMetadata);\n" +
                    $"                                {_wrapperSignature.ReturnType} result;\n" +
                    $"                                try\n" +
                    $"                                {{\n" +
                    $"                                    result = SwiftMarshal.MarshalFromSwift<{_wrapperSignature.ReturnType}>(_vwtBuf);\n" +
                    $"                                }}\n" +
                    $"                                catch\n" +
                    $"                                {{\n" +
                    $"                                    _vwtMetadata.ValueWitnessTable->Destroy((void*)_vwtBuf, _vwtMetadata);\n" +
                    $"                                    NativeMemory.Free((void*)_vwtBuf);\n" +
                    $"                                    throw;\n" +
                    $"                                }}\n" +
                    $"                                finally\n" +
                    $"                                {{\n" +
                    $"                                    _vwtMetadata.ValueWitnessTable->Destroy((void*)resultPtr, _vwtMetadata);\n" +
                    $"                                }}";
            }
            else if (carrierNeedsDestroy)
            {
                // Frozen-with-memory struct (ClassWithBufferStruct, e.g. @frozen with String field).
                // NewFromPayload runs its own InitializeWithCopy into a managed buffer — the returned
                // C# object holds its own +1 independent of the carrier. We only need to release the
                // carrier's +1 (from the Swift-side initializeMemory) before SBW_Free. Resolve the
                // metadata first, then release in a finally so a marshal-throw cannot orphan the +1.
                marshalResultCode =
                    $"var _vwtMetadata = SwiftObjectHelper<{_wrapperSignature.ReturnType}>.GetTypeMetadata();\n" +
                    $"                                {_wrapperSignature.ReturnType} result;\n" +
                    $"                                try\n" +
                    $"                                {{\n" +
                    $"                                    result = SwiftMarshal.MarshalFromSwift<{_wrapperSignature.ReturnType}>(resultPtr);\n" +
                    $"                                }}\n" +
                    $"                                finally\n" +
                    $"                                {{\n" +
                    $"                                    _vwtMetadata.ValueWitnessTable->Destroy((void*)resultPtr, _vwtMetadata);\n" +
                    $"                                }}";
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

                                // Handle both cases: direct TCS or typed SwiftAsyncCallHolder (with copy buffer pointers etc.)
                                if (handle.Target is global::Swift.Runtime.SwiftAsyncCallHolder holder && holder.Tcs is TaskCompletionSource<{{_wrapperSignature.ReturnType}}> holderTcs)
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
                {{BuildAsyncCallbackFaultCatch($"<{_wrapperSignature.ReturnType}>", "            ")}}
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
            out bool usesObjCContainerBridge,
            out bool proxySuppressed)
        {
            runtimeType = null;
            conversionExpr = null;
            usesObjCContainerBridge = false;
            proxySuppressed = false;

            // Thread EmissionContext so the element projection's proxy-suppression gate is armed:
            // an existential element (e.g. `[any Boxable]`) whose EveryProtocol conformance could
            // not be emitted makes GetReturnContainerConversion throw SuppressedProxyReferenceException
            // (the PRODUCE arm) instead of silently emitting `new {Proxy}(…)`. The throw is caught
            // here — during pure string projection, before EmitAsyncWrapperForCollection writes any
            // callback body (Hazard-D-safe) — and surfaced as proxySuppressed so the completion
            // callback faults the awaiting Task rather than referencing the absent proxy class.
            var ctx = new ProjectionContext
            {
                TypeDatabase = _env.TypeDatabase,
                IsParameter = false,
                IsAsync = false,
                EmissionContext = _emissionContext
            };

            var projection = s_projectionFactory.Project(returnTypeSpec, ctx);
            if (projection is ArrayProjection ap)
            {
                usesObjCContainerBridge = ap.UsesObjCContainerBridge;
                runtimeType = ap.ContainerTypeName;
                try
                {
                    conversionExpr = usesObjCContainerBridge
                        ? ap.GetReturnContainerConversion("_ptr")!
                        : ap.GetReturnContainerConversion("_collection")!;
                }
                catch (SuppressedProxyReferenceException)
                {
                    proxySuppressed = true;
                    conversionExpr = string.Empty;
                }
                return true;
            }
            if (projection is DictionaryProjection dp)
            {
                usesObjCContainerBridge = dp.UsesObjCContainerBridge;
                runtimeType = dp.ContainerTypeName;
                try
                {
                    conversionExpr = usesObjCContainerBridge
                        ? dp.GetReturnContainerConversion("_ptr")!
                        : dp.GetReturnContainerConversion("_collection")!;
                }
                catch (SuppressedProxyReferenceException)
                {
                    proxySuppressed = true;
                    conversionExpr = string.Empty;
                }
                return true;
            }
            if (projection is SetProjection sp)
            {
                usesObjCContainerBridge = sp.UsesObjCContainerBridge;
                runtimeType = sp.ContainerTypeName;
                try
                {
                    // SetProjection returns null when no element conversion is needed
                    // (SwiftSet<T> already implements IReadOnlySet<T>). Use identity.
                    conversionExpr = usesObjCContainerBridge
                        ? sp.GetReturnContainerConversion("_ptr")!
                        : (sp.GetReturnContainerConversion("_collection") ?? "_collection");
                }
                catch (SuppressedProxyReferenceException)
                {
                    proxySuppressed = true;
                    conversionExpr = string.Empty;
                }
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
            out string? valueContainerInnerConversion,
            out bool proxySuppressed)
        {
            marshalType = null;
            objcBridgeConversion = null;
            containerBridgeConversion = null;
            valueContainerInnerConversion = null;
            proxySuppressed = false;
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

                try
                {
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
                }
                catch (SuppressedProxyReferenceException)
                {
                    // PRODUCE arm: the optional's inner container element is an existential whose
                    // EveryProtocol proxy was suppressed. Mirror the non-optional collection-return
                    // path — surface a flag so the completion callback faults the awaiting Task
                    // instead of constructing the absent `new {Proxy}(`. The throw fired during pure
                    // string projection, before any callback body was written (Hazard-D-safe).
                    proxySuppressed = true;
                    objcBridgeConversion = null;
                    containerBridgeConversion = null;
                    valueContainerInnerConversion = null;
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
                IsAsync = false,
                // Thread EmissionContext so an Optional<container<existential>> return whose element's
                // proxy was suppressed makes the inner container's GetReturnContainerConversion throw
                // SuppressedProxyReferenceException (PRODUCE arm) — caught in TryGetOptionalMarshalType
                // and surfaced as proxySuppressed — instead of emitting a dangling `new {Proxy}(`. The
                // Project() call itself never throws (only conversion-string building does), so the
                // probe-only callers (IsTopLevelObjCBridgeContainerReturn / IsOptionalObjCBridgeContainerReturn)
                // are unaffected.
                EmissionContext = _emissionContext
            };
            return s_projectionFactory.Project(returnSpec, ctx);
        }

        /// <summary>
        /// Builds the completion-callback body that revives an async collection result from the
        /// carrier and balances the carrier's value-witness <c>+1</c>. Three arms, one per carrier
        /// shape:
        /// <list type="bullet">
        ///   <item><c>proxySuppressed</c> — the existential element's proxy class was not emitted,
        ///   so there is no per-element constructor; fault the awaiting Task instead of marshalling
        ///   (a bare <c>T result = throw …;</c> is not a legal throw-expression context (CS8115) and
        ///   the downstream <c>TrySetResult(result)</c> needs definite assignment, so the throw is
        ///   routed through a <c>?:</c> whose value-typed arm keeps <c>result</c>'s type). The Swift
        ///   wrapper still <c>initializeMemory(as: &lt;Container&gt;.self)</c>'d the container into the
        ///   carrier (it cannot know the C# per-element proxy was suppressed), so the carrier holds a
        ///   <c>+1</c> on the CoW storage — released via the concrete container's value-witness Destroy
        ///   BEFORE the throw, exactly as the plain arm does on success. <paramref name="runtimeType"/>
        ///   names the raw <c>SwiftArray&lt;ExistentialContainer1&gt;</c> /
        ///   <c>SwiftArray&lt;ClassExistentialContainer1&gt;</c> / … carrier (set before the suppression
        ///   catch), never the absent proxy, so the array's element-destroy runs at the correct element
        ///   stride; the suppression only blocks the per-element C# conversion, not the carrier layout.
        ///   This mirrors the shipped optional-container suppressed arm; without it the backing storage
        ///   leaks every call (these are fault-only methods that never return a value).</item>
        ///   <item><c>usesObjCContainerBridge</c> — the Swift wrapper stored a <c>+1</c>-retained
        ///   NS-collection POINTER in the carrier (a pointer-bit carrier, NOT a value-witness value).
        ///   Read it and bridge through the projection's IntPtr-shaped conversion. A VWT Destroy here
        ///   would be WRONG (the 8-byte holder is not a Swift container value) — the <c>+1</c> is on
        ///   the NS object and is released through the bridge.</item>
        ///   <item>plain Swift collection — the wrapper wrote the result via
        ///   <c>initializeMemory(as: &lt;Container&gt;.self)</c>, running the container's copy witness,
        ///   so the carrier holds a <c>+1</c> on the CoW storage. <c>MarshalFromSwift</c> takes an
        ///   independent <c>+1</c> (NewFromPayload → InitializeWithCopy), so the carrier's <c>+1</c>
        ///   is released via VWT Destroy. The Destroy is placed BEFORE the projection conversion
        ///   (which reads only the independent copy) so the <c>+1</c> stays balanced even if the
        ///   conversion throws.</item>
        /// </list>
        /// Extracted as a static builder so the carrier-release contract is unit-testable directly:
        /// the collection arm bypasses <see cref="AsyncResultPlanner"/>, so the planner tests do not
        /// cover it. <paramref name="continuationIndent"/> is the leading whitespace for every line
        /// after the first (the first line inherits the indent of the <c>{marshalLines}</c> insertion
        /// site), so the assembled body lands byte-identically in the callback template.
        /// </summary>
        internal static string BuildCollectionCarrierMarshalLines(
            string runtimeType, string conversionExpr, string returnType,
            bool usesObjCContainerBridge, bool proxySuppressed, string continuationIndent)
        {
            return proxySuppressed
                ? $"// The Swift wrapper still initializeMemory(as: <Container>.self)'d the existential container\n" +
                  $"{continuationIndent}// into the carrier (it cannot know the C# per-element proxy was suppressed), so the\n" +
                  $"{continuationIndent}// carrier holds a +1 on the CoW storage. Release it via the concrete container's\n" +
                  $"{continuationIndent}// value-witness Destroy BEFORE faulting — runtimeType names the raw\n" +
                  $"{continuationIndent}// SwiftArray<ExistentialContainer1>/SwiftArray<ClassExistentialContainer1>/… carrier\n" +
                  $"{continuationIndent}// (set before the suppression catch), never the absent proxy, so the array's\n" +
                  $"{continuationIndent}// element-destroy runs at the correct element stride. Mirrors the shipped\n" +
                  $"{continuationIndent}// optional-container suppressed arm; without it the backing storage leaks every call.\n" +
                  $"{continuationIndent}var _vwtMetadata = SwiftObjectHelper<{runtimeType}>.GetTypeMetadata();\n" +
                  $"{continuationIndent}_vwtMetadata.ValueWitnessTable->Destroy((void*)resultPtr, _vwtMetadata);\n" +
                  $"{continuationIndent}{returnType} result = true ? throw new global::System.NotSupportedException(\"Protocol proxy for the element of '{returnType}' was not emitted (its EveryProtocol conformance is unavailable); cannot marshal the async existential collection result.\") : default;"
                : usesObjCContainerBridge
                ? $"// ObjC-bridge collection: read +1 retained NS-collection pointer from carrier\n" +
                  $"{continuationIndent}IntPtr _ptr = *(IntPtr*)resultPtr;\n" +
                  $"{continuationIndent}var result = {conversionExpr};"
                : $"// Marshal collection from Swift-allocated memory using runtime container type.\n" +
                  $"{continuationIndent}// The Swift async wrapper wrote the result via initializeMemory(as: <Container>.self),\n" +
                  $"{continuationIndent}// running the container's copy witness — the carrier holds a +1 on the CoW buffer.\n" +
                  $"{continuationIndent}// MarshalFromSwift/NewFromPayload takes its OWN independent +1 (InitializeWithCopy into a\n" +
                  $"{continuationIndent}// managed buffer), so release the carrier's +1 via VWT Destroy in a finally — covering the\n" +
                  $"{continuationIndent}// marshal-throw window too — before the projection conversion below (which reads only the\n" +
                  $"{continuationIndent}// independent _collection copy) and before SBW_Free reclaims the raw allocation. Destroying\n" +
                  $"{continuationIndent}// in finally keeps the carrier's +1 balanced even if marshal or conversion throws.\n" +
                  $"{continuationIndent}var _vwtMetadata = SwiftObjectHelper<{runtimeType}>.GetTypeMetadata();\n" +
                  $"{continuationIndent}{runtimeType} _collection;\n" +
                  $"{continuationIndent}try\n" +
                  $"{continuationIndent}{{\n" +
                  $"{continuationIndent}    _collection = SwiftMarshal.MarshalFromSwift<{runtimeType}>(resultPtr);\n" +
                  $"{continuationIndent}}}\n" +
                  $"{continuationIndent}finally\n" +
                  $"{continuationIndent}{{\n" +
                  $"{continuationIndent}    _vwtMetadata.ValueWitnessTable->Destroy((void*)resultPtr, _vwtMetadata);\n" +
                  $"{continuationIndent}}}\n" +
                  $"{continuationIndent}var result = {conversionExpr};";
        }

        /// <summary>
        /// Builds the C# lines that release the Swift-vended carrier +1 on the suppressed-proxy
        /// SCALAR existential async fault path, BEFORE the awaiting Task is faulted and the shared
        /// finally's SBW_Free reclaims the raw allocation. The Swift @_cdecl wrapper ran to
        /// completion and initializeMemory'd the existential into the carrier (it cannot know the C#
        /// proxy was suppressed), so the carrier holds the existential's value-witness +1; without
        /// this release it leaks every call. The correction is per existential SHAPE:
        /// <list type="bullet">
        /// <item>class-bound <c>any P: AnyObject</c> — a compact [classRef][witnessTable] cell whose
        /// +1 is the retained class reference at word 0; balance it with the kind-dispatching
        /// unknown-object release (the conformer may be an ObjC class, so not native-only
        /// swift_release). There is no opaque value-witness table to Destroy.</item>
        /// <item>opaque <c>any P</c> — a 5-word container; Destroy through the ARITY-based existential
        /// metadata. The destroy is structural (it follows the payload's embedded metadata/witness
        /// words), so it does not depend on the protocol's identity. This is NOT a
        /// <c>SwiftObjectHelper&lt;ExistentialContainer{N}&gt;</c> lookup: <c>ExistentialContainer{N}</c>
        /// implements <c>IExistentialContainer</c>, not <c>ISwiftObject</c>, so it cannot satisfy
        /// SwiftObjectHelper's constraint (CS0315).</item>
        /// </list>
        /// <paramref name="arity"/> is the non-marker protocol count <c>GetCSharpExistentialType</c>
        /// embeds in the carrier type (<c>ExistentialContainer{N}</c>). <paramref name="continuationIndent"/>
        /// is prepended to trailing lines so the block nests under the completion callback's indentation.
        /// The returned string is a prefix: the caller appends the value-typed fault throw immediately
        /// after it (the release runs first, then the throw fires).
        /// </summary>
        internal static string BuildSuppressedExistentialCarrierRelease(
            bool isClassBound, int arity, string continuationIndent)
        {
            return isClassBound
                ? $"Swift.Runtime.Arc.UnknownObjectRelease(*(IntPtr*)resultPtr);\n{continuationIndent}"
                : $"var _vwtMetadata = Swift.Runtime.TypeMetadata.GetExistentialTypeMetadata({arity});\n" +
                  $"{continuationIndent}_vwtMetadata.ValueWitnessTable->Destroy((void*)resultPtr, _vwtMetadata);\n" +
                  $"{continuationIndent}";
        }

        /// <summary>
        /// Emits the async wrapper for methods returning collection types (Array, Dictionary, Set).
        /// These use the same OpaquePointer pattern as complex types on the Swift side, but require
        /// <c>MarshalFromSwift</c> with the runtime container type (e.g. <c>SwiftArray&lt;int&gt;</c>)
        /// rather than the public type (e.g. <c>IReadOnlyList&lt;int&gt;</c>). The callback body — and
        /// its three-arm carrier-release contract — is built by
        /// <see cref="BuildCollectionCarrierMarshalLines"/>; this method assembles the surrounding
        /// callback/error-callback scaffolding and the <c>SBW_Free</c> + GCHandle teardown.
        /// </summary>
        private void EmitAsyncWrapperForCollection(CSharpWriter csWriter,
            string callbackFieldName, string callbackMethodName,
            string errorCallbackFieldName, string errorCallbackMethodName,
            string runtimeType, string conversionExpr,
            bool usesObjCContainerBridge, bool proxySuppressed)
        {
            var freePInvokeDecl = GetFreePInvokeDeclIfNeeded();

            string marshalLines = BuildCollectionCarrierMarshalLines(
                runtimeType, conversionExpr, _wrapperSignature.ReturnType,
                usesObjCContainerBridge, proxySuppressed, "                                ");

            var text = $$"""
                        {{freePInvokeDecl}}{{AsyncFieldVisibility}} static unsafe delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void> {{callbackFieldName}} = &{{callbackMethodName}};
                        [UnmanagedCallersOnly(CallConvs = new[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]
                        private static unsafe void {{callbackMethodName}}(IntPtr resultPtr, IntPtr task)
                        {
                            GCHandle handle = GCHandle.FromIntPtr(task);
                            try
                            {
                                {{marshalLines}}

                                // Handle both cases: direct TCS or typed SwiftAsyncCallHolder (with copy buffer pointers etc.)
                                if (handle.Target is global::Swift.Runtime.SwiftAsyncCallHolder holder && holder.Tcs is TaskCompletionSource<{{_wrapperSignature.ReturnType}}> holderTcs)
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
                {{BuildAsyncCallbackFaultCatch($"<{_wrapperSignature.ReturnType}>", "            ")}}
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
        /// Builds the catch block that guards an async [UnmanagedCallersOnly] callback against
        /// a managed exception unwinding into native Swift. The async callbacks run on a Swift
        /// thread that re-enters managed code via a C function pointer; an exception escaping
        /// that boundary aborts the process (SIGABRT). Worse, if the throw happens before the
        /// TaskCompletionSource is resolved, the awaiting Task never completes and the caller
        /// hangs forever. This catch resolves the TCS from the still-live GCHandle target and
        /// faults it, turning an abort into an observable async exception. <c>TrySetException</c>
        /// is a no-op if the result was already set, so it is safe even when the throw occurs
        /// after the success path partially ran. <c>handle.Free()</c> stays in the callback's
        /// own <c>finally</c> and runs after this catch.
        /// </summary>
        /// <param name="tcsType">Generic suffix for the TCS type (e.g. <c>&lt;int&gt;</c>, or empty for void).</param>
        /// <param name="indent">Leading indentation applied to every emitted line.</param>
        private static string BuildAsyncCallbackFaultCatch(string tcsType, string indent)
        {
            return
                $"{indent}catch (global::System.Exception __ex)\n" +
                $"{indent}{{\n" +
                $"{indent}    // Never let a managed exception unwind into native Swift (SIGABRT); fault the\n" +
                $"{indent}    // awaiting Task instead so the failure is observable and the awaiter cannot hang.\n" +
                $"{indent}    // The fault is reachable from result marshalling, which runs BEFORE the normal\n" +
                $"{indent}    // success-branch cleanup, so the holder's native resources (retained self, copy\n" +
                $"{indent}    // buffers, existential heap, deferred containers, cancellation registration) are\n" +
                $"{indent}    // still live and must be freed here too — finally only releases the GCHandle.\n" +
                $"{indent}    if (handle.Target is global::Swift.Runtime.SwiftAsyncCallHolder __holder && __holder.Tcs is TaskCompletionSource{tcsType} __holderTcs)\n" +
                $"{indent}    {{\n" +
                $"{BuildHolderCleanupCode("__holder", indent + "        ")}\n" +
                $"{indent}        __holderTcs.TrySetException(__ex);\n" +
                $"{indent}    }}\n" +
                $"{indent}    else if (handle.Target is TaskCompletionSource{tcsType} __directTcs)\n" +
                $"{indent}        __directTcs.TrySetException(__ex);\n" +
                $"{indent}}}";
        }

        /// <summary>
        /// Builds the C# error callback code block (delegate + method) for async wrappers.
        /// Unified wire format: all branches (typed throws, plain-throws cascade,
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
                                        // Swift reported CancellationError — capture the token and cancel the Task.
                                        // Cleanup is delegated to the exception-safe, idempotent runtime helper
                                        // (SwiftAsyncCallHolder) so the cancellation, success, and fault paths
                                        // share one slot-walk and cannot drift apart.
                                        global::System.Threading.CancellationToken cancelToken = default;
                {{BuildCancellationCleanupLoop("holder", "                                        ")}}{{freeErrorInCancellation}}
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
                {{BuildHolderCleanupCode("holder", "                        ")}}
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
                // Plain-throws cascade: 6-param wire format. The Swift cascade
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
                {{BuildHolderCleanupCode("holder", "                        ")}}
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
                {{BuildHolderCleanupCode("holder", "                        ")}}
                                        holderTcs.TrySetException(exception);
                """;
                directErrorBody = $$"""
                                    var errorMessage = Marshal.PtrToStringUTF8(errorMessagePtr) ?? "Unknown Swift error";
                                    directTcs.TrySetException(new SwiftException(errorMessage));
                """;
            }

            // Unified wire format: typed-throws, plain-throws cascade, and untyped
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
                                // Handle both cases: direct TCS or typed SwiftAsyncCallHolder (with copy buffer pointers etc.)
                                if (handle.Target is global::Swift.Runtime.SwiftAsyncCallHolder holder && holder.Tcs is TaskCompletionSource{{tcsType}} holderTcs)
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
                {{BuildAsyncCallbackFaultCatch(tcsType, "            ")}}
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
        /// Emits the holder-cleanup call for freeing async call resources. Delegates to the typed
        /// holder's instance <c>Cleanup()</c> method, which walks every owned field (SelfRetain,
        /// DeferredSelfHandle, CopyBuffers, ExistentialHeaps, DeferredDisposes,
        /// CancellationRegistration, KeepAlives).
        ///
        /// The helper is exception-safe and idempotent, so this single line is correct on every
        /// async termination path — including the [UnmanagedCallersOnly] fault <c>catch</c>, where
        /// an inlined field walk could throw into native Swift (SIGABRT) or double-free fields the
        /// success path had already released. Centralizing the field set in the runtime also removes
        /// the old three-way mirror (this helper, WrapperEmitter.Async, BuildCancellationCleanupLoop)
        /// that previously had to be kept in lockstep by hand.
        /// </summary>
        /// <param name="holderVar">The variable name for the holder (e.g., "holder" or "_asyncCallHolder").</param>
        /// <param name="indent">The whitespace indent prefix for the emitted line.</param>
        public static string BuildHolderCleanupCode(string holderVar, string indent)
            => $"{indent}{holderVar}.Cleanup();";

        /// <summary>
        /// Emits the cancellation-path cleanup for <c>BuildErrorCallbackBlock</c> when Swift reports
        /// CancellationError. Captures the registered <c>cancelToken</c> (read-only, before any
        /// disposal) via <c>CaptureCancellationToken</c>, then runs the same exception-safe,
        /// idempotent <c>Cleanup</c>. Assigns to a pre-declared <c>cancelToken</c> local at the call
        /// site (does not declare it).
        /// </summary>
        internal static string BuildCancellationCleanupLoop(string holderVar, string indent)
            => $"{indent}cancelToken = {holderVar}.CaptureCancellationToken();\n" +
               $"{indent}{holderVar}.Cleanup();";

        /// <summary>
        /// Builds the typed-holder construction statement (<c>var {holderVar} = new
        /// global::Swift.Runtime.SwiftAsyncCallHolder {{ ... }};</c>). Named-field / collection
        /// initializers replace the historical positional <c>object[]</c> layout: <c>Tcs</c> is
        /// always set; the receiver is the single <c>SelfRetain</c> or <c>DeferredSelfHandle</c>
        /// field (<paramref name="selfFieldInit"/>); per-call resources (copy buffers, keep-alive
        /// GC roots) use collection initializers. The existential heaps and the cancellation
        /// registration are filled later at their own emission sites (<c>.ExistentialHeaps.Add(...)</c>
        /// / <c>.CancellationRegistration = ...</c>), so they are not listed here. An emitter that
        /// stashes an unrecognized resource has no field for it — a compile error, not the silent
        /// leak the positional layout allowed.
        /// </summary>
        /// <param name="holderVar">The holder variable name (e.g. "_asyncCallHolder").</param>
        /// <param name="tcsExpr">The TaskCompletionSource expression (e.g. "_tcs").</param>
        /// <param name="selfFieldInit">"" for static, else a single field assignment such as
        /// "SelfRetain = new RetainedSelfPtr(_selfPtr)" or "DeferredSelfHandle = new DeferredSafeHandleRelease(_payload)".</param>
        /// <param name="deferredListVar">The AsyncDeferredDisposeList variable name, or null when none is needed.</param>
        /// <param name="copyBufferList">Comma-joined CopyBufferWithType expressions, or "" when none.</param>
        /// <param name="keepAliveList">Comma-joined GC-root expressions (e.g. "(object)this, (object)p0"), or "" when none.</param>
        internal static string BuildTypedHolderConstruction(
            string holderVar, string tcsExpr, string selfFieldInit,
            string? deferredListVar, string copyBufferList, string keepAliveList)
        {
            var members = new System.Collections.Generic.List<string> { $"Tcs = {tcsExpr}" };
            if (!string.IsNullOrEmpty(selfFieldInit))
                members.Add(selfFieldInit);
            if (!string.IsNullOrEmpty(deferredListVar))
                members.Add($"DeferredDisposes = {deferredListVar}");
            if (!string.IsNullOrEmpty(copyBufferList))
                members.Add($"CopyBuffers = {{ {copyBufferList} }}");
            if (!string.IsNullOrEmpty(keepAliveList))
                members.Add($"KeepAlives = {{ {keepAliveList} }}");
            return $"var {holderVar} = new global::Swift.Runtime.SwiftAsyncCallHolder {{ {string.Join(", ", members)} }};";
        }

        /// <summary>
        /// Joins non-empty comma-separated GC-root fragments for the holder's <c>KeepAlives</c>
        /// collection initializer (e.g. the receiver <c>(object)this</c> and the original
        /// non-frozen parameter objects).
        /// </summary>
        internal static string CombineKeepAlives(params string[] fragments)
            => string.Join(", ", fragments.Where(f => !string.IsNullOrEmpty(f)));

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
