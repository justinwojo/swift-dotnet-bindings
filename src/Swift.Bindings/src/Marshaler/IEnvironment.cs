// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Swift.Runtime;

namespace BindingsGeneration
{
    /// <summary>
    /// Represents an environment interface. It should contain data required to emit C# code.
    /// </summary>
    public interface IEnvironment
    {
        /// <summary>
        /// Gets the TypeDatabase
        /// </summary>
        public ITypeDatabase TypeDatabase { get; }
    }

    /// <summary>
    /// Represents a module environment.
    /// </summary>
    /// <remarks>
    /// Initializes a new instance of the ModuleEnvironment class.
    /// </remarks>
    /// <param name="moduleDecl">The module declaration.</param>
    /// <param name="typeDatabase">The type database instance.</param>
    public class ModuleEnvironment(ModuleDecl moduleDecl, ITypeDatabase typeDatabase) : IEnvironment
    {
        /// <summary>
        /// Gets the module declaration.
        /// </summary>
        public ModuleDecl ModuleDecl { get; private set; } = moduleDecl;

        /// <summary>
        /// Gets the TypeDatabase
        /// </summary>
        public ITypeDatabase TypeDatabase { get; } = typeDatabase;
    }

    /// <summary>
    /// Represents a type environment.
    /// </summary>
    /// <remarks>
    /// Initializes a new instance of the TypeEnvironment class.
    /// </remarks>
    /// <param name="typeDecl">The type declaration.</param>
    /// <param name="typeDatabase">The type database instance.</param>
    public class TypeEnvironment(TypeDecl typeDecl, ITypeDatabase typeDatabase) : IEnvironment
    {
        /// <summary>
        /// Gets the type declaration.
        /// </summary>
        public TypeDecl TypeDecl { get; private set; } = typeDecl;

        /// <summary>
        /// Gets the TypeDatabase
        /// </summary>
        public ITypeDatabase TypeDatabase { get; } = typeDatabase;

        /// <summary>
        /// Mapping of Swift generic type names to C# generic type names.
        /// </summary>
        public Dictionary<string, GenericParameterCSName> GenericTypeMapping { get; } = NameProvider.GetGenericTypeMappingForType(typeDecl);
    }

    /// <summary>
    /// Represents a method environment.
    /// </summary>
    /// <remarks>
    /// Initializes a new instance of the MethodEnvironment class.
    /// </remarks>
    /// <param name="methodDecl">The method declaration.</param>
    /// <param name="typeDatabase">The type database instance.</param>
    /// <param name="siblingPropertyNames">Optional set of property names in the same type, used for collision detection.</param>
    /// <param name="pinvokeHelperContext">Optional P/Invoke helper context for generic types (to avoid CS7042).</param>
    public class MethodEnvironment(MethodDecl methodDecl, ITypeDatabase typeDatabase, IReadOnlySet<string>? siblingPropertyNames = null, PInvokeHelperContext? pinvokeHelperContext = null, SortedDictionary<string, List<string>>? compositionCollector = null) : IEnvironment
    {
        /// <summary>
        /// Gets the method declaration.
        /// </summary>
        public MethodDecl MethodDecl { get; private set; } = methodDecl;

        /// <summary>
        /// AF13 (Finding 13): the current emission-time symbol for this method — the linker-visible
        /// cdecl/thunk/wrapper symbol that wrapper-strategy promotion selects. This is the
        /// emission-scoped side table that replaces in-place mutation of
        /// <see cref="MethodDecl.MangledName"/> (which stays the immutable parser ABI fact, the
        /// <c>@_silgen_name</c> symbol). Initialized to the decl's silgen symbol; emitters call
        /// <see cref="PromoteSymbol"/> when a wrapper/thunk re-targets the symbol.
        ///
        /// Every emission-time reader of the *promoted* symbol reads this — the P/Invoke
        /// <c>EntryPoint</c> (via <c>PInvokeEmitter.ComputeEntryPoint</c>), the
        /// <c>GetPInvokeName</c> hash that names the C# extern, the <c>@_cdecl</c> symbol emitted
        /// into the wrapper, and the closure invoke-thunk keys. Readers that want the *original*
        /// silgen symbol (dispatch-thunk resolution, native-thunk internals, demangling,
        /// diagnostics) keep reading <see cref="MethodDecl.MangledName"/>.
        /// </summary>
        public string EmissionSymbol { get; private set; } = methodDecl.MangledName;

