// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>Execute the actual harness writers without Nuke, workloads, restore, or a device.</summary>
public class Net11MixedPackageLaneTests
{
    private delegate string Writer(string kind, string directory, string framework, string configuration,
        bool coreClr, bool device);
    private delegate string AppleVersionWriter(string leg, string framework, string configuration,
        bool coreClr, bool device);
    private sealed record HarnessWriters(Writer Consumer, AppleVersionWriter AppleVersion);
    private static readonly Lazy<HarnessWriters> Write = new(CompileWriters);

    [Theory]
    [InlineData("net10.0", "Debug", false, false, false)]
    [InlineData("net10.0", "Debug", false, true, true)]
    [InlineData("net11.0", "Debug", false, false, false)]
    [InlineData("net11.0", "Release", false, false, false)]
    [InlineData("net11.0", "Debug", true, true, false)]
    [InlineData("net11.0", "Release", true, true, false)]
    [InlineData("net11.0", "Debug", false, true, true)]
    [InlineData("net11.0", "Release", false, true, true)]
    public void PackedConsumerTargetsSelectedFrameworkAndEngineWithOneReference(
        string framework, string configuration, bool coreClr, bool device, bool aot)
    {
        WithConsumer("pack", framework, configuration, coreClr, device, (project, program, version) =>
        {
            Assert.Equal(framework + "-ios", Value(project, "TargetFramework"));
            Assert.Equal(device ? "ios-arm64" : "iossimulator-arm64", Value(project, "RuntimeIdentifier"));
            Assert.Equal(aot.ToString().ToLowerInvariant(), Value(project, "PublishAot"));
            Assert.Equal(aot.ToString().ToLowerInvariant(), Value(project, "PublishAotUsingRuntimePack"));
            Assert.Equal(framework == "net11.0" ? "false" : null, Value(project, "UseMonoRuntime"));
            var suppressedWarnings = Value(project, "NoWarn").Split(';');
            Assert.Equal(framework == "net11.0" && coreClr && device,
                suppressedWarnings.Contains("IL2121"));
            Assert.DoesNotContain("IL2104", suppressedWarnings);
            var reference = Assert.Single(project.Descendants("PackageReference"));
            Assert.Equal("MixedPackFixture.SbMixedPack", reference.Attribute("Include")!.Value);
            Assert.Equal(version, reference.Attribute("Version")!.Value);
            AssertNoAppRoots(project);
            AssertLiveBanner(program);
        });
    }

    [Theory]
    [InlineData("net10.0", "Debug")]
    [InlineData("net11.0", "Debug")]
    [InlineData("net11.0", "Release")]
    public void DirectConsumerUsesSelectedSdkTargetAndNeverRequestsAot(string framework, string configuration)
    {
        WithConsumer("direct", framework, configuration, false, false, (project, program, version) =>
        {
            Assert.Equal("SwiftBindings.Sdk/" + version, project.Root!.Attribute("Sdk")!.Value);
            Assert.Equal(framework + "-ios", Value(project, "TargetFramework"));
            Assert.Equal("iossimulator-arm64", Value(project, "RuntimeIdentifier"));
            Assert.Equal("false", Value(project, "PublishAot"));
            Assert.Equal("false", Value(project, "IsPackable"));
            Assert.Single(project.Descendants("SwiftFramework"));
            Assert.Empty(project.Descendants("PackageReference"));
            AssertNoAppRoots(project);
            AssertLiveBanner(program);
        });
    }

    [Theory]
    [InlineData("net10.0")]
    [InlineData("net11.0")]
    public void PackedBindingUsesSameTargetAsConsumer(string framework)
    {
        WithConsumer("fixture", framework, "Debug", false, false, (project, _, version) =>
        {
            Assert.Equal(framework + "-ios", Value(project, "TargetFramework"));
            Assert.Equal("SwiftBindings.Sdk/" + version, project.Root!.Attribute("Sdk")!.Value);
            Assert.Equal(version, Value(project, "PackageVersion"));
            Assert.Equal("true", Value(project, "IsPackable"));
            Assert.Single(project.Descendants("SwiftFramework"));
        });
    }

    [Theory]
    [InlineData("net10.0")]
    [InlineData("net11.0")]
    public void AppleConsumerPreservesNativeAotPackageRootingContract(string framework)
    {
        WithConsumer("apple", framework, "Debug", false, true, (project, program, version) =>
        {
            Assert.Equal(framework + "-ios", Value(project, "TargetFramework"));
            Assert.Equal("true", Value(project, "PublishAot"));
            var reference = Assert.Single(project.Descendants("PackageReference"));
            Assert.Equal("SwiftBindings.Apple", reference.Attribute("Include")!.Value);
            Assert.Equal(version, reference.Attribute("Version")!.Value);
            AssertNoAppRoots(project);
            AssertLiveBanner(program);
            Assert.Contains("LANGUAGE_ELEMENTS:", program);
            Assert.True(version.Length <= "26.2.0-b2".Length, "Throwaway Apple version must preserve NuGet path-length headroom.");
            if (framework == "net11.0") Assert.NotEqual("26.2.0-b2", version);
            else Assert.Equal("26.2.0-b2", version);
        });
    }

