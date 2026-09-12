// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using Xunit;

namespace BindingsGeneration.Tests;

public class CanonicalIdentityCodecTests
{
    [Fact]
    public void EveryKindAccessorAndScope_HasByteParityWithModel()
    {
        foreach (var kind in Enum.GetValues<BindingItemKind>())
        foreach (var accessor in Enum.GetValues<AccessorKind>())
        foreach (var scope in Enum.GetValues<RecoveryScope>())
        {
            var id = DeclId.Create("M|x", "T:n", kind, "name,\\!", accessor: accessor).Unit(scope);
            Assert.True(CanonicalIdentityCodec.TryParseUnit(id.Canonical, out var parsed));
            Assert.Equal(id.Canonical, parsed.Canonical);
            Assert.Equal(id.Describe(), parsed.Describe());
            Assert.Equal(id, RecoveryUnitId.Parse(parsed.Canonical));
        }
    }

    [Theory]
    [InlineData("M|T|Unknown|f||None|||!leaf-api")]
    [InlineData("M|T|1|f||None|||!leaf-api")]
    [InlineData("M|T|Method |f||None|||!leaf-api")]
    [InlineData("M|T|Method|f||getter|||!leaf-api")]
    [InlineData("M|T|Method|f||None|||!unknown")]
    [InlineData("M|T|Method|f||None|||!LeafApi")]
    [InlineData("M|T|Method|f||None|||!0")]
    [InlineData("M|T|Method|f||None||bad:colon|!leaf-api")]
    [InlineData("M|T|Method|f||None||bad,comma|!leaf-api")]
    [InlineData(@"M|T|Method|f||None||bad\q|!leaf-api")]
    [InlineData("M|T|Method|f|bad||None|||!leaf-api")]
    public void MalformedOrNoncanonicalInput_IsRejectedByBothReaders(string text)
    {
        Assert.False(CanonicalIdentityCodec.TryParseUnit(text, out _));
        Assert.False(RecoveryUnitId.TryParse(text, out _));
    }
}
