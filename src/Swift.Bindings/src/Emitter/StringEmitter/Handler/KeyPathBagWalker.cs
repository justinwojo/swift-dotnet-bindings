// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

namespace BindingsGeneration;

/// <summary>
/// Shared helpers for walking a PAT conformer's
/// associated-type "bag" type, deciding which properties are admissible as
/// KeyPath leaves, and projecting their Value type to a public C# spelling.
///
/// <para>
/// Two consumers share this surface:
/// </para>
/// <list type="bullet">
///   <item><description><see cref="KeyPathSingletonEmitter"/> — emits one Lazy
///   keyed singleton per (bag property × conformer) for the
///   typed-singleton trampoline shape.</description></item>
///   <item><description><see cref="KeyPathBagValueSpecializationEmitter"/> —
///   emits one <c>Sort</c> overload per (conformer ×
///   distinct C# overload key) for unconstrained-V keypath-sort methods.
///   Route C iterates <see cref="BagWalkResult.ProjectableProps"/> directly so
///   distinct Swift V variants that collapse to the same C# overload key (e.g.
///   <c>Swift.String</c> + <c>Swift.Optional&lt;Swift.String&gt;</c> both project
///   to C# <c>string</c>) both contribute their concrete Swift V to the
///   trampoline's <c>as?</c> chain.</description></item>
/// </list>
///
/// <para>
/// The walker is the single source of truth for "is this bag emittable?" and
/// "is this property a real KeyPath leaf?" — the design review insisted
/// the walker not be duplicated.
/// </para>
/// </summary>
internal static class KeyPathBagWalker
{
    /// <summary>
    /// A bag property that survived the per-property admission gate and
    /// projects to a real C# Value type. Carries enough state for both
    /// consumers without forcing them to re-project.
    /// </summary>
    internal readonly record struct ProjectedBagProperty(
        PropertyDecl Property,
        ITypeProjection Projection,
        bool IsWritable);

    /// <summary>
    /// Result of <see cref="TryResolveProjectableBagProps"/>: the resolved bag
    /// decl plus the subset of its properties that pass admission AND project
    /// to a real C# Value type. Empty <c>ProjectableProps</c> means the bag is
    /// known but has zero useful properties for this conformer.
    /// </summary>
    internal readonly record struct BagWalkResult(
        TypeDecl BagDecl,
        IReadOnlyList<ProjectedBagProperty> ProjectableProps);

    /// <summary>
    /// Resolves the bag decl for (conformer × associated-type name) and projects
    /// every admissible property to a public C# type. Returns null when the bag
    /// cannot be resolved, fails <see cref="IsEmittableBag"/>, or has zero
    /// projectable properties (since both consumers skip the empty case anyway).
    /// </summary>
    public static BagWalkResult? TryResolveProjectableBagProps(
        ConcreteSpecializationEngine.ConcreteConformer conformer,
        TypeDecl conformerDecl,
        string assocName,
        TypeDecl parentTypeDecl,
        ITypeDatabase typeDatabase,
        IReadOnlyDictionary<string, TypeDecl> typeDeclByName,
        ILogger logger)
    {
        var bagDecl = FindBagDecl(conformerDecl, assocName, conformer, typeDeclByName);
        if (bagDecl is null) return null;
        if (!IsEmittableBag(bagDecl)) return null;

        bool allowAbstract = bagDecl is ProtocolDecl;
        var projector = new TypeProjectionFactory();
        var props = new List<ProjectedBagProperty>();
        foreach (var prop in bagDecl.Properties)
        {
            if (!IsEmittableProperty(prop, allowAbstract)) continue;
            // KeyPath value-slot projection: mirrors the parameters the
            // singleton emitter uses, so the singleton and Route C agree on which
            // properties project successfully.
            var projection = projector.Project(prop.SwiftTypeSpec, new ProjectionContext
            {
                TypeDatabase = typeDatabase,
                IsParameter = false,
                CurrentModuleName = parentTypeDecl.SwiftTypeName?.Module,
            });
            if (projection is null)
            {
                logger.LogDebug(
                    "KeyPathBagWalker: property {Prop} of {Bag} unprojectable {Type} — skipping.",
                    prop.Name, bagDecl.SwiftTypeName?.ModuleQualifiedName, prop.SwiftTypeSpec);
                continue;
            }

            bool isWritable = prop.Accessors.OfType<SetAccessorDecl>().Any();
            props.Add(new ProjectedBagProperty(prop, projection, isWritable));
        }

        if (props.Count == 0) return null;
        return new BagWalkResult(bagDecl, props);
    }

