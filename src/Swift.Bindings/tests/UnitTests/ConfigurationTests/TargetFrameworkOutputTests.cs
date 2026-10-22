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
    [InlineData(ApplePlatform.iOS, "ios", "11", "27.0", "15.0")]
    [InlineData(ApplePlatform.macOS, "macos", "11", "27.0", "14.0")]
    [InlineData(ApplePlatform.tvOS, "tvos", "11", "27.0", "15.0")]
    [InlineData(ApplePlatform.MacCatalyst, "maccatalyst", "11", "27.0", "17.0")]
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
        var target = PlatformInfoFactory.Create(platform, null, $"net11.0-{token}27.0");
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

    public void Dispose() => Directory.Delete(output, true);
}
