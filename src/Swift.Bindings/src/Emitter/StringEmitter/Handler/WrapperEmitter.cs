// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.CodeDom.Compiler;

namespace BindingsGeneration
{
    /// <summary>
    /// Provides methods for emitting wrappers.
    /// </summary>
    internal partial class WrapperEmitter
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
        private readonly SyncMethodPlan _syncPlan;
        private readonly ModuleEmissionContext _emissionContext;
        private bool _needsUnsafeBody;

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
            // Falls back to untyped when: (a) no typed throws, (b) error type unresolvable,
            // (c) free-function async typed throws (known _payload/this bug — D5 guard).
            useTypedErrorCallback = false;
            if (_env.MethodDecl.HasTypedThrows && _requiresSwiftAsync)
            {
                var parentTypeName_ = (_env.ParentDecl as TypeDecl)?.SwiftTypeName;
                bool isFreeFunctionAsync = parentTypeName_ == null;
                if (!isFreeFunctionAsync && _env.TypeDatabase.TryGetTypeRecord(_env.MethodDecl.ThrownErrorType!, out var errorTypeRecord))
                {
                    typedThrowsSwiftErrorType = _env.MethodDecl.ThrownErrorType!.ToString();
                    typedThrowsCSharpErrorType = errorTypeRecord.CSharpTypeName.FullyQualifiedName;
                    useTypedErrorCallback = true;
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
                    // Standalone closure Cdecl wrappers need it only for instance methods
                    // (static methods have no self parameter to pin).
                    _requiresFixedBlock = MarshallingHelpers.MethodIsSetter(_env.MethodDecl)
                        || (_env.MethodDecl.UsesFreeFunctionWrapper && _requiresSwiftSelf)
                        || (_env.MethodDecl.UsesCdeclMethodWrapper && _requiresSwiftSelf);
                }
            }

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
            bool needsTryFinally = isGeneric || hasClosures;

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

            // Declare TypeMetadata, payload, and GCHandle variables
            if (needsTryFinally)
            {
                EmitDeclarationsForAllocations(csWriter);
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

            if (isGeneric)
            {
                EmitProtocolWitnessTables(csWriter);
            }

            EmitPInvokeCall(csWriter);
            EmitGenericInoutWriteback(csWriter);
            EmitSwiftError(csWriter);
            EmitReturnConstructor(csWriter);
            EmitDisposeScopeRegistration(csWriter);

            // Add cleanup in finally block for generics and closures
            if (needsTryFinally)
            {
                EmitTryBlockEnd(csWriter);
                EmitFinally(csWriter);
            }

            EmitUnsafeBlockEnd(csWriter);
            EmitBodyEnd(csWriter);
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
            bool needsTryFinally = isGeneric || hasClosures;

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

            // Declare GCHandle variables before closure marshalling uses them
            if (needsTryFinally)
            {
                EmitDeclarationsForAllocations(csWriter);
            }

            EmitClosureMarshalling(csWriter);
            EmitTypeConversions(csWriter);
            EmitCdeclFrozenStructMarshalling(csWriter);

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
                EmitPInvokeCall(csWriter);
                EmitSwiftError(csWriter);
                csWriter.WriteLine("if (*buf == IntPtr.Zero)");
                csWriter.Indent++;
                csWriter.WriteLine("throw new InvalidOperationException(\"Swift initializer returned null.\");");
                csWriter.Indent--;
                csWriter.WriteLine("return new ObjCRuntime.NativeHandle(*buf);");
            }
            else
            {
                // Class constructors: P/Invoke returns IntPtr directly (pointer in register)
                EmitPInvokeCall(csWriter);
                EmitSwiftError(csWriter);
                csWriter.WriteLine("if (result == IntPtr.Zero)");
                csWriter.Indent++;
                csWriter.WriteLine("throw new InvalidOperationException(\"Swift initializer returned null.\");");
                csWriter.Indent--;
                csWriter.WriteLine("return new ObjCRuntime.NativeHandle(result);");
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
        }

        /// <summary>
        /// Emits the method wrapper.
        /// </summary>
        /// <param name="writer">The IndentedTextWriter instance.</param>
        internal void EmitMethod(CSharpWriter csWriter, SwiftWriter swiftWriter)
        {
            EmitAsyncWrapper(csWriter);
            EmitErrorHelperPInvokes(csWriter);
            EmitClosureCallbacks(csWriter);
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

            EmitTryBlockStart(csWriter);
            EmitFixedBlockStart(csWriter);

            EmitSwiftSelf(csWriter);
            EmitIndirectResultMethod(csWriter);
            EmitGenericArguments(csWriter);
            EmitBoundGenericArguments(csWriter);
            EmitClosureMarshalling(csWriter);
            EmitTypeConversions(csWriter);
            EmitCdeclFrozenStructMarshalling(csWriter);
            EmitProtocolWitnessTables(csWriter);
            EmitOptionalReturnBuffer(csWriter);
            EmitPInvokeCall(csWriter);
            EmitGenericInoutWriteback(csWriter);
            EmitSwiftError(csWriter);
            EmitReturnMethod(csWriter);

            EmitFixedBlockEnd(csWriter);
            EmitTryBlockEnd(csWriter);
            EmitFinally(csWriter);
            EmitUnsafeBlockEnd(csWriter);
            EmitBodyEnd(csWriter);
        }

        /// <summary>
        /// Emits the declarations for allocations.
        /// </summary>
        private void EmitDeclarationsForAllocations(CSharpWriter csWriter)
        {
            foreach (var line in _syncPlan.DeclarationLines)
                csWriter.WriteLine(line);
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
        /// Checks if a protocol is available in the TypeDatabase and can be used as a generic constraint.
        /// Protocols with associated types cannot be used as constraints because they generate generic
        /// C# interfaces which require type arguments.
        /// </summary>
        /// <param name="protocolTypeName">The protocol type name to check.</param>
        /// <returns>True if the protocol is known and can be used as a constraint, false otherwise.</returns>
        private bool IsProtocolAvailableForConstraint(SwiftTypeName protocolTypeName)
        {
            if (_env.TypeDatabase.TryGetTypeRecord(protocolTypeName, out var record))
            {
                // Must be a protocol and must NOT have associated types or Self requirements
                // (both generate generic interfaces which can't be used as non-generic constraints)
                return record.Kind == TypeRecordKind.Protocol &&
                       !record.Flags.HasFlag(TypeRecordFlags.HasAssociatedTypes) &&
                       !record.Flags.HasFlag(TypeRecordFlags.HasSelfRequirement);
            }
            return false;
        }

        /// <summary>
        /// Emits the finally block.
        /// </summary>
        private void EmitFinally(CSharpWriter csWriter)
        {
            csWriter.WriteLine("finally");
            EmitBodyStart(csWriter);
            EmitSafeHandleRelease(csWriter);
            EmitCdeclIndirectResultCleanup(csWriter);
            EmitBodyEnd(csWriter);
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
                csWriter.WriteLine(cleanup);
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