    /// <summary>
    /// Find a property-carrying type matching <paramref name="assocName"/>.
    /// Two shapes are supported:
    /// <list type="number">
    ///   <item><description><b>Nested concrete bag.</b> The associated type resolves to
    ///   a nested struct/class with stored properties.</description></item>
    ///   <item><description><b>Module-scope protocol bag.</b> The associated type
    ///   resolves to a top-level protocol with abstract property requirements that
    ///   <c>\Protocol.requirement</c> KeyPath literals resolve through witness-table
    ///   dispatch at use time.</description></item>
    /// </list>
    /// Preference order: (1) hint dictionary against the conformer's NESTED types;
    /// (2) the conformer's NESTED types by short name (Swift's implicit inference);
    /// (3) hint dictionary against MODULE-SCOPE types (covers typealias-to-protocol);
    /// (4) module-scope <see cref="ProtocolDecl"/> with matching short name (unhinted
    /// fallback for module-scope protocol bags).
    /// </summary>
    public static TypeDecl? FindBagDecl(
        TypeDecl conformerDecl,
        string assocName,
        ConcreteSpecializationEngine.ConcreteConformer conformer,
        IReadOnlyDictionary<string, TypeDecl> typeDeclByName)
    {
        if (conformer.AssociatedTypes is { } map && map.TryGetValue(assocName, out var target))
        {
            foreach (var nested in conformerDecl.Types)
            {
                if (nested.SwiftTypeName?.ModuleQualifiedName == target) return nested;
            }
            if (typeDeclByName.TryGetValue(target, out var moduleScopeBag))
                return moduleScopeBag;
        }
        foreach (var nested in conformerDecl.Types)
        {
            if (nested.Name == assocName) return nested;
        }
        var conformerModule = conformerDecl.SwiftTypeName?.Module;
        if (!string.IsNullOrEmpty(conformerModule))
        {
            foreach (var (_, candidate) in typeDeclByName)
            {
                if (candidate is not ProtocolDecl) continue;
                if (candidate.ParentDecl is TypeDecl) continue;
                if (candidate.Name != assocName) continue;
                if (candidate.SwiftTypeName?.Module != conformerModule) continue;
                return candidate;
            }
        }
        return null;
    }

    /// <summary>
    /// Bag eligibility: must be a property carrier we can drive from a synchronous
    /// trampoline. Rejection rules: generic / SPI /
    /// internal / custom-actor / class-bound protocol / Self-requirement protocol).
    /// </summary>
    public static bool IsEmittableBag(TypeDecl bagDecl)
    {
        if (bagDecl.IsGeneric) return false;
        if (bagDecl.IsSpiProtected) return false;
        if (bagDecl.IsModuleInternal) return false;
        if (bagDecl.IsCustomActor) return false;
        if (bagDecl.IsCustomActorIsolated) return false;
        if (bagDecl is ProtocolDecl pd)
        {
            if (pd.AssociatedTypes.Count > 0) return false;
            if (pd.HasSelfRequirement) return false;
            if (pd.IsClassBound) return false;
            if (!string.IsNullOrEmpty(pd.GenericSignature)) return false;
        }
        bool allowAbstract = bagDecl is ProtocolDecl;
        return bagDecl.Properties.Any(p => IsEmittableProperty(p, allowAbstract));
    }

