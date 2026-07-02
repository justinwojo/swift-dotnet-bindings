// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Marshalling;

/// <summary>
/// Tests the ObjC whole-container bridge on the protocol-witness (existential) return path.
/// A Swift conformer is obtained as <c>any URLContainerProvider</c>, so its requirements dispatch
/// through the generated <c>URLContainerProviderProxy</c>. Each requirement returns a container of an
/// ObjC-bridgeable element (URL), which crosses the boundary as a whole NS* collection (NSSet /
/// NSArray / NSDictionary) at +1 — the same "design b" the concrete class path uses. This pins the
/// fix for the previously-uncompilable witness getter/method body (empty first argument to
/// GetINativeObject) that surfaced once an NS_TYPED_ENUM element synthesized an ObjCBridgeable record.
/// </summary>
public class URLContainerWitnessTests : TestBase
{
    public URLContainerWitnessTests(TestResults results) : base(results) { }

    public void TestWitnessBridgedSetPropertyGetter()
    {
        var provider = TestLibFunctions.MakeURLContainerProvider();
        var set = provider.ProvidedURLSet;
        AssertNotNull(set, "ProvidedURLSet returns non-null through the witness proxy");
        AssertEqual(2, set!.Count, "Bridged Set<URL> getter returns 2 elements through the witness proxy");
        var absolute = new System.Collections.Generic.HashSet<string>();
        foreach (var url in set)
            absolute.Add(url!.AbsoluteString!);
        AssertTrue(absolute.Contains("https://set-a.example.com"), "Set element set-a preserved");
        AssertTrue(absolute.Contains("https://set-b.example.com"), "Set element set-b preserved");
    }

    public void TestWitnessBridgedArrayMethodReturn()
    {
        var provider = TestLibFunctions.MakeURLContainerProvider();
        var urls = provider.ProvideURLArray();
        AssertNotNull(urls, "ProvideURLArray returns non-null through the witness proxy");
        AssertEqual(2, urls!.Count, "Bridged [URL] method returns 2 elements through the witness proxy");
        AssertEqual("https://array-0.example.com", urls[0]!.AbsoluteString, "Array element 0 preserved");
        AssertEqual("https://array-1.example.com", urls[1]!.AbsoluteString, "Array element 1 preserved");
    }

    public void TestWitnessBridgedDictionaryMethodReturn()
    {
        var provider = TestLibFunctions.MakeURLContainerProvider();
        var dict = provider.ProvideURLDictionary();
        AssertNotNull(dict, "ProvideURLDictionary returns non-null through the witness proxy");
        AssertEqual(2, dict!.Count, "Bridged [String: URL] method returns 2 entries through the witness proxy");
        AssertTrue(dict.ContainsKey("home"), "Dictionary contains 'home' key");
        AssertTrue(dict.ContainsKey("api"), "Dictionary contains 'api' key");
        AssertEqual("https://dict-home.example.com", dict["home"]!.AbsoluteString, "'home' URL preserved");
        AssertEqual("https://dict-api.example.com", dict["api"]!.AbsoluteString, "'api' URL preserved");
    }

    /// <summary>
    /// REVERSE direction: a C#-implemented conformer is passed to a Swift free function that invokes
    /// each requirement, so the three ObjC-bridgeable whole-containers cross C# → Swift through the
    /// EveryProtocol vtable. Each is built in C# as a fresh NS* collection handed back at +1
    /// (<c>Arc.UnknownObjectRetain</c>) and consumed Swift-side with <c>takeRetainedValue</c>. A
    /// correct round-trip (Swift reads every element the C# side produced) proves the reverse ABI and
    /// ARC ownership contract — the counterpart to the forward witness tests above.
    /// </summary>
    public void TestReverseDispatchCSharpConformerRoundTrips()
    {
        var summary = TestLibFunctions.SummarizeURLContainerProvider(new CSharpURLContainerProvider());
        AssertEqual(
            "set=[https://cs-set-a.example.com,https://cs-set-b.example.com]|" +
            "array=[https://cs-array-0.example.com,https://cs-array-1.example.com]|" +
            "dict=[api=https://cs-dict-api.example.com,home=https://cs-dict-home.example.com]",
            summary,
            "Swift reads all three C#-built ObjC-bridgeable containers through the reverse EveryProtocol vtable");
    }

    /// C# implementation of the protocol whose requirements return ObjC-bridgeable whole-containers.
    /// Passing an instance into Swift exercises the reverse-dispatch receiver getters/method returns.
    private sealed class CSharpURLContainerProvider : IURLContainerProvider
    {
        public IReadOnlySet<Foundation.NSUrl> ProvidedURLSet => new HashSet<Foundation.NSUrl>
        {
            new Foundation.NSUrl("https://cs-set-a.example.com"),
            new Foundation.NSUrl("https://cs-set-b.example.com"),
        };

