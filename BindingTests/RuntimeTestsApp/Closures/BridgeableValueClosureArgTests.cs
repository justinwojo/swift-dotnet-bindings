// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Foundation;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Closures;

/// <summary>
/// ObjC-bridgeable Swift value types (<c>URLRequest</c>, <c>URL</c>, <c>IndexPath</c>,
/// <c>Locale</c>) crossing a C#-supplied closure. The closure's native slot holds the Swift VALUE;
/// the managed handler receives the Foundation class peer, so the binding has to bridge the value
/// before the handler runs (and bridge a returned peer back). Each test asserts the content the
/// handler read, and the escaping tests read the peer after the callback has returned, so a
/// borrowed peer that Swift released at the end of the call would show up as a crash or garbage.
/// </summary>
public class BridgeableValueClosureArgTests : TestBase
{
    public BridgeableValueClosureArgTests(TestResults results) : base(results) { }

    private const string RequestUrl = "https://example.invalid/bridge/request?q=1";
    private const string RequestMethod = "PATCH";
    private const double RequestTimeout = 42.5;
    private const string HeaderName = "X-Bridge-Probe";
    private const string HeaderValue = "probe-7";
    private const string UrlText = "https://example.invalid/bridge/url#frag";
    private const string LocaleIdentifier = "fr_CA";

    private void AssertProbeRequest(NSUrlRequest? request, string expectedUrl, string route)
    {
        AssertNotNull(request, $"{route}: the handler received a request");
        AssertEqual(expectedUrl, request!.Url?.AbsoluteString, $"{route}: request URL");
        AssertEqual(RequestMethod, request.HttpMethod, $"{route}: request method");
        AssertApproxEqual(RequestTimeout, request.TimeoutInterval, message: $"{route}: request timeout");
        AssertEqual(HeaderValue, request.Headers?.ObjectForKey(new NSString(HeaderName))?.ToString(),
            $"{route}: request header");
    }

    public void TestRequestArgumentCarriesItsContent()
    {
        var lab = new BridgeableValueClosureLab();
        NSUrlRequest? seen = null;
        lab.DeliverRequest(r => seen = r);
        // Read after the callback returned: the managed peer must own its own reference.
        AssertProbeRequest(seen, RequestUrl, "DeliverRequest");
    }

    public void TestUrlArgumentCarriesItsContent()
    {
        var lab = new BridgeableValueClosureLab();
        string? seen = null;
        lab.DeliverURL(u => seen = u.AbsoluteString);
        AssertEqual(UrlText, seen, "DeliverURL: the handler read the URL");
    }

    public void TestIndexPathArgumentCarriesItsContent()
    {
        var lab = new BridgeableValueClosureLab();
        var seen = new List<long>();
        lab.DeliverIndexPath(p =>
        {
            for (nuint i = 0; i < (nuint)p.Length; i++)
                seen.Add((long)p.IndexAtPosition((nint)i));
        });
        AssertEqual("3,1,4", string.Join(",", seen), "DeliverIndexPath: the handler read every index");
    }

    public void TestLocaleArgumentCarriesItsContent()
    {
        var lab = new BridgeableValueClosureLab();
        string? seen = null;
        lab.DeliverLocale(l => seen = l.LocaleIdentifier);
        AssertEqual(LocaleIdentifier, seen, "DeliverLocale: the handler read the identifier");
    }

    public void TestMixedArgumentsKeepTheirSlots()
    {
        var lab = new BridgeableValueClosureLab();
        int seenNumber = 0;
        NSUrlRequest? seenRequest = null;
        string? seenUrl = null;
        lab.DeliverMixed((n, r, u) =>
        {
            seenNumber = n;
            seenRequest = r;
            seenUrl = u.AbsoluteString;
        });
        AssertEqual(17, seenNumber, "DeliverMixed: the integer slot");
        AssertProbeRequest(seenRequest, RequestUrl + "&n=2", "DeliverMixed");
        AssertEqual(UrlText, seenUrl, "DeliverMixed: the URL slot");
    }

    public void TestOptionalRequestArgumentPresentAndAbsent()
    {
        var lab = new BridgeableValueClosureLab();
        NSUrlRequest? seen = null;
        bool called = false;
        lab.DeliverOptionalRequest(true, r => { called = true; seen = r; });
        AssertTrue(called, "DeliverOptionalRequest(present): the handler ran");
        AssertProbeRequest(seen, RequestUrl, "DeliverOptionalRequest(present)");

        called = false;
        seen = null;
        lab.DeliverOptionalRequest(false, r => { called = true; seen = r; });
        AssertTrue(called, "DeliverOptionalRequest(absent): the handler ran");
        AssertNull(seen, "DeliverOptionalRequest(absent): the handler received null");
    }

    public void TestOptionalUrlArgumentPresentAndAbsent()
    {
        var lab = new BridgeableValueClosureLab();
        string? seen = "unset";
        lab.DeliverOptionalURL(true, u => seen = u?.AbsoluteString);
        AssertEqual(UrlText, seen, "DeliverOptionalURL(present): the handler read the URL");
        lab.DeliverOptionalURL(false, u => seen = u?.AbsoluteString);
        AssertEqual(null, seen, "DeliverOptionalURL(absent): the handler received null");
    }

