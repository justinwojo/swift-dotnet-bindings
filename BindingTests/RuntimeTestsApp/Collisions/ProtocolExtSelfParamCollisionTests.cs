// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Collisions;

/// <summary>
/// P1-22 (§4 residual — protocol-extension wrapper param escaping). A protocol-extension method's
/// @_cdecl wrapper injects a synthetic receiver binding <c>self_: UnsafeMutableRawPointer</c>. A user
/// parameter literally spelled <c>self_</c> would produce a duplicate Swift binding, swiftc rejects
/// the wrapper, and the build silently strips the symbol from the dylib — surfacing only at runtime as
/// <c>EntryPointNotFoundException</c>. The fix escapes the user binding
/// (<c>EscapeReservedSwiftWrapperLabel</c>: <c>self_</c> -> <c>__self_</c>) while preserving the Swift
/// argument label <c>self_:</c> at the call site.
///
/// <c>SyntheticExtProtocol.mixSelf(self_:)</c> takes a user param named <c>self_</c>. Reaching this
/// assertion at all proves the wrapper compiled and the symbol was exported (no strip); the
/// round-tripped value proves the escaped binding carries the user argument, not the injected receiver.
/// </summary>
public class ProtocolExtSelfParamCollisionTests : TestBase
{
    public ProtocolExtSelfParamCollisionTests(TestResults results) : base(results) { }

    public void TestSeedGetterUnaffected()
    {
        using var c = new SyntheticExtConformer(7);
        AssertEqual(7, c.Seed, "stored property `seed` projects to the Seed getter");
    }

    public void TestMixSelfUserParamRoundTrips()
    {
        using var c = new SyntheticExtConformer(7);
        // mixSelf(self_:) -> seed * 100 + self_. If the synthetic-receiver collision stripped the
        // symbol, this call would throw EntryPointNotFoundException instead of returning 703.
        AssertEqual(703, c.MixSelf(3),
            "MixSelf(3) -> seed*100 + self_ = 7*100 + 3; user param `self_` survived the synthetic-receiver escape");
        TestLogger.Info("§4 protocol-extension self_ escape round-trip passed");
    }

    public void TestMixSelfDistinctArgumentsProveBindingCarriesUserValue()
    {
        using var c = new SyntheticExtConformer(2);
        // Vary self_ to prove the value flows through the escaped (__self_) binding, not a constant
        // or the receiver pointer.
        AssertEqual(205, c.MixSelf(5), "MixSelf(5) -> 2*100 + 5");
        AssertEqual(209, c.MixSelf(9), "MixSelf(9) -> 2*100 + 9");
    }
}
