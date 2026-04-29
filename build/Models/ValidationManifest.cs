// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nuke.Common.IO;

/// <summary>
/// Typed model for build/validation-libraries.json.
/// Replaces all python3-based manifest parsing from the legacy scripts/lib.sh.
/// </summary>
public record ValidationManifest
{
    [JsonPropertyName("libraries")]
    public IReadOnlyList<ValidationLibrary> Libraries { get; init; } = [];

    public static ValidationManifest Load(AbsolutePath path)
        => JsonSerializer.Deserialize<ValidationManifest>(
            File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    /// <summary>
    /// Expands libraries x products x platforms into flat validation targets.
    /// Replaces: manifest_expand_targets() in lib.sh (~30 lines of Python).
    /// </summary>
    public IReadOnlyList<ValidationTarget> ExpandTargets(
        string? filter = null, int tier = 0, AbsolutePath? librariesDir = null)
    {
        var targets = new List<ValidationTarget>();
        foreach (var lib in Libraries)
        {
            if (tier > 0 && lib.Tier != tier) continue;
            var platforms = lib.Platforms ?? ["ios"];
            foreach (var product in lib.Products)
            {
                foreach (var platform in platforms)
                {
                    // apple-framework targets index by library name so libraries like
                    // StoreKit2 (whose SDK module is "StoreKit") still appear under the
                    // package identifier callers expect in the baseline. Other modes
                    // continue to key on the product framework name.
                    var baseName = lib.Mode == "apple-framework" ? lib.Name : product.Framework;
                    var name = platform == "ios" ? baseName
                                                 : $"{baseName}@{platform}";

                    if (filter != null && !name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                        continue;

                    targets.Add(new ValidationTarget(
                        Name: name,
                        LibraryName: lib.Name,
                        XcframeworkPath: librariesDir != null
                            ? librariesDir / lib.Name / $"{product.Framework}.xcframework"
                            : (AbsolutePath)$".libraries/{lib.Name}/{product.Framework}.xcframework",
                        Mode: lib.Mode,
                        KnownErrors: product.KnownErrors,
                        Platform: platform,
                        Tier: lib.Tier,
                        Dependencies: product.Dependencies ?? [],
                        WrapperDeps: product.WrapperDeps ?? [],
                        FrameworkModule: product.Framework,
                        PlatformVersion: lib.PlatformVersion,
                        NamespacePattern: product.NamespacePattern));
                }
            }
        }
        return targets;
    }
}

public record ValidationLibrary
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("repository")] public string? Repository { get; init; }
    [JsonPropertyName("version")] public string? Version { get; init; }
    [JsonPropertyName("revision")] public string? Revision { get; init; }
    [JsonPropertyName("mode")] public string Mode { get; init; } = "source";
    [JsonPropertyName("minIOS")] public string MinIOS { get; init; } = "15.0";
    [JsonPropertyName("tier")] public int Tier { get; init; } = 1;
    [JsonPropertyName("platforms")] public IReadOnlyList<string>? Platforms { get; init; }
    [JsonPropertyName("buildSettings")] public Dictionary<string, string>? BuildSettings { get; init; }
    [JsonPropertyName("note")] public string? Note { get; init; }
    [JsonPropertyName("platformVersion")] public string? PlatformVersion { get; init; }
    [JsonPropertyName("products")] public IReadOnlyList<ValidationProduct> Products { get; init; } = [];

    // Opt-in flag for the behavior tier. When true, `nuke validate` exercises a fresh
    // consumer that instantiates one type and invokes one Swift function from this
    // library, asserting on the round-trip return value. Today validation only proves
    // bindings *compile*; this closes the gap for the libs we want active runtime
    // coverage on. The fixture itself (which type, which call, expected output) lives
    // in build/Build.BehaviorTier.cs — kept out of JSON because the assertion is C#
    // code referencing generated types. The flag here is only the eligibility gate.
    [JsonPropertyName("behaviorTier")] public bool BehaviorTier { get; init; }

    // macOS deployment target used by the behavior tier when building the macOS slice
    // for this library. Defaults to 12.0 (matches Swift.Bindings.Apple), which is
    // newer than most third-party libs need but old enough to be universally
    // satisfied — Alamofire 5.10.2's `.macOS(.v10_15)` is far below this floor.
    [JsonPropertyName("minMacOS")] public string MinMacOS { get; init; } = "12.0";

    // Per-product overrides applied at xcodebuild scheme/destination time. Behavior
    // tier needs a macOS scheme name (Alamofire's xcodeproj exposes "Alamofire macOS"
    // separately from the iOS scheme used by validate). Keyed by framework name.
    [JsonPropertyName("behaviorTierMacOSScheme")] public string? BehaviorTierMacOSScheme { get; init; }
}

public record ValidationProduct
{
    [JsonPropertyName("framework")] public string Framework { get; init; } = "";
    [JsonPropertyName("scheme")] public string? Scheme { get; init; }
    [JsonPropertyName("project")] public string? Project { get; init; }
    [JsonPropertyName("knownErrors")] public int KnownErrors { get; init; }
    [JsonPropertyName("dependencies")] public IReadOnlyList<string>? Dependencies { get; init; }
    [JsonPropertyName("wrapper_deps")] public IReadOnlyList<string>? WrapperDeps { get; init; }
    [JsonPropertyName("namespacePattern")] public string? NamespacePattern { get; init; }
}

public record ValidationTarget(
    string Name, string LibraryName, AbsolutePath XcframeworkPath,
    string Mode, int KnownErrors, string Platform, int Tier,
    IReadOnlyList<string> Dependencies, IReadOnlyList<string> WrapperDeps,
    string? FrameworkModule = null,
    string? PlatformVersion = null,
    string? NamespacePattern = null);
