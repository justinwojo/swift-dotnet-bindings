// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BindingsGeneration.Producers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Full-corpus parity bake — the cutover gate that authorizes deleting the regex
/// producer (S15 Phase A→B). Drives EVERY public <c>.swiftinterface</c> under the
/// fetched validation libraries (<c>.libraries/</c>) through both the
/// <see cref="RegexInterfaceFactsProducer"/> and the
/// <see cref="SwiftSyntaxInterfaceFactsProducer"/>, then diffs all 31 facts. A clean
/// run (zero divergences) is the measured proof that SwiftSyntax can become the sole
/// producer without changing a single emitted fact.
/// <para/>
/// Unlike <see cref="InterfaceFactsProducerParityTests"/> (synthetic hand-written
/// snippets, runs every <c>nuke test</c>), this gate runs over hundreds of real-world
/// interfaces and is therefore <b>opt-in</b>: it is skipped unless the
/// <c>RUN_PARITY_BAKE</c> environment variable is set, so the inner-loop test run stays
/// fast and CI without fetched libraries does not fail. Run it with:
/// <code>RUN_PARITY_BAKE=1 dotnet test … --filter FullyQualifiedName~InterfaceFactsCorpusParityBake</code>
/// (after <c>nuke fetch</c> + <c>nuke compile</c>).
/// <para/>
/// Because it compares the two producers head-to-head, this gate can only exist while
/// both producers exist; it is removed alongside the regex producer in Phase B.
/// </summary>
public class InterfaceFactsCorpusParityBake
{
    private const string EnableEnvVar = "RUN_PARITY_BAKE";
    private const int MaxReportedDivergences = 60;

