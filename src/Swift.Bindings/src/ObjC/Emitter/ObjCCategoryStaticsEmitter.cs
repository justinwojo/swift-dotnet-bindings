// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Text;
using Microsoft.Extensions.Logging;

namespace BindingsGeneration.ObjC;

/// <summary>
/// One true-static overload of a category CLASS method, to be written into the generated static
/// class as an extra partial part. It forwards to the member bgen produced from the
/// <c>[Static]</c> declaration in ApiDefinition.cs, supplying the receiver bgen insists on.
/// </summary>
public sealed record ObjCCategoryStaticForwarder
{
    /// <summary>Name of the static class bgen generates for the category — the partial this extends.</summary>
    public required string DeclaringClassName { get; init; }

    /// <summary>C# name of the bgen member, which the overload reuses.</summary>
    public required string MethodName { get; init; }

    /// <summary>Mapped C# return type (<c>void</c> for a void selector).</summary>
    public required string ReturnType { get; init; }

    /// <summary>Type the receiver parameter is declared as, i.e. the categorized class.</summary>
    public required string ReceiverType { get; init; }

    /// <summary>Declared parameter list of the overload, already formatted, receiver excluded.</summary>
    public required IReadOnlyList<string> SignatureParts { get; init; }

    /// <summary>Argument list forwarded to the bgen member, receiver excluded.</summary>
    public required IReadOnlyList<string> CallArguments { get; init; }

    /// <summary>ObjC selector this overload ultimately invokes, for the generated doc comment.</summary>
    public required string Selector { get; init; }

    /// <summary>
    /// Availability recovered for the selector, re-emitted on the overload so the member consumers
    /// call carries the same platform floor as the member it forwards to.
    /// </summary>
    public required IReadOnlyList<ObjCAvailability> Availability { get; init; }
}

/// <summary>
/// Writes <c>ObjCCategoryStatics.cs</c>: extra partial parts of the static classes bgen generates
/// from <c>[Category]</c> interfaces, giving every class (<c>+</c>) member of a category a genuinely
/// static C# overload.
///
/// bgen compiles a <c>[Category]</c> interface into a static extension class and gives EVERY member
/// a leading receiver parameter, <c>[Static]</c> included. A class member so compiled still
/// dispatches on the class handle and only keeps the receiver alive, so the call is correct — but a
/// consumer has to reach a factory through an arbitrary instance of the very type the factory
/// exists to produce. The overload here supplies no receiver, which is what the ObjC declaration
/// says.
///
/// The file is a plain compile item, deliberately NOT a bgen input: it extends classes bgen has not
/// generated yet at api-definition-contract-compile time, so feeding it to bgen would fail that
/// compile with an unresolved type.
/// </summary>
public static class ObjCCategoryStaticsEmitter
{
    /// <summary>Fixed file name; the SDK targets and the emitted companion csproj both key on it.</summary>
    public const string FileName = "ObjCCategoryStatics.cs";

    /// <summary>
    /// Writes the forwarders file, or removes a stale one when there is nothing to write. Removal
    /// matters on the SDK's incremental intermediate directory: a file left over from a previous
    /// generate would extend a partial class the current ApiDefinition no longer declares.
    /// Returns the written path, or null when no file is needed.
    /// </summary>
    public static string? Emit(
        IReadOnlyList<ObjCCategoryStaticForwarder> forwarders,
        string outputDir,
        string resolvedNamespace,
        PlatformInfo? platformInfo,
        IReadOnlySet<string> referencedAppleNamespaces,
        ILogger logger)
    {
        Directory.CreateDirectory(outputDir);
        var filePath = Path.Combine(outputDir, FileName);

        if (forwarders.Count == 0)
        {
            if (File.Exists(filePath))
                File.Delete(filePath);
            return null;
        }

        var sb = new StringBuilder();
        ObjCUsingsEmitter.EmitCompanionPartialClassHeader(sb, platformInfo, referencedAppleNamespaces);
        sb.AppendLine();
        sb.AppendLine($"namespace {resolvedNamespace}");
        sb.AppendLine("{");

        var first = true;
        foreach (var group in forwarders.GroupBy(f => f.DeclaringClassName, StringComparer.Ordinal))
        {
            if (!first)
                sb.AppendLine();
            first = false;

            // `static` because bgen declares the category class static; a partial type is static if
            // any part says so, and stating it here keeps the two parts reading the same.
            sb.AppendLine($"    public static partial class {group.Key}");
            sb.AppendLine("    {");

            var firstMember = true;
            foreach (var forwarder in group)
            {
                if (!firstMember)
                    sb.AppendLine();
                firstMember = false;
                EmitForwarder(sb, forwarder);
            }

            sb.AppendLine("    }");
        }

        sb.AppendLine("}");

        File.WriteAllText(filePath, sb.ToString());
        logger.LogInformation("Wrote {FilePath}", filePath);
        return filePath;
    }

    private static void EmitForwarder(StringBuilder sb, ObjCCategoryStaticForwarder forwarder)
    {
        var signature = string.Join(", ", forwarder.SignatureParts);
        var arguments = string.Join(", ", new[] { $"({forwarder.ReceiverType})null!" }.Concat(forwarder.CallArguments));

        sb.AppendLine("        /// <summary>");
        sb.AppendLine($"        /// Invokes the class method <c>+{forwarder.Selector}</c> with no receiver.");
        sb.AppendLine("        /// </summary>");
        sb.AppendLine("        /// <remarks>");
        sb.AppendLine("        /// The sibling overload carries a receiver only because bgen gives every member of a");
        sb.AppendLine("        /// generated category class one. A class method dispatches on the class itself and never");
        sb.AppendLine("        /// reads that receiver, so this overload passes none.");
        sb.AppendLine("        /// </remarks>");
        ObjCAvailabilityEmitter.EmitAvailabilityAttributes(sb, forwarder.Availability, "        ");
        sb.AppendLine($"        public static {forwarder.ReturnType} {forwarder.MethodName}({signature})");
        // An expression body covers a void selector as well as a returning one: the forwarded call
        // is the whole member either way.
        sb.AppendLine($"            => {forwarder.MethodName}({arguments});");
    }
}
