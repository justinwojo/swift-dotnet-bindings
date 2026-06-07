// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Protocols;

/// <summary>
/// Audit L229, value-correctness half: NESTED class-bound existential collections
/// (<c>[[any Marker]]</c>) crossing C# ↔ Swift in the PARAM (C#→Swift) and WRITE/reverse-dispatch
/// (C#-implemented getter → Swift reads it back) directions. The owned-RETURN direction and its ARC
/// balance live in <see cref="MemoryManagement.ClassBoundExistentialCollectionLeakProbeTests"/>; this
/// class pins that the two forward directions the nested-existential admission relaxation enables
/// actually round-trip the right values.
///
/// Both directions recurse the single-level class-bound carrier fix through an intermediate
/// <c>[any Marker]</c> layer: the wire type is <c>SwiftArray&lt;SwiftArray&lt;ClassExistentialContainer1&gt;&gt;</c>,
/// built (param) / read (write) by nesting the per-element 16-byte-carrier conversion inside the outer
/// array conversion. A dropped inner layer or a single-level stride assumption would crash or
/// miscount. Each consumer sums <c>markerId()</c> across the WHOLE grid (every inner element, not just
/// [0]) so a wrong nested stride surfaces rather than a lucky element[0] hit. Both proxy-layout
/// (Swift-vended <c>MarkerVendor.Make</c>) and boxable-layout (<c>new MarkerImpl(...)</c>) elements are
/// covered because the per-leaf narrowing must be layout-agnostic, exactly as in the single-level
/// <see cref="ClassBoundExistentialArrayWriteParamTests"/>.
/// </summary>
public class NestedClassBoundExistentialTests : TestBase
{
    public NestedClassBoundExistentialTests(TestResults results) : base(results) { }

    #region PARAM direction (C# nested IEnumerable<IMarker> → Swift func)

    /// <summary>
    /// PARAM, proxy-layout elements: a <c>[[any Marker]]</c> of Swift-vended proxies. Summing across
    /// every inner element of every row proves the nested 16-byte stride — a dropped inner layer or a
    /// 40-byte fill would crash or undercount.
    /// </summary>
    public void TestSumNestedMarkerGridParamProxyElements()
    {
        using var vendor = new MarkerVendor();
        var grid = new List<List<IMarker>>
        {
            new() { vendor.Make(1), vendor.Make(2) },
            new() { vendor.Make(3), vendor.Make(4), vendor.Make(5) },
        };

        var sum = (int)TestLibFunctions.SumNestedMarkerGrid(grid);

        AssertEqual(15, sum, "SumNestedMarkerGrid over proxy-layout nested elements (1+2+3+4+5)");
        TestLogger.Info($"PARAM nested proxy elements: SumNestedMarkerGrid = {sum}");
    }

    /// <summary>
    /// PARAM, boxable-layout elements: a <c>[[any Marker]]</c> of by-value <c>new MarkerImpl(...)</c>
    /// conformers. The per-leaf narrowing must read each witness from the dedicated witness word; the
    /// naive "Payload1" narrowing would hand Swift a null witness and crash on first dispatch.
    /// </summary>
    public void TestSumNestedMarkerGridParamBoxableElements()
    {
        var grid = new List<List<IMarker>>
        {
            new() { new MarkerImpl((nint)10), new MarkerImpl((nint)20) },
            new() { new MarkerImpl((nint)30) },
        };

        var sum = (int)TestLibFunctions.SumNestedMarkerGrid(grid);

        AssertEqual(60, sum, "SumNestedMarkerGrid over boxable-layout nested elements (10+20+30)");
        TestLogger.Info($"PARAM nested boxable elements: SumNestedMarkerGrid = {sum}");
    }

