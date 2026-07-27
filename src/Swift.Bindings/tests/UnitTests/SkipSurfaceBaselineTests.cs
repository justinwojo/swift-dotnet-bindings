// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for <see cref="SkipSurfaceBaseline"/> — the skip-marker trend ratchet behind
/// <c>nuke binding-tests --compile-only --skip-surface</c>. The count ratchet is the easy half; the
/// load-bearing half is what a FALLING count means. "This member is bound now" and "this member no
/// longer exists" both make a skip marker disappear, and only the first is good news — a withdrawn
/// type takes its API and its markers with it, so an amputation otherwise reads as an improvement
/// and gets a checkmark. <see cref="SkipSurfaceBaseline.Compare"/> resolves that ambiguity by
/// cross-referencing the API manifest: a fall attributable to a type that no longer contributes any
/// symbol-bearing member is a regression. Each test feeds synthetic entries into the pure Compare.
/// </summary>
public class SkipSurfaceBaselineTests
{
    private const string Source = "BindingTests/output/TestLib.cs";

    private static SkipSurfaceBaseline.SkipSurfaceEntry E(string reason, int count, string marker = "Unsupported")
        => new() { Source = Source, Marker = marker, Reason = reason, Count = count };

    private static SkipSurfaceBaseline Seeded(params SkipSurfaceBaseline.SkipSurfaceEntry[] entries)
        => new() { GitSha = "abc1234", Entries = entries.ToList() };

    private static IReadOnlySet<string> Vanished(params string[] typeKeys)
        => new HashSet<string>(typeKeys);

    [Fact]
    public void Compare_CountUp_IsRegression()
    {
        var baseline = Seeded(E("method 'Widget.doThing' — unsupported", 1));

        var (regressions, improvements) = baseline.Compare(new[] { E("method 'Widget.doThing' — unsupported", 3) });

        Assert.Single(regressions);
        Assert.Contains("UP", regressions[0]);
        Assert.Empty(improvements);
    }

    [Fact]
    public void Compare_NewKey_IsRegression()
    {
        var baseline = Seeded(E("method 'Widget.doThing' — unsupported", 1));

        var (regressions, _) = baseline.Compare(new[]
        {
            E("method 'Widget.doThing' — unsupported", 1),
            E("method 'Widget.other' — unsupported", 1),
        });

        Assert.Single(regressions);
        Assert.Contains("NEW", regressions[0]);
    }

    [Fact]
    public void Compare_FlatCount_IsNeitherRegressionNorImprovement()
    {
        var entries = new[] { E("method 'Widget.doThing' — unsupported", 2) };

        var (regressions, improvements) = Seeded(entries).Compare(entries.ToList());

        Assert.Empty(regressions);
        Assert.Empty(improvements);
    }

    [Fact]
    public void Compare_MarkerGoneWhileTypeStillBinds_IsImprovement()
    {
        // The good case: the marker disappeared and the declaring type still contributes members to
        // the manifest, so the skip was genuinely fixed rather than amputated.
        var baseline = Seeded(E("method 'Widget.doThing' — unsupported", 1));

        var (regressions, improvements) = baseline.Compare(
            new List<SkipSurfaceBaseline.SkipSurfaceEntry>(),
            Vanished("TestLib|SomeOtherType"));

        Assert.Empty(regressions);
        Assert.Single(improvements);
        Assert.Contains("GONE", improvements[0]);
    }

    [Fact]
    public void Compare_MarkerGoneBecauseTypeVanished_IsRegression()
    {
        // The inversion this cross-reference exists for: the skip marker is gone because the whole
        // type stopped emitting bindable members. Counting that as an improvement is the gate
        // celebrating the exact failure it should catch.
        var baseline = Seeded(E("method 'Widget.doThing' — unsupported", 1));

        var (regressions, improvements) = baseline.Compare(
            new List<SkipSurfaceBaseline.SkipSurfaceEntry>(),
            Vanished("TestLib|Widget"));

        Assert.Empty(improvements);
        Assert.Single(regressions);
        Assert.Contains("GONE", regressions[0]);
        Assert.Contains("Widget", regressions[0]);
    }

