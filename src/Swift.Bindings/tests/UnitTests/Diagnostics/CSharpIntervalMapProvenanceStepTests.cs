// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;

using BindingsGeneration;
using BindingsGeneration.Diagnostics;

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// The tree-based C# attribution primitive: a positioned Roslyn/SARIF diagnostic resolved through the
/// same per-render interval map the Swift wrapper loop uses, selecting the C# plane. This is what makes
/// a C# compile error land on the exact emitted member fragment whose owner carries the recovery unit
/// the loop withdraws — the union-compatible id, by <see cref="RecoveryUnitClassifier"/> role-collapse.
/// </summary>
/// <remarks>
/// Two behaviours are load-bearing and different from the Swift step, so each gets its own pin: the
/// column is read as a UTF-16 character column (Roslyn/SARIF report characters, not swiftc's UTF-8
/// bytes), and a hit whose fragment is on the Swift plane is rejected outright — a C# diagnostic can
/// only be about C#, so a Swift-plane match means the wrong file matched by leaf name.
/// </remarks>
public class CSharpIntervalMapProvenanceStepTests
{
    // ── the C#-plane resolution the loop depends on ──────────────────────────────────────────────

    [Fact]
    public void TryResolve_OnACSharpPlaneFragment_NamesItsOwningUnit()
    {
        const string content = "public int BrokenMember() => Nope;\n";
        var owner = CSharpLeafOwner("BrokenMember");
        var set = SingleFragmentSet("Broken.cs", content, owner, OutputPlane.CSharp);
        var step = new CSharpIntervalMapProvenanceStep(set);

        // A CS0103 "name 'Nope' does not exist" would anchor at the identifier — inside the member.
        var diagnostic = At("Broken.cs", line: 1, column: 30);

        Assert.True(step.TryResolve(diagnostic, out var hit));
        Assert.Equal(owner.Artifact, hit.Artifact);
        Assert.Equal(owner.Unit, hit.Unit);
        Assert.Equal(RecoveryScope.LeafApi, hit.Unit.Scope);
        Assert.Equal(ProvenanceSource.IntervalMap, hit.Source);
    }

    // ── the Swift-plane rejection: a C# diagnostic is never about Swift ──────────────────────────

    [Fact]
    public void TryResolve_OnASwiftPlaneFragment_ReturnsFalse()
    {
        // A Swift-plane fragment sharing the leaf name is the wrong file matched by name; a C#
        // diagnostic cannot be about it, so the step declines rather than mis-attributing.
        const string content = "@_cdecl(\"SBW_x\") func x() {}\n";
        var owner = CSharpLeafOwner("x");
        var set = SingleFragmentSet("x.swift", content, owner, OutputPlane.Swift);
        var step = new CSharpIntervalMapProvenanceStep(set);

        Assert.False(step.TryResolve(At("x.swift", line: 1, column: 5), out var hit));
        Assert.Equal(default, hit);
    }

    // ── positionless / unmapped: fall through to no resolution (fail-closed upstream) ────────────

    [Fact]
    public void TryResolve_PositionlessDiagnostic_ReturnsFalse()
    {
        var owner = CSharpLeafOwner("member");
        var set = SingleFragmentSet("Any.cs", "int member;\n", owner, OutputPlane.CSharp);
        var step = new CSharpIntervalMapProvenanceStep(set);

        // A project-level diagnostic (restore, missing reference) carries no location.
        var positionless = CompilerDiagnostic.Global(DiagnosticSeverity.Error, "CS0234: no such member");

        Assert.False(step.TryResolve(positionless, out _));
    }

    [Fact]
    public void TryResolve_DiagnosticInAFileNotInTheSet_ReturnsFalse()
    {
        var owner = CSharpLeafOwner("member");
        var set = SingleFragmentSet("Known.cs", "int member;\n", owner, OutputPlane.CSharp);
        var step = new CSharpIntervalMapProvenanceStep(set);

        Assert.False(step.TryResolve(At("Unknown.cs", line: 1, column: 1), out _));
    }

    [Fact]
    public void TryResolve_ColumnPastTheEndOfTheFile_ReturnsFalse()
    {
        var owner = CSharpLeafOwner("member");
        const string content = "int member;\n";
        var set = SingleFragmentSet("Known.cs", content, owner, OutputPlane.CSharp);
        var step = new CSharpIntervalMapProvenanceStep(set);

        // Line 2 does not exist (the file is one line); the map cannot resolve it.
        Assert.False(step.TryResolve(At("Known.cs", line: 2, column: 1), out _));
    }

    // ── UTF-16 characters, not UTF-8 bytes ───────────────────────────────────────────────────────

    /// <summary>
    /// Roslyn/SARIF report character columns; swiftc reports UTF-8 byte columns. The two agree on
    /// pure-ASCII lines — almost every generated line — so a test on generated source alone cannot tell
    /// them apart. This builds a line where they genuinely disagree and pins that the C# step resolves
    /// through the <em>character</em> reading: a column that lands in the second fragment by character
    /// count but the first by byte count must name the second fragment's owner.
    /// </summary>
    [Fact]
    public void TryResolve_UsesUtf16CharacterColumns_NotUtf8ByteColumns()
    {
        // "α β γ " is 6 characters but 9 UTF-8 bytes, so a position at character column 10 is character
        // 9 — inside the SECOND fragment — while byte column 10 would be character 7, in the first.
        const string content = "α β γ XYZabcdef\n";
        var first = Fragment(content[..8], CSharpLeafOwner("first"), OutputPlane.CSharp);
        var second = Fragment(content[8..], CSharpLeafOwner("second"), OutputPlane.CSharp);

        var set = new ModuleFragmentSet { ModuleName = "Fixture" };
        set.Add("Sample.cs", content, new List<FragmentInterval>
        {
            new(first, 0, 8),
            new(second, 8, content.Length),
        });
        var step = new CSharpIntervalMapProvenanceStep(set);

        Assert.True(step.TryResolve(At("Sample.cs", line: 1, column: 10), out var hit));
        // Character-column reading lands in the second fragment. A byte-column reading would have named
        // the first fragment's owner — the bug this pins against.
        Assert.Equal(second.Owner.Unit, hit.Unit);
        Assert.NotEqual(first.Owner.Unit, hit.Unit);
    }

    // ── guards ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Constructor_NullFragmentSet_Throws()
    {
        Assert.Throws<System.ArgumentNullException>(() => new CSharpIntervalMapProvenanceStep(null!));
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────

    private static FragmentOwner CSharpLeafOwner(string memberName) =>
        FragmentOwners.ForDeclId(
            DeclId.Create("Fixture", "T", BindingItemKind.Method, memberName), ArtifactRole.CSharpPublic);

    private static OutputFragment Fragment(string text, FragmentOwner owner, OutputPlane plane) => new()
    {
        Owner = owner,
        Plane = plane,
        Text = text,
        IsWholeScope = true,
        Depth = 0,
    };

    private static ModuleFragmentSet SingleFragmentSet(
        string fileName, string content, FragmentOwner owner, OutputPlane plane)
    {
        var set = new ModuleFragmentSet { ModuleName = "Fixture" };
        set.Add(fileName, content, new List<FragmentInterval>
        {
            new(Fragment(content, owner, plane), 0, content.Length),
        });
        return set;
    }

    private static CompilerDiagnostic At(string file, int line, int column) => new()
    {
        File = file,
        Line = line,
        Column = column,
        Severity = DiagnosticSeverity.Error,
        Message = "test diagnostic",
    };
}