        public IReadOnlyList<Foundation.NSUrl> ProvideURLArray() => new List<Foundation.NSUrl>
        {
            new Foundation.NSUrl("https://cs-array-0.example.com"),
            new Foundation.NSUrl("https://cs-array-1.example.com"),
        };

        public IReadOnlyDictionary<string, Foundation.NSUrl> ProvideURLDictionary() => new Dictionary<string, Foundation.NSUrl>
        {
            ["home"] = new Foundation.NSUrl("https://cs-dict-home.example.com"),
            ["api"] = new Foundation.NSUrl("https://cs-dict-api.example.com"),
        };
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // SCALAR sibling of the container tests above: a protocol whose requirements return a single
    // ObjC-bridgeable value (URL), not a container of them. Exercises the SCALAR reverse-dispatch
    // ownership contract — the C# receiver hands the ObjC pointer back at +1
    // (Arc.UnknownObjectRetain(url.Handle)) and Swift consumes the transferred retain
    // (takeRetainedValue for the non-optional arm, move() for the Optional arm). Before the fix the
    // scalar handoff was +0, so a freshly allocated wrapper the C# getter returns could be freed in the
    // handoff window → use-after-free.

    // No FORWARD scalar test: unlike a whole-container return (which crosses as a dispatchable NS*
    // collection pointer), a scalar ObjC-bridgeable return is gated "not dispatchable" on the forward
    // existential path — the generated URLScalarProviderProxy getters/methods for the Swift-backed arm
    // are [Obsolete(SB0003)] and throw NotSupportedException by design. There is nothing to round-trip
    // forward, so the scalar coverage is reverse-only (the direction this fix touches).

    /// <summary>
    /// REVERSE direction: a C#-implemented conformer that returns a FRESH ObjC wrapper on every call is
    /// passed to a Swift free function that invokes each scalar requirement (twice each) and reads its
    /// <c>absoluteString</c> immediately. Each return crosses C# → Swift through the EveryProtocol
    /// vtable at +1 (Arc.UnknownObjectRetain), so the fresh wrapper survives the handoff even though the
    /// C# side holds no other reference to it. A correct summary proves the reverse ABI + scalar +1 ARC
    /// contract for the property getter and method return in both their non-optional and Optional forms,
    /// and for the Optional arm covers BOTH the `.some` case (a +1-retained pointer) and the `.none` case
    /// (IntPtr.Zero → Swift maps the optional pointer to nil), on both the property-getter and
    /// method-return emission sites.
    /// </summary>
    public void TestReverseScalarDispatchCSharpConformerRoundTrips()
    {
        var summary = TestLibFunctions.SummarizeURLScalarProvider(new CSharpURLScalarProvider());
        AssertEqual(ExpectedScalarSummary, summary,
            "Swift reads all C#-built scalar ObjC-bridgeable URLs (some + none) through the reverse EveryProtocol vtable");
    }

