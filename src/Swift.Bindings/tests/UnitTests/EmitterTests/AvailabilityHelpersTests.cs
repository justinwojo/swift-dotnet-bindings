// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Unit coverage for the single-source-of-truth availability rules in
/// <see cref="AvailabilityHelpers"/>: the numeric OS-version comparer and the macCatalyst→iOS
/// floor lift shared by the Swift <c>@available</c> collector and every C#
/// <c>[SupportedOSPlatform]</c> emitter. The lift keeps the C# attribute floor in agreement with
/// the floor the force-lifted <c>@_cdecl</c> wrapper is exported at — without it a Mac Catalyst
/// consumer between the declared macCatalyst floor and the iOS floor orphans the native symbol.
/// </summary>
public class AvailabilityHelpersTests
{
    private static AvailabilityAnnotation Ann(string platform, string? introduced,
        string? deprecated = null, string? obsoleted = null) =>
        new(platform, introduced, deprecated, obsoleted, false, false, null, null);

    private static string? CatalystIntroduced(IReadOnlyList<AvailabilityAnnotation>? result) =>
        result?.FirstOrDefault(a => a.Platform == "macCatalyst")?.IntroducedVersion;

    // --- CompareOsVersions: numeric, not lexicographic ---

    [Theory]
    [InlineData("13.0", "26.0", -1)]
    [InlineData("26.0", "13.0", 1)]
    [InlineData("9.0", "10.0", -1)]   // lexicographic would flip this
    [InlineData("13", "13.0", 0)]     // missing components treated as 0
    [InlineData("17.4", "17.4", 0)]
    public void CompareOsVersions_IsComponentWiseNumeric(string left, string right, int expectedSign)
    {
        var actual = AvailabilityHelpers.CompareOsVersions(left, right);
        Assert.Equal(expectedSign, System.Math.Sign(actual));
    }

    // --- LiftMacCatalystFloorToIOS: the lift fires ---

    [Fact]
    public void Lift_RaisesExplicitCatalystFloorToIOS()
    {
        var input = new List<AvailabilityAnnotation> { Ann("iOS", "18.0"), Ann("macCatalyst", "17.0") };
        var result = AvailabilityHelpers.LiftMacCatalystFloorToIOS(input);
        Assert.Equal("18.0", CatalystIntroduced(result));
        // iOS entry is untouched.
        Assert.Equal("18.0", result!.First(a => a.Platform == "iOS").IntroducedVersion);
    }

    [Fact]
    public void Lift_UsesMaxIOSAcrossStackedAnnotations()
    {
        // Stacked parent+method+conformer floors: the lift must target the strictest iOS.
        var input = new List<AvailabilityAnnotation>
        {
            Ann("iOS", "16.0"), Ann("iOS", "26.0"), Ann("macCatalyst", "17.0"),
        };
        var result = AvailabilityHelpers.LiftMacCatalystFloorToIOS(input);
        Assert.Equal("26.0", CatalystIntroduced(result));
    }

    // --- LiftMacCatalystFloorToIOS: the lift correctly does NOT fire ---

    [Fact]
    public void Lift_AbsentCatalyst_InventsNothing()
    {
        // iOS-only: .NET's ios→maccatalyst inheritance already covers Catalyst; do not invent
        // a macCatalyst entry (matches the Swift collector's presence gate).
        var input = new List<AvailabilityAnnotation> { Ann("iOS", "18.0") };
        var result = AvailabilityHelpers.LiftMacCatalystFloorToIOS(input);
        Assert.Same(input, result);
        Assert.Null(CatalystIntroduced(result));
    }

    [Fact]
    public void Lift_CatalystAlreadyAtOrAboveIOS_Unchanged()
    {
        var input = new List<AvailabilityAnnotation> { Ann("iOS", "18.0"), Ann("macCatalyst", "20.0") };
        var result = AvailabilityHelpers.LiftMacCatalystFloorToIOS(input);
        Assert.Same(input, result);
        Assert.Equal("20.0", CatalystIntroduced(result));
    }

