// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Build.SkipSurface.cs — Layer B "skip-surface" trend gate.
//
// Parses mechanically-detectable skip markers from generated `.cs` output and
// diffs against `.skip-surface-baseline.json`. Wired as a post-step in the
// `binding-tests` --compile-only path (gated by --skip-surface) so the gate
// runs against fresh generator output. Authoring scope and ratchet semantics
// are documented in src/docs/0.10.0-fix-plan.md §"Layer B — skip-surface trend
// gate, on authored corpus".
//
// The corpus the gate scans is:
//   1. The wider BindingTests-generated output under `BindingTests/output/`,
//      which is what `RunRegenerateBindings()` produces today.
//   2. Any future generator output rooted under
//      `BindingTests/Sources/SurfaceArea/` — once SurfaceArea snippets are
//      contributed by skip-class fix bundles. The directory is empty in this
//      scaffolding commit; the scanner picks up `.cs` from there automatically
//      when bundles populate it.
//
// Scanning the generated `.cs` directly (rather than a sidecar metrics file)
// is deliberate: the markers we ratchet are exactly the strings a consumer
// reading the binding output would see. If the generator emits `// Skipped: …`
// in the file but the metrics sidecar disagrees, the sidecar is wrong; we
// trust the file.

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
    [Parameter("Run the Layer B skip-surface trend gate against .skip-surface-baseline.json")]
    readonly bool SkipSurface;

    AbsolutePath SkipSurfaceBaselinePath => RootDirectory / ".skip-surface-baseline.json";

    AbsolutePath SurfaceAreaDir => BindingTestsDir / "Sources" / "SurfaceArea";

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
    // plausible shape once authored SurfaceArea snippets land alongside the
    // generator output.
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
    // because that's the diagnostic the 0.10.0 plan calls out (see Bundle 7
    // cross-dep with Bundle 2). SB0001 lives in the DiagnosticId named arg, not
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
            Log.Warning("Skip-surface gate: no generator output found at {Output} or {SurfaceArea} — nothing to scan",
                BtOutputDir, SurfaceAreaDir);
            return;
        }

        var entries = ScanForSkipMarkers(roots);
        Log.Information("Skip-surface gate: scanned {Roots} root(s), {Entries} unique (source, marker, reason) keys",
            roots.Count, entries.Count);

        var baseline = SkipSurfaceBaseline.Load(SkipSurfaceBaselinePath);
        var (regressions, improvements) = baseline.Compare(entries);

        foreach (var line in improvements)
            Log.Information("  ✓ {Line}", line);

        if (regressions.Count > 0)
        {
            foreach (var line in regressions)
                Log.Error("  ✗ {Line}", line);

            throw new Exception(
                $"Skip-surface trend gate failed: {regressions.Count} regression(s). " +
                $"Either fix the underlying skip OR — if intentional — update {SkipSurfaceBaselinePath.Name} " +
                $"in the same commit. See src/docs/0.10.0-fix-plan.md §Layer B.");
        }

        Log.Information("Skip-surface trend gate passed (downward or flat against baseline).");
    }

    IReadOnlyList<AbsolutePath> CollectSkipSurfaceRoots()
    {
        var roots = new List<AbsolutePath>();
        if (Directory.Exists(BtOutputDir))
            roots.Add(BtOutputDir);

        // The SurfaceArea source directory ships empty; bundles populate it.
        // We scan it for `.cs` only if it ever produces generated output, which
        // would land under a subdirectory the populating bundle wires up. For
        // now this is a forward-looking path that costs nothing when empty.
        if (Directory.Exists(SurfaceAreaDir))
        {
            // Skip the README and any non-`.cs` corpus inputs — only generated
            // C# is in scope.
            var hasCs = Directory.EnumerateFiles(SurfaceAreaDir, "*.cs", SearchOption.AllDirectories).Any();
            if (hasCs) roots.Add(SurfaceAreaDir);
        }

        return roots;
    }

    /// <summary>
    /// Scans roots for skip markers and aggregates by (source, marker, reason).
    /// </summary>
    /// <remarks>
    /// Tombstone detection (declared in metadata cookie maps but absent from
    /// generated C#) is intentionally not implemented in this scaffolding pass.
    /// Detecting it cleanly requires correlating two artifacts inside the
    /// generator output and is best authored alongside the skip-class fix that
    /// first surfaces the pattern in BindingTests output. See plan-doc Bundle 7.
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
    // Seeds .skip-surface-baseline.json from the current generator output.
    // Run once when this scaffolding lands; thereafter, bundles edit the file
    // by hand to record their downward deltas in the same commit as the fix.
    //
    // The .After(BehaviorTier, ValidateBlastRadius, BindingTests) edges
    // satisfy Nuke `--strict`'s requirement of a total peel order over sinks
    // (BehaviorTier and ValidateBlastRadius are otherwise co-equal sinks);
    // the body never observes any of them, so the edges are pure ordering.
    Target SeedSkipSurfaceBaseline => _ => _
        .After(BindingTests, BehaviorTier, ValidateBlastRadius)
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