    /// <summary>
    /// The +0 handoff's failure mode is a use-after-free: the C# getter returns a freshly allocated
    /// wrapper with no other root, and a GC in the C# → Swift handoff window could free it before Swift
    /// reads it. Driving the reverse scalar dispatch repeatedly with interleaved GC pressure (each call
    /// allocates four fresh wrappers, each iteration drains finalizers) makes that window recur many
    /// times; a stable, correct summary on every iteration proves the +1 transfer holds each fresh
    /// object alive across the boundary.
    /// </summary>
    public void TestReverseScalarDispatchSurvivesGCPressure()
    {
        const int iterations = 250;
        for (int i = 0; i < iterations; i++)
        {
            var summary = TestLibFunctions.SummarizeURLScalarProvider(new CSharpURLScalarProvider());
            if (summary != ExpectedScalarSummary)
                throw new System.Exception(
                    $"reverse scalar dispatch corrupted at iteration {i}: expected '{ExpectedScalarSummary}', got '{summary}'");
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        AssertTrue(true,
            $"reverse scalar ObjC dispatch round-tripped correctly across {iterations} GC-pressured iterations (no use-after-free)");
    }

    private const string ExpectedScalarSummary =
        "prop=https://cs-scalar-prop.example.com|" +
        "method=https://cs-scalar-method.example.com|" +
        "maybeProp=https://cs-scalar-maybe-prop.example.com|" +
        "maybeMethod=https://cs-scalar-maybe-method.example.com|" +
        "maybeNilProp=nil|" +
        "maybeNilMethod=nil";

    /// C# implementation of the scalar protocol. The non-nil requirements return a NEWLY allocated
    /// <c>Foundation.NSUrl</c> — no field caches it — so the reverse receiver's +1 transfer retain is
    /// the only thing keeping the object alive once the receiver frame returns to Swift. The
    /// <c>MaybeNil*</c> requirements return <c>null</c> so the reverse receiver deposits
    /// <c>IntPtr.Zero</c> and Swift's optional-pointer read maps it to nil — the `.none` arm.
    private sealed class CSharpURLScalarProvider : IURLScalarProvider
    {
        public Foundation.NSUrl ProvidedURL => new Foundation.NSUrl("https://cs-scalar-prop.example.com");
        public Foundation.NSUrl ProvideURL() => new Foundation.NSUrl("https://cs-scalar-method.example.com");
        public Foundation.NSUrl? MaybeURL => new Foundation.NSUrl("https://cs-scalar-maybe-prop.example.com");
        public Foundation.NSUrl? ProvideMaybeURL() => new Foundation.NSUrl("https://cs-scalar-maybe-method.example.com");
        public Foundation.NSUrl? MaybeNilURL => null;
        public Foundation.NSUrl? ProvideMaybeNilURL() => null;
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // READ-IN (param-in) mirror of the reverse-RETURN scalar tests above: a protocol whose
    // requirements RECEIVE a scalar Optional ObjC-bridgeable VALUE (URL?) INTO the C# conformer — a
    // settable property (reverse SETTER receiver) and a method param (reverse METHOD-PARAM receiver).
    // Optional<URL> is a 16-byte resilient value, so before the fix Swift passed &copy of that
    // multi-word value while the C# receiver read a one-word SwiftOptional<IntPtr> — a layout mismatch
    // that reinterprets URL's storage bytes as an ObjC pointer → corruption/crash on the `.some` case.
    // The fix bridges to a one-word optional ObjC pointer on both sides (nil ↔ IntPtr.Zero), a +0
    // borrow that mirrors the reverse-RETURN +1 transfer. The C# conformer echoes each received value
    // back through String observation getters (the already-robust read direction), so a mis-read
    // setter/param corrupts an observable round-trip rather than silently passing.

    /// <summary>
    /// REVERSE direction: a C#-implemented conformer RECEIVES an Optional ObjC-bridgeable VALUE (URL?)
    /// through a settable property and a method param. Swift writes BOTH a `.some(URL)` and a `.none`
    /// through each, reading the String observation channel after every write. A correct summary proves
    /// the read-in Optional bridgeable VALUE crosses Swift → C# with the right one-word optional-pointer
    /// layout for both the `.some` and `.none` cases, at both the property-setter and method-param
    /// emission sites.
    /// </summary>
    public void TestReverseOptionalSinkDispatchCSharpConformerRoundTrips()
    {
        var summary = TestLibFunctions.ExerciseURLOptionalSink(new CSharpURLOptionalSink());
        AssertEqual(ExpectedSinkSummary, summary,
            "Swift writes both .some(URL?) and .none into the C# conformer's setter and method param through the reverse EveryProtocol vtable");
    }

    /// <summary>
    /// The layout-mismatch failure mode is a corrupt read: reinterpreting the multi-word Optional&lt;URL&gt;
    /// as a one-word optional pointer reads unrelated storage bytes as an ObjC pointer. Driving the
    /// reverse read-in dispatch repeatedly with interleaved GC pressure (each iteration allocates fresh
    /// URLs and drains finalizers) makes any dangling/garbage-pointer window recur many times; a stable,
    /// correct summary on every iteration proves the one-word optional-pointer borrow round-trips.
    /// </summary>
    public void TestReverseOptionalSinkDispatchSurvivesGCPressure()
    {
        const int iterations = 250;
        for (int i = 0; i < iterations; i++)
        {
            var summary = TestLibFunctions.ExerciseURLOptionalSink(new CSharpURLOptionalSink());
            if (summary != ExpectedSinkSummary)
                throw new System.Exception(
                    $"reverse read-in Optional dispatch corrupted at iteration {i}: expected '{ExpectedSinkSummary}', got '{summary}'");
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        AssertTrue(true,
            $"reverse read-in Optional ObjC dispatch round-tripped correctly across {iterations} GC-pressured iterations (no layout-mismatch corruption)");
    }

    private const string ExpectedSinkSummary =
        "sinkSome=https://cs-sink-some.example.com|" +
        "sinkNone=nil|" +
        "acceptSome=https://cs-accept-some.example.com|" +
        "acceptNone=nil";

    /// C# implementation of the read-in protocol. The settable property and the method param each store
    /// the last received value; the String observation getters echo its <c>AbsoluteString</c> (or "nil"),
    /// so a mis-read of the Optional bridgeable VALUE surfaces as a wrong observed string.
    private sealed class CSharpURLOptionalSink : IURLOptionalSink
    {
        private Foundation.NSUrl? _sink;
        private Foundation.NSUrl? _accepted;
        public Foundation.NSUrl? SinkURL { get => _sink; set => _sink = value; }
        public void AcceptURL(Foundation.NSUrl? url) => _accepted = url;
        public string SinkDescription => _sink?.AbsoluteString ?? "nil";
        public string AcceptedDescription => _accepted?.AbsoluteString ?? "nil";
    }
}
