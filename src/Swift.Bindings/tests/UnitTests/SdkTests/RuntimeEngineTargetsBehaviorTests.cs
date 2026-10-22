// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Executes the packaged Runtime target after workload evaluation, without requiring
/// an installed mobile workload. The fixture pins the engine-resolution excerpt from
/// macios dotnet-11.0.1xx-rc2-12212 Xamarin.Shared.Sdk.props (lines 37-47); the legacy
/// excerpt is identical except its UseMonoRuntime defaults (macOS/AOT false, else true).
/// Both versions disable the generic native publish hook and select runtime-pack AOT
/// (net11 lines 287-292, net10 lines 196-201); Apple's targets invoke IlcCompile directly.
/// These are integration-contract tests, not qualification of the mobile runtime.
/// </summary>
public sealed class RuntimeEngineTargetsBehaviorTests : IDisposable
{
    private readonly string tempDir = Path.Combine(Path.GetTempPath(), "swift-runtime-engine-" + Guid.NewGuid().ToString("N"));

    public RuntimeEngineTargetsBehaviorTests() => Directory.CreateDirectory(tempDir);

    public void Dispose() => Directory.Delete(tempDir, recursive: true);

    [Theory]
    [InlineData("net10.0-ios", false, false, "Auto", "Mono")]
    [InlineData("net10.0-ios", true, false, "Auto", "Mono")]
    [InlineData("net10.0-ios", true, true, "Auto", "NativeAOT")]
    [InlineData("net10.0-ios", false, false, "Direct", "Mono")]
    [InlineData("net10.0-ios", true, true, "Safe", "NativeAOT")]
    [InlineData("net10.0-maccatalyst", false, false, "Safe", "Mono")]
    [InlineData("net10.0-macos", false, false, "Direct", "CoreCLR")]
    [InlineData("net11.0-ios", false, false, "Auto", "CoreCLR")]
    [InlineData("net11.0-ios", true, false, "Auto", "CoreCLR")]
    [InlineData("net11.0-ios", true, true, "Auto", "NativeAOT")]
    [InlineData("net11.0-ios", false, false, "Direct", "CoreCLR")]
    [InlineData("net11.0-ios", true, false, "Direct", "CoreCLR")]
    [InlineData("net11.0-ios", true, true, "Safe", "NativeAOT")]
    [InlineData("net11.0-tvos", false, true, "Direct", "CoreCLR")]
    [InlineData("net11.0-maccatalyst", false, false, "Safe", "CoreCLR")]
    [InlineData("net11.0-macos", true, true, "Safe", "NativeAOT")]
    public void ResolvedEngine_FollowsBuildPublishAndWorkload_IndependentlyOfPolicy(
        string tfm, bool publishAot, bool publishing, string policy, string expected)
    {
        var result = Run(tfm, publishAot, publishing, policy);
        Assert.Equal(0, result.ExitCode);
        AssertEngine(expected);
    }

    [Theory]
    [InlineData("Auto")]
    [InlineData("Safe")]
    [InlineData("Direct")]
    public void CoreClr_WithDynamicCodeDisabled_RemainsCoreClr(string policy)
    {
        var result = Run("net11.0-ios", true, false, policy, extraProperties: "<DynamicCodeSupport>false</DynamicCodeSupport>");
        Assert.Equal(0, result.ExitCode);
        AssertEngine("CoreCLR");
    }

    [Theory]
    [InlineData("net10.0-ios", "Mono")]
    [InlineData("net11.0-ios", "CoreCLR")]
    public void SimulatorRid_DoesNotDetermineEngine(string tfm, string expected)
    {
        var result = Run(tfm, true, false, "Direct", extraProperties: "<RuntimeIdentifier>iossimulator-arm64</RuntimeIdentifier>");
        Assert.Equal(0, result.ExitCode);
        AssertEngine(expected);
    }

    [Fact]
    public void MissingResolvedAppleProperties_FailsExplicitly()
    {
        var result = Run("net11.0-ios", false, false, "Safe", omitWorkloadDefaults: true);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("SWIFTBIND124", result.Output);
    }

    [Fact]
    public void Net11Mono_FailsEvenWhenExplicitlySelected()
    {
        var result = Run("net11.0-ios", false, false, "Safe", extraProperties: "<UseMonoRuntime>true</UseMonoRuntime>");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("SWIFTBIND125", result.Output);
    }

