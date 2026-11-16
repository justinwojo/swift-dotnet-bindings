// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security;
using System.Text;
using System.Text.Json;
using SwiftBindings.TransitionGate;
using Xunit;
using static BindingsGeneration.Tests.SwiftTransitionManifestTests;

namespace BindingsGeneration.Tests;

/// <summary>
/// The two MSBuild tasks the packages ship, run out of process as an app or a pack runs them:
/// <list type="bullet">
/// <item>the consumer check (SWIFTBIND132) through the Runtime's buildTransitive targets, with
/// <c>ResolveAssemblyReferences</c> and <c>CoreCompile</c> stubbed: which configurations it runs on,
/// what it refuses and accepts, the per-package opt-out, which copy of a reference it reads, and its
/// cache;</item>
/// <item>the pack check (SWIFTBIND133) on hand-made packages.</item>
/// </list>
/// The fixture assemblies are compiled with Roslyn; the runtime is the one built beside the tests.
/// </summary>
public sealed class TransitionCheckTasksTests : IDisposable
{
    private const string UntypedSelf = $$"""
        public static unsafe class Counter
        {
            [DllImport("lib", EntryPoint = "$s4Lib7CounterC5countSivg")] {{SwiftTransitionManifestTests.Swift}}
            private static extern nint CountOf(SwiftSelf self);
        }
        """;

    private static readonly string TasksDll = Path.Combine(AppContext.BaseDirectory, "SwiftBindings.Tasks.dll");

    private readonly string tempDir = Directory.CreateTempSubdirectory("swift-transition-tasks-").FullName;

    public void Dispose() => Directory.Delete(tempDir, recursive: true);

    // ---- when the consumer check runs -----------------------------------------------------------

    [Theory]
    [InlineData("net10.0-ios", "Debug", "", "Mono")]
    [InlineData("net10.0-ios", "Release", "", "Mono")]
    [InlineData("net10.0-tvos", "Debug", "", "Mono")]
    [InlineData("net10.0-maccatalyst", "Release", "", "Mono")]
    [InlineData("net11.0-ios", "Debug", "", "the .NET 11 CoreCLR interpreter (Debug)")]
    [InlineData("net11.0-tvos", "Release", "", ".NET 11 CoreCLR (Release)")]
    [InlineData("net11.0-maccatalyst", "Debug", "", "the .NET 11 CoreCLR interpreter (Debug)")]
    [InlineData("net11.0-ios", "Debug", "<OutputType>Library</OutputType><IsAppExtension>true</IsAppExtension>", "the .NET 11 CoreCLR interpreter (Debug)")]
    public void AffectedConfigurations_RefuseAnUnsafeBinding(string tfm, string configuration, string extra, string engine)
    {
        var dll = Fixture("lib", UntypedSelf);
        var (exit, output) = Build(tfm, configuration, References(compile: dll, package: "Acme.Binding"), extra);
        Assert.NotEqual(0, exit);
        Assert.Contains("error SWIFTBIND132", output);
        Assert.Contains($"This build runs on {engine}.", output);
    }

    [Theory]
    [InlineData("net10.0-ios", "Release", "<_UseNativeAot>true</_UseNativeAot>")]
    [InlineData("net11.0-ios", "Release", "<_UseNativeAot>true</_UseNativeAot>")]
    [InlineData("net10.0-macos", "Debug", "")]
    [InlineData("net11.0-macos", "Debug", "")]
    [InlineData("net10.0", "Debug", "")]
    [InlineData("net10.0-ios", "Debug", "<OutputType>Library</OutputType>")]
    [InlineData("net10.0-ios", "Debug", "<DesignTimeBuild>true</DesignTimeBuild>")]
    public void UnaffectedConfigurations_DoNotRun(string tfm, string configuration, string extra)
    {
        var dll = Fixture("lib", UntypedSelf);
        var (exit, output) = Build(tfm, configuration, References(compile: dll, package: "Acme.Binding"), extra);
        Assert.True(exit == 0, output);
        Assert.DoesNotContain("SWIFTBIND132", output);
        Assert.False(File.Exists(CacheFile), "the check ran");
    }

