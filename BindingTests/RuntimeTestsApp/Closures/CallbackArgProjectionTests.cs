// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Closures;

/// <summary>
/// Regression for the callback-arg projection asymmetry bug
/// (closure-arg-tuple elements used a stripped-down translator instead of the full-fat one).
///
/// The bug: closure-arg-tuple elements got translated through TupleHandler's
/// stripped-down translator instead of ClosureHandler's full-fat one, so types
/// that have non-trivial projections (Foundation.Data → byte[],
/// Foundation.URLResponse → Foundation.NSUrlResponse, Optional&lt;T&gt; → T?,
/// Swift.String → string) leaked through as their raw Swift runtime
/// representations only inside callback closures, while the equivalent
/// async-return-tuple emit projected them correctly.
///
/// These tests are mostly a <b>compile-time</b> assertion: each method below
/// captures the closure parameter into a strongly-typed C# delegate. Pre-fix,
/// the callback delegate was <c>Action&lt;Swift.Foundation.Data&gt;</c> /
/// <c>Action&lt;(Swift.Foundation.Data, Swift.SwiftOptional&lt;IntPtr&gt;)&gt;</c> /
/// <c>Action&lt;(Swift.SwiftString, Swift.SwiftOptional&lt;Swift.SwiftString&gt;, bool)&gt;</c>,
/// so even attempting to compile the assignments below would fail with CS1503.
/// Post-fix, the delegate types align with the projected types asserted at the
/// call sites.
/// </summary>
public class CallbackArgProjectionTests : TestBase
{
    public CallbackArgProjectionTests(TestResults results) : base(results) { }

    /// <summary>
    /// Foundation.Data → byte[] inside a single-arg closure. The captured payload
    /// must come through as a real C# byte[], not a Swift wrapper struct.
    /// </summary>
    public void TestCallbackArg_Data_ProjectsToByteArray()
    {
        var lab = new CallbackArgProjectionLab();
        byte[]? captured = null;
        // Compile-time pin: the parameter type IS byte[]. Pre-fix, this assignment
        // failed to compile because the delegate expected Swift.Foundation.Data.
        global::System.Action<byte[]> handler = bytes => captured = bytes;
        lab.LoadBytes(handler);
        AssertTrue(captured is { Length: 3 }, "Expected 3-byte payload from loadBytes");
        AssertEqual((byte)0x42, captured![0], "byte[0]");
        AssertEqual((byte)0x43, captured![1], "byte[1]");
        AssertEqual((byte)0x44, captured![2], "byte[2]");
    }

    /// <summary>
    /// (Foundation.Data, Foundation.URLResponse?) tuple-arg closure — mixed-projection tuple shape.
    /// Element #1 must project to byte[]; element #2 must project to
    /// Foundation.NSUrlResponse? (NSObject lookup + Optional → T?).
    /// </summary>
    public void TestCallbackArg_DataResponseTuple_ProjectsAsyncEquivalent()
    {
        var lab = new CallbackArgProjectionLab();
        byte[]? capturedData = null;
        Foundation.NSUrlResponse? capturedResponse = null;
        // Compile-time pin: this is the post-fix shape, identical to what an async
        // overload returning (Foundation.Data, Foundation.URLResponse?) would yield.
        global::System.Action<byte[], Foundation.NSUrlResponse?> handler = (d, r) =>
        {
            capturedData = d;
            capturedResponse = r;
        };
        lab.LoadResponse(handler);
        AssertTrue(capturedData is { Length: 3 }, "Expected 3-byte data payload");
        AssertEqual((byte)0x10, capturedData![0], "data[0]");
        AssertTrue(capturedResponse is not null, "Expected non-null URLResponse");
    }

    /// <summary>
    /// (String, String?, Bool) tuple-arg closure — exercises Swift.String → string
    /// and Optional&lt;String&gt; → string? inside the same callback tuple.
    /// </summary>
    public void TestCallbackArg_StringOptionalBoolTuple_ProjectsPrimitives()
    {
        var lab = new CallbackArgProjectionLab();
        string? capturedKind = null;
        string? capturedLabel = null;
        bool? capturedFlag = null;
        // Compile-time pin: string + string? + bool, not SwiftString + SwiftOptional<SwiftString> + bool.
        global::System.Action<string, string?, bool> handler = (k, l, b) =>
        {
            capturedKind = k;
            capturedLabel = l;
            capturedFlag = b;
        };
        lab.LoadDescriptor(handler);
        AssertEqual("kind", capturedKind, "kind");
        AssertEqual("label-A", capturedLabel, "label");
        AssertTrue(capturedFlag == true, "flag");
    }

