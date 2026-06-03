// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.CodeDom.Compiler;

namespace BindingsGeneration
{
    /// <summary>
    /// Provides methods for emitting wrappers.
    /// </summary>
    internal partial class WrapperEmitter : IAsyncTupleHelpers
    {
        private static readonly TypeProjectionFactory s_projectionFactory = new();

        private readonly MethodEnvironment _env;
        private readonly GenericContext _genericContext;
        private readonly Signature _wrapperSignature;
        private readonly Signature _pInvokeSignature;
        private readonly bool _requiresIndirectResult;
        private readonly bool _requiresSwiftSelf;
        private readonly bool _requiresSwiftError;
        private readonly bool _requiresSwiftAsync;
        private readonly bool _requiresOpaqueReturnWrapper;
        private readonly bool _requiresFixedBlock;
        private readonly TypeDatabaseExtensions.AnyTypeFallbackInfo? _fallbackInfo;
        // Typed throws state — resolved once, used by both EmitAsync (Swift) and EmitAsyncWrapper (C#).
        // useTypedErrorCallback: true when the method has typed throws, the error type resolves,
        // and the method is not a free-function async (D5 guard).
        private readonly bool useTypedErrorCallback;
        private readonly string? typedThrowsSwiftErrorType;  // e.g., "SwiftBindingsTestLib.ParseError"
        private readonly string? typedThrowsCSharpErrorType;  // e.g., "ParseError"
        private readonly bool typedErrorTransfersOwnershipAsync; // true when MarshalFromSwift takes ownership of error buffer
        // True when the typed error type is `IsFrozenStructProjectedAsClass` — frozen
        // struct with reference-typed fields. The frozen-struct `NewFromPayload` does an
        // `InitializeWithCopy` into a fresh `NativeMemory.Alloc` buffer (owned by the
        // SafeHandle), leaving the wire carrier with `+1` retains on its heap fields.
        // The cleanup must run a VWT `Destroy` on the wire buffer (release the retains)
        // before `SBW_Free` (release the carrier). Mutually exclusive with the other
        // typed-error shapes — this flag implies `typedErrorTransfersOwnershipAsync`
        // should NOT be set, since the SafeHandle owns the COPY, not the wire carrier.
        private readonly bool typedErrorRequiresVwtDestroyAsync;
        // True when the typed error type is a Swift class. Mirrors the cascade
        // dispatcher's `CascadePayloadShape.ClassPointerDirect`: the wire is a +1
        // retained class pointer (no carrier buffer) — Swift emits
        // `Unmanaged.passRetained(error as! T as AnyObject).toOpaque()`, and C#'s
        // `MarshalFromSwift<T>` constructs the SwiftObject taking ownership of the
        // retain. There is nothing to `SBW_Free` (no buffer); on marshal failure C#
        // calls `Arc.Release(errorPtr)` to balance the retain. Mutually exclusive
        // with the other typed-error shapes.
        private readonly bool typedErrorIsClassDirectAsync;
        // Phase 4 plain-throws → typed-exception cascade: true when this is a plain-throws
        // async method (Throws but not HasTypedThrows) AND the module has registered error
        // types via ErrorEnumRegistryEmitter. Drives the 6-param cascade-dispatch wire format.
        private readonly bool useCascadeErrorCallback;
        private readonly SyncMethodPlan _syncPlan;
        private readonly ModuleEmissionContext _emissionContext;
        private bool _needsUnsafeBody;

        /// <summary>
        /// Local-variable name for the P/Invoke return value. Normally "result"; renamed to
        /// "__result" when a method parameter is also named "result" to prevent CS0841/CS0136
        /// shadowing on the self-referential P/Invoke call expression.
        /// </summary>
        private string ReturnLocalName => _syncPlan.ReturnLocalName;
        // The live async C# callback-plumbing path. The Swift @_cdecl half is emitted by
        // WrapperEmitter.Async.EmitAsync; the C# callback half by _asyncHarness.EmitAsyncWrapper.
        private readonly AsyncHarnessEmitter _asyncHarness;
        // Tracks existential container heap allocation variable names for cleanup in finally block.
        // Populated by EmitExistentialHeapDeclarations, consumed by EmitExistentialContainerCleanup.
        private readonly List<string> _existentialHeapNames = new();

        // Tracks parameter names for Optional<generic> arguments passed under Swift @in
        // (callee-destroyed) convention via raw CallConvSwift. Swift consumes the buffer;
        // running the SwiftOptional's normal Dispose afterwards would call VWT Destroy on
        // a deinitialized buffer, double-releasing class fields. Cleared per-emission.
        // Populated by TryEmitParameterConversionViaProjection, consumed by EmitInConventionOptionalCleanup.
        private readonly List<string> _inConventionOptionalNames = new();

        // Counts currently-open raw-buffer `fixed (...)` blocks. Every call to
        // EmitRawBufferFixedStart increments by the number of UnsafeRawBufferPointer
        // parameters; the paired EmitRawBufferFixedEnd decrements by the same count.
        // AssertRawBufferFixedDepthZero (called at wrapper emission tail) fails fast
        // if a future emitter adds an early return between Start and End, since that
        // would produce uncompilable output with an unclosed fixed block. The
        // emitter path is synchronous and not recursive within a single wrapper, so
        // a plain int counter is sufficient — no disposable-scope plumbing needed.
        private int _rawBufferFixedDepth;

        /// <summary>
        /// True when the method has @_cdecl existential parameters that require heap allocation.
        /// Used by constructor and method paths to determine if try/finally cleanup is needed.
        /// </summary>
        string IAsyncTupleHelpers.GetPInvokeTypeForTupleElement(TypeSpec element)
            => GetPInvokeTypeForTupleElement(element);
        string IAsyncTupleHelpers.GetCSharpTypeForTupleElement(TypeSpec element, bool applyIdiomaticConversion)
            => GetCSharpTypeForTupleElement(element, applyIdiomaticConversion);
        string? IAsyncTupleHelpers.GetTupleElementMarshalCode(TypeSpec element, string itemName, string resultName, string csharpType)
            => GetTupleElementMarshalCode(element, itemName, resultName, csharpType);

        private bool HasExistentialHeapAllocations =>
            _env.MethodDecl.UsesCdeclWrapper &&
            _env.MethodDecl.CSSignature.Skip(1).Any(a => _env.ExistentialHandler.IsExistential(a.SwiftTypeSpec));

