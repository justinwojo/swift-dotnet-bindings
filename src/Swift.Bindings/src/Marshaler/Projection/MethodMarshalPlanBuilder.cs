// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Builds a SyncMethodPlan from a MethodEnvironment + signatures.
/// Extracts method-level concerns (SwiftSelf, SwiftError, IndirectResult, etc.) into data
/// so WrapperEmitter's Emit* methods become thin plan-reading wrappers.
/// </summary>
internal class MethodMarshalPlanBuilder
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
    private readonly bool _requiresFixedBlock;
    private readonly Func<SwiftTypeName, bool> _isProtocolAvailable;

    internal MethodMarshalPlanBuilder(
        MethodEnvironment env,
        GenericContext genericContext,
        Signature wrapperSignature,
        Signature pInvokeSignature,
        bool requiresIndirectResult,
        bool requiresSwiftSelf,
        bool requiresSwiftError,
        bool requiresSwiftAsync,
        bool requiresFixedBlock,
        Func<SwiftTypeName, bool> isProtocolAvailable)
    {
        _env = env;
        _genericContext = genericContext;
        _wrapperSignature = wrapperSignature;
        _pInvokeSignature = pInvokeSignature;
        _requiresIndirectResult = requiresIndirectResult;
        _requiresSwiftSelf = requiresSwiftSelf;
        _requiresSwiftError = requiresSwiftError;
        _requiresSwiftAsync = requiresSwiftAsync;
        _requiresFixedBlock = requiresFixedBlock;
        _isProtocolAvailable = isProtocolAvailable;
    }

    /// <summary>
    /// Builds the complete SyncMethodPlan.
    /// </summary>
    internal SyncMethodPlan BuildSyncPlan()
    {
        var returnLocalName = ResolveReturnLocalName();
        return new SyncMethodPlan
        {
            SwiftSelf = BuildSwiftSelfSetup(),
            SwiftError = BuildSwiftErrorSetup(),
            IndirectResultConstructor = BuildIndirectResultSetup(isConstructor: true),
            IndirectResultMethod = BuildIndirectResultSetup(isConstructor: false),
            OptionalReturnBuffer = BuildOptionalReturnBufferSetup(),
            DeclarationLines = BuildDeclarationLines(),
            GenericArgumentMarshallingLines = BuildGenericArgumentMarshallingLines(),
            GenericInoutWritebackLines = BuildGenericInoutWritebackLines(),
            WitnessTableStatements = BuildWitnessTableStatements(),
            PInvokeCallStatement = BuildPInvokeCallStatement(returnLocalName),
            ReturnLocalName = returnLocalName,
            FixedBlockHeader = BuildFixedBlockHeader(),
            RequiresUnsafe = ComputeRequiresUnsafe(),
        };
    }

    /// <summary>
    /// Returns the local variable name for the P/Invoke return value.
    /// Defaults to "result" but falls back to "__result", "__result1", "__result2", …
    /// when a method parameter shares the chosen name, avoiding CS0841/CS0136 shadowing
    /// on the self-referential RHS.
    /// </summary>
    private string ResolveReturnLocalName()
    {
        var paramNames = new HashSet<string>(
            _env.MethodDecl.CSSignature.Skip(1).Select(NameProvider.GetCSharpParameterName));
        if (!paramNames.Contains("result")) return "result";
        if (!paramNames.Contains("__result")) return "__result";
        for (var i = 1; ; i++)
        {
            var candidate = $"__result{i}";
            if (!paramNames.Contains(candidate)) return candidate;
        }
    }

    /// <summary>
    /// Builds generic argument marshalling lines (stackalloc + MarshalToSwift per argument).
    /// Emitted inside the try block.
    /// </summary>
    private IReadOnlyList<string> BuildGenericArgumentMarshallingLines()
    {
        var lines = new List<string>();
        foreach (var argument in _env.MethodDecl.CSSignature.Skip(1).Where(a => a.IsGeneric))
        {
            var csName = NameProvider.GetCSharpParameterName(argument);
            var csTypeParamName = _env.GenericTypeMapping[argument.SwiftTypeSpec.ToString()].TypeParameter;
            var metadataName = NameProvider.GetMetadataName(csTypeParamName);
            var payloadName = NameProvider.GetPayloadName(csName);

            lines.Add($"Span<byte> {payloadName}Span = stackalloc byte[(int){metadataName}.Size];");
            lines.Add($"{payloadName} = (IntPtr)Unsafe.AsPointer(ref MemoryMarshal.GetReference({payloadName}Span));");
            lines.Add($"SwiftMarshal.MarshalToSwift({csName}, ref {payloadName}Span);");
        }
        return lines;
    }

    /// <summary>
    /// Builds generic inout writeback lines emitted after the P/Invoke call.
    /// </summary>
    private IReadOnlyList<string> BuildGenericInoutWritebackLines()
    {
        var lines = new List<string>();
        foreach (var argument in _env.MethodDecl.CSSignature.Skip(1).Where(a => a.IsGeneric && a.IsInOut))
        {
            var csName = NameProvider.GetCSharpParameterName(argument);
            var csTypeParamName = _env.GenericTypeMapping[argument.SwiftTypeSpec.ToString()].TypeParameter;
            var payloadName = NameProvider.GetPayloadName(csName);

            lines.Add($"// Write back modified inout generic parameter");
            lines.Add($"{csName} = SwiftMarshal.MarshalFromSwift<{csTypeParamName}>({payloadName});");
        }
        return lines;
    }

    /// <summary>
    /// Builds protocol witness table extraction statements.
    /// </summary>
    private IReadOnlyList<string> BuildWitnessTableStatements()
    {
        // Both constructor and method @_cdecl wrappers need PWT for constrained generic types —
        // the metadata accessor requires PWT to instantiate the specialized metatype.
        var lines = new List<string>();
        foreach (var genericParameter in _env.MethodDecl.GenericParameters)
        {
            var csTypeParamName = _env.GenericTypeMapping[genericParameter.TypeName].TypeParameter;
            var conformances = genericParameter.GenericConformances.OrderBy(c => c.ConformanceTarget.ModuleQualifiedName);
            foreach (var conformance in conformances)
            {
                if (!_isProtocolAvailable(conformance.ConformanceTarget))
                    continue;

                var pwtName = NameProvider.GetProtocolWitnessTableName(csTypeParamName, conformance.ConformanceTarget.Name);
                // Use the resolved emission namespace (umbrella fallback) so the PWT
                // generic argument matches the actual emitted interface namespace.
                var emissionModule = ProtocolConformanceHelper.ResolveProtocolEmissionModule(
                    conformance.ConformanceTarget, _env.TypeDatabase);
                var protocolName = NameProvider.GetInterfaceName(
                    conformance.ConformanceTarget.Name,
                    moduleName: emissionModule,
                    currentModuleName: _env.MethodDecl.ModuleDecl?.Name ?? "");
                lines.Add($"var {pwtName} = ProtocolWitnessTable.GetOrThrow<{csTypeParamName}, {protocolName}>();");
            }
        }
        return lines;
    }

    /// <summary>
    /// Builds the declaration lines emitted before the try block:
    /// TypeMetadata, IntPtr payload, and GCHandle variables.
    /// </summary>
    private IReadOnlyList<string> BuildDeclarationLines()
    {
        var lines = new List<string>();

        foreach (var genericParameter in _env.MethodDecl.GenericParameters)
        {
            var csTypeParamName = _env.GenericTypeMapping[genericParameter.TypeName].TypeParameter;
            var metadataName = NameProvider.GetMetadataName(csTypeParamName);
            lines.Add($"TypeMetadata {metadataName} = TypeMetadata.GetTypeMetadataOrThrow<{csTypeParamName}>();");
        }

        // Swift's calling convention for non-constructor methods on a generic struct expects
        // the PARENT type's metadata in the first metadata slot — NOT individual generic
        // param metadata. Compute it here so the call site can pass parentMetatype.Handle
        // in place of the first parent generic param's metadata.
        if (RequiresParentMetadataInjection())
        {
            var perParamMetadata = string.Join(", ", _env.PInvokeHelperContext!.GetTypeMetadataAccessorArgumentList());
            lines.Add($"var _parentMetatype = {_env.PInvokeHelperContext.HelperClassName}.PInvoke_getMetadata(TypeMetadataRequest.Complete, {perParamMetadata});");
        }

        foreach (var argument in _env.MethodDecl.CSSignature.Skip(1).Where(a => a.IsGeneric))
        {
            var csName = NameProvider.GetCSharpParameterName(argument);
            var payloadName = NameProvider.GetPayloadName(csName);
            lines.Add($"IntPtr {payloadName} = IntPtr.Zero;");
        }

        // Pre-declare SwiftError for 'ref' passing (caller must zero-initialize per Swift ABI).
        // @_cdecl wrappers and native thunks use 'out IntPtr errorPtr' — no SwiftError needed.
        if (_requiresSwiftError && !_env.MethodDecl.UsesCdeclWrapper && !_env.MethodDecl.UsesNativeThunk)
            lines.Add("var swiftError = default(SwiftError);");

        // Multi-element generic tuple non-cdecl returns split each element into its own @out
        // buffer. The first buffer reuses _cdeclBuf (declared by EmitCdeclPayloadDeclaration);
        // the rest need their own pre-try declarations so the finally block can free them.
        if (MarshallingHelpers.IsMultiElementGenericTupleIndirectReturn(_env))
        {
            var tupleSpec = (TupleTypeSpec)_env.MethodDecl.CSSignature.First().SwiftTypeSpec;
            for (int i = 1; i < tupleSpec.Elements.Count; i++)
            {
                lines.Add($"void* _tupleResult{i}Buf = null;");
            }
        }

        // Declare GCHandle variables for escaping closures. Skip only closures
        // that the async bridge (Session A/B throwing baseline, Session C
        // non-throwing baseline) handles internally via
        // EmitAsyncThrowingClosureMarshallingSetup — those emit their own
        // handle. Non-baseline async non-throwing closures still take the
        // legacy SwiftClosureData path, which requires this declaration.
        var closureParamCount = _env.MethodDecl.CSSignature.Skip(1).Count(_env.ClosureHandler.IsClosure);
        foreach (var argument in _env.MethodDecl.CSSignature.Skip(1).Where(_env.ClosureHandler.IsClosure))
        {
            var closureTypeSpec = _env.ClosureHandler.GetClosureTypeSpec(argument)!;
            if (_env.ClosureHandler.IsSupportedClosure(closureTypeSpec) &&
                _env.ClosureHandler.RequiresThunk(closureTypeSpec, _env.MethodDecl.MangledName, closureParamCount) &&
                !_env.ClosureHandler.IsAsyncThrowingClosure(closureTypeSpec) &&
                !_env.ClosureHandler.IsBaselineAsyncNonThrowingClosure(closureTypeSpec))
            {
                var csName = NameProvider.GetCSharpParameterName(argument);
                lines.Add($"GCHandle {csName}Handle = default;");

                // Track ownership transfer for cdecl-wrapped escaping closures.
                // The Swift wrapper constructs an `_SBClosureCtx` box at function
                // entry (via `_sbWrapClosureContext`); once the P/Invoke returns,
                // Swift's ARC owns the GCHandle. The flag is set to true right
                // after the call so the finally block can distinguish "ownership
                // crossed into Swift" (skip free — Swift will release via deinit)
                // from "alloc happened but call never reached the wrapper body"
                // (free here — closes the leak window when C# marshalling throws
                // between Alloc and the P/Invoke, or when the entry point cannot
                // be resolved).
                if (_env.MethodDecl.HasCdeclClosureMarshalling &&
                    !_env.ClosureHandler.IsAsyncClosure(closureTypeSpec) &&
                    WrapperValidation.IsEffectivelyEscaping(closureTypeSpec, argument.SwiftTypeSpec, _env.ClosureHandler))
                {
                    lines.Add($"bool {csName}Transferred = false;");
                }
            }
        }

        return lines;
    }

    /// <summary>
    /// Builds the optional return buffer setup for large Optional return values.
    /// Returns null when not needed.
    /// </summary>
    private OptionalPointerWrapperSetup? BuildOptionalReturnBufferSetup()
    {
        if (!_env.BoundGenericsHandler.IsLargeOptionalReturn(_env.MethodDecl) ||
            (!_env.MethodDecl.HasOptionalPointerWrapper && !_env.MethodDecl.UsesWrapperLibrary))
            return null;

        // @_cdecl IndirectResult handles the allocation via resultPtr — no separate _optRetPtr needed.
        if (_requiresIndirectResult)
            return null;

        var returnArg = _env.MethodDecl.CSSignature.First();
        var projection = s_projectionFactory.Project(returnArg.SwiftTypeSpec,
            new ProjectionContext { TypeDatabase = _env.TypeDatabase, IsParameter = false, GenericContext = _genericContext, ParentTypeDecl = _env.ParentDecl as TypeDecl, CurrentModuleName = _env.ExistentialHandler.CurrentModuleName });
        var swiftType = projection?.ContainerTypeName ?? _wrapperSignature.ReturnType;

        return new OptionalPointerWrapperSetup
        {
            OptionalTypeName = swiftType,
            AllocationCode = $$"""
                var _optRetSize = (int)TypeMetadata.GetTypeMetadataOrThrow<{{swiftType}}>().Size;
                byte* _optRetBuf = stackalloc byte[_optRetSize];
                IntPtr _optRetPtr = (IntPtr)_optRetBuf;
                """
        };
    }

    /// <summary>
    /// Builds the SwiftSelf creation code for instance methods.
    /// Returns null for static methods, async methods, or free function wrappers.
    /// </summary>
    private SwiftSelfSetup? BuildSwiftSelfSetup()
    {
        if (!_requiresSwiftSelf)
            return null;

        // Async methods, standalone closure Cdecl wrappers, @_cdecl method wrappers, and native thunks
        // pass self as explicit IntPtr parameter — no SwiftSelf needed.
        if (_requiresSwiftAsync || _env.MethodDecl.UsesFreeFunctionWrapper || _env.MethodDecl.UsesCdeclMethodWrapper || _env.MethodDecl.UsesNativeThunk)
            return null;

        // Frozen struct setters use a fixed block to get a pointer to 'this'
        if (_requiresFixedBlock)
        {
            return new SwiftSelfSetup
            {
                Kind = SwiftSelfKind.FixedBlock,
                CreationCode = "var self = new SwiftSelf(__self);"
            };
        }

        if (_env.ParentDecl is StructDecl structDecl && structDecl.IsFrozen)
        {
            var typeRecord = _env.TypeDatabase.GetTypeRecordOrThrow(structDecl.SwiftTypeName);
            if (MarshallingHelpers.RequiresMemoryManagement(typeRecord))
            {
                if (MarshallingHelpers.MethodIsSetter(_env.MethodDecl))
                {
                    return new SwiftSelfSetup
                    {
                        Kind = SwiftSelfKind.FrozenStructSetter,
                        CreationCode = $"var self = new SwiftSelf((void*)_payload.DangerousGetHandle());"
                    };
                }
                else
                {
                    var resolvedName = GetResolvedTypeName();
                    return new SwiftSelfSetup
                    {
                        Kind = SwiftSelfKind.FrozenStructBuffer,
                        CreationCode = $"var self = new SwiftSelf<{resolvedName}.Buffer>(*({resolvedName}.Buffer*)_payload.DangerousGetHandle());",
                        ResolvedTypeName = resolvedName
                    };
                }
            }
            else
            {
                var resolvedName = GetResolvedTypeName();
                return new SwiftSelfSetup
                {
                    Kind = SwiftSelfKind.FrozenStructValue,
                    CreationCode = $"var self = new SwiftSelf<{resolvedName}>(this);",
                    ResolvedTypeName = resolvedName
                };
            }
        }

        if (_env.ParentDecl is ClassDecl classDecl)
        {
            if (classDecl.IsObjCRooted)
            {
                // ObjC-rooted: Handle IS the object pointer (no buffer indirection).
                return new SwiftSelfSetup
                {
                    Kind = SwiftSelfKind.ObjCRootedClass,
                    CreationCode = "var self = new SwiftSelf((void*)Handle);"
                };
            }
            return new SwiftSelfSetup
            {
                Kind = SwiftSelfKind.Class,
                CreationCode = "var self = new SwiftSelf((void*)_handle.DangerousGetHandle());"
            };
        }

        // Non-frozen struct: buffer IS the struct data
        return new SwiftSelfSetup
        {
            Kind = SwiftSelfKind.NonFrozenStruct,
            CreationCode = "var self = new SwiftSelf((void*)_payload.DangerousGetHandle());"
        };
    }

    /// <summary>
    /// Builds the IndirectResult setup for constructor or method context.
    /// Returns null when the method doesn't require indirect result.
    /// </summary>
    private IndirectResultSetup? BuildIndirectResultSetup(bool isConstructor)
    {
        if (!_requiresIndirectResult)
            return null;

        if (isConstructor)
        {
            // Include generic type parameters in SwiftSafeHandle<> for generic types.
            // Nested-on-generic-outer types drop the redundant outer generics (Option A parity
            // with GenericTypeEmitter.GetTypeDeclOwnGenericParams).
            var typeName = GetResolvedTypeName();
            if (_env.ParentDecl is TypeDecl typeDecl && typeDecl.IsGeneric)
            {
                var ownParams = GenericTypeEmitter.GetTypeDeclOwnGenericParams(typeDecl);
                if (ownParams.Count > 0)
                {
                    var genericParams = string.Join(", ", ownParams.Select(p =>
                        _env.GenericTypeMapping.TryGetValue(p.TypeName, out var mapped) ? mapped.TypeParameter : p.TypeName));
                    typeName = $"{typeName}<{genericParams}>";
                }
            }

            // For derived classes, SwiftSafeHandle<T> must use the root base type
            // because _payload is declared as SwiftSafeHandle<RootBase> on the base class.
            // Cross-module Swift parents (Bug #14) participate in the same rule: the inherited
            // _handle field was declared in the parent assembly with the cross-module root's
            // type parameter, so derived constructors must allocate against that same root.
            var safeHandleTypeName = typeName;
            if (_env.ParentDecl is ClassDecl cd && cd.HasResolvedSuperclass)
            {
                var root = cd;
                while (root.HasResolvedSuperclass)
                    root = root.ResolvedSuperclass!;
                safeHandleTypeName = GenericTypeEmitter.GetTypeNameWithGenerics(root);
            }
            else if (_env.ParentDecl is ClassDecl crossCd && crossCd.HasCrossModuleSwiftSuperclass)
            {
                safeHandleTypeName = ClassISwiftObjectMethodWriter.GetRootBaseTypeNameWithGenerics(crossCd, _env.TypeDatabase);
            }

            if (_env.MethodDecl.UsesCdeclConstructorWrapper)
            {
                // Frozen struct @_cdecl constructors: the Swift wrapper writes to resultPtr
                // but the struct is projected as a C# value type (no _payload/_payloadSize).
                // Allocate a stack buffer and read the result back after the P/Invoke call.
                if (_env.ParentDecl is StructDecl frozenStructDecl && frozenStructDecl.IsFrozen)
                {
                    var typeRecord = _env.TypeDatabase.GetTypeRecordOrThrow(frozenStructDecl.SwiftTypeName);
                    if (MarshallingHelpers.IsFrozenStructProjectedAsClass(typeRecord))
                    {
                        // Frozen struct with ref fields (projected as class with Buffer):
                        // allocate heap buffer and assign to _payload after the call.
                        return new IndirectResultSetup
                        {
                            IsConstructor = true,
                            ReturnTypeName = typeName,
                            AllocationCode = $$"""
                                IntPtr bufferPtr = (IntPtr)NativeMemory.Alloc((nuint)sizeof({{typeName}}.Buffer));
                                var resultPtr = bufferPtr;
                                """
                        };
                    }
                    else
                    {
                        // Frozen blittable struct (projected as C# struct):
                        // allocate stack buffer and assign to 'this' after the call.
                        return new IndirectResultSetup
                        {
                            IsConstructor = true,
                            ReturnTypeName = typeName,
                            AllocationCode = $$"""
                                {{typeName}} _cdeclResult;
                                var resultPtr = (IntPtr)(&_cdeclResult);
                                """
                        };
                    }
                }

                return new IndirectResultSetup
                {
                    IsConstructor = true,
                    ReturnTypeName = typeName,
                    AllocationCode = $$"""
                        _payload = new SwiftSafeHandle<{{safeHandleTypeName}}>((IntPtr)NativeMemory.Alloc(_payloadSize));
                        var resultPtr = _payload.DangerousGetHandle();
                        """
                };
            }

            return new IndirectResultSetup
            {
                IsConstructor = true,
                ReturnTypeName = typeName,
                AllocationCode = $$"""
                    _payload = new SwiftSafeHandle<{{safeHandleTypeName}}>((IntPtr)NativeMemory.Alloc(_payloadSize));
                    var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
                    """
            };
        }
        else
        {
            if (_env.MethodDecl.UsesCdeclWrapper)
            {
                // @_cdecl wrapper: plain IntPtr result buffer, not SwiftIndirectResult register.
                // _cdeclBuf is declared before try block (EmitCdeclPayloadDeclaration)
                // so it's accessible in finally for NativeMemory.Free cleanup.
                // NOTE: Native thunks are NOT included here. Thunks rely on AAPCS64's hidden x8
                // register for struct return buffers — SwiftIndirectResult is correct for thunks.
                var returnArg = _env.MethodDecl.CSSignature.First();
                var allocTypeName = _wrapperSignature.ReturnType;

                // Closure returns: fixed 2-pointer size (funcPtr + context = SwiftClosureData).
                // Optional<Closure> uses extra-inhabitant encoding (nil = zero funcPtr),
                // so it has the same 2-pointer layout.
                if (returnArg.SwiftTypeSpec is ClosureTypeSpec ||
                    _env.ClosureHandler.IsOptionalClosure(returnArg.SwiftTypeSpec))
                {
                    return new IndirectResultSetup
                    {
                        IsConstructor = false,
                        ReturnTypeName = "SwiftClosureData",
                        AllocationCode = """
                            _cdeclBuf = NativeMemory.Alloc((nuint)(nint.Size * 2));
                            var resultPtr = (IntPtr)_cdeclBuf;
                            """,
                        CleanupCode = "NativeMemory.Free(_cdeclBuf);"
                    };
                }

                // @_cdecl String returns: the Swift wrapper converts the String to SBW_Utf8Slice
                // (pointer + length), not a Swift.String value. Use fixed Utf8Slice allocation.
                if (returnArg.SwiftTypeSpec is NamedTypeSpec strRetNts && strRetNts.Name == "Swift.String")
                {
                    allocTypeName = "Utf8Slice";
                }
                // Projected types: C# wrapper uses the public type (IReadOnlyList<T>, double?,
                // IReadOnlyDictionary<K,V>, etc.) but allocation needs a type with valid TypeMetadata.
                // Use MarshalFromSwiftType (not ContainerTypeName) because FrozenWithMemoryProjection's
                // ContainerTypeName is "Foo.Buffer" (ABI struct without metadata), while
                // MarshalFromSwiftType is the real type name that has TypeMetadata.
                // Skip Optional<reference-type> which uses IntPtr (no indirect result buffer).
                else if (!(MethodWrapperEmitter.IsOptionalType(returnArg.SwiftTypeSpec) &&
                    CdeclParamMapper.IsOptionalWithReferenceInner(returnArg.SwiftTypeSpec, _env.TypeDatabase)))
                {
                    var projection = s_projectionFactory.Project(returnArg.SwiftTypeSpec,
                        new ProjectionContext { TypeDatabase = _env.TypeDatabase, IsParameter = false,
                            GenericContext = _genericContext,
                            ParentTypeDecl = _env.ParentDecl as TypeDecl,
                            CurrentModuleName = _env.ExistentialHandler.CurrentModuleName });
                    if (projection != null)
                        allocTypeName = projection.MarshalFromSwiftType;
                }

                // Determine allocation strategy based on the return type:
                // - Utf8Slice: fixed 2-pointer size (C# struct, no Swift metadata)
                // - Frozen blittable structs (CGSize, CGPoint, CGRect): plain C# structs,
                //   no ISwiftObject/TypeMetadata. Use Unsafe.SizeOf for allocation.
                // - All other types: use TypeMetadata for correct size.
                var isUtf8Slice = allocTypeName == "Utf8Slice";
                bool isFrozenBlittable = false;
                if (!isUtf8Slice && returnArg.SwiftTypeSpec is NamedTypeSpec allocNts && allocNts.HasModule())
                {
                    var allocSwiftName = SwiftTypeName.FromTypeSpec(allocNts);
                    if (_env.TypeDatabase.TryGetTypeRecord(allocSwiftName, out var allocRecord))
                    {
                        isFrozenBlittable = allocRecord.Kind == TypeRecordKind.Struct &&
                            MarshallingHelpers.IsTypeFrozen(allocRecord) &&
                            !MarshallingHelpers.RequiresMemoryManagement(allocRecord);
                    }
                }

                string allocCode;
                if (isUtf8Slice)
                {
                    allocCode = """
                        _cdeclBuf = NativeMemory.Alloc((nuint)(nint.Size * 2));
                        var resultPtr = (IntPtr)_cdeclBuf;
                        """;
                }
                else if (isFrozenBlittable)
                {
                    // Frozen blittable structs (CGSize, CGPoint, CGRect): plain C# structs with
                    // no ISwiftObject implementation. Use Unsafe.SizeOf instead of TypeMetadata.
                    allocCode = $$"""
                        _cdeclBuf = NativeMemory.Alloc((nuint)System.Runtime.CompilerServices.Unsafe.SizeOf<{{allocTypeName}}>());
                        var resultPtr = (IntPtr)_cdeclBuf;
                        """;
                }
                else
                {
                    // Decomposed Optional getter: allocate inner type buffer + 1 byte for hasValue flag.
                    // The Swift wrapper writes inner payload and hasValue separately (no Optional<T> VWT).
                    if (_env.MethodDecl.UsesCdeclPropertyWrapper &&
                        !_env.MethodDecl.IsSubscriptAccessor &&
                        OptionalMarshalClassifier.IsDecomposed(returnArg.SwiftTypeSpec, _env.TypeDatabase))
                    {
                        var innerSpec = ((NamedTypeSpec)returnArg.SwiftTypeSpec).GenericParameters[0];
                        var innerProjection = s_projectionFactory.Project(innerSpec,
                            new ProjectionContext { TypeDatabase = _env.TypeDatabase, IsParameter = false,
                                GenericContext = _genericContext, ParentTypeDecl = _env.ParentDecl as TypeDecl,
                                CurrentModuleName = _env.ExistentialHandler.CurrentModuleName });
                        // Generic parameters: layout is unknown at compile time, so size the buffer from the
                        // C# generic param's runtime metadata. The wrapper writes Value.self into resultPtr,
                        // so a class T (8-byte pointer) and a large struct T must each get exactly Size bytes
                        // followed by the hasValue byte. Falling through to the existential branch sizes the
                        // buffer to ExistentialContainer1 (16 bytes), which silently overruns for any T whose
                        // metadata.Size exceeds 15 bytes.
                        bool isGenericParam = (innerProjection as BlittableProjection)?.IsGenericParameter == true;
                        // Protocol existential inner types: ExistentialContainer1 is a C# interop struct
                        // without Swift type metadata. Use Unsafe.SizeOf instead of TypeMetadata.
                        bool isExistentialInner = !isGenericParam && (innerSpec is ProtocolListTypeSpec ||
                            (_env.TypeDatabase.TryGetTypeRecord(innerSpec, out var innerRecord) &&
                             (innerRecord.Kind == TypeRecordKind.Protocol || innerRecord.Kind == TypeRecordKind.Existential)));
                        if (isExistentialInner)
                        {
                            // Use the projected container type (ExistentialContainer1, ExistentialContainer2, etc.)
                            // to get the correct size for protocol compositions.
                            var containerTypeName = (innerProjection as ExistentialProjection)?.PInvokeType ?? "ExistentialContainer1";
                            allocCode = $$"""
                                var _innerSize = (nuint)System.Runtime.CompilerServices.Unsafe.SizeOf<{{containerTypeName}}>();
                                var _bufSize = Math.Max(_innerSize + 1, 16);
                                _cdeclBuf = NativeMemory.AllocZeroed(_bufSize);
                                var resultPtr = (IntPtr)_cdeclBuf;
                                var hasValuePtr = (IntPtr)((byte*)_cdeclBuf + (int)_innerSize);
                                """;
                        }
                        else
                        {
                            var innerTypeName = innerProjection?.MarshalFromSwiftType ?? allocTypeName;
                            allocCode = $$"""
                                var innerMetadata = TypeMetadata.GetTypeMetadataOrThrow<{{innerTypeName}}>();
                                var _bufSize = Math.Max((nuint)innerMetadata.Size + 1, 16);
                                _cdeclBuf = NativeMemory.AllocZeroed(_bufSize);
                                var resultPtr = (IntPtr)_cdeclBuf;
                                var hasValuePtr = (IntPtr)((byte*)_cdeclBuf + innerMetadata.Size);
                                """;
                        }
                    }
                    // For Optional returns, ensure the buffer is large enough for the tag byte.
                    // VWT metadata.Size may return incorrect values for Optional<Int32> on some
                    // runtimes (Mono iOS Simulator). Use AllocZeroed with minimum of 16 bytes.
                    else if (MethodWrapperEmitter.IsOptionalType(returnArg.SwiftTypeSpec))
                    {
                        allocCode = $$"""
                            var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<{{allocTypeName}}>();
                            _cdeclBuf = NativeMemory.AllocZeroed(Math.Max((nuint)returnMetadata.Size, 16));
                            var resultPtr = (IntPtr)_cdeclBuf;
                            """;
                    }
                    else if (returnArg.SwiftTypeSpec is TupleTypeSpec tupleSpec)
                    {
                        // Tuple returns: use GetTupleTypeMetadataFromElements() which is NativeAOT-safe
                        // (direct P/Invoke to swift_getTupleTypeMetadata). The generic
                        // GetTypeMetadataOrThrow<ValueTuple<T1,T2>>() path uses MakeGenericMethod
                        // which gets trimmed on NativeAOT.
                        var elementMetaCalls = new List<string>();
                        foreach (var element in tupleSpec.Elements)
                        {
                            var elemTypeName = GetTupleElementMetadataTypeName(element);
                            elementMetaCalls.Add($"TypeMetadata.GetTypeMetadataOrThrow<{elemTypeName}>()");
                        }
                        var elementsJoined = string.Join(",\n                    ", elementMetaCalls);
                        allocCode = $$"""
                            var returnMetadata = TypeMetadata.GetTupleTypeMetadataFromElements(
                                {{elementsJoined}});
                            _cdeclBuf = NativeMemory.Alloc((nuint)returnMetadata.Size);
                            var resultPtr = (IntPtr)_cdeclBuf;
                            """;
                    }
                    else
                    {
                        allocCode = $$"""
                            var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<{{allocTypeName}}>();
                            _cdeclBuf = NativeMemory.Alloc((nuint)returnMetadata.Size);
                            var resultPtr = (IntPtr)_cdeclBuf;
                            """;
                    }
                }

                // Non-frozen structs and complex enums: NewFromPayload stores the buffer
                // pointer directly in SwiftSafeHandle (ownership transfer). Don't free here —
                // SwiftSafeHandle.ReleaseHandle() frees the buffer when disposed.
                // All other types (Utf8Slice, closures, frozen structs, collections) copy the
                // data out, so the temp buffer must be freed.
                // Decomposed Optional: conditionally freed — the return emitter sets _cdeclBuf = null
                // when NewFromPayload takes ownership (Some case). Use conditional free.
                string? cleanupCode;
                if (_env.MethodDecl.UsesCdeclPropertyWrapper &&
                    !_env.MethodDecl.IsSubscriptAccessor &&
                    OptionalMarshalClassifier.IsDecomposed(returnArg.SwiftTypeSpec, _env.TypeDatabase))
                {
                    cleanupCode = "if (_cdeclBuf != null) NativeMemory.Free(_cdeclBuf);";
                }
                else
                {
                    cleanupCode = "NativeMemory.Free(_cdeclBuf);";
                }
                if (returnArg.SwiftTypeSpec is NamedTypeSpec returnNts && returnNts.HasModule())
                {
                    var returnTypeName = SwiftTypeName.FromTypeSpec(returnNts);
                    if (_env.TypeDatabase.TryGetTypeRecord(returnTypeName, out var returnTypeRecord))
                    {
                        bool isNonFrozenStruct = returnTypeRecord.Kind == TypeRecordKind.Struct &&
                            !MarshallingHelpers.IsTypeFrozen(returnTypeRecord);
                        bool isComplexEnum = returnTypeRecord.Kind == TypeRecordKind.Enum &&
                            !returnTypeRecord.Flags.HasFlag(TypeRecordFlags.SimpleEnum);
                        if (isNonFrozenStruct || isComplexEnum)
                            cleanupCode = null;
                    }
                }
                // Bare generic parameter return (e.g. T, U, τ_0_0): the concrete type is not known
                // at emit time. MarshalFromSwift<T> transfers ownership to SwiftSafeHandle for
                // ISwiftStruct types (non-frozen structs, frozen-with-refs), but copies out for
                // primitives/frozen structs/tuples. Emit a runtime check so cleanup only runs
                // when ownership was NOT transferred.
                if (cleanupCode != null
                    && returnArg.SwiftTypeSpec is NamedTypeSpec gpNts
                    && TypeSpecHelpers.IsGenericTypeParameter(gpNts.Name)
                    && _genericContext.TryResolve(gpNts.Name, out var csGenericParamName))
                {
                    cleanupCode = $"if (!typeof(Swift.Runtime.ISwiftStruct).IsAssignableFrom(typeof({csGenericParamName}))) NativeMemory.Free(_cdeclBuf);";
                }

                return new IndirectResultSetup
                {
                    IsConstructor = false,
                    ReturnTypeName = allocTypeName,
                    AllocationCode = allocCode,
                    CleanupCode = cleanupCode
                };
            }

            // Multi-element generic tuple non-cdecl: Swift's ABI splits each address-only element
            // into its own @out register (x0, x1, …). Allocate one buffer per element, expose them
            // as IntPtr locals matching the P/Invoke parameter names (tupleResult{i}Ptr), and free
            // each in the finally block. The first buffer reuses _cdeclBuf (declared by
            // EmitCdeclPayloadDeclaration); subsequent buffers are added to DeclarationLines.
            if (MarshallingHelpers.IsMultiElementGenericTupleIndirectReturn(_env))
            {
                var tupleSpec = (TupleTypeSpec)_env.MethodDecl.CSSignature.First().SwiftTypeSpec;
                var elementCsTypes = new List<string>(tupleSpec.Elements.Count);
                foreach (var element in tupleSpec.Elements)
                {
                    elementCsTypes.Add(GetGenericTupleElementCSharpType(element));
                }

                var allocBuilder = new System.Text.StringBuilder();
                var cleanupBuilder = new System.Text.StringBuilder();
                for (int i = 0; i < elementCsTypes.Count; i++)
                {
                    var elemCsType = elementCsTypes[i];
                    var bufferVar = i == 0 ? "_cdeclBuf" : $"_tupleResult{i}Buf";
                    var ptrVar = $"tupleResult{i}Ptr";
                    var metaVar = $"_tupleResult{i}Meta";

                    if (allocBuilder.Length > 0) allocBuilder.AppendLine();
                    allocBuilder.AppendLine($"var {metaVar} = TypeMetadata.GetTypeMetadataOrThrow<{elemCsType}>();");
                    allocBuilder.AppendLine($"{bufferVar} = NativeMemory.Alloc((nuint){metaVar}.Size);");
                    allocBuilder.Append($"var {ptrVar} = (IntPtr){bufferVar};");

                    if (cleanupBuilder.Length > 0) cleanupBuilder.AppendLine();
                    cleanupBuilder.Append($"NativeMemory.Free({bufferVar});");
                }

                return new IndirectResultSetup
                {
                    IsConstructor = false,
                    ReturnTypeName = _wrapperSignature.ReturnType,
                    AllocationCode = allocBuilder.ToString(),
                    CleanupCode = cleanupBuilder.ToString()
                };
            }

            // Non-cdecl SwiftIndirectResult path: same ownership-transfer logic as @_cdecl.
            // _cdeclBuf is declared before the try block (EmitCdeclPayloadDeclaration) so
            // the finally block can free it. Non-frozen structs and complex enums transfer
            // ownership to SwiftSafeHandle — don't free for those.
            string? swiftIndirectCleanup = "NativeMemory.Free(_cdeclBuf);";
            var returnArg2 = _env.MethodDecl.CSSignature.First();

            // Optional<any P> direct CallConvSwift sret: the Swift function writes the
            // address-only Optional<existential> into the sret buffer using its full ABI shape
            // (≥41 bytes, alignment 8). The wrapper-signature return type for the C# wrapper is
            // a nullable projection (e.g. `Foo.AnyError?` → Nullable<AnyError>) which has no
            // Swift type-metadata producer — sizing the buffer through it throws
            // "Unable to get type metadata for type Nullable`1" at the first call.
            //
            // Size from SwiftOptional<ExistentialContainerN> instead. ExistentialContainerN
            // implements IExistentialContainer (metadata via swift_getExistentialTypeMetadata),
            // so SwiftOptional<…> resolves through its ISwiftObject metadata accessor and
            // produces the correct stride. This is the same shape the unmarshal path reads
            // via MarshalFromSwiftObject<SwiftOptional<…>>, keeping PInvoke signature,
            // buffer allocation, and result extraction aligned on a single Swift ABI
            // source-of-truth.
            if (_env.ExistentialHandler.IsOptionalExistential(returnArg2.SwiftTypeSpec))
            {
                var innerProtocolList = _env.ExistentialHandler.UnwrapOptionalExistential(returnArg2.SwiftTypeSpec);
                if (innerProtocolList != null && _env.ExistentialHandler.IsSupportedExistential(innerProtocolList))
                {
                    var containerType = _env.ExistentialHandler.GetPInvokeExistentialType(innerProtocolList);
                    return new IndirectResultSetup
                    {
                        IsConstructor = false,
                        ReturnTypeName = _wrapperSignature.ReturnType,
                        AllocationCode = $$"""
                            var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<SwiftOptional<{{containerType}}>>();
                            _cdeclBuf = NativeMemory.AllocZeroed((nuint)returnMetadata.Size);
                            var swiftIndirectResult = new SwiftIndirectResult(_cdeclBuf);
                            """,
                        CleanupCode = swiftIndirectCleanup
                    };
                }
            }

            if (returnArg2.SwiftTypeSpec is NamedTypeSpec returnNts2 && returnNts2.HasModule())
            {
                var returnTypeName2 = SwiftTypeName.FromTypeSpec(returnNts2);
                if (_env.TypeDatabase.TryGetTypeRecord(returnTypeName2, out var returnTypeRecord2))
                {
                    bool isNonFrozenStruct2 = returnTypeRecord2.Kind == TypeRecordKind.Struct &&
                        !MarshallingHelpers.IsTypeFrozen(returnTypeRecord2);
                    bool isComplexEnum2 = returnTypeRecord2.Kind == TypeRecordKind.Enum &&
                        !returnTypeRecord2.Flags.HasFlag(TypeRecordFlags.SimpleEnum);
                    if (isNonFrozenStruct2 || isComplexEnum2)
                        swiftIndirectCleanup = null;
                }
            }
            // Bare generic parameter return (same runtime check as @_cdecl path above).
            if (swiftIndirectCleanup != null
                && returnArg2.SwiftTypeSpec is NamedTypeSpec gpNts2
                && TypeSpecHelpers.IsGenericTypeParameter(gpNts2.Name)
                && _genericContext.TryResolve(gpNts2.Name, out var csGenericParamName2))
            {
                swiftIndirectCleanup = $"if (!typeof(Swift.Runtime.ISwiftStruct).IsAssignableFrom(typeof({csGenericParamName2}))) NativeMemory.Free(_cdeclBuf);";
            }

            return new IndirectResultSetup
            {
                IsConstructor = false,
                ReturnTypeName = _wrapperSignature.ReturnType,
                AllocationCode = $$"""
                    var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<{{_wrapperSignature.ReturnType}}>();
                    _cdeclBuf = NativeMemory.Alloc((nuint)returnMetadata.Size);
                    var swiftIndirectResult = new SwiftIndirectResult(_cdeclBuf);
                    """,
                CleanupCode = swiftIndirectCleanup
            };
        }
    }

    /// <summary>
    /// Translates a tuple element TypeSpec to a C# type name for use inside
    /// <c>TypeMetadata.GetTypeMetadataOrThrow&lt;T&gt;()</c> and
    /// <c>SwiftMarshal.MarshalFromSwift&lt;T&gt;()</c> within a generic-tuple wrapper body.
    /// Bare generic parameters resolve via the active <see cref="GenericContext"/>;
    /// concrete and bound-generic types fall back to the type-database resolver
    /// shared with the @_cdecl tuple metadata path.
    /// </summary>
    private string GetGenericTupleElementCSharpType(TypeSpec element)
    {
        if (element is NamedTypeSpec ns &&
            TypeSpecHelpers.IsGenericTypeParameter(ns.Name) &&
            _genericContext.TryResolve(ns.Name, out var csName))
        {
            return csName;
        }
        return GetTupleElementMetadataTypeName(element);
    }

    /// <summary>
    /// Builds the SwiftError setup for throwing methods.
    /// Returns null for non-throwing methods or async methods (which handle errors internally).
    /// Extracts the real error description from Swift via SBW_GetErrorDescription,
    /// releases the error reference via SBW_ReleaseError, and frees the C string via SBW_Free.
    /// For typed throws (C2), also extracts the typed error value via SBW_ExtractTypedError,
    /// with nil-check fallback to message-only exception.
    /// </summary>
    private SwiftErrorSetup? BuildSwiftErrorSetup()
    {
        if (!_requiresSwiftError)
            return null;

        // For typed throws with a resolvable error type, throw SwiftException<TError>
        string? syncTypedErrorType = null;
        string? swiftErrorTypeName = null;
        string? typedErrorSafeSuffix = null;
        // Determine if the typed error type transfers ownership to SafeHandle during MarshalFromSwift.
        // Complex enums and non-frozen structs are projected as C# classes — MarshalFromSwift
        // creates a SafeHandle wrapping the buffer pointer, taking ownership. SBW_Free must NOT
        // be called afterward (double-free → SIGSEGV on GC finalizer thread).
        bool typedErrorTransfersOwnership = false;
        if (_env.MethodDecl.HasTypedThrows &&
            _env.TypeDatabase.TryGetTypeRecord(_env.MethodDecl.ThrownErrorType!, out var syncErrorTypeRecord))
        {
            syncTypedErrorType = syncErrorTypeRecord.CSharpTypeName.FullyQualifiedName;
            swiftErrorTypeName = _env.MethodDecl.ThrownErrorType!.ToString();
            typedErrorSafeSuffix = ErrorDescriptionEmitter.MakeSafeSymbolSuffix(swiftErrorTypeName);

            // MarshalFromSwift<T> takes ownership of the buffer for any type projected as a
            // C# class (SafeHandle wraps the pointer). This includes:
            // - Complex enums (non-SimpleEnum) → EnumSafeHandle
            // - Non-frozen structs → ClassWithOpaquePayload / SafeHandle
            // - Frozen structs with RequiresMemoryManagement → FrozenStructProjectedAsClass / SafeHandle
            // - Swift classes → SwiftClassHandle
            // For these, SBW_Free must NOT be called (double-free when SafeHandle finalizes).
            bool isComplexEnum = syncErrorTypeRecord.Kind == TypeRecordKind.Enum &&
                !syncErrorTypeRecord.Flags.HasFlag(TypeRecordFlags.SimpleEnum);
            bool isNonFrozenStruct = syncErrorTypeRecord.Kind == TypeRecordKind.Struct &&
                !MarshallingHelpers.IsTypeFrozen(syncErrorTypeRecord);
            bool isFrozenStructAsClass = syncErrorTypeRecord.Kind == TypeRecordKind.Struct &&
                MarshallingHelpers.IsFrozenStructProjectedAsClass(syncErrorTypeRecord);
            bool isClass = syncErrorTypeRecord.Kind == TypeRecordKind.Class;
            typedErrorTransfersOwnership = isComplexEnum || isNonFrozenStruct || isFrozenStructAsClass || isClass;
        }

        // When inside a generic type, error helper P/Invokes are in the PInvoke helper class.
        // Prefix all calls with the helper class name.
        var hp = _env.PInvokeHelperContext != null ? $"{_env.PInvokeHelperContext.HelperClassName}." : "";

        // @_cdecl wrappers and native thunks use out IntPtr errorPtr instead of SwiftError swiftError.
        // The error pointer is the same retained AnyObject as SwiftError.Value — all downstream
        // error infrastructure (SBW_GetErrorDescription, SBW_ReleaseError) works identically.
        bool isCdeclConstructor = _env.MethodDecl.UsesCdeclWrapper || _env.MethodDecl.UsesNativeThunk;

        string errorCheckCode;
        if (syncTypedErrorType != null)
        {
            if (isCdeclConstructor)
            {
                // C2: Typed throws via @_cdecl out-pointer
                // For complex enums/non-frozen structs, MarshalFromSwift takes ownership of the buffer
                // (SafeHandle wraps the pointer). Only free on exception to avoid double-free on GC.
                // IMPORTANT: The throw must be OUTSIDE the try-catch so that re-throwing the
                // SwiftException doesn't trigger the catch handler that frees the buffer.
                string typedErrorBlock;
                if (typedErrorTransfersOwnership)
                {
                    typedErrorBlock = $$"""
                            {
                                    {{syncTypedErrorType}} _typedError;
                                    try
                                    {
                                        _typedError = ({{syncTypedErrorType}})SwiftMarshal.MarshalFromSwift<{{syncTypedErrorType}}>(_typedErrorPtr);
                                    }
                                    catch { {{hp}}SBW_Free(_typedErrorPtr); throw; }
                                    throw new SwiftException<{{syncTypedErrorType}}>(_typedError, _errorMessage);
                                }
                    """;
                }
                else
                {
                    typedErrorBlock = $$"""
                            {
                                    try
                                    {
                                        var _typedError = ({{syncTypedErrorType}})SwiftMarshal.MarshalFromSwift<{{syncTypedErrorType}}>(_typedErrorPtr);
                                        throw new SwiftException<{{syncTypedErrorType}}>(_typedError, _errorMessage);
                                    }
                                    finally { {{hp}}SBW_Free(_typedErrorPtr); }
                                }
                    """;
                }
                errorCheckCode = $$"""
                    if (errorPtr != IntPtr.Zero)
                    {
                        string _errorMessage;
                        try
                        {
                            _errorMessage = SwiftMarshal.ReadErrorDescription({{hp}}SBW_GetErrorDescription(errorPtr));
                            var _typedErrorPtr = {{hp}}SBW_ExtractTypedError_{{typedErrorSafeSuffix}}(errorPtr);
                            if (_typedErrorPtr != IntPtr.Zero)
                            {{typedErrorBlock}}
                            throw new SwiftException<{{syncTypedErrorType}}>(_errorMessage);
                        }
                        finally
                        {
                            {{hp}}SBW_ReleaseError(errorPtr);
                        }
                    }
                    """;
            }
            else
            {
                // C2: Typed throws — extract error value with nil-check fallback.
                // SBW_ReleaseError is in the outermost finally, guaranteeing exactly-once release.
                // For complex enums/non-frozen structs, MarshalFromSwift takes ownership of the buffer.
                // IMPORTANT: The throw must be OUTSIDE the try-catch so that re-throwing the
                // SwiftException doesn't trigger the catch handler that frees the buffer.
                string typedErrorBlock2;
                if (typedErrorTransfersOwnership)
                {
                    typedErrorBlock2 = $$"""
                            {
                                    {{syncTypedErrorType}} _typedError;
                                    try
                                    {
                                        _typedError = ({{syncTypedErrorType}})SwiftMarshal.MarshalFromSwift<{{syncTypedErrorType}}>(_typedErrorPtr);
                                    }
                                    catch { {{hp}}SBW_Free(_typedErrorPtr); throw; }
                                    throw new SwiftException<{{syncTypedErrorType}}>(_typedError, _errorMessage);
                                }
                    """;
                }
                else
                {
                    typedErrorBlock2 = $$"""
                            {
                                    try
                                    {
                                        var _typedError = ({{syncTypedErrorType}})SwiftMarshal.MarshalFromSwift<{{syncTypedErrorType}}>(_typedErrorPtr);
                                        throw new SwiftException<{{syncTypedErrorType}}>(_typedError, _errorMessage);
                                    }
                                    finally { {{hp}}SBW_Free(_typedErrorPtr); }
                                }
                    """;
                }
                errorCheckCode = $$"""
                    if (swiftError.Value != null)
                    {
                        string _errorMessage;
                        var _errorPtr = (IntPtr)swiftError.Value;
                        try
                        {
                            _errorMessage = SwiftMarshal.ReadErrorDescription({{hp}}SBW_GetErrorDescription(_errorPtr));
                            var _typedErrorPtr = {{hp}}SBW_ExtractTypedError_{{typedErrorSafeSuffix}}(_errorPtr);
                            if (_typedErrorPtr != IntPtr.Zero)
                            {{typedErrorBlock2}}
                            throw new SwiftException<{{syncTypedErrorType}}>(_errorMessage);
                        }
                        finally
                        {
                            {{hp}}SBW_ReleaseError(_errorPtr);
                        }
                    }
                    """;
            }
        }
        else
        {
            if (isCdeclConstructor)
            {
                // Untyped throws via @_cdecl out-pointer
                errorCheckCode = $$"""
                    if (errorPtr != IntPtr.Zero)
                        SwiftMarshal.ThrowSwiftError(errorPtr, {{hp}}SBW_GetErrorDescription(errorPtr), {{hp}}SBW_ReleaseError);
                    """;
            }
            else
            {
                // Untyped throws — extract message via SBW_GetErrorDescription, release via SBW_ReleaseError
                errorCheckCode = $$"""
                    if (swiftError.Value != null)
                    {
                        var _errorPtr = (IntPtr)swiftError.Value;
                        SwiftMarshal.ThrowSwiftError(_errorPtr, {{hp}}SBW_GetErrorDescription(_errorPtr), {{hp}}SBW_ReleaseError);
                    }
                    """;
            }
        }

        return new SwiftErrorSetup
        {
            IsTypedThrows = syncTypedErrorType != null,
            TypedErrorTypeName = syncTypedErrorType,
            SwiftErrorTypeName = swiftErrorTypeName,
            TypedErrorSafeSuffix = typedErrorSafeSuffix,
            ErrorCheckCode = errorCheckCode
        };
    }

    /// <summary>
    /// Builds the P/Invoke call statement.
    /// </summary>
    private string BuildPInvokeCallStatement(string returnLocalName)
    {
        var voidReturn = _env.MethodDecl.CSSignature.First().SwiftTypeSpec.IsEmptyTuple;
        bool hasOptionalReturnBuffer = _env.BoundGenericsHandler.IsLargeOptionalReturn(_env.MethodDecl) &&
            (_env.MethodDecl.HasOptionalPointerWrapper || _env.MethodDecl.UsesWrapperLibrary);
        var returnPrefix = (_requiresIndirectResult || _requiresSwiftAsync || voidReturn || hasOptionalReturnBuffer) ? "" : $"var {returnLocalName} = ";
        var pInvokeName = NameProvider.GetPInvokeName(_env.MethodDecl);
        var callArgs = _pInvokeSignature.CallArgumentsString();

        if (_env.PInvokeHelperContext != null)
        {
            // Protocol extension methods handle their own metadata via HandleGenericMetadata
            // (explicit + implicit for @_silgen_name ABI). Don't append PInvokeHelperContext metadata.
            string metadataArgs;
            if (_env.MethodDecl.IsProtocolExtensionMethod)
            {
                metadataArgs = "";
            }
            else if (_env.MethodDecl.IsConstructor &&
                     _env.ParentDecl is TypeDecl { IsGeneric: true })
            {
                if (_env.MethodDecl.UsesCdeclWrapper)
                {
                    // @_cdecl constructor wrappers: metadata is already in the P/Invoke signature
                    // via HandleGenericMetadata(). The @_cdecl wrapper receives _metadata0 as a
                    // regular parameter and handles metatype dispatch internally. No extra
                    // trailing metatype argument needed.
                    metadataArgs = "";
                }
                else
                {
                    // Generic type allocating inits: the last metadata argument must be the
                    // specialized type metatype (e.g., GenericClass<T>.self, Wrapper<T>.self),
                    // not per-param T metadata.
                    // Swift allocating init ABI: (init_params, T_metadata..., Type<T>.Type metatype).
                    // Use the helper class metadata accessor to get the specialized metatype
                    // (avoids SwiftObjectHelper<Wrapper<T>> which crashes Mono's generic sharing).
                    //
                    // The metadata accessor PInvoke now also takes PWT args for any
                    // protocol-constrained generic params — see
                    // src/docs/constrained-generic-metadata-witness-tables.md. Use the
                    // type-metadata-accessor argument list (NOT the bare metadata list)
                    // so the call site stays in sync with the P/Invoke declaration.
                    var perParamMetadata = string.Join(", ", _env.PInvokeHelperContext.GetTypeMetadataAccessorArgumentList());
                    metadataArgs = $"{_env.PInvokeHelperContext.HelperClassName}.PInvoke_getMetadata(TypeMetadataRequest.Complete, {perParamMetadata}).Handle";
                }
            }
            else if (_env.MethodDecl.GenericParameters.Count > 0)
            {
                // Non-constructor methods with generic params: HandleGenericMetadata already
                // added inline TypeMetadata to the P/Invoke signature. Skip trailing metadata
                // from PInvokeHelperContext to avoid duplicate TypeMetadata params.
                metadataArgs = "";

                // Swift's calling convention for non-constructor methods on a generic struct
                // expects the PARENT type's metadata in the first metadata slot, NOT the
                // individual generic param's metadata. Substitute the first parent-generic-param
                // metadata.Handle expression with _parentMetatype.Handle. Subsequent parent-generic
                // metadata args (for parents with multiple generic params, e.g., BufferModeQuad)
                // remain in the call list but are ignored by Swift since only x0 is read.
                if (RequiresParentMetadataInjection() && _env.ParentDecl is TypeDecl parentTypeDeclForCall)
                {
                    var firstParentSwiftParam = parentTypeDeclForCall.GenericParameters[0].TypeName;
                    if (_env.GenericTypeMapping.TryGetValue(firstParentSwiftParam, out var mapping))
                    {
                        var firstParentCsParam = mapping.TypeParameter;
                        var oldArg = $"{NameProvider.GetMetadataName(firstParentCsParam)}.Handle";
                        var idx = callArgs.IndexOf(oldArg, StringComparison.Ordinal);
                        if (idx >= 0)
                        {
                            callArgs = callArgs.Substring(0, idx) + "_parentMetatype.Handle" + callArgs.Substring(idx + oldArg.Length);
                        }
                    }
                }
            }
            else
            {
                metadataArgs = string.Join(", ", _env.PInvokeHelperContext.GetMetadataArgumentList());
            }
            var fullArgs = string.IsNullOrEmpty(callArgs)
                ? metadataArgs
                : (string.IsNullOrEmpty(metadataArgs) ? callArgs : $"{callArgs}, {metadataArgs}");
            return $"{returnPrefix}{_env.PInvokeHelperContext.HelperClassName}.{pInvokeName}({fullArgs});";
        }

        return $"{returnPrefix}{pInvokeName}({callArgs});";
    }

    /// <summary>
    /// Builds the fixed block header for frozen struct pointer pinning.
    /// Returns null when no fixed block is needed.
    /// </summary>
    private string? BuildFixedBlockHeader()
    {
        if (!_requiresFixedBlock) return null;
        var resolvedName = GetResolvedTypeName();
        return $"fixed ({resolvedName}* __self = &this)";
    }

    /// <summary>
    /// Computes whether the method body requires an unsafe block.
    /// Constructor: always true. Method: multi-condition check.
    /// </summary>
    private bool ComputeRequiresUnsafe()
    {
        // Constructors always need unsafe (NativeMemory.Alloc, pointer ops)
        if (_env.MethodDecl.IsConstructor)
            return true;

        // Method-own generic params (excluding type-level ones)
        int methodOwnParamCount = 0;
        if (_env.MethodDecl.IsGeneric && !_env.MethodDecl.IsAccessor)
        {
            if (_env.ParentDecl is TypeDecl typeDecl && typeDecl.IsGeneric)
            {
                var typeParamNames = new HashSet<string>(typeDecl.GenericParameters.Select(p => p.TypeName));
                methodOwnParamCount = _env.MethodDecl.GenericParameters
                    .Count(p => !typeParamNames.Contains(p.TypeName));
            }
            else
            {
                methodOwnParamCount = _env.MethodDecl.GenericParameters.Count;
            }
        }

        bool containsBoundGenerics = _env.MethodDecl.CSSignature.Any(_env.BoundGenericsHandler.IsBoundGeneric);

        bool hasClosureParams = _env.MethodDecl.CSSignature.Skip(1).Any(arg =>
        {
            if (!_env.ClosureHandler.IsClosure(arg)) return false;
            var closureSpec = _env.ClosureHandler.GetClosureTypeSpec(arg);
            return closureSpec != null && _env.ClosureHandler.IsSupportedClosure(closureSpec);
        });

        var returnArg = _env.MethodDecl.CSSignature.First();
        bool hasClassReturn = !returnArg.SwiftTypeSpec.IsEmptyTuple && !returnArg.IsGeneric &&
            !_env.ExistentialHandler.IsExistential(returnArg.SwiftTypeSpec) &&
            !_env.ExistentialHandler.IsOptionalExistential(returnArg.SwiftTypeSpec) &&
            !_env.TupleHandler.IsTuple(returnArg) &&
            _env.TypeDatabase.TryGetTypeRecord(returnArg.SwiftTypeSpec, out var returnTypeRecord) &&
            returnTypeRecord.Kind == TypeRecordKind.Class && !MarshallingHelpers.IsObjCBridged(returnTypeRecord);

        return _requiresIndirectResult || _requiresSwiftSelf || _requiresSwiftAsync || _requiresSwiftError ||
            methodOwnParamCount > 0 || containsBoundGenerics || hasClosureParams || hasClassReturn;
    }

    /// <summary>
    /// Gets the resolved simple type name for the parent type, accounting for nested type renames.
    /// </summary>
    private string GetResolvedTypeName()
    {
        if (_env.ParentDecl is TypeDecl typeDecl &&
            _env.TypeDatabase.TryGetTypeRecord(typeDecl.SwiftTypeName, out var record))
        {
            var name = record.CSharpTypeName.Name;
            var lastDot = name.LastIndexOf('.');
            return lastDot >= 0 ? name.Substring(lastDot + 1) : name;
        }
        return _env.ParentDecl.Name;
    }

    /// <summary>
    /// Gets the C# type name suitable for TypeMetadata.GetTypeMetadataOrThrow&lt;T&gt;() for a tuple element.
    /// Primitives use their C# names (int, bool, etc. — pre-registered in KnownMetadata).
    /// ISwiftObject types use their C# type names (pre-registered via module initializer).
    /// </summary>
    private string GetTupleElementMetadataTypeName(TypeSpec element)
    {
        if (element is NamedTypeSpec namedType)
        {
            if (namedType.ContainsGenericParameters)
            {
                var baseTypeName = SwiftTypeName.FromModuleQualifiedName(namedType.Name);
                if (_env.TypeDatabase.TryGetTypeRecord(baseTypeName, out var baseRecord))
                {
                    if (baseRecord == TypeDatabaseExtensions.IntPtrType)
                        return baseRecord.CSharpTypeName.FullyQualifiedName;
                    var translatedParams = namedType.GenericParameters
                        .Select(GetTupleElementMetadataTypeName);
                    return $"{baseRecord.CSharpTypeName.FullyQualifiedName}<{string.Join(", ", translatedParams)}>";
                }
            }

            var typeRecord = _env.TypeDatabase.GetTypeRecordOrAnyType(namedType);
            return typeRecord.CSharpTypeName.FullyQualifiedName;
        }

        return TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName;
    }

    /// <summary>
    /// True when this method needs the PARENT type's metadata injected into the first
    /// metadata slot of the raw CallConvSwift call. Swift's calling convention for a
    /// non-constructor method on a generic struct expects the parent metadata pointer
    /// (not individual generic-param metadata). Methods routed through @_cdecl /
    /// native-thunk wrappers handle this internally and don't need injection.
    /// </summary>
    private bool RequiresParentMetadataInjection()
    {
        if (_env.MethodDecl.IsConstructor) return false;
        if (_env.MethodDecl.UsesCdeclWrapper || _env.MethodDecl.UsesCdeclMethodWrapper) return false;
        if (_env.MethodDecl.UsesNativeThunk) return false;
        if (_env.MethodDecl.UsesFreeFunctionWrapper) return false;
        if (_env.MethodDecl.IsProtocolExtensionMethod) return false;
        if (_env.PInvokeHelperContext == null) return false;
        if (_env.ParentDecl is not TypeDecl { IsGeneric: true }) return false;
        if (_env.MethodDecl.GenericParameters.Count == 0) return false;
        return true;
    }
}
