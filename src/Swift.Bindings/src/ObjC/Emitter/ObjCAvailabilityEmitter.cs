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

/// <summary>
/// Shared helper for emitting availability attributes ([Introduced], [Deprecated], [Obsoleted], [Unavailable])
/// used by both ApiDefinitionEmitter and StructsAndEnumsEmitter.
/// </summary>
public static class ObjCAvailabilityEmitter
{
    /// <summary>
    /// Emits ObjCRuntime availability attributes for iOS platform.
    /// Returns true if the symbol is unavailable (caller should skip emission).
    /// </summary>
    public static bool EmitAvailabilityAttributes(StringBuilder sb, List<ObjCAvailability> availability, string indent)
    {
        // Pre-scan: if any iOS entry is unavailable, skip the entire symbol
        if (availability.Any(a => a.Platform == "ios" && a.IsUnavailable))
            return true;

        foreach (var avail in availability)
        {
            if (avail.Platform != "ios")
                continue;

            if (avail.IntroducedVersion != null)
            {
                var (major, minor) = ParseVersion(avail.IntroducedVersion);
                sb.AppendLine($"{indent}[Introduced(PlatformName.iOS, {major}, {minor})]");
            }

            if (avail.DeprecatedVersion != null)
            {
                var (major, minor) = ParseVersion(avail.DeprecatedVersion);
                var message = FormatMessage(avail.Message);
                sb.AppendLine($"{indent}[Deprecated(PlatformName.iOS, {major}, {minor}{message})]");
            }

            if (avail.ObsoletedVersion != null)
            {
                var (major, minor) = ParseVersion(avail.ObsoletedVersion);
                var message = FormatMessage(avail.Message);
                sb.AppendLine($"{indent}[Obsoleted(PlatformName.iOS, {major}, {minor}{message})]");
            }
        }

        return false;
    }

    internal static (int major, int minor) ParseVersion(string version)
    {
        var parts = version.Split('.');
        var major = int.Parse(parts[0]);
        var minor = parts.Length > 1 ? int.Parse(parts[1]) : 0;
        return (major, minor);
    }

    private static string FormatMessage(string? message)
    {
        if (string.IsNullOrEmpty(message))
            return "";
        var escaped = message.Replace("\"", "\\\"");
        return $", message: \"{escaped}\"";
    }
}
