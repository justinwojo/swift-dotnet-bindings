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
using System.Xml.Linq;
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
    public void WrapperMismatch_NamesEveryEngineAsAffected()
    {
        // A wrapper of another copy breaks the binding on NativeAOT too; only the check does not run there.
        var own = WrapperIdentityTests.Identity("net10.0-ios27.2", "27.2");
        var dll = Path.Combine(tempDir, "lib", "Kit.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(dll)!);
        File.WriteAllBytes(dll, WrapperIdentityTests.StampedAssembly(own));
        var framework = Directory.CreateDirectory(Path.Combine(tempDir, "native", "Kit.framework")).FullName;
        File.WriteAllBytes(Path.Combine(framework, "Kit"),
            WrapperIdentityTests.StampedDylib(tempDir, "older", WrapperIdentityTests.Identity("net10.0-ios26.0", "26.2")));
        var (exit, output) = Build("net10.0-ios", "Debug", References(compile: dll, package: "Acme.Kit", version: "1.2.3"),
            items: $"""<NativeReference Include="{SecurityElement.Escape(framework)}" />""");
        Assert.NotEqual(0, exit);
        var error = Assert.Single(output.Split('\n').Where(l => l.Contains("error SWIFTBIND132", StringComparison.Ordinal)).Distinct());
        Assert.Contains("is not the wrapper Kit was generated with", error);
        Assert.Contains("breaks the binding on every engine, NativeAOT and macOS included", error);
        Assert.DoesNotContain("NativeAOT is not affected", error);
        Assert.Contains("update Acme.Kit to a version packed from one clean build per copy", error);
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
        Assert.Contains("Regenerate the binding with the current SwiftBindings.Sdk", output);
        Assert.False(File.Exists(package), "a refused package must not stay where it can be pushed");
    }

    [Theory]
    // A standalone binding project (Microsoft.NET.Sdk) opts into the runtime's pack check.
    [InlineData("<SwiftBindingsCheckPackedTransitions>true</SwiftBindingsCheckPackedTransitions>", true)]
    // Any other project that references the runtime packs unchecked.
    [InlineData("", false)]
    [InlineData("<SwiftBindingsCheckPackedTransitions>true</SwiftBindingsCheckPackedTransitions><IsPackable>false</IsPackable>", false)]
    public void StandalonePack_RunsTheRuntimesPackCheck_WhenTheProjectAsks(string extra, bool checks)
    {
        var package = Package("Acme.Binding.1.0.0.nupkg", ("lib/net10.0/Fixture.dll", File.ReadAllBytes(Fixture("lib", UntypedSelf))));
        var targets = Path.Combine(SwiftCallPlanTests.RepoRoot, "src/Swift.Runtime/src/build/SwiftBindings.Runtime.targets");
        var project = Path.Combine(tempDir, "binding.proj");
        File.WriteAllText(project, $"""
            <Project>
              <PropertyGroup>
                <TargetFramework>net10.0-ios</TargetFramework>
                <OutputType>Library</OutputType>
                <BaseIntermediateOutputPath>{SecurityElement.Escape(Path.Combine(tempDir, "obj"))}/</BaseIntermediateOutputPath>
                <_SwiftBindingsTasksAssembly>{SecurityElement.Escape(TasksDll)}</_SwiftBindingsTasksAssembly>
                {extra}
              </PropertyGroup>
              <Import Project="{SecurityElement.Escape(targets)}" />
              <Target Name="ResolveAssemblyReferences">
                <ItemGroup><ReferencePath Include="{SecurityElement.Escape(RuntimeDll)}" /></ItemGroup>
              </Target>
              <Target Name="Pack">
                <ItemGroup><NuGetPackOutput Include="{SecurityElement.Escape(package)}" /></ItemGroup>
              </Target>
            </Project>
            """);
        var (exit, output) = MSBuild(project, "Pack", "minimal");
        if (checks)
        {
            Assert.NotEqual(0, exit);
            Assert.Contains("error SWIFTBIND133", output);
            Assert.Contains("lib/net10.0/Fixture.dll would be refused", output);
            Assert.False(File.Exists(package), "a refused package must not stay where it can be pushed");
        }
        else
        {
            Assert.True(exit == 0, output);
            Assert.DoesNotContain("SWIFTBIND133", output);
            Assert.True(File.Exists(package));
        }
    }

    public enum RuntimeTargets { WithoutTheCheck, Current }

    [Theory]
    // Restore picked a SwiftBindings.Runtime whose targets predate the pack check: the generated
    // project refuses before a package is written, naming the fix. (The package would be refused
    // by its consumers, and without the guard it ships.)
    [InlineData(RuntimeTargets.WithoutTheCheck, "UntypedSelf", false, "predates the pack-time Swift transition check")]
    // A Runtime that ships the check: the guard stays quiet and the real check decides.
    [InlineData(RuntimeTargets.Current, "PrimitiveOnly", true, null)]
    [InlineData(RuntimeTargets.Current, "UntypedSelf", false, "lib/net10.0/Fixture.dll would be refused")]
    public void StandalonePack_GeneratedProject_RefusesToPackWhenTheRuntimeLacksTheCheck(
        RuntimeTargets runtime, string fixture, bool written, string? error)
    {
        // The project the generator emits for a published --swift-runtime-version, restored
        // against an older Runtime: the Runtime's PackageReference floats, so restore can pick
        // one whose buildTransitive targets have no pack check.
        var generated = Path.Combine(tempDir, "generated");
        var source = Path.Combine(tempDir, "Lib.xcframework");
        Directory.CreateDirectory(generated);
        Directory.CreateDirectory(source);
        BindingsGeneration.BindingProjectEmitter.Emit(new BindingsGeneration.BindingProjectEmitterOptions
        {
            OutputDirectory = generated,
            ModuleName = "Lib",
            Metadata = new BindingsGeneration.XCFrameworkMetadata
            {
                LibraryVersion = "1.0.0", PackageVersion = "1.0.0", IsVersionPlaceholder = false,
                MinimumOSVersion = "15.0", EffectiveMinimumOSVersion = "15.0", ModuleName = "Lib", Platforms = [],
            },
            SourceXCFrameworkPath = source,
            SwiftRuntimeVersion = "0.20.2",
        }, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        var csproj = XDocument.Load(Directory.GetFiles(generated, "*.csproj").Single()).Root!;
        var optIn = csproj.Descendants("SwiftBindingsCheckPackedTransitions").Single();
        var isPackable = csproj.Descendants("IsPackable").Single();
        // Every target the generated project declares, its pack-check guard among them.
        var projectTargets = string.Concat(csproj.Elements("Target").Select(t => t.ToString()));

        var targets = Path.Combine(SwiftCallPlanTests.RepoRoot, "src/Swift.Runtime/src/build/SwiftBindings.Runtime.targets");
        if (runtime == RuntimeTargets.WithoutTheCheck)
        {
            // Today's targets with the standalone pack check (target and marker) taken out, as a
            // Runtime from before the check ships them.
            var old = XDocument.Load(targets);
            old.Root!.Elements("Target").Single(t => (string?)t.Attribute("Name") == "_SwiftBindingsCheckStandalonePackedTransitions").Remove();
            old.Root.Elements("PropertyGroup").Elements(BindingsGeneration.BindingProjectEmitter.StandalonePackCheckMarker).Single().Parent!.Remove();
            targets = Path.Combine(tempDir, "old-runtime", "SwiftBindings.Runtime.targets");
            Directory.CreateDirectory(Path.GetDirectoryName(targets)!);
            old.Save(targets);
            File.Copy(Path.Combine(SwiftCallPlanTests.RepoRoot, "src/Swift.Runtime/src/build/SwiftBindings.MacFrameworkAnatomy.targets"),
                Path.Combine(tempDir, "old-runtime", "SwiftBindings.MacFrameworkAnatomy.targets"));
        }

        var body = fixture == "UntypedSelf" ? UntypedSelf : PrimitiveOnly;
        var staged = Package("Lib.Swift.iOS.1.0.0.nupkg", ("lib/net10.0/Fixture.dll", File.ReadAllBytes(Fixture("lib", body))));
        var package = Path.Combine(tempDir, "bin", "Lib.Swift.iOS.1.0.0.nupkg");
        var project = Path.Combine(tempDir, "Lib.Swift.iOS.proj");
        File.WriteAllText(project, $"""
            <Project>
              <PropertyGroup>
                <TargetFramework>net10.0-ios</TargetFramework>
                <OutputType>Library</OutputType>
                <BaseIntermediateOutputPath>{SecurityElement.Escape(Path.Combine(tempDir, "obj"))}/</BaseIntermediateOutputPath>
                <_SwiftBindingsTasksAssembly>{SecurityElement.Escape(TasksDll)}</_SwiftBindingsTasksAssembly>
                {optIn}
                {isPackable}
              </PropertyGroup>
              {projectTargets}
              <Import Project="{SecurityElement.Escape(targets)}" />
              <Target Name="ResolveAssemblyReferences">
                <ItemGroup><ReferencePath Include="{SecurityElement.Escape(RuntimeDll)}" NuGetPackageId="SwiftBindings.Runtime" NuGetPackageVersion="0.20.2" /></ItemGroup>
              </Target>
              <Target Name="GenerateNuspec">
                <Copy SourceFiles="{SecurityElement.Escape(staged)}" DestinationFiles="{SecurityElement.Escape(package)}" />
                <ItemGroup><NuGetPackOutput Include="{SecurityElement.Escape(package)}" /></ItemGroup>
              </Target>
              <Target Name="Pack" DependsOnTargets="GenerateNuspec" />
            </Project>
            """);

        var (exit, output) = MSBuild(project, "Pack", "minimal");
        Assert.Equal(written, File.Exists(package));
        if (error is null)
        {
            Assert.True(exit == 0, output);
            Assert.DoesNotContain("SWIFTBIND133", output);
            return;
        }
        Assert.NotEqual(0, exit);
        Assert.Contains("error SWIFTBIND133", output);
        Assert.Contains(error, output);
        if (runtime == RuntimeTargets.WithoutTheCheck)
        {
            Assert.Contains("SwiftBindings.Runtime 0.20.2 predates", output);
            Assert.Contains("Raise this project's SwiftBindings.Runtime PackageReference to", output);
            Assert.DoesNotContain("would be refused", output);
        }
        else
            Assert.DoesNotContain("predates", output);
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

    [Fact]
    public void Pack_EachCopy_IsCheckedAgainstItsOwnCopysNatives()
    {
        // A multi-toolchain package carries one copy per toolchain of one platform, keyed by the full
        // TFM: lib/<tfm>/ is held to native/<tfm>/ only, never to another copy of the same platform.
        var hash = SwiftTransitionManifestReader.Read(RuntimeDll, new SwiftTransitionScanOptions { ReferenceDirectories = [AppContext.BaseDirectory] }).Manifests.Single().Hash;
        var other = SwiftTransitionManifestFormat.Hash("another copy's manifest");
        (string, byte[])[] Natives(string tfm, bool otherManifest) => RuntimeSlices.Select((p, i) => (
            $"native/{tfm}/SwiftBindingsRuntime.xcframework/{i}/SwiftBindingsRuntime.framework/SwiftBindingsRuntime",
            otherManifest ? Replace(File.ReadAllBytes(p), Encoding.ASCII.GetBytes(hash), Encoding.ASCII.GetBytes(other)) : File.ReadAllBytes(p))).ToArray();
        var older = ("lib/net10.0-ios26.0/Swift.Runtime.dll", File.ReadAllBytes(RuntimeDll));
        var newer = ("lib/net10.0-ios27.2/Swift.Runtime.dll", File.ReadAllBytes(RuntimeDll));

        var whole = Package("Whole.1.0.0.nupkg", [older, newer, .. Natives("net10.0-ios26.0", false), .. Natives("net10.0-ios27.2", false)]);
        var (exit, output) = Pack(whole);
        Assert.True(exit == 0, output);

        // The newer copy's wrapper was swapped for one built from another managed binding: only that
        // copy is refused, though the older copy's natives of the same platform would satisfy it.
        var swapped = Package("Swapped.1.0.0.nupkg", [older, newer, .. Natives("net10.0-ios26.0", false), .. Natives("net10.0-ios27.2", true)]);
        (exit, output) = Pack(swapped);
        Assert.NotEqual(0, exit);
        Assert.Contains("lib/net10.0-ios27.2/Swift.Runtime.dll would be refused", output);
        Assert.Contains("native/net10.0-ios27.2/", output);
        Assert.DoesNotContain("lib/net10.0-ios26.0/Swift.Runtime.dll would be refused", output);

        // A copy whose natives are missing is refused, not checked against another copy's.
        var missing = Package("MissingCopy.1.0.0.nupkg", [older, newer, .. Natives("net10.0-ios26.0", false)]);
        (exit, output) = Pack(missing);
        Assert.NotEqual(0, exit);
        Assert.Contains("no image of SwiftBindingsRuntime was found", output);

        // Nor against a matching image outside every copy: the copy's targets never reference it.
        (string, byte[])[] shared = RuntimeSlices.Select((p, i) => (
            $"native/SwiftBindingsRuntime.xcframework/{i}/SwiftBindingsRuntime.framework/SwiftBindingsRuntime", File.ReadAllBytes(p))).ToArray();
        var outside = Package("OutsideCopy.1.0.0.nupkg", [older, newer, .. Natives("net10.0-ios26.0", false), .. shared]);
        (exit, output) = Pack(outside);
        Assert.NotEqual(0, exit);
        Assert.Contains("lib/net10.0-ios27.2/Swift.Runtime.dll would be refused", output);
        Assert.DoesNotContain("lib/net10.0-ios26.0/Swift.Runtime.dll would be refused", output);

        // A one-copy package is known by its declaration: its only wrapper outside native/<tfm>/ does
        // not pass it as a package without copies.
        var declaration = ("buildTransitive/net10.0-ios26.0/Acme.Kit.props",
            Encoding.UTF8.GetBytes(PackageCopyDeclarationEmitter.Compose("Acme.Kit", "net10.0-ios26.0", ".NET 10 + Xcode 26.3", "26.2", "0.21.0")));
        (exit, output) = Pack(Package("StrayCopy.1.0.0.nupkg", [older, declaration, .. shared]));
        Assert.NotEqual(0, exit);
        Assert.Contains("lib/net10.0-ios26.0/Swift.Runtime.dll would be refused", output);
        (exit, output) = Pack(Package("SharedOnly.1.0.0.nupkg", [older, .. shared]));
        Assert.True(exit == 0, output);
        // A props file that only names the item in a comment or a property declares no copy.
        var mention = ("buildTransitive/net10.0-ios26.0/Acme.Kit.props", Encoding.UTF8.GetBytes(
            "<Project><!-- <SwiftBindingPackageCopy Include=\"Acme.Kit\" /> --><PropertyGroup><SwiftBindingPackageCopy>x</SwiftBindingPackageCopy></PropertyGroup></Project>"));
        (exit, output) = Pack(Package("Mention.1.0.0.nupkg", [older, mention, .. shared]));
        Assert.True(exit == 0, output);
    }

    [Fact]
    public void Pack_ACopysWrapperSwappedForAnotherCopys_IsRefused()
    {
        // NEG-C: the newer copy's native/<tfm>/ holds the older copy's wrapper. Neither wrapper routes
        // bridges, so no manifest hash tells them apart; the wrapper identity each was generated with does.
        var older = WrapperIdentityTests.Identity("net10.0-ios26.0", "26.2");
        var newer = WrapperIdentityTests.Identity("net10.0-ios27.2", "27.2");
        var scratch = Directory.CreateDirectory(Path.Combine(tempDir, "natives")).FullName;
        var olderWrapper = WrapperIdentityTests.StampedDylib(scratch, "older", older);
        var newerWrapper = WrapperIdentityTests.StampedDylib(scratch, "newer", newer);
        (string, byte[])[] Copies(byte[] newerCopysWrapper) =>
        [
            ("lib/net10.0-ios26.0/Kit.dll", WrapperIdentityTests.StampedAssembly(older)),
            ("lib/net10.0-ios27.2/Kit.dll", WrapperIdentityTests.StampedAssembly(newer)),
            ("native/net10.0-ios26.0/Kit.xcframework/ios-arm64/Kit.framework/Kit", olderWrapper),
            ("native/net10.0-ios27.2/Kit.xcframework/ios-arm64/Kit.framework/Kit", newerCopysWrapper),
            ("buildTransitive/net10.0-ios26.0/Acme.Kit.props",
                Encoding.UTF8.GetBytes(PackageCopyDeclarationEmitter.Compose("Acme.Kit", "net10.0-ios26.0", ".NET 10 + Xcode 26.3", "26.2", "0.21.0"))),
            ("buildTransitive/net10.0-ios27.2/Acme.Kit.props",
                Encoding.UTF8.GetBytes(PackageCopyDeclarationEmitter.Compose("Acme.Kit", "net10.0-ios27.2", ".NET 10 + Xcode 27.2", "27.2", "0.21.0"))),
        ];

        var (exit, output) = Pack(Package("Acme.Kit.1.0.0.nupkg", Copies(newerWrapper)));
        Assert.True(exit == 0, output);

        var swapped = Package("Acme.Kit.1.0.1.nupkg", Copies(olderWrapper));
        (exit, output) = Pack(swapped);
        Assert.NotEqual(0, exit);
        Assert.Contains("error SWIFTBIND133", output);
        Assert.Contains("lib/net10.0-ios27.2/Kit.dll would be refused", output);
        Assert.Contains("generated as copy net10.0-ios26.0 from the 26.2 SDK", output);
        Assert.Contains("is not the one this assembly was generated with", output);
        Assert.DoesNotContain("lib/net10.0-ios26.0/Kit.dll would be refused", output);
        Assert.False(File.Exists(swapped));
    }

    [Fact]
    public void Pack_ACopysWrapperShippedAsABareDylib_IsHeldToItsIdentity()
    {
        // A consumer's native reference resolves lib<library>.dylib as the library's image, so the
        // pack check reads it as one too: the older copy's wrapper under the newer copy's name is refused.
        var older = WrapperIdentityTests.Identity("net10.0-ios26.0", "26.2");
        var newer = WrapperIdentityTests.Identity("net10.0-ios27.2", "27.2");
        var scratch = Directory.CreateDirectory(Path.Combine(tempDir, "dylibs")).FullName;
        (string, byte[])[] Copies(byte[] newerCopysWrapper) =>
        [
            ("lib/net10.0-ios26.0/Kit.dll", WrapperIdentityTests.StampedAssembly(older)),
            ("lib/net10.0-ios27.2/Kit.dll", WrapperIdentityTests.StampedAssembly(newer)),
            ("native/net10.0-ios26.0/libKit.dylib", WrapperIdentityTests.StampedDylib(scratch, "older", older)),
            ("native/net10.0-ios27.2/libKit.dylib", newerCopysWrapper),
        ];

        var (exit, output) = Pack(Package("Acme.Kit.1.0.0.nupkg", Copies(WrapperIdentityTests.StampedDylib(scratch, "newer", newer))));
        Assert.True(exit == 0, output);

        (exit, output) = Pack(Package("Acme.Kit.1.0.1.nupkg", Copies(WrapperIdentityTests.StampedDylib(scratch, "older2", older))));
        Assert.NotEqual(0, exit);
        Assert.Contains("lib/net10.0-ios27.2/Kit.dll would be refused", output);
        Assert.Contains("native/net10.0-ios27.2/libKit.dylib", output);
    }

    // ---- the producer label (SWIFTBIND137) ------------------------------------------------------

    [Theory]
    // Generated on Xcode 26, packed after Xcode 27 was selected: labelled by the SDK it came from.
    [InlineData("26.2", "27.2", 0, "26.2")]
    // Generated on Xcode 27, packed after Xcode 26 was selected: a 26.0 label would be selected by
    // Xcode 26 apps and compile against API their SDK lacks.
    [InlineData("27.2", "26.2", 1, "error SWIFTBIND137")]
    // An assembly that records no SDK is judged by the SDK selected now.
    [InlineData(null, "26.2", 0, "26.2")]
    [InlineData(null, "27.2", 1, "error SWIFTBIND137")]
    public void Label_IsJudgedByTheSdkTheBindingWasGeneratedFrom(string? generatedSdk, string selectedSdk, int exit, string expected)
    {
        var assembly = Path.Combine(tempDir, "Acme.Kit.dll");
        File.WriteAllBytes(assembly, generatedSdk is null
            ? Compile("Acme.Kit", PrimitiveOnly)
            : WrapperIdentityTests.StampedAssembly(WrapperIdentityTests.Identity("net10.0-ios26.0", generatedSdk), "Acme.Kit"));
        var declaration = Path.Combine(tempDir, "pack", "Acme.Kit.props");
        var project = Path.Combine(tempDir, "label.proj");
        File.WriteAllText(project, $"""
            <Project>
              <UsingTask TaskName="SwiftBindings.Tasks.PrepareSwiftBindingPackageCopy" AssemblyFile="{SecurityElement.Escape(TasksDll)}" />
              <Target Name="Label">
                <PrepareSwiftBindingPackageCopy PackageId="Acme.Kit" PackTfm="net10.0-ios26.0" TargetKind="AppleFramework"
                    PlatformSdkVersion="{selectedSdk}" Assembly="{SecurityElement.Escape(assembly)}"
                    Toolchain=".NET 10 + Xcode" Generator="0.21.0" DeclarationFile="{SecurityElement.Escape(declaration)}" />
              </Target>
            </Project>
            """);
        var (code, output) = MSBuild(project, "Label", "minimal");
        Assert.True(exit == 0 ? code == 0 : code != 0, output);
        if (exit == 0)
            Assert.Contains($"<PlatformSdkVersion>{expected}</PlatformSdkVersion>", File.ReadAllText(declaration));
        else
            Assert.Contains(expected, output);
    }

    [Theory]
    // A pure Objective-C binding of an Apple framework: consumer targets with no natives.
    [InlineData("none", "<Project />", 0, "<NativePayload>none</NativePayload>")]
    // A pure Objective-C binding of a library: natives in lib/<tfm>/, no consumer targets.
    [InlineData("resources", null, 0, "<TargetsFile></TargetsFile>")]
    [InlineData("", "<Project />", 0, "<NativePayload>native</NativePayload>")]
    [InlineData("none", "<Project><ItemGroup><NativeReference Include=\"$(MSBuildThisFileDirectory)../../native/net10.0-ios26.0/Kit.xcframework\" /></ItemGroup></Project>", 1, "error SWIFTBIND138")]
    [InlineData("framework", "<Project />", 1, "'framework' is not a native payload")]
    public void Prepare_DeclaresWhereTheCopysNativesAre(string payload, string? targets, int exit, string expected)
    {
        var assembly = Path.Combine(tempDir, "Acme.Kit.dll");
        File.WriteAllBytes(assembly, Compile("Acme.Kit", PrimitiveOnly));
        var consumerTargets = Path.Combine(tempDir, "Acme.Kit.targets");
        if (targets is not null)
            File.WriteAllText(consumerTargets, targets);
        var declaration = Path.Combine(tempDir, "pack", "Acme.Kit.props");
        var project = Path.Combine(tempDir, "payload.proj");
        File.WriteAllText(project, $"""
            <Project>
              <UsingTask TaskName="SwiftBindings.Tasks.PrepareSwiftBindingPackageCopy" AssemblyFile="{SecurityElement.Escape(TasksDll)}" />
              <Target Name="Declare">
                <PrepareSwiftBindingPackageCopy PackageId="Acme.Kit" PackTfm="net10.0-ios26.0" TargetKind="AppleFramework"
                    PlatformSdkVersion="26.2" Assembly="{SecurityElement.Escape(assembly)}" NativePayload="{payload}"
                    ConsumerTargetsFile="{SecurityElement.Escape(consumerTargets)}"
                    Toolchain=".NET 10 + Xcode" Generator="0.21.0" DeclarationFile="{SecurityElement.Escape(declaration)}" />
              </Target>
            </Project>
            """);
        var (code, output) = MSBuild(project, "Declare", "minimal");
        Assert.True(exit == 0 ? code == 0 : code != 0, output);
        Assert.Contains(expected, exit == 0 ? File.ReadAllText(declaration) : output);
    }

    // ---- the copy check (SWIFTBIND136, SWIFTBIND139) ---------------------------------------------

    [Theory]
    [InlineData("net10.0-ios26.0", "26.2", "", 0, "SwiftBindings copy: Acme.Kit 1.0.0 -> net10.0-ios26.0 (built on .NET 10 + Xcode 26.3)")]
    [InlineData("net10.0-ios27.2", "27.2", "", 1, "error SWIFTBIND136")]
    [InlineData("net10.0-ios26.0", "27.2", "", 0, "SWIFTBIND139")]
    [InlineData("net10.0-ios26.0", "27.2", "<SwiftBindingsRequireExactCopy>true</SwiftBindingsRequireExactCopy>", 1, "error SWIFTBIND139")]
    public void CopyCheck_RunsInEveryAppBuild_OnTheDeclaredCopy(string resolvedFrom, string platformVersion, string extra, int exit, string expected)
    {
        // NuGet imported the 26.0 copy's declaration (buildTransitive/net10.0-ios26.0/Acme.Kit.props);
        // the build resolved the assembly from resolvedFrom's lib/.
        var root = Path.Combine(tempDir, "packages", "acme.kit", "1.0.0");
        foreach (var tfm in new[] { "net10.0-ios26.0", "net10.0-ios27.2" })
            Directory.CreateDirectory(Path.Combine(root, "lib", tfm));
        var dll = Path.Combine(root, "lib", resolvedFrom, "Acme.Kit.dll");
        File.WriteAllBytes(dll, Compile("Acme.Kit", "public static class Kit { }"));
        Directory.CreateDirectory(Path.Combine(root, "buildTransitive", "net10.0-ios26.0"));
        File.WriteAllText(Path.Combine(root, "buildTransitive", "net10.0-ios26.0", "Acme.Kit.targets"), "<Project />");
        var copy = $"""
            <SwiftBindingPackageCopy Include="Acme.Kit">
              <CopyTfm>net10.0-ios26.0</CopyTfm>
              <Toolchain>.NET 10 + Xcode 26.3</Toolchain>
              <LibDirectory>{SecurityElement.Escape(root)}/lib/net10.0-ios26.0/</LibDirectory>
              <NativeDirectory>{SecurityElement.Escape(root)}/native/net10.0-ios26.0/</NativeDirectory>
              <TargetsFile>{SecurityElement.Escape(root)}/buildTransitive/net10.0-ios26.0/Acme.Kit.targets</TargetsFile>
            </SwiftBindingPackageCopy>
            """;

        var (code, output) = Build("net10.0-ios", "Debug", References(compile: dll, package: "Acme.Kit"),
            $"<TargetPlatformVersion>{platformVersion}</TargetPlatformVersion>{extra}", items: copy);

        Assert.True(exit == 0 ? code == 0 : code != 0, output);
        Assert.Contains(expected, output);
    }

    [Fact]
    public void CopyCheck_ACopyThatShipsNoTargets_IsAnnounced_NotRefused()
    {
        // A pure Objective-C binding of a library: its natives travel in lib/<tfm>/, and it ships no
        // consumer targets, which its declaration states.
        var root = Path.Combine(tempDir, "packages", "acme.kit", "1.0.0");
        Directory.CreateDirectory(Path.Combine(root, "lib", "net10.0-ios26.0"));
        var dll = Path.Combine(root, "lib", "net10.0-ios26.0", "Acme.Kit.dll");
        File.WriteAllBytes(dll, Compile("Acme.Kit", "public static class Kit { }"));
        var copy = $"""
            <SwiftBindingPackageCopy Include="Acme.Kit">
              <CopyTfm>net10.0-ios26.0</CopyTfm>
              <Toolchain>.NET 10 + Xcode 26.3</Toolchain>
              <NativePayload>resources</NativePayload>
              <LibDirectory>{SecurityElement.Escape(root)}/lib/net10.0-ios26.0/</LibDirectory>
              <NativeDirectory></NativeDirectory>
              <TargetsFile></TargetsFile>
            </SwiftBindingPackageCopy>
            """;

        var (code, output) = Build("net10.0-ios", "Debug", References(compile: dll, package: "Acme.Kit"),
            "<TargetPlatformVersion>26.2</TargetPlatformVersion>", items: copy);

        Assert.True(code == 0, output);
        Assert.Contains("native=(resources) targets=(none)", output);
        Assert.DoesNotContain("SWIFTBIND136", output);
    }

    [Theory]
    // A pure Objective-C package depends on no SwiftBindings.Runtime, so its declaration checks the copy itself.
    [InlineData("none", "net10.0-ios26.0", false, 0)]
    [InlineData("none", "net10.0-ios27.2", false, 1)]
    [InlineData("resources", "net10.0-ios27.2", false, 1)]
    // With SwiftBindings.Runtime in the build its copy check decides, once.
    [InlineData("none", "net10.0-ios27.2", true, 0)]
    // A copy with native/<tfm>/ always comes with SwiftBindings.Runtime.
    [InlineData("native", "net10.0-ios27.2", false, 0)]
    public void CopyCheck_ANativelessCopy_ChecksItselfInABuildWithoutSwiftBindingsRuntime(
        string payload, string resolvedFrom, bool runtimeInBuild, int exit)
    {
        // NuGet imported the 26.0 copy's declaration; the build resolved the assembly from resolvedFrom's lib/.
        var root = Path.Combine(tempDir, "packages", "acme.kit", "1.0.0");
        foreach (var tfm in new[] { "net10.0-ios26.0", "net10.0-ios27.2" })
            Directory.CreateDirectory(Path.Combine(root, "lib", tfm));
        var dll = Path.Combine(root, "lib", resolvedFrom, "Acme.Kit.dll");
        File.WriteAllBytes(dll, Compile("Acme.Kit", "public static class Kit { }"));
        var declaration = Path.Combine(root, "buildTransitive", "net10.0-ios26.0", "Acme.Kit.props");
        Directory.CreateDirectory(Path.GetDirectoryName(declaration)!);
        var hasTargets = payload != PackageCopyDeclarationEmitter.NativePayloadResources;
        File.WriteAllText(declaration, PackageCopyDeclarationEmitter.Compose("Acme.Kit", "net10.0-ios26.0", ".NET 10 + Xcode 26.3", "26.2",
            "0.21.0", payload, hasConsumerTargets: hasTargets));
        if (hasTargets)
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(declaration)!, "Acme.Kit.targets"), "<Project />");
        var project = Path.Combine(tempDir, "objc-app.proj");
        File.WriteAllText(project, $"""
            <Project>
              <PropertyGroup>
                <TargetFramework>net10.0-ios</TargetFramework>
                {(runtimeInBuild ? $"<_SwiftBindingsTasksAssembly>{SecurityElement.Escape(TasksDll)}</_SwiftBindingsTasksAssembly>" : "")}
              </PropertyGroup>
              <Import Project="{SecurityElement.Escape(declaration)}" />
              <Target Name="ResolveAssemblyReferences">
                <ItemGroup>{References(compile: dll, package: "Acme.Kit")}</ItemGroup>
              </Target>
              <Target Name="CoreCompile" />
            </Project>
            """);

        var (code, output) = MSBuild(project, "CoreCompile", "minimal");

        Assert.True(exit == 0 ? code == 0 : code != 0, output);
        if (exit == 0)
            Assert.DoesNotContain("SWIFTBIND136", output);
        else
            Assert.Contains("error SWIFTBIND136: Acme.Kit: this build (net10.0-ios) resolved Acme.Kit.dll", output);
        // The copy is announced as SwiftBindings.Runtime's check would, by exactly one of the two.
        var announced = exit == 0 && payload != PackageCopyDeclarationEmitter.NativePayloadNative && !runtimeInBuild;
        Assert.Equal(announced, output.Contains($"SwiftBindings copy: Acme.Kit 1.0.0 -> net10.0-ios26.0 (built on .NET 10 + Xcode 26.3); lib=", StringComparison.Ordinal));
    }

    [Theory]
    // The copy is this build's own: same .NET, same platform SDK major (a 26.0 copy serves 26.x).
    [InlineData("v10.0", "26.2", false, "", 0)]
    [InlineData("v10.0", "", false, "", 0)]
    // An older platform SDK or .NET: reported, and refused when the build requires its exact copy.
    [InlineData("v10.0", "27.2", false, "built against the platform 26.0 SDK rather than this build's 27.2", 0)]
    [InlineData("v10.0", "27.2", true, "built against the platform 26.0 SDK rather than this build's 27.2", 1)]
    [InlineData("v11.0", "26.0", false, "built for .NET 10.0 rather than this build's .NET 11.0", 0)]
    public void CopyCheck_ANativelessCopy_ReportsAnOlderCopyItself(
        string frameworkVersion, string platformVersion, bool requireExact, string reason, int exit)
    {
        var project = ObjCApp($"""
            <TargetFrameworkVersion>{frameworkVersion}</TargetFrameworkVersion>
            <TargetPlatformVersion>{platformVersion}</TargetPlatformVersion>
            <SwiftBindingsRequireExactCopy>{(requireExact ? "true" : "")}</SwiftBindingsRequireExactCopy>
            """, NativelessCopy("Acme.Kit"));

        var (code, output) = MSBuild(project, "CoreCompile", "minimal");

        Assert.True(exit == 0 ? code == 0 : code != 0, output);
        if (reason.Length == 0)
            Assert.DoesNotContain("SWIFTBIND139", output);
        else
            Assert.Contains($"{(requireExact ? "error SWIFTBIND139: " : "SWIFTBIND139: ")}Acme.Kit: this build (net10.0-ios, platform {platformVersion}) uses the package's net10.0-ios26.0 copy (built on .NET 10 + Xcode 26.3), {reason}", output);
    }

    [Fact]
    public void CopyCheck_ANativelessCopy_RefusesItsMissingConsumerTargets()
    {
        var copy = NativelessCopy("Acme.Kit");
        var project = ObjCApp("", copy);
        File.Delete(copy.Targets);

        var (code, output) = MSBuild(project, "CoreCompile", "minimal");

        Assert.True(code != 0, output);
        Assert.Contains("error SWIFTBIND136: Acme.Kit: this build (net10.0-ios) uses the package's net10.0-ios26.0 copy, but its consumer targets", output);
    }

    [Fact]
    public void CopyCheck_TwoNativelessPackagesWhoseIdsDifferOnlyInPunctuation_AreBothChecked()
    {
        // Acme.Kit, imported first, resolved its assembly from another copy's lib/; Acme-Kit is laid out
        // right. Acme-Kit's declaration must not replace Acme.Kit's check.
        var project = ObjCApp("", NativelessCopy("Acme.Kit", resolvedFrom: "net10.0-ios27.2"), NativelessCopy("Acme-Kit"));

        var (code, output) = MSBuild(project, "CoreCompile", "minimal");

        Assert.True(code != 0, output);
        Assert.Contains("error SWIFTBIND136: Acme.Kit: this build (net10.0-ios) resolved Acme.Kit.dll", output);
        Assert.DoesNotContain("SWIFTBIND136: Acme-Kit", output);
    }

    /// <summary>
    /// <paramref name="packageId"/>'s pure Objective-C (NativePayload none) net10.0-ios26.0 copy as NuGet
    /// lays it out, its assembly resolved from <paramref name="resolvedFrom"/>'s lib/.
    /// </summary>
    private (string PackageId, string Declaration, string Assembly, string Targets) NativelessCopy(
        string packageId, string resolvedFrom = "net10.0-ios26.0")
    {
        var root = Path.Combine(tempDir, "packages", packageId.ToLowerInvariant(), "1.0.0");
        foreach (var tfm in new[] { "net10.0-ios26.0", "net10.0-ios27.2" })
            Directory.CreateDirectory(Path.Combine(root, "lib", tfm));
        var dll = Path.Combine(root, "lib", resolvedFrom, packageId + ".dll");
        File.WriteAllBytes(dll, Compile(packageId.Replace('-', '_'), "public static class Kit { }"));
        var copy = Path.Combine(root, "buildTransitive", "net10.0-ios26.0");
        Directory.CreateDirectory(copy);
        var declaration = Path.Combine(copy, packageId + ".props");
        File.WriteAllText(declaration, PackageCopyDeclarationEmitter.Compose(packageId, "net10.0-ios26.0", ".NET 10 + Xcode 26.3", "26.2",
            "0.21.0", PackageCopyDeclarationEmitter.NativePayloadNone, hasConsumerTargets: true));
        var targets = Path.Combine(copy, packageId + ".targets");
        File.WriteAllText(targets, "<Project />");
        return (packageId, declaration, dll, targets);
    }

    /// <summary>An app with no SwiftBindings.Runtime that references only <paramref name="copies"/>.</summary>
    private string ObjCApp(string properties, params (string PackageId, string Declaration, string Assembly, string Targets)[] copies)
    {
        var project = Path.Combine(tempDir, "objc-app.proj");
        File.WriteAllText(project, $"""
            <Project>
              <PropertyGroup>
                <TargetFramework>net10.0-ios</TargetFramework>
                {properties}
              </PropertyGroup>
              {string.Concat(copies.Select(c => $"<Import Project=\"{SecurityElement.Escape(c.Declaration)}\" />"))}
              <Target Name="ResolveAssemblyReferences">
                <ItemGroup>{string.Concat(copies.Select(c => References(compile: c.Assembly, package: c.PackageId)))}</ItemGroup>
              </Target>
              <Target Name="CoreCompile" />
            </Project>
            """);
        return project;
    }

    [Fact]
    public void CopyCheck_WithNoDeclaredCopies_IsSilent()
    {
        var dll = Fixture("lib", PrimitiveOnly);
        var (code, output) = Build("net10.0-ios", "Debug", References(compile: dll, package: "Acme.Binding"));
        Assert.True(code == 0, output);
        Assert.DoesNotContain("SwiftBindings copy:", output);
        Assert.DoesNotContain("SWIFTBIND136", output);
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
