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
    internal static class NativeSymbolProbe
    {
        private const string ObjCClassSymbolPrefix = "_OBJC_CLASS_$_";

        /// <summary>
        /// Result of probing one or more native binaries for defined ObjC class symbols.
        /// <see cref="GatheredEvidence"/> is false when no binary could be read (every
        /// <c>nm</c> invocation failed or no binary existed) — callers must fail open and
        /// not filter, because absence of evidence is not evidence of absence.
        /// </summary>
        public readonly record struct ObjCClassSymbolScan(
            IReadOnlySet<string> DefinedClassNames,
            bool GatheredEvidence);

        /// <summary>
        /// Runs <c>nm -gU</c> on each binary path that exists, unions the
        /// <c>_OBJC_CLASS_$_&lt;Name&gt;</c> symbols across all of them, and returns the
        /// set of defined ObjC class names plus whether any binary was successfully read.
        /// Non-existent paths and per-binary <c>nm</c> failures are skipped (logged at
        /// debug), so a multi-slice / multi-dependency union degrades gracefully to the
        /// binaries that resolve.
        /// </summary>
        public static ObjCClassSymbolScan ScanObjCClassSymbols(
            IEnumerable<string> binaryPaths, ICommandRunner commandRunner, ILogger logger)
        {
            var classNames = new HashSet<string>(StringComparer.Ordinal);
            var gathered = false;
            foreach (var path in binaryPaths)
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    continue;
                }
                var symbols = ReadDefinedSymbols(path, commandRunner, logger);
                if (symbols == null)
                {
                    continue; // nm failed on this binary — keep the others
                }
                gathered = true;
                foreach (var sym in symbols)
                {
                    if (sym.StartsWith(ObjCClassSymbolPrefix, StringComparison.Ordinal))
                    {
                        classNames.Add(sym.Substring(ObjCClassSymbolPrefix.Length));
                    }
                }
            }
            return new ObjCClassSymbolScan(classNames, gathered);
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
        /// (externally-referenced) symbol names across all of them. The returned flag mirrors
        /// <see cref="ObjCClassSymbolScan.GatheredEvidence"/>: false means every probe failed
        /// (or no binary existed), so callers must not read an empty set as "no undefined
        /// symbols". Non-existent paths and per-binary <c>nm</c> failures are skipped (logged at
        /// debug). Used to complete the system-framework link-failure hint independently of how
        /// much of its undefined-symbol list the linker chose to print.
        /// </summary>
        public static (IReadOnlySet<string> UndefinedSymbols, bool GatheredEvidence) ScanUndefinedSymbols(
            IEnumerable<string> binaryPaths, ICommandRunner commandRunner, ILogger logger)
        {
            var symbols = new HashSet<string>(StringComparer.Ordinal);
            var gathered = false;
            foreach (var path in binaryPaths)
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    continue;
                }
                var found = ReadUndefinedSymbols(path, commandRunner, logger);
                if (found == null)
                {
                    continue; // nm failed on this binary — keep the others
                }
                gathered = true;
                foreach (var sym in found)
                {
                    symbols.Add(sym);
                }
            }
            return (symbols, gathered);
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
