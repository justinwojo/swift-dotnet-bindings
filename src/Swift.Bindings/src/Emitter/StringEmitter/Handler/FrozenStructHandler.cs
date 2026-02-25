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
        public void Emit(CSharpWriter csWriter, SwiftWriter swiftWriter, IEnvironment env, Conductor conductor, TypeHandlerContext context)
        {
            var structEnv = (TypeEnvironment)env;
            var structDecl = (StructDecl)structEnv.TypeDecl;
            var parentDecl = structDecl.ParentDecl ?? throw new ArgumentNullException(nameof(structDecl.ParentDecl));
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

            // Retrieve type info from the type database
            var typeRecord = env.TypeDatabase.GetTypeRecordOrThrow(structDecl.SwiftTypeName);
            bool isProjectedAsClass = MarshallingHelpers.IsFrozenStructProjectedAsClass(typeRecord!);

            SwiftTypeInfo? swiftTypeInfo = typeRecord?.SwiftTypeInfo;

            // Get generic type parts if this is a generic type
            var typeNameWithGenerics = GenericTypeEmitter.GetTypeNameWithGenerics(structDecl);
            var whereClause = GenericTypeEmitter.GetWhereClause(structDecl, env.TypeDatabase);

            var ISwiftObjectMethodWriter = new ISwiftObjectMethodWriter(csWriter, env.TypeDatabase, moduleDecl, structDecl, typeNameWithGenerics);
            // Create P/Invoke helper context for generic types (to avoid CS7042)
            var ownPInvokeContext = PInvokeHelperContext.CreateIfGeneric(structDecl);
            var pinvokeHelperContext = ownPInvokeContext ?? context.PInvokeHelperContext;

            // Compute property renames to resolve property/nested-type name collisions
            var propertyRenames = NameProvider.ComputePropertyRenames(structDecl, env.TypeDatabase);

            // Build child context for nested handlers
            var childContext = context with {
                PInvokeHelperContext = pinvokeHelperContext,
                PropertyRenames = propertyRenames
            };

            {
                var conformanceValidator = new ProtocolConformanceValidator(moduleDecl, env.TypeDatabase);
                var interfaces = ProtocolConformanceHelper.GetImplementedInterfaces(
                    structDecl,
                    typeNameWithGenerics,
                    moduleDecl.Name,
                    env.TypeDatabase,
                    conformanceValidator);

                XmlDocCommentEmitter.EmitDocComment(csWriter, structDecl);
                if (structDecl.Name.StartsWith("_"))
                    csWriter.WriteLine("[global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]");
                if (isProjectedAsClass)
                {
                    var classDeclaration = $"public partial class {typeNameWithGenerics} : {string.Join(", ", interfaces)}";
                    if (!string.IsNullOrEmpty(whereClause))
                        classDeclaration += $" {whereClause}";
                    csWriter.WriteLine(classDeclaration);
                    csWriter.WriteLine("{");
                    csWriter.Indent++;

                    // Payload used for reference counting
                    csWriter.WriteLine("[EditorBrowsable(EditorBrowsableState.Never)]");
                    csWriter.WriteLine($"private SwiftSafeHandle<{typeNameWithGenerics}> _payload = SwiftSafeHandle<{typeNameWithGenerics}>.Zero;");
                    csWriter.WriteLine();
                    csWriter.WriteLine("[EditorBrowsable(EditorBrowsableState.Never)]");
                    csWriter.WriteLine($"internal SwiftSafeHandle<{typeNameWithGenerics}> Payload => _payload;");
                    csWriter.WriteLine();
                    var simpleName = typeNameWithGenerics.Contains('<')
                        ? typeNameWithGenerics.Substring(0, typeNameWithGenerics.IndexOf('<'))
                        : typeNameWithGenerics;
                    var disposeMethods = $$"""
                    public void Dispose()
                    {
                        _payload.Dispose();
                        GC.SuppressFinalize(this);
                    }

                    ~{{simpleName}}()
                    {
                        Swift.Runtime.SwiftDispose.FinalizerCleanup(_payload);
                    }
                    """;
                    csWriter.WriteLines(disposeMethods);
                }

                if (swiftTypeInfo.HasValue && swiftTypeInfo.Value.MetadataPtr != IntPtr.Zero)
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
                    var structDeclaration = $"public unsafe partial struct {typeNameWithGenerics} : {string.Join(", ", interfaces)}";
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
                    csWriter.WriteLine($"public unsafe PayloadBuffer<{typeNameWithGenerics}.Buffer> PayloadBuffer => new PayloadBuffer<{typeNameWithGenerics}.Buffer>(_payload);");
                    csWriter.WriteLine();
                }

                var emittedPropertyNames = new HashSet<string>();
                foreach (PropertyDecl propertyDecl in structDecl.Properties)
                {
                    // Use post-rename name for consistency with the propertyNames collision set below.
                    var csPropertyName = NameProvider.GetFinalMemberName(
                        NameProvider.GetPropertyName(propertyDecl.Name, structDecl.Name), propertyRenames);
                    if (!emittedPropertyNames.Add(csPropertyName))
                    {
                        _logger.LogInformation($"Skipping duplicate property '{structDecl.Name}.{csPropertyName}'.");
                        ReportCollector.RecordMemberSkipped(BindingItemKind.Property, propertyDecl.Name, structDecl, SkipReason.DuplicateSignature, $"Property '{csPropertyName}' already emitted.");
                        continue;
                    }

                    var skipReason = MemberEmissionValidator.CanEmitProperty(propertyDecl, env.TypeDatabase, out var skipDetails, out _);
                    if (skipReason != null)
                    {
                        ReportCollector.RecordMemberSkipped(BindingItemKind.Property, propertyDecl.Name, structDecl, skipReason.Value, skipDetails ?? "");
                        continue;
                    }

                    if (conductor.TryGetPropertyHandler(propertyDecl, out var propertyHandler))
                    {
                        var propertyEnv = propertyHandler.Marshal(propertyDecl, env.TypeDatabase);
                        propertyHandler.Emit(csWriter, swiftWriter, propertyEnv, conductor, childContext);
                    }
                    else
                    {
                        _logger.LogWarning($"No handler found for property {propertyDecl.Name}");
                    }
                }
                csWriter.WriteLine();

                // Emit operators
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
                    else
                    {
                        ReportCollector.RecordMemberSkipped(BindingItemKind.Operator, operatorDecl.OperatorSymbol, structDecl, SkipReason.UnsupportedType, $"Operator '{operatorDecl.OperatorSymbol}' has no C# equivalent.");
                    }
                }
                // Handle paired operators (e.g., if == is defined but != is not)
                // Use typeNameWithGenerics to ensure generic types have proper type parameters in operator signatures
                operatorHandler.ValidateAndEmitPairs(csWriter, structDecl.Operators, typeNameWithGenerics, emittedOperatorSymbols, isProjectedAsClass);

                bool hasEquality = emittedOperatorSymbols.Contains("==");
                bool hasInequality = emittedOperatorSymbols.Contains("!=");

                // Add Equatable support if the struct conforms to Equatable
                var SwiftEquatableMethodWriter = new EqualityMethodsWriter(csWriter, structDecl, isProjectedAsClass, typeNameWithGenerics, hasEquality, hasInequality);
                SwiftEquatableMethodWriter.WriteSwiftEquatableImplementation();
                ISwiftObjectMethodWriter.WriteFrozenStructImplementation(pinvokeHelperContext, isProjectedAsClass);

                ToStringHelper.EmitToStringIfDescriptionExists(csWriter, structDecl, propertyRenames);

                // Collect property names (post-rename) for method/property collision detection
                var propertyNames = new HashSet<string>(structDecl.Properties.Select(p =>
                    NameProvider.GetFinalMemberName(
                        NameProvider.GetPropertyName(p.Name, structDecl.Name), propertyRenames)));

                SubscriptHandler.EmitSubscripts(csWriter, swiftWriter, structDecl, env.TypeDatabase, conductor, childContext, _logger);

                base.HandleBaseDecl(csWriter, swiftWriter, structDecl.Types, conductor, env.TypeDatabase, childContext);
                base.HandleBaseDecl(csWriter, swiftWriter, structDecl.Methods, conductor, env.TypeDatabase, childContext, propertyNames);

                csWriter.Indent--;
                csWriter.WriteLine("}");
                csWriter.WriteLine();

                // Emit P/Invoke helper class(es) after the main struct.
                // If this is a nested generic inside a generic parent, defer emission
                // (emitting here would still be inside the outer generic type → CS7042).
                if (ownPInvokeContext != null)
                {
                    if (context.PInvokeHelperContext != null)
                        context.DeferredPInvokeHelperContexts.Add(ownPInvokeContext);
                    else
                    {
                        ownPInvokeContext.EmitHelperClass(csWriter);
                        foreach (var deferred in context.DeferredPInvokeHelperContexts)
                            deferred.EmitHelperClass(csWriter);
                        context.DeferredPInvokeHelperContexts.Clear();
                    }
                }
            }
        }

        // ComputePropertyRenames is now centralized in NameProvider.
    }
}
