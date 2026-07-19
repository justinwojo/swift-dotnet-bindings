// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

using BindingsGeneration.Diagnostics;

namespace BindingsGeneration.Tests;

/// <summary>
/// End-to-end attribution over the recorded wrapper-compile failures, plus the individual provenance
/// steps and the classification/priority rules the recorded captures do not exercise on their own.
/// </summary>
public class DiagnosticAttributorTests
{
    // ── end-to-end over recorded captures ───────────────────────────────────────────────────

    /// <summary>One broken member, two errors on its line: attribution names exactly that unit.</summary>
    [Fact]
    public void Attribute_SingleBrokenMember_NamesTheOneBrokenUnit()
    {
        var result = AttributeCapture("SingleBrokenMember");

        var culprit = Assert.Single(result.Culprits);
        Assert.Equal(AttributionFixtures.UnitForSymbol("SBW_Gadget_rotate"), culprit);
        Assert.False(result.HasUnattributedError);
    }

    /// <summary>
    /// Cascade hygiene: four distinct errors, all inside the one <c>SBW_Timer_fire</c> block, collapse
    /// to a single culprit — one denylist increment, not four.
    /// </summary>
    [Fact]
    public void Attribute_CascadeInOneMember_CollapsesToASingleCulprit()
    {
        var result = AttributeCapture("CascadeInOneMember");

        Assert.Equal(4, result.ErrorCount);
        var culprit = Assert.Single(result.Culprits);
        Assert.Equal(AttributionFixtures.UnitForSymbol("SBW_Timer_fire"), culprit);
    }

    /// <summary>Two independent broken members yield two distinct culprits; the clean member is absent.</summary>
    [Fact]
    public void Attribute_TwoBrokenMembers_NamesBothAndOmitsTheCleanOne()
    {
        var result = AttributeCapture("TwoBrokenMembers");

        Assert.Equal(2, result.Culprits.Length);
        Assert.Contains(AttributionFixtures.UnitForSymbol("SBW_Ledger_credit"), result.Culprits);
        Assert.Contains(AttributionFixtures.UnitForSymbol("SBW_Ledger_debit"), result.Culprits);
        Assert.DoesNotContain(AttributionFixtures.UnitForSymbol("SBW_Ledger_balance"), result.Culprits);
    }

    /// <summary>
    /// A missing input module is a global failure: it classifies to InputConfiguration and never
    /// becomes a culprit, so the loop cannot try to "recover" by withdrawing a declaration.
    /// </summary>
    [Fact]
    public void Attribute_MissingModule_ClassifiesToInputConfigurationWithNoCulprit()
    {
        var result = AttributeCapture("MissingModule");

        Assert.Empty(result.Culprits);
        Assert.False(result.HasUnattributedError);

        var decision = Assert.Single(result.Diagnostics);
        Assert.Equal(AttributionKind.Classification, decision.Kind);
        Assert.Equal(CauseOwner.InputConfiguration, decision.Owner);
        Assert.Equal("CompletelyFictionalDependency", decision.ClassificationDetail);
    }

    private static AttributionResult AttributeCapture(string fixture)
    {
        var groups = SwiftDiagnosticParser.Parse(AttributionFixtures.Stderr(fixture));
        var attributor = new DiagnosticAttributor(
            new[] { AttributionFixtures.SymbolStep(AttributionFixtures.Source(fixture)) });
        return attributor.Attribute(groups);
    }

    // ── classification precedes provenance (priority 5) ─────────────────────────────────────

    /// <summary>
    /// A missing-module diagnostic that happens to point inside a resolvable block must still
    /// classify, not attribute — the classifier runs before any provenance step.
    /// </summary>
    [Fact]
    public void Attribute_MissingModuleInsideABlock_StillClassifies()
    {
        const string source = """
            @_cdecl("SBW_Inline_x")
            public func SBW_Inline_x() { }
            """;
        var group = new DiagnosticGroup
        {
            Primary = new CompilerDiagnostic
            {
                File = "Inline.wrapper.swift",
                Line = 2,
                Column = 5,
                Severity = DiagnosticSeverity.Error,
                Message = "no such module 'Ghost'",
            },
        };

        var attributor = new DiagnosticAttributor(new[] { AttributionFixtures.SymbolStep(source) });
        var result = attributor.Attribute(new[] { group });

        Assert.Empty(result.Culprits);
        Assert.Equal(AttributionKind.Classification, Assert.Single(result.Diagnostics).Kind);
    }

