// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Immutable;
using System.Text;

namespace BindingsGeneration;

/// <summary>
/// Overload-stable identity for a Swift declaration tracked by the diagnostic
/// pipeline (emitted member, skipped member, synthesized member, wrapped member).
/// </summary>
/// <remarks>
/// <para>
/// Composed of: Swift module, declaration path (containing-type chain), member
/// kind, base name (no parameter labels), parameter labels and parameter Swift
/// type expressions (jointly — labels alone don't disambiguate trailing-closure-
/// vs-non overloads), accessor kind, and the mangled symbol when available.
/// </para>
/// <para>
/// Two declarations with the same module, decl path, kind, base name, parameter
/// labels, parameter types, and accessor are equal — this is the key the report
/// dedup sets use. Mangled symbol is a last-resort tiebreaker stored alongside
/// for diagnostic context; it participates in equality so two overloads whose
/// only structural difference is the mangled symbol still record distinctly.
/// </para>
/// <para>
/// Fields that don't apply to a given decl kind are explicitly empty rather
/// than inferred: properties have empty parameter lists; types have no accessor
/// or parameters; legacy callers that only know <c>(kind, name, containing)</c>
/// produce identities with empty parameter lists and <see cref="AccessorKind.None"/>.
/// </para>
/// </remarks>
public readonly record struct MemberDiagnosticIdentity
{
    /// <summary>
    /// Swift module name (e.g., <c>"MusicKit"</c>). Empty string for
    /// stdlib/global declarations with no parent module.
    /// </summary>
    public string Module { get; init; }

    /// <summary>
    /// Containing-type chain, dot-separated, relative to <see cref="Module"/>
    /// (e.g., <c>"Loader.Payload"</c>). Empty string when the member is at
    /// module scope.
    /// </summary>
    public string DeclPath { get; init; }

    /// <summary>
    /// Member-kind disambiguator. Distinguishes a method <c>foo</c> from a
    /// property <c>foo</c> (Swift forbids the collision but the model can
    /// hold both shapes during emission).
    /// </summary>
    public BindingItemKind Kind { get; init; }

    /// <summary>
    /// Base name without parameter labels (e.g., <c>"fetch"</c>, <c>"init"</c>,
    /// <c>"subscript"</c>, the operator symbol for operators).
    /// </summary>
    public string BaseName { get; init; }

    /// <summary>
    /// Parameter argument labels in declaration order. Empty for properties,
    /// types, and the legacy <see cref="FromMember"/> entry point.
    /// </summary>
    public ImmutableArray<string> ParameterLabels { get; init; }

    /// <summary>
    /// Parameter Swift type expressions in declaration order, parallel to
    /// <see cref="ParameterLabels"/>. Required: labels alone don't disambiguate
    /// e.g. trailing-closure-vs-non overloads with the same labels but
    /// different types.
    /// </summary>
    public ImmutableArray<string> ParameterTypes { get; init; }

    /// <summary>
    /// Accessor kind for property/subscript accessors;
    /// <see cref="AccessorKind.None"/> for non-accessor declarations.
    /// </summary>
    public AccessorKind Accessor { get; init; }

    /// <summary>
    /// Mangled Swift symbol when known (e.g. for methods/operators/subscripts).
    /// Null when the caller doesn't have it (legacy entry points,
    /// property-level identity without an accessor selected).
    /// </summary>
    public string? MangledSymbol { get; init; }

    /// <summary>
    /// Normalized generic context of the declaration, carried so <see cref="ToDeclId"/> can
    /// produce a complete <see cref="DeclId"/>.
    /// </summary>
    /// <remarks>
    /// Deliberately excluded from <see cref="Equals(MemberDiagnosticIdentity)"/> and
    /// <see cref="GetHashCode"/>: this type's equality is the report dedup key, and folding in a
    /// component the pre-existing key never had would split rows that used to collapse — a
    /// silent change to report contents. Declarations that differ only in generic context are
    /// already separated for dedup purposes by their mangled symbols.
    /// </remarks>
    public string? GenericContext { get; init; }

    /// <summary>
    /// Extra declaration discriminator (e.g. <c>static</c>) carried for the same round-trip reason
    /// as <see cref="GenericContext"/>, and excluded from equality for the same reason: adding an
    /// axis the pre-existing dedup key never had would split report rows that used to collapse.
    /// </summary>
    public string? Discriminator { get; init; }

    /// <summary>
    /// Build an identity directly. Validates that
    /// <see cref="ParameterLabels"/> and <see cref="ParameterTypes"/> have
    /// matching lengths. Every component <see cref="ToDeclId"/> projects is settable here —
    /// including the two excluded from equality — so a hand-built identity round-trips to the
    /// same <see cref="DeclId"/> as the decl-derived one instead of silently dropping an axis.
    /// </summary>
    public static MemberDiagnosticIdentity Create(
        string? module,
        string? declPath,
        BindingItemKind kind,
        string? baseName,
        ImmutableArray<string>? parameterLabels = null,
        ImmutableArray<string>? parameterTypes = null,
        AccessorKind accessor = AccessorKind.None,
        string? mangledSymbol = null,
        string? genericContext = null,
        string? discriminator = null)
    {
        var labels = parameterLabels ?? ImmutableArray<string>.Empty;
        var types = parameterTypes ?? ImmutableArray<string>.Empty;
        if (labels.IsDefault) labels = ImmutableArray<string>.Empty;
        if (types.IsDefault) types = ImmutableArray<string>.Empty;
        if (labels.Length != types.Length)
            throw new ArgumentException(
                $"ParameterLabels.Length ({labels.Length}) must equal ParameterTypes.Length ({types.Length}).");

        return new MemberDiagnosticIdentity
        {
            Module = module ?? string.Empty,
            DeclPath = declPath ?? string.Empty,
            Kind = kind,
            BaseName = baseName ?? string.Empty,
            ParameterLabels = labels,
            ParameterTypes = types,
            Accessor = accessor,
            MangledSymbol = mangledSymbol,
            GenericContext = genericContext,
            Discriminator = discriminator,
        };
    }

    /// <summary>
    /// Projects a <see cref="DeclId"/> down to a diagnostic identity. The two types carry the
    /// same components, so every decl-aware factory below is this projection applied to the
    /// matching <see cref="DeclIdFactory"/> call — there is exactly one implementation of "which
    /// declaration is this", and the report can never disagree with the id.
    /// </summary>
    public static MemberDiagnosticIdentity FromDeclId(DeclId declId) =>
        new()
        {
            Module = declId.Module ?? string.Empty,
            DeclPath = declId.DeclPath ?? string.Empty,
            Kind = declId.Kind,
            BaseName = declId.Name ?? string.Empty,
            ParameterLabels = declId.ParameterLabels.IsDefault
                ? ImmutableArray<string>.Empty
                : declId.ParameterLabels,
            ParameterTypes = declId.ParameterTypes.IsDefault
                ? ImmutableArray<string>.Empty
                : declId.ParameterTypes,
            Accessor = declId.Accessor,
            // DeclId normalizes "no symbol" to empty; this type spells it null. Round-tripping
            // through empty would be equal anyway (Equals normalizes), but keep the original
            // spelling so a caller reading MangledSymbol sees what it saw before.
            MangledSymbol = string.IsNullOrEmpty(declId.Symbol) ? null : declId.Symbol,
            GenericContext = string.IsNullOrEmpty(declId.GenericContext) ? null : declId.GenericContext,
            Discriminator = string.IsNullOrEmpty(declId.Discriminator) ? null : declId.Discriminator,
        };

    /// <summary>
    /// Widens this identity back to a <see cref="DeclId"/> — the stable, serializable form used
    /// as a report field, a denylist key, and an attribution value.
    /// </summary>
    public DeclId ToDeclId() =>
        DeclId.Create(
            Module,
            DeclPath,
            Kind,
            BaseName,
            ParameterLabels,
            ParameterTypes,
            Accessor,
            GenericContext,
            MangledSymbol,
            Discriminator);

    /// <summary>
    /// Convenience builder from the legacy <c>(kind, name, containingDecl)</c>
    /// triple. Parameter labels/types are empty and accessor is
    /// <see cref="AccessorKind.None"/>; the mangled symbol is unknown. Used by
    /// the legacy <see cref="ReportCollector.RecordMemberSkipped(BindingItemKind, string, BaseDecl?, SkipReason, string?)"/>
    /// entry points where the caller doesn't have a richer decl in scope.
    /// Overloads with identical base names share one identity under this
    /// builder — callers that need per-overload identity must use one of the
    /// decl-aware overloads (<see cref="FromMethod"/>, <see cref="FromProperty"/>,
    /// <see cref="FromSubscript"/>, <see cref="FromOperator"/>).
    /// </summary>
    public static MemberDiagnosticIdentity FromMember(BindingItemKind kind, string name, BaseDecl? containingDecl) =>
        FromDeclId(DeclIdFactory.ForMember(kind, name, containingDecl));

    /// <summary>
    /// Identity for an emitted-or-skipped Swift method (or constructor).
    /// Captures parameter labels + Swift type expressions in declaration
    /// order so overloaded methods record distinctly.
    /// </summary>
    public static MemberDiagnosticIdentity FromMethod(MethodDecl methodDecl, BaseDecl? containingDecl = null) =>
        FromDeclId(DeclIdFactory.ForMethod(methodDecl, containingDecl));

    /// <summary>
    /// Identity for an emitted-or-skipped Swift property declaration.
    /// <paramref name="accessor"/> distinguishes the getter, setter, and
    /// observers; pass <see cref="AccessorKind.None"/> for a property-level
    /// identity that covers both accessors as a single unit.
    /// </summary>
    public static MemberDiagnosticIdentity FromProperty(
        PropertyDecl propertyDecl,
        AccessorKind accessor = AccessorKind.None,
        BaseDecl? containingDecl = null) =>
        FromDeclId(DeclIdFactory.ForProperty(propertyDecl, accessor, containingDecl));

    /// <summary>
    /// Identity for an emitted-or-skipped Swift subscript declaration.
    /// All subscripts share a single Swift name (<c>"subscript"</c>), so
    /// disambiguation depends entirely on the index parameter signature
    /// and accessor kind. Pass
    /// <see cref="AccessorKind.SubscriptGetter"/>/<see cref="AccessorKind.SubscriptSetter"/>
    /// to distinguish accessor-level skips.
    /// </summary>
    public static MemberDiagnosticIdentity FromSubscript(
        SubscriptDecl subscriptDecl,
        AccessorKind accessor = AccessorKind.None,
        BaseDecl? containingDecl = null) =>
        FromDeclId(DeclIdFactory.ForSubscript(subscriptDecl, accessor, containingDecl));

    /// <summary>
    /// Identity for an emitted-or-skipped Swift operator declaration.
    /// Pulls the parameter signature from the underlying method so
    /// overloaded operators record distinctly.
    /// </summary>
    public static MemberDiagnosticIdentity FromOperator(OperatorDecl operatorDecl, BaseDecl? containingDecl = null) =>
        FromDeclId(DeclIdFactory.ForOperator(operatorDecl, containingDecl));

    /// <summary>
    /// Identity for an emitted-or-skipped type declaration. Type identity
    /// carries no parameter information.
    /// </summary>
    public static MemberDiagnosticIdentity FromType(TypeDecl typeDecl) =>
        FromDeclId(DeclIdFactory.ForType(typeDecl));

    /// <summary>
    /// Stable, deterministic string projection used for diagnostic output and
    /// dedup-key logging. Format:
    /// <c>Module|DeclPath|Kind|BaseName(label1:Type1,label2:Type2)|Accessor|Mangled</c>.
    /// Two identities are equal iff their stable strings are equal.
    /// </summary>
    public string ToStableString()
    {
        var sb = new StringBuilder();
        // Append-with-null is a no-op on .NET 6+, but be explicit so the
        // projected string is identical for default(T) and an explicitly-empty
        // identity — keeps ToStableString consistent with Equals.
        sb.Append(Module ?? string.Empty).Append('|');
        sb.Append(DeclPath ?? string.Empty).Append('|');
        sb.Append(Kind).Append('|');
        sb.Append(BaseName ?? string.Empty).Append('(');
        if (!ParameterLabels.IsDefaultOrEmpty)
        {
            for (var i = 0; i < ParameterLabels.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(ParameterLabels[i]).Append(':').Append(ParameterTypes[i]);
            }
        }
        sb.Append(")|");
        sb.Append(Accessor).Append('|');
        sb.Append(MangledSymbol ?? string.Empty);
        return sb.ToString();
    }

    /// <inheritdoc />
    public override string ToString() => ToStableString();

    /// <inheritdoc />
    public bool Equals(MemberDiagnosticIdentity other)
    {
        // Normalize null vs empty: factory methods coerce strings to empty,
        // but record-struct `with { Module = null }` and `default(T)` can
        // leave fields null. Treat null and empty as identical so identities
        // built through the legacy paths still dedup correctly.
        if (!string.Equals(Module ?? string.Empty, other.Module ?? string.Empty, StringComparison.Ordinal)) return false;
        if (!string.Equals(DeclPath ?? string.Empty, other.DeclPath ?? string.Empty, StringComparison.Ordinal)) return false;
        if (Kind != other.Kind) return false;
        if (!string.Equals(BaseName ?? string.Empty, other.BaseName ?? string.Empty, StringComparison.Ordinal)) return false;
        if (Accessor != other.Accessor) return false;
        if (!string.Equals(MangledSymbol ?? string.Empty, other.MangledSymbol ?? string.Empty, StringComparison.Ordinal)) return false;

        var leftLabels = ParameterLabels.IsDefault ? ImmutableArray<string>.Empty : ParameterLabels;
        var rightLabels = other.ParameterLabels.IsDefault ? ImmutableArray<string>.Empty : other.ParameterLabels;
        if (leftLabels.Length != rightLabels.Length) return false;

        var leftTypes = ParameterTypes.IsDefault ? ImmutableArray<string>.Empty : ParameterTypes;
        var rightTypes = other.ParameterTypes.IsDefault ? ImmutableArray<string>.Empty : other.ParameterTypes;
        if (leftTypes.Length != rightTypes.Length) return false;

        for (var i = 0; i < leftLabels.Length; i++)
        {
            if (!string.Equals(leftLabels[i], rightLabels[i], StringComparison.Ordinal)) return false;
            if (!string.Equals(leftTypes[i], rightTypes[i], StringComparison.Ordinal)) return false;
        }
        return true;
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        // Mirror the null-vs-empty normalization in Equals so the contract
        // GetHashCode(a) == GetHashCode(b) when a.Equals(b) holds.
        hash.Add(Module ?? string.Empty, StringComparer.Ordinal);
        hash.Add(DeclPath ?? string.Empty, StringComparer.Ordinal);
        hash.Add(Kind);
        hash.Add(BaseName ?? string.Empty, StringComparer.Ordinal);
        hash.Add(Accessor);
        hash.Add(MangledSymbol ?? string.Empty, StringComparer.Ordinal);

        var labels = ParameterLabels.IsDefault ? ImmutableArray<string>.Empty : ParameterLabels;
        var types = ParameterTypes.IsDefault ? ImmutableArray<string>.Empty : ParameterTypes;
        hash.Add(labels.Length);
        for (var i = 0; i < labels.Length; i++)
        {
            hash.Add(labels[i], StringComparer.Ordinal);
            hash.Add(types[i], StringComparer.Ordinal);
        }
        return hash.ToHashCode();
    }

}

/// <summary>
/// Accessor disambiguator carried by <see cref="MemberDiagnosticIdentity"/>.
/// Property and subscript accessors share a base name with the underlying
/// declaration, so an explicit accessor kind is required to distinguish
/// per-accessor diagnostic events.
/// </summary>
public enum AccessorKind
{
    /// <summary>Not an accessor (the default for non-accessor declarations and property-level identities).</summary>
    None,

    /// <summary>Property getter.</summary>
    Getter,

    /// <summary>Property setter.</summary>
    Setter,

    /// <summary>Property <c>willSet</c> observer.</summary>
    WillSet,

    /// <summary>Property <c>didSet</c> observer.</summary>
    DidSet,

    /// <summary>Subscript getter.</summary>
    SubscriptGetter,

    /// <summary>Subscript setter.</summary>
    SubscriptSetter,
}
