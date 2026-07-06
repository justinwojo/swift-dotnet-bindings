// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using BindingsGeneration.ObjC;

namespace BindingsGeneration;

/// <summary>
/// Bridges the ObjC binding pipeline's own diagnostics (<see cref="ObjCBindingDiagnostics"/> /
/// <see cref="ObjCSkippedSymbol"/>) into the shared reporting vocabulary (<see cref="SkippedItem"/> /
/// <see cref="SkipReason"/> / <see cref="BindingItemKind"/>). This is what lets a mixed or pure-ObjC
/// binding's dropped symbols fold into the same <c>SkipTriage</c> / <c>ReviewCount</c> gate as the
/// Swift surface, rather than being visible only in an INFO-level log line.
/// </summary>
public static class ObjCSkipProjection
{
    /// <summary>
    /// Maps an <see cref="ObjCSkipReason"/> to its 1:1 <see cref="SkipReason"/> counterpart. The
    /// mapping is total and deliberately explicit — a new <see cref="ObjCSkipReason"/> added without a
    /// counterpart throws here (and trips <c>ObjCSkipProjectionTests</c>), forcing the report vocabulary
    /// to grow alongside the ObjC diagnostics rather than silently collapsing an unmapped cause.
    /// </summary>
    public static SkipReason ToSkipReason(ObjCSkipReason reason) => reason switch
    {
        ObjCSkipReason.UnresolvableType => SkipReason.ObjCUnresolvableType,
        ObjCSkipReason.UnavailableApi => SkipReason.ObjCUnavailableApi,
        ObjCSkipReason.UnsupportedConstruct => SkipReason.ObjCUnsupportedConstruct,
        ObjCSkipReason.AccessibilityConflict => SkipReason.ObjCAccessibilityConflict,
        ObjCSkipReason.DuplicateSignature => SkipReason.ObjCDuplicateSignature,
        ObjCSkipReason.VariadicFunction => SkipReason.ObjCVariadicFunction,
        ObjCSkipReason.EmptyCategory => SkipReason.ObjCEmptyCategory,
        ObjCSkipReason.MissingNativeSymbol => SkipReason.ObjCMissingNativeSymbol,
        ObjCSkipReason.DuplicateSelector => SkipReason.ObjCDuplicateSelector,
        _ => throw new ArgumentOutOfRangeException(
            nameof(reason), reason, "Unmapped ObjCSkipReason — add its SkipReason counterpart."),
    };

    /// <summary>
    /// Maps the ObjC diagnostics' free-form <c>symbolKind</c> label (e.g. "class", "Method",
    /// "constant") to the report's coarse <see cref="BindingItemKind"/>. The label is a free string
    /// recorded at each drop site with inconsistent casing, so matching is case-insensitive and any
    /// unrecognized kind falls back to <see cref="BindingItemKind.Type"/> (the safe, most-common
    /// default for an ObjC declaration) — a best-effort label, not a correctness-critical value.
    /// </summary>
    public static BindingItemKind ToItemKind(string symbolKind) => symbolKind.ToLowerInvariant() switch
    {
        "method" or "function" or "initializer" or "init" => BindingItemKind.Method,
        "property" => BindingItemKind.Property,
        "constant" or "field" => BindingItemKind.Property,
        "subscript" => BindingItemKind.Subscript,
        _ => BindingItemKind.Type,
    };

    /// <summary>
    /// Projects one recorded ObjC skip into a <see cref="SkippedItem"/> carrying the mapped reason,
    /// kind, the recorded detail, and a matching workaround recommendation.
    /// </summary>
    public static SkippedItem ToSkippedItem(ObjCSkippedSymbol symbol)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        var reason = ToSkipReason(symbol.Reason);
        return new SkippedItem
        {
            Kind = ToItemKind(symbol.SymbolKind),
            Name = symbol.SymbolName,
            Reason = reason,
            Details = symbol.Detail,
            RecommendedWorkaround = WorkaroundRecommendations.GetRecommendation(reason),
        };
    }
}