        internal WrapperEmitter(
            MethodEnvironment methodEnv,
            SignatureHandler signatureHandler,
            TypeDatabaseExtensions.AnyTypeFallbackInfo? fallbackInfo = null,
            ModuleEmissionContext? emissionContext = null)
        {
            _env = methodEnv;
            _fallbackInfo = fallbackInfo;
            _emissionContext = emissionContext ?? ModuleEmissionContext.Default;
            _genericContext = methodEnv.ParentDecl is TypeDecl parentType
                ? GenericContext.FromMethodInType(methodEnv.MethodDecl, parentType)
                : GenericContext.FromMethod(methodEnv.MethodDecl);

            _wrapperSignature = signatureHandler.GetWrapperSignature();
            _pInvokeSignature = signatureHandler.GetPInvokeSignature();

            _requiresIndirectResult = MarshallingHelpers.MethodRequiresIndirectResult(methodEnv);
            _requiresSwiftAsync = _env.MethodDecl.IsAsync;
            // Detect opaque return types (some Protocol) that need a Swift wrapper
            // to box the concrete return value into an existential container (any Protocol)
            _requiresOpaqueReturnWrapper = _env.MethodDecl.CSSignature.First().SwiftTypeSpec is ProtocolListTypeSpec { IsOpaque: true };
            // Async methods need SwiftSelf to pass self to the Swift wrapper
            _requiresSwiftSelf = MarshallingHelpers.MethodRequiresSwiftSelf(methodEnv);
            // Async methods call our generated Swift wrapper which handles errors internally
            _requiresSwiftError = !_requiresSwiftAsync && _env.MethodDecl.Throws;

            // Resolve typed throws for async error callback emission.
            // Falls back to untyped when: (a) no typed throws, (b) error type unresolvable.
            useTypedErrorCallback = false;
            if (_env.MethodDecl.HasTypedThrows && _requiresSwiftAsync)
            {
                if (_env.TypeDatabase.TryGetTypeRecord(_env.MethodDecl.ThrownErrorType!, out var errorTypeRecord))
                {
                    typedThrowsSwiftErrorType = _env.MethodDecl.ThrownErrorType!.ToString();
                    typedThrowsCSharpErrorType = errorTypeRecord.CSharpTypeName.FullyQualifiedName;
                    useTypedErrorCallback = true;

                    // Per-shape ownership: parallels the cascade dispatcher's
                    // `CascadePayloadShape` selector in <see cref="ErrorRegistryHelperEmitter"/>.
                    // Four mutually-exclusive shapes:
                    //   - frozen-with-memory struct → VWT `Destroy` + `SBW_Free` in finally
                    //     (the generated frozen-struct `NewFromPayload` copies into a fresh
                    //     buffer, leaving the wire carrier with +1 retains on heap fields).
                    //   - class error → `Arc.Release` on marshal failure; no buffer at all
                    //     (Swift hands a +1 retained class pointer; C#'s `NewFromPayload`
                    //     takes ownership of the retain on success).
                    //   - complex enum / non-frozen struct → `SBW_Free` only on marshal
                    //     failure (success transfers buffer ownership to the SafeHandle).
                    //   - simple enum / plain frozen struct → `SBW_Free` in finally
                    //     (marshal copies bytes by value).
                    bool isComplexEnum = errorTypeRecord.Kind == TypeRecordKind.Enum &&
                        !errorTypeRecord.Flags.HasFlag(TypeRecordFlags.SimpleEnum);
                    bool isNonFrozenStruct = errorTypeRecord.Kind == TypeRecordKind.Struct &&
                        !MarshallingHelpers.IsTypeFrozen(errorTypeRecord);
                    bool isFrozenStructAsClass = errorTypeRecord.Kind == TypeRecordKind.Struct &&
                        MarshallingHelpers.IsFrozenStructProjectedAsClass(errorTypeRecord);
                    bool isClassError = errorTypeRecord.Kind == TypeRecordKind.Class;
                    typedErrorRequiresVwtDestroyAsync = isFrozenStructAsClass;
                    typedErrorIsClassDirectAsync = isClassError;
                    typedErrorTransfersOwnershipAsync = isComplexEnum || isNonFrozenStruct;
                }
            }

            // Phase 4 plain-throws cascade: distinct from useTypedErrorCallback (which handles
            // statically-typed `throws(T)`). A plain `async throws` method fires the cascade
            // path only when the module has at least one Error-conforming type registered via
            // ErrorEnumRegistryEmitter — otherwise there's nothing to cascade against and the
            // existing 3-param stringification fallback stays in effect.
            useCascadeErrorCallback = !useTypedErrorCallback
                && _requiresSwiftAsync
                && _env.MethodDecl.Throws
                && _emissionContext.ErrorTypeOrder.Count > 0;

            // Frozen struct value types need a fixed block to pin 'this' and get a pointer.
            // Two cases: (1) setters modify the struct in-place (pointer semantics),
            // (2) standalone closure Cdecl wrappers pass self as explicit IntPtr.
            // In both cases the fixed block provides __self for pointer access.
            _requiresFixedBlock = false;
            if (_env.ParentDecl is StructDecl structDecl && structDecl.IsFrozen)
            {
                var typeRecord = _env.TypeDatabase.GetTypeRecordOrThrow(structDecl.SwiftTypeName);
                // Only pure frozen structs (no memory management) need the fixed block
                // Frozen structs with memory management use _payload SafeHandle like non-frozen types
                if (!MarshallingHelpers.RequiresMemoryManagement(typeRecord))
                {
                    // Setters always need the fixed block for pointer-based mutation.
                    // Standalone closure Cdecl wrappers, @_cdecl method wrappers, and native thunks
                    // need it only for instance methods (static methods have no self parameter to pin).
                    _requiresFixedBlock = MarshallingHelpers.MethodIsSetter(_env.MethodDecl)
                        || (_env.MethodDecl.UsesFreeFunctionWrapper && _requiresSwiftSelf)
                        || (_env.MethodDecl.UsesCdeclMethodWrapper && _requiresSwiftSelf)
                        || (_env.MethodDecl.UsesNativeThunk && _requiresSwiftSelf);
                }
            }

            _asyncHarness = new AsyncHarnessEmitter(
                _env,
                _wrapperSignature,
                _pInvokeSignature,
                useTypedErrorCallback,
                typedThrowsSwiftErrorType,
                typedThrowsCSharpErrorType,
                typedErrorTransfersOwnershipAsync,
                typedErrorRequiresVwtDestroyAsync,
                typedErrorIsClassDirectAsync,
                _emissionContext,
                this);

            // Build the sync method plan
            var builder = new MethodMarshalPlanBuilder(
                _env, _genericContext, _wrapperSignature, _pInvokeSignature,
                _requiresIndirectResult, _requiresSwiftSelf, _requiresSwiftError,
                _requiresSwiftAsync, _requiresFixedBlock,
                IsProtocolAvailableForConstraint);
            _syncPlan = builder.BuildSyncPlan();
            _needsUnsafeBody = _syncPlan.RequiresUnsafe;

            // CdeclFrozenStruct params use stackalloc + byte* which require unsafe context
            if (_env.MethodDecl.UsesCdeclWrapper &&
                _env.MethodDecl.CSSignature.Skip(1).Any(arg =>
                    WrapperValidation.IsNonPrimitiveFrozenStructParam(arg, _env.TypeDatabase) &&
                    !_env.BoundGenericsHandler.IsBoundGeneric(arg) &&
                    !_env.ClosureHandler.IsClosure(arg) &&
                    !MarshallingHelpers.IsConvertibleType(arg.SwiftTypeSpec) &&
                    !_env.TypeConversionHandler.HasNativeTypeRemapping(arg.SwiftTypeSpec)))
            {
                _needsUnsafeBody = true;
            }

            // @_cdecl blittable tuple params use stackalloc + pointer casts which require unsafe context
            if (_env.MethodDecl.UsesCdeclWrapper &&
                _env.MethodDecl.CSSignature.Skip(1).Any(arg =>
                    _env.TupleHandler.IsTuple(arg) &&
                    _env.TupleHandler.GetTupleTypeSpec(arg) is TupleTypeSpec tts &&
                    tts.Elements.All(e => CdeclParamMapper.IsCdeclPrimitive(e))))
            {
                _needsUnsafeBody = true;
            }

            // @_cdecl existential params use Unsafe.AsPointer which requires unsafe context
            if (_env.MethodDecl.UsesCdeclWrapper &&
                _env.MethodDecl.CSSignature.Skip(1).Any(arg =>
                    _env.ExistentialHandler.IsExistential(arg.SwiftTypeSpec)))
            {
                _needsUnsafeBody = true;
            }

            // UnsafeRawBufferPointer / UnsafeMutableRawBufferPointer params pin via
            // `fixed (byte* p = span)` which requires unsafe. Both variants follow the same
            // emission shape; only the public C# parameter type and the Swift-side
            // reconstruction differ — see CdeclParamMapper.
            if (_env.MethodDecl.CSSignature.Skip(1).Any(arg =>
                    MarshallingHelpers.IsAnyUnsafeRawBufferPointer(arg.SwiftTypeSpec)))
            {
                _needsUnsafeBody = true;
            }
        }

