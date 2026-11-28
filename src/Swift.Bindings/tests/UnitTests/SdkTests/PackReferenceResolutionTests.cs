// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security;
using System.Text.Json;
using System.Xml.Linq;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// How the pack-time transition check (SWIFTBIND133) and the package merge find the references a
/// packed assembly resolves against: read where they are, never by building. Each test runs real
/// <c>dotnet</c> commands on a binding-shaped project with a <c>ProjectReference</c>, importing the
/// SDK's own pack-check and merge targets (or the runtime's standalone pack check). A target hooked
/// before <c>ResolveProjectReferences</c> stands for the SDK's generate, wrapper and sibling pre-build
/// hooks, and records that it ran.
/// </summary>
public sealed class PackReferenceResolutionTests : IDisposable
{
    private static readonly string TasksDll = Path.Combine(AppContext.BaseDirectory, "SwiftBindings.Tasks.dll");
    private static readonly string RuntimeDll = Path.Combine(AppContext.BaseDirectory, "Swift.Runtime.dll");

    private readonly string root = Directory.CreateTempSubdirectory("swift-pack-references-").FullName;

    public void Dispose() => Directory.Delete(root, recursive: true);

    private string Binding => Path.Combine(root, "Binding", "Binding.csproj");
    private string Sibling => Path.Combine(root, "Sibling", "Sibling.csproj");
    private string AutoSibling => Path.Combine(root, "AutoSibling", "AutoSibling.csproj");
    private string HookMarker => Path.Combine(root, "hook-ran");
    private string SiblingOutput(string configuration, string project = "Sibling") =>
        Path.Combine(root, project, "bin", configuration, "net10.0") + Path.DirectorySeparatorChar;

    public enum PackCheck { Sdk, StandaloneRuntime }

    [Theory]
    [InlineData(PackCheck.Sdk, true)]
    [InlineData(PackCheck.Sdk, false)]
    [InlineData(PackCheck.StandaloneRuntime, true)]
    [InlineData(PackCheck.StandaloneRuntime, false)]
    public void Pack_OfABindingWithAProjectReference_ChecksThePackageWithAndWithoutTheNoBuildSwitch(PackCheck check, bool noBuild)
    {
        WriteProjects(check);
        var output = Path.Combine(root, "packages");
        if (noBuild)
            Run("build", Binding, "-c", "Release");
        var (exit, log) = Dotnet(noBuild
            ? ["pack", Binding, "--no-build", "-c", "Release", "-o", output, "-v:n"]
            : ["pack", Binding, "-c", "Release", "-o", output, "-v:n"]);
        Assert.True(exit == 0, log);
        Assert.DoesNotContain("NETSDK1085", log);
        Assert.True(File.Exists(Path.Combine(output, "Binding.1.0.0.nupkg")), log);
        // The check read the package it wrote.
        Assert.Contains("Swift transition check of Binding.1.0.0.nupkg", log);
    }

    [Fact]
    public void ReferenceDirectories_NameTheRuntimeAndEachProjectReferenceOutput_WithoutBuildingAnything()
    {
        WriteProjects(PackCheck.Sdk, otherReferences: true);
        Run("restore", Binding);
        var (exit, log) = Dotnet(["msbuild", Binding, "-nologo", "-p:TargetFramework=net10.0", "-p:BuildProjectReferences=false",
            "-t:_SwiftTransitionPackReferenceDirectories", "-getTargetResult:_SwiftTransitionPackReferenceDirectories"]);
        Assert.True(exit == 0, log);
        var directories = FirstJson(log)
            .GetProperty("TargetResults").GetProperty("_SwiftTransitionPackReferenceDirectories").GetProperty("Items")
            .EnumerateArray().Select(i => i.GetProperty("Identity").GetString()!).ToList();
        Assert.Contains(Path.GetDirectoryName(RuntimeDll) + Path.DirectorySeparatorChar, directories);
        Assert.Contains(SiblingOutput("Debug"), directories);
        Assert.Contains(SiblingOutput("Debug", "AutoSibling"), directories);
        // A package's compile assets, read from the restored package folder (the runtime arrives this way).
        Assert.Contains(Path.Combine(root, "package-cache", "carrier", "1.0.0", "lib", "net10.0") + Path.DirectorySeparatorChar, directories);
        // Assemblies referenced by a path relative to the project, as the reference or its HintPath.
        Assert.Contains(Path.Combine(root, "hinted") + Path.DirectorySeparatorChar, directories);
        Assert.Contains(Path.Combine(root, "pathed") + Path.DirectorySeparatorChar, directories);
        // Named, not built: the sibling has no output, and no hook before ResolveProjectReferences ran.
        Assert.False(Directory.Exists(Path.Combine(root, "Sibling", "bin")), "resolving the references built the ProjectReference");
        Assert.False(File.Exists(HookMarker), "resolving the references ran the hooks before ResolveProjectReferences");
    }

