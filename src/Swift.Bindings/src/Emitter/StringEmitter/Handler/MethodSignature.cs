// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.CodeDom.Compiler;

namespace BindingsGeneration
{
    /// <summary>
    /// Represents a parameter.
    /// </summary>
    /// <param name="Type"></param>
    /// <param name="Name"></param>
    public record Parameter(string Type, string Name, string modifier = "")
    {
        public string CallString() => $"{Type} {Name}";

        /// <summary>
        /// Returns the parameter string for P/Invoke declarations.
        /// Existential types use the container type (not the public interface type).
        /// </summary>
        public string PInvokeSignatureString() => Type switch
        {
            // Existential types: use container type in P/Invoke declaration
            // Format: "Existential:{containerType}:{publicType}"
            var t when t.StartsWith("Existential:") => $"{modifier} {t.Split(':')[1]} {Name}",
            // All other types delegate to SignatureString
            _ => SignatureString()
        };

        public string SignatureString() => Type switch
        {
            "AsyncCallback" => $"{modifier} void* {Name}",
            "AsyncErrorCallback" => $"{modifier} void* {Name}",
            "AsyncContext" => $"{modifier} void* {Name}",
            "AsyncTask" => $"{modifier} IntPtr {Name}",
            "IntPtrFromNonFrozen" => $"{modifier} IntPtr {Name}",
            // ObjC bridged types use IntPtr in P/Invoke
            var t when t.StartsWith("ObjCBridged:") => $"{modifier} IntPtr {Name}",
            // Enum values use IntPtr in Swift calling-convention P/Invoke (SafeHandle is non-blittable there).
            "EnumSafeHandle" => $"{modifier} IntPtr {Name}",
            // Simple enums (C# value types) use their underlying integer type in P/Invoke.
            // Format: "SimpleEnum:{underlyingType}:{enumTypeName}"
            var t when t.StartsWith("SimpleEnum:") => $"{modifier} {t.Split(':')[1]} {Name}",
            // Existential protocol types: show public interface type in signature.
            // Format: "Existential:{containerType}:{publicType}"
            var t when t.StartsWith("Existential:") => $"{modifier} {t.Split(':')[2]} {Name}",
            // Native-remapped types: URL uses SafeHandle, Data uses the actual Swift type
            "NativeRemappedSafeHandle" => $"{modifier} SafeHandle {Name}",
            var t when t.StartsWith("NativeRemapped:") => $"{modifier} {t.Substring("NativeRemapped:".Length)} {Name}",
            // Async+throwing closure context pointer
            var t when t.StartsWith("AsyncThrowingContext:") => $"{modifier} IntPtr {Name}",
            // Async+throwing closure start function pointer
            var t when t.StartsWith("AsyncThrowingStartFunc:") =>
                $"{modifier} delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, void> {Name}",
            _ => $"{modifier} {Type} {Name}"
        };
    }

    /// <summary>
    /// Represents a signature.
    /// </summary>
    /// <param name="ReturnType"></param>
    /// <param name="Parameters"></param>
    public record Signature(string ReturnType, IReadOnlyList<Parameter> Parameters)
    {
        public bool ContainsPlaceholder =>
        Parameters.Any(p => p.Type.Contains(TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName))
        || ReturnType.Contains(TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName);
        public string ParametersString() => string.Join(", ", Parameters.Select(p => p.SignatureString()));

        /// <summary>
        /// Returns the parameters string for P/Invoke declarations, where existential types
        /// use container types instead of public interface types.
        /// </summary>
        public string PInvokeParametersString() => string.Join(", ", Parameters.Select(p => p.PInvokeSignatureString()));

        public string CallArgumentsString() => string.Join(", ", Parameters.Select(p => GetCallArgumentString(p)));

