// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Globalization;

namespace BindingsGeneration;

/// <summary>
/// What a generated artifact is, relative to the declaration that owns it.
/// </summary>
/// <remarks>
/// One declaration fans out to several artifacts — a public C# member, the P/Invoke behind it, a
/// Swift <c>@_cdecl</c> wrapper, one callback thunk per closure parameter — and each needs its own
/// identity so a diagnostic can name the piece that actually broke rather than the whole member.
/// The type- and module-scoped roles cover artifacts that fan *in*: a metadata helper or a
/// reverse-dispatch vtable is shared by many members but belongs to one owning scope.
/// </remarks>
public enum ArtifactRole
{
    /// <summary>The public C# surface — the member a consumer calls.</summary>
    CSharpPublic,

    /// <summary>The <c>DllImport</c>/<c>LibraryImport</c> extern the public surface calls through.</summary>
    PInvoke,

    /// <summary>A Swift <c>@_cdecl</c>/<c>@_silgen_name</c> wrapper function.</summary>
    SwiftWrapper,

    /// <summary>A closure/callback thunk; the ordinal distinguishes siblings on one declaration.</summary>
    Callback,

    /// <summary>A type-metadata accessor helper.</summary>
    MetadataHelper,

    /// <summary>A reverse-dispatch vtable (the protocol-conformance direction).</summary>
    ReverseVtable,

    /// <summary>The module initializer.</summary>
    ModuleInitializer,
}

