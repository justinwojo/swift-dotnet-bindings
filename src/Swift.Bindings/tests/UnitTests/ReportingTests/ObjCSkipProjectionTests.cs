// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using BindingsGeneration.ObjC;
using Xunit;

namespace BindingsGeneration.Tests;

public class ObjCSkipProjectionTests
{
    /// <summary>
    /// The mapping must be total and 1:1: every <see cref="ObjCSkipReason"/> maps to a distinct
    /// ObjC-prefixed <see cref="SkipReason"/> without throwing. A new ObjCSkipReason added without a
    /// counterpart throws in <see cref="ObjCSkipProjection.ToSkipReason"/> and trips this test —
    /// forcing the report vocabulary to grow with the ObjC diagnostics rather than collapsing a cause.
    /// </summary>
    [Fact]
    public void ToSkipReason_EveryObjCReason_MapsToDistinctObjCSkipReason()
    {
        var mapped = new List<SkipReason>();
        foreach (ObjCSkipReason reason in Enum.GetValues<ObjCSkipReason>())
        {
            var skipReason = ObjCSkipProjection.ToSkipReason(reason);
            Assert.StartsWith("ObjC", skipReason.ToString(), StringComparison.Ordinal);
            mapped.Add(skipReason);
        }

        // 1:1 — no two ObjC reasons collapse onto the same SkipReason.
        Assert.Equal(mapped.Count, mapped.Distinct().Count());
        Assert.Equal(Enum.GetValues<ObjCSkipReason>().Length, mapped.Count);
    }

    [Theory]
    [InlineData(ObjCSkipReason.UnresolvableType, SkipReason.ObjCUnresolvableType)]
    [InlineData(ObjCSkipReason.UnavailableApi, SkipReason.ObjCUnavailableApi)]
    [InlineData(ObjCSkipReason.UnsupportedConstruct, SkipReason.ObjCUnsupportedConstruct)]
    [InlineData(ObjCSkipReason.AccessibilityConflict, SkipReason.ObjCAccessibilityConflict)]
    [InlineData(ObjCSkipReason.DuplicateSignature, SkipReason.ObjCDuplicateSignature)]
    [InlineData(ObjCSkipReason.VariadicFunction, SkipReason.ObjCVariadicFunction)]
    [InlineData(ObjCSkipReason.EmptyCategory, SkipReason.ObjCEmptyCategory)]
    [InlineData(ObjCSkipReason.MissingNativeSymbol, SkipReason.ObjCMissingNativeSymbol)]
    [InlineData(ObjCSkipReason.DuplicateSelector, SkipReason.ObjCDuplicateSelector)]
    public void ToSkipReason_MapsEachCase(ObjCSkipReason reason, SkipReason expected)
    {
        Assert.Equal(expected, ObjCSkipProjection.ToSkipReason(reason));
    }

    [Theory]
    [InlineData("Method", BindingItemKind.Method)]
    [InlineData("method", BindingItemKind.Method)]
    [InlineData("Function", BindingItemKind.Method)]
    [InlineData("Property", BindingItemKind.Property)]
    [InlineData("constant", BindingItemKind.Property)]
    [InlineData("class", BindingItemKind.Type)]
    [InlineData("Class", BindingItemKind.Type)]
    [InlineData("category", BindingItemKind.Type)]
    [InlineData("Struct", BindingItemKind.Type)]
    [InlineData("Delegate", BindingItemKind.Type)]
    [InlineData("somethingUnknown", BindingItemKind.Type)]
    public void ToItemKind_MapsKnownAndUnknownKinds(string symbolKind, BindingItemKind expected)
    {
        Assert.Equal(expected, ObjCSkipProjection.ToItemKind(symbolKind));
    }

    [Fact]
    public void ToSkippedItem_CarriesNameDetailAndRecommendation()
    {
        var symbol = new ObjCSkippedSymbol(
            "Method", "FBSDKTypeUtility.jsonObjectWithData",
            ObjCSkipReason.UnresolvableType, "NSJSONReadingOptions not in registry");

        var item = ObjCSkipProjection.ToSkippedItem(symbol);

        Assert.Equal(BindingItemKind.Method, item.Kind);
        Assert.Equal("FBSDKTypeUtility.jsonObjectWithData", item.Name);
        Assert.Equal(SkipReason.ObjCUnresolvableType, item.Reason);
        Assert.Equal("NSJSONReadingOptions not in registry", item.Details);
        // The recommendation is populated from the shared WorkaroundRecommendations table for the
        // mapped reason — so a report consumer sees an actionable hint on every ObjC drop.
        Assert.False(string.IsNullOrEmpty(item.RecommendedWorkaround));
        Assert.Equal(WorkaroundRecommendations.GetRecommendation(SkipReason.ObjCUnresolvableType),
            item.RecommendedWorkaround);
    }
}
