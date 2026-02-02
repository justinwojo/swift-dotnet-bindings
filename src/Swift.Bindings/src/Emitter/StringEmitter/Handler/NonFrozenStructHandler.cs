// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.CodeDom.Compiler;
using Microsoft.Extensions.Logging;
using Swift.Runtime;

namespace BindingsGeneration
{
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

            if (GenericTypeEmitter.TryGetUnsupportedConstraint(structDecl, out var unsupportedConstraint))
            {
                var reason = unsupportedConstraint.Module == "SwiftUI"
                    ? SkipReason.SwiftUIConstraint
                    : unsupportedConstraint.Module == "Combine"
                        ? SkipReason.CombineFramework
                        : SkipReason.UnsupportedType;
                ReportCollector.RecordTypeSkipped(structDecl, reason, $"Unsupported generic constraint: {unsupportedConstraint.ModuleQualifiedName}");
                _logger.LogWarning(
                    "Skipping type '{TypeName}' - generic constraint references unsupported protocol '{Protocol}' from module '{Module}'.",
                    structDecl.Name,
                    unsupportedConstraint.Name,
                    unsupportedConstraint.Module);
                return;
            }

            ReportCollector.RecordTypeEmitted(structDecl);

            bool implementsEquatable = structDecl.Conformances.Any(c => c.Protocol.Name == "Equatable");

            // Get generic type parts if this is a generic type
            var typeNameWithGenerics = GenericTypeEmitter.GetTypeNameWithGenerics(structDecl);
            var whereClause = GenericTypeEmitter.GetWhereClause(structDecl, env.TypeDatabase);

            var ISwiftObjectMethodWriter = new ISwiftObjectMethodWriter(csWriter, env.TypeDatabase, moduleDecl, structDecl, typeNameWithGenerics);
            // Create P/Invoke helper context for generic types (to avoid CS7042)
            // Set it on the conductor so nested method handlers can access it
            var pinvokeHelperContext = PInvokeHelperContext.CreateIfGeneric(structDecl);
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

                WritePrivateFields(csWriter, typeNameWithGenerics);
                WritePayload(csWriter, typeNameWithGenerics);

                // Emit operators (operators also have P/Invoke - need to handle for generic types)
                var operatorHandler = new OperatorHandler(_logger);
                var emittedOperatorSymbols = new HashSet<string>();
                foreach (var operatorDecl in structDecl.Operators)
                {
                    if (OperatorHandler.IsSupportedOperator(operatorDecl.OperatorSymbol))
                    {
                        if (operatorHandler.EmitOperator(csWriter, operatorDecl, env.TypeDatabase, pinvokeHelperContext))
                        {
                            emittedOperatorSymbols.Add(operatorDecl.OperatorSymbol);
                        }
                    }
                }
                // Handle paired operators (e.g., if == is defined but != is not)
                // Use typeNameWithGenerics to ensure generic types have proper type parameters in operator signatures
                operatorHandler.ValidateAndEmitPairs(csWriter, structDecl.Operators, typeNameWithGenerics, emittedOperatorSymbols);

                bool hasEquality = emittedOperatorSymbols.Contains("==");
                bool hasInequality = emittedOperatorSymbols.Contains("!=");

                // Add Equatable support if the struct conforms to Equatable
                var SwiftEquatableMethodWriter = new EqualityMethodsWriter(csWriter, structDecl, true, hasEquality, hasInequality);
                SwiftEquatableMethodWriter.WriteSwiftEquatableImplementation();
                ISwiftObjectMethodWriter.WriteNonFrozenStructImplementation(pinvokeHelperContext);

                csWriter.WriteLine();

                // Collect property names for method/property collision detection
                // Include nested type names and containing type name for consistent naming with PropertyHandler
                var nestedTypeNames = new HashSet<string>(structDecl.Types.Select(t => t.Name));
                var propertyNames = new HashSet<string>(structDecl.Properties.Select(p =>
                    NameProvider.GetPropertyName(p.Name, nestedTypeNames, structDecl.Name)));

                base.HandleBaseDecl(csWriter, swiftWriter, structDecl.Types, conductor, env.TypeDatabase);
                base.HandleBaseDecl(csWriter, swiftWriter, structDecl.Methods, conductor, env.TypeDatabase, propertyNames, pinvokeHelperContext);

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
        private static void WritePrivateFields(CSharpWriter csWriter, string typeNameWithGenerics)
        {
            csWriter.WriteLine($"static nuint _payloadSize = SwiftObjectHelper<{typeNameWithGenerics}>.GetTypeMetadata().Size;");
            csWriter.WriteLine($"SwiftSafeHandle<{typeNameWithGenerics}> _payload = SwiftSafeHandle<{typeNameWithGenerics}>.Zero;");
            csWriter.WriteLine();
        }

        /// <summary>
        /// Writes the payload accessor for the class.
        /// </summary>
        private static void WritePayload(CSharpWriter csWriter, string typeNameWithGenerics)
        {
            csWriter.WriteLine($"public SwiftSafeHandle<{typeNameWithGenerics}> Payload => _payload;");
            csWriter.WriteLine();
        }
    }
}
