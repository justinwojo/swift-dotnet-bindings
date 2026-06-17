// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.Linq;
using BindingsGeneration.StdlibConformances;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Unit coverage for the pure prune step of <c>--regen-stdlib-conformances</c>. Drives
/// <see cref="StdlibConformancesRegenCommand.PruneAgainstDump"/> with synthetic
/// swift-api-digester-shaped JSON so the behaviour is verified without a live Apple SDK.
/// </summary>
public class StdlibConformancesRegenCommandTests
{
    // Builds a minimal `swift-api-digester -dump-sdk` shaped dump: ABIRoot.children of
    // TypeDecl nodes, each carrying a conformances array of Conformance nodes.
    private static string Dump(params (string Name, string Module, string[] Protocols)[] types)
    {
        var nodes = types.Select(t =>
        {
            var confs = string.Join(",", t.Protocols.Select(p =>
                $"{{\"kind\":\"Conformance\",\"name\":\"{p}\"}}"));
            return $"{{\"kind\":\"TypeDecl\",\"name\":\"{t.Name}\",\"moduleName\":\"{t.Module}\"," +
                   $"\"conformances\":[{confs}]}}";
        });
        return $"{{\"ABIRoot\":{{\"kind\":\"Root\",\"children\":[{string.Join(",", nodes)}]}}}}";
    }

    private static StdlibConformanceTable Table(params (string Type, string[] Protocols)[] entries)
    {
        var table = new StdlibConformanceTable { SchemaVersion = 1, Comment = "test" };
        foreach (var (type, protocols) in entries)
            table.Conformances[type] = protocols.ToList();
        return table;
    }

    [Fact]
    public void PruneAgainstDump_DropsCuratedProtocolNotDeclaredLive()
    {
        // "Swift.String : Swift.RangeExpression" is the real hand-curation error this command
        // exists to catch: String is not a RangeExpression, and the live stdlib never declares it.
        var table = Table(("Swift.String", new[] { "Swift.Comparable", "Swift.RangeExpression", "Swift.Hashable" }));
        var dump = Dump(("String", "Swift", new[] { "Comparable", "Hashable", "Equatable" }));

        var result = StdlibConformancesRegenCommand.PruneAgainstDump(table, dump);

        Assert.Equal(new[] { "Swift.Comparable", "Swift.Hashable" }, table.Conformances["Swift.String"]);
        Assert.Contains("Swift.String : Swift.RangeExpression", result.DroppedPairs);
        Assert.Single(result.DroppedPairs);
        Assert.Empty(result.TypesNotInDump);
    }

    [Fact]
    public void PruneAgainstDump_KeepsAllWhenAllDeclared_NoDriftNoReorder()
    {
        // Every curated protocol is declared live (plus extras the curated set deliberately
        // omits — the command must NOT add them). A clean table is a byte-stable round-trip.
        var ordered = new[] { "Swift.Comparable", "Swift.Equatable", "Swift.Hashable" };
        var table = Table(("Swift.Int", ordered));
        var dump = Dump(("Int", "Swift", new[] { "Hashable", "Comparable", "Equatable", "Decodable", "Sendable" }));

        var result = StdlibConformancesRegenCommand.PruneAgainstDump(table, dump);

        Assert.Empty(result.DroppedPairs);
        Assert.Equal(ordered, table.Conformances["Swift.Int"]); // order preserved, nothing widened
    }

    [Fact]
    public void PruneAgainstDump_TypeAbsentFromDump_LeftUntouchedAndReported()
    {
        // A wrong/empty dump must NOT nuke the table: an unrecognised type keeps its entries.
        var table = Table(("Swift.Mystery", new[] { "Swift.Equatable" }));
        var dump = Dump(("Int", "Swift", new[] { "Equatable" }));

        var result = StdlibConformancesRegenCommand.PruneAgainstDump(table, dump);

        Assert.Equal(new[] { "Swift.Equatable" }, table.Conformances["Swift.Mystery"]);
        Assert.Empty(result.DroppedPairs);
        Assert.Contains("Swift.Mystery", result.TypesNotInDump);
    }

    [Fact]
    public void PruneAgainstDump_UnionsConformancesAcrossSameNameNodes()
    {
        // Conformances split across extensions surface as multiple same-name TypeDecl nodes;
        // a curated protocol declared in the second node must be kept.
        var table = Table(("Swift.Int", new[] { "Swift.BinaryInteger", "Swift.Strideable" }));
        var dump =
            "{\"ABIRoot\":{\"children\":[" +
            "{\"kind\":\"TypeDecl\",\"name\":\"Int\",\"moduleName\":\"Swift\"," +
            "\"conformances\":[{\"kind\":\"Conformance\",\"name\":\"BinaryInteger\"}]}," +
            "{\"kind\":\"TypeDecl\",\"name\":\"Int\",\"moduleName\":\"Swift\"," +
            "\"conformances\":[{\"kind\":\"Conformance\",\"name\":\"Strideable\"}]}" +
            "]}}";

        var result = StdlibConformancesRegenCommand.PruneAgainstDump(table, dump);

        Assert.Empty(result.DroppedPairs);
        Assert.Equal(new[] { "Swift.BinaryInteger", "Swift.Strideable" }, table.Conformances["Swift.Int"]);
    }

    [Fact]
    public void PruneAgainstDump_IgnoresNonSwiftModuleAndNonConformanceNodes()
    {
        // A same-named type from another module must not satisfy a Swift conformance; non-
        // Conformance child nodes (e.g. TypeWitness) must not be read as protocols.
        var table = Table(("Swift.Int", new[] { "Swift.Equatable" }));
        var dump =
            "{\"ABIRoot\":{\"children\":[" +
            "{\"kind\":\"TypeDecl\",\"name\":\"Int\",\"moduleName\":\"OtherModule\"," +
            "\"conformances\":[{\"kind\":\"Conformance\",\"name\":\"Equatable\"}]}," +
            "{\"kind\":\"TypeDecl\",\"name\":\"Int\",\"moduleName\":\"Swift\"," +
            "\"conformances\":[{\"kind\":\"TypeWitness\",\"name\":\"Equatable\"}]}" +
            "]}}";

        var result = StdlibConformancesRegenCommand.PruneAgainstDump(table, dump);

        // The Swift-module Int declares no Conformance node, so Equatable is pruned.
        Assert.Contains("Swift.Int : Swift.Equatable", result.DroppedPairs);
        Assert.Empty(table.Conformances["Swift.Int"]);
    }
}
