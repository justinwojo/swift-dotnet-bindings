// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift.SwiftBindingsTestLib;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// Tests for generic types and protocol conformance types.
/// Class name "BasicGenericTests" sorts alphabetically before "EnumMarshallingTests"
/// (the Mono JIT crash point that kills the process).
/// </summary>
public class BasicGenericTests : TestBase
{
    public BasicGenericTests(TestResults results) : base(results) { }

    #region BoundIntPair (Frozen Struct) Tests

    [TestTier(TestTier.Tier1)]
    public void TestBoundIntPairCreation()
    {
        var pair = new BoundIntPair(first: 10, second: 20);
        AssertEqual(10, pair.First, "BoundIntPair.First");
        AssertEqual(20, pair.Second, "BoundIntPair.Second");
        TestLogger.Info($"BoundIntPair(10, 20) = ({pair.First}, {pair.Second})");
    }

    [TestTier(TestTier.Tier1)]
    public void TestBoundIntPairSum()
    {
        var pair = new BoundIntPair(first: 3, second: 7);
        var sum = pair.Sum();
        AssertEqual(10, sum, "BoundIntPair.Sum()");
        TestLogger.Info($"BoundIntPair(3, 7).Sum() = {sum}");
    }

    [TestTier(TestTier.Tier1)]
    public void TestBoundIntPairZero()
    {
        var pair = new BoundIntPair(first: 0, second: 0);
        AssertEqual(0, pair.First, "Zero first");
        AssertEqual(0, pair.Second, "Zero second");
        AssertEqual(0, pair.Sum(), "Zero sum");
    }

    [TestTier(TestTier.Tier1)]
    public void TestBoundIntPairNegative()
    {
        var pair = new BoundIntPair(first: -5, second: 15);
        AssertEqual(-5, pair.First, "Negative first");
        AssertEqual(15, pair.Second, "Positive second");
        AssertEqual(10, pair.Sum(), "Mixed sign sum");
    }

    #endregion

    #region SummableInt32 (Frozen Struct + Protocol Conformance) Tests

    [TestTier(TestTier.Tier1)]
    public void TestSummableInt32Creation()
    {
        var s = new SummableInt32(value: 42);
        AssertEqual(42, s.Value, "SummableInt32.Value");
        TestLogger.Info($"SummableInt32(42).Value = {s.Value}");
    }

    [TestTier(TestTier.Tier1)]
    public void TestSummableInt32Add()
    {
        var a = new SummableInt32(value: 10);
        var b = new SummableInt32(value: 20);
        var result = a.Add(b);
        AssertEqual(30, result.Value, "SummableInt32.Add()");
        TestLogger.Info($"SummableInt32(10).Add(SummableInt32(20)) = {result.Value}");
    }

    [TestTier(TestTier.Tier1)]
    public void TestSummableInt32AddZero()
    {
        var a = new SummableInt32(value: 7);
        var zero = new SummableInt32(value: 0);
        var result = a.Add(zero);
        AssertEqual(7, result.Value, "Add zero identity");
    }

    [TestTier(TestTier.Tier1)]
    public void TestSummableInt32ChainedAdd()
    {
        var a = new SummableInt32(value: 1);
        var b = new SummableInt32(value: 2);
        var c = new SummableInt32(value: 3);
        var result = a.Add(b).Add(c);
        AssertEqual(6, result.Value, "Chained Add: 1+2+3");
    }

    #endregion

    #region MutableItem (Non-Frozen Struct, Protocol Conformance) Tests

    [TestTier(TestTier.Tier1)]
    public void TestMutableItemCreation()
    {
        var item = new MutableItem(value: 100);
        AssertEqual(100, item.Value, "MutableItem.Value via property");
        AssertEqual(100, item.GetValue(), "MutableItem.GetValue()");
        TestLogger.Info($"MutableItem(100).Value = {item.Value}");
    }

    [TestTier(TestTier.Tier1)]
    public void TestMutableItemSetValue()
    {
        var item = new MutableItem(value: 10);
        AssertEqual(10, item.Value, "Initial value");
        item.SetValue(42);
        AssertEqual(42, item.GetValue(), "After SetValue(42)");
        TestLogger.Info($"MutableItem.SetValue(42) -> GetValue() = {item.GetValue()}");
    }

