// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Build.BindingTests.IngestionKitchen.cs — the durable BindingTests gate for the input-graph closure
// preflight (SWIFTBIND119). Like the ResilienceKitchen gate, it runs UNCONDITIONALLY inside
// `nuke binding-tests --compile-only`, so CI's fail-closed compile gate exercises the preflight on
// every invocation.
//
// The fixture (BindingTests/Sources/IngestionKitchen/) is two modules: IngestionBase (a base module)
// and IngestionBridge (the PRIMARY module — it `@_exported import`s IngestionBase and adds a retroactive
// conformance + foreign extension onto a Base type, so its public surface genuinely depends on Base).
// Two legs share the one built pair of xcframeworks:
//
//   Leg 1 — CLOSED GRAPH. Bind IngestionBridge WITH IngestionBase supplied as a --framework-dependency.
//     The importer (Bridge) is processed before the imported (Base) — the "reverse" order — yet the graph
//     still closes. Assert: generator exit 0, IngestionBridge C# emitted, and NEITHER SWIFTBIND119 (the
//     preflight obligation) NOR SWIFTBIND111 (verify-recover non-convergence) appears.
//
//   Leg 2 — MISSING TRANSITIVE. Bind IngestionBridge with IngestionBase WITHHELD. The preflight must
//     prove the graph is not closed BEFORE any ABI parsing and fail with a structured SWIFTBIND119 that
//     names the missing module (IngestionBase), its importer (IngestionBridge), the evidence
//     (.swiftinterface:line), the searched roots, receipt-neutral provenance, and a remediation. Assert:
//     generator exit != 0, SWIFTBIND119 present with every structured field, SWIFTBIND111 ABSENT (the
//     failure is early, not a late non-convergence), and NO artifacts emitted (no C#, no wrapper Swift) —
//     proving the abort happened before parse/emit.
//
// It is fail-closed on all of the above.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Nuke.Common.IO;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.DotNet;
using Serilog;
using static Nuke.Common.Tools.DotNet.DotNetTasks;

partial class Build
{
    const string IngestionBaseModule = "IngestionBase";
    const string IngestionBridgeModule = "IngestionBridge";

    AbsolutePath IngestionSourceDir => BindingTestsDir / "Sources" / "IngestionKitchen";
    AbsolutePath IngestionScratch => RootDirectory / "artifacts" / "ingestion-kitchen";

    // Runs on every --compile-only invocation (see the CompileOnly branch in Build.BindingTests.cs).
    void RunIngestionKitchenGate()
    {
        Log.Information("=================================================");
        Log.Information(" BindingTests — input-graph closure preflight gate");
        Log.Information("=================================================");

        EnsureGeneratorBuilt();

        var scratch = IngestionScratch;
        if (Directory.Exists(scratch)) scratch.DeleteDirectory();
        scratch.CreateDirectory();

        // Build IngestionBase, then IngestionBridge (compiled against Base's slice so its @_exported
        // import resolves). Each module lives in its OWN build root, so Base is genuinely absent from
        // Bridge's framework-search neighborhood when it is not supplied (leg 2).
        var baseBuildDir = scratch / "base-build";
        var baseSliceDir = BuildIngestionModule(baseBuildDir, IngestionBaseModule,
            IngestionSourceDir / IngestionBaseModule, depSearchDirs: null);
        var baseXcframework = CreateIngestionXcframework(baseBuildDir, IngestionBaseModule, baseSliceDir);

        var bridgeBuildDir = scratch / "bridge-build";
        var bridgeSliceDir = BuildIngestionModule(bridgeBuildDir, IngestionBridgeModule,
            IngestionSourceDir / IngestionBridgeModule, depSearchDirs: new[] { baseSliceDir });
        var bridgeXcframework = CreateIngestionXcframework(bridgeBuildDir, IngestionBridgeModule, bridgeSliceDir);

        var leg1Output = scratch / "leg1-output";
        AssertIngestionLegClosed(bridgeXcframework, baseXcframework, leg1Output);
        AssertIngestionLegMissingTransitive(bridgeXcframework, scratch / "leg2-output");
        AssertIngestionLegProvenClosureQuarantine(bridgeXcframework, baseXcframework, leg1Output, scratch);
        AssertIngestionLegDependencyProtocolQuarantine(bridgeXcframework, baseXcframework, leg1Output, scratch);

        Log.Information("ingestion-kitchen PASSED — closed graph binds, missing transitive fails early with "
            + "a structured SWIFTBIND119 obligation (no SWIFTBIND111, no artifacts), a malformed PRIMARY type "
            + "record degrades to a proven-closure quarantine, and a malformed DEPENDENCY protocol record "
            + "withdraws its cross-module dependents (IngestionWithdrawal, healthy controls stable).");
    }

    // Compiles one simulator-slice framework for the given module from its source subdir, optionally
    // with dependency framework-search dirs (so IngestionBridge's @_exported import IngestionBase
    // resolves at compile time). Returns the slice directory that contains <Module>.framework.
    AbsolutePath BuildIngestionModule(AbsolutePath buildRoot, string module, AbsolutePath sourceSubdir, AbsolutePath[]? depSearchDirs)
    {
        if (Directory.Exists(buildRoot)) buildRoot.DeleteDirectory();
        buildRoot.CreateDirectory();

        var ios = ApplePlatform.IOS;
        var sdkPath = XcRun.GetSdkPath(ios.SimulatorSdkName);
        var sliceDir = buildRoot / ios.SimulatorSliceId;
        var frameworkDir = sliceDir / $"{module}.framework";

        var sources = Directory.GetFiles(sourceSubdir, "*.swift", SearchOption.AllDirectories).ToList();
        if (sources.Count == 0)
            throw new Exception($"ingestion-kitchen: no Swift sources found under {sourceSubdir}.");

        // The module's OWN slice dir is always a search path (its .framework lands there); dependency
        // slice dirs are added so an @_exported import of a sibling module resolves during compile.
        var searchPaths = new List<string> { sliceDir.ToString() };
        if (depSearchDirs != null)
            searchPaths.AddRange(depSearchDirs.Select(d => d.ToString()));

        Log.Information("=== ingestion-kitchen: building {Module} simulator slice ===", module);
        CompileModuleSlice(
            module, ios.SimulatorTarget, sdkPath,
            ios.SimulatorModuleSuffix, ios.MinOsVersion, ios.SimulatorPlistPlatform,
            frameworkDir, sources, frameworkSearchPaths: searchPaths.ToArray(),
            swiftDefines: null);

        return sliceDir;
    }

    AbsolutePath CreateIngestionXcframework(AbsolutePath buildRoot, string module, AbsolutePath sliceDir)
    {
        var frameworkDir = sliceDir / $"{module}.framework";
        var xcframeworkPath = buildRoot / $"{module}.xcframework";
        if (Directory.Exists(xcframeworkPath)) xcframeworkPath.DeleteDirectory();
        XcodeBuild.ExecuteCreateXcframework(new CreateXcframeworkSettings()
            .AddFrameworkPath(frameworkDir)
            .SetOutputPath(xcframeworkPath));
        return xcframeworkPath;
    }

