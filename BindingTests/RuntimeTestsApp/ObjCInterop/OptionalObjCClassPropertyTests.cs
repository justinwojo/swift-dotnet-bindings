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
/// surfaced a confirmed string-corruption bug: the Optional accessor getter returned
/// <c>SwiftOptional&lt;T&gt;</c> (MarshalFromSwift + NewSome), whose two VWT InitializeWithCopy
/// copies mangled the returned object's inline small-string storage ("TestApp" -> "TestCpp",
/// byte at offset 4 drifting up by 2 on each getter call). These tests must round-trip
/// exactly and show no cumulative drift across repeated getter calls. Coverage spans both
/// emitter copy-out paths for the shape: the property accessor (AccessorConversionVisitors,
/// which had the bug) and a method return of <c>Optional&lt;InfoCarrier&gt;</c>
/// (OptionalProjection, which always bypassed SwiftOptional — gated here for string integrity).
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

    // ---- Method-return path (OptionalProjection copy-out, distinct from the property accessor) ----
    //
    // The `Info` property getter above crosses AccessorConversionVisitors; a *method* returning
    // Optional<InfoCarrier> (snapshotInfo / makeInfoCarrier) crosses OptionalProjection — a
    // separate emitter path. Only the ACCESSOR path historically had the double-VWT
    // InitializeWithCopy small-string corruption (the SwiftOptional<T> + MarshalFromSwift + NewSome
    // return); OptionalProjection has always bypassed SwiftOptional (the IntPtr result IS the
    // payload), so it never exhibited that bug. These tests extend string-integrity gating to the
    // return copy-out anyway: the existing return-path coverage
    // (ClassParamCallbackTests.TestOptionalObjCPayloadReturnReads) reads only an Int32 field, so it
    // cannot observe string corruption; these pin the multi-String-field Stripe shape through the
    // return copy-out so a future OptionalProjection refactor can't silently introduce it there.

    public void TestSnapshotInfoReturnNameRoundTrip()
    {
        using var client = new ClientCarrier();
        using var info = new InfoCarrier("TestApp", "partner-123", "9.9", "https://example.com");
        client.Info = info;

        // snapshotInfo() returns the stored peer; not disposed here (the `info` using owns it),
        // mirroring the property-accessor tests above to avoid double-disposing a shared peer.
        // Swift `snapshotInfo()` emits as `GetSnapshotInfo()` (noun-only, zero-arg, non-void → Get prefix).
        var readBack = client.GetSnapshotInfo();
        AssertNotNull(readBack, "ClientCarrier.GetSnapshotInfo() should be Some after set");
        AssertEqual("TestApp", readBack!.Name.ToString(),
            "InfoCarrier.Name round-trips through the Optional method-return copy-out");
        AssertEqual("partner-123", readBack.PartnerId, "PartnerId round-trips through method return");
        AssertEqual("9.9", readBack.Version, "Version round-trips through method return");
        AssertEqual("https://example.com", readBack.Url, "Url round-trips through method return");
    }

    public void TestSnapshotInfoReturnNoCumulativeDrift()
    {
        // Return-path analogue of the accessor no-drift loop: the original corruption bumped the
        // inline storage on every getter call, so repeated method returns must stay stable.
        using var client = new ClientCarrier();
        using var info = new InfoCarrier("TestApp", null, "2.0", null);
        client.Info = info;

        for (int i = 0; i < 8; i++)
        {
            var readBack = client.GetSnapshotInfo();
            AssertNotNull(readBack, $"GetSnapshotInfo() should be Some (iteration {i})");
            AssertEqual("TestApp", readBack!.Name.ToString(),
                $"Name stable through method return with no cumulative drift (iteration {i})");
        }
    }

    public void TestMakeInfoCarrierReturnVariousStrings()
    {
        // Swift-origin strings (built entirely Swift-side) returned through the Optional method
        // path. Includes one heap-backed name (>15 UTF-8 bytes, past Swift's small-string inline
        // limit) so the fix is shown to preserve both inline and out-of-line String storage.
        string[] names = { "TestApp", "ABCDEFG", "X", "this-is-a-long-application-name-well-over-fifteen-bytes" };
        foreach (var expected in names)
        {
            using var readBack = TestLibFunctions.MakeInfoCarrier(expected, null, null, null);
            AssertNotNull(readBack, $"MakeInfoCarrier Some for \"{expected}\"");
            AssertEqual(expected, readBack!.Name.ToString(),
                $"Swift-origin Name round-trips through the Optional method return for \"{expected}\"");
        }
    }

    public void TestMakeInfoCarrierReturnNone()
    {
        var readBack = TestLibFunctions.MakeNilInfoCarrier();
        AssertNull(readBack, "MakeNilInfoCarrier returns None through the Optional method-return path");
    }

    public void TestSnapshotInfoReturnNoneBeforeSet()
    {
        using var client = new ClientCarrier();
        var readBack = client.GetSnapshotInfo();
        AssertNull(readBack, "GetSnapshotInfo() is None before Info is set");
    }
}