    public void TestThrowingClosureReceivesTheRequest()
    {
        var lab = new BridgeableValueClosureLab();
        NSUrlRequest? seen = null;
        Action<NSUrlRequest> observe = r => seen = r;
        AssertEqual("ok", lab.DeliverRequestThrowing(observe), "DeliverRequestThrowing: the handler returned");
        AssertProbeRequest(seen, RequestUrl + "&n=5", "DeliverRequestThrowing");

        Action<NSUrlRequest> reject = r =>
        {
            seen = r;
            throw new InvalidOperationException("bridge-boom");
        };
        seen = null;
        AssertEqual("threw", lab.DeliverRequestThrowing(reject), "DeliverRequestThrowing: the throw reached Swift");
        AssertProbeRequest(seen, RequestUrl + "&n=5", "DeliverRequestThrowing(throws)");
    }

    public void TestEscapingRequestHandlerFiresLater()
    {
        var lab = new BridgeableValueClosureLab();
        var seen = new List<NSUrlRequest>();
        lab.OnRequest(r => seen.Add(r));
        lab.OnRequest(r => seen.Add(r));
        AssertEqual(2, lab.FireRequests(9), "two stored handlers ran");
        AssertEqual(2, lab.FireRequests(10), "the stored handlers run again");
        AssertEqual(4, seen.Count, "every call reached a handler");
        AssertProbeRequest(seen[0], RequestUrl + "&n=9", "OnRequest first fire");
        AssertProbeRequest(seen[3], RequestUrl + "&n=10", "OnRequest second fire");
    }

    public void TestRequestReturnBridgesBack()
    {
        var lab = new BridgeableValueClosureLab();
        var result = lab.RewriteRequest(r =>
        {
            var copy = (NSMutableUrlRequest)r.MutableCopy();
            copy.HttpMethod = "PUT";
            copy.Url = new NSUrl(r.Url!.AbsoluteString + "&rewritten=1");
            return copy;
        });
        AssertEqual("PUT " + RequestUrl + "&rewritten=1", result, "RewriteRequest: Swift read the returned request");
    }

    public void TestUrlReturnBridgesBack()
    {
        var lab = new BridgeableValueClosureLab();
        AssertEqual(UrlText, lab.ProduceURL(() => new NSUrl(UrlText)), "ProduceURL: Swift read the returned URL");
    }

    public void TestOptionalUrlReturnBridgesBack()
    {
        var lab = new BridgeableValueClosureLab();
        AssertEqual(UrlText, lab.ProduceOptionalURL(() => new NSUrl(UrlText)), "ProduceOptionalURL(present)");
        AssertEqual("nil", lab.ProduceOptionalURL(() => null), "ProduceOptionalURL(absent)");
    }

    public void TestThrowingUrlReturnBridgesBack()
    {
        var lab = new BridgeableValueClosureLab();
        Func<NSUrl> produce = () => new NSUrl(UrlText);
        AssertEqual(UrlText, lab.ProduceURLThrowing(produce), "ProduceURLThrowing: Swift read the returned URL");
        Func<NSUrl> fail = () => throw new InvalidOperationException("bridge-boom");
        AssertEqual("threw", lab.ProduceURLThrowing(fail), "ProduceURLThrowing: the throw reached Swift");
    }

    public void TestThrowingOptionalUrlReturnBridgesBack()
    {
        var lab = new BridgeableValueClosureLab();
        Func<NSUrl?> produce = () => new NSUrl(UrlText);
        Func<NSUrl?> absent = () => null;
        Func<NSUrl?> fail = () => throw new InvalidOperationException("bridge-boom");
        AssertEqual(UrlText, lab.ProduceOptionalURLThrowing(produce), "ProduceOptionalURLThrowing(present)");
        AssertEqual("nil", lab.ProduceOptionalURLThrowing(absent), "ProduceOptionalURLThrowing(absent)");
        AssertEqual("threw", lab.ProduceOptionalURLThrowing(fail), "ProduceOptionalURLThrowing(throws)");
    }

    public void TestEscapingUrlProviderAnswersLater()
    {
        var lab = new BridgeableValueClosureLab();
        lab.SetURLProvider(() => new NSUrl(UrlText));
        AssertEqual(UrlText, lab.ReadProvidedURL(), "ReadProvidedURL: the stored provider's URL bridged back twice");
    }

    public void TestClosurePropertiesCarryTheValue()
    {
        var lab = new BridgeableValueClosureLab();
        var seen = new List<NSUrlRequest>();
        lab.RequestProperty = r => seen.Add(r);
        lab.FireRequestProperty(12);
        lab.FireRequestProperty(13);
        AssertEqual(2, seen.Count, "RequestProperty: the stored handler ran on every fire");
        AssertProbeRequest(seen[0], RequestUrl + "&n=12", "RequestProperty first fire");
        AssertProbeRequest(seen[1], RequestUrl + "&n=13", "RequestProperty second fire");

        lab.UrlProperty = () => new NSUrl(UrlText);
        AssertEqual(UrlText, lab.ReadURLProperty(), "UrlProperty: Swift read the returned URL");

        AssertFalse(lab.FireOptionalRequestProperty(14), "OptionalRequestProperty: unset reports false");
        NSUrlRequest? optionalSeen = null;
        lab.OptionalRequestProperty = r => optionalSeen = r;
        AssertTrue(lab.FireOptionalRequestProperty(15), "OptionalRequestProperty: set reports true");
        AssertProbeRequest(optionalSeen, RequestUrl + "&n=15", "OptionalRequestProperty");
    }

    public async Task TestAsyncMemberDeliversTheRequest()
    {
        var lab = new BridgeableValueClosureLab();
        NSUrlRequest? seen = null;
        AssertEqual("delivered", await lab.DeliverRequestAsync(r => seen = r), "DeliverRequestAsync completed");
        AssertProbeRequest(seen, RequestUrl + "&n=11", "DeliverRequestAsync");
    }
}
