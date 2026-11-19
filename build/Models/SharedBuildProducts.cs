// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

/// <summary>
/// The in-tree projects every generated binding's verification build references
/// (<c>Swift.Runtime</c>, <c>SwiftBindings.Apple</c> and what they reference: <c>Swift.Analyzers</c> and
/// <c>SwiftBindings.Tasks</c>), and the contract that lets a parallel validate share them.
///
/// The generator verifies each binding with a real <c>dotnet build</c> of the emitted csproj, which
/// ProjectReferences the in-tree runtime and supplement. Run once per target across a parallel fan-out,
/// each of those builds restored and rebuilt the same shared projects in place: its global properties
/// (<c>-p:TreatWarningsAsErrors=false</c>, a per-build <c>-p:ErrorLog</c>) differ from the prebuild's, so
/// restore is not a no-op, it rewrites the shared <c>nuget.g.props</c>, and every compile behind it re-runs.
/// Two targets then write the same <c>obj</c> dll at once (CS2012, "being used by another process").
///
/// The fix has two halves. The orchestrator builds the shared projects once, before the fan-out. Each
/// target's project tree then imports <see cref="ConsumerPropsContent"/> (a <c>Directory.Build.props</c>
/// at the root of the validate output), which makes the shared projects read-only inputs of that build:
/// <c>BuildProjectReferences=false</c> resolves each reference through <c>GetTargetPath</c> to the
/// prebuilt output instead of building it, and <c>RestoreRecursive=false</c> restores only the
/// generated project, not the referenced ones. <see cref="Snapshot"/> and <see cref="Diff"/> are the
/// tripwire that proves it: any file under a shared project's <c>bin</c> or <c>obj</c> that changes
/// between the prebuild and the end of the fan-out fails the run.
/// </summary>
public static class SharedBuildProducts
{
    /// <summary>The shared projects' directories, relative to the repository root.</summary>
    public static readonly IReadOnlyList<string> ProjectDirectories =
    [
        "src/Swift.Runtime/src",
        "src/Swift.Runtime/tasks",
        "src/Swift.Analyzers",
        "src/Swift.Bindings.Apple",
    ];

    /// <summary>
    /// <c>Directory.Build.props</c> for the root of a parallel validate's output tree. MSBuild imports
    /// the nearest one above each generated project, so every per-target build (the generator's
    /// verification build and the compile gate's) consumes the prebuilt shared projects without
    /// restoring or building them.
    /// </summary>
    public const string ConsumerPropsContent =
        """
        <Project>
          <!-- Written by nuke validate. The shared in-tree projects (Swift.Runtime, SwiftBindings.Apple,
               Swift.Analyzers, SwiftBindings.Tasks) are built once before the parallel fan-out. Every
               generated binding below consumes their prebuilt outputs: references resolve through
               GetTargetPath instead of building, and restore leaves the referenced projects alone, so no
               two targets ever write the same shared build product. -->
          <PropertyGroup>
            <BuildProjectReferences>false</BuildProjectReferences>
            <RestoreRecursive>false</RestoreRecursive>
          </PropertyGroup>
        </Project>

        """;

    /// <summary>One file's identity for the tripwire: size and last write time.</summary>
    public readonly record struct FileStamp(long Length, long LastWriteUtcTicks);

    /// <summary>
    /// Every file under <c>bin</c> and <c>obj</c> of each project directory, keyed by full path. A
    /// project directory without either is recorded as empty, so a later write there still shows.
    /// </summary>
    public static IReadOnlyDictionary<string, FileStamp> Snapshot(IEnumerable<string> projectDirectories)
    {
        var stamps = new SortedDictionary<string, FileStamp>(StringComparer.Ordinal);
        foreach (var project in projectDirectories)
        {
            foreach (var output in new[] { "bin", "obj" })
            {
                var root = Path.Combine(project, output);
                if (!Directory.Exists(root))
                    continue;
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    var info = new FileInfo(file);
                    stamps[info.FullName] = new FileStamp(info.Length, info.LastWriteTimeUtc.Ticks);
                }
            }
        }
        return stamps;
    }

    /// <summary>
    /// The files that were written, created or deleted between two snapshots, each with what happened,
    /// in path order. Empty when the fan-out left every shared build product as the prebuild wrote it.
    /// </summary>
    public static IReadOnlyList<string> Diff(
        IReadOnlyDictionary<string, FileStamp> before,
        IReadOnlyDictionary<string, FileStamp> after)
    {
        var changes = new List<string>();
        foreach (var path in before.Keys.Union(after.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var had = before.TryGetValue(path, out var old);
            var has = after.TryGetValue(path, out var now);
            if (had && has && old == now)
                continue;
            changes.Add((had, has) switch
            {
                (false, true) => $"created {path}",
                (true, false) => $"deleted {path}",
                _ => $"written {path}",
            });
        }
        return changes;
    }
}
