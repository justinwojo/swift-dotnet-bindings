// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.CodeDom.Compiler;

namespace BindingsGeneration
{
    internal partial class WrapperEmitter
    {
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

            // A failable CLASS init routed through a @_cdecl wrapper returns a nullable retained
            // class pointer directly (Unmanaged.passRetained(...).toOpaque(), or nil) — there is no
            // Optional<Self> buffer. The P/Invoke returns IntPtr (Zero == nil), matching the Swift
            // wrapper and the non-failable class convention. Struct failable inits (and CallConvSwift
            // class inits) keep the indirect SwiftOptional<Self> buffer path below.
            bool isClassCdecl = _env.MethodDecl.UsesCdeclConstructorWrapper && _env.ParentDecl is ClassDecl;

            // Emit error helper P/Invokes and closure callbacks before factory body
            EmitErrorHelperPInvokes(csWriter);
            bool hasClosures = _env.MethodDecl.CSSignature.Skip(1).Any(_env.ClosureHandler.IsClosure);
            if (hasClosures)
            {
                EmitClosureCallbacks(csWriter);
            }

            EmitFallbackAttribute(csWriter);
            XmlDocCommentEmitter.EmitMethodDocComment(csWriter, _env.MethodDecl, isFailableFactory: true);
            // Emit signature: public static bool TryCreate(params, out TypeName result)
            var accessModifier = NameProvider.GetAccessModifier(_env.MethodDecl.IsSynthesizedAccessor);
            // _needsUnsafeBody is already true: SyncMethodPlan.RequiresUnsafe returns true for all constructors
            // Out-param name. On the indirect (struct / CallConvSwift class) path the P/Invoke writes into
            // resultBuffer and ReturnLocalName is unused, so the out param can safely reuse it (and inherits
            // its collision-avoidance against any init? param projected as "result"). On the class-cdecl path
            // ReturnLocalName is the P/Invoke return local, so the out param MUST be a distinct identifier:
            // the plan already reserved "result" for it (ResolveReturnLocalName), so take the conventional
            // "result" here while still stepping around any projected parameter that uses that name.
            string resultName;
            if (isClassCdecl)
            {
                var outParamNames = new HashSet<string>(
                    _env.MethodDecl.CSSignature.Skip(1).Select(NameProvider.GetCSharpParameterName));
                resultName = "result";
                if (outParamNames.Contains(resultName)) resultName = "__resultOut";
                for (var i = 1; outParamNames.Contains(resultName); i++) resultName = $"__resultOut{i}";
            }
            else
            {
                resultName = ReturnLocalName;
            }
            csWriter.WriteLine($"{accessModifier} static bool TryCreate({_wrapperSignature.ParametersStringWithoutDefaults()}{(_wrapperSignature.Parameters.Count > 0 ? ", " : "")}out {typeName} {resultName})");
            EmitBodyStart(csWriter);
            EmitAvailabilityGuard(csWriter);
            EmitUnsafeBlockStart(csWriter);

            // Declare TypeMetadata, payload, and GCHandle variables for generic/closure args.
            // Existential heap pointers are declared at the unsafe-block top scope so the
            // matching `NativeMemory.Free(...)` in the finally below can see them.
            EmitDeclarationsForAllocations(csWriter);
            EmitExistentialHeapDeclarations(csWriter);

            // For the indirect (struct / CallConvSwift class) path we need Self + SwiftOptional<Self>
            // metadata and a heap buffer to receive Optional<Self>. The class-cdecl path returns the
            // pointer directly, so it needs none of that.
            if (!isClassCdecl)
            {
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
            }

            // try/finally brackets argument marshalling so SafeHandle AddRefs, GCHandles, and
            // existential heap buffers are always released — and, on the indirect path, the
            // Optional<Self> buffer is destroyed and freed.
            csWriter.WriteLine("try");
            csWriter.WriteLine("{");
            csWriter.Indent++;

