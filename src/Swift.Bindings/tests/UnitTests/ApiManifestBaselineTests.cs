// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for <see cref="ApiManifestBaseline"/> — the ABI-contract ratchet backing
/// <c>nuke binding-tests</c>'s API-manifest gate. The load-bearing case is a silent retarget: a
/// stable <c>(module, C# signature)</c> rebinds to a DIFFERENT native entry symbol (the
/// overload-disambiguation hazard). Because collision suffixes are assigned in declaration order, an
/// upstream reorder can shift the bare-name owner and produce exactly such a retarget — the gate is
/// the net that catches it. The gate fails on retarget but NOT on an added or removed member, so a
/// reorder that shifts a suffix without retargeting any stable signature stays green. Each test feeds
/// synthetic entries straight into the pure <see cref="ApiManifestBaseline.Compare"/>.
/// </summary>
public class ApiManifestBaselineTests
{
    private const string Mod = "TestLib";

    private static ApiManifestBaseline.ApiManifestBaselineEntry E(string signature, string symbol, string module = Mod)
        => new() { Module = module, Signature = signature, Symbol = symbol };

    private static ApiManifestBaseline Seeded(params ApiManifestBaseline.ApiManifestBaselineEntry[] entries)
        => new() { SchemaVersion = 1, Entries = entries.ToList() };

    [Fact]
    public void Compare_SameSignatureDifferentSymbol_IsRetarget()
    {
        // The headline hazard: Foo.Bar(int) bound symbol_A at baseline, now binds symbol_B.
        var baseline = Seeded(E("Foo.Bar(int)", "SBW_A"));
        var current = new[] { E("Foo.Bar(int)", "SBW_B") };

        var (retargets, added, removed) = baseline.Compare(current);

        Assert.Single(retargets);
        Assert.Contains("Foo.Bar(int)", retargets[0]);
        Assert.Contains("SBW_A", retargets[0]);
        Assert.Contains("SBW_B", retargets[0]);
        Assert.Empty(added);
        Assert.Empty(removed);
    }

    [Fact]
    public void Compare_IdenticalManifest_NoFindings()
    {
        var entries = new[] { E("Foo.Bar(int)", "SBW_A"), E("Foo.Baz()", "SBW_C") };
        var baseline = Seeded(entries);

        var (retargets, added, removed) = baseline.Compare(entries.ToList());

        Assert.Empty(retargets);
        Assert.Empty(added);
        Assert.Empty(removed);
    }

    [Fact]
    public void Compare_NewSignature_IsAddedNotRetarget()
    {
        var baseline = Seeded(E("Foo.Bar(int)", "SBW_A"));
        var current = new[] { E("Foo.Bar(int)", "SBW_A"), E("Foo.New(long)", "SBW_N") };

        var (retargets, added, removed) = baseline.Compare(current);

        Assert.Empty(retargets);
        Assert.Single(added);
        Assert.Contains("Foo.New(long)", added[0]);
        Assert.Empty(removed);
    }

    [Fact]
    public void Compare_DroppedSignature_IsRemovedNotRetarget()
    {
        var baseline = Seeded(E("Foo.Bar(int)", "SBW_A"), E("Foo.Gone()", "SBW_G"));
        var current = new[] { E("Foo.Bar(int)", "SBW_A") };

        var (retargets, added, removed) = baseline.Compare(current);

        Assert.Empty(retargets);
        Assert.Empty(added);
        Assert.Single(removed);
        Assert.Contains("Foo.Gone()", removed[0]);
    }

    [Fact]
    public void Compare_TwoOverloadsSwapSymbols_BothRetarget()
    {
        // A source reorder retargets Process and Process2 onto each other's symbol while both
        // signatures stay present. Suffixes are assigned in declaration order, so such a reorder can
        // happen on a binding regen — and the gate is the safety net that catches the retarget.
        var baseline = Seeded(E("C.Process(int)", "SBW_P0"), E("C.Process2(int)", "SBW_P1"));
        var current = new[] { E("C.Process(int)", "SBW_P1"), E("C.Process2(int)", "SBW_P0") };

        var (retargets, added, removed) = baseline.Compare(current);

        Assert.Equal(2, retargets.Count);
        Assert.Empty(added);
        Assert.Empty(removed);
    }

    [Fact]
    public void Compare_SameSignatureDifferentModule_NotConflated()
    {
        // Identical C# signature in two modules must key independently — a retarget in one module
        // must not be masked by (or attributed to) the other.
        var baseline = Seeded(E("Foo.Bar(int)", "SBW_A", "ModX"), E("Foo.Bar(int)", "SBW_B", "ModY"));
        var current = new[] { E("Foo.Bar(int)", "SBW_A", "ModX"), E("Foo.Bar(int)", "SBW_Z", "ModY") };

        var (retargets, added, removed) = baseline.Compare(current);

        Assert.Single(retargets);
        Assert.Contains("ModY", retargets[0]);
        Assert.Empty(added);
        Assert.Empty(removed);
    }

    [Fact]
    public void RoundTrip_PreservesEntriesAndSchema()
    {
        var baseline = Seeded(E("Foo.Bar(int)", "SBW_A"), E("Foo.Baz()", "SBW_C"));

        var reparsed = ApiManifestBaseline.Parse(baseline.ToJson());

        Assert.Equal(1, reparsed.SchemaVersion);
        Assert.Equal(2, reparsed.Entries.Count);
        var (retargets, added, removed) = reparsed.Compare(baseline.Entries.ToList());
        Assert.Empty(retargets);
        Assert.Empty(added);
        Assert.Empty(removed);
    }

    [Fact]
    public void Parse_EmptyJson_YieldsEmptyBaseline()
    {
        var baseline = ApiManifestBaseline.Parse("");
        Assert.Empty(baseline.Entries);
    }
}