        /// <summary>
        /// Promotes the emission-time symbol to <paramref name="symbol"/> — the side-table analogue
        /// of the former <c>MethodDecl.MangledName = symbol</c> mutation. Returns the previous value
        /// so a caller that promotes optimistically and then falls back on a different emission path
        /// can restore it (the former save/restore protocol becomes a local value round-trip, with
        /// no shared-decl mutation to leak across methods).
        /// </summary>
        internal string PromoteSymbol(string symbol)
        {
            var previous = EmissionSymbol;
            EmissionSymbol = symbol;
            return previous;
        }

        /// <summary>
        /// Gets the parent declaration.
        /// </summary>
        public BaseDecl ParentDecl { get; } = methodDecl.ParentDecl ?? throw new ArgumentNullException($"Parent declaration on method {methodDecl.Name} is null.");

        /// <summary>
        /// Gets the TypeDatabase
        /// </summary>
        public ITypeDatabase TypeDatabase { get; } = typeDatabase;

        /// <summary>
        /// Mapping of Swift generic type names to C# generic type names.
        /// </summary>
        public Dictionary<string, GenericParameterCSName> GenericTypeMapping { get; } = NameProvider.GetGenericTypeMapping(methodDecl);

        /// <summary>
        /// The per-module marshalling context attached via <see cref="EmissionContext"/>, or null when
        /// this environment was constructed outside the handler pipeline. When non-null, the handler
        /// properties below delegate to its shared, fully-configured instances instead of newing up a
        /// per-decl handler quintet — the Finding-21 collapse that removes the "configured vs. bare" fork.
        /// </summary>
        internal MarshalingContext? Marshaling => _emissionContext?.Marshaling;

        /// <summary>
        /// Bound generic helper instance. Delegates to the per-module <see cref="MarshalingContext"/>
        /// shared handler when one is attached (the normal pipeline); otherwise lazily builds and caches
        /// a local handler configured exactly as before — same type database, same module
        /// <c>ConformanceGraph</c> — so out-of-pipeline rebuilds keep identical behavior.
        /// </summary>
        private BoundGenericsHandler? _localBoundGenericsHandler;
        public BoundGenericsHandler BoundGenericsHandler =>
            Marshaling?.BoundGenerics
            ?? (_localBoundGenericsHandler ??= new BoundGenericsHandler(TypeDatabase,
                (MethodDecl.ModuleDecl as ModuleDecl)?.ConformanceGraph));

        /// <summary>
        /// Closure handler instance. See <see cref="BoundGenericsHandler"/> for the shared/local contract.
        /// </summary>
        private ClosureHandler? _localClosureHandler;
        public ClosureHandler ClosureHandler =>
            Marshaling?.Closure ?? (_localClosureHandler ??= new ClosureHandler(TypeDatabase));

        /// <summary>
        /// Tuple handler instance. See <see cref="BoundGenericsHandler"/> for the shared/local contract.
        /// </summary>
        private TupleHandler? _localTupleHandler;
        public TupleHandler TupleHandler =>
            Marshaling?.Tuple ?? (_localTupleHandler ??= new TupleHandler(TypeDatabase));

        /// <summary>
        /// Type conversion handler instance for automatic .NET type conversions. See
        /// <see cref="BoundGenericsHandler"/> for the shared/local contract.
        /// </summary>
        private TypeConversionHandler? _localTypeConversionHandler;
        public TypeConversionHandler TypeConversionHandler =>
            Marshaling?.TypeConversion ?? (_localTypeConversionHandler ??= new TypeConversionHandler(TypeDatabase));

