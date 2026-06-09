// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift.Runtime;
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

    public void TestIntContainerCreation()
    {
        var container = new IntContainer(items: new int[] { 10, 20, 30 });
        AssertEqual(3, container.Count, "IntContainer.Count");
        TestLogger.Info($"IntContainer([10, 20, 30]).Count = {container.Count}");
    }

    public void TestIntContainerElementAt()
    {
        var container = new IntContainer(items: new int[] { 100, 200 });
        var first = container.Element(index: 0);
        var second = container.Element(index: 1);
        AssertEqual(100, first, "Element at 0");
        AssertEqual(200, second, "Element at 1");
    }

    public void TestIntContainerEmpty()
    {
        var container = new IntContainer(items: Array.Empty<int>());
        AssertEqual(0, container.Count, "Empty container count");
    }

    #endregion

    #region Unbound Generic Instantiation Tests
    // Generic types use TypeMetadata resolution via SwiftObjectHelper<T>.GetTypeMetadata().
    // Class/struct allocating inits pass specialized metatype (e.g., GenericClass<T>.self).

    public void TestWrapperCreation()
    {
        var inner = new SummableInt32(value: 42);
        var wrapper = new Wrapper<SummableInt32>(wrapped: inner);
        var unwrapped = wrapper.Wrapped;
        AssertEqual(42, unwrapped.Value, "Wrapper<SummableInt32>.Wrapped.Value");
        TestLogger.Info($"Wrapper<SummableInt32>(42).Wrapped.Value = {unwrapped.Value}");
    }

    public void TestWrapperUnwrap()
    {
        var inner = new SummableInt32(value: 99);
        var wrapper = new Wrapper<SummableInt32>(wrapped: inner);
        var result = wrapper.Unwrap();
        AssertEqual(99, result.Value, "Wrapper.Unwrap().Value");
        TestLogger.Info($"Wrapper<SummableInt32>(99).Unwrap().Value = {result.Value}");
    }

    public void TestGenericPairCreation()
    {
        var a = new SummableInt32(value: 10);
        var b = new SummableInt32(value: 20);
        var pair = new GenericPair<SummableInt32, SummableInt32>(first: a, second: b);
        AssertEqual(10, pair.First.Value, "GenericPair.First.Value");
        AssertEqual(20, pair.Second.Value, "GenericPair.Second.Value");
        TestLogger.Info($"GenericPair(10, 20) = ({pair.First.Value}, {pair.Second.Value})");
    }

    public void TestGenericPairMixedTypes()
    {
        var s = new SummableInt32(value: 5);
        var p = new BoundIntPair(first: 1, second: 2);
        var pair = new GenericPair<SummableInt32, BoundIntPair>(first: s, second: p);
        AssertEqual(5, pair.First.Value, "GenericPair mixed First.Value");
        AssertEqual(3, pair.Second.Sum(), "GenericPair mixed Second.Sum()");
    }


    public void TestGenericClassCreation()
    {
        var inner = new SummableInt32(value: 77);
        var gc = new GenericClass<SummableInt32>(value: inner);
        var val = gc.Value;
        AssertEqual(77, val.Value, "GenericClass<SummableInt32>.Value.Value");
        TestLogger.Info($"GenericClass<SummableInt32>(77).Value.Value = {val.Value}");
    }

    public void TestGenericClassGetMethod()
    {
        var inner = new SummableInt32(value: 33);
        var gc = new GenericClass<SummableInt32>(value: inner);
        var result = gc.Get();
        AssertEqual(33, result.Value, "GenericClass.Get().Value");
    }

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
    // Generic free functions pass per-param TypeMetadata directly via CallConvSwift.
    // GetConstrained, SumTwo, GetDescribeConstrained deferred —
    // proxy types (SummableProxy, etc.) satisfy constraints but require
    // wrapper library bundled in RuntimeTestsApp (same blocker as proxy dispatch).

    public void TestGetIdentity()
    {
        var original = new SummableInt32(value: 42);
        var result = TestLibFunctions.Identity(original);
        AssertEqual(42, result.Value, "GetIdentity round-trip");
        TestLogger.Info($"GetIdentity(SummableInt32(42)).Value = {result.Value}");
    }

    public void TestGetIdentityPreservesValue()
    {
        var original = new SummableInt32(value: -100);
        var result = TestLibFunctions.Identity(original);
        AssertEqual(-100, result.Value, "GetIdentity negative value");
    }

    public void TestGetPairSameType()
    {
        var a = new SummableInt32(value: 10);
        var b = new SummableInt32(value: 20);
#pragma warning disable CS0618 // [Obsolete] — CallConvSwift fallback for method-level generics
        var pair = TestLibFunctions.Pair(a, b);
#pragma warning restore CS0618
        AssertEqual(10, pair.Item1.Value, "GetPair Item1.Value");
        AssertEqual(20, pair.Item2.Value, "GetPair Item2.Value");
        TestLogger.Info($"GetPair(10, 20) = ({pair.Item1.Value}, {pair.Item2.Value})");
    }

    public void TestGetPairHeterogeneousStructs()
    {
        var a = new SummableInt32(value: 7);
        var b = new SimpleItem("id-99", "hello");
#pragma warning disable CS0618
        var pair = TestLibFunctions.Pair(a, b);
#pragma warning restore CS0618
        AssertEqual(7, pair.Item1.Value, "GetPair heterogeneous Item1.Value");
        AssertEqual("id-99", pair.Item2.Id.ToString(), "GetPair heterogeneous Item2.Id");
        AssertEqual("hello", pair.Item2.Label.ToString(), "GetPair heterogeneous Item2.Label");
    }

    public void TestGetPairTwoClasses()
    {
        var coord = new CoordinateRef(x: 3, y: 4);
        var label = new LabelRef(text: "origin");
#pragma warning disable CS0618
        var pair = TestLibFunctions.Pair(coord, label);
#pragma warning restore CS0618
        AssertEqual(3, pair.Item1.X, "GetPair class Item1.X");
        AssertEqual(4, pair.Item1.Y, "GetPair class Item1.Y");
        AssertEqual("origin", pair.Item2.Text.ToString(), "GetPair class Item2.Text");
    }

    #endregion

    #region Generic Constructor with PWT (ConstrainedBox)

    public void TestConstrainedBoxCreation()
    {
        var item = new SimpleItem("gen-id", "test");
        var box = new ConstrainedBox<SimpleItem>(item);
        AssertNotNull(box, "ConstrainedBox created");
        TestLogger.Info("ConstrainedBox creation passed");
    }

    public void TestConstrainedBoxGetDescription()
    {
        var item = new SimpleItem("id1", "hello");
        var box = new ConstrainedBox<SimpleItem>(item);
        var desc = box.GetDescription();
        AssertTrue(desc.Contains("hello"), "Description contains label");
        TestLogger.Info($"ConstrainedBox.GetDescription = {desc}");
    }

    #endregion

    #region Generic Class Implementing Protocol (GenericNamedBox)

    public void TestGenericNamedBoxCreation()
    {
        var item = new SummableInt32(value: 42);
        var box = new GenericNamedBox<SummableInt32>(value: item, name: "test-box");
        AssertNotNull(box, "GenericNamedBox created");
        TestLogger.Info("GenericNamedBox creation passed");
    }

    public void TestGenericNamedBoxName()
    {
        var item = new SummableInt32(value: 10);
        var box = new GenericNamedBox<SummableInt32>(value: item, name: "hello");
        var name = box.Name;
        AssertEqual("hello", name, "GenericNamedBox.Name");
        TestLogger.Info($"GenericNamedBox.Name = {name}");
    }

    #endregion

    #region Q2: Generic Class Inheriting Non-Generic Class (TypedEntity)

    public void TestBaseEntityCreation()
    {
        var entity = new BaseEntity(entityId: 1);
        AssertEqual(1, entity.GetEntityId(), "BaseEntity.GetEntityId()");
        TestLogger.Info("BaseEntity creation passed");
    }

    public void TestBaseEntityProperty()
    {
        var entity = new BaseEntity(entityId: 42);
        AssertEqual(42, entity.EntityId, "BaseEntity.EntityId property");
        TestLogger.Info($"BaseEntity.EntityId = {entity.EntityId}");
    }

    public void TestBaseEntityDispose()
    {
        var entity = new BaseEntity(entityId: 5);
        entity.Dispose();
        AssertThrows<ObjectDisposedException>(() => { _ = entity.GetEntityId(); },
            "Disposed BaseEntity throws on access");
    }

    public void TestTypedEntityCreation()
    {
        var item = new SummableInt32(value: 99);
        var entity = new TypedEntity<SummableInt32>(entityId: 5, content: item);
        AssertNotNull(entity, "TypedEntity created");
        AssertEqual(5, entity.GetEntityId(), "Inherited GetEntityId()");
        TestLogger.Info("TypedEntity creation + inherited method passed");
    }

    #endregion

    #region Constrained-generic type-metadata accessor (PWT) coverage
    // Guards the constrained-generic metadata witness-table accessor fix.
    // The type-level metadata accessor for a constrained-generic type must pass
    // a witness-table pointer for each conformance, in declaration-grouped /
    // lex-sorted order. Before the fix the C# call site only passed type
    // metadata, leaving uninitialized register state where Swift's
    // __swift_instantiateGenericMetadata expected a PWT — on arm64e that
    // produced a PAC trap inside MetadataCacheKey::operator==.
    //
    // The simplest valid test is to invoke the metadata accessor itself via
    // SwiftObjectHelper<T>.GetTypeMetadata() and assert a non-zero handle and
    // non-zero size are returned. Both prove that:
    //   1. The PInvoke signature now declares the PWT parameter, and
    //   2. The runtime call to __swift_instantiateGenericMetadata received a
    //      valid (non-uninitialized) witness-table pointer.
    //
    // Constrained generic *class* coverage is already provided by the existing
    // ConstrainedBox<T> tests (TestConstrainedBoxCreation /
    // TestConstrainedBoxGetDescription) — the constructor SBW wrapper for that
    // type calls PInvoke_getMetadata with the PWT, which would fail closed if
    // the fix were missing. The tests below add equivalent coverage for the
    // *enum* and *non-frozen struct* code paths.

    public void TestConstrainedDescribableEnumMetadata()
    {
        // Exercises EnumHandler.cs's metadata-accessor PInvoke and the eager
        // _payloadSize field initializer for a constrained generic enum
        // (DescribableBox<T> where T: Describable). Calling GetTypeMetadata()
        // hits the PInvoke_getMetadata call site that must now include the
        // Describable PWT arg.
        var metadata = SwiftObjectHelper<DescribableBox<SimpleItem>>.GetTypeMetadata();
        AssertTrue(metadata.Handle != IntPtr.Zero, "DescribableBox<SimpleItem> metadata handle is non-zero");
        AssertTrue(metadata.Size > 0, "DescribableBox<SimpleItem> metadata size is non-zero");
        TestLogger.Info($"DescribableBox<SimpleItem> metadata: handle=0x{metadata.Handle:X}, size={metadata.Size}");
    }

    public void TestConstrainedDescribableHolderMetadata()
    {
        // Exercises NonFrozenStructHandler.cs's metadata-accessor PInvoke and
        // the eager _payloadSize field initializer for a constrained generic
        // non-frozen struct (DescribableHolder<T> where T: Describable). This
        // is the precise code path that originally PAC-trapped Lottie on
        // NativeAOT/arm64e before the PWT arg was threaded through.
        var metadata = SwiftObjectHelper<DescribableHolder<SimpleItem>>.GetTypeMetadata();
        AssertTrue(metadata.Handle != IntPtr.Zero, "DescribableHolder<SimpleItem> metadata handle is non-zero");
        AssertTrue(metadata.Size > 0, "DescribableHolder<SimpleItem> metadata size is non-zero");
        TestLogger.Info($"DescribableHolder<SimpleItem> metadata: handle=0x{metadata.Handle:X}, size={metadata.Size}");
    }

    #endregion

    #region Bug #2 Regression — Constrained Extension Conflict Skip
    // ConstrainedExtensionWitness<Marker> has two `where Marker == Concrete`
    // extensions, both declaring `var markerLabel: String`. Pre-fix, the C#
    // emit produced duplicate `MarkerLabel` properties on the merged class and
    // failed compilation with CS0102/CS0111.
    //
    // The fix detects multi-specialization conflicts in
    // MemberEmissionValidator.CanEmitProperty (and the symmetric check in
    // MemberValidationPipeline.ValidatePropertyEmission) and skips ALL
    // conflicting copies. We do not pick a "winner" because each specialization
    // has its own per-monomorphization mangled accessor symbol — keeping one
    // would make `<DedupMarkerBeta>.MarkerLabel` silently dispatch to the
    // alpha specialization's symbol, returning wrong data. C# generics cannot
    // discriminate between closed instantiations at the dispatch site, so the
    // only correct behavior is to drop the property entirely.
    //
    // The structural assertion is that this test compiles (no CS0102/CS0111)
    // and that the unconstrained `Value` property still round-trips. We also
    // assert via reflection that `MarkerLabel` is NOT a public property on
    // either specialization — the constrained-extension members must be
    // genuinely absent, not silently emitted as a stub.

    public void TestConstrainedExtensionConflict_TypeCompilesAndUnconstrainedAccessorWorks()
    {
        var alpha = new ConstrainedExtensionWitness<DedupMarkerAlpha>(value: 7);
        AssertEqual(7, alpha.Value, "Alpha specialization stored value");

        var beta = new ConstrainedExtensionWitness<DedupMarkerBeta>(value: 11);
        AssertEqual(11, beta.Value, "Beta specialization stored value");

        TestLogger.Info(
            "ConstrainedExtensionWitness compiled cleanly across both specializations and Value round-trips.");
    }

    public void TestConstrainedExtensionConflict_MarkerLabelIsNotEmittedAsProperty()
    {
        // The constrained-extension `markerLabel` is emitted as extension methods,
        // NOT as a property on the generic class. Verify via reflection that the
        // property is absent from both closed-generic types (the extension method
        // is a static method on a separate class, not a member of the type itself).
        var alphaType = typeof(ConstrainedExtensionWitness<DedupMarkerAlpha>);
        var betaType = typeof(ConstrainedExtensionWitness<DedupMarkerBeta>);

        var alphaMarker = alphaType.GetProperty(
            "MarkerLabel",
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Static);
        var betaMarker = betaType.GetProperty(
            "MarkerLabel",
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Static);

        AssertTrue(alphaMarker is null,
            "MarkerLabel must NOT be emitted as a property on ConstrainedExtensionWitness<DedupMarkerAlpha>.");
        AssertTrue(betaMarker is null,
            "MarkerLabel must NOT be emitted as a property on ConstrainedExtensionWitness<DedupMarkerBeta>.");

        TestLogger.Info("ConstrainedExtensionWitness.MarkerLabel correctly absent as property on both specializations.");
    }

    public void TestConstrainedExtensionSpecialization_AlphaExtensionMethod()
    {
        // Verify the constrained-extension property is accessible via the extension method
        // on the closed generic type ConstrainedExtensionWitness<DedupMarkerAlpha>.
        var alpha = new ConstrainedExtensionWitness<DedupMarkerAlpha>(value: 42);

        // The extension method GetMarkerLabel() should return "alpha"
        var label = alpha.GetMarkerLabel();
        AssertEqual("alpha", label, "Alpha specialization markerLabel via extension method");

        TestLogger.Info($"ConstrainedExtensionWitness<DedupMarkerAlpha>.GetMarkerLabel() = {label}");
    }

    public void TestConstrainedExtensionSpecialization_BetaExtensionMethod()
    {
        // Verify the beta specialization returns "beta"
        var beta = new ConstrainedExtensionWitness<DedupMarkerBeta>(value: 99);

        var label = beta.GetMarkerLabel();
        AssertEqual("beta", label, "Beta specialization markerLabel via extension method");

        TestLogger.Info($"ConstrainedExtensionWitness<DedupMarkerBeta>.GetMarkerLabel() = {label}");
    }

    #endregion

    #region IndexedSeries<T> (Collection-with-Metadata Projection) Tests

    public void TestIndexedSeriesCount()
    {
        using var series = Functions.MakeIndexedSeriesString();
        AssertEqual(4, series.Count, "IndexedSeries<String>.Count");
    }

    public void TestIndexedSeriesIndexer()
    {
        using var series = Functions.MakeIndexedSeriesString();
        AssertEqual("alpha", series[0].ToString(), "IndexedSeries<String>[0]");
        AssertEqual("beta", series[1].ToString(), "IndexedSeries<String>[1]");
        AssertEqual("gamma", series[2].ToString(), "IndexedSeries<String>[2]");
        AssertEqual("delta", series[3].ToString(), "IndexedSeries<String>[3]");
    }

    public void TestIndexedSeriesEnumeration()
    {
        using var series = Functions.MakeIndexedSeriesString();
        var seen = new List<string>();
        foreach (var element in series)
        {
            seen.Add(element.ToString());
        }
        AssertEqual(4, seen.Count, "IndexedSeries iteration count");
        AssertEqual("alpha", seen[0], "IndexedSeries iteration [0]");
        AssertEqual("beta", seen[1], "IndexedSeries iteration [1]");
        AssertEqual("gamma", seen[2], "IndexedSeries iteration [2]");
        AssertEqual("delta", seen[3], "IndexedSeries iteration [3]");
    }

    public void TestIndexedSeriesMetadata()
    {
        using var series = Functions.MakeIndexedSeriesString();
        AssertEqual("four-strings", series.Metadata, "IndexedSeries<String>.Metadata");
    }

    public void TestIndexedSeriesAsIReadOnlyList()
    {
        using var series = Functions.MakeIndexedSeriesString();
        global::System.Collections.Generic.IReadOnlyList<Swift.SwiftString> asList = series;
        AssertEqual(4, asList.Count, "IReadOnlyList<SwiftString>.Count");
        AssertEqual("alpha", asList[0].ToString(), "IReadOnlyList<SwiftString>[0]");
    }

    public void TestIndexedSeries_DirectCtor_RoundTripsItemsAndMetadata()
    {
        using var a = new Swift.SwiftString("first");
        using var b = new Swift.SwiftString("second");
        using var c = new Swift.SwiftString("third");
        using var series = new IndexedSeries<Swift.SwiftString>(
            items: new[] { a, b, c },
            metadata: "direct-ctor");

        AssertEqual(3, series.Count, "direct ctor: Count");
        AssertEqual("first", series[0].ToString(), "direct ctor: [0]");
        AssertEqual("second", series[1].ToString(), "direct ctor: [1]");
        AssertEqual("third", series[2].ToString(), "direct ctor: [2]");
        AssertEqual("direct-ctor", series.Metadata, "direct ctor: Metadata");
    }

    #endregion
}
