// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Immutable;
using System.Diagnostics;

namespace BindingsGeneration;

/// <summary>
/// Whether lowering receipts make an artifact's plans executable, and each plan's verdict. A plan is
/// executable only when every receipt the judgement rests on shows the plan's exact target with the
/// predicted <c>swiftcc</c> signature on every slice the artifact claims. A receipt in the record that
/// contradicts the prediction for the same symbol, whichever artifact it answers and on any slice,
/// claimed or not, under any compiler, refuses the plan whatever else agrees: a known mismatch is
/// never overridden.
/// </summary>
/// <remarks>
/// A judgement is made one of two ways. <see cref="SwiftLoweringQualification"/> rests on the SDK's
/// committed record, which must hold a current receipt under every qualified compiler family: it is
/// how the SDK's own artifacts and its qualification corpus are judged, the same on every toolchain.
/// <see cref="SwiftLoweringAuthorization"/> rests on one artifact's own receipt, which must have been
/// collected by the compiler and SDKs that build it: it is how a consumer's binding is judged. When
/// the receipts do not hold up (<see cref="Refusal"/>), no plan is executable, and each plan's
/// evidence says why.
/// </remarks>
public sealed class SwiftLoweringJudgement
{
    public const string QualificationBasis = "qualification";

    public const string AuthorizationBasis = "authorization";

    private readonly ImmutableArray<SwiftLoweringReceipt> _authorizing;
    private readonly ImmutableArray<SwiftLoweringReceipt> _known;

    /// <param name="scope">The artifact and the slices it claims.</param>
    /// <param name="basis"><see cref="QualificationBasis"/> or <see cref="AuthorizationBasis"/>.</param>
    /// <param name="refusal">Why the receipts do not authorize anything; null when they do.</param>
    /// <param name="authorizing">The receipts a match must hold under: every one, on every claimed slice.</param>
    /// <param name="known">Every receipt in the record, of any artifact, each of which can contradict a plan.</param>
    internal SwiftLoweringJudgement(
        SwiftArtifactScope scope, string basis, string? refusal,
        IEnumerable<SwiftLoweringReceipt> authorizing, IEnumerable<SwiftLoweringReceipt> known)
    {
        Scope = scope;
        Basis = basis;
        _authorizing = authorizing.ToImmutableArray();
        _known = known.ToImmutableArray();
        Refusal = refusal ?? (_authorizing.IsEmpty ? $"no receipt of {scope.Artifact} authorizes anything" : null);
    }

    public SwiftArtifactScope Scope { get; }

    public string Basis { get; }

    /// <summary>Why no plan of the artifact is executable on these receipts, or null when they authorize plans.</summary>
    public string? Refusal { get; }

    public bool Authorizes => Refusal is null;

    /// <summary>The scope and basis to record on the judged plan sidecar.</summary>
    public SwiftPlanEvidenceScope Record => new(Scope.Artifact, Scope.Slices, Basis);

    /// <summary>A judgement that authorizes nothing, for <paramref name="refusal"/>.</summary>
    public static SwiftLoweringJudgement Refused(SwiftArtifactScope scope, string basis, string refusal) =>
        new(scope, basis, refusal, [], []);

    /// <summary>
    /// Whether a forward plan of <paramref name="symbol"/> predicting <paramref name="callee"/> is
    /// executable, and why. Contradictions are reported first, then the receipts' refusal, then any
    /// claimed slice whose receipt does not show the symbol.
    /// </summary>
    public (bool Executable, string Evidence) Judge(string symbol, string callee)
    {
        var expected = "swiftcc " + callee;
        var contradictions = new List<string>();
        foreach (var receipt in _known)
        {
            foreach (var (triple, slice) in receipt.Slices)
            {
                if (slice.Symbols.TryGetValue(symbol, out var actual) && actual != expected)
                {
                    var source = receipt.Artifact == Scope.Artifact ? "" : $" (the receipt of {receipt.Artifact})";
                    contradictions.Add($"swiftc gives {actual} on {triple} under {receipt.Compiler}{source}; the plan predicts {expected}");
                }
            }
        }
        if (contradictions.Count > 0)
            return (false, string.Join("; ", contradictions));
        if (Refusal is not null)
            return (false, Refusal);

        var gaps = new List<string>();
        foreach (var receipt in _authorizing)
        {
            foreach (var triple in Scope.Slices)
            {
                if (!receipt.Slices.TryGetValue(triple, out var slice))
                    gaps.Add($"no evidence for {triple} under {receipt.Compiler}");
                else if (!slice.Symbols.ContainsKey(symbol))
                    gaps.Add(slice.Unobserved?.TryGetValue(symbol, out var why) == true
                        ? $"swiftc shows no {symbol} on {triple} under {receipt.Compiler}: {why}"
                        : $"swiftc shows no {symbol} on {triple} under {receipt.Compiler}");
            }
        }
        return gaps.Count == 0
            ? (true, $"swiftc gives {expected} on {Scope.Slices.Length} triple(s) under {_authorizing.Length} toolchain(s)")
            : (false, string.Join("; ", gaps));
    }

