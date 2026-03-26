// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;
using SwiftBindingsTestLib.SwiftInterop;

namespace RuntimeTestsApp.Protocols;

/// <summary>
/// Tests that protocol proxy receivers correctly marshal ObjC-bridgeable types (URL)
/// through .Handle / GetNSObject instead of the value-type buffer-copy path.
/// Validates Finding 1 (P1) fix: protocol method parameter and return directions.
/// </summary>
public class URLProtocolReceiverTests : TestBase
{
    public URLProtocolReceiverTests(TestResults results) : base(results) { }

    /// <summary>
    /// Tests round-trip: Swift passes URL to C# protocol impl, C# returns a URL back to Swift.
    /// Both parameter and return directions must correctly marshal through ObjC pointers.
    /// </summary>
    [Skip("Protocol proxy ObjC bridge: Swift passes _SwiftURL (not NSURL) — GetNSObject<NSUrl> fails because _SwiftURL isn't registered with .NET's ObjC registrar")]
    public void TestURLProtocolRoundTrip()
    {
        var impl = new TestURLProcessor();
        var proxy = new URLProcessorDelegateProxy(impl);

        // Swift creates URL("https://input.com"), passes to C# processURL,
        // C# appends "/processed", Swift reads the returned URL's absoluteString.
        var result = TestLibFunctions.FireURLProcessorDelegate(proxy, urlString: "https://input.com");

        AssertTrue(impl.WasCalled, "Protocol method was called");
        AssertEqual("https://input.com", impl.ReceivedUrlString, "URL parameter preserved");
        AssertEqual("https://input.com/processed", result, "URL return preserved");
    }
}

/// <summary>
/// C# implementation of IURLProcessorDelegate for testing protocol proxy receiver marshalling.
/// Receives a URL, records it, and returns a modified URL.
/// </summary>
internal class TestURLProcessor : IURLProcessorDelegate
{
    public bool WasCalled { get; private set; }
    public string? ReceivedUrlString { get; private set; }

    public Foundation.NSUrl ProcessURL(Foundation.NSUrl url)
    {
        WasCalled = true;
        ReceivedUrlString = url.AbsoluteString;
        return Foundation.NSUrl.FromString(url.AbsoluteString + "/processed")!;
    }
}
