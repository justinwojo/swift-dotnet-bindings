// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Immutable;
using Xunit;

using BindingsGeneration;
using BindingsGeneration.Diagnostics;

namespace BindingsGeneration.Tests;

/// <summary>
/// The block index maps a diagnostic line to the innermost strippable wrapper block that owns it,
/// working straight off the compiled source. These pin it to the real fixture sources plus the
/// nesting and anchor cases the fixtures do not cover.
/// </summary>
public class WrapperBlockIndexTests
{
    [Fact]
    public void Build_FindsEverySymbolBlock_AndResolvesAnInnerLineToIt()
    {
        var index = WrapperBlockIndex.Build(AttributionFixtures.Source("SingleBrokenMember"));

        Assert.True(index.TryResolve(8, out var rotate));
        Assert.Equal("SBW_Gadget_rotate", rotate.Symbol);

        Assert.True(index.TryResolve(14, out var scale));
        Assert.Equal("SBW_Gadget_scale", scale.Symbol);
    }

    [Fact]
    public void TryResolve_LineOutsideAnyBlock_ReturnsFalse()
    {
        var index = WrapperBlockIndex.Build(AttributionFixtures.Source("SingleBrokenMember"));

        // Line 4 is the `import Foundation` prelude — no block encloses it.
        Assert.False(index.TryResolve(4, out _));
    }

    /// <summary>
    /// A symbol-bearing function nested inside a symbol-less <c>// SBW-ORIGIN:</c> extension: a line
    /// inside the function resolves to the function (smaller span), a line in the extension header
    /// resolves to the extension.
    /// </summary>
    [Fact]
    public void TryResolve_NestedBlocks_ReturnsTheInnermost()
    {
        const string source = """
            // SBW-ORIGIN: Fixture||Struct|Widget||None|||/swift-wrapper
            extension Widget {
                @_cdecl("SBW_Widget_spin")
                public func SBW_Widget_spin() {
                    doThing()
                }
            }
            """;

        var index = WrapperBlockIndex.Build(source);

        // Line 5 (doThing) is inside the inner function block.
        Assert.True(index.TryResolve(5, out var inner));
        Assert.Equal("SBW_Widget_spin", inner.Symbol);

        // Line 2 (the extension header) is inside only the outer origin-anchored block.
        Assert.True(index.TryResolve(2, out var outer));
        Assert.Null(outer.Symbol);
        Assert.Equal("Fixture||Struct|Widget||None|||/swift-wrapper", outer.OriginAnchor);
    }

    [Fact]
    public void Build_OriginAnchorBlock_CapturesTheArtifactToken()
    {
        var artifact = AttributionFixtures.ArtifactForSymbol("Helpers");
        var source = $$"""
            // SBW-ORIGIN: {{artifact.Canonical}}
            enum SharedHelpers {
                static let broken: Missing = fail()
            }
            """;

        var index = WrapperBlockIndex.Build(source);

        Assert.True(index.TryResolve(3, out var block));
        Assert.Null(block.Symbol);
        Assert.Equal(artifact.Canonical, block.OriginAnchor);
    }

    /// <summary>
    /// An anchor whose serialized <see cref="ArtifactId"/> embeds a space — a decl canonical carrying a
    /// spaced parameter type (<c>any Sequence</c>) or generic context, which the emitter escapes no
    /// whitespace out of — must be captured whole, not truncated at the first space, so it round-trips
    /// back through <see cref="ArtifactId.TryParse"/> to the owning artifact.
    /// </summary>
    [Fact]
    public void Build_OriginAnchorWithSpacedCanonical_CapturesTheWholeTokenAndRoundTrips()
    {
        var decl = DeclId.Create(
            "Fixture",
            declPath: null,
            BindingItemKind.Method,
            "consume",
            parameterLabels: ImmutableArray.Create("seq"),
            parameterTypes: ImmutableArray.Create("any Sequence"));
        var artifact = ArtifactId.Create(decl, ArtifactRole.SwiftWrapper);
        Assert.Contains(' ', artifact.Canonical); // guard: the canonical really does carry a space

        var source = $$"""
            // SBW-ORIGIN: {{artifact.Canonical}}
            extension Consumer {
                func broken() -> Missing { fail() }
            }
            """;

        var index = WrapperBlockIndex.Build(source);

        Assert.True(index.TryResolve(3, out var block));
        Assert.Equal(artifact.Canonical, block.OriginAnchor);
        Assert.True(ArtifactId.TryParse(block.OriginAnchor, out var parsed));
        Assert.Equal(artifact, parsed);
    }
}
