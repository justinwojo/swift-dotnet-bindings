// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using AVFoundation;
using Foundation;
using RuntimeTestsApp.Infrastructure;
using Swift;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Protocols;

/// <summary>
/// A platform framework's Objective-C class composed with a marker protocol
/// (<c>any AVAsset &amp; Sendable</c>) is the class at runtime and projects as the platform
/// binding's class, so a caller passes an <c>AVUrlAsset</c> directly. Before the projection the
/// members took <c>object</c>, and passing the asset threw <c>InvalidCastException</c>. Each check
/// reads back a value only the Swift callee can produce from the object it received (the asset
/// URL's file name, or the identity of the reference).
/// </summary>
public class PlatformClassMarkerCompositionTests : TestBase
{
    public PlatformClassMarkerCompositionTests(TestResults results) : base(results) { }

    static AVUrlAsset Asset(string name)
        => new AVUrlAsset(NSUrl.FromFilename(Path.Combine(Path.GetTempPath(), name)), (AVUrlAssetOptions?)null);

    public async Task TestAsyncThrowingInitializer_TakesTheAsset()
    {
        using var asset = Asset("clip-one.wav");
        using var session = await PlatformAssetMarkerSession.CreateAsync(asset);
        AssertEqual("clip-one.wav", session.AssetName, "async init(asset:) must receive the AVUrlAsset");
    }

    public async Task TestAsyncThrowingInitializer_SurfacesTheSwiftError()
    {
        using var composition = new AVMutableComposition();
        try
        {
            using var session = await PlatformAssetMarkerSession.CreateAsync(composition);
            AssertTrue(false, "a composition is not a URL asset, so the initializer must throw");
        }
        catch (SwiftException<PlatformAssetMarkerError> ex)
        {
            AssertEqual(PlatformAssetMarkerError.NotAURLAsset, ex.Error, "typed Swift error");
        }
    }

    public void TestSyncInitializerAndMethod_ReceiveTheAsset()
    {
        using var first = Asset("first.m4a");
        using var second = Asset("second.m4a");
        using var session = new PlatformAssetMarkerSession(first);
        AssertEqual("first.m4a", session.AssetName, "init(syncAsset:)");
        AssertEqual("second.m4a", session.Describe(second), "method parameter");
    }

    public void TestReturn_RoundTripsTheSameObject()
    {
        using var asset = Asset("echo.wav");
        using var session = new PlatformAssetMarkerSession(asset);
        var echoed = session.Echo(asset);
        AssertTrue(echoed is AVUrlAsset, $"echoed object must keep its class, got {echoed?.GetType().Name}");
        AssertEqual(asset.Handle, echoed.Handle, "the echoed composition must be the identical object");
    }

    public void TestOptionalAndArrayParameters()
    {
        using var a = Asset("a.wav");
        using var b = Asset("b.wav");
        using var session = new PlatformAssetMarkerSession(a);
        AssertEqual("<nil>", session.OptionalDescribe(null), "nil composition");
        AssertEqual("b.wav", session.OptionalDescribe(b), "non-nil composition");
        AssertEqual("a.wav,b.wav", session.Names(new AVAsset[] { a, b }), "an array of the composition is an array of the class");
    }

    public void TestCompositionAndPlainClass_AreTheSameObject()
    {
        using var asset = Asset("same.wav");
        using var other = Asset("same.wav");
        using var session = new PlatformAssetMarkerSession(asset);
        AssertTrue(session.IsSameObject(asset, asset), "the composition must carry the identical reference");
        AssertFalse(session.IsSameObject(asset, other), "distinct objects must stay distinct");
    }
}
