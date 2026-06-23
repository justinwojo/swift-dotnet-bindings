// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace BindingsGeneration
{
    /// <summary>
    /// Holds the result of simulator-only member detection.
    /// Carries both human-readable qualified names (for wrapper comment matching)
    /// and ABI mangled name hashes (for precise overload-aware matching in @_cdecl and thunk symbols).
    /// </summary>
    public sealed class SimulatorOnlyResult
    {
        internal record MemberEntry(string QualifiedName, string? Hash);

        internal readonly List<MemberEntry> _entries = new();
        private readonly HashSet<string> _qualifiedNames = new(StringComparer.Ordinal);

        /// <summary>Qualified names like "TypeName.memberName" for wrapper comment matching.</summary>
        public IReadOnlySet<string> QualifiedNames => _qualifiedNames;

        /// <summary>Number of simulator-only members detected.</summary>
        public int Count => _qualifiedNames.Count;

        internal void Add(string qualifiedName, string patchedMangledName)
        {
            _qualifiedNames.Add(qualifiedName);
            string? hash = !string.IsNullOrEmpty(patchedMangledName)
                ? EmitterUtility.DeterministicHash8(patchedMangledName)
                : null;
            _entries.Add(new MemberEntry(qualifiedName, hash));
        }

        /// <summary>
        /// Checks if a @_cdecl wrapper block matches a simulator-only member.
        /// Uses hash matching for precise overload identification when available,
        /// falls back to qualified name matching for members without mangled names (e.g., properties).
        /// </summary>
        internal bool MatchesCdeclBlock(string qualifiedName, string? cdeclLine)
        {
            foreach (var entry in _entries)
            {
                if (entry.QualifiedName != qualifiedName)
                    continue;

                if (entry.Hash != null && cdeclLine != null)
                {
                    // Precise: check hash in @_cdecl line (uppercase in wrapper name)
                    if (cdeclLine.Contains(entry.Hash, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                else if (entry.Hash == null)
                {
                    // No hash available — name match is sufficient (properties, etc.)
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Checks if a thunk assembly block matches a simulator-only member.
        /// Uses hash matching for precision (the hash appears in the .globl line as lowercase hex).
        /// Falls back to (typeName, memberName) pair matching for members without mangled names.
        /// </summary>
        internal bool MatchesThunkBlock(string blockText)
        {
            foreach (var entry in _entries)
            {
                if (entry.Hash != null)
                {
                    // Precise: hash appears in thunk .globl line (lowercase) or target symbol
                    if (blockText.Contains(entry.Hash, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                else
                {
                    // Fallback for members without a mangled-name hash (e.g. properties):
                    // token-aware, ADJACENCY-aware matching using Swift name-mangling
                    // conventions. See MatchesByMangledName.
                    if (MatchesByMangledName(blockText, entry.QualifiedName))
                        return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Token-aware, adjacency-aware match of a dotted qualified name ("Type.member",
        /// "Outer.Inner.member") against a thunk block's mangled symbols.
        /// </summary>
        /// <remarks>
        /// Swift mangles a member symbol as its nominal parts followed by the member, each as a
        /// length-prefixed token (<c>{len}{name}</c>) and separated ONLY by nominal-kind /
        /// substitution-terminator characters — single uppercase letters such as the <c>C</c>/<c>O</c>
        /// type-kind suffix (e.g. <c>…3FooC2idSivg</c> for <c>Foo.id</c>). The parts must therefore
        /// appear ADJACENT inside a single symbol, not merely be present somewhere in the block.
        ///
        /// Requiring adjacency is what stops a sim-only PROPERTY (<c>Card.id</c> → <c>4Card</c>,
        /// <c>2id</c>) from false-matching a SIBLING METHOD's thunk on the same type
        /// (<c>…4CardC4find2id…</c> for <c>find(id:)</c>), where the same two tokens are present but
        /// separated by an intervening length-prefixed method name (<c>4find</c>). That false match
        /// would strip a live DEVICE thunk and surface as <c>EntryPointNotFoundException</c> on device
        /// (audit Regression-R6, finding #2). The token-fallback itself stays load-bearing — the
        /// property's OWN accessor thunk must still be matched and removed for the device slice — so
        /// this tightens precision rather than removing the fallback.
        /// </remarks>
        private static bool MatchesByMangledName(string blockText, string qualifiedName)
        {
            if (qualifiedName.IndexOf('.') < 0)
                return false;

            var parts = qualifiedName.Split('.');

            // Anchor on every occurrence of the first part (literal length-prefixed form OR a
            // substitution-compressed suffix), then require the remaining parts to follow
            // adjacently — separated only by Swift connective (uppercase) characters.
            for (int i = 0; i < blockText.Length; i++)
            {
                int anchorLen = MatchPartAt(blockText, i, parts[0]);
                if (anchorLen < 0)
                    continue;
                if (RemainingPartsFollowAdjacently(blockText, parts, anchorEnd: i + anchorLen))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Verifies that parts[1..] mangle immediately after <paramref name="anchorEnd"/>, each
        /// separated from the previous by Swift connective characters only (single uppercase
        /// nominal-kind / substitution terminators). A digit or lowercase letter in a gap signals
        /// an intervening length-prefixed identifier (e.g. a sibling method name) — not an adjacent
        /// member access — so the tokens do not form one member symbol.
        /// </summary>
        private static bool RemainingPartsFollowAdjacently(string blockText, string[] parts, int anchorEnd)
        {
            int pos = anchorEnd;
            for (int p = 1; p < parts.Length; p++)
            {
                while (pos < blockText.Length && blockText[pos] >= 'A' && blockText[pos] <= 'Z')
                    pos++;
                int len = MatchPartAt(blockText, pos, parts[p]);
                if (len < 0)
                    return false;
                pos += len;
            }
            return true;
        }

        /// <summary>
        /// Returns the matched token length if <paramref name="part"/> mangles starting exactly at
        /// <paramref name="pos"/> in <paramref name="blockText"/>, else -1. Tries the literal
        /// <c>{len}{name}</c> form first, then a substitution-compressed suffix <c>{len}{suffix}</c>
        /// (suffix ≥ 5 chars) — the latter only when immediately preceded by an uppercase
        /// substitution-index terminator, confirming a real Swift substitution rather than a
        /// coincidental shared suffix.
        /// </summary>
        private static int MatchPartAt(string blockText, int pos, string part)
        {
            if (pos < 0 || pos >= blockText.Length)
                return -1;

            var lengthPrefixed = $"{part.Length}{part}";
            if (HasSubstringAt(blockText, pos, lengthPrefixed))
                return lengthPrefixed.Length;

            for (int len = part.Length - 1; len >= 5; len--)
            {
                var suffix = part.Substring(part.Length - len);
                var suffixPrefixed = $"{len}{suffix}";
                if (HasSubstringAt(blockText, pos, suffixPrefixed)
                    && pos > 0
                    && blockText[pos - 1] >= 'A' && blockText[pos - 1] <= 'Z')
                {
                    return suffixPrefixed.Length;
                }
            }
            return -1;
        }

        private static bool HasSubstringAt(string text, int pos, string value)
        {
            if (pos + value.Length > text.Length)
                return false;
            return string.CompareOrdinal(text, pos, value, 0, value.Length) == 0;
        }
    }

    /// <summary>
    /// Detects Swift members that exist only in the simulator slice of an xcframework.
    /// These members are behind #if targetEnvironment(simulator) in the Swift source.
    /// The wrapper Swift file must guard @_cdecl functions for these members so the
    /// device slice compiles successfully.
    /// </summary>
    public static class SimulatorOnlyMemberDetector
    {
        /// <summary>
        /// Compares simulator and device ABI JSON files to find members that exist only
        /// in the simulator slice. Returns qualified member names and mangled name hashes
        /// for precise overload-aware matching.
        /// </summary>
        public static SimulatorOnlyResult Detect(
            string simulatorAbiJsonPath,
            string? deviceAbiJsonPath,
            ILogger logger)
        {
            if (string.IsNullOrEmpty(deviceAbiJsonPath) || !File.Exists(deviceAbiJsonPath))
                return new SimulatorOnlyResult();

            if (!File.Exists(simulatorAbiJsonPath))
                return new SimulatorOnlyResult();

            try
            {
                var simMap = ExtractMembers(simulatorAbiJsonPath);
                var deviceKeys = new HashSet<string>(
                    ExtractMembers(deviceAbiJsonPath).Keys, StringComparer.Ordinal);

                // Diff on mangledName keys (unique per overload), then collect results
                var result = new SimulatorOnlyResult();
                foreach (var (key, (qualifiedName, patchedMangledName)) in simMap)
                {
                    if (!deviceKeys.Contains(key))
                        result.Add(qualifiedName, patchedMangledName);
                }

                if (result.Count > 0)
                {
                    logger.LogInformation("Detected {Count} simulator-only member(s): {Members}",
                        result.Count, string.Join(", ", result.QualifiedNames));
                }

                return result;
            }
            catch (Exception ex)
            {
                logger.LogWarning("Failed to detect simulator-only members: {Message}", ex.Message);
                return new SimulatorOnlyResult();
            }
        }

        /// <summary>
        /// Extracts a map of (key → (qualifiedName, patchedMangledName)) from an ABI JSON file.
        /// Uses mangledName as key to disambiguate overloaded members with the same name.
        /// Applies constructor mangled name patching (c→C) to match the generator's convention.
        /// Extracts Var, Function, and Constructor declarations that are children of type declarations.
        /// </summary>
        private static Dictionary<string, (string QualifiedName, string PatchedMangledName)> ExtractMembers(string abiJsonPath)
        {
            var members = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
            using var stream = File.OpenRead(abiJsonPath);
            using var doc = JsonDocument.Parse(stream);

            var root = doc.RootElement;
            if (root.TryGetProperty("ABIRoot", out var abiRoot))
                root = abiRoot;

            if (root.TryGetProperty("children", out var topChildren))
            {
                foreach (var child in topChildren.EnumerateArray())
                    WalkNode(child, "", members);
            }

            return members;
        }

        /// <summary>
        /// Recursively walks ABI JSON nodes, collecting (mangledName → (qualifiedName, patchedMangledName)) entries
        /// for Var, Function, and Constructor members.
        /// </summary>
        private static void WalkNode(JsonElement node, string parentType, Dictionary<string, (string QualifiedName, string PatchedMangledName)> members)
        {
            var kind = node.TryGetProperty("kind", out var k) ? k.GetString() ?? "" : "";
            var name = node.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";

            var currentParent = parentType;
            if (kind == "TypeDecl" && !string.IsNullOrEmpty(name))
            {
                currentParent = string.IsNullOrEmpty(parentType) ? name : $"{parentType}.{name}";
            }

            if ((kind == "Var" || kind == "Function" || kind == "Constructor") && !string.IsNullOrEmpty(currentParent) && !string.IsNullOrEmpty(name))
            {
                var qualifiedName = $"{currentParent}.{name}";
                var mangledName = node.TryGetProperty("mangledName", out var m) ? m.GetString() ?? "" : "";

                // Apply constructor mangled name patching to match the generator's convention.
                // Swift ABI JSON uses lowercase 'c' suffix for designated constructors, but the
                // generator patches it to uppercase 'C' (allocating) before computing hashes.
                var patchedMangledName = mangledName;
                if (kind == "Constructor" && patchedMangledName.Length > 0 && patchedMangledName[^1] == 'c')
                    patchedMangledName = patchedMangledName[..^1] + "C";

                // Property Var nodes have mangledNames on the Var node itself, but property
                // @_cdecl wrappers use name-based naming (SBW_Get_/SBW_Set_) without including
                // the Var's mangledName hash. Clear the hash for Var entries so MatchesCdeclBlock
                // uses name-only fallback matching, which correctly matches property wrappers.
                if (kind == "Var")
                    patchedMangledName = "";

                // Use mangledName as key to disambiguate overloads; fall back to qualifiedName
                var key = !string.IsNullOrEmpty(mangledName) ? mangledName : qualifiedName;
                members.TryAdd(key, (qualifiedName, patchedMangledName));
            }

            if (node.TryGetProperty("children", out var children))
            {
                foreach (var child in children.EnumerateArray())
                    WalkNode(child, currentParent, members);
            }
        }

        /// <summary>
        /// Regex matching the comment lines that precede @_cdecl wrapper blocks.
        /// Captures the fully-qualified member path (e.g., "ModuleName.TypeName.memberName").
        /// </summary>
        private static readonly Regex WrapperCommentRegex = new(
            @"// (?:Property [gs]etter|Method|Constructor|Enum case factory) @_cdecl wrapper for (.+)\.",
            RegexOptions.Compiled);

        /// <summary>
        /// Applies #if targetEnvironment(simulator) / #endif guards around @_cdecl wrapper
        /// blocks for simulator-only members in the Swift wrapper source.
        /// Uses mangled name hashes to precisely identify overloads in the @_cdecl function name.
        /// </summary>
        /// <param name="content">Swift wrapper file content.</param>
        /// <param name="moduleName">The module name (e.g., "MyModule").</param>
        /// <param name="simOnly">Simulator-only detection result with qualified names and mangled hashes.</param>
        /// <returns>The content with #if guards applied, and count of guarded blocks.</returns>
        public static (string Content, int GuardedCount) ApplySimulatorGuards(
            string content,
            string moduleName,
            SimulatorOnlyResult simOnly)
        {
            if (simOnly.Count == 0 || string.IsNullOrEmpty(content))
                return (content, 0);

            var lines = content.Split('\n');
            var output = new List<string>(lines.Length + simOnly.Count * 2);
            int guardedCount = 0;
            int i = 0;

            while (i < lines.Length)
            {
                var stripped = lines[i].TrimStart();

                // Check if this line is a wrapper comment for a simulator-only member
                var match = WrapperCommentRegex.Match(stripped);
                if (match.Success)
                {
                    var qualifiedPath = match.Groups[1].Value;
                    var resolvedName = ResolveQualifiedName(qualifiedPath, moduleName, simOnly.QualifiedNames);
                    if (resolvedName != null)
                    {
                        // Find the full block: comment line(s) + optional @available + @_cdecl + func body
                        int blockStart = i;

                        // Include the comment line in the guarded block
                        // Scan forward to find @_cdecl or @_silgen_name, then find block end
                        int funcStart = i + 1;
                        while (funcStart < lines.Length)
                        {
                            var s = lines[funcStart].TrimStart();
                            if (s.StartsWith("@_cdecl(", StringComparison.Ordinal) ||
                                s.StartsWith("@_silgen_name(", StringComparison.Ordinal) ||
                                s.StartsWith("@available(", StringComparison.Ordinal) ||
                                s.StartsWith("@MainActor", StringComparison.Ordinal))
                            {
                                break;
                            }
                            if (!s.StartsWith("//", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(s))
                                break; // Not part of the block header
                            funcStart++;
                        }

                        // Find end of function body (matching braces)
                        int blockEnd = FindBlockEnd(lines, funcStart);

                        // Find the @_cdecl or @_silgen_name line for hash-based overload matching
                        string? cdeclLine = null;
                        for (int j = blockStart; j <= blockEnd && j < lines.Length; j++)
                        {
                            var s = lines[j].TrimStart();
                            if (s.StartsWith("@_cdecl(", StringComparison.Ordinal) ||
                                s.StartsWith("@_silgen_name(", StringComparison.Ordinal))
                            {
                                cdeclLine = s;
                                break;
                            }
                        }

                        if (simOnly.MatchesCdeclBlock(resolvedName, cdeclLine))
                        {
                            // Emit with #if guard
                            output.Add("#if targetEnvironment(simulator)");
                            for (int j = blockStart; j <= blockEnd && j < lines.Length; j++)
                                output.Add(lines[j]);
                            output.Add("#endif");

                            guardedCount++;
                            i = blockEnd + 1;
                            continue;
                        }
                    }
                }

                output.Add(lines[i]);
                i++;
            }

            return (string.Join('\n', output), guardedCount);
        }

        /// <summary>
        /// Resolves a qualified path from a wrapper comment to a member name in the simulator-only set.
        /// Handles module-qualified paths (e.g., "ModuleName.Type.member" → "Type.member").
        /// Returns the resolved name or null if not found.
        /// </summary>
        private static string? ResolveQualifiedName(string qualifiedPath, string moduleName, IReadOnlySet<string> simulatorOnlyNames)
        {
            if (simulatorOnlyNames.Contains(qualifiedPath))
                return qualifiedPath;

            var prefix = moduleName + ".";
            if (qualifiedPath.StartsWith(prefix, StringComparison.Ordinal))
            {
                var stripped = qualifiedPath.Substring(prefix.Length);
                if (simulatorOnlyNames.Contains(stripped))
                    return stripped;
            }

            return null;
        }

        /// <summary>
        /// Creates a filtered copy of a native thunk assembly file that excludes thunks
        /// referencing simulator-only members. Used for device slice compilation.
        /// Uses mangled name hashes for precise matching — each hash uniquely identifies
        /// a member and appears in the thunk's .globl symbol line as lowercase hex.
        /// </summary>
        /// <param name="assemblyFilePath">Path to the original .arm64.s file.</param>
        /// <param name="simOnly">Simulator-only detection result with mangled hashes.</param>
        /// <param name="deviceOutputDirectory">Directory to write the filtered file.</param>
        /// <returns>Path to filtered file and count of removed thunks, or null if no filtering needed.</returns>
        public static (string FilteredPath, int RemovedCount)? FilterThunkAssembly(
            string assemblyFilePath,
            SimulatorOnlyResult simOnly,
            string deviceOutputDirectory)
        {
            if (simOnly.Count == 0)
                return null;

            var lines = File.ReadAllLines(assemblyFilePath);
            var output = new List<string>(lines.Length);
            int removedCount = 0;
            int i = 0;

            while (i < lines.Length)
            {
                // Thunk blocks start with ".globl _thunk_..."
                if (lines[i].TrimStart().StartsWith(".globl _thunk_", StringComparison.Ordinal))
                {
                    // Collect the full thunk block. Two forms exist:
                    // 1. Tail-call: .globl + .p2align + label + "b <symbol>" (no ret)
                    // 2. Multi-instruction: .globl + .p2align + label + ... + "ret"
                    // Block ends at "ret", or at the next ".globl" / end-of-file for tail-call thunks.
                    int blockStart = i;
                    int blockEnd = i;
                    for (int j = i + 1; j < lines.Length; j++)
                    {
                        var trimmed = lines[j].TrimStart();
                        if (trimmed.StartsWith("ret", StringComparison.Ordinal))
                        {
                            blockEnd = j;
                            break;
                        }
                        if (trimmed.StartsWith(".globl ", StringComparison.Ordinal))
                        {
                            // Next thunk starts here — current block is tail-call form.
                            // Exclude trailing blank lines from the block.
                            blockEnd = j - 1;
                            while (blockEnd > blockStart && string.IsNullOrWhiteSpace(lines[blockEnd]))
                                blockEnd--;
                            break;
                        }
                        blockEnd = j;
                    }

                    // Concatenate block for matching
                    var blockText = string.Join(" ", Enumerable.Range(blockStart, blockEnd - blockStart + 1)
                        .Where(j => j < lines.Length).Select(j => lines[j]));

                    if (simOnly.MatchesThunkBlock(blockText))
                    {
                        removedCount++;
                        i = blockEnd + 1;
                        continue;
                    }
                }

                output.Add(lines[i]);
                i++;
            }

            if (removedCount == 0)
                return null;

            var filteredPath = Path.Combine(deviceOutputDirectory, Path.GetFileName(assemblyFilePath));
            File.WriteAllLines(filteredPath, output);
            return (filteredPath, removedCount);
        }

        /// <summary>
        /// Finds the end of a function block by tracking structural brace depth. Braces inside string
        /// literals and comments are ignored (a default value like <c>= "}"</c> must not close the block
        /// early and truncate the simulator-only guard for the device build).
        /// </summary>
        private static int FindBlockEnd(string[] lines, int start)
        {
            int depth = 0;
            bool sawOpenBrace = false;
            int blockCommentDepth = 0;
            for (int j = start; j < lines.Length; j++)
            {
                depth += StructuralBraceScanner.NetLineDelta(lines[j], ref blockCommentDepth, ref sawOpenBrace);
                if (sawOpenBrace && depth <= 0 && j > start)
                    return j;
            }
            return lines.Length - 1;
        }
    }
}
