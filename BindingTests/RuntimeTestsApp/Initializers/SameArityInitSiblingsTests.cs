// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Initializers;

/// <summary>
/// Runtime gate for a trimmed default-parameter constructor beside a sibling initializer
/// of the same arity but different parameter types (<c>SameArityInitSiblings</c>).
///
/// <c>init(url:label:config:)</c> trims to <c>(url, label)</c>, which has the arity of the
/// sibling <c>init(data:config:)</c>. No call binds both (<c>NSUrl</c>/<c>string?</c> against
/// <c>byte[]</c>/<c>SameArityInitConfig</c>), so the trimmed constructor must be emitted; it
/// once was suppressed on arity alone, and this file then failed to compile. Each call must
/// reach its own initializer and let Swift fill the defaults it leaves out.
/// </summary>
public class SameArityInitSiblingsTests : TestBase
{
    public SameArityInitSiblingsTests(TestResults results) : base(results) { }

    static global::Foundation.NSUrl ReportUrl() =>
        global::Foundation.NSUrl.FromString("https://example.com/files/report.pdf")!;

    public void TestTrimmedUrlLabelConstructorFillsTheConfigDefault()
    {
        using var value = new SameArityInitSiblings(ReportUrl(), "named");
        AssertEqual("url:report.pdf", value.Source, "(url, label) reaches init(url:label:config:)");
        AssertEqual("named", value.Label, "(url, label) passes the label");
        AssertEqual(3, (int)value.Scale, "(url, label) leaves config to Swift's default (scale 3)");
    }

    public void TestTrimmedUrlConstructorFillsBothDefaults()
    {
        using var value = new SameArityInitSiblings(ReportUrl());
        AssertEqual("url:report.pdf", value.Source, "(url) reaches init(url:label:config:)");
        AssertEqual("none", value.Label, "(url) leaves label to Swift's default (nil)");
        AssertEqual(3, (int)value.Scale, "(url) leaves config to Swift's default (scale 3)");
    }

    public void TestFullUrlConstructorPassesEveryArgument()
    {
        using var value = new SameArityInitSiblings(ReportUrl(), null, new SameArityInitConfig(scale: 7));
        AssertEqual("url:report.pdf", value.Source, "full init(url:label:config:)");
        AssertEqual("none", value.Label, "a null label reaches Swift as nil");
        AssertEqual(7, (int)value.Scale, "the full constructor passes config");
    }

    public void TestSameAritySiblingReachesTheDataInitializer()
    {
        using var value = new SameArityInitSiblings(new byte[] { 1, 2, 3, 4, 5 }, new SameArityInitConfig(scale: 9));
        AssertEqual("data:5", value.Source, "(data, config) reaches init(data:config:)");
        AssertEqual("none", value.Label, "init(data:config:) sets no label");
        AssertEqual(9, (int)value.Scale, "(data, config) passes config");
    }

    public void TestTrimmedDataConstructorFillsTheConfigDefault()
    {
        using var value = new SameArityInitSiblings(new byte[] { 1, 2 });
        AssertEqual("data:2", value.Source, "(data) reaches init(data:config:)");
        AssertEqual(3, (int)value.Scale, "(data) leaves config to Swift's default (scale 3)");
    }
}
