// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Protocols;

/// <summary>
/// A protocol whose requirements include a subscript must keep its conformance on the concrete
/// types that satisfy it. The emitter writes the indexer onto the concrete type either way; what
/// used to be lost was the `: IIndexedCatalog` on the declaration, so the member was reachable
/// through the class but not through the protocol a consumer holds.
///
/// Keeping the conformance is separate from dispatching through it: a value the consumer converts
/// from a concrete type reads fine, while a Swift-backed existential's indexer remains a declared
/// non-dispatchable member (SB0003) — the last test pins that boundary.
/// </summary>
public class SubscriptRequirementConformanceTests : TestBase
{
    public SubscriptRequirementConformanceTests(TestResults results) : base(results) { }

    public void TestCatalogTable_ReadsThroughInterfaceIndexer()
    {
        using var table = Functions.MakeCatalogTable("alpha", "beta", "gamma");

        // The assignment is the assertion: it does not compile unless the conformance survived.
        IIndexedCatalog view = table;

        AssertEqual(3, view.EntryCount, "class conformer EntryCount");
        AssertEqual("alpha", view[0], "class conformer [0]");
        AssertEqual("gamma", view[2], "class conformer [2]");
    }

    public void TestCatalogRecord_StructConformerReadsThroughInterfaceIndexer()
    {
        using var record = Functions.MakeCatalogRecord("one", "two");

        IIndexedCatalog view = record;

        AssertEqual(2, view.EntryCount, "struct conformer EntryCount");
        AssertEqual("one", view[0], "struct conformer [0]");
        AssertEqual("two", view[1], "struct conformer [1]");
    }

    public void TestCatalogHost_SwiftBackedExistentialIndexerIsDocumentedGap()
    {
        // Pins the documented SB0003 subscript-witness gap: a Swift-side stored property typed as
        // the protocol projects to a Swift-backed existential, and no subscript reverse-dispatches
        // through a witness table today, so the emitter declares the indexer [Obsolete(SB0003)] with
        // a throwing body. Non-subscript requirements on the same existential DO dispatch, which is
        // what separates this gap from a conformance regression.
        using var host = Functions.MakeCatalogHost("first", "second");

        var catalog = host.Catalog;

        AssertEqual(2, catalog.EntryCount, "non-subscript requirement dispatches through the existential");

#pragma warning disable SB0003 // deliberately calling the member the generator marked non-dispatchable
        AssertThrows<NotSupportedException>(() => { var _ = catalog[0]; },
            "subscript through a Swift-backed existential is not dispatchable");
#pragma warning restore SB0003
    }
}
