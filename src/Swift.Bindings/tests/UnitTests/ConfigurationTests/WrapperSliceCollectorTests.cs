// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
using System.Linq;

using BindingsGeneration.Diagnostics;

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Pins the convergence predicate <see cref="WrapperSliceCollector.ToDiagnostics"/> folds recorded slices
/// into. The defect this closes: a collector that recorded ZERO slices and never failed used to return
/// <see cref="WrapperCompileDiagnostics.Clean"/> (<c>!AnyFailed</c>), silently shipping an UNVERIFIED
/// wrapper. Clean now requires positive evidence (≥1 recorded success, no failure); a zero-slice compile
/// is either the explicit no-wrapper-surface outcome or a fail-closed synthesized error.
/// </summary>
public class WrapperSliceCollectorTests
{
    private static SwiftWrapperCompilationResult Result() => new()
    {
        XCFrameworkPath = "/tmp/Test.xcframework",
        CompiledFileCount = 1,
        StrippedBlockCount = 0,
    };

    private static IReadOnlyList<DiagnosticGroup> Errors(string message) => new[]
    {
        new DiagnosticGroup { Primary = CompilerDiagnostic.Global(DiagnosticSeverity.Error, message) },
    };

    [Fact]
    public void ToDiagnostics_ZeroSlices_NoSignal_FailsClosedRatherThanFoldingToClean()
    {
        // The headline fix: nothing recorded, nothing signalled → NOT clean. There is no evidence any
        // promised slice was accepted, so refuse to treat the wrapper as verified. Carries a coherent,
        // non-empty diagnostic so the controller reads an unattributed failure rather than a silent fold.
        var collector = new WrapperSliceCollector();

        var diagnostics = collector.ToDiagnostics(Result());

        Assert.False(diagnostics.AllSlicesClean);
        Assert.False(diagnostics.NoWrapperSurface);
        Assert.Null(diagnostics.Result);
        Assert.NotEmpty(diagnostics.Diagnostics);
    }

    [Fact]
    public void ToDiagnostics_NoWrapperSurfaceSignal_ReturnsDistinctOutcome_CarryingResult()
    {
        var collector = new WrapperSliceCollector();
        collector.MarkNoWrapperSurface("no wrapper source files or thunk assembly were emitted");
        var result = Result();

        var diagnostics = collector.ToDiagnostics(result);

        Assert.True(diagnostics.NoWrapperSurface);
        Assert.False(diagnostics.AllSlicesClean);
        Assert.Empty(diagnostics.Slices);
        Assert.Same(result, diagnostics.Result);
        Assert.Equal("no wrapper source files or thunk assembly were emitted", diagnostics.NoWrapperSurfaceReason);
    }

    [Fact]
    public void ToDiagnostics_OneRecordedSuccess_IsClean()
    {
        var collector = new WrapperSliceCollector();
        collector.RecordSuccess("ios-arm64-simulator");
        var result = Result();

        var diagnostics = collector.ToDiagnostics(result);

        Assert.True(diagnostics.AllSlicesClean);
        Assert.False(diagnostics.NoWrapperSurface);
        Assert.Same(result, diagnostics.Result);
    }

    [Fact]
    public void ToDiagnostics_RecordedFailure_IsFailed_DroppingResult()
    {
        var collector = new WrapperSliceCollector();
        collector.RecordFailure("ios-arm64-simulator", Errors("swiftc: broken wrapper"));

        var diagnostics = collector.ToDiagnostics(Result());

        Assert.False(diagnostics.AllSlicesClean);
        Assert.False(diagnostics.NoWrapperSurface);
        Assert.Null(diagnostics.Result);
        Assert.Contains(diagnostics.Diagnostics, g => g.Primary.Message.Contains("broken wrapper"));
    }

    [Fact]
    public void ToDiagnostics_SuccessThenFailure_IsFailed()
    {
        // A per-arch fat compile where the simulator slice passed but the device slice failed must fail
        // closed: target-slice consistency means a unit that fails on ANY promised slice is not clean.
        var collector = new WrapperSliceCollector();
        collector.RecordSuccess("ios-arm64-simulator");
        collector.RecordFailure("ios-arm64", Errors("swiftc: device-only failure"));

        var diagnostics = collector.ToDiagnostics(Result());

        Assert.False(diagnostics.AllSlicesClean);
        Assert.False(diagnostics.NoWrapperSurface);
        Assert.Null(diagnostics.Result);
    }

    [Fact]
    public void ToDiagnostics_NoWrapperSurfaceSignal_WithRecordedSlice_FailsClosed()
    {
        // A no-wrapper-surface signal alongside a recorded slice is a contradiction (the signal is raised
        // only at a genuine no-source bail, before any slice is attempted). Trust neither claim: fail
        // closed rather than ship on an inconsistent record.
        var collector = new WrapperSliceCollector();
        collector.RecordSuccess("ios-arm64-simulator");
        collector.MarkNoWrapperSurface("inconsistent");

        var diagnostics = collector.ToDiagnostics(Result());

        Assert.False(diagnostics.NoWrapperSurface);
        Assert.False(diagnostics.AllSlicesClean);
        Assert.Null(diagnostics.Result);
    }

    [Fact]
    public void MarkNoWrapperSurface_FirstWriterWins()
    {
        var collector = new WrapperSliceCollector();
        collector.MarkNoWrapperSurface("first");
        collector.MarkNoWrapperSurface("second");

        var diagnostics = collector.ToDiagnostics(result: null);

        Assert.True(diagnostics.NoWrapperSurface);
        Assert.Equal("first", diagnostics.NoWrapperSurfaceReason);
    }

    [Fact]
    public void NoWrapperSurfaceOutcome_ReportsNeitherCleanNorFailedSlices()
    {
        var diagnostics = WrapperCompileDiagnostics.NoWrapperSurfaceOutcome(
            "reason", result: null, System.Array.Empty<WrapperFileProvenance>());

        Assert.True(diagnostics.NoWrapperSurface);
        Assert.False(diagnostics.AllSlicesClean);
        Assert.Empty(diagnostics.Diagnostics);
    }
}
