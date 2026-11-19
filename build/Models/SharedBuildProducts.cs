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
/// target's project tree then imports <see cref="ConsumerPropsContent"/> and
/// <see cref="ConsumerTargetsContent"/> (a <c>Directory.Build.props</c> and <c>Directory.Build.targets</c> at
/// the root of the validate output), which make the shared projects read-only inputs of that build and
/// nothing else: a reference to a shared project resolves through <c>GetTargetPath</c> to the prebuilt
/// output instead of building it, and restore covers the generated project and its own references (a
/// mixed binding's ObjC companion, which lives in the target's output and is that target's alone) but
/// not the shared ones. <see cref="Snapshot"/> and <see cref="Diff"/> are the tripwire that proves it: any
/// file under a shared project's <c>bin</c> or <c>obj</c> that changes between the prebuild and the end
/// of the fan-out fails the run.
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
    /// <c>Directory.Build.props</c> for the root of a parallel validate's output tree: restore covers only
    /// the projects a build names, never the shared projects behind them. MSBuild imports the nearest one
    /// above each generated project, so it reaches every per-target build (the generator's verification
    /// build and the compile gate's).
    /// </summary>
    public const string ConsumerPropsContent =
        """
        <Project>
          <!-- Written by nuke validate. The shared in-tree projects (Swift.Runtime, SwiftBindings.Apple,
               Swift.Analyzers, SwiftBindings.Tasks) are restored and built once before the parallel
               fan-out. Restore below this directory covers only the projects a build names (see the
               Directory.Build.targets beside this file), so no two targets rewrite a shared project's
               restore outputs. -->
          <PropertyGroup>
            <RestoreRecursive>false</RestoreRecursive>
          </PropertyGroup>
        </Project>

        """;

    /// <summary>
    /// <c>Directory.Build.targets</c> for the same root. A reference to one of
    /// <paramref name="sharedProjectDirectories"/> (full paths) resolves through <c>GetTargetPath</c>, so
    /// the build reads the prebuilt output without building it; every other reference builds as usual,
    /// and is added to the referencing project's restore, which the props otherwise keep to the project
    /// itself.
    /// </summary>
    public static string ConsumerTargetsContent(IEnumerable<string> sharedProjectDirectories)
    {
        var isShared = string.Join(" or ", sharedProjectDirectories.Select(directory =>
            $"'%(RootDir)%(Directory)' == '{EscapeForCondition(Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + "/")}'"));
        return $"""
            <Project>
              <!-- Written by nuke validate: see the Directory.Build.props beside this file. -->
              <Target Name="_ValidateConsumePrebuiltSharedProjects" BeforeTargets="ResolveProjectReferences">
                <ItemGroup>
                  <_MSBuildProjectReferenceExistent Condition="{isShared}">
                    <Targets>GetTargetPath</Targets>
                  </_MSBuildProjectReferenceExistent>
                </ItemGroup>
              </Target>
              <Target Name="_ValidateRestoreOwnReferences" AfterTargets="_FilterRestoreGraphProjectInputItems">
                <ItemGroup>
                  <_ValidateOwnReference Include="@(ProjectReference->'%(FullPath)')" />
                  <_ValidateOwnReference Remove="@(_ValidateOwnReference)" Condition="{isShared}" />
                  <FilteredRestoreGraphProjectInputItems Include="@(_ValidateOwnReference)" />
                </ItemGroup>
              </Target>
            </Project>

            """;
    }

    // MSBuild unescapes %XX in conditions, so a path character the condition or the XML would read
    // (a quote, a percent sign, a property or item sigil, an ampersand or angle bracket) goes in escaped.
    private static string EscapeForCondition(string value) =>
        string.Concat(value.Select(c => "%$@'();?*&<>\"".Contains(c) ? $"%{(int)c:X2}" : c.ToString()));

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
