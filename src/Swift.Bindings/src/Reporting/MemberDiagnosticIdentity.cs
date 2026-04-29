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
    /// Build an identity directly. Validates that
    /// <see cref="ParameterLabels"/> and <see cref="ParameterTypes"/> have
    /// matching lengths.
    /// </summary>
    public static MemberDiagnosticIdentity Create(
        string? module,
        string? declPath,
        BindingItemKind kind,
        string? baseName,
        ImmutableArray<string>? parameterLabels = null,
        ImmutableArray<string>? parameterTypes = null,
        AccessorKind accessor = AccessorKind.None,
        string? mangledSymbol = null)
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
        };
    }

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
    public static MemberDiagnosticIdentity FromMember(BindingItemKind kind, string name, BaseDecl? containingDecl)
    {
        var (module, declPath) = SplitContainer(containingDecl);
        return Create(module, declPath, kind, name);
    }

    /// <summary>
    /// Identity for an emitted-or-skipped Swift method (or constructor).
    /// Captures parameter labels + Swift type expressions in declaration
    /// order so overloaded methods record distinctly.
    /// </summary>
    public static MemberDiagnosticIdentity FromMethod(MethodDecl methodDecl, BaseDecl? containingDecl = null)
    {
        ArgumentNullException.ThrowIfNull(methodDecl);
        var container = containingDecl ?? methodDecl.ParentDecl;
        var (module, declPath) = SplitContainer(container);
        var (labels, types) = BuildParameterArrays(methodDecl.CSSignature);
        return new MemberDiagnosticIdentity
        {
            Module = module,
            DeclPath = declPath,
            Kind = BindingItemKind.Method,
            BaseName = methodDecl.Name,
            ParameterLabels = labels,
            ParameterTypes = types,
            Accessor = AccessorKind.None,
            MangledSymbol = methodDecl.MangledName,
        };
    }

    /// <summary>
    /// Identity for an emitted-or-skipped Swift property declaration.
    /// <paramref name="accessor"/> distinguishes the getter, setter, and
    /// observers; pass <see cref="AccessorKind.None"/> for a property-level
    /// identity that covers both accessors as a single unit.
    /// </summary>
    public static MemberDiagnosticIdentity FromProperty(
        PropertyDecl propertyDecl,
        AccessorKind accessor = AccessorKind.None,
        BaseDecl? containingDecl = null)
    {
        ArgumentNullException.ThrowIfNull(propertyDecl);
        var container = containingDecl ?? propertyDecl.ParentDecl;
        var (module, declPath) = SplitContainer(container);
        return new MemberDiagnosticIdentity
        {
            Module = module,
            DeclPath = declPath,
            Kind = BindingItemKind.Property,
            BaseName = propertyDecl.Name,
            ParameterLabels = ImmutableArray<string>.Empty,
            ParameterTypes = ImmutableArray<string>.Empty,
            Accessor = accessor,
            MangledSymbol = null,
        };
    }

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
        BaseDecl? containingDecl = null)
    {
        ArgumentNullException.ThrowIfNull(subscriptDecl);
        var container = containingDecl ?? subscriptDecl.ParentDecl;
        var (module, declPath) = SplitContainer(container);
        var (labels, types) = BuildParameterArrays(subscriptDecl.IndexParameters);
        return new MemberDiagnosticIdentity
        {
            Module = module,
            DeclPath = declPath,
            Kind = BindingItemKind.Subscript,
            BaseName = subscriptDecl.Name,
            ParameterLabels = labels,
            ParameterTypes = types,
            Accessor = accessor,
            MangledSymbol = subscriptDecl.MangledName,
        };
    }

    /// <summary>
    /// Identity for an emitted-or-skipped Swift operator declaration.
    /// Pulls the parameter signature from the underlying method so
    /// overloaded operators record distinctly.
    /// </summary>
    public static MemberDiagnosticIdentity FromOperator(OperatorDecl operatorDecl, BaseDecl? containingDecl = null)
    {
        ArgumentNullException.ThrowIfNull(operatorDecl);
        var container = containingDecl ?? operatorDecl.ParentDecl;
        var (module, declPath) = SplitContainer(container);
        var (labels, types) = BuildParameterArrays(operatorDecl.UnderlyingMethod.CSSignature);
        return new MemberDiagnosticIdentity
        {
            Module = module,
            DeclPath = declPath,
            Kind = BindingItemKind.Operator,
            BaseName = operatorDecl.OperatorSymbol,
            ParameterLabels = labels,
            ParameterTypes = types,
            Accessor = AccessorKind.None,
            MangledSymbol = operatorDecl.UnderlyingMethod.MangledName,
        };
    }

    /// <summary>
    /// Identity for an emitted-or-skipped type declaration. Type identity
    /// carries no parameter information.
    /// </summary>
    public static MemberDiagnosticIdentity FromType(TypeDecl typeDecl)
    {
        ArgumentNullException.ThrowIfNull(typeDecl);
        var (module, declPath) = SplitContainer(typeDecl.ParentDecl);
        return Create(module, declPath, BindingItemKind.Type, typeDecl.Name);
    }

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

    private static (ImmutableArray<string> Labels, ImmutableArray<string> Types) BuildParameterArrays(
        IReadOnlyList<ArgumentDecl> args)
    {
        if (args.Count == 0)
            return (ImmutableArray<string>.Empty, ImmutableArray<string>.Empty);

        var labels = ImmutableArray.CreateBuilder<string>(args.Count);
        var types = ImmutableArray.CreateBuilder<string>(args.Count);
        foreach (var arg in args)
        {
            labels.Add(arg.Name ?? string.Empty);
            types.Add(arg.SwiftTypeSpec?.ToString() ?? string.Empty);
        }
        return (labels.MoveToImmutable(), types.MoveToImmutable());
    }

    private static (string Module, string DeclPath) SplitContainer(BaseDecl? containingDecl)
    {
        switch (containingDecl)
        {
            case TypeDecl typeDecl:
                {
                    // SwiftTypeName.ModuleQualifiedName has the form "Module.TypeChain"
                    // (e.g. "TestModule.Loader.Payload"). Split off the leading
                    // module token so callers can compare module + decl-path
                    // independently.
                    var qualified = typeDecl.SwiftTypeName.ModuleQualifiedName;
                    var firstDot = qualified.IndexOf('.');
                    return firstDot < 0
                        ? (qualified, string.Empty)
                        : (qualified.Substring(0, firstDot), qualified.Substring(firstDot + 1));
                }
            case ModuleDecl moduleDecl:
                return (moduleDecl.Name, string.Empty);
            case null:
                return (string.Empty, string.Empty);
            default:
                {
                    var moduleName = containingDecl.ModuleDecl?.Name ?? string.Empty;
                    return (moduleName, containingDecl.Name);
                }
        }
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
