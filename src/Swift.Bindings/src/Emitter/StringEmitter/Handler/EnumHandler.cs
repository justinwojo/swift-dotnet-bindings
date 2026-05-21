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
        public void Emit(CSharpWriter csWriter, SwiftWriter swiftWriter, IEnvironment env, Conductor conductor, TypeHandlerContext context)
        {
            var enumEnv = (TypeEnvironment)env;
            var enumDecl = (EnumDecl)enumEnv.TypeDecl;
            var parentDecl = enumDecl.ParentDecl ?? throw new ArgumentNullException(nameof(enumDecl.ParentDecl));
            var moduleDecl = enumDecl.ModuleDecl ?? throw new ArgumentNullException(nameof(enumDecl.ModuleDecl));

            // Module-internal enums: suppress Swift wrapper emission but still emit C# type stubs,
            // because other types may reference the enum in method signatures (e.g., GRDB.RowKey).
            // Redirecting swiftWriter to a discard writer prevents Swift wrapper emission while
            // allowing all C# code paths to proceed normally.
            if (enumDecl.IsModuleInternal)
                swiftWriter = new SwiftWriter(new System.IO.StringWriter());

            if (GenericTypeEmitter.TryGetUnsupportedConstraint(enumDecl, out var unsupportedConstraint))
            {
                var reason = AppleFrameworkRegistry.GetUnsupportedConstraintSkipReason(unsupportedConstraint.Module);
                ReportCollector.RecordTypeSkipped(enumDecl, reason, $"Unsupported generic constraint: {unsupportedConstraint.ModuleQualifiedName}");
                UnsupportedCommentEmitter.EmitTypeSkipped(csWriter, enumDecl.Name, reason, $"generic constraint: {unsupportedConstraint.ModuleQualifiedName}");
                _logger.LogWarning(
                    "Skipping type '{TypeName}' - generic constraint references unsupported protocol '{Protocol}' from module '{Module}'.",
                    enumDecl.Name,
                    unsupportedConstraint.Name,
                    unsupportedConstraint.Module);
                return;
            }

            // Create P/Invoke helper context for generic enums (to avoid CS7042).
            // Pre-flatten conformances against the type database so the metadata-accessor
            // emitter can render the correct PWT plumbing.
            //
            // The ShouldSkip check MUST happen BEFORE RecordTypeEmitted: ReportCollector
            // suppresses RecordTypeSkipped if the type key is already in EmittedTypeKeys
            // (ReportCollector.cs:106), so a skipped generic enum would otherwise be
            // silently miscounted as emitted.
            var ownPInvokeContext = PInvokeHelperContext.CreateIfGeneric(enumDecl, env.TypeDatabase);
            if (ownPInvokeContext != null && TypeMetadataAccessorSkipGate.ShouldSkip(
                    enumDecl, ownPInvokeContext, csWriter, _logger))
                return;

            ReportCollector.RecordTypeEmitted(enumDecl);

            // Caseless enums (zero cases) → static class.
            // In Swift, caseless enums cannot be instantiated and are used for namespacing
            // and/or holding static members. Emitting ISwiftObject + SafeHandle is wrong.
            if (enumDecl.IsNamespaceEnum)
            {
                EmitNamespaceEnum(csWriter, swiftWriter, enumDecl, moduleDecl, env.TypeDatabase, conductor, context);
                return;
            }

            // Simple enums (no associated values, non-generic, integral/no raw value)
            // get emitted as C# enum value types instead of unsafe classes.
            // CanSafelyEmitAsSimpleEnum checks structural constraints (nested types,
            // non-equality operators). Compatible members are emitted as extensions;
            // incompatible members are skipped with ReportCollector tracking.
            // Also check TypeRecord flag — post-scan may have demoted the enum if it's
            // used as a generic type argument (C# enums can't implement ISwiftObject).
            // If no TypeRecord exists (test scenarios), fall back to decl-level check only.
            var wasDemotedFromSimple = enumEnv.TypeDatabase.TryGetTypeRecord(
                enumDecl.SwiftTypeName, out var enumTypeRecord) &&
                !enumTypeRecord.Flags.HasFlag(TypeRecordFlags.SimpleEnum);
            if (!wasDemotedFromSimple &&
                ((enumDecl.IsSimpleEnum && CanSafelyEmitAsSimpleEnum(enumDecl)) ||
                (enumDecl.IsStringRawValueSimpleEnum && CanSafelyEmitAsSimpleEnum(enumDecl))))
            {
                EmitSimpleEnum(csWriter, swiftWriter, enumDecl, moduleDecl, env.TypeDatabase, conductor, context);
                return;
            }

            // Single-case enums with no payload have zero runtime size — Swift optimizes the
            // tag away since there's only one possible value. Emitting as ISwiftObject is invalid
            // because TypeMetadata.Size == 0, which breaks SafeHandle allocations. Skip emission;
            // these unit-type enums carry no information and aren't useful as bindings.
            if (enumDecl.Cases.Count == 1 && !enumDecl.HasAssociatedValueCases)
            {
                ReportCollector.RecordTypeSkipped(enumDecl, SkipReason.UnsupportedType,
                    "Single-case enum with no payload has zero runtime size (TypeMetadata.Size == 0).");
                return;
            }

            var typeNameWithGenerics = GenericTypeEmitter.GetTypeNameWithGenerics(enumDecl, env.TypeDatabase);
            var whereClause = GenericTypeEmitter.GetWhereClause(enumDecl, env.TypeDatabase);
            var pinvokeHelperContext = ownPInvokeContext ?? context.PInvokeHelperContext;

            // Compute property renames to resolve property/nested-type name collisions
            var propertyRenames = NameProvider.ComputePropertyRenames(enumDecl, env.TypeDatabase);

            // Compute case name map for case-insensitive collision avoidance
            // (e.g., Swift M vs m → C# M vs M2)
            var caseNameMap = NameProvider.ComputeCaseNameMap(enumDecl.Cases);

            // Build child context for nested handlers
            var childContext = context with {
                PInvokeHelperContext = pinvokeHelperContext,
                PropertyRenames = propertyRenames
            };

            {
                var validatorEmissionCtx = context.GetEmissionContext();
                var extensionDefaultsIndex = validatorEmissionCtx?.ExtensionDefaultsIndex;
                var conformanceValidator = new ProtocolConformanceValidator(moduleDecl, env.TypeDatabase, extensionDefaultsIndex, validatorEmissionCtx);
                var interfaces = ProtocolConformanceHelper.GetImplementedInterfaces(
                    enumDecl,
                    typeNameWithGenerics,
                    moduleDecl.Name,
                    env.TypeDatabase,
                    conformanceValidator);

                // Complex enums are projected as C# classes backed by SwiftSafeHandle — same
                // buffer-ownership model as non-frozen structs (value bytes in the buffer,
                // handle == buffer address). Mark with ISwiftStruct so runtime code that
                // distinguishes struct-like (SafeHandle wraps buffer) from class-like
                // (handle is the instance pointer) treats complex enums as buffer-backed.
                interfaces.Insert(1, nameof(ISwiftStruct));

                XmlDocCommentEmitter.EmitDocComment(csWriter, enumDecl);
                AvailabilityAttributeEmitter.EmitAvailabilityAttributes(csWriter, enumDecl, emitObsolete: true);
                var (opaqueEmittable, opaqueSkipped) = MemberEmissionValidator.CountEmittableMembers(enumDecl, env.TypeDatabase);
                if (opaqueEmittable == 0 && opaqueSkipped > 0)
                {
                    TypeAnnotationHelper.EmitOpaqueTypeAnnotation(csWriter, opaqueSkipped);
                    context.GetEmissionContext()?.AddEmittedOpaqueType(enumDecl.SwiftTypeName.ModuleQualifiedName);
                }
                else
                    TypeAnnotationHelper.EmitDisposalRemarks(csWriter, enumDecl);
                TypeAnnotationHelper.EmitSwiftSendableAnnotation(csWriter, enumDecl);
                if (enumDecl.Name.StartsWith("_"))
                    csWriter.WriteLine("[global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]");
                var classDeclaration = $"public partial class {typeNameWithGenerics} : {string.Join(", ", interfaces)}";
                if (!string.IsNullOrEmpty(whereClause))
                    classDeclaration += $" {whereClause}";
                csWriter.WriteLine(classDeclaration);
                csWriter.WriteLine("{");
                csWriter.Indent++;

                // Emit payload field and property - enums need this for property accessors
                csWriter.WriteLine("[EditorBrowsable(EditorBrowsableState.Never)]");
                if (ownPInvokeContext != null)
                {
                    // Generic enums: call the helper class metadata accessor directly with
                    // per-param metadata (and per-conformance witness tables for constrained
                    // generics). SwiftObjectHelper<GenericEnum<T>> in a static field
                    // initializer crashes Mono's generic sharing
                    // (mini-generic-sharing.c:2759) because the nested generic instantiation
                    // cannot be compiled without the type argument's metadata.
                    //
                    // Route through TypeMetadata.RegisterAndGetSize so RunClassConstructor →
                    // static field init populates both TypeMetadata.Cache and the
                    // NewFromPayloadDispatcher factory. Passing NewFromPayloadCore as the
                    // factory lets MarshalFromSwift resolve generic instantiations without
                    // falling back to reflection on explicit interface implementations
                    // (which NativeAOT may not enumerate via Type.GetMethods on closed
                    // generic instantiations).
                    //
                    // Note: when (num_metadata + num_pwts) > 3, TypeMetadataAccessorSkipGate
                    // already skips the type before we reach this point, so PwtEntries is
                    // always populated correctly here.
                    var metadataArgs = string.Join(", ", ownPInvokeContext.GetTypeMetadataAccessorArgumentList());
                    csWriter.WriteLine($"static nuint _payloadSize = TypeMetadata.RegisterAndGetSize(typeof({typeNameWithGenerics}), {ownPInvokeContext.HelperClassName}.PInvoke_getMetadata(TypeMetadataRequest.Complete, {metadataArgs}), NewFromPayloadCore);");
                }
                else
                {
                    csWriter.WriteLine($"static nuint _payloadSize = SwiftObjectHelper<{typeNameWithGenerics}>.GetTypeMetadata().Size;");
                }
                csWriter.WriteLine("[EditorBrowsable(EditorBrowsableState.Never)]");
                csWriter.WriteLine($"SwiftSafeHandle<{typeNameWithGenerics}> _payload = SwiftSafeHandle<{typeNameWithGenerics}>.Zero;");
                csWriter.WriteLine("[EditorBrowsable(EditorBrowsableState.Never)]");
                csWriter.WriteLine($"public SwiftSafeHandle<{typeNameWithGenerics}> Payload => _payload;");
                csWriter.WriteLine($"IntPtr ISwiftObject.SwiftHandle => _payload.DangerousGetHandle();");
                csWriter.WriteLine("#pragma warning disable CS0649");
                csWriter.WriteLine("[EditorBrowsable(EditorBrowsableState.Never)]");
                csWriter.WriteLine("internal bool _isCachedSingleton;");
                csWriter.WriteLine("#pragma warning restore CS0649");
                csWriter.WriteLine();
                var simpleName = typeNameWithGenerics.Contains('<')
                    ? typeNameWithGenerics.Substring(0, typeNameWithGenerics.IndexOf('<'))
                    : typeNameWithGenerics;
                var disposeMethods = $$"""
                /// <summary>Releases the underlying Swift object. Safe to call multiple times.</summary>
                public void Dispose()
                {
                    if (_isCachedSingleton) return;
                    _payload.Dispose();
                    GC.SuppressFinalize(this);
                }

                ~{{simpleName}}()
                {
                    if (!_isCachedSingleton)
                        Swift.Runtime.SwiftDispose.FinalizerCleanup(_payload);
                }
                """;
                csWriter.WriteLines(disposeMethods);
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
                    if (EmitEnumCaseWithAssociatedValues(csWriter, enumDecl, caseDecl, moduleDecl, env.TypeDatabase, typeNameWithGenerics, pinvokeHelperContext, propertyRenames, caseNameMap, swiftWriter, context.GetEmissionContext()))
                    {
                        emittedCaseConstructorNames.Add(NameProvider.GetFinalMemberName(
                            NameProvider.GetCaseName(caseDecl.Name, caseNameMap), propertyRenames));
                    }
                }
                else
                {
                    simpleCases.Add(caseDecl);
                }
            }

            // Determine if no-payload case properties can be cached as lazy singletons.
            // Only cache when the enum is effectively immutable from C# — mutating methods
            // or writable instance properties would allow a cached singleton to be globally mutated.
            bool canCacheCases =
                !enumDecl.Methods.Any(m => !m.IsConstructor && m.IsMutating) &&
                !enumDecl.Properties.Any(p => !p.IsStatic && p.Accessors.Any(a => a is SetAccessorDecl));

            // Handle simple cases via RawRepresentable if available, otherwise via enum-tag construction.
            // Enum element symbols from ABI JSON are often not exported callable functions.
            if (simpleCases.Count > 0)
            {
                if (enumDecl.IsRawRepresentable)
                {
                    EmitRawRepresentableSupport(csWriter, swiftWriter, enumDecl, simpleCases, moduleDecl, env.TypeDatabase, typeNameWithGenerics, pinvokeHelperContext, canCacheCases, propertyRenames, caseNameMap: caseNameMap, ctx: context.GetEmissionContext());
                }
                else
                {
                    // No RawRepresentable - construct no-payload cases from enum tag.
                    foreach (var caseDecl in simpleCases)
                    {
                        EmitSimpleCaseFromTag(csWriter, enumDecl, caseDecl, typeNameWithGenerics, canCacheCases, propertyRenames, caseNameMap);
                    }
                }
            }

            // Emit CaseTag enum and Tag property for enums with any cases
            if (enumDecl.Cases.Any())
            {
                EmitCaseTagEnum(csWriter, enumDecl, caseNameMap);
                EmitTagProperty(csWriter, enumDecl, typeNameWithGenerics);
            }

            // Emit TryGet methods for cases with associated values
            foreach (var caseDecl in enumDecl.Cases.Where(c => c.HasAssociatedValues))
            {
                EmitTryGetMethod(csWriter, enumDecl, caseDecl, env.TypeDatabase, typeNameWithGenerics, caseNameMap);
            }

            // Add a blank line between cases and other members
            if (enumDecl.Cases.Any())
            {
                csWriter.WriteLine();
            }

            // Emit properties using the same pattern as other handlers
            foreach (var propertyDecl in enumDecl.Properties)
            {
                var propertyName = NameProvider.GetFinalMemberName(
                    NameProvider.GetPropertyName(propertyDecl.Name, enumDecl.Name), propertyRenames);
                if (emittedCaseConstructorNames.Contains(propertyName))
                {
                    _logger.LogInformation($"Skipping enum property '{enumDecl.Name}.{propertyName}' because a case constructor with the same C# name is already emitted.");
                    ReportCollector.RecordMemberSkipped(propertyDecl, SkipReason.DuplicateSignature, $"Enum property '{propertyName}' collides with case constructor name.");
                    continue;
                }

                if (MemberEmissionValidator.IsSynthesizedProtocolProperty(propertyDecl, enumDecl))
                {
                    ReportCollector.RecordMemberSynthesized(propertyDecl);
                    continue;
                }

                var skipReason = MemberEmissionValidator.CanEmitProperty(propertyDecl, env.TypeDatabase, out var skipDetails, out _);
                if (skipReason != null)
                {
                    ReportCollector.RecordMemberSkipped(propertyDecl, skipReason.Value, skipDetails ?? "");
                    // Mirror PropertyHandler's SkipProperty: leave a `// Unsupported:` tombstone
                    // so consumers can grep the file. The outer gate skips PropertyHandler.Emit
                    // entirely, so this is the only place the omission can be made visible.
                    UnsupportedCommentEmitter.EmitMemberSkipped(csWriter, propertyDecl.Name, BindingItemKind.Property, skipReason.Value, skipDetails);
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

            // Emit ISwiftObject implementation
            var iSwiftObjectWriter = new EnumISwiftObjectMethodWriter(csWriter, env.TypeDatabase, moduleDecl, enumDecl, typeNameWithGenerics, pinvokeHelperContext, swiftWriter, context.GetEmissionContext(), hasBoxable: interfaces.Contains("Swift.Runtime.IExistentialBoxable"));
            iSwiftObjectWriter.WriteEnumImplementation();

            // Collect all emitted member names for method/property collision detection.
            // Include actual property names (post-rename), case constructor names, and synthesized names.
            var propertyNames = new HashSet<string>(enumDecl.Properties.Select(p =>
                NameProvider.GetFinalMemberName(
                    NameProvider.GetPropertyName(p.Name, enumDecl.Name), propertyRenames)));

            // Nested type names collide with method names in C# (CS0102)
            foreach (var nestedType in enumDecl.Types)
                propertyNames.Add(NameProvider.ToPascalCase(nestedType.Name));

            // Include case-derived names to prevent method collisions
            foreach (var caseName in emittedCaseConstructorNames)
                propertyNames.Add(caseName);
            if (enumDecl.Cases.Any())
            {
                propertyNames.Add("CaseTag");
                propertyNames.Add("Tag");
            }
            foreach (var caseDecl in enumDecl.Cases.Where(c => c.HasAssociatedValues))
                propertyNames.Add($"TryGet{NameProvider.GetCaseName(caseDecl.Name, caseNameMap)}");
            foreach (var caseDecl in enumDecl.Cases.Where(c => !c.HasAssociatedValues))
                propertyNames.Add(NameProvider.GetFinalMemberName(
                    NameProvider.GetCaseName(caseDecl.Name, caseNameMap), propertyRenames));

            // Record enum operators. Equality operators are emitted below through the
            // EnumEqualityMethodsWriter for Equatable enums-as-class; for simple raw-value enums
            // (lowered as C# enum value types) C# already provides == / != natively. Other
            // operators are unsupported on enum types.
            //
            // The EnumEqualityMethodsWriter routes both `==` and `!=` through a single
            // @_cdecl wrapper that calls Swift's own `lhs == rhs` — that is the authoritative
            // Swift dispatch whether the conformance is synthesized or user-declared. We
            // therefore never delegate to OperatorHandler for enums and we don't track
            // "explicit ==/!=" separately: OperatorHandler isn't dispatched on this path and
            // suppressing one side would produce an asymmetric pair (CS0216).
            foreach (var operatorDecl in enumDecl.Operators)
            {
                if (operatorDecl.OperatorSymbol == "==" || operatorDecl.OperatorSymbol == "!=")
                    ReportCollector.RecordMemberEmitted(operatorDecl);
                else
                    ReportCollector.RecordMemberSkipped(operatorDecl, SkipReason.UnsupportedType, $"Operator '{operatorDecl.Name}' is not supported on enum types.");
            }

            // Bridge Swift's Equatable conformance to .NET's Equals/GetHashCode/IEquatable<T>
            // for enums lowered as classes (associated-value enums, non-frozen complex enums).
            // Without this the synthesized C# class returns object reference equality and the
            // Swift `==` semantics are silently dropped.
            if (enumDecl.Conformances.Any(c => c.Protocol.Name == "Equatable"))
            {
                var enumEqualityWriter = new EnumEqualityMethodsWriter(
                    csWriter,
                    enumDecl,
                    typeNameWithGenerics,
                    swiftWriter,
                    context.GetEmissionContext(),
                    env.TypeDatabase.AsyncLibraryName,
                    env.TypeDatabase);
                enumEqualityWriter.WriteSwiftEquatableImplementation();
            }

            // Record enum constructors as emitted (case constructors handle initialization)
            foreach (var methodDecl in enumDecl.Methods.Where(m => m.IsConstructor))
                ReportCollector.RecordMemberEmitted(methodDecl);

            ToStringHelper.EmitToStringIfDescriptionExists(csWriter, enumDecl, propertyRenames);

            SubscriptHandler.EmitSubscripts(csWriter, swiftWriter, enumDecl, env.TypeDatabase, conductor, childContext, _logger);

            // Emit nested types and methods using base handler
            var emissionCtx = context.GetEmissionContext();
            emissionCtx?.PushTypeNesting(typeNameWithGenerics);
            base.HandleBaseDecl(csWriter, swiftWriter, enumDecl.Types, conductor, env.TypeDatabase, childContext);
            emissionCtx?.PopTypeNesting();
            base.HandleBaseDecl(csWriter, swiftWriter, enumDecl.Methods.Where(m => !m.IsConstructor).ToList(), conductor, env.TypeDatabase, childContext, propertyNames);

            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.WriteLine();

                // Emit constrained-extension specialization classes (e.g.
                // `extension VerificationResult where SignedType == AppTransaction`)
                // for generic enums. Mirrors the wiring in ClassHandler /
                // FrozenStructHandler / NonFrozenStructHandler — without it,
                // properties on a generic enum's `where T == Concrete` extensions
                // are validator-suppressed at the open-generic level (correct, the
                // mangled symbol is bound to the closed instantiation) AND never
                // re-surfaced as closed-generic extension methods, so consumers
                // lose the property surface entirely. StoreKit2's
                // `VerificationResult<SignedType>.jwsRepresentation` etc. are the
                // canonical case — see
                // src/docs/Resolved/gap-0.10.0-multispecialization-drops-generic-property-accessors.md.
                ConstrainedExtensionEmitter.EmitConstrainedExtensions(
                    csWriter, swiftWriter, enumDecl,
                    env.TypeDatabase, context.GetEmissionContext(), _logger);

                // Emit P/Invoke helper class(es) after the main enum.
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
        /// Emits a caseless enum as a static class. Swift uses caseless enums both as pure namespace
        /// containers (e.g., `enum ImageProcessors { struct Resize { } }`) and as non-instantiable
        /// types with static members (e.g., `enum Constants { static let x = 1 }`).
        /// </summary>
        private void EmitNamespaceEnum(CSharpWriter csWriter, SwiftWriter swiftWriter, EnumDecl enumDecl,
            ModuleDecl moduleDecl, ITypeDatabase typeDatabase, Conductor conductor, TypeHandlerContext context)
        {
            var propertyRenames = NameProvider.ComputePropertyRenames(enumDecl, typeDatabase);
            var childContext = context with { PropertyRenames = propertyRenames };

            var typeNameWithGenerics = GenericTypeEmitter.GetTypeNameWithGenerics(enumDecl, typeDatabase);
            var whereClause = GenericTypeEmitter.GetWhereClause(enumDecl, typeDatabase);

            XmlDocCommentEmitter.EmitDocComment(csWriter, enumDecl);
            AvailabilityAttributeEmitter.EmitAvailabilityAttributes(csWriter, enumDecl, emitObsolete: true);
            if (enumDecl.Name.StartsWith("_"))
                csWriter.WriteLine("[global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]");
            var classDeclaration = $"public static partial class {typeNameWithGenerics}";
            if (!string.IsNullOrEmpty(whereClause))
                classDeclaration += $" {whereClause}";
            csWriter.WriteLine(classDeclaration);
            csWriter.WriteLine("{");
            csWriter.Indent++;

            // Only emit static members — instance members are invalid inside a C# static class.
            // (Swift permits instance members on caseless enums, but no real-world library uses them.)
            foreach (var propertyDecl in enumDecl.Properties.Where(p => p.IsStatic))
            {
                if (MemberEmissionValidator.IsSynthesizedProtocolProperty(propertyDecl, enumDecl))
                    continue;

                var skipReason = MemberEmissionValidator.CanEmitProperty(propertyDecl, typeDatabase, out var skipDetails, out _);
                if (skipReason != null)
                {
                    ReportCollector.RecordMemberSkipped(propertyDecl, skipReason.Value, skipDetails ?? "");
                    // Mirror PropertyHandler's SkipProperty: leave a `// Unsupported:` tombstone
                    // so consumers can grep the file. The outer gate skips PropertyHandler.Emit
                    // entirely, so this is the only place the omission can be made visible.
                    UnsupportedCommentEmitter.EmitMemberSkipped(csWriter, propertyDecl.Name, BindingItemKind.Property, skipReason.Value, skipDetails);
                    continue;
                }

                if (conductor.TryGetPropertyHandler(propertyDecl, out var propertyHandler))
                {
                    var propertyEnv = propertyHandler.Marshal(propertyDecl, typeDatabase);
                    propertyHandler.Emit(csWriter, swiftWriter, propertyEnv, conductor, childContext);
                }
            }

            // Subscripts: static subscripts are already skipped by EmitSubscripts (not valid C# indexers),
            // and instance subscripts are invalid in a static class. Nothing to emit.

            // Emit nested types and static methods
            var emissionCtx2 = context.GetEmissionContext();
            emissionCtx2?.PushTypeNesting(typeNameWithGenerics);
            base.HandleBaseDecl(csWriter, swiftWriter, enumDecl.Types, conductor, typeDatabase, childContext);
            emissionCtx2?.PopTypeNesting();
            var propertyNames = new HashSet<string>(enumDecl.Properties.Where(p => p.IsStatic).Select(p =>
                NameProvider.GetFinalMemberName(
                    NameProvider.GetPropertyName(p.Name, enumDecl.Name), propertyRenames)));
            base.HandleBaseDecl(csWriter, swiftWriter, enumDecl.Methods.Where(m => !m.IsConstructor && m.MethodType == MethodType.Static).ToList(), conductor, typeDatabase, childContext, propertyNames);

            // Emit concrete protocol specializations (e.g., func seal<T: DataProtocol>(_ message: T, ...))
            // Namespace enums (caseless enums used as Swift "enum namespaces") host static methods
            // with method-level generic parameters just like structs do — AES.GCM, ChaChaPoly,
            // HPKE, etc. in CryptoKit are the canonical examples. Without this call, CSM never
            // runs for their Seal/Open/Unwrap APIs and consumers can't call the DataProtocol
            // overloads.
            // Null-check emissionCtx to match the PushTypeNesting/PopTypeNesting pattern above
            // (this handler's own surrounding code already treats GetEmissionContext() as
            // nullable); a null SpecializationEngine simply means CSM isn't wired, not an
            // invariant violation.
            var specEmissionCtx = context.GetEmissionContext();
            var specEngine = specEmissionCtx?.SpecializationEngine;
            if (specEngine != null)
            {
                ConcreteProtocolSpecializationEmitter.EmitConcreteSpecializations(
                    csWriter, swiftWriter, enumDecl,
                    typeDatabase, specEmissionCtx!, specEngine, _logger);
            }

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
            var capitalizedName = NameProvider.ToPascalCase(caseName);
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

            // P/Invoke declaration for the case constructor. The entry point is the raw
            // Swift-mangled symbol — PInvokeEmitHelper.SelectCallingConvention pairs $s…
            // with CallConvSwift automatically.
            PInvokeEmitHelper.EmitDeclaration(csWriter, new PInvokeEmissionInfo
            {
                LibraryPath = libPath,
                EntryPoint = caseDecl.MangledName,
                MethodName = pInvokeName,
                ReturnType = "IntPtr",
                ParametersString = string.Empty,
                CallingConvention = PInvokeCallingConvention.Swift,
                Visibility = PInvokeVisibility.Private,
            });
            csWriter.WriteLine();
        }

        /// <summary>
        /// Emits a simple enum case (no associated values) by writing the enum tag directly.
        /// This avoids relying on enum element symbols, which are not guaranteed to be exported as callable functions.
        /// When <paramref name="canCacheCases"/> is true, the case is cached as a lazy singleton to avoid
        /// repeated native memory allocation on each access.
        /// </summary>
        private void EmitSimpleCaseFromTag(CSharpWriter csWriter, EnumDecl enumDecl, EnumCaseDecl caseDecl, string enumTypeName, bool canCacheCases, Dictionary<string, string>? propertyRenames = null, Dictionary<string, string>? caseNameMap = null)
        {
            var caseName = caseDecl.Name;
            var capitalizedName = NameProvider.GetFinalMemberName(
                NameProvider.GetCaseName(caseName, caseNameMap), propertyRenames);
            // Strip backticks for field names (belt-and-suspenders — parser should already strip them)
            var fieldName = caseName.Replace("`", "");
            var caseTag = enumDecl.GetCaseTag(caseDecl);

            if (canCacheCases)
            {
                // Lazy-cached singleton: exactly one native allocation per case, thread-safe.
                csWriter.WriteLine($"private static readonly Lazy<{enumTypeName}> _lazy_{fieldName} = new(() =>");
                csWriter.WriteLine("{");
                csWriter.Indent++;
                csWriter.WriteLine("unsafe");
                csWriter.WriteLine("{");
                csWriter.Indent++;
                csWriter.WriteLine($"var result = new {enumTypeName}();");
                csWriter.WriteLine($"var metadata = SwiftObjectHelper<{enumTypeName}>.GetTypeMetadata();");
                csWriter.WriteLine($"IntPtr buffer = (IntPtr)NativeMemory.Alloc(metadata.Size);");
                csWriter.WriteLine($"metadata.ValueWitnessTable->DestructiveInjectEnumTag((void*)buffer, (uint){caseTag}, metadata);");
                csWriter.WriteLine($"result._payload = new SwiftSafeHandle<{enumTypeName}>(buffer);");
                csWriter.WriteLine("result._isCachedSingleton = true;");
                csWriter.WriteLine("return result;");
                csWriter.Indent--;
                csWriter.WriteLine("}");
                csWriter.Indent--;
                csWriter.WriteLine("});");

                csWriter.WriteLine($"/// <summary>");
                csWriter.WriteLine($"/// Gets the '{caseName}' case of {enumTypeName}.");
                csWriter.WriteLine($"/// </summary>");
                csWriter.WriteLine($"/// <remarks>Cached singleton instance — does not require disposal.</remarks>");
                csWriter.WriteLine($"public static {enumTypeName} {capitalizedName} => _lazy_{fieldName}.Value;");
                csWriter.WriteLine();
            }
            else
            {
                // Per-access construction: enum has mutating methods or writable properties,
                // so caching would allow global mutation of a shared instance.
                csWriter.WriteLine($"/// <summary>");
                csWriter.WriteLine($"/// Gets the '{caseName}' case of {enumTypeName}.");
                csWriter.WriteLine($"/// </summary>");
                csWriter.WriteLine($"public static {enumTypeName} {capitalizedName}");
                csWriter.WriteLine("{");
                csWriter.Indent++;
                csWriter.WriteLine("get");
                csWriter.WriteLine("{");
                csWriter.Indent++;
                csWriter.WriteLine("unsafe");
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
                csWriter.Indent--;
                csWriter.WriteLine("}");
                csWriter.WriteLine();
            }
        }
    }

    /// <summary>
    /// Emits Equals / GetHashCode / operator== / operator!= / IEquatable&lt;T&gt;.Equals
    /// for an Equatable enum that lowers to a C# class. The bridge mirrors the
    /// reference-projected struct path: a Swift @_cdecl wrapper performs the
    /// per-case Swift `==` and the C# methods route through it via the SafeHandle
    /// payload pointer (the same buffer pointer used everywhere else in the enum
    /// projection).
    /// </summary>
    /// <remarks>
    /// Without this writer, an Equatable Swift enum-as-class falls through to
    /// <see cref="object.Equals(object?)"/> reference equality and silently drops the Swift
    /// semantics — see <c>bug-0.10.0-equatable-not-lowered.md</c> Defect 2.
    /// </remarks>
    public class EnumEqualityMethodsWriter
    {
        private readonly System.CodeDom.Compiler.IndentedTextWriter _writer;
        private readonly EnumDecl _enumDecl;
        private readonly string _typeNameWithGenerics;
        private readonly bool _implementsHashable;
        private readonly SwiftWriter? _swiftWriter;
        private readonly ModuleEmissionContext? _emissionContext;
        private readonly string? _wrapperLibraryName;

        public EnumEqualityMethodsWriter(
            CSharpWriter csWriter,
            EnumDecl enumDecl,
            string typeNameWithGenerics,
            SwiftWriter? swiftWriter,
            ModuleEmissionContext? emissionContext,
            string? wrapperLibraryName,
            ITypeDatabase? typeDatabase = null)
        {
            _writer = csWriter;
            _enumDecl = enumDecl;
            _typeNameWithGenerics = typeNameWithGenerics;
            bool directlyDeclaredHashable = enumDecl.Conformances.Any(c =>
                c.Protocol.ModuleQualifiedName == "Swift.Hashable" ||
                (c.Protocol.Name == "Hashable" && string.IsNullOrEmpty(c.Protocol.Module)) ||
                c.Protocol.Name == "OptionSet" ||
                c.Protocol.Name == "RawRepresentable");
            // Swift synthesizes Hashable for enums when all associated values are Hashable
            // AND `==` is itself synthesized. The symbol graph reliably surfaces the
            // synthesized Hashable conformance for enums (more so than for structs), so we
            // require explicit Hashable here. Inferring Hashable from Equatable would risk
            // contract violations for enums with custom `==` whose payload bytes don't fully
            // determine equality.
            bool hashableUnconditional = EquatableConformanceHelper.IsTypeHashableUnconditional(enumDecl, typeDatabase);
            _implementsHashable = hashableUnconditional && directlyDeclaredHashable;
            _swiftWriter = swiftWriter;
            _emissionContext = emissionContext;
            _wrapperLibraryName = wrapperLibraryName;
        }

        public void WriteSwiftEquatableImplementation()
        {
            var eqSymbol = TryEmitSwiftEqualityWrapper();
            if (eqSymbol != null)
                EmitEqualityPInvoke(eqSymbol);

            // Reference-shaped enum-as-class: extract the buffer pointer through the
            // generated `Payload` SafeHandle (same surface as struct-projected-as-class).
            string equalsExpr(string lhs, string rhs)
            {
                if (eqSymbol == null) return $"Swift.Runtime.SwiftEquatable.Equals({lhs}, {rhs})";
                return $"_PInvoke_eq_pinned({lhs}, {rhs})";
            }

            if (eqSymbol != null)
            {
                _writer.WriteLines($$"""
                private static bool _PInvoke_eq_pinned({{_typeNameWithGenerics}} left, {{_typeNameWithGenerics}} right)
                {
                    bool _eqAddedLeft = false;
                    bool _eqAddedRight = false;
                    try
                    {
                        left.Payload.DangerousAddRef(ref _eqAddedLeft);
                        right.Payload.DangerousAddRef(ref _eqAddedRight);
                        return PInvoke_eq(left.Payload.DangerousGetHandle(), right.Payload.DangerousGetHandle());
                    }
                    finally
                    {
                        if (_eqAddedRight) right.Payload.DangerousRelease();
                        if (_eqAddedLeft) left.Payload.DangerousRelease();
                    }
                }
                """);
                _writer.WriteLine();
            }

            var hashCodeBody = _implementsHashable
                ? "return Swift.Runtime.SwiftHashable.GetHashCode(this);"
                : "return 0;";
            _writer.WriteLines($$"""
            public override bool Equals(object? obj)
            {
                return obj is {{_typeNameWithGenerics}} other && {{equalsExpr("this", "other")}};
            }

            public override int GetHashCode()
            {
                {{hashCodeBody}}
            }
            """);
            _writer.WriteLine();

            // Always emit both operators as a matched pair. The @_cdecl wrapper calls
            // Swift's `lhs == rhs`, which dispatches to either the synthesized or
            // user-declared `==` — that single source-of-truth feeds both C# operators
            // and prevents CS0216 (require matching `==`/`!=`).
            _writer.WriteLines($$"""
            public static bool operator ==({{_typeNameWithGenerics}}? left, {{_typeNameWithGenerics}}? right)
            {
                if (left is null) return right is null;
                if (right is null) return false;
                return {{equalsExpr("left", "right")}};
            }
            """);
            _writer.WriteLine();

            _writer.WriteLines($$"""
            public static bool operator !=({{_typeNameWithGenerics}}? left, {{_typeNameWithGenerics}}? right)
            {
                if (left is null) return right is not null;
                if (right is null) return true;
                return !{{equalsExpr("left", "right")}};
            }
            """);
            _writer.WriteLine();

            _writer.WriteLines($$"""
            public bool Equals({{_typeNameWithGenerics}}? other)
            {
                if (other is null) return false;
                return {{equalsExpr("this", "other")}};
            }
            """);
            _writer.WriteLine();
        }

        private static string GetEqualitySymbolName(EnumDecl enumDecl)
        {
            var moduleName = enumDecl.ModuleDecl?.Name ?? "Unknown";
            var safeTypeName = enumDecl.Name.Replace(".", "_");
            var hash = EmitterUtility.DeterministicHash8(enumDecl.MangledName ?? enumDecl.Name);
            return $"SBW_{moduleName}_{safeTypeName}_eq_{hash}";
        }

        private string? TryEmitSwiftEqualityWrapper()
        {
            if (_swiftWriter == null || _emissionContext == null || _wrapperLibraryName == null)
                return null;

            // Generic enums can't be instantiated by a non-generic @_cdecl wrapper.
            if (_enumDecl.GenericParameters.Count > 0)
                return null;

            var symbolName = GetEqualitySymbolName(_enumDecl);

            // S5 audited (Tier B): equality helpers live in the shared `_equality` bucket (also written by ClassHandler and TypeHandlerHelpers). One helper per enum type; symbol name from GetEqualitySymbolName is unique per type, so cross-emitter collisions in the bucket are impossible by construction.
            if (!_emissionContext.TryAddEqualityWrapperSymbol(symbolName))
                return symbolName;

            var swiftTypeName = _enumDecl.SwiftTypeName.ToString();

            // Enums with payloads are value types in Swift just like structs, so the
            // wrapper takes typed pointers via assumingMemoryBound — NOT
            // Unmanaged<AnyObject>.fromOpaque (that path is for ARC reference types).
            bool needsMainActor = WrapperValidation.NeedsMainActorAnnotation(_enumDecl, false);
            var equalityOperator = _enumDecl.Operators
                .FirstOrDefault(op => op.OperatorSymbol == "==" && op.Kind == OperatorKind.Binary);
            var availability = WrapperEmitterHelpers.MergeAvailabilityFromAncestors(
                equalityOperator?.AvailabilityAnnotations, _enumDecl);
            _swiftWriter.WriteLine();
            WrapperEmitterHelpers.EmitCdeclAnnotation(_swiftWriter, symbolName, needsMainActor, availability);
            _swiftWriter.WriteLines($$"""
            public func {{symbolName}}(_ lhs: UnsafeRawPointer, _ rhs: UnsafeRawPointer) -> UInt8 {
                let l = lhs.assumingMemoryBound(to: {{swiftTypeName}}.self).pointee
                let r = rhs.assumingMemoryBound(to: {{swiftTypeName}}.self).pointee
                return (l == r) ? 1 : 0
            }
            """);

            return symbolName;
        }

        private void EmitEqualityPInvoke(string symbolName)
        {
            _writer.WriteLines($$"""
            [global::System.Runtime.InteropServices.UnmanagedCallConv(CallConvs = new global::System.Type[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]
            [global::System.Runtime.InteropServices.LibraryImport("{{_wrapperLibraryName}}", EntryPoint = "{{symbolName}}")]
            [return: global::System.Runtime.InteropServices.MarshalAs(global::System.Runtime.InteropServices.UnmanagedType.U1)]
            private static partial bool PInvoke_eq(IntPtr lhs, IntPtr rhs);
            """);
            _writer.WriteLine();
        }
    }
}
