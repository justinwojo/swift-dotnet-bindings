// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// Issue C regression: bound-generic return types whose type argument is the parent's
/// own sugared type parameter. <c>CollectibleBag&lt;Item&gt;.paired()</c> returns a
/// <c>CollectiblePair&lt;Item&gt;</c> — before the parent-generic pairing filter fix
/// in BoundGenericsHandler, this signature couldn't be marshalled end-to-end.
/// These tests prove the generated C# API is usable by a consumer, not just that
/// the wrapper symbols exist.
/// </summary>
public class CollectibleBagTests : TestBase
{
    public CollectibleBagTests(TestResults results) : base(results) { }

    public void TestCollectibleBag_Paired_RoundTripsBothIds()
    {
        var bag = Functions.MakeCoinBag(firstId: "alpha", secondId: "omega");
        var pair = bag.GetPaired();

        AssertEqual("alpha", pair.First.CollectibleId, "CollectiblePair.First.CollectibleId");
        AssertEqual("omega", pair.Second.CollectibleId, "CollectiblePair.Second.CollectibleId");
    }

    public void TestCollectibleBag_Paired_DistinctIds()
    {
        var bag = Functions.MakeCoinBag(firstId: "copper-1", secondId: "gold-99");
        var pair = bag.GetPaired();

        AssertEqual("copper-1", pair.First.CollectibleId, "first id round-trip");
        AssertEqual("gold-99", pair.Second.CollectibleId, "second id round-trip");
    }
}