        /// <summary>
        /// True when this async method has at least one parameter whose marshalling produces
        /// a Swift container (<c>SwiftArray&lt;T&gt;</c>, <c>SwiftSet&lt;T&gt;</c>,
        /// <c>SwiftDictionary&lt;K,V&gt;</c>) that the @_cdecl wrapper reads via
        /// <c>UnsafeRawPointer</c>. The container's <c>using var</c> would otherwise dispose
        /// the buffer when the foreground async wrapper returns <c>tcs.Task</c> — before the
        /// Swift continuation has finished reading the buffer on its own thread.
        ///
        /// Drives the emission of <c>AsyncDeferredDisposeList _asyncDeferredList</c> in the
        /// holder array and the corresponding <c>_asyncDeferredList.Items.Add(paramSwift)</c>
        /// hand-off in <see cref="WrapperEmitter.Marshalling"/>'s
        /// <c>TryEmitParameterConversionViaProjection</c>.
        /// </summary>
        internal bool RequiresAsyncDeferredDisposeList()
        {
            if (!_requiresSwiftAsync)
                return false;

            // Only @_cdecl wrappers route collection params through RenderWithHandleOverride
            // — the SwiftArray/Set/Dictionary 'using var' lifetime bug only manifests on that
            // path. Other wrapper shapes (witness, protocol-extension) pass containers
            // differently and are not affected here.
            if (!_env.MethodDecl.UsesCdeclWrapper)
                return false;

            return _env.MethodDecl.CSSignature.Skip(1).Any(IsAsyncDeferredDisposeContainerParam);
        }

        /// <summary>
        /// True when <paramref name="p"/> is the kind of parameter whose serialization
        /// container must outlive the foreground async wrapper. See
        /// <see cref="RequiresAsyncDeferredDisposeList"/>.
        /// </summary>
        internal bool IsAsyncDeferredDisposeContainerParam(ArgumentDecl p)
        {
            // Mirror the gates that EmitTypeConversions / TryEmitParameterConversionViaProjection
            // applies. EmitTypeConversions filters only on IsConvertibleType (Array / Set /
            // Dictionary / Optional / String / Date / Data) — it does NOT exclude bound
            // generics. EmitBoundGenericArguments will defer Set<Int> et al to EmitTypeConversions
            // whenever the projection factory can handle them. Excluding bound generics here
            // would silently skip the most common shape (Set<Int>, Array<Int>, …) and emit
            // an undefined `_asyncDeferredList` reference at the marshalling site.
            if (_env.ClosureHandler.IsClosure(p)) return false;
            if (_env.TupleHandler.IsTuple(p)) return false;
            if (_env.ExistentialHandler.IsExistential(p.SwiftTypeSpec)) return false;
            if (!MarshallingHelpers.IsConvertibleType(p.SwiftTypeSpec)) return false;

            var projection = s_projectionFactory.Project(p.SwiftTypeSpec,
                new ProjectionContext
                {
                    TypeDatabase = _env.TypeDatabase,
                    IsParameter = true,
                    GenericContext = _genericContext,
                    ParentTypeDecl = _env.ParentDecl as TypeDecl,
                    CurrentModuleName = _env.ExistentialHandler.CurrentModuleName
                });
            if (projection == null) return false;
            // ObjC bridge containers don't create a SwiftArray-like 'using var' — they
            // emit `using var {name}NSArray = ...` whose lifetime is foreground-only and
            // matches the NSArray's ObjC ARC handoff to Swift. Out of scope here.
            if (projection.UsesObjCContainerBridge) return false;

            // Match the projection set that TryEmitParameterConversionViaProjection routes
            // to RenderWithHandleOverride at WrapperEmitter.Marshalling.cs ~654.
            return projection is ArrayProjection or SetProjection or DictionaryProjection;
        }

