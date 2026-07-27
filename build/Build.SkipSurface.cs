// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Build.SkipSurface.cs — Layer B "skip-surface" trend gate.
//
// Parses mechanically-detectable skip markers from generated `.cs` output and
// diffs against `build/baselines/skip-surface-baseline.json`. Wired as a
// post-step in the `binding-tests` --compile-only path (gated by
// --skip-surface) so the gate runs against fresh generator output. The gate
// ratchets the skip-class count downward over time (skip-surface trend gate
// on authored corpus).
//
// The corpus the gate scans is the BindingTests-generated output under
// `BindingTests/output/`, which is what `RunRegenerateBindings()` produces. That
// is the whole scanned surface — the gate claims no more breadth than the
// authored BindingTests corpus actually has. (An earlier design also scanned a
// second, separately-authored snippet directory; it never received content, so
// it only made the gate's advertised reach look wider than it was.)
//
// Scanning the generated `.cs` directly (rather than a sidecar metrics file)
// is deliberate: the markers we ratchet are exactly the strings a consumer
// reading the binding output would see. If the generator emits `// Skipped: …`
// in the file but the metrics sidecar disagrees, the sidecar is wrong; we
// trust the file.
//
// A disappearing skip marker is NOT automatically good news. It means either
// "the member is bound now" (a real fix) or "the member is gone entirely" — a
// withdrawn type takes both its API and its skip markers with it, and a
// count-based ratchet reads that amputation as an improvement. So every
// GONE/DOWN row is cross-referenced against the API manifest: if the declaring
// type no longer contributes any symbol-bearing member, the row is reclassified
// as a regression instead of being logged with a checkmark. Same blind spot as
// the manifest itself — properties and subscripts are not recorded, so the
// cross-reference only speaks for types that had at least one method or ctor.
//
// Introducing a new authored skip key: run
// `nuke binding-tests --compile-only --skip-surface`, let it fail, and copy the
// reported diff into `build/baselines/skip-surface-baseline.json` in the same
// commit as the change that introduced it.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Nuke.Common;
using Nuke.Common.IO;
using Serilog;

partial class Build
{
    [Parameter("Run the Layer B skip-surface trend gate against build/baselines/skip-surface-baseline.json")]
    readonly bool SkipSurface;

    AbsolutePath SkipSurfaceBaselinePath => BaselinesDir / "skip-surface-baseline.json";

    // ---- Marker patterns ------------------------------------------------
    //
    // Each pattern targets a single emission shape. We normalize the captured
    // reason (collapse whitespace, trim, strip trailing punctuation) so the
    // same logical skip cause aggregates under one baseline row regardless of
    // formatter-driven cosmetic drift.

    // Anchored to start-of-line whitespace under RegexOptions.Multiline so the
    // marker only matches when the comment is the line's leading content. This
    // avoids false-positive hits on string literals or doc comments that
    // happen to contain `// Unsupported:` / `// Skipped:` substrings — a
    // plausible shape in generator output that embeds such text in a string
    // literal or doc comment.
    static readonly Regex UnsupportedComment =
        new(@"^\s*//\s*Unsupported:\s*(?<reason>.+?)\s*$",
            RegexOptions.Compiled | RegexOptions.Multiline);

    static readonly Regex SkippedComment =
        new(@"^\s*//\s*Skipped:\s*(?<reason>.+?)\s*$",
            RegexOptions.Compiled | RegexOptions.Multiline);

    // [UnsupportedSwiftType("reason", …)] — capture the first string-literal
    // argument as the reason; the second argument (when present) is the type
    // name and is intentionally ignored so all instances of the same skip
    // reason aggregate under one baseline row regardless of which type tripped
    // it. The attribute is emitted fully-qualified (`[global::Swift.UnsupportedSwiftType(...)]`)
    // in current generator output, so the optional namespace prefix is handled
    // explicitly.
    static readonly Regex UnsupportedSwiftTypeAttr =
        new(@"\[\s*(?:global::)?(?:[A-Za-z_][A-Za-z0-9_]*\.)*UnsupportedSwiftType\s*\(\s*""(?<reason>[^""]*)""",
            RegexOptions.Compiled);

