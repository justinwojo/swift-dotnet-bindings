// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for <see cref="ObjCEnumCaseNames"/> — the carrier that lets a Swift-side reference to an
/// ObjC enum case name the member the ObjC companion actually declared, rather than re-deriving a
/// name from the Swift spelling and landing on one that was never emitted.
/// </summary>
public class ObjCEnumCaseNamesTests
{
    private static readonly Dictionary<string, string> TagStripped = new(StringComparer.Ordinal)
    {
        ["MLNMapTiler"] = "MapTiler",
        ["MLNMapbox"] = "Mapbox",
    };

    [Fact]
    public void EncodeDecode_RoundTripsTheMap()
    {
        var decoded = ObjCEnumCaseNames.Decode(ObjCEnumCaseNames.Encode(TagStripped));

        Assert.NotNull(decoded);
        Assert.Equal(TagStripped, decoded);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    // A database written before the attribute existed, or one whose attribute is unparseable, has to
    // load — the reference sites fall back to their old transform rather than failing the load.
    [InlineData("garbage-without-a-separator")]
    [InlineData("=Emitted")]
    [InlineData("Raw=")]
    public void Decode_AbsentOrMalformed_YieldsNull(string? attributeValue)
    {
        Assert.Null(ObjCEnumCaseNames.Decode(attributeValue));
    }

    [Fact]
    public void Encode_EmptyMap_YieldsNothingToPersist()
    {
        Assert.Null(ObjCEnumCaseNames.Encode(new Dictionary<string, string>()));
        Assert.Null(ObjCEnumCaseNames.Encode(null));
    }

    [Theory]
    // The spelling Swift produces when its importer found no prefix to strip …
    [InlineData("mlnMapTiler", "MapTiler")]
    // … and when it stripped one of its own.
    [InlineData("mapTiler", "MapTiler")]
    // The emitted name itself, and the raw ObjC name, both resolve.
    [InlineData("MapTiler", "MapTiler")]
    [InlineData("MLNMapTiler", "MapTiler")]
    [InlineData("mlnMapbox", "Mapbox")]
    public void TryResolveEmittedName_ResolvesEverySpellingOfTheSameCase(string spelling, string expected)
    {
        Assert.True(ObjCEnumCaseNames.TryResolveEmittedName(TagStripped, spelling, out var emitted));
        Assert.Equal(expected, emitted);
    }

    [Fact]
    public void TryResolveEmittedName_UnknownSpelling_DoesNotResolve()
    {
        Assert.False(ObjCEnumCaseNames.TryResolveEmittedName(TagStripped, "satellite", out _));
    }

    [Fact]
    public void TryResolveEmittedName_TailMatchMidWord_DoesNotResolve()
    {
        // The tail rule is anchored at a PascalCase word boundary: `Map` is a tail of `OUBitmap`
        // only mid-word, and resolving it would name an unrelated case.
        var caseNames = new Dictionary<string, string>(StringComparer.Ordinal) { ["OUBitmap"] = "Bitmap" };

        Assert.False(ObjCEnumCaseNames.TryResolveEmittedName(caseNames, "map", out _));
    }

    [Fact]
    public void TryResolveEmittedName_AmbiguousTail_DoesNotResolve()
    {
        // Two cases ending in the same word: guessing either one would be a silent mis-binding, so
        // the caller falls back to its own transform and the compiler gets the last word.
        var caseNames = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["OUSourceKind"] = "SourceKind",
            ["OUTargetKind"] = "TargetKind",
        };

        Assert.False(ObjCEnumCaseNames.TryResolveEmittedName(caseNames, "kind", out _));
    }

    [Fact]
    public void TryResolveEmittedName_NoMap_DoesNotResolve()
    {
        Assert.False(ObjCEnumCaseNames.TryResolveEmittedName(null, "mapTiler", out _));
        Assert.False(ObjCEnumCaseNames.TryResolveEmittedName(TagStripped, "", out _));
    }
}