    [Theory]
    [InlineData("<_UseNativeAot>true</_UseNativeAot>", false, false)]
    [InlineData("<_UseNativeAot>true</_UseNativeAot>", true, false)]
    [InlineData("<_UseNativeAot>false</_UseNativeAot>", true, true)]
    public void ContradictoryPublishFacts_Fail(string extraProperties, bool publishAot, bool publishing)
    {
        var result = Run("net11.0-ios", publishAot, publishing, "Safe", extraProperties: extraProperties);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("SWIFTBIND126", result.Output);
    }

    [Fact]
    public void Net10MonoAndNativeAotConflict_Fails()
    {
        var result = Run("net10.0-ios", true, true, "Direct", extraProperties: "<UseMonoRuntime>true</UseMonoRuntime>");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("SWIFTBIND126", result.Output);
    }

    [Theory]
    [InlineData("net10.0")]
    [InlineData("net11.0")]
    public void NeutralMonoAndNativeAotConflict_Fails(string tfm)
    {
        var result = Run(tfm, true, true, "Direct", omitWorkloadDefaults: true,
            extraProperties: "<UseMonoRuntime>true</UseMonoRuntime>");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("SWIFTBIND126", result.Output);
    }

    [Theory]
    [InlineData("net10.0", "")]
    [InlineData("net11.0", "")]
    [InlineData("net11.0", "<UsingAppleNETSdk>true</UsingAppleNETSdk><PublishAotUsingRuntimePack>true</PublishAotUsingRuntimePack>")]
    [InlineData("net10.0-ios", "<UsingAppleNETSdk>false</UsingAppleNETSdk>")]
    [InlineData("net11.0-ios", "<UsingAppleNETSdk>false</UsingAppleNETSdk>")]
    [InlineData("net10.0-ios", "<PublishAotUsingRuntimePack>false</PublishAotUsingRuntimePack>")]
    [InlineData("net11.0-ios", "<PublishAotUsingRuntimePack>false</PublishAotUsingRuntimePack>")]
    public void SuppressedNativeCompilation_WithoutAppleWorkloadAotPath_Fails(string tfm, string extraProperties)
    {
        var result = Run(tfm, true, true, "Direct", omitWorkloadDefaults: !tfm.Contains("-ios", StringComparison.Ordinal),
            extraProperties: "<NativeCompilationDuringPublish>false</NativeCompilationDuringPublish>" + extraProperties);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("SWIFTBIND126", result.Output);
    }

    [Theory]
    [InlineData("net10.0", false, false, "CoreCLR")]
    [InlineData("net10.0", true, false, "CoreCLR")]
    [InlineData("net10.0", true, true, "NativeAOT")]
    [InlineData("net11.0", false, false, "CoreCLR")]
    [InlineData("net11.0", true, false, "CoreCLR")]
    [InlineData("net11.0", true, true, "NativeAOT")]
    public void NeutralApp_ResolvesWithoutAppleWorkloadProperties(string tfm, bool publishAot, bool publishing, string expected)
    {
        var result = Run(tfm, publishAot, publishing, "Safe", omitWorkloadDefaults: true);
        Assert.Equal(0, result.ExitCode);
        AssertEngine(expected);
    }

    [Fact]
    public void LibraryProducer_DoesNotStampItsEngine()
    {
        var result = Run("net11.0-ios", false, false, "Safe", extraProperties: "<OutputType>Library</OutputType>");
        Assert.Equal(0, result.ExitCode);
        Assert.Empty(File.ReadAllLines(Path.Combine(tempDir, "engine.txt")));
    }

    [Fact]
    public void AppExtension_ReceivesItsOwnEngine()
    {
        var result = Run("net11.0-ios", false, false, "Safe", extraProperties: "<OutputType>Library</OutputType><IsAppExtension>true</IsAppExtension>");
        Assert.Equal(0, result.ExitCode);
        AssertEngine("CoreCLR");
    }

    [Fact]
    public void ManuallySuppliedEngine_DoesNotOverrideResolvedFacts()
    {
        var result = Run("net11.0-ios", false, false, "Safe", extraItems:
            "<RuntimeHostConfigurationOption Include=\"Swift.Runtime.Engine.Mono\" Value=\"true\" />");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("SWIFTBIND127", result.Output);
    }