    [Fact]
    public void MissingTaskAssembly_FailsInsteadOfSkipping()
    {
        var dll = Fixture("lib", UntypedSelf);
        var (exit, output) = Build("net10.0-ios", "Debug", References(compile: dll, package: "Acme.Binding"), tasks: Path.Combine(tempDir, "missing.dll"));
        Assert.NotEqual(0, exit);
        Assert.Contains("SWIFTBIND132", output);
        Assert.Contains("is missing", output);
    }

    // ---- what it says ---------------------------------------------------------------------------

    [Fact]
    public void UnsafePackage_NamesTheAssemblyPackageMemberEnginesAndRemedy()
    {
        var dll = Fixture("lib", UntypedSelf);
        var (exit, output) = Build("net10.0-ios", "Debug", References(compile: dll, package: "Acme.Binding", version: "1.2.3"));
        Assert.NotEqual(0, exit);
        var error = Assert.Single(output.Split('\n').Where(l => l.Contains("error SWIFTBIND132", StringComparison.Ordinal)).Distinct());
        foreach (var expected in new[]
                 {
                     "Fixture.dll (package Acme.Binding 1.2.3)", "carries no Swift transition manifest", "1 of its Swift transitions",
                     "Counter::CountOf", "breaks R1", "NativeAOT is not affected",
                     "Update Acme.Binding to a version built with the current SwiftBindings.Sdk",
                     "<SwiftBindingsTransitionSuppress Include=\"Acme.Binding\" />",
                 })
            Assert.Contains(expected, error);
        Assert.Equal("Refused", Verdicts()["Fixture"]);
    }

    [Fact]
    public void Suppression_DowngradesThatPackageOnly()
    {
        var dll = Fixture("lib", UntypedSelf);
        var (exit, output) = Build("net10.0-ios", "Debug", References(compile: dll, package: "Acme.Binding"),
            items: """<SwiftBindingsTransitionSuppress Include="acme.binding" />""");
        Assert.True(exit == 0, output);
        Assert.Contains("warning SWIFTBIND132", output);
        Assert.Contains("is allowed by <SwiftBindingsTransitionSuppress Include=\"Acme.Binding\" />", output);
        Assert.DoesNotContain("error SWIFTBIND132", output);

        (exit, output) = Build("net10.0-ios", "Debug", References(compile: dll, package: "Acme.Binding"),
            items: """<SwiftBindingsTransitionSuppress Include="Other.Binding" />""");
        Assert.NotEqual(0, exit);
        Assert.Contains("error SWIFTBIND132", output);
    }

    [Fact]
    public void ProjectReference_IsNamedAndSuppressedByAssembly()
    {
        var dll = Fixture("lib", UntypedSelf);
        var reference = $"""<ReferencePath Include="{SecurityElement.Escape(dll)}" MSBuildSourceProjectFile="/src/Acme/Acme.csproj" />""";
        var (exit, output) = Build("net10.0-ios", "Debug", reference);
        Assert.NotEqual(0, exit);
        Assert.Contains("Fixture.dll (project Acme.csproj)", output);
        Assert.Contains("Regenerate the binding with the current SwiftBindings.Sdk", output);

        (exit, output) = Build("net10.0-ios", "Debug", reference, items: """<SwiftBindingsTransitionSuppress Include="Fixture" />""");
        Assert.True(exit == 0, output);
        Assert.Contains("warning SWIFTBIND132", output);
    }

    [Fact]
    public void PrimitiveOnlyAndVerifiedBindings_AreAccepted_AndOrdinaryReferencesAreNotRead()
    {
        var primitive = Fixture("primitive", PrimitiveOnly);
        var plain = Fixture("plain", "public static class Plain { public static int One() => 1; }", name: "Plain");
        var references = References(compile: primitive, package: "Acme.Primitive")
                         + References(compile: plain, package: "Acme.Plain")
                         + References(compile: RuntimeDll, package: "SwiftBindings.Runtime");
        var (exit, output) = Build("net11.0-ios", "Debug", references, verbosity: "normal");
        Assert.True(exit == 0, output);
        Assert.DoesNotContain("SWIFTBIND132", output);
        var verdicts = Verdicts();
        Assert.Equal("PrimitiveOnly", verdicts["Fixture"]);
        Assert.Equal("Verified", verdicts["Swift.Runtime"]);
        Assert.False(verdicts.ContainsKey("Plain"));
        // No native reference provides the runtime's library here, so its hash goes unchecked, and the log says so.
        Assert.Contains("routes Swift calls through bridges in SwiftBindingsRuntime, which no native reference of this app provides", output);
    }

