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
    public static void EmitTypeSkipped(CSharpWriter csWriter, string typeName, SkipReason reason, string? details = null)
    {
        var description = WorkaroundRecommendations.GetDescription(reason) ?? reason.ToString();
        var comment = $"// Unsupported: type '{typeName}' — {description}";
        if (!string.IsNullOrWhiteSpace(details))
            comment += $" ({details})";
        csWriter.WriteLine(comment);
    }

    /// <summary>
    /// Emits an unsupported comment for a skipped member (method, property, operator, subscript).
    /// </summary>
    public static void EmitMemberSkipped(CSharpWriter csWriter, string memberName, BindingItemKind kind, SkipReason reason, string? details = null)
    {
        var description = WorkaroundRecommendations.GetDescription(reason) ?? reason.ToString();
        var kindLabel = kind.ToString().ToLowerInvariant();
        var comment = $"// Unsupported: {kindLabel} '{memberName}' — {description}";
        if (!string.IsNullOrWhiteSpace(details))
            comment += $" ({details})";
        csWriter.WriteLine(comment);
    }
}
