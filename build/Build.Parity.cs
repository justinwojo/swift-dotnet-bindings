// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Build.Parity.cs — cross-artifact parity gate for `nuke binding-tests --compile-only`.
//
// After bindings are generated and the wrappers are compiled, this gate diffs the
// generated C# against the built Swift libraries (and the ABI JSON) and fails the
// compile-only run on any NEW divergence across three classes — symbol existence,
// struct-mirror arity, and reverse-dispatch vtable parity. The pure logic lives in
// `build/Helpers/ArtifactParityGate.cs` (link-compiled into the unit tests); this
// file owns only the I/O: locating dylibs, running `nm -gU`, reading the generated
// files, diffing against the committed baseline, and logging.
//
// Fail-closed: violations throw, consistent with the rest of --compile-only. Pass
// --permissive to downgrade to warnings for local exploration. The committed
// baseline (`build/baselines/parity-baseline.json`) absorbs the pre-existing
// divergences this branch carries so the gate is green now; reseed it with
// `nuke SeedParityBaseline` after an intentional, reviewed change.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Nuke.Common;
using Nuke.Common.IO;
using Serilog;

partial class Build
{
    AbsolutePath ParityBaselinePath => BaselinesDir / "parity-baseline.json";

    const string ParityBaselineDescription =
        "Artifact-parity gate baseline. Records pre-existing cross-artifact divergences " +
        "(Defect cluster D member-path symbols; Finding 8 / Defect C vtable over-emissions) " +
        "so the gate is green now and fails on any NEW divergence. See " +
        "src/docs/architecture-review-2026-06.md (Findings 4/8/30, Defect cluster D). " +
        "Reseed with `nuke SeedParityBaseline` after an intentional, reviewed change.";