    // ── NON-optional ObjC-backed reference in a callback-arg slot ──────────────────────────
    //
    // Swift fills a reference-typed closure-arg slot with ONE borrowed object pointer
    // (`Unmanaged.passUnretained(x[ as AnyObject]).toOpaque()`), whether or not the parameter is
    // Optional. The Optional lane above already bridged that pointer with GetNSObject; the
    // NON-optional lane fell through to `SwiftMarshal.MarshalCallbackArg<Foundation.NSUrlResponse>`,
    // and since a Microsoft.iOS peer has no Swift type-metadata record and is not ISwiftObject, that
    // landed in MarshalFromSwift's NSObject arm — `Marshal.ReadIntPtr(ptr)`, i.e. one dereference too
    // many, wrapping the object's ISA WORD as if it were an object.
    //
    // These assertions have to READ THROUGH the delivered peer, not just null-check it: the isa-word
    // read produces a non-null managed object too. Asserting the status code and URL of a known
    // HTTPURLResponse is what separates "the object arrived" from "something arrived".

    /// <summary>
    /// (Foundation.Data, Foundation.URLResponse) — NON-optional bridged Foundation class on a CLASS
    /// parent. The concrete instance is an HTTPURLResponse, so a correctly-read pointer both types as
    /// NSHttpUrlResponse and yields the known status code / URL.
    /// </summary>
    public void TestCallbackArg_NonOptionalUrlResponse_ClassParent_ReadsObjectNotIsa()
    {
        var lab = new CallbackArgProjectionLab();
        byte[]? capturedData = null;
        Foundation.NSUrlResponse? capturedResponse = null;
        global::System.Action<byte[], Foundation.NSUrlResponse> handler = (d, r) =>
        {
            capturedData = d;
            capturedResponse = r;
        };
        lab.LoadDirectResponse(handler);

        AssertTrue(capturedData is { Length: 2 }, "Expected 2-byte data payload");
        AssertEqual((byte)0x71, capturedData![0], "data[0]");
        AssertTrue(capturedResponse is not null, "Expected non-null URLResponse");
        AssertResponseIdentity(capturedResponse!, "class-parent");
    }

    /// <summary>
    /// Same NON-optional bridged-class slot on a STRUCT parent — the callback trampoline is emitted
    /// per member, so a struct parent is a separate emission path from a class parent.
    /// </summary>
    public void TestCallbackArg_NonOptionalUrlResponse_StructParent_ReadsObjectNotIsa()
    {
        var lab = new CallbackArgProjectionStructLab();
        byte[]? capturedData = null;
        Foundation.NSUrlResponse? capturedResponse = null;
        global::System.Action<byte[], Foundation.NSUrlResponse> handler = (d, r) =>
        {
            capturedData = d;
            capturedResponse = r;
        };
        lab.LoadDirectResponse(handler);

        AssertTrue(capturedData is { Length: 1 }, "Expected 1-byte data payload");
        AssertEqual((byte)0x81, capturedData![0], "data[0]");
        AssertTrue(capturedResponse is not null, "Expected non-null URLResponse");
        AssertResponseIdentity(capturedResponse!, "struct-parent");
    }

    /// <summary>
    /// NON-optional <c>NSURL</c> — a second bridged Foundation peer, and an ObjC class rather than
    /// the <c>URL</c> value type, so it exercises the same slot shape through a different type record.
    /// </summary>
    public void TestCallbackArg_NonOptionalNSUrl_ReadsObjectNotIsa()
    {
        var lab = new CallbackArgProjectionLab();
        Foundation.NSUrl? captured = null;
        global::System.Action<Foundation.NSUrl> handler = u => captured = u;
        lab.LoadDirectUrl(handler);

        AssertTrue(captured is not null, "Expected non-null NSUrl");
        AssertEqual<string?>(CallbackArgProjectionProbe.UrlText, captured!.AbsoluteString, "NSUrl.AbsoluteString");
    }