    (int ExitCode, IReadOnlyList<string> Log) RunIngestionGenerator(
        AbsolutePath primaryXcframework, AbsolutePath? dependencyXcframework, AbsolutePath outputDir,
        bool noVerifyCSharp = false)
    {
        outputDir.CreateDirectory();
        var genArgs = new List<string>
        {
            $"\"{GeneratorDll}\"",
            $"--xcframework \"{primaryXcframework}\"",
            $"-o \"{outputDir}\"",
        };
        // NOTE: deliberately NOT --strict-inputs. The closure-preflight fail-open contract (a deferred /
        // unadjudicated candidate must NOT record a --strict-inputs degradation) is proven deterministically
        // at the unit layer (InputGraphPreflightTests.UnadjudicatedDeferral_RecordsInfoNotDegradation). This
        // gate proves the preflight's early-abort behavior (SWIFTBIND119, no artifacts); running it under
        // --strict-inputs instead exercises the unrelated auto-detected-dependency resolver, whose xcframework
        // matching for the supplied dependency is a separate concern from this preflight.
        if (dependencyXcframework != null)
            genArgs.Add($"--framework-dependency \"{dependencyXcframework}\"");
        // A framework-dependent binding emits a <PackageReference> to its dependency's package
        // (IngestionBase.Swift.iOS), which the standalone kitchen never packs into a feed. The in-loop C#
        // verify-recover leg runs `dotnet build` on that emitted csproj; with the dependency package
        // unrestorable it returns NU1101 (inconclusive), and once the recovery loop has withdrawn a member
        // an inconclusive C# verdict escalates fail-closed. That is the fail-closed policy working as
        // designed for a standalone framework-dependent binding whose caller supplies neither a packed
        // dependency (--verification-package-feed) nor this opt-out — it is not a defect in the binding.
        // The degraded C# is instead proven compilable authoritatively by AssertIngestionDegradedBindingCompiles,
        // which compiles the emitted Base + Bridge sources together against the in-tree Swift.Runtime.
        if (noVerifyCSharp)
            genArgs.Add("--no-verify-csharp");

        var proc = ProcessTasks.StartProcess("dotnet", string.Join(" ", genArgs),
            workingDirectory: BindingTestsDir, logOutput: false);
        proc.WaitForExit();
        return (proc.ExitCode, proc.Output.Select(o => o.Text).ToList());
    }

    // Leg 1 — CLOSED GRAPH: Bridge bound WITH Base supplied. Must bind cleanly, no preflight obligation.
    void AssertIngestionLegClosed(AbsolutePath bridgeXcframework, AbsolutePath baseXcframework, AbsolutePath outputDir)
    {
        Log.Information("=== ingestion-kitchen leg 1: CLOSED graph (Bridge + Base supplied) ===");
        var (exit, log) = RunIngestionGenerator(bridgeXcframework, baseXcframework, outputDir);

        if (exit != 0)
        {
            foreach (var line in log) Log.Error("  [generator] {Line}", line);
            throw new Exception($"ingestion-kitchen leg 1: generator exit {exit} ≠ 0 — a CLOSED import graph "
                + "(IngestionBase supplied as a dependency) must bind successfully.");
        }

        var combined = string.Join("\n", log);
        if (combined.Contains("SWIFTBIND119", StringComparison.Ordinal))
            throw new Exception("ingestion-kitchen leg 1: generator log contains SWIFTBIND119 on a CLOSED graph — "
                + "the preflight raised a missing-module obligation for a module that WAS supplied (false positive).");
        if (combined.Contains("SWIFTBIND111", StringComparison.Ordinal))
            throw new Exception("ingestion-kitchen leg 1: generator log contains SWIFTBIND111 — the closed-graph binding "
                + "must converge.");

        var emitted = Directory.EnumerateFiles(outputDir, $"{IngestionBridgeModule}*.cs", SearchOption.TopDirectoryOnly).ToList();
        if (emitted.Count == 0)
            throw new Exception($"ingestion-kitchen leg 1: no {IngestionBridgeModule}*.cs emitted — the closed-graph "
                + "binding produced no C#.");

        // Convergence alone (the weaker closure-only gate) does not prove the foreign Base type
        // actually RESOLVED — a binding that silently degraded Bridge's public surface to AnyType
        // would also converge and emit C#. Assert full cross-module resolution: the two constructs
        // that make Bridge's surface genuinely depend on IngestionBase must bind concretely.
        var bridgeCs = string.Join("\n",
            emitted.Select(f => File.ReadAllText(f)));

        // (a) Foreign extension / re-exported Base type resolved to the CONCRETE cross-module C#
        //     type, not AnyType. BridgeWrapper.baseValue (a BaseValue-typed public property) and its
        //     constructor parameter must name IngestionBase.BaseValue. If the Base module were not
        //     finalized before Bridge's layout, these would degrade to AnyType.
        if (!bridgeCs.Contains($"{IngestionBaseModule}.BaseValue", StringComparison.Ordinal))
            throw new Exception($"ingestion-kitchen leg 1: emitted C# never names the concrete foreign type "
                + $"'{IngestionBaseModule}.BaseValue' — Bridge's public surface did not resolve the Base type "
                + "across the module boundary (a cross-module layout-resolution regression).");

        // (b) The foreign type must NOT have degraded to AnyType anywhere in Bridge's surface.
        if (Regex.IsMatch(bridgeCs, @"\bAnyType\b") || bridgeCs.Contains("SwiftAnyType", StringComparison.Ordinal))
            throw new Exception("ingestion-kitchen leg 1: emitted C# contains an AnyType fallback — a foreign "
                + "type owned by IngestionBase degraded to AnyType instead of resolving concretely.");

        // (c) Retroactive conformance (BaseValue: BridgeMarker) resolved into a bound protocol: the
        //     IBridgeMarker interface with its GetMarker() requirement, and BridgeWrapper.markerOfBase()
        //     which calls through that conformance.
        // "GetMarker(" (open paren) matches the parameterless requirement/impl but NOT
        // "GetMarkerOfBase(", so this stays distinct from the (c) member checked just below.
        if (!bridgeCs.Contains("IBridgeMarker", StringComparison.Ordinal) ||
            !bridgeCs.Contains("GetMarker(", StringComparison.Ordinal))
            throw new Exception("ingestion-kitchen leg 1: emitted C# is missing the IBridgeMarker interface or its "
                + "GetMarker() requirement — the retroactive conformance did not bind.");
        if (!bridgeCs.Contains("GetMarkerOfBase", StringComparison.Ordinal))
            throw new Exception("ingestion-kitchen leg 1: emitted C# is missing BridgeWrapper.GetMarkerOfBase() — the "
                + "member that dispatches through the retroactive conformance did not bind.");

        Log.Information("  ✓ closed graph: generator exit 0, {Count} C# file(s) emitted, no preflight obligation; "
            + "foreign Base type resolved concretely, retroactive conformance bound.", emitted.Count);
    }

