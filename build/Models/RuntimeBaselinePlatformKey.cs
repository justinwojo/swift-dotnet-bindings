// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Maps a runtime-test <b>platform label</b> (the human-readable string the pipeline reports a run
/// under — <c>"Simulator"</c>, <c>"Device/NativeAOT"</c>, <c>"Device/MonoAOT"</c>, …) to the
/// <b>baseline key</b> under which that lane's floor is stored. Shared arm64 runtime lanes use
/// <c>build/baselines/runtime-identity-baseline.json</c> as their single source for both counts and
/// non-pass identities. The two x64 lanes retain scalar-only floors in
/// <c>build/baselines/validation-baseline.json</c> because they have no identity counterpart.
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
/// <c>label ⇒ key</c> arm to <see cref="Resolve"/>, classify its key under exactly one authority
/// list below, then seed that authority. The unit tests over this model fail until every one of
/// those is done.</para>
/// </summary>
public static class RuntimeBaselinePlatformKey
{
    /// <summary>
    /// Every platform label the runtime-test pipeline reports a completed run under. Each of these
    /// must <see cref="Resolve"/> to a key and must have a seeded entry under exactly one baseline
    /// authority — a lane that ships without a floor can regress silently.
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
    /// Lanes whose count floors and non-pass identities come from one authoritative identity
    /// record. These are the lanes shared by the former scalar and identity stores.
    /// </summary>
    public static IReadOnlyList<string> IdentityBackedPlatformKeys { get; } = new[]
    {
        "simulator",
        "device",
        "device_monoaot",
        "macos",
        "maccatalyst",
        "tvos_simulator",
    };

    /// <summary>Lanes that have scalar floors only and are outside identity reconciliation.</summary>
    public static IReadOnlyList<string> ScalarOnlyPlatformKeys { get; } = new[]
    {
        "macos_x64",
        "maccatalyst_x64",
    };

    public static bool IsIdentityBacked(string platformKey)
        => IdentityBackedPlatformKeys.Contains(platformKey, StringComparer.Ordinal);

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