        /// <summary>
        /// Emits the constructor wrapper.
        /// </summary>
        /// <param name="writer">The IndentedTextWriter instance.</param>
        internal void EmitConstructor(CSharpWriter csWriter)
        {
            bool isObjCRooted = _env.ParentDecl is ClassDecl cd && cd.IsObjCRooted;

            if (isObjCRooted)
            {
                EmitObjCRootedConstructor(csWriter);
                return;
            }

            bool isGeneric = _env.MethodDecl.IsGeneric;
            bool hasClosures = _env.MethodDecl.CSSignature.Skip(1).Any(_env.ClosureHandler.IsClosure);
            bool needsTryFinally = isGeneric || hasClosures || HasExistentialHeapAllocations;

            // Emit closure callbacks and error helper P/Invokes before constructor body
            EmitErrorHelperPInvokes(csWriter);
            if (hasClosures)
            {
                EmitClosureCallbacks(csWriter);
            }

            AvailabilityAttributeEmitter.EmitAvailabilityAttributes(csWriter, _env.MethodDecl, _env.ParentDecl, emitObsolete: false);
            EmitSafetyObsolete(csWriter);
            XmlDocCommentEmitter.EmitMethodDocComment(csWriter, _env.MethodDecl, isConstructor: true);
            EmitSignatureConstructor(csWriter);
            EmitBodyStart(csWriter);
            EmitUnsafeBlockStart(csWriter);
            EmitSafeHandleAddRef(csWriter);

            // Declare variables (SwiftError, TypeMetadata, payloads, GCHandles).
            // Always emit — SwiftError 'ref' requires pre-declaration even without try-finally.
            EmitDeclarationsForAllocations(csWriter);
            EmitExistentialHeapDeclarations(csWriter);

            if (needsTryFinally)
            {
                EmitTryBlockStart(csWriter);
            }

            EmitSwiftSelf(csWriter);
            EmitIndirectResultConstructor(csWriter);

            // For generic constructors, marshal generic arguments and get witness tables
            if (isGeneric)
            {
                EmitGenericArguments(csWriter);
            }

            EmitBoundGenericArguments(csWriter);
            EmitClosureMarshalling(csWriter);
            EmitTypeConversions(csWriter);
            EmitCdeclFrozenStructMarshalling(csWriter);
            EmitExistentialContainerMarshalling(csWriter);

            if (isGeneric)
            {
                EmitProtocolWitnessTables(csWriter);
            }

            EmitArrayOwnershipRetain(csWriter);
            EmitRawBufferFixedStart(csWriter);
            EmitPInvokeCall(csWriter);
            EmitConsumedNonCopyableParamCleanup(csWriter);
            EmitInConventionOptionalCleanup(csWriter);
            EmitGenericInoutWriteback(csWriter);
            EmitSwiftError(csWriter);
            EmitReturnConstructor(csWriter);
            EmitRawBufferFixedEnd(csWriter);
            EmitDisposeScopeRegistration(csWriter);

            // Add cleanup in finally block for generics and closures
            if (needsTryFinally)
            {
                EmitTryBlockEnd(csWriter);
                EmitFinally(csWriter);
            }

            EmitUnsafeBlockEnd(csWriter);
            EmitBodyEnd(csWriter);
            AssertRawBufferFixedDepthZero();
        }

        /// <summary>
        /// Emits a constructor for ObjC-rooted classes using the static helper pattern.
        /// The P/Invoke call happens inside a static CreateSwiftInstance_... method that
        /// returns NativeHandle. The constructor chains to base(NativeHandle) and calls
        /// DangerousRelease() to balance NSObject's retain.
        /// </summary>
        private void EmitObjCRootedConstructor(CSharpWriter csWriter)
        {
            bool isGeneric = _env.MethodDecl.IsGeneric;
            bool hasClosures = _env.MethodDecl.CSSignature.Skip(1).Any(_env.ClosureHandler.IsClosure);
            bool needsTryFinally = isGeneric || hasClosures || HasExistentialHeapAllocations;

            // Emit closure callbacks and error helper P/Invokes before constructor
            EmitErrorHelperPInvokes(csWriter);
            if (hasClosures)
            {
                EmitClosureCallbacks(csWriter);
            }

            // Emit the static helper method first
            var helperName = $"CreateSwiftInstance_{NameProvider.GetPInvokeName((MethodDecl)_env.MethodDecl)}";
            var helperParams = _wrapperSignature.ParametersString();
            csWriter.WriteLine($"private static unsafe ObjCRuntime.NativeHandle {helperName}({helperParams})");
            csWriter.WriteLine("{");
            csWriter.Indent++;

            // The helper body contains the full P/Invoke call sequence
            EmitSafeHandleAddRef(csWriter);
            EmitBoundGenericArguments(csWriter);

            // Declare variables (SwiftError, GCHandles, etc.) before use.
            // Always emit — SwiftError 'ref' requires pre-declaration even without try-finally.
            EmitDeclarationsForAllocations(csWriter);
            EmitExistentialHeapDeclarations(csWriter);

            EmitClosureMarshalling(csWriter);
            EmitTypeConversions(csWriter);
            EmitCdeclFrozenStructMarshalling(csWriter);
            EmitExistentialContainerMarshalling(csWriter);

            if (needsTryFinally)
            {
                EmitTryBlockStart(csWriter);
            }

            if (isGeneric)
            {
                EmitGenericArguments(csWriter);
                EmitProtocolWitnessTables(csWriter);
            }

            if (_requiresIndirectResult)
            {
                // Non-frozen struct constructors: result goes into buf via SwiftIndirectResult or IntPtr
                csWriter.WriteLine("IntPtr* buf = stackalloc IntPtr[1];");
                if (_env.MethodDecl.UsesCdeclConstructorWrapper)
                    csWriter.WriteLine("var resultPtr = (IntPtr)buf;");
                else
                    csWriter.WriteLine("var swiftIndirectResult = new SwiftIndirectResult(buf);");
                EmitRawBufferFixedStart(csWriter);
                EmitPInvokeCall(csWriter);
                EmitConsumedNonCopyableParamCleanup(csWriter);
                EmitInConventionOptionalCleanup(csWriter);
                EmitSwiftError(csWriter);
                csWriter.WriteLine("if (*buf == IntPtr.Zero)");
                csWriter.Indent++;
                csWriter.WriteLine("throw new InvalidOperationException(\"Swift initializer returned null.\");");
                csWriter.Indent--;
                csWriter.WriteLine("return new ObjCRuntime.NativeHandle(*buf);");
                EmitRawBufferFixedEnd(csWriter);
            }
            else
            {
                // Class constructors: P/Invoke returns IntPtr directly (pointer in register)
                EmitRawBufferFixedStart(csWriter);
                EmitPInvokeCall(csWriter);
                EmitConsumedNonCopyableParamCleanup(csWriter);
                EmitInConventionOptionalCleanup(csWriter);
                EmitSwiftError(csWriter);
                csWriter.WriteLine($"if ({ReturnLocalName} == IntPtr.Zero)");
                csWriter.Indent++;
                csWriter.WriteLine("throw new InvalidOperationException(\"Swift initializer returned null.\");");
                csWriter.Indent--;
                csWriter.WriteLine($"return new ObjCRuntime.NativeHandle({ReturnLocalName});");
                EmitRawBufferFixedEnd(csWriter);
            }

            if (needsTryFinally)
            {
                EmitTryBlockEnd(csWriter);
                EmitFinally(csWriter);
            }

            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.WriteLine();

            // Now emit the public constructor that calls the helper
            AvailabilityAttributeEmitter.EmitAvailabilityAttributes(csWriter, _env.MethodDecl, _env.ParentDecl, emitObsolete: false);
            EmitSafetyObsolete(csWriter);
            XmlDocCommentEmitter.EmitMethodDocComment(csWriter, _env.MethodDecl, isConstructor: true);
            EmitSignatureConstructor(csWriter);
            EmitBodyStart(csWriter);
            EmitReturnConstructor(csWriter); // Emits DangerousRelease()
            EmitBodyEnd(csWriter);
            AssertRawBufferFixedDepthZero();
        }

