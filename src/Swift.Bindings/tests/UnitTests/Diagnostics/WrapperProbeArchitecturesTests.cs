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

    [Theory]
    // An explicit list that leaves out the architecture the slice resolved to ships no wrapper for it,
    // so the loop grades only the listed architectures, the primary first.
    [InlineData("x86_64", "x86_64")]
    [InlineData("x64", "x86_64")]
    public void Plan_AnExplicitListGradesOnlyTheListedArchitectures(string targetArchitectures, string expected)
    {
        var plan = WrapperProbeArchitectures.Plan(
            "arm64", targetArchitectures, ["arm64", "x86_64"], "ios-arm64_x86_64-simulator");

        Assert.Equal(expected.Split(','), plan);
    }

    [Theory]
    // An arm-only source under auto, or no list, pins nothing: each slice picks its own architecture.
    [InlineData("auto", "arm64", null)]
    [InlineData(null, "arm64,x86_64", null)]
    [InlineData("auto", "arm64,x86_64", "arm64")]
    [InlineData("x86_64", "arm64,x86_64", "x86_64")]
    public void DecidedPrimary_IsThePinTheShippingCompilePassesToEverySlice(
        string? targetArchitectures, string sliceArchitectures, string? expected)
    {
        Assert.Equal(expected, WrapperProbeArchitectures.DecidedPrimary(
            targetArchitectures, sliceArchitectures.Split(','), "ios-arm64_x86_64-simulator"));
    }

    [Theory]
    // An x86_64 + arm64e slice resolves to x86_64, while shipping pins arm64e first.
    [InlineData(true, "arm64e")]
    [InlineData(false, "x86_64")]
    public void IsPrimaryPass_FollowsTheShippingPinWhenThePassCompilesBothSlices(bool compilesBothSlices, string primary)
    {
        string[] plan = ["arm64e", "x86_64"];
        Assert.Equal([primary], plan.Where(a => WrapperProbeArchitectures.IsPrimaryPass(a, plan, "x86_64", compilesBothSlices)));
    }

    [Theory]
    // The SDK builds the other slice, and so does a run that collects the lowering receipt.
    [InlineData(true, false, true, false, "simulator")]
    [InlineData(true, false, false, true, "simulator")]
    [InlineData(true, true, true, false, "device")]
    [InlineData(true, true, false, true, "device")]
    // validate's direct passes ship one slice each; macOS and Mac Catalyst have one slice.
    [InlineData(true, false, false, false, null)]
    [InlineData(true, true, false, false, null)]
    [InlineData(false, false, true, true, null)]
    public void OtherShippedSlice_IsTheSliceAnAppleFrameworkWrapperShipsBesideTheProbedOne(
        bool platformHasSimulator, bool probedSliceIsSimulator, bool sdkMode, bool collectsLoweringEvidence,
        string? expected)
    {
        Assert.Equal(expected, WrapperProbeArchitectures.OtherShippedSlice(
            platformHasSimulator, probedSliceIsSimulator, sdkMode, collectsLoweringEvidence));
    }

    [Fact]
    public void PlanForAppleFramework_DeviceFirst_GradesTheIntelSimulatorPass()
    {
        // SwiftPlatformTarget=device, SwiftTargetArchitectures=arm64,x86_64: the device slice builds arm64
        // alone, and the simulator slice the SDK (or the lowering run) builds is arm64 + x86_64. Before,
        // the loop graded the device's arm64 only.
        var passes = WrapperProbeArchitectures.PlanForAppleFramework(
            ["arm64"], WrapperProbeArchitectures.SimulatorSlice, "arm64", ["x86_64"],
            "arm64", ["arm64", "x86_64"], "arm64");

        Assert.Equal(["arm64", "simulator-arm64", "simulator-x86_64"], passes.Select(p => p.Label));
        Assert.Equal([null, "simulator", "simulator"], passes.Select(p => p.Slice));
    }

    [Fact]
    public void PlanForAppleFramework_SimulatorFirst_GradesTheDeviceSlice_AndNoOtherSliceGradesNothingMore()
    {
        Assert.Equal(["arm64", "x86_64", "device-arm64"], WrapperProbeArchitectures.PlanForAppleFramework(
            ["arm64", "x86_64"], WrapperProbeArchitectures.DeviceSlice, "arm64", ["x86_64"],
            "arm64", ["arm64", "x86_64"], "arm64").Select(p => p.Label));
        Assert.Equal(["arm64"], WrapperProbeArchitectures.PlanForAppleFramework(
            ["arm64"], null, "arm64", ["x86_64"], "arm64", ["arm64", "x86_64"], "arm64").Select(p => p.Label));
        // A simulator pinned to arm64 builds no Intel pass.
        Assert.Equal(["arm64", "simulator-arm64"], WrapperProbeArchitectures.PlanForAppleFramework(
            ["arm64"], WrapperProbeArchitectures.SimulatorSlice, "arm64", [],
            "arm64", ["arm64", "x86_64"], "arm64").Select(p => p.Label));
    }

    [Fact]
    public void Probe_ADeviceSliceFailureWithACompilerError_FailsTheRound_UnderItsOwnSliceLabel()
    {
        var deviceError = Error("A.swift", 7, "'onlyInSimulator' is unavailable in iOS");
        var compiled = new List<string>();
        var outcome = WrapperProbeArchitectures.Probe(
            WrapperProbeArchitectures.Passes(["arm64"], WrapperProbeArchitectures.DeviceSlice, ["arm64"]),
            pass =>
            {
                compiled.Add(pass.Label);
                return pass.Slice is null
                    ? Clean("ios-arm64-simulator", Provenance("A.swift", "/out/A.swift"))
                    : Failed("ios-arm64", deviceError, Provenance("A.swift", "/scratch/A.swift"));
            },
            NullLogger.Instance);

        Assert.Equal(["arm64", "device-arm64"], compiled);
        Assert.False(outcome.AllSlicesClean);
        Assert.Equal([deviceError], outcome.Diagnostics);
        Assert.Contains("ios-arm64@device-arm64", outcome.Slices.Select(s => s.SliceId));
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

    [Theory]
    [InlineData("no such module 'Dependency'")]
    [InlineData("could not find module 'Dependency' for target 'x86_64-apple-ios-simulator'; found: arm64-apple-ios-simulator")]
    public void Probe_AnExtraFailureFromAMissingModule_IsLeftToTheWrapperCompile(string message)
    {
        // A dependency that ships no x86_64 slice: the extra architecture cannot build at all, which the
        // wrapper compile handles; the probe must not turn it into a failed round.
        var outcome = WrapperProbeArchitectures.Probe(
            ["arm64", "x86_64"],
            architecture => architecture == "arm64"
                ? Clean("sim", Provenance("A.swift", "/out/A.swift"))
                : Failed("sim", Error("A.swift", 2, message), Provenance("A.swift", "/scratch/A.swift")),
            NullLogger.Instance);

        Assert.True(outcome.AllSlicesClean);
    }

    [Theory]
    [InlineData("cannot find 'onlyOnArm' in scope")]
    // A member's own diagnostic that quotes the missing-module wording is still the member's.
    [InlineData("'legacy()' is unavailable: could not find module 'Old' for target 'x86_64'")]
    public void Probe_AnExtraFailureWithACompilerError_FailsTheRound(string message)
    {
        var x64Error = Error("A.swift", 4, message);
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
