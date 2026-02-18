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
            XmlDocCommentEmitter.EmitDocComment(csWriter, protocolDecl);
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
            int emittedInterfaceMemberCount = 0;
            var emittedProperties = new HashSet<string>();
            var emittedMethods = new HashSet<string>();
            var emittedCSharpKeys = new HashSet<string>();
            var emittedResolvedSignatures = new HashSet<string>(StringComparer.Ordinal);
            var emittedSubscripts = new HashSet<string>();
            var boundGenericsHandler = new BoundGenericsHandler(env.TypeDatabase);

            // Emit properties as interface members
            var skippedPropertyNames = new HashSet<string>();
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

                // Check for bare generic usage in property type (e.g., SwiftDictionary without <K,V>)
                if (boundGenericsHandler.HasBareGenericUsage(propertyDecl.SwiftTypeSpec, propertyDecl.ModuleDecl ?? protocolDecl.ModuleDecl))
                {
                    skippedPropertyNames.Add(propertyDecl.Name);
                    _logger.LogDebug($"Skipping property '{propertyDecl.Name}' in interface {protocolDecl.Name} - type uses generic declaration without type arguments.");
                    ReportCollector.RecordMemberSkipped(BindingItemKind.Property, propertyDecl.Name, protocolDecl, SkipReason.UnsupportedSignature, "Property type uses generic type without type arguments.");
                    continue;
                }

                // Check for AnyType as a generic type argument in the property type
                if (HasAnyTypeGenericArgInPropertyType(propertyDecl, env.TypeDatabase, protocolDecl))
                {
                    skippedPropertyNames.Add(propertyDecl.Name);
                    _logger.LogDebug($"Skipping property '{propertyDecl.Name}' in interface {protocolDecl.Name} - type contains AnyType as generic type argument.");
                    ReportCollector.RecordMemberSkipped(BindingItemKind.Property, propertyDecl.Name, protocolDecl, SkipReason.AnyTypeFallback, "Property type contains AnyType as a generic type argument, which violates generic constraints.");
                    continue;
                }

                // Skip properties referencing unsupported modules (SwiftUI, Combine) — these types
                // have no C# representation and would produce CS0246 in the emitted interface.
                if (MemberEmissionValidator.ReferencesUnsupportedModule(propertyDecl.SwiftTypeSpec))
                {
                    skippedPropertyNames.Add(propertyDecl.Name);
                    _logger.LogDebug($"Skipping property '{propertyDecl.Name}' in interface {protocolDecl.Name} - type references unsupported module.");
                    ReportCollector.RecordMemberSkipped(BindingItemKind.Property, propertyDecl.Name, protocolDecl, SkipReason.SwiftUIConstraint, "Property type references unsupported module (SwiftUI/Combine).");
                    continue;
                }

                EmitInterfaceProperty(csWriter, propertyDecl, env.TypeDatabase, protocolDecl);
                emittedInterfaceMemberCount++;
                ReportCollector.RecordMemberEmitted(BindingItemKind.Property, propertyDecl.Name, protocolDecl);
            }

            // Emit subscripts as interface indexers
            var skippedSubscriptIndices = new HashSet<int>();
            int subscriptIndex = 0;
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
                var subscriptKey = ProtocolSignatureHelper.GetSubscriptSignatureKey(subscriptDecl, env.TypeDatabase, protocolDecl);
                if (emittedSubscripts.Contains(subscriptKey))
                {
                    _logger.LogDebug($"Skipping duplicate subscript in interface {protocolDecl.Name}");
                    ReportCollector.RecordMemberSkipped(BindingItemKind.Subscript, "subscript", protocolDecl, SkipReason.DuplicateSignature, "Duplicate protocol subscript signature.");
                    subscriptIndex++;
                    continue;
                }
                emittedSubscripts.Add(subscriptKey);

                // Check for bare generic usage in subscript signature
                if (HasBareGenericInSubscriptSignature(subscriptDecl, env.TypeDatabase, protocolDecl))
                {
                    skippedSubscriptIndices.Add(subscriptIndex);
                    _logger.LogDebug($"Skipping subscript in interface {protocolDecl.Name} - signature uses generic declaration without type arguments.");
                    ReportCollector.RecordMemberSkipped(BindingItemKind.Subscript, "subscript", protocolDecl, SkipReason.UnsupportedSignature, "Subscript type uses generic type without type arguments.");
                    subscriptIndex++;
                    continue;
                }

                // Check for AnyType as a generic type argument in the subscript signature
                if (HasAnyTypeGenericArgInSubscriptSignature(subscriptDecl, env.TypeDatabase, protocolDecl))
                {
                    skippedSubscriptIndices.Add(subscriptIndex);
                    _logger.LogDebug($"Skipping subscript in interface {protocolDecl.Name} - signature contains AnyType as generic type argument.");
                    ReportCollector.RecordMemberSkipped(BindingItemKind.Subscript, "subscript", protocolDecl, SkipReason.AnyTypeFallback, "Subscript type contains AnyType as a generic type argument, which violates generic constraints.");
                    subscriptIndex++;
                    continue;
                }

                // Skip subscripts referencing unsupported modules (SwiftUI, Combine)
                if (MemberEmissionValidator.ReferencesUnsupportedModule(subscriptDecl.ReturnTypeSpec) ||
                    subscriptDecl.IndexParameters.Any(p => MemberEmissionValidator.ReferencesUnsupportedModule(p.SwiftTypeSpec)))
                {
                    skippedSubscriptIndices.Add(subscriptIndex);
                    _logger.LogDebug($"Skipping subscript in interface {protocolDecl.Name} - signature references unsupported module.");
                    ReportCollector.RecordMemberSkipped(BindingItemKind.Subscript, "subscript", protocolDecl, SkipReason.SwiftUIConstraint, "Subscript signature references unsupported module (SwiftUI/Combine).");
                    subscriptIndex++;
                    continue;
                }

                EmitInterfaceSubscript(csWriter, subscriptDecl, env.TypeDatabase, protocolDecl);
                emittedInterfaceMemberCount++;
                ReportCollector.RecordMemberEmitted(BindingItemKind.Subscript, "subscript", protocolDecl);
                subscriptIndex++;
            }

            // Emit methods as interface members
            var skippedMethodKeys = new HashSet<string>();
            foreach (var methodDecl in protocolDecl.Methods)
            {
                // Skip constructors and static methods early (they can't be in C# interfaces)
                if (methodDecl.IsConstructor)
                {
                    ReportCollector.RecordMemberSkipped(BindingItemKind.Method, methodDecl.Name, protocolDecl, SkipReason.StaticProtocolMember, "Protocol constructor requirements cannot be declared in C# interfaces.");
                    continue;
                }
                if (methodDecl.MethodType == MethodType.Static)
                {
                    _logger.LogDebug($"Skipping static method '{methodDecl.Name}' in interface {protocolDecl.Name} - static interface members are not supported.");
                    ReportCollector.RecordMemberSkipped(BindingItemKind.Method, methodDecl.Name, protocolDecl, SkipReason.StaticProtocolMember, "Static protocol members cannot be declared in C# interfaces.");
                    continue;
                }

                // Create a unique key for the method (name + parameter types)
                var methodKey = ProtocolSignatureHelper.GetMethodSignatureKey(methodDecl, env.TypeDatabase, protocolDecl);
                if (emittedMethods.Contains(methodKey))
                {
                    _logger.LogDebug($"Skipping duplicate method '{methodDecl.Name}' in interface {protocolDecl.Name}");
                    ReportCollector.RecordMemberSkipped(BindingItemKind.Method, methodDecl.Name, protocolDecl, SkipReason.DuplicateSignature, "Duplicate protocol method signature.");
                    continue;
                }
                emittedMethods.Add(methodKey);

                // Secondary dedup: different Swift types can project to the same C# type
                var projectedKey = ProtocolSignatureHelper.GetProjectedCSharpMethodKey(methodDecl, env.TypeDatabase, protocolDecl);
                if (!emittedCSharpKeys.Add(projectedKey))
                {
                    skippedMethodKeys.Add(methodKey);
                    _logger.LogDebug($"Skipping method '{methodDecl.Name}' - projected C# signature collides with already-emitted method.");
                    ReportCollector.RecordMemberSkipped(BindingItemKind.Method, methodDecl.Name, protocolDecl, SkipReason.DuplicateSignature, "Projected C# method signature collides with already-emitted method.");
                    continue;
                }

                bool hasNonSwiftObjectArg = methodDecl.CSSignature.Any(arg =>
                    boundGenericsHandler.IsBoundGeneric(arg) &&
                    boundGenericsHandler.HasNonSwiftObjectGenericArg(arg.SwiftTypeSpec));
                if (hasNonSwiftObjectArg)
                {
                    skippedMethodKeys.Add(methodKey);
                    _logger.LogDebug($"Skipping method '{methodDecl.Name}' in interface {protocolDecl.Name} - bound generic argument cannot satisfy ISwiftObject.");
                    ReportCollector.RecordMemberSkipped(BindingItemKind.Method, methodDecl.Name, protocolDecl, SkipReason.UnsatisfiedGenericConstraint, "Bound generic contains type argument that cannot satisfy C# ISwiftObject constraint.");
                    continue;
                }

                // Check for bare generic usage in method signature (e.g., SwiftDictionary without <K,V>)
                if (HasBareGenericInMethodSignature(methodDecl, env.TypeDatabase, protocolDecl))
                {
                    skippedMethodKeys.Add(methodKey);
                    _logger.LogDebug($"Skipping method '{methodDecl.Name}' in interface {protocolDecl.Name} - signature uses generic declaration without type arguments.");
                    ReportCollector.RecordMemberSkipped(BindingItemKind.Method, methodDecl.Name, protocolDecl, SkipReason.UnsupportedSignature, "Method signature uses generic type without type arguments.");
                    continue;
                }

                // B9: Skip methods with existential parameters — the receiver can't marshal
                // ExistentialContainer types to/from interface types in [UnmanagedCallersOnly] callbacks.
                var existentialHandlerB9 = new ExistentialHandler(env.TypeDatabase);
                bool hasExistentialParam = methodDecl.CSSignature.Skip(1).Any(arg =>
                    existentialHandlerB9.IsExistential(arg.SwiftTypeSpec) ||
                    existentialHandlerB9.IsOptionalExistential(arg.SwiftTypeSpec));
                if (hasExistentialParam)
                {
                    skippedMethodKeys.Add(methodKey);
                    _logger.LogDebug($"Skipping method '{methodDecl.Name}' in interface {protocolDecl.Name} - has existential parameter.");
                    ReportCollector.RecordMemberSkipped(BindingItemKind.Method, methodDecl.Name, protocolDecl, SkipReason.UnsupportedExistential, "Method has existential parameter that can't be marshalled in protocol receiver.");
                    continue;
                }

                // Check for AnyType as a generic type argument in the method signature
                // (e.g., BatchedCollection<AnyType> violates where T0 : ISwiftCollection)
                if (HasAnyTypeGenericArgInSignature(methodDecl, env.TypeDatabase, protocolDecl))
                {
                    skippedMethodKeys.Add(methodKey);
                    _logger.LogDebug($"Skipping method '{methodDecl.Name}' in interface {protocolDecl.Name} - resolved type contains AnyType as generic argument.");
                    ReportCollector.RecordMemberSkipped(BindingItemKind.Method, methodDecl.Name, protocolDecl, SkipReason.AnyTypeFallback, "Method return type or parameter contains AnyType as a generic type argument.");
                    continue;
                }

                // Skip methods referencing unsupported modules (SwiftUI, Combine)
                bool hasUnsupportedModuleRef = methodDecl.CSSignature.Any(arg =>
                    MemberEmissionValidator.ReferencesUnsupportedModule(arg.SwiftTypeSpec));
                if (hasUnsupportedModuleRef)
                {
                    skippedMethodKeys.Add(methodKey);
                    _logger.LogDebug($"Skipping method '{methodDecl.Name}' in interface {protocolDecl.Name} - signature references unsupported module.");
                    ReportCollector.RecordMemberSkipped(BindingItemKind.Method, methodDecl.Name, protocolDecl, SkipReason.SwiftUIConstraint, "Method signature references unsupported module (SwiftUI/Combine).");
                    continue;
                }

                var emittedSignature = BuildEmittedSignature(methodDecl, env.TypeDatabase, protocolDecl);
                if (!emittedResolvedSignatures.Add(emittedSignature))
                {
                    skippedMethodKeys.Add(methodKey);
                    _logger.LogDebug($"Skipping method '{methodDecl.Name}' - emitted C# signature collides with already-emitted method.");
                    ReportCollector.RecordMemberSkipped(BindingItemKind.Method, methodDecl.Name, protocolDecl, SkipReason.DuplicateSignature, "Emitted C# method signature collides with already-emitted method.");
                    continue;
                }

                EmitInterfaceMethod(csWriter, methodDecl, env.TypeDatabase, protocolDecl);
                emittedInterfaceMemberCount++;
                ReportCollector.RecordMemberEmitted(BindingItemKind.Method, methodDecl.Name, protocolDecl);
            }

            // Record operators as skipped - C# interfaces cannot have operator overloads
            foreach (var operatorDecl in protocolDecl.Operators)
            {
                ReportCollector.RecordMemberSkipped(BindingItemKind.Operator, operatorDecl.Name, protocolDecl, SkipReason.StaticProtocolMember, "Protocol operator requirements cannot be declared in C# interfaces.");
            }

            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.WriteLine();

            // Record the direct emitted member count on the protocol's TypeRecord.
            // This is only the count of members declared directly on this interface.
            // Inherited requirements are added in a post-emission fixup pass
            // (FixupProtocolInheritedRequirements) to avoid order-dependent miscounting
            // when a child protocol is emitted before its parent in the same module.
            if (env.TypeDatabase.TryGetTypeRecord(protocolDecl.SwiftTypeName, out var protoRecord))
            {
                env.TypeDatabase.UpdateTypeRecord(protocolDecl.SwiftTypeName,
                    protoRecord with { EmittedMemberCount = emittedInterfaceMemberCount });
            }

            // Skip proxy class if protocol has members with unsupported module types (SwiftUI, Combine).
            // The Swift EveryProtocol conformance is also skipped (in ModuleHandler), so emitting the
            // C# proxy would produce calls to non-existent Swift symbols (SetVtable, WitnessTableGetter).
            if (!ModuleHandler.HasMembersReferencingUnsupportedModule(protocolDecl))
            {
                EmitProtocolProxy(csWriter, protocolDecl, env.TypeDatabase, skippedMethodKeys, skippedPropertyNames, skippedSubscriptIndices);
            }
            else
            {
                // Use RecordMemberSkipped (not RecordTypeSkipped) because RecordTypeEmitted was
                // already called for the interface at line 70. RecordTypeSkipped silently drops
                // entries for already-emitted types. The proxy is a sub-artifact of the type.
                ReportCollector.RecordMemberSkipped(BindingItemKind.Type, $"{protocolDecl.Name}Proxy",
                    protocolDecl, SkipReason.SwiftUIConstraint,
                    "Protocol proxy skipped: required members reference unsupported module types.");
            }
        }

        /// <summary>
        /// Emits a proxy class that enables C# code to implement this protocol.
        /// The proxy wraps either a C# implementation or a Swift existential container.
        /// </summary>
        private void EmitProtocolProxy(CSharpWriter csWriter, ProtocolDecl protocolDecl, ITypeDatabase typeDatabase,
            HashSet<string> skippedMethodKeys, HashSet<string> skippedPropertyNames, HashSet<int> skippedSubscriptIndices)
        {
            var moduleName = protocolDecl.ModuleDecl?.Name ?? "Swift";
            var proxyEmitter = new ProtocolProxyEmitter(typeDatabase, _logger, moduleName);
            proxyEmitter.EmitProxyClass(csWriter, protocolDecl, skippedMethodKeys, skippedPropertyNames, skippedSubscriptIndices);
        }

        /// <summary>
        /// Gets the interface name, including generic parameters for protocols with associated types.
        /// </summary>
        private static string GetInterfaceNameWithGenerics(ProtocolDecl protocolDecl)
        {
            var baseName = NameProvider.GetInterfaceName(protocolDecl.Name, moduleName: protocolDecl.ModuleDecl?.Name ?? "");

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

                inheritedInterfaces.Add(NameProvider.GetInterfaceName(inherited.NameWithoutModule, moduleName: inherited.Module));
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
                csharpTypeName = ProtocolSignatureHelper.MapAssociatedTypeToGenericParam(assocRef, protocolContext);
            }
            else if (boundGenericsHandler.IsBoundGeneric(propertyDecl))
            {
                csharpTypeName = boundGenericsHandler.TranslateBoundGenericTypeToCSharp(propertyDecl);
            }
            else
            {
                csharpTypeName = typeDatabase.GetTypeRecordOrAnyType(propertyDecl.SwiftTypeSpec).CSharpTypeName.FullyQualifiedName;
            }

            // Apply idiomatic type conversion (SwiftString → string, SwiftArray → IReadOnlyList, etc.)
            // Interface property types must match the implementing class property types
            var typeConversionHandler = new TypeConversionHandler(typeDatabase);
            var idiomaticType = typeConversionHandler.GetIdiomaticCSharpType(
                propertyDecl.SwiftTypeSpec,
                isParameter: false,
                typeSpec =>
                {
                    var rec = typeDatabase.GetTypeRecordOrAnyType(typeSpec);
                    return rec.CSharpTypeName.FullyQualifiedName;
                });
            if (idiomaticType != null)
            {
                csharpTypeName = idiomaticType;
            }
            else if (typeConversionHandler.HasNativeTypeRemapping(propertyDecl.SwiftTypeSpec))
            {
                var nativeType = typeConversionHandler.GetNativeTypeName(propertyDecl.SwiftTypeSpec);
                if (nativeType != null)
                    csharpTypeName = nativeType;
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

            // Emit [UnsupportedSwiftType] if the property type falls back to AnyType
            var closureHandler = new ClosureHandler(typeDatabase);
            if (UnsupportedSwiftTypeSupport.TryFindFallbackInfo(typeDatabase, closureHandler, propertyDecl.SwiftTypeSpec, out var fallbackInfo))
            {
                UnsupportedSwiftTypeSupport.EmitAttribute(csWriter, fallbackInfo);
            }

            XmlDocCommentEmitter.EmitDocComment(csWriter, propertyDecl);
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
            var typeConversionHandler = new TypeConversionHandler(typeDatabase);
            NameProvider.DeduplicateParameterNamesForParameterList(subscriptDecl.IndexParameters);

            // Get return type — apply idiomatic type conversion (SwiftOptional → T?, SwiftString → string)
            string returnTypeName;
            var idiomaticReturnType = typeConversionHandler.GetIdiomaticCSharpType(subscriptDecl.ReturnTypeSpec, isParameter: false);
            if (idiomaticReturnType != null)
            {
                returnTypeName = idiomaticReturnType;
            }
            else if (subscriptDecl.ReturnTypeSpec is AssociatedTypeReferenceSpec assocRef)
            {
                returnTypeName = ProtocolSignatureHelper.MapAssociatedTypeToGenericParam(assocRef, protocolContext);
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
                try
                {
                    returnTypeName = boundGenericsHandler.TranslateBoundGenericTypeToCSharp(tempProperty);
                }
                catch (NotSupportedException)
                {
                    returnTypeName = TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName;
                }
            }
            else
            {
                returnTypeName = typeDatabase.GetTypeRecordOrAnyType(subscriptDecl.ReturnTypeSpec).CSharpTypeName.FullyQualifiedName;
            }

            // Build index parameters — apply idiomatic type conversion
            var parameters = new List<string>();
            foreach (var param in subscriptDecl.IndexParameters)
            {
                var idiomaticParamType = typeConversionHandler.GetIdiomaticCSharpType(param.SwiftTypeSpec, isParameter: true);
                var paramTypeName = idiomaticParamType ?? GetCSharpTypeName(param.SwiftTypeSpec, typeDatabase, boundGenericsHandler, protocolContext);
                var paramName = NameProvider.GetCSharpParameterName(param);
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

            // Emit [UnsupportedSwiftType] if the return type or any parameter falls back to AnyType
            var closureHandler = new ClosureHandler(typeDatabase);
            if (UnsupportedSwiftTypeSupport.TryFindFallbackInfo(typeDatabase, closureHandler, subscriptDecl.ReturnTypeSpec, out var subscriptFallbackInfo))
            {
                UnsupportedSwiftTypeSupport.EmitAttribute(csWriter, subscriptFallbackInfo);
            }
            else
            {
                foreach (var param in subscriptDecl.IndexParameters)
                {
                    if (UnsupportedSwiftTypeSupport.TryFindFallbackInfo(typeDatabase, closureHandler, param.SwiftTypeSpec, out var paramFallbackInfo))
                    {
                        UnsupportedSwiftTypeSupport.EmitAttribute(csWriter, paramFallbackInfo);
                        break; // One attribute is enough to flag the subscript
                    }
                }
            }

            csWriter.WriteLine($"{returnTypeName} this[{string.Join(", ", parameters)}] {accessors}");
        }

        /// <summary>
        /// Emits a method declaration for an interface.
        /// </summary>
        private void EmitInterfaceMethod(CSharpWriter csWriter, MethodDecl methodDecl, ITypeDatabase typeDatabase, ProtocolDecl? protocolContext = null)
        {
            // Note: Constructor, static, duplicate, and AnyType generic arg checks
            // are handled at the loop level in Emit(). This method is only called
            // for methods that pass all pre-checks.

            var boundGenericsHandler = new BoundGenericsHandler(typeDatabase);
            NameProvider.DeduplicateParameterNames(methodDecl.CSSignature);

            // Get return type - apply idiomatic type conversions (SwiftString -> string, etc.)
            // and native type remapping (Foundation.URL -> NSUrl, Foundation.Data -> NSData)
            // to match what MethodHandler emits on concrete types
            var typeConversionHandler = new TypeConversionHandler(typeDatabase);
            var returnType = "void";
            if (methodDecl.CSSignature.Count > 0)
            {
                var returnArg = methodDecl.CSSignature[0];
                if (returnArg.SwiftTypeSpec is not TupleTypeSpec tuple || !tuple.IsEmptyTuple)
                {
                    var idiomaticType = typeConversionHandler.GetIdiomaticCSharpType(returnArg.SwiftTypeSpec, isParameter: false);
                    if (idiomaticType != null)
                    {
                        returnType = idiomaticType;
                    }
                    else if (typeConversionHandler.HasNativeTypeRemapping(returnArg.SwiftTypeSpec))
                    {
                        returnType = typeConversionHandler.GetNativeTypeName(returnArg.SwiftTypeSpec)
                            ?? GetCSharpTypeName(returnArg.SwiftTypeSpec, typeDatabase, boundGenericsHandler, protocolContext);
                    }
                    else
                    {
                        returnType = GetCSharpTypeName(returnArg.SwiftTypeSpec, typeDatabase, boundGenericsHandler, protocolContext);
                    }
                }
            }

            // Build parameters (skip first which is return type)
            var parameters = new List<string>();
            for (int i = 1; i < methodDecl.CSSignature.Count; i++)
            {
                var arg = methodDecl.CSSignature[i];
                var idiomaticParamType = typeConversionHandler.GetIdiomaticCSharpType(arg.SwiftTypeSpec, isParameter: true);
                string argTypeName;
                if (idiomaticParamType != null)
                {
                    argTypeName = idiomaticParamType;
                }
                else if (typeConversionHandler.HasNativeTypeRemapping(arg.SwiftTypeSpec))
                {
                    argTypeName = typeConversionHandler.GetNativeTypeName(arg.SwiftTypeSpec)
                        ?? GetCSharpTypeName(arg.SwiftTypeSpec, typeDatabase, boundGenericsHandler, protocolContext);
                }
                else
                {
                    argTypeName = GetCSharpTypeName(arg.SwiftTypeSpec, typeDatabase, boundGenericsHandler, protocolContext);
                }
                var argName = NameProvider.GetCSharpParameterName(arg);
                parameters.Add($"{argTypeName} {argName}");
            }

            // Capture hasReturnValue BEFORE async conversion turns void → Task
            var hasReturnValue = returnType != "void";

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

            // Add CancellationToken to async interface methods (matches WrapperEmitter emission)
            if (methodDecl.IsAsync)
            {
                parameters.Add("System.Threading.CancellationToken cancellationToken = default");
            }

            // Emit [UnsupportedSwiftType] if the return type or any parameter falls back to AnyType
            var closureHandler = new ClosureHandler(typeDatabase);
            bool emittedAttribute = false;
            if (methodDecl.CSSignature.Count > 0)
            {
                var returnArg = methodDecl.CSSignature[0];
                if (returnArg.SwiftTypeSpec is not TupleTypeSpec tuple || !tuple.IsEmptyTuple)
                {
                    if (UnsupportedSwiftTypeSupport.TryFindFallbackInfo(typeDatabase, closureHandler, returnArg.SwiftTypeSpec, out var returnFallbackInfo))
                    {
                        UnsupportedSwiftTypeSupport.EmitAttribute(csWriter, returnFallbackInfo);
                        emittedAttribute = true;
                    }
                }
            }
            if (!emittedAttribute)
            {
                for (int j = 1; j < methodDecl.CSSignature.Count; j++)
                {
                    if (UnsupportedSwiftTypeSupport.TryFindFallbackInfo(typeDatabase, closureHandler, methodDecl.CSSignature[j].SwiftTypeSpec, out var paramFallbackInfo))
                    {
                        UnsupportedSwiftTypeSupport.EmitAttribute(csWriter, paramFallbackInfo);
                        break; // One attribute is enough to flag the method
                    }
                }
            }

            var methodName = NameProvider.GetPublicMethodName(methodDecl.Name, methodDecl.IsAsync, hasReturnValue: hasReturnValue);
            XmlDocCommentEmitter.EmitMethodDocComment(csWriter, methodDecl);
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
                return ProtocolSignatureHelper.MapAssociatedTypeToGenericParam(assocRef, protocolContext);
            }

            // Handle existential types (any Protocol, protocol compositions)
            var existentialHandler = new ExistentialHandler(typeDatabase);
            if (existentialHandler.IsExistential(typeSpec))
            {
                var protocolList = existentialHandler.ToProtocolListTypeSpec(typeSpec);
                if (protocolList != null && existentialHandler.IsSupportedExistential(protocolList))
                {
                    return existentialHandler.GetPublicExistentialType(protocolList);
                }
            }

            // Handle Optional-wrapped existential types (e.g., (any ImageDecoding)?)
            if (existentialHandler.IsOptionalExistential(typeSpec))
            {
                var innerProtocolList = existentialHandler.UnwrapOptionalExistential(typeSpec);
                if (innerProtocolList != null && existentialHandler.IsSupportedExistential(innerProtocolList))
                {
                    var publicInnerType = existentialHandler.GetPublicExistentialType(innerProtocolList);
                    if (publicInnerType != "object")
                    {
                        return existentialHandler.GetPublicOptionalExistentialType(innerProtocolList);
                    }
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

            // C9: Check for idiomatic type conversions first (e.g., Optional<Bool> → bool?, Array<String> → IReadOnlyList<string>)
            // This ensures protocol interface signatures match concrete implementations which use GetIdiomaticCSharpType.
            if (typeSpec is NamedTypeSpec namedTypeSpec)
            {
                var typeConversionHandler = new TypeConversionHandler(typeDatabase);
                // Pass typeTranslator so GetElementType can recursively resolve generic type args
                // (e.g., Optional<Dictionary<K,V>> → SwiftDictionary<K_resolved, V_resolved>?)
                var idiomaticType = typeConversionHandler.GetIdiomaticCSharpType(typeSpec, isParameter: true,
                    ts => GetCSharpTypeName(ts, typeDatabase, boundGenericsHandler, protocolContext));
                if (idiomaticType != null)
                    return idiomaticType;

                // Handle bound generics (e.g., Optional<T>, Array<T>)
                if (namedTypeSpec.ContainsGenericParameters)
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
                    try
                    {
                        return boundGenericsHandler.TranslateBoundGenericTypeToCSharp(tempProperty);
                    }
                    catch (NotSupportedException)
                    {
                        // Unrecognized bound generic (e.g., SwiftDictionary<K,V>) — return AnyType
                        // to avoid bare type name without generic args (CS0305)
                        return TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName;
                    }
                }
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
        /// Checks if a method signature contains bare generic usage (e.g., SwiftDictionary without type arguments).
        /// </summary>
        private static bool HasBareGenericInMethodSignature(MethodDecl methodDecl, ITypeDatabase typeDatabase, ProtocolDecl? protocolContext)
        {
            var boundGenericsHandler = new BoundGenericsHandler(typeDatabase);
            var moduleDecl = methodDecl.ModuleDecl ?? protocolContext?.ModuleDecl;

            if (methodDecl.CSSignature.Count > 0)
            {
                var returnArg = methodDecl.CSSignature[0];
                if (returnArg.SwiftTypeSpec is not TupleTypeSpec tuple || !tuple.IsEmptyTuple)
                {
                    if (boundGenericsHandler.HasBareGenericUsage(returnArg.SwiftTypeSpec, moduleDecl))
                        return true;
                }
            }

            for (int i = 1; i < methodDecl.CSSignature.Count; i++)
            {
                if (boundGenericsHandler.HasBareGenericUsage(methodDecl.CSSignature[i].SwiftTypeSpec, moduleDecl))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Checks if a subscript signature contains bare generic usage (e.g., SwiftDictionary without type arguments).
        /// </summary>
        private static bool HasBareGenericInSubscriptSignature(SubscriptDecl subscriptDecl, ITypeDatabase typeDatabase, ProtocolDecl? protocolContext)
        {
            var boundGenericsHandler = new BoundGenericsHandler(typeDatabase);
            var moduleDecl = subscriptDecl.ModuleDecl ?? protocolContext?.ModuleDecl;

            if (boundGenericsHandler.HasBareGenericUsage(subscriptDecl.ReturnTypeSpec, moduleDecl))
                return true;

            foreach (var param in subscriptDecl.IndexParameters)
            {
                if (boundGenericsHandler.HasBareGenericUsage(param.SwiftTypeSpec, moduleDecl))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Checks if a property's resolved C# type contains AnyType as a generic type argument.
        /// Uses the same resolution chain as EmitInterfaceProperty.
        /// </summary>
        private bool HasAnyTypeGenericArgInPropertyType(PropertyDecl propertyDecl, ITypeDatabase typeDatabase, ProtocolDecl? protocolContext)
        {
            var boundGenericsHandler = new BoundGenericsHandler(typeDatabase);

            string csharpTypeName;
            if (propertyDecl.SwiftTypeSpec is AssociatedTypeReferenceSpec assocRef)
            {
                csharpTypeName = ProtocolSignatureHelper.MapAssociatedTypeToGenericParam(assocRef, protocolContext);
            }
            else if (boundGenericsHandler.IsBoundGeneric(propertyDecl))
            {
                csharpTypeName = boundGenericsHandler.TranslateBoundGenericTypeToCSharp(propertyDecl);
            }
            else
            {
                csharpTypeName = typeDatabase.GetTypeRecordOrAnyType(propertyDecl.SwiftTypeSpec).CSharpTypeName.FullyQualifiedName;
            }

            // IsBareGenericTypeName is a safety net for resolved C# names that slipped through
            // TypeSpec-level HasBareGenericUsage (checked upstream in emit loop).
            return ContainsAnyTypeGenericArg(csharpTypeName) ||
                   TypeDatabaseExtensions.IsBareGenericTypeName(csharpTypeName);
        }

        /// <summary>
        /// Checks if a subscript's resolved C# return type or index parameter types contain
        /// AnyType as a generic type argument. Uses the same resolution chain as EmitInterfaceSubscript.
        /// </summary>
        private bool HasAnyTypeGenericArgInSubscriptSignature(SubscriptDecl subscriptDecl, ITypeDatabase typeDatabase, ProtocolDecl? protocolContext)
        {
            var boundGenericsHandler = new BoundGenericsHandler(typeDatabase);

            // Check return type
            string returnTypeName;
            if (subscriptDecl.ReturnTypeSpec is AssociatedTypeReferenceSpec assocRef)
            {
                returnTypeName = ProtocolSignatureHelper.MapAssociatedTypeToGenericParam(assocRef, protocolContext);
            }
            else if (subscriptDecl.ReturnTypeSpec is NamedTypeSpec namedTypeSpec && namedTypeSpec.ContainsGenericParameters)
            {
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
                try
                {
                    returnTypeName = boundGenericsHandler.TranslateBoundGenericTypeToCSharp(tempProperty);
                }
                catch (NotSupportedException)
                {
                    returnTypeName = TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName;
                }
            }
            else
            {
                returnTypeName = typeDatabase.GetTypeRecordOrAnyType(subscriptDecl.ReturnTypeSpec).CSharpTypeName.FullyQualifiedName;
            }

            if (ContainsAnyTypeGenericArg(returnTypeName) ||
                TypeDatabaseExtensions.IsBareGenericTypeName(returnTypeName))
                return true;

            // Check index parameters
            foreach (var param in subscriptDecl.IndexParameters)
            {
                var paramTypeName = GetCSharpTypeName(param.SwiftTypeSpec, typeDatabase, boundGenericsHandler, protocolContext);
                if (ContainsAnyTypeGenericArg(paramTypeName) ||
                    TypeDatabaseExtensions.IsBareGenericTypeName(paramTypeName))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Checks if a protocol method's resolved C# signature contains AnyType or a bare
        /// generic type name as a generic type argument (e.g., BatchedCollection&lt;AnyType&gt;), which would
        /// violate generic constraints and produce uncompilable code.
        /// </summary>
        private bool HasAnyTypeGenericArgInSignature(MethodDecl methodDecl, ITypeDatabase typeDatabase, ProtocolDecl? protocolContext)
        {
            var boundGenericsHandler = new BoundGenericsHandler(typeDatabase);
            var typeConversionHandler = new TypeConversionHandler(typeDatabase);

            // Check return type
            if (methodDecl.CSSignature.Count > 0)
            {
                var returnArg = methodDecl.CSSignature[0];
                if (returnArg.SwiftTypeSpec is not TupleTypeSpec tuple || !tuple.IsEmptyTuple)
                {
                    var returnType = ResolveMethodTypeName(returnArg.SwiftTypeSpec, isParameter: false,
                        typeDatabase, boundGenericsHandler, typeConversionHandler, protocolContext);
                    if (ContainsAnyTypeGenericArg(returnType) ||
                        TypeDatabaseExtensions.IsBareGenericTypeName(returnType))
                        return true;
                }
            }

            // Check parameters
            for (int i = 1; i < methodDecl.CSSignature.Count; i++)
            {
                var arg = methodDecl.CSSignature[i];
                var paramType = ResolveMethodTypeName(arg.SwiftTypeSpec, isParameter: true,
                    typeDatabase, boundGenericsHandler, typeConversionHandler, protocolContext);
                if (ContainsAnyTypeGenericArg(paramType) ||
                    TypeDatabaseExtensions.IsBareGenericTypeName(paramType))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Resolves a Swift type spec to its C# type name for a protocol method signature,
        /// using the same resolution chain as EmitInterfaceMethod.
        /// </summary>
        private string ResolveMethodTypeName(TypeSpec swiftTypeSpec, bool isParameter,
            ITypeDatabase typeDatabase, BoundGenericsHandler boundGenericsHandler,
            TypeConversionHandler typeConversionHandler, ProtocolDecl? protocolContext)
        {
            var idiomaticType = typeConversionHandler.GetIdiomaticCSharpType(swiftTypeSpec, isParameter: isParameter);
            if (idiomaticType != null)
                return idiomaticType;

            if (typeConversionHandler.HasNativeTypeRemapping(swiftTypeSpec))
            {
                return typeConversionHandler.GetNativeTypeName(swiftTypeSpec)
                    ?? GetCSharpTypeName(swiftTypeSpec, typeDatabase, boundGenericsHandler, protocolContext);
            }

            return GetCSharpTypeName(swiftTypeSpec, typeDatabase, boundGenericsHandler, protocolContext);
        }

        private string BuildEmittedSignature(MethodDecl methodDecl, ITypeDatabase typeDatabase, ProtocolDecl? protocolContext)
        {
            var boundGenericsHandler = new BoundGenericsHandler(typeDatabase);
            var typeConversionHandler = new TypeConversionHandler(typeDatabase);

            var returnTypeSpec = methodDecl.CSSignature.FirstOrDefault()?.SwiftTypeSpec;
            bool hasReturnValue = returnTypeSpec != null && !returnTypeSpec.IsEmptyTuple;
            var methodName = NameProvider.GetPublicMethodName(methodDecl.Name, methodDecl.IsAsync, hasReturnValue: hasReturnValue);

            var paramTypes = new List<string>();
            for (int i = 1; i < methodDecl.CSSignature.Count; i++)
            {
                var arg = methodDecl.CSSignature[i];
                var paramType = ResolveMethodTypeName(arg.SwiftTypeSpec, isParameter: true,
                    typeDatabase, boundGenericsHandler, typeConversionHandler, protocolContext);
                paramType = ProtocolSignatureHelper.NormalizeParamTypeForOverloadIdentity(paramType, arg.SwiftTypeSpec, typeDatabase);
                paramTypes.Add(paramType);
            }

            return $"{methodName}({string.Join(",", paramTypes)})";
        }

        /// <summary>
        /// Checks if a resolved C# type name contains AnyType as a generic type argument
        /// (inside angle brackets). Plain AnyType as a standalone type is NOT flagged —
        /// it's degraded but compilable. Only AnyType within a bound generic (e.g.,
        /// BatchedCollection&lt;Swift.AnyType&gt;) is problematic because it violates
        /// generic constraints.
        /// </summary>
        internal static bool ContainsAnyTypeGenericArg(string csharpTypeName)
        {
            int angleBracketStart = csharpTypeName.IndexOf('<');
            if (angleBracketStart < 0) return false;
            var genericPart = csharpTypeName.Substring(angleBracketStart);
            // Token-aware match: ensure "AnyType" is a standalone type identifier,
            // not part of a larger name (e.g., reject "MyAnyTypeModel")
            int idx = 0;
            while ((idx = genericPart.IndexOf("AnyType", idx, StringComparison.Ordinal)) >= 0)
            {
                bool startOk = idx == 0 || !IsIdentifierChar(genericPart[idx - 1]);
                int end = idx + "AnyType".Length;
                bool endOk = end >= genericPart.Length || !IsIdentifierChar(genericPart[end]);
                if (startOk && endOk) return true;
                idx++;
            }
            return false;
        }

        private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_';

        /// <summary>
        /// Post-emission fixup: recomputes EmittedMemberCount for all protocol TypeRecords
        /// to include inherited protocol requirements. Must be called after all protocols in
        /// the module have been emitted (so all direct member counts are set), but before
        /// the module database is serialized.
        /// </summary>
        /// <remarks>
        /// During emission, ProtocolHandler.Emit stores only the direct member count to avoid
        /// order-dependent miscounting (a child protocol emitted before its parent would see
        /// null for the parent's count). This fixup iterates to a fixed point so that
        /// transitive inheritance chains (Child → Parent → Grandparent) propagate correctly
        /// regardless of declaration order.
        /// </remarks>
        public static void FixupProtocolInheritedRequirements(ModuleDecl moduleDecl, ITypeDatabase typeDatabase)
        {
            // Recursively collect ALL protocol decls (including nested types) and
            // snapshot their direct member counts before any fixup.
            var protocolDecls = new List<(ProtocolDecl decl, int directCount)>();
            CollectProtocolDecls(moduleDecl.Types, protocolDecls, typeDatabase);

            // Iterate to a fixed point: each pass recomputes total = directCount + inherited.
            // A parent updated in one pass may cause its child to update in the next.
            // Worst case is O(depth) passes for a linear chain; typical modules converge in 1-2.
            bool changed = true;
            while (changed)
            {
                changed = false;
                foreach (var (protocolDecl, directCount) in protocolDecls)
                {
                    int inheritedRequirementCount = 0;
                    foreach (var inherited in protocolDecl.InheritedProtocols)
                    {
                        if (inherited.Name == "AnyObject" || inherited.Name == "Swift.AnyObject")
                            continue;
                        var inheritedSwiftName = SwiftTypeName.FromModuleQualifiedName(inherited.Name);
                        if (typeDatabase.TryGetTypeRecord(inheritedSwiftName, out var inheritedRecord)
                            && inheritedRecord.EmittedMemberCount is null or > 0)
                        {
                            inheritedRequirementCount++;
                        }
                    }

                    int totalRequirements = directCount + inheritedRequirementCount;
                    if (typeDatabase.TryGetTypeRecord(protocolDecl.SwiftTypeName, out var currentRecord)
                        && currentRecord.EmittedMemberCount != totalRequirements)
                    {
                        typeDatabase.UpdateTypeRecord(protocolDecl.SwiftTypeName,
                            currentRecord with { EmittedMemberCount = totalRequirements });
                        changed = true;
                    }
                }
            }
        }

        /// <summary>
        /// Recursively collects all ProtocolDecl instances from a type hierarchy,
        /// including protocols nested inside structs, classes, and enums.
        /// </summary>
        private static void CollectProtocolDecls(
            IEnumerable<TypeDecl> types,
            List<(ProtocolDecl decl, int directCount)> result,
            ITypeDatabase typeDatabase)
        {
            foreach (var typeDecl in types)
            {
                if (typeDecl is ProtocolDecl protocolDecl)
                {
                    if (typeDatabase.TryGetTypeRecord(protocolDecl.SwiftTypeName, out var record)
                        && record.Kind == TypeRecordKind.Protocol
                        && record.EmittedMemberCount != null)
                    {
                        result.Add((protocolDecl, record.EmittedMemberCount.Value));
                    }
                }

                // Recurse into nested types (structs, classes, enums can all contain protocols)
                if (typeDecl.Types.Count > 0)
                {
                    CollectProtocolDecls(typeDecl.Types, result, typeDatabase);
                }
            }
        }

    }
}
