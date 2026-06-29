// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

namespace BindingsGeneration
{
    /// <summary>
    /// Reads symbols from native binaries (Mach-O dylibs, static <c>ar</c>
    /// archives, object files) via <c>nm</c>. Reads <em>defined</em> symbols
    /// (<c>nm -gU</c>) for the TBD-synthesis path (Swift static archives) and the
    /// ObjC over-binding guard, and <em>undefined</em> symbols (<c>nm -u</c>) to
    /// complete the system-framework link-failure hint — so the tricky <c>nm</c>
    /// output parsing lives in one place.
    /// </summary>
    /// <summary>
    /// Tri-state outcome of a native-symbol probe over a set of candidate binaries
    /// (Finding 63). Distinguishes three situations a single "did we gather evidence?"
    /// boolean conflated:
    /// <list type="bullet">
    /// <item><see cref="Gathered"/> — at least one candidate binary was read successfully.
    /// The returned symbol set is real (though it may legitimately be empty).</item>
    /// <item><see cref="AllFailed"/> — one or more candidate binaries existed but
    /// <em>every</em> <c>nm</c> invocation failed. This is a <em>systemic</em> failure (e.g.
    /// <c>nm</c> unavailable or its output format changed), not "no evidence": callers that
    /// would otherwise fail open must instead fail loud, because a probe that silently
    /// gathers nothing degrades to the same code path as "everything is fine".</item>
    /// <item><see cref="NothingToProbe"/> — no candidate binary existed to probe. Genuinely
    /// nothing to do; callers fail open.</item>
    /// </list>
    /// </summary>
    public enum NativeSymbolProbeOutcome
    {
        /// <summary>At least one candidate binary was read successfully.</summary>
        Gathered,

        /// <summary>Candidate binaries existed but every <c>nm</c> invocation failed (systemic).</summary>
        AllFailed,

        /// <summary>No candidate binary existed to probe.</summary>
        NothingToProbe,
    }

    internal static class NativeSymbolProbe
    {
        private const string ObjCClassSymbolPrefix = "_OBJC_CLASS_$_";

        /// <summary>
        /// Computes the tri-state <see cref="NativeSymbolProbeOutcome"/> from the number of
        /// candidate binaries that existed on disk versus the number <c>nm</c> read successfully.
        /// </summary>
        private static NativeSymbolProbeOutcome ClassifyOutcome(int existed, int read) =>
            existed == 0 ? NativeSymbolProbeOutcome.NothingToProbe
            : read == 0 ? NativeSymbolProbeOutcome.AllFailed
            : NativeSymbolProbeOutcome.Gathered;

        /// <summary>
        /// A candidate path is probeable only if it names an existing, non-empty file. A zero-byte
        /// file is not a Mach-O/archive binary: <c>nm</c> will always fail on it, and that failure
        /// is not evidence of a systemic toolchain breakage — so it must NOT count toward the
        /// "existed" tally that drives <see cref="NativeSymbolProbeOutcome.AllFailed"/>. Skipping it
        /// keeps an empty placeholder slice from masquerading as a broken probe (a false-positive
        /// SWIFTBIND028); a present-but-corrupt <em>non-empty</em> binary that <c>nm</c> cannot read
        /// still counts and still trips <see cref="NativeSymbolProbeOutcome.AllFailed"/> as intended.
        /// </summary>
        private static bool IsProbeableBinaryFile(string path) =>
            !string.IsNullOrEmpty(path) && File.Exists(path) && new FileInfo(path).Length > 0;

        /// <summary>
        /// Result of probing one or more native binaries for defined ObjC class symbols.
        /// <see cref="Outcome"/> is the tri-state evidence verdict: callers must fail open on
        /// <see cref="NativeSymbolProbeOutcome.NothingToProbe"/> (absence of evidence is not
        /// evidence of absence) but fail loud on <see cref="NativeSymbolProbeOutcome.AllFailed"/>
        /// (a systemic probe breakage, not a clean "no binaries" result).
        /// </summary>
        public readonly record struct ObjCClassSymbolScan(
            IReadOnlySet<string> DefinedClassNames,
            IReadOnlySet<string> DefinedSymbols,
            NativeSymbolProbeOutcome Outcome);

