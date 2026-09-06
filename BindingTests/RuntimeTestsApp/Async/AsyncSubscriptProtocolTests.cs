// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Async;

/// <summary>
/// A protocol subscript requirement with a <c>get async</c> getter. The binding compiling at all is
/// the gate here: the conformance validator and the indexer emitter must refuse the same shape, or
/// the conformer declares an interface indexer it never implements. What survives is the class
/// with its ordinary members.
/// </summary>
public class AsyncSubscriptProtocolTests : TestBase
{
    public AsyncSubscriptProtocolTests(TestResults results) : base(results) { }

    public void TestConformerKeepsOrdinaryMembers()
    {
        using var table = new AsyncIndexedTable();

        AssertEqual(3, table.Count, "The ordinary property must survive the dropped subscript");
    }
}
