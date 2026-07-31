// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Xml;
using System.Xml.Linq;

namespace BindingsGeneration
{
    /// <summary>
    /// Resolves auto-detected cross-module Swift dependencies into either a concrete
    /// sibling <c>ProjectReference</c> path or an unresolved-dependency warning record.
    ///
    /// This is the typed, unit-testable replacement for the inline POSIX-sh
    /// <c>&lt;Exec&gt;</c> that previously lived in the SDK's
    /// <c>_ResolveSwiftAutoDetectedDependencies</c> target. The SDK target now invokes
    /// the generator's <c>--resolve-auto-deps</c> verb, which delegates here.
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
        /// <param name="enumerateProjectFiles">
        /// Lists the <c>*.csproj</c> files directly inside a directory (real:
        /// <see cref="Directory.GetFiles(string, string, SearchOption)"/>). Together with
        /// <paramref name="readFileText"/> this enables the NAME-INDEPENDENT fifth probe; pass
        /// <see langword="null"/> for either to run the four name-derived probes only.
        /// </param>
        /// <param name="readFileText">
        /// Reads a candidate csproj's text so the fifth probe can confirm it is a binding project
        /// (real: <see cref="File.ReadAllText(string)"/>, returning <see langword="null"/> on I/O error).
        /// </param>
        /// <param name="consumerProjectPath">
        /// The project being built (<c>$(MSBuildProjectFullPath)</c>), excluded from the fifth
        /// probe's candidates so a dependency xcframework co-located with the consumer's own csproj
        /// cannot produce a self-<c>ProjectReference</c>. Optional: omit it and the exclusion is
        /// simply not applied.
        /// </param>
        public static IReadOnlyList<string> Resolve(
            string? autoDepSpec,
            string? explicitDeps,
            Func<string, bool> fileExists,
            Func<string, string> toAbsolutePath,
            Func<string, IReadOnlyList<string>>? enumerateProjectFiles = null,
            Func<string, string?>? readFileText = null,
            string? consumerProjectPath = null)
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

                // Probe 5 — NAME-INDEPENDENT, strictly additive after the four frozen probes.
                //
                // Why it is needed: the PackageId in the record is SYNTHESIZED, not observed. An
                // auto-detected dependency is discovered from the consumer's own binary (otool -L)
                // and carries only a module name and an xcframework path; nothing in that run knows
                // the sibling's real package identity, so `GetEffectivePackageId` falls back to the
                // CLI default `{Module}.Swift.{Platform}` and `EffectiveVersion` to `0.0.0`
                // (FrameworkDependencyInfo.cs). All four probes above build their candidate FROM
                // that synthesized name, so they can only hit a repo that happens to name its
                // projects by the same convention. A repo that names them anything else — e.g.
                // `FBAEMKit/SwiftBindings.Facebook.AEM.csproj` next to `FBAEMKit.xcframework` —
                // misses every probe and reports a satisfied dependency closure as unresolved.
                //
                // Threading the real identity through the record instead is not available: the
                // dependency xcframework is a vendor artifact and the consumer's generation run is
                // the only run that writes the record, so the sibling's PackageId is simply not
                // knowable at record-write time. The directory itself is the evidence.
                //
                // Fail-closed on ambiguity: the hit must be the ONE csproj in the dependency
                // xcframework's own directory that is a Swift-bindings binding project (proven by
                // parsing its XML and matching an exact SDK declaration — see IsBindingProjectText).
                // Zero matches or two-or-more matches leave `found` null and the record warns,
                // exactly as before — a wrong ProjectReference is worse than a warning. The project
                // being built is excluded outright, so a co-located dependency cannot self-reference.
                //
                // What this deliberately does NOT do is prove the candidate binds THIS xcframework
                // (by parsing its SwiftFramework items or its generated metadata). Directory
                // co-location plus exactly-one plus a verified SDK declaration is the evidence;
                // demanding more would reject the auto-discovery shape this probe exists to serve,
                // and a wrong hit here fails visibly at build (the referenced project's types simply
                // are not the ones the generated code names) rather than silently. Recorded as a
                // dismissed-by-design residual in src/docs/not-planned.md.
                if (found is null && enumerateProjectFiles is not null && readFileText is not null)
                    found = ProbeSiblingBindingProject(
                        parent, enumerateProjectFiles, readFileText, consumerProjectPath, toAbsolutePath);

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
        public static void Run(
            string? autoDepSpec,
            string? explicitDeps,
            TextWriter output,
            string? consumerProjectPath = null)
        {
            ArgumentNullException.ThrowIfNull(output);
            foreach (var line in Resolve(
                         autoDepSpec, explicitDeps, File.Exists, Path.GetFullPath,
                         EnumerateProjectFiles, ReadFileTextOrNull, consumerProjectPath))
                output.WriteLine(line);
        }

        /// <summary>
        /// The SDK name a Swift-bindings binding project declares. Compared EXACTLY (ordinal)
        /// against the name component of an SDK reference, never as a substring.
        /// </summary>
        internal const string SwiftBindingsSdkName = "SwiftBindings.Sdk";