    /// <summary>
    /// Per-property eligibility diagnostic — returns the reason a property is
    /// rejected, or null when it's emittable. <paramref name="allowAbstract"/> is
    /// true for ProtocolDecl bags (their requirements are abstract by construction;
    /// the <c>\Protocol.requirement</c> KeyPath literal still compiles and resolves
    /// through the witness table at use time).
    ///
    /// <paramref name="allowComputed"/> admits computed (non-stored) properties on a
    /// concrete root. Swift forms valid KeyPaths for computed properties —
    /// <c>\Root.getOnly</c> is a <c>KeyPath</c> and <c>\Root.getSet</c> is a
    /// <c>WritableKeyPath</c> — so a concrete root (e.g. an <c>AppEntity</c> conformer)
    /// rooting singletons directly on itself wants them, unlike the nested-bag
    /// scenario where only stored bag fields are KeyPath leaves.
    /// </summary>
    public static string? WhyPropertyNotEmittable(PropertyDecl propertyDecl, bool allowAbstract, bool allowComputed = false)
    {
        if (!allowAbstract && !allowComputed && !propertyDecl.HasStorage) return "!HasStorage";
        if (propertyDecl.IsStatic) return "IsStatic";
        if (propertyDecl.IsSpiProtected) return "IsSpiProtected";
        // `@objc optional var foo: T` is inferred by Swift as `KeyPath<any P, T?>`
        // but the trampoline annotates from `prop.SwiftTypeSpec` which carries `T`
        // (not `T?`). The annotated literal then fails to compile against the
        // actual `\P.foo` site.
        if (propertyDecl.IsObjCOptional) return "IsObjCOptional";
        // Parser's negative-space IsModuleInternal detection misclassifies protocol
        // property requirements (members of a `public protocol` are implicitly public).
        // The bag-level check already rejected internal protocols, so when we're
        // allowAbstract=true the requirement is implicitly public.
        if (!allowAbstract && propertyDecl.IsModuleInternal) return "IsModuleInternal";
        var getter = propertyDecl.Accessors.OfType<GetAccessorDecl>().FirstOrDefault();
        if (getter is null) return "!Getter";
        // Effectful read-only properties (`var foo: T { get throws }` / `{ get async }`)
        // cannot be referenced by a `\Root.foo` KeyPath literal — Swift rejects key paths
        // to accessors that carry effects. Only computed properties can be effectful
        // (a stored property's synthesized accessor never is), so this gate is dormant for
        // the stored-bag path and only bites once `allowComputed` admits computed leaves.
        if (getter.Method?.Throws == true || getter.Method?.IsAsync == true) return "EffectfulGetter";
        if (string.IsNullOrEmpty(propertyDecl.Name)) return "EmptyName";
        return null;
    }

    /// <summary>
    /// Single source of truth for per-property eligibility. Delegates to
    /// <see cref="WhyPropertyNotEmittable"/> so the bag-level <c>Any()</c> probe and
    /// the per-property projection loop share the exact same predicate.
    /// </summary>
    public static bool IsEmittableProperty(PropertyDecl propertyDecl, bool allowAbstract, bool allowComputed = false) =>
        WhyPropertyNotEmittable(propertyDecl, allowAbstract, allowComputed) is null;

    /// <summary>
    /// One-shot index: <c>SwiftQualifiedName → TypeDecl</c> over module-scope types
    /// and their nested types. Conformer lookup walks this once per generic-parent
    /// emission, amortising the O(N) walk across all (conformer, bag) pairs.
    /// </summary>
    public static Dictionary<string, TypeDecl> BuildTypeDeclIndex(ModuleDecl moduleDecl)
    {
        var index = new Dictionary<string, TypeDecl>(StringComparer.Ordinal);
        foreach (var t in moduleDecl.Types) AddRecursive(t, index);
        return index;

        static void AddRecursive(TypeDecl td, Dictionary<string, TypeDecl> index)
        {
            if (td.SwiftTypeName is { } name)
                index[name.ModuleQualifiedName] = td;
            foreach (var nested in td.Types) AddRecursive(nested, index);
        }
    }