        /// <summary>
        /// Emits the method wrapper.
        /// </summary>
        /// <param name="writer">The IndentedTextWriter instance.</param>
        internal void EmitMethod(CSharpWriter csWriter, SwiftWriter swiftWriter)
        {
            _asyncHarness.EmitAsyncWrapper(csWriter);
            EmitErrorHelperPInvokes(csWriter);
            EmitClosureCallbacks(csWriter);
            EmitClosureReturnInvokeThunkHelper(csWriter);
            if (_fallbackInfo.HasValue)
            {
                UnsupportedSwiftTypeSupport.EmitAttribute(csWriter, _fallbackInfo.Value);
            }
            AvailabilityAttributeEmitter.EmitAvailabilityAttributes(csWriter, _env.MethodDecl, _env.ParentDecl, emitObsolete: false);
            EmitSafetyObsolete(csWriter);
            if (!_env.MethodDecl.IsAccessor)
            {
                XmlDocCommentEmitter.EmitMethodDocComment(csWriter, _env.MethodDecl);
            }
            EmitReturnTypeOriginalSwiftType(csWriter);
            EmitSignatureMethod(csWriter);
            EmitBodyStart(csWriter);
            EmitUnsafeBlockStart(csWriter);
            // Existential heap variables (`void* xHeap = null;`) must precede EmitAsync.
            // For async methods, EmitAsync opens an outer `try {` whose closing brace is
            // written later by EmitReturnMethod, and the matching `finally { Free(xHeap); }`
            // is written by EmitFinally AFTER that close — so the declarations have to live
            // at the outer scope, not inside the async try, to remain reachable in the
            // trailing finally (CS0103 otherwise). Sync methods are unaffected — the decls
            // still sit above their try-finally block.
            EmitExistentialHeapDeclarations(csWriter);
            EmitAsync(csWriter, swiftWriter);
            EmitOpaqueReturnWrapper(swiftWriter);
            EmitTypedErrorExtractor(swiftWriter);
            EmitSafeHandleAddRef(csWriter);

            EmitDeclarationsForAllocations(csWriter);
            EmitCdeclPayloadDeclaration(csWriter);

            bool needsTryFinally = NeedsTryFinallyForMethod();
            if (needsTryFinally)
                EmitTryBlockStart(csWriter);
            EmitFixedBlockStart(csWriter);

            EmitSwiftSelf(csWriter);
            EmitIndirectResultMethod(csWriter);
            EmitGenericArguments(csWriter);
            EmitBoundGenericArguments(csWriter);
            EmitClosureMarshalling(csWriter);
            EmitTypeConversions(csWriter);
            EmitCdeclFrozenStructMarshalling(csWriter);
            EmitExistentialContainerMarshalling(csWriter);
            EmitProtocolWitnessTables(csWriter);
            EmitOptionalReturnBuffer(csWriter);
            EmitRawBufferFixedStart(csWriter);
            EmitPInvokeCall(csWriter);
            EmitConsumedNonCopyableParamCleanup(csWriter);
            EmitInConventionOptionalCleanup(csWriter);
            EmitGenericInoutWriteback(csWriter);
            EmitSwiftError(csWriter);
            EmitReturnMethod(csWriter);
            EmitRawBufferFixedEnd(csWriter);

            EmitFixedBlockEnd(csWriter);
            if (needsTryFinally)
            {
                EmitTryBlockEnd(csWriter);
                EmitFinally(csWriter);
            }
            EmitUnsafeBlockEnd(csWriter);
            EmitBodyEnd(csWriter);
            AssertRawBufferFixedDepthZero();
        }

        /// <summary>
        /// Emits the declarations for allocations.
        /// Existential heap declarations (<c>void* xHeap = null;</c>) are NOT emitted here —
        /// callers emit them via <see cref="EmitExistentialHeapDeclarations"/> at the right
        /// scope (before <see cref="EmitAsync"/> for async methods so the variables survive
        /// the outer async try / trailing finally).
        /// </summary>
        private void EmitDeclarationsForAllocations(CSharpWriter csWriter)
        {
            foreach (var line in _syncPlan.DeclarationLines)
                csWriter.WriteLine(line);
        }

        /// <summary>
        /// Pre-computes existential container heap allocation variable names and emits
        /// their declarations (void* = null) before the try block so they're accessible
        /// in the finally block for cleanup.
        /// </summary>
        private void EmitExistentialHeapDeclarations(CSharpWriter csWriter)
        {
            if (!_env.MethodDecl.UsesCdeclWrapper)
                return;

            foreach (var arg in _env.MethodDecl.CSSignature.Skip(1)
                .Where(a => _env.ExistentialHandler.IsExistential(a.SwiftTypeSpec)))
            {
                var protocolList = _env.ExistentialHandler.ToProtocolListTypeSpec(arg.SwiftTypeSpec);
                if (protocolList == null || !_env.ExistentialHandler.IsSupportedExistential(protocolList))
                    continue;
                var csName = NameProvider.GetCSharpParameterName(arg);
                var heapName = $"{csName}Heap";
                _existentialHeapNames.Add(heapName);
                csWriter.WriteLine($"void* {heapName} = null;");
            }
        }

        /// <summary>
        /// Emits the SwiftSelf variable.
        /// </summary>
        /// <param name="csWriter">The IndentedTextWriter instance.</param>
        private void EmitSwiftSelf(CSharpWriter csWriter)
        {
            if (_syncPlan.SwiftSelf == null) return;
            csWriter.WriteLine(_syncPlan.SwiftSelf.CreationCode);
            csWriter.WriteLine();
        }

        /// <summary>
        /// Emits the IndirectResult set up in constructor context.
        /// </summary>
        /// <param name="csWriter">The IndentedTextWriter instance.</param>
        private void EmitIndirectResultConstructor(CSharpWriter csWriter)
        {
            if (_syncPlan.IndirectResultConstructor == null) return;
            csWriter.WriteLines(_syncPlan.IndirectResultConstructor.AllocationCode);
            csWriter.WriteLine();
        }

        /// <summary>
        /// Emits the IndirectResult set up in method context.
        /// </summary>
        /// <param name="csWriter">The IndentedTextWriter instance.</param>
        private void EmitIndirectResultMethod(CSharpWriter csWriter)
        {
            if (_syncPlan.IndirectResultMethod == null) return;
            csWriter.WriteLines(_syncPlan.IndirectResultMethod.AllocationCode);
            csWriter.WriteLine();
        }

