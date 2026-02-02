// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.CodeDom.Compiler;
using Microsoft.Extensions.Logging;

namespace BindingsGeneration
{
    /// <summary>
    /// Wraps a retained Swift class pointer for async operations.
    /// Used to track self pointers that were explicitly retained via Arc.Retain()
    /// before calling async Swift methods. Must be released via Arc.Release() after callback.
    /// </summary>
    internal readonly struct RetainedSelfPtr
    {
        public readonly IntPtr Ptr;
        public RetainedSelfPtr(IntPtr ptr) => Ptr = ptr;
    }

    /// <summary>
    /// Factory class for creating instances of ConstructorHandler.
    /// </summary>
    public class ConstructorHandlerFactory : HandlerFactory, IFactory<BaseDecl, IMethodHandler>
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ConstructorHandlerFactory"/> class.
        /// </summary>
        /// <param name="loggerFactory">The logger factory instance.</param>
        public ConstructorHandlerFactory(ILoggerFactory loggerFactory) : base(loggerFactory.CreateLogger<ConstructorHandler>())
        {
        }

        /// <summary>
        /// Determines if the factory handles the specified declaration.
        /// </summary>
        /// <param name="decl">The base declaration.</param>
        public bool Handles(BaseDecl decl)
        {
            return decl is MethodDecl methodDecl && methodDecl.IsConstructor && decl.ParentDecl is StructDecl;
        }

