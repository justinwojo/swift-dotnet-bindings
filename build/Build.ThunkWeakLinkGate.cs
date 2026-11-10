// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Build.ThunkWeakLinkGate.cs — "a thunk never strong-imports its target" ship gate for
// `nuke binding-tests --compile-only`.
//
// A native thunk names the Swift symbol it bridges to (and, for statics and initializers, the type's
// metadata accessor) in hand-written assembly. swiftc weak-links a reference to anything floored
// above the deployment target; the assembler and linker know nothing about availability, so a plain
// assembly reference is a strong import. A strong import of a symbol the running OS lacks is a dyld
// load failure for the whole wrapper — the app dies at launch on that OS even if nothing calls the
// member, and the managed availability guard never runs because it runs after load. The generator
// therefore declares every thunk target `.weak_reference`.
//
// The gate checks that at two levels (the reasoning is on ThunkWeakLinkAudit):
//   1. It assembles every emitted thunk file — each architecture the generator wrote, not just the
//      one this lane links — and requires every undefined symbol of the object to be weak.
//   2. It reads the linked wrapper back the way dyld does (`dyld_info -imports`) and requires every
//      import of the iOS 99 fixtures in `RuntimeAvailabilityGuard.swift` to be weak, whether a thunk
//      or Swift code references it, and the thunk-bound fixtures to still arrive through a thunk so
//      the check keeps covering the thunk path.
// Both wrappers are checked: the main wrapper, which this harness compiles, and the dependency
// wrapper, which the generator's own wrapper compile produces (the path SDK-mode consumers ship).
//
// Fail-closed and unconditional, with no `--permissive` arm: this is a soundness property of the
// emitted artifact, not a baseline, so there is nothing to reseed.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Nuke.Common.IO;
using Nuke.Common.Tooling;
using Serilog;

partial class Build
{
    /// <summary>
    /// Every fixture in <c>RuntimeAvailabilityGuard.swift</c> floored above every deployment target
    /// (<c>@available(iOS 99, …)</c>), by a fragment of its mangled names: the two whole future-floored
    /// types, and the future-floored members of otherwise ordinary types. Each must be imported, and
    /// only weakly. The ones the generator binds through a native thunk (instance and static methods,
    /// and the metadata accessor the static path calls) must also still arrive through one; the rest
    /// reach the wrapper from Swift code only.
    /// </summary>
    static readonly FutureFlooredFixture[] ThunkWeakLinkFutureFlooredFixtures =
    {
        new("15FutureGatedTypeV", ReachedThroughThunk: true),
        new("19RuntimeGuardCarrierV18futureOnlyInstance", ReachedThroughThunk: true),
        new("19RuntimeGuardCarrierV16futureOnlyStatic", ReachedThroughThunk: true),
        new("17FutureGatedBufferV", ReachedThroughThunk: false),
        new("14GuardedOperandV1poi", ReachedThroughThunk: false),
    };

    /// <summary>
    /// Asserts that native thunks reference their targets weakly and that the linked wrappers import
    /// every future-floored fixture weakly. Invoked from the --compile-only path once both wrappers
    /// are built.
    /// </summary>
    void RunThunkWeakLinkGate()
    {
        Log.Information("=========================================");
        Log.Information(" Thunk weak-link gate");
        Log.Information("=========================================");

        var platform = ResolvedPlatform;
        var sliceId = platform.SimulatorSliceId;
        var depWrapper = $"{DepModuleName}SwiftBindings";
        var scratch = BtOutputDir / ".thunk-weak-link";
        if (Directory.Exists(scratch))
            scratch.DeleteDirectory();
        scratch.CreateDirectory();

        var legs = new[]
        {
            (Leg: WrapperModule,
             AssemblyDir: BtOutputDir,
             Binary: BtOutputDir / $"{WrapperModule}.xcframework" / sliceId / $"{WrapperModule}.framework" / WrapperModule,
             Fixtures: ThunkWeakLinkFutureFlooredFixtures),
            (Leg: depWrapper,
             AssemblyDir: BtOutputDir / "dep-swift",
             Binary: BtOutputDir / $"{depWrapper}.xcframework" / sliceId / $"{depWrapper}.framework" / depWrapper,
             Fixtures: Array.Empty<FutureFlooredFixture>()),
        };

        var failures = new List<string>();
        foreach (var (leg, assemblyDir, binary, fixtures) in legs)
        {
            var assemblyFiles = Directory.Exists(assemblyDir)
                ? Directory.GetFiles(assemblyDir, "*.s").OrderBy(f => f, StringComparer.Ordinal).ToList()
                : new List<string>();

            var targets = new HashSet<string>(StringComparer.Ordinal);
            var references = new List<ObjectReference>();
            foreach (var asm in assemblyFiles)
            {
                targets.UnionWith(ThunkWeakLinkAudit.ParseBranchTargets(File.ReadAllText(asm)));

                // `{ns}.{arch}.s` — assemble each for its own architecture on this lane's platform.
                var arch = Path.GetExtension(Path.GetFileNameWithoutExtension(asm)).TrimStart('.');
                var triple = arch + platform.SimulatorTarget.Substring(platform.SimulatorTarget.IndexOf('-'));
                var obj = scratch / $"{leg}.{Path.GetFileNameWithoutExtension(asm)}.o";
                XcRunTool($"clang -c \"{asm}\" -o \"{obj}\" -target {triple}", logOutput: false);
                references.AddRange(ThunkWeakLinkAudit.ParseUndefinedSymbols(StdOut(XcRunTool($"nm -m \"{obj}\"", logOutput: false, logInvocation: false))));
            }

            IReadOnlyList<DyldImport> imports = Array.Empty<DyldImport>();
            if (File.Exists(binary))
                imports = ThunkWeakLinkAudit.ParseImports(StdOut(XcRunTool($"dyld_info -imports \"{binary}\"", logOutput: false, logInvocation: false)));
            else
                failures.Add($"{leg}: wrapper binary not found at {binary}.");

            var verdict = ThunkWeakLinkAudit.Evaluate(leg, targets, references, imports, fixtures);
            Log.Information(
                "{Leg}: {Files} thunk file(s), {Targets} target(s), {Weak} weak in the objects; linked wrapper imports {Imported} " +
                "({Strong} strong because Swift code also references them unconditionally)",
                leg, assemblyFiles.Count, verdict.ThunkTargetCount, verdict.WeakObjectReferenceCount,
                verdict.ImportedThunkTargetCount, verdict.StrongImportedThunkTargetCount);
            failures.AddRange(verdict.Failures);
        }

        if (failures.Count > 0)
        {
            foreach (var f in failures)
                Log.Error("  {Failure}", f);
            throw new Exception(
                $"Thunk weak-link gate: {failures.Count} failure(s). A strong import of a symbol newer than the " +
                "deployment target makes dyld refuse to load the wrapper on an older OS — see the lines above.");
        }

        Log.Information("Thunk weak-link gate: PASSED");

        static string StdOut(IReadOnlyCollection<Output> output) =>
            string.Join("\n", output.Where(o => o.Type == OutputType.Std).Select(o => o.Text));
    }
}
