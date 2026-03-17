// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

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

    public void TestBoundIntPairCreation()
    {
        var pair = new BoundIntPair(first: 10, second: 20);
        AssertEqual(10, pair.First, "BoundIntPair.First");
        AssertEqual(20, pair.Second, "BoundIntPair.Second");
        TestLogger.Info($"BoundIntPair(10, 20) = ({pair.First}, {pair.Second})");
    }

    public void TestBoundIntPairSum()
    {
        var pair = new BoundIntPair(first: 3, second: 7);
        var sum = pair.Sum();
        AssertEqual(10, sum, "BoundIntPair.Sum()");
        TestLogger.Info($"BoundIntPair(3, 7).Sum() = {sum}");
    }

    public void TestBoundIntPairZero()
    {
        var pair = new BoundIntPair(first: 0, second: 0);
        AssertEqual(0, pair.First, "Zero first");
        AssertEqual(0, pair.Second, "Zero second");
        AssertEqual(0, pair.Sum(), "Zero sum");
    }

    public void TestBoundIntPairNegative()
    {
        var pair = new BoundIntPair(first: -5, second: 15);
        AssertEqual(-5, pair.First, "Negative first");
        AssertEqual(15, pair.Second, "Positive second");
        AssertEqual(10, pair.Sum(), "Mixed sign sum");
    }

    #endregion

    #region SummableInt32 (Frozen Struct + Protocol Conformance) Tests

    public void TestSummableInt32Creation()
    {
        var s = new SummableInt32(value: 42);
        AssertEqual(42, s.Value, "SummableInt32.Value");
        TestLogger.Info($"SummableInt32(42).Value = {s.Value}");
    }

    public void TestSummableInt32Add()
    {
        var a = new SummableInt32(value: 10);
        var b = new SummableInt32(value: 20);
        var result = a.Add(b);
        AssertEqual(30, result.Value, "SummableInt32.Add()");
        TestLogger.Info($"SummableInt32(10).Add(SummableInt32(20)) = {result.Value}");
    }

    public void TestSummableInt32AddZero()
    {
        var a = new SummableInt32(value: 7);
        var zero = new SummableInt32(value: 0);
        var result = a.Add(zero);
        AssertEqual(7, result.Value, "Add zero identity");
    }

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

    public void TestMutableItemCreation()
    {
        var item = new MutableItem(value: 100);
        AssertEqual(100, item.Value, "MutableItem.Value via property");
        AssertEqual(100, item.GetValue(), "MutableItem.GetValue()");
        TestLogger.Info($"MutableItem(100).Value = {item.Value}");
    }

    public void TestMutableItemSetValue()
    {
        var item = new MutableItem(value: 10);
        AssertEqual(10, item.Value, "Initial value");
        item.SetValue(42);
        AssertEqual(42, item.GetValue(), "After SetValue(42)");
        TestLogger.Info($"MutableItem.SetValue(42) -> GetValue() = {item.GetValue()}");
    }

    public void TestMutableItemPropertySetter()
    {
        var item = new MutableItem(value: 0);
        AssertEqual(0, item.Value, "Initial zero");
        item.Value = 99;
        AssertEqual(99, item.Value, "After Value = 99");
    }

    public void TestMutableItemDispose()
    {
        var item = new MutableItem(value: 5);
        item.Dispose();
        AssertThrows<ObjectDisposedException>(() => { _ = item.Value; },
            "Disposed MutableItem throws on access");
    }

    #endregion

    #region BoundStringPair (Non-Frozen Struct, String Methods) Tests

    public void TestBoundStringPairCreation()
    {
        var pair = new BoundStringPair(first: "hello", second: "world");
        // Properties return SwiftString (Tier 3 — Mono JIT crash on property getter),
        // so we test via the Joined() method which returns idiomatic string.
        var joined = pair.GetJoined();
        AssertEqual("hello world", joined, "BoundStringPair.GetJoined()");
        TestLogger.Info($"BoundStringPair(\"hello\", \"world\").GetJoined() = \"{joined}\"");
    }

    public void TestBoundStringPairEmpty()
    {
        var pair = new BoundStringPair(first: "", second: "");
        AssertEqual(" ", pair.GetJoined(), "Empty strings joined with space");
    }

    #endregion

    #region SimpleItem (Non-Frozen Struct, Protocol Conformance with Strings) Tests

    public void TestSimpleItemDescribe()
    {
        var item = new SimpleItem(id: "test-1", label: "Widget");
        // Describe() returns idiomatic string (method, not property)
        var desc = item.GetDescribe();
        AssertTrue(desc.Contains("test-1"), "Description contains id");
        AssertTrue(desc.Contains("Widget"), "Description contains label");
        TestLogger.Info($"SimpleItem(\"test-1\", \"Widget\").GetDescribe() = \"{desc}\"");
    }

    #endregion

    #region DisplayItem (Non-Frozen Struct, Protocol Inheritance) Tests

    public void TestDisplayItemDescribe()
    {
        var item = new DisplayItem(text: "Hello");
        var desc = item.GetDescribe();
        AssertEqual("Describe: Hello", desc, "DisplayItem.GetDescribe()");
        TestLogger.Info($"DisplayItem(\"Hello\").GetDescribe() = \"{desc}\"");
    }

    public void TestDisplayItemDisplay()
    {
        var item = new DisplayItem(text: "Hello");
        var disp = item.GetDisplay();
        AssertEqual("Display: Hello", disp, "DisplayItem.GetDisplay()");
        TestLogger.Info($"DisplayItem(\"Hello\").GetDisplay() = \"{disp}\"");
    }

    #endregion

    #region IntContainer (Non-Frozen Struct with Associated Type) Tests
    // IntContainer constructor doesn't properly receive [Int32] array
    // through SwiftIndirectResult marshalling path. Count returns 0 for non-empty
    // arrays, and Element(at:) crashes with index out of range.

    [Skip("IntContainer array marshalling broken: Count returns 0")]
    public void TestIntContainerCreation()
    {
        var container = new IntContainer(items: new int[] { 10, 20, 30 });
        AssertEqual(3, container.Count, "IntContainer.Count");
        TestLogger.Info($"IntContainer([10, 20, 30]).Count = {container.Count}");
    }

    [Skip("IntContainer array marshalling broken: Element crashes")]
    public void TestIntContainerElementAt()
    {
        var container = new IntContainer(items: new int[] { 100, 200 });
        var first = container.Element(index: 0);
        var second = container.Element(index: 1);
        AssertEqual(100, first, "Element at 0");
        AssertEqual(200, second, "Element at 1");
    }

    [Skip("IntContainer array marshalling broken: Count returns 0")]
    public void TestIntContainerEmpty()
    {
        var container = new IntContainer(items: Array.Empty<int>());
        AssertEqual(0, container.Count, "Empty container count");
    }

    #endregion

    #region Unbound Generic Instantiation Tests
    // Tier 3: All unbound generics use TypeMetadata resolution via
    // SwiftObjectHelper<T>.GetTypeMetadata() through CallConvSwift.
    // Expected to hit Mono JIT assertion (jit-info.c:918).

    [Skip("NativeAOT: SIGSEGV in generic Wrapper<T> constructor — constrained generic dispatch crash")]
    public void TestWrapperCreation()
    {
        var inner = new SummableInt32(value: 42);
        var wrapper = new Wrapper<SummableInt32>(wrapped: inner);
        var unwrapped = wrapper.Wrapped;
        AssertEqual(42, unwrapped.Value, "Wrapper<SummableInt32>.Wrapped.Value");
        TestLogger.Info($"Wrapper<SummableInt32>(42).Wrapped.Value = {unwrapped.Value}");
    }

    [Skip("NativeAOT: SIGSEGV in generic Wrapper<T> — constrained generic dispatch crash")]
    public void TestWrapperUnwrap()
    {
        var inner = new SummableInt32(value: 99);
        var wrapper = new Wrapper<SummableInt32>(wrapped: inner);
        var result = wrapper.Unwrap();
        AssertEqual(99, result.Value, "Wrapper.Unwrap().Value");
        TestLogger.Info($"Wrapper<SummableInt32>(99).Unwrap().Value = {result.Value}");
    }

    [Skip("NativeAOT: SIGSEGV in GenericPair<T,U> constructor — constrained generic dispatch crash")]
    public void TestGenericPairCreation()
    {
        var a = new SummableInt32(value: 10);
        var b = new SummableInt32(value: 20);
        var pair = new GenericPair<SummableInt32, SummableInt32>(first: a, second: b);
        AssertEqual(10, pair.First.Value, "GenericPair.First.Value");
        AssertEqual(20, pair.Second.Value, "GenericPair.Second.Value");
        TestLogger.Info($"GenericPair(10, 20) = ({pair.First.Value}, {pair.Second.Value})");
    }

    [Skip("NativeAOT: SIGSEGV in GenericPair<T,U> — constrained generic dispatch crash")]
    public void TestGenericPairMixedTypes()
    {
        var s = new SummableInt32(value: 5);
        var p = new BoundIntPair(first: 1, second: 2);
        var pair = new GenericPair<SummableInt32, BoundIntPair>(first: s, second: p);
        AssertEqual(5, pair.First.Value, "GenericPair mixed First.Value");
        AssertEqual(3, pair.Second.Sum(), "GenericPair mixed Second.Sum()");
    }

    [Skip("NativeAOT: SIGSEGV in GenericClass<T> — constrained generic dispatch crash")]
    public void TestGenericClassCreation()
    {
        var inner = new SummableInt32(value: 77);
        var gc = new GenericClass<SummableInt32>(value: inner);
        var val = gc.Value;
        AssertEqual(77, val.Value, "GenericClass<SummableInt32>.Value.Value");
        TestLogger.Info($"GenericClass<SummableInt32>(77).Value.Value = {val.Value}");
    }

    [Skip("NativeAOT: SIGSEGV in GenericClass<T> — constrained generic dispatch crash")]
    public void TestGenericClassGetMethod()
    {
        var inner = new SummableInt32(value: 33);
        var gc = new GenericClass<SummableInt32>(value: inner);
        var result = gc.Get();
        AssertEqual(33, result.Value, "GenericClass.Get().Value");
    }

    [Skip("NativeAOT: SIGSEGV in generic type — constrained generic dispatch crash")]
    public void TestGenericClassValueSetter()
    {
        var gc = new GenericClass<SummableInt32>(value: new SummableInt32(value: 1));
        AssertEqual(1, gc.Value.Value, "Initial value");
        gc.Value = new SummableInt32(value: 100);
        AssertEqual(100, gc.Value.Value, "After setter");
        TestLogger.Info($"GenericClass.Value setter: 1 -> {gc.Value.Value}");
    }

    #endregion

    #region Generic Free Function Tests
    // Tier 3: Generic free functions use CallConvSwift for type metadata.
    // GetConstrained, SumTwo, GetDescribeConstrained deferred —
    // proxy types (SummableProxy, etc.) satisfy constraints but require
    // wrapper library bundled in RuntimeTestsApp (same blocker as proxy dispatch).

    [Skip("NativeAOT: SIGSEGV in generic type — constrained generic dispatch crash")]
    public void TestGetIdentity()
    {
        var original = new SummableInt32(value: 42);
        var result = TestLibFunctions.Identity(original);
        AssertEqual(42, result.Value, "GetIdentity round-trip");
        TestLogger.Info($"GetIdentity(SummableInt32(42)).Value = {result.Value}");
    }

    [Skip("NativeAOT: SIGSEGV in generic type — constrained generic dispatch crash")]
    public void TestGetIdentityPreservesValue()
    {
        var original = new SummableInt32(value: -100);
        var result = TestLibFunctions.Identity(original);
        AssertEqual(-100, result.Value, "GetIdentity negative value");
    }

    [Skip("NativeAOT: SIGSEGV in generic type — constrained generic dispatch crash")]
    public void TestGetPairSameType()
    {
        var a = new SummableInt32(value: 10);
        var b = new SummableInt32(value: 20);
        var pair = TestLibFunctions.Pair(a, b);
        AssertEqual(10, pair.Item1.Value, "GetPair Item1.Value");
        AssertEqual(20, pair.Item2.Value, "GetPair Item2.Value");
        TestLogger.Info($"GetPair(10, 20) = ({pair.Item1.Value}, {pair.Item2.Value})");
    }

    #endregion

    #region Pass 2 — M2: Generic Constructor with PWT (ConstrainedBox)

    [MonoJitCrash]
    public void TestConstrainedBoxCreation()
    {
        var item = new SimpleItem("gen-id", "test");
        var box = new ConstrainedBox<SimpleItem>(item);
        AssertNotNull(box, "ConstrainedBox created");
        TestLogger.Info("ConstrainedBox creation passed");
    }

    [MonoJitCrash]
    public void TestConstrainedBoxGetDescription()
    {
        var item = new SimpleItem("id1", "hello");
        var box = new ConstrainedBox<SimpleItem>(item);
        var desc = box.GetDescription();
        AssertTrue(desc.Contains("hello"), "Description contains label");
        TestLogger.Info($"ConstrainedBox.GetDescription = {desc}");
    }

    #endregion
}
