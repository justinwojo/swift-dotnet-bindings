// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Decides whether a module's binding depends on a generated Swift wrapper, and writes that as a
/// report fact.
///
/// <para>The SDK treats a missing wrapper xcframework as an error by default. For a module that
/// never emitted wrapper source that severity is aimed at nothing: no wrapper could exist, and
/// none was needed. For a module whose surface is mostly wrapped it is exactly right. Nothing in
/// the emitted artifacts said which case a given binding was, so the answer had to be inferred
/// from the absence of a file — the same observation for both.</para>
///
/// <para>The signal is deliberately "did the wrapper artifacts export an entry point", not "were
/// members recorded as wrapped". Those disagree in both directions: the closure-parameter tombstone
/// path records a wrapped item for a member whose emitted surface is a C# throw with no Swift behind
/// it, while metadata and helper wrappers put real <c>@_cdecl</c> functions in the wrapper source
/// without any member row at all. So this reads the same two file sets the wrapper compile reads.</para>
///
/// <para>File EXISTENCE is not the test, because the emitter writes <c>{namespace}.Wrapper.swift</c>
/// on every run — a module with nothing to wrap still gets an imports-and-helper-struct file, so a
/// count of files is true for every binding and answers nothing. What distinguishes the two cases is
/// whether that source declares anything callable: an <c>@_cdecl</c> or <c>@_silgen_name</c> entry
/// point is a symbol some emitted P/Invoke targets, and losing it removes a call route.</para>
/// </summary>
public static class WrapperRequirementEvaluator
{
    /// <summary>
    /// Computes <see cref="BindingReport.WrapperRequirement"/> from the wrapper artifacts actually
    /// emitted into <paramref name="outputDirectory"/> and the members recorded on the report.
    /// </summary>
    /// <param name="report">The settled report; mutated in place.</param>
    /// <param name="outputDirectory">The generation output directory the wrapper compile reads.</param>
    /// <param name="architecture">
    /// Architecture whose thunk assembly to look for. Thunks are emitted per-arch
    /// (<c>.arm64.s</c> / <c>.x86_64.s</c>), so a mismatched arch here reads as "no thunks" —
    /// matching the wrapper compiler's own per-slice gate.
    /// </param>
    public static void Evaluate(BindingReport report, string outputDirectory, string architecture = "arm64")
    {
        ArgumentNullException.ThrowIfNull(report);

        var entryPointCount = SwiftWrapperCompiler.CollectSwiftFiles(outputDirectory)
            .Sum(CountEntryPoints);
        var thunkFileCount = NativeThunkCompiler.CollectAssemblyFiles(outputDirectory, architecture).Count;
        var wrapperRequired = entryPointCount > 0 || thunkFileCount > 0;

        // A closure-parameter tombstone is recorded as a wrapped item but has no Swift wrapper
        // behind it — its emitted body throws. Counting it here would claim wrapper dependence for
        // a member that would behave identically with no wrapper at all.
        var wrappedMemberCount = report.WrappedItems
            .Count(item => item.WrapperKind != ReportCollector.ClosureParamTombstoneWrapperKind);

        var markedForNoWrapper = report.DegradedSurface is { } surface
            ? surface.ByDiagnosticId.GetValueOrDefault("SB0001")
              + surface.ByDiagnosticId.GetValueOrDefault("SB0009")
            : 0;

        report.WrapperRequirement = new WrapperRequirementSummary
        {
            WrapperRequired = wrapperRequired,
            WrapperEntryPointCount = entryPointCount,
            WrappedMemberCount = wrappedMemberCount,
            UnwrappedMarkedMemberCount = markedForNoWrapper,
            Rationale = BuildRationale(
                wrapperRequired, entryPointCount, markedForNoWrapper),
        };
    }