        /// <summary>
        /// Emits stack-allocated buffer for large Optional return values.
        /// The Swift wrapper writes the result into this buffer via UnsafeMutableRawPointer.
        /// </summary>
        private void EmitOptionalReturnBuffer(CSharpWriter csWriter)
        {
            if (_syncPlan.OptionalReturnBuffer == null) return;
            csWriter.WriteLines(_syncPlan.OptionalReturnBuffer.AllocationCode);
        }

        /// <summary>
        /// Emits the PInvoke call, then marks ownership transfer for any cdecl-wrapped
        /// escaping closure. Once control returns from the P/Invoke, Swift's wrapper has
        /// already constructed the `_SBClosureCtx` box at function entry — Swift ARC owns
        /// the GCHandle, so the C# `finally` must skip its own free. If the call never
        /// reaches the wrapper body (e.g. C# marshalling throws first, or the entry point
        /// cannot be resolved), the flag stays false and the `finally` frees the handle.
        /// </summary>
        /// <param name="writer">The IndentedTextWriter instance.</param>
        private void EmitPInvokeCall(CSharpWriter csWriter)
        {
            csWriter.WriteLine(_syncPlan.PInvokeCallStatement);
            EmitClosureOwnershipTransferred(csWriter);
            csWriter.WriteLine();
        }

        /// <summary>
        /// After a successful P/Invoke return, set the per-closure transfer flag so the
        /// finally block knows Swift's <c>_SBClosureCtx</c> now owns the GCHandle. See
        /// <see cref="EmitPInvokeCall"/> for the lifecycle rationale.
        /// </summary>
        private void EmitClosureOwnershipTransferred(CSharpWriter csWriter)
        {
            var closureParamCount = _env.MethodDecl.CSSignature.Skip(1).Count(_env.ClosureHandler.IsClosure);
            foreach (var argumentDecl in _env.MethodDecl.CSSignature.Skip(1).Where(_env.ClosureHandler.IsClosure))
            {
                var closureTypeSpec = _env.ClosureHandler.GetClosureTypeSpec(argumentDecl)!;
                if (!_env.ClosureHandler.IsSupportedClosure(closureTypeSpec) ||
                    !_env.ClosureHandler.RequiresThunk(closureTypeSpec, _env.MethodDecl.MangledName, closureParamCount) ||
                    _env.ClosureHandler.IsAsyncClosure(closureTypeSpec) ||
                    _env.ClosureHandler.IsAsyncThrowingClosure(closureTypeSpec) ||
                    _env.ClosureHandler.IsBaselineAsyncNonThrowingClosure(closureTypeSpec))
                {
                    continue;
                }

                if (!WrapperValidation.IsEffectivelyEscaping(closureTypeSpec, argumentDecl.SwiftTypeSpec, _env.ClosureHandler))
                    continue;

                // Both cdecl and legacy SwiftClosureData escaping paths declare the
                // {csName}Transferred flag (see MethodMarshalPlanBuilder.cs). The
                // legacy path additionally allocates an `_SBClosureCtx` box from C#
                // when the runtime dylib is present; on dylib-absent builds the box
                // pointer stays IntPtr.Zero and Swift's release of the raw GCHandle
                // ptr is a no-op — the leak persists for those builds (matching 0.10
                // behaviour) since there is no notification channel.
                var csName = NameProvider.GetCSharpParameterName(argumentDecl);
                csWriter.WriteLine($"{csName}Transferred = true;");
            }
        }

        /// <summary>
        /// Emits the SwiftError handling.
        /// </summary>
        /// <param name="csWriter">The IndentedTextWriter instance.</param>
        private void EmitSwiftError(CSharpWriter csWriter)
        {
            if (_syncPlan.SwiftError == null) return;
            csWriter.WriteLines(_syncPlan.SwiftError.ErrorCheckCode);
            csWriter.WriteLine();
        }

        /// <summary>
        /// Emits the Swift typed error extractor function for sync typed throws.
        /// Deduped per Swift error type name via ErrorDescriptionEmitter.
        /// </summary>
        internal void EmitTypedErrorExtractor(SwiftWriter swiftWriter)
        {
            if (_syncPlan.SwiftError?.SwiftErrorTypeName == null) return;
            var moduleName = _env.MethodDecl.ModuleDecl?.Name ?? "";
            ErrorDescriptionEmitter.EmitTypedErrorExtractorIfNeeded(
                swiftWriter, moduleName, _syncPlan.SwiftError.SwiftErrorTypeName, _emissionContext);
        }

        /// <summary>
        /// Delegates to <see cref="MethodValidationGates.IsProtocolAvailableForConstraint(SwiftTypeName, ITypeDatabase)"/>
        /// so the constraint-emission filter has a single source of truth across
        /// <c>WrapperEmitter</c>, <c>PInvokeEmitter</c>, and <c>BoundGenericsHandler</c>.
        /// </summary>
        /// <param name="protocolTypeName">The protocol type name to check.</param>
        /// <returns>True if the protocol can be projected into a C# generic constraint.</returns>
        private bool IsProtocolAvailableForConstraint(SwiftTypeName protocolTypeName)
            => MethodValidationGates.IsProtocolAvailableForConstraint(protocolTypeName, _env.TypeDatabase);

        /// <summary>
        /// Emits the finally block.
        /// </summary>
        private void EmitFinally(CSharpWriter csWriter)
        {
            csWriter.WriteLine("finally");
            EmitBodyStart(csWriter);
            EmitSafeHandleRelease(csWriter);
            EmitCdeclIndirectResultCleanup(csWriter);
            EmitExistentialContainerCleanup(csWriter);
            EmitBodyEnd(csWriter);
        }

        /// <summary>
        /// Frees heap-allocated existential container memory in the finally block.
        /// For async methods the cleanup is owned by the callback's holder-cleanup
        /// loop (the heap is registered as an <c>ExistentialContainerHeap</c> entry
        /// in <c>_asyncCallHolder</c> by <see cref="EmitExistentialContainerMarshalling"/>);
        /// freeing in the foreground finally would dangle Swift's pointer because
        /// the continuation runs after the wrapper has returned <c>_tcs.Task</c>.
        /// </summary>
        private void EmitExistentialContainerCleanup(CSharpWriter csWriter)
        {
            if (_requiresSwiftAsync)
                return;

            foreach (var heapName in _existentialHeapNames)
            {
                csWriter.WriteLine($"NativeMemory.Free({heapName});");
            }
        }

        /// <summary>
        /// Emits DisposeAfterConsumption() calls for SwiftOptional&lt;generic&gt; parameters
        /// passed under Swift's @in (callee-destroyed) convention. Must be called immediately
        /// after the P/Invoke so the buffer is freed without re-running VWT Destroy on the
        /// deinitialized contents (which would double-release class fields).
        ///
        /// Pairs with <see cref="_inConventionOptionalNames"/> populated in
        /// TryEmitParameterConversionViaProjection.
        /// </summary>
        private void EmitInConventionOptionalCleanup(CSharpWriter csWriter)
        {
            foreach (var name in _inConventionOptionalNames)
            {
                csWriter.WriteLine($"{name}Swift.DisposeAfterConsumption();");
            }
        }

