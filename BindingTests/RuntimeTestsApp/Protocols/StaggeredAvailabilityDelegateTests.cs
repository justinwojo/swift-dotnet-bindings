// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.Versioning;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Protocols;

/// <summary>
/// Dispatch tests for a protocol requirement introduced LATER than the protocol that
/// declares it, where a protocol extension supplies a same-signature default.
///
/// The generated Swift forwarder that C# calls to reach the requirement has to be
/// declared with the merged availability of the protocol and the requirement. If it is
/// declared with the protocol's floor alone, the requirement is not visible inside the
/// forwarder body and Swift silently resolves the call to the extension default — a
/// static call, no witness-table dispatch, no diagnostic. The conformer's implementation
/// is then never reached, which is what these tests observe:
/// <see cref="SwiftBindingsTestLib.Functions.GetStaggeredDefaultImplementationReachCount"/>
/// is the tripwire that separates "reached the default" from "reached nothing".
/// </summary>
public class StaggeredAvailabilityDelegateTests : TestBase
{
    public StaggeredAvailabilityDelegateTests(TestResults results) : base(results) { }

    /// <summary>
    /// The requirement that is newer than its protocol, called through the delegate value
    /// read back out of Swift (so the call travels C# → forwarder → witness table → C#).
    /// </summary>
    [SupportedOSPlatform("ios17.0")]
    public void TestNewerRequirementDispatchesThroughReadBackProxy()
    {
        Functions.ResetStaggeredAvailabilityCounters();

        var impl = new StaggeredAvailabilityDelegateImpl();
        using var harness = new StaggeredAvailabilityHarness();
        harness.Delegate = impl;

        var readBack = harness.Delegate;
        AssertNotNull(readBack, "the delegate property must read back the stored existential.");

        readBack!.NewerDidChange(41);

        AssertEqual(1, impl.NewerCount,
            "the newer requirement must dispatch into the C# implementation.");
        AssertEqual(41, impl.LastNewerValue, "the parameter must round-trip.");
        AssertEqual(0, Functions.GetStaggeredDefaultImplementationReachCount(),
            "the protocol extension's default must NOT run — reaching it means the forwarder " +
            "bound the call statically instead of dispatching through the witness table.");

        GC.KeepAlive(impl);
    }

    /// <summary>
    /// Control: the requirement that is as old as its protocol takes the same forwarder
    /// path and already dispatched correctly, so a failure here means something broader
    /// than the availability stagger broke.
    /// </summary>
    [SupportedOSPlatform("ios16.0")]
    public void TestOlderRequirementDispatchesThroughReadBackProxy()
    {
        Functions.ResetStaggeredAvailabilityCounters();

        var impl = new StaggeredAvailabilityDelegateImpl();
        using var harness = new StaggeredAvailabilityHarness();
        harness.Delegate = impl;

        var readBack = harness.Delegate;
        AssertNotNull(readBack, "the delegate property must read back the stored existential.");

        readBack!.OlderDidChange(7);

        AssertEqual(1, impl.OlderCount,
            "the protocol-floor requirement must dispatch into the C# implementation.");
        AssertEqual(7, impl.LastOlderValue, "the parameter must round-trip.");

        GC.KeepAlive(impl);
    }

    /// <summary>
    /// Control from the other side: Swift itself calls the newer requirement from a context
    /// widened with <c>if #available</c>, which resolves to the witness table by construction.
    /// It proves the conformance and its witness thunk are sound, so a failure of the
    /// read-back test above is the forwarder's availability and nothing else.
    /// </summary>
    [SupportedOSPlatform("ios16.0")]
    public void TestNewerRequirementDispatchesFromSwiftInvocation()
    {
        Functions.ResetStaggeredAvailabilityCounters();

        var impl = new StaggeredAvailabilityDelegateImpl();
        using var harness = new StaggeredAvailabilityHarness();
        harness.Delegate = impl;

        harness.InvokeNewerFromSwift(13);

        AssertEqual(1, impl.NewerCount,
            "a Swift-side call inside an `if #available` widening must reach the C# implementation.");
        AssertEqual(13, impl.LastNewerValue, "the parameter must round-trip.");
        AssertEqual(0, Functions.GetStaggeredDefaultImplementationReachCount(),
            "the protocol extension's default must NOT run for a conformer that implements the requirement.");

        GC.KeepAlive(impl);
    }

    /// <summary>
    /// The property whose Swift setter is introduced after the property itself, written
    /// through the delegate value read back out of Swift. The write travels C# → setter
    /// forwarder → witness table → C#, so it only lands if the setter got its own witness
    /// slot and its own forwarder instead of collapsing onto the getter. The running OS
    /// satisfies both floors, so the proxy's availability guard must let the call through
    /// rather than throwing.
    /// </summary>
    [SupportedOSPlatform("ios17.0")]
    public void TestStaggeredSetterDispatchesThroughReadBackProxy()
    {
        Functions.ResetStaggeredAvailabilityCounters();

        var impl = new StaggeredAvailabilityDelegateImpl();
        using var harness = new StaggeredAvailabilityHarness();
        harness.Delegate = impl;

        var readBack = harness.Delegate;
        AssertNotNull(readBack, "the delegate property must read back the stored existential.");

        readBack!.StaggeredValue = 55;

        AssertEqual(1, impl.SetterCallCount,
            "the later-introduced setter must dispatch into the C# implementation.");
        AssertEqual(55, impl.StaggeredValue, "the assigned value must round-trip.");

        GC.KeepAlive(impl);
    }

    /// <summary>
    /// The same property read and written from the Swift side, where the write is reached
    /// inside an <c>if #available</c> widening. Proves the conformance carries BOTH accessors
    /// in its witness table — the getter walk has not cost the setter its own slot.
    /// </summary>
    [SupportedOSPlatform("ios16.0")]
    public void TestStaggeredAccessorsDispatchFromSwiftInvocation()
    {
        Functions.ResetStaggeredAvailabilityCounters();

        var impl = new StaggeredAvailabilityDelegateImpl();
        using var harness = new StaggeredAvailabilityHarness();
        harness.Delegate = impl;

        harness.WriteStaggeredValueFromSwift(88);

        AssertEqual(1, impl.SetterCallCount,
            "a Swift-side write inside an `if #available` widening must reach the C# setter.");
        AssertEqual(88, harness.ReadStaggeredValueFromSwift(),
            "the Swift-side read must observe the value the Swift-side write stored.");

        GC.KeepAlive(impl);
    }
}

/// <summary>
/// Plain C# conformer. Records each requirement separately so a test can tell which one ran.
/// </summary>
[SupportedOSPlatform("ios16.0")]
public class StaggeredAvailabilityDelegateImpl : IStaggeredAvailabilityDelegate
{
    public int OlderCount { get; private set; }

    public int LastOlderValue { get; private set; }

    public int NewerCount { get; private set; }

    public int LastNewerValue { get; private set; }

    public void OlderDidChange(int value)
    {
        OlderCount++;
        LastOlderValue = value;
    }

    [SupportedOSPlatform("ios17.0")]
    public void NewerDidChange(int value)
    {
        NewerCount++;
        LastNewerValue = value;
    }

    public int SetterCallCount { get; private set; }

    private int _staggeredValue;

    // The generated interface declares this as an ungated `{ get; set; }`: the Swift
    // requirement's accessor-level `@available` does not survive into the ABI input this
    // fixture is generated from. Gating the accessor here would claim a narrower platform
    // range than the interface member it implements.
    public int StaggeredValue
    {
        get => _staggeredValue;
        set
        {
            SetterCallCount++;
            _staggeredValue = value;
        }
    }
}