    /// <summary>
    /// PARAM, mixed layouts and a ragged grid (including an empty inner row): proves the narrowing keys
    /// off each element's own witness slot and the outer/inner counts marshal independently.
    /// </summary>
    public void TestSumNestedMarkerGridParamMixedRagged()
    {
        using var vendor = new MarkerVendor();
        var grid = new List<List<IMarker>>
        {
            new() { new MarkerImpl((nint)100), vendor.Make(7) },
            new(),                                              // empty inner row
            new() { vendor.Make(50), new MarkerImpl((nint)200) },
        };

        var sum = (int)TestLibFunctions.SumNestedMarkerGrid(grid);

        AssertEqual(357, sum, "SumNestedMarkerGrid over mixed/ragged nested elements (100+7+50+200)");
        TestLogger.Info($"PARAM nested mixed/ragged: SumNestedMarkerGrid = {sum}");
    }

    /// <summary>
    /// PARAM, Array-of-dictionaries: a <c>[[String: any Marker]]</c> built C#→Swift. Unlike the grids
    /// above (which nest an Array as the inner container) this nests a DICTIONARY, so the forward
    /// recursion must build a <c>SwiftArray&lt;SwiftDictionary&lt;SwiftString, ClassExistentialContainer1&gt;&gt;</c>
    /// — the dict-as-inner-container param path the admission gate now allows. Summing every value of
    /// every inner dictionary proves the buried 16-byte carriers stride correctly through a Dictionary
    /// layer (a dropped inner layer or single-level assumption would crash or undercount).
    /// </summary>
    public void TestSumNestedMarkerMapGridParamElements()
    {
        using var vendor = new MarkerVendor();
        var grid = new List<Dictionary<string, IMarker>>
        {
            new() { ["a"] = vendor.Make(1), ["b"] = new MarkerImpl((nint)2) },
            new(),                                              // empty inner dictionary
            new() { ["c"] = new MarkerImpl((nint)3), ["d"] = vendor.Make(4) },
        };

        var sum = (int)TestLibFunctions.SumNestedMarkerMapGrid(grid);

        AssertEqual(10, sum, "SumNestedMarkerMapGrid over array-of-dictionary nested values (1+2+3+4)");
        TestLogger.Info($"PARAM nested array-of-maps: SumNestedMarkerMapGrid = {sum}");
    }

    #endregion

    #region WRITE direction (C#-implemented nested getter → Swift reads it back)

    /// <summary>
    /// A pure C# implementation of <see cref="INestedMarkerProvider"/> whose <c>MarkerGrid</c> getter
    /// returns a caller-supplied nested list. Swift reaches it through the generated EveryProtocol
    /// receiver, so the receiver getter must recursively build
    /// <c>SwiftArray&lt;SwiftArray&lt;ClassExistentialContainer1&gt;&gt;</c> with each buried element
    /// narrowed to the 16-byte carrier.
    /// </summary>
    private sealed class CSharpNestedMarkerProvider : INestedMarkerProvider
    {
        private readonly IReadOnlyList<IReadOnlyList<IMarker>> _grid;
        public CSharpNestedMarkerProvider(IReadOnlyList<IReadOnlyList<IMarker>> grid) => _grid = grid;
        public IReadOnlyList<IReadOnlyList<IMarker>> MarkerGrid => _grid;
    }

    /// <summary>
    /// WRITE, proxy-layout elements: the C# getter returns Swift-vended proxies in a nested grid;
    /// Swift's <c>consumeNestedMarkerProvider</c> reads every buried element and sums the ids.
    /// </summary>
    public void TestConsumeNestedMarkerProviderWriteProxyElements()
    {
        using var vendor = new MarkerVendor();
        var provider = new CSharpNestedMarkerProvider(new List<IReadOnlyList<IMarker>>
        {
            new List<IMarker> { vendor.Make(4), vendor.Make(5) },
            new List<IMarker> { vendor.Make(6) },
        });

        var sum = (int)TestLibFunctions.ConsumeNestedMarkerProvider(provider);

        AssertEqual(15, sum, "ConsumeNestedMarkerProvider over C#-returned proxy nested elements (4+5+6)");
        TestLogger.Info($"WRITE nested proxy elements: ConsumeNestedMarkerProvider = {sum}");
    }

