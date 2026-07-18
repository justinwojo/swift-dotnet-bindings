// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Behavior of <see cref="ArtifactId"/>: one declaration fans out to several generated artifacts,
/// and each must be nameable, distinct from its siblings, and round-trippable through text.
/// </summary>
public class ArtifactIdTests
{
    private static DeclId SampleDecl()
    {
        var module = TestModelFactory.CreateModuleDecl();
        return DeclIdFactory.ForMethod(
            TestModelFactory.CreateMethod("fetch", module, new[] { ("from", "Swift.String") }));
    }

    [Fact]
    public void ArtifactsOfOneDeclaration_AreDistinctPerRole()
    {
        var decl = SampleDecl();
        var roles = Enum.GetValues<ArtifactRole>();

        var canonicals = roles.Select(r => decl.Artifact(r).Canonical).ToList();

        Assert.Equal(roles.Length, canonicals.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void ArtifactsOfDifferentDeclarations_InTheSameRole_AreDistinct()
    {
        var module = TestModelFactory.CreateModuleDecl();
        var first = DeclIdFactory.ForMethod(TestModelFactory.CreateMethod("fetch", module));
        var second = DeclIdFactory.ForMethod(TestModelFactory.CreateMethod("store", module));

        Assert.NotEqual(
            first.Artifact(ArtifactRole.SwiftWrapper),
            second.Artifact(ArtifactRole.SwiftWrapper));
    }

    [Fact]
    public void RepeatedRole_DistinguishesSiblingsByOrdinal()
    {
        var decl = SampleDecl();

        var zero = decl.Artifact(ArtifactRole.Callback, 0);
        var one = decl.Artifact(ArtifactRole.Callback, 1);

        Assert.NotEqual(zero, one);
        Assert.NotEqual(zero.Canonical, one.Canonical);
    }

    [Fact]
    public void RepeatingRole_AlwaysSerializesItsOrdinal()
    {
        // Callbacks are numbered from zero, so an "omit when zero" rule would make the bare role
        // token and callback #0 the same string — two artifacts, one id.
        var canonical = SampleDecl().Artifact(ArtifactRole.Callback, 0).Canonical;

        Assert.EndsWith("/callback#0", canonical, StringComparison.Ordinal);
    }

    [Fact]
    public void SingleOccurrenceRole_RejectsANonZeroOrdinal()
    {
        var decl = SampleDecl();

        Assert.Throws<ArgumentOutOfRangeException>(() => decl.Artifact(ArtifactRole.SwiftWrapper, 1));
    }

    [Fact]
    public void NegativeOrdinal_IsRejected()
    {
        var decl = SampleDecl();

        Assert.Throws<ArgumentOutOfRangeException>(() => decl.Artifact(ArtifactRole.Callback, -1));
    }

    [Theory]
    [InlineData(ArtifactRole.CSharpPublic)]
    [InlineData(ArtifactRole.PInvoke)]
    [InlineData(ArtifactRole.SwiftWrapper)]
    [InlineData(ArtifactRole.MetadataHelper)]
    [InlineData(ArtifactRole.ReverseVtable)]
    [InlineData(ArtifactRole.ModuleInitializer)]
    public void Canonical_RoundTripsThroughParse(ArtifactRole role)
    {
        var id = SampleDecl().Artifact(role);

        var parsed = ArtifactId.Parse(id.Canonical);

        Assert.Equal(id, parsed);
        Assert.Equal(id.Canonical, parsed.Canonical);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public void Canonical_RoundTripsThroughParse_ForCallbackOrdinals(int ordinal)
    {
        var id = SampleDecl().Artifact(ArtifactRole.Callback, ordinal);

        var parsed = ArtifactId.Parse(id.Canonical);

        Assert.Equal(id, parsed);
        Assert.Equal(ordinal, parsed.Ordinal);
    }

    [Fact]
    public void Canonical_RoundTripsWhenTheDeclarationContainsASlash()
    {
        // The split is on the LAST '/', so a Swift type expression carrying one must not confuse it.
        var decl = DeclId.Create(
            "M", "T", BindingItemKind.Method, "f",
            System.Collections.Immutable.ImmutableArray.Create("path"),
            System.Collections.Immutable.ImmutableArray.Create("Swift.String/*note*/"));
        var id = decl.Artifact(ArtifactRole.PInvoke);

        var parsed = ArtifactId.Parse(id.Canonical);

        Assert.Equal(id, parsed);
        Assert.Equal(decl, parsed.Decl);
    }

    [Theory]
    [InlineData("")]
    [InlineData("M|T|Method|f||None|||")]                       // decl id with no role suffix
    [InlineData("M|T|Method|f||None|||/not-a-role")]            // unknown role token
    [InlineData("M|T|Method|f||None|||/swift-wrapper#1")]       // ordinal on a single-occurrence role
    [InlineData("M|T|Method|f||None|||/callback")]              // missing ordinal on a repeating role
    [InlineData("M|T|Method|f||None|||/callback#x")]            // malformed ordinal
    [InlineData("M|T|Method|f||None|||/callback#-1")]           // negative ordinal
    [InlineData("not-a-decl-id/pinvoke")]                       // decl prefix isn't a DeclId
    public void TryParse_RejectsMalformedInput(string canonical)
    {
        Assert.False(ArtifactId.TryParse(canonical, out _));
        Assert.Throws<FormatException>(() => ArtifactId.Parse(canonical));
    }

    [Fact]
    public void ShortHash_IsEightUppercaseHexCharactersAndVariesByRole()
    {
        var decl = SampleDecl();
        var wrapper = decl.Artifact(ArtifactRole.SwiftWrapper).ShortHash;
        var pinvoke = decl.Artifact(ArtifactRole.PInvoke).ShortHash;

        Assert.Equal(8, wrapper.Length);
        Assert.All(wrapper, c => Assert.True(char.IsAsciiDigit(c) || (c >= 'A' && c <= 'F'), $"'{c}' is not uppercase hex."));
        Assert.NotEqual(wrapper, pinvoke);
    }

    [Fact]
    public void EveryRole_HasADistinctStableToken()
    {
        // A duplicated or missing token would silently merge two artifact kinds on the wire.
        var decl = SampleDecl();
        var tokens = Enum.GetValues<ArtifactRole>()
            .Select(r => decl.Artifact(r).Canonical)
            .Select(c => c.Substring(c.LastIndexOf('/') + 1))
            .ToList();

        Assert.Equal(tokens.Count, tokens.Distinct(StringComparer.Ordinal).Count());
        Assert.All(tokens, t => Assert.DoesNotContain('|', t));
    }
}