    [SkippableFact]
    public void RegexAndSwiftSyntax_ProduceIdenticalFacts_AcrossValidationCorpus()
    {
        Skip.If(string.IsNullOrEmpty(Environment.GetEnvironmentVariable(EnableEnvVar)),
            $"Full-corpus parity bake is opt-in. Set {EnableEnvVar}=1 (and run `nuke fetch` " +
            "+ `nuke compile` first) to execute the cutover gate.");

        var binaryPath = SwiftSyntaxInterfaceFactsProducer.TryLocateBinary();
        Skip.If(binaryPath is null || !File.Exists(binaryPath),
            "SwiftInterfaceParser host binary not found — run `nuke compile`.");

        var librariesDir = LocateLibrariesDir();
        Skip.If(librariesDir is null,
            "Validation libraries not fetched (.libraries/ absent) — run `nuke fetch`.");

        // PRODUCTION-RELEVANT CORPUS ONLY: the generator consumes the `.xcframework`
        // slice (`--xcframework`), never the source-build intermediates (`build-device/`,
        // `.build/`, `Intermediates.noindex/`, `artifacts/`) that the xcframework assembly
        // leaves behind under `.libraries/`. Diffing those would measure parity on files
        // no consumer ever feeds the generator.
        var files = Directory
            .EnumerateFiles(librariesDir!, "*.swiftinterface", SearchOption.AllDirectories)
            .Where(p => !p.EndsWith(".private.swiftinterface", StringComparison.Ordinal))
            .Where(p => p.Contains($".xcframework{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        Skip.If(files.Count == 0, "No .xcframework .swiftinterface files found under .libraries/.");

        var regex = new RegexInterfaceFactsProducer();
        var swiftSyntax = new SwiftSyntaxInterfaceFactsProducer(binaryPath!);

        var divergences = new List<string>();
        var byKind = new Dictionary<InterfaceFactKind, int>();
        var filesWithDivergence = 0;
        var compared = 0;
        foreach (var file in files)
        {
            ProducerResult regexResult;
            ProducerResult swiftResult;
            try
            {
                regexResult = regex.Produce(file, NullLogger.Instance);
                swiftResult = swiftSyntax.Produce(file, NullLogger.Instance);
            }
            catch (Exception ex)
            {
                divergences.Add($"{Rel(librariesDir!, file)}: PRODUCER THREW {ex.GetType().Name}: {ex.Message}");
                continue;
            }

            compared++;
            var before = divergences.Count;
            DiffFacts(Rel(librariesDir!, file), regexResult.Facts, swiftResult.Facts,
                swiftResult.CoveredFacts, divergences, byKind);
            if (divergences.Count > before)
            {
                filesWithDivergence++;
            }
        }

        if (divergences.Count > 0)
        {
            var histogram = string.Join("\n", byKind
                .OrderByDescending(kv => kv.Value)
                .Select(kv => $"    {kv.Value,6}  {kv.Key}"));
            var shown = string.Join("\n", divergences.Take(MaxReportedDivergences));
            var tail = divergences.Count > MaxReportedDivergences
                ? $"\n… and {divergences.Count - MaxReportedDivergences} more."
                : string.Empty;
            Assert.Fail(
                $"Parity bake found {divergences.Count} divergence(s) in {filesWithDivergence} of " +
                $"{compared} compared interface(s).\nPer-fact divergence histogram (file-count):\n{histogram}\n\n" +
                $"First {MaxReportedDivergences} divergences:\n{shown}{tail}");
        }

        // A clean run must actually have compared something — guard against a silently
        // empty corpus passing as "parity".
        Assert.True(compared > 0, "Parity bake compared zero interfaces.");
    }

    /// <summary>Compares every fact the SwiftSyntax producer claims to cover against the
    /// regex producer's value; appends one human-readable line per divergence.</summary>
    private static void DiffFacts(
        string file,
        PartialSwiftInterfaceFacts r,
        PartialSwiftInterfaceFacts s,
        HashSet<InterfaceFactKind> covered,
        List<string> outDivergences,
        Dictionary<InterfaceFactKind, int> byKind)
    {
        void Cmp(InterfaceFactKind kind, Func<PartialSwiftInterfaceFacts, string> render)
        {
            // Only assert parity on facts SwiftSyntax will own after cutover. The regex
            // producer declares full coverage, so `covered` is the limiting set.
            if (!covered.Contains(kind))
            {
                return;
            }

            var regexValue = render(r);
            var swiftValue = render(s);
            if (!string.Equals(regexValue, swiftValue, StringComparison.Ordinal))
            {
                outDivergences.Add(
                    $"{file} [{kind}]:\n    regex ={Trunc(regexValue)}\n    swift ={Trunc(swiftValue)}");
                byKind[kind] = byKind.TryGetValue(kind, out var n) ? n + 1 : 1;
            }
        }

        Cmp(InterfaceFactKind.InternalMemberKeys, f => CanonSet(f.InternalMemberKeys));
        Cmp(InterfaceFactKind.PublicMemberNames, f => CanonSet(f.PublicMemberNames));
        Cmp(InterfaceFactKind.ParameterNames, f => CanonMapSeq(f.ParameterNames));
        Cmp(InterfaceFactKind.TypedThrowsErrors, f => CanonMapStr(f.TypedThrowsErrors));
        Cmp(InterfaceFactKind.EnumCaseLabels, f => CanonMapSeqNullable(f.EnumCaseLabels));
        Cmp(InterfaceFactKind.EnumCaseRawValues, f => CanonMapStr(f.EnumCaseRawValues));
        Cmp(InterfaceFactKind.PublicTypeNames, f => CanonSet(f.PublicTypeNames));
        Cmp(InterfaceFactKind.MainActorTypes, f => CanonSet(f.MainActorTypes));
        Cmp(InterfaceFactKind.CustomActorTypes, f => CanonSet(f.CustomActorTypes));
        Cmp(InterfaceFactKind.CustomActorIsolatorMap, f => CanonMapStr(f.CustomActorIsolatorMap));
        Cmp(InterfaceFactKind.ActorIsolatedMembers, f => CanonSet(f.ActorIsolatedMembers));
        Cmp(InterfaceFactKind.MainActorIsolatedMembers, f => CanonSet(f.MainActorIsolatedMembers));
        Cmp(InterfaceFactKind.NonisolatedMembers, f => CanonSet(f.NonisolatedMembers));
        Cmp(InterfaceFactKind.MarkerProtocolConformances, f => CanonMapPool(f.MarkerProtocolConformances));
        Cmp(InterfaceFactKind.AvailabilityAnnotations, f => CanonAvail(f.AvailabilityAnnotations));
        Cmp(InterfaceFactKind.DefaultParameterValues, f => CanonMapSeqNullable(f.DefaultParameterValues));
        Cmp(InterfaceFactKind.AutoclosureParameters, f => CanonMapBool(f.AutoclosureParameters));
        Cmp(InterfaceFactKind.ConstLiteralParameters, f => CanonMapBool(f.ConstLiteralParameters));
        Cmp(InterfaceFactKind.ClosureParameterAttributes, f => CanonMapNestedList(f.ClosureParameterAttributes));
        Cmp(InterfaceFactKind.ObjCRuntimeNames, f => CanonMapStr(f.ObjCRuntimeNames));
        Cmp(InterfaceFactKind.SubscriptLabels, f => CanonMapSeq(f.SubscriptLabels));
        Cmp(InterfaceFactKind.VariadicMembers, f => CanonSet(f.VariadicMembers));
        Cmp(InterfaceFactKind.ConventionCProtocols, f => CanonSet(f.ConventionCProtocols));
        Cmp(InterfaceFactKind.HiddenRequirementProtocols, f => CanonMapSet(f.HiddenRequirementProtocols));
        Cmp(InterfaceFactKind.MainActorTypePositions, f => CanonMapPos(f.MainActorTypePositions));
        Cmp(InterfaceFactKind.AvailabilityAnnotationPositions, f => CanonMapPos(f.AvailabilityAnnotationPositions));
        Cmp(InterfaceFactKind.ConventionCProtocolPositions, f => CanonMapPos(f.ConventionCProtocolPositions));
        Cmp(InterfaceFactKind.ProtocolNames, f => CanonSet(f.ProtocolNames));
        Cmp(InterfaceFactKind.ProtocolExtensionMethods, f => CanonPem(f.ProtocolExtensionMethods));
        Cmp(InterfaceFactKind.ExtensionMemberCandidates, f => CanonEmc(f.ExtensionMemberCandidates));
        Cmp(InterfaceFactKind.SpiOnlyConformances, f => CanonSet(f.SpiOnlyConformances));
    }

    // ----- canonical renderers -------------------------------------------------------
    // Set-like fields: order-insensitive → sort. Dictionary keys: always sorted.
    // Index-aligned value lists (param-positional facts): order preserved. Pool-like
    // value lists (conformer sets, extension-member bags): sorted, since the two
    // producers may legitimately accumulate them in different encounter order.

    private const string Nil = "∅";

    private static string CanonSet(IEnumerable<string>? items) =>
        items is null ? Nil : "[" + string.Join(",", items.OrderBy(x => x, StringComparer.Ordinal)) + "]";

    private static string CanonMapStr(IReadOnlyDictionary<string, string>? m) =>
        m is null ? Nil : "{" + string.Join(",",
            m.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}")) + "}";

    private static string CanonMapSeq(IReadOnlyDictionary<string, List<string>>? m) =>
        m is null ? Nil : "{" + string.Join(",",
            m.OrderBy(kv => kv.Key, StringComparer.Ordinal)
             .Select(kv => $"{kv.Key}=[{string.Join("|", kv.Value)}]")) + "}";

    private static string CanonMapSeqNullable(IReadOnlyDictionary<string, List<string?>>? m) =>
        m is null ? Nil : "{" + string.Join(",",
            m.OrderBy(kv => kv.Key, StringComparer.Ordinal)
             .Select(kv => $"{kv.Key}=[{string.Join("|", kv.Value.Select(v => v ?? Nil))}]")) + "}";

    private static string CanonMapBool(IReadOnlyDictionary<string, List<bool>>? m) =>
        m is null ? Nil : "{" + string.Join(",",
            m.OrderBy(kv => kv.Key, StringComparer.Ordinal)
             .Select(kv => $"{kv.Key}=[{string.Join("|", kv.Value.Select(b => b ? "T" : "F"))}]")) + "}";

    private static string CanonMapNestedList(IReadOnlyDictionary<string, List<List<string>>>? m) =>
        m is null ? Nil : "{" + string.Join(",",
            m.OrderBy(kv => kv.Key, StringComparer.Ordinal)
             .Select(kv => $"{kv.Key}=[{string.Join("|", kv.Value.Select(inner => "(" + string.Join(";", inner) + ")"))}]")) + "}";

    private static string CanonMapPool(IReadOnlyDictionary<string, List<string>>? m) =>
        m is null ? Nil : "{" + string.Join(",",
            m.OrderBy(kv => kv.Key, StringComparer.Ordinal)
             .Select(kv => $"{kv.Key}=[{string.Join("|", kv.Value.OrderBy(x => x, StringComparer.Ordinal))}]")) + "}";

    private static string CanonMapSet(IReadOnlyDictionary<string, HashSet<string>>? m) =>
        m is null ? Nil : "{" + string.Join(",",
            m.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={CanonSet(kv.Value)}")) + "}";

    private static string CanonMapPos(IReadOnlyDictionary<string, SourcePosition>? m) =>
        m is null ? Nil : "{" + string.Join(",",
            m.OrderBy(kv => kv.Key, StringComparer.Ordinal)
             .Select(kv => $"{kv.Key}=({kv.Value.Line}:{kv.Value.Column})")) + "}";

    private static string CanonAvail(IReadOnlyDictionary<string, List<AvailabilityAnnotation>>? m) =>
        m is null ? Nil : "{" + string.Join(",",
            m.OrderBy(kv => kv.Key, StringComparer.Ordinal)
             .Select(kv => $"{kv.Key}=[{string.Join("|", kv.Value.Select(a => a.ToString()))}]")) + "}";

    private static string CanonPem(IReadOnlyDictionary<string, List<ProtocolExtensionMethodDecl>>? m) =>
        m is null ? Nil : "{" + string.Join(",",
            m.OrderBy(kv => kv.Key, StringComparer.Ordinal)
             .Select(kv => $"{kv.Key}=[{string.Join("||", kv.Value.Select(RenderPem).OrderBy(x => x, StringComparer.Ordinal))}]")) + "}";

    private static string CanonEmc(IReadOnlyList<ExtensionMemberCandidate>? l) =>
        l is null ? Nil : "[" + string.Join("||", l.Select(RenderEmc).OrderBy(x => x, StringComparer.Ordinal)) + "]";

    private static string RenderPem(ProtocolExtensionMethodDecl d) =>
        $"{d.ProtocolQualifiedName}|{d.MethodName}|{d.PrintedName}|{d.RawSignature}|self={d.ReturnsSelf}" +
        $"|ma={d.IsMainActorIsolated}|static={d.IsStatic}|prop={d.IsProperty}|set={d.HasSetter}" +
        $"|dep={d.IsDeprecated}|mut={d.IsMutating}|where=({string.Join(";", d.WhereConstraints)})";

    private static string RenderEmc(ExtensionMemberCandidate c) =>
        $"{c.ExtendedTypeName}|{c.MethodName}|{c.PrintedName}|{c.RawSignature}|self={c.ReturnsSelf}" +
        $"|ma={c.IsMainActorIsolated}|static={c.IsStatic}|prop={c.IsProperty}|set={c.HasSetter}" +
        $"|dep={c.IsDeprecated}|mut={c.IsMutating}|where=({string.Join(";", c.WhereConstraints)})";

    // ----- corpus / path helpers -----------------------------------------------------

    private static string? LocateLibrariesDir()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 12 && !string.IsNullOrEmpty(dir); i++)
        {
            var candidate = Path.Combine(dir, ".libraries");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }

        return null;
    }

    private static string Rel(string root, string full) => Path.GetRelativePath(root, full);

    private static string Trunc(string s) =>
        s.Length <= 240 ? s : s.Substring(0, 240) + $"…(len {s.Length})";
}
