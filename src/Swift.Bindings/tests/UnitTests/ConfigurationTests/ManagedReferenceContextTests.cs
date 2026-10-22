// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging;
using System.CommandLine;
using Xunit;

namespace BindingsGeneration.Tests;

public sealed class ManagedReferenceContextTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "managed-refs-" + Guid.NewGuid().ToString("N"));
    private string Sdk => Path.Combine(root, "sdk", "10.0.107");

    public ManagedReferenceContextTests()
    {
        Directory.CreateDirectory(Sdk);
        File.WriteAllText(Path.Combine(Sdk, "Microsoft.NETCoreSdk.BundledVersions.props"), """
            <Project><ItemGroup><KnownFrameworkReference Include="Microsoft.NETCore.App"
              TargetFramework="net10.0" TargetingPackVersion="10.0.7" />
            </ItemGroup></Project>
            """);
        WriteCore("10.0.7", "net10.0");
    }

    [Theory]
    [InlineData(ApplePlatform.iOS, "Microsoft.iOS", "27.1")]
    [InlineData(ApplePlatform.macOS, "Microsoft.macOS", "27.0")]
    [InlineData(ApplePlatform.tvOS, "Microsoft.tvOS", "27.0")]
    [InlineData(ApplePlatform.MacCatalyst, "Microsoft.MacCatalyst", "27.0")]
    public void RequestedPlatformAndVersion_SelectExactFamily(ApplePlatform platform, string name, string tpv)
    {
        var expected = WriteApple(name, tpv);
        WriteApple(name, "99.0");
        WriteApple("Microsoft.iOS", "26.0");
        WriteCore("10.0.99", "net10.0");
        WriteCore("11.0.0", "net11.0");

        var context = Resolve(platform, tpv);

        Assert.True(context.IsAvailable);
        Assert.Equal(expected, context.AppleReferencePath);
        Assert.Equal("10.0.7", context.NetCoreRefPackVersion);
        Assert.DoesNotContain(context.ReferencePaths, p => p.Contains("99.0") || p.Contains("net11.0"));
        Assert.Contains(expected, CSharpProbeReferenceSet.Resolve(context).MetadataReferencePaths);
    }

    [Fact]
    public void InstallingNewFamilies_DoesNotChangeOldContext()
    {
        WriteApple("Microsoft.iOS", "26.2");
        var before = Resolve(ApplePlatform.iOS, "26.2");
        WriteApple("Microsoft.iOS", "27.1");
        WriteCore("10.0.13", "net10.0");
        var after = Resolve(ApplePlatform.iOS, "26.2");
        Assert.Equal(before.ReferencePaths, after.ReferencePaths);
        Assert.Equal(before.CacheKey, after.CacheKey);
    }

    [Fact]
    public void MissingExactFamily_DiagnosesAndNeverUsesNewerReferences()
    {
        WriteApple("Microsoft.iOS", "27.1");
        var context = Resolve(ApplePlatform.iOS, "26.2");
        Assert.False(context.IsAvailable);
        Assert.Empty(context.ReferencePaths);
        Assert.Contains(context.Diagnostics, d => d.Contains("Microsoft.iOS.Ref.net10.0_26.2"));
        var logger = new ReferenceLogger();
        BindingsGeneratorCommand.RequireReferenceAvailability(context, explicitTarget: false, logger);
        Assert.Contains(logger.Messages, m => m.Level == LogLevel.Warning && m.Message.Contains("26.2"));
        Assert.Throws<InvalidOperationException>(() =>
            BindingsGeneratorCommand.RequireReferenceAvailability(context, explicitTarget: true, logger));
        Assert.Null(AppleTypeSurfaceIndex.GetForContext(context));
        WriteApple("Microsoft.iOS", "26.2");
        Assert.True(Resolve(ApplePlatform.iOS, "26.2").IsAvailable);
    }

    [Fact]
    public void AmbiguousExactFamily_DoesNotChooseHighestVersion()
    {
        WriteApple("Microsoft.iOS", "26.2", "26.2.1");
        WriteApple("Microsoft.iOS", "26.2", "26.2.2");
        var context = Resolve(ApplePlatform.iOS, "26.2");
        Assert.False(context.IsAvailable);
        Assert.Contains(context.Diagnostics, d => d.Contains("found 2"));
    }

    [Fact]
    public void MissingPinnedBcl_DoesNotChooseAnotherInstalledPatch()
    {
        WriteApple("Microsoft.iOS", "26.2");
        Directory.Delete(Path.Combine(root, "packs", "Microsoft.NETCore.App.Ref", "10.0.7"), true);
        WriteCore("10.0.13", "net10.0");
        var context = Resolve(ApplePlatform.iOS, "26.2");
        Assert.False(context.IsAvailable);
        Assert.Contains(context.Diagnostics, d => d.Contains("pinned version '10.0.7'"));
    }

    [Fact]
    public void AppleSdkPin_AllowsDeclaredFamilyMappingAndExactNugetRestore()
    {
        var sdk = Path.Combine(root, "packs", "Microsoft.iOS.Sdk.net10.0_26.0", "26.0.1", "targets");
        Directory.CreateDirectory(sdk);
        File.WriteAllText(Path.Combine(sdk, "Microsoft.iOS.Sdk.Versions.props"), """
            <Project><ItemGroup><KnownFrameworkReference Include="Microsoft.iOS"
              TargetFramework="net10.0" TargetingPackName="Microsoft.iOS.Ref.net10.0_26.2"
              TargetingPackVersion="26.2.123" /></ItemGroup></Project>
            """);
        WriteApple("Microsoft.iOS", "26.2", "26.2.999");
        var cache = Path.Combine(root, "nuget");
        var restored = Path.Combine(cache, "microsoft.ios.ref.net10.0_26.2", "26.2.123", "ref", "net10.0", "Microsoft.iOS.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(restored)!);
        File.WriteAllText(restored, "exact restored references");
        var target = PlatformInfoFactory.Create(ApplePlatform.iOS, "26.0");
        var context = ManagedReferenceContext.ResolveInstalled(target, root, Sdk, cache);
        Assert.True(context.IsAvailable);
        Assert.Equal(restored, context.AppleReferencePath);
        Assert.DoesNotContain(context.ReferencePaths, p => p.Contains("26.2.999"));
    }

    [Fact]
    public void MsbuildContext_ValidatesTargetAndAcceptsResolvedLowercasePackageMapping()
    {
        var target = PlatformInfoFactory.Create(ApplePlatform.iOS, "26.0");
        var apple = Path.Combine(root, "nuget", "microsoft.ios.ref.net10.0_26.2", "26.2.123", "ref", "net10.0", "Microsoft.iOS.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(apple)!);
        File.WriteAllText(apple, "resolved Apple references");
        var file = WriteReferenceFile("msbuild-target-framework=net10.0-ios",
            "msbuild-target-platform-version=26.0", apple, CorePath("10.0.7", "net10.0"));
        Assert.Equal(apple, ManagedReferenceContext.FromReferenceFile(target, file).AppleReferencePath);
        Assert.Throws<InvalidOperationException>(() => ManagedReferenceContext.FromReferenceFile(
            PlatformInfoFactory.Create(ApplePlatform.iOS, "27.1"), file));
    }

    [Fact]
    public void ExplicitReferences_AcceptLowercaseExactNugetFamily()
    {
        var apple = WriteApple("Microsoft.iOS", "26.2");
        var lower = Path.Combine(root, "nuget", "microsoft.ios.ref.net10.0_26.2", "1.0.0", "ref", "net10.0", "Microsoft.iOS.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(lower)!);
        File.Copy(apple, lower);
        var file = WriteReferenceFile(lower, CorePath("10.0.7", "net10.0"));
        Assert.True(ManagedReferenceContext.FromReferenceFile(
            PlatformInfoFactory.Create(ApplePlatform.iOS, "26.2"), file).IsAvailable);
    }

    [Theory]
    [InlineData("Microsoft.iOS", "27.1", "net10.0")]
    [InlineData("Microsoft.macOS", "26.2", "net10.0")]
    [InlineData("Microsoft.iOS", "26.2", "net11.0")]
    public void ExplicitReferences_RejectInconsistentTargets(string name, string tpv, string framework)
    {
        var apple = WriteApple(name, tpv, framework: framework);
        var file = WriteReferenceFile(apple, CorePath("10.0.7", "net10.0"));
        Assert.Throws<InvalidOperationException>(() => ManagedReferenceContext.FromReferenceFile(
            PlatformInfoFactory.Create(ApplePlatform.iOS, "26.2"), file));
    }

    [Fact]
    public void ExplicitReferences_RejectMissingFilesAndMissingRequiredAssemblies()
    {
        var target = PlatformInfoFactory.Create(ApplePlatform.iOS, "26.2");
        Assert.Throws<InvalidOperationException>(() => ManagedReferenceContext.FromReferenceFile(target,
            WriteReferenceFile(Path.Combine(root, "missing.dll"))));
        Assert.Throws<InvalidOperationException>(() => ManagedReferenceContext.FromReferenceFile(target,
            WriteReferenceFile(CorePath("10.0.7", "net10.0"))));
    }

    [Fact]
    public void ContextScopes_RestoreOuterContextAndProbeUsesSameReferences()
    {
        WriteApple("Microsoft.iOS", "26.2");
        WriteApple("Microsoft.macOS", "27.0");
        var old = Resolve(ApplePlatform.iOS, "26.2");
        var newer = Resolve(ApplePlatform.macOS, "27.0");
        using (AppleTypeSurfaceIndex.UseReferenceContext(old))
        {
            using (AppleTypeSurfaceIndex.UseReferenceContext(newer))
            {
                Assert.Same(newer, AppleTypeSurfaceIndex.AmbientReferences);
                Assert.Contains(newer.AppleReferencePath!, CSharpProbeReferenceSet.Resolve().MetadataReferencePaths);
                Assert.DoesNotContain(old.AppleReferencePath!, CSharpProbeReferenceSet.Resolve().MetadataReferencePaths);
            }
            Assert.Same(old, AppleTypeSurfaceIndex.AmbientReferences);
        }
    }

    [Fact]
    public async Task ParallelScopes_DoNotExchangeTargetContexts()
    {
        WriteApple("Microsoft.iOS", "26.2");
        WriteApple("Microsoft.iOS", "27.1");
        var contexts = new[] { Resolve(ApplePlatform.iOS, "26.2"), Resolve(ApplePlatform.iOS, "27.1") };
        await Task.WhenAll(contexts.Select(async context =>
        {
            using var scope = AppleTypeSurfaceIndex.UseReferenceContext(context);
            await Task.Yield();
            Assert.Same(context, AppleTypeSurfaceIndex.AmbientReferences);
            Assert.Contains(context.AppleReferencePath!, CSharpProbeReferenceSet.Resolve().MetadataReferencePaths);
        }));
    }

    [Fact]
    public void SurfaceCache_UsesReferenceContentAndTargetIdentity()
    {
        var oldPath = WriteApple("Microsoft.iOS", "26.2");
        var newPath = WriteApple("Microsoft.iOS", "27.1");
        EmitApple(oldPath, "public class ProfileType {}", "public class OnlyOld {}");
        EmitApple(newPath, "public enum ProfileType { Zero }", "public class OnlyNew {}");
        CopyCoreMetadata();
        var old = Resolve(ApplePlatform.iOS, "26.2");
        var newer = Resolve(ApplePlatform.iOS, "27.1");
        var oldIndex = AppleTypeSurfaceIndex.GetForContext(old);
        var newIndex = AppleTypeSurfaceIndex.GetForContext(newer);
        Assert.NotNull(oldIndex);
        Assert.NotNull(newIndex);
        Assert.True(oldIndex.TryResolveQualified("UIKit", "ProfileType", out var oldType));
        Assert.True(newIndex.TryResolveQualified("UIKit", "ProfileType", out var newType));
        Assert.Equal(AppleTypeSurfaceKind.Class, oldType!.Kind);
        Assert.Equal(AppleTypeSurfaceKind.Enum, newType!.Kind);
        Assert.False(oldIndex.TryResolveQualified("UIKit", "OnlyNew", out _));
        Assert.True(newIndex.TryResolveQualified("UIKit", "OnlyNew", out _));
        Assert.Same(oldIndex, AppleTypeSurfaceIndex.GetForContext(Resolve(ApplePlatform.iOS, "26.2")));

        EmitApple(oldPath, "public enum ProfileType { Zero }", "public class Replaced {}");
        var replaced = Resolve(ApplePlatform.iOS, "26.2");
        Assert.NotEqual(old.CacheKey, replaced.CacheKey);
        Assert.True(AppleTypeSurfaceIndex.GetForContext(replaced)!.TryResolveQualified("UIKit", "Replaced", out _));
    }

    [Theory]
    [InlineData("net10.0-ios26.2", true)]
    [InlineData("net10.0-ios", true)]
    [InlineData("net10.0-ios27.1", false)]
    [InlineData("net10.0-macos26.2", false)]
    [InlineData("net12.0-ios26.2", false)]
    public void FrameworkValidation_PreventsMislabelledOutput(string tfm, bool valid)
    {
        if (valid)
            Assert.Equal("net10.0-ios26.2", PlatformInfoFactory.Create(ApplePlatform.iOS, "26.2", tfm).PackTfm);
        else
            Assert.Throws<ArgumentException>(() => PlatformInfoFactory.Create(ApplePlatform.iOS, "26.2", tfm));
    }

    [Theory]
    [InlineData(ApplePlatform.iOS, "net10.0-ios27.1", "27.1")]
    [InlineData(ApplePlatform.macOS, "net10.0-macos27.0", "27.0")]
    [InlineData(ApplePlatform.tvOS, "net10.0-tvos27.0", "27.0")]
    [InlineData(ApplePlatform.MacCatalyst, "net10.0-maccatalyst27.0", "27.0")]
    public void FullFramework_InfersPlatformVersionAndPackLabel(ApplePlatform platform, string tfm, string tpv)
    {
        var target = PlatformInfoFactory.Create(platform, null, tfm);
        Assert.Equal(tpv, target.PlatformVersion);
        Assert.Equal(tfm, target.PackTfm);
        Assert.Equal(target, PlatformInfoFactory.Create(platform, tpv, tfm));
    }

    [Theory]
    [InlineData(null, "26.0")]
    [InlineData("26.2", "26.2")]
    public void VersionlessFramework_RetainsDefaultOrExplicitVersion(string? version, string expected)
    {
        var target = PlatformInfoFactory.Create(ApplePlatform.iOS, version, "net10.0-ios");
        Assert.Equal(expected, target.PlatformVersion);
    }

    [Theory]
    [InlineData("net9.0-ios27.1")]
    [InlineData("net12.0-ios27.1")]
    [InlineData("net10.0-macos27.1")]
    [InlineData("net10.0-ios27")]
    [InlineData("net10.0-ios27.1.0")]
    [InlineData("net10.0-ios27.1-preview")]
    [InlineData("net10.0-ios27.1 ")]
    [InlineData("net10.0-ios27.1\n")]
    [InlineData(" net10.0-ios27.1")]
    [InlineData("net10.0-iossimulator27.1")]
    [InlineData("net10.0")]
    public void Inference_RejectsUnsupportedOrMalformedFrameworks(string tfm)
    {
        Assert.Throws<ArgumentException>(() => PlatformInfoFactory.Create(ApplePlatform.iOS, null, tfm));
    }

    [Fact]
    public void CliFullFramework_InfersVersionWithoutSeparatePlatformVersionOption()
    {
        var options = new CliOptions();
        var parsed = options.CreateRootCommand().Parse(new[] { "--target-framework", "net10.0-ios27.1" });
        Assert.Empty(parsed.Errors);
        Assert.Null(parsed.GetValueForOption(options.PlatformVersion));
        var target = PlatformInfoFactory.Create(ApplePlatform.iOS,
            parsed.GetValueForOption(options.PlatformVersion), parsed.GetValueForOption(options.TargetFramework));
        Assert.Equal("net10.0-ios27.1", target.PackTfm);
        Assert.Throws<ArgumentException>(() => PlatformInfoFactory.Create(ApplePlatform.iOS, "26.2",
            parsed.GetValueForOption(options.TargetFramework)));
    }

    [Theory]
    [InlineData(ApplePlatform.iOS, "ios", "Microsoft.iOS")]
    [InlineData(ApplePlatform.macOS, "macos", "Microsoft.macOS")]
    [InlineData(ApplePlatform.tvOS, "tvos", "Microsoft.tvOS")]
    [InlineData(ApplePlatform.MacCatalyst, "maccatalyst", "Microsoft.MacCatalyst")]
    public void Net11References_StayWithinRequestedFramework(ApplePlatform platform, string token, string name)
    {
        File.WriteAllText(Path.Combine(Sdk, "Microsoft.NETCoreSdk.BundledVersions.props"), """
            <Project><ItemGroup>
              <KnownFrameworkReference Include="Microsoft.NETCore.App" TargetFramework="net10.0" TargetingPackVersion="10.0.7" />
              <KnownFrameworkReference Include="Microsoft.NETCore.App" TargetFramework="net11.0" TargetingPackVersion="11.0.0-rc.2" />
            </ItemGroup></Project>
            """);
        WriteCore("11.0.0-rc.2", "net11.0");
        WriteApple(name, "27.0");
        var apple = WriteApple(name, "27.0", framework: "net11.0");
        var target = PlatformInfoFactory.Create(platform, null, $"net11.0-{token}");
        var context = ManagedReferenceContext.ResolveInstalled(target, root, Sdk);
        Assert.True(context.IsAvailable);
        Assert.Equal(apple, context.AppleReferencePath);
        Assert.Equal("11.0.0-rc.2", context.NetCoreRefPackVersion);
        Assert.All(context.ReferencePaths, p => Assert.Contains("/ref/net11.0/", p));
        var file = WriteReferenceFile($"msbuild-target-framework=net11.0-{token}",
            "msbuild-target-platform-version=27.0", apple, CorePath("11.0.0-rc.2", "net11.0"));
        Assert.Equal(context.ReferencePaths, ManagedReferenceContext.FromReferenceFile(target, file).ReferencePaths);
        // Header identity must match even when the resolved assembly paths look correct.
        File.WriteAllText(file, File.ReadAllText(file).Replace("msbuild-target-framework=net11.0-", "msbuild-target-framework=net10.0-"));
        Assert.Throws<InvalidOperationException>(() => ManagedReferenceContext.FromReferenceFile(target, file));
    }

    private ManagedReferenceContext Resolve(ApplePlatform platform, string tpv) =>
        ManagedReferenceContext.ResolveInstalled(PlatformInfoFactory.Create(platform, tpv), root, Sdk);

    private string WriteApple(string name, string tpv, string version = "1.0.0", string framework = "net10.0")
    {
        var path = Path.Combine(root, "packs", $"{name}.Ref.{framework}_{tpv}", version, "ref", framework, name + ".dll");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, name + tpv + version);
        return path;
    }

    private string CorePath(string version, string framework) => Path.Combine(root, "packs", "Microsoft.NETCore.App.Ref", version, "ref", framework, "System.Runtime.dll");

    private void WriteCore(string version, string framework)
    {
        var path = CorePath(version, framework);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, version);
    }

    private string WriteReferenceFile(params string[] paths)
    {
        var file = Path.Combine(root, "references.txt");
        File.WriteAllLines(file, paths);
        return file;
    }

    private static string RuntimeAssemblyPath(string name)
    {
        // Test hosts can list a TPA more than once. These fixtures need the executing runtime's
        // own facade/corelib pair, so select their exact shared-framework directory directly.
        var path = Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), name);
        Assert.True(File.Exists(path), $"Executing runtime assembly missing: {path}");
        return path;
    }

    private void CopyCoreMetadata()
    {
        var directory = Path.GetDirectoryName(CorePath("10.0.7", "net10.0"))!;
        foreach (var name in new[] { "System.Runtime.dll", "System.Private.CoreLib.dll" })
            File.Copy(RuntimeAssemblyPath(name), Path.Combine(directory, name), overwrite: true);
    }

    private static void EmitApple(string path, params string[] declarations)
    {
        var compilation = CSharpCompilation.Create("Microsoft.iOS",
            new[] { CSharpSyntaxTree.ParseText("namespace UIKit { " + string.Join(" ", declarations) + " }") },
            new[] { MetadataReference.CreateFromFile(RuntimeAssemblyPath("System.Private.CoreLib.dll")) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        File.WriteAllBytes(path, stream.ToArray());
    }

    private sealed class ReferenceLogger : ILogger
    {
        internal readonly List<(LogLevel Level, string Message)> Messages = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add((level, formatter(state, exception)));
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}
