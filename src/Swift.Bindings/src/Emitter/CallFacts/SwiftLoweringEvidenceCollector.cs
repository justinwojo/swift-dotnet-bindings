// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Immutable;
using System.Reflection;

namespace BindingsGeneration;

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

    /// <summary>The reason a target no evidence source showed is recorded as unobserved.</summary>
    public const string UnseenReason = "no evidence source defines or declares it";

    /// <summary>
    /// The slice of evidence the compile showed: <paramref name="symbols"/> for the targets it saw,
    /// every other of <paramref name="targets"/> as unobserved (with <paramref name="unseenReason"/>'s
    /// reason, or <see cref="UnseenReason"/>), and the SDK and sources it read.
    /// </summary>
    public static SwiftLoweringSlice Slice(
        string sdk, IEnumerable<SwiftEvidenceSource> sources, IEnumerable<string> targets, IReadOnlyDictionary<string, string> symbols,
        Func<string, string>? unseenReason = null)
    {
        var unseen = targets.Where(t => !symbols.ContainsKey(t))
            .ToImmutableSortedDictionary(t => t, t => unseenReason?.Invoke(t) ?? UnseenReason, StringComparer.Ordinal);
        return new SwiftLoweringSlice
        {
            Sdk = sdk,
            Sources = sources.ToImmutableArray(),
            Symbols = symbols.ToImmutableSortedDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal),
            Unobserved = unseen.IsEmpty ? null : unseen,
        };
    }

    /// <summary>The generator that records the evidence, by name and version, without build metadata.</summary>
    public static string Generator =>
        "Swift.Bindings " + (typeof(SwiftLoweringEvidence).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(SwiftLoweringEvidence).Assembly.GetName().Version?.ToString() ?? "unknown").Split('+')[0];

    /// <summary>
    /// Records <paramref name="slice"/> as <paramref name="artifact"/>'s evidence on
    /// <paramref name="triple"/> under <paramref name="compiler"/> in the evidence file at
    /// <paramref name="path"/>, keeping every other artifact, compiler family and slice.
    /// </summary>
    public static void Record(string path, string artifact, string compiler, string triple, SwiftLoweringSlice slice)
    {
        var evidence = File.Exists(path) ? SwiftLoweringEvidence.Load(path) : SwiftLoweringEvidence.Empty;
        Write(path, evidence.WithSlice(artifact, compiler, SwiftLoweringContract.Current, Generator, triple, slice));
    }

    /// <summary>
    /// Writes <paramref name="evidence"/> to <paramref name="path"/> so that a reader sees the previous
    /// file or the whole new one, never part of it: a same-directory temporary file renamed into place.
    /// </summary>
    public static void Write(string path, SwiftLoweringEvidence evidence) => AtomicArtifactWriter.Write(path, evidence.ToJson());
}