    /// <summary>
    /// Runs the cross-artifact parity gate against fresh generator output. Fail-closed
    /// unless <c>--permissive</c>. Invoked from the --compile-only path after the
    /// wrappers are built.
    /// </summary>
    void RunParityGate(bool failClosed)
    {
        Log.Information("=========================================");
        Log.Information(" Artifact-parity gate");
        Log.Information("=========================================");

        // Resolve inputs, compute findings, and load the baseline under one fail-open
        // guard so --permissive uniformly downgrades EVERY setup failure (missing dylib/
        // ABI/generated source, an `nm` error, or a malformed committed baseline) to a
        // warning. Under fail-closed (the default) any of these propagates and fails loud.
        ArtifactParityGate.ParityFindings findings;
        ArtifactParityGate.ParityBaseline baseline;
        try
        {
            var inputs = ResolveParityInputs();

            findings = ArtifactParityGate.ComputeFindings(
                inputs.CsSource, inputs.SwiftWrapperSource, inputs.AbiJson,
                inputs.SymbolsByLibrary, inputs.WrapperAuthoredSymbols);

            // Report (non-gated) the libraries we have no dylib for, so a system-lib
            // skip is never silent. These are libswiftCore et al. that we don't own.
            foreach (var (lib, count) in findings.SkippedLibraries.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                Log.Information("  (skipped non-artifact library '{Lib}': {Count} P/Invoke(s) — not gated)", lib, count);

            baseline = ArtifactParityGate.ParityBaseline.Parse(
                File.Exists(ParityBaselinePath) ? File.ReadAllText(ParityBaselinePath) : "");
        }
        catch (Exception ex) when (!failClosed)
        {
            Log.Warning("Parity gate: setup unavailable ({Message}) — skipped (--permissive).", ex.Message);
            return;
        }

        var violations = ArtifactParityGate.DiffAgainstBaseline(findings, baseline);

        Log.Information(
            "Parity gate: {Externs} symbol(s) forward-missing / {Orphans} reverse-orphan(s) / "
            + "{Arity} struct-arity / {Vtable} vtable field / {CsOnly} C#-only vtable / {SwiftOnly} Swift-only vtable "
            + "(pre-baseline). {Violations} NEW violation(s) after baseline.",
            findings.ForwardMissingByLibrary.Values.Sum(v => v.Count),
            findings.ReverseOrphans.Count,
            findings.StructArity.Count,
            findings.VtableFieldMismatches.Count,
            findings.VtableCsOnly.Count,
            findings.VtableSwiftOnly.Count,
            violations.Count);

        if (violations.Count == 0)
        {
            Log.Information("Artifact-parity gate passed (no new divergence against baseline).");
            return;
        }

        foreach (var v in violations)
            Log.Error("  ✗ [{Gate}] {Detail}", v.Gate, v.Detail);

        var message =
            $"Artifact-parity gate failed: {violations.Count} new divergence(s) between the generated C# "
            + $"and the built Swift artifacts. Each is a latent runtime fault (EntryPointNotFound, OOB read, "
            + $"or wrong-slot dispatch). Fix the generator/emitter, OR — if the divergence is intentional and "
            + $"reviewed — reseed {ParityBaselinePath.Name} with `nuke SeedParityBaseline` in the same change.";

        if (failClosed)
            throw new Exception(message);

        Log.Warning("{Message} (downgraded to a warning by --permissive.)", message);
    }

    // ---- Artifact resolution ------------------------------------------

    sealed record ParityInputs(
        string CsSource,
        string SwiftWrapperSource,
        string AbiJson,
        IReadOnlyDictionary<string, IReadOnlySet<string>> SymbolsByLibrary,
        IReadOnlySet<string> WrapperAuthoredSymbols);

    ParityInputs ResolveParityInputs()
    {
        var csPath = BtOutputDir / $"{ModuleName}.cs";
        var swiftPath = BtOutputDir / $"{ModuleName}.Wrapper.swift";
        if (!File.Exists(csPath)) throw new FileNotFoundException($"generated bindings not found: {csPath}");
        if (!File.Exists(swiftPath)) throw new FileNotFoundException($"generated wrapper not found: {swiftPath}");

        var abiPath = FindAbiJson(ModuleName)
            ?? throw new FileNotFoundException($"ABI JSON for {ModuleName} not found under {BtBuildDir}");

        // Main library dylib: the generator-built xcframework slice.
        var mainDylib = FindFrameworkBinary(BtXcframeworkDir, ModuleName)
            ?? throw new FileNotFoundException($"main dylib not found under {BtXcframeworkDir}");

        // Generator wrapper dylib: the generator's OWN compiled async wrapper. We gate
        // against this (not the harness's stripped `SwiftBindings.xcframework`) so the
        // symbol check is decoupled from the BindingTests source stripper / its
        // PreservedProtocols allowlist — the genuine never-emitted member-path symbols
        // (Defect cluster D) are absent here too, while the benign stripped witness-
        // getters are present, so they don't false-positive.
        var wrapperXcf = BtOutputDir / $"{ModuleName}{WrapperModule}.xcframework";
        var wrapperDylib = FindFrameworkBinary(wrapperXcf, $"{ModuleName}{WrapperModule}")
            ?? throw new FileNotFoundException($"generator wrapper dylib not found under {wrapperXcf}");

        var symbolsByLibrary = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            [ModuleName] = ArtifactParityGate.ParseNmSymbols(RunNm(mainDylib)),
            // The generated C# names the wrapper library `WrapperModule` ("SwiftBindings").
            [WrapperModule] = ArtifactParityGate.ParseNmSymbols(RunNm(wrapperDylib)),
        };

        // Optional dependency-module dylibs, when this fixture exercises them.
        var depDylib = FindFrameworkBinary(BtDepXcframeworkDir, DepModuleName);
        if (depDylib != null)
            symbolsByLibrary[DepModuleName] = ArtifactParityGate.ParseNmSymbols(RunNm(depDylib));
        var depWrapperXcf = BtOutputDir / $"{DepModuleName}{WrapperModule}.xcframework";
        var depWrapperDylib = FindFrameworkBinary(depWrapperXcf, $"{DepModuleName}{WrapperModule}");
        if (depWrapperDylib != null)
            symbolsByLibrary[$"{DepModuleName}{WrapperModule}"] = ArtifactParityGate.ParseNmSymbols(RunNm(depWrapperDylib));

        var csSource = File.ReadAllText(csPath);
        var swiftSource = File.ReadAllText(swiftPath);
        var abiJson = File.ReadAllText(abiPath);

        // Dependency module: when the fixture builds a cross-module dependency, gate its
        // generated surface too. Its dylibs are already mapped above; here we fold in its
        // generated C#, wrapper Swift, and ABI so all three checks cover it. The dep .cs
        // names its libraries `SwiftBindingsTestLibDependency{,SwiftBindings}` — the keys
        // added above — so forward checks resolve. If the .cs is present we REQUIRE the
        // wrapper Swift and ABI: parsing the C# without them would manufacture false
        // cs-only vtables / skipped arity. Absent entirely, the gate just covers main.
        var depCsPath = BtOutputDir / $"{DepModuleName}.cs";
        if (File.Exists(depCsPath))
        {
            var depSwiftPath = BtOutputDir / "dep-swift" / $"{DepModuleName}.Wrapper.swift";
            if (!File.Exists(depSwiftPath))
                throw new FileNotFoundException($"dependency wrapper not found: {depSwiftPath}");
            var depAbiPath = FindAbiJson(DepModuleName)
                ?? throw new FileNotFoundException($"ABI JSON for {DepModuleName} not found under {BtBuildDir}");

            // ...and REQUIRE both dep dylibs (mapped optionally above). Folding the dep .cs
            // adds its called P/Invokes to the forward check; with the dep dylib(s) absent
            // those symbols hit the "no nm symbols for this library" path and are counted as
            // SKIPPED (fail-open) rather than missing — a genuinely absent dep symbol would
            // then slip through. Requiring the dylibs here keeps the gate fail-closed on
            // incomplete dep inputs, symmetric with the wrapper-Swift/ABI requirement above.
            if (!symbolsByLibrary.ContainsKey(DepModuleName))
                throw new FileNotFoundException(
                    $"dependency dylib not found under {BtDepXcframeworkDir} but {depCsPath.Name} is present — " +
                    "its P/Invokes would be silently skipped instead of symbol-checked.");
            if (!symbolsByLibrary.ContainsKey($"{DepModuleName}{WrapperModule}"))
                throw new FileNotFoundException(
                    $"dependency wrapper dylib not found under {depWrapperXcf} but {depCsPath.Name} is present — " +
                    "its wrapper P/Invokes would be silently skipped instead of symbol-checked.");

            csSource += "\n" + File.ReadAllText(depCsPath);
            swiftSource += "\n" + File.ReadAllText(depSwiftPath);
            // ParseAbiStoredInstanceProps accepts a JSON array of ABI documents; main first.
            abiJson = "[" + abiJson + "," + File.ReadAllText(depAbiPath) + "]";
        }

        // Reverse check: union the authored exports of every wrapper dylib we mapped
        // (main + dependency), so a dep wrapper export referenced by no P/Invoke is caught.
        var wrapperAuthored = symbolsByLibrary
            .Where(kv => kv.Key.EndsWith(WrapperModule, StringComparison.Ordinal))
            .SelectMany(kv => kv.Value)
            .Where(ArtifactParityGate.IsAuthoredWrapperSymbol)
            .ToHashSet(StringComparer.Ordinal);

        return new ParityInputs(csSource, swiftSource, abiJson, symbolsByLibrary, wrapperAuthored);
    }

