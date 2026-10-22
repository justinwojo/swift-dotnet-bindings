// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Swift.Runtime;

/// <summary>
/// Provides runtime environment detection for runtime-specific behavior differences.
/// </summary>
/// <remarks>
/// Engine identity is supplied by the final application's resolved workload properties.
/// Mono, CoreCLR and NativeAOT are independent of platform, dynamic-code capability and
/// the binding's Safe/Direct interop policy. Older .NET 10 consumers retain their legacy
/// switch and heuristic fallback. Ambiguous unconfigured .NET 11 consumers fail explicitly.
/// </remarks>
public static class SwiftRuntimeInfo
{
    // Keep the old policy-derived switch as a compatibility input only. New engine
    // switches take precedence because Direct/Safe did not identify the actual engine.
    internal const string NativeAotSwitchName = "Swift.Runtime.IsNativeAot";
    internal const string MonoEngineSwitchName = "Swift.Runtime.Engine.Mono";
    internal const string CoreClrEngineSwitchName = "Swift.Runtime.Engine.CoreCLR";
    internal const string NativeAotEngineSwitchName = "Swift.Runtime.Engine.NativeAOT";

    internal readonly record struct RuntimeFlavor(bool IsNativeAot, bool IsMono);

    /// <summary>
    /// Lazily-resolved runtime flavor. The resolution is deliberately deferred OFF the static
    /// constructor: the runtime-flavor-conflict check in <see cref="ResolveIsNativeAot"/> throws
    /// <see cref="InvalidOperationException"/>, and a throw from a static cctor is wrapped in a
    /// <see cref="TypeInitializationException"/> that permanently POISONS the type — every later read
    /// of <see cref="IsNativeAotRuntime"/>/<see cref="IsMonoRuntime"/> (touched on virtually every
    /// marshal path and from generated <c>[ModuleInitializer]</c>s) would re-throw the cached wrapper
    /// with the actionable conflict text demoted to <c>.InnerException</c>. A <see cref="Lazy{T}"/> in
    /// the default <c>ExecutionAndPublication</c> mode runs the check on first explicit access and
    /// caches+re-throws the ORIGINAL <see cref="InvalidOperationException"/> (conflict text at top
    /// level) on every access, preserving the fail-fast intent without the type-poison cascade. The
    /// conflict path is genuinely reachable (a Direct build run on the iOS Simulator), so this is the
    /// fix for that real misconfiguration, not a theoretical one.
    /// </summary>
    private static readonly Lazy<RuntimeFlavor> s_flavor = new(ResolveFlavor);

    /// <summary>
    /// True when running on NativeAOT, independently of interop policy or platform.
    /// Public so generated bindings can gate NativeAOT registration and eager caching.
    /// </summary>
    public static bool IsNativeAotRuntime => s_flavor.Value.IsNativeAot;

    /// <summary>True when running on Mono, independently of compilation mode or platform.</summary>
    public static bool IsMonoRuntime => s_flavor.Value.IsMono;

    private static RuntimeFlavor ResolveFlavor()
    {
        bool monoPresent = AppContext.TryGetSwitch(MonoEngineSwitchName, out bool mono);
        bool coreClrPresent = AppContext.TryGetSwitch(CoreClrEngineSwitchName, out bool coreClr);
        bool nativeAotPresent = AppContext.TryGetSwitch(NativeAotEngineSwitchName, out bool nativeAot);
        bool legacyPresent = AppContext.TryGetSwitch(NativeAotSwitchName, out bool legacyNativeAot);
        return ResolveRuntimeFlavor(
            monoPresent ? mono : null, coreClrPresent ? coreClr : null,
            nativeAotPresent ? nativeAot : null, legacyPresent, legacyNativeAot,
            DetectMonoIndicator(), TryGetRuntimeIdentifier(), RuntimeFeature.IsDynamicCodeSupported,
            Environment.Version.Major);
    }