    [Fact]
    public void Merge_ResolvesItsReferencesWithoutBuildingTheProjectOrItsReferences()
    {
        WriteProjects(PackCheck.Sdk);
        // The sibling as `dotnet pack` of the binding leaves it: built in Release.
        Run("build", Sibling, "-c", "Release");
        var missing = Path.Combine(root, "contributions", "Binding.1.0.0.nupkg");
        var (exit, log) = Dotnet(["msbuild", Binding, "-restore", "-nologo", "-nodeReuse:false", "-t:SwiftBindingsMergePackages",
            $"-p:SwiftBindingsMergeContributions={missing}", "-p:SwiftBindingsMergeTargetFrameworks=net10.0",
            $"-p:SwiftBindingsMergeOutput={Path.Combine(root, "merged")}", "-getItem:_SwiftBindingsMergeReference"]);
        // The merge reaches its own refusal of the missing contribution...
        Assert.NotEqual(0, exit);
        Assert.Contains("SWIFTBIND138", log);
        Assert.Contains($"The contribution {missing} does not exist.", log);
        // ...with the references read where the packs left them...
        var directories = FirstJson(log)
            .GetProperty("Items").GetProperty("_SwiftBindingsMergeReference")
            .EnumerateArray().Select(i => i.GetProperty("Identity").GetString()!).ToList();
        Assert.Contains(Path.GetDirectoryName(RuntimeDll) + Path.DirectorySeparatorChar, directories);
        Assert.Contains(SiblingOutput("Release"), directories);
        Assert.Contains(SiblingOutput("Release", "AutoSibling"), directories);
        // ...having built nothing on the way.
        Assert.DoesNotContain("NETSDK1085", log);
        Assert.False(Directory.Exists(SiblingOutput("Debug")), "the merge built the ProjectReference");
        Assert.False(Directory.Exists(Path.Combine(root, "AutoSibling", "bin")), "the merge built the auto-detected sibling");
        Assert.False(Directory.Exists(Path.Combine(root, "Binding", "bin")), "the merge built the project");
        Assert.False(File.Exists(HookMarker), "the merge ran the hooks before ResolveProjectReferences");
    }

