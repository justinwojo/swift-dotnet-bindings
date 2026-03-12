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
            PInvokeCallStatement = BuildPInvokeCallStatement(),
            FixedBlockHeader = BuildFixedBlockHeader(),
            RequiresUnsafe = ComputeRequiresUnsafe(),
        };
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
                var protocolName = NameProvider.GetInterfaceName(conformance.ConformanceTarget.Name, moduleName: conformance.ConformanceTarget.Module);
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

        foreach (var argument in _env.MethodDecl.CSSignature.Skip(1).Where(a => a.IsGeneric))
        {
            var csName = NameProvider.GetCSharpParameterName(argument);
            var payloadName = NameProvider.GetPayloadName(csName);
            lines.Add($"IntPtr {payloadName} = IntPtr.Zero;");
        }

        // Declare GCHandle variables for escaping closures (except async+throwing which handle their own)
        foreach (var argument in _env.MethodDecl.CSSignature.Skip(1).Where(_env.ClosureHandler.IsClosure))
        {
            var closureTypeSpec = _env.ClosureHandler.GetClosureTypeSpec(argument)!;
            if (_env.ClosureHandler.IsSupportedClosure(closureTypeSpec) &&
                _env.ClosureHandler.RequiresThunk(closureTypeSpec) &&
                !_env.ClosureHandler.IsAsyncThrowingClosure(closureTypeSpec))
            {
                var csName = NameProvider.GetCSharpParameterName(argument);
                lines.Add($"GCHandle {csName}Handle = default;");
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

        // Async methods, standalone closure Cdecl wrappers, and @_cdecl method wrappers
        // pass self as explicit IntPtr parameter — no SwiftSelf needed.
        if (_requiresSwiftAsync || _env.MethodDecl.UsesFreeFunctionWrapper || _env.MethodDecl.UsesCdeclMethodWrapper)
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
                CreationCode = "var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());"
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
            // Include generic type parameters in SwiftSafeHandle<> for generic types
            var typeName = GetResolvedTypeName();
            if (_env.ParentDecl is TypeDecl typeDecl && typeDecl.IsGeneric)
            {
                var genericParams = string.Join(", ", typeDecl.GenericParameters.Select(p =>
                    _env.GenericTypeMapping.TryGetValue(p.TypeName, out var mapped) ? mapped.TypeParameter : p.TypeName));
                typeName = $"{typeName}<{genericParams}>";
            }

            // For derived classes, SwiftSafeHandle<T> must use the root base type
            // because _payload is declared as SwiftSafeHandle<RootBase> on the base class.
            var safeHandleTypeName = typeName;
            if (_env.ParentDecl is ClassDecl cd && cd.HasResolvedSuperclass)
            {
                var root = cd;
                while (root.HasResolvedSuperclass)
                    root = root.ResolvedSuperclass!;
                safeHandleTypeName = GenericTypeEmitter.GetTypeNameWithGenerics(root);
            }

            if (_env.MethodDecl.UsesCdeclConstructorWrapper)
            {
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
                // payload is declared before try block (EmitCdeclPayloadDeclaration)
                // so it's accessible in finally for NativeMemory.Free cleanup.
                var returnArg = _env.MethodDecl.CSSignature.First();
                var allocTypeName = _wrapperSignature.ReturnType;

                // Closure returns: fixed 2-pointer size (funcPtr + context = SwiftClosureData)
                if (returnArg.SwiftTypeSpec is ClosureTypeSpec)
                {
                    return new IndirectResultSetup
                    {
                        IsConstructor = false,
                        ReturnTypeName = "SwiftClosureData",
                        AllocationCode = """
                            payload = NativeMemory.Alloc((nuint)(nint.Size * 2));
                            var resultPtr = (IntPtr)payload;
                            """,
                        CleanupCode = "NativeMemory.Free(payload);"
                    };
                }

                // Optional<value-type>: projected as C# nullable (double?), but TypeMetadata
                // needs the Swift container type (SwiftOptional<double>) for correct size.
                if (MethodWrapperEmitter.IsOptionalType(returnArg.SwiftTypeSpec) &&
                    !MethodWrapperEmitter.IsOptionalWithReferenceInner(returnArg.SwiftTypeSpec, _env.TypeDatabase))
                {
                    var projection = s_projectionFactory.Project(returnArg.SwiftTypeSpec,
                        new ProjectionContext { TypeDatabase = _env.TypeDatabase, IsParameter = false,
                            GenericContext = _genericContext,
                            ParentTypeDecl = _env.ParentDecl as TypeDecl,
                            CurrentModuleName = _env.ExistentialHandler.CurrentModuleName });
                    if (projection != null)
                        allocTypeName = projection.ContainerTypeName;
                }

                // Utf8Slice is a C# struct (no Swift metadata); use fixed-size allocation.
                // Real Swift types use TypeMetadata for correct size.
                var isUtf8Slice = allocTypeName == "Utf8Slice";
                var allocCode = isUtf8Slice
                    ? """
                        payload = NativeMemory.Alloc((nuint)(nint.Size * 2));
                        var resultPtr = (IntPtr)payload;
                        """
                    : $$"""
                        var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<{{allocTypeName}}>();
                        payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                        var resultPtr = (IntPtr)payload;
                        """;

                return new IndirectResultSetup
                {
                    IsConstructor = false,
                    ReturnTypeName = allocTypeName,
                    AllocationCode = allocCode,
                    CleanupCode = "NativeMemory.Free(payload);"
                };
            }

            return new IndirectResultSetup
            {
                IsConstructor = false,
                ReturnTypeName = _wrapperSignature.ReturnType,
                AllocationCode = $$"""
                    var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<{{_wrapperSignature.ReturnType}}>();
                    var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                    var swiftIndirectResult = new SwiftIndirectResult(payload);
                    """
            };
        }
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
        if (_env.MethodDecl.HasTypedThrows &&
            _env.TypeDatabase.TryGetTypeRecord(_env.MethodDecl.ThrownErrorType!, out var syncErrorTypeRecord))
        {
            syncTypedErrorType = syncErrorTypeRecord.CSharpTypeName.FullyQualifiedName;
            swiftErrorTypeName = _env.MethodDecl.ThrownErrorType!.ToString();
            typedErrorSafeSuffix = ErrorDescriptionEmitter.MakeSafeSymbolSuffix(swiftErrorTypeName);
        }

        // When inside a generic type, error helper P/Invokes are in the PInvoke helper class.
        // Prefix all calls with the helper class name.
        var hp = _env.PInvokeHelperContext != null ? $"{_env.PInvokeHelperContext.HelperClassName}." : "";

        // @_cdecl constructor wrappers use out IntPtr errorPtr instead of SwiftError swiftError.
        // The error pointer is the same retained AnyObject as SwiftError.Value — all downstream
        // error infrastructure (SBW_GetErrorDescription, SBW_ReleaseError) works identically.
        bool isCdeclConstructor = _env.MethodDecl.UsesCdeclWrapper;

        string errorCheckCode;
        if (syncTypedErrorType != null)
        {
            if (isCdeclConstructor)
            {
                // C2: Typed throws via @_cdecl out-pointer
                errorCheckCode = $$"""
                    if (errorPtr != IntPtr.Zero)
                    {
                        string _errorMessage;
                        try
                        {
                            var _descPtr = {{hp}}SBW_GetErrorDescription(errorPtr);
                            try
                            {
                                _errorMessage = _descPtr != IntPtr.Zero
                                    ? global::System.Runtime.InteropServices.Marshal.PtrToStringUTF8(_descPtr) ?? "Unknown Swift error"
                                    : "Unknown Swift error";
                            }
                            finally
                            {
                                if (_descPtr != IntPtr.Zero) {{hp}}SBW_Free(_descPtr);
                            }
                            var _typedErrorPtr = {{hp}}SBW_ExtractTypedError_{{typedErrorSafeSuffix}}(errorPtr);
                            if (_typedErrorPtr != IntPtr.Zero)
                            {
                                try
                                {
                                    var _typedError = ({{syncTypedErrorType}})SwiftMarshal.MarshalFromSwift<{{syncTypedErrorType}}>(_typedErrorPtr);
                                    throw new SwiftException<{{syncTypedErrorType}}>(_typedError, _errorMessage);
                                }
                                finally
                                {
                                    {{hp}}SBW_Free(_typedErrorPtr);
                                }
                            }
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
                errorCheckCode = $$"""
                    if (swiftError.Value != null)
                    {
                        string _errorMessage;
                        var _errorPtr = (IntPtr)swiftError.Value;
                        try
                        {
                            var _descPtr = {{hp}}SBW_GetErrorDescription(_errorPtr);
                            try
                            {
                                _errorMessage = _descPtr != IntPtr.Zero
                                    ? global::System.Runtime.InteropServices.Marshal.PtrToStringUTF8(_descPtr) ?? "Unknown Swift error"
                                    : "Unknown Swift error";
                            }
                            finally
                            {
                                if (_descPtr != IntPtr.Zero) {{hp}}SBW_Free(_descPtr);
                            }
                            var _typedErrorPtr = {{hp}}SBW_ExtractTypedError_{{typedErrorSafeSuffix}}(_errorPtr);
                            if (_typedErrorPtr != IntPtr.Zero)
                            {
                                try
                                {
                                    var _typedError = ({{syncTypedErrorType}})SwiftMarshal.MarshalFromSwift<{{syncTypedErrorType}}>(_typedErrorPtr);
                                    throw new SwiftException<{{syncTypedErrorType}}>(_typedError, _errorMessage);
                                }
                                finally
                                {
                                    {{hp}}SBW_Free(_typedErrorPtr);
                                }
                            }
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
                    {
                        string _errorMessage;
                        var _descPtr = {{hp}}SBW_GetErrorDescription(errorPtr);
                        try
                        {
                            _errorMessage = _descPtr != IntPtr.Zero
                                ? global::System.Runtime.InteropServices.Marshal.PtrToStringUTF8(_descPtr) ?? "Unknown Swift error"
                                : "Unknown Swift error";
                        }
                        finally
                        {
                            if (_descPtr != IntPtr.Zero) {{hp}}SBW_Free(_descPtr);
                            {{hp}}SBW_ReleaseError(errorPtr);
                        }
                        throw new SwiftRuntimeException(_errorMessage);
                    }
                    """;
            }
            else
            {
                // Untyped throws — extract message via SBW_GetErrorDescription, release via SBW_ReleaseError
                errorCheckCode = $$"""
                    if (swiftError.Value != null)
                    {
                        string _errorMessage;
                        var _errorPtr = (IntPtr)swiftError.Value;
                        var _descPtr = {{hp}}SBW_GetErrorDescription(_errorPtr);
                        try
                        {
                            _errorMessage = _descPtr != IntPtr.Zero
                                ? global::System.Runtime.InteropServices.Marshal.PtrToStringUTF8(_descPtr) ?? "Unknown Swift error"
                                : "Unknown Swift error";
                        }
                        finally
                        {
                            if (_descPtr != IntPtr.Zero) {{hp}}SBW_Free(_descPtr);
                            {{hp}}SBW_ReleaseError(_errorPtr);
                        }
                        throw new SwiftRuntimeException(_errorMessage);
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
    private string BuildPInvokeCallStatement()
    {
        var voidReturn = _env.MethodDecl.CSSignature.First().SwiftTypeSpec.IsEmptyTuple;
        bool hasOptionalReturnBuffer = _env.BoundGenericsHandler.IsLargeOptionalReturn(_env.MethodDecl) &&
            (_env.MethodDecl.HasOptionalPointerWrapper || _env.MethodDecl.UsesWrapperLibrary);
        var returnPrefix = (_requiresIndirectResult || _requiresSwiftAsync || voidReturn || hasOptionalReturnBuffer) ? "" : "var result = ";
        var pInvokeName = NameProvider.GetPInvokeName(_env.MethodDecl);
        var callArgs = _pInvokeSignature.CallArgumentsString();

        if (_env.PInvokeHelperContext != null)
        {
            // Protocol extension methods handle their own metadata via HandleGenericMetadata
            // (explicit + implicit for @_silgen_name ABI). Don't append PInvokeHelperContext metadata.
            var metadataArgs = _env.MethodDecl.IsProtocolExtensionMethod
                ? ""
                : string.Join(", ", _env.PInvokeHelperContext.GetMetadataArgumentList());
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
}
