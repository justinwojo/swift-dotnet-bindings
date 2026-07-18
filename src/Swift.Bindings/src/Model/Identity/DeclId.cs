// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Immutable;
using System.Text;

namespace BindingsGeneration;

/// <summary>
/// Stable, serializable identity for one logical Swift declaration.
/// </summary>
/// <remarks>
/// <para>
/// Every component is a fact the parser reads off the ABI surface — owning module, the
/// containing-decl chain, declaration kind, base name, accessor kind, parameter labels and
/// parameter Swift type expressions, the generic context, and the mangled symbol where one
/// exists. Nothing positional participates: no line numbers, no emission order, no collection
/// iteration order. The same declaration therefore produces the same id in a later process, on
/// another machine, and under a different emission order — which is what makes the id usable as
/// a denylist key, a report key, and an attribution value that survives being written to disk.
/// </para>
/// <para>
/// Two forms are provided. <see cref="Canonical"/> is a lossless, human-readable projection that
/// round-trips through <see cref="Parse"/>; <see cref="ShortHash"/> is the 8-hex-character FNV-1a
/// digest of that canonical string, for places that need a compact token (symbol suffixes,
/// terse logs). Equality is structural over the components, so two ids are equal exactly when
/// their canonical strings are.
/// </para>
/// <para>
/// Computation lives here and in <see cref="DeclIdFactory"/> rather than in the emitters, so an
/// emitter can never grow its own slightly-different notion of "which declaration is this".
/// The factory is a set of pure functions with no process-global memo: identity is cheap to
/// recompute and a static cache is exactly the kind of cross-run shared state that makes
/// emission non-deterministic.
/// </para>
/// </remarks>
public readonly record struct DeclId
{
    /// <summary>Field separator in <see cref="Canonical"/>.</summary>
    private const char FieldSeparator = '|';

    /// <summary>Separator between parameter entries inside the parameter field.</summary>
    private const char ParameterSeparator = ',';

    /// <summary>Separator between a parameter's label and its Swift type expression.</summary>
    private const char LabelSeparator = ':';

    private const char EscapePrefix = '\\';

    /// <summary>Number of <see cref="FieldSeparator"/>-delimited fields in <see cref="Canonical"/>.</summary>
    private const int FieldCount = 9;

    /// <summary>
    /// Owning Swift module (e.g. <c>"MusicKit"</c>). Empty for a declaration with no module.
    /// </summary>
    public string Module { get; init; }

    /// <summary>
    /// Containing-declaration chain, dot-separated, relative to <see cref="Module"/>
    /// (e.g. <c>"Loader.Payload"</c>). Empty when the declaration sits at module scope.
    /// The fully-qualified Swift path is <see cref="Module"/> + <see cref="DeclPath"/> +
    /// <see cref="Name"/>; see <see cref="QualifiedPath"/>.
    /// </summary>
    public string DeclPath { get; init; }

    /// <summary>Declaration kind — distinguishes a method <c>foo</c> from a property <c>foo</c>.</summary>
    public BindingItemKind Kind { get; init; }

    /// <summary>
    /// Base name with no parameter labels (e.g. <c>"fetch"</c>, <c>"init"</c>,
    /// <c>"subscript"</c>, the operator symbol for operators, the module name for a module).
    /// </summary>
    public string Name { get; init; }

    /// <summary>Parameter argument labels in declaration order. Empty for non-parameterized declarations.</summary>
    public ImmutableArray<string> ParameterLabels { get; init; }

    /// <summary>
    /// Parameter Swift type expressions in declaration order, parallel to
    /// <see cref="ParameterLabels"/>. Labels alone don't separate every overload pair, so both
    /// axes are carried.
    /// </summary>
    public ImmutableArray<string> ParameterTypes { get; init; }

    /// <summary>Accessor discriminator; <see cref="AccessorKind.None"/> for non-accessor declarations.</summary>
    public AccessorKind Accessor { get; init; }

    /// <summary>
    /// Normalized generic context — a method's <c>genericSig</c> or a type's generic parameter
    /// list, whitespace-collapsed. Empty for non-generic declarations. Separates an id whose
    /// other components coincide but whose generic environment differs.
    /// </summary>
    public string GenericContext { get; init; }

    /// <summary>
    /// Mangled Swift symbol (or USR) when the declaration has one; empty otherwise. Carried as a
    /// component so two declarations the structural fields cannot separate still get distinct ids.
    /// </summary>
    public string Symbol { get; init; }

    /// <summary>
    /// Extra stable discriminator for declaration shapes the structural fields above cannot
    /// separate. Empty for most declarations.
    /// </summary>
    /// <remarks>
    /// The motivating case is Swift permitting an instance and a static member of the same name on
    /// one type: <c>var count: Int</c> and <c>static var count: Int</c> agree on module, path,
    /// kind, name, (empty) parameters, accessor, generic context, and — because a property id
    /// carries no accessor symbol — mangled symbol too. Without a discriminator they are one id,
    /// which would silently merge two distinct declarations in every consumer keyed on it.
    /// </remarks>
    public string Discriminator { get; init; }

    /// <summary>
    /// Builds an id from its components, normalizing nulls to empty and validating that the two
    /// parameter arrays are parallel.
    /// </summary>
    public static DeclId Create(
        string? module,
        string? declPath,
        BindingItemKind kind,
        string? name,
        ImmutableArray<string>? parameterLabels = null,
        ImmutableArray<string>? parameterTypes = null,
        AccessorKind accessor = AccessorKind.None,
        string? genericContext = null,
        string? symbol = null,
        string? discriminator = null)
    {
        var labels = Normalize(parameterLabels);
        var types = Normalize(parameterTypes);
        if (labels.Length != types.Length)
            throw new ArgumentException(
                $"ParameterLabels.Length ({labels.Length}) must equal ParameterTypes.Length ({types.Length}).");

        return new DeclId
        {
            Module = module ?? string.Empty,
            DeclPath = declPath ?? string.Empty,
            Kind = kind,
            Name = name ?? string.Empty,
            ParameterLabels = labels,
            ParameterTypes = types,
            Accessor = accessor,
            GenericContext = NormalizeGenericContext(genericContext),
            Symbol = symbol ?? string.Empty,
            Discriminator = discriminator ?? string.Empty,
        };
    }

    /// <summary>
    /// Dotted Swift path of the declaration itself — <c>Module.Containing.Chain.Name</c>, with
    /// empty segments elided. Convenience projection for logs and report text; not the identity.
    /// </summary>
    public string QualifiedPath
    {
        get
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrEmpty(Module)) sb.Append(Module);
            if (!string.IsNullOrEmpty(DeclPath))
            {
                if (sb.Length > 0) sb.Append('.');
                sb.Append(DeclPath);
            }
            if (!string.IsNullOrEmpty(Name))
            {
                if (sb.Length > 0) sb.Append('.');
                sb.Append(Name);
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// Lossless canonical projection — nine <c>|</c>-separated fields:
    /// <c>Module|DeclPath|Kind|Name|label:Type,label:Type|Accessor|GenericContext|Symbol|Discriminator</c>.
    /// Component values escape the four structural characters, so the form round-trips through
    /// <see cref="Parse"/> even for Swift types that contain commas, colons, or pipes.
    /// <see cref="Parse"/> accepts only this exact shape, so <c>Parse(x).Canonical == x</c> holds
    /// for every string it accepts — two distinct persisted strings can never name one declaration.
    /// </summary>
    public string Canonical
    {
        get
        {
            var sb = new StringBuilder();
            AppendEscaped(sb, Module).Append(FieldSeparator);
            AppendEscaped(sb, DeclPath).Append(FieldSeparator);
            sb.Append(Kind).Append(FieldSeparator);
            AppendEscaped(sb, Name).Append(FieldSeparator);

            var labels = Normalize(ParameterLabels);
            var types = Normalize(ParameterTypes);
            for (var i = 0; i < labels.Length; i++)
            {
                if (i > 0) sb.Append(ParameterSeparator);
                AppendEscaped(sb, labels[i]).Append(LabelSeparator);
                AppendEscaped(sb, types[i]);
            }
            sb.Append(FieldSeparator);

            sb.Append(Accessor).Append(FieldSeparator);
            AppendEscaped(sb, GenericContext).Append(FieldSeparator);
            AppendEscaped(sb, Symbol).Append(FieldSeparator);
            AppendEscaped(sb, Discriminator);
            return sb.ToString();
        }
    }

    /// <summary>
    /// 8-character uppercase-hex FNV-1a digest of <see cref="Canonical"/> — the compact form for
    /// symbol suffixes and terse diagnostics. Deterministic across processes and platforms.
    /// </summary>
    public string ShortHash => EmitterUtility.DeterministicHash8(Canonical);

    /// <summary>
    /// Parses a <see cref="Canonical"/> string back into an id.
    /// </summary>
    /// <exception cref="FormatException">The input is not a well-formed canonical id.</exception>
    public static DeclId Parse(string canonical)
    {
        if (!TryParse(canonical, out var id))
            throw new FormatException($"'{canonical}' is not a well-formed DeclId canonical string.");
        return id;
    }

    /// <summary>
    /// Attempts to parse a <see cref="Canonical"/> string. Returns false (leaving
    /// <paramref name="id"/> at its default) when the input has the wrong field count, an
    /// unrecognized kind/accessor, a malformed parameter entry, or an escape sequence
    /// <see cref="Canonical"/> could not have written.
    /// </summary>
    public static bool TryParse(string? canonical, out DeclId id)
    {
        id = default;
        if (canonical is null)
            return false;

        var fields = SplitUnescaped(canonical, FieldSeparator);
        if (fields.Count != FieldCount)
            return false;

        if (!TryParseEnumName(fields[2], out BindingItemKind kind))
            return false;
        if (!TryParseEnumName(fields[5], out AccessorKind accessor))
            return false;

        var labels = ImmutableArray.CreateBuilder<string>();
        var types = ImmutableArray.CreateBuilder<string>();
        if (fields[4].Length > 0)
        {
            foreach (var entry in SplitUnescaped(fields[4], ParameterSeparator))
            {
                var parts = SplitUnescaped(entry, LabelSeparator);
                if (parts.Count != 2)
                    return false;
                if (!TryUnescape(parts[0], out var label) || !TryUnescape(parts[1], out var type))
                    return false;
                labels.Add(label);
                types.Add(type);
            }
        }

        if (!TryUnescape(fields[0], out var module) ||
            !TryUnescape(fields[1], out var declPath) ||
            !TryUnescape(fields[3], out var name) ||
            !TryUnescape(fields[6], out var genericContext) ||
            !TryUnescape(fields[7], out var symbol) ||
            !TryUnescape(fields[8], out var discriminator))
        {
            return false;
        }

        id = new DeclId
        {
            Module = module,
            DeclPath = declPath,
            Kind = kind,
            Name = name,
            ParameterLabels = labels.ToImmutable(),
            ParameterTypes = types.ToImmutable(),
            Accessor = accessor,
            GenericContext = genericContext,
            Symbol = symbol,
            Discriminator = discriminator,
        };
        return true;
    }

    /// <summary>
    /// Parses an enum by NAME only. <see cref="Enum.TryParse{TEnum}(string, bool, out TEnum)"/>
    /// also accepts a bare integer and undefined values, which would let two different strings
    /// parse to the same id and break the <c>Parse(x).Canonical == x</c> guarantee that makes the
    /// canonical form usable as a persisted key.
    /// </summary>
    private static bool TryParseEnumName<TEnum>(string text, out TEnum value)
        where TEnum : struct, Enum
    {
        value = default;
        if (text.Length == 0 || char.IsAsciiDigit(text[0]) || text[0] == '-' || text[0] == '+')
            return false;
        return Enum.TryParse(text, ignoreCase: false, out value) && Enum.IsDefined(value);
    }

    /// <inheritdoc />
    public override string ToString() => Canonical;

    /// <inheritdoc />
    public bool Equals(DeclId other)
    {
        // Null and empty are the same absent value: an id built through Create is normalized,
        // but `default(DeclId)` and a `with { Module = null }` clone are not, and both must still
        // compare equal to their normalized twin.
        if (!SameText(Module, other.Module)) return false;
        if (!SameText(DeclPath, other.DeclPath)) return false;
        if (Kind != other.Kind) return false;
        if (!SameText(Name, other.Name)) return false;
        if (Accessor != other.Accessor) return false;
        if (!SameText(GenericContext, other.GenericContext)) return false;
        if (!SameText(Symbol, other.Symbol)) return false;
        if (!SameText(Discriminator, other.Discriminator)) return false;

        var leftLabels = Normalize(ParameterLabels);
        var rightLabels = Normalize(other.ParameterLabels);
        var leftTypes = Normalize(ParameterTypes);
        var rightTypes = Normalize(other.ParameterTypes);
        if (leftLabels.Length != rightLabels.Length || leftTypes.Length != rightTypes.Length)
            return false;

        for (var i = 0; i < leftLabels.Length; i++)
        {
            if (!SameText(leftLabels[i], rightLabels[i])) return false;
            if (!SameText(leftTypes[i], rightTypes[i])) return false;
        }
        return true;
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Module ?? string.Empty, StringComparer.Ordinal);
        hash.Add(DeclPath ?? string.Empty, StringComparer.Ordinal);
        hash.Add(Kind);
        hash.Add(Name ?? string.Empty, StringComparer.Ordinal);
        hash.Add(Accessor);
        hash.Add(GenericContext ?? string.Empty, StringComparer.Ordinal);
        hash.Add(Symbol ?? string.Empty, StringComparer.Ordinal);
        hash.Add(Discriminator ?? string.Empty, StringComparer.Ordinal);

        var labels = Normalize(ParameterLabels);
        var types = Normalize(ParameterTypes);
        hash.Add(labels.Length);
        for (var i = 0; i < labels.Length; i++)
        {
            hash.Add(labels[i], StringComparer.Ordinal);
            hash.Add(types[i], StringComparer.Ordinal);
        }
        return hash.ToHashCode();
    }

    private static bool SameText(string? left, string? right) =>
        string.Equals(left ?? string.Empty, right ?? string.Empty, StringComparison.Ordinal);

    private static ImmutableArray<string> Normalize(ImmutableArray<string>? values)
    {
        var array = values ?? ImmutableArray<string>.Empty;
        return array.IsDefault ? ImmutableArray<string>.Empty : array;
    }

    private static ImmutableArray<string> Normalize(ImmutableArray<string> values) =>
        values.IsDefault ? ImmutableArray<string>.Empty : values;

    /// <summary>
    /// Collapses every whitespace run in a generic signature to a single space and trims the
    /// result, so two spellings of the same signature ("&lt;τ_0_0  where …&gt;" vs
    /// "&lt;τ_0_0 where …&gt;") cannot produce different ids.
    /// </summary>
    private static string NormalizeGenericContext(string? generic)
    {
        if (string.IsNullOrWhiteSpace(generic))
            return string.Empty;

        var sb = new StringBuilder(generic.Length);
        var pendingSpace = false;
        foreach (var c in generic)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = sb.Length > 0;
                continue;
            }
            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static StringBuilder AppendEscaped(StringBuilder sb, string? value)
    {
        if (string.IsNullOrEmpty(value))
            return sb;

        foreach (var c in value)
        {
            if (c is EscapePrefix or FieldSeparator or ParameterSeparator or LabelSeparator)
                sb.Append(EscapePrefix);
            sb.Append(c);
        }
        return sb;
    }

    /// <summary>
    /// Splits on <paramref name="separator"/> occurrences that are not escape-prefixed. Always
    /// returns at least one (possibly empty) segment.
    /// </summary>
    private static List<string> SplitUnescaped(string value, char separator)
    {
        var segments = new List<string>();
        var start = 0;
        var escaped = false;
        for (var i = 0; i < value.Length; i++)
        {
            if (escaped)
            {
                escaped = false;
                continue;
            }
            if (value[i] == EscapePrefix)
            {
                escaped = true;
                continue;
            }
            if (value[i] == separator)
            {
                segments.Add(value.Substring(start, i - start));
                start = i + 1;
            }
        }
        segments.Add(value.Substring(start));
        return segments;
    }

    /// <summary>
    /// Reverses <see cref="AppendEscaped"/>, rejecting any escape sequence that writer could not
    /// have produced: a prefix before a non-structural character, or a trailing lone prefix.
    /// Accepting those would let <c>foo\q</c> and <c>fooq</c> both parse to the same id — two
    /// persisted strings naming one declaration, which breaks the canonical form as a key.
    /// </summary>
    private static bool TryUnescape(string value, out string result)
    {
        result = value;
        if (value.IndexOf(EscapePrefix) < 0)
            return true;

        var sb = new StringBuilder(value.Length);
        var escaped = false;
        foreach (var c in value)
        {
            if (escaped)
            {
                if (c is not (EscapePrefix or FieldSeparator or ParameterSeparator or LabelSeparator))
                    return false;
                sb.Append(c);
                escaped = false;
                continue;
            }
            if (c == EscapePrefix)
            {
                escaped = true;
                continue;
            }
            sb.Append(c);
        }
        if (escaped)
            return false;

        result = sb.ToString();
        return true;
    }
}
