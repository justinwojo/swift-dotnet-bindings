// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

using Microsoft.Extensions.Logging.Abstractions;

using BindingsGeneration.Diagnostics;

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// The verify-recover loop grades every architecture the shipped wrapper is built for
/// (<see cref="WrapperProbeArchitectures"/>): which architectures it plans, and how their outcomes fold,
/// so that a member failing on any architecture is withdrawn on all of them.
/// </summary>
public class WrapperProbeArchitecturesTests
{
    [Theory]
    [InlineData("auto", "arm64,x86_64", "arm64,x86_64")]
    [InlineData("auto", "x86_64,arm64", "arm64,x86_64")]
    [InlineData("auto", "arm64", "arm64")]
    [InlineData("arm64,x86_64", "arm64,x86_64", "arm64,x86_64")]
    [InlineData("x64", "arm64,x86_64", "arm64,x86_64")]
    [InlineData("arm64", "arm64,x86_64", "arm64")]
    [InlineData(null, "arm64,x86_64", "arm64")]
    [InlineData("", "arm64,x86_64", "arm64")]
    // An explicit architecture the slice lacks, or an invalid token: the post-loop compile owns the
    // diagnostic, so the loop grades the architecture it already compiles.
    [InlineData("arm64,x86_64", "arm64", "arm64")]
    [InlineData("arm64,ppc", "arm64,x86_64", "arm64")]
    public void Plan_GradesEveryArchitectureTheWrapperIsBuiltFor(
        string? targetArchitectures, string sliceArchitectures, string expected)
    {
        var plan = WrapperProbeArchitectures.Plan(
            "arm64", targetArchitectures, sliceArchitectures.Split(','), "ios-arm64_x86_64-simulator");

        Assert.Equal(expected.Split(','), plan);
    }

    [Fact]
    public void Plan_KeepsTheLoopsOwnArchitectureFirst()
    {
        // The first entry is the compile the loop already runs (in place, or the SDK's probe); every
        // other entry compiles in a scratch copy.
        var plan = WrapperProbeArchitectures.Plan("arm64e", "auto", ["arm64e", "x86_64"], "slice");

        Assert.Equal(["arm64e", "x86_64"], plan);
    }

    [Fact]
    public void PlanForSlice_DropsArchitecturesTheSliceCannotBuild()
    {
        Assert.Equal(["arm64", "x86_64"],
            WrapperProbeArchitectures.PlanForSlice("arm64", "arm64", ["x86_64"], ["arm64", "x86_64"]));
        Assert.Equal(["arm64"],
            WrapperProbeArchitectures.PlanForSlice("arm64", "arm64", ["x86_64"], ["arm64"]));
        Assert.Equal(["arm64"],
            WrapperProbeArchitectures.PlanForSlice("arm64", null, [], ["arm64", "x86_64"]));
    }

    [Fact]
    public void Fold_AFailureOnAnExtraArchitectureFailsTheRound_WithThatArchitecturesDiagnostics()
    {
        var x64Error = Error("Wrapper.swift", 12, "cannot find 'onlyOnArm' in scope");
        var outcome = WrapperProbeArchitectures.Fold(
        [
            ("arm64", Clean("sim", Provenance("Wrapper.swift", "/out/Wrapper.swift"))),
            ("x86_64", Failed("sim", x64Error, Provenance("Wrapper.swift", "/scratch/Wrapper.swift"))),
        ]);

        Assert.False(outcome.AllSlicesClean);
        Assert.Null(outcome.Result);
        Assert.Equal([x64Error], outcome.Diagnostics);
        Assert.Equal(["sim", "sim@x86_64"], outcome.Slices.Select(s => s.SliceId));
        Assert.Equal([true, false], outcome.Slices.Select(s => s.Succeeded));

        // One provenance entry per file, reachable from either architecture's compile path.
        var file = Assert.Single(outcome.FileProvenance);
        Assert.Equal(["/scratch/Wrapper.swift", "/out/Wrapper.swift"], file.CompileInputPaths);
    }

    [Fact]
    public void Fold_UnionsTheDiagnosticsOfEveryFailingArchitecture()
    {
        var armError = Error("A.swift", 3, "arm failure");
        var x64Error = Error("B.swift", 7, "x86_64 failure");
        var outcome = WrapperProbeArchitectures.Fold(
        [
            ("arm64", Failed("sim", armError, Provenance("A.swift", "/out/A.swift"))),
            ("x86_64", Failed("sim", x64Error, Provenance("B.swift", "/scratch/B.swift"))),
        ]);

        Assert.Equal([armError, x64Error], outcome.Diagnostics);
        Assert.Equal(["A.swift", "B.swift"], outcome.FileProvenance.Select(p => p.FileName));
    }

