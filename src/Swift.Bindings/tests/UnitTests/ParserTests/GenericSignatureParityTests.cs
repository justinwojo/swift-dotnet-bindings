// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Text.RegularExpressions;
using BindingsGeneration;
using Newtonsoft.Json;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Finding 19 — generic-signature grammar unification parity gate. Before F19, at least six call
/// sites each hand-parsed the raw <c>genericSig</c> string with their own substring marker scans
/// and bespoke regexes; those grammars drifted apart. F19 routes every site through one grammar
/// (<see cref="GenericSignatureParser.ParseSignature"/> → <see cref="GenericSignatureModel"/>).
///
/// This test pins the equivalence the unification rests on: for each predicate the doc names
/// (per-protocol class-bound / superclass grading, the same-type-on-parent-param dispatch guard,
/// the direct-Self conformance extraction, and the method-level constraint map), the AST-derived
/// reformulation must produce EXACTLY the same answer the pre-F19 regex/substring logic did, across
/// a curated corpus of the tricky signature shapes AND every <c>genericSig</c> harvested live from
/// the available ABI corpora. A divergence is a real behavior change, not a refactor — it fails the
/// test instead of silently shipping.
///
/// The pre-F19 implementations are frozen verbatim below as the reference oracle; the "new" side
/// transcribes the exact predicate expressions the production sites now use against the parsed
/// model. The curated cases keep the gate meaningful without a BindingTests build present; the live
/// harvest (gitignored, appears after <c>nuke binding-tests</c>) widens breadth opportunistically,
/// mirroring <see cref="ReductionCorpusLoudnessTests"/>.
/// </summary>
public class GenericSignatureParityTests
{
    // ── Curated corpus ───────────────────────────────────────────────────────────────────────
    // (signature, declKind) pairs covering the documented tricky shapes. DeclKind "Protocol" routes
    // a sig to the protocol-domain predicates (Site 1 grading, Site 3 extraction); anything else to
    // the method-domain predicates (Site 2 constraint map, Site 6 same-type guard). Every pair is a
    // shape that the OLD and NEW logic genuinely AGREE on — that agreement is exactly what F19 must
    // preserve. Shapes where the new grammar is deliberately MORE correct than the old buggy scan
    // (e.g. a constructed-generic target a naive scan would truncate at its first inner comma) do not
    // occur in these clause positions on the real corpus and are intentionally excluded here.
    private static readonly (string Sig, string DeclKind)[] CuratedCorpus =
    {
        // Protocol class-bound via direct AnyObject (inline, no where).
        ("<τ_0_0 where τ_0_0 : AnyObject>", "Protocol"),
        ("<τ_0_0 : AnyObject>", "Protocol"),
        ("<τ_0_0 : Swift.AnyObject>", "Protocol"),
        // Sugared dialect of the same class-bound constraint, as swift-api-digester -dump-sdk
        // prints it. The frozen oracle answers these WRONG on purpose (see
        // IsSugaredDirectAnyObjectBlindSpot) — they are here so the gate pins the one place the
        // production grading now deliberately outruns it.
        ("<Self : AnyObject>", "Protocol"),
        ("<Self where Self : AnyObject>", "Protocol"),
        ("<Self : Swift.AnyObject>", "Protocol"),
        ("<Self : AnyObject, Self : Swift.Sendable>", "Protocol"),
        // Protocol superclass constraint (target is neither marker nor a conformance) ⇒ class-bound.
        ("<τ_0_0 where τ_0_0 : SwiftBindingsTestLib.BaseRule>", "Protocol"),
        // Associated-type AnyObject must NOT make the protocol class-bound (member clause, not direct).
        ("<τ_0_0 where τ_0_0.Element : AnyObject>", "Protocol"),
        // Marker-only conformances: not class-bound, not a superclass.
        ("<τ_0_0 where τ_0_0 : Swift.Sendable>", "Protocol"),
        ("<τ_0_0 where τ_0_0 : Swift.Copyable, τ_0_0 : Swift.Escapable>", "Protocol"),
        // Direct conformance extraction targets (Site 3): qualified + simple, multiple params.
        ("<τ_0_0 where τ_0_0 : ObjectiveC.NSObjectProtocol>", "Protocol"),
        ("<τ_0_0 : SwiftBindingsTestLib.HasTransform>", "Protocol"),
        ("<τ_0_0, τ_0_1 where τ_0_0 : SwiftBindingsTestLib.A, τ_0_1 : SwiftBindingsTestLib.B>", "Protocol"),
        // Protocol with a direct conformance AND a member conformance (member excluded from direct set).
        ("<τ_0_0 where τ_0_0 : SwiftBindingsTestLib.P, τ_0_0.Element : SwiftBindingsTestLib.Q>", "Protocol"),
        // Bare-parameter signatures (no constraints).
        ("<τ_0_0>", "Protocol"),
        ("<τ_0_0, τ_0_1>", "Func"),
        // Method same-type on a parent param (Site 6 true).
        ("<τ_0_0 where τ_0_0 == Foundation.Data>", "Func"),
        // Method same-type on an associated-type member (Site 6 false — not direct).
        ("<τ_0_0 where τ_0_0.Element == Swift.Int>", "Func"),
        // Method same-type with a constructed-generic target (top-level-comma-aware: target intact).
        ("<τ_0_0 where τ_0_0 == Foundation.Measurement<Foundation.UnitDuration>>", "Func"),
        ("<τ_0_0 where τ_0_0 == Swift.Dictionary<Swift.String, Swift.Int>>", "Func"),
        // Method mixed conformance + same-type across two params.
        ("<τ_0_0, τ_0_1 where τ_0_0 : SwiftBindingsTestLib.Protocol, τ_0_1 == SwiftBindingsTestLib.ConcreteType>", "Func"),
        // Method dependent-member conformance (keyed by root with a member path).
        ("<τ_0_0 where τ_0_0.Element : SwiftBindingsTestLib.Thing>", "Func"),
        // Multi-segment member path on both sides.
        ("<τ_0_0 where τ_0_0.UTF8View.Index == Swift.String.Index>", "Func"),
        // Method-level conformance on a method-own (depth-1) param must not look like a parent same-type.
        ("<τ_0_0, τ_1_0 where τ_1_0 : Swift.Equatable>", "Func"),
        // Empty.
        ("", "Func"),
    };

