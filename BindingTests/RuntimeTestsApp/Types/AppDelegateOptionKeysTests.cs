// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#if UIKIT_OPTION_KEYS

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Types;

/// <summary>
/// Runtime coverage for members whose dictionary key is a UIKit NS_TYPED_ENUM string key
/// (<c>UIApplication.LaunchOptionsKey</c> / <c>UIApplication.OpenURLOptionsKey</c>).
/// </summary>
/// <remarks>
/// Compiling is only half the claim. The key type is described as bridging to
/// <c>NSString</c>, which asserts an ABI: each key crosses the boundary as the pointer to a
/// bridged <c>NSString</c> instance while the values cross as boxed existentials. A record
/// that named the wrong managed type — or a projection that passed the key through as an
/// opaque word — would still emit a declaration and still compile; only calling through it
/// can show Swift receiving a dictionary it can actually count and read back. The parameter
/// direction is covered on both key types and on both a method and a free function; the
/// return direction is covered by a member that hands a populated dictionary back.
/// </remarks>
public class AppDelegateOptionKeysTests : TestBase
{
    public AppDelegateOptionKeysTests(TestResults results) : base(results) { }

    // The real UIKit key constants. Their identity is the NSString contents, so spelling them
    // out here keeps the test independent of whether the framework binding exposes them.
    const string SourceApplicationKey = "UIApplicationLaunchOptionsSourceApplicationKey";
    const string UrlKey = "UIApplicationLaunchOptionsURLKey";
    const string OpenInPlaceKey = "UIApplicationOpenURLOptionsOpenInPlaceKey";
    const string AnnotationKey = "UIApplicationOpenURLOptionsAnnotationKey";

    public void TestSiblingMemberOnTheSameTypeStillBinds()
    {
        // The positive control: if the key type ever stops resolving, the members below
        // disappear from the binding entirely and this is the only member left standing.
        using var relay = new AppDelegateOptionRelay(7);
        AssertEqual(7, relay.ForwardedCount, "the Int32 sibling round-trips through the constructor");
    }

    public void TestLaunchOptionsKeyedDictionaryReachesSwift()
    {
        using var relay = new AppDelegateOptionRelay(0);

        var options = new Dictionary<Foundation.NSString, object>
        {
            { new Foundation.NSString(SourceApplicationKey), "com.example.caller" },
            { new Foundation.NSString(UrlKey), "https://example.com" },
        };

        AssertEqual(2, relay.CountLaunchOptions(options), "Swift counts both launch-options entries");
        AssertEqual(
            0,
            relay.CountLaunchOptions(new Dictionary<Foundation.NSString, object>()),
            "an empty launch-options dictionary crosses as empty rather than as garbage");
    }

    /// <summary>
    /// A count cannot tell whether Swift can use the dictionary: Swift hashes each key by its
    /// string, so only a Swift lookup with a key of Swift's own making shows that the dictionary
    /// C# built stored every key where Swift looks for it.
    /// </summary>
    public void TestSwiftLooksUpKeysOfADictionaryBuiltInCSharp()
    {
        var options = new Dictionary<Foundation.NSString, object>
        {
            { new Foundation.NSString(SourceApplicationKey), "com.example.caller" },
            { new Foundation.NSString(UrlKey), "https://example.com" },
        };

        AssertEqual("com.example.caller", TestLibFunctions.LookUpLaunchOption(options, SourceApplicationKey),
            "Swift finds the source-application key C# stored");
        AssertEqual("https://example.com", TestLibFunctions.LookUpLaunchOption(options, UrlKey),
            "Swift finds the URL key C# stored");
        AssertNull(TestLibFunctions.LookUpLaunchOption(options, "UIApplicationLaunchOptionsRemoteNotificationKey"),
            "Swift misses a key C# never stored");
    }

    public void TestOpenUrlOptionsKeyedDictionaryReachesSwift()
    {
        using var relay = new AppDelegateOptionRelay(0);

        var options = new Dictionary<Foundation.NSString, object>
        {
            { new Foundation.NSString(OpenInPlaceKey), true },
            { new Foundation.NSString(AnnotationKey), "annotation" },
            { new Foundation.NSString(SourceApplicationKey), 3L },
        };

        AssertEqual(3, relay.CountOpenURLOptions(options), "the second key type carries a mixed-value dictionary");
    }