            // Indirect path: create the result buffer variable — SwiftIndirectResult (Swift ABI) or
            // plain IntPtr (cdecl struct). The class-cdecl path takes no resultPtr (P/Invoke returns IntPtr).
            if (!isClassCdecl)
            {
                if (_env.MethodDecl.UsesCdeclConstructorWrapper)
                    csWriter.WriteLine($"var {ResultPtrName} = (IntPtr)resultBuffer;");
                else
                    csWriter.WriteLine($"var {SwiftIndirectResultName} = new SwiftIndirectResult(resultBuffer);");
                csWriter.WriteLine();
            }

            // Marshal arguments using existing helpers
            EmitSafeHandleAddRef(csWriter);
            EmitGenericArguments(csWriter);
            EmitBoundGenericArguments(csWriter);
            EmitClosureMarshalling(csWriter);
            EmitTypeConversions(csWriter);
            EmitCdeclFrozenStructMarshalling(csWriter);
            EmitExistentialContainerMarshalling(csWriter);
            EmitProtocolWitnessTables(csWriter);

            EmitArrayOwnershipRetain(csWriter);
            // Call P/Invoke. Indirect path writes Optional<Self> into resultBuffer; class-cdecl path
            // returns the retained class pointer (or null) into ReturnLocalName.
            EmitPInvokeCall(csWriter);
            EmitInConventionOptionalCleanup(csWriter);

            // Write back inout generic params before error check (so mutations survive exceptions)
            EmitGenericInoutWriteback(csWriter);

            // Check SwiftError if the constructor throws
            EmitSwiftError(csWriter);

            if (isClassCdecl)
            {
                // Class-cdecl: the wrapper returned a nullable retained class pointer directly.
                csWriter.WriteLine($"if ({ReturnLocalName} == IntPtr.Zero) // nil");
                csWriter.WriteLine("{");
                csWriter.Indent++;
                csWriter.WriteLine($"{resultName} = default!;");
                csWriter.WriteLine("return false;");
                csWriter.Indent--;
                csWriter.WriteLine("}");
                csWriter.WriteLine();

                // Wrap the +1 (passRetained) pointer. ObjC-rooted classes adopt+retain in
                // base(NativeHandle) then DangerousRelease to balance the passRetained +1 (mirrors
                // the non-failable ObjC constructor). Non-ObjC Swift classes hand the +1 straight to
                // SwiftClassHandle via the private (SwiftHandle) constructor.
                bool isObjCRootedClass = _env.ParentDecl is ClassDecl cdRoot && cdRoot.IsObjCRooted;
                if (isObjCRootedClass)
                {
                    csWriter.WriteLine($"{resultName} = new {typeName}(new ObjCRuntime.NativeHandle({ReturnLocalName}));");
                    csWriter.WriteLine($"{resultName}.DangerousRelease();");
                }
                else
                {
                    csWriter.WriteLine($"{resultName} = new {typeName}((SwiftHandle){ReturnLocalName});");
                }
                csWriter.WriteLine("return true;");
            }
            else
            {
                // Check tag: 0 = Some, 1 = None
                // For @_cdecl frozen structs, use the @_cdecl tag helper instead of VWT->GetEnumTag.
                // VWT function pointer calls through CallConvSwift corrupt memory on Mono.
                if (_env.MethodDecl.UsesCdeclConstructorWrapper && isFrozenValue)
                {
                    var tagHelperCall = _env.PInvokeHelperContext != null
                        ? $"{_env.PInvokeHelperContext.HelperClassName}.PInvoke_GetOptionalTag"
                        : "PInvoke_GetOptionalTag";
                    csWriter.WriteLine($"uint tag = {tagHelperCall}((IntPtr)resultBuffer);");
                }
                else
                {
                    csWriter.WriteLine("uint tag = optionalMetadata.ValueWitnessTable->GetEnumTag((byte*)resultBuffer, optionalMetadata);");
                }
                csWriter.WriteLine();
                csWriter.WriteLine("if (tag == 1) // None");
                csWriter.WriteLine("{");
                csWriter.Indent++;
                csWriter.WriteLine($"{resultName} = default!;");
                csWriter.WriteLine("return false;");
                csWriter.Indent--;
                csWriter.WriteLine("}");
                csWriter.WriteLine();

                // Extract value based on type kind
                if (isFrozenValue)
                {
                    // Frozen struct (C# value type): read value directly from the optional's payload
                    csWriter.WriteLine($"{resultName} = *({typeName}*)resultBuffer;");
                    csWriter.WriteLine("return true;");
                }
                else
                {
                    // Non-frozen struct projected as class (opaque payload):
                    // copy payload and create instance via the private SwiftHandle/NativeHandle constructor
                    bool isObjCRooted = _env.ParentDecl is ClassDecl cd && cd.IsObjCRooted;
                    var ctorArg = isObjCRooted
                        ? "new ObjCRuntime.NativeHandle(payloadBuffer)"
                        : "(SwiftHandle)payloadBuffer";
                    csWriter.WriteLines($$"""
                        IntPtr payloadBuffer = (IntPtr)NativeMemory.Alloc(selfMetadata.Size);
                        selfMetadata.ValueWitnessTable->InitializeWithCopy((void*)payloadBuffer, resultBuffer, selfMetadata);
                        {{resultName}} = new {{typeName}}({{ctorArg}});
                        return true;
                        """);
                }
            }

            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.WriteLine("finally");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            if (!isClassCdecl)
            {
                // VWT->Destroy uses a CallConvSwift function pointer.
                // For frozen value types with @_cdecl wrappers, the Destroy is a no-op
                // (no ARC reference counting needed), so skip it to avoid Mono JIT issues.
                // Non-frozen structs need Destroy for proper reference cleanup.
                if (!(_env.MethodDecl.UsesCdeclConstructorWrapper && isFrozenValue))
                    csWriter.WriteLine("optionalMetadata.ValueWitnessTable->Destroy(resultBuffer, optionalMetadata);");
                csWriter.WriteLine("NativeMemory.Free(resultBuffer);");
            }
            EmitExistentialContainerCleanup(csWriter);
            // Clean up generic payloads and closure GCHandles
            EmitSafeHandleRelease(csWriter);
            csWriter.Indent--;
            csWriter.WriteLine("}");

