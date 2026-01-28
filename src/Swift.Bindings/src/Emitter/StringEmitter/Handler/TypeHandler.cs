// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.CodeDom.Compiler;
using Microsoft.Extensions.Logging;
using Swift.Runtime;

namespace BindingsGeneration
{
    /// <summary>
    /// Factory class for creating instances of FrozenStructHandler.
    /// </summary>
    public class FrozenStructHandlerFactory : HandlerFactory, IFactory<BaseDecl, ITypeHandler>
    {

        /// <summary>
        /// Initializes a new instance of the <see cref="FrozenStructHandlerFactory"/> class.
        /// </summary>
        /// <param name="loggerFactory">The logger factory instance.</param>
        public FrozenStructHandlerFactory(ILoggerFactory loggerFactory) : base(loggerFactory.CreateLogger<FrozenStructHandler>())
        {
        }

        /// <summary>
        /// Determines if the factory handles the specified declaration.
        /// </summary>
        /// <param name="decl">The base declaration.</param>
        public bool Handles(BaseDecl decl)
        {
            return decl is StructDecl structDecl && structDecl.IsFrozen;
        }

        /// <summary>
        /// Constructs a new instance of StructHandler.
        /// </summary>
        public ITypeHandler Construct()
        {
            return new FrozenStructHandler(_handlerLogger);
        }
    }

    /// <summary>
    /// Handler class for frozen struct declarations.
    /// </summary>
    public class FrozenStructHandler : BaseHandler, ITypeHandler
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="FrozenStructHandler"/> class.
        /// </summary>
        /// <param name="logger">The logger instance.</param>
        /// <remarks>
        public FrozenStructHandler(ILogger logger) : base(logger)
        {
        }

        /// <inheritdoc/>
        public IEnvironment Marshal(BaseDecl baseDecl, ITypeDatabase typeDatabase)
        {
            if (baseDecl is not StructDecl structDecl)
            {
                throw new ArgumentException("The provided decl must be a StructDecl.", nameof(baseDecl));
            }
            return new TypeEnvironment(structDecl, typeDatabase);
        }