    [Fact]
    public void VersionsSeparateFrameworkConfigurationAndEngineWhileLegacyVersionIsUnchanged()
    {
        var versions = new HashSet<string>();
        foreach (var (configuration, coreClr) in new[] { ("Debug", false), ("Release", false), ("Debug", true), ("Release", true) })
            WithConsumer("pack", "net11.0", configuration, coreClr, true,
                (_, _, version) => Assert.True(versions.Add(version), "Lane versions must not share NuGet cache entries."));
        WithConsumer("pack", "net10.0", "Debug", false, true,
            (_, _, version) => Assert.Equal("0.0.0-mixedpack", version));
    }

    [Fact]
    public void MixedAppleVersionsAreCompactAndUniqueAcrossSupportedLegsAndLanes()
    {
        var versions = new HashSet<string>();
        foreach (var leg in new[] { "pack", "direct" })
        foreach (var framework in new[] { "net10.0", "net11.0" })
        foreach (var configuration in framework == "net11.0" ? new[] { "Debug", "Release" } : new[] { "Debug" })
        foreach (bool coreClr in framework == "net11.0" ? new[] { false, true } : new[] { false })
        {
            var version = Write.Value.AppleVersion(leg, framework, configuration, coreClr, true);
            var lane = $"{leg}/{framework}/{configuration}/deviceCoreClr={coreClr}";
            Assert.True(versions.Add(version), $"Apple package version {version} collides for {lane}.");
            Assert.True(version.Length <= 9, $"Apple version {version} for {lane} exceeds its Windows path budget.");
            Assert.StartsWith("26.2.0-", version);
        }
    }

    [Theory]
    [InlineData("pack", "0.0.0-mixedpack")]
    [InlineData("direct", "0.0.0-mixeddirect")]
    public void CompactAppleVersionsPreserveSdkAndRuntimeVersions(string leg, string legacyVersion)
    {
        WithConsumer(leg, "net10.0", "Debug", false, false,
            (_, _, version) => Assert.Equal(legacyVersion, version));
        foreach (var configuration in new[] { "Debug", "Release" })
        foreach (bool coreClr in new[] { false, true })
            WithConsumer(leg, "net11.0", configuration, coreClr, true,
                (_, _, version) => Assert.Equal(
                    $"{legacyVersion}-net11.0-{configuration.ToLowerInvariant()}-{(coreClr ? "coreclr" : "nativeaot")}", version));
    }