    // Leg 2 — MISSING TRANSITIVE: Bridge bound WITHOUT Base. Must fail early with a structured
    // SWIFTBIND119, no SWIFTBIND111, and no emitted artifacts.
    void AssertIngestionLegMissingTransitive(AbsolutePath bridgeXcframework, AbsolutePath outputDir)
    {
        Log.Information("=== ingestion-kitchen leg 2: MISSING transitive (Bridge only, Base withheld) ===");
        var (exit, log) = RunIngestionGenerator(bridgeXcframework, dependencyXcframework: null, outputDir);
        var combined = string.Join("\n", log);

        // (1) The generator must fail — an unresolved public compile-import is not bindable.
        if (exit == 0)
        {
            foreach (var line in log) Log.Error("  [generator] {Line}", line);
            throw new Exception("ingestion-kitchen leg 2: generator exit 0 with IngestionBase withheld — a missing "
                + "transitive module must fail the binding, not silently narrow it.");
        }

        // (2) SWIFTBIND119 must be present and carry every structured field.
        if (!combined.Contains("SWIFTBIND119", StringComparison.Ordinal))
        {
            foreach (var line in log) Log.Error("  [generator] {Line}", line);
            throw new Exception("ingestion-kitchen leg 2: generator failed but did NOT emit SWIFTBIND119 — the missing "
                + "transitive module must be reported as a structured closure-preflight obligation, not an opaque failure.");
        }

        void RequireField(string needle, string description)
        {
            if (!combined.Contains(needle, StringComparison.Ordinal))
                throw new Exception($"ingestion-kitchen leg 2: SWIFTBIND119 report is missing {description} "
                    + $"(expected to contain '{needle}').");
        }

        RequireField(IngestionBaseModule, "the missing module name (IngestionBase)");
        RequireField(IngestionBridgeModule, "the importer module name (IngestionBridge)");
        // Evidence: the importer's .swiftinterface with a 1-based line number.
        if (!Regex.IsMatch(combined, @"\.swiftinterface:\d+"))
            throw new Exception("ingestion-kitchen leg 2: SWIFTBIND119 report is missing the evidence location "
                + "(expected '<...>.swiftinterface:<line>').");
        RequireField("searched roots", "the searched framework-search roots section");
        // Receipt-neutral provenance phrasing — never "conversion failed to produce it".
        RequireField("required module not supplied; conversion provenance unavailable",
            "the receipt-neutral provenance phrasing");
        if (combined.Contains("conversion failed to produce", StringComparison.OrdinalIgnoreCase))
            throw new Exception("ingestion-kitchen leg 2: SWIFTBIND119 report uses the FORBIDDEN 'conversion failed to "
                + "produce it' phrasing — with no receipt, absence must be reported receipt-neutrally.");
        RequireField("@_exported", "the @_exported re-export note in the remediation");
        // Action: names the module to supply.
        if (!Regex.IsMatch(combined, @"supply '" + Regex.Escape(IngestionBaseModule) + "'"))
            throw new Exception("ingestion-kitchen leg 2: SWIFTBIND119 report is missing an actionable remediation "
                + "naming the module to supply.");

        // (3) SWIFTBIND111 must be ABSENT — the failure is an EARLY preflight abort, not a late
        //     verify-recover non-convergence after a wasted parse+emit.
        if (combined.Contains("SWIFTBIND111", StringComparison.Ordinal))
            throw new Exception("ingestion-kitchen leg 2: generator log contains SWIFTBIND111 — the missing module was "
                + "caught LATE (verify-recover non-convergence) instead of by the EARLY closure preflight. The preflight "
                + "must fail before ABI parsing.");

        // (4) No artifacts — the abort happened before parse/emit, so nothing was generated.
        var strayCs = Directory.Exists(outputDir)
            ? Directory.EnumerateFiles(outputDir, "*.cs", SearchOption.AllDirectories).ToList()
            : new List<string>();
        var strayWrapper = Directory.Exists(outputDir)
            ? Directory.EnumerateFiles(outputDir, "*.Wrapper.swift", SearchOption.AllDirectories).ToList()
            : new List<string>();
        if (strayCs.Count > 0 || strayWrapper.Count > 0)
        {
            foreach (var f in strayCs) Log.Error("  ✗ stray C# artifact: {File}", f);
            foreach (var f in strayWrapper) Log.Error("  ✗ stray wrapper artifact: {File}", f);
            throw new Exception($"ingestion-kitchen leg 2: the preflight failed but emitted artifacts "
                + $"({strayCs.Count} .cs, {strayWrapper.Count} wrapper) — the abort must happen BEFORE parse/emit so no "
                + "partial binding is ever produced for an unclosed graph.");
        }

        Log.Information("  ✓ missing transitive: generator failed early with a structured SWIFTBIND119 "
            + "(module/importer/evidence/roots/provenance/action), no SWIFTBIND111, no artifacts.");
    }

    // The withdrawal report Details wording the ingestion-quarantine seam stamps on every withdrawn
    // dependent (EmitterFaultRecord.IngestionWithdrawalDetailsPrefix). A row is an ingestion withdrawal
    // iff its Details start with this — distinct from the four verify-recover wordings the resilience
    // gate keys on.
    const string IngestionWithdrawalDetailsPrefix = "Withdrawn by ingestion quarantine:";

    // Leg 3 — PROVEN-CLOSURE QUARANTINE. The pristine Bridge binds cleanly (leg 1). Here we clone the
    // Bridge's complete artifacts and empty EXACTLY QuarantinedPayload's `mangledName` in the ABI JSON —
    // the shape of a digester that emitted a malformed type record. The ingestion ledger must QUARANTINE
    // that type and drag its proven dependent-edge closure (the free functions whose signatures name it)
    // into the SAME withdrawal with origin IngestionWithdrawal, while HealthyControl — which shares no
    // edge with it — survives byte-identically to the leg-1 control. The binding still SUCCEEDS: a
    // genuine degraded binding, not a fatal.
    void AssertIngestionLegProvenClosureQuarantine(
        AbsolutePath bridgeXcframework, AbsolutePath baseXcframework, AbsolutePath controlOutput, AbsolutePath scratch)
    {
        Log.Information("=== ingestion-kitchen leg 3: PROVEN-CLOSURE quarantine (malformed QuarantinedPayload record) ===");

        // (0) Clone the complete Bridge artifacts and empty EXACTLY one type's mangledName. The clone
        //     keeps every other node — including the USR — untouched, so the quarantine is triggered by a
        //     single malformed field, not a wholesale corruption.
        var poisonedBridge = scratch / "leg3-bridge-poisoned.xcframework";
        if (Directory.Exists(poisonedBridge)) poisonedBridge.DeleteDirectory();
        FileSystemTasks_CopyDirectory(bridgeXcframework, poisonedBridge);
        EmptyTypeMangledName(poisonedBridge, "QuarantinedPayload", "Struct");

        // (1) The degraded binding must SUCCEED — a proven closure degrades, it does not fail the module.
        //     --no-verify-csharp: this standalone run does not pack the IngestionBase dependency into a
        //     verification feed, so the in-loop C# verify-recover would fail restore (NU1101) and — with a
        //     withdrawal present — escalate fail-closed. The emitted C# is proven compilable instead by the
        //     authoritative compile-check in step (5).
        var output = scratch / "leg3-output";
        var (exit, log) = RunIngestionGenerator(poisonedBridge, baseXcframework, output, noVerifyCSharp: true);
        var combined = string.Join("\n", log);

        if (exit != 0)
        {
            foreach (var line in log) Log.Error("  [generator] {Line}", line);
            throw new Exception($"ingestion-kitchen leg 3: generator exit {exit} ≠ 0 — a malformed type record whose "
                + "withdrawal closure is PROVABLY complete must degrade to a quarantine and still produce a binding, "
                + "not fail the module.");
        }
        // SWIFTBIND120 is the closure-unproven fatal; on a provable closure it must be ABSENT.
        if (combined.Contains("SWIFTBIND120", StringComparison.Ordinal))
            throw new Exception("ingestion-kitchen leg 3: generator log contains SWIFTBIND120 — the withdrawal closure "
                + "for QuarantinedPayload was declared unprovable, but this fixture's dependents are all enumerable "
                + "signature edges, so the closure IS provable and must not fail closed.");
        // No SWIFTBIND119 (closure preflight) — the graph is closed; no SWIFTBIND111 (verify-recover non-convergence).
        if (combined.Contains("SWIFTBIND119", StringComparison.Ordinal) || combined.Contains("SWIFTBIND111", StringComparison.Ordinal))
            throw new Exception("ingestion-kitchen leg 3: generator log contains SWIFTBIND119/SWIFTBIND111 — the malformed "
                + "record is neither a missing-module obligation nor a verify-recover non-convergence; it must be an "
                + "ingestion quarantine.");

        var emitted = Directory.EnumerateFiles(output, $"{IngestionBridgeModule}*.cs", SearchOption.TopDirectoryOnly).ToList();
        if (emitted.Count == 0)
            throw new Exception($"ingestion-kitchen leg 3: no {IngestionBridgeModule}*.cs emitted — the degraded binding "
                + "produced no C#.");

        // (2) The withdrawal report lists every withdrawn dependent with origin IngestionWithdrawal,
        //     re-attributed to the input configuration at the Parse stage.
        AssertIngestionWithdrawalRows(output / "binding-report.json");

        // (2b) The structured ingestion ledger projects onto the artifact manifest's input-resolution
        //      section: every withdrawn node appears with its identity, disposition, terminal status, and
        //      evidence — so a consumer of the degraded binding can read exactly what was withdrawn and why
        //      from the manifest, not only the human-facing report's SkippedItems.
        AssertIngestionManifestLedger(output / "binding-artifact-manifest.json");

        // (3) HealthyControl — sharing no edge with the quarantined type — survives byte-identically to
        //     the leg-1 control run: the dependent-edge closure never reached it.
        AssertIngestionHealthyControlStable(controlOutput, output, "Control", "leg 3");

        // (4) No live (non-comment) reference to any withdrawn symbol remains in the emitted C# or the
        //     final wrapper Swift — a retained reference to a withdrawn declaration would not compile.
        AssertIngestionNoDanglingWithdrawnRefs(output, new[] { "Quarantined" }, "leg 3");

        // (5) The degraded binding actually COMPILES. Bridge's surface genuinely depends on IngestionBase,
        //     so the compile references a pristine Base binding alongside the degraded Bridge binding.
        AssertIngestionDegradedBindingCompiles(baseXcframework, output, scratch, "leg3");

        Log.Information("  ✓ proven-closure quarantine: generator exit 0, degraded C# emitted + compiles, "
            + "QuarantinedPayload + dependent free functions withdrawn (IngestionWithdrawal/Parse), HealthyControl "
            + "byte-identical to control, no dangling references.");
    }