        /// <summary>
        /// True when <paramref name="text"/> parses as an MSBuild project that DECLARES the
        /// Swift-bindings SDK — either as <c>&lt;Project Sdk="SwiftBindings.Sdk[/version]"&gt;</c>
        /// (the attribute form, which may carry a <c>;</c>-delimited list) or as a top-level
        /// <c>&lt;Sdk Name="SwiftBindings.Sdk" [Version="…"] /&gt;</c> element.
        /// <para>
        /// This is a real XML parse rather than a substring scan, and the difference is the whole
        /// point of the check. Substring markers accepted a commented-out declaration
        /// (<c>&lt;!-- &lt;Project Sdk="SwiftBindings.Sdk/0.18.0"&gt; --&gt;</c>) and a prefix
        /// collision (<c>Sdk="SwiftBindings.SdkSomethingElse"</c>) while MISSING valid XML that
        /// spells the attribute with whitespace (<c>Sdk = "SwiftBindings.Sdk"</c>) — three ways to
        /// get the wrong answer about a file whose grammar is fully specified. A false positive here
        /// injects a <c>ProjectReference</c> on an unrelated project, so the check is exact:
        /// the root element must be <c>Project</c>, and some SDK reference's name component (the
        /// text before the optional <c>/version</c>) must equal <see cref="SwiftBindingsSdkName"/>.
        /// </para>
        /// <para>
        /// FAIL CLOSED: malformed XML, a non-<c>Project</c> root, or an empty file returns false —
        /// consistent with the rest of this probe, where "no evidence" always means "warn", never
        /// "guess". Element and attribute NAMES are matched case-insensitively and namespace-
        /// agnostically (MSBuild tolerates both, and the legacy
        /// <c>xmlns="…/developer/msbuild/2003"</c> form is still valid); the SDK name VALUE is
        /// matched ordinal-exactly. A project spelling the SDK id in a different case would
        /// therefore be missed and warn — the safe direction, and not a shape this repo's
        /// templates or the generator ever emit.
        /// </para>
        /// <para>
        /// Generator-emitted binding projects (<c>{Module}.Swift.{Platform}.csproj</c>) use
        /// <c>Microsoft.NET.Sdk</c> and are found by the four name-derived probes; this probe exists
        /// for consumer-authored SDK-mode projects, which are exactly the ones free to be named
        /// anything.
        /// </para>
        /// </summary>
        internal static bool IsBindingProjectText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            XDocument document;
            try
            {
                // XDocument.Parse prohibits DTD processing by default, so a candidate csproj
                // cannot pull in an external entity while being sniffed.
                document = XDocument.Parse(text, LoadOptions.None);
            }
            catch (XmlException)
            {
                return false; // not well-formed XML — not a project we can vouch for.
            }

            var root = document.Root;
            if (root is null || !NameIs(root.Name, "Project"))
                return false;

            // <Project Sdk="SwiftBindings.Sdk/0.18.0"> — the attribute may list several SDKs.
            foreach (var attribute in root.Attributes())
            {
                if (NameIs(attribute.Name, "Sdk") && DeclaresSwiftBindingsSdk(attribute.Value))
                    return true;
            }

            // <Project><Sdk Name="SwiftBindings.Sdk" Version="0.18.0" /></Project>. Only direct
            // children count: that is the only position MSBuild honors, and accepting a nested
            // element would reintroduce a false-positive surface for no gain.
            foreach (var element in root.Elements())
            {
                if (!NameIs(element.Name, "Sdk"))
                    continue;

                foreach (var attribute in element.Attributes())
                {
                    if (NameIs(attribute.Name, "Name") && DeclaresSwiftBindingsSdk(attribute.Value))
                        return true;
                }
            }

