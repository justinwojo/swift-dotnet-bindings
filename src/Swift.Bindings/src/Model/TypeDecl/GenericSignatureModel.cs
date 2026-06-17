// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// The kind of a single generic-signature requirement clause.
/// </summary>
public enum GenericRequirementKind
{
    /// <summary>A conformance/superclass clause written with <c>:</c> (e.g. <c>τ_0_0 : Swift.Equatable</c>).</summary>
    Conformance,

    /// <summary>A same-type clause written with <c>==</c> (e.g. <c>τ_0_0.Element == Swift.String</c>).</summary>
    SameType,
}

/// <summary>
/// One requirement clause parsed from a Swift generic signature, captured verbatim.
///
/// <para>
/// This is the single faithful representation of a generic-signature constraint. Unlike
/// <see cref="GenericParameterConformance"/> (which the representable-conformance path in
/// <see cref="GenericSignatureParser.ParseGenericSignature"/> deliberately drops for marker /
/// constructed-generic / unqualified targets so an unrepresentable constraint never tears down
/// the enclosing decl), a <see cref="GenericRequirement"/> drops nothing: every clause survives
/// with its target text intact. That makes it safe to drive predicates that previously
/// hand-scanned the raw signature string (Finding 19), including verbatim re-emission of a Swift
/// <c>where</c> clause.
/// </para>
/// </summary>
/// <param name="Subject">
/// The left-hand-side dotted path, split on <c>.</c>. A direct constraint on the parameter is a
/// single segment (<c>["τ_0_0"]</c>); a constrained-extension associated-type clause carries the
/// member path (<c>["τ_0_0", "Element"]</c>, <c>["Self", "Element"]</c>).
/// </param>
/// <param name="Target">
/// The right-hand-side target, verbatim and untrimmed of inner structure — e.g. <c>AnyObject</c>,
/// <c>Swift.Sendable</c>, <c>SwiftBindingsTestLib.BaseRule</c>, <c>KeyPath&lt;Intent, Parameter&gt;</c>,
/// or a generic-parameter placeholder such as <c>τ_1_0.Element</c>.
/// </param>
/// <param name="Kind">Whether the clause is a conformance (<c>:</c>) or same-type (<c>==</c>) requirement.</param>
public sealed record GenericRequirement(
    IReadOnlyList<string> Subject,
    string Target,
    GenericRequirementKind Kind)
{
    /// <summary>The root generic parameter the clause constrains (e.g. <c>τ_0_0</c> or <c>Self</c>).</summary>
    public string SubjectRoot => Subject.Count > 0 ? Subject[0] : string.Empty;

    /// <summary>
    /// True when the clause constrains the parameter itself, not one of its associated-type members
    /// (i.e. a single-segment <see cref="Subject"/>). Direct constraints are what the legacy
    /// <c>τ_0_0 :</c> / <c>τ_0_0 ==</c> marker scans matched; member clauses (<c>τ_0_0.Element :</c>)
    /// were excluded because the marker required the operator immediately after the bare parameter.
    /// </summary>
    public bool IsDirect => Subject.Count == 1;

    /// <summary>The associated-type member path after the root (e.g. <c>Element</c>, <c>UTF8View.Index</c>), empty for a direct clause.</summary>
    public string MemberPath => Subject.Count > 1 ? string.Join(".", Subject.Skip(1)) : string.Empty;

    /// <summary>
    /// The simple (unqualified) name of <see cref="Target"/>: the final dotted segment of the
    /// target's head, before any generic-argument list. <c>Swift.Sendable</c> → <c>Sendable</c>,
    /// <c>KeyPath&lt;A, B&gt;</c> → <c>KeyPath</c>, <c>AnyObject</c> → <c>AnyObject</c>.
    /// </summary>
    public string TargetSimpleName
    {
        get
        {
            var t = Target;
            var lt = t.IndexOf('<');
            var head = lt >= 0 ? t[..lt] : t;
            var dot = head.LastIndexOf('.');
            return dot >= 0 ? head[(dot + 1)..] : head;
        }
    }
}

/// <summary>
/// The structured form of a Swift generic signature: its parameter list and the full set of
/// requirement clauses (conformance and same-type), parsed once by
/// <see cref="GenericSignatureParser.ParseSignature"/>.
///
/// <para>
/// Finding 19: before this existed, at least six call sites each re-implemented their own
/// hand-parse of the raw <c>genericSig</c> string — substring marker scans (<c>"τ_0_0 : "</c>),
/// bespoke regexes (<c>τ_0_\d+\s*==</c>), and ad-hoc <c>where</c>-section splits — to answer
/// questions like "is this protocol class-bound?", "does this method add a same-type constraint on
/// a parent param?", or "what parent protocols does this protocol inherit?". Those grammars drifted
/// apart (some honoured top-level commas, some did not; some read the param section, some only the
/// <c>where</c> section). This model is the one grammar they all query.
/// </para>
///
/// <para>
/// The parser reads constraints from BOTH the parameter section and the <c>where</c> section,
/// because Swift's api-digester renders protocol inheritance inline (<c>&lt;τ_0_0 : AnyObject&gt;</c>,
/// no <c>where</c>) while method/function constraints use a <c>where</c> clause
/// (<c>&lt;τ_0_0 where τ_0_0 : Swift.Equatable&gt;</c>). Both are the same kind of requirement.
/// </para>
/// </summary>
public sealed record GenericSignatureModel(
    IReadOnlyList<string> Parameters,
    IReadOnlyList<GenericRequirement> Requirements)
{
    /// <summary>An empty signature (no parameters, no requirements). Returned for null/blank input.</summary>
    public static readonly GenericSignatureModel Empty =
        new(Array.Empty<string>(), Array.Empty<GenericRequirement>());

    /// <summary>
    /// The verbatim conformance targets of every DIRECT constraint whose root is one of
    /// <paramref name="roots"/> (matched ordinally). This reproduces the legacy
    /// <c>ExtractConstraints(sig, "τ_0_0 : ")</c> / <c>"Self : "</c> marker scans: direct
    /// conformance clauses only, member clauses excluded.
    /// </summary>
    public IEnumerable<string> DirectConformanceTargets(params string[] roots)
    {
        foreach (var r in Requirements)
        {
            if (r.Kind != GenericRequirementKind.Conformance || !r.IsDirect)
                continue;
            for (int i = 0; i < roots.Length; i++)
            {
                if (string.Equals(r.SubjectRoot, roots[i], StringComparison.Ordinal))
                {
                    yield return r.Target;
                    break;
                }
            }
        }
    }
}
