// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>
/// The targets a toolchain pairing's users build: the managed framework of the profile's .NET
/// major and the Apple platform version of its workload. A gate that builds a consumer, or
/// generates an Apple framework the way the SDK does for a user on that pairing, reads them here
/// instead of hard-coding the .NET 10 + Xcode 26 values.
/// </summary>
/// <remarks>
/// Outside a profile (the unprofiled lane, which is the .NET 10 + Xcode 26.3 pairing) the
/// framework is net10.0 and the platform version is whatever the caller pinned, so that lane
/// keeps its exact arguments. Under a profile the framework comes from the launcher
/// (<c>SWIFT_BINDINGS_MANAGED_FRAMEWORK</c>) and the platform version from the profile's
/// <c>applePlatformVersion</c>: the version of the Apple reference packs the profile's workload
/// installs, the one a consumer on that toolchain targets.
/// </remarks>
public sealed record ToolchainTargets(string? Profile, string ManagedFramework, string? ApplePlatformVersion)
{
    public const string DefaultFramework = "net10.0";
    public const string ProfileVariable = "SWIFT_BINDINGS_TOOLCHAIN_PROFILE";
    public const string FrameworkVariable = "SWIFT_BINDINGS_MANAGED_FRAMEWORK";

    static readonly Regex PlatformVersionPattern = new(@"\A[0-9]+\.[0-9]+\z");

    public bool IsProfiled => Profile != null;

    /// <summary>
    /// Resolves the targets of the active lane. <paramref name="readProfile"/> returns the
    /// profile manifest's text (build/toolchains/&lt;profile&gt;.json), or null when it is missing.
    /// </summary>
    public static ToolchainTargets Resolve(string? profile, string? managedFramework, Func<string, string?> readProfile)
    {
        if (string.IsNullOrWhiteSpace(profile))
            return new(null, DefaultFramework, null);
        var framework = string.IsNullOrWhiteSpace(managedFramework) ? DefaultFramework : managedFramework;
        if (framework is not ("net10.0" or "net11.0"))
            throw new InvalidDataException($"Toolchain profile {profile} selects unsupported managed framework '{framework}'.");
        var text = readProfile(profile) ?? throw new InvalidDataException($"Toolchain profile manifest for {profile} is missing.");
        using var document = JsonDocument.Parse(text);
        if (!document.RootElement.TryGetProperty("applePlatformVersion", out var version) ||
            version.ValueKind != JsonValueKind.String || !PlatformVersionPattern.IsMatch(version.GetString() ?? ""))
            throw new InvalidDataException($"Toolchain profile {profile} must declare applePlatformVersion as <major>.<minor>.");
        var declared = document.RootElement.TryGetProperty("managedFramework", out var fw) ? fw.GetString() : DefaultFramework;
        if (declared != framework)
            throw new InvalidDataException($"Toolchain profile {profile} declares {declared} but the launcher selected {framework}.");
        return new(profile, framework, version.GetString());
    }

    public static ToolchainTargets FromEnvironment(string profilesDirectory) => Resolve(
        Environment.GetEnvironmentVariable(ProfileVariable),
        Environment.GetEnvironmentVariable(FrameworkVariable),
        name =>
        {
            var path = Path.Combine(profilesDirectory, name + ".json");
            return File.Exists(path) ? File.ReadAllText(path) : null;
        });

    /// <summary>The unversioned consumer TFM for a platform suffix (ios, macos, tvos, maccatalyst).</summary>
    public string ConsumerTfm(string tfmSuffix) => $"{ManagedFramework}-{tfmSuffix}";

    /// <summary>
    /// Generator arguments that select the managed target of an Apple-framework binding:
    /// the pinned platform version outside a profile, the profile's own version and framework
    /// under one. net10.0 is the generator's default framework, so it is not spelled out.
    /// </summary>
    public IReadOnlyList<string> AppleFrameworkGeneratorArguments(string tfmSuffix, string? pinnedPlatformVersion)
    {
        var version = IsProfiled ? ApplePlatformVersion : pinnedPlatformVersion;
        var arguments = new List<string>();
        if (!string.IsNullOrWhiteSpace(version))
            arguments.Add($"--platform-version {version}");
        if (ManagedFramework != DefaultFramework)
            arguments.Add($"--target-framework {ConsumerTfm(tfmSuffix)}");
        return arguments;
    }
}
