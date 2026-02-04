// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.CodeDom.Compiler;
using Microsoft.Extensions.Logging;
using Swift.Runtime;

namespace BindingsGeneration
{
    /// <summary>
    /// Factory class for creating instances of ProtocolHandler.
    /// </summary>
    public class ProtocolHandlerFactory : HandlerFactory, IFactory<BaseDecl, ITypeHandler>
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ProtocolHandlerFactory"/> class.
        /// </summary>
        /// <param name="loggerFactory">The logger factory instance.</param>
        public ProtocolHandlerFactory(ILoggerFactory loggerFactory) : base(loggerFactory.CreateLogger<ProtocolHandler>())
        {
        }

        /// <summary>
        /// Determines if the factory handles the specified declaration.
        /// </summary>
        /// <param name="decl">The base declaration.</param>
        public bool Handles(BaseDecl decl)
        {
            return decl is ProtocolDecl;
        }

        /// <summary>
        /// Constructs a new instance of ProtocolHandler.
        /// </summary>
        public ITypeHandler Construct()
        {
            return new ProtocolHandler(_handlerLogger);
        }
    }

    /// <summary>
    /// Handler class for protocol declarations.
    /// </summary>
    public class ProtocolHandler : BaseHandler, ITypeHandler
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ProtocolHandler"/> class.
        /// </summary>
        /// <param name="logger">The logger instance.</param>
        public ProtocolHandler(ILogger logger) : base(logger)
        {
        }

        /// <inheritdoc/>
        public IEnvironment Marshal(BaseDecl baseDecl, ITypeDatabase typeDatabase)
        {
            if (baseDecl is not ProtocolDecl protocolDecl)
            {
                throw new ArgumentException("The provided decl must be a ProtocolDecl.", nameof(baseDecl));
            }
            return new TypeEnvironment(protocolDecl, typeDatabase);
        }

        /// <inheritdoc/>
        public void Emit(CSharpWriter csWriter, SwiftWriter swiftWriter, IEnvironment env, Conductor conductor)
        {
            var protocolEnv = (TypeEnvironment)env;
            var protocolDecl = (ProtocolDecl)protocolEnv.TypeDecl;
            ReportCollector.RecordTypeEmitted(protocolDecl);

            var interfaceName = GetInterfaceNameWithGenerics(protocolDecl);
            var inheritedInterfaces = GetInheritedInterfaceList(protocolDecl);

            // Write the interface declaration
            if (inheritedInterfaces.Count > 0)
            {
                csWriter.WriteLine($"public interface {interfaceName} : {string.Join(", ", inheritedInterfaces)}");
            }
            else
            {
                csWriter.WriteLine($"public interface {interfaceName}");
            }
            csWriter.WriteLine("{");
            csWriter.Indent++;

            // Track emitted members to avoid duplicates
            var emittedProperties = new HashSet<string>();
            var emittedMethods = new HashSet<string>();
            var emittedSubscripts = new HashSet<string>();

            // Emit properties as interface members
            foreach (var propertyDecl in protocolDecl.Properties)
            {
                // Skip static properties - C# interfaces cannot have static members as requirements
                // Note: Static properties are still emitted on conforming types, just not in the interface
                if (propertyDecl.IsStatic)
                {
                    _logger.LogDebug($"Skipping static property '{propertyDecl.Name}' in interface {protocolDecl.Name} - static interface members are not supported.");
                    ReportCollector.RecordMemberSkipped(BindingItemKind.Property, propertyDecl.Name, protocolDecl, SkipReason.StaticProtocolMember, "Static protocol members cannot be declared in C# interfaces.");
                    continue;
                }

                // Create a unique key for the property (name is sufficient since properties can't be overloaded)
                var propertyKey = propertyDecl.Name;
                if (emittedProperties.Contains(propertyKey))
                {
                    _logger.LogDebug($"Skipping duplicate property '{propertyDecl.Name}' in interface {protocolDecl.Name}");
                    ReportCollector.RecordMemberSkipped(BindingItemKind.Property, propertyDecl.Name, protocolDecl, SkipReason.DuplicateSignature, "Duplicate protocol property signature.");
                    continue;
                }
                emittedProperties.Add(propertyKey);
                EmitInterfaceProperty(csWriter, propertyDecl, env.TypeDatabase, protocolDecl);
                ReportCollector.RecordMemberEmitted(BindingItemKind.Property, propertyDecl.Name, protocolDecl);
            }

            // Emit subscripts as interface indexers
            foreach (var subscriptDecl in protocolDecl.Subscripts)
            {
                // Skip static subscripts - C# interfaces cannot have static members as requirements
                // Note: Static subscripts are still emitted on conforming types, just not in the interface
                if (subscriptDecl.IsStatic)
                {
                    _logger.LogDebug($"Skipping static subscript in interface {protocolDecl.Name} - static interface members are not supported.");
                    ReportCollector.RecordMemberSkipped(BindingItemKind.Subscript, "subscript", protocolDecl, SkipReason.StaticProtocolMember, "Static protocol members cannot be declared in C# interfaces.");
                    continue;
                }

                // Create a unique key for the subscript based on index parameter types
                var subscriptKey = GetSubscriptSignatureKey(subscriptDecl, env.TypeDatabase, protocolDecl);
                if (emittedSubscripts.Contains(subscriptKey))
                {
                    _logger.LogDebug($"Skipping duplicate subscript in interface {protocolDecl.Name}");
                    ReportCollector.RecordMemberSkipped(BindingItemKind.Subscript, "subscript", protocolDecl, SkipReason.DuplicateSignature, "Duplicate protocol subscript signature.");
                    continue;
                }
                emittedSubscripts.Add(subscriptKey);
                EmitInterfaceSubscript(csWriter, subscriptDecl, env.TypeDatabase, protocolDecl);
                ReportCollector.RecordMemberEmitted(BindingItemKind.Subscript, "subscript", protocolDecl);
            }

            // Emit methods as interface members
            foreach (var methodDecl in protocolDecl.Methods)
            {
                // Create a unique key for the method (name + parameter types)
                var methodKey = GetMethodSignatureKey(methodDecl, env.TypeDatabase, protocolDecl);
                if (emittedMethods.Contains(methodKey))
                {
                    _logger.LogDebug($"Skipping duplicate method '{methodDecl.Name}' in interface {protocolDecl.Name}");
                    ReportCollector.RecordMemberSkipped(BindingItemKind.Method, methodDecl.Name, protocolDecl, SkipReason.DuplicateSignature, "Duplicate protocol method signature.");
                    continue;
                }
                emittedMethods.Add(methodKey);
                EmitInterfaceMethod(csWriter, methodDecl, env.TypeDatabase, protocolDecl);
                ReportCollector.RecordMemberEmitted(BindingItemKind.Method, methodDecl.Name, protocolDecl);
            }

            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.WriteLine();

            // Emit the proxy class that enables C# implementations of this protocol
            EmitProtocolProxy(csWriter, protocolDecl, env.TypeDatabase);
        }

        /// <summary>
        /// Emits a proxy class that enables C# code to implement this protocol.
        /// The proxy wraps either a C# implementation or a Swift existential container.
        /// </summary>
        private void EmitProtocolProxy(CSharpWriter csWriter, ProtocolDecl protocolDecl, ITypeDatabase typeDatabase)
        {
            var moduleName = protocolDecl.ModuleDecl?.Name ?? "Swift";
            var proxyEmitter = new ProtocolProxyEmitter(typeDatabase, _logger, moduleName);
            proxyEmitter.EmitProxyClass(csWriter, protocolDecl);
        }

        /// <summary>
        /// Creates a unique signature key for a method based on name and parameter types.
        /// </summary>
        private string GetMethodSignatureKey(MethodDecl methodDecl, ITypeDatabase typeDatabase, ProtocolDecl? protocolContext = null)
        {
            var boundGenericsHandler = new BoundGenericsHandler(typeDatabase);
            var paramTypes = new List<string>();
            // Skip first element (return type) in CSSignature
            for (int i = 1; i < methodDecl.CSSignature.Count; i++)
            {
                var arg = methodDecl.CSSignature[i];
                try
                {
                    // Handle associated type references for protocols
                    if (arg.SwiftTypeSpec is AssociatedTypeReferenceSpec assocRef)
                    {
                        paramTypes.Add(MapAssociatedTypeToGenericParam(assocRef, protocolContext));
                    }
                    else
                    {
                        var typeRecord = typeDatabase.GetTypeRecordOrAnyType(arg.SwiftTypeSpec);
                        paramTypes.Add(typeRecord.CSharpTypeName.FullyQualifiedName);
                    }
                }
                catch
                {
                    // For generic type parameters or other unsupported types,
                    // use the string representation of the type spec
                    paramTypes.Add(arg.SwiftTypeSpec?.ToString() ?? "unknown");
                }
            }
            return $"{methodDecl.Name}({string.Join(",", paramTypes)})";
        }

        /// <summary>
        /// Creates a unique signature key for a subscript based on index parameter types.
        /// </summary>
        private string GetSubscriptSignatureKey(SubscriptDecl subscriptDecl, ITypeDatabase typeDatabase, ProtocolDecl? protocolContext = null)
        {
            var paramTypes = new List<string>();
            foreach (var param in subscriptDecl.IndexParameters)
            {
                try
                {
                    // Handle associated type references for protocols
                    if (param.SwiftTypeSpec is AssociatedTypeReferenceSpec assocRef)
                    {
                        paramTypes.Add(MapAssociatedTypeToGenericParam(assocRef, protocolContext));
                    }
                    else if (param.SwiftTypeSpec != null)
                    {
                        var typeRecord = typeDatabase.GetTypeRecordOrAnyType(param.SwiftTypeSpec);
                        paramTypes.Add(typeRecord.CSharpTypeName.FullyQualifiedName);
                    }
                    else
                    {
                        paramTypes.Add("unknown");
                    }
                }
                catch
                {
                    // For generic type parameters or other unsupported types,
                    // use the string representation of the type spec
                    paramTypes.Add(param.SwiftTypeSpec?.ToString() ?? "unknown");
                }
            }
            return $"subscript[{string.Join(",", paramTypes)}]";
        }

        /// <summary>
        /// Gets the interface name, including generic parameters for protocols with associated types.
        /// </summary>
        private static string GetInterfaceNameWithGenerics(ProtocolDecl protocolDecl)
        {
            var baseName = NameProvider.GetInterfaceName(protocolDecl.Name);

            // If the protocol has associated types or Self requirement, make it generic
            if (protocolDecl.HasSelfRequirement)
            {
                return $"{baseName}<TSelf> where TSelf : {baseName}<TSelf>";
            }

            if (protocolDecl.AssociatedTypes.Count > 0)
            {
                var typeParams = protocolDecl.AssociatedTypes.Select(at => $"T{at.Name}");
                return $"{baseName}<{string.Join(", ", typeParams)}>";
            }

            return baseName;
        }

        /// <summary>
        /// Gets the list of inherited interfaces for the protocol.
        /// </summary>
        private static List<string> GetInheritedInterfaceList(ProtocolDecl protocolDecl)
        {
            var inheritedInterfaces = new List<string>();

            foreach (var inherited in protocolDecl.InheritedProtocols)
            {
                // Skip AnyObject as it doesn't translate to a C# interface
                if (inherited.Name == "AnyObject" || inherited.Name == "Swift.AnyObject")
                    continue;

                inheritedInterfaces.Add(NameProvider.GetInterfaceName(inherited.NameWithoutModule));
            }

            return inheritedInterfaces;
        }

        /// <summary>
        /// Emits a property declaration for an interface.
        /// </summary>
        private void EmitInterfaceProperty(CSharpWriter csWriter, PropertyDecl propertyDecl, ITypeDatabase typeDatabase, ProtocolDecl? protocolContext = null)
        {
            var boundGenericsHandler = new BoundGenericsHandler(typeDatabase);

            // Check for associated type references in protocol context
            string csharpTypeName;
            if (propertyDecl.SwiftTypeSpec is AssociatedTypeReferenceSpec assocRef)
            {
                csharpTypeName = MapAssociatedTypeToGenericParam(assocRef, protocolContext);
            }
            else if (boundGenericsHandler.IsBoundGeneric(propertyDecl))
            {
                csharpTypeName = boundGenericsHandler.TranslateBoundGenericTypeToCSharp(propertyDecl);
            }
            else
            {
                csharpTypeName = typeDatabase.GetTypeRecordOrAnyType(propertyDecl.SwiftTypeSpec).CSharpTypeName.FullyQualifiedName;
            }

            // Determine accessors
            var hasGetter = propertyDecl.Accessors.OfType<GetAccessorDecl>().Any();
            var hasSetter = propertyDecl.Accessors.OfType<SetAccessorDecl>().Any();

            string accessors;
            if (hasGetter && hasSetter)
            {
                accessors = "{ get; set; }";
            }
            else if (hasGetter)
            {
                accessors = "{ get; }";
            }
            else if (hasSetter)
            {
                accessors = "{ set; }";
            }
            else
            {
                // Default to get-only if no accessors found
                accessors = "{ get; }";
            }

            var propertyName = NameProvider.GetPropertyName(propertyDecl.Name);
            csWriter.WriteLine($"{csharpTypeName} {propertyName} {accessors}");
        }

        /// <summary>
        /// Emits a subscript declaration as a C# indexer for an interface.
        /// Swift: subscript(key: ImageCacheKey) -> ImageContainer? { get set }
        /// C#:   SwiftOptional<ImageContainer> this[ImageCacheKey key] { get; set; }
        /// </summary>
        private void EmitInterfaceSubscript(CSharpWriter csWriter, SubscriptDecl subscriptDecl, ITypeDatabase typeDatabase, ProtocolDecl? protocolContext = null)
        {
            var boundGenericsHandler = new BoundGenericsHandler(typeDatabase);

            // Get return type
            string returnTypeName;
            if (subscriptDecl.ReturnTypeSpec is AssociatedTypeReferenceSpec assocRef)
            {
                returnTypeName = MapAssociatedTypeToGenericParam(assocRef, protocolContext);
            }
            else if (subscriptDecl.ReturnTypeSpec is NamedTypeSpec namedTypeSpec && namedTypeSpec.ContainsGenericParameters)
            {
                // Create a temporary property to use the BoundGenericsHandler
                var tempProperty = new PropertyDecl
                {
                    Name = "_temp",
                    SwiftTypeSpec = subscriptDecl.ReturnTypeSpec,
                    IsStatic = false,
                    HasStorage = false,
                    Accessors = new List<AccessorDecl>(),
                    ParentDecl = null,
                    ModuleDecl = null
                };
                returnTypeName = boundGenericsHandler.TranslateBoundGenericTypeToCSharp(tempProperty);
            }
            else
            {
                returnTypeName = typeDatabase.GetTypeRecordOrAnyType(subscriptDecl.ReturnTypeSpec).CSharpTypeName.FullyQualifiedName;
            }

            // Build index parameters
            var parameters = new List<string>();
            foreach (var param in subscriptDecl.IndexParameters)
            {
                var paramTypeName = GetCSharpTypeName(param.SwiftTypeSpec, typeDatabase, boundGenericsHandler, protocolContext);
                var paramName = string.IsNullOrEmpty(param.Name) ? "index" : param.Name;
                parameters.Add($"{paramTypeName} {paramName}");
            }

            // Determine accessors
            var hasGetter = subscriptDecl.HasGetter;
            var hasSetter = subscriptDecl.HasSetter;

            string accessors;
            if (hasGetter && hasSetter)
            {
                accessors = "{ get; set; }";
            }
            else if (hasGetter)
            {
                accessors = "{ get; }";
            }
            else if (hasSetter)
            {
                accessors = "{ set; }";
            }
            else
            {
                // Default to get-only if no accessors found
                accessors = "{ get; }";
            }

            csWriter.WriteLine($"{returnTypeName} this[{string.Join(", ", parameters)}] {accessors}");
        }

        /// <summary>
        /// Emits a method declaration for an interface.
        /// </summary>
        private void EmitInterfaceMethod(CSharpWriter csWriter, MethodDecl methodDecl, ITypeDatabase typeDatabase, ProtocolDecl? protocolContext = null)
        {
            // Skip constructors - they can't be in interfaces
            if (methodDecl.IsConstructor)
                return;

            // Skip static methods - C# interfaces cannot have static members as requirements
            // Note: Static methods are still emitted on conforming types, just not in the interface
            if (methodDecl.MethodType == MethodType.Static)
            {
                _logger.LogDebug($"Skipping static method '{methodDecl.Name}' in interface {protocolContext?.Name} - static interface members are not supported.");
                ReportCollector.RecordMemberSkipped(BindingItemKind.Method, methodDecl.Name, protocolContext, SkipReason.StaticProtocolMember, "Static protocol members cannot be declared in C# interfaces.");
                return;
            }

            var boundGenericsHandler = new BoundGenericsHandler(typeDatabase);

            // Get return type - apply idiomatic type conversions (SwiftString -> string, etc.)
            // to match what MethodHandler emits on concrete types
            var typeConversionHandler = new TypeConversionHandler(typeDatabase);
            var returnType = "void";
            if (methodDecl.CSSignature.Count > 0)
            {
                var returnArg = methodDecl.CSSignature[0];
                if (returnArg.SwiftTypeSpec is not TupleTypeSpec tuple || !tuple.IsEmptyTuple)
                {
                    var idiomaticType = typeConversionHandler.GetIdiomaticCSharpType(returnArg.SwiftTypeSpec, isParameter: false);
                    returnType = idiomaticType ?? GetCSharpTypeName(returnArg.SwiftTypeSpec, typeDatabase, boundGenericsHandler, protocolContext);
                }
            }

            // Build parameters (skip first which is return type)
            var parameters = new List<string>();
            for (int i = 1; i < methodDecl.CSSignature.Count; i++)
            {
                var arg = methodDecl.CSSignature[i];
                var idiomaticParamType = typeConversionHandler.GetIdiomaticCSharpType(arg.SwiftTypeSpec, isParameter: true);
                var argTypeName = idiomaticParamType ?? GetCSharpTypeName(arg.SwiftTypeSpec, typeDatabase, boundGenericsHandler, protocolContext);
                var argName = string.IsNullOrEmpty(arg.Name) ? $"arg{i}" : arg.Name;
                parameters.Add($"{argTypeName} {argName}");
            }

            // Handle async methods
            if (methodDecl.IsAsync)
            {
                if (returnType == "void")
                {
                    returnType = "Task";
                }
                else
                {
                    returnType = $"Task<{returnType}>";
                }
            }

            var methodName = NameProvider.ToPascalCase(methodDecl.Name);
            csWriter.WriteLine($"{returnType} {methodName}({string.Join(", ", parameters)});");
        }

        /// <summary>
        /// Gets the C# type name for a Swift type specification, handling bound generics and associated types.
        /// For protocol interfaces, this also handles closures, tuples, and existentials with relaxed requirements
        /// since we're just emitting signatures, not PInvoke implementations.
        /// </summary>
        private string GetCSharpTypeName(TypeSpec typeSpec, ITypeDatabase typeDatabase, BoundGenericsHandler boundGenericsHandler, ProtocolDecl? protocolContext = null)
        {
            // Handle associated type references (e.g., Self.Element, τ_0_0.Element)
            if (typeSpec is AssociatedTypeReferenceSpec assocRef)
            {
                return MapAssociatedTypeToGenericParam(assocRef, protocolContext);
            }

            // Handle existential types (any Protocol, protocol compositions)
            var existentialHandler = new ExistentialHandler(typeDatabase);
            if (existentialHandler.IsExistential(typeSpec))
            {
                var protocolList = existentialHandler.ToProtocolListTypeSpec(typeSpec);
                if (protocolList != null && existentialHandler.IsSupportedExistential(protocolList))
                {
                    return existentialHandler.GetCSharpExistentialType(protocolList);
                }
            }

            // Handle closures - translate to C# delegate types for protocol interfaces
            if (typeSpec is ClosureTypeSpec closureTypeSpec)
            {
                return GetClosureCSharpType(closureTypeSpec, typeDatabase, protocolContext);
            }

            // Handle tuples - translate to C# ValueTuple types for protocol interfaces
            if (typeSpec is TupleTypeSpec tupleTypeSpec && !tupleTypeSpec.IsEmptyTuple)
            {
                return GetTupleCSharpType(tupleTypeSpec, typeDatabase, protocolContext);
            }

            // Handle bound generics (e.g., Optional<T>, Array<T>)
            if (typeSpec is NamedTypeSpec namedTypeSpec && namedTypeSpec.ContainsGenericParameters)
            {
                // Create a temporary property to use the BoundGenericsHandler
                var tempProperty = new PropertyDecl
                {
                    Name = "_temp",
                    SwiftTypeSpec = typeSpec,
                    IsStatic = false,
                    HasStorage = false,
                    Accessors = new List<AccessorDecl>(),
                    ParentDecl = null,
                    ModuleDecl = null
                };
                return boundGenericsHandler.TranslateBoundGenericTypeToCSharp(tempProperty);
            }

            // For non-generic types, use the standard lookup
            return typeDatabase.GetTypeRecordOrAnyType(typeSpec).CSharpTypeName.FullyQualifiedName;
        }

        /// <summary>
        /// Translates a Swift closure type to a C# delegate type for protocol interface emission.
        /// This is less restrictive than the full closure handler since we're just emitting signatures.
        /// </summary>
        private string GetClosureCSharpType(ClosureTypeSpec closureTypeSpec, ITypeDatabase typeDatabase, ProtocolDecl? protocolContext)
        {
            var boundGenericsHandler = new BoundGenericsHandler(typeDatabase);

            // Build parameter types
            var paramTypes = new List<string>();
            foreach (var arg in closureTypeSpec.EachArgument())
            {
                paramTypes.Add(GetCSharpTypeName(arg, typeDatabase, boundGenericsHandler, protocolContext));
            }

            // Get return type
            var returnType = closureTypeSpec.ReturnType;
            bool hasReturn = !returnType.IsEmptyTuple;

            if (!hasReturn)
            {
                // Action delegate
                if (paramTypes.Count == 0)
                    return "Action";
                return $"Action<{string.Join(", ", paramTypes)}>";
            }
            else
            {
                // Func delegate
                var returnTypeName = GetCSharpTypeName(returnType, typeDatabase, boundGenericsHandler, protocolContext);
                if (paramTypes.Count == 0)
                    return $"Func<{returnTypeName}>";
                return $"Func<{string.Join(", ", paramTypes)}, {returnTypeName}>";
            }
        }

        /// <summary>
        /// Translates a Swift tuple type to a C# ValueTuple type for protocol interface emission.
        /// </summary>
        private string GetTupleCSharpType(TupleTypeSpec tupleTypeSpec, ITypeDatabase typeDatabase, ProtocolDecl? protocolContext)
        {
            var boundGenericsHandler = new BoundGenericsHandler(typeDatabase);
            var elements = new List<string>();

            foreach (var element in tupleTypeSpec.Elements)
            {
                var typeName = GetCSharpTypeName(element, typeDatabase, boundGenericsHandler, protocolContext);

                // Include label if present
                if (!string.IsNullOrEmpty(element.TypeLabel))
                {
                    elements.Add($"{typeName} {element.TypeLabel}");
                }
                else
                {
                    elements.Add(typeName);
                }
            }

            return $"({string.Join(", ", elements)})";
        }

        /// <summary>
        /// Maps an associated type reference to a C# generic parameter name.
        /// For example, "Self.Element" in a protocol with associated type "Element" becomes "TElement".
        /// </summary>
        private string MapAssociatedTypeToGenericParam(AssociatedTypeReferenceSpec assocRef, ProtocolDecl? protocolDecl)
        {
            // Handle Self reference
            if (assocRef.BaseType == "Self" && string.IsNullOrEmpty(assocRef.AssociatedTypeName))
            {
                return "TSelf";
            }

            // Handle associated type reference like "Self.Element"
            if (!string.IsNullOrEmpty(assocRef.AssociatedTypeName))
            {
                // Map "Element" -> "TElement"
                return $"T{assocRef.AssociatedTypeName}";
            }

            // Fallback for generic parameter like τ_0_0
            if (assocRef.BaseType.StartsWith("τ_") || assocRef.BaseType.StartsWith("T"))
            {
                // Already a generic param reference
                return assocRef.BaseType;
            }

            _logger.LogWarning($"Unknown associated type reference: {assocRef}");
            return "object";
        }
    }
}
