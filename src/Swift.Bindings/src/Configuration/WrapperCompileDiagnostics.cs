// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;

using BindingsGeneration.Diagnostics;

namespace BindingsGeneration;

/// <summary>
/// The structured outcome of compiling one promised wrapper slice: whether swiftc accepted it, and —
/// when it did not — the diagnostics parsed from that slice's own stderr.
/// </summary>
/// <remarks>
/// A slice is "promised" when the compile contract requires it (the simulator slice always; the
/// device slice when a device resolution is supplied). A promised slice that throws for any reason —
/// a swiftc error, a failed thunk link, a missing SDK — is recorded as <see cref="Succeeded"/> false
/// with whatever diagnostics could be recovered, and the compile moves on to the next promised slice
/// rather than aborting the whole build at the first failure. That is what lets the verify-recover
/// loop see a device-only failure the simulator slice masked.
/// </remarks>
public sealed record WrapperSliceDiagnostics(
    string SliceId,
    bool Succeeded,
    IReadOnlyList<DiagnosticGroup> Diagnostics);

/// <summary>
/// The union outcome of a recovery-mode wrapper compile across every promised slice: whether they all
/// compiled clean, the union of their diagnostics, the per-slice breakdown, and — only when the whole
/// set compiled clean — the compilation result carrying the promoted xcframework.
/// </summary>
/// <remarks>
/// <para>
/// This is the shape the verify-recover driver consumes. <see cref="AllSlicesClean"/> is the loop's
/// converged signal; <see cref="Diagnostics"/> is the cross-slice union the attributor is handed, so a
/// unit that fails on <em>any</em> required slice is withdrawn on <em>every</em> slice (target-slice
/// consistency); <see cref="Result"/> is non-null exactly when the compile settled a promotable
/// wrapper, so the caller never mistakes a partial staging tree for a shippable one.
/// </para>
/// <para>
/// A recovery-mode compile never promotes a partial staging tree: if any promised slice failed, the
/// staging tree is dropped and <see cref="Result"/> is null, so the only artifact that ever reaches
/// the canonical path is one every promised slice accepted.
/// </para>
/// <para>
/// <see cref="NoWrapperSurface"/> is a THIRD terminal outcome, distinct from both clean and failed: the
/// module emitted no compilable wrapper surface at all (no wrapper source files were produced), so there
/// was nothing to verify — not a clean compile of zero slices. Keeping it separate is what stops a
/// recorded-zero-slices compile from silently folding to "clean" (<see cref="AllSlicesClean"/>) and
/// shipping an unverified wrapper. Whether a no-wrapper-surface binding is shippable is decided
/// downstream from the emitted C# surface (the usable-surface gate), not from the wrapper compile.
/// </para>
/// </remarks>
public sealed record WrapperCompileDiagnostics(
    bool AllSlicesClean,
    IReadOnlyList<DiagnosticGroup> Diagnostics,
    IReadOnlyList<WrapperSliceDiagnostics> Slices,
    SwiftWrapperCompilationResult? Result,
    IReadOnlyList<WrapperFileProvenance> FileProvenance)
{
    /// <summary>
    /// True when the module had no compilable wrapper surface to verify (no wrapper source files were
    /// emitted). Mutually exclusive with <see cref="AllSlicesClean"/> and with any recorded slice: the
    /// compile never reached a slice. The driver converges on this without attributing a failure.
    /// </summary>
    public bool NoWrapperSurface { get; init; }

    /// <summary>The reason a no-wrapper-surface outcome was raised, for the report; null otherwise.</summary>
    public string? NoWrapperSurfaceReason { get; init; }

    /// <summary>Builds a clean (all-slices-passed) outcome carrying the promoted compilation result.</summary>
    public static WrapperCompileDiagnostics Clean(
        SwiftWrapperCompilationResult? result,
        IReadOnlyList<WrapperSliceDiagnostics> slices,
        IReadOnlyList<WrapperFileProvenance> fileProvenance) =>
        new(true, Array.Empty<DiagnosticGroup>(), slices, result, fileProvenance);

    /// <summary>
    /// Builds a failed outcome from the per-slice results, unioning every failing slice's diagnostics
    /// in slice order. <see cref="Result"/> is null: a failed compile has no promotable wrapper.
    /// <paramref name="fileProvenance"/> carries the pre/post-strip bytes the driver remaps the failing
    /// diagnostics against, captured before the staging tree was dropped.
    /// </summary>
    public static WrapperCompileDiagnostics Failed(
        IReadOnlyList<WrapperSliceDiagnostics> slices,
        IReadOnlyList<WrapperFileProvenance> fileProvenance)
    {
        ArgumentNullException.ThrowIfNull(slices);
        var union = slices.Where(s => !s.Succeeded).SelectMany(s => s.Diagnostics).ToList();
        return new WrapperCompileDiagnostics(false, union, slices, null, fileProvenance);
    }

    /// <summary>
    /// Builds the no-wrapper-surface outcome: the module needed no wrapper, so there is nothing to
    /// compile and nothing failed. <see cref="AllSlicesClean"/> is false (it is not a clean compile of
    /// slices), <see cref="NoWrapperSurface"/> is set, and <see cref="Slices"/> is empty. The result is
    /// carried through unchanged so a caller can still record whatever the degenerate producer returned.
    /// </summary>
    public static WrapperCompileDiagnostics NoWrapperSurfaceOutcome(
        string reason,
        SwiftWrapperCompilationResult? result,
        IReadOnlyList<WrapperFileProvenance> fileProvenance) =>
        new(false, Array.Empty<DiagnosticGroup>(), Array.Empty<WrapperSliceDiagnostics>(), result, fileProvenance)
        {
            NoWrapperSurface = true,
            NoWrapperSurfaceReason = reason,
        };
}

