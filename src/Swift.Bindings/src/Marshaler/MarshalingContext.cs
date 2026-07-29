// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Constructed-once-per-module holder of the fully-configured marshalling handler instances
/// and the per-module configuration they all share.
///
/// <para>
/// Historically every <see cref="MethodEnvironment"/> / <see cref="PropertyEnvironment"/> news up
/// its own handler quintet, mostly unconfigured (no <c>SpecializationEngine</c>, a per-decl
/// <c>CurrentModuleName</c>), and the engine is published later through the environment's
/// <c>EmissionContext</c> setter. That created a "configured vs. bare" fork: the projection path
/// built engine-aware handlers while the env path did not — the structural root of Defect E
/// (Finding 21). This context removes that fork by construction: every handler it owns is born
/// with the module's <see cref="CurrentModuleName"/> and <see cref="SpecializationEngine"/> already
/// set, and the marshalling environments delegate their handler properties to these shared
/// instances. One module run emits exactly one module, so a single instance is correct for the
/// whole emission; the handlers carry no per-method mutable state across method boundaries.
/// </para>
///
/// <para>
/// The composition collector is intentionally NOT a constructor argument: it is created at module
/// emit start (after this context is built) and injected onto the shared <see cref="Existential"/>
/// handler through the same single <c>SetCompositionCollector</c> late-injection point the env
/// path already uses, so the collector dictionary stays the one per-module instance and is never
/// swapped mid-module.
/// </para>
/// </summary>
public sealed class MarshalingContext
{
    /// <summary>The per-module type database.</summary>
    public ITypeDatabase TypeDatabase { get; }

    /// <summary>The name of the module being emitted. Drives cross-module existential qualification.</summary>
    public string? CurrentModuleName { get; }

    /// <summary>The module's conformer-discovery engine (PAT existential → ExistentialUnion projection).</summary>
    public ConcreteSpecializationEngine? SpecializationEngine { get; }

    /// <summary>Bound-generic handler, configured with the module's <c>ConformanceGraph</c>.</summary>
    public BoundGenericsHandler BoundGenerics { get; }

    /// <summary>Closure handler.</summary>
    public ClosureHandler Closure { get; }

    /// <summary>Tuple handler.</summary>
    public TupleHandler Tuple { get; }

    /// <summary>Type-conversion handler.</summary>
    public TypeConversionHandler TypeConversion { get; }

    /// <summary>Existential handler, born with the module name + specialization engine set.</summary>
    public ExistentialHandler Existential { get; }

    /// <summary>AsyncStream handler.</summary>
    public AsyncStreamHandler AsyncStream { get; }

    private ModuleEmissionContext? _emissionContext;

    /// <summary>
    /// The per-module emission context, late-injected at module-emit start (the same pattern as the
    /// composition collector). It carries the suppressed-proxy set the closure and existential
    /// projection paths consult to decide CONSUME (drop the <c>static __v =&gt; new {Proxy}(__v)</c>
    /// wrap fallback, member stays) vs PRODUCE (throw <see cref="SuppressedProxyReferenceException"/>
    /// so the member-emit boundary stubs the whole member). Assigning it pushes the same instance onto
    /// the shared <see cref="Closure"/> handler so the closure emission path shares one oracle with the
    /// string-emitter and projection paths.
    /// </summary>
    public ModuleEmissionContext? EmissionContext
    {
        get => _emissionContext;
        set
        {
            _emissionContext = value;
            Closure.EmissionContext = value;
        }
    }

    /// <summary>
    /// Builds the per-module marshalling context. <paramref name="moduleDecl"/> supplies the module
    /// name (for existential qualification) and the <c>ConformanceGraph</c> the bound-generic handler
    /// needs for associated-type resolution.
    /// </summary>
    public MarshalingContext(ModuleDecl moduleDecl, ITypeDatabase typeDatabase, ConcreteSpecializationEngine? specializationEngine)
    {
        TypeDatabase = typeDatabase;
        CurrentModuleName = moduleDecl.Name;
        SpecializationEngine = specializationEngine;

        // Every handler that can render an existential's public interface or proxy class name into
        // generated code gets the module name, not just Existential below: a cross-module existential
        // reached through a CONTAINER (closure parameter/return, tuple element, bound-generic argument)
        // is qualified by whichever oracle renders it, and an unqualified name there is a dangling
        // reference in the consuming assembly.
        BoundGenerics = new BoundGenericsHandler(typeDatabase, moduleDecl.ConformanceGraph, moduleDecl.Name);
        Closure = new ClosureHandler(typeDatabase, moduleDecl.Name);
        Tuple = new TupleHandler(typeDatabase, moduleDecl.Name);
        TypeConversion = new TypeConversionHandler(typeDatabase);
        // Collector is null at construction; injected at module-emit start via SetCompositionCollector
        // (the single late-injection point preserved from the per-env path).
        Existential = new ExistentialHandler(typeDatabase)
        {
            CurrentModuleName = moduleDecl.Name,
            SpecializationEngine = specializationEngine,
        };
        AsyncStream = new AsyncStreamHandler(typeDatabase);
    }

    /// <summary>
    /// Builds a <see cref="ProjectionContext"/> that inherits this module's <see cref="CurrentModuleName"/>
    /// and <see cref="SpecializationEngine"/>, so the projection path and the env path can never diverge on
    /// module qualification or conformer discovery. The composition collector defaults to <c>null</c> and is
    /// only threaded when the caller is on an emission path that intends composition-interface collection —
    /// overload-key and validation contexts must keep passing <c>null</c> to avoid registering extra
    /// composition interfaces as a projection side effect.
    /// </summary>
    public ProjectionContext NewProjectionContext(
        bool isParameter,
        GenericContext? genericContext = null,
        TypeDecl? parentTypeDecl = null,
        SortedDictionary<string, List<string>>? compositionCollector = null,
        bool isAsync = false,
        bool throws = false,
        string? callbackNamePrefix = null)
    {
        return new ProjectionContext
        {
            TypeDatabase = TypeDatabase,
            IsParameter = isParameter,
            IsAsync = isAsync,
            Throws = throws,
            CallbackNamePrefix = callbackNamePrefix,
            GenericContext = genericContext,
            ParentTypeDecl = parentTypeDecl,
            CurrentModuleName = CurrentModuleName,
            SpecializationEngine = SpecializationEngine,
            CompositionCollector = compositionCollector,
            EmissionContext = _emissionContext,
        };
    }
}
