// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for ConformanceGraph CRUD operations and resolution logic.
/// </summary>
public class ConformanceGraphTests
{
    [Fact]
    public void AddWitness_AndResolve_ReturnsCorrectType()
    {
        var graph = new ConformanceGraph();
        var resolvedType = new NamedTypeSpec("GRDB.Statement");

        graph.AddWitness("GRDB.SQLStatementCursor", "GRDB.Cursor", "Element", resolvedType);

        Assert.True(graph.TryResolve("GRDB.SQLStatementCursor", "GRDB.Cursor", "Element", out var result));
        Assert.Equal("GRDB.Statement", result!.ToString());
    }

    [Fact]
    public void TryResolve_MissingEntry_ReturnsFalse()
    {
        var graph = new ConformanceGraph();

        Assert.False(graph.TryResolve("GRDB.SQLStatementCursor", "GRDB.Cursor", "Element", out var result));
        Assert.Null(result);
    }

    [Fact]
    public void Count_ReflectsAddedEntries()
    {
        var graph = new ConformanceGraph();
        Assert.Equal(0, graph.Count);

        graph.AddWitness("GRDB.SQLStatementCursor", "GRDB.Cursor", "Element", new NamedTypeSpec("GRDB.Statement"));
        Assert.Equal(1, graph.Count);

        graph.AddWitness("GRDB.RowCursor", "GRDB.Cursor", "Element", new NamedTypeSpec("GRDB.Row"));
        Assert.Equal(2, graph.Count);
    }

    [Fact]
    public void AddWitness_OverwritesPreviousEntry()
    {
        var graph = new ConformanceGraph();

        graph.AddWitness("MyModule.MyType", "MyModule.MyProtocol", "Element", new NamedTypeSpec("Swift.Int"));
        graph.AddWitness("MyModule.MyType", "MyModule.MyProtocol", "Element", new NamedTypeSpec("Swift.String"));

        Assert.True(graph.TryResolve("MyModule.MyType", "MyModule.MyProtocol", "Element", out var result));
        Assert.Equal("Swift.String", result!.ToString());
        Assert.Equal(1, graph.Count); // Overwritten, not duplicated
    }

    [Fact]
    public void Ambiguity_SameTypeTwoProtocols_CorrectPerProtocolResolution()
    {
        // A type can conform to two protocols that both define "Element"
        // but map it to different concrete types
        var graph = new ConformanceGraph();

        graph.AddWitness("MyModule.MyCollection", "Swift.Sequence", "Element", new NamedTypeSpec("Swift.Int"));
        graph.AddWitness("MyModule.MyCollection", "MyModule.Container", "Element", new NamedTypeSpec("Swift.String"));

        Assert.True(graph.TryResolve("MyModule.MyCollection", "Swift.Sequence", "Element", out var seqResult));
        Assert.Equal("Swift.Int", seqResult!.ToString());

        Assert.True(graph.TryResolve("MyModule.MyCollection", "MyModule.Container", "Element", out var containerResult));
        Assert.Equal("Swift.String", containerResult!.ToString());
    }

    [Fact]
    public void GenericForwarding_ReturnsGenericParam()
    {
        // RecordCursor<Record> : Cursor → Element = τ_0_0
        var graph = new ConformanceGraph();
        var genericParam = new NamedTypeSpec("τ_0_0");

        graph.AddWitness("GRDB.RecordCursor", "GRDB.Cursor", "Element", genericParam);

        Assert.True(graph.TryResolve("GRDB.RecordCursor", "GRDB.Cursor", "Element", out var result));
        Assert.Equal("τ_0_0", result!.ToString());
    }

    [Fact]
    public void ChainedReference_ReturnsAssociatedTypeReferenceSpec()
    {
        // Chained: Fetcher → τ_0_0.Fetcher (an AssociatedTypeReferenceSpec)
        var graph = new ConformanceGraph();
        var chained = new AssociatedTypeReferenceSpec("τ_0_0", "Fetcher");

        graph.AddWitness("GRDB.SomeType", "GRDB.SomeProtocol", "Fetcher", chained);

        Assert.True(graph.TryResolve("GRDB.SomeType", "GRDB.SomeProtocol", "Fetcher", out var result));
        Assert.IsType<AssociatedTypeReferenceSpec>(result);
        Assert.Equal("τ_0_0.Fetcher", result!.ToString());
    }
}