    [Theory]
    [InlineData("net10.0")]
    [InlineData("net11.0")]
    public void MixedAppleVersionRejectsUnsupportedLegs(string framework)
    {
        foreach (var leg in new[] { "", "apple", "fixture", "PACK" })
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                Write.Value.AppleVersion(leg, framework, "Debug", false, false));
    }

    [Fact]
    public void Net11SimulatorSelectsCoreClrWithDefaultDevicePreference()
    {
        var lane = RuntimeTestLane.Create("net11.0", null, "Release", false, false, false);
        Assert.False(lane.DeviceCoreClr);
        Assert.Equal("coreclr", lane.Engine(device: false));
        Assert.Equal("nativeaot", lane.Engine(device: true));
        WithConsumer("pack", "net11.0", "Release", false, false, (project, _, _) =>
        {
            Assert.Equal("false", Value(project, "UseMonoRuntime"));
            Assert.Equal("false", Value(project, "PublishAot"));
        });
    }

    private static string Value(XDocument project, string element) => project.Descendants(element).SingleOrDefault()?.Value;

    private static void AssertNoAppRoots(XDocument project)
    {
        Assert.Empty(project.Descendants("TrimmerRootDescriptor"));
        Assert.Empty(project.Descendants("IlcArg"));
        Assert.Empty(project.Descendants("RuntimeHostConfigurationOption"));
    }

    private static void AssertLiveBanner(string program)
    {
        // Require observations from the running process, never a configured engine string.
        Assert.Contains("SwiftRuntimeInfo.IsMonoRuntime", program);
        Assert.Contains("SwiftRuntimeInfo.IsNativeAotRuntime", program);
        Assert.Contains("AppContext.TargetFrameworkName", program);
        Assert.Contains("RuntimeInformation.FrameworkDescription", program);
        Assert.DoesNotContain(CSharpSyntaxTree.ParseText(program).GetDiagnostics(),
            d => d.Severity == DiagnosticSeverity.Error);
    }

    private static void WithConsumer(string kind, string framework, string configuration, bool coreClr, bool device,
        Action<XDocument, string, string> verify)
    {
        var directory = Path.Combine(Path.GetTempPath(), "mixed-lane-writer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var version = Write.Value.Consumer(kind, directory, framework, configuration, coreClr, device);
            var project = XDocument.Load(Directory.GetFiles(directory, "*.csproj").Single());
            var programPath = Path.Combine(directory, "Program.cs");
            verify(project, File.Exists(programPath) ? File.ReadAllText(programPath) : "", version);
        }
        finally { Directory.Delete(directory, true); }
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Test-only dynamically compiled production writers, retained in full.")]
    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "Test-only dynamically compiled fixture entry, retained in full.")]
    private static HarnessWriters CompileWriters()
    {
        var root = LocateRepoRoot();
        var sources = new[] { "MixedPack", "MixedDirect", "ApplePackageConsumer" }
            .Select(name => CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root, "build", $"Build.BindingTests.{name}.cs"))).GetRoot())
            .ToArray();
        var names = new[] { "WriteMixedPackConsumerApp", "WriteMixedPackFixture", "WriteMixedDirectConsumerApp",
            "WriteApplePackageConsumer", "MixedPackNuGetConfig", "MixedConsumerRuntimeProperties", "MixedConsumerRuntimeBanner",
            "MixedPackageLaneVersion", "MixedPackageLaneSuffix", "MixedPackageAppleVersion", "ApplePackageConsumerVersionFor" };
        var methods = sources.SelectMany(source => source.DescendantNodes().OfType<MethodDeclarationSyntax>())
            .Where(method => names.Contains(method.Identifier.Text)).Select(method => method.ToString());
        var constants = sources.SelectMany(source => source.DescendantNodes().OfType<FieldDeclarationSyntax>())
            .Where(field => field.Modifiers.Any(SyntaxKind.ConstKeyword)).Select(field => field.ToString());
        var appleVersionProperties = sources.SelectMany(source => source.DescendantNodes().OfType<PropertyDeclarationSyntax>())
            .Where(property => property.Identifier.Text is "MixedPackLaneAppleVersion" or "MixedDirectLaneAppleVersion")
            .Select(property => property.ToString());
        var fixture = $$"""
            #nullable enable
            using System;
            using System.IO;
            public sealed record AbsolutePath(string Value) {
                public static implicit operator AbsolutePath(string value) => new(value);
                public static implicit operator string(AbsolutePath value) => value.Value;
                public static AbsolutePath operator /(AbsolutePath path, string suffix) => new(Path.Combine(path.Value, suffix));
                public override string ToString() => Value;
            }
            public class WriterFixture {
                {{string.Join("\n", constants)}}
                {{string.Join("\n", methods)}}
                {{string.Join("\n", appleVersionProperties)}}
                private RuntimeTestLane RuntimeLane { get; init; } = null!;
                public static string AppleVersion(string leg, string framework, string configuration, bool coreClr, bool device) {
                    var lane = RuntimeTestLane.Create(framework, null, configuration, coreClr, device, false);
                    var fixture = new WriterFixture { RuntimeLane = lane };
                    return leg switch {
                        "pack" => fixture.MixedPackLaneAppleVersion,
                        "direct" => fixture.MixedDirectLaneAppleVersion,
                        _ => MixedPackageAppleVersion(leg, lane)
                    };
                }
                public static string Run(string kind, string directory, string framework, string configuration, bool coreClr, bool device) {
                    var lane = RuntimeTestLane.Create(framework, null, configuration, coreClr, device, false);
                    var version = kind == "apple" ? ApplePackageConsumerVersionFor(lane, apple: true)
                        : MixedPackageLaneVersion(kind == "direct" ? MixedDirectVersion : MixedPackVersion, lane);
                    switch (kind) {
                        case "pack": WriteMixedPackConsumerApp(directory, MixedPackModule, MixedPackProbeClass, device ? "ios-arm64" : "iossimulator-arm64", lane, version); break;
                        case "direct": WriteMixedDirectConsumerApp(directory, MixedDirectModule, MixedDirectProbeClass, "/fixture.xcframework", lane, version); break;
                        case "fixture": WriteMixedPackFixture(directory, "/packages", MixedPackModule, "/fixture.xcframework", lane, version); break;
                        case "apple": WriteApplePackageConsumer(directory, "/packages", lane, version); break;
                        default: throw new ArgumentException(kind);
                    }
                    return version;
                }
            }
            """;
        var trees = new[] { "RuntimeTestLane.cs", "DeviceRuntimeFlavorGate.cs" }
            .Select(name => CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root, "build", "Models", name))))
            .Append(CSharpSyntaxTree.ParseText(fixture));
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))
            .Split(Path.PathSeparator).Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create("MixedLaneWriters" + Guid.NewGuid().ToString("N"), trees, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var stream = new MemoryStream();
        var emitted = compilation.Emit(stream);
        Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));
        var fixtureType = Assembly.Load(stream.ToArray()).GetType("WriterFixture");
        return new HarnessWriters(fixtureType.GetMethod("Run").CreateDelegate<Writer>(),
            fixtureType.GetMethod("AppleVersion").CreateDelegate<AppleVersionWriter>());
    }

    private static string LocateRepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "build", "Build.BindingTests.MixedPack.cs"))) return directory.FullName;
        throw new InvalidOperationException("Could not locate mixed consumer harness sources.");
    }
}
