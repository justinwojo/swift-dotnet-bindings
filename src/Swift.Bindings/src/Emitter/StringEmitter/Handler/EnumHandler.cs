// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.CodeDom.Compiler;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Swift.Runtime;

namespace BindingsGeneration
{
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
    public partial class EnumHandler : BaseHandler, ITypeHandler
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

            if (GenericTypeEmitter.TryGetUnsupportedConstraint(enumDecl, out var unsupportedConstraint))
            {
                var reason = unsupportedConstraint.Module == "SwiftUI"
                    ? SkipReason.SwiftUIConstraint
                    : unsupportedConstraint.Module == "Combine"
                        ? SkipReason.CombineFramework
                        : SkipReason.UnsupportedType;
                ReportCollector.RecordTypeSkipped(enumDecl, reason, $"Unsupported generic constraint: {unsupportedConstraint.ModuleQualifiedName}");
                _logger.LogWarning(
                    "Skipping type '{TypeName}' - generic constraint references unsupported protocol '{Protocol}' from module '{Module}'.",
                    enumDecl.Name,
                    unsupportedConstraint.Name,
                    unsupportedConstraint.Module);
                return;
            }

            ReportCollector.RecordTypeEmitted(enumDecl);

            // Simple enums (no associated values, frozen, non-generic, integral or no raw value)
            // get emitted as C# enum value types instead of unsafe classes.
            if (enumDecl.IsSimpleEnum)
            {
                EmitSimpleEnum(csWriter, swiftWriter, enumDecl, moduleDecl, env.TypeDatabase, conductor);
                return;
            }

            var typeNameWithGenerics = GenericTypeEmitter.GetTypeNameWithGenerics(enumDecl);
            var whereClause = GenericTypeEmitter.GetWhereClause(enumDecl, env.TypeDatabase);

            // Create P/Invoke helper context for generic enums (to avoid CS7042).
            var pinvokeHelperContext = PInvokeHelperContext.CreateIfGeneric(enumDecl);
            var previousContext = conductor.CurrentPInvokeHelperContext;
            conductor.CurrentPInvokeHelperContext = pinvokeHelperContext;