    // ── Site 1 — protocol class-bound / superclass grading ───────────────────────────────────

    [Fact]
    public void Site1_ProtocolGrading_AstMatchesFrozenReference_AcrossCorpus()
    {
        foreach (var (sig, _) in EnumerateProtocolSigs())
        {
            // No-conformance-list variant of the corpus: the inherited-protocol short-circuit
            // (`inheritedProtocols.Any(p => p.Name == "AnyObject")`) and the conformance-name
            // exclusion are independent of the raw-sig parse this gate covers, so both sides see an
            // empty conformance set and the comparison isolates the sig grammar.
            bool oldClassBound = OldIsClassBoundFromSig(sig, Array.Empty<string>());
            bool newClassBound = NewIsClassBoundFromSig(sig, Array.Empty<string>());
            if (IsSugaredDirectAnyObjectBlindSpot(sig))
            {
                // The single sanctioned divergence: the frozen regex only ever matched the
                // desugared subject root, so it graded a sugared `Self : AnyObject` protocol
                // opaque. That is not a rendering difference — a class-bound existential is two
                // words and an opaque one is five, so the old answer put the witness table in the
                // wrong word for every Apple-direct delegate protocol. Assert the divergence runs
                // in exactly that direction rather than tolerating any disagreement here.
                Assert.True(!oldClassBound && newClassBound,
                    $"sugared class-bound sig <{sig}> must grade class-bound now and did not " +
                    $"under the frozen oracle: old={oldClassBound} new={newClassBound}");
            }
            else
            {
                Assert.True(oldClassBound == newClassBound,
                    $"class-bound grading diverged for sig <{sig}>: old={oldClassBound} new={newClassBound}");
            }

            bool oldSelf = OldHasSelfRequirement(sig);
            bool newSelf = NewHasSelfRequirement(sig);
            Assert.True(oldSelf == newSelf,
                $"hasSelfRequirement diverged for sig <{sig}>: old={oldSelf} new={newSelf}");
        }
    }

