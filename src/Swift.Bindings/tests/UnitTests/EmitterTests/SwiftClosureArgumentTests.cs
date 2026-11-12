// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// The argument every emitter passes for a closure-typed parameter of the API it wraps. The emitter
/// sites themselves are pinned by their own tests; these pin what the shared rendering produces.
/// </summary>
public class SwiftClosureArgumentTests
{
    private static ClosureTypeSpec Autoclosure(bool throws = false, bool isAsync = false)
    {
        var spec = new ClosureTypeSpec(TupleTypeSpec.Empty, new NamedTypeSpec("Swift.Bool"))
        {
            Throws = throws,
            IsAsync = isAsync,
        };
        spec.Attributes.Add(new TypeSpecAttribute("autoclosure"));
        return spec;
    }

    [Fact]
    public void OrdinaryClosureParameter_TakesTheClosureItself()
    {
        var function = new ClosureTypeSpec(TupleTypeSpec.Empty, new NamedTypeSpec("Swift.Bool"));

        Assert.Equal("adapter", SwiftClosureArgument.Render("adapter", function));
        Assert.Equal(("", ""), SwiftClosureArgument.Invocation(function));
    }

    [Fact]
    public void NonClosureParameter_IsUnchanged()
    {
        Assert.Equal("count", SwiftClosureArgument.Render("count", new NamedTypeSpec("Swift.Int")));
        Assert.Equal("count", SwiftClosureArgument.Render("count", null));
    }

    [Theory]
    [InlineData(false, false, "adapter()")]
    [InlineData(true, false, "try adapter()")]
    [InlineData(false, true, "await adapter()")]
    [InlineData(true, true, "try await adapter()")]
    public void AutoclosureParameter_TakesTheClosuresValueWithItsEffects(bool throws, bool isAsync, string expected)
    {
        // Swift checks the autoclosure's effects where the expression is written, so a throwing or
        // async autoclosure's value needs its own `try` / `await`.
        Assert.Equal(expected, SwiftClosureArgument.Render("adapter", Autoclosure(throws, isAsync)));
    }

    [Fact]
    public void AutoclosureLiteral_IsInvokedInPlace()
    {
        var (before, after) = SwiftClosureArgument.Invocation(Autoclosure(throws: true));

        Assert.Equal("try { () throws -> Swift.Bool in true }()", $"{before}{{ () throws -> Swift.Bool in true }}{after}");
    }

    [Fact]
    public void ShimRedeclaringTheAutoclosureAsAFunction_TakesTheClosureItself()
    {
        Assert.Equal("adapter", SwiftClosureArgument.Render("adapter", Autoclosure(), calleeTakesAFunction: true));
    }
}