    /// <summary>
    /// NON-optional generator-bound <c>@objc … : NSObject</c> class — the ObjC-ROOTED neighbour.
    /// It carries Swift class metadata, so it keeps the isa-aware marshal rather than the ObjC bridge;
    /// this is the positive control that the shared classifier still routes each reference flavour to
    /// its own adapter instead of collapsing them onto the bridge.
    /// </summary>
    public void TestCallbackArg_NonOptionalObjCRootedClass_KeepsSwiftMetadataMarshal()
    {
        var lab = new CallbackArgProjectionLab();
        DirectCallbackMarker? captured = null;
        global::System.Action<DirectCallbackMarker> handler = m => captured = m;
        lab.LoadDirectMarker(handler);

        AssertTrue(captured is not null, "Expected non-null DirectCallbackMarker");
        AssertEqual(CallbackArgProjectionProbe.MarkerTag, captured!.Tag, "DirectCallbackMarker.Tag");
    }

    /// <summary>
    /// Same ObjC-rooted positive control on a STRUCT parent.
    /// </summary>
    public void TestCallbackArg_NonOptionalObjCRootedClass_StructParent()
    {
        var lab = new CallbackArgProjectionStructLab();
        DirectCallbackMarker? captured = null;
        global::System.Action<DirectCallbackMarker> handler = m => captured = m;
        lab.LoadDirectMarker(handler);

        AssertTrue(captured is not null, "Expected non-null DirectCallbackMarker");
        AssertEqual(CallbackArgProjectionProbe.MarkerTag, captured!.Tag, "DirectCallbackMarker.Tag");
    }

    /// <summary>
    /// NON-optional pure-Swift class — the third reference flavour, which stays on the owning
    /// borrowed-class marshal.
    /// </summary>
    public void TestCallbackArg_NonOptionalPureSwiftClass_KeepsBorrowedClassMarshal()
    {
        var lab = new CallbackArgProjectionLab();
        DirectCallbackToken? captured = null;
        global::System.Action<DirectCallbackToken> handler = t => captured = t;
        lab.LoadDirectToken(handler);

        AssertTrue(captured is not null, "Expected non-null DirectCallbackToken");
        AssertEqual(CallbackArgProjectionProbe.TokenLabel, captured!.Label, "DirectCallbackToken.Label");
    }

    /// <summary>
    /// The bridged peer must still be readable after the Swift closure has returned and a GC has run.
    /// The Swift side hands the pointer over at +0 (passUnretained), so the managed peer created by
    /// GetNSObject is what keeps the object alive; if the bridge were skipped, or the peer created
    /// without a retain, the object is free to die when the Swift callback frame unwinds.
    /// </summary>
    public void TestCallbackArg_NonOptionalUrlResponse_SurvivesInducedGC()
    {
        var lab = new CallbackArgProjectionLab();
        Foundation.NSUrlResponse? capturedResponse = null;
        Foundation.NSUrl? capturedUrl = null;
        lab.LoadDirectResponse((_, r) => capturedResponse = r);
        lab.LoadDirectUrl(u => capturedUrl = u);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        AssertTrue(capturedResponse is not null, "Expected the URLResponse peer to survive the callback");
        AssertResponseIdentity(capturedResponse!, "post-GC");
        AssertTrue(capturedUrl is not null, "Expected the NSUrl peer to survive the callback");
        AssertEqual<string?>(CallbackArgProjectionProbe.UrlText, capturedUrl!.AbsoluteString, "NSUrl.AbsoluteString post-GC");
    }

    /// <summary>
    /// Reads through the delivered peer: its dynamic type must be the ObjC class Swift constructed
    /// (<c>NSHTTPURLResponse</c>), and the status code / URL must be the known probe values. An isa-word
    /// read yields a non-null object whose class is whatever the isa pointer happens to name, so the
    /// type check alone is the tightest single assertion; the value reads pin that it is the RIGHT
    /// instance rather than merely an instance.
    /// </summary>
    private void AssertResponseIdentity(Foundation.NSUrlResponse response, string label)
    {
        AssertTrue(response is Foundation.NSHttpUrlResponse,
            $"[{label}] Expected the delivered peer to be an NSHttpUrlResponse, got {response.GetType().Name} " +
            "— a wrong class here means the callback slot was read as the address of an object pointer " +
            "rather than as the object pointer itself");
        var http = (Foundation.NSHttpUrlResponse)response;
        AssertEqual(CallbackArgProjectionProbe.ResponseStatus, (int)http.StatusCode, $"[{label}] StatusCode");
        AssertEqual<string?>(CallbackArgProjectionProbe.ResponseUrl, http.Url?.AbsoluteString, $"[{label}] Url.AbsoluteString");
    }
}