        /// <summary>
        /// Existential handler instance for handling protocol existential types.
        /// <para>
        /// Returns the per-module <see cref="MarshalingContext"/> shared handler whenever an
        /// <see cref="EmissionContext"/> carrying a <see cref="MarshalingContext"/> is attached: that
        /// handler is born with the module's <c>CurrentModuleName</c> and <c>SpecializationEngine</c> and
        /// is the SINGLE instance the per-module composition collector is injected onto (at module-emit
        /// start in <c>ModuleHandler</c>). The shared path is intentionally NOT cached — it is already a
        /// per-module singleton, and not caching lets the accessor switch from the local fallback to the
        /// shared handler the moment <see cref="EmissionContext"/> is attached. (The collector is injected
        /// at <c>MethodHandler</c> BEFORE <see cref="EmissionContext"/>, so a cached early access would
        /// otherwise pin the local fallback for the whole emission.) When no context is attached
        /// (out-of-pipeline rebuilds), a local handler is lazily built and cached with the same module name
        /// and composition collector the env was constructed with; its engine is wired later by the
        /// <see cref="EmissionContext"/> setter exactly as before.
        /// </para>
        /// </summary>
        private ExistentialHandler? _localExistentialHandler;
        public ExistentialHandler ExistentialHandler =>
            Marshaling?.Existential
            ?? (_localExistentialHandler ??= new ExistentialHandler(TypeDatabase, CompositionCollector)
            {
                CurrentModuleName = (MethodDecl.ModuleDecl as ModuleDecl)?.Name
            });

        /// <summary>
        /// Builds a <see cref="ProjectionContext"/> for a type projection requested from this
        /// environment. Delegates to the per-module <see cref="MarshalingContext"/> when one is
        /// attached, so the projection inherits the module's <c>CurrentModuleName</c> and
        /// <c>SpecializationEngine</c> from the single source the env-path existential oracle uses —
        /// instead of each emitter hand-assembling a context and reaching into
        /// <see cref="ExistentialHandler"/> for the module name (the Finding-21 "forked translator"
        /// shape). Out of pipeline (no context attached) it reproduces exactly what those call sites
        /// did: the local existential handler's module name and no engine.
        /// <para>
        /// Output is byte-identical either way: the projection factory consults the engine only via
        /// <see cref="ExistentialHandler.GetPublicExistentialType"/> with <c>allowUnionProjection: false</c>
        /// (<c>TypeProjectionFactory.ProjectExistential</c>), so threading the engine here is inert today —
        /// it is the forward-looking wiring that lets the projection path become union-capable without a
        /// second engine source, while <c>CurrentModuleName</c> is preserved at the same value the bare
        /// sites read from <see cref="ExistentialHandler"/>.
        /// </para>
        /// </summary>
        internal ProjectionContext NewProjectionContext(
            bool isParameter,
            GenericContext? genericContext = null,
            TypeDecl? parentTypeDecl = null,
            SortedDictionary<string, List<string>>? compositionCollector = null)
        {
            if (Marshaling is { } marshaling)
                return marshaling.NewProjectionContext(isParameter, genericContext, parentTypeDecl, compositionCollector);

            return new ProjectionContext
            {
                TypeDatabase = TypeDatabase,
                IsParameter = isParameter,
                GenericContext = genericContext,
                ParentTypeDecl = parentTypeDecl,
                CurrentModuleName = ExistentialHandler.CurrentModuleName,
                CompositionCollector = compositionCollector,
                // Thread the change-8 suppression gate exactly as the in-pipeline arm
                // (MarshalingContext.NewProjectionContext) does, so a suppressed existential proxy
                // is gated identically whether or not a MarshalingContext happens to be attached.
                EmissionContext = _emissionContext,
            };
        }

        /// <summary>
        /// Gets the set of property names in the same parent type.
        /// Used to detect and resolve method/property name collisions.
        /// </summary>
        public IReadOnlySet<string>? SiblingPropertyNames { get; } = siblingPropertyNames;

        /// <summary>
        /// Collision disambiguation index for methods with projected C# signature collisions.
        /// 0 = no collision (first occurrence). Positive values append a numeric suffix (2, 3, ...)
        /// so that multiple Swift overloads that project to the same C# signature remain accessible.
        /// </summary>
        public int CollisionIndex { get; set; }

