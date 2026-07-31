// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Text;
using Microsoft.Extensions.Logging;

namespace BindingsGeneration.ObjC;

/// <summary>
/// One array-taking public overload to be written into the generated partial class, forwarding to
/// the <c>[Internal]</c> pointer+count member bgen produced from the ApiDefinition.
/// </summary>
public sealed record ObjCArrayOverload
{
    /// <summary>C# name of the class bgen generates — the partial this overload extends.</summary>
    public required string DeclaringClassName { get; init; }

    /// <summary>Public method name the overload declares.</summary>
    public required string PublicName { get; init; }

    /// <summary>Name of the <c>[Internal]</c> pointer+count member to forward to.</summary>
    public required string InternalName { get; init; }

    /// <summary>Mapped C# return type (<c>void</c> for a void selector).</summary>
    public required string ReturnType { get; init; }

    public required bool IsStatic { get; init; }

    /// <summary>C# element type of the array parameter.</summary>
    public required string ElementType { get; init; }

    /// <summary>Declared parameter list of the public overload, already formatted.</summary>
    public required IReadOnlyList<string> SignatureParts { get; init; }

    /// <summary>Argument list passed to the internal member, already formatted.</summary>
    public required IReadOnlyList<string> CallArguments { get; init; }

    /// <summary>Escaped C# identifier of the array parameter, pinned at the call.</summary>
    public required string ArrayParameterName { get; init; }

    /// <summary>ObjC selector this overload ultimately invokes, for the generated doc comment.</summary>
    public required string Selector { get; init; }

    /// <summary>
    /// Availability recovered for the selector, re-emitted on the overload so the member consumers
    /// call carries the same platform floor as the internal member it forwards to.
    /// </summary>
    public required IReadOnlyList<ObjCAvailability> Availability { get; init; }
}

/// <summary>
/// Writes <c>ObjCArrayOverloads.cs</c>: hand-written-style partial-class extensions that give a
/// selector taking a C array of value types an array-shaped C# signature. bgen cannot marshal such a
/// parameter itself, so the ApiDefinition declares the selector once as an <c>[Internal]</c>
/// pointer+count member and the overload here pins a managed array and forwards to it — the same
/// division of labour dotnet/macios uses for the equivalent MapKit selectors.
///
/// The file is a plain compile item, deliberately NOT a bgen input: it references members bgen has
/// not generated yet at api-definition-contract-compile time, so feeding it to bgen would fail that
/// compile with an unresolved member.
/// </summary>
public static class ObjCArrayOverloadsEmitter
{
    /// <summary>Fixed file name; the SDK targets and the emitted companion csproj both key on it.</summary>
    public const string FileName = "ObjCArrayOverloads.cs";

    /// <summary>
    /// Writes the overloads file, or removes a stale one when there is nothing to write. Removal
    /// matters on the SDK's incremental intermediate directory: a file left over from a previous
    /// generate would reference internal members the current ApiDefinition no longer declares.
    /// Returns the written path, or null when no file is needed.
    /// </summary>
    public static string? Emit(
        IReadOnlyList<ObjCArrayOverload> overloads,
        string outputDir,
        string resolvedNamespace,
        PlatformInfo? platformInfo,
        IReadOnlySet<string> referencedAppleNamespaces,
        ILogger logger)
    {
        Directory.CreateDirectory(outputDir);
        var filePath = Path.Combine(outputDir, FileName);

        if (overloads.Count == 0)
        {
            if (File.Exists(filePath))
                File.Delete(filePath);
            return null;
        }

        var sb = new StringBuilder();
        ObjCUsingsEmitter.EmitArrayOverloadsHeader(sb, platformInfo, referencedAppleNamespaces);
        sb.AppendLine();
        sb.AppendLine($"namespace {resolvedNamespace}");
        sb.AppendLine("{");

        var first = true;
        foreach (var group in overloads.GroupBy(o => o.DeclaringClassName, StringComparer.Ordinal))
        {
            if (!first)
                sb.AppendLine();
            first = false;

            // `unsafe` is required for the `fixed` pin below; bgen declares the same class `unsafe`
            // too, and the modifier is per-part so the two agree either way.
            sb.AppendLine($"    public unsafe partial class {group.Key}");
            sb.AppendLine("    {");

            var firstMember = true;
            foreach (var overload in group)
            {
                if (!firstMember)
                    sb.AppendLine();
                firstMember = false;
                EmitOverload(sb, overload);
            }

            sb.AppendLine("    }");
        }

        sb.AppendLine("}");

        File.WriteAllText(filePath, sb.ToString());
        logger.LogInformation("Wrote {FilePath}", filePath);
        return filePath;
    }

    private static void EmitOverload(StringBuilder sb, ObjCArrayOverload overload)
    {
        var staticModifier = overload.IsStatic ? "static " : "";
        var signature = string.Join(", ", overload.SignatureParts);
        var arguments = string.Join(", ", overload.CallArguments);

        sb.AppendLine("        /// <summary>");
        sb.AppendLine($"        /// Invokes <c>{overload.Selector}</c>, passing the array's elements as the");
        sb.AppendLine("        /// contiguous buffer the selector expects and its length as the element count.");
        sb.AppendLine("        /// </summary>");
        ObjCAvailabilityEmitter.EmitAvailabilityAttributes(sb, overload.Availability, "        ");
        sb.AppendLine($"        public {staticModifier}{overload.ReturnType} {overload.PublicName}({signature})");
        sb.AppendLine("        {");
        // Pinning a null or empty array yields a null pointer, which is what a zero count means to
        // the callee — so no null branch is needed here.
        sb.AppendLine($"            fixed ({overload.ElementType}* {PinnedPointerName} = {overload.ArrayParameterName})");
        sb.AppendLine("            {");
        var call = $"{overload.InternalName}({arguments});";
        sb.AppendLine(overload.ReturnType == "void"
            ? $"                {call}"
            : $"                return {call}");
        sb.AppendLine("            }");
        sb.AppendLine("        }");
    }

    /// <summary>
    /// Local name of the pinned pointer. Double-underscored so it cannot collide with a parameter
    /// name derived from an ObjC selector keyword.
    /// </summary>
    public const string PinnedPointerName = "__arrayPtr";
}
