// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Identity of one recovery unit: the declaration that owns it plus the scope at which it can be
/// withdrawn.
/// </summary>
/// <remarks>
/// <para>
/// The scope is part of the identity because one declaration owns several units at once. A protocol
/// owns a <see cref="RecoveryScope.ForwardProtocolView"/> (the subset C# can call on a Swift-vended
/// conformer), a <see cref="RecoveryScope.ManagedProtocolConformance"/> (the reverse capability), and
/// a <see cref="RecoveryScope.TypeSurface"/> — three independently withdrawable things. A struct owns
/// both a <see cref="RecoveryScope.TypeRepresentation"/> and a <see cref="RecoveryScope.TypeSurface"/>.
/// Keying on the declaration alone would collapse them.
/// </para>
/// <para>
/// Canonical form is <c>{decl-canonical}!{scope-token}</c>. Scope tokens contain no <c>!</c> and the
/// form always ends in one, so parsing splits on the LAST <c>!</c> and hands the prefix to
/// <see cref="DeclId.Parse"/> — a Swift declaration spelled with <c>!</c> (<c>init!</c>, the <c>!</c>
/// operator) cannot confuse the split.
/// </para>
/// </remarks>
public readonly record struct RecoveryUnitId
{
    private const char ScopeSeparator = '!';

    /// <summary>The declaration that owns this unit.</summary>
    public DeclId Decl { get; init; }

    /// <summary>The granularity at which this unit can be withdrawn.</summary>
    public RecoveryScope Scope { get; init; }

    /// <summary>
    /// Builds a unit id from an owning declaration that is already at the right granularity.
    /// </summary>
    /// <remarks>
    /// Three scopes are <em>not</em> at the right granularity when constructed this way, and have
    /// dedicated factories instead: <see cref="RecoveryScope.AccessorGroup"/> (accessor ids must be
    /// normalized to the property, or a getter and setter become two "groups"),
    /// <see cref="RecoveryScope.SharedHelperBundle"/> and <see cref="RecoveryScope.ConformanceEdge"/>
    /// (both are multi-instance per owning declaration, so they need a qualifier or every instance
    /// collapses onto one id). Use <see cref="ForAccessorGroup"/>, <see cref="ForSharedHelper"/> and
    /// <see cref="ForConformanceEdge"/> for those.
    /// </remarks>
    public static RecoveryUnitId Create(DeclId decl, RecoveryScope scope) =>
        new() { Decl = decl, Scope = scope };

    /// <summary>
    /// The unit covering both accessors of one property or subscript. Normalizes an accessor-level
    /// declaration to the property-level one, so the getter id and the setter id name the same unit.
    /// </summary>
    public static RecoveryUnitId ForAccessorGroup(DeclId accessorOrPropertyDecl) =>
        Create(
            accessorOrPropertyDecl.Accessor == AccessorKind.None
                ? accessorOrPropertyDecl
                : accessorOrPropertyDecl with { Accessor = AccessorKind.None },
            RecoveryScope.AccessorGroup);

    /// <summary>
    /// One shared-helper bundle on a module. <paramref name="bundleKey"/> separates the independent
    /// bundles a module owns — UTF-8 slice helpers, the error registry, the EveryProtocol carrier,
    /// closure-context helpers, NativeAOT registration. Without it they are one unit, and withdrawing
    /// the UTF-8 helpers would claim to withdraw the error registry too.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="bundleKey"/> is null or blank.</exception>
    public static RecoveryUnitId ForSharedHelper(DeclId moduleDecl, string bundleKey)
    {
        if (string.IsNullOrWhiteSpace(bundleKey))
            throw new ArgumentException(
                "A shared-helper bundle needs a key; without one every bundle in the module collapses "
                + "onto a single unit.",
                nameof(bundleKey));

        return Create(Qualify(moduleDecl, $"helper={bundleKey}"), RecoveryScope.SharedHelperBundle);
    }

    /// <summary>
    /// One <c>: IFoo</c> relation on a concrete type. The edge is a binary relation, so the unit is
    /// qualified by the protocol as well as the conformer — a type with three conformances owns three
    /// edge units, not one.
    /// </summary>
    public static RecoveryUnitId ForConformanceEdge(DeclId conformerDecl, DeclId protocolDecl) =>
        Create(Qualify(conformerDecl, $"edge={protocolDecl.Canonical}"), RecoveryScope.ConformanceEdge);

    /// <summary>
    /// Appends a qualifier to a declaration's discriminator rather than replacing it — the field is
    /// already load-bearing for other distinctions (an instance and a static property of the same
    /// name are separated by it), so overwriting would re-merge declarations it exists to split.
    /// </summary>
    private static DeclId Qualify(DeclId decl, string qualifier) =>
        decl with
        {
            Discriminator = string.IsNullOrEmpty(decl.Discriminator)
                ? qualifier
                : $"{decl.Discriminator};{qualifier}",
        };

    /// <summary>Coarseness of this unit's scope; see <see cref="RecoveryScopeLattice.Rank"/>.</summary>
    public int Rank => RecoveryScopeLattice.Rank(Scope);

    /// <summary>
    /// Canonical form: <c>{decl-canonical}!{scope-token}</c>. Round-trips through
    /// <see cref="Parse"/> for every constructible id.
    /// </summary>
    public string Canonical => $"{Decl.Canonical}{ScopeSeparator}{RecoveryScopeLattice.ToToken(Scope)}";

    /// <summary>8-character uppercase-hex FNV-1a digest of <see cref="Canonical"/>.</summary>
    public string ShortHash => EmitterUtility.DeterministicHash8(Canonical);

    /// <summary>
    /// Human-readable label for logs and report text — <c>Module.Type.member (scope)</c>. Not the
    /// identity; use <see cref="Canonical"/> for anything a machine reads back.
    /// </summary>
    public string Describe() => $"{Decl.QualifiedPath} ({RecoveryScopeLattice.ToToken(Scope)})";

    /// <summary>Parses a <see cref="Canonical"/> unit id.</summary>
    /// <exception cref="FormatException">The input is not a well-formed canonical unit id.</exception>
    public static RecoveryUnitId Parse(string canonical)
    {
        if (!TryParse(canonical, out var id))
            throw new FormatException($"'{canonical}' is not a well-formed RecoveryUnitId canonical string.");
        return id;
    }

    /// <summary>
    /// Attempts to parse a <see cref="Canonical"/> unit id. Returns false for a missing separator, an
    /// unknown scope token, or a prefix that is not itself a well-formed <see cref="DeclId"/>.
    /// </summary>
    public static bool TryParse(string? canonical, out RecoveryUnitId id)
    {
        id = default;
        if (string.IsNullOrEmpty(canonical))
            return false;

        var split = canonical.LastIndexOf(ScopeSeparator);
        if (split < 0)
            return false;

        if (!RecoveryScopeLattice.TryParseToken(canonical[(split + 1)..], out var scope))
            return false;
        if (!DeclId.TryParse(canonical[..split], out var decl))
            return false;

        id = new RecoveryUnitId { Decl = decl, Scope = scope };
        return true;
    }

    /// <inheritdoc />
    public override string ToString() => Canonical;
}

