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
        private bool _needsUnsafeBody;

        internal WrapperEmitter(
            MethodEnvironment methodEnv,
            SignatureHandler signatureHandler,
            TypeDatabaseExtensions.AnyTypeFallbackInfo? fallbackInfo = null)
        {
            _env = methodEnv;
            _fallbackInfo = fallbackInfo;
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
                        || (_env.MethodDecl.UsesFreeFunctionWrapper && _requiresSwiftSelf);
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
        }

        /// <summary>
        /// Emits the constructor wrapper.
        /// </summary>
        /// <param name="writer">The IndentedTextWriter instance.</param>
        internal void EmitConstructor(CSharpWriter csWriter)
        {
            bool isGeneric = _env.MethodDecl.IsGeneric;
            bool hasClosures = _env.MethodDecl.CSSignature.Skip(1).Any(_env.ClosureHandler.IsClosure);
            bool needsTryFinally = isGeneric || hasClosures;

            // Emit closure callbacks and error helper P/Invokes before constructor body
            EmitErrorHelperPInvokes(csWriter);
            if (hasClosures)
            {
                EmitClosureCallbacks(csWriter);
            }

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

            if (isGeneric)
            {
                EmitProtocolWitnessTables(csWriter);
            }

            EmitPInvokeCall(csWriter);
            EmitGenericInoutWriteback(csWriter);
            EmitSwiftError(csWriter);
            EmitReturnConstructor(csWriter);

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

            EmitTryBlockStart(csWriter);
            EmitFixedBlockStart(csWriter);

            EmitSwiftSelf(csWriter);
            EmitIndirectResultMethod(csWriter);
            EmitGenericArguments(csWriter);
            EmitBoundGenericArguments(csWriter);
            EmitClosureMarshalling(csWriter);
            EmitTypeConversions(csWriter);
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
                swiftWriter, moduleName, _syncPlan.SwiftError.SwiftErrorTypeName);
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
            EmitBodyEnd(csWriter);
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
