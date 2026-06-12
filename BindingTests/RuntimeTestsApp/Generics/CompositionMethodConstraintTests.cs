// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// ABI Coverage Grid — generics corner. Runtime round-trip for a Concrete-Specialization-Engine
/// method whose method-level generic carries a protocol-COMPOSITION constraint
/// <c>&lt;T: Describable &amp; TestIdentifiable&gt;</c> (and the explicit-<c>where</c> spelling of the
/// same composition). This is the reaching fixture for the roadmap latent "CSM per-method
/// where-clause filter treats <c>T : P &amp; Q</c> as one opaque target": if that filter were live,
/// <c>ParseMethodLevelConstraints</c> would store <c>"Describable &amp; TestIdentifiable"</c> as a
/// single name, never match a declared protocol, and false-reject every conformer — so NO concrete
/// <c>DescribeBoth(SimpleItem)</c> overload would emit and this file would fail to compile.
///
/// <para>It compiles and runs, which proves the engine SPLITS the composition and verifies each
/// protocol independently, emitting one concrete <c>CallConvCdecl</c> specialization per conformer
/// (<c>SimpleItem</c>, <c>MultiProtocolEntity</c>) for both the inline-composition method
/// (<c>describeBoth</c>) and the explicit-<c>where</c> method (<c>tagBoth</c>). Unlike the
/// open-generic constrained free functions, these specializations carry a real <c>@_cdecl</c>
/// wrapper and no SB0001 safety diagnostic — a supported surface, hence <c>expect-green</c>.</para>
///
/// <para>Overload resolution picks the concrete <c>DescribeBoth(SimpleItem)</c> over the
/// open-generic <c>DescribeBoth&lt;T&gt;</c> fallback, so these tests exercise the CSM-specialized
/// dispatch path, not the CallConvSwift fallback.</para>
/// </summary>
public class CompositionMethodConstraintTests : TestBase
{
    public CompositionMethodConstraintTests(TestResults results) : base(results) { }

    // MARK: - describeBoth<T: Describable & TestIdentifiable> — inline protocol composition

    public void TestDescribeBoth_InlineComposition_StructConformer_RoundTrips()
    {
        using var processor = new CompositionItemProcessor(prefix: "P");
        using var item = new SimpleItem(id: "node-7", label: "alpha");
        // Swift: "\(prefix): [\(item.id)] \(item.describe())" where describe() == "[node-7] alpha".
        var result = processor.DescribeBoth(item);
        AssertEqual("P: [node-7] [node-7] alpha", result, "describeBoth<SimpleItem>");
    }

    public void TestDescribeBoth_InlineComposition_ClassConformer_RoundTrips()
    {
        using var processor = new CompositionItemProcessor(prefix: "P");
        using var entity = new MultiProtocolEntity(id: "ent-3", name: "beta");
        // describe() for MultiProtocolEntity == "[ent-3] beta".
        var result = processor.DescribeBoth(entity);
        AssertEqual("P: [ent-3] [ent-3] beta", result, "describeBoth<MultiProtocolEntity>");
    }

    // MARK: - tagBoth<T> where T: Describable & TestIdentifiable — explicit where-clause composition

    public void TestTagBoth_WhereClauseComposition_StructConformer_RoundTrips()
    {
        using var processor = new CompositionItemProcessor(prefix: "P");
        using var item = new SimpleItem(id: "node-7", label: "alpha");
        // Swift: "\(prefix)#\(tag): \(item.describe())".
        var result = processor.TagBoth(item, tag: 7);
        AssertEqual("P#7: [node-7] alpha", result, "tagBoth<SimpleItem>");
    }

    public void TestTagBoth_WhereClauseComposition_ClassConformer_RoundTrips()
    {
        using var processor = new CompositionItemProcessor(prefix: "P");
        using var entity = new MultiProtocolEntity(id: "ent-3", name: "beta");
        var result = processor.TagBoth(entity, tag: 3);
        AssertEqual("P#3: [ent-3] beta", result, "tagBoth<MultiProtocolEntity>");
    }
}
