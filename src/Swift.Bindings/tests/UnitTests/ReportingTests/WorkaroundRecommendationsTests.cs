// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Xunit;

namespace BindingsGeneration.Tests;

public class WorkaroundRecommendationsTests
{
    [Theory]
    [InlineData(SkipReason.UnsupportedExistential)]
    [InlineData(SkipReason.AnyTypeFallback)]
    [InlineData(SkipReason.UnsupportedSignature)]
    [InlineData(SkipReason.AsyncProperty)]
    [InlineData(SkipReason.SwiftUIConstraint)]
    [InlineData(SkipReason.CombineFramework)]
    [InlineData(SkipReason.GenericProtocolConstraint)]
    [InlineData(SkipReason.UnsatisfiedGenericConstraint)]
    [InlineData(SkipReason.UnsupportedClosure)]
    [InlineData(SkipReason.UnsupportedAsyncStream)]
    [InlineData(SkipReason.DuplicateSignature)]
    [InlineData(SkipReason.SwiftUIView)]
    [InlineData(SkipReason.StaticProtocolMember)]
    [InlineData(SkipReason.GenericTypeCallback)]
    [InlineData(SkipReason.ActorIsolatedAsyncStream)]
    [InlineData(SkipReason.SynthesizedCodable)]
    [InlineData(SkipReason.UnderscorePrefixInternal)]
    [InlineData(SkipReason.MissingHandler)]
    [InlineData(SkipReason.UnsupportedType)]
    [InlineData(SkipReason.Unknown)]
    public void GetRecommendation_ReturnsNonNullForAllSkipReasons(SkipReason reason)
    {
        var recommendation = WorkaroundRecommendations.GetRecommendation(reason);

        Assert.NotNull(recommendation);
        Assert.NotEmpty(recommendation);
    }

    [Fact]
    public void GetRecommendation_UnsupportedExistential_ContainsSwiftWrapper()
    {
        var recommendation = WorkaroundRecommendations.GetRecommendation(SkipReason.UnsupportedExistential);

        Assert.Contains("Swift wrapper", recommendation);
    }

    [Fact]
    public void GetRecommendation_AsyncProperty_SuggestsAsyncMethod()
    {
        var recommendation = WorkaroundRecommendations.GetRecommendation(SkipReason.AsyncProperty);

        Assert.Contains("async method", recommendation);
    }

    [Fact]
    public void GetRecommendation_DuplicateSignature_SuggestsRename()
    {
        var recommendation = WorkaroundRecommendations.GetRecommendation(SkipReason.DuplicateSignature);

        Assert.Contains("Rename", recommendation);
    }

    [Theory]
    [InlineData(SkipReason.UnsupportedExistential)]
    [InlineData(SkipReason.AnyTypeFallback)]
    [InlineData(SkipReason.UnsupportedSignature)]
    [InlineData(SkipReason.AsyncProperty)]
    [InlineData(SkipReason.SwiftUIConstraint)]
    [InlineData(SkipReason.SwiftUIView)]
    [InlineData(SkipReason.CombineFramework)]
    [InlineData(SkipReason.GenericProtocolConstraint)]
    [InlineData(SkipReason.UnsatisfiedGenericConstraint)]
    [InlineData(SkipReason.UnsupportedClosure)]
    [InlineData(SkipReason.UnsupportedAsyncStream)]
    [InlineData(SkipReason.DuplicateSignature)]
    [InlineData(SkipReason.StaticProtocolMember)]
    [InlineData(SkipReason.GenericTypeCallback)]
    [InlineData(SkipReason.ActorIsolatedAsyncStream)]
    [InlineData(SkipReason.SynthesizedCodable)]
    [InlineData(SkipReason.UnderscorePrefixInternal)]
    [InlineData(SkipReason.MissingHandler)]
    [InlineData(SkipReason.UnsupportedType)]
    [InlineData(SkipReason.Unknown)]
    public void GetDescription_ReturnsNonNullForAllSkipReasons(SkipReason reason)
    {
        var description = WorkaroundRecommendations.GetDescription(reason);

        Assert.NotNull(description);
        Assert.NotEmpty(description);
    }
}