        public static string GetCallArgumentString(Parameter parameter)
        {
            return parameter switch
            {
                { Type: "SafeHandle" } => $"{parameter.Name}.Payload",
                { Type: "EnumSafeHandle" } => $"{parameter.Name}.Payload.DangerousGetHandle()",
                // Simple enums: cast to underlying integer type for P/Invoke
                { Type: var type } when type.StartsWith("SimpleEnum:") => $"({type.Split(':')[1]}){parameter.Name}",
                // Existential protocol types: extract container from interface
                // Format: "Existential:{containerType}:{publicType}"
                { Type: var type } when type.StartsWith("Existential:") =>
                    $"((Swift.Runtime.ISwiftExistentialConvertible<{type.Split(':')[1]}>){parameter.Name}).GetExistentialContainer()",
                { Type: "IntPtrFromNonFrozen" } => $"{parameter.Name}Handle",
                // Handle .Buffer params: ref modifier uses BufferRef (ref-returning property) for in-place mutation
                { Type: var type, modifier: "ref" } when type.EndsWith(".Buffer") => $"ref {parameter.Name}Disposable.BufferRef",
                { Type: var type } when type.EndsWith(".Buffer") => $"{parameter.Name}Disposable.Buffer",
                { Type: "AsyncCallback" } => $"{parameter.Name}",
                { Type: "AsyncErrorCallback" } => $"{parameter.Name}",
                { Type: "AsyncContext" } => "null",
                { Type: "AsyncTask" } => $"GCHandle.ToIntPtr({parameter.Name})",
                { modifier: "out" } => $"out var {parameter.Name}",
                { modifier: "ref" } => $"ref {parameter.Name}",
                // Handle escaping closures: parameter is SwiftClosureData, variable is {name}Closure
                { Type: "SwiftClosureData" } => $"{parameter.Name}Closure",
                // Handle async+throwing closure context: AsyncThrowingContext:{paramName} -> {paramName}ContextPtr
                { Type: var type } when type.StartsWith("AsyncThrowingContext:") =>
                    $"{type.Substring("AsyncThrowingContext:".Length)}ContextPtr",
                // Handle async+throwing closure start function: AsyncThrowingStartFunc:{callbackName} -> s_{callbackName}_Start
                // NOTE: We pass the function pointer directly (not cast to IntPtr) since P/Invoke expects the delegate* type
                { Type: var type } when type.StartsWith("AsyncThrowingStartFunc:") =>
                    $"s_{type.Substring("AsyncThrowingStartFunc:".Length)}_Start",
                // Handle @convention(c) closure function pointers
                { Type: var type } when type.StartsWith("delegate* unmanaged") =>
                    parameter.Name.EndsWith("FuncPtr") ? parameter.Name : $"{parameter.Name}FuncPtr",
                // ObjC bridged types: extract Handle from the .NET iOS binding object
                { Type: var type } when type.StartsWith("ObjCBridged:") => $"{parameter.Name}Handle",
                // Native-remapped types (URL, Data): use the converted Swift variable
                { Type: "NativeRemappedSafeHandle" } => $"{parameter.Name}Swift.Payload",
                { Type: var type } when type.StartsWith("NativeRemapped:") => $"{parameter.Name}Swift",
                // Async instance methods pass self as explicit IntPtr (not SwiftSelf register)
                // For classes, dereference the payload buffer to get the actual class pointer
                { Name: "_selfClass" } => "*(IntPtr*)_payload.DangerousGetHandle()",
                { Name: "_self", Type: "IntPtr" } => "_payload.DangerousGetHandle()",
                _ => parameter.Name
            };
        }
    }

    /// <summary>
    /// Base class for signature builders that provides common fields and methods.
    /// </summary>
    public abstract class SignatureBuilderBase
    {
        /// <summary>The return type of the method.</summary>
        protected string _returnType = "invalid";

        /// <summary>The list of parameters.</summary>
        protected readonly List<Parameter> _parameters = new();

        /// <summary>The method environment.</summary>
        protected readonly MethodEnvironment _env;

        /// <summary>Merged generic context for resolving type-level + method-level generic parameters.</summary>
        protected readonly GenericContext _genericContext;

