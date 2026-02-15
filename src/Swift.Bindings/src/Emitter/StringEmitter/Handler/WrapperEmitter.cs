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

            // Emit closure callbacks before constructor body (like methods do)
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
        /// Emits a static factory method for failable initializers (init?).
        /// Instead of a constructor, generates a TryCreate method that returns null on failure.
        /// </summary>
        /// <param name="csWriter">The CSharpWriter instance.</param>
        internal void EmitFailableFactory(CSharpWriter csWriter)
        {
            var typeName = GetResolvedTypeName();

            // Support both struct and class parents
            bool isFrozenValue = false;
            TypeRecord typeRecord;
            if (_env.ParentDecl is StructDecl structDecl)
            {
                typeRecord = _env.TypeDatabase.GetTypeRecordOrThrow(structDecl.SwiftTypeName);
                isFrozenValue = MarshallingHelpers.IsTypeFrozen(typeRecord) && !MarshallingHelpers.RequiresMemoryManagement(typeRecord);
            }
            else if (_env.ParentDecl is ClassDecl classDecl)
            {
                typeRecord = _env.TypeDatabase.GetTypeRecordOrThrow(classDecl.SwiftTypeName);
                // Classes are never frozen value types
                isFrozenValue = false;
            }
            else
            {
                return;
            }

            // Emit closure callbacks before factory body (like methods do)
            bool hasClosures = _env.MethodDecl.CSSignature.Skip(1).Any(_env.ClosureHandler.IsClosure);
            if (hasClosures)
            {
                EmitClosureCallbacks(csWriter);
            }

            XmlDocCommentEmitter.EmitMethodDocComment(csWriter, _env.MethodDecl, isFailableFactory: true);
            // Emit signature: public static bool TryCreate(params, out TypeName result)
            var accessModifier = NameProvider.GetAccessModifier(_env.MethodDecl.Visibility);
            _needsUnsafeBody = true;
            csWriter.WriteLine($"{accessModifier} static bool TryCreate({_wrapperSignature.ParametersString()}{(_wrapperSignature.Parameters.Count > 0 ? ", " : "")}out {typeName} result)");
            EmitBodyStart(csWriter);
            EmitUnsafeBlockStart(csWriter);

            // Declare TypeMetadata, payload, and GCHandle variables for generic/closure args
            EmitDeclarationsForAllocations(csWriter);

            // Get metadata for Self type
            csWriter.WriteLine($"var selfMetadata = TypeMetadata.GetTypeMetadataOrThrow<{typeName}>();");
            csWriter.WriteLine();

            // Get metadata for SwiftOptional<Self>
            var optionalMetadataCall = _env.PInvokeHelperContext != null
                ? $"{_env.PInvokeHelperContext.HelperClassName}.PInvokesForSwiftOptional_MetadataAccessor"
                : "PInvokesForSwiftOptional_MetadataAccessor";
            csWriter.WriteLine($"var optionalMetadata = {optionalMetadataCall}(");
            csWriter.Indent++;
            csWriter.WriteLine("TypeMetadataRequest.Complete, selfMetadata);");
            csWriter.Indent--;
            csWriter.WriteLine();

            // Allocate buffer for SwiftOptional<Self> result
            csWriter.WriteLine("void* resultBuffer = NativeMemory.AllocZeroed(optionalMetadata.Size);");
            csWriter.WriteLine("try");
            csWriter.WriteLine("{");
            csWriter.Indent++;

            // Create SwiftIndirectResult pointing to the optional buffer
            csWriter.WriteLine("var swiftIndirectResult = new SwiftIndirectResult(resultBuffer);");
            csWriter.WriteLine();

            // Marshal arguments using existing helpers
            EmitSafeHandleAddRef(csWriter);
            EmitGenericArguments(csWriter);
            EmitBoundGenericArguments(csWriter);
            EmitClosureMarshalling(csWriter);
            EmitTypeConversions(csWriter);
            EmitProtocolWitnessTables(csWriter);

            // Call P/Invoke (writes Optional<Self> into resultBuffer)
            EmitPInvokeCall(csWriter);

            // Write back inout generic params before error check (so mutations survive exceptions)
            EmitGenericInoutWriteback(csWriter);

            // Check SwiftError if the constructor throws
            EmitSwiftError(csWriter);

            // Check tag: 0 = Some, 1 = None
            csWriter.WriteLine("uint tag = optionalMetadata.ValueWitnessTable->GetEnumTag((byte*)resultBuffer, optionalMetadata);");
            csWriter.WriteLine();
            csWriter.WriteLine("if (tag == 1) // None");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            csWriter.WriteLine("result = default;");
            csWriter.WriteLine("return false;");
            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.WriteLine();

            // Extract value based on type kind
            if (isFrozenValue)
            {
                // Frozen struct (C# value type): read value directly from the optional's payload
                csWriter.WriteLine($"result = *({typeName}*)resultBuffer;");
                csWriter.WriteLine("return true;");
            }
            else
            {
                // Non-frozen or frozen-with-memory-management (C# class):
                // copy payload and create instance via the private SwiftHandle constructor
                csWriter.WriteLines($$"""
                    IntPtr payloadBuffer = (IntPtr)NativeMemory.Alloc(selfMetadata.Size);
                    selfMetadata.ValueWitnessTable->InitializeWithCopy((void*)payloadBuffer, resultBuffer, selfMetadata);
                    result = new {{typeName}}(payloadBuffer);
                    return true;
                    """);
            }

            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.WriteLine("finally");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            csWriter.WriteLine("optionalMetadata.ValueWitnessTable->Destroy(resultBuffer, optionalMetadata);");
            csWriter.WriteLine("NativeMemory.Free(resultBuffer);");
            // Clean up generic payloads and closure GCHandles
            EmitSafeHandleRelease(csWriter);
            csWriter.Indent--;
            csWriter.WriteLine("}");

            EmitUnsafeBlockEnd(csWriter);
            EmitBodyEnd(csWriter);
        }

        /// <summary>
        /// Emits the P/Invoke for the SwiftOptional metadata accessor ($sSqMa).
        /// Called once per type that has failable initializers, to avoid duplicate declarations.
        /// </summary>
        internal void EmitOptionalMetadataAccessorPInvoke(CSharpWriter csWriter)
        {
            if (_env.PInvokeHelperContext != null)
            {
                // AddDeclaration deduplicates by method name
                _env.PInvokeHelperContext.AddDeclaration(new PInvokeDeclaration
                {
                    LibraryPath = "/usr/lib/swift/libswiftCore.dylib",
                    EntryPoint = "$sSqMa",
                    MethodName = "PInvokesForSwiftOptional_MetadataAccessor",
                    ReturnType = "TypeMetadata",
                    ParametersString = "TypeMetadataRequest request, TypeMetadata typeMetadata",
                    IsAsync = false
                });
            }
            else
            {
                csWriter.WriteLine("[DllImport(\"/usr/lib/swift/libswiftCore.dylib\", EntryPoint = \"$sSqMa\")]");
                csWriter.WriteLine("[UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]");
                csWriter.WriteLine("private static extern TypeMetadata PInvokesForSwiftOptional_MetadataAccessor(TypeMetadataRequest request, TypeMetadata typeMetadata);");
                csWriter.WriteLine();
            }
        }

        /// <summary>
        /// Emits the method wrapper.
        /// </summary>
        /// <param name="writer">The IndentedTextWriter instance.</param>
        internal void EmitMethod(CSharpWriter csWriter, SwiftWriter swiftWriter)
        {
            EmitAsyncWrapper(csWriter);
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
            foreach (var genericParameter in _env.MethodDecl.GenericParameters)
            {
                var csTypeParamName = _env.GenericTypeMapping[genericParameter.TypeName].TypeParameter;
                var metadataName = NameProvider.GetMetadataName(csTypeParamName);

                csWriter.WriteLine($"TypeMetadata {metadataName} = TypeMetadata.GetTypeMetadataOrThrow<{csTypeParamName}>();");
            }

            foreach (var argument in _env.MethodDecl.CSSignature.Skip(1).Where(a => a.IsGeneric))
            {
                var csName = NameProvider.GetCSharpParameterName(argument);
                var payloadName = NameProvider.GetPayloadName(csName);
                csWriter.WriteLine($"IntPtr {payloadName} = IntPtr.Zero;");
            }

            // Declare GCHandle variables for escaping closures (except async+throwing which handle their own)
            foreach (var argument in _env.MethodDecl.CSSignature.Skip(1).Where(_env.ClosureHandler.IsClosure))
            {
                var closureTypeSpec = _env.ClosureHandler.GetClosureTypeSpec(argument)!;
                // Skip async+throwing closures - they declare GCHandle in EmitAsyncThrowingClosureMarshallingSetup
                // and free it in the Task.Run's finally block
                if (_env.ClosureHandler.IsSupportedClosure(closureTypeSpec) &&
                    _env.ClosureHandler.RequiresThunk(closureTypeSpec) &&
                    !_env.ClosureHandler.IsAsyncThrowingClosure(closureTypeSpec))
                {
                    var csName = NameProvider.GetCSharpParameterName(argument);
                    csWriter.WriteLine($"GCHandle {csName}Handle = default;");
                }
            }
        }

        /// <summary>
        /// Emits the SwiftSelf variable.
        /// </summary>
        /// <param name="csWriter">The IndentedTextWriter instance.</param>
        private void EmitSwiftSelf(CSharpWriter csWriter)
        {
            if (!_requiresSwiftSelf)
            {
                return;
            }

            // Async methods either use singleton workaround or pass self as explicit IntPtr parameter.
            // Either way, no SwiftSelf variable is needed on the C# side.
            // Standalone closure Cdecl wrapper methods also pass self as explicit IntPtr.
            // Note: wrapper generator paths (ArraySlice, DefaultParam) with HasClosureCdeclWrapper
            // still use extension methods with implicit self → SwiftSelf is still needed.
            if (_requiresSwiftAsync || _env.MethodDecl.UsesFreeFunctionWrapper)
            {
                return;
            }

            // Frozen struct setters use a fixed block to get a pointer to 'this'
            if (_requiresFixedBlock)
            {
                csWriter.WriteLine("var self = new SwiftSelf(__self);");
            }
            else if (_env.ParentDecl is StructDecl structDecl && structDecl.IsFrozen)
            {
                var typeRecord = _env.TypeDatabase.GetTypeRecordOrThrow(structDecl.SwiftTypeName);
                if (MarshallingHelpers.RequiresMemoryManagement(typeRecord))
                {
                    // Setters need pointer semantics (SwiftSelf without type parameter)
                    // Getters can use value semantics (SwiftSelf<T.Buffer>)
                    if (MarshallingHelpers.MethodIsSetter(_env.MethodDecl))
                        csWriter.WriteLine($"var self = new SwiftSelf((void*)_payload.DangerousGetHandle());");
                    else
                    {
                        var resolvedName = GetResolvedTypeName();
                        csWriter.WriteLine($"var self = new SwiftSelf<{resolvedName}.Buffer>(*({resolvedName}.Buffer*)_payload.DangerousGetHandle());");
                    }
                }
                else
                {
                    var resolvedName = GetResolvedTypeName();
                    csWriter.WriteLine($"var self = new SwiftSelf<{resolvedName}>(this);");
                }
            }
            else if (_env.ParentDecl is ClassDecl)
            {
                // For Swift classes, the payload buffer contains a pointer to the class instance.
                // We need to dereference to get the actual class pointer for SwiftSelf.
                csWriter.WriteLine("var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());");
            }
            else
            {
                // For non-frozen structs, the buffer IS the struct data, so buffer address is correct.
                csWriter.WriteLine("var self = new SwiftSelf((void*)_payload.DangerousGetHandle());");
            }

            csWriter.WriteLine();
        }

        /// <summary>
        /// Emits the IndirectResult set up in constructor context.
        /// </summary>
        /// <param name="csWriter">The IndentedTextWriter instance.</param>
        private void EmitIndirectResultConstructor(CSharpWriter csWriter)
        {
            if (!_requiresIndirectResult)
            {
                return;
            }

            // Bug #7: Include generic type parameters in SwiftSafeHandle<> for generic types.
            // Without this, generic types like BatchedCollection<T0> emit SwiftSafeHandle<BatchedCollection>
            // which causes CS0305 (missing type arguments).
            var typeName = GetResolvedTypeName();
            if (_env.ParentDecl is TypeDecl typeDecl && typeDecl.IsGeneric)
            {
                var genericParams = string.Join(", ", typeDecl.GenericParameters.Select(p =>
                    _env.GenericTypeMapping.TryGetValue(p.TypeName, out var mapped) ? mapped.TypeParameter : p.TypeName));
                typeName = $"{typeName}<{genericParams}>";
            }

            var text = $$"""
            _payload = new SwiftSafeHandle<{{typeName}}>((IntPtr)NativeMemory.Alloc(_payloadSize));
            var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
            """;

            csWriter.WriteLines(text);
            csWriter.WriteLine();
        }

        /// <summary>
        /// Emits the IndirectResult set up in method context.
        /// </summary>
        /// <param name="csWriter">The IndentedTextWriter instance.</param>
        private void EmitIndirectResultMethod(CSharpWriter csWriter)
        {
            if (!_requiresIndirectResult)
            {
                return;
            }

            var text = $$"""
            var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<{{_wrapperSignature.ReturnType}}>();
            var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
            var swiftIndirectResult = new SwiftIndirectResult(payload);
            """;

            csWriter.WriteLines(text);
            csWriter.WriteLine();
        }

        /// <summary>
        /// Emits the PInvoke call.
        /// </summary>
        /// <param name="writer">The IndentedTextWriter instance.</param>
        private void EmitPInvokeCall(CSharpWriter csWriter)
        {
            var voidReturn = _env.MethodDecl.CSSignature.First().SwiftTypeSpec.IsEmptyTuple;
            var returnPrefix = (_requiresIndirectResult || _requiresSwiftAsync || voidReturn) ? "" : "var result = ";
            var pInvokeName = NameProvider.GetPInvokeName(_env.MethodDecl);
            var callArgs = _pInvokeSignature.CallArgumentsString();

            // If we're inside a generic type, call via the helper class and pass metadata parameters
            if (_env.PInvokeHelperContext != null)
            {
                var metadataArgs = string.Join(", ", _env.PInvokeHelperContext.GetMetadataArgumentList());
                var fullArgs = string.IsNullOrEmpty(callArgs)
                    ? metadataArgs
                    : (string.IsNullOrEmpty(metadataArgs) ? callArgs : $"{callArgs}, {metadataArgs}");
                csWriter.WriteLine($"{returnPrefix}{_env.PInvokeHelperContext.HelperClassName}.{pInvokeName}({fullArgs});");
            }
            else
            {
                csWriter.WriteLine($"{returnPrefix}{pInvokeName}({callArgs});");
            }
            csWriter.WriteLine();
        }

        /// <summary>
        /// Emits the SwiftError handling.
        /// </summary>
        /// <param name="csWriter">The IndentedTextWriter instance.</param>
        private void EmitSwiftError(CSharpWriter csWriter)
        {
            if (!_requiresSwiftError)
            {
                return;
            }

            // For typed throws with a resolvable error type, throw SwiftException<TError>
            // with the error message but no error value (sync path can't extract from existential box).
            // This provides typed catch(SwiftException<ParseError>) IntelliSense.
            string? syncTypedErrorType = null;
            if (_env.MethodDecl.HasTypedThrows &&
                _env.TypeDatabase.TryGetTypeRecord(_env.MethodDecl.ThrownErrorType!, out var syncErrorTypeRecord))
            {
                syncTypedErrorType = syncErrorTypeRecord.CSharpTypeName.FullyQualifiedName;
            }

            string text;
            if (syncTypedErrorType != null)
            {
                text = $$"""
                if (error.Value != null)
                {
                    throw new SwiftException<{{syncTypedErrorType}}>("Call to Swift method {{_env.MethodDecl.Name}} failed.");
                }
                """;
            }
            else
            {
                text = $$"""
                if (error.Value != null)
                {
                    throw new SwiftRuntimeException("Call to Swift method {{_env.MethodDecl.Name}} failed.");
                }
                """;
            }

            csWriter.WriteLines(text);
            csWriter.WriteLine();
        }

        /// <summary>
        /// Returns only the method-own generic parameters (excluding those inherited from the parent type).
        /// Methods inside generic types have their parent type's generic params copied into GenericParameters
        /// by the parser. These should not be redeclared on the method/constructor signature because:
        /// - For methods: it shadows the type's params (CS0693 warning, semantically wrong)
        /// - For constructors: C# doesn't support generic constructors
        /// </summary>
        private List<GenericArgumentDecl> GetMethodOwnGenericParams()
        {
            if (!_env.MethodDecl.IsGeneric)
                return new List<GenericArgumentDecl>();

            // Accessor methods never have their own generic params
            if (_env.MethodDecl.IsAccessor)
                return new List<GenericArgumentDecl>();

            // If parent is not a generic type, all params are method-own
            if (_env.ParentDecl is not TypeDecl typeDecl || !typeDecl.IsGeneric)
                return _env.MethodDecl.GenericParameters;

            // Filter out params that match the parent type's generic params
            var typeParamNames = new HashSet<string>(typeDecl.GenericParameters.Select(p => p.TypeName));
            return _env.MethodDecl.GenericParameters
                .Where(p => !typeParamNames.Contains(p.TypeName))
                .ToList();
        }

        /// <summary>
        /// Builds the where clause for generic constraints.
        /// Only emits constraints for method-own generic parameters (not type-inherited ones).
        /// Type-level constraints are already declared on the containing type.
        /// </summary>
        /// <returns>The where clause string, or empty string if no constraints.</returns>
        private string BuildWhereClause()
        {
            var methodOwnParams = GetMethodOwnGenericParams();
            if (methodOwnParams.Count == 0)
                return "";

            var constraints = new List<string>();

            foreach (var param in methodOwnParams)
            {
                if (!_env.GenericTypeMapping.TryGetValue(param.TypeName, out var csNameInfo))
                    continue;

                var csName = csNameInfo.TypeParameter;
                var paramConstraints = new List<string> { "ISwiftObject" };

                foreach (var conformance in param.GenericConformances)
                {
                    // Skip unknown protocols and protocols with associated types
                    // (protocols with associated types generate generic interfaces which can't be used as constraints)
                    if (!IsProtocolAvailableForConstraint(conformance.ConformanceTarget))
                        continue;

                    var interfaceName = NameProvider.GetInterfaceName(conformance.ConformanceTarget.Name, moduleName: conformance.ConformanceTarget.Module);
                    paramConstraints.Add(interfaceName);
                }

                constraints.Add($"where {csName} : {string.Join(", ", paramConstraints)}");
            }

            return constraints.Count > 0
                ? "    " + string.Join("\n    ", constraints)
                : "";
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
                // Must be a protocol and must NOT have associated types
                // (protocols with associated types generate generic interfaces which can't be used as constraints)
                return record.Kind == TypeRecordKind.Protocol &&
                       !record.Flags.HasFlag(TypeRecordFlags.HasAssociatedTypes);
            }
            return false;
        }

        /// <summary>
        /// Emits the constructor signature.
        /// </summary>
        /// <param name="csWriter">The IndentedTextWriter instance.</param>
        private void EmitSignatureConstructor(CSharpWriter csWriter)
        {
            // C# does not support generic constructors — never emit <...> on a constructor.
            // Type-level generic params are already declared on the containing type.
            var accessModifier = NameProvider.GetAccessModifier(_env.MethodDecl.Visibility);
            // Use the resolved C# type name (may be renamed for nested type collision avoidance)
            var constructorName = GetResolvedTypeName();
            _needsUnsafeBody = true;
            csWriter.WriteLine($"{accessModifier} {constructorName}({_wrapperSignature.ParametersString(BuildOriginalSwiftTypeAttributes())})");
        }

        /// <summary>
        /// Emits the method signature.
        /// </summary>
        /// <param name="writer">The IndentedTextWriter instance.</param>
        private void EmitSignatureMethod(CSharpWriter csWriter)
        {
            // Only emit <T0, T1, ...> for method-own generic params.
            // Type-level params are already declared on the containing type and must not be redeclared.
            var methodOwnParams = GetMethodOwnGenericParams();
            var genericParams = methodOwnParams.Count > 0
                ? $"<{string.Join(", ", methodOwnParams.Select(p => _env.GenericTypeMapping[p.TypeName].TypeParameter))}>"
                : "";

            bool containsBoundGenerics = _env.MethodDecl.CSSignature.Any(_env.BoundGenericsHandler.IsBoundGeneric);

            // Closure parameters that pass EmitClosureMarshalling emit delegate* unmanaged pointers,
            // which require unsafe context (matches gating at Marshalling.cs:153-156)
            bool hasClosureParams = _env.MethodDecl.CSSignature.Skip(1).Any(arg =>
            {
                if (!_env.ClosureHandler.IsClosure(arg)) return false;
                var closureSpec = _env.ClosureHandler.GetClosureTypeSpec(arg);
                return closureSpec != null && _env.ClosureHandler.IsSupportedClosure(closureSpec);
            });

            // Async constructors emit as static CreateAsync() factory methods
            // (C# doesn't support async constructors)
            bool isAsyncConstructor = _env.MethodDecl.IsConstructor && _env.MethodDecl.IsAsync;

            var staticKeyword = _env.MethodDecl.MethodType == MethodType.Static || _env.ParentDecl is ModuleDecl || isAsyncConstructor ? "static " : "";
            // Class-type returns use sizeof(IntPtr) + pointer dereference for payload allocation
            var returnArg = _env.MethodDecl.CSSignature.First();
            bool hasClassReturn = !returnArg.SwiftTypeSpec.IsEmptyTuple && !returnArg.IsGeneric &&
                !_env.ExistentialHandler.IsExistential(returnArg.SwiftTypeSpec) &&
                !_env.ExistentialHandler.IsOptionalExistential(returnArg.SwiftTypeSpec) &&
                !_env.TupleHandler.IsTuple(returnArg) &&
                _env.TypeDatabase.TryGetTypeRecord(returnArg.SwiftTypeSpec, out var returnTypeRecord) &&
                returnTypeRecord.Kind == TypeRecordKind.Class && !MarshallingHelpers.IsObjCBridged(returnTypeRecord);

            _needsUnsafeBody = _requiresIndirectResult || _requiresSwiftSelf || _requiresSwiftAsync || _requiresSwiftError || methodOwnParams.Count > 0 || containsBoundGenerics || hasClosureParams || hasClassReturn;

            var returnType = _wrapperSignature.ReturnType;
            if (_requiresSwiftAsync)
            {
                returnType = $"Task{(_env.MethodDecl.CSSignature.First().SwiftTypeSpec.IsEmptyTuple ? "" : $"<{_wrapperSignature.ReturnType}>")}";
            }

            // Use CreateAsync for async constructors (with collision detection)
            var methodName = isAsyncConstructor
                ? NameProvider.GetMethodName("createAsync", _env.SiblingPropertyNames)
                : _env.CSharpMethodName;

            var accessModifier = NameProvider.GetAccessModifier(_env.MethodDecl.Visibility);
            // Async methods get CancellationToken as the last parameter
            var cancellationTokenParam = _requiresSwiftAsync
                ? $"{(_wrapperSignature.Parameters.Count > 0 ? ", " : "")}System.Threading.CancellationToken cancellationToken = default"
                : "";
            csWriter.WriteLine($"{accessModifier} {staticKeyword}{returnType} {methodName}{genericParams}({_wrapperSignature.ParametersString(BuildOriginalSwiftTypeAttributes())}{cancellationTokenParam})");

            // Emit where clauses for generic constraints
            var whereClause = BuildWhereClause();
            if (!string.IsNullOrEmpty(whereClause))
                csWriter.WriteLines(whereClause);
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
        /// Emits [Obsolete] with custom DiagnosticId for methods with unmitigated JIT risks or missing exported symbols.
        /// Uses SB0001 for JIT risk (Mono-specific, safe on NativeAOT) and SB0002 for missing symbols.
        /// Combined issues use SB0001 (broader scope). Skips accessors — property-level [Obsolete] requires
        /// separate PropertyHandler wiring. Consumer .targets suppress these via SwiftBindingsInteropMode=Direct.
        /// </summary>
        private void EmitSafetyObsolete(CSharpWriter csWriter)
        {
            bool hasJitRisk = false;
            var issues = new List<string>();

            // Deliverable 1: JIT risk (skip accessors — see property deferral)
            if (!_env.MethodDecl.IsAccessor &&
                _env.MethodDecl.DetectedJitRisks != MonoJitRiskDetector.MonoJitRisk.None)
            {
                var (_, needsWrapper) = PInvokeEmitter.ComputeEntryPoint((MethodDecl)_env.MethodDecl);
                if (!needsWrapper)
                {
                    hasJitRisk = true;
                    issues.Add("Mono JIT crash risk: this method uses CallConvSwift P/Invoke patterns " +
                        "that crash on Mono runtime. Safe on NativeAOT (PublishAot=true)");
                }
            }

            // Deliverable 2: Missing symbol (skip accessors — same as JIT risk above)
            if (!_env.MethodDecl.IsAccessor && _env.MethodDecl.IsMissingExportedSymbol)
            {
                issues.Add("P/Invoke entry point not exported by the library. " +
                    "This method will throw EntryPointNotFoundException at runtime");
            }

            if (issues.Count > 0)
            {
                var message = string.Join(". ", issues) + ".";
                // SB0001: JIT risk (suppressible on NativeAOT via SwiftBindingsInteropMode=Direct)
                // SB0002: Missing symbol (not runtime-dependent — always relevant)
                var diagnosticId = hasJitRisk ? "SB0001" : "SB0002";
                csWriter.WriteLine($"[Obsolete(\"{UnsupportedSwiftTypeSupport.EscapeStringLiteral(message)}\", " +
                    $"DiagnosticId = \"{diagnosticId}\", " +
                    $"UrlFormat = \"https://github.com/malinicr/swift-bindings/blob/main/src/docs/known-issues-workarounds.md\")]");
            }
        }

        /// <summary>
        /// Builds a dictionary mapping parameter names to [OriginalSwiftType] attribute strings
        /// for parameters that fell back to AnyType during type projection.
        /// Returns null when no parameters have fallbacks (avoids allocation).
        /// </summary>
        private Dictionary<string, string>? BuildOriginalSwiftTypeAttributes()
        {
            Dictionary<string, string>? attrs = null;
            var parameters = _wrapperSignature.Parameters;
            var csSignatureParams = _env.MethodDecl.CSSignature.Skip(1).ToList();

            for (int i = 0; i < parameters.Count && i < csSignatureParams.Count; i++)
            {
                if (UnsupportedSwiftTypeSupport.TryFindFallbackInfo(
                    _env.TypeDatabase, _env.ClosureHandler, csSignatureParams[i].SwiftTypeSpec, out var info))
                {
                    attrs ??= new Dictionary<string, string>();
                    attrs[parameters[i].Name] = $"[global::Swift.OriginalSwiftType(\"{UnsupportedSwiftTypeSupport.EscapeStringLiteral(info.SwiftType)}\")]";
                }
            }
            return attrs;
        }

        /// <summary>
        /// Emits [return: OriginalSwiftType("...")] before the method signature when the return type
        /// fell back to AnyType. Not called for constructors (C# constructors have no return type).
        /// </summary>
        private void EmitReturnTypeOriginalSwiftType(CSharpWriter csWriter)
        {
            // Constructors have no return type in C#, so [return:] is invalid
            if (_env.MethodDecl.IsConstructor) return;

            var returnArg = _env.MethodDecl.CSSignature.First();
            if (UnsupportedSwiftTypeSupport.TryFindFallbackInfo(
                _env.TypeDatabase, _env.ClosureHandler, returnArg.SwiftTypeSpec, out var info))
            {
                csWriter.WriteLine($"[return: global::Swift.OriginalSwiftType(\"{UnsupportedSwiftTypeSupport.EscapeStringLiteral(info.SwiftType)}\")]");
            }
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
            if (!_requiresFixedBlock) return;

            var resolvedName = GetResolvedTypeName();
            csWriter.WriteLine($"fixed ({resolvedName}* __self = &this)");
            csWriter.WriteLine("{");
            csWriter.Indent++;
        }

        /// <summary>
        /// Emits the end of a fixed block for frozen struct setters.
        /// </summary>
        private void EmitFixedBlockEnd(CSharpWriter csWriter)
        {
            if (!_requiresFixedBlock) return;

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
