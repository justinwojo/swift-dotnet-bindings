// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Build.OsLoadSafetyGate.cs — "a missing OS symbol can never stop a wrapper loading" (load-safety P1)
// for the wrappers this build links itself, and the gate that reads every built wrapper slice back.
//
// Swift weak-links a reference from the imported declaration's availability alone. An SDK member
// declared without a floor that an older OS does not ship is therefore a strong import, and dyld then
// refuses the whole wrapper, and the app with it, on that OS. The generator's wrapper links weak-link
// every OS-resident framework (OsFrameworkWeakLinking); the harness-built BindingTests wrapper and the
// Apple supplement link the same way through WeakLinkOsFrameworks below.
//
// The gate runs in every `--compile-only` invocation after the thunk weak-link gate. For every slice
// of both BindingTests wrappers it requires every `/System/` dylib load to be LC_LOAD_WEAK_DYLIB, and
// requires the wrapper to load at least one OS framework so the check cannot pass on an empty read.
// Fail-closed and unconditional, with no `--permissive` arm: a soundness property, not a baseline.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BindingsGeneration;
using Nuke.Common.IO;
using Nuke.Common.Tooling;
using Serilog;

partial class Build
{
    /// <summary>Reads <paramref name="binary"/>'s dylib load commands with <c>otool -l</c>.</summary>
    IReadOnlyList<OsFrameworkWeakLinking.LoadedDylib> ReadDylibLoads(AbsolutePath binary) =>
        OsFrameworkWeakLinking.ParseLoadCommands(string.Join("\n",
            XcRunTool($"otool -l \"{binary}\"", logOutput: false, logInvocation: false)
                .Where(o => o.Type == OutputType.Std).Select(o => o.Text)));

    /// <summary>
    /// Load-safety P1 for a wrapper this build links: reads the linked <paramref name="binary"/>'s load
    /// commands and, when an OS-resident framework came out strong, relinks once through
    /// <paramref name="relink"/> with that set weak-linked, then requires nothing under <c>/System/</c>
    /// to be loaded strongly. <paramref name="relink"/> receives the swiftc arguments to append and
    /// returns whether the relink succeeded.
    /// </summary>
    void WeakLinkOsFrameworks(string label, AbsolutePath binary, Func<IReadOnlyList<string>, bool> relink)
    {
        var strong = OsFrameworkWeakLinking.StrongOsResidentLoads(ReadDylibLoads(binary));
        if (strong.Count == 0)
            return;
        var frameworks = strong.Select(OsFrameworkWeakLinking.FrameworkName).OfType<string>()
            .Distinct(StringComparer.Ordinal).OrderBy(f => f, StringComparer.Ordinal).ToList();
        Log.Information("{Label}: relinking to weak-link the OS framework(s) it loaded strongly: {Frameworks}",
            label, string.Join(", ", frameworks));
        var args = frameworks.SelectMany(f => new[] { "-Xlinker", "-weak_framework", "-Xlinker", f }).ToList();
        if (frameworks.Count == 0 || !relink(args))
            throw new Exception(OsFrameworkWeakLinking.StillStrongMessage(label, binary, strong));
        var still = OsFrameworkWeakLinking.StrongOsResidentLoads(ReadDylibLoads(binary));
        if (still.Count > 0)
            throw new Exception(OsFrameworkWeakLinking.StillStrongMessage(label, binary, still));
    }

    /// <summary>
    /// Reads every slice of both BindingTests wrappers back and requires every OS-resident dylib load to
    /// be weak. Invoked from the --compile-only path once both wrappers are built.
    /// </summary>
    void RunOsLoadSafetyGate()
    {
        Log.Information("=========================================");
        Log.Information(" OS load-safety gate");
        Log.Information("=========================================");

        var depWrapper = $"{DepModuleName}SwiftBindings";
        var legs = new[]
        {
            (Leg: WrapperModule, Xcframework: BtOutputDir / $"{WrapperModule}.xcframework"),
            (Leg: depWrapper, Xcframework: BtOutputDir / $"{depWrapper}.xcframework"),
        };

        var failures = new List<string>();
        foreach (var (leg, xcframework) in legs)
        {
            var binaries = Directory.Exists(xcframework)
                ? Directory.GetDirectories(xcframework)
                    .Select(slice => (AbsolutePath)Path.Combine(slice, $"{leg}.framework", leg))
                    .Where(b => File.Exists(b))
                    .OrderBy(b => b.ToString(), StringComparer.Ordinal)
                    .ToList()
                : new List<AbsolutePath>();
            if (binaries.Count == 0)
            {
                failures.Add($"{leg}: no wrapper slice found under {xcframework}.");
                continue;
            }
            foreach (var binary in binaries)
            {
                var verdict = OsLoadSafetyAudit.EvaluateLoads(leg, binary, ReadDylibLoads(binary));
                Log.Information("{Leg} {Slice}: {Os} OS-resident load(s), {Weak} weak",
                    leg, Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(binary))),
                    verdict.OsResidentLoadCount, verdict.WeakOsResidentLoadCount);
                failures.AddRange(verdict.Failures);
            }
        }

        if (failures.Count > 0)
        {
            foreach (var f in failures)
                Log.Error("  {Failure}", f);
            throw new Exception(
                $"OS load-safety gate: {failures.Count} failure(s). A strong load of an OS framework makes dyld refuse " +
                "the whole wrapper, and the app with it, on an OS that lacks one of the symbols it imports — see the lines above.");
        }

        Log.Information("OS load-safety gate: PASSED");
    }
}