        /// <summary>
        /// When non-null, this override method adopted its same-module ancestor slot's emitted C#
        /// name (resolved by full Swift selector — method name + external argument labels + parameter
        /// Swift types) instead of recomputing one from its own class body. A C# <c>override</c> MUST
        /// reuse the EXACT ancestor name; when a base disambiguated two same-name/same-type overloads
        /// that differ only by Swift argument label with a B15 collision suffix (e.g. <c>Process</c> /
        /// <c>Process2</c>), a derived class overriding only the suffixed slot has a single method in
        /// its own body, so its local <see cref="CollisionIndex"/> is 0 and it would otherwise emit the
        /// suffix-free name — binding to the WRONG base slot and silently mis-dispatching. Set once in
        /// the <c>IHandler</c> dedup pass (the single locus that owns <see cref="CollisionIndex"/>) so
        /// every <see cref="CSharpMethodName"/> reader — the public signature, the override modifier,
        /// and the forwarding native-int/closure/default-parameter overloads — stays in lockstep.
        /// </summary>
        public string? AdoptedOverrideCSharpName { get; set; }

        /// <summary>
        /// Gets the C# method name, resolving any collisions with property names
        /// and applying collision disambiguation suffix when needed.
        /// </summary>
        public string CSharpMethodName
        {
            get
            {
                // An override that adopted its ancestor slot's emitted name uses it verbatim — the
                // ancestor name already carries whatever B15 suffix that ancestor's class body assigned.
                if (AdoptedOverrideCSharpName != null)
                    return AdoptedOverrideCSharpName;
                var name = NameProvider.GetPublicMethodName(PublicMethodNameContext.ForMethod(MethodDecl, SiblingPropertyNames));
                return CollisionIndex > 0 ? $"{name}{CollisionIndex + 1}" : name;
            }
        }

        /// <summary>
        /// Returns true if the method returns its declaring type (fluent/builder pattern).
        /// Self-returning methods skip the "Get" prefix (e.g., "equalTo" → "EqualTo", not "GetEqualTo").
        /// Only applies to non-constructor, non-accessor instance methods.
        /// </summary>
        internal bool IsSelfReturning => IsSelfReturningMethod(MethodDecl);

        /// <summary>
        /// Static helper for detecting self-returning methods.
        /// Reused by dedup key builders that don't have a MethodEnvironment.
        /// </summary>
        internal static bool IsSelfReturningMethod(MethodDecl methodDecl)
        {
            // Only instance methods can be "self-returning" (fluent/builder pattern).
            // Static methods returning Self are factories/singletons where Get prefix IS appropriate.
            if (methodDecl.IsConstructor || methodDecl.IsAccessor || methodDecl.IsAsync)
                return false;
            if (methodDecl.MethodType == MethodType.Static)
                return false;
            if (methodDecl.CSSignature.Count == 0)
                return false;

            var returnTypeSpec = methodDecl.CSSignature[0].SwiftTypeSpec;
            if (returnTypeSpec.IsEmptyTuple)
                return false;

            // Check for literal Self returns (protocol extension methods)
            if (returnTypeSpec.IsDynamicSelf)
                return true;

            // Check for concrete type matching the parent type
            if (methodDecl.ParentDecl is TypeDecl parentTypeDecl &&
                returnTypeSpec is NamedTypeSpec named &&
                named.Name == parentTypeDecl.SwiftTypeName.ModuleQualifiedName)
                return true;

            return false;
        }

        /// <summary>
        /// Collision-safe names for the synthetic locals the sync-wrapper emission path hardcodes
        /// (resultPtr, hasValuePtr, swiftIndirectResult, …). Resolved once and shared across
        /// <c>PInvokeSignatureBuilder</c>, <c>MethodMarshalPlanBuilder</c>, and
        /// <c>WrapperEmitter</c> so the synthetic P/Invoke parameter, the allocation snippet local,
        /// and the return-marshalling read all agree. Byte-identical to the bare names unless a user
        /// parameter collides. See <see cref="SyntheticLocalNames"/>.
        /// </summary>
        private SyntheticLocalNames? _syntheticLocals;
        public SyntheticLocalNames SyntheticLocals => _syntheticLocals ??= SyntheticLocalNames.Resolve(MethodDecl);

        /// <summary>
        /// Gets the P/Invoke helper context for collecting P/Invoke declarations in generic types.
        /// When non-null, P/Invoke declarations are collected here instead of emitted inline (to avoid CS7042).
        /// </summary>
        public PInvokeHelperContext? PInvokeHelperContext { get; } = pinvokeHelperContext;