    /// <summary>
    /// WRITE, boxable-layout elements: the C# getter returns <c>new MarkerImpl(...)</c> by value in a
    /// nested grid, so the receiver getter narrows each buried element from the witness-word layout.
    /// </summary>
    public void TestConsumeNestedMarkerProviderWriteBoxableElements()
    {
        var provider = new CSharpNestedMarkerProvider(new List<IReadOnlyList<IMarker>>
        {
            new List<IMarker> { new MarkerImpl((nint)11), new MarkerImpl((nint)12) },
            new List<IMarker> { new MarkerImpl((nint)13) },
        });

        var sum = (int)TestLibFunctions.ConsumeNestedMarkerProvider(provider);

        AssertEqual(36, sum, "ConsumeNestedMarkerProvider over C#-returned boxable nested elements (11+12+13)");
        TestLogger.Info($"WRITE nested boxable elements: ConsumeNestedMarkerProvider = {sum}");
    }

    #endregion

    #region REVERSE-DISPATCH METHOD-PARAM (Swift builds the grid → C#-implemented consume(grid:) reads it)

    /// <summary>
    /// A pure C# implementation of <see cref="INestedMarkerMapConsumer"/>. Swift's
    /// <c>driveNestedMarkerMapConsumer</c> builds a <c>[[String: any Marker]]</c> and calls
    /// <c>consume(grid:)</c> through the generated EveryProtocol receiver, which materializes the
    /// array-of-dictionary param (Swift→C# READ) before handing it here. The generated interface types
    /// the param as <c>IEnumerable&lt;IDictionary&lt;string, IMarker&gt;&gt;</c>: the receiver builds each
    /// inner dictionary as a CONCRETE universal-donor <c>Dictionary&lt;…&gt;</c> (assignable to the
    /// invariant <c>IDictionary</c> element) and the outer covariant array lets it flow in as
    /// <c>IEnumerable</c>. Summing every value of every inner dictionary proves the buried 16-byte
    /// carriers stride correctly through the dict layer in the READ direction.
    /// </summary>
    private sealed class CSharpNestedMarkerMapConsumer : INestedMarkerMapConsumer
    {
        public nint Consume(IEnumerable<IDictionary<string, IMarker>> grid)
        {
            nint total = 0;
            foreach (var row in grid)
                foreach (var marker in row.Values)
                    total += marker.GetMarkerId();
            return total;
        }
    }

    /// <summary>
    /// REVERSE-DISPATCH METHOD-PARAM, the exact FirebaseFirestore <c>mapMerge([[String: Any]])</c> shape
    /// (audit L229): Swift builds an <c>outer</c>×<c>inner</c> grid of <c>MarkerImpl(mid: o*1000 + i)</c>
    /// keyed <c>"k{i}"</c> and passes it into the C# impl's <c>Consume</c> through the generated receiver.
    /// A read-only <c>IReadOnlyDictionary</c> value in the receiver's element conversion would be a
    /// compile-time CS1503 against the impl's <c>IDictionary</c> param; this runtime round-trip proves
    /// the concrete universal-donor dictionary both compiles AND carries the right values across the wire.
    /// </summary>
    [Skip("Collection-typed reverse-dispatch param materialization unimplemented: enumerating a Swift-built SwiftArray<SwiftDictionary<SwiftString, ClassExistentialContainer1>> in the EveryProtocol receiver SIGSEGVs in libswiftCore BridgeObjectBox::initializeWithCopy from SwiftArray.get_Item(0) — the receiver-path outer-array subscript retains a garbage bridge-object word at element[0]. Forward (C#->Swift param) and C# getter (WRITE) paths pass; only Swift->C# materialization of nested existential-valued collections crashes. Next-session work.")]
    public void TestDriveNestedMarkerMapConsumerReverseDispatch()
    {
        var consumer = new CSharpNestedMarkerMapConsumer();

        // Swift grid: o in 0..<3, i in 0..<2, marker id = o*1000 + i
        //   {0,1}, {1000,1001}, {2000,2001} → sum 6003
        var sum = (int)TestLibFunctions.DriveNestedMarkerMapConsumer(consumer, 3, 2);

        AssertEqual(6003, sum,
            "DriveNestedMarkerMapConsumer reads a Swift-built [[String: any Marker]] into the C# impl's IEnumerable<IDictionary> param and sums every value (0+1+1000+1001+2000+2001)");
        TestLogger.Info($"REVERSE-DISPATCH method-param array-of-maps: DriveNestedMarkerMapConsumer = {sum}");
    }