        /// <summary>
        /// Runs <c>nm -gU</c> on each binary path that exists, unions the
        /// <c>_OBJC_CLASS_$_&lt;Name&gt;</c> symbols across all of them, and returns the
        /// set of defined ObjC class names plus the tri-state probe <see cref="NativeSymbolProbeOutcome"/>.
        /// Non-existent and zero-byte paths are skipped silently (see <see cref="IsProbeableBinaryFile"/>);
        /// per-binary <c>nm</c> failures are logged at
        /// debug and skipped, so a multi-slice / multi-dependency union degrades gracefully to the
        /// binaries that resolve — but if binaries existed and <em>all</em> of them failed, the
        /// outcome is <see cref="NativeSymbolProbeOutcome.AllFailed"/> so the caller can fail loud
        /// rather than mistake a systemic <c>nm</c> breakage for "no symbols".
        /// </summary>
        public static ObjCClassSymbolScan ScanObjCClassSymbols(
            IEnumerable<string> binaryPaths, ICommandRunner commandRunner, ILogger logger)
        {
            var classNames = new HashSet<string>(StringComparer.Ordinal);
            var definedSymbols = new HashSet<string>(StringComparer.Ordinal);
            var existed = 0;
            var read = 0;
            foreach (var path in binaryPaths)
            {
                if (!IsProbeableBinaryFile(path))
                {
                    continue;
                }
                existed++;
                var symbols = ReadDefinedSymbols(path, commandRunner, logger);
                if (symbols == null)
                {
                    continue; // nm failed on this binary — keep the others
                }
                read++;
                foreach (var sym in symbols)
                {
                    // Retain every defined symbol verbatim (with its leading `_`) so the free-symbol
                    // guard can test whether a header-declared C function or extern global is actually
                    // exported, while still extracting the `_OBJC_CLASS_$_<Name>` class names.
                    definedSymbols.Add(sym);
                    if (sym.StartsWith(ObjCClassSymbolPrefix, StringComparison.Ordinal))
                    {
                        classNames.Add(sym.Substring(ObjCClassSymbolPrefix.Length));
                    }
                }
            }
            return new ObjCClassSymbolScan(classNames, definedSymbols, ClassifyOutcome(existed, read));
        }