            EmitUnsafeBlockEnd(csWriter);
            EmitBodyEnd(csWriter);
        }

        /// <summary>
        /// Emits the P/Invoke for the Optional tag helper @_cdecl function.
        /// Used by TryCreate to avoid VWT->GetEnumTag on Mono.
        /// Called once per type that has @_cdecl failable initializers on frozen structs.
        /// </summary>
        internal void EmitOptionalTagHelperPInvoke(CSharpWriter csWriter)
        {
            var parentTypeDecl = _env.ParentDecl as TypeDecl;
            if (parentTypeDecl == null) return;

            var entryPoint = ConstructorWrapperEmitter.GetOptionalTagSymbolName(
                parentTypeDecl.SwiftTypeName.Module, parentTypeDecl.Name);

            if (_env.PInvokeHelperContext != null)
            {
                _env.PInvokeHelperContext.AddDeclaration(new PInvokeDeclaration
                {
                    LibraryPath = "SwiftBindings",
                    EntryPoint = entryPoint,
                    MethodName = "PInvoke_GetOptionalTag",
                    ReturnType = "uint",
                    ParametersString = "IntPtr optionalBuffer",
                    IsAsync = false
                });
            }
            else
            {
                PInvokeEmitHelper.EmitDeclaration(csWriter, new PInvokeEmissionInfo
                {
                    LibraryPath = "SwiftBindings",
                    EntryPoint = entryPoint,
                    MethodName = "PInvoke_GetOptionalTag",
                    ReturnType = "uint",
                    ParametersString = "IntPtr optionalBuffer"
                });
                csWriter.WriteLine();
            }
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
                    IsAsync = false,
                    CallingConvention = PInvokeCallingConvention.Swift
                });
            }
            else
            {
                PInvokeEmitHelper.EmitDeclaration(csWriter, new PInvokeEmissionInfo
                {
                    LibraryPath = "/usr/lib/swift/libswiftCore.dylib",
                    EntryPoint = "$sSqMa",
                    MethodName = "PInvokesForSwiftOptional_MetadataAccessor",
                    ReturnType = "TypeMetadata",
                    ParametersString = "TypeMetadataRequest request, TypeMetadata typeMetadata",
                    CallingConvention = PInvokeCallingConvention.Swift
                });
                csWriter.WriteLine();
            }
        }
    }
}