    /// <summary>An error resolvable by no step and matching no classifier surfaces as unattributed.</summary>
    [Fact]
    public void Attribute_ErrorInNoBlock_IsUnattributed()
    {
        var group = ErrorAt("Orphan.wrapper.swift", line: 999, "some unrecognized failure");
        var attributor = new DiagnosticAttributor(
            new[] { AttributionFixtures.SymbolStep(AttributionFixtures.Source("SingleBrokenMember")) });

        var result = attributor.Attribute(new[] { group });

        Assert.Empty(result.Culprits);
        Assert.True(result.HasUnattributedError);
        Assert.Equal(AttributionKind.Unattributed, Assert.Single(result.Diagnostics).Kind);
    }

    // ── interval map is priority 1 ──────────────────────────────────────────────────────────

    [Fact]
    public void IntervalMapStep_ResolvesASwiftPlaneDiagnosticToItsFragmentsUnit()
    {
        var (set, unit) = BuildFragmentSet("Target.Wrapper.swift", "one\ntwo broken\n", "SBW_Interval_target");
        var step = new IntervalMapProvenanceStep(set);

        var diag = ErrorAt("/tmp/build/Target.Wrapper.swift", line: 2, "boom").Primary;

        Assert.True(step.TryResolve(diag, out var hit));
        Assert.Equal(unit, hit.Unit);
        Assert.Equal(ProvenanceSource.IntervalMap, hit.Source);
    }

    /// <summary>
    /// When both the interval map and the symbol index could resolve a diagnostic, the interval map —
    /// the authoritative mechanism — wins, and the recorded source proves it.
    /// </summary>
    [Fact]
    public void Attribute_WhenIntervalMapAndSymbolBothApply_IntervalMapWins()
    {
        const string content = "@_cdecl(\"SBW_Both_x\")\npublic func SBW_Both_x() { fail() }\n";
        var (set, intervalUnit) = BuildFragmentSet("Both.Wrapper.swift", content, "SBW_Interval_owner");

        var attributor = new DiagnosticAttributor(new IProvenanceStep[]
        {
            new IntervalMapProvenanceStep(set),
            AttributionFixtures.SymbolStep(content),
        });

        var result = attributor.Attribute(new[] { ErrorAt("Both.Wrapper.swift", line: 2, "fail") });

        var culprit = Assert.Single(result.Culprits);
        Assert.Equal(intervalUnit, culprit);   // not UnitForSymbol("SBW_Both_x")
        Assert.Equal(ProvenanceSource.IntervalMap, result.Diagnostics[0].Source);
    }

    // ── origin anchor is priority 3 ─────────────────────────────────────────────────────────

    /// <summary>
    /// A symbol-less strippable block carries a <c>// SBW-ORIGIN:</c> anchor naming its artifact; a
    /// diagnostic inside it resolves through that anchor when no symbol and no interval map apply.
    /// </summary>
    [Fact]
    public void SymbolAnchorStep_WithNoSymbolButAnOriginAnchor_ResolvesThroughTheAnchor()
    {
        var artifact = AttributionFixtures.ArtifactForSymbol("SharedHelpers");
        var source = $$"""
            // SBW-ORIGIN: {{artifact.Canonical}}
            enum SharedHelpers {
                static let broken: Missing = fail()
            }
            """;
        var step = new SymbolAnchorProvenanceStep(
            WrapperBlockIndex.Build(source),
            _ => null,                                   // no symbol on this block
            AttributionFixtures.SymbolUnitLookup());

        var diag = ErrorAt("Helpers.wrapper.swift", line: 3, "cannot find type 'Missing'").Primary;

        Assert.True(step.TryResolve(diag, out var hit));
        Assert.Equal(ProvenanceSource.OriginAnchor, hit.Source);
        Assert.Equal(artifact.Decl, hit.Unit.Decl);
    }

