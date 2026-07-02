// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

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
}