/// <summary>
/// A single wrapper Swift file's compile provenance, captured in recovery mode before the transient
/// <c>.wrapper-build</c> staging tree is deleted: the pre-strip bytes the emission fragment map was
/// built against, the post-strip bytes the post-processor actually handed swiftc, the line-origin
/// vector that ties the two together, and whether the simulator-guard pass rewrote the file after the
/// strip.
/// </summary>
/// <remarks>
/// This is the exact input <c>WrapperStripRemap.Remap</c> needs to recompute a file's intervals onto
/// the bytes swiftc compiled, so a diagnostic's line/column resolves against the right fragment. A
/// file the guard pass rewrote (<see cref="GuardRewrote"/> true) carries no provenance for its inserted
/// <c>#if</c> lines, so it must be treated as UNMAPPED at attribution time and resolved through the
/// symbol/anchor index instead — the remap deliberately does not cover that pass.
/// </remarks>
public sealed record WrapperFileProvenance(
    string FileName,
    string PreStripContent,
    string PostStripContent,
    IReadOnlyList<int>? CleanedLineSources,
    bool GuardRewrote);

/// <summary>
/// The mutable per-slice accumulator a recovery-mode wrapper compile writes into: one
/// <see cref="WrapperSliceDiagnostics"/> per promised slice, in the order the slices are compiled.
/// </summary>
/// <remarks>
/// Passing one of these into <c>CompileAll</c> / <c>CompileSlice</c> switches those methods from
/// their default first-failure-abort behavior into recovery mode: a promised slice that throws is
/// recorded here (never rethrown) and the compile continues to the next slice, so a device-only
/// failure the simulator slice masked is still observed. When <see cref="AnyFailed"/> is set, the
/// compile refuses to promote the staging tree — the only artifact that ever reaches the canonical
/// path is one every promised slice accepted. When the accumulator is absent (null), the compile
/// keeps its exact throw-on-first-failure path, byte for byte.
/// </remarks>
public sealed class WrapperSliceCollector
{
    private readonly List<WrapperSliceDiagnostics> _slices = new();
    private readonly Dictionary<string, WrapperFileProvenance> _fileProvenance = new(StringComparer.Ordinal);

    /// <summary>The recorded per-slice outcomes, in compile order.</summary>
    public IReadOnlyList<WrapperSliceDiagnostics> Slices => _slices;

    /// <summary>
    /// The per-file compile provenance captured this compile, keyed by file name — the remap inputs
    /// the verify-recover driver rebuilds its interval map from. Empty on a non-recovery compile.
    /// </summary>
    public IReadOnlyCollection<WrapperFileProvenance> FileProvenance => _fileProvenance.Values;

    /// <summary>True once any promised slice has been recorded as failed.</summary>
    public bool AnyFailed { get; private set; }

    private string? _noWrapperSurfaceReason;

    /// <summary>
    /// True once <see cref="MarkNoWrapperSurface"/> recorded that this module has no compilable wrapper
    /// surface to verify — no wrapper source files were emitted, so the compile bailed before any slice.
    /// </summary>
    public bool NoWrapperSurface => _noWrapperSurfaceReason != null;

    /// <summary>
    /// Records that the compile found no wrapper surface to build — a genuine no-source bail point, not
    /// a strip-to-empty (that keeps zero slices and fails closed). First-writer-wins so the earliest
    /// reason survives. This is what lets <see cref="ToDiagnostics"/> report a distinct no-wrapper-surface
    /// outcome instead of folding a zero-slice compile to "clean".
    /// </summary>
    public void MarkNoWrapperSurface(string reason) => _noWrapperSurfaceReason ??= reason;