        /// <summary>
        /// Indicates whether the containing type is generic and P/Invoke must be emitted in a helper class.
        /// </summary>
        public bool IsContainingTypeGeneric => PInvokeHelperContext != null;

        /// <summary>
        /// Composition collector for multi-protocol existential interfaces.
        /// Threaded from TypeHandlerContext to ExistentialHandler during emission.
        /// </summary>
        public SortedDictionary<string, List<string>>? CompositionCollector { get; } = compositionCollector;

        /// <summary>
        /// Shared set of projected C# method signatures already emitted, used to deduplicate
        /// default parameter overloads against the main emission pass. (C6/C7)
        /// Set by HandleBaseDecl before method emission; null if not available.
        /// </summary>
        public HashSet<string>? EmittedProjectedSignatures { get; set; }

        /// <summary>
        /// Per-module emission context, threaded by the handler so emitters that need
        /// authoritative wrapper-symbol-registration data (e.g. the wrapper-symbol
        /// contract check in <c>PInvokeEmitHelper</c>) can consult it without each call
        /// site re-plumbing a parameter. Null when the environment is constructed
        /// outside the handler pipeline (some normalization/post-processing paths
        /// rebuild a fresh environment); contract enforcement is opt-in and only
        /// fires when this is non-null.
        /// </summary>
        /// <remarks>
        /// Setting this also publishes the module's <see cref="ConcreteSpecializationEngine"/>
        /// onto this environment's <see cref="ExistentialHandler"/>, so env-path existential
        /// resolution (method/property returns) can reach the conformer oracle. Direction safety
        /// is enforced by the <c>allowUnionProjection</c> flag on
        /// <see cref="ExistentialHandler.GetPublicExistentialType"/> — NOT by engine presence —
        /// so wiring the engine unconditionally never flips a parameter/setter (which has no
        /// ExistentialUnion input marshalling) to an unmarshallable type.
        /// </remarks>
        public ModuleEmissionContext? EmissionContext
        {
            get => _emissionContext;
            set
            {
                _emissionContext = value;
                ExistentialHandler.SpecializationEngine = value?.SpecializationEngine;
            }
        }
        private ModuleEmissionContext? _emissionContext;

        /// <summary>
        /// Whether a non-optional existential RETURN in this environment is eligible to project to the
        /// read-only <c>Swift.Runtime.ExistentialUnion</c> wrapper. ExistentialUnion is return-only
        /// (forward try-cast, no input marshalling), so projection is confined to pure-read return
        /// positions. Subscript accessors (the indexer type is resolved separately by SubscriptHandler and
        /// mixes a read return with input index params) and async returns (the async harness materializes
        /// the result through its own object-typed path) are BOTH deferred this session — see S12 plan.
        /// This is the SINGLE position gate consulted by the signature path
        /// (<c>WrapperSignatureBuilder.HandleReturnType</c>), the return-body wrapping
        /// (<c>WrapperEmitter.Return</c>), and the degradation-marker suppression (<c>MethodHandler</c> /
        /// <c>DefaultParameterOverloadEmitter</c>), so those sites can never disagree on whether a given
        /// return projects to union. The conformer/engine gate is layered on top inside
        /// <see cref="ExistentialHandler.GetPublicExistentialType"/>; this property encodes POSITION
        /// eligibility only.
        /// <para>
        /// <see cref="IsSettablePropertyAccessor"/> is folded in DIRECTLY (not via engine presence): a
        /// settable property keeps its public type at <c>object</c> (ExistentialUnion has no input
        /// marshalling), so its backing getter must too. Relying on "don't wire the engine" is NOT robust
        /// because the <see cref="EmissionContext"/> setter re-publishes the engine onto the accessor env
        /// after the signature is built — leaving the signature <c>object</c> but letting the body project
        /// to union. Gating the predicate itself forces <c>allowUnionProjection: false</c> at every site
        /// regardless of when/whether the engine is (re-)wired.
        /// </para>
        /// </summary>
        public bool AllowsExistentialReturnUnionProjection =>
            !MethodDecl.IsSubscriptAccessor && !MethodDecl.IsAsync && !IsSettablePropertyAccessor;