/// <summary>Fluent construction of a <see cref="RecoveryUnitId"/> from the owning declaration.</summary>
public static class DeclIdRecoveryExtensions
{
    /// <summary>Builds the unit id for <paramref name="scope"/> on this declaration.</summary>
    public static RecoveryUnitId Unit(this DeclId decl, RecoveryScope scope) =>
        RecoveryUnitId.Create(decl, scope);

    /// <summary>
    /// Whether <paramref name="outer"/> strictly encloses <paramref name="inner"/> — i.e. the inner
    /// declaration is nested inside the outer one, in the same module.
    /// </summary>
    /// <remarks>
    /// This is the depth half of the escalation measure. Same-scope escalation (a nested type into
    /// its containing type) is legal only when this holds, and because a nested declaration's
    /// enclosing chain is finite and strictly shortens on every step, such a chain cannot cycle.
    /// Strict: a declaration never encloses itself.
    /// </remarks>
    public static bool Encloses(this DeclId outer, DeclId inner)
    {
        if (!string.Equals(outer.Module, inner.Module, StringComparison.Ordinal))
            return false;
        // A nameless declaration contributes no path segment, so it could only ever match by having
        // its containing path mistaken for its own. Refusing outright keeps the relation strict.
        if (string.IsNullOrEmpty(outer.Name))
            return false;

        // A type's own containing path, as its members and nested types would spell it.
        var outerPath = string.IsNullOrEmpty(outer.DeclPath)
            ? outer.Name
            : $"{outer.DeclPath}.{outer.Name}";

        // DeclId is a record struct, so a default or partially initialized value carries a null path
        // despite the non-nullable annotation. Treating that as "no containing path" keeps a stray
        // default from turning an enclosure question into a NullReferenceException.
        var innerPath = inner.DeclPath ?? string.Empty;
        return innerPath.Length > outerPath.Length
            ? innerPath.StartsWith(outerPath + ".", StringComparison.Ordinal)
            : string.Equals(innerPath, outerPath, StringComparison.Ordinal);
    }
}
