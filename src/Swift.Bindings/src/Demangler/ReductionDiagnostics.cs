// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration.Demangling;

/// <summary>
/// Process-wide accumulator for <see cref="Swift5Reducer"/> rule-miss telemetry (Finding 18).
///
/// Before this existed, <see cref="RuleRunner.RunRules"/> returned a
/// <c>ReductionError("No rule for node {Kind}")</c> on any unruled node and the parser silently
/// swallowed it (downgrading to mangled-name substring heuristics such as the former
/// <c>DetectAsyncFromMangledName</c> "Ya"-scan and the <c>"XC"</c> convention probe). That made
/// demangle-based async / convention / variadic detection degrade invisibly: a single new node kind
/// the reducer didn't handle disabled detection for every symbol carrying it, with no diagnostic.
///
/// This counter makes every miss observable. The generator emits a one-line <c>SWIFTBIND058</c>
/// summary listing the distinct missed kinds, and the corpus-loudness unit test fails closed on any
/// reachable miss across the BindingTests + validation ABI corpora. That gated channel was the
/// precondition for retiring the substring fallbacks in Finding 17 (async now reads the raw-tree
/// <c>AsyncAnnotation</c> marker, convention the reduced <c>CFunctionPointer</c> rule) — a deletion
/// can no longer silently regress detection, because a reintroduced "No rule" hole turns the corpus
/// test red.
///
/// The accumulator is process-global (not session-scoped like <see cref="ReportCollector"/>) because
/// demangling runs during dependency/ABI parsing, which happens BEFORE
/// <see cref="ReportCollector.Start"/>; there is no report session active at miss time. Callers that
/// want a per-run figure (the generator, each unit test) call <see cref="Reset"/> first.
/// </summary>
internal static class ReductionDiagnostics
{
    private static readonly object Sync = new();
    private static long _attempts;
    private static long _misses;
    private static readonly Dictionary<NodeKind, int> _missesByKind = new();
    private static readonly Dictionary<NodeKind, string> _exampleSymbolByKind = new();

    /// <summary>
    /// Node kinds the reducer intentionally does not reduce, each with the reason it is safe. These
    /// are NOT bugs: detection that would otherwise need them is sourced elsewhere — async/variadic
    /// from the raw-tree marker walks (<see cref="Swift5Demangler.HasAsyncMarker"/>,
    /// <see cref="Swift5Demangler.HasVariadicParameterMarker"/>), which do not require the reduction to
    /// succeed; emitted-type shapes from the ABI-JSON signature directly.
    ///
    /// This set is the single source of truth for "expected non-reduction." It gates both the
    /// SWIFTBIND058 warning (<see cref="Snapshot.HasUnexpectedMisses"/> — so the warning fires only on
    /// a genuinely new hole, never on the Constructor/accessor misses every real library produces) and
    /// the corpus-loudness unit test. A miss whose kind is NOT here is an undocumented silent-degrade
    /// hole; the fix is to add a reducer rule, or (if the non-reduction is genuinely safe) an entry
    /// here with the reason. Finding 17 tightened this gate by adding a <c>CFunctionPointer</c> reducer
    /// rule and removing its former entry from this set.
    /// </summary>
    public static readonly IReadOnlyDictionary<NodeKind, string> IntentionallyUnreducedKinds =
        new Dictionary<NodeKind, string>
        {
            // Whole-symbol kinds that never reduce to a FunctionReduction. Async/variadic for these is
            // detected by the raw-tree marker walks over the built node tree, not by reduction.
            [NodeKind.Constructor] = "init symbol — async via HasAsyncMarker, variadic via HasVariadicParameterMarker",
            [NodeKind.Getter] = "property/subscript getter accessor symbol — not reduced to a function",
            [NodeKind.Setter] = "property/subscript setter accessor symbol — not reduced to a function",
            [NodeKind.ModifyAccessor] = "_modify coroutine accessor symbol — not reduced to a function",
            [NodeKind.Variable] = "stored-property storage symbol — not reduced to a function",
            [NodeKind.Subscript] = "subscript symbol — not reduced to a function",
            // Function-signature wrapper/leaf kinds that block a FunctionType reduction. The method's
            // async/variadic detection falls back to the raw-tree marker walks, which do not depend on
            // the reduction succeeding, so these are safe non-reductions.
            [NodeKind.InOut] = "inout/borrowing parameter wrapper inside a function signature",
            [NodeKind.Owned] = "__owned/consuming parameter wrapper inside a function signature",
            [NodeKind.Metatype] = ".Type metatype inside a function signature",
            [NodeKind.DynamicSelf] = "Self return type inside a function signature",
            [NodeKind.AssociatedTypeRef] = "P.Assoc associated-type reference inside a function signature",
            [NodeKind.TypeAlias] = "sugar typealias node inside a function signature",
            [NodeKind.Identifier] = "opaque-result-type (some P) leaf inside a function signature",
            [NodeKind.OpaqueReturnType] = "Qr opaque return type (-> some P) — boxed to any P; the method's async/throws falls back to the raw-tree marker walks",
            [NodeKind.Function] = "protocol-extension/opaque function shapes whose Function child-rules don't match",
            // NOTE: CFunctionPointer (@convention(c)) is NOT here — Finding 17 gave it a reducer rule
            // (it reduces through ConvertFunctionType and sets ClosureTypeSpec.IsConventionC), so a
            // CFunctionPointer miss is now a genuine hole and trips SWIFTBIND058 / the corpus test.
        };