    [Fact]
    public void TheRuntimeCopyOfAReference_IsTheOneRead()
    {
        // A reference assembly drops private P/Invokes; the implementation the app loads is what counts.
        var reference = Fixture("ref", PrimitiveOnly);
        var implementation = Fixture("lib", UntypedSelf);
        var items = References(compile: reference, package: "Acme.Binding")
                    + $"""<_StubCopyLocal Include="{SecurityElement.Escape(implementation)}" NuGetPackageId="Acme.Binding" NuGetPackageVersion="1.0.0" />""";
        var (exit, output) = Build("net10.0-ios", "Debug", items);
        Assert.NotEqual(0, exit);
        Assert.Contains("Counter::CountOf", output);
    }

    [Fact]
    public void Cache_IsReusedForTheSameReferences_AndDroppedWhenOneChanges()
    {
        var dll = Fixture("lib", PrimitiveOnly);
        var plain = Fixture("plain", "public static class Plain { public static int One() => 1; }", name: "Plain");
        var references = References(compile: dll, package: "Acme.Binding") + References(compile: plain, package: "Acme.Plain");
        var (_, first) = Build("net10.0-ios", "Debug", references, verbosity: "detailed");
        Assert.Contains("1 reference(s) can make Swift transitions, 1 inspected, 0 from the cache", first);
        var (_, second) = Build("net10.0-ios", "Debug", references, verbosity: "detailed");
        Assert.Contains("1 reference(s) can make Swift transitions, 0 inspected, 1 from the cache", second);

        // Another reference can hold the binding's carrier types, so replacing it drops the cache too.
        Fixture("plain", "public static class Plain { public static int Two() => 2; }", name: "Plain");
        var (_, afterDependency) = Build("net10.0-ios", "Debug", references, verbosity: "detailed");
        Assert.Contains("1 reference(s) can make Swift transitions, 1 inspected, 0 from the cache", afterDependency);

        // A rebuilt binding has a new MVID, and is read again.
        Fixture("lib", UntypedSelf);
        var (exit, third) = Build("net10.0-ios", "Debug", references, verbosity: "detailed");
        Assert.NotEqual(0, exit);
        Assert.Contains("1 inspected, 0 from the cache", third);
    }

    // ---- the pack check -------------------------------------------------------------------------

    [Fact]
    public void Pack_ManifestlessUnsafeAssembly_IsRefusedAndDeleted()
    {
        var package = Package("Acme.Binding.1.0.0.nupkg", ("lib/net10.0/Fixture.dll", File.ReadAllBytes(Fixture("lib", UntypedSelf))));
        var (exit, output) = Pack(package);
        Assert.NotEqual(0, exit);
        Assert.Contains("error SWIFTBIND133", output);
        Assert.Contains("lib/net10.0/Fixture.dll would be refused by its consumers' Swift transition check (evidence AbsentNonPrimitive)", output);
        Assert.Contains("rebuild the binding clean", output);
        Assert.False(File.Exists(package), "a refused package must not stay where it can be pushed");
    }

    [Fact]
    public void Pack_ThatCannotBeRead_IsRefusedAndDeleted()
    {
        var package = Path.Combine(tempDir, "feed", "Broken.1.0.0.nupkg");
        Directory.CreateDirectory(Path.GetDirectoryName(package)!);
        File.WriteAllText(package, "not a zip");
        var (exit, output) = Pack(package);
        Assert.NotEqual(0, exit);
        Assert.Contains("error SWIFTBIND133", output);
        Assert.False(File.Exists(package), "a package that could not be checked must not stay where it can be pushed");
    }