            // <Import Project="Sdk.props" Sdk="SwiftBindings.Sdk" /> — the explicit-import form,
            // which a project uses when it needs to interleave its own properties between the
            // SDK's props and targets. It is a real declaration and the previous substring markers
            // accepted it, so rejecting it here would be a regression that silently turns a
            // resolvable dependency back into SWIFTBIND080. Imports are matched at any depth
            // (ImportGroup/Choose are legal parents); the Sdk ATTRIBUTE is still required, so an
            // <Import Project="…/SwiftBindings.Sdk/Sdk.props" /> naming the SDK only inside a path
            // remains a non-match.
            foreach (var element in root.Descendants())
            {
                if (!NameIs(element.Name, "Import"))
                    continue;

                foreach (var attribute in element.Attributes())
                {
                    if (NameIs(attribute.Name, "Sdk") && DeclaresSwiftBindingsSdk(attribute.Value))
                        return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Namespace-agnostic, case-insensitive XML name comparison. MSBuild accepts both the
        /// bare and the 2003-namespaced project grammar and does not care about element-name
        /// casing, so neither may decide whether a project is a binding project.
        /// </summary>
        private static bool NameIs(XName name, string localName) =>
            string.Equals(name.LocalName, localName, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// True when a <c>Sdk</c>/<c>Name</c> attribute value references the Swift-bindings SDK.
        /// The value is a <c>;</c>-delimited list of <c>Name[/Version]</c> entries; each entry's
        /// NAME component must equal <see cref="SwiftBindingsSdkName"/> exactly, so
        /// <c>SwiftBindings.SdkSomethingElse</c> does not qualify.
        /// </summary>
        private static bool DeclaresSwiftBindingsSdk(string sdkReferenceList)
        {
            foreach (var entry in sdkReferenceList.Split(';'))
            {
                var slash = entry.IndexOf('/');
                var name = (slash < 0 ? entry : entry.Substring(0, slash)).Trim();
                if (string.Equals(name, SwiftBindingsSdkName, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Fifth probe: the single binding project living in the dependency xcframework's OWN
        /// directory, found by content rather than by a synthesized name. Returns null when the
        /// directory holds no binding project or more than one (ambiguous — fail closed).
        /// <para>
        /// <paramref name="consumerProjectPath"/>, when supplied, is the project currently being
        /// built and is never a candidate. It has to be excluded explicitly because the consumer's
        /// own csproj can legitimately sit in the same directory as an auto-detected dependency's
        /// xcframework (a vendor dropping several xcframeworks beside one binding project), where
        /// it would otherwise be the sole content match and probe 5 would inject a self-
        /// <c>ProjectReference</c>. Comparison is on the normalized path and case-insensitive:
        /// this generator runs on macOS, whose default filesystem is case-insensitive, and being
        /// permissive here only ever removes a candidate — the fail-closed direction.
        /// </para>
        /// <para>
        /// The comparison normalizes but does not RESOLVE (no <c>realpath</c>): two spellings of the
        /// same file that differ by a symlinked directory — macOS's <c>/tmp</c> vs <c>/private/tmp</c>
        /// being the classic pair — are not recognized as equal, and the exclusion silently does not
        /// apply. Both paths come from the same build (MSBuild's <c>$(MSBuildProjectFullPath)</c> and
        /// a directory walked up from the dependency's absolutized xcframework path), so they agree
        /// in practice; when they do not, the outcome is simply the pre-exclusion behavior — a
        /// self-<c>ProjectReference</c> that MSBuild rejects loudly as a circular reference, never a
        /// silently wrong build.
        /// </para>
        /// </summary>
        internal static string? ProbeSiblingBindingProject(
            string directory,
            Func<string, IReadOnlyList<string>> enumerateProjectFiles,
            Func<string, string?> readFileText,
            string? consumerProjectPath = null,
            Func<string, string>? toAbsolutePath = null)
        {
            var normalize = toAbsolutePath ?? (p => p);
            var consumer = string.IsNullOrEmpty(consumerProjectPath) ? null : Normalize(consumerProjectPath, normalize);

            string? single = null;
            foreach (var candidate in enumerateProjectFiles(directory))
            {
                if (consumer is not null &&
                    string.Equals(Normalize(candidate, normalize), consumer, StringComparison.OrdinalIgnoreCase))
                    continue; // the project being built is not its own dependency.

                var text = readFileText(candidate);
                if (text is null || !IsBindingProjectText(text))
                    continue;

                if (single is not null)
                    return null; // two binding projects in one directory — refuse to guess.

                single = candidate;
            }

            return single;
        }

        /// <summary>
        /// Best-effort path normalization for the self-reference comparison; a normalizer that
        /// throws on a malformed path degrades to the raw string rather than aborting resolution.
        /// </summary>
        private static string Normalize(string path, Func<string, string> toAbsolutePath)
        {
            try
            {
                return toAbsolutePath(path);
            }
            catch (ArgumentException)
            {
                return path;
            }
            catch (IOException)
            {
                return path;
            }
            catch (NotSupportedException)
            {
                return path;
            }
            catch (System.Security.SecurityException)
            {
                return path;
            }
        }

        /// <summary>
        /// Real <c>*.csproj</c> enumeration for <see cref="Run"/>. Ordinal-sorted so the
        /// ambiguity check is deterministic regardless of filesystem enumeration order, and
        /// best-effort: an unreadable or missing directory yields no candidates rather than
        /// aborting resolution of the remaining records.
        /// </summary>
        private static IReadOnlyList<string> EnumerateProjectFiles(string directory)
        {
            try
            {
                if (!Directory.Exists(directory))
                    return Array.Empty<string>();

                var files = Directory.GetFiles(directory, "*.csproj", SearchOption.TopDirectoryOnly);
                Array.Sort(files, StringComparer.Ordinal);
                return files;
            }
            catch (IOException)
            {
                return Array.Empty<string>();
            }
            catch (UnauthorizedAccessException)
            {
                return Array.Empty<string>();
            }
        }

        /// <summary>
        /// Real csproj read for <see cref="Run"/>; null on any I/O failure so an unreadable
        /// candidate is simply not a match.
        /// </summary>
        private static string? ReadFileTextOrNull(string path)
        {
            try
            {
                return File.ReadAllText(path);
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
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
