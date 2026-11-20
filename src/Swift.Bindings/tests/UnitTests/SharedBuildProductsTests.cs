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
/// and targets that keep each per-target build from restoring or building the in-tree runtime and
/// supplement while still building the target's own references, the project list the tripwire watches,
/// and the tripwire's diff.
/// </summary>
public class SharedBuildProductsTests
{
    [Fact]
    public void ConsumerProps_KeepRestoreToTheProjectsABuildNames()
    {
        var properties = XDocument.Parse(SharedBuildProducts.ConsumerPropsContent).Root!
            .Elements("PropertyGroup").Elements()
            .ToDictionary(e => e.Name.LocalName, e => e.Value);

        Assert.Equal("false", properties["RestoreRecursive"]);
        Assert.False(properties.ContainsKey("BuildProjectReferences"));
    }

    [Fact]
    public void ConsumerTargets_ReadTheSharedProjectPrebuilt_AndBuildTheTargetsOwnReference()
    {
        // The shape of a mixed binding under validate: the generated project references a shared
        // project (the runtime, prebuilt once) and its own ObjC companion, which only that target's
        // build produces. Built the way the generator's verification build runs it (different global
        // properties from the prebuild), the companion must restore and build, and the shared project's
        // bin and obj must not change.
        var root = Path.Combine(Path.GetTempPath(), "shared-products-msbuild-" + Guid.NewGuid().ToString("N"));
        try
        {
            var shared = Path.Combine(root, "repo", "Shared");
            var target = Path.Combine(root, "out", "Target");
            WriteLibrary(shared, "Shared", "public static class SharedApi { public static int Value => 1; }");
            WriteLibrary(Path.Combine(target, "Companion"), "Companion",
                "public static class CompanionApi { public static int Value => 2; }");
            WriteLibrary(target, "Binding",
                "public static class BindingApi { public static int Sum => SharedApi.Value + CompanionApi.Value; }",
                "../../repo/Shared/Shared.csproj", "Companion/Companion.csproj");

            var prebuild = RunDotnet($"build \"{Path.Combine(shared, "Shared.csproj")}\" -nologo");
            Assert.True(prebuild.ExitCode == 0, prebuild.Output);
            File.WriteAllText(Path.Combine(root, "out", "Directory.Build.props"), SharedBuildProducts.ConsumerPropsContent);
            File.WriteAllText(Path.Combine(root, "out", "Directory.Build.targets"),
                SharedBuildProducts.ConsumerTargetsContent(new[] { shared }));
            var before = SharedBuildProducts.Snapshot(new[] { shared });

            var build = RunDotnet($"build \"{Path.Combine(target, "Binding.csproj")}\" -p:TreatWarningsAsErrors=false -nologo");

            Assert.True(build.ExitCode == 0, build.Output);
            Assert.True(File.Exists(Path.Combine(target, "bin", "Debug", "net10.0", "Binding.dll")), build.Output);
            Assert.Empty(SharedBuildProducts.Diff(before, SharedBuildProducts.Snapshot(new[] { shared })));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static void WriteLibrary(string directory, string name, string source, params string[] references)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, name + ".cs"), source);
        var referenceItems = string.Concat(references.Select(r => $"<ProjectReference Include=\"{r}\" />"));
        File.WriteAllText(Path.Combine(directory, name + ".csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework>" +
            $"<EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup><ItemGroup><Compile Include=\"{name}.cs\" />" +
            $"{referenceItems}</ItemGroup></Project>");
    }

    private static (int ExitCode, string Output) RunDotnet(string arguments)
    {
        var start = new System.Diagnostics.ProcessStartInfo("dotnet", arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        // A build inside the test host must not join the host's build server or node reuse.
        start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        start.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
        using var process = System.Diagnostics.Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return (process.ExitCode, stdout.Result + stderr.Result);
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
