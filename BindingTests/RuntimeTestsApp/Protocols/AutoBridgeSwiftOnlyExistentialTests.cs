// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Protocols;

/// <summary>
/// Pins the autoBridge Swift-only existential filter fix. Mirrors the RealityKit
/// <c>MultipeerConnectivityService.Owner(Entity) -&gt; any RealityFoundation.SynchronizationPeerID</c>
/// suppression pattern via Foundation.LocalizedError on a BindingTests-owned
/// fixture.
///
/// Pre-fix, <c>TypeDatabaseExtensions.IsObjCModuleType</c> classified every
/// non-value-type from an autoBridge module as ObjC even when the type's name
/// didn't match its module's declared <c>objcPrefixes</c>. That stripped Swift-only
/// protocols out of <c>ExistentialHandler.GetEffectiveProtocols</c>, dropped the
/// effective count to 0, returned <c>"object"</c> from <c>GetPublicExistentialType</c>,
/// and tripped <c>B6 UnsupportedExistential</c> in <c>MemberEmissionValidator</c> —
/// the method never appeared in the generated bindings.
///
/// Post-fix, <c>IsObjCExistentialBridgedProtocol</c> uses the per-module ObjC prefix
/// gate, so <c>Foundation.LocalizedError</c> (Foundation declares <c>["NS"]</c>) survives
/// the filter, the method emits, and the call below is reachable from C#. The
/// existential return falls back to <c>object</c> because Foundation isn't bound to
/// a C# interface — we only assert reachability + non-null payload here.
/// </summary>
public class AutoBridgeSwiftOnlyExistentialTests : TestBase
{
    public AutoBridgeSwiftOnlyExistentialTests(TestResults results) : base(results) { }

    public void TestOwnerConstructorSucceeds()
    {
        var owner = new AutoBridgeSwiftOnlyExistentialOwner("hello");
        AssertNotNull(owner, "AutoBridgeSwiftOnlyExistentialOwner constructed");
        TestLogger.Info("AutoBridgeSwiftOnlyExistentialOwner(description:) construction passed");
    }

    public void TestGetOwnerMethodIsReachable()
    {
        // The trip-wire: pre-fix the generator suppressed `owner()` entirely
        // because effective.Count==0 → GetPublicExistentialType returned "object"
        // → B6 UnsupportedExistential validator rejected the member. Reaching
        // this call site at all proves the method survives emission.
        var owner = new AutoBridgeSwiftOnlyExistentialOwner("hello");
        var existential = owner.GetOwner();
        AssertNotNull(existential, "owner() returned non-null existential");
        TestLogger.Info("AutoBridgeSwiftOnlyExistentialOwner.GetOwner() returned a non-null existential (B6 suppression no longer fires)");
    }

    public void TestGetOwnerSurvivesMultipleCalls()
    {
        // Sanity: the existential container marshalling round-trips on
        // repeated invocations without leaking or crashing.
        var owner = new AutoBridgeSwiftOnlyExistentialOwner("repeat");
        for (int i = 0; i < 3; i++)
        {
            var existential = owner.GetOwner();
            AssertNotNull(existential, $"owner() call #{i + 1} returned non-null existential");
        }
        TestLogger.Info("AutoBridgeSwiftOnlyExistentialOwner.GetOwner() survived 3 sequential calls");
    }
}