    /// <summary>
    /// <paramref name="coverage"/> with each planned entry's executable flag and evidence set. A
    /// forward entry is judged by its target and predicted callee; a thick or reverse one is not
    /// executable; any unread argument without a reason fails closed.
    /// </summary>
    public ImmutableArray<SwiftPlanCoverageEntry> Join(ImmutableArray<SwiftPlanCoverageEntry> coverage, ImmutableArray<SwiftCallPlan> plans)
    {
        var bySymbol = plans.ToDictionary(p => p.Symbol, StringComparer.Ordinal);
        return coverage.Select(entry =>
        {
            if (entry.Status != SwiftPlanStatus.Planned)
                return entry with { Executable = false, Evidence = null };
            if (entry.Symbol is not { } symbol || !bySymbol.TryGetValue(symbol, out var plan))
                return entry with { Executable = false, Evidence = $"its plan {entry.Symbol} is not in the set" };
            if (plan.Body.Kind != SwiftPlanKind.Forward || plan.Body.Target is null)
                return entry with { Executable = false, Evidence = SwiftLoweringEvidence.ClosureReason };
            if (plan.Body.Managed.Arguments.Any(a => a.Unread && a.UnreadReason is null))
                return entry with { Executable = false, Evidence = "an argument the callee does not read has no reason it is benign" };
            if (entry.Callee is not { } callee)
                return entry with { Executable = false, Evidence = "the entry records no predicted callee signature" };
            var (executable, evidence) = Judge(plan.Body.Target, callee);
            return entry with { Executable = executable, Evidence = evidence };
        }).ToImmutableArray();
    }
}

/// <summary>
/// SDK qualification: the planner, the evidence reader and the bridges are qualified against a fixed
/// set of compiler families, and the SDK's committed record holds one current receipt per family for
/// each artifact it qualifies (the SDK's own assemblies and the qualification corpus). An artifact
/// qualifies only when every family's receipt covers every slice it claims under the current
/// contract, and the record holds no receipt of a family outside the set.
/// </summary>
public static class SwiftLoweringQualification
{
    /// <summary>The compiler families the SDK is qualified under (see <see cref="SwiftCompilerIdentity.Family"/>).</summary>
    public static ImmutableArray<string> Families { get; } = ["Apple Swift 6.2", "Apple Swift 6.4"];

    public static bool IsQualified(string compiler) =>
        SwiftCompilerIdentity.Family(compiler) is { } family && Families.Contains(family, StringComparer.Ordinal);

    public static SwiftLoweringJudgement Qualify(SwiftLoweringEvidence record, SwiftArtifactScope scope)
    {
        var receipts = record.ReceiptsOf(scope.Artifact).ToImmutableArray();
        var refusals = new List<string>();
        foreach (var family in Families)
        {
            if (!receipts.Any(r => SwiftCompilerIdentity.Family(r.Compiler) == family))
                refusals.Add($"the qualification record has no receipt of {scope.Artifact} under {family}");
        }
        foreach (var receipt in receipts)
        {
            if (!IsQualified(receipt.Compiler))
                refusals.Add($"the qualification record holds a receipt of {scope.Artifact} under {receipt.Compiler}, which is not a qualified compiler family ({string.Join(", ", Families)})");
            if (receipt.Contract != SwiftLoweringContract.Current)
                refusals.Add($"the receipt of {scope.Artifact} under {receipt.Compiler} was collected under {receipt.Contract}; this generator judges under {SwiftLoweringContract.Current}");
            foreach (var triple in scope.Slices.Where(t => !receipt.Slices.ContainsKey(t)))
                refusals.Add($"the receipt of {scope.Artifact} under {receipt.Compiler} has no evidence for {triple}");
        }
        return new SwiftLoweringJudgement(
            scope, SwiftLoweringJudgement.QualificationBasis,
            refusals.Count == 0 ? null : string.Join("; ", refusals),
            receipts, record.Receipts);
    }
}