    // ── Site 6 — same-type constraint on a parent generic param ──────────────────────────────

    [Fact]
    public void Site6_SameTypeOnParentParam_AstMatchesFrozenReference_AcrossCorpus()
    {
        foreach (var (sig, _) in EnumerateMethodSigs())
        {
            bool oldGuard = OldHasSameTypeConstraintOnParentGenericParam(sig);
            bool newGuard = NewHasSameTypeConstraintOnParentGenericParam(sig);
            Assert.True(oldGuard == newGuard,
                $"same-type-on-parent-param guard diverged for sig <{sig}>: old={oldGuard} new={newGuard}");
        }
    }

    // ── Site 3 — direct-Self conformance extraction (EveryProtocol) ──────────────────────────

    [Fact]
    public void Site3_DirectConformanceExtraction_AstMatchesFrozenReference_AcrossCorpus()
    {
        foreach (var (sig, _) in EnumerateProtocolSigs())
        {
            var oldTargets = OldParseGenericSigConstraints(sig).ToList();
            var newTargets = GenericSignatureParser.ParseSignature(sig)
                .DirectConformanceTargets("τ_0_0", "Self").ToList();
            Assert.True(oldTargets.SequenceEqual(newTargets, StringComparer.Ordinal),
                $"direct-conformance extraction diverged for sig <{sig}>: " +
                $"old=[{string.Join(", ", oldTargets)}] new=[{string.Join(", ", newTargets)}]");
        }
    }

    // ── Site 2 — method-level constraint map ─────────────────────────────────────────────────

    [Fact]
    public void Site2_MethodLevelConstraints_AstMatchesFrozenReference_AcrossCorpus()
    {
        foreach (var (sig, _) in EnumerateMethodSigs())
        {
            var oldMap = OldParseMethodLevelConstraints(sig);
            var newMap = NewParseMethodLevelConstraints(sig);
            Assert.True(MethodConstraintMapsEqual(oldMap, newMap),
                $"method-level constraint map diverged for sig <{sig}>:\n" +
                $"  old={RenderMap(oldMap)}\n  new={RenderMap(newMap)}");
        }
    }

    // ── Corpus enumeration (curated + opportunistic live ABI harvest) ────────────────────────

    private static IEnumerable<(string Sig, string DeclKind)> EnumerateAllSigs()
    {
        foreach (var pair in CuratedCorpus)
            yield return pair;
        foreach (var pair in HarvestLiveAbiSigs())
            yield return pair;
    }

    private static IEnumerable<(string Sig, string DeclKind)> EnumerateProtocolSigs() =>
        EnumerateAllSigs().Where(p => string.Equals(p.DeclKind, "Protocol", StringComparison.Ordinal));

    private static IEnumerable<(string Sig, string DeclKind)> EnumerateMethodSigs() =>
        EnumerateAllSigs().Where(p => !string.Equals(p.DeclKind, "Protocol", StringComparison.Ordinal));