        /// <summary>
        /// Initializes a new instance of the <see cref="SignatureBuilderBase"/> class.
        /// </summary>
        /// <param name="env">The method environment.</param>
        protected SignatureBuilderBase(MethodEnvironment env)
        {
            _env = env;
            _genericContext = env.ParentDecl is TypeDecl parentType
                ? GenericContext.FromMethodInType(env.MethodDecl, parentType)
                : GenericContext.FromMethod(env.MethodDecl);
        }

        /// <summary>
        /// Builds the signature.
        /// </summary>
        /// <returns>The signature.</returns>
        public Signature Build()
        {
            return new Signature(_returnType, _parameters.ToArray());
        }

        /// <summary>
        /// Sets the return type of the method.
        /// </summary>
        /// <param name="returnType">The return type.</param>
        protected void SetReturnType(string returnType)
        {
            _returnType = returnType;
        }

        /// <summary>
        /// Adds a parameter to the signature.
        /// </summary>
        /// <param name="type">The parameter type.</param>
        /// <param name="name">The parameter name.</param>
        /// <param name="modifier">Optional parameter modifier (e.g., "out").</param>
        protected void AddParameter(string type, string name, string modifier = "")
        {
            _parameters.Add(new Parameter(type, name, modifier));
        }
    }

    /// <summary>
    /// Builds the wrapper method signature (C# public API).
    /// </summary>
    public class WrapperSignatureBuilder : SignatureBuilderBase
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="WrapperSignatureBuilder"/> class.
        /// </summary>
        /// <param name="env">The method environment.</param>
        public WrapperSignatureBuilder(MethodEnvironment env) : base(env)
        {
        }

