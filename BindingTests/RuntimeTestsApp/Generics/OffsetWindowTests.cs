// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.Linq;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// <c>OffsetWindow&lt;Element&gt;</c> is a <c>RandomAccessCollection</c> whose index space
/// starts at 100 — private storage, so the collection-witness projection path fires.
///
/// Every other Collection fixture in the library starts at zero, which let the projection
/// pass the managed index straight through to Swift as a native index without anyone
/// noticing. <c>IReadOnlyList&lt;T&gt;</c> is zero-based by contract: position 0 is the first
/// element and the valid range is <c>0 .. Count-1</c>, whatever the Swift collection's own
/// index space happens to be. These tests read exclusively through the interface, which is
/// how a consumer holds the value, and assert that contract end to end.
/// </summary>
public class OffsetWindowTests : TestBase
{
    public OffsetWindowTests(TestResults results) : base(results) { }

    public void TestOffsetWindow_FixtureReallyStartsAtNonZeroIndex()
    {
        // Positive control. If the fixture ever collapsed to a zero-based collection, every
        // other assertion in this class would pass while proving nothing, so pin the base
        // through the type's own projected property before relying on it.
        using var window = Functions.MakeOffsetWindow(
            firstId: "a", secondId: "b", thirdId: "c");

        AssertEqual(100, window.WindowBase, "OffsetWindow native start index");
    }

    public void TestOffsetWindow_IndexZeroIsFirstElement()
    {
        // The heart of the contract: managed position 0 must reach the element at native
        // index 100, not trip a bounds error. Reading the last element too pins the far end
        // of the translation — an off-by-one in the offset walk shows up there first.
        using var window = Functions.MakeOffsetWindow(
            firstId: "first", secondId: "middle", thirdId: "last");
        IReadOnlyList<CollectibleCoin> view = window;

        AssertEqual(3, view.Count, "OffsetWindow.Count");

        using var first = view[0];
        using var middle = view[1];
        using var last = view[view.Count - 1];

        AssertEqual("first", first.CollectibleId, "view[0]");
        AssertEqual("middle", middle.CollectibleId, "view[1]");
        AssertEqual("last", last.CollectibleId, "view[Count - 1]");
    }

    public void TestOffsetWindow_ForeachYieldsEveryElementInOrder()
    {
        // The enumerator walks 0 .. Count-1 through the projected indexer, so it only works
        // if the offset translation does. Against the pre-fix projection this yielded nothing
        // but bounds errors, since the very first step asked for position 0.
        using var window = Functions.MakeOffsetWindow(
            firstId: "w-0", secondId: "w-1", thirdId: "w-2");

        var collected = new List<string>();
        foreach (var coin in window)
        {
            collected.Add(coin.CollectibleId);
            coin.Dispose();
        }

        AssertEqual(3, collected.Count, "foreach yielded 3 elements");
        AssertEqual("w-0", collected[0], "foreach element 0");
        AssertEqual("w-1", collected[1], "foreach element 1");
        AssertEqual("w-2", collected[2], "foreach element 2");
    }

    public void TestOffsetWindow_OutOfRangeOffsetsThrow()
    {
        // -1 and Count are the two ordinary bounds errors. Both must be rejected before the
        // native subscript is evaluated: Swift's Collection subscript is a precondition, so a
        // miss that reached it would trap the process instead of raising a catchable error.
        // These two only pin the offset range itself; the assertion that distinguishes the
        // two index spaces is the WindowBase one below, where a value that IS a valid native
        // index (100) has to be rejected as an offset.
        using var window = Functions.MakeOffsetWindow(
            firstId: "a", secondId: "b", thirdId: "c");
        IReadOnlyList<CollectibleCoin> view = window;

        AssertThrows<ArgumentOutOfRangeException>(
            () => { using var _ = view[-1]; }, "view[-1] throws");
        AssertThrows<ArgumentOutOfRangeException>(
            () => { using var _ = view[view.Count]; }, "view[Count] throws");

        // The window's own native start index is NOT a valid managed offset, even though the
        // pre-fix projection accepted exactly that value and rejected everything else.
        AssertThrows<ArgumentOutOfRangeException>(
            () => { using var _ = view[window.WindowBase]; }, "view[WindowBase] throws");

        using var stillReadable = view[0];
        AssertEqual("a", stillReadable.CollectibleId, "view[0] still reads after the misses");
    }

    public void TestOffsetWindow_Empty_CountZeroAndNoElements()
    {
        // Empty but still based at 100: startIndex == endIndex == 100, count == 0. Enumeration
        // must yield nothing and position 0 must be a managed bounds error rather than a trap.
        using var empty = Functions.MakeEmptyOffsetWindow();
        IReadOnlyList<CollectibleCoin> view = empty;

        AssertEqual(0, view.Count, "empty OffsetWindow.Count");
        AssertEqual(100, empty.WindowBase, "empty OffsetWindow native start index");

        var enumerated = 0;
        foreach (var coin in empty)
        {
            enumerated++;
            coin.Dispose();
        }
        AssertEqual(0, enumerated, "empty OffsetWindow yields no elements");

        AssertThrows<ArgumentOutOfRangeException>(
            () => { using var _ = view[0]; }, "empty view[0] throws");
    }

    public void TestOffsetWindow_LinqOverInterface()
    {
        // LINQ runs off IEnumerable<T>.GetEnumerator() — the same path a view model or any
        // consumer library takes when it never touches the concrete type at all.
        using var window = Functions.MakeOffsetWindow(
            firstId: "x", secondId: "y", thirdId: "z");
        IReadOnlyList<CollectibleCoin> view = window;

        var ids = view.Select(c => c.CollectibleId).ToArray();

        AssertEqual(3, ids.Length, "LINQ element count");
        AssertEqual("x", ids[0], "LINQ[0]");
        AssertEqual("y", ids[1], "LINQ[1]");
        AssertEqual("z", ids[2], "LINQ[2]");
    }
}