    private static List<(string Sig, string DeclKind)> HarvestLiveAbiSigs()
    {
        var result = new List<(string, string)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in LocateAbiJsons())
        {
            string json;
            try { json = File.ReadAllText(path); }
            catch { continue; }
            ABIRootNode? root;
            try { root = JsonConvert.DeserializeObject<ABIRootNode>(json); }
            catch { continue; }
            if (root?.ABIRoot?.Children is null)
                continue;
            foreach (var child in root.ABIRoot.Children)
                WalkSigs(child, result, seen);
        }
        return result;
    }

    private static void WalkSigs(Node? node, List<(string, string)> sink, HashSet<string> seen)
    {
        if (node is null)
            return;
        if (!string.IsNullOrWhiteSpace(node.GenericSig))
        {
            // De-dup on (sig, declKind) so the same shape across modules isn't re-checked endlessly.
            var key = node.DeclKind + "" + node.GenericSig;
            if (seen.Add(key))
                sink.Add((node.GenericSig!, node.DeclKind ?? string.Empty));
        }
        if (node.Children is not null)
            foreach (var c in node.Children) WalkSigs(c, sink, seen);
        if (node.Conformances is not null)
            foreach (var c in node.Conformances) WalkSigs(c, sink, seen);
        if (node.Accessors is not null)
            foreach (var a in node.Accessors) WalkSigs(a, sink, seen);
    }

    private static List<string> LocateAbiJsons()
    {
        var repoRoot = FindRepoRoot();
        var found = new List<string>();
        if (repoRoot is null)
            return found;

        var staticFixture = Path.Combine(repoRoot,
            "BindingTests", "Fixtures", "StaticSwift", "StaticSwiftLib.xcframework",
            "ios-arm64-simulator", "Modules", "StaticSwiftLib.swiftmodule",
            "arm64-apple-ios15.0-simulator.abi.json");
        if (File.Exists(staticFixture))
            found.Add(staticFixture);

        foreach (var baseDir in new[]
                 {
                     Path.Combine(repoRoot, "BindingTests", ".build"),
                     Path.Combine(repoRoot, "BindingTests", "output", "pack-staging"),
                 })
        {
            if (!Directory.Exists(baseDir))
                continue;
            foreach (var f in Directory.EnumerateFiles(baseDir, "*.abi.json", SearchOption.AllDirectories))
            {
                if (f.Contains("SwiftBindingsTestLib", StringComparison.Ordinal))
                    found.Add(f);
            }
        }
        return found.GroupBy(p => (Path.GetFileName(p), new FileInfo(p).Length))
                    .Select(g => g.First())
                    .ToList();
    }

    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")) ||
                File.Exists(Path.Combine(dir.FullName, "CLAUDE.md")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    // ── Frozen pre-F19 reference oracle (verbatim copies of the replaced logic) ───────────────
    //
    // These are EXACT transcriptions of the substring/regex implementations that lived at each site
    // before F19. They must not be "improved" — their job is to reproduce the old answer so the new
    // grammar can be proven equivalent. Source: git HEAD e78acdcd, pre-F19.

    // SwiftABIParser Site 1 — hasSelfRequirement (old substring scan).
    private static bool OldHasSelfRequirement(string? genericSig) =>
        genericSig != null &&
        (genericSig.Contains("Self.", StringComparison.Ordinal) ||
         genericSig.Contains("Self ==", StringComparison.Ordinal));

    // SwiftABIParser Site 1 — class-bound grading (old AnyObject regex + superclass regex scan).
    private static bool OldIsClassBoundFromSig(string? genericSig, IReadOnlyCollection<string> conformanceSimpleNames)
    {
        bool isClassBound = genericSig != null &&
            Regex.IsMatch(genericSig, @"τ_0_0\s*:[^,]*\bAnyObject\b");
        if (!isClassBound && genericSig != null)
        {
            foreach (Match match in Regex.Matches(genericSig, @"(?:τ_0_0|Self)\s*:\s*([A-Za-z_][\w.]*)"))
            {
                var simpleTarget = match.Groups[1].Value.Split('.')[^1];
                if (simpleTarget is "AnyObject" or "Sendable" or "Escapable"
                    or "Copyable" or "SendableMetatype" or "Any")
                    continue;
                if (conformanceSimpleNames.Contains(simpleTarget))
                    continue;
                isClassBound = true;
                break;
            }
        }
        return isClassBound;
    }

    // GenericDispatchEmitter Site 6 — same-type-on-parent-param (old regex).
    private static bool OldHasSameTypeConstraintOnParentGenericParam(string? sig) =>
        !string.IsNullOrEmpty(sig) && Regex.IsMatch(sig, @"τ_0_\d+\s*==");

    // EveryProtocolEmitter Site 3 — direct-Self conformance extraction (old marker scans).
    private static IEnumerable<string> OldParseGenericSigConstraints(string sig)
    {
        foreach (var c in OldExtractConstraints(sig, "τ_0_0 : "))
            yield return c;
        foreach (var c in OldExtractConstraints(sig, "Self : "))
            yield return c;
    }

    private static IEnumerable<string> OldExtractConstraints(string sig, string marker)
    {
        int idx = 0;
        while (idx < sig.Length)
        {
            var pos = sig.IndexOf(marker, idx, StringComparison.Ordinal);
            if (pos < 0)
                break;
            pos += marker.Length;
            var end = pos;
            while (end < sig.Length && sig[end] != ',' && sig[end] != '>')
                end++;
            var constraint = sig.Substring(pos, end - pos).Trim();
            idx = end;
            if (!string.IsNullOrEmpty(constraint))
                yield return constraint;
        }
    }

    private enum MethodConstraintKind { Conformance, SameType }

    // ConcreteSpecializationEngine Site 2 — method-level constraint map (old where-section split).
    private static Dictionary<string, List<(MethodConstraintKind Kind, string MemberPath, string Target)>>
        OldParseMethodLevelConstraints(string rawGenericSig)
    {
        var result = new Dictionary<string, List<(MethodConstraintKind, string, string)>>(StringComparer.Ordinal);
        var whereIdx = rawGenericSig.IndexOf(" where ", StringComparison.Ordinal);
        if (whereIdx < 0) return result;

        var afterWhere = rawGenericSig.Substring(whereIdx + " where ".Length).Trim();
        if (rawGenericSig.TrimStart().StartsWith("<", StringComparison.Ordinal) &&
            afterWhere.EndsWith(">", StringComparison.Ordinal))
            afterWhere = afterWhere.Substring(0, afterWhere.Length - 1).TrimEnd();
        foreach (var rawClause in SwiftTypeListText.SplitTopLevelCommas(afterWhere))
        {
            var clause = rawClause.Trim();
            MethodConstraintKind kind;
            int opIdx;
            int opLen;
            var eqIdx = clause.IndexOf("==", StringComparison.Ordinal);
            if (eqIdx > 0)
            {
                kind = MethodConstraintKind.SameType;
                opIdx = eqIdx;
                opLen = 2;
            }
            else
            {
                var colonIdx = clause.IndexOf(':');
                if (colonIdx <= 0) continue;
                kind = MethodConstraintKind.Conformance;
                opIdx = colonIdx;
                opLen = 1;
            }

            var lhs = clause.Substring(0, opIdx).Trim();
            var target = clause.Substring(opIdx + opLen).Trim();
            if (lhs.Length == 0 || target.Length == 0) continue;

            string rootParam;
            string memberPath;
            var dotIdx = lhs.IndexOf('.');
            if (dotIdx < 0)
            {
                rootParam = lhs;
                memberPath = string.Empty;
            }
            else
            {
                rootParam = lhs.Substring(0, dotIdx);
                memberPath = lhs.Substring(dotIdx + 1);
            }
            if (rootParam.Length == 0) continue;

            if (!result.TryGetValue(rootParam, out var list))
            {
                list = new List<(MethodConstraintKind, string, string)>();
                result[rootParam] = list;
            }
            var entry = (kind, memberPath, target);
            if (!list.Contains(entry))
                list.Add(entry);
        }
        return result;
    }

    // ── New AST-derived reformulations (transcribed from the production sites) ─────────────────

    private static bool NewHasSelfRequirement(string? sig) =>
        GenericSignatureParser.ParseSignature(sig).Requirements.Any(r =>
            string.Equals(r.SubjectRoot, "Self", StringComparison.Ordinal) &&
            (!r.IsDirect || r.Kind == GenericRequirementKind.SameType));

    /// <summary>
    /// True when the signature carries a DIRECT class-bound constraint written in the sugared
    /// dialect (<c>Self : AnyObject</c>) — the one shape where the frozen pre-F19 oracle, whose
    /// regex only ever matched the desugared subject root, disagrees with production on purpose.
    /// </summary>
    private static bool IsSugaredDirectAnyObjectBlindSpot(string? sig) =>
        GenericSignatureParser.ParseSignature(sig).Requirements.Any(r =>
            r.IsDirect && r.Kind == GenericRequirementKind.Conformance &&
            string.Equals(r.SubjectRoot, "Self", StringComparison.Ordinal) &&
            r.TargetSimpleName == "AnyObject");

    private static bool NewIsClassBoundFromSig(string? sig, IReadOnlyCollection<string> conformanceSimpleNames)
    {
        var parsedSig = GenericSignatureParser.ParseSignature(sig);
        bool isClassBound = parsedSig.Requirements.Any(r =>
            r.IsDirect && r.Kind == GenericRequirementKind.Conformance &&
            (string.Equals(r.SubjectRoot, "τ_0_0", StringComparison.Ordinal) ||
             string.Equals(r.SubjectRoot, "Self", StringComparison.Ordinal)) &&
            r.TargetSimpleName == "AnyObject");
        if (!isClassBound)
        {
            foreach (var r in parsedSig.Requirements)
            {
                if (!r.IsDirect || r.Kind != GenericRequirementKind.Conformance)
                    continue;
                if (!string.Equals(r.SubjectRoot, "τ_0_0", StringComparison.Ordinal) &&
                    !string.Equals(r.SubjectRoot, "Self", StringComparison.Ordinal))
                    continue;
                var simpleTarget = r.TargetSimpleName;
                if (simpleTarget is "AnyObject" or "Sendable" or "Escapable"
                    or "Copyable" or "SendableMetatype" or "Any")
                    continue;
                if (conformanceSimpleNames.Contains(simpleTarget))
                    continue;
                isClassBound = true;
                break;
            }
        }
        return isClassBound;
    }

    private static bool NewHasSameTypeConstraintOnParentGenericParam(string? sig) =>
        GenericSignatureParser.ParseSignature(sig).Requirements.Any(r =>
            r.Kind == GenericRequirementKind.SameType && r.IsDirect &&
            Regex.IsMatch(r.SubjectRoot, @"^τ_0_\d+$"));

    private static Dictionary<string, List<(MethodConstraintKind Kind, string MemberPath, string Target)>>
        NewParseMethodLevelConstraints(string rawGenericSig)
    {
        var result = new Dictionary<string, List<(MethodConstraintKind, string, string)>>(StringComparer.Ordinal);
        foreach (var r in GenericSignatureParser.ParseSignature(rawGenericSig).Requirements)
        {
            var rootParam = r.SubjectRoot;
            if (rootParam.Length == 0) continue;
            var kind = r.Kind == GenericRequirementKind.SameType
                ? MethodConstraintKind.SameType
                : MethodConstraintKind.Conformance;
            if (!result.TryGetValue(rootParam, out var list))
            {
                list = new List<(MethodConstraintKind, string, string)>();
                result[rootParam] = list;
            }
            var entry = (kind, r.MemberPath, r.Target);
            if (!list.Contains(entry))
                list.Add(entry);
        }
        return result;
    }

    // ── Comparison helpers ────────────────────────────────────────────────────────────────────

    private static bool MethodConstraintMapsEqual(
        Dictionary<string, List<(MethodConstraintKind Kind, string MemberPath, string Target)>> a,
        Dictionary<string, List<(MethodConstraintKind Kind, string MemberPath, string Target)>> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var (key, listA) in a)
        {
            if (!b.TryGetValue(key, out var listB)) return false;
            // Both sides build the list in clause order with the same dedup; the old where-section
            // split and the new top-level-comma split visit clauses in the same order, so an
            // order-sensitive compare is the strictest faithful check.
            if (!listA.SequenceEqual(listB)) return false;
        }
        return true;
    }

    private static string RenderMap(
        Dictionary<string, List<(MethodConstraintKind Kind, string MemberPath, string Target)>> map) =>
        "{" + string.Join("; ", map.Select(kv =>
            $"{kv.Key}=[{string.Join(", ", kv.Value.Select(e => $"({e.Kind},{e.MemberPath},{e.Target})"))}]")) + "}";
}
