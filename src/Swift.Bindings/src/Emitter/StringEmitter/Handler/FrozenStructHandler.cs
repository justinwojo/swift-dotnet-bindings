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

            var ISwiftObjectMethodWriter = new ISwiftObjectMethodWriter(csWriter, env.TypeDatabase, moduleDecl, structDecl, typeNameWithGenerics, swiftWriter, context.GetEmissionContext());
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
                var extensionDefaultsIndex = context.GetEmissionContext()?.ExtensionDefaultsIndex;
                var conformanceValidator = new ProtocolConformanceValidator(moduleDecl, env.TypeDatabase, extensionDefaultsIndex);
                var interfaces = ProtocolConformanceHelper.GetImplementedInterfaces(
                    structDecl,
                    typeNameWithGenerics,
                    moduleDecl.Name,
                    env.TypeDatabase,
                    conformanceValidator);

                // Frozen structs projected as C# classes (ref fields) — mark with ISwiftStruct
                // so the SB1001 analyzer can distinguish them from Swift classes (Warning vs Info).
                if (isProjectedAsClass)
                    interfaces.Insert(1, nameof(ISwiftStruct));

                XmlDocCommentEmitter.EmitDocComment(csWriter, structDecl);
                AvailabilityAttributeEmitter.EmitAvailabilityAttributes(csWriter, structDecl, emitObsolete: true);
                var (opaqueEmittable, opaqueSkipped) = MemberEmissionValidator.CountEmittableMembers(structDecl, env.TypeDatabase);
                if (opaqueEmittable == 0 && opaqueSkipped > 0)
                    TypeAnnotationHelper.EmitOpaqueTypeAnnotation(csWriter, opaqueSkipped);
                else if (isProjectedAsClass)
                    TypeAnnotationHelper.EmitDisposalRemarks(csWriter, structDecl);
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
                    csWriter.WriteLine($"public SwiftSafeHandle<{typeNameWithGenerics}> Payload => _payload;");
                    csWriter.WriteLine($"IntPtr ISwiftObject.SwiftHandle => _payload.DangerousGetHandle();");
                    csWriter.WriteLine();
                    var simpleName = typeNameWithGenerics.Contains('<')
                        ? typeNameWithGenerics.Substring(0, typeNameWithGenerics.IndexOf('<'))
                        : typeNameWithGenerics;
                    var disposeMethods = $$"""
                    /// <summary>Releases the underlying Swift object. Safe to call multiple times.</summary>
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

                    // Emit per-type @_cdecl destroy wrapper to avoid CallConvSwift crash on NativeAOT.
                    DestroyWrapperEmitter.EmitIfNeeded(
                        csWriter, swiftWriter,
                        simpleName,
                        typeNameWithGenerics,
                        moduleDecl.Name,
                        structDecl.SwiftTypeName.ToString(),
                        env.TypeDatabase.AsyncLibraryName,
                        context.GetEmissionContext());
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

                        // Handle Optional<T> unconditionally — the generic Swift.Optional TypeRecord
                        // has no concrete InlineSize (varies by T). Some registrations mark Optional
                        // with RequiresMemoryManagement (SwiftDatabase.xml), others don't (enum kind).
                        // Either way, Optional<T> in a frozen struct Buffer needs IntPtr-based emission
                        // with the correctly computed size from the inner type T.
                        if (TryComputeOptionalInlineSize(propertyDecl.SwiftTypeSpec, env.TypeDatabase, out int optionalFieldSize))
                        {
                            EmitIntPtrFields(csWriter, propertyDecl.Name, optionalFieldSize);
                        }
                        else if ((fieldRecord.Flags & TypeRecordFlags.RequiresMemoryManagement) != 0)
                        {
                            // Determine the actual inline size of the field type.
                            // Swift.String is 16 bytes (2 words) but was previously mapped to IntPtr (8 bytes),
                            // causing heap overflow and SIGSEGV for frozen structs with String fields.
                            int fieldSize = IntPtr.Size; // default: single pointer
                            if (fieldRecord.InlineSize.HasValue)
                            {
                                fieldSize = fieldRecord.InlineSize.Value;
                            }
                            else if (fieldRecord.SwiftTypeInfo.HasValue && fieldRecord.SwiftTypeInfo.Value.MetadataPtr != IntPtr.Zero)
                            {
                                unsafe
                                {
                                    fieldSize = (int)fieldRecord.SwiftTypeInfo.Value.ValueWitnessTable->Size;
                                }
                            }

                            EmitIntPtrFields(csWriter, propertyDecl.Name, fieldSize);
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

                    if (MemberEmissionValidator.IsSynthesizedProtocolProperty(propertyDecl, structDecl))
                    {
                        ReportCollector.RecordMemberSynthesized(BindingItemKind.Property, propertyDecl.Name, structDecl);
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
                // For Equatable types with @_cdecl wrapper support, skip == and != operators
                // here so EqualityMethodsWriter emits them with CallConvCdecl instead of
                // CallConvSwift (which crashes on NativeAOT with non-blittable SafeHandle params).
                var operatorHandler = new OperatorHandler(_logger);
                var emittedOperatorSymbols = new HashSet<string>();
                bool hasEquatableConformance = structDecl.Conformances.Any(c => c.Protocol.Name == "Equatable");
                bool hasCdeclWrapperSupport = context.GetEmissionContext() != null &&
                    env.TypeDatabase.AsyncLibraryName != null &&
                    structDecl.GenericParameters.Count == 0;
                bool deferEqualityToWrapper = hasEquatableConformance && hasCdeclWrapperSupport;
                foreach (var operatorDecl in structDecl.Operators)
                {
                    if (OperatorHandler.IsSupportedOperator(operatorDecl.OperatorSymbol))
                    {
                        // Skip == and != when @_cdecl equality wrapper will handle them
                        if (deferEqualityToWrapper &&
                            (operatorDecl.OperatorSymbol == "==" || operatorDecl.OperatorSymbol == "!="))
                            continue;
                        if (operatorHandler.EmitOperator(csWriter, operatorDecl, env.TypeDatabase, pinvokeHelperContext,
                            swiftWriter: swiftWriter, emissionContext: context.GetEmissionContext()))
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

                // Add Equatable support if the struct conforms to Equatable.
                // Pass SwiftWriter + context for @_cdecl equality wrapper (avoids CallConvSwift crash).
                var SwiftEquatableMethodWriter = new EqualityMethodsWriter(csWriter, structDecl, isProjectedAsClass, typeNameWithGenerics, hasEquality, hasInequality, swiftWriter, context.GetEmissionContext(), env.TypeDatabase.AsyncLibraryName);
                SwiftEquatableMethodWriter.WriteSwiftEquatableImplementation();
                ISwiftObjectMethodWriter.WriteFrozenStructImplementation(pinvokeHelperContext, isProjectedAsClass, emitBoxable: interfaces.Contains("Swift.Runtime.IExistentialBoxable"));

                ToStringHelper.EmitToStringIfDescriptionExists(csWriter, structDecl, propertyRenames);

                // Collect property names (post-rename) for method/property collision detection
                var propertyNames = new HashSet<string>(structDecl.Properties.Select(p =>
                    NameProvider.GetFinalMemberName(
                        NameProvider.GetPropertyName(p.Name, structDecl.Name), propertyRenames)));

                SubscriptHandler.EmitSubscripts(csWriter, swiftWriter, structDecl, env.TypeDatabase, conductor, childContext, _logger);

                var emissionCtx = context.GetEmissionContext();
                emissionCtx?.PushTypeNesting(typeNameWithGenerics);
                base.HandleBaseDecl(csWriter, swiftWriter, structDecl.Types, conductor, env.TypeDatabase, childContext);
                emissionCtx?.PopTypeNesting();
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

        /// <summary>
        /// Computes the inline size of Optional&lt;T&gt; for frozen struct Buffer fields.
        /// The generic Swift.Optional TypeRecord has no concrete InlineSize since it varies
        /// by instantiation. This method resolves T and computes the correct size:
        /// - If T has extra inhabitants (String, classes, arrays): Optional&lt;T&gt;.size == T.size
        /// - If T has no extra inhabitants (Int32, Double): Optional&lt;T&gt;.size == T.size + 1
        /// </summary>
        private static bool TryComputeOptionalInlineSize(TypeSpec fieldTypeSpec, ITypeDatabase typeDatabase, out int optionalSize)
        {
            optionalSize = IntPtr.Size;

            if (fieldTypeSpec is not NamedTypeSpec optionalSpec ||
                optionalSpec.Name != "Swift.Optional" ||
                optionalSpec.GenericParameters.Count != 1)
                return false;

            var innerTypeSpec = optionalSpec.GenericParameters[0];
            if (!typeDatabase.TryGetTypeRecord(innerTypeSpec, out var innerRecord))
                return false;

            // Determine inner type's inline size
            int innerSize;
            if (innerRecord.InlineSize.HasValue)
            {
                innerSize = innerRecord.InlineSize.Value;
            }
            else if (innerRecord.SwiftTypeInfo.HasValue && innerRecord.SwiftTypeInfo.Value.MetadataPtr != IntPtr.Zero)
            {
                unsafe { innerSize = (int)innerRecord.SwiftTypeInfo.Value.ValueWitnessTable->Size; }
            }
            else
            {
                return false; // Can't determine inner size
            }

            // Determine if inner type has extra inhabitants
            bool hasExtraInhabitants;
            if (innerRecord.SwiftTypeInfo.HasValue && innerRecord.SwiftTypeInfo.Value.MetadataPtr != IntPtr.Zero)
            {
                unsafe { hasExtraInhabitants = innerRecord.SwiftTypeInfo.Value.ValueWitnessTable->HasExtraInhabitants; }
            }
            else
            {
                // Heuristic: RequiresMemoryManagement types (String, classes, arrays) contain
                // pointers which have extra inhabitants. Primitive value types don't.
                hasExtraInhabitants = (innerRecord.Flags & TypeRecordFlags.RequiresMemoryManagement) != 0;
            }

            optionalSize = hasExtraInhabitants ? innerSize : innerSize + 1;
            return true;
        }

        /// <summary>
        /// Emits IntPtr-based backing fields for a frozen struct Buffer field.
        /// Fields larger than one pointer word get numbered suffixes (_0_, _1_, etc.).
        /// </summary>
        private static void EmitIntPtrFields(CSharpWriter csWriter, string fieldName, int fieldSize)
        {
            int wordCount = (fieldSize + IntPtr.Size - 1) / IntPtr.Size;
            if (wordCount <= 1)
            {
                csWriter.WriteLine($"private IntPtr {fieldName}_;  // Note: Do not access this field directly - use the property accessors");
            }
            else
            {
                for (int i = 0; i < wordCount; i++)
                {
                    csWriter.WriteLine($"private IntPtr {fieldName}_{i}_;  // Note: Do not access this field directly - use the property accessors");
                }
            }
        }
    }
}
