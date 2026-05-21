// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

namespace BindingsGeneration;

/// <summary>
/// Session 6c (Route C) — shared helpers for walking a PAT conformer's
/// associated-type "bag" type, deciding which properties are admissible as
/// KeyPath leaves, and projecting their Value type to a public C# spelling.
///
/// <para>
/// Two consumers share this surface:
/// </para>
/// <list type="bullet">
///   <item><description><see cref="KeyPathSingletonEmitter"/> — emits one Lazy
///   keyed singleton per (bag property × conformer) for Session 4's
///   typed-singleton trampoline shape.</description></item>
///   <item><description><see cref="KeyPathBagValueSpecializationEmitter"/> —
///   Session 6c Route C; emits one <c>Sort</c> overload per (conformer ×
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
/// "is this property a real KeyPath leaf?" — Codex F2 + Grok F2 from the
/// design review insisted Session 4's walker not be duplicated.
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
            // KeyPath value-slot projection: mirrors the parameters Session 4's
            // singleton emitter used, so the singleton and Route C agree on which
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
    /// trampoline. Mirrors the rejection rules Session 4 settled on (generic / SPI /
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
    /// </summary>
    public static string? WhyPropertyNotEmittable(PropertyDecl propertyDecl, bool allowAbstract)
    {
        if (!allowAbstract && !propertyDecl.HasStorage) return "!HasStorage";
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
        if (!propertyDecl.Accessors.OfType<GetAccessorDecl>().Any()) return "!Getter";
        if (string.IsNullOrEmpty(propertyDecl.Name)) return "EmptyName";
        return null;
    }

    /// <summary>
    /// Single source of truth for per-property eligibility. Delegates to
    /// <see cref="WhyPropertyNotEmittable"/> so the bag-level <c>Any()</c> probe and
    /// the per-property projection loop share the exact same predicate.
    /// </summary>
    public static bool IsEmittableProperty(PropertyDecl propertyDecl, bool allowAbstract) =>
        WhyPropertyNotEmittable(propertyDecl, allowAbstract) is null;

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
}