    [Fact]
    public void Lift_IOSBelow13_Unchanged()
    {
        // Pre-unified-SDK era: macabi does not 1:1-map iOS onto macCatalyst below 13.0.
        var input = new List<AvailabilityAnnotation> { Ann("iOS", "12.0"), Ann("macCatalyst", "11.0") };
        var result = AvailabilityHelpers.LiftMacCatalystFloorToIOS(input);
        Assert.Same(input, result);
        Assert.Equal("11.0", CatalystIntroduced(result));
    }

    [Fact]
    public void Lift_NullOrEmpty_ReturnsInput()
    {
        Assert.Null(AvailabilityHelpers.LiftMacCatalystFloorToIOS(null));
        var empty = new List<AvailabilityAnnotation>();
        Assert.Same(empty, AvailabilityHelpers.LiftMacCatalystFloorToIOS(empty));
    }

    [Fact]
    public void Lift_DoesNotMutateInput()
    {
        var input = new List<AvailabilityAnnotation> { Ann("iOS", "18.0"), Ann("macCatalyst", "17.0") };
        AvailabilityHelpers.LiftMacCatalystFloorToIOS(input);
        // Original list + record are untouched (pure projection).
        Assert.Equal("17.0", input[1].IntroducedVersion);
    }

    // --- LiftMacCatalystFloorToIOS: vacuous-deprecation clamp ---

    [Fact]
    public void Lift_ClearsDeprecationBelowLiftedFloor()
    {
        // macCatalyst introduced 17.0 / deprecated 17.5, but the iOS floor forces introduced→18.0.
        // A deprecation below the introduced floor is vacuous; clearing it avoids emitting a
        // backwards [ObsoletedOSPlatform("maccatalyst17.5")] under [SupportedOSPlatform("maccatalyst18.0")].
        var input = new List<AvailabilityAnnotation>
        {
            Ann("iOS", "18.0"), Ann("macCatalyst", "17.0", deprecated: "17.5", obsoleted: "17.5"),
        };
        var result = AvailabilityHelpers.LiftMacCatalystFloorToIOS(input);
        var catalyst = result!.First(a => a.Platform == "macCatalyst");
        Assert.Equal("18.0", catalyst.IntroducedVersion);
        Assert.Null(catalyst.DeprecatedVersion);
        Assert.Null(catalyst.ObsoletedVersion);
    }

    [Fact]
    public void Lift_KeepsDeprecationAboveLiftedFloor()
    {
        // deprecated 26.0 stays valid above the lifted introduced 18.0.
        var input = new List<AvailabilityAnnotation>
        {
            Ann("iOS", "18.0"), Ann("macCatalyst", "17.0", deprecated: "26.0"),
        };
        var result = AvailabilityHelpers.LiftMacCatalystFloorToIOS(input);
        var catalyst = result!.First(a => a.Platform == "macCatalyst");
        Assert.Equal("18.0", catalyst.IntroducedVersion);
        Assert.Equal("26.0", catalyst.DeprecatedVersion);
    }

    [Fact]
    public void Lift_KeepsDeprecationEqualToLiftedFloor()
    {
        // Boundary of the strict-less-than clamp: deprecated exactly AT the lifted introduced
        // floor is a legitimate "introduced and immediately deprecated" pair, so it is kept.
        var input = new List<AvailabilityAnnotation>
        {
            Ann("iOS", "18.0"), Ann("macCatalyst", "17.0", deprecated: "18.0"),
        };
        var result = AvailabilityHelpers.LiftMacCatalystFloorToIOS(input);
        var catalyst = result!.First(a => a.Platform == "macCatalyst");
        Assert.Equal("18.0", catalyst.IntroducedVersion);
        Assert.Equal("18.0", catalyst.DeprecatedVersion);
    }
}
