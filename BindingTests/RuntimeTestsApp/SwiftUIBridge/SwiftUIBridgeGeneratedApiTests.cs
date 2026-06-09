// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#if SWIFTUI_BRIDGE

using System.Runtime.InteropServices;
using System.Text;
using RuntimeTestsApp.Infrastructure;

namespace RuntimeTestsApp.SwiftUIBridge;

/// <summary>
/// End-to-end runtime gate for SwiftUI-bridge defect fixes.
/// Each test exercises the GENERATED public Session API (managed Action → generated
/// trampoline), not the raw delegate* path, so the UnmanagedCallersOnly
/// fail-fast guards and callback marshalling are genuinely on the hot path rather than
/// bypassed.
///
///   ArrayEnumView         — [AlertStyle] failable enum decode, no force-unwrap crash
///   EnumModifierView      — self-returning modifier w/ Optional&lt;AlertStyle&gt; (construct smoke)
///   UrlParamView          — URL param crosses the Create ABI as an ObjC-bridgeable struct
///   OptionalUrlParamView  — URL? param — Some + nil
///   UrlResultView         — Result&lt;URL, ScanError&gt; success branch must not UAF the bridged NSURL
///   UrlClosureView        — typed (URL)-&gt;Void — bridged NSURL arg via GetNSObject, not raw bytes
///   FrozenRefClosureView  — @frozen struct w/ String field — heap-buffer ARC marshalling
///   UserDataAsyncView     — async Create whose user params collide with synthetic trailing params
///   HandleParamView       — init params named handle/session must not collide with generated locals
/// </summary>
public class SwiftUIBridgeGeneratedApiTests : TestBase
{
    public SwiftUIBridgeGeneratedApiTests(TestResults results) : base(results) { }

    // ────────────────────────────────────────────────────────────────
    // ArrayEnumView — [BoundEnum] failable decode
    // ────────────────────────────────────────────────────────────────

    public void TestArrayEnumView_ValidEnumsRoundTrip()
    {
        using var session = global::SwiftBindingsTestLib.ArrayEnumViewSession.Create(new[]
        {
            global::SwiftBindingsTestLib.AlertStyle.Info,
            global::SwiftBindingsTestLib.AlertStyle.Warning,
            global::SwiftBindingsTestLib.AlertStyle.Error,
        });

        AssertEqual(3, BridgeTestHelpers.ArrayEnumView_GetCount(session.Handle), "ArrayEnumView decoded 3 styles");
        AssertEqual(0, BridgeTestHelpers.ArrayEnumView_GetElement(session.Handle, 0), "styles[0] == Info");
        AssertEqual(1, BridgeTestHelpers.ArrayEnumView_GetElement(session.Handle, 1), "styles[1] == Warning");
        AssertEqual(2, BridgeTestHelpers.ArrayEnumView_GetElement(session.Handle, 2), "styles[2] == Error");
        TestLogger.Info("ArrayEnumView: valid enum array round-tripped");
    }

    public void TestArrayEnumView_EmptyArrayDecodesToZero()
    {
        using var session = global::SwiftBindingsTestLib.ArrayEnumViewSession.Create(
            global::System.Array.Empty<global::SwiftBindingsTestLib.AlertStyle>());

        AssertEqual(0, BridgeTestHelpers.ArrayEnumView_GetCount(session.Handle), "empty styles → count 0");
        TestLogger.Info("ArrayEnumView: empty array decoded to 0");
    }

    public void TestArrayEnumView_InvalidRawValueThrowsNotCrash()
    {
        // An out-of-range raw value must surface as a graceful
        // InvalidOperationException (Swift failable init → nil → Create returns null),
        // NOT a force-unwrap SIGTRAP inside the @_cdecl wrapper.
        AssertThrows<InvalidOperationException>(() =>
        {
            using var session = global::SwiftBindingsTestLib.ArrayEnumViewSession.Create(new[]
            {
                (global::SwiftBindingsTestLib.AlertStyle)99,
            });
        }, "invalid AlertStyle raw value throws InvalidOperationException");
        TestLogger.Info("ArrayEnumView: invalid raw value degraded gracefully (no force-unwrap crash)");
    }

    // ────────────────────────────────────────────────────────────────
    // EnumModifierView — Optional<BoundEnum> self-returning modifier
    // (the modifier emits no runtime entry point; this is the construct smoke
    //  for the optional-enum modifier emission path)
    // ────────────────────────────────────────────────────────────────

