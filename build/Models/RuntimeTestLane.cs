// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable
using System;

/// <summary>Validated managed target and execution identity for the BindingTests harness.</summary>
/// <param name="CoreClrInterpreter">Set by <c>--coreclr-interp</c>: the run is the ordinary Debug .NET 11
/// app on the CoreCLR interpreter, on every platform it names (the device included).</param>
public sealed record RuntimeTestLane(string Framework, string Configuration, bool DeviceCoreClr, bool CoreClrInterpreter = false)
{
    public bool IsNet11 => Framework == "net11.0";
    public static RuntimeTestLane Create(string? requestedFramework, string? profileFramework,
        string? configuration, bool deviceCoreClr, bool device, bool monoAot, bool coreClrInterp = false)
    {
        var framework = requestedFramework ?? profileFramework ?? "net10.0";
        if (framework is not ("net10.0" or "net11.0"))
            throw new ArgumentException("--binding-test-framework must be net10.0 or net11.0.");
        if (profileFramework != null && framework != profileFramework)
            throw new ArgumentException($"Requested framework {framework} disagrees with the selected toolchain profile ({profileFramework}).");
        if (coreClrInterp)
        {
            if (framework != "net11.0")
                throw new ArgumentException("--coreclr-interp selects the .NET 11 CoreCLR interpreter; run it under the net11 toolchain profile.");
            if (configuration is not (null or "Debug"))
                throw new ArgumentException("--coreclr-interp is the Debug interpreter lane; the matching ReadyToRun leg is --binding-test-configuration Release.");
            // A device build has no JIT: the ordinary Debug CoreCLR app runs the interpreter there too.
            deviceCoreClr |= device;
        }
        var config = configuration ?? "Debug";
        if (config is not ("Debug" or "Release"))
            throw new ArgumentException("--binding-test-configuration must be Debug or Release.");
        if (framework == "net10.0" && config != "Debug")
            throw new ArgumentException("--binding-test-configuration Release is only supported for .NET 11 lanes; .NET 10 keeps its existing Debug/NativeAOT Release policy.");
        if (monoAot && (framework != "net10.0" || !device || deviceCoreClr))
            throw new ArgumentException("--mono-aot requires --device, .NET 10, and no --core-clr; .NET 11 does not support Mono.");
        if (deviceCoreClr && (!device || framework != "net11.0"))
            throw new ArgumentException("--core-clr selects the .NET 11 physical-device runtime and requires --device --binding-test-framework net11.0.");
        return new(framework, config, deviceCoreClr, coreClrInterp);
    }

    public string Engine(bool device) => device && !DeviceCoreClr ? "nativeaot" : "coreclr";

    /// <summary>
    /// How this lane's app must execute, proven per test class by <see cref="RuntimeLaneIdentity"/>.
    /// Null for .NET 10, whose Mono lanes the banner already identifies.
    /// </summary>
    public RuntimeExecutionLane? ExecutionLane(bool device) =>
        IsNet11 ? RuntimeLaneIdentity.Expected(Engine(device), BuildConfiguration(device, false)) : null;

    /// <summary>
    /// How many crash-recovery relaunches a run may make. The .NET 11 lanes are measured on emission
    /// the interpreter is known to mishandle, so a crash must not hide the classes after it: each
    /// relaunch excludes at least one class, and the budget lets every eligible class run. .NET 10
    /// keeps its fixed budget.
    /// </summary>
    public int CrashRecoveryRetries(int eligibleClasses) => IsNet11 ? Math.Max(DefaultCrashRecoveryRetries, eligibleClasses) : DefaultCrashRecoveryRetries;

    public const int DefaultCrashRecoveryRetries = 5;
    public string BuildConfiguration(bool device, bool monoAot) =>
        device && !monoAot && !DeviceCoreClr ? "Release" : Configuration;
    public string BaselineKey(string platform, string rid, bool device) =>
        $"{Framework}_{platform}_{rid}_{Engine(device)}_{BuildConfiguration(device, false).ToLowerInvariant()}";
    /// <summary>The Release floor of the same platform, architecture and engine: the reference a Debug floor is checked against.</summary>
    public string ReleaseSiblingKey(string platform, string rid, bool device) =>
        (this with { Configuration = "Release" }).BaselineKey(platform, rid, device);
    public string AppBinDirectory(bool device) => IsNet11 ? $"bin/{Engine(device)}" : "bin";

