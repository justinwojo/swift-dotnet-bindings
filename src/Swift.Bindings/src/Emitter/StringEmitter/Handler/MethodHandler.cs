// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.CodeDom.Compiler;
using Microsoft.Extensions.Logging;

namespace BindingsGeneration
{
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
            var signatureHandler = new SignatureHandler(methodEnv);

            if (methodEnv.MethodDecl.IsGeneric)
            {
                // TODO: This should revert writing the entire struct: https://github.com/dotnet/runtimelab/issues/2890
                _logger.LogWarning($"Constructor {methodEnv.MethodDecl.Name} has unsupported generic parameters");
                return;
            }

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
            "AsyncContext" => $"{modifier} void* {Name}",
            "AsyncTask" => $"{modifier} IntPtr {Name}",
            "IntPtrFromNonFrozen" => $"{modifier} IntPtr {Name}",
            // ObjC bridged types use IntPtr in P/Invoke
            var t when t.StartsWith("ObjCBridged:") => $"{modifier} IntPtr {Name}",
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
                { Type: "AsyncContext" } => "null",
                { Type: "AsyncTask" } => $"GCHandle.ToIntPtr({parameter.Name})",
                { modifier: "out" } => $"out var {parameter.Name}",
                // Handle escaping closures: parameter is SwiftClosureData, variable is {name}Closure
                { Type: "SwiftClosureData" } => $"{parameter.Name}Closure",
                // Handle @convention(c) closure function pointers
                { Type: var type } when type.StartsWith("delegate* unmanaged") =>
                    parameter.Name.EndsWith("FuncPtr") ? parameter.Name : $"{parameter.Name}FuncPtr",
                // ObjC bridged types: extract Handle from the .NET iOS binding object
                { Type: var type } when type.StartsWith("ObjCBridged:") => $"{parameter.Name}Handle",
                _ => parameter.Name
            };
        }
    }

    public class WrapperSignatureBuilder
    {
        private string _returnType = "invalid";
        private readonly List<Parameter> _parameters = new();
        private readonly MethodEnvironment _env;

        public WrapperSignatureBuilder(MethodEnvironment env)
        {
            _env = env;
        }

        /// <summary>
        /// Handles the return type of the method.
        /// </summary>
        public void HandleReturnType()
        {
            var argument = _env.MethodDecl.CSSignature.First();

            if (_env.BoundGenericsHandler.IsBoundGeneric(argument))
            {
                var csTypeParam = _env.BoundGenericsHandler.TranslateBoundGenericTypeToCSharp(argument);
                SetReturnType(csTypeParam);
                return;
            }

            // Handle closure return types
            if (_env.ClosureHandler.IsClosure(argument))
            {
                var closureTypeSpec = _env.ClosureHandler.GetClosureTypeSpec(argument)!;
                if (_env.ClosureHandler.IsSupportedClosure(closureTypeSpec))
                {
                    var delegateType = _env.ClosureHandler.GetCSharpDelegateType(closureTypeSpec);
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
                if (_env.BoundGenericsHandler.IsBoundGeneric(argument))
                {
                    var csTypeParam = _env.BoundGenericsHandler.TranslateBoundGenericTypeToCSharp(argument);
                    AddParameter(csTypeParam, argument.Name);
                    continue;
                }

                // Handle closure arguments
                if (_env.ClosureHandler.IsClosure(argument))
                {
                    var closureTypeSpec = _env.ClosureHandler.GetClosureTypeSpec(argument)!;
                    if (_env.ClosureHandler.IsSupportedClosure(closureTypeSpec))
                    {
                        var delegateType = _env.ClosureHandler.GetCSharpDelegateType(closureTypeSpec);
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

                if (argument.IsGeneric)
                {
                    var csTypeParamName = _env.GenericTypeMapping[argument.SwiftTypeSpec.ToString()].TypeParameter;
                    AddParameter(csTypeParamName, argument.Name);
                }
                else
                {
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
        /// Builds the PInvoke signature.
        /// </summary>
        /// <returns>The PInvoke signature.</returns>
        public Signature Build()
        {
            return new Signature(_returnType, _parameters.ToArray());
        }


        /// <summary>
        /// Sets the return type of the method.
        /// </summary>
        /// <param name="returnType">The return type.</param>
        private void SetReturnType(string returnType)
        {
            _returnType = returnType;
        }

        /// <summary>
        /// Adds a parameter to the PInvoke signature.
        /// </summary>
        /// <param name="type">The parameter type.</param>
        /// <param name="name">The parameter name.</param>s
        private void AddParameter(string type, string name)
        {
            _parameters.Add(new Parameter(type, name));
        }
    }

    /// <summary>
    /// Represents a PInvoke signature builder.
    /// </summary>
    public class PInvokeSignatureBuilder
    {
        private string _returnType = "invalid";
        private readonly List<Parameter> _parameters = new();
        private readonly MethodEnvironment _env;

        /// <summary>
        /// Initializes a new instance of the <see cref="PInvokeSignatureBuilder"/> class.
        /// </summary>
        /// <param name="methodDecl">The method declaration.</param>
        /// <param name="parentDecl">The parent declaration.</param>
        /// <param name="typeDatabase">The type database.</param>
        public PInvokeSignatureBuilder(MethodEnvironment env)
        {
            _env = env;
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

            // Handle closure return types - Swift returns closures as SwiftClosureData (function + context pointers)
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
                // Our Swift wrapper expects: callback, task (handle), then method arguments
                // No context parameter needed - we handle the callback in Swift
                AddParameter("AsyncCallback", NameProvider.GetAsyncCallbackFieldName(_env.MethodDecl));
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

                // Handle closure arguments
                if (_env.ClosureHandler.IsClosure(argument))
                {
                    var closureTypeSpec = _env.ClosureHandler.GetClosureTypeSpec(argument)!;
                    if (_env.ClosureHandler.IsSupportedClosure(closureTypeSpec))
                    {
                        if (_env.ClosureHandler.RequiresThunk(closureTypeSpec))
                        {
                            // Escaping closures are passed as a single SwiftClosureData struct
                            // containing both function pointer and context
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
                    var pwtName = NameProvider.GetProtocolWitnessTableName(_env.GenericTypeMapping[genericParameter.TypeName].TypeParameter, conformance.ConformanceTarget.Name);
                    AddParameter("ProtocolWitnessTable", pwtName);
                }
            }
        }

        /// <summary>
        /// Handles the SwiftSelf parameter of the method.
        /// </summary>
        public void HandleSwiftSelf()
        {
            // Async methods call our generated Swift wrapper which handles self implicitly
            if (_env.MethodDecl.IsAsync)
                return;

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
                            AddParameter($"SwiftSelf<Buffer>", "self");
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
        /// Builds the PInvoke signature.
        /// </summary>
        /// <returns>The PInvoke signature.</returns>
        public Signature Build()
        {
            return new Signature(_returnType, _parameters.ToArray());
        }

        /// <summary>
        /// Sets the return type of the method.
        /// </summary>
        /// <param name="returnType">The return type.</param>
        private void SetReturnType(string returnType)
        {
            _returnType = returnType;
        }

        /// <summary>
        /// Adds a parameter to the PInvoke signature.
        /// </summary>
        /// <param name="type">The parameter type.</param>
        /// <param name="name">The parameter name.</param>s
        private void AddParameter(string type, string name, string modifier = "")
        {
            _parameters.Add(new Parameter(type, name, modifier));
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
        /// Emits the PInvoke signature.
        /// </summary>
        /// <param name="csWriter">The IndentedTextWriter instance.</param>
        /// <param name="methodEnv">The method environment.</param>
        public static void EmitPInvoke(CSharpWriter csWriter, MethodEnvironment methodEnv, SignatureHandler signatureHandler)
        {
            var methodDecl = (MethodDecl)methodEnv.MethodDecl;
            var moduleDecl = methodDecl.ModuleDecl ?? throw new ArgumentNullException(nameof(methodDecl.ModuleDecl));

            var pInvokeName = NameProvider.GetPInvokeName(methodDecl);
            // Async methods use generated Swift wrappers in SwiftBindings library
            var libPath = methodDecl.IsAsync
                ? "SwiftBindings"
                : methodEnv.TypeDatabase.GetLibraryPath(moduleDecl.Name);

            var pInvokeSignature = signatureHandler.GetPInvokeSignature();

            csWriter.WriteLine("[UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]");
            csWriter.WriteLine($"[DllImport(\"{libPath}\", EntryPoint = \"{NameProvider.GetMangledName(methodDecl)}\")]");
            csWriter.WriteLine($"private static extern {(methodDecl.IsAsync ? "void" : pInvokeSignature.ReturnType)} {pInvokeName}({pInvokeSignature.ParametersString()});");
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
            // Async methods call our generated Swift wrapper which handles self/error internally
            _requiresSwiftSelf = !_requiresSwiftAsync && MarshallingHelpers.MethodRequiresSwiftSelf(methodEnv);
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
            EmitSignatureConstructor(csWriter);
            EmitBodyStart(csWriter);
            EmitSafeHandleAddRef(csWriter);
            EmitSwiftSelf(csWriter);
            EmitBoundGenericArguments(csWriter);
            EmitIndirectResultConstructor(csWriter);
            EmitPInvokeCall(csWriter);
            EmitSwiftError(csWriter);
            EmitReturnConstructor(csWriter);
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

            // Declare GCHandle variables for escaping closures
            foreach (var argument in _env.MethodDecl.CSSignature.Skip(1).Where(_env.ClosureHandler.IsClosure))
            {
                var closureTypeSpec = _env.ClosureHandler.GetClosureTypeSpec(argument)!;
                if (_env.ClosureHandler.IsSupportedClosure(closureTypeSpec) && _env.ClosureHandler.RequiresThunk(closureTypeSpec))
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

            // Frozen struct setters use a fixed block to get a pointer to 'this'
            if (_requiresFixedBlock)
            {
                csWriter.WriteLine("var self = new SwiftSelf(__self);");
            }
            else if (_env.ParentDecl is StructDecl structDecl && structDecl.IsFrozen)
            {
                var typeRecord = _env.TypeDatabase.GetTypeRecordOrThrow(structDecl.SwiftTypeName);
                if ((typeRecord.Flags & TypeRecordFlags.RequiresMemoryManagement) != 0)
                    csWriter.WriteLine($"var self = new SwiftSelf<{structDecl.Name}.Buffer>(*({structDecl.Name}.Buffer*)_payload.DangerousGetHandle());");
                else
                    csWriter.WriteLine($"var self = new SwiftSelf<{structDecl.Name}>(this);");
            }
            else
            {
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

            csWriter.WriteLines($$"""
            TaskCompletionSource{{(isEmptyTuple ? "" : $"<{_wrapperSignature.ReturnType}>")}} task = new TaskCompletionSource{{(isEmptyTuple ? "" : $"<{_wrapperSignature.ReturnType}>")}}();
            GCHandle handle = GCHandle.Alloc(task, GCHandleType.Normal);
            """);

            // Identify non-frozen parameters that need special Swift wrapper handling
            var nonFrozenParams = _env.MethodDecl.CSSignature
                .Skip(1)
                .Where(p => !p.IsGeneric && !_env.BoundGenericsHandler.IsBoundGeneric(p) && !_env.ClosureHandler.IsClosure(p) && !_env.TupleHandler.IsTuple(p))
                .Where(p =>
                {
                    var typeRecord = _env.TypeDatabase.GetTypeRecordOrThrow(p.SwiftTypeSpec);
                    return !MarshallingHelpers.IsTypeFrozen(typeRecord);
                })
                .ToList();

            // Build parameter string - non-frozen types use UnsafeRawPointer in Swift wrapper
            string parameters = string.Join(
                ", ",
                new[]
                {
                    $"callback: @escaping ({(isEmptyTuple ? "" : $"{(_env.MethodDecl.CSSignature.First().IsGeneric ? _env.MethodDecl.GenericParameters[0].SugaredTypeName : _env.MethodDecl.CSSignature.First().SwiftTypeSpec)}, ")}Int64) -> Void",
                    "task: Int64"
                }.Concat(
                    _env.MethodDecl.CSSignature
                        .Skip(1)
                        .Select(p =>
                        {
                            // Check if this is a non-frozen parameter that needs UnsafeRawPointer
                            if (nonFrozenParams.Any(nfp => nfp.Name == p.Name))
                            {
                                return $"{p.Name}: UnsafeRawPointer";
                            }
                            return $"{p.Name}: {(p.IsGeneric ? _env.MethodDecl.GenericParameters.Find(g => g.TypeName == p.SwiftTypeSpec.ToString())!.SugaredTypeName : p.SwiftTypeSpec)}";
                        })
                )
            );

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

            // Generate copy allocation code for non-frozen parameters (before Task block)
            var copyAllocCode = string.Join("\n        ", nonFrozenParams.Select(p =>
                $"let {p.Name}Copy = UnsafeMutablePointer<{p.SwiftTypeSpec}>.allocate(capacity: 1)\n        " +
                $"{p.Name}Copy.initialize(from: {p.Name}.assumingMemoryBound(to: {p.SwiftTypeSpec}.self), count: 1)"));

            // Generate defer cleanup code for non-frozen parameters (inside Task block)
            var deferCleanupCode = nonFrozenParams.Count > 0
                ? "defer {\n                    " +
                  string.Join("\n                    ", nonFrozenParams.Select(p =>
                      $"{p.Name}Copy.deinitialize(count: 1)\n                    {p.Name}Copy.deallocate()")) +
                  "\n                }"
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

                    // For non-frozen params, use the copy's pointee
                    if (nonFrozenParams.Any(nfp => nfp.Name == p.Name))
                    {
                        var label = p.Name switch
                        {
                            var n when n.StartsWith("arg") => "",
                            var n when n.StartsWith("_") => $"{n.Substring(1)}: ",
                            var n => $"{n}: "
                        };
                        return $"{label}{p.Name}Copy.pointee";
                    }
                    return argName;
                }));

            var parentTypeName = (_env.ParentDecl as TypeDecl)!.SwiftTypeName;

            // Generate the Swift wrapper with proper non-frozen parameter handling
            if (nonFrozenParams.Count > 0)
            {
                swiftWriter.WriteLine($$"""
            extension {{parentTypeName.ModuleQualifiedName}} {
                @_silgen_name("{{NameProvider.GetMangledName(_env.MethodDecl)}}")
                public {{(_env.MethodDecl.MethodType == MethodType.Static ? "static " : "")}} func {{NameProvider.GetPInvokeName(_env.MethodDecl)}}{{genericParams}}({{parameters}}){{whereClause}}{
                    // Properly copy non-frozen parameters with reference counting
                    {{copyAllocCode}}

                    Task {
                        {{deferCleanupCode}}
                        {{(isEmptyTuple ? "" : $"let result{_env.MethodDecl.Name} = ")}}try! await {{(_env.MethodDecl.MethodType == MethodType.Static ? $"{parentTypeName.ModuleQualifiedName}." : "")}}{{_env.MethodDecl.Name}}(
                            {{methodCallArgs}}
                        )
                        callback({{(isEmptyTuple ? "" : $"result{_env.MethodDecl.Name}{(_env.MethodDecl.CSSignature.First().IsGeneric ? $" as! {_env.MethodDecl.GenericParameters[0].SugaredTypeName}" : "")}, ")}}task);
                    }
                }
            }
            """);
            }
            else
            {
                // No non-frozen parameters - use original simpler code
                swiftWriter.WriteLine($$"""
            extension {{parentTypeName.ModuleQualifiedName}} {
                @_silgen_name("{{NameProvider.GetMangledName(_env.MethodDecl)}}")
                public {{(_env.MethodDecl.MethodType == MethodType.Static ? "static " : "")}} func {{NameProvider.GetPInvokeName(_env.MethodDecl)}}{{genericParams}}({{parameters}}){{whereClause}}{
                    Task {
                        {{(isEmptyTuple ? "" : $"let result{_env.MethodDecl.Name} = ")}}try! await {{(_env.MethodDecl.MethodType == MethodType.Static ? $"{parentTypeName.ModuleQualifiedName}." : "")}}{{_env.MethodDecl.Name}}(
                            {{methodCallArgs}}
                        )
                        callback({{(isEmptyTuple ? "" : $"result{_env.MethodDecl.Name}{(_env.MethodDecl.CSSignature.First().IsGeneric ? $" as! {_env.MethodDecl.GenericParameters[0].SugaredTypeName}" : "")}, ")}}task);
                    }
                }
            }
            """);
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
        /// </summary>
        private void EmitBoundGenericArguments(CSharpWriter csWriter)
        {
            foreach (var argumentDecl in _env.MethodDecl.CSSignature.Skip(1).Where(_env.BoundGenericsHandler.IsBoundGeneric))
            {
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
        /// </summary>
        private void EmitClosureMarshalling(CSharpWriter csWriter)
        {
            foreach (var argumentDecl in _env.MethodDecl.CSSignature.Skip(1).Where(_env.ClosureHandler.IsClosure))
            {
                var closureTypeSpec = _env.ClosureHandler.GetClosureTypeSpec(argumentDecl)!;
                if (!_env.ClosureHandler.IsSupportedClosure(closureTypeSpec))
                    continue;

                if (_env.ClosureHandler.IsConventionC(closureTypeSpec))
                {
                    // For @convention(c) closures, convert delegate to function pointer
                    var funcPtrType = _env.ClosureHandler.GetPInvokeFunctionPointerType(closureTypeSpec);

                    // Marshal.GetFunctionPointerForDelegate returns IntPtr, cast to the proper function pointer type
                    csWriter.WriteLine($"var {argumentDecl.Name}FuncPtr = ({funcPtrType})Marshal.GetFunctionPointerForDelegate({argumentDecl.Name});");
                }
                else if (_env.ClosureHandler.RequiresThunk(closureTypeSpec))
                {
                    // For escaping closures, create a SwiftClosureData struct with thunk pointer and delegate in context
                    var callbackName = ClosureHandler.GetCallbackFunctionName(_env.MethodDecl.Name, argumentDecl.Name);
                    csWriter.WriteLines($"""
                        {argumentDecl.Name}Handle = GCHandle.Alloc({argumentDecl.Name});
                        var {argumentDecl.Name}Closure = new SwiftClosureData((IntPtr)s_{callbackName}, GCHandle.ToIntPtr({argumentDecl.Name}Handle));
                        """);
                }
            }
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
                    ClosureEmitter.EmitClosureCallbackPointer(csWriter, _env.MethodDecl.Name, argumentDecl.Name, closureTypeSpec, _env.ClosureHandler);
                    ClosureEmitter.EmitEscapingClosureCallback(csWriter, _env.MethodDecl.Name, argumentDecl.Name, closureTypeSpec, _env.ClosureHandler);
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
            }

            foreach (var argumentDecl in _env.MethodDecl.CSSignature.Skip(1).Where(a => !a.IsGeneric && !_env.BoundGenericsHandler.IsBoundGeneric(a) && !_env.ClosureHandler.IsClosure(a) && !_env.TupleHandler.IsTuple(a)))
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

            // For async methods, non-frozen parameters need explicit lifetime management
            // since we pass IntPtr instead of SafeHandle to the P/Invoke
            if (_env.MethodDecl.IsAsync)
            {
                foreach (var argumentDecl in _env.MethodDecl.CSSignature.Skip(1).Where(a =>
                    !a.IsGeneric &&
                    !_env.BoundGenericsHandler.IsBoundGeneric(a) &&
                    !_env.ClosureHandler.IsClosure(a) &&
                    !_env.TupleHandler.IsTuple(a)))
                {
                    TypeRecord typeRecord = _env.TypeDatabase.GetTypeRecordOrThrow(argumentDecl.SwiftTypeSpec);
                    // Skip ObjC bridged types - they're already handled above
                    if (MarshallingHelpers.IsObjCBridged(typeRecord))
                        continue;
                    if (!MarshallingHelpers.IsTypeFrozen(typeRecord))
                    {
                        csWriter.WriteLine($"bool {argumentDecl.Name}Success = false;");
                        csWriter.WriteLine($"{argumentDecl.Name}.Payload.DangerousAddRef(ref {argumentDecl.Name}Success);");
                        csWriter.WriteLine($"IntPtr {argumentDecl.Name}Handle = {argumentDecl.Name}.Payload.DangerousGetHandle();");
                    }
                }
            }
        }

        /// <summary>
        /// Emits the SafeHandle release.
        /// Frozen structs are passed as lowered buffers, so explicit release is needed.
        /// Non-frozen structs are passed as SafeHandle, so reference counting is managed automatically.
        /// Generics are copied prior to the call via MarshalToSwift, no ref counting is needed on a copy; Destroy is called on the copy.
        /// </summary>
        private void EmitSafeHandleRelease(CSharpWriter csWriter)
        {
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
                if (_env.ClosureHandler.IsClosure(argumentDecl))
                {
                    var closureTypeSpec = _env.ClosureHandler.GetClosureTypeSpec(argumentDecl)!;
                    if (_env.ClosureHandler.IsSupportedClosure(closureTypeSpec) && _env.ClosureHandler.RequiresThunk(closureTypeSpec))
                    {
                        csWriter.WriteLine($"if ({argumentDecl.Name}Handle.IsAllocated) {argumentDecl.Name}Handle.Free();");
                    }
                }
            }

            // Release refs for async non-frozen parameters
            if (_env.MethodDecl.IsAsync)
            {
                foreach (var argumentDecl in _env.MethodDecl.CSSignature.Skip(1).Where(a =>
                    !a.IsGeneric &&
                    !_env.BoundGenericsHandler.IsBoundGeneric(a) &&
                    !_env.ClosureHandler.IsClosure(a) &&
                    !_env.TupleHandler.IsTuple(a)))
                {
                    TypeRecord typeRecord = _env.TypeDatabase.GetTypeRecordOrThrow(argumentDecl.SwiftTypeSpec);
                    if (!MarshallingHelpers.IsTypeFrozen(typeRecord))
                    {
                        csWriter.WriteLine($"if ({argumentDecl.Name}Success)");
                        csWriter.WriteLine($"    {argumentDecl.Name}.Payload.DangerousRelease();");
                    }
                }
            }
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

            // TODO: Replace with correct method name
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
                csWriter.WriteLine($"return SwiftMarshal.MarshalFromSwift<{_wrapperSignature.ReturnType}>(new IntPtr(swiftIndirectResult.Value));");
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
                    ClosureEmitter.EmitClosureReturnMarshalling(csWriter, closureTypeSpec, _env.ClosureHandler, "result");
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
            TypeRecord returnTypeRecord = _env.TypeDatabase.GetTypeRecordOrThrow(returnType.SwiftTypeSpec);
            var voidReturn = returnType.SwiftTypeSpec.IsEmptyTuple;
            var requiresInitWithCopy = !voidReturn && (MarshallingHelpers.RequiresMemoryManagement(returnTypeRecord) || returnType.IsGeneric);

            var marshallFromSwiftArgument = "new IntPtr(&rawResult)";

            var callbackFieldName = NameProvider.GetAsyncCallbackFieldName(_env.MethodDecl);
            var callbackMethodName = NameProvider.GetAsyncCallbackMethodName(_env.MethodDecl);
            var text = $$"""
                        private static unsafe delegate* unmanaged[Cdecl]<{{(voidReturn ? "" : $"{_pInvokeSignature.ReturnType}, ")}}IntPtr, void> {{callbackFieldName}} = &{{callbackMethodName}};
                        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
                        private static void {{callbackMethodName}}({{(voidReturn ? "" : $"{_pInvokeSignature.ReturnType} rawResult, ")}}IntPtr task)
                        {
                            GCHandle handle = GCHandle.FromIntPtr(task);
                            try
                            {
                                {{(voidReturn ? "" : $"var result = SwiftMarshal.MarshalFromSwift<{_wrapperSignature.ReturnType}>({marshallFromSwiftArgument});")}}
                                {{(requiresInitWithCopy ? $"var metadata = SwiftObjectHelper<{_wrapperSignature.ReturnType}>.GetTypeMetadata();" : "")}}
                                {{(requiresInitWithCopy ? $"Span<byte> payloadSpan = stackalloc byte[(int)metadata.Size];" : "")}}
                                {{(requiresInitWithCopy ? $"IntPtr payload = (IntPtr)Unsafe.AsPointer(ref MemoryMarshal.GetReference(payloadSpan));" : "")}}
                                {{(requiresInitWithCopy ? $"SwiftMarshal.MarshalToSwift(result, ref payloadSpan);" : "")}}
                                if (handle.Target is TaskCompletionSource{{(voidReturn ? "" : $"<{_wrapperSignature.ReturnType}>")}} tcs)
                                {
                                    tcs.TrySetResult({{(voidReturn ? "" : "result")}});
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