/// <summary>
/// What a build expects of the receipt that authorizes its artifact: the compiler and SDKs it builds
/// with, the contract it plans under, and, once its native inputs are final, the sources the
/// evidence must have been read from.
/// </summary>
/// <param name="Scope">The artifact and the slices it claims.</param>
/// <param name="Compiler">The build's <c>swiftc --version</c> line.</param>
/// <param name="Contract">The contract the build plans under.</param>
/// <param name="Sdks">The SDK identity of each claimed slice.</param>
/// <param name="Sources">
/// Each claimed slice's evidence sources, or null when the build cannot know them yet (generation
/// precedes the wrapper's final source): the receipt's sources are then not compared, and the build
/// must compare them before it trusts its routing.
/// </param>
public sealed record SwiftEvidenceExpectation(
    SwiftArtifactScope Scope,
    string Compiler,
    SwiftLoweringContract Contract,
    ImmutableSortedDictionary<string, string> Sdks,
    ImmutableSortedDictionary<string, ImmutableArray<SwiftEvidenceSource>>? Sources = null);

/// <summary>
/// Authorization of one artifact by its own receipt: collected by the compiler that builds the
/// artifact (a qualified family), under the current contract, against the build's SDK on every
/// claimed slice, and, when the expectation names them, from exactly the build's sources. Anything
/// else (another compiler, a changed SDK or input, a missing slice, a stale contract) leaves the
/// receipt non-authorizing.
/// </summary>
public static class SwiftLoweringAuthorization
{
    public static SwiftLoweringJudgement Authorize(SwiftEvidenceExpectation expected, SwiftLoweringEvidence evidence)
    {
        var scope = expected.Scope;
        var known = evidence.ReceiptsOf(scope.Artifact).ToImmutableArray();
        var receipt = known.FirstOrDefault(r => r.Compiler == expected.Compiler);
        var refusals = new List<string>();
        if (!SwiftLoweringQualification.IsQualified(expected.Compiler))
            refusals.Add($"{expected.Compiler} is not a qualified compiler family ({string.Join(", ", SwiftLoweringQualification.Families)})");
        if (receipt is null)
        {
            refusals.Add(known.IsEmpty
                ? $"no receipt of {scope.Artifact}"
                : $"the receipt of {scope.Artifact} was collected by {string.Join(", ", known.Select(r => r.Compiler))}; this build compiles with {expected.Compiler}");
        }
        else
        {
            var contract = receipt.Contract;
            if (contract.PlanVersion != expected.Contract.PlanVersion)
                refusals.Add($"the receipt of {scope.Artifact} was collected under plan version {contract.PlanVersion}; this build plans under {expected.Contract.PlanVersion}");
            if (contract.FactsVersion != expected.Contract.FactsVersion)
                refusals.Add($"the receipt of {scope.Artifact} was collected under facts version {contract.FactsVersion}; this build records facts version {expected.Contract.FactsVersion}");
            if (contract.CollectorVersion != expected.Contract.CollectorVersion)
                refusals.Add($"the receipt of {scope.Artifact} was collected under collector version {contract.CollectorVersion}; this build reads evidence as collector version {expected.Contract.CollectorVersion}");
            foreach (var triple in scope.Slices)
            {
                if (!receipt.Slices.TryGetValue(triple, out var slice))
                {
                    refusals.Add($"the receipt of {scope.Artifact} has no evidence for {triple}");
                    continue;
                }
                var sdk = expected.Sdks.GetValueOrDefault(triple);
                if (sdk != slice.Sdk)
                    refusals.Add($"the evidence of {scope.Artifact} for {triple} was collected against {slice.Sdk}; this build uses {sdk ?? "no known SDK"}");
                if (expected.Sources is { } sources)
                    refusals.AddRange(SourceDrift(scope.Artifact, triple, sources.GetValueOrDefault(triple, []), slice.Sources));
            }
        }
        return new SwiftLoweringJudgement(
            scope, SwiftLoweringJudgement.AuthorizationBasis,
            refusals.Count == 0 ? null : string.Join("; ", refusals),
            receipt is null ? [] : [receipt], evidence.Receipts);
    }