    [Fact]
    public void OldPolicySwitch_IsReplacedWithActualEngine()
    {
        var result = Run("net11.0-ios", false, false, "Direct", extraItems:
            "<RuntimeHostConfigurationOption Include=\"Swift.Runtime.IsNativeAot\" Value=\"true\" />");
        Assert.Equal(0, result.ExitCode);
        AssertEngine("CoreCLR");
    }

    private void AssertEngine(string expected)
    {
        var lines = File.ReadAllLines(Path.Combine(tempDir, "engine.txt"));
        foreach (string engine in new[] { "Mono", "CoreCLR", "NativeAOT" })
            Assert.Equal($"Swift.Runtime.Engine.{engine}={string.Equals(engine, expected, StringComparison.Ordinal).ToString().ToLowerInvariant()}",
                Assert.Single(lines, line => line.StartsWith($"Swift.Runtime.Engine.{engine}=", StringComparison.Ordinal)));
        Assert.Equal($"Swift.Runtime.IsNativeAot={(expected == "NativeAOT").ToString().ToLowerInvariant()}",
            Assert.Single(lines, line => line.StartsWith("Swift.Runtime.IsNativeAot=", StringComparison.Ordinal)));
    }

    private (int ExitCode, string Output) Run(string tfm, bool publishAot, bool publishing,
        string policy, bool omitWorkloadDefaults = false, string extraProperties = "", string extraItems = "")
    {
        string target = Path.Combine(FindRepoRoot(), "src/Swift.Runtime/src/build/SwiftBindings.Runtime.targets");
        // Import before workload property resolution deliberately: generation-time values must win.
        string workloadDefaults = omitWorkloadDefaults ? "" : $"""
            <UsingAppleNETSdk>true</UsingAppleNETSdk>
            <NativeCompilationDuringPublish Condition="'$(PublishAot)' == 'true'">false</NativeCompilationDuringPublish>
            <PublishAotUsingRuntimePack Condition="'$(PublishAot)' == 'true'">true</PublishAotUsingRuntimePack>
            <_UseNativeAot Condition="'$(PublishAot)' == 'true' And '$(_IsPublishing)' == 'true'">true</_UseNativeAot>
            <UseMonoRuntime Condition="'$(UseMonoRuntime)' == ''">{(tfm.StartsWith("net10", StringComparison.Ordinal) && !tfm.Contains("macos", StringComparison.Ordinal) && !(publishAot && publishing) ? "true" : "false")}</UseMonoRuntime>
            """;
        string project = Path.Combine(tempDir, "engine.proj");
        File.WriteAllText(project, $"""
            <Project>
              <PropertyGroup><TargetFramework>{tfm}</TargetFramework></PropertyGroup>
              <Import Project="{SecurityElement.Escape(target)}" />
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <PublishAot>{publishAot.ToString().ToLowerInvariant()}</PublishAot>
                <_IsPublishing>{publishing.ToString().ToLowerInvariant()}</_IsPublishing>
                <SwiftBindingsInteropMode>{policy}</SwiftBindingsInteropMode>
                {workloadDefaults}
                {extraProperties}
              </PropertyGroup>
              <ItemGroup>{extraItems}</ItemGroup>
              <Target Name="GenerateBuildRuntimeConfigurationFiles">
                <WriteLinesToFile File="{SecurityElement.Escape(Path.Combine(tempDir, "engine.txt"))}"
                    Lines="@(RuntimeHostConfigurationOption->'%(Identity)=%(Value)')" Overwrite="true" />
              </Target>
              <Target Name="GenerateRuntimeConfigurationFiles" DependsOnTargets="GenerateBuildRuntimeConfigurationFiles" />
            </Project>
            """);
        var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in new[] { "msbuild", project, "-nologo", "-v:quiet", "-t:GenerateRuntimeConfigurationFiles" })
            info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("Runtime engine MSBuild fixture timed out.");
        }
        return (process.ExitCode, stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
    }

    private static string FindRepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "src/Swift.Runtime/src/build/SwiftBindings.Runtime.targets")))
                return directory.FullName;
        throw new InvalidOperationException("Could not locate Runtime buildTransitive targets.");
    }
}