        /// <summary>
        /// Handles the return type of the method.
        /// </summary>
        public void HandleReturnType()
        {
            var argument = _env.MethodDecl.CSSignature.First();

            // Check for automatic .NET type conversion (SwiftString -> string, SwiftArray -> IReadOnlyList, etc.)
            if (!_env.MethodDecl.IsAccessor)
            {
                var idiomaticType = _env.TypeConversionHandler.GetIdiomaticCSharpType(
                    argument.SwiftTypeSpec,
                    isParameter: false,
                    typeSpec => TranslateTypeSpecForConversion(typeSpec));
                if (idiomaticType != null)
                {
                    SetReturnType(idiomaticType);
                    return;
                }
            }

            if (_env.BoundGenericsHandler.IsBoundGeneric(argument))
            {
                var csTypeParam = _env.BoundGenericsHandler.TranslateBoundGenericTypeToCSharp(argument, _genericContext);
                SetReturnType(csTypeParam);
                return;
            }

            // Handle closure return types (including optional closures)
            if (_env.ClosureHandler.IsClosure(argument))
            {
                var closureTypeSpec = _env.ClosureHandler.GetClosureTypeSpec(argument)!;
                if (_env.ClosureHandler.IsSupportedClosure(closureTypeSpec))
                {
                    bool isOptional = _env.ClosureHandler.IsOptionalClosure(argument.SwiftTypeSpec);
                    var delegateType = isOptional
                        ? _env.ClosureHandler.GetCSharpOptionalDelegateType(argument.SwiftTypeSpec)
                        : _env.ClosureHandler.GetCSharpDelegateType(closureTypeSpec);
                    SetReturnType(delegateType);
                }
                else
                {
                    SetReturnType(TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName);
                }
                return;
            }

            // Handle tuple return types
            if (_env.TupleHandler.IsTuple(argument.SwiftTypeSpec))
            {
                var tupleTypeSpec = (TupleTypeSpec)argument.SwiftTypeSpec;
                bool hasGenericElements = _env.TupleHandler.HasGenericTypeParameterElements(tupleTypeSpec);
                // Generic-element tuples (e.g., (T, U)) are accepted when a generic context can resolve them
                // and the method is not async (async methods skip indirect result, which generic tuples need).
                if (_env.TupleHandler.IsSupportedTuple(tupleTypeSpec) ||
                    (_env.TupleHandler.IsSupportedTuple(tupleTypeSpec, _genericContext) && !(hasGenericElements && _env.MethodDecl.IsAsync)))
                    SetReturnType(_env.TupleHandler.GetCSharpTupleType(tupleTypeSpec, _genericContext));
                else
                    SetReturnType(TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName);
                return;
            }

            if (argument.IsGeneric)
            {
                var csTypeParamName = _env.GenericTypeMapping[argument.SwiftTypeSpec.ToString()].TypeParameter;
                SetReturnType(csTypeParamName);
                return;
            }

            // Handle existential return types (any Protocol)
            if (_env.ExistentialHandler.IsExistential(argument.SwiftTypeSpec))
            {
                var protocolList = _env.ExistentialHandler.ToProtocolListTypeSpec(argument.SwiftTypeSpec)!;
                if (_env.ExistentialHandler.IsSupportedExistential(protocolList))
                {
                    var publicType = _env.ExistentialHandler.GetPublicExistentialType(protocolList);
                    SetReturnType(publicType);
                }
                else
                {
                    SetReturnType(TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName);
                }
                return;
            }

            // Handle Optional-wrapped existential return types like (any DataCaching)?
            if (_env.ExistentialHandler.IsOptionalExistential(argument.SwiftTypeSpec))
            {
                var innerProtocolList = _env.ExistentialHandler.UnwrapOptionalExistential(argument.SwiftTypeSpec)!;
                if (_env.ExistentialHandler.IsSupportedExistential(innerProtocolList))
                {
                    var publicOptionalType = _env.ExistentialHandler.GetPublicOptionalExistentialType(innerProtocolList);
                    SetReturnType(publicOptionalType);
                }
                else
                {
                    SetReturnType(TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName);
                }
                return;
            }

            // Check for native type remapping (URL → NSUrl, Data → NSData)
            if (!_env.MethodDecl.IsAccessor && _env.TypeConversionHandler.HasNativeTypeRemapping(argument.SwiftTypeSpec))
            {
                var nativeType = _env.TypeConversionHandler.GetNativeTypeName(argument.SwiftTypeSpec);
                if (nativeType != null)
                {
                    SetReturnType(nativeType);
                    return;
                }
            }

            var typeRecord = _env.TypeDatabase.GetTypeRecordOrAnyType(argument.SwiftTypeSpec);
            // Protocol types (interfaces) are not supported as return types because they don't have Payload property
            if (typeRecord.Kind == TypeRecordKind.Protocol)
            {
                SetReturnType(TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName);
                return;
            }
            SetReturnType(typeRecord.CSharpTypeName.FullyQualifiedName);
        }

