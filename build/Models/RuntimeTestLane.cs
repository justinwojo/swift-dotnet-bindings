// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable
using System;

/// <summary>Validated managed target and execution identity for the BindingTests harness.</summary>
public sealed record RuntimeTestLane(string Framework, string Configuration, bool DeviceCoreClr)
{
    public bool IsNet11 => Framework == "net11.0";
    public static RuntimeTestLane Create(string? requestedFramework, string? profileFramework,
        string? configuration, bool deviceCoreClr, bool device, bool monoAot)
    {
        var framework = requestedFramework ?? profileFramework ?? "net10.0";
        if (framework is not ("net10.0" or "net11.0"))
            throw new ArgumentException("--binding-test-framework must be net10.0 or net11.0.");
        if (profileFramework != null && framework != profileFramework)
            throw new ArgumentException($"Requested framework {framework} disagrees with the selected toolchain profile ({profileFramework}).");
        var config = configuration ?? "Debug";
        if (config is not ("Debug" or "Release"))
            throw new ArgumentException("--binding-test-configuration must be Debug or Release.");
        if (framework == "net10.0" && config != "Debug")
            throw new ArgumentException("--binding-test-configuration Release is only supported for .NET 11 lanes; .NET 10 keeps its existing Debug/NativeAOT Release policy.");
        if (monoAot && (framework != "net10.0" || !device || deviceCoreClr))
            throw new ArgumentException("--mono-aot requires --device, .NET 10, and no --core-clr; .NET 11 does not support Mono.");
        if (deviceCoreClr && (!device || framework != "net11.0"))
            throw new ArgumentException("--core-clr selects the .NET 11 physical-device runtime and requires --device --binding-test-framework net11.0.");
        return new(framework, config, deviceCoreClr);
    }

    public string Engine(bool device) => device && !DeviceCoreClr ? "nativeaot" : "coreclr";
    public string BuildConfiguration(bool device, bool monoAot) =>
        device && !monoAot && !DeviceCoreClr ? "Release" : Configuration;
    public string BaselineKey(string platform, string rid, bool device) =>
        $"{Framework}_{platform}_{rid}_{Engine(device)}_{BuildConfiguration(device, false).ToLowerInvariant()}";
    public string AppBinDirectory(bool device) => IsNet11 ? $"bin/{Engine(device)}" : "bin";

    /// <summary>The baseline file that owns this lane's runtime-identity floors.</summary>
    public string IdentityBaselineFile => IsNet11 ? "runtime-identity-net11-baseline.json" : "runtime-identity-baseline.json";

    /// <summary>
    /// Why <c>--seed-runtime-identity-baseline</c> may not write this lane's floor, or null when it may.
    /// </summary>
    /// <remarks>
    /// The .NET 10 floors belong to the unprofiled repository toolchain, so a toolchain profile never
    /// writes them. The .NET 11 floors are the opposite: only the net11 profile can build those apps,
    /// so they are seeded only inside it, and its launcher copies that one file back to the checkout.
    /// Debug .NET 11 lanes run the CoreCLR interpreter, whose known defects the bridge work has not
    /// closed, so they get no floor yet. <paramref name="buildConfiguration"/> is the configuration
    /// the lane actually builds; when seeding is checked before any lane runs, pass the requested
    /// configuration, which every non-NativeAOT lane builds.
    /// </remarks>
    public string? IdentitySeedRefusal(bool isolatedProfile, string buildConfiguration)
    {
        if (!IsNet11)
            return isolatedProfile
                ? "A toolchain profile never writes the .NET 10 runtime-identity floors; seed them from the repository toolchain."
                : null;
        if (!isolatedProfile)
            return "The .NET 11 runtime-identity floors are seeded only through the net11 toolchain profile launcher, " +
                   "which records the SDK, workload and Xcode identity and copies the seeded file back.";
        if (buildConfiguration != "Release")
            return "Debug .NET 11 lanes have no runtime-identity floor yet; seed with --binding-test-configuration Release.";
        return null;
    }

    /// <summary>Requires positive framework and engine evidence; dynamic-code support is irrelevant.</summary>
    public bool MatchesLiveIdentity(string? output, bool device)
    {
        var banner = DeviceRuntimeFlavorGate.ExtractBanner(output);
        if (banner == null || !banner.Contains("Framework=.NETCoreApp,Version=v11.0,", StringComparison.Ordinal))
            return false;
        var nativeAot = Engine(device) == "nativeaot";
        return banner.Contains("IsMonoRuntime=False", StringComparison.Ordinal)
            && banner.Contains($"IsNativeAotRuntime={nativeAot}", StringComparison.Ordinal);
    }

    public static void AssertFrameworkStamp(string framework, string? recorded)
    {
        // Artifacts predating target selection were always .NET 10.
        if ((recorded ?? "net10.0") != framework)
            throw new InvalidOperationException("Cached bindings target a different managed framework; rerun without --skip-regen or --skip-build.");
    }
}
