// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using BindingsGeneration;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Unit tests for <see cref="MemberSignatureNormalizer"/>. The normalization helper
/// is the contract that lets the C# regex parser, the Swift parser, and the ABI
/// parser all converge on the same disambiguation suffix for an overload — every
/// case here MUST collapse to the same string regardless of which parser the
/// input came from. The Swift-side mirror in <c>AvailabilityWalker.normalizeParamType</c>
/// is exercised end-to-end by the BindingTests Layer A fixtures; this class
/// covers the .NET-side normalizer in isolation.
/// </summary>
public class MemberSignatureNormalizerTests
{
    [Theory]
    [InlineData("Foundation.URL", "URL")]
    [InlineData("URL", "URL")]
    [InlineData("Nuke.ImageRequest", "ImageRequest")]
    // Generic args are preserved and recursively normalized so distinct
    // specializations don't collide. Pre-fix, every `Array<T>` reduced to `Array`,
    // collapsing `func f(_ x: Array<Int>)` and `func f(_ x: Array<String>)` to the
    // same disamb signature and reintroducing the Family-F broadcast bug.
    [InlineData("Optional<Int>", "Optional<Int>")]
    [InlineData("Array<Int>", "Array<Int>")]
    [InlineData("Array<String>", "Array<String>")]
    [InlineData("Foundation.Array<Foundation.URL>", "Array<URL>")]
    [InlineData("Dictionary<String, Array<Int>>", "Dictionary<String,Array<Int>>")]
    [InlineData("Set<Int>", "Set<Int>")]
    [InlineData("Array<Int>?", "Array<Int>")]
    [InlineData("Int?", "Int")]
    [InlineData("Int!", "Int")]
    // Collection sugar must fold to the nominal generic form so the
    // swiftinterface side (typically prints `[T]` / `[K: V]`) and the ABI
    // side (which may print either) converge on the same disamb tail.
    [InlineData("[Int]", "Array<Int>")]
    [InlineData("[Swift.Int]", "Array<Int>")]
    [InlineData("[Swift.String: Swift.Int]", "Dictionary<String,Int>")]
    [InlineData("[String: Int]", "Dictionary<String,Int>")]
    [InlineData("[[Int]]", "Array<Array<Int>>")]
    [InlineData("[Int]?", "Array<Int>")]
    [InlineData("Swift.Array<Swift.Int>", "Array<Int>")]
    [InlineData("inout Int", "Int")]
    [InlineData("borrowing Foo", "Foo")]
    [InlineData("consuming Foo", "Foo")]
    [InlineData("some UIScene", "UIScene")]
    [InlineData("any Sendable", "Sendable")]
    [InlineData("__owned Bar", "Bar")]
    [InlineData("__shared Bar", "Bar")]
    [InlineData("UIKit.UIViewController", "UIViewController")]
    [InlineData("`class`", "class")]
    [InlineData("Int = 0", "Int")]
    [InlineData("inout some Foundation.URL", "URL")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    // Variadic ellipsis is stripped because the swiftinterface producer's
    // param.type doesn't include `...` (the ellipsis is a separate token on
    // FunctionParameterSyntax). Without this strip, ABI-side param `printedName`
    // (which DOES include `...`) computes a different disamb suffix and the
    // producer-side annotation lookup misses — masking method-level @available
    // floors like AppShortcutsBuilder.buildBlock(_:)'s iOS 17.4 overload.
    [InlineData("AppShortcut...", "AppShortcut")]
    [InlineData("[AppShortcut]...", "Array<AppShortcut>")]
    [InlineData("AppIntents.AppShortcut...", "AppShortcut")]
    public void NormalizeParamType_CollapsesEquivalentForms(string input, string expected)
    {
        Assert.Equal(expected, MemberSignatureNormalizer.NormalizeParamType(input));
    }

    /// <summary>
    /// Regression: variadic ellipsis in ABI JSON printedNames must produce the
    /// SAME disamb key as the swiftinterface producer (which sees param.type
    /// without `...`). Before this strip, overloads like
    /// <c>buildBlock(AppShortcut...)</c> vs <c>buildBlock([AppShortcut]...)</c>
    /// composed mismatching keys across producer/consumer, the consumer-side
    /// disamb lookup missed, and the producer-side bare key was vacated —
    /// leaving the method with no @available annotations and inheriting only
    /// the parent type's looser floor.
    /// </summary>
    [Fact]
    public void NormalizeParamType_VariadicAndArrayVariadicStayDistinct()
    {
        var a = MemberSignatureNormalizer.NormalizeParamType("AppShortcut...");
        var b = MemberSignatureNormalizer.NormalizeParamType("[AppShortcut]...");
        Assert.NotEqual(a, b);
        Assert.Equal("AppShortcut", a);
        Assert.Equal("Array<AppShortcut>", b);
    }

    /// <summary>
    /// Direct regression test for the Family-F generic-specialized overload bug.
    /// Before the fix, both forms reduced to "Array"
    /// and the disamb logic counted only one distinct signature, causing the
    /// availability annotation to be stored under the bare key and broadcast
    /// across both overloads.
    /// </summary>
    [Fact]
    public void NormalizeParamType_DistinctGenericSpecializations_StayDistinct()
    {
        var a = MemberSignatureNormalizer.NormalizeParamType("Array<Int>");
        var b = MemberSignatureNormalizer.NormalizeParamType("Array<String>");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void BuildSignature_EmptyList_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, MemberSignatureNormalizer.BuildSignature(new string[0]));
    }

    [Fact]
    public void BuildSignature_JoinsNormalizedTailsWithComma()
    {
        var sig = MemberSignatureNormalizer.BuildSignature(new[]
        {
            "Foundation.URL",
            "Set<Int>",
            "some UIScene",
        });
        Assert.Equal("URL,Set<Int>,UIScene", sig);
    }

    [Fact]
    public void ComposeKey_EmptySig_ReturnsBareKey()
    {
        Assert.Equal("Foo.bar(_:)", MemberSignatureNormalizer.ComposeKey("Foo.bar(_:)", ""));
    }

    [Fact]
    public void ComposeKey_NonEmptySig_PipeSeparated()
    {
        Assert.Equal("Foo.bar(_:)|URL,Set<Int>",
            MemberSignatureNormalizer.ComposeKey("Foo.bar(_:)", "URL,Set<Int>"));
    }

    [Theory]
    [InlineData("for url: Foundation.URL", "URL")]
    [InlineData("for url: Foundation.URL, options: Set<Int>", "URL,Set<Int>")]
    [InlineData("confirmIn scene: some UIScene, options: Set<Int>", "UIScene,Set<Int>")]
    [InlineData("", "")]
    public void ExtractParamTypesFromSwiftClause_ReturnsNormalizedTypeList(string clause, string expectedJoined)
    {
        var types = MemberSignatureNormalizer.ExtractParamTypesFromSwiftClause(clause);
        Assert.Equal(expectedJoined, MemberSignatureNormalizer.BuildSignature(types));
    }
}