            try
            {
                // Use unsafe class since methods may use function pointers
                var conformanceValidator = new ProtocolConformanceValidator(moduleDecl, env.TypeDatabase);
                var interfaces = ProtocolConformanceHelper.GetImplementedInterfaces(
                    enumDecl,
                    typeNameWithGenerics,
                    moduleDecl.Name,
                    env.TypeDatabase,
                    conformanceValidator);
                var classDeclaration = $"public unsafe class {typeNameWithGenerics} : {string.Join(", ", interfaces)}";
                if (!string.IsNullOrEmpty(whereClause))
                    classDeclaration += $" {whereClause}";
                csWriter.WriteLine(classDeclaration);
                csWriter.WriteLine("{");
                csWriter.Indent++;

                // Emit payload field and property - enums need this for property accessors
                csWriter.WriteLine($"static nuint _payloadSize = SwiftObjectHelper<{typeNameWithGenerics}>.GetTypeMetadata().Size;");
                csWriter.WriteLine($"SwiftSafeHandle<{typeNameWithGenerics}> _payload = SwiftSafeHandle<{typeNameWithGenerics}>.Zero;");
                csWriter.WriteLine($"internal SwiftSafeHandle<{typeNameWithGenerics}> Payload => _payload;");
                csWriter.WriteLine();
                csWriter.WriteLine("public void Dispose() => _payload.Dispose();");
                csWriter.WriteLine();

            // Emit case constructors for all cases
            // Cases with associated values become static methods with P/Invoke constructors
            // Simple cases (no associated values) use RawRepresentable if available
            var simpleCases = new List<EnumCaseDecl>();
            var emittedCaseConstructorNames = new HashSet<string>();
            foreach (var caseDecl in enumDecl.Cases)
            {
                if (caseDecl.HasAssociatedValues)
                {
                    if (EmitEnumCaseWithAssociatedValues(csWriter, enumDecl, caseDecl, moduleDecl, env.TypeDatabase, typeNameWithGenerics, pinvokeHelperContext))
                    {
                        emittedCaseConstructorNames.Add(NameProvider.ToPascalCase(caseDecl.Name));
                    }
                }
                else
                {
                    simpleCases.Add(caseDecl);
                }
            }

            // Precompute naming-collision context for enum properties.
            var nestedTypeNames = new HashSet<string>(enumDecl.Types.Select(t => t.Name));

            // Handle simple cases via RawRepresentable if available, otherwise via enum-tag construction.
            // Enum element symbols from ABI JSON are often not exported callable functions.
            if (simpleCases.Count > 0)
            {
                if (enumDecl.IsRawRepresentable)
                {
                    EmitRawRepresentableSupport(csWriter, swiftWriter, enumDecl, simpleCases, moduleDecl, env.TypeDatabase, typeNameWithGenerics, pinvokeHelperContext);
                }
                else
                {
                    // No RawRepresentable - construct no-payload cases from enum tag.
                    foreach (var caseDecl in simpleCases)
                    {
                        EmitSimpleCaseFromTag(csWriter, enumDecl, caseDecl, typeNameWithGenerics);
                    }
                }
            }

            // Emit CaseTag enum and Tag property for enums with any cases
            if (enumDecl.Cases.Any())
            {
                EmitCaseTagEnum(csWriter, enumDecl);
                EmitTagProperty(csWriter, enumDecl);
            }

            // Emit TryGet methods for cases with associated values
            foreach (var caseDecl in enumDecl.Cases.Where(c => c.HasAssociatedValues))
            {
                EmitTryGetMethod(csWriter, enumDecl, caseDecl, env.TypeDatabase);
            }

            // Add a blank line between cases and other members
            if (enumDecl.Cases.Any())
            {
                csWriter.WriteLine();
            }

            // Emit properties using the same pattern as other handlers
            foreach (var propertyDecl in enumDecl.Properties)
            {
                var propertyName = NameProvider.GetPropertyName(propertyDecl.Name, nestedTypeNames, enumDecl.Name);
                if (propertyDecl.IsStatic && emittedCaseConstructorNames.Contains(propertyName))
                {
                    _logger.LogInformation($"Skipping enum static property '{enumDecl.Name}.{propertyName}' because a case constructor with the same C# name is already emitted.");
                    ReportCollector.RecordMemberSkipped(BindingItemKind.Property, propertyDecl.Name, enumDecl, SkipReason.DuplicateSignature, $"Enum static property '{propertyName}' collides with case constructor name.");
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

            // Emit ISwiftObject implementation
            var iSwiftObjectWriter = new EnumISwiftObjectMethodWriter(csWriter, env.TypeDatabase, moduleDecl, enumDecl, typeNameWithGenerics, pinvokeHelperContext);
            iSwiftObjectWriter.WriteEnumImplementation();

            // Collect property names for method/property collision detection
            // Include nested type names and containing type name for consistent naming with PropertyHandler
            var propertyNames = new HashSet<string>(enumDecl.Properties.Select(p =>
                NameProvider.GetPropertyName(p.Name, nestedTypeNames, enumDecl.Name)));

            // Record enum operators — equality operators are handled by C# enum semantics
            // (RawValue comparison), other operators are unsupported on enum types.
            foreach (var operatorDecl in enumDecl.Operators)
            {
                if (operatorDecl.Name == "==" || operatorDecl.Name == "!=")
                    ReportCollector.RecordMemberEmitted(BindingItemKind.Operator, operatorDecl.Name, enumDecl);
                else
                    ReportCollector.RecordMemberSkipped(BindingItemKind.Operator, operatorDecl.Name, enumDecl, SkipReason.UnsupportedType, $"Operator '{operatorDecl.Name}' is not supported on enum types.");
            }

            // Record enum constructors as emitted (case constructors handle initialization)
            foreach (var methodDecl in enumDecl.Methods.Where(m => m.IsConstructor))
                ReportCollector.RecordMemberEmitted(BindingItemKind.Method, methodDecl.Name, enumDecl);

            // Emit nested types and methods using base handler
            base.HandleBaseDecl(csWriter, swiftWriter, enumDecl.Types, conductor, env.TypeDatabase);
            base.HandleBaseDecl(csWriter, swiftWriter, enumDecl.Methods.Where(m => !m.IsConstructor).ToList(), conductor, env.TypeDatabase, propertyNames, pinvokeHelperContext);

            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.WriteLine();

                // Emit the P/Invoke helper class after the main enum.
                pinvokeHelperContext?.EmitHelperClass(csWriter);
            }
            finally
            {
                conductor.CurrentPInvokeHelperContext = previousContext;
            }
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
        /// Emits a simple enum case (no associated values) by writing the enum tag directly.
        /// This avoids relying on enum element symbols, which are not guaranteed to be exported as callable functions.
        /// </summary>
        private void EmitSimpleCaseFromTag(CSharpWriter csWriter, EnumDecl enumDecl, EnumCaseDecl caseDecl, string enumTypeName)
        {
            var caseName = caseDecl.Name;
            var capitalizedName = char.ToUpper(caseName[0]) + caseName.Substring(1);
            var caseTag = enumDecl.GetCaseTag(caseDecl);

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
            csWriter.WriteLine($"var metadata = SwiftObjectHelper<{enumTypeName}>.GetTypeMetadata();");
            csWriter.WriteLine($"IntPtr buffer = (IntPtr)NativeMemory.Alloc(metadata.Size);");
            csWriter.WriteLine($"metadata.ValueWitnessTable->DestructiveInjectEnumTag((void*)buffer, (uint){caseTag}, metadata);");
            csWriter.WriteLine($"result._payload = new SwiftSafeHandle<{enumTypeName}>(buffer);");
            csWriter.WriteLine("return result;");

            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.WriteLine();
        }
    }
}