    [Fact]
    public void Compare_CountDownBecauseTypeVanished_IsRegression()
    {
        // Partial amputation reads the same way as total amputation — a falling count attributable
        // to a vanished type is not progress.
        var baseline = Seeded(E("method 'Widget.doThing' — unsupported", 3));

        var (regressions, improvements) = baseline.Compare(
            new[] { E("method 'Widget.doThing' — unsupported", 1) },
            Vanished("TestLib|Widget"));

        Assert.Empty(improvements);
        Assert.Single(regressions);
        Assert.Contains("DOWN", regressions[0]);
    }

    [Fact]
    public void Compare_VanishedTypeInAnotherModule_DoesNotBlame()
    {
        // Type names are not unique across modules; attribution must key on (module, type) or one
        // module's withdrawal would indict an identically-named type in another.
        var baseline = Seeded(E("method 'Widget.doThing' — unsupported", 1));

        var (regressions, improvements) = baseline.Compare(
            new List<SkipSurfaceBaseline.SkipSurfaceEntry>(),
            Vanished("OtherLib|Widget"));

        Assert.Empty(regressions);
        Assert.Single(improvements);
    }

    [Fact]
    public void Compare_ReasonWithoutAMember_StaysAnUnverifiedImprovement()
    {
        // Attribute-shaped markers carry generic prose with no member name, so there is nothing to
        // attribute. The honest outcome is to leave the row an improvement rather than invent a
        // blame target — the cross-reference narrows the blind spot, it does not close it.
        var baseline = Seeded(E("Unsupported closure fallback", 2, marker: "UnsupportedSwiftType"));

        var (regressions, improvements) = baseline.Compare(
            new List<SkipSurfaceBaseline.SkipSurfaceEntry>(),
            Vanished("TestLib|Widget"));

        Assert.Empty(regressions);
        Assert.Single(improvements);
    }

    [Fact]
    public void Compare_FreeFunctionReason_StaysAnUnverifiedImprovement()
    {
        // A free function has no declaring type, so a vanished-type set can never speak for it.
        var baseline = Seeded(E("method 'appendPathElement' — unsupported", 1));

        var (regressions, improvements) = baseline.Compare(
            new List<SkipSurfaceBaseline.SkipSurfaceEntry>(),
            Vanished("TestLib|Widget"));

        Assert.Empty(regressions);
        Assert.Single(improvements);
    }

    [Fact]
    public void Compare_NoCrossReference_RunsTheCountRatchetAlone()
    {
        // The cross-reference is a corroborating layer; when the manifest is unavailable the gate
        // must degrade to its pre-cross-reference behavior, not start failing everything.
        var baseline = Seeded(E("method 'Widget.doThing' — unsupported", 1));

        var (regressions, improvements) = baseline.Compare(new List<SkipSurfaceBaseline.SkipSurfaceEntry>());

        Assert.Empty(regressions);
        Assert.Single(improvements);
    }

    [Fact]
    public void TryExtractDeclaringType_HandlesNestedTypesAndFreeFunctions()
    {
        Assert.Equal("Widget", SkipSurfaceBaseline.TryExtractDeclaringType("Widget.DoThing(int)"));
        Assert.Equal("Outer.Inner", SkipSurfaceBaseline.TryExtractDeclaringType("Outer.Inner.DoThing()"));
        // A free function's signature has no declaring type to attribute anything to.
        Assert.Null(SkipSurfaceBaseline.TryExtractDeclaringType("AdjustAndLog(int,string)"));
    }

    [Fact]
    public void TryExtractDeclaringTypeFromReason_ReadsTheQuotedMemberName()
    {
        Assert.Equal("Widget", SkipSurfaceBaseline.TryExtractDeclaringTypeFromReason(
            "method 'Widget.doThing' — parameter or return type not yet supported"));
        Assert.Equal("Data.Payload", SkipSurfaceBaseline.TryExtractDeclaringTypeFromReason(
            "method 'Data.Payload.tagged' — module-internal"));
        Assert.Equal("Host", SkipSurfaceBaseline.TryExtractDeclaringTypeFromReason(
            "property 'Host.values' — type could not be resolved"));
        Assert.Null(SkipSurfaceBaseline.TryExtractDeclaringTypeFromReason("Unsupported closure fallback"));
        Assert.Null(SkipSurfaceBaseline.TryExtractDeclaringTypeFromReason("method 'freeFunction' — unsupported"));
    }