        /// <inheritdoc/>
        public void Emit(CSharpWriter csWriter, SwiftWriter swiftWriter, IEnvironment env, Conductor conductor)
        {
            var structEnv = (TypeEnvironment)env;
            var structDecl = (StructDecl)structEnv.TypeDecl;
            var parentDecl = structDecl.ParentDecl ?? throw new ArgumentNullException(nameof(structDecl.ParentDecl));
            var moduleDecl = structDecl.ModuleDecl ?? throw new ArgumentNullException(nameof(structDecl.ParentDecl));
            // Retrieve type info from the type database
            var typeRecord = env.TypeDatabase.GetTypeRecordOrThrow(structDecl.SwiftTypeName);
            bool isProjectedAsClass = MarshallingHelpers.IsFrozenStructProjectedAsClass(typeRecord!);

            // Check for equality/inequality operators (explicit or synthesized)
            bool hasEquality = OperatorHandler.WillHaveEqualityOperator(structDecl.Operators);
            bool hasInequality = OperatorHandler.WillHaveInequalityOperator(structDecl.Operators);

            var ISwiftObjectMethodWriter = new ISwiftObjectMethodWriter(csWriter, env.TypeDatabase, moduleDecl, structDecl);
            var SwiftEquatableMethodWriter = new EqualityMethodsWriter(csWriter, structDecl, isProjectedAsClass, hasEquality, hasInequality);
            bool implementsEquatable = structDecl.Conformances.Any(c => c.Protocol.Name == "Equatable");

            SwiftTypeInfo? swiftTypeInfo = typeRecord?.SwiftTypeInfo;

            var interfaces = new List<string> {
                typeof(ISwiftObject).Name,
            };
            if (implementsEquatable)
            {
                interfaces.Add($"IEquatable<{structDecl.Name}>");
            }

            if (isProjectedAsClass)
            {
                // Use unsafe class since methods may use function pointers for closure parameters
                csWriter.WriteLine($"public unsafe class {structDecl.Name} : {string.Join(", ", interfaces)}");
                csWriter.WriteLine("{");
                csWriter.Indent++;

                // Payload used for reference counting
                csWriter.WriteLine($"private SwiftSafeHandle<{structDecl.Name}> _payload = SwiftSafeHandle<{structDecl.Name}>.Zero;");
                csWriter.WriteLine();
                csWriter.WriteLine($"public SwiftSafeHandle<{structDecl.Name}> Payload => _payload;");
            }

            if (swiftTypeInfo.HasValue)
            {
                unsafe
                {
                    // Apply struct layout attributes
                    // TODO: refactor to use type metadata
                    csWriter.WriteLine($"[StructLayout(LayoutKind.Sequential, Size = {swiftTypeInfo.Value.ValueWitnessTable->Size})]");
                }
            }
            if (isProjectedAsClass)
            {
                csWriter.WriteLine($"public struct Buffer {{");
            }
            else
            {
                csWriter.WriteLine($"public unsafe struct {structDecl.Name} : {string.Join(", ", interfaces)}");
                csWriter.WriteLine("{");
            }
            csWriter.Indent++;

            csWriter.WriteLine(@"
            // For frozen structs, we need to emit fields that match the Swift struct's memory layout exactly.
            // These backing fields are required for proper memory layout and marshalling, even though they
            // are never directly accessed from C# code. The actual value access happens through Swift's
            // accessor methods.
            //
            // Important: Direct access to these fields from C# will not provide the correct value - always
            // use the generated property accessors which call into Swift.");

            foreach (PropertyDecl propertyDecl in structDecl.Properties)
            {
                if (propertyDecl.HasStorage)
                {
                    var fieldRecord = env.TypeDatabase.GetTypeRecordOrThrow(propertyDecl.SwiftTypeSpec);
                    if ((fieldRecord.Flags & TypeRecordFlags.RequiresMemoryManagement) != 0)
                    {
                        csWriter.WriteLine($"private IntPtr {propertyDecl.Name}_;  // Note: Do not access this field directly - use the property accessors");
                    }
                    else
                    {
                        csWriter.WriteLine($"private {fieldRecord.CSharpTypeName.FullyQualifiedName} {propertyDecl.Name}_;  // Note: Do not access this field directly - use the property accessors");
                    }
                }
            }

            if (isProjectedAsClass)
            {
                // Payload used for lowering at PInvoke boundary
                csWriter.Indent -= 2;
                csWriter.WriteLine("}");
                csWriter.WriteLine();
                csWriter.WriteLine($"public unsafe PayloadBuffer<{structDecl.Name}.Buffer> PayloadBuffer => new PayloadBuffer<{structDecl.Name}.Buffer>(_payload);");
                csWriter.WriteLine();
            }

            foreach (PropertyDecl propertyDecl in structDecl.Properties)
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
            csWriter.WriteLine();

            // Emit operators
            var operatorHandler = new OperatorHandler(_logger);
            foreach (var operatorDecl in structDecl.Operators)
            {
                if (OperatorHandler.IsSupportedOperator(operatorDecl.OperatorSymbol))
                {
                    operatorHandler.EmitOperator(csWriter, operatorDecl, env.TypeDatabase);
                }
            }
            // Handle paired operators (e.g., if == is defined but != is not)
            operatorHandler.ValidateAndEmitPairs(csWriter, structDecl.Operators, structDecl.Name);

            // Add Equatable support if the struct conforms to Equatable
            SwiftEquatableMethodWriter.WriteSwiftEquatableImplementation();
            ISwiftObjectMethodWriter.WriteFrozenStructImplementation();

            base.HandleBaseDecl(csWriter, swiftWriter, structDecl.Types, conductor, env.TypeDatabase);
            base.HandleBaseDecl(csWriter, swiftWriter, structDecl.Methods, conductor, env.TypeDatabase);

            csWriter.Indent--;
            csWriter.WriteLine("}");
        }
    }

    /// <summary>
    /// Factory class for creating instances of NonFrozenStructHandler.
    /// </summary>
    public class NonFrozenStructHandlerFactory : HandlerFactory, IFactory<BaseDecl, ITypeHandler>
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="NonFrozenStructHandlerFactory"/> class.
        /// </summary>
        /// <param name="loggerFactory">The logger factory instance.</param>
        public NonFrozenStructHandlerFactory(ILoggerFactory loggerFactory) : base(loggerFactory.CreateLogger<NonFrozenStructHandler>())
        {
        }

        /// <summary>
        /// Determines if the factory handles the specified declaration.
        /// </summary>
        /// <param name="decl">The base declaration.</param>
        public bool Handles(BaseDecl decl)
        {
            return decl is StructDecl structDecl && !structDecl.IsFrozen;
        }