        /// <summary>
        /// After the P/Invoke returns, emits <c>{param}.Payload.MarkConsumed();</c> for each
        /// non-copyable struct parameter passed under Swift's <c>consuming</c> (Owned) ownership.
        /// On that path the @_cdecl wrapper moved the value out of the C# buffer with <c>.move()</c>
        /// (see <see cref="CdeclParamMapper"/>) and Swift ran the value's deinit exactly once;
        /// MarkConsumed tells the owning <c>SwiftSafeHandle</c> to free the now-empty buffer WITHOUT a
        /// second value-witness Destroy. Without it the value is destroyed twice (P0-06, SIGABRT).
        /// The predicate mirrors CdeclParamMapper's non-copyable branch exactly (Owned ownership +
        /// <c>TypeRecordFlags.NonCopyable</c>) and is gated on the @_cdecl wrapper path, so the
        /// MarkConsumed call is emitted iff the paired <c>.move()</c> was emitted.
        /// </summary>
        private void EmitConsumedNonCopyableParamCleanup(CSharpWriter csWriter)
        {
            if (!_env.MethodDecl.UsesCdeclWrapper)
                return;

            foreach (var argumentDecl in _env.MethodDecl.CSSignature.Skip(1))
            {
                if (argumentDecl.Ownership != ParameterOwnership.Owned)
                    continue;
                if (!_env.TypeDatabase.TryGetTypeRecord(argumentDecl.SwiftTypeSpec, out var record)
                    || !record.Flags.HasFlag(TypeRecordFlags.NonCopyable))
                    continue;

                var csName = NameProvider.GetCSharpParameterName(argumentDecl);
                csWriter.WriteLine($"{csName}.Payload.MarkConsumed();");
            }
        }

        /// <summary>
        /// Determines whether the method's finally block would contain any cleanup code.
        /// When false, the try/finally wrapper is omitted to reduce generated code size.
        /// Must stay in sync with <see cref="EmitSafeHandleRelease"/> and <see cref="EmitCdeclIndirectResultCleanup"/>.
        /// </summary>
        private bool NeedsTryFinallyForMethod()
        {
            // Async instance methods defer all cleanup to callback — no finally needed
            if (_env.MethodDecl.IsAsync && _env.MethodDecl.MethodType != MethodType.Static && !_env.MethodDecl.IsConstructor)
                return false;

            // Instance methods on managed types need SafeHandle release
            if (_env.MethodDecl.MethodType != MethodType.Static && !_env.MethodDecl.IsConstructor)
            {
                if (_env.ParentDecl is StructDecl structDecl)
                {
                    var typeRecord = _env.TypeDatabase.GetTypeRecordOrThrow(structDecl.SwiftTypeName);
                    if (MarshallingHelpers.RequiresMemoryManagement(typeRecord) || !MarshallingHelpers.IsTypeFrozen(typeRecord))
                        return true;
                }
                else if (_env.ParentDecl is ClassDecl classParent && !classParent.IsObjCRooted)
                    return true;
                else if (_env.ParentDecl is EnumDecl)
                    return true;
            }

            // Generic params need VWT Destroy cleanup; closures may need GCHandle.Free
            foreach (var arg in _env.MethodDecl.CSSignature.Skip(1))
            {
                if (arg.IsGeneric) return true;
                if (_env.ClosureHandler.IsClosure(arg)) return true;
            }

            // Indirect result buffer cleanup (NativeMemory.Free)
            if (!_env.MethodDecl.IsConstructor && _syncPlan?.IndirectResultMethod?.CleanupCode != null)
                return true;

            // Existential container heap allocations need cleanup.
            // Async methods register the heap with the callback holder cleanup loop
            // (see EmitExistentialContainerMarshalling); a foreground finally would
            // dangle Swift's pointer once the continuation runs on its own thread.
            if (HasExistentialHeapAllocations && !_requiresSwiftAsync)
                return true;

            return false;
        }

        /// <summary>
        /// Emits NativeMemory.Free for indirect result buffers allocated via NativeMemory.Alloc.
        /// Covers both @_cdecl paths (resultPtr) and non-cdecl SwiftIndirectResult paths.
        /// Constructor paths use _payload SafeHandle (not raw payload pointer) and don't need cleanup.
        /// Non-frozen structs and complex enums transfer ownership to SafeHandle (CleanupCode = null).
        /// </summary>
        private void EmitCdeclIndirectResultCleanup(CSharpWriter csWriter)
        {
            // Constructor indirect results use _payload SafeHandle — no raw memory to free.
            if (_env.MethodDecl.IsConstructor) return;

            var cleanup = _syncPlan?.IndirectResultMethod?.CleanupCode;
            if (cleanup != null)
            {
                foreach (var line in cleanup.Split('\n'))
                {
                    csWriter.WriteLine(line.TrimEnd('\r'));
                }
            }
        }

        /// <summary>
        /// Declares the _cdeclBuf variable before the try block so it's accessible in finally for cleanup.
        /// Emitted for both @_cdecl wrappers and non-cdecl SwiftIndirectResult paths that use
        /// NativeMemory.Alloc. The variable must be in the enclosing scope so the finally block
        /// can free it. Also needed when ownership transfers to SafeHandle (no cleanup code, but
        /// _cdeclBuf is still used in the allocation code and must be declared in the enclosing scope).
        /// Uses _cdeclBuf (not payload) to avoid CS0136 collisions with method parameters named "payload".
        /// </summary>
        private void EmitCdeclPayloadDeclaration(CSharpWriter csWriter)
        {
            if (_syncPlan?.IndirectResultMethod?.CleanupCode != null ||
                _syncPlan?.IndirectResultMethod?.AllocationCode != null)
            {
                csWriter.WriteLine("void* _cdeclBuf = null;");
            }
        }

        /// <summary>
        /// Emits the start of a fixed block for frozen struct setters.
        /// The fixed block pins the struct in memory so we can get a pointer to it.
        /// </summary>
        private void EmitFixedBlockStart(CSharpWriter csWriter)
        {
            if (_syncPlan.FixedBlockHeader == null) return;
            csWriter.WriteLine(_syncPlan.FixedBlockHeader);
            csWriter.WriteLine("{");
            csWriter.Indent++;
        }

        /// <summary>
        /// Emits the end of a fixed block for frozen struct setters.
        /// </summary>
        private void EmitFixedBlockEnd(CSharpWriter csWriter)
        {
            if (_syncPlan.FixedBlockHeader == null) return;
            csWriter.Indent--;
            csWriter.WriteLine("}");
        }

