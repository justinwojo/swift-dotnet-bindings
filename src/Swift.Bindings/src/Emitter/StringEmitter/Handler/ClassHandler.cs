// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.CodeDom.Compiler;
using Microsoft.Extensions.Logging;
using Swift.Runtime;

namespace BindingsGeneration
{
    /// <summary>
    /// Factory class for creating instances of ClassHandler.
    /// </summary>
    public class ClassHandlerFactory : HandlerFactory, IFactory<BaseDecl, ITypeHandler>
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ClassHandlerFactory"/> class.
        /// </summary>
        /// <param name="loggerFactory">The logger factory instance.</param>
        public ClassHandlerFactory(ILoggerFactory loggerFactory) : base(loggerFactory.CreateLogger<ClassHandler>())
        {
        }

        /// <summary>
        /// Determines if the factory handles the specified declaration.
        /// </summary>
        /// <param name="decl">The base declaration.</param>
        public bool Handles(BaseDecl decl)
        {
            return decl is ClassDecl;
        }

        /// <summary>
        /// Constructs a new instance of ClassHandler.
        /// </summary>
        public ITypeHandler Construct()
        {
            return new ClassHandler(_handlerLogger);
        }
    }

    /// <summary>
    /// Handler class for class declarations.
    /// </summary>
    public class ClassHandler : BaseHandler, ITypeHandler
    {
        public ClassHandler(ILogger logger) : base(logger)
        {
        }

        /// <inheritdoc/>
        public IEnvironment Marshal(BaseDecl baseDecl, ITypeDatabase typeDatabase)
        {
            if (baseDecl is not ClassDecl classDecl)
            {
                throw new ArgumentException("The provided decl must be a ClassDecl.", nameof(baseDecl));
            }
            return new TypeEnvironment(classDecl, typeDatabase);
        }

        /// <inheritdoc/>
        public void Emit(CSharpWriter csWriter, SwiftWriter swiftWriter, IEnvironment env, Conductor conductor)
        {
            var classEnv = (TypeEnvironment)env;
            var classDecl = (ClassDecl)classEnv.TypeDecl;
            var moduleDecl = classDecl.ModuleDecl ?? throw new ArgumentNullException(nameof(classDecl.ModuleDecl));

            if (GenericTypeEmitter.TryGetUnsupportedConstraint(classDecl, out var unsupportedConstraint))
            {
                var reason = unsupportedConstraint.Module == "SwiftUI"
                    ? SkipReason.SwiftUIConstraint
                    : unsupportedConstraint.Module == "Combine"
                        ? SkipReason.CombineFramework
                        : SkipReason.UnsupportedType;
                ReportCollector.RecordTypeSkipped(classDecl, reason, $"Unsupported generic constraint: {unsupportedConstraint.ModuleQualifiedName}");
                _logger.LogWarning(
                    "Skipping type '{TypeName}' - generic constraint references unsupported protocol '{Protocol}' from module '{Module}'.",
                    classDecl.Name,
                    unsupportedConstraint.Name,
                    unsupportedConstraint.Module);
                return;
            }

            ReportCollector.RecordTypeEmitted(classDecl);

            // Get generic type parts if this is a generic type
            // Pass conductor renames so nested types that were renamed by their parent appear with the correct name
            var typeNameWithGenerics = GenericTypeEmitter.GetTypeNameWithGenerics(classDecl, conductor.NestedTypeRenames);
            var whereClause = GenericTypeEmitter.GetWhereClause(classDecl, env.TypeDatabase);

            // Create P/Invoke helper context for generic types (to avoid CS7042)
            // Set it on the conductor so nested method handlers can access it
            var pinvokeHelperContext = PInvokeHelperContext.CreateIfGeneric(classDecl);
            var previousContext = conductor.CurrentPInvokeHelperContext;
            conductor.CurrentPInvokeHelperContext = pinvokeHelperContext;

            // Compute nested type renames to resolve property/nested-type name collisions
            // Instead of suffixing properties with "Value", we rename colliding nested types with "Info"
            // Note: TypeDatabase is already updated by the pre-pass in ModuleHandler; this call
            // is idempotent and returns the local rename dictionary needed for conductor.NestedTypeRenames.
            var nestedTypeRenames = NameProvider.ComputeAndApplyNestedTypeRenames(classDecl, env.TypeDatabase);
            var previousRenames = conductor.NestedTypeRenames;
            conductor.NestedTypeRenames = nestedTypeRenames;

            try
            {
                var conformanceValidator = new ProtocolConformanceValidator(moduleDecl, env.TypeDatabase);
                var interfaces = ProtocolConformanceHelper.GetImplementedInterfaces(
                    classDecl,
                    typeNameWithGenerics,
                    moduleDecl.Name,
                    env.TypeDatabase,
                    conformanceValidator);

                if (classDecl.IsActor)
                    csWriter.WriteLine("// Swift actor type - methods are actor-isolated unless marked nonisolated");

                XmlDocCommentEmitter.EmitDocComment(csWriter, classDecl);
                var classDeclaration = $"public class {typeNameWithGenerics} : {string.Join(", ", interfaces)}";
                if (!string.IsNullOrEmpty(whereClause))
                    classDeclaration += $" {whereClause}";
                csWriter.WriteLine(classDeclaration);
                csWriter.WriteLine("{");
                csWriter.Indent++;

                // Emit properties (skip unownedExecutor for actors - it's an internal actor runtime property)
                // Bug #9: Track emitted property C# names to detect duplicates.
                // Swift allows a type to have both static (from protocol conformance) and instance
                // properties with the same name, but C# does not (CS0102).
                var emittedPropertyNames = new HashSet<string>();
                foreach (PropertyDecl propertyDecl in classDecl.Properties)
                {
                    if (classDecl.IsActor && propertyDecl.Name == "unownedExecutor")
                    {
                        _logger.LogInformation($"Skipping actor runtime property 'unownedExecutor' on {classDecl.Name}.");
                        ReportCollector.RecordMemberSkipped(BindingItemKind.Property, propertyDecl.Name, classDecl, SkipReason.UnsupportedType, "Actor runtime property 'unownedExecutor' is not user-facing.");
                        continue;
                    }

                    // Bug #9: Skip duplicate property names (static + instance with same C# name)
                    var csPropertyName = NameProvider.GetPropertyName(propertyDecl.Name, classDecl.Name);
                    if (!emittedPropertyNames.Add(csPropertyName))
                    {
                        _logger.LogInformation($"Skipping duplicate property '{classDecl.Name}.{csPropertyName}' (static/instance collision).");
                        ReportCollector.RecordMemberSkipped(BindingItemKind.Property, propertyDecl.Name, classDecl, SkipReason.DuplicateSignature, $"Property '{csPropertyName}' already emitted with different staticness.");
                        continue;
                    }

                    if (conductor.TryGetPropertyHandler(propertyDecl, out var propertyHandler))
                    {
                        var propertyEnv = propertyHandler.Marshal(propertyDecl, env.TypeDatabase);
                        propertyHandler.Emit(csWriter, swiftWriter, propertyEnv, conductor);
                    }
                    else
                    {
                        _logger.LogWarning($"No handler found for property {propertyDecl.Name}");
                    }
                }

                // Emit private fields and payload
                WriteClassPrivateFields(csWriter, typeNameWithGenerics);
                WriteClassPayload(csWriter, typeNameWithGenerics);

                // Emit operators
                var operatorHandler = new OperatorHandler(_logger);
                var emittedOperatorSymbols = new HashSet<string>();
                foreach (var operatorDecl in classDecl.Operators)
                {
                    if (OperatorHandler.IsSupportedOperator(operatorDecl.OperatorSymbol))
                    {
                        if (operatorHandler.EmitOperator(csWriter, operatorDecl, env.TypeDatabase, pinvokeHelperContext))
                        {
                            emittedOperatorSymbols.Add(operatorDecl.OperatorSymbol);
                        }
                    }
                    else
                    {
                        ReportCollector.RecordMemberSkipped(BindingItemKind.Operator, operatorDecl.OperatorSymbol, classDecl, SkipReason.UnsupportedType, $"Operator '{operatorDecl.OperatorSymbol}' has no C# equivalent.");
                    }
                }
                // Handle paired operators (e.g., if == is defined but != is not)
                // Use typeNameWithGenerics to ensure generic types have proper type parameters in operator signatures
                operatorHandler.ValidateAndEmitPairs(csWriter, classDecl.Operators, typeNameWithGenerics, emittedOperatorSymbols, isReferenceType: true);

                bool hasEquality = emittedOperatorSymbols.Contains("==");
                bool hasInequality = emittedOperatorSymbols.Contains("!=");

                // Emit ISwiftObject implementation
                var iSwiftObjectWriter = new ClassISwiftObjectMethodWriter(csWriter, env.TypeDatabase, moduleDecl, classDecl, typeNameWithGenerics, pinvokeHelperContext);
                var equatableWriter = new ClassEqualityMethodsWriter(csWriter, classDecl, typeNameWithGenerics, hasEquality, hasInequality);

                equatableWriter.WriteSwiftEquatableImplementation();
                iSwiftObjectWriter.WriteClassImplementation();

                // Collect property names for method/property collision detection
                var propertyNames = new HashSet<string>(classDecl.Properties.Select(p =>
                    NameProvider.GetPropertyName(p.Name, classDecl.Name)));

                base.HandleBaseDecl(csWriter, swiftWriter, classDecl.Types, conductor, env.TypeDatabase);
                base.HandleBaseDecl(csWriter, swiftWriter, classDecl.Methods, conductor, env.TypeDatabase, propertyNames, pinvokeHelperContext);

                csWriter.Indent--;
                csWriter.WriteLine("}");
                csWriter.WriteLine();

                // Emit the P/Invoke helper class after the main class
                pinvokeHelperContext?.EmitHelperClass(csWriter);
            }
            finally
            {
                // Restore the previous context
                conductor.CurrentPInvokeHelperContext = previousContext;
                conductor.NestedTypeRenames = previousRenames;
            }
        }

        // ComputeAndApplyNestedTypeRenames is now centralized in NameProvider.

        /// <summary>
        /// Writes the private fields for the class.
        /// </summary>
        private static void WriteClassPrivateFields(CSharpWriter csWriter, string typeNameWithGenerics)
        {
            csWriter.WriteLine($"static nuint _payloadSize = SwiftObjectHelper<{typeNameWithGenerics}>.GetTypeMetadata().Size;");
            csWriter.WriteLine($"SwiftSafeHandle<{typeNameWithGenerics}> _payload = SwiftSafeHandle<{typeNameWithGenerics}>.Zero;");
            csWriter.WriteLine();
        }

        /// <summary>
        /// Writes the payload accessor for the class.
        /// </summary>
        private static void WriteClassPayload(CSharpWriter csWriter, string typeNameWithGenerics)
        {
            csWriter.WriteLine($"internal SwiftSafeHandle<{typeNameWithGenerics}> Payload => _payload;");
            csWriter.WriteLine();
            csWriter.WriteLine("public void Dispose() => _payload.Dispose();");
            csWriter.WriteLine();
        }
    }

    /// <summary>
    /// Class responsible for emitting the necessary code for ISwiftObject methods for classes.
    /// </summary>
    class ClassISwiftObjectMethodWriter
    {
        private readonly IndentedTextWriter _writer;
        private readonly ITypeDatabase _typeDatabase;
        private readonly ModuleDecl _moduleDecl;
        private readonly ClassDecl _classDecl;
        private readonly string _typeNameWithGenerics;
        private readonly string _constructorName;
        private readonly PInvokeHelperContext? _pinvokeHelperContext;

        public ClassISwiftObjectMethodWriter(CSharpWriter csWriter, ITypeDatabase typeDatabase, ModuleDecl moduleDecl, ClassDecl classDecl, string typeNameWithGenerics, PInvokeHelperContext? pinvokeHelperContext = null)
        {
            _writer = csWriter;
            _typeDatabase = typeDatabase;
            _moduleDecl = moduleDecl;
            _classDecl = classDecl;
            _typeNameWithGenerics = typeNameWithGenerics;
            var angleBracket = typeNameWithGenerics.IndexOf('<');
            _constructorName = angleBracket >= 0 ? typeNameWithGenerics.Substring(0, angleBracket) : typeNameWithGenerics;
            _pinvokeHelperContext = pinvokeHelperContext;
        }

        /// <summary>
        /// Writes the implementation for ISwiftObject methods for classes.
        /// </summary>
        public void WriteClassImplementation()
        {
            WriteGetTypeMetadata();
            WriteNewFromPayload();
            WriteMarshalToSwift();
            WriteGetProtocolConformanceDescriptor();
        }

        /// <summary>
        /// Writes the GetTypeMetadata method for the class along with the PInvoke method.
        /// </summary>
        private void WriteGetTypeMetadata()
        {
            string libPath = _typeDatabase.GetLibraryPath(_moduleDecl.Name);
            // For classes, the metadata accessor is the mangled name + "Ma"
            string metadataAccessor = $"{_classDecl.MangledName}Ma";

            if (_pinvokeHelperContext != null)
            {
                // For generic types, call the helper class with type metadata arguments
                var metadataArgs = string.Join(", ", _pinvokeHelperContext.GetMetadataArgumentList());
                _writer.WriteLine($"static TypeMetadata ISwiftObject.GetTypeMetadata() => {_pinvokeHelperContext.HelperClassName}.PInvoke_getMetadata({metadataArgs});");
                _writer.WriteLine();

                // Add the P/Invoke declaration to the helper context
                var declaration = new PInvokeDeclaration
                {
                    LibraryPath = libPath,
                    EntryPoint = metadataAccessor,
                    MethodName = "PInvoke_getMetadata",
                    ReturnType = "TypeMetadata",
                    ParametersString = "",
                    IsAsync = false,
                    MetadataParameters = _pinvokeHelperContext.GetMetadataParameterDeclarations()
                };
                _pinvokeHelperContext.AddDeclaration(declaration);
            }
            else
            {
                _writer.WriteLine("static TypeMetadata ISwiftObject.GetTypeMetadata() => PInvoke_getMetadata();");
                _writer.WriteLine();

                var pinvokeText = $$"""
                [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
                [DllImport("{{libPath}}", EntryPoint = "{{metadataAccessor}}")]
                internal static extern TypeMetadata PInvoke_getMetadata();
                """;

                _writer.WriteLines(pinvokeText);
                _writer.WriteLine();
            }
        }

        /// <summary>
        /// Writes the NewFromPayload method for the class.
        /// </summary>
        private void WriteNewFromPayload()
        {
            var text = $$"""
            static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
            {
                return new {{_typeNameWithGenerics}}(handle);
            }
            """;

            _writer.WriteLines(text);
            _writer.WriteLine();

            EmitPrivateConstructor();
        }

        /// <summary>
        /// Writes the private constructor accepting a SwiftHandle.
        /// </summary>
        private void EmitPrivateConstructor()
        {
            var text = $$"""
            {{_constructorName}}(SwiftHandle handle)
            {
                _payload = new SwiftSafeHandle<{{_typeNameWithGenerics}}>(handle);
            }
            """;

            _writer.WriteLines(text);
            _writer.WriteLine();
        }

        /// <summary>
        /// Writes the MarshalToSwift method for the class.
        /// </summary>
        private void WriteMarshalToSwift()
        {
            var text = $$"""
            unsafe int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
            {
                var metadata = SwiftObjectHelper<{{_typeNameWithGenerics}}>.GetTypeMetadata();
                if ((int)metadata.Size > swiftDestSpan.Length)
                {
                    throw new ArgumentException($"Span size does not match type size, Expected: {(int)metadata.Size}, Actual: {swiftDestSpan.Length}");
                }
                fixed (void* swiftDest = swiftDestSpan)
                {
                    // Ensure that the instance is valid before making copy
                    bool success = false;
                    _payload.DangerousAddRef(ref success);
                    try
                    {
                        metadata.ValueWitnessTable->InitializeWithCopy(swiftDest, (void*)_payload.DangerousGetHandle(), metadata);
                        return (int)metadata.Size;
                    }
                    finally
                    {
                        if (success)
                            _payload.DangerousRelease();
                    }
                }
            }
            """;

            _writer.WriteLines(text);
            _writer.WriteLine();
        }

        /// <summary>
        /// Writes the GetProtocolConformanceDescriptor method for the class.
        /// </summary>
        private void WriteGetProtocolConformanceDescriptor()
        {
            WriteStaticConstructor();
            var libPath = _typeDatabase.GetLibraryPath(_moduleDecl.Name);
            var text = $$"""
            static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
                where TProtocol : class
            {
                if (!_protocolConformanceSymbols.TryGetValue(typeof(TProtocol), out var symbolName))
                {
                    throw new SwiftRuntimeException($"Attempted to retrieve protocol conformance descriptor for type {{_classDecl.Name}} and protocol {typeof(TProtocol).Name}, but no conformance was found.");
                }

                return ProtocolConformanceDescriptor.LoadFromSymbol("{{libPath}}", symbolName);
            }
            """;

            _writer.WriteLines(text);
            _writer.WriteLine();
        }

        /// <summary>
        /// Writes the static constructor for the class.
        /// </summary>
        private void WriteStaticConstructor()
        {
            var text = $$"""
            private static Dictionary<Type, string> _protocolConformanceSymbols;

            static {{_constructorName}}()
            {
                _protocolConformanceSymbols = new Dictionary<Type, string>
                {
                    {{GenerateGetProtocolConformanceDictionaryEntries()}}
                };
            }
            """;

            _writer.WriteLines(text);
            _writer.WriteLine();
        }

        private string GenerateGetProtocolConformanceDictionaryEntries()
        {
            return ProtocolConformanceHelper.GenerateProtocolConformanceDictionaryEntries(
                _classDecl.Conformances,
                _moduleDecl.Name,
                _typeNameWithGenerics,
                _typeDatabase);
        }
    }

    /// <summary>
    /// Class responsible for emitting equality methods for class types.
    /// </summary>
    public class ClassEqualityMethodsWriter
    {
        private readonly IndentedTextWriter _writer;
        private readonly ClassDecl _classDecl;
        private readonly string _typeNameWithGenerics;
        private readonly bool _implementsEquatable;
        private readonly bool _hasExplicitEqualityOperator;
        private readonly bool _hasExplicitInequalityOperator;

        public ClassEqualityMethodsWriter(CSharpWriter csWriter, ClassDecl classDecl, string typeNameWithGenerics, bool hasExplicitEqualityOperator, bool hasExplicitInequalityOperator)
        {
            _writer = csWriter;
            _classDecl = classDecl;
            _typeNameWithGenerics = typeNameWithGenerics;
            _implementsEquatable = _classDecl.Conformances.Any(c => c.Protocol.Name == "Equatable");
            _hasExplicitEqualityOperator = hasExplicitEqualityOperator;
            _hasExplicitInequalityOperator = hasExplicitInequalityOperator;
        }

        public void WriteSwiftEquatableImplementation()
        {
            if (_implementsEquatable)
            {
                WriteSwiftEquatableImplementationWithSwiftEquals();
            }
            else
            {
                WriteDefaultEquatableImplementation();
            }
        }

        private void WriteSwiftEquatableImplementationWithSwiftEquals()
        {
            // Always write Equals and GetHashCode methods
            // Use typeNameWithGenerics for is-check
            var equalsMethods = $$"""
            public override bool Equals(object? obj)
            {
                return obj is {{_typeNameWithGenerics}} other && Swift.Runtime.SwiftEquatable.Equals(this, other);
            }

            public override int GetHashCode()
            {
                // TODO: Implement when Swift Hashable protocol binding is supported.
                // Returning constant 0 satisfies the Equals/GetHashCode contract
                // (equal objects must have equal hashes). This is correct but makes
                // hash-based collections O(n) until Hashable is supported.
                return 0;
            }
            """;

            _writer.WriteLines(equalsMethods);
            _writer.WriteLine();

            // Only write operator == if no explicit operator is defined
            // Use typeNameWithGenerics for operator parameters to fix CS0563/CS0305
            if (!_hasExplicitEqualityOperator)
            {
                var equalityOperator = $$"""
                public static bool operator ==({{_typeNameWithGenerics}} left, {{_typeNameWithGenerics}} right)
                {
                    return Swift.Runtime.SwiftEquatable.Equals(left, right);
                }
                """;
                _writer.WriteLines(equalityOperator);
                _writer.WriteLine();
            }

            // Only write operator != if no explicit operator is defined
            if (!_hasExplicitInequalityOperator)
            {
                var inequalityOperator = $$"""
                public static bool operator !=({{_typeNameWithGenerics}} left, {{_typeNameWithGenerics}} right)
                {
                    return !Swift.Runtime.SwiftEquatable.Equals(left, right);
                }
                """;
                _writer.WriteLines(inequalityOperator);
                _writer.WriteLine();
            }

            // Write the IEquatable<T>.Equals method - use typeNameWithGenerics
            var equatableEquals = $$"""
            public bool Equals({{_typeNameWithGenerics}}? other)
            {
                return Swift.Runtime.SwiftEquatable.Equals(this, other);
            }
            """;

            _writer.WriteLines(equatableEquals);
            _writer.WriteLine();
        }

        private void WriteDefaultEquatableImplementation()
        {
            // Non-Equatable types: no Equals/GetHashCode/operator overrides.
            // Classes inherit reference equality from object.
        }
    }
}
