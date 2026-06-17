// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Text.RegularExpressions;
using BindingsGeneration.Demangling;
using Newtonsoft.Json;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Finding 18 — corpus loudness gate. Demangles every Swift method/function/type mangled name across
/// the available ABI corpora and asserts the set of "No rule for node {Kind}" reduction misses stays
/// within a documented baseline. A miss means a node kind reached the reducer with no rule; for a
/// function-signature node that disables demangle-based async/convention/variadic detection for the
/// method, which previously degraded silently to a mangled-name substring heuristic.
///
/// The reducer is deliberately partial — it reduces only the shapes the parser consumes (functions
/// for async/variadic, the four TBD reduction categories). Whole-symbol kinds like
/// <c>Constructor</c>/accessors never reduce to a function; their async/variadic detection comes from
/// raw-tree marker walks (<see cref="Swift5Demangler.HasAsyncMarker"/>,
/// <see cref="Swift5Demangler.HasVariadicParameterMarker"/>), not reduction. The
/// <see cref="ReductionDiagnostics.IntentionallyUnreducedKinds"/> allowlist (shared with the
/// SWIFTBIND058 warning) enumerates exactly which kinds are expected to have no rule, with the reason.
/// This is the "gated channel": a NEW unruled kind (outside the allowlist) fails the test instead of
/// silently weakening detection — the precondition for retiring the substring heuristics in Finding
/// 17. Finding 17 tightens this gate by adding the <c>CFunctionPointer</c> rule and removing it from
/// the allowlist.
///
/// Result inspection (not the <see cref="ReductionDiagnostics"/> global accumulator) is used so the
/// assertion is race-free under xUnit's parallel class execution: a miss on any node reached during a
/// symbol's reduction propagates to the top-level <see cref="ReductionError"/> message.
/// </summary>
public class ReductionCorpusLoudnessTests
{
    private static readonly Regex NoRuleMessage =
        new(@"No rule for node (?<kind>\w+)", RegexOptions.Compiled);

    // Swift mangling prefixes — restricts the corpus to real Swift symbols so our own C wrapper
    // exports (e.g. SBSW_*, which demangle to a bare `Suffix` node) are not counted as misses.
    private static readonly string[] SwiftManglingPrefixes = { "$s", "_$s", "$S", "_$S", "_T0", "_Tt" };

    // The documented allowlist lives in production code (ReductionDiagnostics) so the SWIFTBIND058
    // warning and this gate share one source of truth. Compared by enum-name string because the
    // "No rule for node {Kind}" message carries the NodeKind name.
    private static readonly HashSet<string> IntentionallyUnreducedKindNames =
        ReductionDiagnostics.IntentionallyUnreducedKinds.Keys
            .Select(k => k.ToString())
            .ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void AbiCorpus_ReductionMisses_StayWithinDocumentedBaseline()
    {
        var paths = LocateAbiJsons();
        // The checked-in StaticSwift fixture is always present, so this never silently no-ops; the
        // BindingTests build outputs (gitignored) add full breadth after `nuke binding-tests`.
        Assert.True(paths.Count > 0, "No ABI JSON corpus found — expected at least the checked-in StaticSwift fixture.");

        var mangledNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in paths)
            CollectMangledNames(path, mangledNames);
        Assert.True(mangledNames.Count > 0, $"Located {paths.Count} ABI JSON(s) but extracted 0 mangled names.");

        var misses = CollectMisses(mangledNames);
        var unexpected = misses.Keys.Where(k => !IntentionallyUnreducedKindNames.Contains(k))
                                     .OrderBy(k => k, StringComparer.Ordinal)
                                     .ToList();

        Assert.True(unexpected.Count == 0,
            $"Demangle reduction left UNDOCUMENTED node kind(s) unruled across the ABI corpus " +
            $"({mangledNames.Count} Swift symbols, {paths.Count} file(s)). Each silently disables " +
            $"demangle-based detection — add a reducer rule, or (if intentional) an entry to " +
            $"IntentionallyUnreducedKinds with the reason: " +
            string.Join("; ", unexpected.Select(k => $"{k} (e.g. {misses[k]})")));
    }

    // --- helpers -----------------------------------------------------------------------------

    private static bool IsSwiftSymbol(string mangled) =>
        SwiftManglingPrefixes.Any(p => mangled.StartsWith(p, StringComparison.Ordinal));

    private static Dictionary<string, string> CollectMisses(IEnumerable<string> mangledNames)
    {
        var misses = new Dictionary<string, string>(StringComparer.Ordinal); // kind -> first example
        var demangler = new Swift5Demangler();
        foreach (var mangled in mangledNames)
        {
            if (string.IsNullOrEmpty(mangled) || !IsSwiftSymbol(mangled))
                continue;
            IReduction reduction;
            try
            {
                reduction = demangler.Run(mangled);
            }
            catch
            {
                // A throw is a different failure mode; this gate is about the silent "No rule" degrade.
                continue;
            }
            if (reduction is ReductionError error)
            {
                var match = NoRuleMessage.Match(error.Message);
                if (match.Success)
                {
                    var kind = match.Groups["kind"].Value;
                    if (!misses.ContainsKey(kind))
                        misses[kind] = mangled;
                }
            }
        }
        return misses;
    }

    private static List<string> LocateAbiJsons()
    {
        var repoRoot = FindRepoRoot();
        var found = new List<string>();
        if (repoRoot is null)
            return found;

        // 1) Always-present checked-in fixture.
        var staticFixture = Path.Combine(repoRoot,
            "BindingTests", "Fixtures", "StaticSwift", "StaticSwiftLib.xcframework",
            "ios-arm64-simulator", "Modules", "StaticSwiftLib.swiftmodule",
            "arm64-apple-ios15.0-simulator.abi.json");
        if (File.Exists(staticFixture))
            found.Add(staticFixture);

        // 2) BindingTests build outputs (gitignored; present after `nuke binding-tests`). Glob both
        //    the SwiftBindingsTestLib and its dependency across the staging dirs they land in.
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
        // De-dup identical content paths cheaply by file name + length.
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

    private static void CollectMangledNames(string abiJsonPath, HashSet<string> sink)
    {
        string json;
        try
        {
            json = File.ReadAllText(abiJsonPath);
        }
        catch
        {
            return;
        }
        var root = JsonConvert.DeserializeObject<BindingsGeneration.ABIRootNode>(json);
        if (root?.ABIRoot?.Children is null)
            return;
        foreach (var child in root.ABIRoot.Children)
            WalkNode(child, sink);
    }

    private static void WalkNode(BindingsGeneration.Node? node, HashSet<string> sink)
    {
        if (node is null)
            return;
        if (!string.IsNullOrEmpty(node.MangledName))
            sink.Add(node.MangledName);
        if (node.Children is not null)
            foreach (var c in node.Children)
                WalkNode(c, sink);
        if (node.Accessors is not null)
            foreach (var a in node.Accessors)
                WalkNode(a, sink);
        if (node.Conformances is not null)
            foreach (var c in node.Conformances)
                WalkNode(c, sink);
    }
}
