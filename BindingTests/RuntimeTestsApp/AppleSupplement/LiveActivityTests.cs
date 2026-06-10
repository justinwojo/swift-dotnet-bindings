// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// End-to-end runtime gate for the ActivityKit Live Activities binding: drives
// the full .NET -> @_cdecl -> ActivityKit chain from C#. The managed facade
// (Swift.ActivityKit.LiveActivity, in Swift.Bindings.Apple) projects the
// SBW_LiveActivity_* trampolines exported from SBApple.xcframework over
// [LibraryImport]; these tests call request/update/end/observe and assert the
// lifecycle contract plus the Swift registry's use-after-free hardening.
//
// What is deliberately NOT asserted here: the on-screen rendering of the Live
// Activity. The iOS Simulator does not composite third-party Live Activities
// into the Dynamic Island, and the widget extension that draws the UI is a
// separate process the harness only embeds + drives on the --device leg (where
// the rendered pixel is the proof). On the simulator request() still succeeds
// and the OS tracks the activity with no widget present — exactly the contract
// this fixture pins. So these tests pass on sim WITHOUT a widget extension.

using System;
using System.Threading.Tasks;
using RuntimeTestsApp.Infrastructure;
using Swift.ActivityKit;

namespace RuntimeTestsApp.AppleSupplement;

/// <summary>
/// Lifecycle + hardening + JSON-boundary coverage for <see cref="LiveActivity"/>.
/// </summary>
public class LiveActivityTests : TestBase
{
    public LiveActivityTests(TestResults results) : base(results) { }

    // The content-based ActivityKit API needs iOS 16.2+; below that the facade's
    // EnsureSupported() throws PlatformNotSupportedException by design, and the
    // app's minimum OS is 15.0 — so gate at runtime and early-return (the
    // established AppleSupplement convention, see AppleSupplementRoundTripTests)
    // instead of failing all eight tests on an older runtime.
    private static bool IsSupportedOS => OperatingSystem.IsIOSVersionAtLeast(16, 2);

    /// <summary>
    /// Exercises the @_cdecl Int-&gt;bool projection. The VALUE is asserted only on
    /// the simulator, where the harness Info.plist carries
    /// <c>NSSupportsLiveActivities</c> and a fresh simulator defaults the per-app
    /// toggle on. On a physical device the toggle is user-controlled Settings
    /// state the harness cannot pin — there, completing the call is the binding
    /// assertion.
    /// </summary>
    public void TestAreActivitiesEnabled_ReportsCapabilityPresent()
    {
        if (!IsSupportedOS)
        {
            TestLogger.Info("ActivityKit requires iOS 16.2+; skipping on this runtime.");
            return;
        }
        bool enabled = LiveActivity.AreActivitiesEnabled;
        if (ObjCRuntime.Runtime.Arch == ObjCRuntime.Arch.SIMULATOR)
        {
            AssertTrue(enabled,
                "Live Activities enabled (NSSupportsLiveActivities present, sim toggle on)");
        }
        else if (!enabled)
        {
            TestLogger.Info(
                "Live Activities are toggled off in Settings on this device; value assertion skipped.");
        }
    }

    /// <summary>
    /// request -&gt; live handle -&gt; update -&gt; end, the happy path. Proves the
    /// JSON attributes/content-state cross the C ABI and the handle round-trips.
    /// </summary>
    public async Task TestLifecycle_RequestUpdateEnd_RoundTrips()
    {
        if (!await EnsureReadyToRequestAsync()) return;
        LiveActivity activity = LiveActivity.Request(
            name: "delivery",
            attributesJson: "{\"title\":\"Order #42\"}",
            contentStateJson: "{\"status\":\"preparing\"}");
        try
        {
            AssertTrue(activity.IsActive, "request returned a live handle");
            AssertTrue(activity.Update("{\"status\":\"out for delivery\"}"),
                "update dispatched on a live handle");
        }
        finally
        {
            AssertTrue(activity.End("{\"status\":\"delivered\"}", immediate: true),
                "end succeeded on a live handle");
        }
        AssertFalse(activity.IsActive, "handle is dead after end");
    }

    /// <summary>
    /// A second End() on the same handle is a safe no-op (the Swift registry
    /// removed the handle on the first end), not a double-free.
    /// </summary>
    public async Task TestEnd_IsIdempotent()
    {
        if (!await EnsureReadyToRequestAsync()) return;
        LiveActivity activity = LiveActivity.Request("delivery");
        AssertTrue(activity.End(immediate: true), "first end returns true");
        AssertFalse(activity.End(immediate: true), "second end is a safe no-op");
    }

    /// <summary>
    /// Update() after End() must return false rather than dereference a dangling
    /// Activity — the core use-after-free hardening the id-&gt;Activity registry buys.
    /// </summary>
    public async Task TestUpdate_AfterEnd_IsSafeNoOp()
    {
        if (!await EnsureReadyToRequestAsync()) return;
        LiveActivity activity = LiveActivity.Request("delivery");
        activity.End(immediate: true);
        AssertFalse(activity.Update("{\"status\":\"late\"}"),
            "update after end is a safe no-op (registry hardening)");
    }