    /// <summary>
    /// A diagnostic inside a nested <c>@_silgen_name</c> whose promoted symbol isn't in the registry
    /// must fall back to the enclosing anchored <c>extension</c> header rather than sinking to coarse
    /// module scope. The resolve chain walks containing blocks innermost-first and stops at the first
    /// one that names a resolvable owner — here the inner symbol block misses, so the outer anchor wins.
    /// </summary>
    [Fact]
    public void SymbolAnchorStep_InnerSymbolUnregistered_FallsBackToEnclosingAnchor()
    {
        var artifact = AttributionFixtures.ArtifactForSymbol("DefaultedHasher");
        var source = $$"""
            // SBW-ORIGIN: {{artifact.Canonical}}
            extension Module.DefaultedHasher {
                @_silgen_name("DBW_unregistered")
                public func _dbg_hash(value: Int) -> Int {
                    return brokenCall(value)
                }
            }
            """;
        var step = new SymbolAnchorProvenanceStep(
            WrapperBlockIndex.Build(source),
            symbol => symbol == "DBW_unregistered"
                ? (ArtifactId?)null                      // the nested wrapper symbol isn't registered
                : AttributionFixtures.ArtifactForSymbol(symbol),
            AttributionFixtures.SymbolUnitLookup());

        // Line 5 is inside the inner @_silgen_name function body, not on the extension header.
        var diag = ErrorAt("Mod.wrapper.swift", line: 5, "cannot find 'brokenCall' in scope").Primary;

        Assert.True(step.TryResolve(diag, out var hit));
        Assert.Equal(ProvenanceSource.OriginAnchor, hit.Source);
        Assert.Equal(artifact.Decl, hit.Unit.Decl);
    }

    // ── linker symbol is priority 4 ─────────────────────────────────────────────────────────

    /// <summary>
    /// An undefined-symbol linker error carries no position, only the mangled symbol. The step must
    /// recover the owning unit from the symbol, stripping the linker's leading underscore.
    /// </summary>
    [Fact]
    public void LinkerSymbolStep_MatchesTheUnderscoredSymbolToItsUnit()
    {
        var step = new LinkerSymbolProvenanceStep(
            symbol => symbol == "SBW_Gadget_rotate" ? AttributionFixtures.ArtifactForSymbol(symbol) : (ArtifactId?)null,
            AttributionFixtures.SymbolUnitLookup());

        var diag = CompilerDiagnostic.Global(
            DiagnosticSeverity.Error,
            "Undefined symbols for architecture arm64:\n  \"_SBW_Gadget_rotate\", referenced from:\n      _main");

        Assert.True(step.TryResolve(diag, out var hit));
        Assert.Equal(ProvenanceSource.LinkerSymbol, hit.Source);
        Assert.Equal(AttributionFixtures.UnitForSymbol("SBW_Gadget_rotate"), hit.Unit);
    }

    [Fact]
    public void LinkerSymbolStep_IgnoresPositionedDiagnostics()
    {
        var step = new LinkerSymbolProvenanceStep(
            symbol => AttributionFixtures.ArtifactForSymbol(symbol),
            AttributionFixtures.SymbolUnitLookup());

        var positioned = ErrorAt("X.wrapper.swift", line: 3, "mentions _SBW_Gadget_rotate but has a position").Primary;

        Assert.False(step.TryResolve(positioned, out _));
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────

    private static DiagnosticGroup ErrorAt(string file, int line, string message) => new()
    {
        Primary = new CompilerDiagnostic
        {
            File = file,
            Line = line,
            Column = 1,
            Severity = DiagnosticSeverity.Error,
            Message = message,
        },
    };

    private static (ModuleFragmentSet Set, RecoveryUnitId Unit) BuildFragmentSet(
        string fileName, string content, string ownerSymbol)
    {
        var decl = AttributionFixtures.DeclForSymbol(ownerSymbol);
        var unit = RecoveryUnitId.Create(decl, RecoveryScope.LeafApi);
        var fragment = new OutputFragment
        {
            Owner = new FragmentOwner(ArtifactId.Create(decl, ArtifactRole.SwiftWrapper), unit),
            Plane = OutputPlane.Swift,
            Text = content,
            IsWholeScope = true,
            Depth = 0,
        };
        var set = new ModuleFragmentSet { ModuleName = "Fixture" };
        set.Add(fileName, content, new List<FragmentInterval> { new(fragment, 0, content.Length) });
        return (set, unit);
    }
}
