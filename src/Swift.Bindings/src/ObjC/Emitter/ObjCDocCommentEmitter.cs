// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Text;

namespace BindingsGeneration.ObjC;

/// <summary>
/// Shared helper for emitting XML doc comments as /// summary/param tags.
/// </summary>
public static class ObjCDocCommentEmitter
{
    public static void EmitDocComment(StringBuilder sb, string? docComment, List<ObjCDocParam>? docParams, string indent)
    {
        if (string.IsNullOrEmpty(docComment) && (docParams == null || docParams.Count == 0))
            return;

        if (!string.IsNullOrEmpty(docComment))
        {
            sb.AppendLine($"{indent}/// <summary>");
            sb.AppendLine($"{indent}/// {EscapeXml(docComment)}");
            sb.AppendLine($"{indent}/// </summary>");
        }

        if (docParams != null)
        {
            foreach (var param in docParams)
            {
                var escapedName = EscapeXml(param.Name).Replace("\"", "&quot;");
                sb.AppendLine($"{indent}/// <param name=\"{escapedName}\">{EscapeXml(param.Description)}</param>");
            }
        }
    }

    private static string EscapeXml(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