/// <summary>
/// Identity for one generated artifact: the <see cref="DeclId"/> that owns it plus the
/// <see cref="ArtifactRole"/> it plays, and an ordinal for roles that repeat.
/// </summary>
/// <remarks>
/// <para>
/// Canonical form is <c>{decl-canonical}/{role-token}</c>, with <c>#{ordinal}</c> appended when the
/// ordinal is non-zero — e.g. <c>…/callback#2</c>. Role tokens never contain <c>/</c>, so parsing
/// splits on the LAST separator and hands the prefix back to <see cref="DeclId.Parse"/>; the decl
/// canonical form escapes nothing to <c>/</c>, so a Swift type spelled with a slash cannot confuse
/// the split.
/// </para>
/// <para>
/// Shared helpers (one metadata accessor serving many members) take the id of their owning scope —
/// the type or the module — rather than of any one consuming member. Which scope owns which shared
/// artifact is a policy question this type deliberately doesn't answer; it only has to be able to
/// express the answer.
/// </para>
/// </remarks>
public readonly record struct ArtifactId
{
    private const char RoleSeparator = '/';
    private const char OrdinalSeparator = '#';

    /// <summary>The declaration this artifact was generated for.</summary>
    public DeclId Decl { get; init; }

    /// <summary>Which artifact of that declaration this is.</summary>
    public ArtifactRole Role { get; init; }

    /// <summary>
    /// Distinguishes repeated artifacts in the same role on one declaration (callback thunk 0 vs
    /// 1). Zero for roles that occur once.
    /// </summary>
    public int Ordinal { get; init; }

    /// <summary>
    /// True for roles that can occur more than once on a single declaration, and whose canonical
    /// form therefore always carries an explicit ordinal.
    /// </summary>
    /// <remarks>
    /// Callback thunks are numbered from zero, so "omit the ordinal when it is zero" would make
    /// <c>callback</c> and callback #0 the same string — two readings of one id. Repeating roles
    /// always serialize their ordinal; single-occurrence roles never do, and reject a non-zero one
    /// outright rather than silently accepting an ordinal that cannot round-trip.
    /// </remarks>
    public static bool RoleRepeats(ArtifactRole role) => role == ArtifactRole.Callback;

    /// <summary>Builds an artifact id.</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="ordinal"/> is negative, or is non-zero for a role that occurs at most once.
    /// </exception>
    public static ArtifactId Create(DeclId decl, ArtifactRole role, int ordinal = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ordinal);
        if (ordinal != 0 && !RoleRepeats(role))
            throw new ArgumentOutOfRangeException(
                nameof(ordinal), ordinal, $"Role '{role}' occurs at most once per declaration, so its ordinal must be 0.");
        return new ArtifactId { Decl = decl, Role = role, Ordinal = ordinal };
    }

    /// <summary>
    /// Canonical form: <c>{decl-canonical}/{role-token}</c>, with <c>#{ordinal}</c> always appended
    /// for a repeating role (see <see cref="RoleRepeats"/>) and never for any other.
    /// Round-trips through <see cref="Parse"/> for every constructible id.
    /// </summary>
    public string Canonical =>
        RoleRepeats(Role)
            ? $"{Decl.Canonical}{RoleSeparator}{ToToken(Role)}{OrdinalSeparator}{Ordinal.ToString(CultureInfo.InvariantCulture)}"
            : $"{Decl.Canonical}{RoleSeparator}{ToToken(Role)}";

    /// <summary>
    /// 8-character uppercase-hex FNV-1a digest of <see cref="Canonical"/>, for compact tokens.
    /// </summary>
    public string ShortHash => EmitterUtility.DeterministicHash8(Canonical);

    /// <summary>Parses a <see cref="Canonical"/> artifact id.</summary>
    /// <exception cref="FormatException">The input is not a well-formed canonical artifact id.</exception>
    public static ArtifactId Parse(string canonical)
    {
        if (!TryParse(canonical, out var id))
            throw new FormatException($"'{canonical}' is not a well-formed ArtifactId canonical string.");
        return id;
    }

    /// <summary>
    /// Attempts to parse a <see cref="Canonical"/> artifact id. Returns false for a missing role
    /// separator, an unknown role token, a malformed ordinal, or a decl prefix that isn't itself a
    /// well-formed <see cref="DeclId"/>.
    /// </summary>
    public static bool TryParse(string? canonical, out ArtifactId id)
    {
        id = default;
        if (string.IsNullOrEmpty(canonical))
            return false;

        // Role tokens contain no '/', and DeclId.Canonical never introduces one as a delimiter,
        // so the last separator is unambiguously the decl/role boundary.
        var split = canonical.LastIndexOf(RoleSeparator);
        if (split < 0)
            return false;

        var rolePart = canonical.Substring(split + 1);
        var ordinal = 0;
        var hash = rolePart.IndexOf(OrdinalSeparator);
        if (hash >= 0)
        {
            if (!int.TryParse(
                    rolePart.AsSpan(hash + 1),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out ordinal))
                return false;
            rolePart = rolePart.Substring(0, hash);
        }

        if (!TryParseToken(rolePart, out var role))
            return false;
        // Enforce the same rule the writer follows, so a hand-edited or foreign string cannot
        // produce an id whose Canonical differs from the text it was parsed from.
        if (RoleRepeats(role) != (hash >= 0))
            return false;
        if (!DeclId.TryParse(canonical.Substring(0, split), out var decl))
            return false;

        id = new ArtifactId { Decl = decl, Role = role, Ordinal = ordinal };
        return true;
    }

    /// <inheritdoc />
    public override string ToString() => Canonical;

    /// <summary>Stable kebab-case wire token for a role.</summary>
    private static string ToToken(ArtifactRole role) => role switch
    {
        ArtifactRole.CSharpPublic => "csharp-public",
        ArtifactRole.PInvoke => "pinvoke",
        ArtifactRole.SwiftWrapper => "swift-wrapper",
        ArtifactRole.Callback => "callback",
        ArtifactRole.MetadataHelper => "metadata-helper",
        ArtifactRole.ReverseVtable => "reverse-vtable",
        ArtifactRole.ModuleInitializer => "module-initializer",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unhandled artifact role."),
    };

    private static bool TryParseToken(string token, out ArtifactRole role)
    {
        switch (token)
        {
            case "csharp-public": role = ArtifactRole.CSharpPublic; return true;
            case "pinvoke": role = ArtifactRole.PInvoke; return true;
            case "swift-wrapper": role = ArtifactRole.SwiftWrapper; return true;
            case "callback": role = ArtifactRole.Callback; return true;
            case "metadata-helper": role = ArtifactRole.MetadataHelper; return true;
            case "reverse-vtable": role = ArtifactRole.ReverseVtable; return true;
            case "module-initializer": role = ArtifactRole.ModuleInitializer; return true;
            default: role = default; return false;
        }
    }
}

/// <summary>
/// Fluent construction of an <see cref="ArtifactId"/> from the owning declaration.
/// </summary>
public static class DeclIdArtifactExtensions
{
    /// <summary>Builds the artifact id for <paramref name="role"/> on this declaration.</summary>
    public static ArtifactId Artifact(this DeclId decl, ArtifactRole role, int ordinal = 0) =>
        ArtifactId.Create(decl, role, ordinal);
}
