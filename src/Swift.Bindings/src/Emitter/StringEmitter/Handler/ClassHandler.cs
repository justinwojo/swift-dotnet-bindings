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
        public void Emit(CSharpWriter csWriter, SwiftWriter swiftWriter, IEnvironment env, Conductor conductor, TypeHandlerContext context)
        {
            var classEnv = (TypeEnvironment)env;
            var classDecl = (ClassDecl)classEnv.TypeDecl;
            var moduleDecl = classDecl.ModuleDecl ?? throw new ArgumentNullException(nameof(classDecl.ModuleDecl));

            if (GenericTypeEmitter.TryGetUnsupportedConstraint(classDecl, out var unsupportedConstraint))
            {
                var reason = unsupportedConstraint.Module is "SwiftUI" or "SwiftUICore"
                    ? SkipReason.SwiftUIConstraint
                    : unsupportedConstraint.Module == "Combine"
                        ? SkipReason.CombineFramework
                        : SkipReason.UnsupportedType;
                ReportCollector.RecordTypeSkipped(classDecl, reason, $"Unsupported generic constraint: {unsupportedConstraint.ModuleQualifiedName}");
                UnsupportedCommentEmitter.EmitTypeSkipped(csWriter, classDecl.Name, reason, $"generic constraint: {unsupportedConstraint.ModuleQualifiedName}");
                _logger.LogWarning(
                    "Skipping type '{TypeName}' - generic constraint references unsupported protocol '{Protocol}' from module '{Module}'.",
                    classDecl.Name,
                    unsupportedConstraint.Name,
                    unsupportedConstraint.Module);
                return;
            }

            // Create P/Invoke helper context for generic types (to avoid CS7042).
            // Pre-flatten conformances against the type database so the metadata-accessor
            // emitter can render the correct PWT plumbing.
            //
            // The ShouldSkip check MUST happen BEFORE RecordTypeEmitted: ReportCollector
            // suppresses RecordTypeSkipped if the type key is already in EmittedTypeKeys
            // (ReportCollector.cs:106), so a skipped type would be silently miscounted as
            // emitted. The cross-module extension branch below also depends on this ordering
            // since cross-module extensions of skipped types should not be emitted either.
            var ownPInvokeContext = PInvokeHelperContext.CreateIfGeneric(classDecl, env.TypeDatabase);
            if (ownPInvokeContext != null && TypeMetadataAccessorSkipGate.ShouldSkip(
                    classDecl, ownPInvokeContext, csWriter, _logger))
                return;

            ReportCollector.RecordTypeEmitted(classDecl);

            // Cross-module extension: type defined in module A, extended in module B.
            // Emit as a static extension class instead of a duplicate partial class.
            // Restrict to top-level receivers: nested types under a cross-module-extension
            // parent are physically owned by the current module (they were declared via
            // `extension ForeignModule.ForeignType { struct Nested {} }`) and must emit
            // through the normal type path so members are produced rather than re-routed as
            // a second cross-module hop that would skip on missing TypeDatabase entries.
            if (!string.IsNullOrEmpty(classDecl.SwiftTypeName.Module) &&
                classDecl.SwiftTypeName.Module != moduleDecl.Name &&
                classDecl.ParentDecl is ModuleDecl)
            {
                CrossModuleExtensionEmitter.Emit(
                    csWriter, swiftWriter, classDecl, moduleDecl, conductor, env, _logger,
                    context: context,
                    recurseNestedTypes: (decls, ctx) =>
                        base.HandleBaseDecl(csWriter, swiftWriter, decls, conductor, env.TypeDatabase, ctx));
                return;
            }

            // Get generic type parts if this is a generic type
            var typeNameWithGenerics = GenericTypeEmitter.GetTypeNameWithGenerics(classDecl, env.TypeDatabase);
            var whereClause = GenericTypeEmitter.GetWhereClause(classDecl, env.TypeDatabase);
            var pinvokeHelperContext = ownPInvokeContext ?? context.PInvokeHelperContext;

            // Compute property renames to resolve property/nested-type name collisions
            var propertyRenames = NameProvider.ComputePropertyRenames(classDecl, env.TypeDatabase);

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
                    classDecl,
                    typeNameWithGenerics,
                    moduleDecl.Name,
                    env.TypeDatabase,
                    conformanceValidator);

                // A class is derived only if its resolved base class will actually be emitted.
                // If the base class would be skipped (e.g., unsupported generic constraints),
                // fall back to flat emission to avoid referencing a non-emitted base type.
                bool isDerived = IsEffectivelyDerived(classDecl);
                bool isSameModuleDerived = classDecl.HasResolvedSuperclass
                    && !GenericTypeEmitter.TryGetUnsupportedConstraint(classDecl.ResolvedSuperclass!, out _);
                bool isCrossModuleDerived = isDerived && !isSameModuleDerived;
                bool isObjCRooted = classDecl.IsObjCRooted;
                // An ObjC-rooted boundary class directly inherits an ObjC type (e.g., CALayer)
                // and is NOT derived from a Swift parent (same-module or cross-module).
                bool isObjCBoundary = isObjCRooted && !isDerived;

                if (isSameModuleDerived)
                {
                    var baseName = GenericTypeEmitter.GetTypeNameWithGenerics(classDecl.ResolvedSuperclass!, env.TypeDatabase);

                    // Collect base class interfaces to filter duplicates
                    var baseInterfaces = new HashSet<string>(
                        ProtocolConformanceHelper.GetImplementedInterfaces(
                            classDecl.ResolvedSuperclass!,
                            baseName,
                            moduleDecl.Name, env.TypeDatabase, conformanceValidator));

                    // Start with base class name. Keep ISwiftObject (needed for explicit interface
                    // re-implementation with derived type metadata). Skip IDisposable (inherited).
                    // Skip base-class protocols that aren't parameterized differently on derived.
                    var derivedInterfaces = new List<string> { baseName };
                    foreach (var iface in interfaces)
                    {
                        if (iface == "IDisposable")
                            continue;
                        if (iface == "ISwiftObject")
                        {
                            derivedInterfaces.Add(iface);
                            continue;
                        }
                        if (!baseInterfaces.Contains(iface))
                            derivedInterfaces.Add(iface);
                    }
                    interfaces = derivedInterfaces;
                }
                else if (isCrossModuleDerived)
                {
                    // Cross-module Swift parent: emit `: ParentNamespace.Parent` followed by the
                    // derived class's interface set, with the parent's already-implemented
                    // interfaces filtered out so we don't list e.g. IRealityCoordinateSpace twice.
                    // The parent's TypeRecord supplies the C# name and (when available) the
                    // recursive ProtocolConformances chain we walk for dedup.
                    var baseName = classDecl.CrossModuleSuperclassCSharpName!;
                    var baseInterfaces = ProtocolConformanceHelper.GetCrossModuleInheritedInterfaces(
                        classDecl.CrossModuleSuperclassRecord!, env.TypeDatabase, moduleDecl.Name);

                    var derivedInterfaces = new List<string> { baseName };
                    foreach (var iface in interfaces)
                    {
                        if (iface == "IDisposable")
                            continue;
                        if (iface == "ISwiftObject")
                        {
                            derivedInterfaces.Add(iface);
                            continue;
                        }
                        if (!baseInterfaces.Contains(iface))
                            derivedInterfaces.Add(iface);
                    }
                    interfaces = derivedInterfaces;
                }
                else if (isObjCBoundary)
                {
                    // ObjC-rooted boundary class: inherit from MAUI ObjC binding type.
                    // Replace IDisposable (inherited from NSObject) with ObjC base type.
                    // Fallback: cross-module ObjC-rooted with unresolved Swift parent → Foundation.NSObject
                    // (all ObjC-rooted Swift classes ultimately derive from NSObject).
                    var objcBaseName = MarshallingHelpers.GetObjCBaseTypeName(classDecl) ?? "Foundation.NSObject";
                    var boundaryInterfaces = new List<string> { objcBaseName };
                    foreach (var iface in interfaces)
                    {
                        if (iface == "IDisposable")
                            continue; // Inherited from NSObject
                        boundaryInterfaces.Add(iface);
                    }
                    interfaces = boundaryInterfaces;
                }

                if (classDecl.IsActor)
                    csWriter.WriteLine("// Swift actor type - methods are actor-isolated unless marked nonisolated");

                XmlDocCommentEmitter.EmitDocComment(csWriter, classDecl);
                AvailabilityAttributeEmitter.EmitAvailabilityAttributes(csWriter, classDecl, emitObsolete: true);
                var (opaqueEmittable, opaqueSkipped) = MemberEmissionValidator.CountEmittableMembers(classDecl, env.TypeDatabase);
                if (opaqueEmittable == 0 && opaqueSkipped > 0)
                {
                    TypeAnnotationHelper.EmitOpaqueTypeAnnotation(csWriter, opaqueSkipped);
                    context.GetEmissionContext()?.AddEmittedOpaqueType(classDecl.SwiftTypeName.ModuleQualifiedName);
                }
                else
                    TypeAnnotationHelper.EmitDisposalRemarks(csWriter, classDecl);
                TypeAnnotationHelper.EmitSwiftSendableAnnotation(csWriter, classDecl);
                if (classDecl.Name.StartsWith("_"))
                    csWriter.WriteLine("[global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]");
                var classDeclaration = $"public partial class {typeNameWithGenerics} : {string.Join(", ", interfaces)}";
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
                        ReportCollector.RecordMemberSkipped(propertyDecl, SkipReason.UnsupportedType, "Actor runtime property 'unownedExecutor' is not user-facing.");
                        continue;
                    }

                    // Bug #9: Skip duplicate property names (static + instance with same C# name)
                    // Use post-rename name for consistency with the propertyNames collision set below.
                    var csPropertyName = NameProvider.GetFinalMemberName(
                        NameProvider.GetPropertyName(propertyDecl.Name, classDecl.Name), propertyRenames);
                    if (!emittedPropertyNames.Add(csPropertyName))
                    {
                        _logger.LogInformation($"Skipping duplicate property '{classDecl.Name}.{csPropertyName}' (static/instance collision).");
                        ReportCollector.RecordMemberSkipped(propertyDecl, SkipReason.DuplicateSignature, $"Property '{csPropertyName}' already emitted with different staticness.");
                        continue;
                    }

                    if (MemberEmissionValidator.IsSynthesizedProtocolProperty(propertyDecl, classDecl))
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

                // Emit private fields and handle.
                // ObjC-rooted classes skip all handle/Dispose — lifecycle managed by NSObject.
                if (!isObjCRooted)
                {
                    // Class constructors use Unmanaged.passRetained().toOpaque() — no _payloadSize needed.
                    // Only structs/enums allocate via _payloadSize (SwiftSafeHandle path).
                    // Emitting _payloadSize for classes triggers SwiftObjectHelper<T>.GetTypeMetadata().Size
                    // at class load time, which can return garbage or cause crashes (e.g., CryptoSwift SIGABRT).

                    // Only root classes emit _handle, Payload property, Dispose().
                    // SwiftClassHandle calls Arc.Release directly (no VWT Destroy wrapper needed).
                    // No generated finalizer needed — SafeHandle's built-in finalizer calls ReleaseHandle.
                    if (!isDerived)
                    {
                        // !isDerived implies the C# class declaration has no base class
                        // (only interfaces — see line 231 / cross-module branches above).
                        // System.Object has none of these handle members, so `new` would
                        // produce CS0109 ("does not hide an accessible member") for every
                        // root-emitted class with an unbindable Swift superclass.
                        WriteClassHandleField(csWriter, typeNameWithGenerics, needsNewModifier: false);
                        WriteClassHandleAccessors(csWriter, typeNameWithGenerics, needsNewModifier: false);
                    }
                }

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
                        ReportCollector.RecordMemberSkipped(operatorDecl, SkipReason.UnsupportedType, $"Operator '{operatorDecl.OperatorSymbol}' has no C# equivalent.");
                    }
                }
                // Handle paired operators (e.g., if == is defined but != is not)
                // Use typeNameWithGenerics to ensure generic types have proper type parameters in operator signatures
                operatorHandler.ValidateAndEmitPairs(csWriter, classDecl.Operators, typeNameWithGenerics, emittedOperatorSymbols, isReferenceType: true);

                bool hasEquality = emittedOperatorSymbols.Contains("==");
                bool hasInequality = emittedOperatorSymbols.Contains("!=");

                // Emit ISwiftObject implementation
                var iSwiftObjectWriter = new ClassISwiftObjectMethodWriter(csWriter, env.TypeDatabase, moduleDecl, classDecl, typeNameWithGenerics, pinvokeHelperContext, swiftWriter, context.GetEmissionContext(), hasBoxable: interfaces.Contains("Swift.Runtime.IExistentialBoxable"));
                var equatableWriter = new ClassEqualityMethodsWriter(csWriter, classDecl, typeNameWithGenerics, hasEquality, hasInequality, swiftWriter, context.GetEmissionContext(), env.TypeDatabase.AsyncLibraryName, env.TypeDatabase);

                // Derived classes emit equality if they have their own IEquatable<DerivedType>
                // (IEquatable<Derived> is a different interface from IEquatable<Base>).
                // Root classes always emit equality if Equatable.
                if (!isDerived || interfaces.Any(i => i.StartsWith("IEquatable<")))
                    equatableWriter.WriteSwiftEquatableImplementation();
                iSwiftObjectWriter.WriteClassImplementation();

                ToStringHelper.EmitToStringIfDescriptionExists(csWriter, classDecl, propertyRenames);

                // Collect property and nested type names for method/member collision detection
                var propertyNames = new HashSet<string>(classDecl.Properties.Select(p =>
                    NameProvider.GetFinalMemberName(
                        NameProvider.GetPropertyName(p.Name, classDecl.Name), propertyRenames)));
                // Nested type names collide with method names in C# (CS0102)
                foreach (var nestedType in classDecl.Types)
                    propertyNames.Add(NameProvider.ToPascalCase(nestedType.Name));

                SubscriptHandler.EmitSubscripts(csWriter, swiftWriter, classDecl, env.TypeDatabase, conductor, childContext, _logger);

                // Push type name (with generics) for nested type factory registration (NativeAOT).
                // C# requires Outer<T>.Nested, not Outer.Nested, for nested types under generic outers.
                var emissionCtx = context.GetEmissionContext();
                emissionCtx?.PushTypeNesting(typeNameWithGenerics);
                base.HandleBaseDecl(csWriter, swiftWriter, classDecl.Types, conductor, env.TypeDatabase, childContext);
                emissionCtx?.PopTypeNesting();
                base.HandleBaseDecl(csWriter, swiftWriter, classDecl.Methods, conductor, env.TypeDatabase, childContext, propertyNames);

                // Emit concrete protocol specializations (e.g., func hash<D: DataProtocol>(data: D))
                // Must be inside the class body — these emit instance/static methods.
                var specEngine = context.GetEmissionContext().SpecializationEngine;
                if (specEngine != null)
                {
                    ConcreteProtocolSpecializationEmitter.EmitConcreteSpecializations(
                        csWriter, swiftWriter, classDecl,
                        env.TypeDatabase, context.GetEmissionContext(), specEngine, _logger);
                }

                // AsyncSequence → IAsyncEnumerable<T>: emit GetAsyncEnumerator that
                // adapts the Swift iterator's NextAsync(ct) → Task<T?> to
                // IAsyncEnumerator<T>. Interface adoption is added by GetImplementedInterfaces.
                AsyncSequenceEmitter.TryEmitAsyncEnumerableBridge(csWriter, classDecl, env.TypeDatabase);

                csWriter.Indent--;
                csWriter.WriteLine("}");
                csWriter.WriteLine();

                // Emit constrained-extension specialization classes (e.g., extension X where T == Concrete)
                ConstrainedExtensionEmitter.EmitConstrainedExtensions(
                    csWriter, swiftWriter, classDecl,
                    env.TypeDatabase, context.GetEmissionContext(), _logger);

                // Generic-parent CSM: per-parent-conformer static extension classes
                // (e.g. HMAC<SHA256>.Update overloads). Must live outside the parent's
                // body so the receiver can close over the generic.
                if (specEngine != null)
                {
                    ConcreteProtocolSpecializationEmitter.EmitConcreteSpecializationsForGenericParent(
                        csWriter, swiftWriter, classDecl,
                        env.TypeDatabase, context.GetEmissionContext(), specEngine, _logger);

                    // Session 4 — typed KeyPath singleton trampolines for closed
                    // conformers whose nested associated-type bag is referenced as a
                    // KeyPath Root in any of this generic parent's methods. Same
                    // emission window as CSM extensions: namespace-scope, after the
                    // parent class body is closed.
                    KeyPathSingletonEmitter.EmitKeyPathSingletonsForGenericParent(
                        csWriter, swiftWriter, classDecl,
                        env.TypeDatabase, context.GetEmissionContext(), specEngine, _logger);

                    // Session 6c Route C — per-(conformer × distinct projectable V)
                    // Sort overloads for unconstrained-V keypath-sort methods on this
                    // PAT-constrained generic parent. Sibling to CSM, not a CSM
                    // extension: Route C suppresses the original method's open-V
                    // emission and replaces it with a closed set of typed overloads.
                    KeyPathBagValueSpecializationEmitter.EmitRouteCSpecializationsForGenericParent(
                        csWriter, swiftWriter, classDecl,
                        env.TypeDatabase, context.GetEmissionContext(), specEngine, _logger);
                }

                // Emit P/Invoke helper class(es) after the main class.
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
        /// Returns true if the class has a superclass whose binding will actually be reachable
        /// from the emitted C# — either a same-module ClassDecl that survives generic-constraint
        /// validation, or a cross-module Swift parent registered in the global type database.
        /// This is the canonical "effectively derived" predicate; use it everywhere instead of
        /// raw <c>HasResolvedSuperclass</c> so cross-module hierarchies (Bug #14) are picked up.
        /// </summary>
        internal static bool IsEffectivelyDerived(ClassDecl classDecl)
        {
            if (classDecl.HasResolvedSuperclass &&
                !GenericTypeEmitter.TryGetUnsupportedConstraint(classDecl.ResolvedSuperclass!, out _))
                return true;
            return classDecl.HasCrossModuleSwiftSuperclass;
        }

        /// <summary>
        /// Writes the _handle instance field (root classes only — derived classes inherit).
        /// Uses SwiftClassHandle&lt;T&gt; which directly holds the Swift object pointer (no buffer).
        /// </summary>
        private static void WriteClassHandleField(CSharpWriter csWriter, string typeNameWithGenerics, bool needsNewModifier = false)
        {
            var newKeyword = needsNewModifier ? "new " : "";
            csWriter.WriteLine("[EditorBrowsable(EditorBrowsableState.Never)]");
            csWriter.WriteLine($"{newKeyword}protected SwiftClassHandle<{typeNameWithGenerics}> _handle = SwiftClassHandle<{typeNameWithGenerics}>.Zero;");
            csWriter.WriteLine();
        }

        /// <summary>
        /// Writes the handle accessor, Payload compatibility property, and Dispose for classes.
        /// No generated finalizer needed — SwiftClassHandle's built-in SafeHandle finalizer
        /// calls ReleaseHandle → Arc.Release, which is safe on both Mono and NativeAOT.
        /// </summary>
        private static void WriteClassHandleAccessors(CSharpWriter csWriter, string typeNameWithGenerics, bool needsNewModifier = false)
        {
            var newKeyword = needsNewModifier ? "new " : "";
            csWriter.WriteLine("[EditorBrowsable(EditorBrowsableState.Never)]");
            csWriter.WriteLine($"{newKeyword}public SwiftClassHandle<{typeNameWithGenerics}> Payload => _handle;");
            csWriter.WriteLine($"IntPtr ISwiftObject.SwiftHandle => _handle.DangerousGetHandle();");
            csWriter.WriteLine($"internal {newKeyword}IntPtr GetSwiftHandle() => _handle.DangerousGetHandle();");
            csWriter.WriteLine();
            var newDispose = needsNewModifier ? "new " : "";
            var disposeMethods = $$"""
            /// <summary>
            /// Releases the underlying Swift ARC reference. Safe to call multiple times.
            /// Not required for correctness — the finalizer handles ARC cleanup automatically.
            /// Use for deterministic cleanup of scarce resources.
            /// </summary>
            public {{newDispose}}void Dispose()
            {
                _handle.Dispose();
                GC.SuppressFinalize(this);
            }
            """;
            csWriter.WriteLines(disposeMethods);
            csWriter.WriteLine();
        }

        /// <summary>
        /// Post-emission fixup: stamps each Class TypeRecord with the Swift signatures of its
        /// instance methods that survived emission (<c>WasEmitted == true</c>). Must run after
        /// <c>EmitModule</c> (so all <c>WasEmitted</c> bits are set) and before module database
        /// serialization. Consumed by <c>WrapperEmitter.HasMethodInResolvedAncestors</c> in a
        /// downstream module: when a derived class declares <c>override</c> on a method whose
        /// parent lives in another module, the verifier consults the parent record's
        /// <see cref="EmittedClassMethod"/> list and only emits C# <c>override</c> on a match —
        /// otherwise it falls through to <c>virtual</c>, avoiding silent CS0115 when the parent
        /// binding skipped the method (e.g., validation gates dropped it).
        /// </summary>
        public static void PopulateEmittedClassMethods(ModuleDecl moduleDecl, ITypeDatabase typeDatabase)
        {
            CollectAndStampClassMethods(moduleDecl.Types, typeDatabase);
        }

        private static void CollectAndStampClassMethods(IEnumerable<TypeDecl> types, ITypeDatabase typeDatabase)
        {
            foreach (var typeDecl in types)
            {
                if (typeDecl is ClassDecl classDecl
                    && typeDatabase.TryGetTypeRecord(classDecl.SwiftTypeName, out var record)
                    && record.Kind == TypeRecordKind.Class)
                {
                    var emitted = new List<EmittedClassMethod>();
                    foreach (var method in classDecl.Methods)
                    {
                        if (!method.WasEmitted) continue;
                        if (method.IsAccessor) continue;
                        if (method.IsConstructor) continue;
                        // Static methods can't participate in C# override dispatch — exclude
                        // so the cross-module verifier only sees instance methods.
                        if (method.MethodType == MethodType.Static) continue;

                        var paramTypes = new List<string>(method.CSSignature.Count - 1);
                        for (int i = 1; i < method.CSSignature.Count; i++)
                            paramTypes.Add(method.CSSignature[i].SwiftTypeSpec.ToString());
                        // Prefer the C# name stamped at emission time on MethodDecl — that's
                        // the truth, including any numeric suffix that IHandler assigned for
                        // projected-signature collisions (`Foo` vs `Foo2`). Only when the field
                        // is absent (synthesized methods that bypass the conductor) do we fall
                        // back to recomputing via NameProvider — which doesn't see the runtime
                        // collision suffix, so the fallback is acceptable only when there's no
                        // collision (the bypass paths don't participate in projected-signature
                        // dedup, so collisionIndex is always 0 for them).
                        var csharpName = method.EmittedCSharpName
                            ?? WrapperEmitter.ComputeMethodCSharpName(method, classDecl, typeDatabase);
                        emitted.Add(new EmittedClassMethod(method.Name, csharpName, paramTypes));
                    }

                    typeDatabase.UpdateTypeRecord(classDecl.SwiftTypeName,
                        record with
                        {
                            EmittedClassMethods = emitted,
                            EmittedMetadataPInvoke = classDecl.EmittedMetadataPInvoke
                        });
                }

                if (typeDecl.Types.Count > 0)
                    CollectAndStampClassMethods(typeDecl.Types, typeDatabase);
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
        private readonly string _typeNameWithGenerics;
        private readonly string _constructorName;
        private readonly PInvokeHelperContext? _pinvokeHelperContext;
        private readonly bool _isDerived;
        private readonly bool _isObjCRooted;
        private readonly bool _isObjCBoundary;
        private readonly string _rootBaseTypeNameWithGenerics;
        private readonly SwiftWriter? _swiftWriter;
        private readonly ModuleEmissionContext? _emissionCtx;
        private readonly bool _hasBoxable;

        public ClassISwiftObjectMethodWriter(CSharpWriter csWriter, ITypeDatabase typeDatabase, ModuleDecl moduleDecl, ClassDecl classDecl, string typeNameWithGenerics, PInvokeHelperContext? pinvokeHelperContext = null, SwiftWriter? swiftWriter = null, ModuleEmissionContext? emissionCtx = null, bool hasBoxable = false)
        {
            _writer = csWriter;
            _typeDatabase = typeDatabase;
            _moduleDecl = moduleDecl;
            _classDecl = classDecl;
            _typeNameWithGenerics = typeNameWithGenerics;
            var angleBracket = typeNameWithGenerics.IndexOf('<');
            _constructorName = angleBracket >= 0 ? typeNameWithGenerics.Substring(0, angleBracket) : typeNameWithGenerics;
            _pinvokeHelperContext = pinvokeHelperContext;
            _isDerived = ClassHandler.IsEffectivelyDerived(classDecl);
            _isObjCRooted = classDecl.IsObjCRooted;
            _isObjCBoundary = _isObjCRooted && !_isDerived;
            _rootBaseTypeNameWithGenerics = GetRootBaseTypeNameWithGenerics(classDecl, _typeDatabase);
            _swiftWriter = swiftWriter;
            _emissionCtx = emissionCtx;
            _hasBoxable = hasBoxable;
        }

        /// <summary>
        /// Walks the ResolvedSuperclass chain to find the root base class type name.
        /// Stops at non-emittable ancestors (unsupported generic constraints) to stay
        /// consistent with IsEffectivelyDerived — a flat-emitted class must use its own
        /// type name so _payload and the private constructor agree on SwiftSafeHandle&lt;T&gt;.
        /// When the same-module walk lands on a cross-module-derived class, continues up
        /// through TypeRecord.SuperclassTypeName so derived classes share their cross-module
        /// root's <c>SwiftClassHandle&lt;T&gt;</c> typing (the inherited <c>_handle</c> field
        /// was emitted in the parent module's binding output against that root).
        /// </summary>
        internal static string GetRootBaseTypeNameWithGenerics(ClassDecl classDecl, ITypeDatabase? typeDatabase = null)
        {
            var current = classDecl;
            while (current.HasResolvedSuperclass
                   && !GenericTypeEmitter.TryGetUnsupportedConstraint(current.ResolvedSuperclass!, out _))
                current = current.ResolvedSuperclass!;

            // Cross-module fallthrough: if the current class derives from a Swift parent in
            // another module, walk that parent's TypeRecord chain to the root.
            if (current.HasCrossModuleSwiftSuperclass && typeDatabase != null)
            {
                var record = current.CrossModuleSuperclassRecord!;
                var visited = new HashSet<string>(StringComparer.Ordinal)
                {
                    current.SwiftTypeName.ModuleQualifiedName
                };
                while (record.SuperclassTypeName != null
                       && visited.Add(record.SwiftTypeName.ModuleQualifiedName)
                       && typeDatabase.TryGetTypeRecord(record.SuperclassTypeName, out var parent)
                       && parent.Kind == TypeRecordKind.Class)
                {
                    record = parent;
                }
                return record.CSharpTypeName.FullyQualifiedName;
            }
            return GenericTypeEmitter.GetTypeNameWithGenerics(current, typeDatabase);
        }

        /// <summary>
        /// Checks whether the class has a truly parameterless constructor (init() in Swift).
        /// Only checks for zero-parameter constructors in the ABI, NOT constructors with all-default
        /// parameters. The default-parameter overload emitter may skip methods with unsupported types,
        /// so we can't rely on it to produce a parameterless overload.
        /// </summary>
        private static bool HasParameterlessConstructor(ClassDecl classDecl)
        {
            return classDecl.Methods.Any(m =>
                m.IsConstructor && m.Visibility == Visibility.Public &&
                m.CSSignature.Count <= 1); // CSSignature[0] is return type, no parameters = Count <= 1
        }

        /// <summary>
        /// Writes the implementation for ISwiftObject methods for classes.
        /// </summary>
        public void WriteClassImplementation()
        {
            if (_isObjCBoundary)
            {
                // ObjC-rooted boundary class: NSObject.Handle IS the Swift object pointer.
                _writer.WriteLine("IntPtr ISwiftObject.SwiftHandle => Handle;");
                _writer.WriteLine("internal IntPtr GetSwiftHandle() => Handle;");
                _writer.WriteLine();
            }
            WriteGetTypeMetadata();
            WriteNewFromPayload();
            WriteMarshalToSwift();
            WriteGetProtocolConformanceDescriptor();
            WriteBoxAsExistential1(_hasBoxable);
            RecordTypeIfNonGeneric();
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
                // Type metadata accessor: Swift's metadata accessor for a generic type expects
                // metadata + witness tables for any protocol-constrained generic params (per
                // runtime-metadata.md). AddMetadataAccessorDeclaration transparently routes to
                // thin-mode (<= 3 args) or buffer-mode (> 3 args) on the helper side.
                var metadataArgs = string.Join(", ", _pinvokeHelperContext.GetTypeMetadataAccessorArgumentList());
                _writer.WriteLine($"static TypeMetadata ISwiftObject.GetTypeMetadata() => {_pinvokeHelperContext.HelperClassName}.PInvoke_getMetadata(TypeMetadataRequest.Complete, {metadataArgs});");
                _writer.WriteLine();

                _pinvokeHelperContext.AddMetadataAccessorDeclaration(libPath, metadataAccessor);
            }
            else if (_swiftWriter != null && _emissionCtx != null &&
                     !string.IsNullOrEmpty(_typeDatabase.AsyncLibraryName))
            {
                // Xcframework mode: emit @_cdecl metadata wrapper.
                // Internal types (both @usableFromInline and truly internal) are inaccessible
                // by name from external Swift code, so the wrapper's `Module.Type.self` reference
                // won't compile. Fall back to CallConvSwift targeting the dylib directly.
                var moduleQualified = _classDecl.SwiftTypeName.ModuleQualifiedName;
                var moduleName = _classDecl.SwiftTypeName.Module;

                if (_classDecl.IsModuleInternal)
                {
                    // Fallback: use CallConvSwift P/Invoke targeting the dylib's metadata accessor
                    _writer.WriteLine("static TypeMetadata ISwiftObject.GetTypeMetadata() => PInvoke_getMetadata();");
                    _writer.WriteLine();

                    foreach (var line in PInvokeEmitHelper.FormatDeclarationLines(new PInvokeEmissionInfo
                    {
                        LibraryPath = libPath,
                        EntryPoint = metadataAccessor,
                        MethodName = "PInvoke_getMetadata",
                        ReturnType = "TypeMetadata",
                        ParametersString = "",
                        Visibility = PInvokeVisibility.Internal,
                        HasNewModifier = HasMetadataPInvokeInResolvedAncestors(_classDecl),
                        CallingConvention = PInvokeCallingConvention.Swift
                    }))
                        _writer.WriteLine(line);
                    _writer.WriteLine();
                    _classDecl.EmittedMetadataPInvoke = true;
                }
                else
                {
                    var symbol = MetadataWrapperEmitter.GetMetadataSymbolName(moduleName, moduleQualified);
                    MetadataWrapperEmitter.EmitIfNeeded(_swiftWriter, moduleName, moduleQualified, symbol, _emissionCtx, _classDecl);

                    // Try wrapper DLL first (Cdecl), fall back to dylib (CallConvSwift)
                    // when the wrapper wasn't compiled for this module.
                    _writer.WriteLines("""
                        static TypeMetadata ISwiftObject.GetTypeMetadata()
                        {
                            try
                            {
                                return PInvoke_getMetadata();
                            }
                            catch (System.DllNotFoundException)
                            {
                                return PInvoke_getMetadata_fallback();
                            }
                            catch (System.EntryPointNotFoundException)
                            {
                                return PInvoke_getMetadata_fallback();
                            }
                        }
                        """);
                    _writer.WriteLine();

                    var hasNew = HasMetadataPInvokeInResolvedAncestors(_classDecl);
                    foreach (var line in PInvokeEmitHelper.FormatDeclarationLines(new PInvokeEmissionInfo
                    {
                        LibraryPath = _typeDatabase.AsyncLibraryName!,
                        EntryPoint = symbol,
                        MethodName = "PInvoke_getMetadata",
                        ReturnType = "TypeMetadata",
                        ParametersString = "",
                        Visibility = PInvokeVisibility.Internal,
                        CallingConvention = PInvokeCallingConvention.Cdecl,
                        HasNewModifier = hasNew
                    }))
                        _writer.WriteLine(line);
                    _writer.WriteLine();

                    // Fallback P/Invoke targeting the dylib's metadata accessor directly
                    foreach (var line in PInvokeEmitHelper.FormatDeclarationLines(new PInvokeEmissionInfo
                    {
                        LibraryPath = libPath,
                        EntryPoint = metadataAccessor,
                        MethodName = "PInvoke_getMetadata_fallback",
                        ReturnType = "TypeMetadata",
                        ParametersString = "",
                        Visibility = PInvokeVisibility.Internal,
                        HasNewModifier = hasNew,
                        CallingConvention = PInvokeCallingConvention.Swift
                    }))
                        _writer.WriteLine(line);
                    _writer.WriteLine();
                    _classDecl.EmittedMetadataPInvoke = true;
                }
            }
            else
            {
                // Manual mode: existing CallConvSwift path
                _writer.WriteLine("static TypeMetadata ISwiftObject.GetTypeMetadata() => PInvoke_getMetadata();");
                _writer.WriteLine();

                foreach (var line in PInvokeEmitHelper.FormatDeclarationLines(new PInvokeEmissionInfo
                {
                    LibraryPath = libPath,
                    EntryPoint = metadataAccessor,
                    MethodName = "PInvoke_getMetadata",
                    ReturnType = "TypeMetadata",
                    ParametersString = "",
                    Visibility = PInvokeVisibility.Internal,
                    HasNewModifier = HasMetadataPInvokeInResolvedAncestors(_classDecl),
                    CallingConvention = PInvokeCallingConvention.Swift
                }))
                    _writer.WriteLine(line);
                _writer.WriteLine();
                _classDecl.EmittedMetadataPInvoke = true;
            }
        }

        /// <summary>
        /// Returns true when any ancestor (same-module via <see cref="ClassDecl.ResolvedSuperclass"/>
        /// or cross-module via persisted <see cref="TypeRecord.EmittedMetadataPInvoke"/>) emitted an
        /// instance-level <c>PInvoke_getMetadata</c> on its own class body. Drives the C# <c>new</c>
        /// modifier on the derived class's <c>PInvoke_getMetadata</c> declaration so the modifier
        /// only appears when there is a real inherited member to shadow — otherwise the compiler
        /// emits CS0109 ("does not hide an inherited member"). The flag is false on parents whose
        /// metadata accessor lives on a generic <see cref="PInvokeHelperContext"/> helper class
        /// instead of on the parent itself; those parents have nothing for the derived member
        /// to shadow.
        /// </summary>
        internal static bool HasMetadataPInvokeInResolvedAncestors(ClassDecl classDecl)
        {
            for (var a = classDecl.ResolvedSuperclass; a != null; a = a.ResolvedSuperclass)
            {
                if (a.EmittedMetadataPInvoke) return true;
            }
            // Cross-module immediate parent: consult its persisted flag. When the flag is
            // null (legacy module databases produced before this field existed) preserve
            // pre-fix behavior — assume the parent emitted PInvoke_getMetadata so that
            // already-published parent NuGets continue to compile against newly generated
            // children. The CS0109 warning was emitted before this fix anyway.
            if (classDecl.HasCrossModuleSwiftSuperclass)
            {
                var flag = classDecl.CrossModuleSuperclassRecord!.EmittedMetadataPInvoke;
                if (flag == true) return true;
                if (flag == null) return true;
            }
            return false;
        }

        /// <summary>
        /// Writes the NewFromPayload method for the class.
        /// The handle must carry a +1 ARC retain that this wrapper takes ownership of.
        /// </summary>
        private void WriteNewFromPayload()
        {
            if (_isObjCRooted)
            {
                // ObjC-rooted: handle is the raw Swift object pointer (same as pure Swift classes).
                // Wrap with SwiftHandle → base(NativeHandle) → NSObject takes ownership.
                var text = $$"""
                [EditorBrowsable(EditorBrowsableState.Never)]
                static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
                {
                    return new {{_typeNameWithGenerics}}(new SwiftHandle(handle));
                }
                """;
                _writer.WriteLines(text);
                _writer.WriteLine();
            }
            else
            {
                // Wrap the raw IntPtr in a SwiftHandle explicitly so the call resolves to the
                // private SwiftHandle-taking constructor. Relying on the implicit IntPtr→SwiftHandle
                // conversion causes CS0121 ambiguity when the class also has a public single-arg
                // constructor whose parameter type accepts an implicit IntPtr conversion
                // (e.g. SwiftOptional<IntPtr> for non-bridged optional parameters).
                var text = $$"""
                [EditorBrowsable(EditorBrowsableState.Never)]
                static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
                {
                    var obj = new {{_typeNameWithGenerics}}(new SwiftHandle(handle));
                    Swift.Runtime.SwiftDisposeScope.TryRegister(obj);
                    return obj;
                }
                """;
                _writer.WriteLines(text);
                _writer.WriteLine();
            }

            EmitPrivateConstructor();
        }

        /// <summary>
        /// Writes the private constructor accepting a SwiftHandle.
        /// The handle must carry a +1 ARC retain that this wrapper takes ownership of.
        /// Dispose releases exactly one ARC reference.
        /// </summary>
        private void EmitPrivateConstructor()
        {
            if (_isObjCRooted)
            {
                // ObjC-rooted: SwiftHandle entry-point constructor chains to base(NativeHandle).
                // DangerousRelease() balances: Swift returns +1, MAUI NSObject(NativeHandle,false) retains +2, release back to +1.
                var text = $$"""
                internal {{_constructorName}}(SwiftHandle handle) : base((ObjCRuntime.NativeHandle)handle.Handle)
                {
                    DangerousRelease();
                }
                """;
                _writer.WriteLines(text);
                _writer.WriteLine();

                // Protected NativeHandle constructor for same-module Swift subclass chaining.
                // NO DangerousRelease — only the entry-point SwiftHandle ctor releases.
                var protectedCtor = $$"""
                [EditorBrowsable(EditorBrowsableState.Never)]
                protected {{_constructorName}}(ObjCRuntime.NativeHandle handle) : base(handle) { }
                """;
                _writer.WriteLines(protectedCtor);
                _writer.WriteLine();
            }
            else
            {
                // SwiftClassHandle directly holds the Swift object pointer (no buffer).
                // Derived classes assign to the inherited _handle field using the ROOT base class's
                // SwiftClassHandle<T> type parameter. Arc.Release operates on the isa pointer
                // inside the Swift object, ignoring the metadata's T.
                var handleType = _rootBaseTypeNameWithGenerics;
                // Derived private constructors chain to the base's protected sentinel constructor
                var baseChain = _isDerived ? " : base(default(SwiftInheritanceChain))" : "";
                var text = $$"""
                internal {{_constructorName}}(SwiftHandle handle){{baseChain}}
                {
                    _handle = new SwiftClassHandle<{{handleType}}>(handle);
                }
                """;

                _writer.WriteLines(text);
                _writer.WriteLine();

                // All classes emit a protected constructor with a SwiftInheritanceChain sentinel parameter
                // for derived class constructor chaining. Derived constructors chain to
                // base(default(SwiftInheritanceChain)) to invoke this constructor. SwiftInheritanceChain
                // is a marker struct from Swift.Runtime that cannot conflict with any Swift-generated
                // constructor parameters (unlike bool, int, etc. which Swift types commonly use).
                {
                    var sentinelBaseChain = _isDerived ? " : base(default(SwiftInheritanceChain))" : "";
                    var protectedCtor = $$"""
                    [EditorBrowsable(EditorBrowsableState.Never)]
                    protected {{_constructorName}}(SwiftInheritanceChain _swiftObject){{sentinelBaseChain}} { }
                    """;
                    _writer.WriteLines(protectedCtor);
                    _writer.WriteLine();
                }
            }
        }

        /// <summary>
        /// Records this type for NativeAOT factory registration if it's non-generic.
        /// Generic types rely on constrained code paths for registration.
        /// Also records protocol conformance pairs for NativeAOT pre-registration.
        /// </summary>
        private void RecordTypeIfNonGeneric()
        {
            if (_emissionCtx != null && !_typeNameWithGenerics.Contains('<'))
            {
                _emissionCtx.RecordSwiftObjectType(_typeNameWithGenerics);
                foreach (var protocolName in ProtocolConformanceHelper.GetConformanceProtocolNames(
                    _classDecl.Conformances, _moduleDecl.Name, _typeNameWithGenerics, _typeDatabase))
                {
                    _emissionCtx.RecordConformance(_typeNameWithGenerics, protocolName);
                }
            }
        }

        /// <summary>
        /// Writes the MarshalToSwift method for the class.
        /// </summary>
        private void WriteMarshalToSwift()
        {
            if (_isObjCRooted)
            {
                // ObjC-rooted: use Handle directly (the NSObject pointer IS the Swift object pointer).
                // No DangerousAddRef/Release — NSObject manages lifecycle via ARC.
                var text = $$"""
                [EditorBrowsable(EditorBrowsableState.Never)]
                unsafe int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
                {
                    var metadata = SwiftObjectHelper<{{_typeNameWithGenerics}}>.GetTypeMetadata();
                    fixed (void* swiftDest = swiftDestSpan)
                    {
                        IntPtr selfPtr = Handle;
                        metadata.ValueWitnessTable->InitializeWithCopy(swiftDest, &selfPtr, metadata);
                        return (int)metadata.Size;
                    }
                }
                """;
                _writer.WriteLines(text);
                _writer.WriteLine();
            }
            else
            {
                // SwiftClassHandle: DangerousGetHandle() IS the Swift object pointer (no buffer).
                // VWT->InitializeWithCopy for classes expects a pointer TO the class pointer,
                // so we take the address of a local copy.
                // DangerousAddRef/Release prevents concurrent finalizer from releasing the handle.
                var text = $$"""
                [EditorBrowsable(EditorBrowsableState.Never)]
                unsafe int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
                {
                    var metadata = SwiftObjectHelper<{{_typeNameWithGenerics}}>.GetTypeMetadata();
                    if ((int)metadata.Size > swiftDestSpan.Length)
                    {
                        throw new ArgumentException($"Span size does not match type size, Expected: {(int)metadata.Size}, Actual: {swiftDestSpan.Length}");
                    }
                    fixed (void* swiftDest = swiftDestSpan)
                    {
                        bool success = false;
                        _handle.DangerousAddRef(ref success);
                        try
                        {
                            IntPtr selfPtr = _handle.DangerousGetHandle();
                            metadata.ValueWitnessTable->InitializeWithCopy(swiftDest, &selfPtr, metadata);
                            return (int)metadata.Size;
                        }
                        finally
                        {
                            if (success)
                                _handle.DangerousRelease();
                        }
                    }
                }
                """;
                _writer.WriteLines(text);
                _writer.WriteLine();
            }
        }

        /// <summary>
        /// Writes the GetProtocolConformanceDescriptor method for the class.
        /// </summary>
        private void WriteGetProtocolConformanceDescriptor()
        {
            WriteStaticConstructor();
            var libPath = _typeDatabase.GetLibraryPath(_moduleDecl.Name);
            var text = $$"""
            [EditorBrowsable(EditorBrowsableState.Never)]
            static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
                where TProtocol : class
            {
                if (!_protocolConformanceSymbols.TryGetValue(typeof(TProtocol), out var symbolName))
                {
                    // Closed-constrained existentials project to typed C# interfaces (e.g. ILabelledContainer<SwiftString>),
                    // but for a single-PAT conforming type the conformance dictionary is keyed on typeof(object) — so the
                    // typed lookup misses. Fall back to the object key for any generic-protocol lookup; if no object entry
                    // exists, the fallback is a no-op and the throw path runs.
                    if (!(typeof(TProtocol).IsGenericType && _protocolConformanceSymbols.TryGetValue(typeof(object), out symbolName)))
                    {
                        throw new SwiftRuntimeException($"Attempted to retrieve protocol conformance descriptor for type {{_classDecl.Name}} and protocol {typeof(TProtocol).Name}, but no conformance was found.");
                    }
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
            [EditorBrowsable(EditorBrowsableState.Never)]
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

        private void WriteBoxAsExistential1(bool emit)
        {
            if (!emit)
                return;

            var text = $$"""
            [EditorBrowsable(EditorBrowsableState.Never)]
            ExistentialContainer1 Swift.Runtime.IExistentialBoxable.BoxAsExistential1<TProtocol>()
                => ExistentialContainerFactory.Create<{{_typeNameWithGenerics}}, TProtocol>(this);
            """;

            _writer.WriteLines(text);
            _writer.WriteLine();
        }

        private string GenerateGetProtocolConformanceDictionaryEntries()
        {
            return ProtocolConformanceHelper.GenerateProtocolConformanceDictionaryEntries(
                CollectAllConformancesWithResolvedSymbols(),
                _moduleDecl.Name,
                _typeNameWithGenerics,
                _typeDatabase);
        }

        /// <summary>
        /// Collects all conformances for this class, including inherited conformances from ancestors.
        /// Only walks ancestors if _isDerived is true (which uses the strict IsEffectivelyDerived predicate).
        /// Own conformances are yielded first; empty symbols are resolved from ancestors via 'with' expression.
        /// Deduplicates by Protocol.ModuleQualifiedName to avoid duplicate dictionary entries.
        /// </summary>
        private IEnumerable<TypeConformance> CollectAllConformancesWithResolvedSymbols()
        {
            var seen = new HashSet<string>();

            // Yield own conformances first, resolving empty symbols from ancestors
            foreach (var conformance in _classDecl.Conformances)
            {
                seen.Add(conformance.Protocol.ModuleQualifiedName);

                if (string.IsNullOrEmpty(conformance.ProtocolConformanceDescriptor) && _isDerived)
                {
                    // Try to find a non-empty symbol from an ancestor
                    var ancestorSymbol = FindConformanceSymbolInAncestors(conformance.Protocol);
                    if (ancestorSymbol != null)
                    {
                        yield return conformance with { ProtocolConformanceDescriptor = ancestorSymbol };
                        continue;
                    }
                }

                yield return conformance;
            }

            // Yield ancestor conformances not already present on this class
            if (!_isDerived)
                yield break;

            var current = _classDecl;
            while (current.HasResolvedSuperclass)
            {
                var ancestor = current.ResolvedSuperclass!;
                if (GenericTypeEmitter.TryGetUnsupportedConstraint(ancestor, out _))
                    break; // Stop at non-emittable ancestor

                foreach (var conformance in ancestor.Conformances)
                {
                    if (seen.Add(conformance.Protocol.ModuleQualifiedName))
                        yield return conformance;
                }

                current = ancestor;
            }
        }

        /// <summary>
        /// Walks the ResolvedSuperclass chain looking for a non-empty conformance symbol
        /// for the given protocol. Returns null if no ancestor has it.
        /// </summary>
        private string? FindConformanceSymbolInAncestors(SwiftTypeName protocol)
        {
            var current = _classDecl;
            while (current.HasResolvedSuperclass)
            {
                var ancestor = current.ResolvedSuperclass!;
                if (GenericTypeEmitter.TryGetUnsupportedConstraint(ancestor, out _))
                    break;

                var match = ancestor.Conformances.FirstOrDefault(c =>
                    c.Protocol.ModuleQualifiedName == protocol.ModuleQualifiedName
                    && !string.IsNullOrEmpty(c.ProtocolConformanceDescriptor));
                if (match != null)
                    return match.ProtocolConformanceDescriptor;

                current = ancestor;
            }
            return null;
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
        private readonly bool _implementsHashable;
        private readonly bool _hasExplicitEqualityOperator;
        private readonly bool _hasExplicitInequalityOperator;
        private readonly SwiftWriter? _swiftWriter;
        private readonly ModuleEmissionContext? _emissionContext;
        private readonly string? _wrapperLibraryName;

        public ClassEqualityMethodsWriter(CSharpWriter csWriter, ClassDecl classDecl, string typeNameWithGenerics, bool hasExplicitEqualityOperator, bool hasExplicitInequalityOperator)
            : this(csWriter, classDecl, typeNameWithGenerics, hasExplicitEqualityOperator, hasExplicitInequalityOperator, typeDatabase: null)
        {
        }

        public ClassEqualityMethodsWriter(CSharpWriter csWriter, ClassDecl classDecl, string typeNameWithGenerics, bool hasExplicitEqualityOperator, bool hasExplicitInequalityOperator, ITypeDatabase? typeDatabase)
        {
            _writer = csWriter;
            _classDecl = classDecl;
            _typeNameWithGenerics = typeNameWithGenerics;

            // Filter Equatable / Hashable conformances through the conditional-witness gate so
            // generic class types whose Swift conformance is conditional drop their typed
            // equality / hash surface. See EquatableConformanceHelper for the rule and the
            // matching struct-side code path in TypeHandlerHelpers.EqualityMethodsWriter.
            bool equatableUnconditional = EquatableConformanceHelper.IsConformanceUnconditionalForCSharp(
                _classDecl, typeDatabase, EquatableConformanceHelper.SwiftEquatableModuleQualifiedName);
            bool hashableUnconditional = EquatableConformanceHelper.IsConformanceUnconditionalForCSharp(
                _classDecl, typeDatabase, EquatableConformanceHelper.SwiftHashableModuleQualifiedName);

            _implementsEquatable = equatableUnconditional
                && _classDecl.Conformances.Any(c => c.Protocol.Name == "Equatable");
            // OptionSet, RawRepresentable, and SetAlgebra imply Hashable in Swift.
            // The ABI JSON may not list Hashable explicitly for types that get it transitively.
            bool directlyDeclaredHashable = _classDecl.Conformances.Any(c =>
                c.Protocol.ModuleQualifiedName == "Swift.Hashable" ||
                (c.Protocol.Name == "Hashable" && string.IsNullOrEmpty(c.Protocol.Module)) ||
                c.Protocol.Name == "OptionSet" ||
                c.Protocol.Name == "RawRepresentable");
            // Classes never get synthesized Hashable from Equatable in Swift (synthesis only
            // applies to structs and enums). For a class with value-based `==` but no
            // Hashable witness, the runtime's identity-hash fallback would return different
            // hashes for value-equal instances, breaking the Equals/GetHashCode contract.
            // Require explicit Hashable conformance for classes to opt in to SwiftHashable.
            _implementsHashable = hashableUnconditional && directlyDeclaredHashable;
            _hasExplicitEqualityOperator = hasExplicitEqualityOperator;
            _hasExplicitInequalityOperator = hasExplicitInequalityOperator;
        }

        /// <summary>
        /// Constructor with Swift wrapper support. When swiftWriter and emissionContext are provided,
        /// emits @_cdecl equality wrappers instead of using SwiftEquatable.Equals (which uses
        /// CallConvSwift and crashes on NativeAOT).
        /// </summary>
        public ClassEqualityMethodsWriter(CSharpWriter csWriter, ClassDecl classDecl, string typeNameWithGenerics, bool hasExplicitEqualityOperator, bool hasExplicitInequalityOperator, SwiftWriter? swiftWriter, ModuleEmissionContext? emissionContext, string? wrapperLibraryName, ITypeDatabase? typeDatabase = null)
            : this(csWriter, classDecl, typeNameWithGenerics, hasExplicitEqualityOperator, hasExplicitInequalityOperator, typeDatabase)
        {
            _swiftWriter = swiftWriter;
            _emissionContext = emissionContext;
            _wrapperLibraryName = wrapperLibraryName;
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

        /// <summary>
        /// Gets the @_cdecl symbol name for a class equality wrapper.
        /// </summary>
        private static string GetEqualitySymbolName(ClassDecl classDecl)
        {
            var moduleName = classDecl.ModuleDecl?.Name ?? "Unknown";
            var safeTypeName = classDecl.Name.Replace(".", "_");
            var hash = EmitterUtility.DeterministicHash8(classDecl.MangledName ?? classDecl.Name);
            return $"SBW_{moduleName}_{safeTypeName}_eq_{hash}";
        }

        /// <summary>
        /// Emits a @_cdecl Swift wrapper for class equality comparison and returns the symbol name.
        /// Uses Unmanaged&lt;AnyObject&gt;.fromOpaque to safely cast opaque pointers back to class instances.
        /// Returns null if wrapper emission is not available or not needed.
        /// </summary>
        private string? TryEmitSwiftEqualityWrapper()
        {
            if (_swiftWriter == null || _emissionContext == null || _wrapperLibraryName == null)
                return null;

            // Skip for generic types (wrapper can't be instantiated)
            if (_classDecl.GenericParameters.Count > 0)
                return null;

            // Skip for module-internal classes — the wrapper library can't name them.
            // Same guard as metadata wrapper emission (WriteGetTypeMetadata).
            if (_classDecl.IsModuleInternal)
                return null;

            var symbolName = GetEqualitySymbolName(_classDecl);

            // S5 audited (Tier B): equality helpers live in the shared `_equality` bucket (also written by EnumHandler and TypeHandlerHelpers). One helper per class type; symbol name from GetEqualitySymbolName is unique per type, so cross-emitter collisions in the bucket are impossible by construction.
            if (!_emissionContext.TryAddEqualityWrapperSymbol(symbolName))
                return symbolName; // Already emitted, return for C# P/Invoke

            var swiftTypeName = _classDecl.SwiftTypeName.ToString();

            // Classes use Unmanaged<AnyObject>.fromOpaque to safely convert opaque pointers
            // back to class instances (NOT assumingMemoryBound which is for structs).
            // Add @MainActor when the type's == operator is actor-isolated (Swift 6 strict concurrency).
            bool needsMainActor = WrapperValidation.NeedsMainActorAnnotation(_classDecl, false);
            // Carry availability from the `==` operator (so retroactive Equatable conformances
            // like RealityFoundation.TextureResource — class is iOS 13+, Equatable is iOS 18+ —
            // get the operator's @available floor, not just the class's), merged with the class's
            // ancestors so nested availability still flows through.
            var equalityOperator = _classDecl.Operators
                .FirstOrDefault(op => op.OperatorSymbol == "==" && op.Kind == OperatorKind.Binary);
            var availability = WrapperEmitterHelpers.MergeAvailabilityFromAncestors(
                equalityOperator?.AvailabilityAnnotations, _classDecl);
            _swiftWriter.WriteLine();
            WrapperEmitterHelpers.EmitCdeclAnnotation(_swiftWriter, symbolName, needsMainActor, availability);
            _swiftWriter.WriteLines($$"""
            public func {{symbolName}}(_ lhs: UnsafeRawPointer, _ rhs: UnsafeRawPointer) -> UInt8 {
                let l = Unmanaged<AnyObject>.fromOpaque(lhs).takeUnretainedValue() as! {{swiftTypeName}}
                let r = Unmanaged<AnyObject>.fromOpaque(rhs).takeUnretainedValue() as! {{swiftTypeName}}
                return (l == r) ? 1 : 0
            }
            """);

            return symbolName;
        }

        /// <summary>
        /// Emits the C# P/Invoke declaration for the class equality wrapper.
        /// </summary>
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

        private void WriteSwiftEquatableImplementationWithSwiftEquals()
        {
            // Try to emit @_cdecl equality wrapper (avoids CallConvSwift which crashes on NativeAOT).
            // Only works for non-generic types with wrapper library support.
            var eqSymbol = TryEmitSwiftEqualityWrapper();
            if (eqSymbol != null)
            {
                EmitEqualityPInvoke(eqSymbol);
            }

            // Equality comparison expression — use @_cdecl P/Invoke if available.
            // Classes: extract pointer via GetSwiftHandle() (emitted on root and ObjC boundary classes).
            string equalsExpr(string lhs, string rhs)
            {
                if (eqSymbol == null) return $"Swift.Runtime.SwiftEquatable.Equals({lhs}, {rhs})";
                return $"PInvoke_eq({lhs}.GetSwiftHandle(), {rhs}.GetSwiftHandle())";
            }

            // Always write Equals and GetHashCode methods
            // Use typeNameWithGenerics for is-check
            var hashCodeBody = _implementsHashable
                ? "return Swift.Runtime.SwiftHashable.GetHashCode(this);"
                : "return 0;";
            var equalsMethods = $$"""
            public override bool Equals(object? obj)
            {
                return obj is {{_typeNameWithGenerics}} other && {{equalsExpr("this", "other")}};
            }

            public override int GetHashCode()
            {
                {{hashCodeBody}}
            }
            """;

            _writer.WriteLines(equalsMethods);
            _writer.WriteLine();

            // Only write operator == if no explicit operator is defined
            // Use typeNameWithGenerics for operator parameters to fix CS0563/CS0305
            if (!_hasExplicitEqualityOperator)
            {
                var equalityOperator = $$"""
                public static bool operator ==({{_typeNameWithGenerics}}? left, {{_typeNameWithGenerics}}? right)
                {
                    if (left is null) return right is null;
                    if (right is null) return false;
                    return {{equalsExpr("left", "right")}};
                }
                """;
                _writer.WriteLines(equalityOperator);
                _writer.WriteLine();
            }

            // Only write operator != if no explicit operator is defined
            if (!_hasExplicitInequalityOperator)
            {
                var inequalityOperator = $$"""
                public static bool operator !=({{_typeNameWithGenerics}}? left, {{_typeNameWithGenerics}}? right)
                {
                    if (left is null) return right is not null;
                    if (right is null) return true;
                    return !{{equalsExpr("left", "right")}};
                }
                """;
                _writer.WriteLines(inequalityOperator);
                _writer.WriteLine();
            }

            // Write the IEquatable<T>.Equals method - use typeNameWithGenerics
            var equatableEquals = $$"""
            public bool Equals({{_typeNameWithGenerics}}? other)
            {
                if (other is null) return false;
                return {{equalsExpr("this", "other")}};
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
