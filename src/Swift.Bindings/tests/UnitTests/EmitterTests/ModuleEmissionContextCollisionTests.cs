// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for the module/type-name collision rewriter on <see cref="ModuleEmissionContext"/>.
/// When a Swift module has a public type with the same name as the module itself
/// (e.g. module "Foo" containing class "Foo"), bare references like
/// <c>Foo.X</c> in wrapper source resolve to "X nested inside class Foo"
/// rather than to the module-level X. The emission-time fix strips the prefix when the
/// next segment is NOT a real nested member of the colliding class.
/// </summary>
public class ModuleEmissionContextCollisionTests
{
    [Fact]
    public void NoCollisionContext_LeavesNamesUnchanged()
    {
        var ctx = new ModuleEmissionContext();
        Assert.Equal("NetworkMonitor.Connection", ctx.QualifyForWrapperSource("NetworkMonitor.Connection"));
    }

    [Fact]
    public void Collision_StripsTopLevelTypePrefix()
    {
        var ctx = new ModuleEmissionContext();
        ctx.SetCollisionContext("NetworkMonitor", nestedTypesInCollidingClass: null);

        Assert.Equal("Connection", ctx.QualifyForWrapperSource("NetworkMonitor.Connection"));
        Assert.Equal("Foo.Bar", ctx.QualifyForWrapperSource("NetworkMonitor.Foo.Bar"));
    }

    [Fact]
    public void Collision_PreservesPrefixForNestedTypeInCollidingClass()
    {
        // LoggingLib: class LoggingLib contains a nested enum Level. References to
        // "LoggingLib.Level" in the wrapper are legitimately reaching the class-nested
        // type, so the prefix must NOT be stripped.
        var ctx = new ModuleEmissionContext();
        var nested = new HashSet<string>(StringComparer.Ordinal) { "Level" };
        ctx.SetCollisionContext("LoggingLib", nested);

        Assert.Equal("LoggingLib.Level", ctx.QualifyForWrapperSource("LoggingLib.Level"));
        Assert.Equal("LoggingLib.Level.verbose", ctx.QualifyForWrapperSource("LoggingLib.Level.verbose"));
        // Non-nested top-level module type still gets stripped.
        Assert.Equal("Destination", ctx.QualifyForWrapperSource("LoggingLib.Destination"));
    }

    [Fact]
    public void Collision_RewritesEmbeddedReferences()
    {
        // Pattern 5 ran line-by-line over the wrapper file, so embedded module-qualified
        // references inside type specs were also stripped. The emission-time helper must
        // mirror this regex behaviour.
        var ctx = new ModuleEmissionContext();
        ctx.SetCollisionContext("NetworkMonitor", nestedTypesInCollidingClass: null);

        Assert.Equal("Optional<Connection>", ctx.QualifyForWrapperSource("Optional<NetworkMonitor.Connection>"));
        Assert.Equal("(Int, Connection)", ctx.QualifyForWrapperSource("(Int, NetworkMonitor.Connection)"));
        Assert.Equal("Array<Optional<Connection>>",
            ctx.QualifyForWrapperSource("Array<Optional<NetworkMonitor.Connection>>"));
    }

    [Fact]
    public void Collision_DoesNotMatchPartialIdentifier()
    {
        // Word-boundary anchored: a module name that is a prefix of a longer identifier
        // (e.g. "Foo" inside "FooKit") must NOT match — "Kit" is part of a larger identifier.
        var ctx = new ModuleEmissionContext();
        ctx.SetCollisionContext("NetworkMonitor", nestedTypesInCollidingClass: null);

        Assert.Equal("NetworkMonitorKit.Foo", ctx.QualifyForWrapperSource("NetworkMonitorKit.Foo"));
        Assert.Equal("MyNetworkMonitor.Foo", ctx.QualifyForWrapperSource("MyNetworkMonitor.Foo"));
    }

    [Fact]
    public void Collision_EmptyOrNullInputsAreSafe()
    {
        var ctx = new ModuleEmissionContext();
        ctx.SetCollisionContext("NetworkMonitor", nestedTypesInCollidingClass: null);

        Assert.Equal("", ctx.QualifyForWrapperSource(""));
    }