    /// <summary>Records one reduction attempt (one <see cref="RuleRunner.RunRules"/> call).</summary>
    public static void RecordAttempt()
    {
        lock (Sync)
        {
            _attempts++;
        }
    }

    /// <summary>
    /// Records a reduction that matched no rule. <paramref name="kind"/> is the unruled node kind;
    /// <paramref name="symbol"/> is the originating mangled name, retained as the first example per
    /// kind so the diagnostic can point at a concrete reproducer.
    /// </summary>
    public static void RecordNoRuleMiss(NodeKind kind, string symbol)
    {
        lock (Sync)
        {
            _misses++;
            _missesByKind[kind] = _missesByKind.GetValueOrDefault(kind) + 1;
            if (!_exampleSymbolByKind.ContainsKey(kind) && !string.IsNullOrEmpty(symbol))
                _exampleSymbolByKind[kind] = symbol;
        }
    }

    /// <summary>Clears all counters. Call at the start of a generation run or a hermetic test.</summary>
    public static void Reset()
    {
        lock (Sync)
        {
            _attempts = 0;
            _misses = 0;
            _missesByKind.Clear();
            _exampleSymbolByKind.Clear();
        }
    }

    /// <summary>An immutable point-in-time view of the accumulator.</summary>
    public sealed record Snapshot(
        long Attempts,
        long Misses,
        IReadOnlyDictionary<NodeKind, int> MissesByKind,
        IReadOnlyDictionary<NodeKind, string> ExampleSymbolByKind)
    {
        /// <summary>True when at least one reduction matched no rule (including expected ones).</summary>
        public bool HasMisses => Misses > 0;

        /// <summary>
        /// The missed kinds NOT on the <see cref="IntentionallyUnreducedKinds"/> allowlist — i.e. the
        /// undocumented silent-degrade holes. This is what the SWIFTBIND058 warning keys off so it
        /// never fires on the benign Constructor/accessor misses every real library produces.
        /// </summary>
        public IReadOnlyDictionary<NodeKind, int> UnexpectedMissesByKind =>
            MissesByKind.Where(kv => !IntentionallyUnreducedKinds.ContainsKey(kv.Key))
                        .ToDictionary(kv => kv.Key, kv => kv.Value);

        /// <summary>True when at least one miss is for a kind outside the documented allowlist.</summary>
        public bool HasUnexpectedMisses => UnexpectedMissesByKind.Count > 0;

        /// <summary>
        /// A deterministic, human-readable one-liner of ALL missed kinds and an example symbol each,
        /// ordered by kind name. Empty string when there are no misses.
        /// </summary>
        public string Describe() => DescribeKinds(MissesByKind);

        /// <summary>As <see cref="Describe"/>, but only the undocumented (unexpected) missed kinds.</summary>
        public string DescribeUnexpected() => DescribeKinds(UnexpectedMissesByKind);

        private string DescribeKinds(IReadOnlyDictionary<NodeKind, int> kinds)
        {
            if (kinds.Count == 0)
                return string.Empty;
            return string.Join("; ", kinds
                .OrderBy(kv => kv.Key.ToString(), StringComparer.Ordinal)
                .Select(kv => ExampleSymbolByKind.TryGetValue(kv.Key, out var example)
                    ? $"{kv.Key} x{kv.Value} (e.g. {example})"
                    : $"{kv.Key} x{kv.Value}"));
        }
    }

    /// <summary>Captures an immutable snapshot of the current counters.</summary>
    public static Snapshot Capture()
    {
        lock (Sync)
        {
            return new Snapshot(
                _attempts,
                _misses,
                new Dictionary<NodeKind, int>(_missesByKind),
                new Dictionary<NodeKind, string>(_exampleSymbolByKind));
        }
    }
}