        /// <summary>
        /// Constructs a new instance of StructHandler.
        /// </summary>
        public ITypeHandler Construct()
        {
            return new NonFrozenStructHandler(_handlerLogger);
        }
    }

    /// <summary>
    /// Handler class for non-frozen struct declarations.
    /// </summary>
    public class NonFrozenStructHandler : BaseHandler, ITypeHandler
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="NonFrozenStructHandler"/> class.
        /// </summary>
        /// <param name="logger">The logger instance.</param>
        public NonFrozenStructHandler(ILogger logger) : base(logger)
        {
        }

        /// <inheritdoc/>
        public IEnvironment Marshal(BaseDecl baseDecl, ITypeDatabase typeDatabase)
        {
            if (baseDecl is not StructDecl structDecl)
            {
                throw new ArgumentException("The provided decl must be a StructDecl.", nameof(baseDecl));

            }
            return new TypeEnvironment(structDecl, typeDatabase);
        }

        /// <inheritdoc/>
        public void Emit(CSharpWriter csWriter, SwiftWriter swiftWriter, IEnvironment env, Conductor conductor)
        {
            var structEnv = (TypeEnvironment)env;
            var structDecl = (StructDecl)structEnv.TypeDecl;
            var moduleDecl = structDecl.ModuleDecl ?? throw new ArgumentNullException(nameof(structDecl.ModuleDecl));

            // Check for equality/inequality operators (explicit or synthesized)
            bool hasEquality = OperatorHandler.WillHaveEqualityOperator(structDecl.Operators);
            bool hasInequality = OperatorHandler.WillHaveInequalityOperator(structDecl.Operators);

            var ISwiftObjectMethodWriter = new ISwiftObjectMethodWriter(csWriter, env.TypeDatabase, moduleDecl, structDecl);
            var SwiftEquatableMethodWriter = new EqualityMethodsWriter(csWriter, structDecl, true, hasEquality, hasInequality);
            bool implementsEquatable = structDecl.Conformances.Any(c => c.Protocol.Name == "Equatable");

            var interfaces = new List<string> {
                typeof(ISwiftObject).Name,
            };
            if (implementsEquatable)
            {
                interfaces.Add($"IEquatable<{structDecl.Name}>");
            }
            csWriter.WriteLine($"public unsafe class {structDecl.Name} : {string.Join(", ", interfaces)}");
            csWriter.WriteLine("{");
            csWriter.Indent++;

            foreach (PropertyDecl propertyDecl in structDecl.Properties)
            {
                if (conductor.TryGetPropertyHandler(propertyDecl, out var propertyHandler))
                {
                    var propertyEnv = propertyHandler.Marshal(propertyDecl, env.TypeDatabase);
                    propertyHandler.Emit(csWriter, swiftWriter, propertyEnv, conductor);
                }
                else
                    _logger.LogWarning($"No handler found for field {propertyDecl.Name}");
            }

            WritePrivateFields(csWriter, structDecl);
            WritePayload(csWriter, structDecl);

            // Emit operators
            var operatorHandler = new OperatorHandler(_logger);
            foreach (var operatorDecl in structDecl.Operators)
            {
                if (OperatorHandler.IsSupportedOperator(operatorDecl.OperatorSymbol))
                {
                    operatorHandler.EmitOperator(csWriter, operatorDecl, env.TypeDatabase);
                }
            }
            // Handle paired operators (e.g., if == is defined but != is not)
            operatorHandler.ValidateAndEmitPairs(csWriter, structDecl.Operators, structDecl.Name);

            // Add Equatable support if the struct conforms to Equatable
            SwiftEquatableMethodWriter.WriteSwiftEquatableImplementation();
            ISwiftObjectMethodWriter.WriteNonFrozenStructImplementation();

            csWriter.WriteLine();

            base.HandleBaseDecl(csWriter, swiftWriter, structDecl.Types, conductor, env.TypeDatabase);
            base.HandleBaseDecl(csWriter, swiftWriter, structDecl.Methods, conductor, env.TypeDatabase);

            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.WriteLine();
        }

        /// <summary>
        /// Writes the private fields for the class.
        /// </summary>
        private static void WritePrivateFields(CSharpWriter csWriter, StructDecl structDecl)
        {
            csWriter.WriteLine($"static nuint _payloadSize = SwiftObjectHelper<{structDecl.Name}>.GetTypeMetadata().Size;");
            csWriter.WriteLine($"SwiftSafeHandle<{structDecl.Name}> _payload = SwiftSafeHandle<{structDecl.Name}>.Zero;");
            csWriter.WriteLine();
        }

        /// <summary>
        /// Writes the payload accessor for the class.
        /// </summary>
        private static void WritePayload(CSharpWriter csWriter, StructDecl structDecl)
        {
            csWriter.WriteLine($"public SwiftSafeHandle<{structDecl.Name}> Payload => _payload;");
            csWriter.WriteLine();
        }
    }

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

            var interfaces = new List<string> {
                typeof(ISwiftObject).Name,
            };

            csWriter.WriteLine($"public unsafe class {classDecl.Name} : {string.Join(", ", interfaces)} {{");
            csWriter.Indent++;


            csWriter.WriteLine($"SwiftSafeHandle<{classDecl.Name}> _payload = SwiftSafeHandle<{classDecl.Name}>.Zero;");
            csWriter.WriteLine();
            csWriter.WriteLine($"public SwiftSafeHandle<{classDecl.Name}> Payload => _payload;");
            csWriter.WriteLine();
            csWriter.WriteLine(@"
                static TypeMetadata ISwiftObject.GetTypeMetadata() => throw new NotImplementedException();

                static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
                    where TProtocol : class
                {
                    throw new NotImplementedException();
                }

                int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
                {
                    throw new NotImplementedException();
                }
                static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
                {
                    throw new NotImplementedException();
                }
            ");

            base.HandleBaseDecl(csWriter, swiftWriter, classDecl.Types, conductor, env.TypeDatabase);
            base.HandleBaseDecl(csWriter, swiftWriter, classDecl.Methods, conductor, env.TypeDatabase);

            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.WriteLine();
        }
    }

    /// <summary>
    /// Class responsible for emitting the necessary code for ISwiftObject methods.
    /// </summary>
    class ISwiftObjectMethodWriter
    {
        private readonly IndentedTextWriter _writer;
        private readonly ITypeDatabase _typeDatabase;
        private readonly ModuleDecl _moduleDecl;
        private readonly StructDecl _structDecl;

        public ISwiftObjectMethodWriter(CSharpWriter csWriter, ITypeDatabase typeDatabase, ModuleDecl moduleDecl, StructDecl structDecl)
        {
            _writer = csWriter;
            _typeDatabase = typeDatabase;
            _moduleDecl = moduleDecl;
            _structDecl = structDecl;
        }

        /// <summary>
        /// Writes the implementation for ISwiftObject methods for non-frozen structs.
        /// </summary>
        public void WriteNonFrozenStructImplementation()
        {
            WriteGetTypeMetadata();
            WriteNewFromPayloadNonFrozenStruct();
            WriteMarshalToSwiftNonFrozenStruct();
            WriteGetProtocolConformanceDescriptor();
        }

        /// <summary>
        /// Writes the implementation for ISwiftObject methods for frozen structs.
        /// </summary>
        public void WriteFrozenStructImplementation()
        {
            WriteGetTypeMetadata();
            WriteNewFromPayloadFrozenStruct();
            WriteMarshalToSwiftFrozenStruct();
            WriteGetProtocolConformanceDescriptor();
        }

        /// <summary>
        /// Writes the GetTypeMetadata method for the struct along with the PInvoke method.
        /// </summary>
        private void WriteGetTypeMetadata()
        {
            _writer.WriteLine("static TypeMetadata ISwiftObject.GetTypeMetadata() => PInvoke_getMetadata();");
            _writer.WriteLine();

            string libPath = _typeDatabase.GetLibraryPath(_moduleDecl.Name);

            var pinvokeText = $$"""
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("{{libPath}}", EntryPoint = "{{_structDecl.MetadataAccessor}}")]
            internal static extern TypeMetadata PInvoke_getMetadata();
            """;

            _writer.WriteLines(pinvokeText);
            _writer.WriteLine();
        }

        /// <summary>
        /// Writes the NewFromPayload method for the struct.
        /// </summary>
        private void WriteNewFromPayloadFrozenStruct()
        {
            TypeRecord typeRecord = _typeDatabase.GetTypeRecordOrThrow(_structDecl.SwiftTypeName);
            if (MarshallingHelpers.IsFrozenStructProjectedAsClass(typeRecord))
            {
                var text = $$"""
                static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
                {
                    return new {{_structDecl.Name}}(handle);
                }

                unsafe {{_structDecl.Name}}(IntPtr handle)
                {
                    IntPtr bufferPtr = (IntPtr)NativeMemory.Alloc((nuint)sizeof({{_structDecl.Name}}.Buffer));
                    *({{_structDecl.Name}}.Buffer*)bufferPtr = *({{_structDecl.Name}}.Buffer*)handle;
                    _payload = new SwiftSafeHandle<{{_structDecl.Name}}>(bufferPtr);
                }
                """;

                _writer.WriteLines(text);
                _writer.WriteLine();
            }
            else
            {
                var text = $$"""
                static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
                {
                    return *({{_structDecl.Name}}*)handle;
                }
                """;

                _writer.WriteLines(text);
                _writer.WriteLine();
            }
        }

        /// <summary>
        /// Writes the NewFromPayload method for the struct.
        /// </summary>
        private void WriteNewFromPayloadNonFrozenStruct()
        {
            var text = $$"""
            static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
            {
                return new {{_structDecl.Name}}(handle);
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
            {{_structDecl.Name}}(SwiftHandle handle)
            {
                _payload = new SwiftSafeHandle<{{_structDecl.Name}}>(handle);
            }
            """;

            _writer.WriteLines(text);
            _writer.WriteLine();
        }

        /// <summary>
        /// Writes the MarshalToSwift method for the struct.
        /// </summary>
        private void WriteMarshalToSwiftFrozenStruct()
        {
            TypeRecord typeRecord = _typeDatabase.GetTypeRecordOrThrow(_structDecl.SwiftTypeName);
            if (MarshallingHelpers.IsFrozenStructProjectedAsClass(typeRecord))
            {
                var text = $$"""
                unsafe int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
                {
                    var metadata = SwiftObjectHelper<{{_structDecl.Name}}>.GetTypeMetadata();
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
            }
            else
            {
                var text = $$"""
                unsafe int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
                {
                    var metadata = SwiftObjectHelper<{{_structDecl.Name}}>.GetTypeMetadata();
                    if ((int)metadata.Size > swiftDestSpan.Length)
                    {
                        throw new ArgumentException($"Span size does not match type size, Expected: {(int)metadata.Size}, Actual: {swiftDestSpan.Length}");
                    }
                    fixed (void* payload = &this)
                    fixed (void* swiftDest = swiftDestSpan)
                    {
                        metadata.ValueWitnessTable->InitializeWithCopy(swiftDest, payload, metadata);
                        return (int)metadata.Size;
                    }
                }
                """;

                _writer.WriteLines(text);
            }

            _writer.WriteLine();
        }

        /// <summary>
        /// Writes the MarshalToSwift method for the struct.
        /// </summary>
        private void WriteMarshalToSwiftNonFrozenStruct()
        {
            var text = $$"""
            unsafe int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
            {
                var metadata = SwiftObjectHelper<{{_structDecl.Name}}>.GetTypeMetadata();
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
        /// Writes the GetProtocolConformanceDescriptor method for the struct.
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
                    throw new SwiftRuntimeException($"Attempted to retrieve protocol conformance descriptor for type {{_structDecl.Name}} and protocol {typeof(TProtocol).Name}, but no conformance was found.");
                }

                return ProtocolConformanceDescriptor.LoadFromSymbol("{{libPath}}", symbolName);
            }
            """;

            _writer.WriteLines(text);
            _writer.WriteLine();
        }

        /// <summary>
        /// Writes the static constructor for the struct.
        /// </summary>
        private void WriteStaticConstructor()
        {
            var text = $$"""
            private static Dictionary<Type, string> _protocolConformanceSymbols;

            static {{_structDecl.Name}}()
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
            var crossModuleSupportedProtocols = new HashSet<string> // TODO: Remove this once we process multiple modules
            {
                { "Swift.Equatable"},
            };
            var libPath = _typeDatabase.GetLibraryPath(_moduleDecl.Name);
            var entries = new List<string>();

            foreach (var conformance in _structDecl.Conformances)
            {
                if (conformance.Protocol.Module != _moduleDecl.Name && !crossModuleSupportedProtocols.Contains(conformance.Protocol.ModuleQualifiedName))
                {
                    continue;
                }

                var protocol = NameProvider.GetInterfaceName(conformance.Protocol.Name, _structDecl.Name);
                var protocolConformanceSymbol = conformance.ProtocolConformanceDescriptor;

                entries.Add($"{{typeof({protocol}), \"{protocolConformanceSymbol}\"}}");
            }

            return string.Join(",\n", entries);
        }
    }

    public class EqualityMethodsWriter
    {
        private readonly IndentedTextWriter _writer;
        private readonly StructDecl _structDecl;
        private readonly bool _implementsEquatable;
        private readonly bool _isRefType;
        private readonly bool _hasExplicitEqualityOperator;
        private readonly bool _hasExplicitInequalityOperator;

        public EqualityMethodsWriter(CSharpWriter csWriter, StructDecl structDecl, bool refType)
            : this(csWriter, structDecl, refType, false, false)
        {
        }

        public EqualityMethodsWriter(CSharpWriter csWriter, StructDecl structDecl, bool refType, bool hasExplicitEqualityOperator, bool hasExplicitInequalityOperator)
        {
            _writer = csWriter;
            _structDecl = structDecl;
            _implementsEquatable = _structDecl.Conformances.Any(c => c.Protocol.Name == "Equatable");
            _isRefType = refType;
            _hasExplicitEqualityOperator = hasExplicitEqualityOperator;
            _hasExplicitInequalityOperator = hasExplicitInequalityOperator;
        }

        public void WriteSwiftEquatableImplementation()
        {
            if (_implementsEquatable)
            {
                WriteSwiftEquatableImplementationWithSwiftEquals(_isRefType);
            }
            else
            {
                WriteDefaultEquatableImplementation();
            }
        }

        private void WriteSwiftEquatableImplementationWithSwiftEquals(bool refType)
        {
            // Always write Equals and GetHashCode methods
            var equalsMethods = $$"""
            public override bool Equals(object? obj)
            {
                return obj is {{_structDecl.Name}} other && Swift.Runtime.SwiftEquatable.Equals(this, other);
            }

            public override int GetHashCode()
            {
                throw new InvalidOperationException("Type {{_structDecl.Name}} does not implement Swift's Hashable protocol, so GetHashCode() is not supported.");
            }
            """;

            _writer.WriteLines(equalsMethods);
            _writer.WriteLine();

            // Only write operator == if no explicit operator is defined
            if (!_hasExplicitEqualityOperator)
            {
                var equalityOperator = $$"""
                public static bool operator ==({{_structDecl.Name}} left, {{_structDecl.Name}} right)
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
                public static bool operator !=({{_structDecl.Name}} left, {{_structDecl.Name}} right)
                {
                    return !Swift.Runtime.SwiftEquatable.Equals(left, right);
                }
                """;
                _writer.WriteLines(inequalityOperator);
                _writer.WriteLine();
            }

            // Write the IEquatable<T>.Equals method
            var equatableEquals = $$"""
            public bool Equals({{_structDecl.Name}}{{(refType == true ? "?" : "")}} other)
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
            var equalsMethods = $$"""
            // Swift structs cannot be compared using .NET's default equality semantics,
            // since Swift's equality is defined by the Equatable protocol.
            // This type does not implement Swift's Equatable protocol.

            public override bool Equals(object? obj)
            {
                throw new InvalidOperationException("Type {{_structDecl.Name}} does not implement Swift's Equatable protocol, so equality comparison is not supported.");
            }

            public override int GetHashCode()
            {
                throw new InvalidOperationException("Type {{_structDecl.Name}} does not implement Swift's Equatable protocol, so GetHashCode() is not supported.");
            }
            """;

            _writer.WriteLines(equalsMethods);
            _writer.WriteLine();

            // Only write operator == if no explicit operator is defined
            if (!_hasExplicitEqualityOperator)
            {
                var equalityOperator = $$"""
                public static bool operator ==({{_structDecl.Name}} left, {{_structDecl.Name}} right)
                {
                    throw new InvalidOperationException("Type {{_structDecl.Name}} does not implement Swift's Equatable protocol, so equality comparison is not supported.");
                }
                """;
                _writer.WriteLines(equalityOperator);
                _writer.WriteLine();
            }

            // Only write operator != if no explicit operator is defined
            if (!_hasExplicitInequalityOperator)
            {
                var inequalityOperator = $$"""
                public static bool operator !=({{_structDecl.Name}} left, {{_structDecl.Name}} right)
                {
                    throw new InvalidOperationException("Type {{_structDecl.Name}} does not implement Swift's Equatable protocol, so equality comparison is not supported.");
                }
                """;
                _writer.WriteLines(inequalityOperator);
                _writer.WriteLine();
            }
        }
    }

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

            // Emit properties as interface members
            foreach (var propertyDecl in protocolDecl.Properties)
            {
                // Create a unique key for the property (name is sufficient since properties can't be overloaded)
                var propertyKey = propertyDecl.Name;
                if (emittedProperties.Contains(propertyKey))
                {
                    _logger.LogDebug($"Skipping duplicate property '{propertyDecl.Name}' in interface {protocolDecl.Name}");
                    continue;
                }
                emittedProperties.Add(propertyKey);
                EmitInterfaceProperty(csWriter, propertyDecl, env.TypeDatabase);
            }

            // Emit methods as interface members
            foreach (var methodDecl in protocolDecl.Methods)
            {
                // Create a unique key for the method (name + parameter types)
                var methodKey = GetMethodSignatureKey(methodDecl, env.TypeDatabase);
                if (emittedMethods.Contains(methodKey))
                {
                    _logger.LogDebug($"Skipping duplicate method '{methodDecl.Name}' in interface {protocolDecl.Name}");
                    continue;
                }
                emittedMethods.Add(methodKey);
                EmitInterfaceMethod(csWriter, methodDecl, env.TypeDatabase);
            }

            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.WriteLine();
        }

        /// <summary>
        /// Creates a unique signature key for a method based on name and parameter types.
        /// </summary>
        private static string GetMethodSignatureKey(MethodDecl methodDecl, ITypeDatabase typeDatabase)
        {
            var paramTypes = new List<string>();
            // Skip first element (return type) in CSSignature
            for (int i = 1; i < methodDecl.CSSignature.Count; i++)
            {
                var arg = methodDecl.CSSignature[i];
                try
                {
                    var typeRecord = typeDatabase.GetTypeRecordOrAnyType(arg.SwiftTypeSpec);
                    paramTypes.Add(typeRecord.CSharpTypeName.FullyQualifiedName);
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
        private void EmitInterfaceProperty(CSharpWriter csWriter, PropertyDecl propertyDecl, ITypeDatabase typeDatabase)
        {
            var typeRecord = typeDatabase.GetTypeRecordOrAnyType(propertyDecl.SwiftTypeSpec);
            var csharpTypeName = typeRecord.CSharpTypeName.FullyQualifiedName;

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

            csWriter.WriteLine($"{csharpTypeName} {propertyDecl.Name} {accessors}");
        }

        /// <summary>
        /// Emits a method declaration for an interface.
        /// </summary>
        private void EmitInterfaceMethod(CSharpWriter csWriter, MethodDecl methodDecl, ITypeDatabase typeDatabase)
        {
            // Skip constructors - they can't be in interfaces
            if (methodDecl.IsConstructor)
                return;

            // Skip static methods for now - they can be in C# 8+ interfaces but require implementation
            if (methodDecl.MethodType == MethodType.Static)
            {
                _logger.LogDebug($"Skipping static method '{methodDecl.Name}' in interface - static interface members require implementation.");
                return;
            }

            // Get return type
            var returnType = "void";
            if (methodDecl.CSSignature.Count > 0)
            {
                var returnArg = methodDecl.CSSignature[0];
                if (returnArg.SwiftTypeSpec is not TupleTypeSpec tuple || !tuple.IsEmptyTuple)
                {
                    var typeRecord = typeDatabase.GetTypeRecordOrAnyType(returnArg.SwiftTypeSpec);
                    returnType = typeRecord.CSharpTypeName.FullyQualifiedName;
                }
            }

            // Build parameters (skip first which is return type)
            var parameters = new List<string>();
            for (int i = 1; i < methodDecl.CSSignature.Count; i++)
            {
                var arg = methodDecl.CSSignature[i];
                var argTypeRecord = typeDatabase.GetTypeRecordOrAnyType(arg.SwiftTypeSpec);
                var argTypeName = argTypeRecord.CSharpTypeName.FullyQualifiedName;
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

            csWriter.WriteLine($"{returnType} {methodDecl.Name}({string.Join(", ", parameters)});");
        }
    }

    /// <summary>
    /// Factory class for creating instances of EnumHandler.
    /// </summary>
    public class EnumHandlerFactory : HandlerFactory, IFactory<BaseDecl, ITypeHandler>
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="EnumHandlerFactory"/> class.
        /// </summary>
        /// <param name="loggerFactory">The logger factory instance.</param>
        public EnumHandlerFactory(ILoggerFactory loggerFactory) : base(loggerFactory.CreateLogger<EnumHandler>())
        {
        }

        /// <summary>
        /// Determines if the factory handles the specified declaration.
        /// </summary>
        /// <param name="decl">The base declaration.</param>
        public bool Handles(BaseDecl decl)
        {
            return decl is EnumDecl;
        }

        /// <summary>
        /// Constructs a new instance of EnumHandler.
        /// </summary>
        public ITypeHandler Construct()
        {
            return new EnumHandler(_handlerLogger);
        }
    }

    /// <summary>
    /// Handler class for enum declarations.
    /// </summary>
    public class EnumHandler : BaseHandler, ITypeHandler
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="EnumHandler"/> class.
        /// </summary>
        /// <param name="logger">The logger instance.</param>
        public EnumHandler(ILogger logger) : base(logger)
        {
        }

        /// <inheritdoc/>
        public IEnvironment Marshal(BaseDecl baseDecl, ITypeDatabase typeDatabase)
        {
            if (baseDecl is not EnumDecl enumDecl)
            {
                throw new ArgumentException("The provided decl must be an EnumDecl.", nameof(baseDecl));
            }
            return new TypeEnvironment(enumDecl, typeDatabase);
        }

        /// <inheritdoc/>
        public void Emit(CSharpWriter csWriter, SwiftWriter swiftWriter, IEnvironment env, Conductor conductor)
        {
            var enumEnv = (TypeEnvironment)env;
            var enumDecl = (EnumDecl)enumEnv.TypeDecl;
            var parentDecl = enumDecl.ParentDecl ?? throw new ArgumentNullException(nameof(enumDecl.ParentDecl));
            var moduleDecl = enumDecl.ModuleDecl ?? throw new ArgumentNullException(nameof(enumDecl.ModuleDecl));

            // Enums with associated values are complex and need different handling
            // For now, we focus on simple enums without associated values
            if (enumDecl.HasAssociatedValueCases)
            {
                _logger.LogWarning($"Enum '{enumDecl.Name}' has associated value cases which are not fully supported yet.");
            }

            // Use unsafe class since methods may use function pointers
            csWriter.WriteLine($"public unsafe class {enumDecl.Name} : {typeof(ISwiftObject).Name}");
            csWriter.WriteLine("{");
            csWriter.Indent++;

            // Emit static case constructors for simple cases (no associated values)
            foreach (var caseDecl in enumDecl.Cases.Where(c => !c.HasAssociatedValues))
            {
                EmitEnumCase(csWriter, enumDecl, caseDecl, moduleDecl);
            }

            // Add a blank line between cases and other members
            if (enumDecl.Cases.Any(c => !c.HasAssociatedValues))
            {
                csWriter.WriteLine();
            }

            // Emit properties using the same pattern as other handlers
            foreach (var propertyDecl in enumDecl.Properties)
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

            // Emit ISwiftObject stub implementations
            EmitEnumISwiftObjectImplementation(csWriter, enumDecl);

            // Emit nested types and methods using base handler
            base.HandleBaseDecl(csWriter, swiftWriter, enumDecl.Types, conductor, env.TypeDatabase);
            base.HandleBaseDecl(csWriter, swiftWriter, enumDecl.Methods.Where(m => !m.IsConstructor).ToList(), conductor, env.TypeDatabase);

            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.WriteLine();
        }

        /// <summary>
        /// Emits stub ISwiftObject implementations for enum types.
        /// </summary>
        private static void EmitEnumISwiftObjectImplementation(CSharpWriter csWriter, EnumDecl enumDecl)
        {
            csWriter.WriteLines($@"
                static TypeMetadata ISwiftObject.GetTypeMetadata() => throw new NotImplementedException(""Enum type metadata not yet implemented"");

                static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
                    where TProtocol : class
                {{
                    throw new NotImplementedException(""Enum protocol conformance not yet implemented"");
                }}

                int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
                {{
                    throw new NotImplementedException(""Enum marshalling not yet implemented"");
                }}

                static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
                {{
                    throw new NotImplementedException(""Enum NewFromPayload not yet implemented"");
                }}
            ");
        }

        /// <summary>
        /// Emits a static property for a simple enum case (no associated values).
        /// </summary>
        private void EmitEnumCase(CSharpWriter csWriter, EnumDecl enumDecl, EnumCaseDecl caseDecl, ModuleDecl moduleDecl)
        {
            var caseName = caseDecl.Name;
            var enumTypeName = enumDecl.Name;
            var pInvokeName = $"PInvoke_{caseName}";

            // Generate a unique static property for this case
            csWriter.WriteLine($"/// <summary>");
            csWriter.WriteLine($"/// Gets the '{caseName}' case of {enumTypeName}.");
            csWriter.WriteLine($"/// </summary>");
            csWriter.WriteLine($"public static {enumTypeName} {char.ToUpper(caseName[0])}{caseName.Substring(1)} => throw new NotImplementedException(\"Enum case constructors not yet implemented\");");
            csWriter.WriteLine();
        }
    }
}
