// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable
using System;
using System.IO;
using System.Linq;
using Xunit;

namespace BindingsGeneration.Tests;

public class ToolchainTargetsTests
{
    static string RepoProfiles([System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "..", "..", "build", "toolchains"));

    static ToolchainTargets FromRepo(string? profile)
    {
        var dir = RepoProfiles();
        var framework = profile == null ? null :
            System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, profile + ".json")))
                .RootElement.TryGetProperty("managedFramework", out var fw) ? fw.GetString() : "net10.0";
        return ToolchainTargets.Resolve(profile, framework, name =>
            File.Exists(Path.Combine(dir, name + ".json")) ? File.ReadAllText(Path.Combine(dir, name + ".json")) : null);
    }

    [Fact]
    public void Unprofiled_KeepsThePinnedArgumentsExactly()
    {
        var lane = FromRepo(null);
        Assert.False(lane.IsProfiled);
        Assert.Equal(new[] { "--platform-version 26.2" }, lane.AppleFrameworkGeneratorArguments("ios", "26.2"));
        Assert.Empty(lane.AppleFrameworkGeneratorArguments("ios", null));
        Assert.Equal("net10.0-macos", lane.ConsumerTfm("macos"));
    }

    [Theory]
    // net10-xcode26 is the unprofiled pairing: its arguments must equal the unprofiled ones.
    [InlineData("net10-xcode26", "ios", "--platform-version 26.2", "net10.0-macos")]
    [InlineData("net10-xcode27", "ios", "--platform-version 27.2", "net10.0-macos")]
    [InlineData("net10-xcode27", "maccatalyst", "--platform-version 27.2", "net10.0-macos")]
    [InlineData("net11-xcode27", "ios", "--platform-version 27.2|--target-framework net11.0-ios", "net11.0-macos")]
    [InlineData("net11-xcode27", "tvos", "--platform-version 27.2|--target-framework net11.0-tvos", "net11.0-macos")]
    public void EachProfile_SelectsTheTargetsItsUsersBuild(string profile, string suffix, string expected, string consumer)
    {
        var lane = FromRepo(profile);
        Assert.True(lane.IsProfiled);
        // A profile's own version wins over the manifest's pin, which names the unprofiled lane's.
        Assert.Equal(expected.Split('|'), lane.AppleFrameworkGeneratorArguments(suffix, "26.2"));
        Assert.Equal(consumer, lane.ConsumerTfm("macos"));
    }

    [Fact]
    public void EveryCommittedProfile_DeclaresItsApplePlatformVersion()
    {
        var profiles = Directory.GetFiles(RepoProfiles(), "*.json").Select(Path.GetFileNameWithoutExtension).ToArray();
        Assert.NotEmpty(profiles);
        foreach (var profile in profiles)
            Assert.Matches(@"^\d+\.\d+$", FromRepo(profile).ApplePlatformVersion);
    }

    [Theory]
    [InlineData("{\"xcodeVersion\":\"27.2\"}", "net10.0")]
    [InlineData("{\"applePlatformVersion\":\"27\"}", "net10.0")]
    [InlineData("{\"applePlatformVersion\":27.2}", "net10.0")]
    [InlineData("{\"applePlatformVersion\":\"27.2\"}", "net11.0")]
    [InlineData("{\"applePlatformVersion\":\"27.2\",\"managedFramework\":\"net11.0\"}", "net10.0")]
    [InlineData("{\"applePlatformVersion\":\"27.2\",\"managedFramework\":\"net12.0\"}", "net12.0")]
    public void MalformedOrInconsistentProfile_FailsClosed(string json, string launcherFramework)
    {
        Assert.Throws<InvalidDataException>(() => ToolchainTargets.Resolve("p", launcherFramework, _ => json));
    }

    [Fact]
    public void MissingProfileManifest_FailsClosed()
    {
        Assert.Throws<InvalidDataException>(() => ToolchainTargets.Resolve("absent", "net10.0", _ => null));
    }
}