    public void TestEnumModifierView_CreateAndGetVC()
    {
        using var session = global::SwiftBindingsTestLib.EnumModifierViewSession.Create(title: "audit");
        AssertTrue(session.Handle != IntPtr.Zero, "EnumModifierView handle != 0");
        AssertTrue(session.GetViewController() != IntPtr.Zero, "EnumModifierView GetVC != 0");
        TestLogger.Info("EnumModifierView: create/getVC cycle passed");
    }

    // ────────────────────────────────────────────────────────────────
    // UrlParamView — ObjC-bridgeable struct (URL) param
    // ────────────────────────────────────────────────────────────────

    public void TestUrlParamView_UrlRoundTrips()
    {
        const string url = "https://audit.example/p0-04/target";
        using var session = global::SwiftBindingsTestLib.UrlParamViewSession.Create(new Foundation.NSUrl(url));

        int expectedLen = Encoding.UTF8.GetByteCount(url);
        AssertEqual(expectedLen, BridgeTestHelpers.UrlParamView_GetTargetLength(session.Handle),
            "UrlParamView target absoluteString length matches input");
        TestLogger.Info("UrlParamView: URL crossed Create ABI intact");
    }

    // ────────────────────────────────────────────────────────────────
    // OptionalUrlParamView — Optional<ObjC-bridgeable struct>
    // ────────────────────────────────────────────────────────────────

    public void TestOptionalUrlParamView_SomeRoundTrips()
    {
        const string url = "https://audit.example/p0-04/optional";
        using var session = global::SwiftBindingsTestLib.OptionalUrlParamViewSession.Create(new Foundation.NSUrl(url));

        int expectedLen = Encoding.UTF8.GetByteCount(url);
        AssertEqual(expectedLen, BridgeTestHelpers.OptionalUrlParamView_GetTargetLength(session.Handle),
            "OptionalUrlParamView Some(URL) length matches input");
        TestLogger.Info("OptionalUrlParamView: Some(URL) round-tripped");
    }

    public void TestOptionalUrlParamView_NilEncodesAsNil()
    {
        using var session = global::SwiftBindingsTestLib.OptionalUrlParamViewSession.Create(target: null);

        // Helper returns -2 when the stored target is nil.
        AssertEqual(-2, BridgeTestHelpers.OptionalUrlParamView_GetTargetLength(session.Handle),
            "OptionalUrlParamView nil target stays nil across the ABI");
        TestLogger.Info("OptionalUrlParamView: nil round-tripped as nil");
    }

    // ────────────────────────────────────────────────────────────────
    // UrlResultView — Result<URL, ScanError> success-branch UAF probe
    // ────────────────────────────────────────────────────────────────

    public void TestUrlResultView_SuccessUrlSurvivesCallback()
    {
        Foundation.NSUrl? captured = null;
        int successCount = 0;

        using var session = global::SwiftBindingsTestLib.UrlResultViewSession.Create(
            onResultSuccess: u => { captured = u; successCount++; });

        int rc = BridgeTestHelpers.UrlResultView_InvokeSuccess(session.Handle, 7);
        AssertEqual(1, rc, "UrlResultView invoke-success returned 1");
        AssertEqual(1, successCount, "success callback fired exactly once");
        AssertNotNull(captured, "bridged NSURL delivered to callback (not freed)");
        // If the bridged temporary were released before the callback read it (UAF),
        // AbsoluteString would be garbage or this would crash.
        AssertEqual("https://audit.example/url-result/7", captured!.AbsoluteString,
            "NSURL absoluteString intact across the synchronous callback");
        TestLogger.Info("UrlResultView: success URL survived the callback");
    }

    public void TestUrlResultView_ErrorRoundTrips()
    {
        global::SwiftBindingsTestLib.ScanError? captured = null;
        int errorCount = 0;

        using var session = global::SwiftBindingsTestLib.UrlResultViewSession.Create(
            onResultError: e => { captured = e; errorCount++; });

        int rc = BridgeTestHelpers.UrlResultView_InvokeError(session.Handle, -99);
        AssertEqual(1, rc, "UrlResultView invoke-error returned 1");
        AssertEqual(1, errorCount, "error callback fired exactly once");
        AssertNotNull(captured, "ScanError delivered to callback");
        AssertEqual(-99, captured!.Code, "ScanError.code round-tripped");
        TestLogger.Info("UrlResultView: error branch round-tripped");
    }

