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
        private readonly SyncMethodPlan _syncPlan;
        private readonly ModuleEmissionContext _emissionContext;
        private bool _needsUnsafeBody;

        /// <summary>
        /// Local-variable name for the P/Invoke return value. Normally "result"; renamed to
        /// "__result" when a method parameter is also named "result" to prevent CS0841/CS0136
        /// shadowing on the self-referential P/Invoke call expression.
        /// </summary>
        private string ReturnLocalName => _syncPlan.ReturnLocalName;
        private readonly AsyncHarnessEmitter _asyncHarness;
        // Legacy fields retained for dead-code in WrapperEmitter.Async.cs until the extraction
        // cleanup pass removes the duplicated helpers. The live path goes through _asyncHarness.
        private System.IO.StringWriter? _asyncHelperWriter;
        private CSharpWriter? _asyncHelperCsWriter;
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

                    // Same ownership check as sync path: complex enums, non-frozen structs,
                    // frozen-with-memory structs, and classes all transfer buffer ownership to SafeHandle.
                    bool isComplexEnum = errorTypeRecord.Kind == TypeRecordKind.Enum &&
                        !errorTypeRecord.Flags.HasFlag(TypeRecordFlags.SimpleEnum);
                    bool isNonFrozenStruct = errorTypeRecord.Kind == TypeRecordKind.Struct &&
                        !MarshallingHelpers.IsTypeFrozen(errorTypeRecord);
                    bool isFrozenStructAsClass = errorTypeRecord.Kind == TypeRecordKind.Struct &&
                        MarshallingHelpers.IsFrozenStructProjectedAsClass(errorTypeRecord);
                    bool isClassError = errorTypeRecord.Kind == TypeRecordKind.Class;
                    typedErrorTransfersOwnershipAsync = isComplexEnum || isNonFrozenStruct || isFrozenStructAsClass || isClassError;
                }
            }

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
        /// </summary>
        private void EmitDeclarationsForAllocations(CSharpWriter csWriter)
        {
            foreach (var line in _syncPlan.DeclarationLines)
                csWriter.WriteLine(line);
            EmitExistentialHeapDeclarations(csWriter);
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
        /// Emits the PInvoke call.
        /// </summary>
        /// <param name="writer">The IndentedTextWriter instance.</param>
        private void EmitPInvokeCall(CSharpWriter csWriter)
        {
            csWriter.WriteLine(_syncPlan.PInvokeCallStatement);
            csWriter.WriteLine();
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
        /// </summary>
        private void EmitExistentialContainerCleanup(CSharpWriter csWriter)
        {
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

            // Existential container heap allocations need cleanup
            if (HasExistentialHeapAllocations)
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