        /// <summary>
        /// Handles the arguments of the method.
        /// Uses GetCSharpParameterName() for public API parameter names.
        /// </summary>
        public void HandleArguments()
        {
            foreach (var argument in _env.MethodDecl.CSSignature.Skip(1))
            {
                var csParamName = NameProvider.GetCSharpParameterName(argument);

                // Check for automatic .NET type conversion (SwiftString -> string, SwiftArray -> IEnumerable, etc.)
                if (!_env.MethodDecl.IsAccessor)
                {
                    var idiomaticType = _env.TypeConversionHandler.GetIdiomaticCSharpType(
                        argument.SwiftTypeSpec,
                        isParameter: true,
                        typeSpec => TranslateTypeSpecForConversion(typeSpec));
                    if (idiomaticType != null)
                    {
                        AddParameter(idiomaticType, csParamName);
                        continue;
                    }
                }

                if (_env.BoundGenericsHandler.IsBoundGeneric(argument))
                {
                    var csTypeParam = _env.BoundGenericsHandler.TranslateBoundGenericTypeToCSharp(argument, _genericContext);
                    AddParameter(csTypeParam, csParamName);
                    continue;
                }

                // Handle closure arguments (including optional closures)
                if (_env.ClosureHandler.IsClosure(argument))
                {
                    var closureTypeSpec = _env.ClosureHandler.GetClosureTypeSpec(argument)!;
                    if (_env.ClosureHandler.IsSupportedClosure(closureTypeSpec))
                    {
                        bool isOptional = _env.ClosureHandler.IsOptionalClosure(argument.SwiftTypeSpec);
                        var delegateType = isOptional
                            ? _env.ClosureHandler.GetCSharpOptionalDelegateType(argument.SwiftTypeSpec)
                            : _env.ClosureHandler.GetCSharpDelegateType(closureTypeSpec);
                        AddParameter(delegateType, csParamName);
                    }
                    else
                    {
                        // Unsupported closure - use placeholder that will cause method to be skipped
                        AddParameter(TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName, csParamName);
                    }
                    continue;
                }

                // Handle tuple arguments
                if (_env.TupleHandler.IsTuple(argument))
                {
                    var tupleTypeSpec = _env.TupleHandler.GetTupleTypeSpec(argument)!;
                    if (_env.TupleHandler.IsSupportedTuple(tupleTypeSpec) ||
                        _env.TupleHandler.IsSupportedTuple(tupleTypeSpec, _genericContext))
                        AddParameter(_env.TupleHandler.GetCSharpTupleType(tupleTypeSpec, _genericContext), csParamName);
                    else
                        AddParameter(TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName, csParamName);
                    continue;
                }

                // Handle existential arguments (any Protocol)
                if (_env.ExistentialHandler.IsExistential(argument.SwiftTypeSpec))
                {
                    var protocolList = _env.ExistentialHandler.ToProtocolListTypeSpec(argument.SwiftTypeSpec)!;
                    if (_env.ExistentialHandler.IsSupportedExistential(protocolList))
                    {
                        var publicType = _env.ExistentialHandler.GetPublicExistentialType(protocolList);
                        var containerType = _env.ExistentialHandler.GetCSharpExistentialType(protocolList);
                        // Use Existential: prefix so P/Invoke call extracts container from interface
                        AddParameter($"Existential:{containerType}:{publicType}", csParamName);
                    }
                    else
                    {
                        AddParameter(TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName, csParamName);
                    }
                    continue;
                }

                // Handle Optional-wrapped existential arguments like (any DataCaching)?
                if (_env.ExistentialHandler.IsOptionalExistential(argument.SwiftTypeSpec))
                {
                    var innerProtocolList = _env.ExistentialHandler.UnwrapOptionalExistential(argument.SwiftTypeSpec)!;
                    if (_env.ExistentialHandler.IsSupportedExistential(innerProtocolList))
                    {
                        var publicOptionalType = _env.ExistentialHandler.GetPublicOptionalExistentialType(innerProtocolList);
                        AddParameter(publicOptionalType, csParamName);
                    }
                    else
                    {
                        AddParameter(TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName, csParamName);
                    }
                    continue;
                }

                // Determine ref modifier for inout parameters
                var inoutModifier = argument.IsInOut ? "ref" : "";

                if (argument.IsGeneric)
                {
                    var csTypeParamName = _env.GenericTypeMapping[argument.SwiftTypeSpec.ToString()].TypeParameter;
                    AddParameter(csTypeParamName, csParamName, inoutModifier);
                }
                else
                {
                    // Check for native type remapping (URL → NSUrl, Data → NSData)
                    // Skip for property accessors to maintain property/accessor type consistency
                    if (!_env.MethodDecl.IsAccessor && _env.TypeConversionHandler.HasNativeTypeRemapping(argument.SwiftTypeSpec))
                    {
                        var nativeType = _env.TypeConversionHandler.GetNativeTypeName(argument.SwiftTypeSpec);
                        if (nativeType != null)
                        {
                            AddParameter(nativeType, csParamName, inoutModifier);
                            continue;
                        }
                    }

                    var typeRecord = _env.TypeDatabase.GetTypeRecordOrAnyType(argument.SwiftTypeSpec);
                    // Protocol types (interfaces) are not supported as parameters because they don't have Payload property
                    if (typeRecord.Kind == TypeRecordKind.Protocol)
                    {
                        AddParameter(TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName, csParamName);
                        continue;
                    }
                    AddParameter(typeRecord.CSharpTypeName.FullyQualifiedName, csParamName, inoutModifier);
                }
            }
        }