    /// <summary>
    /// Availability floor contributed by every named type appearing in a KeyPath's <c>Value</c>
    /// type. Both KeyPath trampoline emitters name the Value type in the <c>@_cdecl</c> body
    /// (<c>\Root.prop as KeyPath&lt;Root, Value&gt;</c> / <c>Dep&lt;Value&gt;(getter: kp)</c>),
    /// so a Value introduced on a later OS than the conformer / dependency class would leave the
    /// trampoline under-annotated — it fails to type-check against the device SDK and is silently
    /// stripped from the wrapper, leaving the C# P/Invoke with no symbol at runtime. Callers
    /// concatenate this with the conformer / dep / property floors; the Swift <c>@available</c>
    /// and C# <c>[SupportedOSPlatform]</c> emitters both dedup to one entry per platform (max
    /// version), so plain concatenation is correct.
    ///
    /// <para>Walks every named node in the spec — the outermost type, its nested
    /// <see cref="NamedTypeSpec.InnerType"/> chain, generic arguments, the elements / argument /
    /// return specs of any tuple or closure Value, and the member protocols of an existential
    /// (<c>any P &amp; GatedQ</c>) Value — so wrappers like <c>Optional&lt;Gated&gt;</c>,
    /// <c>(Gated, Int)</c>, <c>(Gated) -> Void</c>, or <c>any GatedProtocol</c> still surface the
    /// inner floor. <see cref="TypeRecord.AvailabilityAnnotations"/> is already ancestor-merged at
    /// parse time, so no further ancestor walk is needed here. Stdlib / primitive Value types resolve
    /// to records with no annotations and contribute nothing.</para>
    ///
    /// <para>Known limitation: resolution follows a typealias to the underlying type's record, but the
    /// generated trampoline spells the Value via <c>SwiftTypeSpec.ToString()</c>, which may be the alias
    /// name. If a local typealias declaration carries a tighter floor than the type it aliases, that
    /// alias floor is not captured — a documented gap, narrow enough that the concrete-nominal Values
    /// these emitters actually see are unaffected.</para>
    /// </summary>
    public static IReadOnlyList<AvailabilityAnnotation>? CollectValueTypeAvailability(
        TypeSpec valueSpec, ITypeDatabase typeDatabase)
    {
        var collected = new List<AvailabilityAnnotation>();
        CollectInto(valueSpec, typeDatabase, collected);
        return collected.Count > 0 ? collected : null;

        static void CollectInto(TypeSpec spec, ITypeDatabase db, List<AvailabilityAnnotation> acc)
        {
            switch (spec)
            {
                case NamedTypeSpec nts:
                    if (db.TryGetTypeRecord(nts, out var record)
                        && record.AvailabilityAnnotations is { Count: > 0 } annotations)
                    {
                        acc.AddRange(annotations);
                    }
                    foreach (var generic in nts.GenericParameters)
                        CollectInto(generic, db, acc);
                    if (nts.InnerType is { } inner)
                        CollectInto(inner, db, acc);
                    break;
                case TupleTypeSpec tuple:
                    foreach (var element in tuple.Elements)
                        CollectInto(element, db, acc);
                    break;
                case ClosureTypeSpec closure:
                    CollectInto(closure.Arguments, db, acc);
                    CollectInto(closure.ReturnType, db, acc);
                    break;
                case ProtocolListTypeSpec protocolList:
                    // Existential Value (`any P & GatedQ`): the trampoline names the composition,
                    // so each member protocol's floor counts. Members are NamedTypeSpec.
                    foreach (var proto in protocolList.Protocols.Keys)
                        CollectInto(proto, db, acc);
                    break;
            }
        }
    }
}