    [Fact]
    public void Collision_RoundTripsThroughSetCollisionContext()
    {
        // Re-setting collision context to null clears the rewriter so the helper goes back
        // to passthrough behaviour. Confirms there's no leftover regex state.
        var ctx = new ModuleEmissionContext();
        ctx.SetCollisionContext("NetworkMonitor", nestedTypesInCollidingClass: null);
        Assert.Equal("Connection", ctx.QualifyForWrapperSource("NetworkMonitor.Connection"));

        ctx.SetCollisionContext(null, nestedTypesInCollidingClass: null);
        Assert.Equal("NetworkMonitor.Connection", ctx.QualifyForWrapperSource("NetworkMonitor.Connection"));
    }

    [Fact]
    public void Collision_ScopedImportSurvivesWhileTopLevelReferenceIsStripped()
    {
        // Scoped imports (`import class Foo.Bar`) are the one place a "Foo." prefix must
        // survive: they are not type references to rewrite but the declarations that make
        // the stripped bare names resolve. Stripping them to `import class Bar` is a
        // syntax error that fails the whole wrapper.
        var ctx = new ModuleEmissionContext();
        ctx.SetCollisionContext("Foo", nestedTypesInCollidingClass: null);

        Assert.Equal("import class Foo.Bar", ctx.QualifyForWrapperSource("import class Foo.Bar"));
        Assert.Equal("Baz", ctx.QualifyForWrapperSource("Foo.Baz"));
    }

    [Fact]
    public void Collision_ScopedImportSurvivesForAllKindKeywords()
    {
        var ctx = new ModuleEmissionContext();
        ctx.SetCollisionContext("Foo", nestedTypesInCollidingClass: null);

        Assert.Equal("import class Foo.Bar", ctx.QualifyForWrapperSource("import class Foo.Bar"));
        Assert.Equal("import struct Foo.S", ctx.QualifyForWrapperSource("import struct Foo.S"));
        Assert.Equal("import enum Foo.E", ctx.QualifyForWrapperSource("import enum Foo.E"));
        Assert.Equal("import protocol Foo.P", ctx.QualifyForWrapperSource("import protocol Foo.P"));
        Assert.Equal("import typealias Foo.T", ctx.QualifyForWrapperSource("import typealias Foo.T"));
    }

    [Fact]
    public void Collision_NestedCarveOutStaysQualifiedAlongsideScopedImport()
    {
        // Nested members of the shadowing type keep the Module.Nested spelling (carve-out),
        // while a top-level sibling still strips and a scoped import is left alone.
        var ctx = new ModuleEmissionContext();
        var nested = new HashSet<string>(StringComparer.Ordinal) { "Nested" };
        ctx.SetCollisionContext("Foo", nested);

        Assert.Equal("import class Foo.Bar", ctx.QualifyForWrapperSource("import class Foo.Bar"));
        Assert.Equal("Foo.Nested", ctx.QualifyForWrapperSource("Foo.Nested"));
        Assert.Equal("Foo.Nested.inner", ctx.QualifyForWrapperSource("Foo.Nested.inner"));
        Assert.Equal("Sibling", ctx.QualifyForWrapperSource("Foo.Sibling"));
    }

    [Fact]
    public void Collision_ScopedImportSurvivesInsideMultiLineWrapperSnippet()
    {
        // QualifyForWrapperSource runs over the whole wrapper source string, so scoped
        // imports and stripped type references co-exist in one pass.
        var ctx = new ModuleEmissionContext();
        ctx.SetCollisionContext("Foo", nestedTypesInCollidingClass: null);

        var input =
            "import class Foo.Bar\n" +
            "func f() -> Foo.Baz { Foo.Baz() }\n";
        var output = ctx.QualifyForWrapperSource(input);

        Assert.Contains("import class Foo.Bar", output);
        Assert.Contains("func f() -> Baz { Baz() }", output);
        Assert.DoesNotContain("Foo.Baz", output);
    }
}
