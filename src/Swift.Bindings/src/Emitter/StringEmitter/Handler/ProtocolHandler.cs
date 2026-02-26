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
        private SortedDictionary<string, List<string>>? _compositionCollector;

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
        public void Emit(CSharpWriter csWriter, SwiftWriter swiftWriter, IEnvironment env, Conductor conductor, TypeHandlerContext context)
        {
            _compositionCollector = context.CompositionCollector;
            var protocolEnv = (TypeEnvironment)env;
            var protocolDecl = (ProtocolDecl)protocolEnv.TypeDecl;
            ReportCollector.RecordTypeEmitted(protocolDecl);

            var interfaceName = GetInterfaceNameWithGenerics(protocolDecl);
            var inheritedInterfaces = GetInheritedInterfaceList(protocolDecl);

            // Count total declared members that are candidates for interface emission
            // (non-static, non-constructor). Used for SB0004 diagnostic.
            int totalDeclaredMembers =
                protocolDecl.Properties.Count(p => !p.IsStatic) +
                protocolDecl.Subscripts.Count(s => !s.IsStatic) +
                protocolDecl.Methods.Count(m => !m.IsConstructor && m.MethodType != MethodType.Static);

            // Buffer the interface body so we can decide whether to emit SB0004
            // (empty interface with skipped members) before the declaration.
            var bodyStringWriter = new System.IO.StringWriter();
            var bodyWriter = new CSharpWriter(bodyStringWriter);
            bodyWriter.Indent = csWriter.Indent + 1;

            // Track emitted members to avoid duplicates
            int emittedInterfaceMemberCount = 0;
            var emittedProperties = new HashSet<string>();
            var emittedMethods = new HashSet<string>();
            var emittedCSharpKeys = new HashSet<string>();
            var emittedResolvedSignatures = new HashSet<string>(StringComparer.Ordinal);
            var emittedSubscripts = new HashSet<string>();
            var closureHandler = new ClosureHandler(env.TypeDatabase);

            // Emit properties as interface members
            var skippedPropertyNames = new HashSet<string>();
            var closureSkippedPropertyNames = new HashSet<string>(); // Closure properties: in interface, proxy needs stub
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

                // Evaluate property gates via centralized evaluator (P3-P7)
                var gateEvaluator = new MemberGateEvaluator(env.TypeDatabase);
                var propertyGate = gateEvaluator.EvaluateProperty(propertyDecl, protocolDecl.ModuleDecl, protocolDecl);
                if (propertyGate.IsSkipped)
                {
                    skippedPropertyNames.Add(propertyDecl.Name);
                    _logger.LogDebug($"Skipping property '{propertyDecl.Name}' in interface {protocolDecl.Name} - {propertyGate.Details}");
                    ReportCollector.RecordMemberSkipped(BindingItemKind.Property, propertyDecl.Name, protocolDecl, propertyGate.Reason!.Value, propertyGate.Details!);
                    continue;
                }
                if (propertyGate.IsInterfaceOnly)
                {
                    // Closure property: emit in interface, track for proxy NotSupportedException stub
                    skippedPropertyNames.Add(propertyDecl.Name);
                    if (propertyGate.SoftFlags.HasFlag(SoftGateFlags.HasClosureProperty))
                        closureSkippedPropertyNames.Add(propertyDecl.Name);
                    _logger.LogDebug($"Property '{propertyDecl.Name}' in interface {protocolDecl.Name} has closure type - proxy will use NotSupportedException stub.");
                    // Fall through to emit in interface — concrete types can implement it
                }

                EmitInterfaceProperty(bodyWriter, propertyDecl, env.TypeDatabase, closureHandler, protocolDecl);
                emittedInterfaceMemberCount++;
                ReportCollector.RecordMemberEmitted(BindingItemKind.Property, propertyDecl.Name, protocolDecl);
            }

            // Collect actually-emitted C# property names for method/property collision detection.
            // Only include properties that passed all gates and were emitted in the interface
            // (not properties that were seen for dedup but gate-skipped).
            var emittedCSharpPropertyNames = new HashSet<string>();
            foreach (var propKey in emittedProperties)
            {
                if (!skippedPropertyNames.Contains(propKey))
                    emittedCSharpPropertyNames.Add(NameProvider.GetPropertyName(propKey));
            }
            // Also include closure properties that ARE emitted in the interface
            // (they're in skippedPropertyNames for proxy tracking, but they're still
            // interface members that can collide with method names).
            foreach (var closurePropName in closureSkippedPropertyNames)
                emittedCSharpPropertyNames.Add(NameProvider.GetPropertyName(closurePropName));

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

                // Evaluate subscript gates via centralized evaluator (S3-S5)
                var subscriptGateEvaluator = new MemberGateEvaluator(env.TypeDatabase);
                var subscriptGate = subscriptGateEvaluator.EvaluateSubscript(subscriptDecl, protocolDecl.ModuleDecl, protocolDecl);
                if (subscriptGate.IsSkipped)
                {
                    skippedSubscriptIndices.Add(subscriptIndex);
                    _logger.LogDebug($"Skipping subscript in interface {protocolDecl.Name} - {subscriptGate.Details}");
                    ReportCollector.RecordMemberSkipped(BindingItemKind.Subscript, "subscript", protocolDecl, subscriptGate.Reason!.Value, subscriptGate.Details!);
                    subscriptIndex++;
                    continue;
                }

                EmitInterfaceSubscript(bodyWriter, subscriptDecl, env.TypeDatabase, closureHandler, protocolDecl);
                emittedInterfaceMemberCount++;
                ReportCollector.RecordMemberEmitted(BindingItemKind.Subscript, "subscript", protocolDecl);
                subscriptIndex++;
            }

            // Emit methods as interface members
            var skippedMethodKeys = new HashSet<string>();
            var closureSkippedMethodKeys = new HashSet<string>(); // Closure methods: in interface, proxy needs stub
            var existentialSkippedMethodKeys = new HashSet<string>(); // Existential methods: in interface, proxy needs stub
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

                // Evaluate method gates via centralized evaluator (M5-M10)
                var methodGateEvaluator = new MemberGateEvaluator(env.TypeDatabase);
                var methodGate = methodGateEvaluator.EvaluateMethod(methodDecl, protocolDecl.ModuleDecl, protocolDecl);
                if (methodGate.IsSkipped)
                {
                    skippedMethodKeys.Add(methodKey);
                    _logger.LogDebug($"Skipping method '{methodDecl.Name}' in interface {protocolDecl.Name} - {methodGate.Details}");
                    ReportCollector.RecordMemberSkipped(BindingItemKind.Method, methodDecl.Name, protocolDecl, methodGate.Reason!.Value, methodGate.Details!);
                    continue;
                }

                // M11: Emitted signature collision (stays inline — uses stateful HashSet)
                var emittedSignature = BuildEmittedSignature(methodDecl, env.TypeDatabase, protocolDecl, emittedCSharpPropertyNames);
                if (!emittedResolvedSignatures.Add(emittedSignature))
                {
                    skippedMethodKeys.Add(methodKey);
                    _logger.LogDebug($"Skipping method '{methodDecl.Name}' - emitted C# signature collides with already-emitted method.");
                    ReportCollector.RecordMemberSkipped(BindingItemKind.Method, methodDecl.Name, protocolDecl, SkipReason.DuplicateSignature, "Emitted C# method signature collides with already-emitted method.");
                    continue;
                }

                // Track closure/existential methods that passed all gates and are emitted in interface
                if (methodGate.IsInterfaceOnly)
                {
                    skippedMethodKeys.Add(methodKey);
                    if (methodGate.SoftFlags.HasFlag(SoftGateFlags.HasClosureParam))
                        closureSkippedMethodKeys.Add(methodKey);
                    if (methodGate.SoftFlags.HasFlag(SoftGateFlags.HasExistentialParam))
                        existentialSkippedMethodKeys.Add(methodKey);
                }

                EmitInterfaceMethod(bodyWriter, methodDecl, env.TypeDatabase, closureHandler, protocolDecl, emittedCSharpPropertyNames);
                emittedInterfaceMemberCount++;
                ReportCollector.RecordMemberEmitted(BindingItemKind.Method, methodDecl.Name, protocolDecl);
            }

            // Record operators as skipped - C# interfaces cannot have operator overloads
            foreach (var operatorDecl in protocolDecl.Operators)
            {
                ReportCollector.RecordMemberSkipped(BindingItemKind.Operator, operatorDecl.Name, protocolDecl, SkipReason.StaticProtocolMember, "Protocol operator requirements cannot be declared in C# interfaces.");
            }

            // Now emit the interface declaration with optional SB0004 diagnostic.
            // We deferred writing the declaration until after the body was buffered
            // so we know whether any members were emitted.
            XmlDocCommentEmitter.EmitDocComment(csWriter, protocolDecl);
            if (emittedInterfaceMemberCount == 0 && totalDeclaredMembers > 0 && inheritedInterfaces.Count == 0)
            {
                csWriter.WriteLine($"[Obsolete(\"All {totalDeclaredMembers} protocol member(s) were skipped during binding generation (SB0004). \" +");
                csWriter.WriteLine("    \"This interface is empty because no members could be projected to C#.\",");
                csWriter.WriteLine("    DiagnosticId = \"SB0004\",");
                csWriter.WriteLine("    UrlFormat = \"https://github.com/malinicr/swift-bindings/blob/main/src/docs/known-issues-workarounds.md\")]");
            }
            if (protocolDecl.Name.StartsWith("_"))
                csWriter.WriteLine("[global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]");
            if (inheritedInterfaces.Count > 0)
            {
                csWriter.WriteLine($"public interface {interfaceName} : {string.Join(", ", inheritedInterfaces)}");
            }
            else
            {
                csWriter.WriteLine($"public interface {interfaceName}");
            }
            csWriter.WriteLine("{");
            // Flush the buffered body (already indented by bodyWriter)
            csWriter.InnerWriter.Write(bodyStringWriter.ToString());
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
                EmitProtocolProxy(csWriter, protocolDecl, env.TypeDatabase, skippedMethodKeys, skippedPropertyNames, skippedSubscriptIndices,
                    closureSkippedMethodKeys, closureSkippedPropertyNames, existentialSkippedMethodKeys, context.GetEmissionContext());
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
            HashSet<string> skippedMethodKeys, HashSet<string> skippedPropertyNames, HashSet<int> skippedSubscriptIndices,
            HashSet<string> closureSkippedMethodKeys, HashSet<string> closureSkippedPropertyNames,
            HashSet<string> existentialSkippedMethodKeys, ModuleEmissionContext? emissionCtx = null)
        {
            var moduleName = protocolDecl.ModuleDecl?.Name ?? "Swift";
            var proxyEmitter = new ProtocolProxyEmitter(typeDatabase, _logger, moduleName, emissionCtx);
            proxyEmitter.EmitProxyClass(csWriter, protocolDecl, skippedMethodKeys, skippedPropertyNames, skippedSubscriptIndices,
                closureSkippedMethodKeys, closureSkippedPropertyNames, existentialSkippedMethodKeys);
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
        private void EmitInterfaceProperty(CSharpWriter csWriter, PropertyDecl propertyDecl, ITypeDatabase typeDatabase, ClosureHandler closureHandler, ProtocolDecl? protocolContext = null)
        {
            var boundGenericsHandler = new BoundGenericsHandler(typeDatabase);

            // Resolve property type using factory-first projection
            var csharpTypeName = GetCSharpTypeName(propertyDecl.SwiftTypeSpec, typeDatabase, boundGenericsHandler, protocolContext, isParameter: false);

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
        private void EmitInterfaceSubscript(CSharpWriter csWriter, SubscriptDecl subscriptDecl, ITypeDatabase typeDatabase, ClosureHandler closureHandler, ProtocolDecl? protocolContext = null)
        {
            var boundGenericsHandler = new BoundGenericsHandler(typeDatabase);
            NameProvider.DeduplicateParameterNamesForParameterList(subscriptDecl.IndexParameters);

            // Resolve return type using factory-first projection
            var returnTypeName = GetCSharpTypeName(subscriptDecl.ReturnTypeSpec, typeDatabase, boundGenericsHandler, protocolContext, isParameter: false);

            // Build index parameters
            var parameters = new List<string>();
            foreach (var param in subscriptDecl.IndexParameters)
            {
                var paramTypeName = GetCSharpTypeName(param.SwiftTypeSpec, typeDatabase, boundGenericsHandler, protocolContext);
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
        private void EmitInterfaceMethod(CSharpWriter csWriter, MethodDecl methodDecl, ITypeDatabase typeDatabase, ClosureHandler closureHandler, ProtocolDecl? protocolContext = null, IReadOnlySet<string>? propertyNames = null)
        {
            // Note: Constructor, static, duplicate, and AnyType generic arg checks
            // are handled at the loop level in Emit(). This method is only called
            // for methods that pass all pre-checks.

            var boundGenericsHandler = new BoundGenericsHandler(typeDatabase);
            NameProvider.DeduplicateParameterNames(methodDecl.CSSignature);

            // Get return type using factory-first projection
            var returnType = "void";
            if (methodDecl.CSSignature.Count > 0)
            {
                var returnArg = methodDecl.CSSignature[0];
                if (returnArg.SwiftTypeSpec is not TupleTypeSpec tuple || !tuple.IsEmptyTuple)
                {
                    returnType = GetCSharpTypeName(returnArg.SwiftTypeSpec, typeDatabase, boundGenericsHandler, protocolContext, isParameter: false);
                }
            }

            // Build parameters (skip first which is return type)
            var parameters = new List<string>();
            for (int i = 1; i < methodDecl.CSSignature.Count; i++)
            {
                var arg = methodDecl.CSSignature[i];
                var argTypeName = GetCSharpTypeName(arg.SwiftTypeSpec, typeDatabase, boundGenericsHandler, protocolContext);
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

            var isSelfReturning = MethodEnvironment.IsSelfReturningMethod(methodDecl);
            var methodName = NameProvider.GetPublicMethodName(methodDecl.Name, methodDecl.IsAsync, hasReturnValue: hasReturnValue,
                propertyNames: propertyNames, isSelfReturning: isSelfReturning,
                parameterCount: methodDecl.CSSignature.Skip(1).Count(a => !DefaultParameterOverloadEmitter.IsDebugParameter(a)));
            XmlDocCommentEmitter.EmitMethodDocComment(csWriter, methodDecl);
            csWriter.WriteLine($"{returnType} {methodName}({string.Join(", ", parameters)});");
        }

        /// <summary>
        /// Gets the C# type name for a Swift type specification, handling bound generics and associated types.
        /// For protocol interfaces, this also handles closures, tuples, and existentials with relaxed requirements
        /// since we're just emitting signatures, not PInvoke implementations.
        /// </summary>
        private string GetCSharpTypeName(TypeSpec typeSpec, ITypeDatabase typeDatabase, BoundGenericsHandler boundGenericsHandler, ProtocolDecl? protocolContext = null, bool isParameter = true)
        {
            // Handle associated type references (e.g., Self.Element, τ_0_0.Element)
            if (typeSpec is AssociatedTypeReferenceSpec assocRef)
            {
                return ProtocolSignatureHelper.MapAssociatedTypeToGenericParam(assocRef, protocolContext);
            }

            // Factory-first with GenericContext: handles all types
            // For Self-requirement protocols, map τ_0_0 → TSelf
            var genericContext = protocolContext?.HasSelfRequirement == true
                ? GenericContext.ForProtocolSelf()
                : GenericContext.Empty;

            var factory = new TypeProjectionFactory();
            var projection = factory.Project(typeSpec, new ProjectionContext
            {
                TypeDatabase = typeDatabase,
                IsParameter = isParameter,
                GenericContext = genericContext,
                CompositionCollector = _compositionCollector
            });
            if (projection != null)
                return projection.PublicType;

            // Closure fallback when factory can't fully resolve (e.g., inner types not in TypeDatabase)
            if (typeSpec is ClosureTypeSpec closureType)
                return GetClosureCSharpType(closureType, typeDatabase, protocolContext);

            // Tuple fallback
            if (typeSpec is TupleTypeSpec tupleType)
            {
                if (tupleType.IsEmptyTuple) return "void";
                var elements = tupleType.Elements.Select(e => GetCSharpTypeName(e, typeDatabase, boundGenericsHandler, protocolContext, isParameter)).ToList();
                return $"({string.Join(", ", elements)})";
            }

            // Bound generic fallback: produce full type name with generic args
            // (e.g., BatchedCollection<Swift.AnyType> for unknown inner types).
            // Factory returns null when inner types can't be projected (existentials, unknown types).
            if (typeSpec is NamedTypeSpec boundGeneric && boundGeneric.ContainsGenericParameters)
                return boundGenericsHandler.TranslateBoundGenericTypeToCSharp(typeSpec, GenericContext.Empty);

            // Type record fallback
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

        private string BuildEmittedSignature(MethodDecl methodDecl, ITypeDatabase typeDatabase, ProtocolDecl? protocolContext, IReadOnlySet<string>? propertyNames = null)
        {
            var boundGenericsHandler = new BoundGenericsHandler(typeDatabase);

            var returnTypeSpec = methodDecl.CSSignature.FirstOrDefault()?.SwiftTypeSpec;
            bool hasReturnValue = returnTypeSpec != null && !returnTypeSpec.IsEmptyTuple;
            var isSelfReturning = MethodEnvironment.IsSelfReturningMethod(methodDecl);
            var methodName = NameProvider.GetPublicMethodName(methodDecl.Name, methodDecl.IsAsync, hasReturnValue: hasReturnValue,
                propertyNames: propertyNames, isSelfReturning: isSelfReturning,
                parameterCount: methodDecl.CSSignature.Skip(1).Count(a => !DefaultParameterOverloadEmitter.IsDebugParameter(a)));

            var paramTypes = new List<string>();
            for (int i = 1; i < methodDecl.CSSignature.Count; i++)
            {
                var arg = methodDecl.CSSignature[i];
                var paramType = GetCSharpTypeName(arg.SwiftTypeSpec, typeDatabase, boundGenericsHandler, protocolContext, isParameter: true);
                paramType = ProtocolSignatureHelper.NormalizeParamTypeForOverloadIdentity(paramType, arg.SwiftTypeSpec, typeDatabase);
                paramTypes.Add(paramType);
            }

            return $"{methodName}({string.Join(",", paramTypes)})";
        }

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