    /// <summary>Finds the framework binary <c>{module}.framework/{module}</c> inside an
    /// xcframework, preferring a simulator slice (the default compile-only platform).</summary>
    static AbsolutePath? FindFrameworkBinary(AbsolutePath xcframeworkDir, string module)
    {
        if (!Directory.Exists(xcframeworkDir)) return null;
        var candidates = Directory
            .EnumerateFiles(xcframeworkDir, module, SearchOption.AllDirectories)
            .Where(p => p.Replace('\\', '/').EndsWith($"{module}.framework/{module}", StringComparison.Ordinal))
            .ToList();
        if (candidates.Count == 0) return null;
        var pick = candidates.FirstOrDefault(p => p.Contains("simulator", StringComparison.OrdinalIgnoreCase))
                   ?? candidates[0];
        return (AbsolutePath)pick;
    }

    AbsolutePath? FindAbiJson(string module)
    {
        if (!Directory.Exists(BtBuildDir)) return null;
        var candidates = Directory
            .EnumerateFiles(BtBuildDir, "*.abi.json", SearchOption.AllDirectories)
            .Where(p => p.Replace('\\', '/').Contains($"{module}.framework/", StringComparison.Ordinal)
                        || p.Replace('\\', '/').Contains($"{module}.swiftmodule/", StringComparison.Ordinal))
            .ToList();
        if (candidates.Count == 0) return null;
        var pick = candidates.FirstOrDefault(p => p.Contains("simulator", StringComparison.OrdinalIgnoreCase))
                   ?? candidates[0];
        return (AbsolutePath)pick;
    }

