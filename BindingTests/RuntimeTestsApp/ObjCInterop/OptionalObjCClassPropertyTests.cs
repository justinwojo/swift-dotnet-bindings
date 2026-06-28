// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.ObjCInterop;

/// <summary>
/// Round-trips an Optional-typed ObjC-rooted (NSObject-derived) class property, then
/// reads String properties off the returned object. Mirrors the Stripe
/// <c>STPAPIClient.appInfo: STPAppInfo?</c> / <c>STPAppInfo.name: String</c> shape that
/// surfaced a confirmed string-corruption bug: the Optional getter's retain on the
/// returned object mangled its inline small-string storage ("TestApp" -> "TestCpp",
/// byte at offset 4 drifting up by 2 on each getter call). These tests must round-trip
/// exactly and show no cumulative drift across repeated getter calls.
/// </summary>
public class OptionalObjCClassPropertyTests : TestBase
{
    public OptionalObjCClassPropertyTests(TestResults results) : base(results) { }

    public void TestOptionalObjCClassPropertyNameRoundTrip()
    {
        using var client = new ClientCarrier();
        using var info = new InfoCarrier("TestApp", null, "2.0", null);
        client.Info = info;

        var readBack = client.Info;
        AssertNotNull(readBack, "ClientCarrier.Info should be Some after set");
        AssertEqual("TestApp", readBack!.Name.ToString(),
            "InfoCarrier.Name round-trips through Optional ObjC-class getter");
        TestLogger.Info($"ClientCarrier.Info.Name = \"{readBack.Name}\"");
    }

    public void TestOptionalObjCClassPropertyNoCumulativeDrift()
    {
        // The original corruption was cumulative: each Optional-getter call retained the
        // returned object and bumped its inline small-string storage, so the same name read
        // worse on each iteration. Reading the getter many times must keep the value stable.
        using var client = new ClientCarrier();
        using var info = new InfoCarrier("TestApp", null, "2.0", null);
        client.Info = info;

        for (int i = 0; i < 8; i++)
        {
            var readBack = client.Info;
            AssertNotNull(readBack, $"ClientCarrier.Info should be Some (iteration {i})");
            AssertEqual("TestApp", readBack!.Name.ToString(),
                $"InfoCarrier.Name stable with no cumulative drift (iteration {i})");
        }
    }

    public void TestOptionalObjCClassPropertyVariousSmallStrings()
    {
        // Small (SSO) strings store their bytes inline in the object's field, the exact
        // memory the corruption mutated. Exercise several so a single lucky value can't pass.
        string[] names = { "TestApp", "ABCDEFG", "abcde", "X", "Hello World" };
        foreach (var expected in names)
        {
            using var client = new ClientCarrier();
            using var info = new InfoCarrier(expected, null, null, null);
            client.Info = info;

            var readBack = client.Info;
            AssertNotNull(readBack, $"Info Some for \"{expected}\"");
            AssertEqual(expected, readBack!.Name.ToString(),
                $"InfoCarrier.Name round-trips for \"{expected}\"");
        }
    }

    public void TestOptionalObjCClassPropertyOptionalStringFields()
    {
        // STPAppInfo also carries optional String fields (partnerId/version/url). Verify the
        // optional-String getters on the round-tripped object are equally uncorrupted.
        using var client = new ClientCarrier();
        using var info = new InfoCarrier("PartnerApp", "partner-123", "9.9", "https://example.com");
        client.Info = info;

        var readBack = client.Info;
        AssertNotNull(readBack, "ClientCarrier.Info should be Some");
        AssertEqual("PartnerApp", readBack!.Name.ToString(), "Name round-trips");
        AssertEqual("partner-123", readBack.PartnerId, "PartnerId round-trips");
        AssertEqual("9.9", readBack.Version, "Version round-trips");
        AssertEqual("https://example.com", readBack.Url, "Url round-trips");
    }

    public void TestOptionalObjCClassPropertyNone()
    {
        using var client = new ClientCarrier();
        var readBack = client.Info;
        AssertNull(readBack, "ClientCarrier.Info should be None before set");
    }
}
