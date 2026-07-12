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
    /// Fail-closed, structural post-emission net: every wrapper entry point a generated C#
    /// P/Invoke targets (<c>SBW_</c> @_cdecl / <c>SBSW_</c> Swift-CC) MUST be defined by a wrapper
    /// function this generation actually emitted. A reference to an undefined wrapper symbol is a
    /// generator defect that would surface at runtime as an <c>EntryPointNotFoundException</c> /
    /// <c>DllNotFoundException</c> — it is caught here at generation time and turned into a hard
    /// non-zero exit (the <c>--strict</c> / <c>--compile-only</c> seam already hard-fails on a
    /// non-zero generator exit).
    ///
    /// <para>
    /// This is the durable regression net for the in-band wrapper-symbol contract: the in-band
    /// gate (<see cref="WrapperSymbolContractGate"/> / <see cref="PInvokeEmitHelper"/>) only fires
    /// per-emit when <c>EnforceWrapperContract</c> is set, so a P/Invoke emitted on a path that
    /// leaves the flag default-false (a proxy accessor, a helper, a future emitter) can still ship
    /// a dangling reference. This gate reconciles the FINAL emitted text after all emission,
    /// independent of the per-emit flag.
    /// </para>
    ///
    /// <para>
    /// Reconciliation is text-vs-text against the emitted SOURCE, not a compiled symbol table
    /// (<c>nm</c>) — cheaper and compile-independent, honoring the session's inner-loop constraint;
    /// the compile-strip channel covers binary truth separately. DEFS are every symbol a wrapper
    /// <c>.swift</c> defines via <c>@_cdecl("SBW_…")</c> / <c>@_silgen_name("SB(S)W_…")</c>; REFS
    /// are every <c>EntryPoint = "SB(S)W_…"</c> a generated <c>.cs</c> P/Invoke names. The scan is
    /// over the whole output directory (recursively, so a co-emitted dependency wrapper under
    /// <c>dep-swift/</c> is included), which makes each single-module generation self-contained:
    /// a module's C# only references symbols its own emitted wrappers define, and a co-emitted
    /// dependency's defs are present too. Empirically the healthy corpus reconciles to zero.
    /// </para>
    /// </summary>
    internal static class WrapperSymbolIntegrityGate
    {
        // EntryPoint = "SBW_…" / "SBSW_…" — tolerant of spacing and of both the LibraryImport
        // (`EntryPoint = "…"`) and generated-DllImport (`EntryPoint="…"`) spellings.
        private static readonly Regex RefPattern = new(
            "EntryPoint\\s*=\\s*\"((?:SBW_|SBSW_)[A-Za-z0-9_]+)\"",
            RegexOptions.Compiled);

        // @_cdecl("SBW_…") / @_silgen_name("SBW_…"|"SBSW_…") — the wrapper-side definition forms.
        private static readonly Regex DefPattern = new(
            "@_(?:cdecl|silgen_name)\\s*\\(\\s*\"((?:SBW_|SBSW_)[A-Za-z0-9_]+)\"",
            RegexOptions.Compiled);

        // Cap the number of individually-logged violations so a systemic break doesn't flood the
        // log; the count is always reported in full.
        private const int MaxLoggedSymbols = 25;

        /// <summary>
        /// Reconciles every emitted wrapper-symbol P/Invoke reference against the emitted wrapper
        /// definitions under <paramref name="outputDirectory"/>. Returns <c>true</c> when a
        /// dangling reference exists (the caller must fail the generation), after logging each
        /// offending symbol as a <c>SWIFTBIND108</c> error. Returns <c>false</c> when every
        /// reference is satisfied.
        /// </summary>
        public static bool HasViolations(string outputDirectory, ILogger logger)
        {
            if (string.IsNullOrEmpty(outputDirectory) || !Directory.Exists(outputDirectory))
                return false;

            var defined = new HashSet<string>();
            var referenced = new SortedSet<string>();

            foreach (var file in EnumerateSourceFiles(outputDirectory))
            {
                string text;
                try
                {
                    text = File.ReadAllText(file);
                }
                catch (IOException)
                {
                    continue;
                }

                if (file.EndsWith(".swift", System.StringComparison.Ordinal))
                {
                    foreach (Match m in DefPattern.Matches(text))
                        defined.Add(m.Groups[1].Value);
                }
                else // .cs
                {
                    foreach (Match m in RefPattern.Matches(text))
                        referenced.Add(m.Groups[1].Value);
                }
            }

            var dangling = referenced.Where(r => !defined.Contains(r)).ToList();
            if (dangling.Count == 0)
                return false;

            logger.LogError(
                "SWIFTBIND108: {Count} generated C# P/Invoke(s) reference a wrapper symbol that no " +
                "emitted Swift wrapper defines — a dangling entry point that would throw " +
                "EntryPointNotFoundException at runtime. This is a generator defect (a member was " +
                "planned against a wrapper symbol that was never emitted); the member must be " +
                "skipped at planning time, or its wrapper emitted.",
                dangling.Count);

            foreach (var symbol in dangling.Take(MaxLoggedSymbols))
                logger.LogError("SWIFTBIND108:   undefined wrapper symbol '{Symbol}'", symbol);

            if (dangling.Count > MaxLoggedSymbols)
                logger.LogError("SWIFTBIND108:   … and {More} more.", dangling.Count - MaxLoggedSymbols);

            return true;
        }

        private static IEnumerable<string> EnumerateSourceFiles(string outputDirectory)
        {
            foreach (var file in Directory.EnumerateFiles(outputDirectory, "*.*", SearchOption.AllDirectories))
            {
                if (!file.EndsWith(".cs", System.StringComparison.Ordinal)
                    && !file.EndsWith(".swift", System.StringComparison.Ordinal))
                    continue;

                // Skip build intermediates — a previously-built output dir may carry obj/bin
                // trees whose generated LibraryImport shims duplicate the same symbols (harmless,
                // but pure scan cost).
                var rel = Path.GetRelativePath(outputDirectory, file);
                if (rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                        .Any(seg => seg is "obj" or "bin"))
                    continue;

                yield return file;
            }
        }
    }
}