    [Fact]
    public void Pack_PrimitiveOnlyAssembly_IsKept()
    {
        var package = Package("Acme.Binding.1.0.0.nupkg", ("lib/net10.0/Fixture.dll", File.ReadAllBytes(Fixture("lib", PrimitiveOnly))));
        var (exit, output) = Pack(package);
        Assert.True(exit == 0, output);
        Assert.True(File.Exists(package));
    }

    [Fact]
    public void Pack_RoutedAssembly_NeedsItsNativeImagesInThePackage()
    {
        var runtime = ("lib/net10.0/Swift.Runtime.dll", File.ReadAllBytes(RuntimeDll));
        var bare = Package("Bare.1.0.0.nupkg", runtime);
        var (exit, output) = Pack(bare);
        Assert.NotEqual(0, exit);
        Assert.Contains("no image of SwiftBindingsRuntime was found", output);

        var slices = RuntimeSlices.Select((p, i) => ($"native/{i}/SwiftBindingsRuntime.framework/SwiftBindingsRuntime", File.ReadAllBytes(p)));
        var complete = Package("Complete.1.0.0.nupkg", [runtime, .. slices]);
        (exit, output) = Pack(complete);
        Assert.True(exit == 0, output);
        Assert.True(File.Exists(complete));
    }

    [Fact]
    public void Pack_EachTargetFramework_IsCheckedAgainstItsOwnPlatformsNatives()
    {
        // A multi-platform binding ships one wrapper per platform under runtimes/<rid>/native/, and
        // two platforms' manifests hash differently when their routing differs. A consumer of one
        // target framework links only its own platform's natives, so the pack check pairs them the
        // same way: here the tvOS slices hold another manifest's hash.
        var hash = SwiftTransitionManifestReader.Read(RuntimeDll, new SwiftTransitionScanOptions { ReferenceDirectories = [AppContext.BaseDirectory] }).Manifests.Single().Hash;
        var other = SwiftTransitionManifestFormat.Hash("another platform's manifest");
        (string, byte[])[] Natives(string rid, bool otherManifest) => RuntimeSlices.Select((p, i) => (
            $"runtimes/{rid}/native/SwiftBindingsRuntime.xcframework/{i}/SwiftBindingsRuntime.framework/SwiftBindingsRuntime",
            otherManifest ? Replace(File.ReadAllBytes(p), Encoding.ASCII.GetBytes(hash), Encoding.ASCII.GetBytes(other)) : File.ReadAllBytes(p))).ToArray();
        var ios = ("lib/net10.0-ios26.0/Swift.Runtime.dll", File.ReadAllBytes(RuntimeDll));
        var tvos = ("lib/net10.0-tvos26.0/Swift.Runtime.dll", File.ReadAllBytes(RuntimeDll));

        var scoped = Package("Scoped.1.0.0.nupkg", [ios, .. Natives("ios-arm64", false), .. Natives("tvos-arm64", true)]);
        var (exit, output) = Pack(scoped);
        Assert.True(exit == 0, output);
        Assert.True(File.Exists(scoped));

        // The tvOS assembly is still held to the tvOS natives.
        var mismatched = Package("Mismatched.1.0.0.nupkg", [ios, tvos, .. Natives("ios-arm64", false), .. Natives("tvos-arm64", true)]);
        (exit, output) = Pack(mismatched);
        Assert.NotEqual(0, exit);
        Assert.Contains("lib/net10.0-tvos26.0/Swift.Runtime.dll would be refused", output);
        Assert.Contains("runtimes/tvos-arm64/native/", output);
        Assert.DoesNotContain("lib/net10.0-ios26.0/Swift.Runtime.dll would be refused", output);

        // An assembly with none of its platform's natives is refused, not passed vacuously.
        var missing = Package("Missing.1.0.0.nupkg", [tvos, .. Natives("ios-arm64", false)]);
        (exit, output) = Pack(missing);
        Assert.NotEqual(0, exit);
        Assert.Contains("no image of SwiftBindingsRuntime was found", output);
    }

    // ---- fixtures -------------------------------------------------------------------------------

