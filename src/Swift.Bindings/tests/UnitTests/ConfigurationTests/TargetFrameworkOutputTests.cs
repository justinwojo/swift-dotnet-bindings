// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Xml.Linq;
using BindingsGeneration.ObjC;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

public sealed class TargetFrameworkOutputTests : IDisposable
{
    private readonly string output = Path.Combine(Path.GetTempPath(), "target-output-" + Guid.NewGuid().ToString("N"));

    public TargetFrameworkOutputTests() => Directory.CreateDirectory(output);

    [Theory]
    [InlineData(ApplePlatform.iOS, "ios", "10", "26.0", "15.0")]
    [InlineData(ApplePlatform.macOS, "macos", "10", "26.0", "12.0")]
    [InlineData(ApplePlatform.tvOS, "tvos", "10", "26.0", "15.0")]
    [InlineData(ApplePlatform.MacCatalyst, "maccatalyst", "10", "26.0", "15.0")]
    [InlineData(ApplePlatform.iOS, "ios", "11", "27.2", "15.0")]
    [InlineData(ApplePlatform.macOS, "macos", "11", "27.2", "14.0")]
    [InlineData(ApplePlatform.tvOS, "tvos", "11", "27.2", "15.0")]
    [InlineData(ApplePlatform.MacCatalyst, "maccatalyst", "11", "27.2", "17.0")]
    public void EmittedProjectsAndPackageMetadata_UseOneTarget(
        ApplePlatform platform, string token, string major, string tpv, string floor)
    {
        var target = PlatformInfoFactory.Create(platform, null, $"net{major}.0-{token}");
        Assert.Equal($"net{major}.0-{token}{tpv}", target.PackTfm);
        Assert.Equal(floor, target.DefaultMinimumOS);
        var minimum = XCFrameworkMetadataExtractor.ClampMinimumOSVersion("1.0", target);
        Assert.Equal(floor, minimum);
        var framework = Path.Combine(output, "Example.xcframework");
        Directory.CreateDirectory(framework);
        var companion = ObjCBindingProjectEmitter.Emit(new ObjCBindingProjectOptions
        {
            OutputDirectory = output,
            ModuleName = "Example",
            SourceXCFrameworkPath = framework,
            PlatformInfo = target,
        }, NullLogger.Instance);
        BindingProjectEmitter.Emit(new BindingProjectEmitterOptions
        {
            OutputDirectory = output,
            ModuleName = "Example",
            SourceXCFrameworkPath = framework,
            PlatformInfo = target,
            ObjCProjectFileName = Path.GetFileName(companion),
            SwiftRuntimeVersion = "0.16.2",
            Metadata = new XCFrameworkMetadata
            {
                ModuleName = "Example",
                PackageVersion = "1.0.0",
                IsVersionPlaceholder = false,
                EffectiveMinimumOSVersion = minimum,
                Platforms = new List<string> { token },
            },
        }, NullLogger.Instance);
        var project = XDocument.Load(Path.Combine(output, target.GetDefaultSwiftPackageId("Example") + ".csproj"));
        var objc = XDocument.Load(companion);
        Assert.Equal(target.PackTfm, project.Descendants("TargetFramework").Single().Value);
        Assert.Equal(target.PackTfm, objc.Descendants("TargetFramework").Single().Value);
        Assert.Equal(floor, project.Descendants("SupportedOSPlatformVersion").Single().Value);
        Assert.Equal(floor, objc.Descendants("SupportedOSPlatformVersion").Single().Value);
        Assert.Contains(project.Descendants("None"), item =>
            ((string?)item.Attribute("PackagePath"))?.Contains($"buildTransitive/{target.PackTfm}/") == true);
        Assert.Contains(project.Descendants("BuildOutputInPackage"), item =>
            item.Attribute("Include")?.Value.Contains("_SwiftBindingCompanionBuildOutput") == true);
        Assert.Equal("[0.16.2,0.17.0)", project.Descendants("PackageReference")
            .Single(item => (string?)item.Attribute("Include") == "SwiftBindings.Runtime").Attribute("Version")!.Value);
    }

    [Theory]
    [InlineData(ApplePlatform.iOS, "ios", "15.0")]
    [InlineData(ApplePlatform.macOS, "macos", "14.0")]
    [InlineData(ApplePlatform.tvOS, "tvos", "15.0")]
    [InlineData(ApplePlatform.MacCatalyst, "maccatalyst", "17.0")]
    public void Net11Prototype_UsesRequestedFrameworkAndClampsExplicitMinimum(
        ApplePlatform platform, string token, string floor)
    {
        var target = PlatformInfoFactory.Create(platform, null, $"net11.0-{token}{PlatformInfo.Net11DefaultPlatformVersion}");
        var result = AppleSupplementPrototypeEmitter.Emit(new AppleSupplementPrototypeEmitter.Options
        {
            PrototypeDirectory = output,
            ReferencedIdentities = new[] { "Foundation.Locale.Language" },
            PlatformInfo = target,
            MinimumOSVersion = "12.0",
            SwiftRuntimeVersion = "0.16.2",
        }, NullLogger.Instance);
        var project = XDocument.Load(result.CsprojPath);
        Assert.Equal(target.PackTfm, project.Descendants("TargetFramework").Single().Value);
        Assert.Equal(floor, project.Descendants("SupportedOSPlatformVersion").Single().Value);
        Assert.Equal("SwiftBindings.Apple", project.Descendants("AssemblyName").Single().Value);
    }