    /// <summary>
    /// How many symbols the generated C# can bind to <paramref name="swiftFilePath"/> declares.
    ///
    /// <para>Counts every match rather than stopping at the first, because the number is what the
    /// rationale is phrased from: it is the one measure of wrapper dependence that covers the
    /// ordinary <c>@_cdecl</c> method path, which records nothing per-member. That costs a full
    /// read of a source that runs to tens of thousands of lines for a large module — one linear
    /// pass over a file the wrapper compile is about to parse in its entirety anyway.</para>
    /// </summary>
    private static int CountEntryPoints(string swiftFilePath)
    {
        var count = 0;
        try
        {
            foreach (var line in File.ReadLines(swiftFilePath))
            {
                if (line.Contains("@_cdecl(", StringComparison.Ordinal)
                    || line.Contains("@_silgen_name(", StringComparison.Ordinal))
                    count++;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unreadable wrapper file is not evidence that nothing was exported, and this is a
            // report fact rather than a gate — claiming dependence is the answer that keeps the
            // SDK's default severity in force rather than quietly excusing a missing wrapper.
            // Both arms are the same situation: the bytes could not be read, for a reason that says
            // nothing about what they contain. Whatever was counted before the read failed stands,
            // floored at one so the summary cannot read as "exports nothing".
            return Math.Max(count, 1);
        }

        return count;
    }

    /// <summary>
    /// Re-states the summary from counts that have since been settled by a later pass.
    ///
    /// <para>The counts written by <see cref="Evaluate"/> describe the surface as generated, but the
    /// wrapper compile runs after that and can strip symbols, co-gating the members that bound to
    /// them out of the emitted surface. Left alone, the summary keeps counting those members in the
    /// same report that lists them as skipped. The caller passes recomputed totals rather than
    /// deltas so the numbers cannot drift from the lists they summarize.</para>
    ///
    /// <para><see cref="WrapperRequirementSummary.WrapperRequired"/> and
    /// <see cref="WrapperRequirementSummary.WrapperEntryPointCount"/> are deliberately not revisited
    /// — they answer what the artifacts export, which is measured from the files themselves and does
    /// not follow from a count of co-gated members.</para>
    /// </summary>
    internal static void Restate(
        WrapperRequirementSummary summary, int wrappedMemberCount, int unwrappedMarkedMemberCount)
    {
        ArgumentNullException.ThrowIfNull(summary);

        summary.WrappedMemberCount = wrappedMemberCount;
        summary.UnwrappedMarkedMemberCount = unwrappedMarkedMemberCount;
        summary.Rationale = BuildRationale(
            summary.WrapperRequired, summary.WrapperEntryPointCount, unwrappedMarkedMemberCount);
    }

    /// <summary>
    /// The one sentence a consumer reads when deciding whether <c>SwiftWrapperRequired=false</c> is
    /// honest here. Phrased from the entry-point count rather than the wrapped-member count: only
    /// the former covers the ordinary <c>@_cdecl</c> method path, so only the former can state the
    /// cost without understating it.
    /// </summary>
    private static string BuildRationale(bool wrapperRequired, int entryPointCount, int markedForNoWrapper)
    {
        if (!wrapperRequired)
        {
            return "This module's wrapper source declares no entry points and no native thunks were "
                + "emitted, so there is nothing for the build to produce and nothing is lost if the "
                + "wrapper is absent.";
        }

        var alreadyDegraded = markedForNoWrapper == 0
            ? string.Empty
            : $" A further {markedForNoWrapper} member(s) are already marked because no wrapper could be "
              + "generated for them, which is a separate condition and is not fixed by building one.";

        // Required with no wrapper entry points is a real shape rather than a contradiction: native
        // thunks alone settle the question, and they are assembly rather than Swift source.
        if (entryPointCount == 0)
        {
            return "This module's wrapper source declares no entry points, but native thunks were "
                + "emitted for this slice and the generated code calls them, so a missing wrapper "
                + $"build is not safe to ignore.{alreadyDegraded}";
        }

        var exported = entryPointCount == 1
            ? "The generated wrapper exports 1 entry point"
            : $"The generated wrapper exports {entryPointCount} entry points";

        return $"{exported} that generated P/Invokes target; without the wrapper those call routes "
            + $"do not exist at runtime.{alreadyDegraded}";
    }
}