        /// <summary>
        /// Set by <c>PropertyHandler</c> on a settable property's accessor environment so
        /// <see cref="AllowsExistentialReturnUnionProjection"/> keeps the backing getter at <c>object</c>
        /// in lockstep with the public (settable) property type. Default false: free functions, methods,
        /// and get-only property accessors are unaffected.
        /// </summary>
        public bool IsSettablePropertyAccessor { get; set; }

        /// <summary>
        /// Whether the return of this member ACTUALLY projects to <c>Swift.Runtime.ExistentialUnion</c> —
        /// i.e. the return is a PAT existential with known conformers, in a position eligible per
        /// <see cref="AllowsExistentialReturnUnionProjection"/>, AND the conformer/engine gate inside
        /// <see cref="ExistentialHandler.GetPublicExistentialType"/> resolves it to the union wrapper. This is
        /// the SINGLE "did this return project to union?" decision shared by the signature builder, the
        /// return-body wrapper, the degradation-marker suppression in <c>MethodHandler</c> /
        /// <c>DefaultParameterOverloadEmitter</c>, AND the <c>[return: OriginalSwiftType]</c> suppression in
        /// <c>WrapperEmitter.EmitReturnTypeOriginalSwiftType</c>. Because every site reads the same predicate
        /// against the same environment state, the declared signature type and every degradation marker stay
        /// in lockstep: a union return drops the degradation marker (it did not degrade), while an ineligible
        /// position (parameter, settable getter, async, subscript) keeps its <c>object</c> signature AND its
        /// marker. Returns false (safe — keep the marker, matching an <c>object</c> signature) whenever the
        /// engine is unwired, since the signature builder, gated identically, would also have stayed at
        /// <c>object</c>.
        /// </summary>
        public bool ReturnProjectsToExistentialUnion
        {
            get
            {
                var signatureArgs = MethodDecl.CSSignature;
                var returnSpec = signatureArgs.Count > 0 ? signatureArgs[0].SwiftTypeSpec : null;
                if (returnSpec == null || !ExistentialHandler.IsExistential(returnSpec))
                    return false;

                var returnProtoList = ExistentialHandler.ToProtocolListTypeSpec(returnSpec)!;
                return ExistentialHandler.GetPublicExistentialType(
                    returnProtoList, allowUnionProjection: AllowsExistentialReturnUnionProjection)
                        == "Swift.Runtime.ExistentialUnion";
            }
        }
    }

    /// <summary>
    /// Represents a property environment.
    /// </summary>
    /// <remarks>
    /// Initializes a new instance of the PropertyEnvironment class.
    /// </remarks>
    /// <param name="propertyDecl">The property declaration.</param>
    /// <param name="typeDatabase">The type database instance.</param>
    /// <param name="siblingNestedTypeNames">Optional set of nested type names in the same parent type, used for collision detection.</param>
    public class PropertyEnvironment(PropertyDecl propertyDecl, ITypeDatabase typeDatabase, IReadOnlySet<string>? siblingNestedTypeNames = null, SortedDictionary<string, List<string>>? compositionCollector = null) : IEnvironment
    {
        /// <summary>
        /// Gets the property declaration.
        /// </summary>
        public PropertyDecl PropertyDecl { get; private set; } = propertyDecl;

        /// <summary>
        /// Gets the TypeDatabase
        /// </summary>
        public ITypeDatabase TypeDatabase { get; } = typeDatabase;

        /// <summary>
        /// Gets the sibling nested type names for collision detection.
        /// </summary>
        public IReadOnlySet<string>? SiblingNestedTypeNames { get; } = siblingNestedTypeNames;

        /// <summary>
        /// The per-module marshalling context attached via <see cref="EmissionContext"/>, or null when
        /// this environment was constructed outside the handler pipeline. When non-null, the handler
        /// properties below delegate to its shared, fully-configured instances (the Finding-21 collapse).
        /// </summary>
        internal MarshalingContext? Marshaling => _emissionContext?.Marshaling;