    private static IEnumerable<string> SourceDrift(
        string artifact, string triple, ImmutableArray<SwiftEvidenceSource> expected, ImmutableArray<SwiftEvidenceSource> recorded)
    {
        var byKey = recorded.ToDictionary(s => (s.Role, s.Name));
        foreach (var source in expected)
        {
            if (!byKey.Remove((source.Role, source.Name), out var actual))
                yield return $"the evidence of {artifact} for {triple} was not read from the {source.Role} {source.Name}";
            else if (actual.Digest != source.Digest)
                yield return $"the {source.Role} {source.Name} changed since the evidence of {artifact} for {triple} was collected ({actual.Digest}, now {source.Digest})";
            else if (!actual.Flags.SequenceEqual(source.Flags, StringComparer.Ordinal))
                yield return $"the {source.Role} {source.Name} was compiled with [{string.Join(' ', actual.Flags)}] for the evidence of {artifact} on {triple}; this build compiles it with [{string.Join(' ', source.Flags)}]";
        }
        foreach (var extra in byKey.Values)
            yield return $"the evidence of {artifact} for {triple} was read from the {extra.Role} {extra.Name}, which this build does not compile";
    }
}

/// <summary>Identifies the selected Swift compiler and SDKs through <c>xcrun</c>, which honours <c>DEVELOPER_DIR</c>.</summary>
public static class SwiftToolchainProbe
{
    /// <summary>The selected compiler's <c>swiftc --version</c> line, or null when there is none.</summary>
    public static string? Compiler()
    {
        var (code, output) = Xcrun("swiftc", "--version");
        return code == 0 ? output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Contains("Swift version", StringComparison.Ordinal)) : null;
    }

    /// <summary>The identity of the SDK <paramref name="sdk"/> (<c>iphoneos</c>, ...): its name, version and build, e.g. <c>iphoneos 26.2 (23C53)</c>.</summary>
    public static string? SdkIdentity(string sdk)
    {
        var (versionCode, version) = Xcrun("--sdk", sdk, "--show-sdk-version");
        var (buildCode, build) = Xcrun("--sdk", sdk, "--show-sdk-build-version");
        return versionCode == 0 && buildCode == 0 && version.Trim().Length > 0 && build.Trim().Length > 0
            ? $"{sdk} {version.Trim()} ({build.Trim()})"
            : null;
    }

    /// <summary>
    /// What a build of <paramref name="scope"/> with the selected toolchain expects of its receipt,
    /// before its sources are final; null, with <paramref name="why"/>, when the toolchain cannot be
    /// identified.
    /// </summary>
    public static SwiftEvidenceExpectation? Expect(SwiftArtifactScope scope, out string? why)
    {
        why = null;
        if (Compiler() is not { } compiler)
        {
            why = "the selected Swift compiler could not be identified (xcrun swiftc --version)";
            return null;
        }
        var sdks = ImmutableSortedDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var triple in scope.Slices)
        {
            var name = SwiftArtifactScope.SdkOf(triple);
            if (SdkIdentity(name) is not { } identity)
            {
                why = $"the {name} SDK could not be identified (xcrun --sdk {name} --show-sdk-version)";
                return null;
            }
            sdks[triple] = identity;
        }
        return new SwiftEvidenceExpectation(scope, compiler, SwiftLoweringContract.Current, sdks.ToImmutable());
    }

    private static (int Code, string Output) Xcrun(params string[] arguments)
    {
        if (!OperatingSystem.IsMacOS() || !File.Exists("/usr/bin/xcrun"))
            return (-1, string.Empty);
        var info = new ProcessStartInfo("/usr/bin/xcrun") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);
        using var process = Process.Start(info);
        if (process is null)
            return (-1, string.Empty);
        var output = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return (process.ExitCode, output.Result);
    }
}