    // [Obsolete("message", DiagnosticId = "SB0001", …)] — narrow to SB0001
    // because that's the diagnostic for obsolete-via-skip surface (SB0001 lives
    // in the DiagnosticId named arg, not
    // in the message itself, so the pattern requires SB0001 anywhere inside
    // the attribute's argument list while capturing the first string literal
    // (the message) as the reason. Optional namespace prefix mirrors the
    // UnsupportedSwiftType pattern so a future emitter switch to
    // `[global::System.Obsolete(...)]` or `[System.Obsolete(...)]` doesn't
    // silently drop the SB0001 count and report a false improvement.
    static readonly Regex ObsoleteSb0001Attr =
        new(@"\[\s*(?:global::)?(?:[A-Za-z_][A-Za-z0-9_]*\.)*Obsolete\s*\(\s*""(?<reason>[^""]*)""[^)]*SB0001[^)]*\)\s*\]",
            RegexOptions.Compiled);

    /// <summary>
    /// Walks the generator output, parses skip markers, and diffs against the
    /// committed baseline. Throws when an upward delta or a new key without a
    /// baseline update is detected.
    /// </summary>
    void RunSkipSurfaceGate()
    {
        Log.Information("=========================================");
        Log.Information(" Skip-surface trend gate");
        Log.Information("=========================================");

        var roots = CollectSkipSurfaceRoots();
        if (roots.Count == 0)
        {
            Log.Warning("Skip-surface gate: no generator output found at {Output} — nothing to scan", BtOutputDir);
            return;
        }

        var entries = ScanForSkipMarkers(roots);
        Log.Information("Skip-surface gate: scanned {Roots} root(s), {Entries} unique (source, marker, reason) keys",
            roots.Count, entries.Count);

        var baseline = SkipSurfaceBaseline.Load(SkipSurfaceBaselinePath);
        var vanished = CollectVanishedManifestTypes();
        if (vanished.Count > 0)
            Log.Information("Skip-surface gate: {Count} type(s) lost every symbol-bearing member since the API " +
                "manifest baseline — their skip markers cannot count as improvements.", vanished.Count);

        var (regressions, improvements) = baseline.Compare(entries, vanished);

        foreach (var line in improvements)
            Log.Information("  ✓ {Line}", line);

        if (regressions.Count > 0)
        {
            foreach (var line in regressions)
                Log.Error("  ✗ {Line}", line);

            throw new Exception(
                $"Skip-surface trend gate failed: {regressions.Count} regression(s). " +
                $"Either fix the underlying skip OR — if intentional — update {SkipSurfaceBaselinePath.Name} " +
                $"in the same commit.");
        }

        Log.Information("Skip-surface trend gate passed (downward or flat against baseline).");
    }

    IReadOnlyList<AbsolutePath> CollectSkipSurfaceRoots()
    {
        var roots = new List<AbsolutePath>();
        if (Directory.Exists(BtOutputDir))
            roots.Add(BtOutputDir);

        return roots;
    }

    /// <summary>
    /// Types that had at least one symbol-bearing member in the API-manifest baseline and have
    /// NONE in the current manifests — i.e. the type stopped contributing bindable API entirely.
    /// A skip marker that disappears for such a type disappeared because the surface did, not
    /// because the skip was fixed, which is the inversion this cross-reference exists to catch.
    /// Keyed <c>{module}|{TypeName}</c>; free functions (no declaring type) contribute nothing.
    /// </summary>
    IReadOnlySet<string> CollectVanishedManifestTypes()
    {
        try
        {
            var manifestBaseline = ApiManifestBaseline.Load(ApiManifestBaselinePath);
            var current = ScanApiManifests();
            return SkipSurfaceBaseline.ComputeVanishedTypes(manifestBaseline.Entries, current.Entries);
        }
        catch (Exception ex)
        {
            // The cross-reference is a corroborating signal layered on top of the count ratchet;
            // a missing or malformed manifest must not take down the ratchet itself. Without it
            // every GONE/DOWN row simply stays an unverified improvement, which is the gate's
            // pre-cross-reference behavior.
            Log.Warning("Skip-surface gate: API-manifest cross-reference unavailable ({Message}); " +
                "GONE/DOWN rows will not be corroborated this run.", ex.Message);
        }

        return new HashSet<string>(StringComparer.Ordinal);
    }

