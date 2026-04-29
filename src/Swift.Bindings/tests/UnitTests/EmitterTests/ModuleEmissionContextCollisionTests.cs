// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for the module/type-name collision rewriter on <see cref="ModuleEmissionContext"/>.
/// When a Swift module has a public type with the same name as the module itself
/// (e.g. module "Reachability" containing class "Reachability"), bare references like
/// <c>Reachability.X</c> in wrapper source resolve to "X nested inside class Reachability"
/// rather than to the module-level X. The emission-time fix strips the prefix when the
/// next segment is NOT a real nested member of the colliding class.
/// </summary>
public class ModuleEmissionContextCollisionTests
{
    [Fact]
    public void NoCollisionContext_LeavesNamesUnchanged()
    {
        var ctx = new ModuleEmissionContext();
        Assert.Equal("Reachability.Connection", ctx.QualifyForWrapperSource("Reachability.Connection"));
    }

    [Fact]
    public void Collision_StripsTopLevelTypePrefix()
    {
        var ctx = new ModuleEmissionContext();
        ctx.SetCollisionContext("Reachability", nestedTypesInCollidingClass: null);

        Assert.Equal("Connection", ctx.QualifyForWrapperSource("Reachability.Connection"));
        Assert.Equal("Foo.Bar", ctx.QualifyForWrapperSource("Reachability.Foo.Bar"));
    }

    [Fact]
    public void Collision_PreservesPrefixForNestedTypeInCollidingClass()
    {
        // SwiftyBeaver: class SwiftyBeaver contains a nested enum Level. References to
        // "SwiftyBeaver.Level" in the wrapper are legitimately reaching the class-nested
        // type, so the prefix must NOT be stripped.
        var ctx = new ModuleEmissionContext();
        var nested = new HashSet<string>(StringComparer.Ordinal) { "Level" };
        ctx.SetCollisionContext("SwiftyBeaver", nested);

        Assert.Equal("SwiftyBeaver.Level", ctx.QualifyForWrapperSource("SwiftyBeaver.Level"));
        Assert.Equal("SwiftyBeaver.Level.verbose", ctx.QualifyForWrapperSource("SwiftyBeaver.Level.verbose"));
        // Non-nested top-level module type still gets stripped.
        Assert.Equal("Destination", ctx.QualifyForWrapperSource("SwiftyBeaver.Destination"));
    }

    [Fact]
    public void Collision_RewritesEmbeddedReferences()
    {
        // Pattern 5 ran line-by-line over the wrapper file, so embedded references inside
        // a type spec like Optional<Reachability.Foo> were also stripped. The emission-time
        // helper must mirror this regex behaviour.
        var ctx = new ModuleEmissionContext();
        ctx.SetCollisionContext("Reachability", nestedTypesInCollidingClass: null);

        Assert.Equal("Optional<Connection>", ctx.QualifyForWrapperSource("Optional<Reachability.Connection>"));
        Assert.Equal("(Int, Connection)", ctx.QualifyForWrapperSource("(Int, Reachability.Connection)"));
        Assert.Equal("Array<Optional<Connection>>",
            ctx.QualifyForWrapperSource("Array<Optional<Reachability.Connection>>"));
    }

    [Fact]
    public void Collision_DoesNotMatchPartialIdentifier()
    {
        // Word-boundary anchored: "ReachabilityKit" must NOT match because the regex starts
        // with \b<Module>\. — "Kit" is part of a larger identifier.
        var ctx = new ModuleEmissionContext();
        ctx.SetCollisionContext("Reachability", nestedTypesInCollidingClass: null);

        Assert.Equal("ReachabilityKit.Foo", ctx.QualifyForWrapperSource("ReachabilityKit.Foo"));
        Assert.Equal("MyReachability.Foo", ctx.QualifyForWrapperSource("MyReachability.Foo"));
    }

    [Fact]
    public void Collision_EmptyOrNullInputsAreSafe()
    {
        var ctx = new ModuleEmissionContext();
        ctx.SetCollisionContext("Reachability", nestedTypesInCollidingClass: null);

        Assert.Equal("", ctx.QualifyForWrapperSource(""));
    }

    [Fact]
    public void Collision_RoundTripsThroughSetCollisionContext()
    {
        // Re-setting collision context to null clears the rewriter so the helper goes back
        // to passthrough behaviour. Confirms there's no leftover regex state.
        var ctx = new ModuleEmissionContext();
        ctx.SetCollisionContext("Reachability", nestedTypesInCollidingClass: null);
        Assert.Equal("Connection", ctx.QualifyForWrapperSource("Reachability.Connection"));

        ctx.SetCollisionContext(null, nestedTypesInCollidingClass: null);
        Assert.Equal("Reachability.Connection", ctx.QualifyForWrapperSource("Reachability.Connection"));
    }
}
