// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Build.ClosureDelegateParityGate.cs — "one delegate type per closure" ship gate for
// `nuke binding-tests --compile-only`.
//
// See ClosureDelegateParityScanner for what is compared and why the comparison is per-file. The
// short version: a closure's public delegate type and the type its [UnmanagedCallersOnly] trampoline
// casts the stored delegate back to must be one computation. When they were two, both compilers
// stayed green and the FIRST callback aborted the process with an InvalidCastException inside the
// trampoline. Nothing else in the gate chain can see that — the parity gate compares C# against
// Swift artifacts, the API-manifest gate compares signatures against a baseline, and a signature
// that changed on BOTH sides in the same wrong direction would satisfy both.
//
// Fail-closed by construction, like the overload-name gate next to it: this encodes a soundness
// property rather than a baseline, so there is nothing to reseed and no local state in which a
// mismatched cast is acceptable. It also carries an empty-ledger positive control — a run that finds
// no trampoline casts at all reds the gate rather than passing vacuously, so a regeneration that
// silently stopped emitting closures cannot read as a green.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Nuke.Common;
using Serilog;

partial class Build
{
    /// <summary>
    /// Asserts that every closure trampoline in the freshly-generated bindings casts the stored
    /// delegate back to a type that appears on the same file's public delegate surface. Invoked from
    /// the --compile-only path after the overload-name gate.
    /// </summary>
    void RunClosureDelegateParityGate()
    {
        Log.Information("=============================================");
        Log.Information(" Closure delegate-type parity gate");
        Log.Information("=============================================");

        var files = Directory.Exists(BtOutputDir)
            ? Directory.EnumerateFiles(BtOutputDir, "*.cs", SearchOption.AllDirectories)
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList()
            : new List<string>();

        if (files.Count == 0)
            throw new Exception(
                $"Closure delegate-type parity gate: no generated `.cs` found under {BtOutputDir}. " +
                "Run `nuke binding-tests --compile-only` (regenerates) first.");

        var verdict = ClosureDelegateParityScanner.Scan(
            files.Select(p => (File: Path.GetFileName(p), Text: File.ReadAllText(p))));

        // Positive control. The corpus contains closures by construction, so a zero-cast scan means
        // the scanner (or the emission it reads) stopped producing the thing under test.
        if (verdict.CastCount == 0)
            throw new Exception(
                "Closure delegate-type parity gate: found ZERO closure trampoline casts across " +
                $"{files.Count} generated file(s). The BindingTests corpus emits closures, so an empty " +
                "ledger means the scan stopped reaching the emitted trampolines — investigate rather " +
                "than treating this as a pass.");

        foreach (var m in verdict.Mismatches)
        {
            Log.Error("  ✗ {File}: trampoline casts to {Cast}, which no public delegate parameter in " +
                      "that file declares", m.File, m.CastType);
        }

        if (!verdict.Passed)
            throw new Exception(
                $"Closure delegate-type parity gate: {verdict.Mismatches.Count} trampoline cast(s) " +
                "recover a delegate under a type the public signature never stored it as. That cast is " +
                "unchecked, so this compiles on both sides and aborts the process on the first callback " +
                "(InvalidCastException → FailFastUnhandledClosureException). The public delegate type and " +
                "the cast target must come from ONE computation — see TypeProjectionFactory.ProjectClosure.");

        Log.Information("  ✓ {Casts} closure trampoline cast(s) across {Files} file(s), all matching a " +
                        "declared public delegate type", verdict.CastCount, verdict.FilesWithCasts);
    }
}
