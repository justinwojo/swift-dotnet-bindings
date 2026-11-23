// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Build.CdeclAbiParityGate.cs — "a P/Invoke passes every argument in the register its @_cdecl
// reads" ship gate for `nuke binding-tests --compile-only`.
//
// The generator emits each @_cdecl Swift function and the C# P/Invoke that calls it from separate
// code, each with its own parameter list. A width disagreement (C# `int` against Swift `Int`)
// compiles on both sides and usually works, because the upper half of the register is usually
// zero; an engine that leaves it dirty hands Swift a garbage value. A register-class disagreement
// (integer against floating-point) is wrong everywhere. The gate reads both halves of the generated
// output, pairs each import with the @_cdecl of the same library and entry point, and fails on any
// argument or return in a different register (the reasoning is on CdeclAbiParity).
//
// Fail-closed and unconditional, with no `--permissive` arm: this is a soundness property of the
// emitted artifact, not a baseline, so there is nothing to reseed.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Serilog;

partial class Build
{
    /// <summary>
    /// Pairs found on a healthy tree are about 14,000 (every generated wrapper and bridge call); a
    /// count far below that means the output or a parser went missing, not that the fixtures shrank.
    /// </summary>
    const int CdeclAbiParityMinimumPairs = 10_000;

    /// <summary>
    /// Asserts that every generated P/Invoke naming a generated @_cdecl agrees with it on the
    /// register class and width of every argument and the return. Invoked from the --compile-only
    /// path after generation; reads text only.
    /// </summary>
    void RunCdeclAbiParityGate()
    {
        Log.Information("=========================================");
        Log.Information(" Cdecl ABI parity gate");
        Log.Information("=========================================");

        // Each generated Swift file and the framework it compiles into — the library name the C#
        // imports carry. The main wrapper is every top-level Swift file except the SwiftUI bridge
        // (the set RunBuildAsyncWrapper compiles); the dependency wrapper is the generator's own
        // wrapper compile of dep-swift/.
        // wrapper compile of dep-swift/. Each leg's own wrapper file is named outright, so a leg whose
        // sources went missing fails as "not found" instead of dropping out of the check.
        var sources = WrapperSources(BtOutputDir, BtOutputDir / $"{ModuleName}.Wrapper.swift", WrapperModule)
            .Concat(WrapperSources(BtOutputDir / "dep-swift", BtOutputDir / "dep-swift" / $"{DepModuleName}.Wrapper.swift",
                $"{DepModuleName}SwiftBindings"))
            .Append(new CdeclSwiftSource(BtOutputDir / $"{ModuleName}.SwiftUIBridge.swift", BridgeModule))
            .ToList();

        var controls = new[]
        {
            // An array crosses the SwiftUI bridge as (pointer, count): the shape whose count once
            // crossed as 32 bits against Swift's 64. It must be found and checked argument by argument.
            new CdeclPositiveControl(BridgeModule, $"SBW_{ModuleName}_RawShadeView_Create",
                "a SwiftUI view whose initializer takes an array"),
        };

        var verdict = CdeclAbiParity.EvaluateTree(BtOutputDir, sources, CdeclAbiParityMinimumPairs, controls);
        Log.Information(
            "{Pairs} P/Invoke ↔ @_cdecl pair(s) from {Swift} @_cdecl function(s) and {PInvokes} P/Invoke(s); " +
            "{Checked} argument(s) checked, {Skipped} skipped as undecidable (struct or enum by value), " +
            "{FullyChecked} pair(s) fully checked, {ArityUnchecked} not arity-checked",
            verdict.PairCount, verdict.SwiftDefinitionCount, verdict.PInvokeCount, verdict.CheckedArgumentCount,
            verdict.SkippedArgumentCount, verdict.FullyCheckedPairCount, verdict.ArityUncheckedPairCount);
        foreach (var (library, count) in verdict.PairsByLibrary.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            Log.Information("  {Library}: {Count} pair(s)", library, count);

        if (!verdict.Passed)
        {
            foreach (var f in verdict.Failures)
                Log.Error("  {Failure}", f);
            throw new Exception(
                $"Cdecl ABI parity gate: {verdict.Failures.Count} failure(s). A P/Invoke argument in a different " +
                "register than its @_cdecl reads compiles cleanly and passes Swift a wrong value on some engine — see the lines above.");
        }

        Log.Information("Cdecl ABI parity gate: PASSED");

        static IEnumerable<CdeclSwiftSource> WrapperSources(string dir, string requiredWrapper, string library) =>
            (Directory.Exists(dir) ? Directory.GetFiles(dir, "*.swift") : Array.Empty<string>())
                .Where(f => !f.EndsWith(".SwiftUIBridge.swift", StringComparison.Ordinal))
                .Append(requiredWrapper)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(f => f, StringComparer.Ordinal)
                .Select(f => new CdeclSwiftSource(f, library));
    }
}
