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
            var whereClause = GenericTypeEmitter.GetWhereClause(structDecl, env.TypeDatabase);

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
}
