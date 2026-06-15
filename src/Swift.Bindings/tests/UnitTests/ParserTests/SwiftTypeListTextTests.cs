// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for the consolidated Swift type-list text splitters (Finding 49 grammar centralization).
/// <see cref="SwiftTypeListText.SplitTopLevelParameters"/> is the single implementation the
/// per-class <c>SplitParameters</c> clones delegate to; these tests pin the arrow-guard and
/// bracket/string-literal behavior the weaker clones previously lacked, plus assert the narrow
/// angle-only <see cref="SwiftTypeListText.SplitTopLevelCommas"/> is intentionally left unchanged.
/// </summary>
public class SwiftTypeListTextTests
{
    // --- SplitTopLevelParameters: the robust parameter splitter ---

    [Fact]
    public void SplitTopLevelParameters_ClosureParam_DoesNotMergeTrailingParams()
    {
        // The regression the arrow guard fixes: without it, the '>' in '->' drives depth
        // negative and the trailing parameters get merged into the closure parameter.
        var parts = SwiftTypeListText.SplitTopLevelParameters("value: T, transform: (T) -> U, flag: Bool");
        Assert.Equal(3, parts.Count);
        Assert.Equal("value: T", parts[0].Trim());
        Assert.Equal("transform: (T) -> U", parts[1].Trim());
        Assert.Equal("flag: Bool", parts[2].Trim());
    }

    [Fact]
    public void SplitTopLevelParameters_CommaInsideAngleBrackets_NotSplit()
    {
        var parts = SwiftTypeListText.SplitTopLevelParameters("a: Swift.Dictionary<Swift.String, Swift.Int>, b: Bool");
        Assert.Equal(2, parts.Count);
        Assert.Equal("a: Swift.Dictionary<Swift.String, Swift.Int>", parts[0].Trim());
        Assert.Equal("b: Bool", parts[1].Trim());
    }

    [Fact]
    public void SplitTopLevelParameters_CommaInsideParensAndBrackets_NotSplit()
    {
        var parts = SwiftTypeListText.SplitTopLevelParameters("handler: (Swift.Int, Swift.String) -> Swift.Bool, value: [Swift.Int]");
        Assert.Equal(2, parts.Count);
        Assert.Equal("handler: (Swift.Int, Swift.String) -> Swift.Bool", parts[0].Trim());
        Assert.Equal("value: [Swift.Int]", parts[1].Trim());
    }

    [Fact]
    public void SplitTopLevelParameters_CommaInsideStringLiteralDefault_NotSplit()
    {
        var parts = SwiftTypeListText.SplitTopLevelParameters("a: Swift.String = \"x, y\", b: Swift.Int");
        Assert.Equal(2, parts.Count);
        Assert.Equal("a: Swift.String = \"x, y\"", parts[0].Trim());
        Assert.Equal("b: Swift.Int", parts[1].Trim());
    }

    [Fact]
    public void SplitTopLevelParameters_SingleParam_ReturnsOnePart()
    {
        var parts = SwiftTypeListText.SplitTopLevelParameters("x: Swift.Int");
        Assert.Single(parts);
        Assert.Equal("x: Swift.Int", parts[0].Trim());
    }

    [Fact]
    public void SplitTopLevelParameters_EmptyString_ReturnsSingleEmptyPart()
    {
        var parts = SwiftTypeListText.SplitTopLevelParameters("");
        Assert.Single(parts);
        Assert.Equal("", parts[0]);
    }

    // --- IndexOfTopLevelArrow: guarded return-arrow finder ---

    [Theory]
    [InlineData("(Swift.Int) -> Swift.Bool", 12)] // arrow after the closing paren
    [InlineData("-> Swift.Int", 0)]               // leading return arrow
    [InlineData("async throws -> Swift.Int", 13)] // arrow after effects keywords
    public void IndexOfTopLevelArrow_FindsTopLevelArrow(string input, int expected)
    {
        Assert.Equal(expected, SwiftTypeListText.IndexOfTopLevelArrow(input));
    }

    [Fact]
    public void IndexOfTopLevelArrow_NoArrow_ReturnsMinusOne()
    {
        Assert.Equal(-1, SwiftTypeListText.IndexOfTopLevelArrow("Swift.Int"));
    }

    [Fact]
    public void IndexOfTopLevelArrow_NestedArrowOnly_ReturnsMinusOne()
    {
        // The only "->" is nested inside the generic arguments — not a top-level return arrow.
        Assert.Equal(-1, SwiftTypeListText.IndexOfTopLevelArrow("Swift.Dictionary<Swift.String, () -> Swift.Int>"));
    }

    [Fact]
    public void IndexOfTopLevelArrow_CurriedReturn_FindsFirstTopLevelArrow()
    {
        // For "(A) -> (B) -> C" the first top-level arrow (index 4) is the function's own
        // return arrow; the second arrow belongs to the returned closure type.
        Assert.Equal(4, SwiftTypeListText.IndexOfTopLevelArrow("(A) -> (B) -> C"));
    }

    // --- SplitTopLevelCommas: the narrow angle-only where-clause splitter (intentionally unchanged) ---

    [Fact]
    public void SplitTopLevelCommas_AngleBracketDepth_KeepsConstructedGenericIntact()
    {
        var parts = SwiftTypeListText.SplitTopLevelCommas("KeyPath<Intent, Parameter>, Foo");
        Assert.Equal(2, parts.Count);
        Assert.Equal("KeyPath<Intent, Parameter>", parts[0].Trim());
        Assert.Equal("Foo", parts[1].Trim());
    }

    [Fact]
    public void SplitTopLevelCommas_TracksAnglesOnly_ByDesign()
    {
        // Documents the deliberate scope boundary: the where-clause splitter tracks ONLY angle
        // brackets (constraint lists never carry top-level parens), so it WOULD split commas
        // inside parentheses. That is why parameter lists use SplitTopLevelParameters instead and
        // why the where-clause consumers were intentionally not migrated.
        var parts = SwiftTypeListText.SplitTopLevelCommas("(A, B), C");
        Assert.Equal(3, parts.Count);
    }
}
