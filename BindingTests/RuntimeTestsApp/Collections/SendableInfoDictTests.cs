// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Collections;

/// <summary>
/// Tests for <c>[Key: any Sendable]</c> dictionary projection — the marker-only existential
/// value case. <c>any Sendable</c> filters to zero non-marker protocols, so its ABI container is
/// the zero-witness-table <c>ExistentialContainer0</c>, identical to bare <c>Any</c>; the value
/// projects to <c>object</c> and round-trips through <c>ExistentialContainer0.Box/Unbox</c>.
/// Mirrors Nuke's cross-library <c>ImageContainer.userInfo</c> / <c>ImageRequest.userInfo</c>
/// (<c>[UserInfoKey: any Sendable]</c>): a String-keyed form isolates the existential value and a
/// custom-Hashable-struct-keyed form (faithful to Nuke's <c>UserInfoKey</c>) exercises the same
/// value projection with a non-String key. Sibling of <see cref="DictionaryAnyTests"/> (bare Any).
/// </summary>
public class SendableInfoDictTests : TestBase
{
    public SendableInfoDictTests(TestResults results) : base(results) { }

    #region String-keyed [String: any Sendable]

    public void TestSendableInfoConstructionAndCount()
    {
        using var box = new SendableInfoBox(new Dictionary<string, object>
        {
            { "name", "test" },
            { "count", 42L },
        });
        AssertEqual(2, box.GetStringKeyedCount(), "String-keyed any Sendable dict has count 2");
    }

    public void TestSendableInfoStringValueParamDirection()
    {
        // C# boxes a string into the any Sendable value; Swift reads it back via `as? String`.
        using var box = new SendableInfoBox(new Dictionary<string, object>
        {
            { "name", "hello world" },
        });
        AssertEqual("hello world", box.StringValue("name"),
            "String value boxed into any Sendable survives C#→Swift round-trip (Swift as? String)");
    }

    public void TestSendableInfoIntValueParamDirection()
    {
        // Swift Int is 64-bit, so box a C# long; Swift reads it back via `as? Int`.
        using var box = new SendableInfoBox(new Dictionary<string, object>
        {
            { "count", 42L },
        });
        AssertEqual(42L, box.IntValue("count"),
            "Int value boxed into any Sendable survives C#→Swift round-trip (Swift as? Int)");
    }

    public void TestSendableInfoPropertyGetter()
    {
        using var box = new SendableInfoBox(new Dictionary<string, object>
        {
            { "name", "alpha" },
            { "count", 7L },
        });
        var projected = box.StringKeyed;
        AssertEqual(2, projected.Count, "Projected [String: any Sendable] property has 2 entries");
        AssertEqual("alpha", (string)projected["name"], "Projected string value unboxes to C# string");
        AssertEqual(7L, (long)projected["count"], "Projected int value unboxes to C# long");
    }

    public void TestSendableInfoReturnDirection()
    {
        // Swift builds ["name": name, "count": count] as [String: any Sendable] and returns it;
        // C# unboxes each any Sendable value back to a managed object.
        var made = TestLibFunctions.MakeSendableInfo("world", 7);
        AssertEqual(2, made.Count, "makeSendableInfo returns 2 entries");
        AssertEqual("world", (string)made["name"], "Swift String value unboxes to C# string");
        AssertEqual(7L, (long)made["count"], "Swift Int value unboxes to C# long");
    }

    public void TestSendableInfoFreeFunctionParamDirection()
    {
        var count = TestLibFunctions.CountSendableInfo(new Dictionary<string, object>
        {
            { "a", "alpha" },
            { "b", 2L },
            { "c", true },
        });
        AssertEqual(3L, count, "countSendableInfo counts 3 entries");
    }

    #endregion

    #region Custom-Hashable-struct-keyed [SendableInfoKey: any Sendable] (faithful Nuke UserInfoKey)

    public void TestSendableInfoStructKeyedSetAndRead()
    {
        using var box = new SendableInfoBox(new Dictionary<string, object>());
        box.SetStructKeyed(new Dictionary<SendableInfoKey, object>
        {
            { new SendableInfoKey("theme"), "dark" },
        });
        AssertEqual(1, box.GetStructKeyedCount(), "Struct-keyed any Sendable dict has count 1");
        AssertEqual("dark", box.StructKeyedStringValue("theme"),
            "String value survives custom-Hashable-key any Sendable round-trip");
    }

    #endregion
}
