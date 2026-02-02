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

            // Check for equality/inequality operators (explicit or synthesized)
            bool hasEquality = OperatorHandler.WillHaveEqualityOperator(classDecl.Operators);
            bool hasInequality = OperatorHandler.WillHaveInequalityOperator(classDecl.Operators);
            bool implementsEquatable = classDecl.Conformances.Any(c => c.Protocol.Name == "Equatable");

            // Get generic type parts if this is a generic type
            var typeNameWithGenerics = GenericTypeEmitter.GetTypeNameWithGenerics(classDecl);
            var whereClause = GenericTypeEmitter.GetWhereClause(classDecl, env.TypeDatabase);

            // Create P/Invoke helper context for generic types (to avoid CS7042)
            // Set it on the conductor so nested method handlers can access it
            var pinvokeHelperContext = PInvokeHelperContext.CreateIfGeneric(classDecl);
            var previousContext = conductor.CurrentPInvokeHelperContext;
            conductor.CurrentPInvokeHelperContext = pinvokeHelperContext;

            try
            {
                var interfaces = new List<string> {
                    typeof(ISwiftObject).Name,
                };
                if (implementsEquatable)
                {
                    interfaces.Add($"IEquatable<{typeNameWithGenerics}>");
                }

                var classDeclaration = $"public unsafe class {typeNameWithGenerics} : {string.Join(", ", interfaces)}";
                if (!string.IsNullOrEmpty(whereClause))
                    classDeclaration += $" {whereClause}";
                csWriter.WriteLine(classDeclaration);
                csWriter.WriteLine("{");
                csWriter.Indent++;

                // Emit properties
                foreach (PropertyDecl propertyDecl in classDecl.Properties)
                {
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
                foreach (var operatorDecl in classDecl.Operators)
                {
                    if (OperatorHandler.IsSupportedOperator(operatorDecl.OperatorSymbol))
                    {
                        operatorHandler.EmitOperator(csWriter, operatorDecl, env.TypeDatabase, pinvokeHelperContext);
                    }
                }
                // Handle paired operators (e.g., if == is defined but != is not)
                // Use typeNameWithGenerics to ensure generic types have proper type parameters in operator signatures
                operatorHandler.ValidateAndEmitPairs(csWriter, classDecl.Operators, typeNameWithGenerics);

                // Emit ISwiftObject implementation
                var iSwiftObjectWriter = new ClassISwiftObjectMethodWriter(csWriter, env.TypeDatabase, moduleDecl, classDecl, typeNameWithGenerics, pinvokeHelperContext);
                var equatableWriter = new ClassEqualityMethodsWriter(csWriter, classDecl, hasEquality, hasInequality);

                equatableWriter.WriteSwiftEquatableImplementation();
                iSwiftObjectWriter.WriteClassImplementation();

                // Collect property names for method/property collision detection
                // Include nested type names and containing type name for consistent naming with PropertyHandler
                var nestedTypeNames = new HashSet<string>(classDecl.Types.Select(t => t.Name));
                var propertyNames = new HashSet<string>(classDecl.Properties.Select(p =>
                    NameProvider.GetPropertyName(p.Name, nestedTypeNames, classDecl.Name)));

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
            }
        }

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
            csWriter.WriteLine($"public SwiftSafeHandle<{typeNameWithGenerics}> Payload => _payload;");
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
        private readonly PInvokeHelperContext? _pinvokeHelperContext;

        public ClassISwiftObjectMethodWriter(CSharpWriter csWriter, ITypeDatabase typeDatabase, ModuleDecl moduleDecl, ClassDecl classDecl, string typeNameWithGenerics, PInvokeHelperContext? pinvokeHelperContext = null)
        {
            _writer = csWriter;
            _typeDatabase = typeDatabase;
            _moduleDecl = moduleDecl;
            _classDecl = classDecl;
            _typeNameWithGenerics = typeNameWithGenerics;
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
                return new {{_classDecl.Name}}(handle);
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
            // Note: Constructor name uses _classDecl.Name (not generic), but SwiftSafeHandle uses _typeNameWithGenerics
            var text = $$"""
            {{_classDecl.Name}}(SwiftHandle handle)
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

            static {{_classDecl.Name}}()
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
                _classDecl.Name,
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

        public ClassEqualityMethodsWriter(CSharpWriter csWriter, ClassDecl classDecl, bool hasExplicitEqualityOperator, bool hasExplicitInequalityOperator)
        {
            _writer = csWriter;
            _classDecl = classDecl;
            // Use type name with generics for operators to fix CS0563/CS0305 errors on generic types
            _typeNameWithGenerics = GenericTypeEmitter.GetTypeNameWithGenerics(classDecl);
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
                throw new InvalidOperationException("Type {{_classDecl.Name}} does not implement Swift's Hashable protocol, so GetHashCode() is not supported.");
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
            // Always write Equals and GetHashCode methods
            // Use simple name for error messages
            var equalsMethods = $$"""
            // Swift classes cannot be compared using .NET's default equality semantics,
            // since Swift's equality is defined by the Equatable protocol.
            // This type does not implement Swift's Equatable protocol.

            public override bool Equals(object? obj)
            {
                throw new InvalidOperationException("Type {{_classDecl.Name}} does not implement Swift's Equatable protocol, so equality comparison is not supported.");
            }

            public override int GetHashCode()
            {
                throw new InvalidOperationException("Type {{_classDecl.Name}} does not implement Swift's Equatable protocol, so GetHashCode() is not supported.");
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
                    throw new InvalidOperationException("Type {{_classDecl.Name}} does not implement Swift's Equatable protocol, so equality comparison is not supported.");
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
                    throw new InvalidOperationException("Type {{_classDecl.Name}} does not implement Swift's Equatable protocol, so equality comparison is not supported.");
                }
                """;
                _writer.WriteLines(inequalityOperator);
                _writer.WriteLine();
            }
        }
    }
}