        /// <summary>
        /// Constructs a new instance of ConstructorHandler.
        /// </summary>
        public IMethodHandler Construct()
        {
            return new ConstructorHandler(_handlerLogger);
        }
    }

    /// <summary>
    /// Handler class for constructor declarations.
    /// </summary>
    public class ConstructorHandler : BaseHandler, IMethodHandler
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ConstructorHandler"/> class.
        /// </summary>
        /// <param name="logger">The logger instance.</param>
        public ConstructorHandler(ILogger logger) : base(logger)
        {
        }

        /// <inheritdoc/>
        public IEnvironment Marshal(BaseDecl baseDecl, ITypeDatabase typeDatabase)
        {
            if (baseDecl is not MethodDecl methodDecl)
            {
                throw new ArgumentException("The provided decl must be a MethodDecl.", nameof(baseDecl));
            }
            return new MethodEnvironment(methodDecl, typeDatabase);
        }

        /// <inheritdoc/>
        public void Emit(CSharpWriter csWriter, SwiftWriter swiftWriter, IEnvironment env, Conductor conductor)
        {
            var methodEnv = (MethodEnvironment)env;

            // If the conductor has a P/Invoke helper context and the environment doesn't,
            // create a new environment with the context (for generic type support)
            if (conductor.CurrentPInvokeHelperContext != null && methodEnv.PInvokeHelperContext == null)
            {
                methodEnv = new MethodEnvironment(methodEnv.MethodDecl, methodEnv.TypeDatabase, methodEnv.SiblingPropertyNames, conductor.CurrentPInvokeHelperContext);
            }

            var signatureHandler = new SignatureHandler(methodEnv);

            if (signatureHandler.GetWrapperSignature().ContainsPlaceholder)
            {
                _logger.LogWarning($"Constructor {methodEnv.MethodDecl.Name} has unsupported signature: ({signatureHandler.GetWrapperSignature().ParametersString()}) -> {signatureHandler.GetWrapperSignature().ReturnType}");
                return;
            }

            var wrapperEmitter = new WrapperEmitter(methodEnv, signatureHandler);
            wrapperEmitter.EmitConstructor(csWriter);
            PInvokeEmitter.EmitPInvoke(csWriter, methodEnv, signatureHandler);
            csWriter.WriteLine();
        }
    }

    /// <summary>
    /// Represents a method handler factory.
    /// </summary>
    public class MethodHandlerFactory : HandlerFactory, IFactory<BaseDecl, IMethodHandler>
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="MethodHandlerFactory"/> class.
        /// </summary>
        /// <param name="loggerFactory">The logger factory instance.</param>
        public MethodHandlerFactory(ILoggerFactory loggerFactory) : base(loggerFactory.CreateLogger<MethodHandler>())
        {
        }

        /// <summary>
        /// Checks if the factory can handle the declaration.
        /// </summary>
        /// <param name="decl">The base declaration.</param>
        /// <returns></returns>
        public bool Handles(BaseDecl decl)
        {
            return decl is MethodDecl;
        }

        /// <summary>
        /// Constructs a handler.
        /// </summary>
        public IMethodHandler Construct()
        {
            return new MethodHandler(_handlerLogger);
        }
    }

    /// <summary>
    /// Represents a method handler.
    /// </summary>
    public class MethodHandler : BaseHandler, IMethodHandler
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="MethodHandler"/> class.
        /// </summary>
        /// <param name="logger">The logger instance.</param>
        public MethodHandler(ILogger logger) : base(logger)
        {
        }

        /// <inheritdoc/>
        public IEnvironment Marshal(BaseDecl baseDecl, ITypeDatabase typeDatabase)
        {
            if (baseDecl is not MethodDecl methodDecl)
            {
                throw new ArgumentException("The provided decl must be a MethodDecl.", nameof(baseDecl));
            }
            return new MethodEnvironment(methodDecl, typeDatabase);
        }

        /// <inheritdoc/>
        public void Emit(CSharpWriter csWriter, SwiftWriter swiftWriter, IEnvironment env, Conductor conductor)
        {
            var methodEnv = (MethodEnvironment)env;

            // If the conductor has a P/Invoke helper context and the environment doesn't,
            // create a new environment with the context (for generic type support)
            if (conductor.CurrentPInvokeHelperContext != null && methodEnv.PInvokeHelperContext == null)
            {
                methodEnv = new MethodEnvironment(methodEnv.MethodDecl, methodEnv.TypeDatabase, methodEnv.SiblingPropertyNames, conductor.CurrentPInvokeHelperContext);
            }

            // Skip methods with constraints on protocols with associated types
            // (these protocols generate generic C# interfaces which can't be used as constraints without type arguments)
            if (HasUnsupportedProtocolConstraints(methodEnv))
            {
                _logger.LogWarning($"Skipping method {methodEnv.MethodDecl.Name}: has constraints on protocols with associated types");
                return;
            }

            var signatureHandler = new SignatureHandler(methodEnv);

            if (signatureHandler.GetWrapperSignature().ContainsPlaceholder)
            {
                _logger.LogWarning($"Method {methodEnv.MethodDecl.Name} has unsupported signature: ({signatureHandler.GetWrapperSignature().ParametersString()}) -> {signatureHandler.GetWrapperSignature().ReturnType}");
                return;
            }

            var wrapperEmitter = new WrapperEmitter(methodEnv, signatureHandler);
            wrapperEmitter.EmitMethod(csWriter, swiftWriter);
            PInvokeEmitter.EmitPInvoke(csWriter, methodEnv, signatureHandler);
            csWriter.WriteLine();
        }

        /// <summary>
        /// Checks if the method has constraints on protocols with associated types.
        /// Such protocols generate generic C# interfaces which can't be used as constraints without type arguments.
        /// </summary>
        private bool HasUnsupportedProtocolConstraints(MethodEnvironment methodEnv)
        {
            if (!methodEnv.MethodDecl.IsGeneric)
                return false;

            foreach (var param in methodEnv.MethodDecl.GenericParameters)
            {
                foreach (var conformance in param.GenericConformances)
                {
                    if (conformance.Kind == ConformanceKind.Protocol)
                    {
                        if (methodEnv.TypeDatabase.TryGetTypeRecord(conformance.ConformanceTarget, out var record))
                        {
                            if (record.Kind == TypeRecordKind.Protocol &&
                                record.Flags.HasFlag(TypeRecordFlags.HasAssociatedTypes))
                            {
                                return true;
                            }
                        }
                    }
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Represents a parameter.
    /// </summary>
    /// <param name="Type"></param>
    /// <param name="Name"></param>
    public record Parameter(string Type, string Name, string modifier = "")
    {
        public string CallString() => $"{Type} {Name}";
        public string SignatureString() => Type switch
        {
            "AsyncCallback" => $"{modifier} void* {Name}",
            "AsyncErrorCallback" => $"{modifier} void* {Name}",
            "AsyncContext" => $"{modifier} void* {Name}",
            "AsyncTask" => $"{modifier} IntPtr {Name}",
            "IntPtrFromNonFrozen" => $"{modifier} IntPtr {Name}",
            // ObjC bridged types use IntPtr in P/Invoke
            var t when t.StartsWith("ObjCBridged:") => $"{modifier} IntPtr {Name}",
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

        public string CallArgumentsString() => string.Join(", ", Parameters.Select(p => GetCallArgumentString(p)));

        public static string GetCallArgumentString(Parameter parameter)
        {
            return parameter switch
            {
                { Type: "SafeHandle" } => $"{parameter.Name}.Payload",
                { Type: "IntPtrFromNonFrozen" } => $"{parameter.Name}Handle",
                { Type: var type } when type.EndsWith(".Buffer") => $"{parameter.Name}Disposable.Buffer",
                { Type: "AsyncCallback" } => $"{parameter.Name}",
                { Type: "AsyncErrorCallback" } => $"{parameter.Name}",
                { Type: "AsyncContext" } => "null",
                { Type: "AsyncTask" } => $"GCHandle.ToIntPtr({parameter.Name})",
                { modifier: "out" } => $"out var {parameter.Name}",
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

        /// <summary>
        /// Initializes a new instance of the <see cref="SignatureBuilderBase"/> class.
        /// </summary>
        /// <param name="env">The method environment.</param>
        protected SignatureBuilderBase(MethodEnvironment env)
        {
            _env = env;
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

            // Check for automatic .NET type conversion FIRST (SwiftString -> string, SwiftArray -> IReadOnlyList, etc.)
            // Skip for property accessors to avoid type mismatch with property declaration
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
                var csTypeParam = _env.BoundGenericsHandler.TranslateBoundGenericTypeToCSharp(argument);
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
                if (_env.TupleHandler.IsSupportedTuple(tupleTypeSpec))
                    SetReturnType(_env.TupleHandler.GetCSharpTupleType(tupleTypeSpec));
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
                    var existentialType = _env.ExistentialHandler.GetCSharpExistentialType(protocolList);
                    SetReturnType(existentialType);
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
                    var optionalExistentialType = _env.ExistentialHandler.GetCSharpOptionalExistentialType(innerProtocolList);
                    SetReturnType(optionalExistentialType);
                }
                else
                {
                    SetReturnType(TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName);
                }
                return;
            }

            // Check for native type remapping (URL → NSUrl, Data → NSData)
            // Skip for property accessors to maintain property/accessor type consistency
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
        /// </summary>
        public void HandleArguments()
        {
            foreach (var argument in _env.MethodDecl.CSSignature.Skip(1))
            {
                // Check for automatic .NET type conversion FIRST (SwiftString -> string, SwiftArray -> IEnumerable, etc.)
                // Skip for property accessors to avoid type mismatch with property declaration
                if (!_env.MethodDecl.IsAccessor)
                {
                    var idiomaticType = _env.TypeConversionHandler.GetIdiomaticCSharpType(
                        argument.SwiftTypeSpec,
                        isParameter: true,
                        typeSpec => TranslateTypeSpecForConversion(typeSpec));
                    if (idiomaticType != null)
                    {
                        AddParameter(idiomaticType, argument.Name);
                        continue;
                    }
                }

                if (_env.BoundGenericsHandler.IsBoundGeneric(argument))
                {
                    var csTypeParam = _env.BoundGenericsHandler.TranslateBoundGenericTypeToCSharp(argument);
                    AddParameter(csTypeParam, argument.Name);
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
                        AddParameter(delegateType, argument.Name);
                    }
                    else
                    {
                        // Unsupported closure - use placeholder that will cause method to be skipped
                        AddParameter(TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName, argument.Name);
                    }
                    continue;
                }

                // Handle tuple arguments
                if (_env.TupleHandler.IsTuple(argument))
                {
                    var tupleTypeSpec = _env.TupleHandler.GetTupleTypeSpec(argument)!;
                    if (_env.TupleHandler.IsSupportedTuple(tupleTypeSpec))
                        AddParameter(_env.TupleHandler.GetCSharpTupleType(tupleTypeSpec), argument.Name);
                    else
                        AddParameter(TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName, argument.Name);
                    continue;
                }

                // Handle existential arguments (any Protocol)
                if (_env.ExistentialHandler.IsExistential(argument.SwiftTypeSpec))
                {
                    var protocolList = _env.ExistentialHandler.ToProtocolListTypeSpec(argument.SwiftTypeSpec)!;
                    if (_env.ExistentialHandler.IsSupportedExistential(protocolList))
                    {
                        var existentialType = _env.ExistentialHandler.GetCSharpExistentialType(protocolList);
                        AddParameter(existentialType, argument.Name);
                    }
                    else
                    {
                        AddParameter(TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName, argument.Name);
                    }
                    continue;
                }

                // Handle Optional-wrapped existential arguments like (any DataCaching)?
                if (_env.ExistentialHandler.IsOptionalExistential(argument.SwiftTypeSpec))
                {
                    var innerProtocolList = _env.ExistentialHandler.UnwrapOptionalExistential(argument.SwiftTypeSpec)!;
                    if (_env.ExistentialHandler.IsSupportedExistential(innerProtocolList))
                    {
                        var optionalExistentialType = _env.ExistentialHandler.GetCSharpOptionalExistentialType(innerProtocolList);
                        AddParameter(optionalExistentialType, argument.Name);
                    }
                    else
                    {
                        AddParameter(TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName, argument.Name);
                    }
                    continue;
                }

                if (argument.IsGeneric)
                {
                    var csTypeParamName = _env.GenericTypeMapping[argument.SwiftTypeSpec.ToString()].TypeParameter;
                    AddParameter(csTypeParamName, argument.Name);
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
                            AddParameter(nativeType, argument.Name);
                            continue;
                        }
                    }

                    var typeRecord = _env.TypeDatabase.GetTypeRecordOrAnyType(argument.SwiftTypeSpec);
                    // Protocol types (interfaces) are not supported as parameters because they don't have Payload property
                    if (typeRecord.Kind == TypeRecordKind.Protocol)
                    {
                        AddParameter(TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName, argument.Name);
                        continue;
                    }
                    AddParameter(typeRecord.CSharpTypeName.FullyQualifiedName, argument.Name);
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
            if (_env.ExistentialHandler.IsExistential(typeSpec))
            {
                var protocolList = _env.ExistentialHandler.ToProtocolListTypeSpec(typeSpec);
                if (protocolList != null && _env.ExistentialHandler.IsSupportedExistential(protocolList))
                    return _env.ExistentialHandler.GetCSharpExistentialType(protocolList);
                return TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName;
            }

            if (typeSpec is NamedTypeSpec namedTypeSpec)
            {
                var typeRecord = _env.TypeDatabase.GetTypeRecordOrAnyType(namedTypeSpec);

                // If the type falls back to AnyType, don't append generic parameters
                if (typeRecord == TypeDatabaseExtensions.AnyType)
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
    /// Builds the P/Invoke signature (low-level native interop).
    /// </summary>
    public class PInvokeSignatureBuilder : SignatureBuilderBase
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="PInvokeSignatureBuilder"/> class.
        /// </summary>
        /// <param name="env">The method environment.</param>
        public PInvokeSignatureBuilder(MethodEnvironment env) : base(env)
        {
        }

        /// <summary>
        /// Handles the return type of the method.
        /// </summary>
        public void HandleReturnType()
        {
            var returnType = _env.MethodDecl.CSSignature.First();

            // For non-constructor methods, bound generics that require marshalling (SwiftArray, SwiftOptional, etc.)
            // return IntPtr directly from PInvoke. Constructors need special handling via indirect result
            // since failable initializers return Optional<Self> which can't be assigned to 'this'.
            if (!_env.MethodDecl.IsConstructor && _env.BoundGenericsHandler.IsBoundGeneric(returnType))
            {
                var csTypeParam = _env.BoundGenericsHandler.RequiresBoundGenericMarshalling(returnType) switch
                {
                    true => _env.BoundGenericsHandler.GetBufferType(returnType),
                    false => _env.BoundGenericsHandler.TranslateBoundGenericTypeToCSharp(returnType)
                };
                SetReturnType(csTypeParam);
                return;
            }

            // Handle closure return types (including optional closures)
            // Swift returns closures as SwiftClosureData (function + context pointers)
            // Optional closures use the same struct - nil is represented by zero pointers
            if (_env.ClosureHandler.IsClosure(returnType))
            {
                var closureTypeSpec = _env.ClosureHandler.GetClosureTypeSpec(returnType)!;
                if (_env.ClosureHandler.IsSupportedClosure(closureTypeSpec))
                {
                    SetReturnType("SwiftClosureData");
                }
                else
                {
                    SetReturnType(TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName);
                }
                return;
            }

            // Handle tuple return types
            if (_env.TupleHandler.IsTuple(returnType.SwiftTypeSpec))
            {
                var tupleTypeSpec = (TupleTypeSpec)returnType.SwiftTypeSpec;
                if (_env.TupleHandler.IsSupportedTuple(tupleTypeSpec))
                    SetReturnType(_env.TupleHandler.GetPInvokeTupleType(tupleTypeSpec));
                else
                    SetReturnType(TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName);
                return;
            }

            // Handle existential return types (any Protocol)
            if (_env.ExistentialHandler.IsExistential(returnType.SwiftTypeSpec))
            {
                var protocolList = _env.ExistentialHandler.ToProtocolListTypeSpec(returnType.SwiftTypeSpec)!;
                if (_env.ExistentialHandler.IsSupportedExistential(protocolList))
                {
                    var existentialType = _env.ExistentialHandler.GetPInvokeExistentialType(protocolList);
                    SetReturnType(existentialType);
                }
                else
                {
                    SetReturnType(TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName);
                }
                return;
            }

            // Handle Optional-wrapped existential return types like (any DataCaching)?
            // For P/Invoke, these use IntPtr since they require indirect marshalling
            if (_env.ExistentialHandler.IsOptionalExistential(returnType.SwiftTypeSpec))
            {
                var innerProtocolList = _env.ExistentialHandler.UnwrapOptionalExistential(returnType.SwiftTypeSpec)!;
                if (_env.ExistentialHandler.IsSupportedExistential(innerProtocolList))
                {
                    // Optional existentials are passed by pointer/indirect result
                    SetReturnType("IntPtr");
                }
                else
                {
                    SetReturnType(TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName);
                }
                return;
            }

            if (MarshallingHelpers.MethodRequiresIndirectResult(_env))
            {
                AddParameter("SwiftIndirectResult", "swiftIndirectResult");
                SetReturnType("void");
                return;
            }

            TypeRecord returnTypeRecord = _env.TypeDatabase.GetTypeRecordOrThrow(returnType.SwiftTypeSpec);

            // ObjC bridged types return IntPtr in P/Invoke, then wrapped with GetNSObject<T>
            if (MarshallingHelpers.IsObjCBridged(returnTypeRecord))
            {
                SetReturnType("IntPtr");
                return;
            }

            // Swift classes return pointers directly in registers (not via indirect result)
            // Since classes don't have a Buffer struct, return IntPtr and create the object from it
            if (returnTypeRecord.Kind == TypeRecordKind.Class)
            {
                SetReturnType("IntPtr");
                return;
            }

            if (_env.MethodDecl.IsAsync && !MarshallingHelpers.IsTypeFrozen(returnTypeRecord))
            {
                SetReturnType("IntPtr");
                return;
            }

            if (MarshallingHelpers.RequiresMemoryManagement(returnTypeRecord))
                SetReturnType(returnTypeRecord.CSharpTypeName.FullyQualifiedName + ".Buffer");
            else
                SetReturnType(returnTypeRecord.CSharpTypeName.FullyQualifiedName);
        }

        /// <summary>
        /// Handles the Swift async arguments of the method.
        /// </summary>
        public void HandleSwiftAsync()
        {
            if (_env.MethodDecl.IsAsync)
            {
                // Our Swift wrapper expects: callback, errorCallback, task (handle), then method arguments
                // No context parameter needed - we handle the callback in Swift
                AddParameter("AsyncCallback", NameProvider.GetAsyncCallbackFieldName(_env.MethodDecl));
                AddParameter("AsyncErrorCallback", NameProvider.GetAsyncErrorCallbackFieldName(_env.MethodDecl));
                AddParameter("AsyncTask", "handle");
            }
        }

        /// <summary>
        /// Handles the arguments of the method.
        /// </summary>
        public void HandleArguments()
        {
            foreach (var argument in _env.MethodDecl.CSSignature.Skip(1))
            {
                if (_env.BoundGenericsHandler.IsBoundGeneric(argument))
                {
                    var (csTypeParam, csTypeName) = _env.BoundGenericsHandler.RequiresBoundGenericMarshalling(argument) switch
                    {
                        true => (_env.BoundGenericsHandler.GetBufferType(argument), NameProvider.GetBoundGenericBufferName(argument.Name)),
                        false => (_env.BoundGenericsHandler.TranslateBoundGenericTypeToCSharp(argument), argument.Name)
                    };

                    AddParameter(csTypeParam, csTypeName);
                    continue;
                }

                // Handle closure arguments (including optional closures)
                if (_env.ClosureHandler.IsClosure(argument))
                {
                    var closureTypeSpec = _env.ClosureHandler.GetClosureTypeSpec(argument)!;
                    if (_env.ClosureHandler.IsSupportedClosure(closureTypeSpec))
                    {
                        // Async+throwing closures use a different pattern - they pass context + start function
                        // to a Swift wrapper that creates the actual async closure
                        if (_env.ClosureHandler.IsAsyncThrowingClosure(closureTypeSpec))
                        {
                            // Pass context pointer and start function pointer as separate parameters
                            // The Swift wrapper will use these to create the async closure
                            // Use special type markers that include the parameter name for variable mapping
                            var callbackName = ClosureHandler.GetCallbackFunctionName(
                                _env.MethodDecl.Name, argument.Name, _env.MethodDecl.MangledName);
                            AddParameter($"AsyncThrowingContext:{argument.Name}", argument.Name + "Context");
                            AddParameter($"AsyncThrowingStartFunc:{callbackName}", argument.Name + "StartFunc");
                        }
                        else if (_env.ClosureHandler.RequiresThunk(closureTypeSpec))
                        {
                            // Escaping closures are passed as a single SwiftClosureData struct
                            // containing both function pointer and context.
                            // Optional closures use the same struct - nil is represented by zero pointers.
                            AddParameter("SwiftClosureData", argument.Name);
                        }
                        else
                        {
                            // @convention(c) closures just need the function pointer
                            var funcPtrType = _env.ClosureHandler.GetPInvokeFunctionPointerType(closureTypeSpec);
                            AddParameter(funcPtrType, argument.Name);
                        }
                    }
                    else
                    {
                        // Unsupported closure - use placeholder
                        AddParameter(TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName, argument.Name);
                    }
                    continue;
                }

                // Handle tuple arguments
                if (_env.TupleHandler.IsTuple(argument))
                {
                    var tupleTypeSpec = _env.TupleHandler.GetTupleTypeSpec(argument)!;
                    if (_env.TupleHandler.IsSupportedTuple(tupleTypeSpec))
                        AddParameter(_env.TupleHandler.GetPInvokeTupleType(tupleTypeSpec), argument.Name);
                    else
                        AddParameter(TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName, argument.Name);
                    continue;
                }

                // Handle existential arguments (any Protocol) - pass container by value
                if (_env.ExistentialHandler.IsExistential(argument.SwiftTypeSpec))
                {
                    var protocolList = _env.ExistentialHandler.ToProtocolListTypeSpec(argument.SwiftTypeSpec)!;
                    if (_env.ExistentialHandler.IsSupportedExistential(protocolList))
                    {
                        var existentialType = _env.ExistentialHandler.GetPInvokeExistentialType(protocolList);
                        AddParameter(existentialType, argument.Name);
                    }
                    else
                    {
                        AddParameter(TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName, argument.Name);
                    }
                    continue;
                }

                // Handle Optional-wrapped existential arguments like (any DataCaching)?
                // These are passed as nullable existential containers
                if (_env.ExistentialHandler.IsOptionalExistential(argument.SwiftTypeSpec))
                {
                    var innerProtocolList = _env.ExistentialHandler.UnwrapOptionalExistential(argument.SwiftTypeSpec)!;
                    if (_env.ExistentialHandler.IsSupportedExistential(innerProtocolList))
                    {
                        var optionalExistentialType = _env.ExistentialHandler.GetCSharpOptionalExistentialType(innerProtocolList);
                        AddParameter(optionalExistentialType, argument.Name);
                    }
                    else
                    {
                        AddParameter(TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName, argument.Name);
                    }
                    continue;
                }

                // Handle native type remapping (URL → NSUrl, Data → NSData in public API)
                // Skip for property accessors to maintain consistency with wrapper signature
                if (!_env.MethodDecl.IsAccessor && _env.TypeConversionHandler.HasNativeTypeRemapping(argument.SwiftTypeSpec))
                {
                    TypeRecord nativeRemapTypeRecord = _env.TypeDatabase.GetTypeRecordOrThrow(argument.SwiftTypeSpec);
                    var swiftWrapperType = _env.TypeConversionHandler.GetSwiftWrapperTypeForNative(argument.SwiftTypeSpec);
                    if (!MarshallingHelpers.IsTypeFrozen(nativeRemapTypeRecord))
                    {
                        // Non-frozen (URL): use NativeRemappedSafeHandle marker
                        AddParameter("NativeRemappedSafeHandle", argument.Name);
                    }
                    else
                    {
                        // Frozen (Data): use NativeRemapped:{type} marker
                        AddParameter($"NativeRemapped:{swiftWrapperType}", argument.Name);
                    }
                    continue;
                }

                if (argument.IsGeneric)
                {
                    var payloadName = NameProvider.GetPayloadName(argument.Name);
                    AddParameter("IntPtr", payloadName);
                    continue;
                }

                TypeRecord argumentTypeRecord = _env.TypeDatabase.GetTypeRecordOrThrow(argument.SwiftTypeSpec);

                // ObjC bridged types use IntPtr in P/Invoke, Handle extracted from the .NET iOS binding
                if (MarshallingHelpers.IsObjCBridged(argumentTypeRecord))
                {
                    // Store the original C# type name for use in wrapper generation
                    AddParameter($"ObjCBridged:{argumentTypeRecord.CSharpTypeName.FullyQualifiedName}", argument.Name);
                    continue;
                }

                if (!MarshallingHelpers.IsTypeFrozen(argumentTypeRecord))
                {
                    // For async methods, SafeHandle cannot be used with Swift calling convention.
                    // Use IntPtr and manage lifetime manually via DangerousAddRef/DangerousRelease.
                    if (_env.MethodDecl.IsAsync)
                        AddParameter("IntPtrFromNonFrozen", argument.Name);
                    else
                        AddParameter("SafeHandle", argument.Name);
                    continue;
                }

                if (MarshallingHelpers.RequiresMemoryManagement(argumentTypeRecord))
                    AddParameter(argumentTypeRecord.CSharpTypeName.FullyQualifiedName + ".Buffer", argument.Name);
                else
                    AddParameter(argumentTypeRecord.CSharpTypeName.FullyQualifiedName, argument.Name);
            }
        }

        /// <summary>
        /// Handles the metadata of generic arguments.
        /// </summary>
        public void HandleGenericMetadata()
        {
            foreach (var genericParameter in _env.MethodDecl.GenericParameters)
            {
                var metadataName = NameProvider.GetMetadataName(_env.GenericTypeMapping[genericParameter.TypeName].TypeParameter);
                AddParameter("TypeMetadata", metadataName);
            }
        }

        /// <summary>
        /// Handles the protocol conformances of the generic parameters of the method.
        /// </summary>
        public void HandleProtocolConformance()
        {
            foreach (var genericParameter in _env.MethodDecl.GenericParameters)
            {
                var conformances = genericParameter.GenericConformances.OrderBy(c => c.ConformanceTarget.ModuleQualifiedName);
                foreach (var conformance in conformances)
                {
                    // Skip unknown protocols and protocols with associated types
                    // (protocols with associated types generate generic interfaces which can't be used here)
                    // This must match the check in EmitProtocolWitnessTables to avoid generating
                    // PInvoke signatures with parameters that have no corresponding variables.
                    if (!IsProtocolAvailableForConstraint(conformance.ConformanceTarget, _env.TypeDatabase))
                        continue;

                    var pwtName = NameProvider.GetProtocolWitnessTableName(_env.GenericTypeMapping[genericParameter.TypeName].TypeParameter, conformance.ConformanceTarget.Name);
                    AddParameter("ProtocolWitnessTable", pwtName);
                }
            }
        }

        /// <summary>
        /// Determines whether a protocol can be used as a generic constraint.
        /// Returns false for unknown protocols or protocols with associated types.
        /// </summary>
        private static bool IsProtocolAvailableForConstraint(SwiftTypeName protocolTypeName, ITypeDatabase typeDatabase)
        {
            if (typeDatabase.TryGetTypeRecord(protocolTypeName, out var record))
            {
                // Must be a protocol and must NOT have associated types
                // (protocols with associated types generate generic interfaces which can't be used as constraints)
                return record.Kind == TypeRecordKind.Protocol &&
                       !record.Flags.HasFlag(TypeRecordFlags.HasAssociatedTypes);
            }
            return false;
        }

        /// <summary>
        /// Handles the SwiftSelf parameter of the method.
        /// </summary>
        public void HandleSwiftSelf()
        {
            // Async instance methods on non-singleton classes pass self as explicit IntPtr parameter.
            // We use a module-level free function (not extension method) to avoid SwiftSelf binding issues.
            // Singleton classes use the ClassName.shared workaround and don't need _self.
            if (_env.MethodDecl.IsAsync && MarshallingHelpers.MethodRequiresSwiftSelf(_env))
            {
                var hasSingleton = (_env.ParentDecl as TypeDecl)?.HasSingletonPattern ?? false;
                if (!hasSingleton)
                {
                    // For non-singleton async methods, pass self as explicit IntPtr
                    // Use different parameter names to distinguish at call site:
                    // - _selfClass: class instance (needs dereferencing - payload contains pointer to class)
                    // - _self: struct instance (no dereference - payload IS the data)
                    var selfName = _env.ParentDecl is ClassDecl ? "_selfClass" : "_self";
                    AddParameter("IntPtr", selfName);
                }
                // For singleton classes, don't add any self parameter - we use .shared in Swift
                return;
            }

            if (MarshallingHelpers.MethodRequiresSwiftSelf(_env))
            {
                if (_env.ParentDecl is StructDecl structDecl && structDecl.IsFrozen)
                {
                    // Setters always need pointer semantics (even for frozen structs)
                    // because they modify the struct in-place
                    if (MarshallingHelpers.MethodIsSetter(_env.MethodDecl))
                    {
                        AddParameter("SwiftSelf", "self");
                    }
                    else
                    {
                        // Getters can use value semantics for frozen structs
                        var typeRecord = _env.TypeDatabase.GetTypeRecordOrThrow(structDecl.SwiftTypeName);
                        if (MarshallingHelpers.RequiresMemoryManagement(typeRecord))
                            AddParameter($"SwiftSelf<{_env.ParentDecl.Name}.Buffer>", "self");
                        else
                            AddParameter($"SwiftSelf<{_env.ParentDecl.Name}>", "self");
                    }
                }
                else
                {
                    AddParameter("SwiftSelf", "self");
                }
            }
        }

        /// <summary>
        /// Handles the SwiftError parameter of the method.
        /// </summary>
        public void HandleSwiftError()
        {
            // Async methods call our generated Swift wrapper which handles errors internally
            if (_env.MethodDecl.IsAsync)
                return;

            if (_env.MethodDecl.Throws)
            {
                AddParameter("SwiftError", "error", "out");
            }
        }

        /// <summary>
        /// Adds context parameter to a function pointer type string for escaping closures.
        /// Transforms "delegate* unmanaged[Cdecl]&lt;int, void&gt;" to "delegate* unmanaged[Cdecl]&lt;int, IntPtr, void&gt;"
        /// </summary>
        private static string AddContextToFunctionPointerType(string funcPtrType)
        {
            // Find the last comma before '>'
            int lastAngle = funcPtrType.LastIndexOf('>');
            if (lastAngle == -1)
                return funcPtrType;

            int lastComma = funcPtrType.LastIndexOf(',', lastAngle);
            if (lastComma == -1)
            {
                // No parameters, just return type: "delegate* unmanaged[Cdecl]<void>"
                // Insert "IntPtr, " before the return type
                int openAngle = funcPtrType.IndexOf('<');
                if (openAngle == -1)
                    return funcPtrType;

                return funcPtrType.Insert(openAngle + 1, "IntPtr, ");
            }

            // Insert ", IntPtr" after the last comma
            return funcPtrType.Insert(lastComma + 1, " IntPtr,");
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

    /// <summary>
    /// Provides methods for emitting PInvoke signatures.
    /// </summary>
    internal static class PInvokeEmitter
    {
        /// <summary>
        /// Emits the PInvoke signature or collects it to a helper context for generic types.
        /// </summary>
        /// <param name="csWriter">The IndentedTextWriter instance.</param>
        /// <param name="methodEnv">The method environment.</param>
        /// <param name="signatureHandler">The signature handler.</param>
        public static void EmitPInvoke(CSharpWriter csWriter, MethodEnvironment methodEnv, SignatureHandler signatureHandler)
        {
            var methodDecl = (MethodDecl)methodEnv.MethodDecl;
            var moduleDecl = methodDecl.ModuleDecl ?? throw new ArgumentNullException(nameof(methodDecl.ModuleDecl));

            var pInvokeName = NameProvider.GetPInvokeName(methodDecl);
            // Async methods use generated Swift wrappers that may be compiled into a separate library
            // If AsyncLibraryName is set, use it; otherwise fall back to the module's library
            var moduleLibPath = methodEnv.TypeDatabase.GetLibraryPath(moduleDecl.Name);
            var libPath = methodDecl.IsAsync && methodEnv.TypeDatabase.AsyncLibraryName != null
                ? methodEnv.TypeDatabase.AsyncLibraryName
                : moduleLibPath;

            var pInvokeSignature = signatureHandler.GetPInvokeSignature();

            // If we're inside a generic type, collect the P/Invoke to the helper context
            // instead of emitting it inline (to avoid CS7042: DllImport in generic type)
            if (methodEnv.PInvokeHelperContext != null)
            {
                var declaration = new PInvokeDeclaration
                {
                    LibraryPath = libPath,
                    EntryPoint = NameProvider.GetMangledName(methodDecl),
                    MethodName = pInvokeName,
                    ReturnType = pInvokeSignature.ReturnType,
                    ParametersString = pInvokeSignature.ParametersString(),
                    IsAsync = methodDecl.IsAsync,
                    MetadataParameters = methodEnv.PInvokeHelperContext.GetMetadataParameterDeclarations()
                };
                methodEnv.PInvokeHelperContext.AddDeclaration(declaration);
            }
            else
            {
                // Emit directly (non-generic type)
                csWriter.WriteLine("[UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]");
                csWriter.WriteLine($"[DllImport(\"{libPath}\", EntryPoint = \"{NameProvider.GetMangledName(methodDecl)}\")]");
                csWriter.WriteLine($"private static extern {(methodDecl.IsAsync ? "void" : pInvokeSignature.ReturnType)} {pInvokeName}({pInvokeSignature.ParametersString()});");
            }
        }
    }

    /// <summary>
    /// Provides methods for emitting wrappers.
    /// </summary>

    internal class WrapperEmitter
    {
        private readonly MethodEnvironment _env;
        private readonly Signature _wrapperSignature;
        private readonly Signature _pInvokeSignature;
        private readonly bool _requiresIndirectResult;
        private readonly bool _requiresSwiftSelf;
        private readonly bool _requiresSwiftError;
        private readonly bool _requiresSwiftAsync;
        private readonly bool _requiresFixedBlock;

        internal WrapperEmitter(MethodEnvironment methodEnv, SignatureHandler signatureHandler)
        {
            _env = methodEnv;

            _wrapperSignature = signatureHandler.GetWrapperSignature();
            _pInvokeSignature = signatureHandler.GetPInvokeSignature();

            _requiresIndirectResult = MarshallingHelpers.MethodRequiresIndirectResult(methodEnv);
            _requiresSwiftAsync = _env.MethodDecl.IsAsync;
            // Async methods need SwiftSelf to pass self to the Swift wrapper
            _requiresSwiftSelf = MarshallingHelpers.MethodRequiresSwiftSelf(methodEnv);
            // Async methods call our generated Swift wrapper which handles errors internally
            _requiresSwiftError = !_requiresSwiftAsync && _env.MethodDecl.Throws;

            // Frozen struct setters need a fixed block to get a pointer to 'this'
            // because setters modify the struct in-place (pointer semantics)
            // while getters can pass by value (value semantics)
            _requiresFixedBlock = false;
            if (_env.ParentDecl is StructDecl structDecl && structDecl.IsFrozen && MarshallingHelpers.MethodIsSetter(_env.MethodDecl))
            {
                var typeRecord = _env.TypeDatabase.GetTypeRecordOrThrow(structDecl.SwiftTypeName);
                // Only pure frozen structs (no memory management) need the fixed block
                // Frozen structs with memory management use _payload SafeHandle like non-frozen types
                _requiresFixedBlock = !MarshallingHelpers.RequiresMemoryManagement(typeRecord);
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

            EmitSignatureConstructor(csWriter);
            EmitBodyStart(csWriter);
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
            EmitSwiftError(csWriter);
            EmitReturnConstructor(csWriter);

            // Add cleanup in finally block for generics and closures
            if (needsTryFinally)
            {
                EmitTryBlockEnd(csWriter);
                EmitFinally(csWriter);
            }

            EmitBodyEnd(csWriter);
        }

        /// <summary>
        /// Emits the method wrapper.
        /// </summary>
        /// <param name="writer">The IndentedTextWriter instance.</param>
        internal void EmitMethod(CSharpWriter csWriter, SwiftWriter swiftWriter)
        {
            EmitAsyncWrapper(csWriter);
            EmitClosureCallbacks(csWriter);
            EmitSignatureMethod(csWriter);
            EmitBodyStart(csWriter);
            EmitAsync(csWriter, swiftWriter);
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
            EmitSwiftError(csWriter);
            EmitReturnMethod(csWriter);

            EmitFixedBlockEnd(csWriter);
            EmitTryBlockEnd(csWriter);
            EmitFinally(csWriter);
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
                var payloadName = NameProvider.GetPayloadName(argument.Name);
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
                    csWriter.WriteLine($"GCHandle {argument.Name}Handle = default;");
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
            if (_requiresSwiftAsync)
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
                        csWriter.WriteLine($"var self = new SwiftSelf<{structDecl.Name}.Buffer>(*({structDecl.Name}.Buffer*)_payload.DangerousGetHandle());");
                }
                else
                    csWriter.WriteLine($"var self = new SwiftSelf<{structDecl.Name}>(this);");
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
        /// Emits the Async task.
        /// </summary>
        /// <param name="csWriter">The IndentedTextWriter instance.</param>
        private void EmitAsync(CSharpWriter csWriter, SwiftWriter swiftWriter)
        {
            if (!_requiresSwiftAsync)
                return;

            bool isEmptyTuple = _env.MethodDecl.CSSignature.First().SwiftTypeSpec.IsEmptyTuple;
            bool isInstanceMethod = _env.MethodDecl.MethodType != MethodType.Static;
            bool isSwiftClass = _env.ParentDecl is ClassDecl;

            // Identify non-frozen parameters that need to be kept alive until callback
            var nonFrozenParams = _env.MethodDecl.CSSignature
                .Skip(1)
                .Where(p => !p.IsGeneric && !_env.BoundGenericsHandler.IsBoundGeneric(p) && !_env.ClosureHandler.IsClosure(p) && !_env.TupleHandler.IsTuple(p))
                .Where(p =>
                {
                    var typeRecord = _env.TypeDatabase.GetTypeRecordOrThrow(p.SwiftTypeSpec);
                    return !MarshallingHelpers.IsTypeFrozen(typeRecord);
                })
                .ToList();

            // For non-frozen parameters, create proper copies using InitializeWithCopy FIRST
            // (before the holder is created), so the copy buffer pointers can be stored in the holder.
            // Swift reads via .pointee (bitwise copy). Original params kept alive to maintain ref count.
            if (nonFrozenParams.Count > 0)
            {
                // Create copy buffers for non-frozen parameters
                foreach (var p in nonFrozenParams)
                {
                    var typeRecord = _env.TypeDatabase.GetTypeRecordOrThrow(p.SwiftTypeSpec);
                    var typeName = typeRecord.CSharpTypeName.FullyQualifiedName;

                    // For native-remapped types (e.g., Foundation.NSUrl -> Swift.URL), we need to
                    // convert to the Swift type first before copying. The wrapper signature uses the
                    // native type but the underlying Swift type is what we need to copy.
                    if (!_env.MethodDecl.IsAccessor && _env.TypeConversionHandler.HasNativeTypeRemapping(p.SwiftTypeSpec))
                    {
                        // Convert native type to Swift type, then copy from Swift type
                        var conversion = _env.TypeConversionHandler.GetNativeParameterConversion(p.Name, p.SwiftTypeSpec);
                        csWriter.WriteLines($"""
                            var {p.Name}Metadata = SwiftObjectHelper<{typeName}>.GetTypeMetadata();
                            IntPtr {p.Name}CopyBuffer = (IntPtr)NativeMemory.Alloc({p.Name}Metadata.Size);
                            using var {p.Name}SwiftTemp = {conversion};
                            {p.Name}Metadata.ValueWitnessTable->InitializeWithCopy(
                                (void*){p.Name}CopyBuffer,
                                (void*){p.Name}SwiftTemp.Payload.DangerousGetHandle(),
                                {p.Name}Metadata);
                            IntPtr {p.Name}Handle = {p.Name}CopyBuffer;
                            var {p.Name}CopyBufferWrapper = new CopyBufferWithType({p.Name}CopyBuffer, {p.Name}Metadata);
                            """);
                    }
                    else
                    {
                        csWriter.WriteLines($"""
                            var {p.Name}Metadata = SwiftObjectHelper<{typeName}>.GetTypeMetadata();
                            IntPtr {p.Name}CopyBuffer = (IntPtr)NativeMemory.Alloc({p.Name}Metadata.Size);
                            {p.Name}Metadata.ValueWitnessTable->InitializeWithCopy(
                                (void*){p.Name}CopyBuffer,
                                (void*){p.Name}.Payload.DangerousGetHandle(),
                                {p.Name}Metadata);
                            IntPtr {p.Name}Handle = {p.Name}CopyBuffer;
                            var {p.Name}CopyBufferWrapper = new CopyBufferWithType({p.Name}CopyBuffer, {p.Name}Metadata);
                            """);
                    }
                }

                // Now create the holder with copy buffer pointers AND original parameters AND self (for instance methods)
                // Original parameters must be kept alive to prevent GC from calling Destroy on them
                // while the async task is still running (InitializeWithCopy increments ref count,
                // but if original is destroyed, the internal storage could be freed prematurely)
                // Also keep 'this' alive for instance methods since SwiftSelf doesn't prevent GC
                var copyBufferList = string.Join(", ", nonFrozenParams.Select(p => $"{p.Name}CopyBufferWrapper"));
                var originalParamList = string.Join(", ", nonFrozenParams.Select(p => $"(object){p.Name}"));

                // For Swift classes, Arc.Retain the self pointer before async call.
                // SwiftSelf passes a raw pointer - no ARC semantics. By the time Swift's Task{}
                // closure runs, 'self' may be deallocated. Retain ensures Swift ARC tracks it.
                string selfInHolder;
                if (isInstanceMethod && isSwiftClass)
                {
                    // For Swift classes, retain self and store a RetainedSelfPtr marker
                    // The payload buffer contains a pointer to the class instance - we need to dereference it
                    csWriter.WriteLines($$"""
            IntPtr _selfPtr = *(IntPtr*)_payload.DangerousGetHandle();
            Arc.Retain(_selfPtr);
            """);
                    selfInHolder = ", new RetainedSelfPtr(_selfPtr), (object)this";
                }
                else if (isInstanceMethod)
                {
                    // For structs, keep 'this' alive and defer SafeHandle release until callback
                    selfInHolder = ", new DeferredSafeHandleRelease(_payload), (object)this";
                }
                else
                {
                    selfInHolder = "";
                }

                csWriter.WriteLines($$"""
            TaskCompletionSource{{(isEmptyTuple ? "" : $"<{_wrapperSignature.ReturnType}>")}} task = new TaskCompletionSource{{(isEmptyTuple ? "" : $"<{_wrapperSignature.ReturnType}>")}}();
            object[] _asyncCallHolder = new object[] { task, {{copyBufferList}}, {{originalParamList}}{{selfInHolder}} };
            GCHandle handle = GCHandle.Alloc(_asyncCallHolder, GCHandleType.Normal);
            """);
            }
            else if (isInstanceMethod)
            {
                // No non-frozen parameters, but still need to keep 'this' alive for instance methods
                // For Swift classes, also retain self to prevent deallocation during async execution
                if (isSwiftClass)
                {
                    // The payload buffer contains a pointer to the class instance - we need to dereference it
                    csWriter.WriteLines($$"""
            TaskCompletionSource{{(isEmptyTuple ? "" : $"<{_wrapperSignature.ReturnType}>")}} task = new TaskCompletionSource{{(isEmptyTuple ? "" : $"<{_wrapperSignature.ReturnType}>")}}();
            IntPtr _selfPtr = *(IntPtr*)_payload.DangerousGetHandle();
            Arc.Retain(_selfPtr);
            object[] _asyncCallHolder = new object[] { task, new RetainedSelfPtr(_selfPtr), (object)this };
            GCHandle handle = GCHandle.Alloc(_asyncCallHolder, GCHandleType.Normal);
            """);
                }
                else
                {
                    // For structs, keep 'this' alive and defer SafeHandle release until callback
                    csWriter.WriteLines($$"""
            TaskCompletionSource{{(isEmptyTuple ? "" : $"<{_wrapperSignature.ReturnType}>")}} task = new TaskCompletionSource{{(isEmptyTuple ? "" : $"<{_wrapperSignature.ReturnType}>")}}();
            object[] _asyncCallHolder = new object[] { task, new DeferredSafeHandleRelease(_payload), (object)this };
            GCHandle handle = GCHandle.Alloc(_asyncCallHolder, GCHandleType.Normal);
            """);
                }
            }
            else
            {
                // Static method with no non-frozen parameters - no holder needed
                csWriter.WriteLines($$"""
            TaskCompletionSource{{(isEmptyTuple ? "" : $"<{_wrapperSignature.ReturnType}>")}} task = new TaskCompletionSource{{(isEmptyTuple ? "" : $"<{_wrapperSignature.ReturnType}>")}}();
            GCHandle handle = GCHandle.Alloc(task, GCHandleType.Normal);
            """);
            }

            // Build parameter string - non-frozen types use UnsafeRawPointer in Swift wrapper
            // For tuple returns, flatten the tuple elements into separate callback parameters
            // because @convention(c) doesn't support Swift tuples
            var returnTypeArg = _env.MethodDecl.CSSignature.First();
            var isTupleReturn = _env.TupleHandler.IsTuple(returnTypeArg.SwiftTypeSpec) &&
                                _env.TupleHandler.IsSupportedTuple((TupleTypeSpec)returnTypeArg.SwiftTypeSpec);

            string callbackParams;
            string callbackResultArgs;
            if (isEmptyTuple)
            {
                callbackParams = "";
                callbackResultArgs = "";
            }
            else if (isTupleReturn)
            {
                // Flatten tuple elements for @convention(c) compatibility
                var tupleTypeSpec = (TupleTypeSpec)returnTypeArg.SwiftTypeSpec;
                var elementTypes = tupleTypeSpec.Elements.Select(e => e.ToString()).ToList();
                callbackParams = string.Join(", ", elementTypes) + ", ";
                // For callback invocation, access tuple elements with .0, .1, etc.
                callbackResultArgs = string.Join(", ", Enumerable.Range(0, tupleTypeSpec.Elements.Count).Select(i => $"result{_env.MethodDecl.Name}.{i}")) + ", ";
            }
            else if (returnTypeArg.IsGeneric)
            {
                callbackParams = _env.MethodDecl.GenericParameters[0].SugaredTypeName + ", ";
                callbackResultArgs = $"result{_env.MethodDecl.Name} as! {_env.MethodDecl.GenericParameters[0].SugaredTypeName}, ";
            }
            else
            {
                callbackParams = returnTypeArg.SwiftTypeSpec + ", ";
                callbackResultArgs = $"result{_env.MethodDecl.Name}, ";
            }

            var baseParams = new[]
            {
                $"callback: @escaping @convention(c) ({callbackParams}Int64) -> Void",
                "errorCallback: @escaping @convention(c) (UnsafePointer<CChar>, Int64) -> Void",
                "task: Int64"
            };

            var methodParams = _env.MethodDecl.CSSignature
                .Skip(1)
                .Select(p =>
                {
                    // Check if this is a non-frozen parameter that needs UnsafeRawPointer
                    if (nonFrozenParams.Any(nfp => nfp.Name == p.Name))
                    {
                        return $"{p.Name}: UnsafeRawPointer";
                    }
                    return $"{p.Name}: {(p.IsGeneric ? _env.MethodDecl.GenericParameters.Find(g => g.TypeName == p.SwiftTypeSpec.ToString())!.SugaredTypeName : p.SwiftTypeSpec)}";
                });

            // For async instance methods on non-singleton classes, add _self: OpaquePointer as explicit parameter
            // Singleton classes use ClassName.shared workaround and don't need _self
            var hasSingletonForParams = (_env.ParentDecl as TypeDecl)?.HasSingletonPattern ?? false;
            var needsSelfParam = isInstanceMethod && _env.MethodDecl.MethodType != MethodType.Static && !hasSingletonForParams;
            var selfParam = needsSelfParam
                ? new[] { "_self: OpaquePointer" }
                : Array.Empty<string>();

            string parameters = string.Join(", ", baseParams.Concat(methodParams).Concat(selfParam));

            var genericParams = _env.MethodDecl.IsGeneric switch
            {
                true => $"<{string.Join(", ", _env.MethodDecl.GenericParameters.Select(p => p.SugaredTypeName))}>",
                false => ""
            };

            var whereClause = (_env.MethodDecl.IsGeneric && _env.MethodDecl.GenericParameters.Any(p => p.GenericConformances.Any() || p.AssosiatedTypeConformances.Any())) switch
            {
                true => " where " + string.Join(
                    ", ",
                    _env.MethodDecl.GenericParameters.Select(p =>
                    {
                        // Build conformances of the form "T : ProtocolName"
                        var genericConformances = p.GenericConformances
                            .Select(gc => $"{p.SugaredTypeName} : {gc.ConformanceTarget.Name}");

                        // Build type conformances of the form "T.AssociatedType == ProtocolName"
                        var typeConformances = p.AssosiatedTypeConformances
                            .Select(tc =>
                                $"{p.SugaredTypeName}.{string.Join(".", tc.Path.Skip(1))} == {tc.ConformanceTarget.Name}"
                            );

                        return string.Join(", ", genericConformances.Concat(typeConformances));
                    })
                ),
                false => ""
            };

            // Generate code to read non-frozen parameters via .pointee
            // C# created proper copies using InitializeWithCopy (handles reference counting).
            // Read non-frozen parameters via .pointee (bitwise copy, doesn't affect ref count).
            // The copy buffer created by C#'s InitializeWithCopy owns a proper reference.
            // C# will call Destroy on the copy buffer after callback completes.
            var readCode = nonFrozenParams.Count > 0
                ? string.Join("\n        ", nonFrozenParams.Select(p =>
                    $"let {p.Name}Value = {p.Name}.assumingMemoryBound(to: {p.SwiftTypeSpec}.self).pointee"))
                : "";

            // Generate argument list for the actual Swift method call
            var methodCallArgs = string.Join(", ", _env.MethodDecl.CSSignature.Skip(1)
                .Select(p =>
                {
                    var argName = p.Name switch
                    {
                        var n when n.StartsWith("arg") => n,
                        var n when n.StartsWith("_") => $"{n.Substring(1)}: {n}",
                        var n => $"{n}: {n}"
                    };

                    // For non-frozen params, use the captured value
                    if (nonFrozenParams.Any(nfp => nfp.Name == p.Name))
                    {
                        var label = p.Name switch
                        {
                            var n when n.StartsWith("arg") => "",
                            var n when n.StartsWith("_") => $"{n.Substring(1)}: ",
                            var n => $"{n}: "
                        };
                        return $"{label}{p.Name}Value";
                    }
                    return argName;
                }));

            var parentTypeName = (_env.ParentDecl as TypeDecl)!.SwiftTypeName;

            // For async instance methods on Swift classes, C# calls Arc.Retain on self before
            // invoking this wrapper, ensuring Swift ARC keeps self alive through the Task closure.
            // The matching Arc.Release is called in the C# callback after async completion.
            var selfComment = (isInstanceMethod && isSwiftClass)
                ? "// selfInstance is safe - C# called Arc.Retain before invoking this method"
                : "";

            // For async instance methods:
            // - If the parent class has a singleton pattern (static 'shared' property), use that
            // - Otherwise, use a free function that receives _self as OpaquePointer
            var isAsyncInstanceMethod = isInstanceMethod && _env.MethodDecl.MethodType != MethodType.Static;
            var hasSingleton = (_env.ParentDecl as TypeDecl)?.HasSingletonPattern ?? false;

            // Determine how to call the method:
            // - Static methods: ClassName.method()
            // - Async instance methods on singleton classes: ClassName.shared.method() (workaround)
            // - Async instance methods on non-singleton classes: __self.method() (convert _self pointer)
            // - Regular instance methods: self.method()
            string selfConversion;
            string methodCallPrefix;
            if (_env.MethodDecl.MethodType == MethodType.Static)
            {
                selfConversion = "";
                methodCallPrefix = $"{parentTypeName.ModuleQualifiedName}.";
            }
            else if (isAsyncInstanceMethod && hasSingleton)
            {
                // Singleton workaround: use ClassName.shared instead of passing self
                // This avoids the SwiftSelf binding issue with @_silgen_name and Task closures
                selfConversion = "";
                methodCallPrefix = $"{parentTypeName.ModuleQualifiedName}.shared.";
            }
            else if (isAsyncInstanceMethod)
            {
                // Non-singleton async instance method: convert _self pointer to type reference
                if (isSwiftClass)
                {
                    // For classes: the pointer IS the object reference, use unsafeBitCast
                    selfConversion = $"let __self = unsafeBitCast(_self, to: {parentTypeName.ModuleQualifiedName}.self)";
                }
                else
                {
                    // For structs: the pointer points TO the struct data, dereference it
                    selfConversion = $"let __self = UnsafePointer<{parentTypeName.ModuleQualifiedName}>(_self).pointee";
                }
                methodCallPrefix = "__self.";
            }
            else
            {
                selfConversion = "";
                methodCallPrefix = "self.";
            }

            // Generate the Swift wrapper
            // For async instance methods, we use a free function to avoid SwiftSelf binding issues
            // For all other methods, we use extension methods
            if (isAsyncInstanceMethod)
            {
                // Free function for async instance methods (marked public to ensure export)
                if (nonFrozenParams.Count > 0)
                {
                    swiftWriter.WriteLine($$"""
            @_silgen_name("{{NameProvider.GetMangledName(_env.MethodDecl)}}")
            public func {{NameProvider.GetPInvokeName(_env.MethodDecl)}}{{genericParams}}({{parameters}}){{whereClause}}{
                // Read non-frozen parameters via .pointee (bitwise copy)
                // C# created copies using InitializeWithCopy (owns a proper reference)
                {{readCode}}
                {{selfConversion}}
                {{selfComment}}

                Task {
                    do {
                        {{(isEmptyTuple ? "" : $"let result{_env.MethodDecl.Name} = ")}}try await {{methodCallPrefix}}{{_env.MethodDecl.Name}}(
                            {{methodCallArgs}}
                        )
                        callback({{callbackResultArgs}}task)
                    } catch {
                        let errorMessage = String(describing: error)
                        errorMessage.withCString { errorCallback($0, task) }
                    }
                }
            }
            """);
                }
                else
                {
                    swiftWriter.WriteLine($$"""
            @_silgen_name("{{NameProvider.GetMangledName(_env.MethodDecl)}}")
            public func {{NameProvider.GetPInvokeName(_env.MethodDecl)}}{{genericParams}}({{parameters}}){{whereClause}}{
                {{selfConversion}}
                {{selfComment}}
                Task {
                    do {
                        {{(isEmptyTuple ? "" : $"let result{_env.MethodDecl.Name} = ")}}try await {{methodCallPrefix}}{{_env.MethodDecl.Name}}(
                            {{methodCallArgs}}
                        )
                        callback({{callbackResultArgs}}task)
                    } catch {
                        let errorMessage = String(describing: error)
                        errorMessage.withCString { errorCallback($0, task) }
                    }
                }
            }
            """);
                }
            }
            else
            {
                // Extension method for static methods and non-async methods
                var staticModifier = _env.MethodDecl.MethodType == MethodType.Static ? "static " : "";
                if (nonFrozenParams.Count > 0)
                {
                    swiftWriter.WriteLine($$"""
            extension {{parentTypeName.ModuleQualifiedName}} {
                @_silgen_name("{{NameProvider.GetMangledName(_env.MethodDecl)}}")
                public {{staticModifier}}func {{NameProvider.GetPInvokeName(_env.MethodDecl)}}{{genericParams}}({{parameters}}){{whereClause}}{
                    // Read non-frozen parameters via .pointee (bitwise copy)
                    // C# created copies using InitializeWithCopy (owns a proper reference)
                    {{readCode}}
                    {{selfComment}}

                    Task {
                        do {
                            {{(isEmptyTuple ? "" : $"let result{_env.MethodDecl.Name} = ")}}try await {{methodCallPrefix}}{{_env.MethodDecl.Name}}(
                                {{methodCallArgs}}
                            )
                            callback({{callbackResultArgs}}task)
                        } catch {
                            let errorMessage = String(describing: error)
                            errorMessage.withCString { errorCallback($0, task) }
                        }
                    }
                }
            }
            """);
                }
                else
                {
                    swiftWriter.WriteLine($$"""
            extension {{parentTypeName.ModuleQualifiedName}} {
                @_silgen_name("{{NameProvider.GetMangledName(_env.MethodDecl)}}")
                public {{staticModifier}}func {{NameProvider.GetPInvokeName(_env.MethodDecl)}}{{genericParams}}({{parameters}}){{whereClause}}{
                    {{selfComment}}
                    Task {
                        do {
                            {{(isEmptyTuple ? "" : $"let result{_env.MethodDecl.Name} = ")}}try await {{methodCallPrefix}}{{_env.MethodDecl.Name}}(
                                {{methodCallArgs}}
                            )
                            callback({{callbackResultArgs}}task)
                        } catch {
                            let errorMessage = String(describing: error)
                            errorMessage.withCString { errorCallback($0, task) }
                        }
                    }
                }
            }
            """);
                }
            }
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

            var text = $$"""
            _payload = new SwiftSafeHandle<{{_env.ParentDecl.Name}}>((IntPtr)NativeMemory.Alloc(_payloadSize));
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
        /// Emits bound generic argument marshalling.
        /// Skips arguments that have type conversion (those are handled by EmitTypeConversions).
        /// </summary>
        private void EmitBoundGenericArguments(CSharpWriter csWriter)
        {
            foreach (var argumentDecl in _env.MethodDecl.CSSignature.Skip(1).Where(_env.BoundGenericsHandler.IsBoundGeneric))
            {
                // Skip if this argument uses type conversion (already handled in EmitTypeConversions)
                // But for property accessors, type conversion is disabled so don't skip
                if (!_env.MethodDecl.IsAccessor && _env.TypeConversionHandler.IsConvertibleType(argumentDecl.SwiftTypeSpec))
                    continue;

                if (_env.BoundGenericsHandler.RequiresBoundGenericMarshalling(argumentDecl))
                {
                    var bufferName = NameProvider.GetBoundGenericBufferName(argumentDecl.Name);
                    csWriter.WriteLine($"using PayloadBuffer<IntPtr> {argumentDecl.Name}Disposable = {argumentDecl.Name}.PayloadBuffer;");
                    csWriter.WriteLine($"IntPtr {bufferName} = {argumentDecl.Name}Disposable.Buffer;");
                }
            }
        }

        /// <summary>
        /// Emits closure argument marshalling.
        /// For @convention(c) closures, converts C# delegates to unmanaged function pointers.
        /// For escaping closures, creates closure data with a thunk and GCHandle context.
        /// For optional closures, handles null by creating a zero-initialized SwiftClosureData.
        /// </summary>
        private void EmitClosureMarshalling(CSharpWriter csWriter)
        {
            foreach (var argumentDecl in _env.MethodDecl.CSSignature.Skip(1).Where(_env.ClosureHandler.IsClosure))
            {
                var closureTypeSpec = _env.ClosureHandler.GetClosureTypeSpec(argumentDecl)!;
                if (!_env.ClosureHandler.IsSupportedClosure(closureTypeSpec))
                    continue;

                bool isOptional = _env.ClosureHandler.IsOptionalClosure(argumentDecl.SwiftTypeSpec);

                if (_env.ClosureHandler.IsConventionC(closureTypeSpec))
                {
                    // For @convention(c) closures, convert delegate to function pointer
                    var funcPtrType = _env.ClosureHandler.GetPInvokeFunctionPointerType(closureTypeSpec);

                    if (isOptional)
                    {
                        // Optional @convention(c) closure - handle null case
                        csWriter.WriteLines($"""
                            var {argumentDecl.Name}FuncPtr = {argumentDecl.Name} != null
                                ? ({funcPtrType})Marshal.GetFunctionPointerForDelegate({argumentDecl.Name})
                                : ({funcPtrType})IntPtr.Zero;
                            """);
                    }
                    else
                    {
                        // Marshal.GetFunctionPointerForDelegate returns IntPtr, cast to the proper function pointer type
                        csWriter.WriteLine($"var {argumentDecl.Name}FuncPtr = ({funcPtrType})Marshal.GetFunctionPointerForDelegate({argumentDecl.Name});");
                    }
                }
                else if (_env.ClosureHandler.IsAsyncThrowingClosure(closureTypeSpec))
                {
                    // Async+throwing closures use a special pattern with AsyncThrowingClosureState
                    // The state holds the user's async delegate, and we pass context + start function to Swift
                    ClosureEmitter.EmitAsyncThrowingClosureMarshallingSetup(
                        csWriter,
                        _env.MethodDecl.Name,
                        argumentDecl.Name,
                        closureTypeSpec,
                        _env.ClosureHandler,
                        _env.MethodDecl.MangledName);
                }
                else if (_env.ClosureHandler.RequiresThunk(closureTypeSpec))
                {
                    // For escaping closures, create a SwiftClosureData struct with thunk pointer and delegate in context
                    var callbackName = ClosureHandler.GetCallbackFunctionName(_env.MethodDecl.Name, argumentDecl.Name, _env.MethodDecl.MangledName);

                    if (isOptional)
                    {
                        // Optional escaping closure - handle null case with zero-initialized SwiftClosureData
                        csWriter.WriteLine($"SwiftClosureData {argumentDecl.Name}Closure;");
                        csWriter.WriteLine($"if ({argumentDecl.Name} != null)");
                        csWriter.WriteLine("{");
                        csWriter.Indent++;
                        csWriter.WriteLine($"{argumentDecl.Name}Handle = GCHandle.Alloc({argumentDecl.Name});");
                        csWriter.WriteLine($"{argumentDecl.Name}Closure = new SwiftClosureData((IntPtr)s_{callbackName}, GCHandle.ToIntPtr({argumentDecl.Name}Handle));");
                        csWriter.Indent--;
                        csWriter.WriteLine("}");
                        csWriter.WriteLine("else");
                        csWriter.WriteLine("{");
                        csWriter.Indent++;
                        csWriter.WriteLine($"{argumentDecl.Name}Closure = default; // Zero-initialized = nil in Swift");
                        csWriter.Indent--;
                        csWriter.WriteLine("}");
                    }
                    else
                    {
                        csWriter.WriteLines($"""
                            {argumentDecl.Name}Handle = GCHandle.Alloc({argumentDecl.Name});
                            var {argumentDecl.Name}Closure = new SwiftClosureData((IntPtr)s_{callbackName}, GCHandle.ToIntPtr({argumentDecl.Name}Handle));
                            """);
                    }
                }
            }
        }

        /// <summary>
        /// Emits type conversions for parameters that use idiomatic .NET types.
        /// Converts string -> SwiftString, IEnumerable&lt;T&gt; -> SwiftArray&lt;T&gt;, T? -> SwiftOptional&lt;T&gt;.
        /// Also handles payload buffer creation for bound generic types that have been type-converted.
        /// </summary>
        private void EmitTypeConversions(CSharpWriter csWriter)
        {
            // Skip type conversions for property accessors
            if (_env.MethodDecl.IsAccessor)
                return;

            foreach (var argumentDecl in _env.MethodDecl.CSSignature.Skip(1))
            {
                if (_env.TypeConversionHandler.IsSwiftString(argumentDecl.SwiftTypeSpec))
                {
                    // string -> SwiftString (using pattern for automatic disposal)
                    csWriter.WriteLine($"using var {argumentDecl.Name}Swift = new SwiftString({argumentDecl.Name});");
                    csWriter.WriteLine($"using PayloadBuffer<SwiftString.Buffer> {argumentDecl.Name}Disposable = {argumentDecl.Name}Swift.PayloadBuffer;");
                }
                else if (_env.TypeConversionHandler.IsSwiftArray(argumentDecl.SwiftTypeSpec))
                {
                    // IEnumerable<T> -> SwiftArray<T>
                    var swiftType = _env.TypeConversionHandler.GetSwiftWrapperType(
                        argumentDecl.SwiftTypeSpec,
                        typeSpec => TranslateTypeSpecForConversion(typeSpec));
                    csWriter.WriteLine($"using var {argumentDecl.Name}Swift = {swiftType}.FromEnumerable({argumentDecl.Name});");
                    // Create payload buffer for P/Invoke (same as bound generic handling)
                    csWriter.WriteLine($"using PayloadBuffer<IntPtr> {argumentDecl.Name}Disposable = {argumentDecl.Name}Swift.PayloadBuffer;");
                    var bufferName = NameProvider.GetBoundGenericBufferName(argumentDecl.Name);
                    csWriter.WriteLine($"IntPtr {bufferName} = {argumentDecl.Name}Disposable.Buffer;");
                }
                else if (_env.TypeConversionHandler.IsSwiftOptional(argumentDecl.SwiftTypeSpec) &&
                         !_env.ClosureHandler.IsOptionalClosure(argumentDecl.SwiftTypeSpec))
                {
                    // T? -> SwiftOptional<T> (but not for optional closures - those are handled by EmitClosureSetup)
                    // Use pattern matching which works for both nullable value types and reference types
                    var swiftType = _env.TypeConversionHandler.GetSwiftWrapperType(
                        argumentDecl.SwiftTypeSpec,
                        typeSpec => TranslateTypeSpecForConversion(typeSpec));
                    csWriter.WriteLine($"using var {argumentDecl.Name}Swift = {argumentDecl.Name} is {{}} {argumentDecl.Name}Value ? {swiftType}.NewSome({argumentDecl.Name}Value) : {swiftType}.NewNone();");
                    // Create payload buffer for P/Invoke (same as bound generic handling)
                    csWriter.WriteLine($"using PayloadBuffer<IntPtr> {argumentDecl.Name}Disposable = {argumentDecl.Name}Swift.PayloadBuffer;");
                    var bufferName = NameProvider.GetBoundGenericBufferName(argumentDecl.Name);
                    csWriter.WriteLine($"IntPtr {bufferName} = {argumentDecl.Name}Disposable.Buffer;");
                }
                else if (_env.TypeConversionHandler.HasNativeTypeRemapping(argumentDecl.SwiftTypeSpec))
                {
                    // Native type remapping: Foundation.NSUrl -> Swift.URL, Foundation.NSData -> Swift.Data
                    var conversion = _env.TypeConversionHandler.GetNativeParameterConversion(argumentDecl.Name, argumentDecl.SwiftTypeSpec);
                    if (conversion != null)
                    {
                        if (_env.TypeConversionHandler.IsFoundationURL(argumentDecl.SwiftTypeSpec))
                        {
                            // URL is non-frozen and requires disposal
                            csWriter.WriteLine($"using var {argumentDecl.Name}Swift = {conversion};");
                        }
                        else
                        {
                            // Data is a frozen struct
                            csWriter.WriteLine($"var {argumentDecl.Name}Swift = {conversion};");
                        }
                    }
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
            if (_env.ExistentialHandler.IsExistential(typeSpec))
            {
                var protocolList = _env.ExistentialHandler.ToProtocolListTypeSpec(typeSpec);
                if (protocolList != null && _env.ExistentialHandler.IsSupportedExistential(protocolList))
                    return _env.ExistentialHandler.GetCSharpExistentialType(protocolList);
                return TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName;
            }

            if (typeSpec is NamedTypeSpec namedTypeSpec)
            {
                var typeRecord = _env.TypeDatabase.GetTypeRecordOrAnyType(namedTypeSpec);

                // If the type falls back to AnyType, don't append generic parameters
                if (typeRecord == TypeDatabaseExtensions.AnyType)
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

        /// <summary>
        /// Emits callback functions and pointers for escaping closures.
        /// </summary>
        private void EmitClosureCallbacks(CSharpWriter csWriter)
        {
            foreach (var argumentDecl in _env.MethodDecl.CSSignature.Skip(1).Where(_env.ClosureHandler.IsClosure))
            {
                var closureTypeSpec = _env.ClosureHandler.GetClosureTypeSpec(argumentDecl)!;
                if (!_env.ClosureHandler.IsSupportedClosure(closureTypeSpec))
                    continue;

                if (_env.ClosureHandler.RequiresThunk(closureTypeSpec))
                {
                    // Check if this is an async+throwing closure (must check before throwing-only)
                    if (_env.ClosureHandler.IsAsyncThrowingClosure(closureTypeSpec))
                    {
                        // Async+throwing closures use a special "start" callback pattern
                        // The start function is synchronous and spawns Task.Run
                        ClosureEmitter.EmitAsyncThrowingClosureCallbackPointer(csWriter, _env.MethodDecl.Name, argumentDecl.Name, _env.MethodDecl.MangledName);
                        ClosureEmitter.EmitAsyncThrowingClosureCallback(csWriter, _env.MethodDecl.Name, argumentDecl.Name, closureTypeSpec, _env.ClosureHandler, _env.MethodDecl.MangledName);
                    }
                    // Check if this is a throwing closure (but not async+throwing)
                    else if (_env.ClosureHandler.IsThrowingClosure(closureTypeSpec))
                    {
                        // Throwing closures need special callback that handles SwiftError
                        ClosureEmitter.EmitThrowingClosureCallbackPointer(csWriter, _env.MethodDecl.Name, argumentDecl.Name, closureTypeSpec, _env.ClosureHandler, _env.MethodDecl.MangledName);
                        ClosureEmitter.EmitThrowingClosureCallback(csWriter, _env.MethodDecl.Name, argumentDecl.Name, closureTypeSpec, _env.ClosureHandler, _env.MethodDecl.MangledName);
                    }
                    // Check if this closure needs indirect return marshalling
                    else if (_env.ClosureHandler.RequiresIndirectReturnMarshalling(closureTypeSpec))
                    {
                        ClosureEmitter.EmitIndirectReturnCallbackPointer(csWriter, _env.MethodDecl.Name, argumentDecl.Name, closureTypeSpec, _env.ClosureHandler, _env.MethodDecl.MangledName);
                        ClosureEmitter.EmitIndirectReturnCallback(csWriter, _env.MethodDecl.Name, argumentDecl.Name, closureTypeSpec, _env.ClosureHandler, _env.MethodDecl.MangledName);
                    }
                    else
                    {
                        ClosureEmitter.EmitClosureCallbackPointer(csWriter, _env.MethodDecl.Name, argumentDecl.Name, closureTypeSpec, _env.ClosureHandler, _env.MethodDecl.MangledName);
                        ClosureEmitter.EmitEscapingClosureCallback(csWriter, _env.MethodDecl.Name, argumentDecl.Name, closureTypeSpec, _env.ClosureHandler, _env.MethodDecl.MangledName);
                    }
                    csWriter.WriteLine();
                }
            }
        }

        /// <summary>
        /// Emits the SafeHandle add reference.
        /// Frozen structs are passed as lowered buffers, so explicit retain is needed.
        /// Non-frozen structs are passed as SafeHandle, so reference counting is managed automatically.
        /// Generics are copied prior to the call via MarshalToSwift, no ref counting is needed on a copy. InitWithCopy is called to create a copy.
        /// </summary>
        private void EmitSafeHandleAddRef(CSharpWriter csWriter)
        {
            if (_env.MethodDecl.MethodType != MethodType.Static && !_env.MethodDecl.IsConstructor)
            {
                if (_env.ParentDecl is StructDecl structDecl)
                {
                    var typeRecord = _env.TypeDatabase.GetTypeRecordOrThrow(structDecl.SwiftTypeName);
                    if (MarshallingHelpers.RequiresMemoryManagement(typeRecord) || !MarshallingHelpers.IsTypeFrozen(typeRecord))
                    {
                        csWriter.WriteLine($"var success = false;");
                        csWriter.WriteLine($"_payload.DangerousAddRef(ref success);");
                    }
                }
                else if (_env.ParentDecl is ClassDecl)
                {
                    // Swift classes always need ref counting - they use _payload SafeHandle
                    csWriter.WriteLine($"var success = false;");
                    csWriter.WriteLine($"_payload.DangerousAddRef(ref success);");
                }
            }

            // For property accessors, don't skip convertible types since type conversion is not applied
            foreach (var argumentDecl in _env.MethodDecl.CSSignature.Skip(1).Where(a => !a.IsGeneric && !_env.BoundGenericsHandler.IsBoundGeneric(a) && !_env.ClosureHandler.IsClosure(a) && !_env.TupleHandler.IsTuple(a) && (_env.MethodDecl.IsAccessor || !_env.TypeConversionHandler.IsConvertibleType(a.SwiftTypeSpec))))
            {
                TypeRecord typeRecord = _env.TypeDatabase.GetTypeRecordOrThrow(argumentDecl.SwiftTypeSpec);

                // ObjC bridged types: extract Handle from .NET iOS binding object
                if (MarshallingHelpers.IsObjCBridged(typeRecord))
                {
                    csWriter.WriteLine($"IntPtr {argumentDecl.Name}Handle = {argumentDecl.Name}?.Handle ?? IntPtr.Zero;");
                    continue;
                }

                if (MarshallingHelpers.IsFrozenStructProjectedAsClass(typeRecord))
                {
                    csWriter.WriteLine($"using PayloadBuffer<{typeRecord.CSharpTypeName}.Buffer> {argumentDecl.Name}Disposable = {argumentDecl.Name}.PayloadBuffer;");
                }
            }

            // NOTE: For async methods, non-frozen parameter copy buffers are created in EmitAsync
            // (before the GCHandle holder) using InitializeWithCopy. The {param}Handle and
            // {param}CopyBuffer variables are already declared there. Nothing more to do here.
        }

        /// <summary>
        /// Emits the SafeHandle release.
        /// Frozen structs are passed as lowered buffers, so explicit release is needed.
        /// Non-frozen structs are passed as SafeHandle, so reference counting is managed automatically.
        /// Generics are copied prior to the call via MarshalToSwift, no ref counting is needed on a copy; Destroy is called on the copy.
        ///
        /// For async instance methods, DangerousRelease is deferred until the async callback fires.
        /// This prevents the SafeHandle from being released while the Swift async Task is still running.
        /// </summary>
        private void EmitSafeHandleRelease(CSharpWriter csWriter)
        {
            // For async instance methods, skip immediate release - the callback will handle it
            // via DeferredSafeHandleRelease stored in the async holder
            if (_env.MethodDecl.IsAsync && _env.MethodDecl.MethodType != MethodType.Static && !_env.MethodDecl.IsConstructor)
            {
                // Async instance methods defer release to callback
                return;
            }

            if (_env.MethodDecl.MethodType != MethodType.Static && !_env.MethodDecl.IsConstructor)
            {
                if (_env.ParentDecl is StructDecl structDecl)
                {
                    var typeRecord = _env.TypeDatabase.GetTypeRecordOrThrow(structDecl.SwiftTypeName);
                    if (MarshallingHelpers.RequiresMemoryManagement(typeRecord) || !MarshallingHelpers.IsTypeFrozen(typeRecord))
                    {
                        csWriter.WriteLine($"if (success)");
                        csWriter.WriteLine($"   _payload.DangerousRelease();");
                    }
                }
                else if (_env.ParentDecl is ClassDecl)
                {
                    // Swift classes always need ref counting - they use _payload SafeHandle
                    csWriter.WriteLine($"if (success)");
                    csWriter.WriteLine($"   _payload.DangerousRelease();");
                }
            }

            foreach (var argumentDecl in _env.MethodDecl.CSSignature.Skip(1))
            {
                if (argumentDecl.IsGeneric)
                {
                    var csTypeParamName = _env.GenericTypeMapping[argumentDecl.SwiftTypeSpec.ToString()].TypeParameter;
                    var metadataName = NameProvider.GetMetadataName(csTypeParamName);
                    var payloadName = NameProvider.GetPayloadName(argumentDecl.Name);
                    csWriter.WriteLine($"{metadataName}.ValueWitnessTable->Destroy((void *){payloadName}, {metadataName});");
                    continue;
                }

                // Free GCHandle for escaping closures
                // Note: Async+throwing closures free their GCHandle inside Task.Run's finally block
                if (_env.ClosureHandler.IsClosure(argumentDecl))
                {
                    var closureTypeSpec = _env.ClosureHandler.GetClosureTypeSpec(argumentDecl)!;
                    if (_env.ClosureHandler.IsSupportedClosure(closureTypeSpec) &&
                        _env.ClosureHandler.RequiresThunk(closureTypeSpec) &&
                        !_env.ClosureHandler.IsAsyncThrowingClosure(closureTypeSpec))
                    {
                        csWriter.WriteLine($"if ({argumentDecl.Name}Handle.IsAllocated) {argumentDecl.Name}Handle.Free();");
                    }
                }
            }

            // NOTE: Async non-frozen parameters are NOT released here.
            // They are kept alive by the GCHandle (in the object[] holder) until the callback fires.
            // This prevents SIGSEGV crashes caused by GC finalizing the parameter while Swift's
            // async Task is still pending and may access copy-on-write shared storage.
        }

        /// <summary>
        /// Emits the generic arguments setup.
        /// </summary>
        private void EmitGenericArguments(CSharpWriter csWriter)
        {
            foreach (var argument in _env.MethodDecl.CSSignature.Skip(1).Where(a => a.IsGeneric))
            {
                var csTypeParamName = _env.GenericTypeMapping[argument.SwiftTypeSpec.ToString()].TypeParameter;
                var metadataName = NameProvider.GetMetadataName(csTypeParamName);
                var payloadName = NameProvider.GetPayloadName(argument.Name);

                var text = $$"""
                Span<byte> {{payloadName}}Span = stackalloc byte[(int){{metadataName}}.Size];
                {{payloadName}} = (IntPtr)Unsafe.AsPointer(ref MemoryMarshal.GetReference({{payloadName}}Span));
                SwiftMarshal.MarshalToSwift({{argument.Name}}, ref {{payloadName}}Span);
                """;
                csWriter.WriteLines(text);
            }
            csWriter.WriteLine();
        }


        private void EmitProtocolWitnessTables(CSharpWriter csWriter)
        {
            foreach (var genericParameter in _env.MethodDecl.GenericParameters)
            {
                var csTypeParamName = _env.GenericTypeMapping[genericParameter.TypeName].TypeParameter;
                var conformances = genericParameter.GenericConformances.OrderBy(c => c.ConformanceTarget.ModuleQualifiedName);
                foreach (var conformance in conformances)
                {
                    // Skip unknown protocols and protocols with associated types
                    // (protocols with associated types generate generic interfaces which can't be used here)
                    if (!IsProtocolAvailableForConstraint(conformance.ConformanceTarget))
                        continue;

                    var pwtName = NameProvider.GetProtocolWitnessTableName(csTypeParamName, conformance.ConformanceTarget.Name);
                    var protocolName = NameProvider.GetInterfaceName(conformance.ConformanceTarget.Name);
                    csWriter.WriteLine($"var {pwtName} = ProtocolWitnessTable.GetOrThrow<{csTypeParamName}, {protocolName}>();");
                }
            }
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
            csWriter.WriteLine($"{returnPrefix}{NameProvider.GetPInvokeName(_env.MethodDecl)}({_pInvokeSignature.CallArgumentsString()});");
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

            var text = $$"""
            if (error.Value != null)
            {
                throw new SwiftRuntimeException("Call to Swift method {{_env.MethodDecl.Name}} failed.");
            }
            """;

            csWriter.WriteLines(text);
            csWriter.WriteLine();
        }

        /// <summary>
        /// Emits the return statement for the constructor.
        /// </summary>
        /// <param name="csWriter">The IndentedTextWriter instance.</param>
        private void EmitReturnConstructor(CSharpWriter csWriter)
        {
            if (_env.ParentDecl is StructDecl structDecl)
            {
                TypeRecord typeRecord = _env.TypeDatabase.GetTypeRecordOrThrow(structDecl.SwiftTypeName);
                if (MarshallingHelpers.IsFrozenStructProjectedAsClass(typeRecord))
                {
                    csWriter.WriteLine($@"
                        unsafe {{
                            IntPtr bufferPtr = (IntPtr)NativeMemory.Alloc((nuint)sizeof({_env.ParentDecl.Name}.Buffer));
                            *({_env.ParentDecl.Name}.Buffer*)bufferPtr = result;
                            _payload = new SwiftSafeHandle<{structDecl.Name}>(bufferPtr);
                        }}");
                    return;
                }
            }
            if (!_requiresIndirectResult)
            {
                csWriter.WriteLine("this = result;");
            }
        }

        /// <summary>
        /// Emits the return statement for the method.
        /// </summary>
        /// <param name="csWriter">The IndentedTextWriter instance.</param>
        private void EmitReturnMethod(CSharpWriter csWriter)
        {
            var returnArg = _env.MethodDecl.CSSignature.First();

            if (_requiresSwiftAsync)
            {
                csWriter.WriteLine("return task.Task;");
                return;
            }

            // Check indirect result first - it takes precedence since the result is stored there.
            // This handles failable initializers (init?) that return SwiftOptional via indirect result.
            if (_requiresIndirectResult)
            {
                // Handle type conversion for indirect result
                if (_env.TypeConversionHandler.IsConvertibleType(returnArg.SwiftTypeSpec))
                {
                    EmitTypeConvertedIndirectReturn(csWriter, returnArg);
                    return;
                }

                // Handle native type remapping for indirect result
                // Skip for property accessors to maintain property/accessor type consistency
                if (!_env.MethodDecl.IsAccessor && _env.TypeConversionHandler.HasNativeTypeRemapping(returnArg.SwiftTypeSpec))
                {
                    var swiftWrapperType = _env.TypeConversionHandler.GetSwiftWrapperTypeForNative(returnArg.SwiftTypeSpec);
                    if (_env.TypeConversionHandler.IsFoundationURL(returnArg.SwiftTypeSpec))
                    {
                        // URL via indirect result - create from handle and convert
                        csWriter.WriteLines($$"""
                            var swiftResult = new {{swiftWrapperType}}(new IntPtr(swiftIndirectResult.Value));
                            return swiftResult.ToNSUrl();
                            """);
                    }
                    else if (_env.TypeConversionHandler.IsFoundationData(returnArg.SwiftTypeSpec))
                    {
                        // Data via indirect result - marshal and convert
                        csWriter.WriteLines($$"""
                            var swiftResult = SwiftMarshal.MarshalFromSwift<{{swiftWrapperType}}>(new IntPtr(swiftIndirectResult.Value));
                            return swiftResult.ToNSData();
                            """);
                    }
                    return;
                }

                csWriter.WriteLine($"return SwiftMarshal.MarshalFromSwift<{_wrapperSignature.ReturnType}>(new IntPtr(swiftIndirectResult.Value));");
                return;
            }

            // Handle type conversion for return values FIRST
            // Skip for property accessors to avoid type mismatch with property declaration
            if (!_env.MethodDecl.IsAccessor && _env.TypeConversionHandler.IsConvertibleType(returnArg.SwiftTypeSpec))
            {
                EmitTypeConvertedReturn(csWriter, returnArg);
                return;
            }

            // Bound generics that return IntPtr directly (not via indirect result)
            if (_env.BoundGenericsHandler.RequiresBoundGenericMarshalling(returnArg))
            {
                csWriter.WriteLine($"return SwiftMarshal.MarshalFromSwift<{_env.BoundGenericsHandler.TranslateBoundGenericTypeToCSharp(returnArg)}>(new IntPtr(&result));");
                return;
            }

            // Handle closure return types - result is SwiftClosureData, wrap in delegate
            if (_env.ClosureHandler.IsClosure(returnArg))
            {
                var closureTypeSpec = _env.ClosureHandler.GetClosureTypeSpec(returnArg)!;
                if (_env.ClosureHandler.IsSupportedClosure(closureTypeSpec))
                {
                    // Throwing closures need special marshalling to handle SwiftError
                    if (_env.ClosureHandler.IsThrowingClosure(closureTypeSpec))
                    {
                        ClosureEmitter.EmitThrowingClosureReturnMarshalling(csWriter, closureTypeSpec, _env.ClosureHandler, "result");
                    }
                    // Use non-frozen struct marshalling if any parameter is a non-frozen struct
                    // (requires heap allocation with NativeMemory and InitializeWithCopy/Destroy)
                    else if (_env.ClosureHandler.RequiresNonFrozenMarshalling(closureTypeSpec))
                    {
                        ClosureEmitter.EmitClosureReturnMarshallingWithNonFrozenParams(csWriter, closureTypeSpec, _env.ClosureHandler, "result");
                    }
                    // Use frozen struct marshalling if any parameter is a frozen struct
                    // (uses stackalloc for stack allocation)
                    else if (_env.ClosureHandler.RequiresStructMarshalling(closureTypeSpec))
                    {
                        ClosureEmitter.EmitClosureReturnMarshallingWithStructParams(csWriter, closureTypeSpec, _env.ClosureHandler, "result");
                    }
                    else
                    {
                        ClosureEmitter.EmitClosureReturnMarshalling(csWriter, closureTypeSpec, _env.ClosureHandler, "result");
                    }
                    return;
                }
            }

            if (!returnArg.IsGeneric)
            {
                var typeRecord = _env.TypeDatabase.GetTypeRecordOrThrow(returnArg.SwiftTypeSpec);

                // ObjC bridged types: wrap IntPtr result with GetNSObject<T>
                if (MarshallingHelpers.IsObjCBridged(typeRecord))
                {
                    csWriter.WriteLine($"return ObjCRuntime.Runtime.GetNSObject<{_wrapperSignature.ReturnType}>(result);");
                    return;
                }

                // Swift classes return pointer directly - allocate buffer and store the pointer
                // The buffer is then managed by SwiftSafeHandle
                if (typeRecord.Kind == TypeRecordKind.Class)
                {
                    csWriter.WriteLines($$"""
                        var classPayload = NativeMemory.Alloc((nuint)sizeof(IntPtr));
                        *(IntPtr*)classPayload = result;
                        return ({{_wrapperSignature.ReturnType}})SwiftMarshal.MarshalFromSwift<{{_wrapperSignature.ReturnType}}>(new IntPtr(classPayload));
                        """);
                    return;
                }

                // Native type remapping: convert Swift type to native .NET type
                // Skip for property accessors to maintain property/accessor type consistency
                if (!_env.MethodDecl.IsAccessor && _env.TypeConversionHandler.HasNativeTypeRemapping(returnArg.SwiftTypeSpec))
                {
                    var swiftWrapperType = _env.TypeConversionHandler.GetSwiftWrapperTypeForNative(returnArg.SwiftTypeSpec);
                    if (_env.TypeConversionHandler.IsFoundationURL(returnArg.SwiftTypeSpec))
                    {
                        // URL is non-frozen, result is IntPtr (SafeHandle marshalling)
                        // Create Swift.URL from handle, then convert to NSUrl
                        csWriter.WriteLines($$"""
                            var swiftResult = new {{swiftWrapperType}}(result);
                            return swiftResult.ToNSUrl();
                            """);
                    }
                    else if (_env.TypeConversionHandler.IsFoundationData(returnArg.SwiftTypeSpec))
                    {
                        // Data is frozen struct, marshal from buffer and convert to NSData
                        csWriter.WriteLines($$"""
                            var swiftResult = SwiftMarshal.MarshalFromSwift<{{swiftWrapperType}}>(new IntPtr(&result));
                            return swiftResult.ToNSData();
                            """);
                    }
                    return;
                }

                if ((typeRecord.Flags & TypeRecordFlags.RequiresMemoryManagement) != 0 && (typeRecord.Flags & TypeRecordFlags.Frozen) != 0)
                {
                    csWriter.WriteLine($$"""
                        unsafe {
                            return SwiftMarshal.MarshalFromSwift<{{_wrapperSignature.ReturnType}}>(new IntPtr(&result));
                        }
                        """);
                    return;
                }
            }

            if (returnArg.SwiftTypeSpec.IsEmptyTuple)
            {
                csWriter.WriteLine("return;");
                return;
            }

            csWriter.WriteLine("return result;");
        }

        /// <summary>
        /// Emits return handling for type-converted return values.
        /// Converts Swift types (SwiftString, SwiftArray, SwiftOptional) to idiomatic .NET types.
        /// </summary>
        private void EmitTypeConvertedReturn(CSharpWriter csWriter, ArgumentDecl returnArg)
        {
            if (_env.TypeConversionHandler.IsSwiftString(returnArg.SwiftTypeSpec))
            {
                // SwiftString.Buffer -> string
                // Marshal from buffer to SwiftString, then convert to string
                csWriter.WriteLines($$"""
                    unsafe {
                        var swiftResult = SwiftMarshal.MarshalFromSwift<SwiftString>(new IntPtr(&result));
                        return swiftResult.ToString();
                    }
                    """);
            }
            else if (_env.TypeConversionHandler.IsSwiftArray(returnArg.SwiftTypeSpec))
            {
                // SwiftArray<T> -> IReadOnlyList<T>
                // SwiftArray already implements IReadOnlyList, so marshal and return directly
                var swiftType = _env.BoundGenericsHandler.TranslateBoundGenericTypeToCSharp(returnArg);
                csWriter.WriteLine($"return SwiftMarshal.MarshalFromSwift<{swiftType}>(new IntPtr(&result));");
            }
            else if (_env.TypeConversionHandler.IsSwiftOptional(returnArg.SwiftTypeSpec))
            {
                // SwiftOptional<T> -> T?
                // Marshal to SwiftOptional, then convert to nullable
                var swiftType = _env.BoundGenericsHandler.TranslateBoundGenericTypeToCSharp(returnArg);
                csWriter.WriteLines($$"""
                    var swiftResult = SwiftMarshal.MarshalFromSwift<{{swiftType}}>(new IntPtr(&result));
                    return swiftResult.ToNullable();
                    """);
            }
        }

        /// <summary>
        /// Emits return handling for type-converted return values via indirect result.
        /// Converts Swift types (SwiftString, SwiftArray, SwiftOptional) to idiomatic .NET types.
        /// </summary>
        private void EmitTypeConvertedIndirectReturn(CSharpWriter csWriter, ArgumentDecl returnArg)
        {
            if (_env.TypeConversionHandler.IsSwiftString(returnArg.SwiftTypeSpec))
            {
                // SwiftString -> string via indirect result
                csWriter.WriteLines($$"""
                    var swiftResult = SwiftMarshal.MarshalFromSwift<SwiftString>(new IntPtr(swiftIndirectResult.Value));
                    return swiftResult.ToString();
                    """);
            }
            else if (_env.TypeConversionHandler.IsSwiftArray(returnArg.SwiftTypeSpec))
            {
                // SwiftArray<T> -> IReadOnlyList<T> via indirect result
                var swiftType = _env.BoundGenericsHandler.TranslateBoundGenericTypeToCSharp(returnArg);
                csWriter.WriteLine($"return SwiftMarshal.MarshalFromSwift<{swiftType}>(new IntPtr(swiftIndirectResult.Value));");
            }
            else if (_env.TypeConversionHandler.IsSwiftOptional(returnArg.SwiftTypeSpec))
            {
                // SwiftOptional<T> -> T? via indirect result
                var swiftType = _env.BoundGenericsHandler.TranslateBoundGenericTypeToCSharp(returnArg);
                csWriter.WriteLines($$"""
                    var swiftResult = SwiftMarshal.MarshalFromSwift<{{swiftType}}>(new IntPtr(swiftIndirectResult.Value));
                    return swiftResult.ToNullable();
                    """);
            }
        }

        /// <summary>
        /// Builds the where clause for generic constraints.
        /// </summary>
        /// <returns>The where clause string, or empty string if no constraints.</returns>
        private string BuildWhereClause()
        {
            if (!_env.MethodDecl.IsGeneric)
                return "";

            var constraints = new List<string>();

            foreach (var param in _env.MethodDecl.GenericParameters)
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

                    var interfaceName = NameProvider.GetInterfaceName(conformance.ConformanceTarget.Name);
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
            var genericParams = _env.MethodDecl.IsGeneric switch
            {
                true => $"<{string.Join(", ", _env.MethodDecl.GenericParameters.Select(p => _env.GenericTypeMapping[p.TypeName].TypeParameter))}>",
                false => ""
            };
            var accessModifier = NameProvider.GetAccessModifier(_env.MethodDecl.Visibility);
            csWriter.WriteLine($"{accessModifier} unsafe {_env.ParentDecl.Name}{genericParams}({_wrapperSignature.ParametersString()})");

            // Emit where clauses for generic constraints
            var whereClause = BuildWhereClause();
            if (!string.IsNullOrEmpty(whereClause))
                csWriter.WriteLines(whereClause);
        }

        /// <summary>
        /// Emits the method signature.
        /// </summary>
        /// <param name="writer">The IndentedTextWriter instance.</param>
        private void EmitSignatureMethod(CSharpWriter csWriter)
        {
            var genericParams = _env.MethodDecl.IsGeneric switch
            {
                true => $"<{string.Join(", ", _env.MethodDecl.GenericParameters.Select(p => _env.GenericTypeMapping[p.TypeName].TypeParameter))}>",
                false => ""
            };

            bool containsBoundGenerics = _env.MethodDecl.CSSignature.Any(_env.BoundGenericsHandler.IsBoundGeneric);

            var staticKeyword = _env.MethodDecl.MethodType == MethodType.Static || _env.ParentDecl is ModuleDecl ? "static " : "";
            var unsafeKeyword = _requiresIndirectResult || _requiresSwiftSelf || _requiresSwiftAsync || _env.MethodDecl.IsGeneric || containsBoundGenerics ? "unsafe " : "";

            var returnType = _wrapperSignature.ReturnType;
            if (_requiresSwiftAsync)
            {
                returnType = $"Task{(_env.MethodDecl.CSSignature.First().SwiftTypeSpec.IsEmptyTuple ? "" : $"<{_wrapperSignature.ReturnType}>")}";
            }

            var accessModifier = NameProvider.GetAccessModifier(_env.MethodDecl.Visibility);
            csWriter.WriteLine($"{accessModifier} {staticKeyword}{unsafeKeyword}{returnType} {_env.CSharpMethodName}{genericParams}({_wrapperSignature.ParametersString()})");

            // Emit where clauses for generic constraints
            var whereClause = BuildWhereClause();
            if (!string.IsNullOrEmpty(whereClause))
                csWriter.WriteLines(whereClause);
        }

        /// <summary>
        /// Emits a wrapper for Swift async method.
        /// </summary>
        /// <param name="writer">The IndentedTextWriter instance.</param>
        private void EmitAsyncWrapper(CSharpWriter csWriter)
        {
            if (!_requiresSwiftAsync)
                return;

            var returnType = _env.MethodDecl.CSSignature.First();
            var voidReturn = returnType.SwiftTypeSpec.IsEmptyTuple;
            var isTupleReturn = _env.TupleHandler.IsTuple(returnType.SwiftTypeSpec) &&
                                _env.TupleHandler.IsSupportedTuple((TupleTypeSpec)returnType.SwiftTypeSpec);

            var callbackFieldName = NameProvider.GetAsyncCallbackFieldName(_env.MethodDecl);
            var callbackMethodName = NameProvider.GetAsyncCallbackMethodName(_env.MethodDecl);
            var errorCallbackFieldName = NameProvider.GetAsyncErrorCallbackFieldName(_env.MethodDecl);
            var errorCallbackMethodName = NameProvider.GetAsyncErrorCallbackMethodName(_env.MethodDecl);

            // For tuple returns, we need to marshal each element individually
            if (isTupleReturn)
            {
                EmitAsyncWrapperForTuple(csWriter, returnType, callbackFieldName, callbackMethodName, errorCallbackFieldName, errorCallbackMethodName);
                return;
            }

            // Non-tuple return handling
            TypeRecord returnTypeRecord = _env.TypeDatabase.GetTypeRecordOrThrow(returnType.SwiftTypeSpec);
            var isObjCBridged = !voidReturn && MarshallingHelpers.IsObjCBridged(returnTypeRecord);

            // Convertible types (SwiftString -> string, SwiftArray -> IReadOnlyList, etc.) are already
            // properly marshalled and don't need InitWithCopy. Using SwiftObjectHelper with their projected
            // types (string, IReadOnlyList<T>) would fail since those types don't implement ISwiftObject.
            var isConvertibleType = _env.TypeConversionHandler.IsConvertibleType(returnType.SwiftTypeSpec);

            // ObjC bridged types and convertible types don't need InitWithCopy
            var requiresInitWithCopy = !voidReturn && !isObjCBridged && !isConvertibleType && (MarshallingHelpers.RequiresMemoryManagement(returnTypeRecord) || returnType.IsGeneric);

            // For ObjC bridged types, the rawResult is the ObjC object pointer directly
            // For Swift types, we need to marshal from Swift memory layout
            string marshalResultCode;
            if (isObjCBridged)
            {
                // ObjC types: rawResult is the ObjC object pointer, wrap with GetNSObject<T>
                marshalResultCode = $"var result = ObjCRuntime.Runtime.GetNSObject<{_wrapperSignature.ReturnType}>(rawResult);";
            }
            else
            {
                marshalResultCode = $"var result = SwiftMarshal.MarshalFromSwift<{_wrapperSignature.ReturnType}>(new IntPtr(&rawResult));";
            }

            var text = $$"""
                        private static unsafe delegate* unmanaged[Cdecl]<{{(voidReturn ? "" : $"{_pInvokeSignature.ReturnType}, ")}}IntPtr, void> {{callbackFieldName}} = &{{callbackMethodName}};
                        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
                        private static void {{callbackMethodName}}({{(voidReturn ? "" : $"{_pInvokeSignature.ReturnType} rawResult, ")}}IntPtr task)
                        {
                            GCHandle handle = GCHandle.FromIntPtr(task);
                            try
                            {
                                {{(voidReturn ? "" : marshalResultCode)}}
                                {{(requiresInitWithCopy ? $"var metadata = SwiftObjectHelper<{_wrapperSignature.ReturnType}>.GetTypeMetadata();" : "")}}
                                {{(requiresInitWithCopy ? $"Span<byte> payloadSpan = stackalloc byte[(int)metadata.Size];" : "")}}
                                {{(requiresInitWithCopy ? $"IntPtr payload = (IntPtr)Unsafe.AsPointer(ref MemoryMarshal.GetReference(payloadSpan));" : "")}}
                                {{(requiresInitWithCopy ? $"SwiftMarshal.MarshalToSwift(result, ref payloadSpan);" : "")}}
                                // Handle both cases: direct TCS or object[] holder (with copy buffer pointers)
                                if (handle.Target is object[] holder && holder[0] is TaskCompletionSource{{(voidReturn ? "" : $"<{_wrapperSignature.ReturnType}>")}} holderTcs)
                                {
                                    // Free copy buffer memory for non-frozen params and release retained self
                                    // Note: Original params in holder keep internal storage alive
                                    for (int i = 1; i < holder.Length; i++)
                                    {
                                        if (holder[i] is RetainedSelfPtr retained && retained.Ptr != IntPtr.Zero)
                                        {
                                            // Release the extra retain added for async safety
                                            Arc.Release(retained.Ptr);
                                        }
                                        else if (holder[i] is DeferredSafeHandleRelease deferred)
                                        {
                                            // Release the SafeHandle that was kept alive for async safety
                                            deferred.Handle.DangerousRelease();
                                        }
                                        else if (holder[i] is CopyBufferWithType copyBuffer && copyBuffer.Buffer != IntPtr.Zero)
                                        {
                                            // Call Destroy to release Swift references, then free buffer
                                            copyBuffer.Metadata.ValueWitnessTable->Destroy((void*)copyBuffer.Buffer, copyBuffer.Metadata);
                                            NativeMemory.Free((void*)copyBuffer.Buffer);
                                        }
                                    }
                                    holderTcs.TrySetResult({{(voidReturn ? "" : "result")}});
                                }
                                else if (handle.Target is TaskCompletionSource{{(voidReturn ? "" : $"<{_wrapperSignature.ReturnType}>")}} directTcs)
                                {
                                    directTcs.TrySetResult({{(voidReturn ? "" : "result")}});
                                }
                            }
                            finally
                            {
                                handle.Free();
                            }
                        }

                        private static unsafe delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void> {{errorCallbackFieldName}} = &{{errorCallbackMethodName}};
                        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
                        private static void {{errorCallbackMethodName}}(IntPtr errorMessagePtr, IntPtr task)
                        {
                            GCHandle handle = GCHandle.FromIntPtr(task);
                            try
                            {
                                var errorMessage = Marshal.PtrToStringUTF8(errorMessagePtr) ?? "Unknown Swift error";
                                var exception = new SwiftException(errorMessage);

                                // Handle both cases: direct TCS or object[] holder (with copy buffer pointers)
                                if (handle.Target is object[] holder && holder[0] is TaskCompletionSource{{(voidReturn ? "" : $"<{_wrapperSignature.ReturnType}>")}} holderTcs)
                                {
                                    // Free copy buffer memory for non-frozen params and release retained self
                                    for (int i = 1; i < holder.Length; i++)
                                    {
                                        if (holder[i] is RetainedSelfPtr retained && retained.Ptr != IntPtr.Zero)
                                        {
                                            Arc.Release(retained.Ptr);
                                        }
                                        else if (holder[i] is DeferredSafeHandleRelease deferred)
                                        {
                                            // Release the SafeHandle that was kept alive for async safety
                                            deferred.Handle.DangerousRelease();
                                        }
                                        else if (holder[i] is CopyBufferWithType copyBuffer && copyBuffer.Buffer != IntPtr.Zero)
                                        {
                                            // Call Destroy to release Swift references, then free buffer
                                            copyBuffer.Metadata.ValueWitnessTable->Destroy((void*)copyBuffer.Buffer, copyBuffer.Metadata);
                                            NativeMemory.Free((void*)copyBuffer.Buffer);
                                        }
                                    }
                                    holderTcs.TrySetException(exception);
                                }
                                else if (handle.Target is TaskCompletionSource{{(voidReturn ? "" : $"<{_wrapperSignature.ReturnType}>")}} directTcs)
                                {
                                    directTcs.TrySetException(exception);
                                }
                            }
                            finally
                            {
                                handle.Free();
                            }
                        }
                """;
            csWriter.WriteLine(text);
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
                var pInvokeType = GetPInvokeTypeForTupleElement(element);
                var csharpType = GetCSharpTypeForTupleElement(element);

                delegateParams.Add(pInvokeType);
                methodParams.Add($"{pInvokeType} {rawParamName}");

                // Determine how to marshal this element
                var marshalCode = GetTupleElementMarshalCode(element, rawParamName, resultName, csharpType);
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
                        private static unsafe delegate* unmanaged[Cdecl]<{{delegateTypeParams}}> {{callbackFieldName}} = &{{callbackMethodName}};
                        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
                        private static void {{callbackMethodName}}({{methodParamList}})
                        {
                            GCHandle handle = GCHandle.FromIntPtr(task);
                            try
                            {
                                {{marshalResultCode}}
                                {{tupleConstruction}}
                                // Handle both cases: direct TCS or object[] holder (with copy buffer pointers)
                                if (handle.Target is object[] holder && holder[0] is TaskCompletionSource<{{_wrapperSignature.ReturnType}}> holderTcs)
                                {
                                    // Free copy buffer memory for non-frozen params and release retained self
                                    for (int i = 1; i < holder.Length; i++)
                                    {
                                        if (holder[i] is RetainedSelfPtr retained && retained.Ptr != IntPtr.Zero)
                                        {
                                            Arc.Release(retained.Ptr);
                                        }
                                        else if (holder[i] is DeferredSafeHandleRelease deferred)
                                        {
                                            // Release the SafeHandle that was kept alive for async safety
                                            deferred.Handle.DangerousRelease();
                                        }
                                        else if (holder[i] is CopyBufferWithType copyBuffer && copyBuffer.Buffer != IntPtr.Zero)
                                        {
                                            // Call Destroy to release Swift references, then free buffer
                                            copyBuffer.Metadata.ValueWitnessTable->Destroy((void*)copyBuffer.Buffer, copyBuffer.Metadata);
                                            NativeMemory.Free((void*)copyBuffer.Buffer);
                                        }
                                    }
                                    holderTcs.TrySetResult(result);
                                }
                                else if (handle.Target is TaskCompletionSource<{{_wrapperSignature.ReturnType}}> directTcs)
                                {
                                    directTcs.TrySetResult(result);
                                }
                            }
                            finally
                            {
                                handle.Free();
                            }
                        }

                        private static unsafe delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void> {{errorCallbackFieldName}} = &{{errorCallbackMethodName}};
                        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
                        private static void {{errorCallbackMethodName}}(IntPtr errorMessagePtr, IntPtr task)
                        {
                            GCHandle handle = GCHandle.FromIntPtr(task);
                            try
                            {
                                var errorMessage = Marshal.PtrToStringUTF8(errorMessagePtr) ?? "Unknown Swift error";
                                var exception = new SwiftException(errorMessage);

                                // Handle both cases: direct TCS or object[] holder (with copy buffer pointers)
                                if (handle.Target is object[] holder && holder[0] is TaskCompletionSource<{{_wrapperSignature.ReturnType}}> holderTcs)
                                {
                                    // Free copy buffer memory for non-frozen params and release retained self
                                    for (int i = 1; i < holder.Length; i++)
                                    {
                                        if (holder[i] is RetainedSelfPtr retained && retained.Ptr != IntPtr.Zero)
                                        {
                                            Arc.Release(retained.Ptr);
                                        }
                                        else if (holder[i] is DeferredSafeHandleRelease deferred)
                                        {
                                            // Release the SafeHandle that was kept alive for async safety
                                            deferred.Handle.DangerousRelease();
                                        }
                                        else if (holder[i] is CopyBufferWithType copyBuffer && copyBuffer.Buffer != IntPtr.Zero)
                                        {
                                            // Call Destroy to release Swift references, then free buffer
                                            copyBuffer.Metadata.ValueWitnessTable->Destroy((void*)copyBuffer.Buffer, copyBuffer.Metadata);
                                            NativeMemory.Free((void*)copyBuffer.Buffer);
                                        }
                                    }
                                    holderTcs.TrySetException(exception);
                                }
                                else if (handle.Target is TaskCompletionSource<{{_wrapperSignature.ReturnType}}> directTcs)
                                {
                                    directTcs.TrySetException(exception);
                                }
                            }
                            finally
                            {
                                handle.Free();
                            }
                        }
                """;
            csWriter.WriteLine(text);
        }

        /// <summary>
        /// Gets the P/Invoke type for a tuple element.
        /// </summary>
        private string GetPInvokeTypeForTupleElement(TypeSpec element)
        {
            // Handle Optional<T> types - check for ObjC bridged inner types
            if (element is NamedTypeSpec namedType && namedType.ContainsGenericParameters)
            {
                var baseTypeName = SwiftTypeName.FromModuleQualifiedName(namedType.Name);
                if (_env.TypeDatabase.TryGetTypeRecord(baseTypeName, out var baseRecord) &&
                    baseRecord.CSharpTypeName.Name == "SwiftOptional" &&
                    namedType.GenericParameters.Count > 0)
                {
                    var innerType = namedType.GenericParameters[0];
                    if (innerType is NamedTypeSpec innerNamed &&
                        _env.TypeDatabase.TryGetTypeRecord(innerNamed, out var innerRecord) &&
                        MarshallingHelpers.IsObjCBridged(innerRecord))
                    {
                        // Optional ObjC type → IntPtr (null is IntPtr.Zero)
                        return "IntPtr";
                    }
                }
                // Other bound generics → void* (opaque pointer)
                return "void*";
            }

            if (element is NamedTypeSpec named)
            {
                var typeRecord = _env.TypeDatabase.GetTypeRecordOrAnyType(named);

                // ObjC bridged types use IntPtr
                if (MarshallingHelpers.IsObjCBridged(typeRecord))
                {
                    return "IntPtr";
                }

                // Non-frozen types needing memory management use Buffer type
                if ((typeRecord.Flags & TypeRecordFlags.RequiresMemoryManagement) != 0 &&
                    (typeRecord.Flags & TypeRecordFlags.Frozen) == 0)
                {
                    return $"{typeRecord.CSharpTypeName.FullyQualifiedName}.Buffer";
                }

                // Frozen types with memory management use Buffer type
                if ((typeRecord.Flags & TypeRecordFlags.RequiresMemoryManagement) != 0 &&
                    (typeRecord.Flags & TypeRecordFlags.Frozen) != 0)
                {
                    return $"{typeRecord.CSharpTypeName.FullyQualifiedName}.Buffer";
                }

                return typeRecord.CSharpTypeName.FullyQualifiedName;
            }

            return "void*";
        }

        /// <summary>
        /// Gets the C# type name for a tuple element.
        /// </summary>
        private string GetCSharpTypeForTupleElement(TypeSpec element)
        {
            // Handle Optional<T> (bound generic with Optional)
            if (element is NamedTypeSpec namedType && namedType.ContainsGenericParameters)
            {
                var baseTypeName = SwiftTypeName.FromModuleQualifiedName(namedType.Name);
                if (_env.TypeDatabase.TryGetTypeRecord(baseTypeName, out var baseRecord))
                {
                    // Recursively translate generic parameters
                    var translatedParams = new List<string>();
                    foreach (var param in namedType.GenericParameters)
                    {
                        translatedParams.Add(GetCSharpTypeForTupleElement(param));
                    }
                    return $"{baseRecord.CSharpTypeName.FullyQualifiedName}<{string.Join(", ", translatedParams)}>";
                }
            }

            if (element is NamedTypeSpec named)
            {
                var typeRecord = _env.TypeDatabase.GetTypeRecordOrAnyType(named);
                return typeRecord.CSharpTypeName.FullyQualifiedName;
            }

            return TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName;
        }

        /// <summary>
        /// Generates marshalling code for a single tuple element.
        /// </summary>
        private string? GetTupleElementMarshalCode(TypeSpec element, string itemName, string resultName, string csharpType)
        {
            // Handle Optional<T> types
            if (element is NamedTypeSpec namedType && namedType.ContainsGenericParameters)
            {
                var baseTypeName = SwiftTypeName.FromModuleQualifiedName(namedType.Name);
                if (_env.TypeDatabase.TryGetTypeRecord(baseTypeName, out var baseRecord) &&
                    baseRecord.CSharpTypeName.Name == "SwiftOptional")
                {
                    // For optional ObjC types, the P/Invoke type is IntPtr
                    // For optional Swift types, it's SwiftOptional<T>.Buffer
                    if (namedType.GenericParameters.Count > 0)
                    {
                        var innerType = namedType.GenericParameters[0];
                        if (innerType is NamedTypeSpec innerNamed &&
                            _env.TypeDatabase.TryGetTypeRecord(innerNamed, out var innerRecord) &&
                            MarshallingHelpers.IsObjCBridged(innerRecord))
                        {
                            // Optional ObjC type: IntPtr -> SwiftOptional<NSObject>
                            // Use factory methods NewNone() and NewSome() since constructors are private
                            var innerCSharp = innerRecord.CSharpTypeName.FullyQualifiedName;
                            return $"var {resultName} = {itemName} == IntPtr.Zero ? Swift.SwiftOptional<{innerCSharp}>.NewNone() : Swift.SwiftOptional<{innerCSharp}>.NewSome(ObjCRuntime.Runtime.GetNSObject<{innerCSharp}>({itemName}));";
                        }
                    }
                    // Non-ObjC optional: marshal from buffer
                    return $"var {resultName} = SwiftMarshal.MarshalFromSwift<{csharpType}>(new IntPtr(&{itemName}));";
                }
            }

            // Handle non-generic types
            if (element is NamedTypeSpec named)
            {
                if (_env.TypeDatabase.TryGetTypeRecord(named, out var typeRecord))
                {
                    // ObjC bridged types
                    if (MarshallingHelpers.IsObjCBridged(typeRecord))
                    {
                        return $"var {resultName} = ObjCRuntime.Runtime.GetNSObject<{csharpType}>({itemName});";
                    }

                    // Primitive types - use directly
                    if (typeRecord.Kind == TypeRecordKind.Struct &&
                        (typeRecord.Flags & TypeRecordFlags.Frozen) != 0 &&
                        (typeRecord.Flags & TypeRecordFlags.RequiresMemoryManagement) == 0)
                    {
                        return $"var {resultName} = {itemName};";
                    }

                    // Frozen structs requiring memory management
                    if (MarshallingHelpers.IsTypeFrozen(typeRecord))
                    {
                        return $"var {resultName} = SwiftMarshal.MarshalFromSwift<{csharpType}>(new IntPtr(&{itemName}));";
                    }

                    // Non-frozen types
                    return $"var {resultName} = SwiftMarshal.MarshalFromSwift<{csharpType}>(new IntPtr(&{itemName}));";
                }
            }

            // Fallback
            return $"var {resultName} = {itemName};";
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

            var structDecl = (StructDecl)_env.ParentDecl;
            csWriter.WriteLine($"fixed ({structDecl.Name}* __self = &this)");
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
    }
}