        /// <summary>
        /// Bound generic helper instance. Delegates to the per-module <see cref="MarshalingContext"/>
        /// shared handler when one is attached; otherwise lazily builds and caches a local handler
        /// configured exactly as before (same type database, same module <c>ConformanceGraph</c>).
        /// </summary>
        private BoundGenericsHandler? _localBoundGenericsHandler;
        public BoundGenericsHandler BoundGenericsHandler =>
            Marshaling?.BoundGenerics
            ?? (_localBoundGenericsHandler ??= new BoundGenericsHandler(TypeDatabase,
                (PropertyDecl.ModuleDecl as ModuleDecl)?.ConformanceGraph));

        /// <summary>
        /// Tuple handler instance. See <see cref="BoundGenericsHandler"/> for the shared/local contract.
        /// </summary>
        private TupleHandler? _localTupleHandler;
        public TupleHandler TupleHandler =>
            Marshaling?.Tuple ?? (_localTupleHandler ??= new TupleHandler(TypeDatabase));

        /// <summary>
        /// Type conversion handler instance for automatic .NET type conversions. See
        /// <see cref="BoundGenericsHandler"/> for the shared/local contract.
        /// </summary>
        private TypeConversionHandler? _localTypeConversionHandler;
        public TypeConversionHandler TypeConversionHandler =>
            Marshaling?.TypeConversion ?? (_localTypeConversionHandler ??= new TypeConversionHandler(TypeDatabase));

        /// <summary>
        /// Existential handler instance for handling protocol existential types. Returns the per-module
        /// shared handler (engine + module name + the single injected composition collector) when an
        /// <see cref="EmissionContext"/> is attached; otherwise a cached local handler built with the
        /// env's module name + composition collector, its engine wired by the <see cref="EmissionContext"/>
        /// setter. The shared path is uncached so attaching the context switches to it immediately. See
        /// the <c>MethodEnvironment.ExistentialHandler</c> remarks for the full shared/local rationale.
        /// </summary>
        private ExistentialHandler? _localExistentialHandler;
        public ExistentialHandler ExistentialHandler =>
            Marshaling?.Existential
            ?? (_localExistentialHandler ??= new ExistentialHandler(TypeDatabase, CompositionCollector)
            {
                CurrentModuleName = (PropertyDecl.ModuleDecl as ModuleDecl)?.Name
            });

        /// <summary>
        /// Composition collector for multi-protocol existential interfaces.
        /// </summary>
        public SortedDictionary<string, List<string>>? CompositionCollector { get; } = compositionCollector;

        /// <summary>
        /// Closure handler instance for handling closure (function) types. See
        /// <see cref="BoundGenericsHandler"/> for the shared/local contract.
        /// </summary>
        private ClosureHandler? _localClosureHandler;
        public ClosureHandler ClosureHandler =>
            Marshaling?.Closure ?? (_localClosureHandler ??= new ClosureHandler(TypeDatabase));

        /// <summary>
        /// AsyncStream handler instance for handling Swift AsyncStream types. See
        /// <see cref="BoundGenericsHandler"/> for the shared/local contract.
        /// </summary>
        private AsyncStreamHandler? _localAsyncStreamHandler;
        public AsyncStreamHandler AsyncStreamHandler =>
            Marshaling?.AsyncStream ?? (_localAsyncStreamHandler ??= new AsyncStreamHandler(TypeDatabase));

        /// <summary>
        /// Per-module emission context, threaded by the property handler. Setting it publishes the
        /// module's <see cref="ConcreteSpecializationEngine"/> onto this environment's
        /// <see cref="ExistentialHandler"/> so a get-only existential property getter (a pure-read
        /// position) can project a PAT-with-conformers existential to <c>Swift.Runtime.ExistentialUnion</c>.
        /// Direction safety is the caller's responsibility via the <c>allowUnionProjection</c> flag on
        /// <see cref="ExistentialHandler.GetPublicExistentialType"/>: a settable property keeps its
        /// shared getter/setter type at <c>object</c> because the setter has no ExistentialUnion input
        /// marshalling, so the getter type must pass <c>allowUnionProjection: false</c> there.
        /// </summary>
        public ModuleEmissionContext? EmissionContext
        {
            get => _emissionContext;
            set
            {
                _emissionContext = value;
                ExistentialHandler.SpecializationEngine = value?.SpecializationEngine;
            }
        }
        private ModuleEmissionContext? _emissionContext;
    }
}
