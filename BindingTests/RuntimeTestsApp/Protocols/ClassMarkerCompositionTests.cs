// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Protocols;

/// <summary>
/// A class composed only with marker protocols (<c>any SomeClass &amp; Sendable</c>) is the
/// class itself at runtime — same metadata, same single-reference layout — so it projects as
/// the class. These calls hand real objects across in every position the composition can take
/// (parameters, returns, properties, subscripts, a specialized generic class) and read back a
/// value only the Swift callee can produce from the object it received: the
/// subclass override's description proves the object arrived intact and dynamically dispatched.
/// Before the projection the members were skipped or took <c>object</c>, and passing a
/// <c>MarkerCompositionAsset</c> threw <c>InvalidCastException</c>.
/// </summary>
public class ClassMarkerCompositionTests : TestBase
{
    public ClassMarkerCompositionTests(TestResults results) : base(results) { }

    public void TestInitializer_TakesPureSwiftClassComposition()
    {
        using var clip = new MarkerCompositionClip(7, "intro");
        using var session = new MarkerCompositionSession(clip);
        AssertEqual("clip intro #7", session.Summary, "init(asset:) must receive the subclass instance");
    }

    public void TestInitializer_TakesNSObjectSubclassComposition()
    {
        using var media = new MarkerCompositionMedia("Overture");
        using var session = new MarkerCompositionSession(media);
        AssertEqual("media Overture", session.Summary, "init(media:) must receive the NSObject subclass instance");
    }

    public async Task TestAsyncInitializer_TakesComposition()
    {
        using var asset = new MarkerCompositionAsset(11);
        using var session = await MarkerCompositionSession.CreateAsync(asset);
        AssertEqual("async asset #11", session.Summary, "async init(asyncAsset:) must receive the instance");
    }

    public void TestMethodParameter_DispatchesThroughReceivedObject()
    {
        using var asset = new MarkerCompositionAsset(3);
        using var session = new MarkerCompositionSession(asset);
        using var clip = new MarkerCompositionClip(4, "outro");
        AssertEqual("asset #3", session.Describe(asset), "base-class object");
        AssertEqual("clip outro #4", session.Describe(clip), "subclass object dispatches to its override");
    }

    public void TestReturn_RoundTripsTheSameObject()
    {
        using var asset = new MarkerCompositionAsset(5);
        using var session = new MarkerCompositionSession(asset);
        using var clip = new MarkerCompositionClip(9, "echo");
        using var echoed = session.Echo(clip);
        AssertEqual(9L, (long)echoed.AssetID, "echoed composition must carry the original object's state");
        AssertEqual("clip echo #9", session.Describe(echoed), "echoed object must still be the subclass");
    }

    public void TestOptionalParameter_NilAndValue()
    {
        using var asset = new MarkerCompositionAsset(2);
        using var session = new MarkerCompositionSession(asset);
        AssertEqual("<nil>", session.OptionalDescribe(null), "nil composition");
        AssertEqual("asset #2", session.OptionalDescribe(asset), "non-nil composition");
    }

    public void TestArrayParameter_OfNSObjectComposition()
    {
        using var first = new MarkerCompositionMedia("Prelude");
        using var second = new MarkerCompositionMedia("Fugue");
        using var session = new MarkerCompositionSession(first);
        AssertEqual("Prelude", session.FirstTitle(new[] { first, second }),
            "an array of the composition is an array of the class");
    }

    public void TestCompositionAndPlainClass_AreTheSameObject()
    {
        using var media = new MarkerCompositionMedia("Coda");
        using var session = new MarkerCompositionSession(media);
        AssertTrue(session.IsSameObject(media, media), "the composition must carry the identical reference");
        using var other = new MarkerCompositionMedia("Coda");
        AssertFalse(session.IsSameObject(media, other), "distinct objects must stay distinct");
    }

    public void TestFreeFunction_TakesComposition()
    {
        using var clip = new MarkerCompositionClip(1, "free");
        AssertEqual("clip free #1", Functions.DescribeMarkerCompositionAsset(clip),
            "free function must receive the subclass instance");
    }

    public void TestStoredProperty_GetAndSetTheComposition()
    {
        using var first = new MarkerCompositionClip(2, "first");
        using var slot = new MarkerCompositionSlot(first);
        using var read = slot.Current;
        AssertEqual(2L, (long)read.AssetID, "the getter must return the stored instance");

        using var second = new MarkerCompositionClip(3, "second");
        slot.Current = second;
        AssertEqual("clip second #3", slot.CurrentDescription, "the setter must store the instance handed in");
    }

    public void TestStaticProperty_GetAndSetTheComposition()
    {
        using var original = MarkerCompositionSlot.Fallback;
        try
        {
            using var replacement = new MarkerCompositionClip(4, "static");
            MarkerCompositionSlot.Fallback = replacement;
            using var read = MarkerCompositionSlot.Fallback;
            AssertEqual(4L, (long)read.AssetID, "the static property must round-trip the instance");
        }
        finally
        {
            MarkerCompositionSlot.Fallback = original;
        }
    }

    public void TestSubscript_GetAndSetTheComposition()
    {
        using var first = new MarkerCompositionClip(5, "sub");
        using var slot = new MarkerCompositionSlot(first);
        using var read = slot[0];
        AssertEqual(5L, (long)read.AssetID, "the subscript getter must return the stored instance");

        using var second = new MarkerCompositionClip(6, "sub-set");
        slot[0] = second;
        AssertEqual("clip sub-set #6", slot.CurrentDescription, "the subscript setter must store the instance");
    }

    public void TestGenericClassComposition_PassesTheSpecializedClass()
    {
        using var box = new MarkerCompositionBox<int>(42);
        AssertEqual(42, Functions.MarkerCompositionBoxValue(box),
            "a specialized generic class must cross as the composition's object");
    }
}