    // ────────────────────────────────────────────────────────────────
    // UrlClosureView — typed (URL)->Void closure with ObjC-bridgeable struct arg
    // ────────────────────────────────────────────────────────────────

    public void TestUrlClosureView_DeliversBridgedUrl()
    {
        Foundation.NSUrl? captured = null;
        int pickCount = 0;

        using var session = global::SwiftBindingsTestLib.UrlClosureViewSession.Create(
            onPick: u => { captured = u; pickCount++; });

        int rc = BridgeTestHelpers.UrlClosureView_InvokeOnPick(session.Handle, 42);
        AssertEqual(1, rc, "UrlClosureView invoke-onPick returned 1");
        AssertEqual(1, pickCount, "onPick fired exactly once");
        AssertNotNull(captured, "bridged NSURL delivered to typed closure (not freed)");
        // Pre-fix: the Swift side heap-allocated the raw URL struct bytes and the C# trampoline
        // read them via MarshalFromSwift<NSUrl> (assumingMemoryBound), reinterpreting an object
        // pointer as struct memory → garbage AbsoluteString or SIGSEGV.
        AssertEqual("https://audit.example/url-closure/42", captured!.AbsoluteString,
            "NSURL absoluteString intact through the typed-closure ABI");
        TestLogger.Info("UrlClosureView: bridged NSURL delivered through typed (URL)->Void closure");
    }

    // ────────────────────────────────────────────────────────────────
    // FrozenRefClosureView — @frozen struct w/ ref field buffer marshalling
    // ────────────────────────────────────────────────────────────────

    public void TestFrozenRefClosureView_FrozenRefArgRoundTrips()
    {
        global::SwiftBindingsTestLib.FrozenRefArg? captured = null;
        int eventCount = 0;

        using var session = global::SwiftBindingsTestLib.FrozenRefClosureViewSession.Create(
            onEvent: a => { captured = a; eventCount++; });

        int rc = BridgeTestHelpers.FrozenRefClosureView_InvokeOnEvent(session.Handle, 5);
        AssertEqual(1, rc, "FrozenRefClosureView invoke returned 1");
        AssertEqual(1, eventCount, "onEvent fired exactly once");
        AssertNotNull(captured, "FrozenRefArg delivered to callback");
        // A wrong heap-buffer copy would corrupt or leak the String field.
        AssertEqual("frozen-ref-5", captured!.S, "FrozenRefArg.s round-tripped through the heap buffer");
        TestLogger.Info("FrozenRefClosureView: frozen-with-ref struct arg round-tripped");
    }

    // ────────────────────────────────────────────────────────────────
    // UserDataAsyncView — async Create with colliding trailing params
    // ────────────────────────────────────────────────────────────────

    public async Task TestUserDataAsyncView_CreateAsyncDedupedParams()
    {
        // The async Create appends synthetic trailing `userData`/`onError` params that
        // collide with the user's identically-named primitive params. The dedup renames
        // the synthetic ones; if it failed this wouldn't have compiled. Exercising it at
        // runtime confirms the renamed wiring is also connected correctly.
        using var session = await WithTimeout(
            global::SwiftBindingsTestLib.UserDataAsyncViewSession.CreateAsync(userData: 11, onError: 22, key: "audit-key"),
            TimeSpan.FromSeconds(5));

        AssertNotNull(session, "UserDataAsyncView CreateAsync produced a session");
        AssertTrue(session.GetViewController() != IntPtr.Zero, "UserDataAsyncView GetVC != 0");
        TestLogger.Info("UserDataAsyncView: async create with deduped trailing params succeeded");
    }

    // ────────────────────────────────────────────────────────────────
    // HandleParamView — init params colliding with generated locals
    // ────────────────────────────────────────────────────────────────

    public void TestHandleParamView_FieldsRoundTrip()
    {
        using var session = global::SwiftBindingsTestLib.HandleParamViewSession.Create(handle: 101, session: 202);

        // If the generated Create factory shadowed its own `handle`/`session` locals with
        // the init params, these stored values would be corrupted.
        AssertEqual(101, BridgeTestHelpers.HandleParamView_GetHandle(session.Handle), "handle field == 101");
        AssertEqual(202, BridgeTestHelpers.HandleParamView_GetSession(session.Handle), "session field == 202");
        TestLogger.Info("HandleParamView: colliding-named params round-tripped without shadowing");
    }
}

#endif