    /// <summary>
    /// The push-token observation contract: registers true on a live handle,
    /// false on a dead one. No APNs/push capability is needed — no token ever
    /// arrives, but the handle-validation path and GCHandle root/free are exercised.
    /// </summary>
    public async Task TestObservePushToken_ValidatesHandle()
    {
        if (!await EnsureReadyToRequestAsync()) return;
        LiveActivity activity = LiveActivity.Request("delivery");
        AssertTrue(activity.ObservePushToken(_ => { }),
            "observe registers on a live handle");
        activity.End(immediate: true);
        AssertFalse(activity.ObservePushToken(_ => { }),
            "observe returns false on a dead handle");
    }

    /// <summary>
    /// Nested/escaped JSON (quotes, non-ASCII) must round-trip losslessly through
    /// the null-terminated UTF-8 boundary the shim uses for the payload blobs.
    /// </summary>
    public async Task TestRequest_WithEscapedUnicodeJson_Succeeds()
    {
        if (!await EnsureReadyToRequestAsync()) return;
        LiveActivity activity = LiveActivity.Request(
            name: "delivery",
            attributesJson: "{\"meta\":{\"id\":7,\"tags\":[\"a\",\"b\"]}}",
            contentStateJson: "{\"note\":\"He said \\\"hi\\\" \\u00e9 \\ud83c\\udf0d\"}");
        try
        {
            AssertTrue(activity.IsActive, "escaped + unicode JSON request succeeded");
        }
        finally
        {
            activity.End(immediate: true);
        }
    }

    /// <summary>
    /// The documented "a second observe replaces the first" contract. Both calls
    /// return true on a live handle, and replacing must not crash: the Swift side
    /// cancels the prior observer task and releases its managed context rather than
    /// leaving the old GCHandle dangling for a still-running task. No token ever
    /// arrives on the simulator (no APNs), so this pins the register/replace/release
    /// path — the use-after-free surface — not delivery.
    /// </summary>
    public async Task TestObservePushToken_SecondCallReplaces_DoesNotCrash()
    {
        if (!await EnsureReadyToRequestAsync()) return;
        LiveActivity activity = LiveActivity.Request("delivery");
        try
        {
            AssertTrue(activity.ObservePushToken(_ => { }), "first observe registers");
            AssertTrue(activity.ObservePushToken(_ => { }), "second observe replaces the first");
        }
        finally
        {
            AssertTrue(activity.End(immediate: true), "end after replace succeeds");
        }
        AssertFalse(activity.ObservePushToken(_ => { }), "observe after end is a no-op");
    }

    /// <summary>
    /// A raw embedded NUL is invalid JSON, so the facade's JSON validation rejects
    /// it up front rather than letting Swift's <c>String(cString:)</c> silently
    /// truncate the payload at the first NUL on the C-string crossing.
    /// </summary>
    public void TestRequest_WithEmbeddedNul_Throws()
    {
        if (!IsSupportedOS) return;
        AssertThrows<ArgumentException>(
            () => LiveActivity.Request("delivery", "{\"a\":\"b\0c\"}"),
            "embedded NUL in attributes JSON is rejected");
    }

    /// <summary>
    /// Malformed JSON would start an activity whose widget extension (a separate
    /// process) silently renders nothing — invisible from .NET. The facade rejects
    /// it before it crosses the boundary; throws before any P/Invoke, so no
    /// foreground wait is needed.
    /// </summary>
    public void TestRequest_WithMalformedJson_Throws()
    {
        if (!IsSupportedOS) return;
        AssertThrows<ArgumentException>(
            () => LiveActivity.Request("delivery", "{\"status\" \"missing colon\"}"),
            "malformed attributes JSON is rejected before crossing the boundary");
        AssertThrows<ArgumentException>(
            () => LiveActivity.Request("delivery", contentStateJson: "[1,2,3]"),
            "non-object content-state JSON is rejected before crossing the boundary");
    }

    /// <summary>
    /// Common preamble for the Request-issuing tests: iOS 16.2 gate, bounded
    /// foreground-active wait (a HARD failure on timeout, so a slow launch shows
    /// up as "never reached foreground" instead of a bare ActivityKit visibility
    /// error), and the user-toggleable enablement state (runtime skip when off —
    /// a disabled app cannot request, and that is environment, not binding,
    /// state). Returns false when the test should early-return as a runtime skip.
    /// </summary>
    private async Task<bool> EnsureReadyToRequestAsync()
    {
        if (!IsSupportedOS)
        {
            TestLogger.Info("ActivityKit requires iOS 16.2+; skipping on this runtime.");
            return false;
        }
        AssertTrue(
            await ActivityKitReadiness.WaitForForegroundActiveAsync(TimeSpan.FromSeconds(10)),
            "app reached foreground-active within 10s (ActivityKit request precondition)");
        if (!LiveActivity.AreActivitiesEnabled)
        {
            TestLogger.Info("Live Activities are disabled for this app; skipping request-based test.");
            return false;
        }
        return true;
    }
}
