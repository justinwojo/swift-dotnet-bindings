// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for <see cref="ModuleEmissionContext.RecordOpenGenericISwiftObjectType"/>
/// and <see cref="ModuleEmissionContext.EmittedOpenGenericISwiftObjectTypes"/> —
/// the per-module dictionary that drives <see cref="TrimmerDescriptorEmitter"/>
/// (RC-AOT). The recorder mirrors the well-tested non-generic ISwiftObject
/// recorder's nesting/open-generic-ancestor rules so the descriptor stays in
/// lockstep with what the eager-cctor path actually preserves.
/// </summary>
public class ModuleEmissionContextOpenGenericTrackingTests
{
    [Fact]
    public void RecordOpenGenericISwiftObjectType_TopLevel_StoresArity()
    {
        var ctx = new ModuleEmissionContext();
        ctx.RecordOpenGenericISwiftObjectType("BlittableElementBuffer", arity: 1);
        ctx.RecordOpenGenericISwiftObjectType("Pair", arity: 2);

        var map = ctx.EmittedOpenGenericISwiftObjectTypes;
        Assert.Equal(2, map.Count);
        Assert.Equal(1, map["BlittableElementBuffer"]);
        Assert.Equal(2, map["Pair"]);
    }

    [Fact]
    public void RecordOpenGenericISwiftObjectType_NestedInsideClosedOuter_QualifiesName()
    {
        // Closed-outer nesting is legal: the outer type's static-init context can reach
        // the nested open generic by its fully qualified name. The descriptor must use
        // the dot-joined name so ILC's fullname match resolves to the right metadata.
        var ctx = new ModuleEmissionContext();
        ctx.PushTypeNesting("Outer");
        ctx.RecordOpenGenericISwiftObjectType("Inner", arity: 1);
        ctx.PopTypeNesting();

        var map = ctx.EmittedOpenGenericISwiftObjectTypes;
        Assert.Single(map);
        Assert.Equal(1, map["Outer.Inner"]);
    }

    [Fact]
    public void RecordOpenGenericISwiftObjectType_NestedInsideOpenOuter_Skipped()
    {
        // An open-generic outer (e.g. Container<T>) carries an unbound parameter that is
        // not in scope at module-init time. The eager-cctor path explicitly skips this
        // case via HasOpenGenericAncestor; the descriptor must match — otherwise ILC
        // would chase a metadata token that the runtime can never instantiate without
        // first closing the outer, producing trim warnings and false-positive roots.
        var ctx = new ModuleEmissionContext();
        ctx.PushTypeNesting("Container<T>");
        ctx.RecordOpenGenericISwiftObjectType("Inner", arity: 1);
        ctx.PopTypeNesting();

        Assert.Empty(ctx.EmittedOpenGenericISwiftObjectTypes);
    }

    [Fact]
    public void RecordOpenGenericISwiftObjectType_IsIdempotent()
    {
        // Two emission passes for the same type (handler called twice in different code
        // paths) must not duplicate the entry; the descriptor would otherwise list the
        // same fullname twice, which ILC tolerates but is noisy in source-controlled diffs.
        var ctx = new ModuleEmissionContext();
        ctx.RecordOpenGenericISwiftObjectType("Box", arity: 1);
        ctx.RecordOpenGenericISwiftObjectType("Box", arity: 1);

        var map = ctx.EmittedOpenGenericISwiftObjectTypes;
        Assert.Single(map);
        Assert.Equal(1, map["Box"]);
    }

    [Fact]
    public void RecordOpenGenericISwiftObjectType_RejectsInvalidInputs()
    {
        // Defensive: a handler that loses the type name or computes arity 0 should be a
        // no-op, not a crash and not a malformed descriptor entry.
        var ctx = new ModuleEmissionContext();
        ctx.RecordOpenGenericISwiftObjectType("", arity: 1);
        ctx.RecordOpenGenericISwiftObjectType("Box", arity: 0);
        ctx.RecordOpenGenericISwiftObjectType("Box", arity: -1);

        Assert.Empty(ctx.EmittedOpenGenericISwiftObjectTypes);
    }

    [Fact]
    public void EmittedOpenGenericISwiftObjectTypes_OrdersOrdinally()
    {
        // The descriptor reads this map in iteration order; SortedDictionary with
        // StringComparer.Ordinal pins emit order regardless of recording order, which
        // keeps the generated XML diff-stable across runs.
        var ctx = new ModuleEmissionContext();
        ctx.RecordOpenGenericISwiftObjectType("Zebra", arity: 1);
        ctx.RecordOpenGenericISwiftObjectType("Alpha", arity: 1);
        ctx.RecordOpenGenericISwiftObjectType("Mango", arity: 1);

        var keys = ctx.EmittedOpenGenericISwiftObjectTypes.Keys.ToList();
        Assert.Equal(new[] { "Alpha", "Mango", "Zebra" }, keys);
    }
}