    /// <summary>
    /// Scans roots for skip markers and aggregates by (source, marker, reason).
    /// </summary>
    /// <remarks>
    /// Tombstone detection (declared in metadata cookie maps but absent from
    /// generated C#) is intentionally not implemented in this scaffolding pass.
    /// Detecting it cleanly requires correlating two artifacts inside the
    /// generator output and is best authored alongside the skip-class fix that
    /// first surfaces the pattern in BindingTests output.
    /// The marker keyword <c>"Tombstone"</c> is reserved in the baseline schema
    /// for that work to slot into without a schema change.
    /// </remarks>
    IReadOnlyList<SkipSurfaceBaseline.SkipSurfaceEntry> ScanForSkipMarkers(IReadOnlyList<AbsolutePath> roots)
    {
        var counts = new Dictionary<(string Source, string Marker, string Reason), int>();

        foreach (var root in roots)
        {
            foreach (var path in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                var text = File.ReadAllText(path);
                // Nuke's GetRelativePathTo runs from `this` to `other`, so the
                // call we want is `RootDirectory → path` to land a repo-rooted
                // forward path like `BindingTests/output/SwiftBindingsTestLib.cs`.
                // Forward slashes for cross-platform-stable baseline keys.
                var source = RootDirectory.GetRelativePathTo((AbsolutePath)path)
                    .ToString().Replace('\\', '/');
                // File-per-type split: fold each module's per-type files
                // ({Module}.Types.{Leaf}.cs) back onto the module source ({Module}.cs) so skip
                // counts are path-stable across the split. The gate tracks per-module skip
                // trends, not per-physical-file, so this keeps the baseline valid without a
                // reseed — the same (marker, reason) totals, just re-homed to the module file.
                source = Regex.Replace(source, @"\.Types\.[^/]+\.cs$", ".cs");

                Tally(text, "Unsupported", UnsupportedComment, source, counts);
                Tally(text, "Skipped", SkippedComment, source, counts);
                Tally(text, "UnsupportedSwiftType", UnsupportedSwiftTypeAttr, source, counts);
                Tally(text, "ObsoleteSB0001", ObsoleteSb0001Attr, source, counts);
            }
        }

        return counts
            .OrderBy(kv => kv.Key.Source, StringComparer.Ordinal)
            .ThenBy(kv => kv.Key.Marker, StringComparer.Ordinal)
            .ThenBy(kv => kv.Key.Reason, StringComparer.Ordinal)
            .Select(kv => new SkipSurfaceBaseline.SkipSurfaceEntry
            {
                Source = kv.Key.Source,
                Marker = kv.Key.Marker,
                Reason = kv.Key.Reason,
                Count = kv.Value,
            })
            .ToList();
    }

    static void Tally(
        string text, string marker, Regex pattern, string source,
        Dictionary<(string, string, string), int> counts)
    {
        foreach (Match match in pattern.Matches(text))
        {
            var reason = NormalizeReason(match.Groups["reason"].Value);
            if (string.IsNullOrEmpty(reason)) continue;
            var key = (source, marker, reason);
            counts[key] = counts.TryGetValue(key, out var n) ? n + 1 : 1;
        }
    }

    static string NormalizeReason(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        // Collapse internal whitespace, trim trailing punctuation/whitespace.
        var collapsed = Regex.Replace(raw, @"\s+", " ").Trim();
        return collapsed.TrimEnd('.', ',', ';', ' ');
    }

    // ---- Manual baseline reseeder ---------------------------------------
    //
    // Seeds build/baselines/skip-surface-baseline.json from the current generator output.
    // Run once when this scaffolding lands; thereafter, bundles edit the file
    // by hand to record their downward deltas in the same commit as the fix.
    //
    // The .After(BehaviorTier, ValidateBlastRadius, BindingTests, X64SimGate) edges
    // satisfy Nuke `--strict`'s requirement of a total peel order over sinks
    // (BehaviorTier and ValidateBlastRadius are otherwise co-equal sinks, and the
    // X64*Gate chain terminates at X64SimGate); the body never observes any of
    // them, so the edges are pure ordering.
    Target SeedSkipSurfaceBaseline => _ => _
        .After(BindingTests, BehaviorTier, ValidateBlastRadius, X64SimGate)
        .Executes(() =>
        {
            var roots = CollectSkipSurfaceRoots();
            if (roots.Count == 0)
                throw new Exception(
                    $"Cannot seed: no generator output found. Run `nuke binding-tests --compile-only` first.");

            var entries = ScanForSkipMarkers(roots);
            var baseline = new SkipSurfaceBaseline
            {
                GitSha = ReadHeadShaShort(),
                Entries = entries,
            };
            baseline.Save(SkipSurfaceBaselinePath);
            Log.Information("Seeded {Path} with {Count} entries from {Roots} root(s).",
                SkipSurfaceBaselinePath, entries.Count, roots.Count);
        });

    static string ReadHeadShaShort()
    {
        try
        {
            var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                Arguments = "rev-parse --short HEAD",
                RedirectStandardOutput = true,
                UseShellExecute = false,
            });
            proc!.WaitForExit();
            return proc.StandardOutput.ReadToEnd().Trim();
        }
        catch
        {
            return "";
        }
    }
}
