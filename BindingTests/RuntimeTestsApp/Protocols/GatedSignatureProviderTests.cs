// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.Versioning;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Protocols;

/// <summary>
/// Dispatch tests for protocol requirements whose SIGNATURE TYPES are newer than the
/// protocol that declares them.
///
/// <para>Sibling to <see cref="StaggeredAvailabilityDelegateTests"/>, and a different
/// failure. There the requirement carried the later floor while its signature was made of
/// universally-available types, so an under-annotated forwarder still compiled and merely
/// mis-dispatched. Here the parameter and return types are themselves gated above the
/// protocol's floor, so an under-annotated witness does not compile at all — and because
/// the reverse-dispatch conformance extension carries no <c>@_cdecl</c> symbol, that error
/// attributes to module scope and costs the entire binding rather than the one member.</para>
///
/// <para>The compile gate is therefore the primary observer for this shape. These tests add
/// what compiling cannot show: that the witnesses the fix emits actually dispatch into the
/// C# conformer, across all three emission paths (method, property, subscript), rather than
/// compiling into something that silently reaches nothing.</para>
/// </summary>
public class GatedSignatureProviderTests : TestBase
{
    public GatedSignatureProviderTests(TestResults results) : base(results) { }

    /// <summary>
    /// Control: the requirement at the protocol's own floor, whose signature names no gated
    /// type. It must keep dispatching exactly as it did before the fix — the fix annotates
    /// the gated members only, so a failure here means the delta emitter widened something
    /// it should have left alone.
    /// </summary>
    [SupportedOSPlatform("ios16.0")]
    public void TestBaselineRequirementDispatchesFromSwift()
    {
        var impl = new GatedSignatureProviderImpl();
        using var harness = new GatedSignatureHarness();
        harness.Provider = impl;

        AssertEqual(GatedSignatureProviderImpl.BaselineConstant, harness.InvokeBaselineFromSwift(),
            "the ungated requirement must dispatch into the C# implementation.");
        AssertEqual(1, impl.BaselineCount, "the C# implementation must have been reached exactly once.");

        GC.KeepAlive(impl);
    }

    /// <summary>
    /// The method path, both halves of the signature at once: Swift calls
    /// <c>makeGatedPayload(seed:)</c> for a gated RETURN type and feeds the result straight
    /// back through <c>consumeGatedPayload(_:)</c> for a gated PARAMETER type. Both calls sit
    /// inside the Swift harness's <c>if #available</c> widening, so they resolve through the
    /// witness table and land in the C# conformer.
    /// </summary>
    [SupportedOSPlatform("ios17.0")]
    public void TestGatedMethodsRoundTripThroughWitnessTable()
    {
        var impl = new GatedSignatureProviderImpl();
        using var harness = new GatedSignatureHarness();
        harness.Provider = impl;

        // The conformer doubles the seed, and consume returns the doubled value it is handed.
        AssertEqual(42 * 2, harness.RoundTripGatedFromSwift(42),
            "the gated payload must survive the trip out of and back into the C# conformer.");
        AssertEqual(1, impl.MakeCount, "the gated-return requirement must have been reached.");
        AssertEqual(1, impl.ConsumeCount, "the gated-parameter requirement must have been reached.");

        GC.KeepAlive(impl);
    }

    /// <summary>
    /// The property path, which emits separately from methods — a fix wired only into the
    /// method loop leaves this one red at the compile gate and unreached at runtime.
    /// </summary>
    [SupportedOSPlatform("ios17.0")]
    public void TestGatedPropertyDispatchesThroughWitnessTable()
    {
        var impl = new GatedSignatureProviderImpl();
        using var harness = new GatedSignatureHarness();
        harness.Provider = impl;

        AssertEqual(GatedSignatureProviderImpl.PropertySeed * 2, harness.ReadGatedPayloadFromSwift(),
            "the gated property must dispatch into the C# implementation.");
        AssertEqual(1, impl.PropertyReadCount, "the C# getter must have been reached exactly once.");

        GC.KeepAlive(impl);
    }

    /// <summary>
    /// The subscript path — the third emission loop, separate from both methods and
    /// properties. An instance subscript over a plain index returning a struct takes a real
    /// vtable slot rather than degrading to a stub, so this observes a genuine witness.
    /// </summary>
    [SupportedOSPlatform("ios17.0")]
    public void TestGatedSubscriptDispatchesThroughWitnessTable()
    {
        var impl = new GatedSignatureProviderImpl();
        using var harness = new GatedSignatureHarness();
        harness.Provider = impl;

        AssertEqual(9 * 2, harness.ReadGatedSubscriptFromSwift(9),
            "the gated subscript must dispatch into the C# implementation.");
        AssertEqual(1, impl.SubscriptCount, "the C# subscript must have been reached exactly once.");
        AssertEqual(9, impl.LastSubscriptIndex, "the index must round-trip.");

        GC.KeepAlive(impl);
    }

    /// <summary>
    /// The gated payload type itself, constructed and read on the C# side. It isolates the
    /// type's own availability floor from the protocol dispatch above, so a failure in the
    /// dispatch tests can be attributed to the witness rather than to the payload.
    /// </summary>
    [SupportedOSPlatform("ios17.0")]
    public void TestGatedPayloadConstructsAndReadsOnItsOwn()
    {
        using var payload = new GatedPayload(21);

        AssertEqual(21, payload.Seed, "the seed must round-trip through the Swift initializer.");
        AssertEqual(42, payload.Doubled, "the initializer's derived field must be readable.");
    }
}

/// <summary>
/// Plain C# conformer. Counts each requirement separately so a test can tell which of the
/// three emission paths actually ran.
/// </summary>
[SupportedOSPlatform("ios16.0")]
public class GatedSignatureProviderImpl : IGatedSignatureProvider
{
    public const int BaselineConstant = 101;

    public const int PropertySeed = 63;

    public int BaselineCount { get; private set; }

    public int MakeCount { get; private set; }

    public int ConsumeCount { get; private set; }

    public int PropertyReadCount { get; private set; }

    public int SubscriptCount { get; private set; }

    public int LastSubscriptIndex { get; private set; }

    public int GetBaselineValue()
    {
        BaselineCount++;
        return BaselineConstant;
    }

    [SupportedOSPlatform("ios17.0")]
    public GatedPayload MakeGatedPayload(int seed)
    {
        MakeCount++;
        return new GatedPayload(seed);
    }

    [SupportedOSPlatform("ios17.0")]
    public int ConsumeGatedPayload(GatedPayload payload)
    {
        ConsumeCount++;
        return payload.Doubled;
    }

    [SupportedOSPlatform("ios17.0")]
    public GatedPayload CurrentGatedPayload
    {
        get
        {
            PropertyReadCount++;
            return new GatedPayload(PropertySeed);
        }
    }

    [SupportedOSPlatform("ios17.0")]
    public GatedPayload this[int gatedIndex]
    {
        get
        {
            SubscriptCount++;
            LastSubscriptIndex = gatedIndex;
            return new GatedPayload(gatedIndex);
        }
    }
}
