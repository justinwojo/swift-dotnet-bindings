// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.Linq;
using BindingsGeneration.StdlibConformances;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Unit coverage for the pure reconcile step of <c>--regen-stdlib-conformances</c>. Drives
/// <see cref="StdlibConformancesRegenCommand.ReconcileAgainstDump"/> with synthetic
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

    // A table whose protocol axis is exactly the protocols its entries already list, so the
    // complete step has nothing to add and only the prune step is under test.
    private static StdlibConformanceTable Table(params (string Type, string[] Protocols)[] entries)
        => Matrix(entries.SelectMany(e => e.Protocols).Distinct().ToArray(), entries);

    private static StdlibConformanceTable Matrix(string[] axis, params (string Type, string[] Protocols)[] entries)
    {
        var table = new StdlibConformanceTable { SchemaVersion = 2, Comment = "test", Protocols = axis.ToList() };
        foreach (var (type, protocols) in entries)
            table.Conformances[type] = protocols.ToList();
        return table;
    }

    [Fact]
    public void ReconcileAgainstDump_DropsCuratedProtocolNotDeclaredLive()
    {
        // "Swift.String : Swift.RangeExpression" is the real hand-curation error this command
        // exists to catch: String is not a RangeExpression, and the live stdlib never declares it.
        var table = Table(("Swift.String", new[] { "Swift.Comparable", "Swift.RangeExpression", "Swift.Hashable" }));
        var dump = Dump(("String", "Swift", new[] { "Comparable", "Hashable", "Equatable" }));

        var result = StdlibConformancesRegenCommand.ReconcileAgainstDump(table, dump);

        Assert.Equal(new[] { "Swift.Comparable", "Swift.Hashable" }, table.Conformances["Swift.String"]);
        Assert.Contains("Swift.String : Swift.RangeExpression", result.DroppedPairs);
        Assert.Single(result.DroppedPairs);
        Assert.Empty(result.TypesNotInDump);
    }

    [Fact]
    public void ReconcileAgainstDump_DropsListedProtocolOffTheAxis_EvenWhenDeclaredLive()
    {
        // The table is the curated matrix and nothing else: a pair whose protocol left the
        // axis is pruned although the stdlib still declares it.
        var table = Matrix(new[] { "Swift.Hashable" }, ("Swift.Int", new[] { "Swift.Equatable", "Swift.Hashable" }));
        var dump = Dump(("Int", "Swift", new[] { "Equatable", "Hashable" }));

        var result = StdlibConformancesRegenCommand.ReconcileAgainstDump(table, dump);

        Assert.Equal(new[] { "Swift.Hashable" }, table.Conformances["Swift.Int"]);
        Assert.Equal(new[] { "Swift.Int : Swift.Equatable" }, result.DroppedPairs);
        Assert.Empty(result.AddedPairs);
    }

    [Fact]
    public void ReconcileAgainstDump_KeepsAllWhenAllDeclared_NoDriftNoReorder()
    {
        // Every curated protocol is declared live (plus extras off the protocol axis — the
        // command must NOT add them). A clean table is a byte-stable round-trip.
        var ordered = new[] { "Swift.Comparable", "Swift.Equatable", "Swift.Hashable" };
        var table = Table(("Swift.Int", ordered));
        var dump = Dump(("Int", "Swift", new[] { "Hashable", "Comparable", "Equatable", "Decodable", "Sendable" }));

        var result = StdlibConformancesRegenCommand.ReconcileAgainstDump(table, dump);

        Assert.Empty(result.DroppedPairs);
        Assert.Empty(result.AddedPairs);
        Assert.Equal(ordered, table.Conformances["Swift.Int"]); // order preserved, nothing widened
    }

    [Fact]
    public void ReconcileAgainstDump_TypeAbsentFromDump_LeftUntouchedAndReported()
    {
        // A wrong/empty dump must NOT nuke the table: an unrecognised type keeps its entries.
        var table = Table(("Swift.Mystery", new[] { "Swift.Equatable" }));
        var dump = Dump(("Int", "Swift", new[] { "Equatable" }));

        var result = StdlibConformancesRegenCommand.ReconcileAgainstDump(table, dump);

        Assert.Equal(new[] { "Swift.Equatable" }, table.Conformances["Swift.Mystery"]);
        Assert.Empty(result.DroppedPairs);
        Assert.Contains("Swift.Mystery", result.TypesNotInDump);
    }

    [Fact]
    public void ReconcileAgainstDump_UnionsConformancesAcrossSameNameNodes()
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

        var result = StdlibConformancesRegenCommand.ReconcileAgainstDump(table, dump);

        Assert.Empty(result.DroppedPairs);
        Assert.Equal(new[] { "Swift.BinaryInteger", "Swift.Strideable" }, table.Conformances["Swift.Int"]);
    }

    [Fact]
    public void ReconcileAgainstDump_IgnoresNonSwiftModuleAndNonConformanceNodes()
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

        var result = StdlibConformancesRegenCommand.ReconcileAgainstDump(table, dump);

        // The Swift-module Int declares no Conformance node, so Equatable is pruned.
        Assert.Contains("Swift.Int : Swift.Equatable", result.DroppedPairs);
        Assert.Empty(table.Conformances["Swift.Int"]);
    }

    [Fact]
    public void ReconcileAgainstDump_CompletesAxisProtocolDeclaredLiveButMissing()
    {
        // The gap this step exists to close: Decodable was curated (on the axis, listed for Int)
        // but missing for Float, so every `T: Codable` constraint bound to Float went unproven.
        var axis = new[] { "Swift.Equatable", "Swift.Decodable", "Swift.Encodable" };
        var table = Matrix(axis,
            ("Swift.Int", new[] { "Swift.Equatable", "Swift.Decodable", "Swift.Encodable" }),
            ("Swift.Float", new[] { "Swift.Equatable" }),
            ("Swift.Character", new string[0]));
        var dump = Dump(
            ("Int", "Swift", new[] { "Equatable", "Decodable", "Encodable" }),
            ("Float", "Swift", new[] { "Equatable", "Decodable", "Encodable", "BinaryFloatingPoint" }),
            ("Character", "Swift", new[] { "Equatable", "Hashable" }));

        var result = StdlibConformancesRegenCommand.ReconcileAgainstDump(table, dump);

        Assert.Equal(new[] { "Swift.Equatable", "Swift.Decodable", "Swift.Encodable" }, table.Conformances["Swift.Float"]);
        // Character is not Codable: only the axis protocols it really declares are completed,
        // and Hashable (declared, but off the axis) is not.
        Assert.Equal(new[] { "Swift.Equatable" }, table.Conformances["Swift.Character"]);
        Assert.Equal(
            new[] { "Swift.Float : Swift.Decodable", "Swift.Float : Swift.Encodable", "Swift.Character : Swift.Equatable" },
            result.AddedPairs);
        Assert.Empty(result.DroppedPairs);
    }

    [Fact]
    public void ReconcileAgainstDump_PrunesAndCompletesInOnePass_ThenIsStable()
    {
        var axis = new[] { "Swift.Comparable", "Swift.Hashable" };
        var table = Matrix(axis, ("Swift.Bool", new[] { "Swift.Comparable" }));
        var dump = Dump(("Bool", "Swift", new[] { "Hashable", "Equatable" }));

        var first = StdlibConformancesRegenCommand.ReconcileAgainstDump(table, dump);
        Assert.Equal(new[] { "Swift.Bool : Swift.Comparable" }, first.DroppedPairs);
        Assert.Equal(new[] { "Swift.Bool : Swift.Hashable" }, first.AddedPairs);
        Assert.Equal(new[] { "Swift.Hashable" }, table.Conformances["Swift.Bool"]);

        var second = StdlibConformancesRegenCommand.ReconcileAgainstDump(table, dump);
        Assert.Empty(second.DroppedPairs);
        Assert.Empty(second.AddedPairs);
    }

    [Fact]
    public void ReconcileAgainstDump_TypeAbsentFromDump_IsNotCompleted()
    {
        // A wrong dump must not fill a type it does not describe either.
        var table = Matrix(new[] { "Swift.Equatable" }, ("Swift.Mystery", new string[0]));
        var dump = Dump(("Int", "Swift", new[] { "Equatable" }));

        var result = StdlibConformancesRegenCommand.ReconcileAgainstDump(table, dump);

        Assert.Empty(table.Conformances["Swift.Mystery"]);
        Assert.Empty(result.AddedPairs);
        Assert.Contains("Swift.Mystery", result.TypesNotInDump);
    }

    [Fact]
    public void CommittedTable_IsAClosedMatrix_EveryEntryOnTheAxisAndEveryScalarCodable()
    {
        // The committed table must be what a reconcile would leave: no entry off the protocol
        // axis, and the Codable pair present for every stdlib scalar that declares it (all but
        // Character). The live reconcile itself needs a toolchain; this pins its output shape.
        using var stream = typeof(ConformanceOracle).Assembly
            .GetManifestResourceStream("Swift.Bindings.Data.stdlib-conformances.json")!;
        using var reader = new System.IO.StreamReader(stream);
        var table = Newtonsoft.Json.JsonConvert.DeserializeObject<StdlibConformanceTable>(reader.ReadToEnd())!;

        Assert.NotEmpty(table.Protocols);
        foreach (var (type, protocols) in table.Conformances)
            foreach (var protocol in protocols)
                Assert.True(table.Protocols.Contains(protocol), $"{type} : {protocol} is not on the protocol axis");

        foreach (var scalar in new[] { "String", "Bool", "Int", "Int8", "Int16", "Int32", "Int64",
                     "UInt", "UInt8", "UInt16", "UInt32", "UInt64", "Float", "Double", "Float16" })
        {
            Assert.Contains("Swift.Decodable", table.Conformances["Swift." + scalar]);
            Assert.Contains("Swift.Encodable", table.Conformances["Swift." + scalar]);
        }
        Assert.DoesNotContain("Swift.Decodable", table.Conformances["Swift.Character"]);
    }
}