        /// <summary>
        /// Emits nested `fixed (byte* {name}PinnedPtr = {name})` blocks for every
        /// Swift.UnsafeRawBufferPointer / Swift.UnsafeMutableRawBufferPointer parameter,
        /// pinning the C# (ReadOnly)Span&lt;byte&gt; so its data survives across the P/Invoke
        /// call. Empty spans pin to a null pointer, which the Swift side reconstructs as
        /// (Mutable)RawBufferPointer(start: nil, count: 0). Same `fixed` shape works for
        /// both variants — Span&lt;T&gt;.GetPinnableReference() is defined on both Span and
        /// ReadOnlySpan.
        /// </summary>
        private void EmitRawBufferFixedStart(CSharpWriter csWriter)
        {
            foreach (var arg in _env.MethodDecl.CSSignature.Skip(1))
            {
                if (!MarshallingHelpers.IsAnyUnsafeRawBufferPointer(arg.SwiftTypeSpec))
                    continue;
                var csName = NameProvider.GetCSharpParameterName(arg);
                csWriter.WriteLine($"fixed (byte* {csName}PinnedPtr = {csName})");
                csWriter.WriteLine("{");
                csWriter.Indent++;
                _rawBufferFixedDepth++;
            }
        }

        /// <summary>
        /// Closes the nested fixed blocks opened by <see cref="EmitRawBufferFixedStart"/>.
        /// </summary>
        private void EmitRawBufferFixedEnd(CSharpWriter csWriter)
        {
            foreach (var arg in _env.MethodDecl.CSSignature.Skip(1))
            {
                if (!MarshallingHelpers.IsAnyUnsafeRawBufferPointer(arg.SwiftTypeSpec))
                    continue;
                csWriter.Indent--;
                csWriter.WriteLine("}");
                _rawBufferFixedDepth--;
            }
        }

        /// <summary>
        /// Asserts every raw-buffer <c>fixed</c> block opened by
        /// <see cref="EmitRawBufferFixedStart"/> was closed by the paired
        /// <see cref="EmitRawBufferFixedEnd"/>. Called at the tail of every wrapper
        /// emission entry point. Throws loudly on mismatch so a future emitter
        /// that adds an early <c>return</c> (or reorders emission) between Start
        /// and End is caught at generation time, not at the C# compile step
        /// with a cryptic "closing brace expected" error.
        /// </summary>
        private void AssertRawBufferFixedDepthZero()
        {
            if (_rawBufferFixedDepth != 0)
            {
                throw new InvalidOperationException(
                    $"WrapperEmitter: {_rawBufferFixedDepth} unclosed raw-buffer fixed block(s) for " +
                    $"{_env.MethodDecl.ModuleDecl?.Name}.{_env.MethodDecl.Name}. EmitRawBufferFixedStart " +
                    "and EmitRawBufferFixedEnd must be paired — check for an early return between them.");
            }
        }

        /// <summary>
        /// Emits the try block start.
        /// </summary>
        private void EmitTryBlockStart(CSharpWriter csWriter)
        {
            csWriter.WriteLine("try");
            EmitBodyStart(csWriter);
        }

        /// <summary>
        /// Emits the try block end.
        /// </summary>
        private void EmitTryBlockEnd(CSharpWriter csWriter)
        {
            EmitBodyEnd(csWriter);
        }

        /// <summary>
        /// Emits the body start.
        /// </summary>
        /// <param name="csWriter">The IndentedTextWriter instance.</param>
        private void EmitBodyStart(CSharpWriter csWriter)
        {
            csWriter.WriteLine("{");
            csWriter.Indent++;
        }

        /// <summary>
        /// Emits the body end.
        /// </summary>
        /// <param name="csWriter">The IndentedTextWriter instance.</param>
        private void EmitBodyEnd(CSharpWriter csWriter)
        {
            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.WriteLine();
        }

        /// <summary>
        /// Emits SwiftDisposeScope.TryRegister(this) at the end of constructors for heap-backed types.
        /// Only class-projected types register (classes, non-frozen structs, frozen structs with ref fields).
        /// Frozen blittable structs (C# struct) do NOT register — boxing `this` creates a copy.
        /// ObjC-rooted classes do NOT register — lifecycle managed by NSObject.
        /// </summary>
        private void EmitDisposeScopeRegistration(CSharpWriter csWriter)
        {
            if (_env.ParentDecl is ClassDecl classDecl)
            {
                // ObjC-rooted classes use NSObject lifecycle, not DisposeScope
                if (classDecl.IsObjCRooted)
                    return;
                csWriter.WriteLine("Swift.Runtime.SwiftDisposeScope.TryRegister(this);");
                return;
            }

            if (_env.ParentDecl is StructDecl structDecl)
            {
                if (!structDecl.IsFrozen)
                {
                    // Non-frozen struct: always projected as class (heap-backed)
                    csWriter.WriteLine("Swift.Runtime.SwiftDisposeScope.TryRegister(this);");
                    return;
                }

                // Frozen struct: check if projected as class (has ref fields)
                var typeRecord = _env.TypeDatabase.GetTypeRecordOrThrow(structDecl.SwiftTypeName);
                if (MarshallingHelpers.IsFrozenStructProjectedAsClass(typeRecord))
                {
                    csWriter.WriteLine("Swift.Runtime.SwiftDisposeScope.TryRegister(this);");
                }
                // Else: frozen blittable struct → C# struct, no registration
            }
        }

        /// <summary>
        /// Emits the start of an unsafe block if the method body requires unsafe context.
        /// </summary>
        private void EmitUnsafeBlockStart(CSharpWriter csWriter)
        {
            if (_needsUnsafeBody)
            {
                csWriter.WriteLine("unsafe");
                csWriter.WriteLine("{");
                csWriter.Indent++;
            }
        }

        /// <summary>
        /// Emits the end of an unsafe block if one was opened.
        /// </summary>
        private void EmitUnsafeBlockEnd(CSharpWriter csWriter)
        {
            if (_needsUnsafeBody)
            {
                csWriter.Indent--;
                csWriter.WriteLine("}");
            }
        }

        /// <summary>
        /// Gets the resolved simple type name for the parent type, accounting for nested type renames.
        /// Falls back to the declaration name if no TypeRecord is found.
        /// </summary>
        private string GetResolvedTypeName()
        {
            if (_env.ParentDecl is TypeDecl typeDecl &&
                _env.TypeDatabase.TryGetTypeRecord(typeDecl.SwiftTypeName, out var record))
            {
                // TypeRecord Name may be qualified (e.g., "NestedOuter.InnerInfo") — take last segment
                var name = record.CSharpTypeName.Name;
                var lastDot = name.LastIndexOf('.');
                return lastDot >= 0 ? name.Substring(lastDot + 1) : name;
            }
            return _env.ParentDecl.Name;
        }
    }
}