    [Fact]
    public void Fold_CleanOnEveryArchitecture_CarriesTheFirstArchitecturesResult()
    {
        var primary = Clean("sim", Provenance("Wrapper.swift", "/out/Wrapper.swift"));
        var outcome = WrapperProbeArchitectures.Fold(
        [
            ("arm64", primary),
            ("x86_64", Clean("sim", Provenance("Wrapper.swift", "/scratch/Wrapper.swift"))),
        ]);

        Assert.True(outcome.AllSlicesClean);
        Assert.Same(primary.FileProvenance, outcome.FileProvenance);
        Assert.Equal(["sim", "sim@x86_64"], outcome.Slices.Select(s => s.SliceId));
    }

    [Fact]
    public void Fold_SingleArchitectureOrNoWrapperSurface_IsTheFirstOutcomeUnchanged()
    {
        var only = Failed("sim", Error("A.swift", 1, "e"), Provenance("A.swift", "/out/A.swift"));
        Assert.Same(only, WrapperProbeArchitectures.Fold([("arm64", only)]));

        var none = WrapperCompileDiagnostics.NoWrapperSurfaceOutcome("no wrapper files", null, []);
        Assert.Same(none, WrapperProbeArchitectures.Fold(
            [("arm64", none), ("x86_64", Clean("sim", Provenance("A.swift", "/s/A.swift")))]));
    }

    [Fact]
    public void Probe_CompilesEachArchitectureOnce_AndLeavesAnExtraThatThrowsToTheWrapperCompile()
    {
        var compiled = new List<string>();
        var outcome = WrapperProbeArchitectures.Probe(
            ["arm64", "x86_64"],
            architecture =>
            {
                compiled.Add(architecture);
                return architecture == "arm64"
                    ? Clean("sim", Provenance("A.swift", "/out/A.swift"))
                    : throw new InvalidOperationException("no x86_64 slice");
            },
            NullLogger.Instance);

        Assert.Equal(["arm64", "x86_64"], compiled);
        Assert.True(outcome.AllSlicesClean);
        Assert.Equal(["sim"], outcome.Slices.Select(s => s.SliceId));
    }

    [Fact]
    public void Probe_AnExtraFailureWithoutACompilerError_IsLeftToTheWrapperCompile()
    {
        // A swiftc timeout on the extra architecture: recorded as a failed slice with no diagnostic.
        // It names no member, so it must not fail the round the primary passed.
        var outcome = WrapperProbeArchitectures.Probe(
            ["arm64", "x86_64"],
            architecture => architecture == "arm64"
                ? Clean("sim", Provenance("A.swift", "/out/A.swift"))
                : WrapperCompileDiagnostics.Failed(
                    [new WrapperSliceDiagnostics("sim", false, [])], [Provenance("A.swift", "/scratch/A.swift")]),
            NullLogger.Instance);

        Assert.True(outcome.AllSlicesClean);
        Assert.Equal(["sim"], outcome.Slices.Select(s => s.SliceId));
    }

    [Fact]
    public void Probe_AnExtraFailureWithACompilerError_FailsTheRound()
    {
        var x64Error = Error("A.swift", 4, "cannot find 'onlyOnArm' in scope");
        var outcome = WrapperProbeArchitectures.Probe(
            ["arm64", "x86_64"],
            architecture => architecture == "arm64"
                ? Clean("sim", Provenance("A.swift", "/out/A.swift"))
                : Failed("sim", x64Error, Provenance("A.swift", "/scratch/A.swift")),
            NullLogger.Instance);

        Assert.False(outcome.AllSlicesClean);
        Assert.Equal([x64Error], outcome.Diagnostics);
    }

    [Fact]
    public void Probe_APrimaryCompileThatThrows_Propagates()
    {
        Assert.Throws<InvalidOperationException>(() => WrapperProbeArchitectures.Probe(
            ["arm64", "x86_64"],
            _ => throw new InvalidOperationException("swiftc missing"),
            NullLogger.Instance));
    }

    private static DiagnosticGroup Error(string file, int line, string message) => new()
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

    private static WrapperFileProvenance Provenance(string fileName, string compilePath) =>
        new(fileName, "pre", "post", null, false) { CompileInputPaths = [compilePath] };

    private static WrapperCompileDiagnostics Clean(string sliceId, WrapperFileProvenance provenance) =>
        WrapperCompileDiagnostics.Clean(null, [new WrapperSliceDiagnostics(sliceId, true, [])], [provenance]);

    private static WrapperCompileDiagnostics Failed(string sliceId, DiagnosticGroup error, WrapperFileProvenance provenance) =>
        WrapperCompileDiagnostics.Failed([new WrapperSliceDiagnostics(sliceId, false, [error])], [provenance]);
}