    private string CacheFile => Path.Combine(tempDir, "obj", "swiftbindings-transition-check.json");

    /// <summary>Compiles <paramref name="body"/> to <c>{tempDir}/{directory}/{name}.dll</c>.</summary>
    private string Fixture(string directory, string body, string name = "Fixture")
    {
        var path = Path.Combine(tempDir, directory, name + ".dll");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Compile(name, body));
        return path;
    }

    private static string References(string compile, string package, string version = "1.0.0") =>
        $"""<ReferencePath Include="{SecurityElement.Escape(compile)}" NuGetPackageId="{package}" NuGetPackageVersion="{version}" />""";

    private System.Collections.Generic.Dictionary<string, string> Verdicts()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(CacheFile));
        return document.RootElement.GetProperty("Results").EnumerateObject()
            .ToDictionary(r => r.Value.GetProperty("Name").GetString()!, r => r.Value.GetProperty("Verdict").GetString()!);
    }

    private (int ExitCode, string Output) Build(string tfm, string configuration, string references, string extra = "",
        string items = "", string? tasks = null, string verbosity = "minimal")
    {
        var targets = Path.Combine(SwiftCallPlanTests.RepoRoot, "src/Swift.Runtime/src/build/SwiftBindings.Runtime.targets");
        var project = Path.Combine(tempDir, "app.proj");
        File.WriteAllText(project, $"""
            <Project>
              <PropertyGroup>
                <TargetFramework>{tfm}</TargetFramework>
                <Configuration>{configuration}</Configuration>
                <OutputType>Exe</OutputType>
                <UseMonoRuntime Condition="$(TargetFramework.StartsWith('net10')) AND !$(TargetFramework.EndsWith('-macos'))">true</UseMonoRuntime>
                <IntermediateOutputPath>{SecurityElement.Escape(Path.Combine(tempDir, "obj"))}/</IntermediateOutputPath>
                <_SwiftBindingsTasksAssembly>{SecurityElement.Escape(tasks ?? TasksDll)}</_SwiftBindingsTasksAssembly>
                {extra}
              </PropertyGroup>
              <Import Project="{SecurityElement.Escape(targets)}" />
              <ItemGroup>{items}</ItemGroup>
              <Target Name="ResolveAssemblyReferences">
                <ItemGroup>
                  {references}
                  <ReferenceCopyLocalPaths Include="@(_StubCopyLocal)" />
                </ItemGroup>
              </Target>
              <Target Name="CoreCompile" />
            </Project>
            """);
        return MSBuild(project, "CoreCompile", verbosity);
    }

    private string Package(string name, params (string Path, byte[] Bytes)[] entries)
    {
        var path = Path.Combine(tempDir, "feed", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entry, bytes) in entries)
        {
            using var stream = zip.CreateEntry(entry).Open();
            stream.Write(bytes);
        }
        return path;
    }

    private (int ExitCode, string Output) Pack(string package)
    {
        var project = Path.Combine(tempDir, "pack.proj");
        File.WriteAllText(project, $"""
            <Project>
              <UsingTask TaskName="SwiftBindings.Tasks.SwiftTransitionPackageCheck" AssemblyFile="{SecurityElement.Escape(TasksDll)}" />
              <Target Name="Check">
                <SwiftTransitionPackageCheck Packages="{SecurityElement.Escape(package)}"
                    ReferenceDirectories="{SecurityElement.Escape(AppContext.BaseDirectory)}"
                    ScratchDirectory="{SecurityElement.Escape(Path.Combine(tempDir, "pack-scratch"))}" />
              </Target>
            </Project>
            """);
        return MSBuild(project, "Check", "minimal");
    }

    private static (int ExitCode, string Output) MSBuild(string project, string target, string verbosity)
    {
        Assert.True(File.Exists(TasksDll), $"{TasksDll} was not copied beside the tests");
        var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "msbuild", project, "-nologo", "-nodeReuse:false", $"-v:{verbosity}", $"-t:{target}" })
            info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(120_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("The transition check MSBuild fixture timed out.");
        }
        return (process.ExitCode, stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
    }
}