        /// <summary>
        /// Translates a TypeSpec to C# type name for use in type conversion handlers.
        /// Handles generic types by translating their type parameters.
        /// </summary>
        private string TranslateTypeSpecForConversion(TypeSpec typeSpec)
        {
            // Handle existential types (ProtocolListTypeSpec and NamedTypeSpec with IsAny)
            // Use public interface type for wrapper signatures (e.g., ISwiftDescribable instead of ExistentialContainer1)
            if (_env.ExistentialHandler.IsExistential(typeSpec))
            {
                var protocolList = _env.ExistentialHandler.ToProtocolListTypeSpec(typeSpec);
                if (protocolList != null && _env.ExistentialHandler.IsSupportedExistential(protocolList))
                    return _env.ExistentialHandler.GetPublicExistentialType(protocolList);
                return TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName;
            }

            if (typeSpec is NamedTypeSpec namedTypeSpec)
            {
                // Check if this is a generic type parameter that can be resolved
                if (TypeSpecHelpers.IsGenericTypeParameter(namedTypeSpec.Name) &&
                    _genericContext.TryResolve(namedTypeSpec.Name, out var csName))
                {
                    return csName;
                }

                var typeRecord = _env.TypeDatabase.GetTypeRecordOrAnyType(namedTypeSpec);

                // If the type falls back to AnyType or is IntPtr (pointer types), don't append generic parameters
                // Pointer types like UnsafeMutablePointer<T> resolve to IntPtr which doesn't support generics
                if (typeRecord == TypeDatabaseExtensions.AnyType ||
                    typeRecord == TypeDatabaseExtensions.IntPtrType)
                {
                    return typeRecord.CSharpTypeName.FullyQualifiedName;
                }

                // Handle generic parameters
                if (namedTypeSpec.GenericParameters.Count > 0)
                {
                    var translatedParams = namedTypeSpec.GenericParameters
                        .Select(p => TranslateTypeSpecForConversion(p))
                        .ToList();
                    return $"{typeRecord.CSharpTypeName.FullyQualifiedName}<{string.Join(", ", translatedParams)}>";
                }

                return typeRecord.CSharpTypeName.FullyQualifiedName;
            }
            return TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName;
        }
    }

    /// <summary>
    /// Provides methods for handling method signatures.
    /// </summary>
    public class SignatureHandler
    {
        private Signature? _pInvokeSignature;
        private Signature? _wrapperSignature;
        private readonly MethodEnvironment _env;

        public SignatureHandler(MethodEnvironment env)
        {
            _env = env;
        }

        /// <summary>
        /// Gets the PInvoke signature.
        /// </summary>
        /// <returns>The PInvoke signature.</returns>
        public Signature GetPInvokeSignature()
        {
            if (_pInvokeSignature == null)
            {
                var pInvokeSignature = new PInvokeSignatureBuilder(_env);
                pInvokeSignature.HandleReturnType();
                pInvokeSignature.HandleSwiftAsync();
                pInvokeSignature.HandleArguments();
                pInvokeSignature.HandleGenericMetadata();
                pInvokeSignature.HandleProtocolConformance();
                pInvokeSignature.HandleSwiftSelf();
                pInvokeSignature.HandleSwiftError();
                _pInvokeSignature = pInvokeSignature.Build();
            }
            return _pInvokeSignature;
        }

        /// <summary>
        /// Gets the wrapper method signature.
        /// </summary>
        /// <returns>The wrapper method signature.</returns>
        public Signature GetWrapperSignature()
        {
            if (_wrapperSignature == null)
            {
                var wrapperSignature = new WrapperSignatureBuilder(_env);
                wrapperSignature.HandleReturnType();
                wrapperSignature.HandleArguments();
                _wrapperSignature = wrapperSignature.Build();
            }
            return _wrapperSignature;
        }
    }
}