    [Theory]
    [InlineData(ApplePlatform.iOS, "ios")]
    [InlineData(ApplePlatform.macOS, "macos")]
    [InlineData(ApplePlatform.tvOS, "tvos")]
    [InlineData(ApplePlatform.MacCatalyst, "maccatalyst")]
    public void VersionlessNet11Target_DefaultsToTheBuildable27_2Platform(ApplePlatform platform, string token)
    {
        // The .NET 11 GA Apple workloads (27.2.11039) accept only 27.2 as a buildable TPV on
        // every Apple platform; 27.0 is define-constants-only and fails restore with NETSDK1140.
        Assert.Equal("27.2", PlatformInfo.Net11DefaultPlatformVersion);
        var target = PlatformInfoFactory.Create(platform, null, $"net11.0-{token}");
        Assert.Equal("27.2", target.PlatformVersion);
        Assert.Equal($"net11.0-{token}27.2", target.PackTfm);
        // An explicit version still wins, and .NET 10 keeps its own default.
        Assert.Equal($"net11.0-{token}27.4", PlatformInfoFactory.Create(platform, "27.4", $"net11.0-{token}").PackTfm);
        Assert.Equal($"net10.0-{token}{PlatformInfo.DefaultPlatformVersion}",
            PlatformInfoFactory.Create(platform, null, $"net10.0-{token}").PackTfm);
    }

    /// <summary>
    /// Ties the generator's per-framework default TPV to what the installed Apple workload will
    /// actually build. Each SDK pack lists its accepted TPVs in SupportedTargetPlatforms.props;
    /// entries marked DefineConstantsOnly only add OS-version defines and fail restore when named
    /// in a TFM. Runs against the active .NET root, so each toolchain profile checks its own
    /// workloads; a framework with no installed Apple SDK pack is skipped.
    /// </summary>
    [SkippableTheory]
    [InlineData("net10.0", ApplePlatform.iOS, "iOS")]
    [InlineData("net10.0", ApplePlatform.macOS, "macOS")]
    [InlineData("net10.0", ApplePlatform.tvOS, "tvOS")]
    [InlineData("net10.0", ApplePlatform.MacCatalyst, "MacCatalyst")]
    [InlineData("net11.0", ApplePlatform.iOS, "iOS")]
    [InlineData("net11.0", ApplePlatform.macOS, "macOS")]
    [InlineData("net11.0", ApplePlatform.tvOS, "tvOS")]
    [InlineData("net11.0", ApplePlatform.MacCatalyst, "MacCatalyst")]
    public void DefaultPlatformVersion_IsBuildableByTheInstalledWorkload(
        string framework, ApplePlatform platform, string sdkName)
    {
        var packs = Path.Combine(ManagedReferenceContext.ActiveDotNetRoot(), "packs");
        var props = Directory.Exists(packs)
            ? Directory.GetDirectories(packs, $"Microsoft.{sdkName}.Sdk.{framework}_*")
                .SelectMany(Directory.GetDirectories)
                .Select(version => Path.Combine(version, "targets", $"Microsoft.{sdkName}.Sdk.SupportedTargetPlatforms.props"))
                .Where(File.Exists)
                .ToArray()
            : Array.Empty<string>();
        Skip.If(props.Length == 0, $"No {framework} {sdkName} Apple SDK pack is installed under {packs}.");

        // Several SDK packs can coexist (an older workload's pack stays on disk); the workload
        // set selects the newest platform family, so judge the default against that one.
        var newest = props.OrderByDescending(path => Version.Parse(
            Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(path))!)!)!
                .Split('_').Last())).First();
        var buildable = XDocument.Load(newest).Descendants($"{sdkName}SdkSupportedTargetPlatformVersion")
            .Where(item => !string.Equals((string?)item.Attribute("DefineConstantsOnly"), "true", StringComparison.OrdinalIgnoreCase))
            .Select(item => (string?)item.Attribute("Include"))
            .ToArray();
        var token = platform == ApplePlatform.MacCatalyst ? "maccatalyst" : sdkName.ToLowerInvariant();
        var target = PlatformInfoFactory.Create(platform, null, $"{framework}-{token}");
        Assert.True(buildable.Contains(target.PlatformVersion),
            $"{target.PackTfm} is not buildable with {newest}; buildable versions: {string.Join(", ", buildable)}");
    }

    public void Dispose() => Directory.Delete(output, true);
}
