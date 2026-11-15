// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace SwiftBindings.TransitionGate;

/// <summary>
/// The residual-guard check over one managed assembly as a package ships it: one
/// <c>lib/&lt;framework&gt;/&lt;assembly&gt;</c> entry of a nupkg, read from the package bytes.
/// <see cref="SidecarProblems"/> is the inventory tripwire against the generator's guard sidecars
/// for that framework's build, when the package carries generated code.
/// </summary>
public sealed record PackedAssemblyResidual(
    string Package,
    string Entry,
    string Framework,
    AssemblyTransitionReport Report,
    ResidualGuardResult Result,
    ImmutableArray<string> SidecarProblems)
{
    /// <summary>The scan read every body, the check found no unguarded known-unsafe transition, and the sidecars agree.</summary>
    public bool Passed => Report.InspectionErrors.IsEmpty && Result.Passed && SidecarProblems.IsEmpty;
}

/// <summary>
/// The residual-guard check (<see cref="ResidualGuardCheck"/>) over what a pack actually ships, not
/// the compile-check build beside the BindingTests output: every <c>lib/&lt;framework&gt;/</c> copy of
/// an assembly in a nupkg, so a framework the compile check never builds (the supplement's macOS
/// build, the runtime's portable one) and the Release configuration a package carries are held to
/// the same rule. Each assembly is scanned with the package's own copy of its references
/// (<paramref name="references"/>, the same framework's entries of the packages it depends on).
/// </summary>
public static class PackedResidualCheck
{
    public static ImmutableArray<PackedAssemblyResidual> Check(
        string nupkg,
        string assemblyFileName,
        Func<string, IReadOnlyList<byte[]>> references,
        Func<string, string?> sidecarDirectory,
        IReadOnlyCollection<string>? handWrittenTypes = null)
    {
        var results = new List<PackedAssemblyResidual>();
        foreach (var (framework, entry, image) in ReadLibEntries(nupkg, assemblyFileName))
        {
            var report = SwiftTransitionScanner.Scan(image, $"{Path.GetFileName(nupkg)}!{entry}", new SwiftTransitionScanOptions
            {
                ReferenceDirectories = new[] { System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory() },
                ReferenceImages = references(framework),
            });
            var result = ResidualGuardCheck.Check(image, report);
            var sidecars = sidecarDirectory(framework);
            var problems = sidecars is null
                ? ImmutableArray<string>.Empty
                : ResidualGuardCheck.CrossCheckSidecars(result, sidecars, handWrittenTypes ?? Array.Empty<string>());
            results.Add(new PackedAssemblyResidual(Path.GetFileName(nupkg), entry, framework, report, result, problems));
        }
        return results.ToImmutableArray();
    }

    /// <summary>Every <c>lib/&lt;framework&gt;/&lt;assemblyFileName&gt;</c> entry of a nupkg, with its framework folder name.</summary>
    public static IEnumerable<(string Framework, string Entry, byte[] Image)> ReadLibEntries(string nupkg, string assemblyFileName)
    {
        using var archive = ZipFile.OpenRead(nupkg);
        foreach (var entry in archive.Entries.OrderBy(e => e.FullName, StringComparer.Ordinal))
        {
            var parts = entry.FullName.Split('/');
            if (parts.Length != 3 || parts[0] != "lib" || !string.Equals(parts[2], assemblyFileName, StringComparison.Ordinal))
                continue;
            using var stream = entry.Open();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            yield return (parts[1], entry.FullName, buffer.ToArray());
        }
    }

    /// <summary>The target framework a package folder names, without its platform version: <c>net10.0-ios26.0</c> → <c>net10.0-ios</c>.</summary>
    public static string ProjectFramework(string packageFolder)
    {
        var dash = packageFolder.IndexOf('-');
        if (dash < 0)
            return packageFolder;
        var platform = packageFolder[(dash + 1)..].TrimEnd('.', '0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
        return packageFolder[..(dash + 1)] + platform;
    }

    /// <summary>A readable account of one packed assembly: the check's summary and every sidecar problem.</summary>
    public static string ToSummary(PackedAssemblyResidual residual)
    {
        var text = new StringBuilder();
        text.AppendLine($"[{residual.Package} {residual.Entry}] {(residual.Passed ? "PASSED" : "FAILED")}: " +
                        $"{residual.Report.Inventory.SwiftPInvokes} Swift-convention import(s), {residual.Report.Inventory.BridgeTwins} bridge twin(s)");
        text.Append(ResidualGuardCheck.ToSummary(residual.Result));
        foreach (var error in residual.Report.InspectionErrors)
            text.AppendLine($"  scan error {error.Member}: {error.Message}");
        foreach (var problem in residual.SidecarProblems)
            text.AppendLine($"  sidecar {problem}");
        return text.ToString();
    }
}
