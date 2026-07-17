// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// SDK ambient-scope probe for module names that collide with an already-visible type.
/// All subprocess traffic is faked via <see cref="ICommandRunner"/> — never real xcrun.
/// </summary>
public class ModuleNameShadowProbeTests
{
    [Fact]
    public void IsModuleNameShadowedBySdk_TypecheckSucceeds_ReturnsTrue()
    {
        var runner = new MockCommandRunner();
        runner.SetResponse("--show-sdk-path", 0, "/fake/sdk/path");
        runner.SetResponse("swift-frontend -typecheck", 0, "");

        var platformInfo = PlatformInfoFactory.Create(ApplePlatform.iOS);
        var result = ModuleNameShadowProbe.IsModuleNameShadowedBySdk(
            "Semaphore", platformInfo, runner, NullLogger.Instance);

        Assert.True(result);
        Assert.Contains(runner.Invocations, i =>
            i.Command == "xcrun" && i.Arguments.Contains("swift-frontend -typecheck", StringComparison.Ordinal));
    }

    [Fact]
    public void IsModuleNameShadowedBySdk_TypecheckFails_ReturnsFalse()
    {
        var runner = new MockCommandRunner();
        runner.SetResponse("--show-sdk-path", 0, "/fake/sdk/path");
        runner.SetResponse("swift-frontend -typecheck", 1, "", "error: cannot find type");

        var platformInfo = PlatformInfoFactory.Create(ApplePlatform.iOS);
        var result = ModuleNameShadowProbe.IsModuleNameShadowedBySdk(
            "MyModule", platformInfo, runner, NullLogger.Instance);

        Assert.False(result);
        Assert.Contains(runner.Invocations, i =>
            i.Command == "xcrun" && i.Arguments.Contains("swift-frontend -typecheck", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Foo-Bar")]
    [InlineData("1Foo")]
    [InlineData("")]
    [InlineData("Foo.Bar")]
    public void IsModuleNameShadowedBySdk_NonSwiftIdentifier_ReturnsFalseWithoutTypecheck(string moduleName)
    {
        var runner = new MockCommandRunner();
        // If these were ever consulted, a successful typecheck would incorrectly report shadowing.
        runner.SetResponse("--show-sdk-path", 0, "/fake/sdk/path");
        runner.SetResponse("swift-frontend -typecheck", 0, "");

        var platformInfo = PlatformInfoFactory.Create(ApplePlatform.iOS);
        var result = ModuleNameShadowProbe.IsModuleNameShadowedBySdk(
            moduleName, platformInfo, runner, NullLogger.Instance);

        Assert.False(result);
        Assert.Empty(runner.Invocations);
    }

    [Fact]
    public void IsModuleNameShadowedBySdk_SdkPathResolutionFails_DoesNotClaimShadow()
    {
        // A toolchain that can't answer must never be read as "shadowed": claiming a shadow
        // strips every module qualifier from the wrapper. ResolveSdkPath reports an unresolvable
        // SDK by throwing, so the probe has to absorb that into its fail-open answer rather than
        // aborting generation over it.
        var runner = new MockCommandRunner();
        runner.SetResponse("--show-sdk-path", 1, "", "xcode-select: error: tool 'xcrun' requires Xcode");

        var platformInfo = PlatformInfoFactory.Create(ApplePlatform.iOS);

        var result = ModuleNameShadowProbe.IsModuleNameShadowedBySdk(
            "MyModule", platformInfo, runner, NullLogger.Instance);

        Assert.False(result);
        Assert.DoesNotContain(runner.Invocations, i =>
            i.Arguments.Contains("swift-frontend -typecheck", StringComparison.Ordinal));
    }

    [Fact]
    public void IsModuleNameShadowedBySdk_SdkPathEmptyOutput_DoesNotClaimShadow()
    {
        // exit 0 with empty stdout is also a failed resolution in ResolveSdkPath, and lands on
        // the same fail-open answer.
        var runner = new MockCommandRunner();
        runner.SetResponse("--show-sdk-path", 0, "");

        var platformInfo = PlatformInfoFactory.Create(ApplePlatform.iOS);

        var result = ModuleNameShadowProbe.IsModuleNameShadowedBySdk(
            "MyModule", platformInfo, runner, NullLogger.Instance);

        Assert.False(result);
        Assert.DoesNotContain(runner.Invocations, i =>
            i.Arguments.Contains("swift-frontend -typecheck", StringComparison.Ordinal));
    }
}