    /// <summary>
    /// Classifies authoritative engine switches first, with legacy inputs isolated to old
    /// consumers. Boolean switches work in both runtimeconfig hosts and NativeAOT's feature
    /// switch pipeline. All three must be present with exactly one true.
    /// </summary>
    internal static RuntimeFlavor ResolveRuntimeFlavor(
        bool? monoEngine, bool? coreClrEngine, bool? nativeAotEngine,
        bool legacySwitchPresent, bool legacySwitchValue, bool monoDetected,
        string? rid, bool isDynamicCodeSupported, int runtimeMajor)
    {
        if (monoEngine.HasValue || coreClrEngine.HasValue || nativeAotEngine.HasValue)
        {
            int engineCount = (monoEngine == true ? 1 : 0) + (coreClrEngine == true ? 1 : 0)
                + (nativeAotEngine == true ? 1 : 0);
            if (!monoEngine.HasValue || !coreClrEngine.HasValue || !nativeAotEngine.HasValue || engineCount != 1)
                throw new InvalidOperationException(
                    "Swift.Runtime runtime-engine conflict: expected all three engine switches with exactly one true. " +
                    "Rebuild the final app with SwiftBindings.Runtime buildTransitive targets.");
            if (monoDetected && monoEngine != true)
                throw new InvalidOperationException(
                    "Swift.Runtime runtime-engine conflict: the configured engine contradicts the live Mono.Runtime type.");
            // Neither an old policy switch nor a RID/dynamic-code heuristic can override
            // the final app's engine. In particular, CoreCLR can disable dynamic code.
            return new RuntimeFlavor(nativeAotEngine == true, monoEngine == true);
        }

        if (runtimeMajor >= 11)
        {
            if (monoDetected)
                return new RuntimeFlavor(false, true);
            // Preserve previously published net10 libraries on desktop CoreCLR hosts.
            // Mobile RIDs and disabled dynamic code are ambiguous without the new identity.
            if (!IsAppleMobileRid(rid) && isDynamicCodeSupported)
                return new RuntimeFlavor(false, false);
            throw new InvalidOperationException(
                "Swift.Runtime runtime engine is unresolved on .NET 11 or later. Import the final app's " +
                "SwiftBindings.Runtime buildTransitive targets; RID, dynamic-code support and the legacy " +
                $"'{NativeAotSwitchName}' policy switch cannot identify its engine.");
        }

        bool isNativeAot = ResolveIsNativeAot(legacySwitchPresent, legacySwitchValue,
            monoDetected, IsSimulatorRid(rid), isDynamicCodeSupported);
        return new RuntimeFlavor(isNativeAot, ResolveIsMono(isNativeAot, monoDetected, IsAppleMobileRid(rid)));
    }

    /// <summary>
    /// Legacy .NET 10 classification of the NativeAOT flavor. The build-time switch is authoritative
    /// when present; the heuristic is a fallback only. In the fallback, a live Mono indicator
    /// or an Apple <i>simulator</i> RID is conclusively <i>not</i> NativeAOT — NativeAOT does
    /// not run on the iOS/tvOS simulator, so the simulator is always Mono there. Only the
    /// ambiguous Apple <i>device</i> case (rid <c>ios-arm64</c>/<c>tvos-arm64</c>, no Mono
    /// indicator, no dynamic code) cannot be told apart from Mono full-AOT, which is the exact
    /// gap the build-time switch closes; that branch keeps the legacy NativeAOT default.
    /// Throws on the hard conflict where the build declared NativeAOT but the live runtime is
    /// conclusively Mono — failing fast with a clear managed exception beats the cryptic Mono
    /// JIT abort that the direct dispatch path would otherwise produce.
    /// </summary>
    internal static bool ResolveIsNativeAot(
        bool switchPresent, bool switchValue, bool monoDetected, bool isSimulatorRid, bool isDynamicCodeSupported)
    {
        // A live Mono.Runtime type OR an Apple simulator RID conclusively rules out NativeAOT.
        bool conclusivelyMono = monoDetected || isSimulatorRid;

        if (switchPresent)
        {
            if (switchValue && conclusivelyMono)
                throw new InvalidOperationException(
                    $"Swift.Runtime runtime-flavor conflict: the build injected '{NativeAotSwitchName}=true' " +
                    "(NativeAOT / Direct interop mode) but the live runtime is conclusively Mono (the Mono.Runtime " +
                    "type is present or the RID is an Apple simulator). Taking the direct static-virtual dispatch " +
                    "path would abort Mono (jit-info.c:918). This indicates the binding was built for NativeAOT " +
                    "(PublishAot/SwiftBindingsInteropMode=Direct) yet is running on Mono — rebuild without " +
                    "PublishAot, or set SwiftBindingsInteropMode=Safe for the Mono target.");
            return switchValue;
        }

        // Switch absent: best-effort heuristic (e.g. a ProjectReference consumer that does not
        // import the package's buildTransitive targets). A conclusively-Mono signal rules out
        // NativeAOT outright — this is the simulator-RID signal the legacy
        // DetectNonNativeAotRuntime() folded in; dropping it misclassified the simulator as
        // NativeAOT. Only the ambiguous device case (no Mono indicator, no dynamic code) falls
        // back to the dynamic-code probe and keeps the legacy NativeAOT default; the SDK injects
        // the switch for every supported config precisely so that ambiguous path is not relied
        // upon there.
        if (conclusivelyMono)
            return false;
        return !isDynamicCodeSupported;
    }