    static string RunNm(AbsolutePath dylib)
    {
        using var proc = Process.Start(new ProcessStartInfo
        {
            FileName = "nm",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        }.Tap(psi =>
        {
            psi.ArgumentList.Add("-gU");
            psi.ArgumentList.Add(dylib);
        }))!;
        var stdout = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit();
        if (proc.ExitCode != 0)
            throw new Exception($"`nm -gU {dylib}` failed (exit {proc.ExitCode}): {proc.StandardError.ReadToEnd().Trim()}");
        return stdout;
    }

    // ---- Reseed target ------------------------------------------------
    //
    // Regenerates build/baselines/parity-baseline.json from the current artifacts so
    // the committed baseline can never drift from the gate's own detection logic.
    // Run after generating bindings (`nuke binding-tests --compile-only`) and only
    // when the new divergences are intentional and reviewed.
    //
    // The .After(...) edges give Nuke `--strict` a total order over the otherwise
    // co-equal sinks; the body observes none of them.
    Target SeedParityBaseline => _ => _
        .After(BindingTests, BehaviorTier, ValidateBlastRadius, X64SimGate)
        .Executes(() =>
        {
            var inputs = ResolveParityInputs();
            var findings = ArtifactParityGate.ComputeFindings(
                inputs.CsSource, inputs.SwiftWrapperSource, inputs.AbiJson,
                inputs.SymbolsByLibrary, inputs.WrapperAuthoredSymbols);

            var baseline = ArtifactParityGate.ParityBaseline.Seed(
                findings, ReadHeadShaShort(), ParityBaselineDescription);
            File.WriteAllText(ParityBaselinePath, baseline.ToJson());

            Log.Information(
                "Seeded {Path}: {Fwd} forward-missing, {Orphans} reverse-orphan, {Arity} struct-arity, "
                + "{Vtable} vtable-field, {CsOnly} C#-only vtable. "
                + "(Swift-only vtables are never baselined: {SwiftOnly} present.)",
                ParityBaselinePath,
                findings.ForwardMissingByLibrary.Values.Sum(v => v.Count),
                findings.ReverseOrphans.Count,
                findings.StructArity.Count,
                findings.VtableFieldMismatches.Count,
                findings.VtableCsOnly.Count,
                findings.VtableSwiftOnly.Count);

            if (findings.VtableSwiftOnly.Count > 0)
                Log.Warning("Parity reseed: {Count} Swift-only vtable(s) will STILL fail the gate (by design): {Protos}",
                    findings.VtableSwiftOnly.Count, string.Join(", ", findings.VtableSwiftOnly));
        });
}

internal static class ProcessStartInfoExtensions
{
    // Small fluent helper so the ArgumentList can be populated inline.
    public static ProcessStartInfo Tap(this ProcessStartInfo psi, Action<ProcessStartInfo> configure)
    {
        configure(psi);
        return psi;
    }
}