    /// <summary>
    /// A binding-shaped project (net10.0, so no platform workload is needed) that references the
    /// runtime and a sibling project, with the pack check <paramref name="check"/> imported; with
    /// <paramref name="otherReferences"/>, also a package from a local feed (restored into a package
    /// folder of the test's own) and assemblies referenced by relative path and by relative HintPath.
    /// </summary>
    private void WriteProjects(PackCheck check, bool otherReferences = false)
    {
        Assert.True(File.Exists(TasksDll), $"{TasksDll} was not copied beside the tests");
        Assert.True(File.Exists(RuntimeDll), $"{RuntimeDll} was not copied beside the tests");
        File.WriteAllText(Path.Combine(root, "Directory.Build.props"), "<Project />");
        File.WriteAllText(Path.Combine(root, "Directory.Build.targets"), "<Project />");
        Directory.CreateDirectory(Path.GetDirectoryName(Sibling)!);
        File.WriteAllText(Sibling, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(root, "Sibling", "Sibling.cs"), "namespace Sibling { public enum Mode { A, B } }");
        Directory.CreateDirectory(Path.GetDirectoryName(AutoSibling)!);
        File.Copy(Sibling, AutoSibling);
        File.WriteAllText(Path.Combine(root, "AutoSibling", "AutoSibling.cs"), "namespace AutoSibling { public enum Mode { A, B } }");

        var package = "";
        if (otherReferences)
        {
            var carrier = Path.Combine(root, "Carrier", "Carrier.csproj");
            Directory.CreateDirectory(Path.GetDirectoryName(carrier)!);
            File.WriteAllText(carrier, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <Version>1.0.0</Version>
                  </PropertyGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(root, "Carrier", "Carrier.cs"), "namespace Carrier { public struct Value { public int A; } }");
            var feed = Path.Combine(root, "feed");
            Run("pack", carrier, "-o", feed);
            File.WriteAllText(Path.Combine(root, "nuget.config"), $"""
                <configuration>
                  <config><add key="globalPackagesFolder" value="{SecurityElement.Escape(Path.Combine(root, "package-cache"))}" /></config>
                  <packageSources><clear /><add key="local" value="{SecurityElement.Escape(feed)}" /></packageSources>
                </configuration>
                """);
            foreach (var folder in new[] { "hinted", "pathed" })
            {
                Directory.CreateDirectory(Path.Combine(root, folder));
                File.Copy(RuntimeDll, Path.Combine(root, folder, $"{folder}.dll"));
            }
            package = """
                <PackageReference Include="Carrier" Version="1.0.0" />
                <Reference Include="hinted"><HintPath>../hinted/hinted.dll</HintPath></Reference>
                <Reference Include="../pathed/pathed.dll" />
                """;
        }

        string imports, properties;
        if (check == PackCheck.Sdk)
        {
            var targets = Path.Combine(root, "sdk-pack.targets");
            File.WriteAllText(targets, SdkPackTargets());
            imports = $"""<Import Project="{SecurityElement.Escape(targets)}" />""";
            // The binding metadata a build of the binding leaves, naming one sibling binding.
            var metadata = Path.Combine(root, "Binding", "swift-metadata") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(metadata);
            File.WriteAllText(Path.Combine(metadata, "binding-metadata.props"),
                "<Project><PropertyGroup><_SwiftBindingDependencies>AutoSibling</_SwiftBindingDependencies></PropertyGroup></Project>");
            properties = $"<_SwiftBindingsSdkTasksAssembly>{SecurityElement.Escape(TasksDll)}</_SwiftBindingsSdkTasksAssembly>"
                         + $"<_SwiftBindingIntermediateDir>{SecurityElement.Escape(metadata)}</_SwiftBindingIntermediateDir>";
        }
        else
        {
            var targets = Path.Combine(SwiftCallPlanTests.RepoRoot, "src/Swift.Runtime/src/build/SwiftBindings.Runtime.targets");
            imports = $"""<Import Project="{SecurityElement.Escape(targets)}" />""";
            properties = $"<_SwiftBindingsTasksAssembly>{SecurityElement.Escape(TasksDll)}</_SwiftBindingsTasksAssembly>"
                         + "<SwiftBindingsCheckPackedTransitions>true</SwiftBindingsCheckPackedTransitions>";
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Binding)!);
        File.WriteAllText(Binding, $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <IsPackable>true</IsPackable>
                <Version>1.0.0</Version>
                {properties}
              </PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="../Sibling/Sibling.csproj" />
                <Reference Include="{SecurityElement.Escape(RuntimeDll)}" />
                {package}
              </ItemGroup>
              {imports}
              <Target Name="_StandInForTheSdkHooks" BeforeTargets="ResolveProjectReferences">
                <Touch Files="{SecurityElement.Escape(HookMarker)}" AlwaysCreate="true" />
              </Target>
            </Project>
            """);
        File.WriteAllText(Path.Combine(root, "Binding", "Binding.cs"),
            "namespace Binding { public static class Kit { public static Sibling.Mode Mode => Sibling.Mode.B; } }");
    }

    /// <summary>The SDK's pack check and merge targets, exactly as Sdk.targets declares them.</summary>
    private static string SdkPackTargets()
    {
        var sdk = XDocument.Load(Path.Combine(SwiftCallPlanTests.RepoRoot, "src/Swift.Bindings.Sdk/Sdk/Sdk.targets"));
        string[] targets = ["_SwiftTransitionPackReadBindingDependencies", "_SwiftTransitionPackReferenceDirectories",
            "_SwiftBindingsCheckPackedTransitions", "SwiftBindingsMergePackages"];
        string[] tasks = ["SwiftBindings.Tasks.SwiftTransitionPackageCheck", "SwiftBindings.Tasks.MergeSwiftBindingPackages"];
        const string resolver = "_ResolveSwiftAutoDetectedDependencies";
        var project = new XElement("Project",
            sdk.Root!.Elements("UsingTask").Where(u => tasks.Contains((string?)u.Attribute("TaskName"))),
            sdk.Root.Elements("Target").Where(t => targets.Contains((string?)t.Attribute("Name"))),
            // The SDK's resolution of the siblings the metadata names (through the generator), standing
            // in: the one sibling the test's metadata names, injected as that resolution injects it.
            new XElement("Target", new XAttribute("Name", resolver),
                new XElement("ItemGroup", new XAttribute("Condition", "'$(_SwiftBindingDependencies)' == 'AutoSibling'"),
                    new XElement("ProjectReference", new XAttribute("Include", "../AutoSibling/AutoSibling.csproj")))));
        Assert.Equal(targets.Length + 1, project.Elements("Target").Count());
        Assert.Equal(tasks.Length, project.Elements("UsingTask").Count());
        Assert.Single(sdk.Root.Elements("Target"), t => (string?)t.Attribute("Name") == resolver);
        return project.ToString();
    }

    /// <summary>The JSON object MSBuild prints for -getItem or -getTargetResult, ahead of any build output that follows it.</summary>
    private static JsonElement FirstJson(string log)
    {
        var reader = new Utf8JsonReader(System.Text.Encoding.UTF8.GetBytes(log[log.IndexOf('{')..]));
        return JsonDocument.ParseValue(ref reader).RootElement;
    }

    private void Run(params string[] args)
    {
        var (exit, log) = Dotnet(args);
        Assert.True(exit == 0, $"dotnet {string.Join(" ", args)} failed:\n{log}");
    }

    private (int ExitCode, string Output) Dotnet(string[] args)
    {
        var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = root };
        foreach (var arg in args)
            info.ArgumentList.Add(arg);
        // A command of its own: no build server or node is shared with the test host's builds.
        info.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        info.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        // The fixture's nuget.config names its own package folder. An inherited NUGET_PACKAGES (a toolchain
        // profile sets one) would win over it and restore into the caller's cache instead.
        info.Environment.Remove("NUGET_PACKAGES");
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(300_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"dotnet {string.Join(" ", args)} timed out.");
        }
        return (process.ExitCode, stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
    }
}