    /// <summary>Records that <paramref name="sliceId"/> compiled clean.</summary>
    public void RecordSuccess(string sliceId) =>
        _slices.Add(new WrapperSliceDiagnostics(sliceId, true, Array.Empty<DiagnosticGroup>()));

    /// <summary>Records that <paramref name="sliceId"/> failed to compile, with its parsed diagnostics.</summary>
    public void RecordFailure(string sliceId, IReadOnlyList<DiagnosticGroup> diagnostics)
    {
        _slices.Add(new WrapperSliceDiagnostics(sliceId, false, diagnostics));
        AnyFailed = true;
    }

    /// <summary>
    /// Records a wrapper file's pre-strip/post-strip bytes and line-origin vector, captured at the
    /// post-processing site before the staging tree is dropped. Idempotent per file name (the strip is
    /// deterministic across per-arch compile passes, so a later pass replaces the identical earlier
    /// entry). Preserves an already-set <see cref="WrapperFileProvenance.GuardRewrote"/> flag so a
    /// re-record cannot clear it.
    /// </summary>
    public void RecordFileProvenance(
        string fileName, string preStripContent, string postStripContent,
        IReadOnlyList<int>? cleanedLineSources)
    {
        var guardRewrote = _fileProvenance.TryGetValue(fileName, out var existing) && existing.GuardRewrote;
        _fileProvenance[fileName] = new WrapperFileProvenance(
            fileName, preStripContent, postStripContent, cleanedLineSources, guardRewrote);
    }

    /// <summary>
    /// Marks that the simulator-guard pass rewrote <paramref name="fileName"/> after the strip, so the
    /// driver treats it as unmapped (its inserted <c>#if</c> lines have no remap provenance). A no-op
    /// when the file has no recorded provenance (fully stripped, never written).
    /// </summary>
    public void MarkGuardRewrote(string fileName)
    {
        if (_fileProvenance.TryGetValue(fileName, out var existing) && !existing.GuardRewrote)
            _fileProvenance[fileName] = existing with { GuardRewrote = true };
    }

    /// <summary>
    /// Folds the recorded slices into the union outcome the verify-recover driver consumes. The clean
    /// verdict now requires POSITIVE evidence — at least one recorded successful slice and no failures —
    /// so a collector that recorded nothing can no longer fold to "clean". The four terminal shapes:
    /// <list type="bullet">
    /// <item>an explicit no-wrapper-surface signal → the distinct no-wrapper-surface outcome;</item>
    /// <item>any recorded failure → <see cref="WrapperCompileDiagnostics.Failed"/> (result dropped);</item>
    /// <item>zero recorded slices with no signal → failed with a synthesized diagnostic, because there is
    /// no evidence any promised slice was accepted (the strip-to-empty producer and any bug that skips
    /// every <see cref="RecordSuccess"/> land here — refusing to ship an unverified wrapper);</item>
    /// <item>otherwise → <see cref="WrapperCompileDiagnostics.Clean"/> carrying the promoted result.</item>
    /// </list>
    /// </summary>
    public WrapperCompileDiagnostics ToDiagnostics(SwiftWrapperCompilationResult? result)
    {
        var fileProvenance = FileProvenance.ToList();

        // A no-wrapper-surface signal is a terminal outcome that must not coexist with recorded slices:
        // the compile raises it only at a genuine no-source bail point, before any slice is attempted.
        // A signal alongside a recorded slice (or a failure) is a contradiction — fail closed rather
        // than trust an inconsistent record.
        if (NoWrapperSurface)
        {
            if (_slices.Count != 0 || AnyFailed)
                return WrapperCompileDiagnostics.Failed(Slices, fileProvenance);
            return WrapperCompileDiagnostics.NoWrapperSurfaceOutcome(_noWrapperSurfaceReason!, result, fileProvenance);
        }

        if (AnyFailed)
            return WrapperCompileDiagnostics.Failed(Slices, fileProvenance);

        if (_slices.Count == 0)
            return WrapperCompileDiagnostics.Failed(NoRecordedSlices(), fileProvenance);

        return WrapperCompileDiagnostics.Clean(result, Slices, fileProvenance);
    }

    // A synthetic failed slice for the "recorded nothing, signalled nothing" case: a coherent,
    // unattributable global error the controller reads as an unattributed failure and fails the module
    // closed — never a silent fold to clean. Names why in the diagnostic so the report explains it.
    private static IReadOnlyList<WrapperSliceDiagnostics> NoRecordedSlices()
    {
        var group = new DiagnosticGroup
        {
            Primary = CompilerDiagnostic.Global(
                DiagnosticSeverity.Error,
                "wrapper compilation recorded no slice results and raised no no-wrapper-surface signal; " +
                "refusing to treat an unverified wrapper as clean"),
        };
        return new[] { new WrapperSliceDiagnostics("<none>", false, new[] { group }) };
    }
}
