// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Swift.Runtime;

/// <summary>
/// Provides runtime environment detection for runtime-specific behavior differences.
/// </summary>
/// <remarks>
/// <para>
/// On .NET 10 iOS simulator, none of the standard Mono detection methods work:
/// <c>Type.GetType("Mono.Runtime")</c> returns null, <c>FrameworkDescription</c> says ".NET 10.0.3",
/// and <c>IsDynamicCodeSupported</c> is false (same as NativeAOT). The only reliable distinguisher
/// is <c>RuntimeIdentifier</c>: "iossimulator-arm64" for Mono AOT vs "ios-arm64" for NativeAOT device.
/// </para>
/// <para>
/// Used by <c>RuntimeLimitations</c> to determine which runtime-specific workarounds are needed
/// (e.g., Mono JIT assertion with CallConvSwift, NativeAOT float struct parameter issues).
/// Note: GC finalizer cleanup of Swift structs is safe on all runtimes — VWT Destroy is called
/// via a Cdecl trampoline (<c>SBW_VWTDestroy</c>) whose DllImport stub is resolved by the
/// runtime loader without JIT compilation.
/// </para>
/// <para>
/// Three-way runtime taxonomy:
/// <list type="bullet">
/// <item>Desktop CoreCLR (macOS): <c>IsMonoRuntime=false</c>, <c>IsDynamicCodeSupported=true</c></item>
/// <item>iOS Simulator (Mono AOT): <c>IsMonoRuntime=true</c>, <c>IsDynamicCodeSupported=false</c></item>
/// <item>iOS Device, no PublishAot (Mono full-AOT): <c>IsMonoRuntime=true</c>, <c>IsDynamicCodeSupported=false</c></item>
/// <item>iOS Device, PublishAot (NativeAOT): <c>IsMonoRuntime=false</c>, <c>IsDynamicCodeSupported=false</c></item>
/// </list>
/// </para>
/// <para>
/// <b>Defect H — build-time truth.</b> The managed heuristic alone cannot tell Mono
/// full-AOT on an iOS <i>device</i> (rid <c>ios-arm64</c>, no <c>PublishAot</c>) from
/// NativeAOT: both report <c>IsDynamicCodeSupported=false</c> and neither exposes the
/// <c>Mono.Runtime</c> type. The legacy <c>!IsMonoRuntime &amp;&amp; !IsDynamicCodeSupported</c>
/// formula therefore misclassified the supported device-Mono (Safe) config as NativeAOT,
/// silently enabling direct static-virtual generic dispatch and crashing Mono
/// (jit-info.c:918). The SDK's <c>SwiftBindings.Runtime.targets</c> now injects an
/// <c>AppContext</c> feature switch (<see cref="NativeAotSwitchName"/>) carrying the
/// build-time interop contract — <c>true</c> for the NativeAOT (Direct) mode, <c>false</c>
/// otherwise. That switch is authoritative for <see cref="IsNativeAotRuntime"/>; the
/// heuristic is used only as a fallback when the switch is absent (e.g. a ProjectReference
/// consumer that does not import the package's buildTransitive targets). Even in that
/// fallback the simulator is never mistaken for NativeAOT — an Apple <i>simulator</i> RID is
/// a conclusive Mono signal (NativeAOT does not run on the simulator), so only the genuinely
/// ambiguous device case relies on the switch. <see cref="IsMonoRuntime"/> is computed
/// <i>separately</i> from definitive Mono/platform indicators — never as the negation of
/// <see cref="IsNativeAotRuntime"/> — so a non-AOT desktop CoreCLR consumer is not mislabeled Mono.
/// </para>
/// </remarks>
public static class SwiftRuntimeInfo
{
    /// <summary>
    /// Name of the <c>AppContext</c> feature switch injected by the SDK's
    /// <c>SwiftBindings.Runtime.targets</c> (a <c>RuntimeHostConfigurationOption</c> written
    /// into <c>runtimeconfig.json</c>). <c>true</c> iff the binding was built for the NativeAOT
    /// (Direct) interop contract; <c>false</c> for Mono/CoreCLR (Safe). Authoritative,
    /// build-time-resolved input for <see cref="IsNativeAotRuntime"/>.
    /// </summary>
    internal const string NativeAotSwitchName = "Swift.Runtime.IsNativeAot";

    /// <summary>Resolved runtime flavor — NativeAOT vs Mono — computed once on first access.</summary>
    private readonly record struct RuntimeFlavor(bool IsNativeAot, bool IsMono);

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
    /// True when running on NativeAOT (iOS/tvOS device with PublishAot, or a desktop
    /// NativeAOT publish). False on Mono (simulator, device full-AOT, Catalyst) and
    /// CoreCLR (desktop macOS). Driven by the build-time <see cref="NativeAotSwitchName"/>
    /// switch when present, else by the legacy heuristic.
    /// Public so generated binding assemblies can gate eager static-cctor metadata
    /// caching on NativeAOT (mirrors the SwiftArray eager-init pattern).
    /// </summary>
    public static bool IsNativeAotRuntime => s_flavor.Value.IsNativeAot;

    /// <summary>
    /// True when running on a Mono runtime (iOS/tvOS simulator, iOS/tvOS device full-AOT,
    /// Mac Catalyst). False on NativeAOT and desktop CoreCLR.
    /// Used by <see cref="RuntimeLimitations"/> to gate runtime-specific workarounds.
    /// Note: VWT Destroy from the GC finalizer is safe on all runtimes via the Cdecl trampoline.
    /// </summary>
    public static bool IsMonoRuntime => s_flavor.Value.IsMono;

    private static RuntimeFlavor ResolveFlavor()
    {
        bool switchPresent = AppContext.TryGetSwitch(NativeAotSwitchName, out bool switchValue);
        bool monoDetected = DetectMonoIndicator();
        bool isDynamicCodeSupported = RuntimeFeature.IsDynamicCodeSupported;
        string? rid = TryGetRuntimeIdentifier();

        bool isNativeAot = ResolveIsNativeAot(
            switchPresent, switchValue, monoDetected, IsSimulatorRid(rid), isDynamicCodeSupported);
        bool isMono = ResolveIsMono(isNativeAot, monoDetected, IsAppleMobileRid(rid));
        return new RuntimeFlavor(isNativeAot, isMono);
    }

    /// <summary>
    /// Pure classification of the NativeAOT flavor. The build-time switch is authoritative
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
    /// Pure classification of the Mono flavor, computed independently of
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
    /// True for Apple non-desktop RIDs where the runtime is Mono (unless NativeAOT):
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
    /// NativeAOT does not run on the Apple simulator, so a simulator RID is a definitive
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