        /// <summary>
        /// Runs <c>nm -gU</c> on a single binary and returns its defined, external symbol
        /// names, or null if <c>nm</c> failed. No <c>-arch</c> filter: a fat <c>ar</c>
        /// archive only lists its <c>_OBJC_CLASS_$_*</c> symbols when <c>nm</c> reads every
        /// member — an explicit <c>-arch</c> on a fat archive can return zero classes.
        /// </summary>
        public static List<string>? ReadDefinedSymbols(
            string binaryPath, ICommandRunner commandRunner, ILogger logger)
        {
            try
            {
                var (exitCode, stdout, stderr) = commandRunner.Run(
                    "nm",
                    $"-gU \"{binaryPath}\"",
                    timeoutMs: 60000);
                if (exitCode != 0)
                {
                    logger.LogDebug(
                        "nm -gU failed for '{Path}' (exit {Exit}): {Err}",
                        binaryPath, exitCode, stderr);
                    return null;
                }
                return ParseNmSymbols(stdout);
            }
            catch (Exception ex)
            {
                logger.LogDebug("nm -gU threw for '{Path}': {Message}", binaryPath, ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Runs <c>nm -u</c> on each existing binary path and unions the undefined
        /// (externally-referenced) symbol names across all of them. The returned
        /// <see cref="NativeSymbolProbeOutcome"/> mirrors <see cref="ObjCClassSymbolScan.Outcome"/>:
        /// <see cref="NativeSymbolProbeOutcome.NothingToProbe"/> means no binary existed and
        /// <see cref="NativeSymbolProbeOutcome.AllFailed"/> means every probe failed, so callers
        /// must not read an empty set as "no undefined symbols". Non-existent and zero-byte paths
        /// (see <see cref="IsProbeableBinaryFile"/>) and per-binary
        /// <c>nm</c> failures are skipped (logged at debug). Used to complete the system-framework
        /// link-failure hint independently of how much of its undefined-symbol list the linker
        /// chose to print — this consumer stays advisory and acts only on
        /// <see cref="NativeSymbolProbeOutcome.Gathered"/>.
        /// </summary>
        public static (IReadOnlySet<string> UndefinedSymbols, NativeSymbolProbeOutcome Outcome) ScanUndefinedSymbols(
            IEnumerable<string> binaryPaths, ICommandRunner commandRunner, ILogger logger)
        {
            var symbols = new HashSet<string>(StringComparer.Ordinal);
            var existed = 0;
            var read = 0;
            foreach (var path in binaryPaths)
            {
                if (!IsProbeableBinaryFile(path))
                {
                    continue;
                }
                existed++;
                var found = ReadUndefinedSymbols(path, commandRunner, logger);
                if (found == null)
                {
                    continue; // nm failed on this binary — keep the others
                }
                read++;
                foreach (var sym in found)
                {
                    symbols.Add(sym);
                }
            }
            return (symbols, ClassifyOutcome(existed, read));
        }

        /// <summary>
        /// Runs <c>nm -u</c> on a single binary and returns its undefined, externally-referenced
        /// symbol names, or null if <c>nm</c> failed. No <c>-arch</c> filter (same rationale as
        /// <see cref="ReadDefinedSymbols"/>): a fat <c>ar</c> archive must be read whole.
        /// </summary>
        public static List<string>? ReadUndefinedSymbols(
            string binaryPath, ICommandRunner commandRunner, ILogger logger)
        {
            try
            {
                var (exitCode, stdout, stderr) = commandRunner.Run(
                    "nm",
                    $"-u \"{binaryPath}\"",
                    timeoutMs: 60000);
                if (exitCode != 0)
                {
                    logger.LogDebug(
                        "nm -u failed for '{Path}' (exit {Exit}): {Err}",
                        binaryPath, exitCode, stderr);
                    return null;
                }
                return ParseNmUndefinedSymbols(stdout);
            }
            catch (Exception ex)
            {
                logger.LogDebug("nm -u threw for '{Path}': {Message}", binaryPath, ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Parses undefined symbol names out of <c>nm -u</c> output. Unlike <c>nm -gU</c>, the
        /// <c>-u</c> form omits the address/type columns and prints bare names (with interspersed
        /// <c>member.o:</c> headers and blank lines), though some <c>nm</c> builds prefix a single
        /// <c>U</c> type code. Undefined symbol names carry no embedded whitespace, so taking the
        /// last whitespace-delimited token parses both shapes. Header/blank lines are skipped and
        /// names de-duplicated (a symbol can be undefined across many member objects).
        /// </summary>
        internal static List<string> ParseNmUndefinedSymbols(string nmOutput)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<string>();
            foreach (var raw in nmOutput.Split('\n'))
            {
                var line = raw.Trim();
                if (string.IsNullOrEmpty(line))
                {
                    continue;
                }
                if (line.EndsWith(":", StringComparison.Ordinal))
                {
                    continue; // member-object header (e.g. "foo.o:")
                }
                var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0)
                {
                    continue; // defensive: a non-empty trimmed line always yields a token today,
                              // but this is an error-path builder — degrade, don't throw.
                }
                var name = parts[parts.Length - 1];
                if (seen.Add(name))
                {
                    result.Add(name);
                }
            }
            return result;
        }

        /// <summary>
        /// Parses defined symbol names out of <c>nm -gU</c> output.
        /// </summary>
        internal static List<string> ParseNmSymbols(string nmOutput)
        {
            // `nm -gU` on an archive emits per-object headers (`Foo-1.o:`)
            // followed by `<hex>  <type>  <name>` rows. The name field can
            // legitimately contain whitespace — Swift's reflection metadata
            // surfaces entries like `_symbolic SS` and
            // `_symbolic _____ 14Module0A8TypeV` — so we cannot just take the
            // last token. Skip address (run of non-whitespace), skip the
            // single-char type code, then take the rest of the line as the
            // symbol name. Dedup because archives may repeat a symbol across
            // member objects (linkonce/coalesced/etc.).
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<string>();
            foreach (var raw in nmOutput.Split('\n'))
            {
                var line = raw.TrimEnd();
                if (string.IsNullOrEmpty(line))
                {
                    continue;
                }
                if (line.EndsWith(":"))
                {
                    continue; // object-file header
                }
                var name = ExtractNmSymbolName(line);
                if (name == null)
                {
                    continue;
                }
                if (seen.Add(name))
                {
                    result.Add(name);
                }
            }
            return result;
        }

        private static string? ExtractNmSymbolName(string line)
        {
            int i = 0;
            // Leading whitespace (defensive — TrimStart equivalent).
            while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
            // Address column (run of non-whitespace; may be empty for
            // undefined symbols, but `-U` excludes those).
            while (i < line.Length && !char.IsWhiteSpace(line[i])) i++;
            // Whitespace separator.
            while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
            // Type column — exactly one non-whitespace character.
            if (i >= line.Length) return null;
            i++;
            // Whitespace separator.
            while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
            // Remainder is the symbol name (may contain spaces).
            if (i >= line.Length) return null;
            return line.Substring(i);
        }
    }
}
