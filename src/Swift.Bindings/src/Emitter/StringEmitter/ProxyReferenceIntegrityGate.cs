// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace BindingsGeneration
{
    /// <summary>
    /// Fail-closed, structural post-emission net for protocol-proxy reference completeness: every
    /// bare <c>new {Protocol}Proxy(…)</c> construction a generated C# member emits MUST be backed by a
    /// <c>{Protocol}Proxy</c> class this generation actually emitted. A bare reference to a proxy
    /// class that was never emitted — because the protocol's proxy was suppressed (an unemitted
    /// EveryProtocol conformance, OR a member reaching an ingestion-quarantined / unsupported-module
    /// type) while a retained existential consumer survived — is a dangling reference that fails the
    /// C# compile with CS0246. This gate catches it at generation time and turns it into a hard
    /// non-zero exit.
    ///
    /// <para>
    /// This is the closure-completeness backstop for the proxy-suppression contract (defect b of the
    /// SwiftRichString <c>StyleProtocolProxy</c> regression). The suppression is SUPPOSED to be
    /// recorded in <see cref="ModuleEmissionContext.SuppressedProxyClassNames"/> so the consumer
    /// downgrade machinery (CONSUME arms drop the <c>static __v =&gt; new {P}Proxy(__v)</c> wrap;
    /// PRODUCE sites stub) fires and no bare construction is emitted. This gate verifies the OUTCOME
    /// independent of that mechanism: if a future non-emit proxy arm forgets to record its
    /// suppression — or any other path emits a bare construction of an unemitted proxy — the dangling
    /// reference is reconciled here and the module fails closed rather than shipping a binding that
    /// won't compile.
    /// </para>
    ///
    /// <para>
    /// Reconciliation is text-vs-text against the emitted SOURCE (cheap, compile-independent, honoring
    /// the single-render corpus-soak path that has no emitted-C# compile leg — the exact path that let
    /// the regression ship). DEFS are every <c>class {X}Proxy</c> an emitted <c>.cs</c> declares; REFS
    /// are every BARE <c>new {X}Proxy(</c> a generated <c>.cs</c> constructs. Bare-only scopes the gate
    /// to SAME-MODULE proxy construction, which is the regression's shape (SwiftRichString's
    /// <c>StyleXML</c> → <c>StyleProtocolProxy</c>, one module): a cross-module proxy reference is
    /// emitted namespace-qualified (<c>new global::Ns.SwiftInterop.{X}Proxy(</c> or the plain-dotted
    /// <c>new Ns.SwiftInterop.{X}Proxy(</c>), and the identifier group cannot span <c>::</c>/<c>.</c>,
    /// so every dotted form is excluded. That is deliberate: a cross-module proxy's defining class is
    /// NOT in this module's output (its suppression is instead reconciled downstream via the
    /// module-database's suppressed-proxy list), so a same-module text gate cannot and must not judge
    /// it. A bare construction is therefore provably same-generation and its defining class must be
    /// present in the same output. The scan is recursive over the whole output directory (co-emitted
    /// dependency wrappers under <c>dep-swift/</c> included), so each single-module generation is
    /// self-contained. Empirically the healthy corpus reconciles to zero.
    /// </para>
    /// </summary>
    internal static class ProxyReferenceIntegrityGate
    {
        // BARE `new {X}Proxy(` or `new {X}Proxy<…>(` — the construction forms. The `new\s+` anchor
        // followed immediately by the identifier means a qualified `new global::Ns.…{X}Proxy(` does
        // NOT match (the identifier group cannot span `::`/`.`), so cross-module references — which are
        // always emitted qualified — are excluded by construction. The identifier must END in `Proxy`
        // immediately before `(` or `<`, so `new FooProxyBuilder(` is not a false match.
        private static readonly Regex RefPattern = new(
            @"\bnew\s+([A-Za-z_][A-Za-z0-9_]*Proxy)\s*[(<]",
            RegexOptions.Compiled);

        // `class {X}Proxy` — the proxy class declaration form (e.g. `public unsafe partial class
        // FooProxy : IFoo, …` and its generic `class FooProxy<T> : …`). `Proxy` ends on a word
        // boundary so the generic-arg `<`, the base-list `:`, and trailing whitespace all delimit it.
        private static readonly Regex DefPattern = new(
            @"\bclass\s+([A-Za-z_][A-Za-z0-9_]*Proxy)\b",
            RegexOptions.Compiled);

        // A comment OR a string/char literal. BOTH are blanked before scanning: the CS0246 the gate
        // catches is a COMPILED `new {X}Proxy(` — never comment text (the generated fixtures document
        // exactly this pattern in `///` doc comments) and never a string literal's dead contents (a
        // proxy is a marshalling carrier, never constructed inside a string interpolation, so blanking
        // string bodies cannot hide a live construction). Blanking strings too removes the whole
        // false-positive surface — `"new GhostProxy(x)"`, `@"…"`, `$"…"` — that keeping them verbatim
        // would leave. String/char alternatives come FIRST so a `//` or `/*` inside a literal is consumed
        // as part of the literal, not misread as a comment start.
        private static readonly Regex CommentOrLiteralPattern = new(
            "@\"(?:[^\"]|\"\")*\"" +          // verbatim string  @"…"" …"
            "|\"(?:\\\\.|[^\"\\\\])*\"" +      // regular/interpolated string   "… \" …" / $"…"
            "|'(?:\\\\.|[^'\\\\])*'" +          // char literal     '…'
            "|//[^\n]*" +                       // line comment     // … (incl. /// doc)
            "|/\\*.*?\\*/",                     // block comment    /* … */
            RegexOptions.Compiled | RegexOptions.Singleline);

        // Blanks comments and string/char literals to spaces, so only live code text remains for the
        // def/ref scan. Preserves line structure loosely (a single space per literal/comment) — the
        // scan is whitespace-tolerant, so exact spacing is immaterial.
        private static string StripCommentsAndLiterals(string text) =>
            CommentOrLiteralPattern.Replace(text, " ");

        // Cap the number of individually-logged violations so a systemic break doesn't flood the log;
        // the count is always reported in full.
        private const int MaxLoggedSymbols = 25;

        /// <summary>
        /// Reconciles every emitted bare <c>new {X}Proxy(</c> reference against the emitted
        /// <c>class {X}Proxy</c> definitions under <paramref name="outputDirectory"/>. Returns
        /// <c>true</c> when a dangling reference exists (the caller must fail the generation), after
        /// logging each offending proxy as a <c>SWIFTBIND122</c> error. Returns <c>false</c> when every
        /// reference is satisfied. <paramref name="suppressedProxyClassNames"/> is used only to enrich
        /// the diagnostic — it names which dangling proxies were suppressed-but-not-downgraded (the
        /// completeness-machinery leak) versus dangling for an unrelated reason.
        /// </summary>
        public static bool HasViolations(
            string outputDirectory,
            IReadOnlyCollection<string>? suppressedProxyClassNames,
            ILogger logger)
        {
            if (string.IsNullOrEmpty(outputDirectory) || !Directory.Exists(outputDirectory))
                return false;

            var defined = new HashSet<string>();
            var referenced = new SortedSet<string>();
            var unreadable = new List<string>();

            foreach (var file in EnumerateSourceFiles(outputDirectory))
            {
                string text;
                try
                {
                    text = File.ReadAllText(file);
                }
                catch (System.Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Fail closed, honoring this gate's contract: an emitted artifact we cannot read
                    // might carry the ONLY dangling `new {X}Proxy(…)` construction, so silently skipping
                    // it (the former `catch (IOException) { continue; }`) would let the very regression
                    // this net exists to catch ship unseen. A permission error surfaces as
                    // UnauthorizedAccessException — NOT an IOException — so the narrower catch also let it
                    // crash the generator ungracefully; both now become a reported violation.
                    unreadable.Add(Path.GetFileName(file));
                    continue;
                }

                // Blank comments and string/char literals first so a `new {X}Proxy(` written in a doc
                // comment (the generated fixtures carry exactly such prose) or sitting as dead text in a
                // string literal is not read as a live construction.
                text = StripCommentsAndLiterals(text);

                foreach (Match m in DefPattern.Matches(text))
                    defined.Add(m.Groups[1].Value);
                foreach (Match m in RefPattern.Matches(text))
                    referenced.Add(m.Groups[1].Value);
            }

            var dangling = referenced.Where(r => !defined.Contains(r)).ToList();
            if (dangling.Count == 0 && unreadable.Count == 0)
                return false;

            if (unreadable.Count > 0)
            {
                logger.LogError(
                    "SWIFTBIND122: {Count} emitted C# source file(s) could not be read to verify " +
                    "proxy-reference completeness. Failing closed: an unreadable emitted artifact may " +
                    "carry a dangling `new {{X}}Proxy(…)` construction, so it is treated as a violation " +
                    "rather than assumed clean.",
                    unreadable.Count);
                foreach (var file in unreadable.Take(MaxLoggedSymbols))
                    logger.LogError("SWIFTBIND122:   unreadable emitted source '{File}'", file);
                if (unreadable.Count > MaxLoggedSymbols)
                    logger.LogError("SWIFTBIND122:   … and {More} more.", unreadable.Count - MaxLoggedSymbols);
            }

            if (dangling.Count == 0)
                return true;

            var suppressedSet = suppressedProxyClassNames as ISet<string>
                ?? new HashSet<string>(suppressedProxyClassNames ?? System.Array.Empty<string>());

            logger.LogError(
                "SWIFTBIND122: {Count} generated C# member(s) construct a `new {{X}}Proxy(…)` whose " +
                "`{{X}}Proxy` class no emitted C# defines — a dangling proxy reference that fails the " +
                "binding compile with CS0246. The protocol's proxy was suppressed but a retained " +
                "existential consumer was not downgraded; the suppression must be recorded so the " +
                "consumer drops the wrap fallback, or the consumer must be withdrawn.",
                dangling.Count);

            foreach (var proxy in dangling.Take(MaxLoggedSymbols))
            {
                var suppressedNote = suppressedSet.Contains(proxy)
                    ? " (recorded suppressed but a consumer still constructs it — downgrade machinery leak)"
                    : " (not in the suppressed set — an unrecorded suppression or unrelated dangling reference)";
                logger.LogError("SWIFTBIND122:   undefined proxy class '{Proxy}'{Note}", proxy, suppressedNote);
            }

            if (dangling.Count > MaxLoggedSymbols)
                logger.LogError("SWIFTBIND122:   … and {More} more.", dangling.Count - MaxLoggedSymbols);

            return true;
        }

        private static IEnumerable<string> EnumerateSourceFiles(string outputDirectory)
        {
            foreach (var file in Directory.EnumerateFiles(outputDirectory, "*.cs", SearchOption.AllDirectories))
            {
                // Skip build intermediates — a previously-built output dir may carry obj/bin trees
                // whose generated shims duplicate declarations (harmless, but pure scan cost).
                var rel = Path.GetRelativePath(outputDirectory, file);
                if (rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                        .Any(seg => seg is "obj" or "bin"))
                    continue;

                yield return file;
            }
        }
    }
}
