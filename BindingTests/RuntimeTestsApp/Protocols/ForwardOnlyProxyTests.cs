// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;
using Swift;

namespace RuntimeTestsApp.Protocols;

/// <summary>
/// Tests for FORWARD-ONLY (read-only) protocol proxies whose protocol cannot host an
/// EveryProtocol reverse-dispatch conformance but whose <c>any P</c> existential is a valid
/// READ target through its own witness table.
///
/// Reproduces RealityFoundation <c>ModelComponent.materials -&gt; [any Material]</c>: the
/// <c>Material</c> proxy was fully suppressed (it can't host a reverse conformance — an
/// <c>__</c>-prefixed hidden requirement is stripped from the framework ABI), so the getter
/// emitted <c>throw new NotSupportedException("Protocol proxy not available...")</c>. The
/// fix admits these protocols as forward-only proxies, so the getter projects the existential.
///
/// The hidden-requirement shape is not reproducible on this test library's toolchain (the
/// digester keeps <c>__</c> names here), so the IDENTICAL forward-read mechanism is exercised
/// through the deterministic <c>InheritsUnsatisfiedStdlibProtocol</c> shape:
/// <c>ForwardReadableTrait: CustomStringConvertible</c> and the literal PhysicsJoint shape
/// <c>ForwardReadableJoint: Equatable</c>. Before the fix every getter below threw
/// <see cref="NotSupportedException"/>.
/// </summary>
public class ForwardOnlyProxyTests : TestBase
{
    public ForwardOnlyProxyTests(TestResults results) : base(results) { }

    #region Scalar `any P` getter (the suppressed-getter crash site)

    public void TestScalarExistentialGetterRoundTrips()
    {
        var vendor = new ForwardReadableVendor();
        // Before the fix: `get => throw new NotSupportedException(...)`.
        var primary = vendor.Primary;
        AssertNotNull(primary, "vendor.Primary returned a forward-only proxy (was NotSupportedException)");
        AssertEqual("solo", primary.DisplayName, "Primary.DisplayName forward-dispatched");
        AssertEqual("summary(solo)", primary.GetSummary(), "Primary.GetSummary() forward-dispatched");
        (primary as IDisposable)?.Dispose();
        TestLogger.Info("Scalar forward-only existential getter round-tripped");
    }

    public void TestMethodReturnExistentialRoundTrips()
    {
        var vendor = new ForwardReadableVendor();
        var made = vendor.MakePrimary();
        AssertNotNull(made, "vendor.MakePrimary() returned a forward-only proxy (was NotSupportedException)");
        AssertEqual("solo", made.DisplayName, "MakePrimary().DisplayName forward-dispatched");
        (made as IDisposable)?.Dispose();
        TestLogger.Info("Method-return forward-only existential round-tripped");
    }

    #endregion

    #region `[any P]` array getter — the literal `ModelComponent.materials` shape

    public void TestExistentialArrayGetterRoundTrips()
    {
        var vendor = new ForwardReadableVendor();
        // Before the fix: `get => throw new NotSupportedException(...)`.
        var all = vendor.All;
        AssertNotNull(all, "vendor.All returned a list (was NotSupportedException)");
        AssertEqual(2, all.Count, "vendor.All.Count");
        AssertEqual("alpha", all[0].DisplayName, "All[0].DisplayName");
        AssertEqual("beta", all[1].DisplayName, "All[1].DisplayName");
        AssertEqual("summary(alpha)", all[0].GetSummary(), "All[0].GetSummary()");
        foreach (var e in all)
            (e as IDisposable)?.Dispose();
        TestLogger.Info($"Forward-only existential array round-tripped: {all.Count} elements");
    }

    #endregion

    #region `(any P)?` optional getter

    public void TestOptionalExistentialGetterRoundTrips()
    {
        var vendor = new ForwardReadableVendor();
        var maybe = vendor.Maybe;
        AssertNotNull(maybe, "vendor.Maybe returned a forward-only proxy (was NotSupportedException)");
        AssertEqual("solo", maybe!.DisplayName, "Maybe.DisplayName forward-dispatched");
        (maybe as IDisposable)?.Dispose();
        TestLogger.Info("Optional forward-only existential getter round-tripped");
    }

    #endregion

    #region Equatable-constrained protocol — the literal `PhysicsJoint: Equatable` shape

    public void TestEquatableConstrainedExistentialGetterRoundTrips()
    {
        var vendor = new ForwardReadableJointVendor();
        // `ForwardReadableJoint: Equatable` mirrors RealityFoundation `PhysicsJoint`'s
        // `<Self: Swift.Equatable>` — reverse-impossible (EveryProtocol can't synthesize `==`),
        // forward-readable for the non-`Self` members. Before the fix: NotSupportedException.
        var joint = vendor.PrimaryJoint;
        AssertNotNull(joint, "vendor.PrimaryJoint returned a forward-only proxy (was NotSupportedException)");
        AssertEqual("hinge", joint.JointLabel, "PrimaryJoint.JointLabel forward-dispatched");
        (joint as IDisposable)?.Dispose();
        TestLogger.Info("Equatable-constrained forward-only existential getter round-tripped");
    }

    #endregion
}