    [Fact]
    public void ModuleFromSource_StripsDirectoriesAndSuffixes()
    {
        Assert.Equal("TestLib", SkipSurfaceBaseline.ModuleFromSource("BindingTests/output/TestLib.cs"));
        Assert.Equal("TestLib", SkipSurfaceBaseline.ModuleFromSource("TestLib.cs"));
        // Per-type split files are folded onto the module source before keying, but the module name
        // must survive either shape.
        Assert.Equal("TestLib", SkipSurfaceBaseline.ModuleFromSource("BindingTests/output/TestLib.Types.Widget.cs"));
    }

    [Fact]
    public void RoundTrip_PreservesEntries()
    {
        var baseline = Seeded(E("method 'Widget.doThing' — unsupported", 2), E("Unsupported closure fallback", 5));

        var reparsed = SkipSurfaceBaseline.Parse(baseline.ToJson());

        Assert.Equal(2, reparsed.Entries.Count);
        var (regressions, improvements) = reparsed.Compare(baseline.Entries.ToList());
        Assert.Empty(regressions);
        Assert.Empty(improvements);
    }

    [Fact]
    public void Parse_EmptyJson_YieldsEmptyBaseline()
    {
        Assert.Empty(SkipSurfaceBaseline.Parse("").Entries);
    }

    // ── Vanished-type cross-reference input ──

    private static ApiManifestBaseline.ApiManifestBaselineEntry M(string module, string signature) =>
        new() { Module = module, Signature = signature, Symbol = "$s" + signature.GetHashCode().ToString("x8") };

    /// <summary>
    /// An empty CURRENT manifest against a populated baseline is total surface collapse — the
    /// maximal loss the cross-reference exists to catch — so every baseline type must come back
    /// vanished. Reporting "nothing vanished" here would let the resulting flood of GONE skip rows
    /// bank as improvements and ratchet the baseline down on a run that emitted nothing at all.
    /// </summary>
    [Fact]
    public void ComputeVanishedTypes_EmptyCurrentManifest_VanishesEveryBaselineType()
    {
        var baseline = new[] { M("Lib", "Widget.DoThing(int)"), M("Lib", "Gadget.Value") };

        var vanished = SkipSurfaceBaseline.ComputeVanishedTypes(
            baseline, System.Array.Empty<ApiManifestBaseline.ApiManifestBaselineEntry>());

        Assert.Equal(2, vanished.Count);
        Assert.Contains("Lib|Widget", vanished);
        Assert.Contains("Lib|Gadget", vanished);
    }

    /// <summary>An empty baseline has no reference point, so nothing can be shown to have vanished.</summary>
    [Fact]
    public void ComputeVanishedTypes_EmptyBaseline_VanishesNothing()
    {
        Assert.Empty(SkipSurfaceBaseline.ComputeVanishedTypes(
            System.Array.Empty<ApiManifestBaseline.ApiManifestBaselineEntry>(),
            new[] { M("Lib", "Widget.DoThing(int)") }));
    }

    /// <summary>
    /// A type that still contributes ANY symbol-bearing member has not vanished, even if the
    /// specific member changed; only a type with nothing left counts.
    /// </summary>
    [Fact]
    public void ComputeVanishedTypes_TypeStillBinds_IsNotVanished()
    {
        var baseline = new[] { M("Lib", "Widget.DoThing(int)"), M("Lib", "Gadget.Value") };
        var current = new[] { M("Lib", "Widget.DoThingElse(string)") };

        var vanished = SkipSurfaceBaseline.ComputeVanishedTypes(baseline, current);

        Assert.DoesNotContain("Lib|Widget", vanished);
        Assert.Contains("Lib|Gadget", vanished);
    }
}