    [TestTier(TestTier.Tier1)]
    public void TestMutableItemPropertySetter()
    {
        var item = new MutableItem(value: 0);
        AssertEqual(0, item.Value, "Initial zero");
        item.Value = 99;
        AssertEqual(99, item.Value, "After Value = 99");
    }

    [TestTier(TestTier.Tier1)]
    public void TestMutableItemDispose()
    {
        var item = new MutableItem(value: 5);
        item.Dispose();
        AssertThrows<ObjectDisposedException>(() => { _ = item.Value; },
            "Disposed MutableItem throws on access");
    }

    #endregion

    #region BoundStringPair (Non-Frozen Struct, String Methods) Tests

    [TestTier(TestTier.Tier2)]
    public void TestBoundStringPairCreation()
    {
        var pair = new BoundStringPair(first: "hello", second: "world");
        // Properties return SwiftString (Tier 3 — Mono JIT crash on property getter),
        // so we test via the Joined() method which returns idiomatic string.
        var joined = pair.Joined();
        AssertEqual("hello world", joined, "BoundStringPair.Joined()");
        TestLogger.Info($"BoundStringPair(\"hello\", \"world\").Joined() = \"{joined}\"");
    }

    [TestTier(TestTier.Tier2)]
    public void TestBoundStringPairEmpty()
    {
        var pair = new BoundStringPair(first: "", second: "");
        AssertEqual(" ", pair.Joined(), "Empty strings joined with space");
    }

    #endregion

    #region SimpleItem (Non-Frozen Struct, Protocol Conformance with Strings) Tests

    [TestTier(TestTier.Tier2)]
    public void TestSimpleItemDescribe()
    {
        var item = new SimpleItem(id: "test-1", label: "Widget");
        // Describe() returns idiomatic string (method, not property)
        var desc = item.Describe();
        AssertTrue(desc.Contains("test-1"), "Description contains id");
        AssertTrue(desc.Contains("Widget"), "Description contains label");
        TestLogger.Info($"SimpleItem(\"test-1\", \"Widget\").Describe() = \"{desc}\"");
    }

    #endregion

    #region DisplayItem (Non-Frozen Struct, Protocol Inheritance) Tests

    [TestTier(TestTier.Tier2)]
    public void TestDisplayItemDescribe()
    {
        var item = new DisplayItem(text: "Hello");
        var desc = item.Describe();
        AssertEqual("Describe: Hello", desc, "DisplayItem.Describe()");
        TestLogger.Info($"DisplayItem(\"Hello\").Describe() = \"{desc}\"");
    }

    [TestTier(TestTier.Tier2)]
    public void TestDisplayItemDisplay()
    {
        var item = new DisplayItem(text: "Hello");
        var disp = item.Display();
        AssertEqual("Display: Hello", disp, "DisplayItem.Display()");
        TestLogger.Info($"DisplayItem(\"Hello\").Display() = \"{disp}\"");
    }

    #endregion

    #region IntContainer (Non-Frozen Struct with Associated Type) Tests
    // Tier 3: IntContainer constructor doesn't properly receive [Int32] array
    // through SwiftIndirectResult marshalling path. Count returns 0 for non-empty
    // arrays, and Element(at:) crashes with index out of range.

    [TestTier(TestTier.Tier3)]
    public void TestIntContainerCreation()
    {
        var container = new IntContainer(items: new int[] { 10, 20, 30 });
        AssertEqual(3, container.Count, "IntContainer.Count");
        TestLogger.Info($"IntContainer([10, 20, 30]).Count = {container.Count}");
    }

    [TestTier(TestTier.Tier3)]
    public void TestIntContainerElementAt()
    {
        var container = new IntContainer(items: new int[] { 100, 200 });
        var first = container.Element(at: 0);
        var second = container.Element(at: 1);
        AssertEqual(100, first, "Element at 0");
        AssertEqual(200, second, "Element at 1");
    }

    [TestTier(TestTier.Tier3)]
    public void TestIntContainerEmpty()
    {
        var container = new IntContainer(items: Array.Empty<int>());
        AssertEqual(0, container.Count, "Empty container count");
    }

    #endregion
}
