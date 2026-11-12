// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BindingsGeneration.Tests;

/// <summary>
/// Collects the lowering evidence of plan targets from swiftc's IR: each target's calling convention
/// and normalized signature where a module defines it, or else where a module declares it to call it.
/// </summary>
public static class SwiftLoweringEvidenceCollector
{
    /// <summary>Every forward plan target of the plan sidecars at <paramref name="planFiles"/>.</summary>
    public static SortedSet<string> Targets(IEnumerable<string> planFiles) => new(
        planFiles.SelectMany(f => SwiftCallPlanSet.Read(f).Plans)
            .Where(p => p.Body.Kind == SwiftPlanKind.Forward)
            .Select(p => p.Body.Target)
            .OfType<string>(),
        StringComparer.Ordinal);

    /// <summary>
    /// The evidence of each target the modules show: a definition wins over a declaration. Two modules
    /// that show the same target differently are recorded as the conflict, which no plan can match.
    /// </summary>
    public static SortedDictionary<string, string> Signatures(IEnumerable<string> targets, IReadOnlyList<SwiftIrModule> modules)
    {
        var evidence = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var target in targets)
        {
            var defined = modules.Select(m => m.Functions.GetValueOrDefault(target)).OfType<SwiftIrFunction>().Select(Text).Distinct().ToList();
            var shown = defined.Count > 0
                ? defined
                : modules.Select(m => m.Declarations.GetValueOrDefault(target)).OfType<SwiftIrFunction>().Select(Text).Distinct().ToList();
            if (shown.Count == 1)
                evidence[target] = shown[0];
            else if (shown.Count > 1)
                evidence[target] = "conflict: " + string.Join(" | ", shown.Order(StringComparer.Ordinal));
        }
        return evidence;
    }

    private static string Text(SwiftIrFunction function) => $"{function.CallingConvention} {function.Signature}";

    /// <summary>
    /// Records <paramref name="symbols"/> as this toolchain's evidence on <paramref name="triple"/> in
    /// the evidence file at <paramref name="path"/>, keeping every other toolchain and triple.
    /// </summary>
    public static void Record(string path, string toolchain, string triple, IReadOnlyDictionary<string, string> symbols)
    {
        var evidence = File.Exists(path) ? SwiftLoweringEvidence.Load(path) : SwiftLoweringEvidence.Empty;
        File.WriteAllText(path, evidence.WithTriple(toolchain, triple, symbols).ToJson());
    }
}
