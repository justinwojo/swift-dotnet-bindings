// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Swift.Runtime;
using Xunit;

namespace Swift.Runtime.Tests;

/// <summary>
/// <see cref="SwiftEnumCaseMap"/> reads a resilient enum's case tags from the library that defines
/// it. The standard library's <c>DecodingError</c> is resilient, and its four cases (all with
/// payloads, so tagged in declaration order) export their "enum case" globals.
/// </summary>
public class SwiftEnumCaseMapTests
{
    private const string SwiftCore = "/usr/lib/swift/libswiftCore.dylib";

    private static readonly string[] DecodingErrorCases =
    [
        "$ss13DecodingErrorO12typeMismatchyABypXp_AB7ContextVtcABmFWC",
        "$ss13DecodingErrorO13valueNotFoundyABypXp_AB7ContextVtcABmFWC",
        "$ss13DecodingErrorO11keyNotFoundyABs9CodingKey_p_AB7ContextVtcABmFWC",
        "$ss13DecodingErrorO13dataCorruptedyA2B7ContextVcABmFWC",
    ];

    // Managed identities unrelated to the tags, as a binding's name-keyed constants are.
    private static readonly uint[] Ids = [7000u, 31u, 900u, 12u];

    [Fact]
    public void ReadsEachCaseTagFromTheLoadedLibrary_BothWays()
    {
        // The declaration tags given here are wrong on purpose: the library's own tags must win.
        var map = new SwiftEnumCaseMap(SwiftCore, DecodingErrorCases, Ids, [3u, 2u, 1u, 0u]);
        for (var i = 0; i < Ids.Length; i++)
        {
            Assert.Equal((uint)i, map.ToTag(Ids[i]));
            Assert.Equal(Ids[i], map.ToCaseId((uint)i));
            Assert.True(map.HasCase(Ids[i]));
        }
        Assert.Equal(SwiftEnumCaseMap.UnknownCase, map.ToCaseId(4));
    }

    [Fact]
    public void ACaseTheLoadedLibraryLacks_IsNotBuildable_AndNoTagReadsAsIt()
    {
        // Three real cases and one the library does not have, as an OS older than the binding's SDK.
        var symbols = new[] { DecodingErrorCases[0], "$ss13DecodingErrorO13notARealCaseyA2BmFWC", DecodingErrorCases[1], DecodingErrorCases[2] };
        var map = new SwiftEnumCaseMap(SwiftCore, symbols, [1u, 2u, 3u, 4u], [0u, 1u, 2u, 3u]);
        Assert.False(map.HasCase(2u));
        var refusal = Assert.Throws<PlatformNotSupportedException>(() => map.ToTag(2u));
        Assert.Contains("notARealCase", refusal.Message);
        Assert.Equal(0u, map.ToTag(1u));
        Assert.Equal(1u, map.ToTag(3u));
        Assert.Equal(2u, map.ToTag(4u));
        Assert.Equal(3u, map.ToCaseId(1));
        Assert.Equal(SwiftEnumCaseMap.UnknownCase, map.ToCaseId(3));
    }

    [Theory]
    // A library with none of the symbols: a module built without library evolution, whose tags are
    // the declaration-order tags.
    [InlineData(SwiftCore)]
    // A library that does not load.
    [InlineData("/nonexistent/NoSuch.framework/NoSuch")]
    public void WhenNoCaseSymbolResolves_TheDeclarationTagsAreTheTags(string library)
    {
        var map = new SwiftEnumCaseMap(library, ["$s4Kit5ShadeO5firstyA2CmFWC", "$s4Kit5ShadeO4lateyA2CmFWC"], [500u, 9u], [0u, 1u]);
        Assert.Equal(0u, map.ToTag(500u));
        Assert.Equal(1u, map.ToTag(9u));
        Assert.Equal(9u, map.ToCaseId(1));
        Assert.Equal(SwiftEnumCaseMap.UnknownCase, map.ToCaseId(2));
    }

    [Fact]
    public void AnIdentityTheEnumDoesNotHave_IsAnArgumentError()
    {
        var map = new SwiftEnumCaseMap(SwiftCore, DecodingErrorCases, Ids, [0u, 1u, 2u, 3u]);
        Assert.Throws<ArgumentOutOfRangeException>(() => map.ToTag(5u));
        Assert.False(map.HasCase(5u));
    }

    [Fact]
    public void MismatchedArrays_AreRefused()
        => Assert.Throws<ArgumentException>(() => new SwiftEnumCaseMap(SwiftCore, DecodingErrorCases, [1u], [0u, 1u, 2u, 3u]));
}
