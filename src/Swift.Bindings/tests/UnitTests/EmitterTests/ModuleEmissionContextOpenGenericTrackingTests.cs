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
    public void RecordPayloadSemantics_NestedInsideOpenOuter_RegistersUnboundForm()
    {
        // The closed name would reference the outer's type parameter, which is not in scope in the
        // module initializer, but the unbound form names the generic type definition that every
        // closed instantiation resolves through. Unregistered, the type falls to a reflection
        // backstop NativeAOT cannot satisfy for a nested type.
        var ctx = new ModuleEmissionContext();
        ctx.PushTypeNesting("Outcome<TSigned>");
        ctx.RecordPayloadSemantics("Failure", Swift.Runtime.PayloadConstructionSemantics.Adopt);
        ctx.PopTypeNesting();

        Assert.Equal(("Outcome<>.Failure", Swift.Runtime.PayloadConstructionSemantics.Adopt), Assert.Single(ctx.PayloadSemantics));
    }

    [Fact]
    public void RecordOpenGenericPayloadSemantics_NestedInsideOpenOuter_OpensEveryGenericPart()
    {
        // C# rejects a partially-unbound name such as Outer<T, U>.Pair<,>, so the ancestors are
        // opened alongside the leaf, and a non-generic intermediate ancestor keeps its plain name.
        // The Swift arity counts the two inherited parameters as well as Pair's own two.
        var ctx = new ModuleEmissionContext();
        ctx.PushTypeNesting("Outer<T, U>");
        ctx.PushTypeNesting("Middle");
        ctx.RecordOpenGenericPayloadSemantics("Pair", arity: 4, Swift.Runtime.PayloadConstructionSemantics.Copy);
        ctx.PopTypeNesting();
        ctx.PopTypeNesting();

        Assert.Equal(("Outer<,>.Middle.Pair<,>", Swift.Runtime.PayloadConstructionSemantics.Copy), Assert.Single(ctx.PayloadSemantics));
    }

    [Fact]
    public void RecordOpenGenericPayloadSemantics_NestedTypeWithOnlyInheritedParameters_IsNotGenericInCSharp()
    {
        // Swift counts a nested type's inherited outer parameters as its own, so the handler reports
        // arity 1 for Outcome<T>.Failure. C# declares Failure with no parameters of its own, and
        // naming it Failure<> is CS0308.
        var ctx = new ModuleEmissionContext();
        ctx.PushTypeNesting("Outcome<TSigned>");
        ctx.RecordOpenGenericPayloadSemantics("Failure", arity: 1, Swift.Runtime.PayloadConstructionSemantics.Adopt);
        ctx.PopTypeNesting();

        Assert.Equal(("Outcome<>.Failure", Swift.Runtime.PayloadConstructionSemantics.Adopt), Assert.Single(ctx.PayloadSemantics));
    }

    [Fact]
    public void RecordPayloadSemantics_NestedInsideClosedOuter_KeepsQualifiedName()
    {
        var ctx = new ModuleEmissionContext();
        ctx.PushTypeNesting("Outer");
        ctx.RecordPayloadSemantics("Inner", Swift.Runtime.PayloadConstructionSemantics.Move);
        ctx.PopTypeNesting();

        Assert.Equal(("Outer.Inner", Swift.Runtime.PayloadConstructionSemantics.Move), Assert.Single(ctx.PayloadSemantics));
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
