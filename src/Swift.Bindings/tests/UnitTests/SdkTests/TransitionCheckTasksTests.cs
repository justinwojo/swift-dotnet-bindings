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
        Assert.Contains("Regenerate the binding with the current SwiftBindings.Sdk", output);
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