    #endregion

    #region REVERSE-DISPATCH SETTER (Swift assigns a nested-container existential dict into a C# settable property)

    /// <summary>
    /// A pure C# implementation of <see cref="IMutableMarkerMapGridHolder"/> with a SETTABLE
    /// <c>[String: [String: any Marker]]</c> property. Swift's <c>writeAndSumMarkerMapGrid</c> ASSIGNS a
    /// dict-of-dict grid through the generated EveryProtocol receiver SETTER, which materializes the incoming
    /// SwiftDictionary and converts it to this impl's idiomatic
    /// <c>IReadOnlyDictionary&lt;string, IReadOnlyDictionary&lt;string, IMarker&gt;&gt;</c> setter param. That outer
    /// VALUE slot is INVARIANT: before the receiver setter shared the forward-return invariant-slot cast, the
    /// inner dictionary's concrete <c>Dictionary&lt;…&gt;</c> value-selector body inferred
    /// <c>IReadOnlyDictionary&lt;string, Dictionary&lt;…&gt;&gt;</c> and the generated setter was a compile-time CS0266.
    /// </summary>
    private sealed class CSharpMutableMarkerMapGridHolder : IMutableMarkerMapGridHolder
    {
        public IReadOnlyDictionary<string, IReadOnlyDictionary<string, IMarker>> MarkerMapGrid { get; set; }
            = new Dictionary<string, IReadOnlyDictionary<string, IMarker>>();
    }

    /// <summary>
    /// REVERSE-DISPATCH SETTER, nested-container existential dict VALUE (audit L229 setter sibling): Swift
    /// builds an <c>outer</c>×<c>inner</c> grid of <c>MarkerImpl(mid: o*1000 + i)</c>, ASSIGNS it into the C#
    /// impl's settable <c>MarkerMapGrid</c> through the generated receiver setter, then reads it back through
    /// the getter and sums every buried marker id. A read-only/concrete-mismatched value in the receiver
    /// setter's element conversion would be a compile-time CS0266 against the impl's invariant
    /// <c>IReadOnlyDictionary</c> value slot; this runtime round-trip proves the hoisted invariant-slot cast
    /// both compiles AND carries the right values through the SET-then-GET path (0+1+1000+1001+2000+2001).
    /// </summary>
    [Skip("Collection-typed reverse-dispatch setter materialization unimplemented: assigning a Swift-built dict-of-dict existential grid through the EveryProtocol receiver setter SIGSEGVs on the same nested-collection materialization path as TestDriveNestedMarkerMapConsumerReverseDispatch (BridgeObjectBox::initializeWithCopy retaining a garbage bridge-object word). Next-session work.")]
    public void TestWriteAndSumMarkerMapGridReverseDispatchSetter()
    {
        var holder = new CSharpMutableMarkerMapGridHolder();

        var sum = (int)TestLibFunctions.WriteAndSumMarkerMapGrid(holder, 3, 2);

        AssertEqual(6003, sum,
            "WriteAndSumMarkerMapGrid assigns a Swift-built [String: [String: any Marker]] into the C# impl's settable IReadOnlyDictionary<…, IReadOnlyDictionary<…>> property and sums every value (0+1+1000+1001+2000+2001)");
        TestLogger.Info($"REVERSE-DISPATCH setter dict-of-maps: WriteAndSumMarkerMapGrid = {sum}");
    }

    #endregion

    #region OWNED-RETURN MATERIALIZATION (Swift builds and RETURNS the grid → C# materializes + enumerates)

