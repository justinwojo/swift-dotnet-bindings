// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// The parallel validate's shared build products (<see cref="SharedBuildProducts"/>): the consumer props
/// that keep each per-target build from restoring or building the in-tree runtime and supplement, the
/// project list the tripwire watches, and the tripwire's diff.
/// </summary>
public class SharedBuildProductsTests
{
    [Fact]
    public void ConsumerProps_ResolveReferencesWithoutBuildingOrRestoringThem()
    {
        var properties = XDocument.Parse(SharedBuildProducts.ConsumerPropsContent).Root!
            .Elements("PropertyGroup").Elements()
            .ToDictionary(e => e.Name.LocalName, e => e.Value);

        Assert.Equal("false", properties["BuildProjectReferences"]);
        Assert.Equal("false", properties["RestoreRecursive"]);
    }

    [Fact]
    public void WatchedProjects_CoverTheWholeReferenceClosureOfTheRuntimeAndSupplement()
    {
        // The tripwire is only as wide as this list. Walk the ProjectReferences of the two projects a
        // generated binding references, transitively, and require every project directory reached to be
        // watched: a new reference added to the runtime would otherwise build in every target unseen.
        var root = RepoRoot;
        var watched = SharedBuildProducts.ProjectDirectories
            .Select(d => Path.GetFullPath(Path.Combine(root, d)))
            .ToHashSet(StringComparer.Ordinal);
        var pending = new Queue<string>(new[]
        {
            Path.Combine(root, "src", "Swift.Runtime", "src", "Swift.Runtime.csproj"),
            Path.Combine(root, "src", "Swift.Bindings.Apple", "Swift.Bindings.Apple.csproj"),
        });
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (pending.Count > 0)
        {
            var project = Path.GetFullPath(pending.Dequeue());
            if (!seen.Add(project))
                continue;
            Assert.Contains(Path.GetDirectoryName(project)!, watched);
            foreach (var reference in XDocument.Load(project).Descendants("ProjectReference"))
            {
                var include = reference.Attribute("Include")!.Value.Replace('\\', '/');
                pending.Enqueue(Path.Combine(Path.GetDirectoryName(project)!, include));
            }
        }

        Assert.True(seen.Count >= 4, $"reference walk reached only {seen.Count} project(s)");
    }

    [Fact]
    public void Diff_NamesEveryWrittenCreatedAndDeletedFile_AndNothingElse()
    {
        var before = new Dictionary<string, SharedBuildProducts.FileStamp>
        {
            ["/r/obj/a.dll"] = new(10, 100),
            ["/r/obj/project.assets.json"] = new(20, 100),
            ["/r/obj/same.cache"] = new(30, 100),
            ["/r/bin/gone.dll"] = new(40, 100),
        };
        var after = new Dictionary<string, SharedBuildProducts.FileStamp>
        {
            ["/r/obj/a.dll"] = new(10, 200),
            ["/r/obj/project.assets.json"] = new(21, 100),
            ["/r/obj/same.cache"] = new(30, 100),
            ["/r/obj/new.sarif"] = new(1, 300),
        };

        Assert.Equal(
            new[]
            {
                "deleted /r/bin/gone.dll",
                "written /r/obj/a.dll",
                "created /r/obj/new.sarif",
                "written /r/obj/project.assets.json",
            },
            SharedBuildProducts.Diff(before, after));
        Assert.Empty(SharedBuildProducts.Diff(before, before));
    }

    [Fact]
    public void Snapshot_ReadsOnlyBinAndObj_AndSeesARewrite()
    {
        var project = Path.Combine(Path.GetTempPath(), "shared-products-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(project, "obj", "Debug"));
            Directory.CreateDirectory(Path.Combine(project, "bin"));
            File.WriteAllText(Path.Combine(project, "obj", "Debug", "x.dll"), "one");
            File.WriteAllText(Path.Combine(project, "bin", "x.dll"), "one");
            File.WriteAllText(Path.Combine(project, "Source.cs"), "// not a build product");

            var before = SharedBuildProducts.Snapshot(new[] { project });
            Assert.Equal(2, before.Count);

            var rewritten = Path.Combine(project, "obj", "Debug", "x.dll");
            File.WriteAllText(rewritten, "two!");
            File.SetLastWriteTimeUtc(rewritten, DateTime.UtcNow.AddMinutes(1));
            File.WriteAllText(Path.Combine(project, "Source.cs"), "// edited, still not a build product");

            Assert.Equal(new[] { "written " + new FileInfo(rewritten).FullName },
                SharedBuildProducts.Diff(before, SharedBuildProducts.Snapshot(new[] { project })));
        }
        finally
        {
            Directory.Delete(project, recursive: true);
        }
    }

    private static string RepoRoot { get; } = LocateRepoRoot();

    private static string LocateRepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "BindingTests", "RuntimeTestsApp", "RuntimeTestsApp.csproj")))
                return directory.FullName;
        throw new InvalidOperationException("Could not locate the repository root from the test output directory.");
    }
}