    // Leg 4 — CROSS-MODULE DEPENDENCY-PROTOCOL QUARANTINE. Leg 3 poisons a PRIMARY-module type; here the
    // malformed record is a DEPENDENCY protocol (IngestionBase.BaseSignal), and the primary module
    // (IngestionBridge) declares BridgeRelay: BaseSignal — a protocol that inherits the poisoned parent
    // across the module boundary. Binding Bridge would otherwise resolve BaseSignal BY NAME out of the
    // dependency protocol stash and lay out its inherited vtable slots against the malformed record — a
    // runtime crash the C#-compile gate can't see. The ingestion-quarantine closure must instead withdraw
    // BridgeRelay WHOLE (an IngestionWithdrawal row naming BaseSignal), while BridgeBeacon: BaseProviding —
    // inheriting a HEALTHY dependency protocol — survives byte-identically. This is the DEGRADE plane's
    // cross-module seed: no parser loss, even one rooted in a dependency, is ever silent.
    void AssertIngestionLegDependencyProtocolQuarantine(
        AbsolutePath bridgeXcframework, AbsolutePath baseXcframework, AbsolutePath controlOutput, AbsolutePath scratch)
    {
        Log.Information("=== ingestion-kitchen leg 4: CROSS-MODULE dependency-protocol quarantine (malformed BaseSignal record) ===");

        // (0) Clone the DEPENDENCY (Base) xcframework and empty EXACTLY BaseSignal's protocol mangledName,
        //     leaving BaseProviding (the healthy control's parent) and every other node untouched.
        var poisonedBase = scratch / "leg4-base-poisoned.xcframework";
        if (Directory.Exists(poisonedBase)) poisonedBase.DeleteDirectory();
        FileSystemTasks_CopyDirectory(baseXcframework, poisonedBase);
        EmptyTypeMangledName(poisonedBase, "BaseSignal", "Protocol");

        // (1) Bind Bridge against the POISONED base. The degraded binding must SUCCEED — the closure that
        //     withdraws BridgeRelay is provable (it is one inheritance edge). --no-verify-csharp for the same
        //     reason as leg 3 (the standalone run packs no dependency verification feed).
        var output = scratch / "leg4-output";
        var (exit, log) = RunIngestionGenerator(bridgeXcframework, poisonedBase, output, noVerifyCSharp: true);
        var combined = string.Join("\n", log);

        if (exit != 0)
        {
            foreach (var line in log) Log.Error("  [generator] {Line}", line);
            throw new Exception($"ingestion-kitchen leg 4: generator exit {exit} ≠ 0 — a malformed DEPENDENCY protocol record "
                + "whose primary-side withdrawal closure is PROVABLY complete (one inheritance edge) must degrade to a "
                + "quarantine and still produce a binding, not fail the module.");
        }
        if (combined.Contains("SWIFTBIND120", StringComparison.Ordinal))
            throw new Exception("ingestion-kitchen leg 4: generator log contains SWIFTBIND120 — the primary-side withdrawal "
                + "closure for the dependency-quarantined BaseSignal is a single inheritance edge, so it IS provable and "
                + "must not fail closed.");
        if (combined.Contains("SWIFTBIND119", StringComparison.Ordinal) || combined.Contains("SWIFTBIND111", StringComparison.Ordinal))
            throw new Exception("ingestion-kitchen leg 4: generator log contains SWIFTBIND119/SWIFTBIND111 — the malformed "
                + "dependency record is neither a missing-module obligation nor a verify-recover non-convergence; it must be "
                + "an ingestion quarantine.");

        var emitted = Directory.EnumerateFiles(output, $"{IngestionBridgeModule}*.cs", SearchOption.TopDirectoryOnly).ToList();
        if (emitted.Count == 0)
            throw new Exception($"ingestion-kitchen leg 4: no {IngestionBridgeModule}*.cs emitted — the degraded binding "
                + "produced no C#.");

        // (2) BridgeRelay is withdrawn WHOLE (type-surface) through an IngestionWithdrawal row
        //     (InputConfiguration/Parse); the healthy control BridgeBeacon is NEVER withdrawn.
        AssertIngestionDependencyWithdrawalRows(output / "binding-report.json");

        // (2b) The withdrawal's CAUSE is the malformed cross-module parent: the generator's own withdrawal
        //      trace names BridgeRelay AND the specific quarantined dependency protocol BaseSignal (the skip
        //      row carries only the withdrawn decl + scope, not the reached poison — that evidence lives in
        //      the SWIFTBIND046 trace). This is the one place the cross-module cause is asserted positively;
        //      it fails if the closure withdrew BridgeRelay for the wrong reason or reported an anonymous '?'.
        if (!Regex.IsMatch(combined,
                @"SWIFTBIND046:\s*withdrawing\s+IngestionBridge\.BridgeRelay[^\n]*BaseSignal"))
        {
            foreach (var line in log) Log.Error("  [generator] {Line}", line);
            throw new Exception("ingestion-kitchen leg 4: the generator's SWIFTBIND046 withdrawal trace does not name "
                + "BridgeRelay's reached poison as the cross-module 'BaseSignal'. Either BridgeRelay was withdrawn for the "
                + "wrong reason, or the withdrawal evidence reported an anonymous '?' instead of the malformed parent "
                + "(a cross-module protocol-inheritance evidence gap).");
        }

        // (2c) Structural proof the emitter did NOT lay out an inherited vtable slot against the malformed
        //      record: the withdrawn protocol emitted NO interface file, while the healthy control did.
        var relayInterface = output / $"{IngestionBridgeModule}.Types.IBridgeRelay.cs";
        if (File.Exists(relayInterface))
            throw new Exception("ingestion-kitchen leg 4: the withdrawn protocol BridgeRelay still emitted an interface file "
                + $"({Path.GetFileName(relayInterface)}) — a whole-type withdrawal must produce no bound surface for the "
                + "protocol that inherits the malformed dependency record.");
        var beaconInterface = output / $"{IngestionBridgeModule}.Types.IBridgeBeacon.cs";
        if (!File.Exists(beaconInterface))
            throw new Exception("ingestion-kitchen leg 4: the healthy control protocol BridgeBeacon emitted NO interface file "
                + $"({Path.GetFileName(beaconInterface)}) — the cross-module withdrawal over-reached to a descendant of the "
                + "HEALTHY BaseProviding, or the control fixture is missing.");

        // (3) BridgeBeacon — inheriting the HEALTHY BaseProviding — survives byte-identically to the leg-1
        //     control run: the cross-module withdrawal reached only the malformed parent's descendant.
        AssertIngestionHealthyControlStable(controlOutput, output, "Beacon", "leg 4");

        // (4) No live reference to the withdrawn BridgeRelay or the malformed BaseSignal remains in the
        //     PRIMARY (Bridge) C# or its wrapper. (Scoped to IngestionBridge*.cs — the pristine Base binding
        //     in the compile-check legitimately emits its own IBaseSignal surface.)
        AssertIngestionNoDanglingWithdrawnRefs(output, new[] { "BridgeRelay", "BaseSignal" }, "leg 4");

        // (5) The degraded binding COMPILES against a PRISTINE Base binding (BaseSignal healthy there) — the
        //     degraded Bridge simply never references it, and BridgeBeacon binds through healthy BaseProviding.
        AssertIngestionDegradedBindingCompiles(baseXcframework, output, scratch, "leg4");

        Log.Information("  ✓ cross-module dependency-protocol quarantine: generator exit 0, degraded C# emitted + compiles, "
            + "BridgeRelay withdrawn whole (IngestionWithdrawal/Parse, names BaseSignal), BridgeBeacon byte-identical to "
            + "control, no dangling references.");
    }

