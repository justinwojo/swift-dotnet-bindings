// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Emits <c>// Unsupported: {name} — {reason}</c> comments into generated C# files
/// when types or members are skipped during binding generation.
/// </summary>
public static class UnsupportedCommentEmitter
{
    /// <summary>
    /// Emits an unsupported comment for a skipped type.
    /// </summary>
    /// <param name="declId">
    /// Identity of the dropped type, recorded alongside the comment so a consumer can correlate the
    /// drop with the declaration across regenerations. Every production call site has the
    /// <see cref="TypeDecl"/> in scope and passes <c>DeclIdFactory.ForType(decl)</c>; the name-only
    /// fallback below exists for tests and ad-hoc callers and is strictly weaker (no module, no
    /// containing-type chain), so a new call site that omits it silently degrades the join key.
    /// Purely additive — the emitted comment text does not depend on it.
    /// </param>
    public static void EmitTypeSkipped(
        CSharpWriter csWriter,
        string typeName,
        SkipReason reason,
        string? details = null,
        DeclId? declId = null)
    {
        var description = WorkaroundRecommendations.GetDescription(reason) ?? reason.ToString();
        var comment = $"// Unsupported: type '{typeName}' — {description}";
        if (!string.IsNullOrWhiteSpace(details))
            comment += $" ({details})";
        csWriter.WriteLine(comment);
        // Finding 53: a comment-drop is a degradation — surface it loudly (SWIFTBIND025) via the
        // ambient collector. Strip the leading "// " so the diagnostic reads cleanly.
        ReportCollector.RecordUnsupportedCommentDrop(
            comment.Substring(3),
            declId ?? DeclId.Create(module: null, declPath: null, BindingItemKind.Type, typeName));
    }

    /// <summary>
    /// Emits an unsupported comment for a skipped member (method, property, operator, subscript).
    /// </summary>
    /// <param name="containingDecl">
    /// The member's parent declaration (typically <c>memberDecl.ParentDecl</c>), used to qualify the
    /// member by its FULL declaring-type path (<c>Outer.Inner.member</c>) in both the emitted comment
    /// and the SWIFTBIND025 dedup/display key (Finding 53). The dedup key is the comment text, and a
    /// member's simple name is not unique across types — two distinct types each dropping a same-named
    /// member for the same reason would otherwise collapse to ONE SWIFTBIND025 entry, under-counting
    /// drops in a diagnostic whose entire purpose is "never silent". The full nesting path (not just
    /// the leaf type name) is used so a member of <c>A.Inner</c> stays distinct from one of
    /// <c>B.Inner</c>. A <see cref="ModuleDecl"/> ancestor terminates the walk (it is the module, not a
    /// declaring type): a module-level free function therefore stays unqualified, as does a
    /// <c>null</c> parent. Same-type overloads still share one entry — their comment text is identical
    /// because parameter types are not part of it; the per-overload accounting lives in the structured
    /// <c>skippedItems</c> report channel, so the loud comment channel intentionally summarizes them.
    /// </param>
    /// <param name="declId">
    /// Identity of the dropped member. The default is the coarse <c>(kind, name, containing decl)</c>
    /// id below, and for this channel that is the RIGHT granularity, not a shortfall: a row here is
    /// keyed by its comment text, which omits parameter types, so one row covers every same-named
    /// overload dropped for the same reason. The coarse id describes exactly that group; a precise
    /// per-overload id would name one arbitrary member of it and imply the siblings were fine.
    /// Per-overload accounting is the structured <c>skippedItems</c> channel's job. Pass an explicit
    /// id only when the call site knows its comment text is already unique to one declaration.
    /// Purely additive — the emitted comment text does not depend on it.
    /// </param>
    public static void EmitMemberSkipped(CSharpWriter csWriter, string memberName, BindingItemKind kind, SkipReason reason, string? details = null, BaseDecl? containingDecl = null, DeclId? declId = null)
    {
        var description = WorkaroundRecommendations.GetDescription(reason) ?? reason.ToString();
        var kindLabel = kind.ToString().ToLowerInvariant();
        var typePath = BuildContainingTypePath(containingDecl);
        var qualifiedName = typePath is null ? memberName : $"{typePath}.{memberName}";
        var comment = $"// Unsupported: {kindLabel} '{qualifiedName}' — {description}";
        if (!string.IsNullOrWhiteSpace(details))
            comment += $" ({details})";
        csWriter.WriteLine(comment);
        // Finding 53: a comment-drop is a degradation — surface it loudly (SWIFTBIND025) via the
        // ambient collector. Strip the leading "// " so the diagnostic reads cleanly. The qualified
        // name keeps the dedup key distinct per declaring type.
        ReportCollector.RecordUnsupportedCommentDrop(
            comment.Substring(3),
            declId ?? DeclIdFactory.ForMember(kind, memberName, containingDecl));
    }

    /// <summary>
    /// Builds the full declaring-type path for a dropped member (innermost type last, e.g.
    /// <c>Outer.Inner</c>) by walking <paramref name="containingDecl"/> up its
    /// <see cref="BaseDecl.ParentDecl"/> chain until a <see cref="ModuleDecl"/> or <c>null</c>
    /// terminates it. Returns <c>null</c> when there is no declaring type (a module-level free
    /// function or a null parent), so the caller leaves the member unqualified.
    /// </summary>
    private static string? BuildContainingTypePath(BaseDecl? containingDecl)
    {
        var names = new List<string>();
        for (var d = containingDecl; d is not null and not ModuleDecl; d = d.ParentDecl)
            names.Add(d.Name);
        if (names.Count == 0)
            return null;
        names.Reverse();
        return string.Join(".", names);
    }
}
