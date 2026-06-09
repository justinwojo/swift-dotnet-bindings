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
    /// (Foundation.Data, Foundation.URLResponse?) tuple-arg closure — the Nuke shape.
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
}