    // (2, leg 4) The dependency-quarantine withdrawal is attributed to the INPUT configuration at the PARSE
    //     stage; BridgeRelay (the primary protocol inheriting the malformed cross-module BaseSignal) is
    //     withdrawn at TYPE-SURFACE scope; the healthy control BridgeBeacon is never named by any withdrawal
    //     row. (The reached poison BaseSignal is asserted separately against the generator's SWIFTBIND046
    //     trace — the skip row itself carries only the withdrawn decl and its scope, not the reached type.)
    void AssertIngestionDependencyWithdrawalRows(AbsolutePath reportPath)
    {
        if (!File.Exists(reportPath))
            throw new Exception($"ingestion-kitchen leg 4: binding-report.json missing at {reportPath}.");

        using var doc = JsonDocument.Parse(File.ReadAllText(reportPath));
        var root = doc.RootElement;
        if (!root.TryGetProperty("SkippedItems", out var items) || items.ValueKind != JsonValueKind.Array)
            throw new Exception("ingestion-kitchen leg 4: binding-report.json has no SkippedItems array — the cross-module "
                + "dependency quarantine produced no withdrawal rows.");

        int ingestionRows = 0;
        bool relayWithdrawnWhole = false;
        foreach (var row in items.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            string Str(string prop) => row.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

            var details = Str("Details");
            if (!details.StartsWith(IngestionWithdrawalDetailsPrefix, StringComparison.Ordinal)) continue;
            ingestionRows++;

            var owner = Str("CauseOwner");
            if (owner != "InputConfiguration")
                throw new Exception($"ingestion-kitchen leg 4: ingestion withdrawal row '{Str("Name")}' CauseOwner='{owner}' "
                    + "(expected InputConfiguration — a malformed dependency ABI record is the input's fault, not the emitter's).");
            var stage = Str("RecoveryStage");
            if (stage != "Parse")
                throw new Exception($"ingestion-kitchen leg 4: ingestion withdrawal row '{Str("Name")}' RecoveryStage='{stage}' "
                    + "(expected Parse — the quarantine is decided at ingestion, not at a compile plane).");

            var rowText = $"{Str("Name")}\n{details}\n{Str("RootCauseId")}";

            // The healthy control must never be dragged into a cross-module withdrawal.
            if (rowText.IndexOf("Beacon", StringComparison.Ordinal) >= 0)
                throw new Exception($"ingestion-kitchen leg 4: ingestion withdrawal row '{Str("Name")}' names the healthy control "
                    + "family 'Beacon' — the cross-module closure over-reached from the malformed BaseSignal to a descendant "
                    + "of the HEALTHY BaseProviding.");

            // BridgeRelay is withdrawn WHOLE (type-surface): the whole protocol goes, because its inherited
            // contract embeds the malformed dependency record — not as a leaf, which would retain the type.
            if (rowText.IndexOf("BridgeRelay", StringComparison.Ordinal) >= 0
                && rowText.IndexOf("!type-surface", StringComparison.Ordinal) >= 0)
                relayWithdrawnWhole = true;
        }

        if (ingestionRows == 0)
            throw new Exception("ingestion-kitchen leg 4: no ingestion-withdrawal rows (Details starting "
                + $"'{IngestionWithdrawalDetailsPrefix}') — the malformed cross-module dependency protocol was not withdrawn "
                + "through the ingestion seam. The primary construct inheriting it may have been emitted against the malformed "
                + "record by name (a silent runtime-crash binding).");

        if (!relayWithdrawnWhole)
            throw new Exception("ingestion-kitchen leg 4: no type-surface ingestion-withdrawal row for 'BridgeRelay'. The "
                + "proven-closure walk failed to withdraw the primary protocol WHOLE for inheriting a dependency-quarantined "
                + "protocol — the emitter would resolve BaseSignal by name from the dependency stash and lay out an inherited "
                + "vtable slot against the malformed record.");

        Log.Information("  ✓ {Count} cross-module ingestion-withdrawal row(s), all InputConfiguration/Parse; BridgeRelay "
            + "withdrawn whole (type-surface), healthy BridgeBeacon never withdrawn.", ingestionRows);
    }

