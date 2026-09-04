// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;

/// <summary>
/// Maps a runtime-test <b>platform label</b> (the human-readable string the pipeline reports a run
/// under — <c>"Simulator"</c>, <c>"Device/NativeAOT"</c>, <c>"Device/MonoAOT"</c>, …) to the
/// <b>baseline key</b> under which that lane's floor is stored in both
/// <c>build/baselines/validation-baseline.json</c> (<c>runtime_tests.&lt;key&gt;</c>, the scalar
/// pass-count floor) and <c>build/baselines/runtime-identity-baseline.json</c>
/// (<c>platforms.&lt;key&gt;</c>, the per-test-identity ratchet).
///
/// <para><b>Why this is its own model.</b> The mapping is the single point where a lane becomes
/// <i>gated</i> or stays <i>ungraded</i>: an unmapped label makes <c>CompareRuntimeBaseline</c>
/// return early with "No runtime test baseline for … — skipping comparison", which is a silent
/// pass, not a failure. That fail-open shape is correct while a lane is being brought up and
/// dangerous once it is real, so the mapping is pulled out of the Nuke target into a BCL-only
/// model the unit-test project link-compiles — the same pattern as <c>DeviceRuntimeFlavorGate</c>.
/// A test can then assert that each shipping lane resolves AND that its baseline entry exists,
/// rather than the absence of a floor being invisible until a regression slips through.</para>
///
/// <para><b>Adding a lane.</b> Add its label to <see cref="ShippingPlatformLabels"/>, add the
/// <c>label ⇒ key</c> arm to <see cref="Resolve"/>, add the matching typed property to
/// <c>ValidationBaseline.RuntimeTestsBaseline</c> plus its two switch arms in
/// <c>CompareRuntimeBaseline</c> (lookup and green-improvement auto-update), and seed both
/// baseline files. The unit tests over this model fail until every one of those is done.</para>
/// </summary>
public static class RuntimeBaselinePlatformKey
{
    /// <summary>
    /// Every platform label the runtime-test pipeline reports a completed run under. Each of these
    /// must <see cref="Resolve"/> to a key and must have a seeded entry in both baseline files —
    /// a lane that ships without a floor can regress silently.
    /// </summary>
    public static IReadOnlyList<string> ShippingPlatformLabels { get; } = new[]
    {
        "Simulator",
        "Device/NativeAOT",
        "Device/MonoAOT",
        "macOS",
        "macOS x64",
        "Mac Catalyst",
        "Mac Catalyst x64",
        "tvOS Simulator",
    };

    /// <summary>
    /// Returns the baseline key for a platform label, or <c>null</c> when the label is not one this
    /// repo grades (the caller then skips the comparison entirely). Matching is case-insensitive so
    /// a label's display casing can change without silently un-gating the lane.
    /// </summary>
    public static string? Resolve(string? platform) => platform?.ToLowerInvariant() switch
    {
        "simulator" => "simulator",
        // "device" is the historical label from before the device lane named its runtime flavor.
        "device/nativeaot" or "device" => "device",
        // The Mono full-AOT device lane grades against its OWN floor. Both device lanes run the
        // same suite on the same phone, but their skip sets differ (the runtime-detected Mono skips
        // apply only here; the NativeAOT-Release-shaped ones only there), so folding this lane onto
        // "device" would false-regress on every run and let its own drift through ungraded.
        "device/monoaot" => "device_monoaot",
        "macos" => "macos",
        "macos x64" => "macos_x64",
        "mac catalyst" => "maccatalyst",
        "mac catalyst x64" => "maccatalyst_x64",
        "tvos simulator" => "tvos_simulator",
        _ => null,
    };
}
