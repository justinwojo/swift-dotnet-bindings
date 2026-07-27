// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// SharedUdidRouting carries its own `#nullable enable` (a null UDID means "none supplied"), so the
// null cases below need string? parameters; this project builds with Nullable=disable +
// warnings-as-errors, where the annotation would otherwise raise CS8632.
#nullable enable

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for <see cref="SharedUdidRouting"/> — which leg owns <c>--device-udid</c>.
///
/// <para><b>The defect these pin.</b> One parameter is read by two families: the physical-device legs
/// hand it to <c>devicectl</c>, the simulator legs to <c>simctl</c>. Following two separately
/// documented instructions at once — the flag table advertises <c>--mixed-pack --sim --device</c> as
/// the both-platforms form, and device runs are documented as needing <c>--device-udid</c> — gave the
/// simulator leg a physical iPhone's UDID and a <c>simctl install</c> failure that pointed at the
/// simulator instead of at argument routing.</para>
///
/// <para>The other half of the contract is just as load-bearing: without <c>--device</c> the simulator
/// legs must pin exactly the UDID they always did. Those are the cases that keep this change inert
/// for every single-platform invocation, so a regression that widened the discard would turn one of
/// them red.</para>
/// </summary>
public class SharedUdidRoutingTests
{
    const string PhoneUdid = "559479FD-3C60-51E4-8B2C-872D8CBA8B54";
    const string SimUdid = "A1B2C3D4-0000-1111-2222-333344445555";

    // ===================================================================
    //  The device leg owns the UDID when it is running
    // ===================================================================

    [Fact]
    public void WithBothLegsRunning_TheUdidIsTheDeviceLegs()
    {
        Assert.True(SharedUdidRouting.BelongsToDeviceLeg(PhoneUdid, deviceLegRequested: true));
        Assert.Null(SharedUdidRouting.SimulatorUdid(PhoneUdid, deviceLegRequested: true));
    }

    // ===================================================================
    //  Inertness: nothing changes unless the device leg is also running
    // ===================================================================

    [Fact]
    public void WithoutTheDeviceLeg_TheSimulatorPinsTheSuppliedUdid()
    {
        Assert.False(SharedUdidRouting.BelongsToDeviceLeg(SimUdid, deviceLegRequested: false));
        Assert.Equal(SimUdid, SharedUdidRouting.SimulatorUdid(SimUdid, deviceLegRequested: false));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void WithNoUdidSupplied_TheSimulatorResolvesItsOwn_WhicheverLegsRun(string? supplied)
    {
        // Both legs' behaviour is unchanged when the parameter was never passed: there is nothing to
        // route, and the simulator legs fall through to their own resolution exactly as before.
        Assert.False(SharedUdidRouting.BelongsToDeviceLeg(supplied, deviceLegRequested: false));
        Assert.False(SharedUdidRouting.BelongsToDeviceLeg(supplied, deviceLegRequested: true));
        Assert.Null(SharedUdidRouting.SimulatorUdid(supplied, deviceLegRequested: false));
        Assert.Null(SharedUdidRouting.SimulatorUdid(supplied, deviceLegRequested: true));
    }

    /// <summary>
    /// The routing decision turns on the device flag alone, so the ONLY invocation whose behaviour
    /// moves is one that supplies a UDID and requests the device leg. Stated as a table so a future
    /// widening of the discard has to turn this red.
    /// </summary>
    [Theory]
    [InlineData(null, false, false)]
    [InlineData(null, true, false)]
    [InlineData(PhoneUdid, false, false)]
    [InlineData(PhoneUdid, true, true)]
    public void OnlyTheSuppliedUdidPlusDeviceLegCombinationDiverts(
        string? supplied, bool deviceLegRequested, bool expectDiverted)
    {
        Assert.Equal(expectDiverted, SharedUdidRouting.BelongsToDeviceLeg(supplied, deviceLegRequested));

        // And the diverted case is exactly the case where the simulator stops seeing the value.
        Assert.Equal(expectDiverted, SharedUdidRouting.SimulatorUdid(supplied, deviceLegRequested) == null
                                     && !string.IsNullOrEmpty(supplied));
    }

    // ===================================================================
    //  The discard is announced, not silent
    // ===================================================================

    [Theory]
    [InlineData("iOS Simulator")]
    [InlineData("--mixed-pack (sim)")]
    [InlineData("tvOS Simulator")]
    public void TheDiscardNoticeNamesTheLegTheUdidAndTheReason(string leg)
    {
        var notice = SharedUdidRouting.DiscardNotice(leg, PhoneUdid);

        Assert.Contains(leg, notice);
        Assert.Contains(PhoneUdid, notice);
        Assert.Contains("--device", notice);
    }
}
