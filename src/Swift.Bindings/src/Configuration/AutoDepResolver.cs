// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration
{
    /// <summary>
    /// Resolves auto-detected cross-module Swift dependencies into either a concrete
    /// sibling <c>ProjectReference</c> path or an unresolved-dependency warning record.
    ///
    /// This is the typed, unit-testable replacement for the inline POSIX-sh
    /// <c>&lt;Exec&gt;</c> that previously lived in the SDK's
    /// <c>_ResolveSwiftAutoDetectedDependencies</c> target (architecture-review-2026-06
    /// Finding 1). The SDK target now invokes the generator's <c>--resolve-auto-deps</c>
    /// verb, which delegates here.
    ///
    /// The output line grammar is a behavioral contract consumed by the SDK target and
    /// is FROZEN — do not change it without updating the consuming MSBuild ItemGroup /
    /// Warning in <c>Sdk.targets</c> and the tests that pin it:
    /// <list type="bullet">
    ///   <item><c>PROJREF|&lt;absolute-csproj-path&gt;</c> — a sibling project was found;
    ///   the SDK injects it as a <c>ProjectReference</c> via <c>.Substring(8)</c>.</item>
    ///   <item><c>WARN|&lt;rawModule&gt;|&lt;rawPackageId&gt;|&lt;rawVersion&gt;|&lt;rawXcframeworkPath&gt;</c>
    ///   — no sibling found; the SDK emits SWIFTBIND080 and splits on <c>|</c> by index.
    ///   The four trailing fields are the ORIGINAL percent-encoded values (so the warning
    ///   round-trips embedded <c>|</c>/<c>;</c>/<c>%</c> back to the user verbatim).</item>
    /// </list>
    /// </summary>
    public static class AutoDepResolver
    {
        /// <summary>
        /// Resolves the percent-encoded auto-dependency spec into frozen-grammar output lines.
        /// Pure and side-effect free aside from the injected probes — callers pass
        /// <paramref name="fileExists"/> / <paramref name="toAbsolutePath"/> so the parse,
        /// decode-order, dedup, and probe-order logic is fully unit-testable.
        /// </summary>
        /// <param name="autoDepSpec">
        /// Semicolon-delimited records, each a pipe-delimited 4-tuple
        /// <c>Module|PackageId|Version|XCFrameworkPath</c>. Literal <c>|</c>/<c>;</c>/<c>%</c>
        /// inside a field arrive percent-encoded as <c>%7C</c>/<c>%3B</c>/<c>%25</c>.
        /// </param>
        /// <param name="explicitDeps">
        /// Semicolon-delimited module names already declared via <c>SwiftFrameworkDependency</c>;
        /// any auto-detected dep whose (decoded) module matches one of these is skipped.
        /// </param>
        /// <param name="fileExists">Probe predicate for candidate csproj paths (real: <see cref="File.Exists"/>).</param>
        /// <param name="toAbsolutePath">Absolute-path normalizer for a found csproj (real: <see cref="Path.GetFullPath(string)"/>).</param>
        public static IReadOnlyList<string> Resolve(
            string? autoDepSpec,
            string? explicitDeps,
            Func<string, bool> fileExists,
            Func<string, string> toAbsolutePath)
        {
            ArgumentNullException.ThrowIfNull(fileExists);
            ArgumentNullException.ThrowIfNull(toAbsolutePath);

            var results = new List<string>();
            if (string.IsNullOrEmpty(autoDepSpec))
                return results;

            // Dedup set: the shell did a `case ";$EXPLICIT_DEPS;" in *";$MOD;"*` delimiter-
            // bounded substring match against the DECODED module. This C# port splits the
            // explicit-deps `;`-list into a set and tests `Contains(module)` — exact-element
            // membership, which matches the shell for every real input (Swift module names are
            // identifiers with no `;`). The one corner where the two diverge: a module that
            // decodes to contain a `;` (from `%3B`) can never equal a split element, so it is
            // emitted here where the shell would have deduped it. That input cannot arise from
            // the producer (a `;` in a module name is itself impossible), and the set-membership
            // form is arguably the more-correct reading of a `;`-list, so the divergence is
            // intentional, not a parity bug (Regression-R4 finding C2). Ordinal: module names
            // are case-sensitive ABI identifiers.
            var explicitSet = new HashSet<string>(StringComparer.Ordinal);
            if (!string.IsNullOrEmpty(explicitDeps))
            {
                foreach (var dep in explicitDeps.Split(';'))
                {
                    if (!string.IsNullOrEmpty(dep))
                        explicitSet.Add(dep);
                }
            }

            // `echo "$DEPS" | tr ';' '\n' | while IFS='|' read -r MOD PKG VER XCFW`
            foreach (var record in autoDepSpec.Split(';'))
            {
                // `read` with 4 vars puts any remainder (incl. embedded '|') into the last
                // field; an over-long record therefore keeps its tail in XCFW. Mirror with a
                // 4-way split limit.
                var fields = record.Split('|', 4);

                var rawMod = fields.Length > 0 ? fields[0] : string.Empty;
                var rawPkg = fields.Length > 1 ? fields[1] : string.Empty;
                var rawVer = fields.Length > 2 ? fields[2] : string.Empty;
                var rawXcfw = fields.Length > 3 ? fields[3] : string.Empty;

                // `[ -z "$MOD_NAME" ] && continue` — guard fires on the RAW (still-encoded)
                // first field, before decode, so blank/empty records are skipped.
                if (string.IsNullOrEmpty(rawMod))
                    continue;

                var module = Decode(rawMod);
                var packageId = Decode(rawPkg);
                var xcframeworkPath = Decode(rawXcfw);

                if (explicitSet.Contains(module))
                    continue;

                // dirname / dirname twice, exactly as the shell walked up from the xcframework.
                var parent = DirName(xcframeworkPath);
                var grandparent = DirName(parent);

                // Probe order is significant and FROZEN — first hit wins.
                string? found = null;
                foreach (var candidate in new[]
                {
                    Combine(parent, packageId + ".csproj"),
                    Combine(grandparent, packageId, packageId + ".csproj"),
                    Combine(grandparent, module, packageId + ".csproj"),
                    Combine(grandparent, packageId + ".csproj"),
                })
                {
                    if (fileExists(candidate))
                    {
                        found = candidate;
                        break;
                    }
                }

                if (found is not null)
                {
                    // Shell: FOUND_ABS=`cd $(dirname FOUND) && pwd`; echo PROJREF|$FOUND_ABS/$(basename FOUND).
                    // toAbsolutePath is Path.GetFullPath, which logically normalizes an ABSOLUTE
                    // candidate without resolving symlinks — exact parity with the shell's `pwd -L`
                    // for the real inputs (the dependency XCFrameworkPath is always absolutized by
                    // Program.cs before this resolver sees it). The one divergence: for a RELATIVE
                    // candidate, GetFullPath prepends Environment.CurrentDirectory, which macOS
                    // realpath-resolves (`/private/tmp/…` vs the `/tmp/…` `pwd -L` preserves). That
                    // path is unreachable in normal SDK use, and `/tmp`↔`/private/tmp` are the same
                    // inode so the injected ProjectReference resolves identically — cosmetic, not a
                    // functional parity bug (Regression-R4 finding C3).
                    results.Add("PROJREF|" + toAbsolutePath(found));
                }
                else
                {
                    // WARN carries the ORIGINAL encoded fields so SWIFTBIND080 round-trips them.
                    results.Add($"WARN|{rawMod}|{rawPkg}|{rawVer}|{rawXcfw}");
                }
            }

            return results;
        }

        /// <summary>
        /// Resolves <paramref name="autoDepSpec"/> against the real filesystem and writes each
        /// frozen-grammar line to <paramref name="output"/> (one per line). Used by the
        /// <c>--resolve-auto-deps</c> CLI verb; the SDK captures these via
        /// <c>ConsoleToMSBuild</c>.
        /// </summary>
        public static void Run(string? autoDepSpec, string? explicitDeps, TextWriter output)
        {
            ArgumentNullException.ThrowIfNull(output);
            foreach (var line in Resolve(autoDepSpec, explicitDeps, File.Exists, Path.GetFullPath))
                output.WriteLine(line);
        }

        /// <summary>
        /// Percent-decodes a single field. Order is significant and mirrors the original
        /// <c>sed 's/%7C/|/g;s/%3B/;/g;s/%25/%/g'</c>: <c>%25</c> (the escape for a literal
        /// <c>%</c>) MUST decode LAST so that an encoded <c>%257C</c> becomes the literal
        /// text <c>%7C</c> rather than a pipe.
        /// </summary>
        private static string Decode(string field) =>
            field.Replace("%7C", "|").Replace("%3B", ";").Replace("%25", "%");

        /// <summary>
        /// POSIX <c>dirname</c> analog. The shell script walked up the xcframework path with
        /// <c>dirname</c>, so this reproduces its semantics for shell parity rather than using
        /// <see cref="Path.GetDirectoryName(string)"/> (which keeps a trailing-slash path as its
        /// own directory and returns empty for a basename-only path):
        /// <list type="bullet">
        ///   <item>empty input -&gt; <c>"."</c> (POSIX <c>dirname ""</c>);</item>
        ///   <item>trailing slashes are stripped before the parent is taken
        ///   (<c>.../X.xcframework/</c> -&gt; <c>...</c>);</item>
        ///   <item>a basename-only path (no <c>/</c>) -&gt; <c>"."</c>;</item>
        ///   <item>a path whose only slash is the leading root -&gt; <c>"/"</c>.</item>
        /// </list>
        /// In practice the field is always an absolute, normalized xcframework path (no trailing
        /// slash, not basename-only), for which this and <c>Path.GetDirectoryName</c> agree; the
        /// edge cases exist only to keep the documented parity contract honest.
        /// </summary>
        private static string DirName(string path)
        {
            if (string.IsNullOrEmpty(path))
                return ".";

            // POSIX dirname strips trailing slashes before taking the parent; a path that is
            // all slashes collapses to the root.
            var trimmed = path.TrimEnd('/');
            if (trimmed.Length == 0)
                return "/";

            var slash = trimmed.LastIndexOf('/');
            if (slash < 0)
                return "."; // basename-only

            // Drop the final component, then strip any redundant trailing slashes on the parent.
            var parent = trimmed.Substring(0, slash).TrimEnd('/');
            return parent.Length == 0 ? "/" : parent;
        }

        /// <summary>
        /// Joins path segments with <c>/</c>, mirroring the shell's literal <c>"$DIR/$NAME"</c>
        /// concatenation rather than <see cref="Path.Combine(string, string)"/> (which would
        /// discard the left side if a later segment looked rooted).
        /// </summary>
        private static string Combine(params string[] segments) =>
            string.Join("/", segments);
    }
}