    // Recursively copies a directory tree. Nuke's FileSystemTasks.CopyDirectoryRecursively is available,
    // but wrapping it keeps the call site intent-revealing and the excluded-nothing semantics explicit.
    static void FileSystemTasks_CopyDirectory(AbsolutePath source, AbsolutePath dest)
    {
        dest.CreateDirectory();
        foreach (var dir in Directory.GetDirectories((string)source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(dir.Replace((string)source, (string)dest));
        foreach (var file in Directory.GetFiles((string)source, "*", SearchOption.AllDirectories))
            File.Copy(file, file.Replace((string)source, (string)dest), overwrite: true);
    }

    // Empties EXACTLY the `mangledName` of the TypeDecl of kind `declKind` named `typeName` across every ABI
    // JSON in the cloned xcframework, asserting one-and-only-one node changed and its USR survives — the
    // precise malformed-type-record shape the quarantine path is contracted to catch (a load-bearing name
    // absent while identity remains). Fails loudly if the node is not found or if more than one node matches.
    // `declKind` is "Struct" for the primary-module leg (QuarantinedPayload) and "Protocol" for the
    // dependency-module leg (BaseSignal).
    void EmptyTypeMangledName(AbsolutePath xcframeworkRoot, string typeName, string declKind)
    {
        var abiJsons = Directory.GetFiles((string)xcframeworkRoot, "*.abi.json", SearchOption.AllDirectories);
        if (abiJsons.Length == 0)
            throw new Exception($"ingestion-kitchen: no *.abi.json under the cloned xcframework at {xcframeworkRoot}.");

        int totalChanged = 0;
        foreach (var path in abiJsons)
        {
            var root = JsonNode.Parse(File.ReadAllText(path))!;
            int changed = 0;
            string? preservedUsr = null;
            EmptyMangledNameInPlace(root, typeName, declKind, ref changed, ref preservedUsr);
            if (changed > 1)
                throw new Exception($"ingestion-kitchen: emptying '{typeName}' mangledName matched {changed} nodes in "
                    + $"{Path.GetFileName(path)} — the fixture must have exactly one such {declKind} so the quarantine is "
                    + "attributable to a single malformed record.");
            if (changed == 1)
            {
                if (string.IsNullOrEmpty(preservedUsr))
                    throw new Exception($"ingestion-kitchen: '{typeName}' node in {Path.GetFileName(path)} had no USR to "
                        + "preserve — the malformed-record simulation must keep identity (USR) intact while only the "
                        + "mangledName is absent.");
                File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
                totalChanged += changed;
                Log.Information("  · emptied {Type}.mangledName ({Kind}) in {File} (USR '{Usr}' preserved).",
                    typeName, declKind, Path.GetFileName(path), preservedUsr);
            }
        }

        if (totalChanged == 0)
            throw new Exception($"ingestion-kitchen: the {declKind} '{typeName}' was not found in any ABI JSON of the cloned "
                + $"xcframework — the fixture ('{typeName}') is missing or renamed.");
    }

    // Walks the ABI JSON tree, emptying the `mangledName` of every object that is a TypeDecl of kind
    // `declKind` named `typeName` with a non-empty mangledName. Records the pre-empty USR so the caller can
    // prove identity survived.
    static void EmptyMangledNameInPlace(JsonNode? node, string typeName, string declKind, ref int changed, ref string? preservedUsr)
    {
        switch (node)
        {
            case JsonObject obj:
                var name = obj["name"]?.GetValue<string>();
                var kind = obj["declKind"]?.GetValue<string>();
                var mangled = obj["mangledName"]?.GetValue<string>();
                if (name == typeName && kind == declKind && !string.IsNullOrEmpty(mangled))
                {
                    preservedUsr = obj["usr"]?.GetValue<string>();
                    obj["mangledName"] = "";
                    changed++;
                }
                foreach (var kv in obj)
                    EmptyMangledNameInPlace(kv.Value, typeName, declKind, ref changed, ref preservedUsr);
                break;
            case JsonArray arr:
                foreach (var item in arr)
                    EmptyMangledNameInPlace(item, typeName, declKind, ref changed, ref preservedUsr);
                break;
        }
    }

    // (2) Every ingestion withdrawal row is attributed away from the emitter to the INPUT configuration at
    //     the PARSE stage, and the two dependent free functions on the quarantined type are both present —
    //     proving the dependent-edge closure walk dragged the signature-reaching members into the same
    //     withdrawal, while never touching a healthy sibling (no withdrawal row names the control surface).
    void AssertIngestionWithdrawalRows(AbsolutePath reportPath)
    {
        if (!File.Exists(reportPath))
            throw new Exception($"ingestion-kitchen leg 3: binding-report.json missing at {reportPath}.");

        using var doc = JsonDocument.Parse(File.ReadAllText(reportPath));
        var root = doc.RootElement;
        if (!root.TryGetProperty("SkippedItems", out var items) || items.ValueKind != JsonValueKind.Array)
            throw new Exception("ingestion-kitchen leg 3: binding-report.json has no SkippedItems array — the quarantine "
                + "produced no withdrawal rows.");

        int ingestionRows = 0;
        var covered = new List<string>();
        foreach (var row in items.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            string Str(string prop) => row.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

            var details = Str("Details");
            if (!details.StartsWith(IngestionWithdrawalDetailsPrefix, StringComparison.Ordinal)) continue;
            ingestionRows++;

            // Every ingestion withdrawal is re-attributed: the honest owner is whoever supplied the malformed
            // ABI record (InputConfiguration), decided at the Parse stage — NOT an emitter/compile fault.
            var owner = Str("CauseOwner");
            if (owner != "InputConfiguration")
                throw new Exception($"ingestion-kitchen leg 3: ingestion withdrawal row '{Str("Name")}' CauseOwner='{owner}' "
                    + "(expected InputConfiguration — a malformed input record is the input's fault, not the emitter's).");
            var stage = Str("RecoveryStage");
            if (stage != "Parse")
                throw new Exception($"ingestion-kitchen leg 3: ingestion withdrawal row '{Str("Name")}' RecoveryStage='{stage}' "
                    + "(expected Parse — the quarantine is decided at ingestion, not at a compile plane).");

            // The ratchet: an ingestion withdrawal must ONLY ever name a construct in the quarantined type's
            // dependent closure — every such symbol in THIS fixture carries the 'Quarantined' stem. A row
            // naming a healthy sibling would mean the closure walk over-reached.
            var rowText = $"{Str("Name")}\n{details}\n{Str("RootCauseId")}";
            if (rowText.IndexOf("Quarantined", StringComparison.OrdinalIgnoreCase) < 0)
                throw new Exception($"ingestion-kitchen leg 3: ingestion withdrawal row '{Str("Name")}' does not name a construct "
                    + "in QuarantinedPayload's dependent closure — the closure walk withdrew a member unrelated to the "
                    + "malformed type (over-reach).");
            covered.Add(rowText);
        }

        if (ingestionRows == 0)
            throw new Exception("ingestion-kitchen leg 3: no ingestion-withdrawal rows (Details starting "
                + $"'{IngestionWithdrawalDetailsPrefix}') — the malformed type record was not withdrawn through the "
                + "ingestion seam (it may have been silently dropped or, worse, emitted against a malformed record).");

        // Both dependent free functions must be withdrawn: one names the type as a RETURN, the other as a
        // PARAMETER — the two signature-edge shapes the closure walk must both catch.
        foreach (var dependent in new[] { "makeQuarantinedPayload", "inspectQuarantined" })
        {
            if (!covered.Any(t => t.IndexOf(dependent, StringComparison.OrdinalIgnoreCase) >= 0))
                throw new Exception($"ingestion-kitchen leg 3: the dependent '{dependent}' — whose signature names the "
                    + "quarantined type — has no ingestion-withdrawal row. The proven-closure walk failed to drag a "
                    + "signature-reaching free function into the quarantine (a retained reference to a withdrawn type "
                    + "would not compile).");
        }

        // Enum associated-value payload edge: PayloadCarrier's `.boxed` case embeds the quarantined type in
        // the enum's indivisible in-line layout, so the whole enum must be withdrawn at TYPE-SURFACE scope
        // (not as a leaf). A closure walk that missed enum payloads would retain PayloadCarrier over a
        // withdrawn payload and return a falsely-complete proof.
        if (!covered.Any(t => t.IndexOf("PayloadCarrier", StringComparison.Ordinal) >= 0
                              && t.IndexOf("!type-surface", StringComparison.Ordinal) >= 0))
            throw new Exception("ingestion-kitchen leg 3: the enum 'PayloadCarrier' — whose associated-value payload "
                + "embeds the quarantined type — has no type-surface ingestion-withdrawal row. The proven-closure walk "
                + "failed to withdraw an enum whole through its payload edge.");

        // Operator signature edge on a RETAINED host: `==` names the quarantined type as an operand and is
        // withdrawn as a LEAF, while its host struct PayloadComparator (which has no structural edge to the
        // quarantined type) must survive. Assert both: the operator leaf is withdrawn, and the host is NOT
        // withdrawn whole (no type-surface row for it) — a closure that over-reached to the host, or one that
        // missed the operator, both fail here.
        if (!covered.Any(t => t.IndexOf("|Operator|", StringComparison.Ordinal) >= 0
                              && t.IndexOf("PayloadComparator", StringComparison.Ordinal) >= 0
                              && t.IndexOf("!leaf-api", StringComparison.Ordinal) >= 0))
            throw new Exception("ingestion-kitchen leg 3: the operator on 'PayloadComparator' — whose signature names "
                + "the quarantined type — has no leaf-api ingestion-withdrawal row. The proven-closure walk failed to "
                + "drag a signature-reaching operator into the quarantine.");
        if (covered.Any(t => t.IndexOf("|Type|PayloadComparator", StringComparison.Ordinal) >= 0
                             && t.IndexOf("!type-surface", StringComparison.Ordinal) >= 0))
            throw new Exception("ingestion-kitchen leg 3: the host struct 'PayloadComparator' was withdrawn at "
                + "type-surface scope, but only its `==` operator names the quarantined type — the closure over-reached "
                + "and withdrew a host that shares no structural edge with the malformed type.");

        Log.Information("  ✓ {Count} ingestion-withdrawal row(s), all InputConfiguration/Parse; both dependent free "
            + "functions (return-edge + parameter-edge), the enum-payload whole-withdrawal, and the operator leaf "
            + "(host retained) all present.", ingestionRows);
    }

    // (2b) The structured ingestion ledger projects onto the artifact manifest. Reads
    //      binding-artifact-manifest.json → InputResolution → Ledger and asserts: the projection is total
    //      (LedgerEntryCount == Ledger.Count, QuarantinedCount == the number of Quarantined rows), every row
    //      carries its identity + terminal status + evidence, and the malformed root plus its dependent
    //      closure (the quarantined free functions, the enum-payload whole-withdrawal) are all named. This is
    //      the manifest-side counterpart to the report's SkippedItems: a degraded binding must publish WHY it
    //      is degraded in the structured manifest, not only the human report.
    void AssertIngestionManifestLedger(AbsolutePath manifestPath)
    {
        if (!File.Exists(manifestPath))
            throw new Exception($"ingestion-kitchen leg 3: binding-artifact-manifest.json missing at {manifestPath} — "
                + "the ingestion ledger has no manifest projection to audit.");

        using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var root = doc.RootElement;
        if (!root.TryGetProperty("InputResolution", out var ir) || ir.ValueKind != JsonValueKind.Object)
            throw new Exception("ingestion-kitchen leg 3: manifest has no InputResolution section — the ingestion ledger "
                + "was not projected onto the artifact manifest.");
        if (!ir.TryGetProperty("Ledger", out var ledger) || ledger.ValueKind != JsonValueKind.Array)
            throw new Exception("ingestion-kitchen leg 3: manifest InputResolution has no Ledger array — the structured "
                + "ingestion ledger was not projected (only the aggregate decision counts would be visible).");

        int rows = 0, quarantined = 0;
        var inputs = new List<string>();
        foreach (var e in ledger.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object) continue;
            string Str(string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
            rows++;
            var input = Str("Input");
            if (string.IsNullOrEmpty(input))
                throw new Exception("ingestion-kitchen leg 3: a manifest ledger row has an empty Input identity — a losable "
                    + "node was projected without a stable identity.");
            if (string.IsNullOrEmpty(Str("Evidence")))
                throw new Exception($"ingestion-kitchen leg 3: manifest ledger row '{input}' has empty Evidence — the row "
                    + "does not record why the node was withdrawn.");
            var status = Str("Status");
            if (status.Length == 0)
                throw new Exception($"ingestion-kitchen leg 3: manifest ledger row '{input}' has no terminal Status.");
            if (status == "Quarantined") quarantined++;
            inputs.Add($"{input}|{Str("Disposition")}|{status}|{Str("Referenced")}");
        }

        if (rows == 0)
            throw new Exception("ingestion-kitchen leg 3: the manifest ledger projection is empty — the withdrawn nodes did "
                + "not project onto the manifest even though the report recorded withdrawal rows.");

        int Count(string prop) => ir.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : -1;
        if (Count("LedgerEntryCount") != rows)
            throw new Exception($"ingestion-kitchen leg 3: manifest LedgerEntryCount ({Count("LedgerEntryCount")}) disagrees "
                + $"with the projected Ledger row count ({rows}) — the projection is not total.");
        if (Count("QuarantinedCount") != quarantined)
            throw new Exception($"ingestion-kitchen leg 3: manifest QuarantinedCount ({Count("QuarantinedCount")}) disagrees "
                + $"with the number of Quarantined ledger rows ({quarantined}).");
        if (quarantined == 0)
            throw new Exception("ingestion-kitchen leg 3: no Quarantined rows in the manifest ledger — the proven-closure "
                + "withdrawal did not surface as a quarantine on the manifest.");

        // The malformed root and its signature-reaching dependents must all be named in the manifest ledger,
        // matching the report's SkippedItems closure — the manifest is a losable-node record, not a summary.
        foreach (var expected in new[] { "QuarantinedPayload", "makeQuarantinedPayload", "inspectQuarantined", "PayloadCarrier" })
        {
            if (!inputs.Any(i => i.IndexOf(expected, StringComparison.Ordinal) >= 0))
                throw new Exception($"ingestion-kitchen leg 3: the manifest ledger does not name '{expected}' — the closure "
                    + "that the report recorded did not project fully onto the manifest.");
        }

        Log.Information("  ✓ manifest ledger projects {Rows} node(s) ({Quarantined} quarantined), all with identity + "
            + "status + evidence; malformed root and dependent closure all named.", rows, quarantined);
    }

    // (3) The healthy control surface is byte-for-byte identical between the pristine (leg-1) and degraded
    //     runs: the dependent-edge closure withdrew ONLY the quarantined type's dependents and left every
    //     construct that shares no edge with it untouched — same names, same collision suffixes. `familyStem`
    //     scopes the diff to the declarations naming the healthy control family for this leg ('Control' for
    //     leg 3's HealthyControl family, 'Beacon' for leg 4's BridgeBeacon family — neither stem is ever
    //     carried by the withdrawn family).
    void AssertIngestionHealthyControlStable(AbsolutePath controlOutput, AbsolutePath degradedOutput, string familyStem, string legLabel)
    {
        HashSet<string> ControlLines(AbsolutePath outputDir)
        {
            var lines = new HashSet<string>(StringComparer.Ordinal);
            foreach (var file in Directory.EnumerateFiles(outputDir, $"{IngestionBridgeModule}*.cs", SearchOption.TopDirectoryOnly))
                foreach (var raw in File.ReadLines(file))
                {
                    var trimmed = raw.Trim();
                    if (!trimmed.StartsWith("public ", StringComparison.Ordinal)) continue;
                    if (trimmed.IndexOf(familyStem, StringComparison.Ordinal) < 0) continue;
                    lines.Add(Regex.Replace(trimmed, @"\s+", " "));
                }
            return lines;
        }

        var control = ControlLines(controlOutput);
        var degraded = ControlLines(degradedOutput);

        if (control.Count == 0)
            throw new Exception($"ingestion-kitchen {legLabel}: the leg-1 control emitted no public '{familyStem}' surface — the "
                + "control baseline is empty, so a byte-identical comparison would be vacuous.");

        var lost = control.Except(degraded).OrderBy(s => s, StringComparer.Ordinal).ToList();
        var added = degraded.Except(control).OrderBy(s => s, StringComparer.Ordinal).ToList();
        if (lost.Count > 0 || added.Count > 0)
        {
            foreach (var l in lost) Log.Error("  ✗ {Stem} surface LOST in degraded run: {Line}", familyStem, l);
            foreach (var l in added) Log.Error("  ✗ {Stem} surface ADDED only in degraded run: {Line}", familyStem, l);
            throw new Exception($"ingestion-kitchen {legLabel}: the '{familyStem}' public surface differs between the control and "
                + $"degraded runs ({lost.Count} lost, {added.Count} added). The quarantine's dependent-edge closure must "
                + "not perturb a construct that shares no edge with the malformed type.");
        }

        Log.Information("  ✓ '{Stem}' surface byte-identical to control ({Count} public declaration(s)).", familyStem, control.Count);
    }

    // (4) No live reference to a withdrawn symbol survives: every emitted mention of a withdrawn-family stem
    //     is a `// Unsupported:` tombstone comment, and the final wrapper Swift carries no withdrawn symbol.
    //     `stems` scopes the scan to the withdrawn family for this leg (leg 3: 'Quarantined'; leg 4: the
    //     cross-module parent 'BaseSignal' + its withdrawn descendant 'BridgeRelay'). Scoped to
    //     IngestionBridge*.cs only — the Base binding legitimately emits its own IBaseSignal surface, which is
    //     not a dangling reference in the PRIMARY (Bridge) binding this leg degrades.
    void AssertIngestionNoDanglingWithdrawnRefs(AbsolutePath outputDir, string[] stems, string legLabel)
    {
        bool Mentions(string line) => stems.Any(s => line.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0);

        foreach (var file in Directory.EnumerateFiles(outputDir, $"{IngestionBridgeModule}*.cs", SearchOption.TopDirectoryOnly))
            foreach (var raw in File.ReadLines(file))
            {
                if (!Mentions(raw)) continue;
                if (raw.TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
                throw new Exception($"ingestion-kitchen {legLabel}: live (non-comment) reference to a withdrawn symbol in "
                    + $"{Path.GetFileName(file)}:\n    {raw.Trim()}\nA withdrawn type and its dependents must "
                    + "appear only in `// Unsupported:` tombstones.");
            }

        var wrapper = outputDir / $"{IngestionBridgeModule}.Wrapper.swift";
        if (File.Exists(wrapper))
        {
            foreach (var raw in File.ReadLines(wrapper))
            {
                if (!Mentions(raw)) continue;
                if (raw.TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
                throw new Exception($"ingestion-kitchen {legLabel}: the final wrapper Swift still carries a live withdrawn "
                    + $"symbol:\n    {raw.Trim()}\nThe emitter must re-emit the wrapper without the withdrawn units.");
            }
        }

        Log.Information("  ✓ no dangling references: withdrawn symbols appear only in `// Unsupported:` comments.");
    }

    // (5) The degraded Bridge binding COMPILES. Bridge's public surface genuinely depends on IngestionBase,
    //     so a pristine Base binding is generated and compiled alongside the degraded Bridge binding in one
    //     hermetic csproj referencing only Swift.Runtime + Swift.Bindings.Apple.
    void AssertIngestionDegradedBindingCompiles(AbsolutePath baseXcframework, AbsolutePath degradedBridgeOutput, AbsolutePath scratch, string legTag)
    {
        // A pristine Base binding — the real cross-module dependency the degraded Bridge surface references.
        var baseOutput = scratch / $"{legTag}-base-output";
        var (baseExit, baseLog) = RunIngestionGenerator(baseXcframework, dependencyXcframework: null, baseOutput);
        if (baseExit != 0)
        {
            foreach (var line in baseLog) Log.Error("  [base-generator] {Line}", line);
            throw new Exception($"ingestion-kitchen {legTag}: binding IngestionBase for the compile-check failed (exit {baseExit}).");
        }

        var csprojDir = scratch / $"{legTag}-compile-check";
        if (Directory.Exists(csprojDir)) csprojDir.DeleteDirectory();
        csprojDir.CreateDirectory();
        var csprojPath = csprojDir / "IngestionDegradedCompileCheck.csproj";

        var csproj = $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Library</OutputType>
                <TargetFramework>net10.0-ios</TargetFramework>
                <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
                <Nullable>enable</Nullable>
                <NoWarn>CS0169;CS0649;CA1418;CA1420</NoWarn>
                <!-- Mirror how REAL generated bindings compile: the emitted binding csproj sets
                     only a NoWarn list and never TreatWarningsAsErrors, and the generator's own
                     C# verifier builds bindings with -p:TreatWarningsAsErrors=false. This check
                     lives inside the repo tree, so it would otherwise inherit the generator repo's
                     Directory.Build.props TreatWarningsAsErrors=True and fail on style warnings a
                     real consumer never sees (e.g. CS0660/CS0661 on an operator type that does not
                     also override Equals/GetHashCode). Compile ERRORS — a genuine dangling reference
                     to a withdrawn type — still fail the build. -->
                <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
              </PropertyGroup>
              <ItemGroup>
                <AssemblyAttribute Include="System.Runtime.CompilerServices.DisableRuntimeMarshallingAttribute" />
              </ItemGroup>
              <ItemGroup>
                <ProjectReference Include="{SourceDir / "Swift.Runtime" / "src" / "Swift.Runtime.csproj"}" />
                <ProjectReference Include="{SourceDir / "Swift.Bindings.Apple" / "Swift.Bindings.Apple.csproj"}" />
              </ItemGroup>
              <ItemGroup>
                <Compile Include="{baseOutput / $"{IngestionBaseModule}*.cs"}" />
                <Compile Include="{degradedBridgeOutput / $"{IngestionBridgeModule}*.cs"}" />
              </ItemGroup>
            </Project>
            """;
        File.WriteAllText(csprojPath, csproj);

        Log.Information("=== ingestion-kitchen {LegTag}: compile-checking the degraded binding (Base + degraded Bridge) ===", legTag);
        DotNetBuild(s => s
            .SetProjectFile(csprojPath)
            .SetConfiguration("Debug")
            .SetVerbosity(DotNetVerbosity.quiet));
        Log.Information("  ✓ degraded binding compiled cleanly.");
    }
}