    /// <summary>
    /// The lane a built test app belongs to, as its build receipt records it. .NET 11 names its
    /// engine; a .NET 10 lane is fixed by its bundle path, whose configuration segment already
    /// separates the NativeAOT (Release) and Mono full-AOT (Debug) device apps.
    /// </summary>
    public string AppIdentity(bool device, bool monoAot) => IsNet11
        ? $"{Framework}/{Engine(device)}/{BuildConfiguration(device, monoAot)}"
        : $"{Framework}/{BuildConfiguration(device, monoAot)}";

    /// <summary>The baseline file that owns this lane's runtime-identity floors.</summary>
    public string IdentityBaselineFile => IsNet11 ? "runtime-identity-net11-baseline.json" : "runtime-identity-baseline.json";

    /// <summary>
    /// Why <c>--seed-runtime-identity-baseline</c> may not write this lane's floor, or null when the
    /// lane is eligible. This is the check that needs no run: it is made before the build and again
    /// for each lane at its seed, where <see cref="RuntimeIdentitySeedPolicy"/> adds what the run proved.
    /// </summary>
    /// <remarks>
    /// <para>The .NET 10 floors belong to the unprofiled repository toolchain, so a toolchain profile
    /// never writes them. The .NET 11 floors are the opposite: only the net11 profile can build those
    /// apps, so they are seeded only inside it, and its launcher copies that one file back to the
    /// checkout.</para>
    /// <para>A Debug .NET 11 lane runs the CoreCLR interpreter, which has known defects the generated
    /// bindings route around. It is eligible, and earns its floor from its own run's evidence: the
    /// run must prove the interpreter ran, every registered negative control must behave as expected,
    /// and the floor may not drop below its previous Debug floor or its Release sibling without a
    /// justification (<see cref="RuntimeIdentitySeedPolicy"/>). macOS is not such a lane: its
    /// runtime has no interpreter, so Debug runs CoreCLR's JIT, and its run proves no execution
    /// lane, so it gets no Debug floor.</para>
    /// <para><paramref name="buildConfiguration"/> is the configuration the lane actually builds;
    /// before any lane runs, pass the configuration the requested lanes build. A lane that builds
    /// another configuration than the run requested (the NativeAOT device publishes Release) seeds
    /// only from an explicit request for that configuration, so a Debug request never writes a
    /// Release floor.</para>
    /// </remarks>
    /// <param name="macOS">The lane, or before the build any requested lane, is macOS.</param>
    /// <param name="classFilter">The run selects classes. A floor needs the whole suite, and a filter
    /// naming only a negative control would grade the control and never reach the seed.</param>
    public string? IdentitySeedRefusal(bool isolatedProfile, string buildConfiguration, bool macOS = false, bool classFilter = false)
    {
        if (!IsNet11)
            return isolatedProfile
                ? "A toolchain profile never writes the .NET 10 runtime-identity floors; seed them from the repository toolchain."
                : null;
        if (!isolatedProfile)
            return "The .NET 11 runtime-identity floors are seeded only through the net11 toolchain profile launcher, " +
                   "which records the SDK, workload and Xcode identity and copies the seeded file back.";
        if (classFilter)
            return "A floor is seeded from the whole suite; drop --class-filter.";
        if (buildConfiguration != Configuration)
            return $"This lane builds {buildConfiguration} whatever was requested; seed its floor with an explicit --binding-test-configuration {buildConfiguration}.";
        if (buildConfiguration == "Debug" && macOS)
            return "Debug .NET 11 floors qualify the CoreCLR interpreter, which the macOS runtime does not have: " +
                   "a Debug macOS app runs the JIT, and the macOS lane proves no execution lane. Seed macOS with --binding-test-configuration Release.";
        return null;
    }

    /// <summary>
    /// Why this lane refuses the requested auxiliary gates, or null when it runs them.
    /// </summary>
    /// <remarks>
    /// The compile-only gate needs no runtime and runs for either framework: it generates and
    /// compile-checks for this lane's framework, and its baselines describe the generated surface.
    /// The ABI grid grades results against the Mono JIT simulator and NativeAOT device runtimes its
    /// manifest declares, and the Apple-framework smokes regenerate their snapshots for .NET 10, so
    /// both keep .NET 10 policy, as do the Mono-only ActivityKit push-token and Catalyst x64 JIT arms.
    /// </remarks>
    public string? AuxiliaryGateRefusal(bool abiGrid, bool appleSmoke, bool activityKitPushToken, bool catalystX64Jit)
    {
        if (!IsNet11 || !(abiGrid || appleSmoke || activityKitPushToken || catalystX64Jit))
            return null;
        return "This auxiliary gate has not been migrated to .NET 11. " +
               "ABI-grid, Apple smoke and Mono-specific auxiliary gates retain their .NET 10 policy.";
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