    /// <summary>
    /// Legacy .NET 10 classification of the Mono flavor, computed independently of
    /// <see cref="IsNativeAotRuntime"/>. NativeAOT is never Mono. Otherwise a runtime is Mono
    /// when a definitive Mono indicator is present OR the RID is an Apple non-desktop RID
    /// (iOS/tvOS device + simulator, Mac Catalyst). Desktop macOS (osx-*) is CoreCLR, not Mono.
    /// </summary>
    internal static bool ResolveIsMono(bool isNativeAot, bool monoDetected, bool isAppleMobileRid)
    {
        if (isNativeAot)
            return false;
        return monoDetected || isAppleMobileRid;
    }

    /// <summary>
    /// True for Apple non-desktop RIDs (a legacy Mono signal on .NET 10):
    /// iOS device/simulator, tvOS device/simulator, Mac Catalyst. Desktop macOS (osx-*) is
    /// CoreCLR and returns false.
    /// </summary>
    internal static bool IsAppleMobileRid(string? rid)
    {
        if (string.IsNullOrEmpty(rid))
            return false;
        return rid.StartsWith("ios", StringComparison.OrdinalIgnoreCase)
            || rid.StartsWith("tvos", StringComparison.OrdinalIgnoreCase)
            || rid.StartsWith("maccatalyst", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True for Apple <i>simulator</i> RIDs (<c>iossimulator-*</c>, <c>tvossimulator-*</c>).
    /// In the legacy .NET 10 fallback a simulator RID is treated as a definitive
    /// "not NativeAOT / is Mono" signal even when the <c>Mono.Runtime</c> type is absent (as it
    /// is on .NET 10+ Mono AOT). Consumed by <see cref="ResolveIsNativeAot"/>'s switch-less
    /// fallback so the simulator is not mistaken for NativeAOT.
    /// </summary>
    internal static bool IsSimulatorRid(string? rid)
    {
        return !string.IsNullOrEmpty(rid)
            && rid.Contains("simulator", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Definitive positive Mono indicator. Returns true only when the classic
    /// <c>Mono.Runtime</c> type is present (.NET 8 and earlier, and some Mono configs).
    /// Note: this is absent on .NET 10+ iOS Mono AOT, which is why the RID and the
    /// build-time switch are also consulted.
    /// </summary>
    private static bool DetectMonoIndicator()
    {
        return Type.GetType("Mono.Runtime") != null;
    }

    private static string? TryGetRuntimeIdentifier()
    {
        try
        {
            return RuntimeInformation.RuntimeIdentifier;
        }
        catch
        {
            // RuntimeInformation may not be available in all contexts.
            return null;
        }
    }
}