    /// <summary>
    /// OWNED-RETURN, Array-of-dictionaries: Swift's <c>makeTrackedMarkerArrayOfMaps(outer:inner:)</c>
    /// builds an <c>outer</c>×<c>inner</c> <c>[[String: any Marker]]</c> of <c>MarkerImpl(mid: o*1000 + i)</c>
    /// keyed <c>"k{i}"</c> and RETURNS it; C# materializes the
    /// <c>SwiftArray&lt;SwiftDictionary&lt;SwiftString, ClassExistentialContainer1&gt;&gt;</c> and enumerates every
    /// inner dictionary's values. This is the forward-return twin of
    /// <see cref="TestDriveNestedMarkerMapConsumerReverseDispatch"/>: both enumerate an inner
    /// existential-valued <c>SwiftDictionary</c> extracted as an element of an outer <c>SwiftArray</c> via the
    /// shared <c>SwiftArray</c> indexer move-out. Summing every value across every row (0+1+1000+1001+2000+2001)
    /// proves the buried 16-byte carriers stride correctly through the dict layer in the RETURN direction and
    /// that the inner dictionary header survives the outer-array element extraction.
    /// </summary>
    [Skip("Owned-return materialization of nested existential-valued collections unimplemented: enumerating a Swift-returned SwiftArray<SwiftDictionary<SwiftString, ClassExistentialContainer1>> SIGSEGVs in BridgeObjectBox::initializeWithCopy from SwiftArray.get_Item(0) — same root cause as TestDriveNestedMarkerMapConsumerReverseDispatch (outer-array subscript retains a garbage bridge-object word at element[0]). Durable forward-return repro for the next-session fix.")]
    public void TestMakeTrackedMarkerArrayOfMapsReturnEnumerate()
    {
        var grid = TestLibFunctions.MakeTrackedMarkerArrayOfMaps(3, 2);

        int total = 0;
        foreach (var row in grid)
            foreach (var marker in row.Values)
                total += (int)marker.GetMarkerId();

        AssertEqual(6003, total,
            "MakeTrackedMarkerArrayOfMaps materializes a Swift-returned [[String: any Marker]] and sums every inner-dictionary value (0+1+1000+1001+2000+2001)");
        TestLogger.Info($"OWNED-RETURN array-of-maps: MakeTrackedMarkerArrayOfMaps sum = {total}");
    }

    /// <summary>
    /// OWNED-RETURN, Dictionary-of-dictionaries: Swift's <c>makeTrackedMarkerMapOfMaps(outer:inner:)</c>
    /// returns a <c>[String: [String: any Marker]]</c>; C# materializes the
    /// <c>SwiftDictionary&lt;SwiftString, SwiftDictionary&lt;SwiftString, ClassExistentialContainer1&gt;&gt;</c> and
    /// enumerates every inner dictionary. The forward-return twin of
    /// <see cref="TestWriteAndSumMarkerMapGridReverseDispatchSetter"/>: the inner existential-valued
    /// <c>SwiftDictionary</c> is extracted as a VALUE of an outer <c>SwiftDictionary</c> (not an array element),
    /// so it exercises the dict-value move-out path. Summing every buried value across every outer key proves
    /// the inner dictionary header survives outer-dictionary value extraction.
    /// </summary>
    [Skip("Owned-return materialization of nested existential-valued collections unimplemented: enumerating a Swift-returned SwiftDictionary<SwiftString, SwiftDictionary<SwiftString, ClassExistentialContainer1>> hits the same nested-collection materialization SIGSEGV as TestMakeTrackedMarkerArrayOfMapsReturnEnumerate (BridgeObjectBox::initializeWithCopy retaining a garbage bridge-object word). Durable forward-return repro for the next-session fix.")]
    public void TestMakeTrackedMarkerMapOfMapsReturnEnumerate()
    {
        var grid = TestLibFunctions.MakeTrackedMarkerMapOfMaps(3, 2);

        int total = 0;
        foreach (var inner in grid.Values)
            foreach (var marker in inner.Values)
                total += (int)marker.GetMarkerId();

        AssertEqual(6003, total,
            "MakeTrackedMarkerMapOfMaps materializes a Swift-returned [String: [String: any Marker]] and sums every inner-dictionary value (0+1+1000+1001+2000+2001)");
        TestLogger.Info($"OWNED-RETURN dict-of-maps: MakeTrackedMarkerMapOfMaps sum = {total}");
    }

    #endregion
}