    public void TestFreeFunctionKeyedByOptionKeyBinds()
    {
        // Same shape outside a nominal type — a free function has no `self` word, so it is a
        // separate P/Invoke shape rather than a second instance of the method one.
        AssertEqual(
            "empty",
            TestLibFunctions.DescribeOpenURLOptions(new Dictionary<Foundation.NSString, object>()),
            "free function sees an empty dictionary");
        AssertEqual(
            "2 options",
            TestLibFunctions.DescribeOpenURLOptions(new Dictionary<Foundation.NSString, object>
            {
                { new Foundation.NSString(OpenInPlaceKey), false },
                { new Foundation.NSString(AnnotationKey), "note" },
            }),
            "free function sees both entries");
    }

    public void TestOptionKeyedDictionaryComesBackFromSwift()
    {
        using var relay = new AppDelegateOptionRelay(0);

        var produced = relay.MakeLaunchOptions("com.example.origin");
        AssertEqual(1, produced.Count, "Swift hands back the single entry it built");

        // Read the pair out by enumeration rather than by indexing: the assertion under test is
        // that the key arrived as a usable NSString, not that NSString hashes a particular way.
        string? key = null;
        object? value = null;
        foreach (var pair in produced)
        {
            key = pair.Key?.ToString();
            value = pair.Value;
        }

        AssertEqual(SourceApplicationKey, key, "the returned key is the bridged NSString constant, readable as text");
        AssertEqual("com.example.origin", value as string, "the boxed Any value round-trips as the string Swift stored");
    }

    public void TestReturnedOptionsFeedBackIntoSwift()
    {
        using var relay = new AppDelegateOptionRelay(0);

        // A returned dictionary is IReadOnlyDictionary while the parameter slot is IDictionary,
        // so a consumer copies it — the same asymmetry pinned for [String: Any].
        var round = new Dictionary<Foundation.NSString, object>(relay.MakeLaunchOptions("com.example.origin"));

        AssertEqual(1, relay.CountLaunchOptions(round), "a dictionary produced by Swift is accepted back by Swift");
    }

    /// <summary>
    /// Reading a dictionary Swift returns leaves no reference on its keys. Each key crosses as the
    /// pointer to its NSString; the dictionary's iterator hands every key over, and if a read kept
    /// a reference nothing released, the probe key's retain count would climb by one per read.
    /// </summary>
    public void TestReadingReturnedOptionsLeavesNoKeyReference()
    {
        AssertTrue(TestLibFunctions.CheckLaunchOptionsProbeKeyIsStored(), "the dictionary's key is the probe key itself");
        nint baseline = TestLibFunctions.GetLaunchOptionsProbeKeyRetainCount();
        TestLibFunctions.RetainLaunchOptionsProbeKey();
        nint retained = TestLibFunctions.GetLaunchOptionsProbeKeyRetainCount();
        TestLibFunctions.ReleaseLaunchOptionsProbeKey();
        AssertEqual(baseline + 1, retained, "one extra reference moves the probe key's retain count (control)");

        nint before = TestLibFunctions.GetLaunchOptionsProbeKeyRetainCount();
        ReadProbeKeyedOptions(25);
        for (int i = 0; i < 4; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        nint after = TestLibFunctions.GetLaunchOptionsProbeKeyRetainCount();
        AssertEqual(before, after, "reading the returned dictionary 25 times leaves the key's retain count where it was");
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private void ReadProbeKeyedOptions(int iterations)
    {
        for (int i = 0; i < iterations; i++)
        {
            var produced = TestLibFunctions.MakeLaunchOptionsWithProbeKey();
            try
            {
                foreach (var pair in produced)
                {
                    AssertEqual("UIApplicationLaunchOptionsOwnershipProbeKey", pair.Key.ToString(), "the probe key reads back");
                    pair.Key.Dispose();
                }
                foreach (var key in produced.Keys)
                    key.Dispose();
            }
            finally
            {
                (produced as IDisposable)?.Dispose();
            }
        }
    }
}

#endif
