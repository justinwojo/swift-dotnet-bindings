// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// Self-contained nullable context so this file compiles identically whether built in the Nuke
// build assembly or link-compiled into the unit-test project.
#nullable enable

/// <summary>
/// Who owns <c>--device-udid</c> when one invocation runs both a simulator leg and the physical
/// device leg.
///
/// <para><b>The defect this closes.</b> <c>--device-udid</c> is a SINGLE parameter that both families
/// read: the device legs pass it to <c>devicectl</c>, the simulator legs to <c>simctl</c>. Each
/// simulator leg used to trust it unconditionally, so the documented both-platforms form —
/// <c>--mixed-pack --sim --device --device-udid &lt;physical iPhone&gt;</c>, where the flag table
/// advertises the flag composition and the device leg genuinely needs the UDID — handed the phone's
/// identifier to <c>simctl install</c> and died with <c>Invalid device: …</c>. The red then accused
/// the simulator when the fault was argument routing.</para>
///
/// <para><b>The rule.</b> When the device leg was requested in the same invocation, a supplied UDID
/// belongs to IT, and the simulator legs resolve their own device (<c>SimCtl.EnsureBootedDevice</c>
/// already does this correctly — it is how the sim gates pick their simulator when no flag is
/// passed). This is preferred over adding a separate <c>--sim-udid</c> the caller must remember: a
/// parameter that silently means the wrong thing is worse than one that is absent.</para>
///
/// <para><b>Scope, deliberately narrow.</b> Nothing changes unless the device leg was requested AND a
/// UDID was supplied. Without <c>--device</c> the simulator legs pin exactly the UDID they always
/// did, so every single-platform invocation behaves identically.</para>
/// </summary>
public static class SharedUdidRouting
{
    /// <summary>
    /// True when a UDID was supplied and the device leg is running, so it is the device leg's and a
    /// simulator leg must not consume it. The caller announces this at Information level — a silent
    /// discard is a smaller version of the problem being fixed.
    /// </summary>
    public static bool BelongsToDeviceLeg(string? sharedUdid, bool deviceLegRequested) =>
        !string.IsNullOrEmpty(sharedUdid) && deviceLegRequested;

    /// <summary>
    /// The UDID a simulator leg should pin, or null to let it resolve its own simulator. Null both
    /// when nothing was supplied and when what was supplied belongs to the device leg.
    /// </summary>
    public static string? SimulatorUdid(string? sharedUdid, bool deviceLegRequested)
    {
        if (string.IsNullOrEmpty(sharedUdid)) return null;
        return deviceLegRequested ? null : sharedUdid;
    }

    /// <summary>
    /// What the operator is told when a simulator leg gives the UDID up. Names the leg and the reason,
    /// so the log explains the resolution rather than leaving an unexplained simulator choice.
    /// </summary>
    public static string DiscardNotice(string legLabel, string sharedUdid) =>
        $"{legLabel}: ignoring --device-udid {sharedUdid} for the simulator — --device was also passed, so that " +
        "UDID identifies the physical device leg's target. Resolving a booted simulator instead. Pass --device-udid " +
        "without --device to pin a specific simulator.";
}
