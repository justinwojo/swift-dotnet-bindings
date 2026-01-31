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

            // Get generic type parts if this is a generic type
            var typeNameWithGenerics = GenericTypeEmitter.GetTypeNameWithGenerics(structDecl);
            var whereClause = GenericTypeEmitter.GetWhereClause(structDecl);

            var interfaces = new List<string> {
                typeof(ISwiftObject).Name,
            };
            if (implementsEquatable)
            {
                interfaces.Add($"IEquatable<{typeNameWithGenerics}>");
            }

            if (isProjectedAsClass)
            {
                // Use unsafe class since methods may use function pointers for closure parameters
                var classDeclaration = $"public unsafe class {typeNameWithGenerics} : {string.Join(", ", interfaces)}";
                if (!string.IsNullOrEmpty(whereClause))
                    classDeclaration += $" {whereClause}";
                csWriter.WriteLine(classDeclaration);
                csWriter.WriteLine("{");
                csWriter.Indent++;

                // Payload used for reference counting
                csWriter.WriteLine($"private SwiftSafeHandle<{typeNameWithGenerics}> _payload = SwiftSafeHandle<{typeNameWithGenerics}>.Zero;");
                csWriter.WriteLine();
                csWriter.WriteLine($"public SwiftSafeHandle<{typeNameWithGenerics}> Payload => _payload;");
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
                var structDeclaration = $"public unsafe struct {typeNameWithGenerics} : {string.Join(", ", interfaces)}";
                if (!string.IsNullOrEmpty(whereClause))
                    structDeclaration += $" {whereClause}";
                csWriter.WriteLine(structDeclaration);
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

            // Collect property names for method/property collision detection
            var propertyNames = new HashSet<string>(structDecl.Properties.Select(p => NameProvider.GetPropertyName(p.Name)));

            base.HandleBaseDecl(csWriter, swiftWriter, structDecl.Types, conductor, env.TypeDatabase);
            base.HandleBaseDecl(csWriter, swiftWriter, structDecl.Methods, conductor, env.TypeDatabase, propertyNames);

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

            // Get generic type parts if this is a generic type
            var typeNameWithGenerics = GenericTypeEmitter.GetTypeNameWithGenerics(structDecl);
            var whereClause = GenericTypeEmitter.GetWhereClause(structDecl);

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

            // Collect property names for method/property collision detection
            var propertyNames = new HashSet<string>(structDecl.Properties.Select(p => NameProvider.GetPropertyName(p.Name)));

            base.HandleBaseDecl(csWriter, swiftWriter, structDecl.Types, conductor, env.TypeDatabase);
            base.HandleBaseDecl(csWriter, swiftWriter, structDecl.Methods, conductor, env.TypeDatabase, propertyNames);

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
            var moduleDecl = classDecl.ModuleDecl ?? throw new ArgumentNullException(nameof(classDecl.ModuleDecl));

            // Check for equality/inequality operators (explicit or synthesized)
            bool hasEquality = OperatorHandler.WillHaveEqualityOperator(classDecl.Operators);
            bool hasInequality = OperatorHandler.WillHaveInequalityOperator(classDecl.Operators);
            bool implementsEquatable = classDecl.Conformances.Any(c => c.Protocol.Name == "Equatable");

            // Get generic type parts if this is a generic type
            var typeNameWithGenerics = GenericTypeEmitter.GetTypeNameWithGenerics(classDecl);
            var whereClause = GenericTypeEmitter.GetWhereClause(classDecl);

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
            WriteClassPrivateFields(csWriter, classDecl);
            WriteClassPayload(csWriter, classDecl);

            // Emit operators
            var operatorHandler = new OperatorHandler(_logger);
            foreach (var operatorDecl in classDecl.Operators)
            {
                if (OperatorHandler.IsSupportedOperator(operatorDecl.OperatorSymbol))
                {
                    operatorHandler.EmitOperator(csWriter, operatorDecl, env.TypeDatabase);
                }
            }
            // Handle paired operators (e.g., if == is defined but != is not)
            operatorHandler.ValidateAndEmitPairs(csWriter, classDecl.Operators, classDecl.Name);

            // Emit ISwiftObject implementation
            var iSwiftObjectWriter = new ClassISwiftObjectMethodWriter(csWriter, env.TypeDatabase, moduleDecl, classDecl);
            var equatableWriter = new ClassEqualityMethodsWriter(csWriter, classDecl, hasEquality, hasInequality);

            equatableWriter.WriteSwiftEquatableImplementation();
            iSwiftObjectWriter.WriteClassImplementation();

            // Collect property names for method/property collision detection
            var propertyNames = new HashSet<string>(classDecl.Properties.Select(p => NameProvider.GetPropertyName(p.Name)));

            base.HandleBaseDecl(csWriter, swiftWriter, classDecl.Types, conductor, env.TypeDatabase);
            base.HandleBaseDecl(csWriter, swiftWriter, classDecl.Methods, conductor, env.TypeDatabase, propertyNames);

            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.WriteLine();
        }

        /// <summary>
        /// Writes the private fields for the class.
        /// </summary>
        private static void WriteClassPrivateFields(CSharpWriter csWriter, ClassDecl classDecl)
        {
            csWriter.WriteLine($"static nuint _payloadSize = SwiftObjectHelper<{classDecl.Name}>.GetTypeMetadata().Size;");
            csWriter.WriteLine($"SwiftSafeHandle<{classDecl.Name}> _payload = SwiftSafeHandle<{classDecl.Name}>.Zero;");
            csWriter.WriteLine();
        }

        /// <summary>
        /// Writes the payload accessor for the class.
        /// </summary>
        private static void WriteClassPayload(CSharpWriter csWriter, ClassDecl classDecl)
        {
            csWriter.WriteLine($"public SwiftSafeHandle<{classDecl.Name}> Payload => _payload;");
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
    /// Class responsible for emitting the necessary code for ISwiftObject methods for classes.
    /// </summary>
    class ClassISwiftObjectMethodWriter
    {
        private readonly IndentedTextWriter _writer;
        private readonly ITypeDatabase _typeDatabase;
        private readonly ModuleDecl _moduleDecl;
        private readonly ClassDecl _classDecl;

        public ClassISwiftObjectMethodWriter(CSharpWriter csWriter, ITypeDatabase typeDatabase, ModuleDecl moduleDecl, ClassDecl classDecl)
        {
            _writer = csWriter;
            _typeDatabase = typeDatabase;
            _moduleDecl = moduleDecl;
            _classDecl = classDecl;
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
            _writer.WriteLine("static TypeMetadata ISwiftObject.GetTypeMetadata() => PInvoke_getMetadata();");
            _writer.WriteLine();

            string libPath = _typeDatabase.GetLibraryPath(_moduleDecl.Name);
            // For classes, the metadata accessor is the mangled name + "Ma"
            string metadataAccessor = $"{_classDecl.MangledName}Ma";

            var pinvokeText = $$"""
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("{{libPath}}", EntryPoint = "{{metadataAccessor}}")]
            internal static extern TypeMetadata PInvoke_getMetadata();
            """;

            _writer.WriteLines(pinvokeText);
            _writer.WriteLine();
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
            var text = $$"""
            {{_classDecl.Name}}(SwiftHandle handle)
            {
                _payload = new SwiftSafeHandle<{{_classDecl.Name}}>(handle);
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
                var metadata = SwiftObjectHelper<{{_classDecl.Name}}>.GetTypeMetadata();
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
            var crossModuleSupportedProtocols = new HashSet<string> // TODO: Remove this once we process multiple modules
            {
                { "Swift.Equatable"},
            };
            var libPath = _typeDatabase.GetLibraryPath(_moduleDecl.Name);
            var entries = new List<string>();

            foreach (var conformance in _classDecl.Conformances)
            {
                if (conformance.Protocol.Module != _moduleDecl.Name && !crossModuleSupportedProtocols.Contains(conformance.Protocol.ModuleQualifiedName))
                {
                    continue;
                }

                var protocol = NameProvider.GetInterfaceName(conformance.Protocol.Name, _classDecl.Name);
                var protocolConformanceSymbol = conformance.ProtocolConformanceDescriptor;

                entries.Add($"{{typeof({protocol}), \"{protocolConformanceSymbol}\"}}");
            }

            return string.Join(",\n", entries);
        }
    }

    /// <summary>
    /// Class responsible for emitting equality methods for class types.
    /// </summary>
    public class ClassEqualityMethodsWriter
    {
        private readonly IndentedTextWriter _writer;
        private readonly ClassDecl _classDecl;
        private readonly bool _implementsEquatable;
        private readonly bool _hasExplicitEqualityOperator;
        private readonly bool _hasExplicitInequalityOperator;

        public ClassEqualityMethodsWriter(CSharpWriter csWriter, ClassDecl classDecl, bool hasExplicitEqualityOperator, bool hasExplicitInequalityOperator)
        {
            _writer = csWriter;
            _classDecl = classDecl;
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
            var equalsMethods = $$"""
            public override bool Equals(object? obj)
            {
                return obj is {{_classDecl.Name}} other && Swift.Runtime.SwiftEquatable.Equals(this, other);
            }

            public override int GetHashCode()
            {
                throw new InvalidOperationException("Type {{_classDecl.Name}} does not implement Swift's Hashable protocol, so GetHashCode() is not supported.");
            }
            """;

            _writer.WriteLines(equalsMethods);
            _writer.WriteLine();

            // Only write operator == if no explicit operator is defined
            if (!_hasExplicitEqualityOperator)
            {
                var equalityOperator = $$"""
                public static bool operator ==({{_classDecl.Name}} left, {{_classDecl.Name}} right)
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
                public static bool operator !=({{_classDecl.Name}} left, {{_classDecl.Name}} right)
                {
                    return !Swift.Runtime.SwiftEquatable.Equals(left, right);
                }
                """;
                _writer.WriteLines(inequalityOperator);
                _writer.WriteLine();
            }

            // Write the IEquatable<T>.Equals method
            var equatableEquals = $$"""
            public bool Equals({{_classDecl.Name}}? other)
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
            if (!_hasExplicitEqualityOperator)
            {
                var equalityOperator = $$"""
                public static bool operator ==({{_classDecl.Name}} left, {{_classDecl.Name}} right)
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
                public static bool operator !=({{_classDecl.Name}} left, {{_classDecl.Name}} right)
                {
                    throw new InvalidOperationException("Type {{_classDecl.Name}} does not implement Swift's Equatable protocol, so equality comparison is not supported.");
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
            var emittedSubscripts = new HashSet<string>();

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
                EmitInterfaceProperty(csWriter, propertyDecl, env.TypeDatabase, protocolDecl);
            }

            // Emit subscripts as interface indexers
            foreach (var subscriptDecl in protocolDecl.Subscripts)
            {
                // Create a unique key for the subscript based on index parameter types
                var subscriptKey = GetSubscriptSignatureKey(subscriptDecl, env.TypeDatabase, protocolDecl);
                if (emittedSubscripts.Contains(subscriptKey))
                {
                    _logger.LogDebug($"Skipping duplicate subscript in interface {protocolDecl.Name}");
                    continue;
                }
                emittedSubscripts.Add(subscriptKey);
                EmitInterfaceSubscript(csWriter, subscriptDecl, env.TypeDatabase, protocolDecl);
            }

            // Emit methods as interface members
            foreach (var methodDecl in protocolDecl.Methods)
            {
                // Create a unique key for the method (name + parameter types)
                var methodKey = GetMethodSignatureKey(methodDecl, env.TypeDatabase, protocolDecl);
                if (emittedMethods.Contains(methodKey))
                {
                    _logger.LogDebug($"Skipping duplicate method '{methodDecl.Name}' in interface {protocolDecl.Name}");
                    continue;
                }
                emittedMethods.Add(methodKey);
                EmitInterfaceMethod(csWriter, methodDecl, env.TypeDatabase, protocolDecl);
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

            csWriter.WriteLine($"{csharpTypeName} {propertyDecl.Name} {accessors}");
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

            // Skip static methods for now - they can be in C# 8+ interfaces but require implementation
            if (methodDecl.MethodType == MethodType.Static)
            {
                _logger.LogDebug($"Skipping static method '{methodDecl.Name}' in interface - static interface members require implementation.");
                return;
            }

            var boundGenericsHandler = new BoundGenericsHandler(typeDatabase);

            // Get return type
            var returnType = "void";
            if (methodDecl.CSSignature.Count > 0)
            {
                var returnArg = methodDecl.CSSignature[0];
                if (returnArg.SwiftTypeSpec is not TupleTypeSpec tuple || !tuple.IsEmptyTuple)
                {
                    returnType = GetCSharpTypeName(returnArg.SwiftTypeSpec, typeDatabase, boundGenericsHandler, protocolContext);
                }
            }

            // Build parameters (skip first which is return type)
            var parameters = new List<string>();
            for (int i = 1; i < methodDecl.CSSignature.Count; i++)
            {
                var arg = methodDecl.CSSignature[i];
                var argTypeName = GetCSharpTypeName(arg.SwiftTypeSpec, typeDatabase, boundGenericsHandler, protocolContext);
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


            // Use unsafe class since methods may use function pointers
            csWriter.WriteLine($"public unsafe class {enumDecl.Name} : {typeof(ISwiftObject).Name}");
            csWriter.WriteLine("{");
            csWriter.Indent++;

            // Emit payload field and property - enums need this for property accessors
            csWriter.WriteLine($"static nuint _payloadSize = SwiftObjectHelper<{enumDecl.Name}>.GetTypeMetadata().Size;");
            csWriter.WriteLine($"SwiftSafeHandle<{enumDecl.Name}> _payload = SwiftSafeHandle<{enumDecl.Name}>.Zero;");
            csWriter.WriteLine($"public SwiftSafeHandle<{enumDecl.Name}> Payload => _payload;");
            csWriter.WriteLine();

            // Emit case constructors for all cases
            // Cases with associated values become static methods with P/Invoke constructors
            // Simple cases (no associated values) are NOT emitted - Swift doesn't export constructor
            // functions for them. They use RawRepresentable which requires different handling.
            foreach (var caseDecl in enumDecl.Cases)
            {
                if (caseDecl.HasAssociatedValues)
                {
                    EmitEnumCaseWithAssociatedValues(csWriter, enumDecl, caseDecl, moduleDecl, env.TypeDatabase);
                }
                else
                {
                    // Skip simple enum cases - Swift only exports witness table data (WC suffix),
                    // not constructor functions. Proper support requires RawRepresentable implementation.
                    _logger.LogWarning($"Skipping enum case '{enumDecl.Name}.{caseDecl.Name}' - simple enum cases without associated values are not yet supported (requires RawRepresentable).");
                }
            }

            // Add a blank line between cases and other members
            if (enumDecl.Cases.Any())
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

            // Emit ISwiftObject implementation
            var iSwiftObjectWriter = new EnumISwiftObjectMethodWriter(csWriter, env.TypeDatabase, moduleDecl, enumDecl);
            iSwiftObjectWriter.WriteEnumImplementation();

            // Collect property names for method/property collision detection
            var propertyNames = new HashSet<string>(enumDecl.Properties.Select(p => NameProvider.GetPropertyName(p.Name)));

            // Emit nested types and methods using base handler
            base.HandleBaseDecl(csWriter, swiftWriter, enumDecl.Types, conductor, env.TypeDatabase);
            base.HandleBaseDecl(csWriter, swiftWriter, enumDecl.Methods.Where(m => !m.IsConstructor).ToList(), conductor, env.TypeDatabase, propertyNames);

            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.WriteLine();
        }

        /// <summary>
        /// Emits a static property for a simple enum case (no associated values).
        /// </summary>
        private void EmitEnumCase(CSharpWriter csWriter, EnumDecl enumDecl, EnumCaseDecl caseDecl, ModuleDecl moduleDecl, ITypeDatabase typeDatabase)
        {
            var caseName = caseDecl.Name;
            var enumTypeName = enumDecl.Name;
            var capitalizedName = char.ToUpper(caseName[0]) + caseName.Substring(1);
            var pInvokeName = $"PInvoke_{capitalizedName}";
            var libPath = typeDatabase.GetLibraryPath(moduleDecl.Name);

            // Generate a static property for this case with backing P/Invoke
            csWriter.WriteLine($"/// <summary>");
            csWriter.WriteLine($"/// Gets the '{caseName}' case of {enumTypeName}.");
            csWriter.WriteLine($"/// </summary>");
            csWriter.WriteLine($"public static {enumTypeName} {capitalizedName}");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            csWriter.WriteLine("get");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            csWriter.WriteLine($"var result = new {enumTypeName}();");
            csWriter.WriteLine($"IntPtr casePtr = {pInvokeName}();");
            csWriter.WriteLine($"result._payload = new SwiftSafeHandle<{enumTypeName}>(casePtr);");
            csWriter.WriteLine("return result;");
            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.WriteLine();

            // P/Invoke declaration for the case constructor
            csWriter.WriteLine($"[DllImport(\"{libPath}\", EntryPoint = \"{caseDecl.MangledName}\")]");
            csWriter.WriteLine($"private static extern IntPtr {pInvokeName}();");
            csWriter.WriteLine();
        }

        /// <summary>
        /// Emits a static method for an enum case with associated values.
        /// </summary>
        private void EmitEnumCaseWithAssociatedValues(CSharpWriter csWriter, EnumDecl enumDecl, EnumCaseDecl caseDecl, ModuleDecl moduleDecl, ITypeDatabase typeDatabase)
        {
            var caseName = caseDecl.Name;
            var enumTypeName = enumDecl.Name;
            var capitalizedName = char.ToUpper(caseName[0]) + caseName.Substring(1);
            var pInvokeName = $"PInvoke_{capitalizedName}";
            var libPath = typeDatabase.GetLibraryPath(moduleDecl.Name);
            var boundGenericsHandler = new BoundGenericsHandler(typeDatabase);

            // Build parameter list from associated values
            var parameters = new List<(string type, string name, TypeSpec typeSpec)>();
            for (int i = 0; i < caseDecl.AssociatedValues.Count; i++)
            {
                var typeSpec = caseDecl.AssociatedValues[i];
                var csharpType = GetCSharpTypeNameForEnumCase(typeSpec, typeDatabase, boundGenericsHandler);

                // Check if type is unsupported
                if (csharpType == TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName)
                {
                    _logger.LogWarning($"Enum case '{enumDecl.Name}.{caseName}' has unsupported associated value type at index {i}. Skipping case.");
                    return;
                }

                // Use type label if available, otherwise generate a name
                var paramName = typeSpec.TypeLabel ?? $"value{i}";
                // Sanitize parameter name (remove invalid characters, ensure starts with letter)
                paramName = SanitizeParameterName(paramName);
                parameters.Add((csharpType, paramName, typeSpec));
            }

            // Generate the static method for this case
            csWriter.WriteLine($"/// <summary>");
            csWriter.WriteLine($"/// Creates the '{caseName}' case of {enumTypeName}.");
            csWriter.WriteLine($"/// </summary>");

            var parameterString = string.Join(", ", parameters.Select(p => $"{p.type} {p.name}"));
            csWriter.WriteLine($"public static {enumTypeName} {capitalizedName}({parameterString})");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            csWriter.WriteLine($"var result = new {enumTypeName}();");

            // Build the P/Invoke call with arguments
            var argList = new List<string>();
            for (int i = 0; i < parameters.Count; i++)
            {
                var (type, name, typeSpec) = parameters[i];
                argList.Add(GetPInvokeArgument(name, typeSpec, typeDatabase));
            }

            csWriter.WriteLine($"IntPtr casePtr = {pInvokeName}({string.Join(", ", argList)});");
            csWriter.WriteLine($"result._payload = new SwiftSafeHandle<{enumTypeName}>(casePtr);");
            csWriter.WriteLine("return result;");
            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.WriteLine();

            // P/Invoke declaration for the case constructor with associated values
            var pInvokeParams = new List<string>();
            for (int i = 0; i < parameters.Count; i++)
            {
                var (_, name, typeSpec) = parameters[i];
                var pInvokeType = GetPInvokeType(typeSpec, typeDatabase);
                pInvokeParams.Add($"{pInvokeType} {name}");
            }

            csWriter.WriteLine($"[DllImport(\"{libPath}\", EntryPoint = \"{caseDecl.MangledName}\")]");
            csWriter.WriteLine($"private static extern IntPtr {pInvokeName}({string.Join(", ", pInvokeParams)});");
            csWriter.WriteLine();
        }

        /// <summary>
        /// Gets the C# type name for an enum case associated value type.
        /// </summary>
        private static string GetCSharpTypeNameForEnumCase(TypeSpec typeSpec, ITypeDatabase typeDatabase, BoundGenericsHandler boundGenericsHandler)
        {
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

            // Handle tuple types
            if (typeSpec is TupleTypeSpec tupleType)
            {
                var tupleHandler = new TupleHandler(typeDatabase);
                // Use a recursive translator that handles bound generics for each element
                return tupleHandler.GetCSharpTupleType(tupleType, elementTypeSpec =>
                    GetCSharpTypeNameForEnumCase(elementTypeSpec, typeDatabase, boundGenericsHandler));
            }

            // For non-generic types, use the standard lookup
            return typeDatabase.GetTypeRecordOrAnyType(typeSpec).CSharpTypeName.FullyQualifiedName;
        }

        /// <summary>
        /// Gets the P/Invoke argument expression for a parameter.
        /// </summary>
        private static string GetPInvokeArgument(string paramName, TypeSpec typeSpec, ITypeDatabase typeDatabase)
        {
            // Handle tuple types - need to construct a ValueTuple with extracted payloads
            if (typeSpec is TupleTypeSpec tupleType)
            {
                var elementArgs = new List<string>();
                for (int i = 0; i < tupleType.Elements.Count; i++)
                {
                    var element = tupleType.Elements[i];
                    // Access tuple element by name if it has a label, otherwise by Item1, Item2, etc.
                    var elementAccess = !string.IsNullOrEmpty(element.TypeLabel)
                        ? $"{paramName}.{element.TypeLabel}"
                        : $"{paramName}.Item{i + 1}";

                    // Recursively get the P/Invoke argument for this element
                    elementArgs.Add(GetPInvokeArgument(elementAccess, element, typeDatabase));
                }
                return $"({string.Join(", ", elementArgs)})";
            }

            var typeRecord = typeDatabase.GetTypeRecordOrAnyType(typeSpec);

            // ObjC bridged types use .Handle to get the native pointer
            if (MarshallingHelpers.IsObjCBridged(typeRecord))
            {
                return $"{paramName}.Handle";
            }

            // For types that have payloads (non-frozen structs, classes), access the Payload.DangerousGetHandle()
            if (MarshallingHelpers.RequiresMemoryManagement(typeRecord))
            {
                return $"{paramName}.Payload.DangerousGetHandle()";
            }

            return paramName;
        }

        /// <summary>
        /// Gets the P/Invoke parameter type for an associated value.
        /// </summary>
        private static string GetPInvokeType(TypeSpec typeSpec, ITypeDatabase typeDatabase)
        {
            // Handle tuple types
            if (typeSpec is TupleTypeSpec tupleType)
            {
                var tupleHandler = new TupleHandler(typeDatabase);
                // Use recursive type translation for P/Invoke tuple elements
                return tupleHandler.GetPInvokeTupleType(tupleType, elementTypeSpec =>
                    GetPInvokeType(elementTypeSpec, typeDatabase));
            }

            var typeRecord = typeDatabase.GetTypeRecordOrAnyType(typeSpec);

            // For types that require memory management, use IntPtr in P/Invoke
            if (MarshallingHelpers.RequiresMemoryManagement(typeRecord))
            {
                return "IntPtr";
            }

            // For primitives and frozen structs, use the C# type directly
            return typeRecord.CSharpTypeName.FullyQualifiedName;
        }

        /// <summary>
        /// Sanitizes a parameter name to be a valid C# identifier.
        /// </summary>
        private static string SanitizeParameterName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return "value";

            // Replace invalid characters with underscores
            var sanitized = new System.Text.StringBuilder();
            foreach (var c in name)
            {
                if (char.IsLetterOrDigit(c) || c == '_')
                    sanitized.Append(c);
                else
                    sanitized.Append('_');
            }

            var result = sanitized.ToString();

            // Ensure starts with letter or underscore
            if (result.Length > 0 && char.IsDigit(result[0]))
                result = "_" + result;

            // Handle C# reserved keywords
            var keywords = new HashSet<string> { "string", "int", "bool", "float", "double", "object", "class", "struct", "enum", "delegate", "event", "interface", "namespace", "using", "static", "public", "private", "protected", "internal", "abstract", "sealed", "virtual", "override", "new", "return", "if", "else", "for", "foreach", "while", "do", "switch", "case", "default", "break", "continue", "goto", "throw", "try", "catch", "finally", "lock", "using", "checked", "unchecked", "fixed", "unsafe", "volatile", "extern", "ref", "out", "in", "params", "this", "base", "null", "true", "false", "is", "as", "typeof", "sizeof", "stackalloc", "await", "async", "yield", "nameof", "var", "dynamic" };

            if (keywords.Contains(result))
                result = "@" + result;

            return string.IsNullOrEmpty(result) ? "value" : result;
        }
    }

    /// <summary>
    /// Class responsible for emitting the necessary code for ISwiftObject methods for enums.
    /// </summary>
    class EnumISwiftObjectMethodWriter
    {
        private readonly IndentedTextWriter _writer;
        private readonly ITypeDatabase _typeDatabase;
        private readonly ModuleDecl _moduleDecl;
        private readonly EnumDecl _enumDecl;

        public EnumISwiftObjectMethodWriter(CSharpWriter csWriter, ITypeDatabase typeDatabase, ModuleDecl moduleDecl, EnumDecl enumDecl)
        {
            _writer = csWriter;
            _typeDatabase = typeDatabase;
            _moduleDecl = moduleDecl;
            _enumDecl = enumDecl;
        }

        /// <summary>
        /// Writes the implementation for ISwiftObject methods for enums.
        /// </summary>
        public void WriteEnumImplementation()
        {
            WriteGetTypeMetadata();
            WriteNewFromPayload();
            WriteMarshalToSwift();
            WriteGetProtocolConformanceDescriptor();
        }

        /// <summary>
        /// Writes the GetTypeMetadata method for the enum along with the PInvoke method.
        /// </summary>
        private void WriteGetTypeMetadata()
        {
            _writer.WriteLine("static TypeMetadata ISwiftObject.GetTypeMetadata() => PInvoke_getMetadata();");
            _writer.WriteLine();

            string libPath = _typeDatabase.GetLibraryPath(_moduleDecl.Name);

            var pinvokeText = $$"""
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("{{libPath}}", EntryPoint = "{{_enumDecl.MetadataAccessor}}")]
            internal static extern TypeMetadata PInvoke_getMetadata();
            """;

            _writer.WriteLines(pinvokeText);
            _writer.WriteLine();
        }

        /// <summary>
        /// Writes the NewFromPayload method for the enum.
        /// </summary>
        private void WriteNewFromPayload()
        {
            var text = $$"""
            static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
            {
                return new {{_enumDecl.Name}}(handle);
            }
            """;

            _writer.WriteLines(text);
            _writer.WriteLine();

            EmitDefaultConstructor();
            EmitPrivateConstructor();
        }

        /// <summary>
        /// Writes the default private constructor (for enum case construction).
        /// </summary>
        private void EmitDefaultConstructor()
        {
            var text = $$"""
            {{_enumDecl.Name}}()
            {
            }
            """;

            _writer.WriteLines(text);
            _writer.WriteLine();
        }

        /// <summary>
        /// Writes the private constructor accepting a SwiftHandle.
        /// </summary>
        private void EmitPrivateConstructor()
        {
            var text = $$"""
            {{_enumDecl.Name}}(SwiftHandle handle)
            {
                _payload = new SwiftSafeHandle<{{_enumDecl.Name}}>(handle);
            }
            """;

            _writer.WriteLines(text);
            _writer.WriteLine();
        }

        /// <summary>
        /// Writes the MarshalToSwift method for the enum.
        /// </summary>
        private void WriteMarshalToSwift()
        {
            var text = $$"""
            unsafe int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
            {
                var metadata = SwiftObjectHelper<{{_enumDecl.Name}}>.GetTypeMetadata();
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
        /// Writes the GetProtocolConformanceDescriptor method for the enum.
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
                    throw new SwiftRuntimeException($"Attempted to retrieve protocol conformance descriptor for type {{_enumDecl.Name}} and protocol {typeof(TProtocol).Name}, but no conformance was found.");
                }

                return ProtocolConformanceDescriptor.LoadFromSymbol("{{libPath}}", symbolName);
            }
            """;

            _writer.WriteLines(text);
            _writer.WriteLine();
        }

        /// <summary>
        /// Writes the static constructor for the enum.
        /// </summary>
        private void WriteStaticConstructor()
        {
            var text = $$"""
            private static Dictionary<Type, string> _protocolConformanceSymbols;

            static {{_enumDecl.Name}}()
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

            foreach (var conformance in _enumDecl.Conformances)
            {
                if (conformance.Protocol.Module != _moduleDecl.Name && !crossModuleSupportedProtocols.Contains(conformance.Protocol.ModuleQualifiedName))
                {
                    continue;
                }

                var protocol = NameProvider.GetInterfaceName(conformance.Protocol.Name, _enumDecl.Name);
                var protocolConformanceSymbol = conformance.ProtocolConformanceDescriptor;

                entries.Add($"{{typeof({protocol}), \"{protocolConformanceSymbol}\"}}");
            }

            return string.Join(",\n", entries);
        }
    }
}
