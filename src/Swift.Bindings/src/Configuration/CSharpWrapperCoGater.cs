// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace BindingsGeneration
{
    /// <summary>
    /// Post-processes generated C# binding files to suppress members whose
    /// corresponding Swift @_cdecl wrapper symbols were stripped by the post-processor.
    /// Prevents DllNotFoundException at runtime by co-gating C# with the Swift wrapper.
    /// </summary>
    public static class CSharpWrapperCoGater
    {
        /// <summary>
        /// Result of co-gating a single C# source file.
        /// </summary>
        public sealed class CoGatingResult
        {
            public required string Content { get; init; }
            public required int StrippedMemberCount { get; init; }
        }

        private static readonly Regex EntryPointRegex = new(
            @"EntryPoint\s*=\s*""([^""]+)""",
            RegexOptions.Compiled);

        // Matches LibraryImport("SwiftBindings" or "XyzSwiftBindings" but NOT "SwiftBindingsTestLib".
        // The wrapper library name is always "{ModuleName}SwiftBindings" or just "SwiftBindings".
        private static readonly Regex WrapperLibraryImportRegex = new(
            @"LibraryImport\(""(\w*SwiftBindings)""",
            RegexOptions.Compiled);

        /// <summary>
        /// Processes a single C# source file, removing members that reference stripped wrapper symbols.
        /// Uses 3-level transitive closure: P/Invoke → caller → property forwarder.
        /// </summary>
        public static CoGatingResult Process(string content, IReadOnlySet<string> strippedSymbols)
        {
            if (strippedSymbols.Count == 0 || string.IsNullOrEmpty(content))
                return new CoGatingResult { Content = content, StrippedMemberCount = 0 };

            var lines = SplitLines(content);
            var removals = new HashSet<int>();

            // Step A: Find P/Invoke declarations targeting stripped wrapper symbols.
            // Collect candidates with their line ranges — don't apply removals yet,
            // because some P/Invokes may be exempted by GetMetadata fallback callers.
            var candidatePInvokes = new Dictionary<string, (int preambleStart, int declEnd)>();
            FindStrippedPInvokeCandidates(lines, strippedSymbols, candidatePInvokes);

            if (candidatePInvokes.Count == 0)
                return new CoGatingResult { Content = content, StrippedMemberCount = 0 };

            // Step A2: Build per-type interface member protection.
            // If stripping a member would remove an interface implementation, the type
            // would fail to compile (CS0535). Scoped to the actual interfaces each type implements.
            var interfaceMembers = ParseInterfaceMembers(lines);
            var typeProtectedMembers = BuildTypeProtectedMembers(lines, interfaceMembers);
            var lineToType = BuildLineToTypeMap(lines);

            // Step A3: Exempt P/Invokes whose callers are non-strippable:
            // - DllNotFoundException fallback (GetMetadata pattern)
            // - Interface implementations (member name matches an interface the containing type implements)
            var exemptedNames = FindExemptedPInvokes(lines, candidatePInvokes.Keys, typeProtectedMembers, lineToType);

            // Step A4: Detect scope-ambiguous method names.
            // If a P/Invoke method name (e.g., "PInvoke_eq") appears in multiple class scopes,
            // file-wide caller detection would false-match across scopes. Skip these entirely.
            var ambiguousNames = FindAmbiguousMethodNames(lines, candidatePInvokes.Keys);

            // Apply P/Invoke removals for non-exempted, non-ambiguous names
            var strippedPInvokeNames = new HashSet<string>();
            foreach (var (name, (preambleStart, declEnd)) in candidatePInvokes)
            {
                if (exemptedNames.Contains(name) || ambiguousNames.Contains(name))
                    continue;
                strippedPInvokeNames.Add(name);
                for (int j = preambleStart; j <= declEnd; j++)
                    removals.Add(j);
            }

            if (strippedPInvokeNames.Count == 0)
                return new CoGatingResult { Content = content, StrippedMemberCount = 0 };

            // Step B: Find Level 1 callers (methods/constructors calling stripped P/Invokes)
            var strippedCallerNames = new HashSet<string>();
            FindAndMarkCallers(lines, strippedPInvokeNames, strippedCallerNames, removals);

            // Step C: Find Level 2 forwarders (properties delegating to stripped helpers)
            if (strippedCallerNames.Count > 0)
            {
                var _ = new HashSet<string>();
                FindAndMarkCallers(lines, strippedCallerNames, _, removals);
            }

            // Step D: Strip dangling ToString() expression-bodied methods.
            // When the Description property is stripped (Steps B-C), the generated
            // "public override string ToString() => Description;" becomes a dangling reference.
            StripDanglingToString(lines, removals);

            // Step E: Strip orphaned narrowing overloads (int/uint → nint/nuint convenience wrappers)
            // whose delegate target was stripped. Handles single-line and multi-line indexers,
            // and expression-bodied method overloads.
            StripOrphanedNarrowingOverloads(lines, removals, lineToType);

            if (removals.Count == 0)
                return new CoGatingResult { Content = content, StrippedMemberCount = 0 };

            // Build output, skipping removed lines
            var sb = new StringBuilder();
            for (int i = 0; i < lines.Count; i++)
            {
                if (!removals.Contains(i))
                    sb.Append(lines[i]);
            }

            return new CoGatingResult
            {
                Content = sb.ToString(),
                StrippedMemberCount = strippedPInvokeNames.Count
            };
        }

        /// <summary>
        /// Processes all .cs files in a directory, removing members targeting stripped wrapper symbols.
        /// Files are modified in-place.
        /// </summary>
        public static int ProcessDirectory(string directory, IReadOnlySet<string> strippedSymbols, ILogger? logger = null)
        {
            if (strippedSymbols.Count == 0)
                return 0;

            var csFiles = Directory.GetFiles(directory, "*.cs");
            int totalStripped = 0;

            foreach (var file in csFiles)
            {
                var content = File.ReadAllText(file);
                var result = Process(content, strippedSymbols);
                if (result.StrippedMemberCount > 0)
                {
                    File.WriteAllText(file, result.Content);
                    totalStripped += result.StrippedMemberCount;
                    logger?.LogInformation("  Co-gated {Count} member(s) from {File}",
                        result.StrippedMemberCount, Path.GetFileName(file));
                }
            }

            if (totalStripped > 0)
                logger?.LogInformation("Co-gated {Count} total P/Invoke(s) and their callers from generated C#.",
                    totalStripped);

            return totalStripped;
        }

        #region Step A: P/Invoke Detection

        /// <summary>
        /// Finds P/Invoke declarations whose EntryPoint matches a stripped symbol.
        /// Returns candidates as name → (preambleStart, declEnd) without applying removals.
        /// </summary>
        private static void FindStrippedPInvokeCandidates(
            List<string> lines, IReadOnlySet<string> strippedSymbols,
            Dictionary<string, (int preambleStart, int declEnd)> candidates)
        {
            for (int i = 0; i < lines.Count; i++)
            {
                if (!IsWrapperLibraryImportLine(lines[i]))
                    continue;

                var entryPointMatch = EntryPointRegex.Match(lines[i]);
                if (!entryPointMatch.Success)
                    continue;

                var entryPoint = entryPointMatch.Groups[1].Value;

                // SBW_Free symbols are shared across all types — never strip
                if (entryPoint.StartsWith("SBW_Free_", StringComparison.Ordinal))
                    continue;

                if (!strippedSymbols.Contains(entryPoint))
                    continue;

                // Find the partial method declaration within the next few lines
                int declLine = FindPartialDeclaration(lines, i);
                if (declLine < 0) continue;

                // Extract the C# method name from the declaration
                var methodName = ExtractMethodNameFromPartialDecl(lines[declLine]);
                if (methodName == null) continue;

                int preambleStart = ScanBackwardForPreamble(lines, i);
                candidates.TryAdd(methodName, (preambleStart, declLine));
            }
        }

        private static readonly HashSet<string> StandardInterfaces = new()
        {
            "ISwiftObject", "IDisposable", "ISwiftStruct", "IExistentialBoxable"
        };

        /// <summary>
        /// Parses all interface declarations in the file.
        /// Returns interfaceName → set of member names.
        /// </summary>
        private static Dictionary<string, HashSet<string>> ParseInterfaceMembers(List<string> lines)
        {
            var result = new Dictionary<string, HashSet<string>>();

            for (int i = 0; i < lines.Count; i++)
            {
                var trimmed = lines[i].TrimStart();
                if (!trimmed.StartsWith("public interface ", StringComparison.Ordinal))
                    continue;

                var nameMatch = Regex.Match(trimmed, @"public interface (I\w+)");
                if (!nameMatch.Success) continue;
                var ifaceName = nameMatch.Groups[1].Value;

                if (StandardInterfaces.Contains(ifaceName)) continue;
                if (ifaceName.StartsWith("IEquatable", StringComparison.Ordinal)) continue;

                int blockEnd = FindBlockEnd(lines, i);
                var members = new HashSet<string>();
                for (int j = i + 1; j < blockEnd; j++)
                {
                    var memberTrimmed = lines[j].TrimStart();
                    if (string.IsNullOrWhiteSpace(memberTrimmed)) continue;
                    if (memberTrimmed.StartsWith("//", StringComparison.Ordinal)) continue;
                    if (memberTrimmed.StartsWith("{", StringComparison.Ordinal)) continue;
                    if (memberTrimmed.StartsWith("}", StringComparison.Ordinal)) continue;

                    var memberName = ExtractInterfaceMemberName(memberTrimmed);
                    if (memberName != null)
                        members.Add(memberName);
                }

                if (members.Count > 0)
                    result[ifaceName] = members;
            }

            return result;
        }

        /// <summary>
        /// Builds per-type protected member sets by matching each type's interface list
        /// against the parsed interface declarations.
        /// </summary>
        private static Dictionary<string, HashSet<string>> BuildTypeProtectedMembers(
            List<string> lines, Dictionary<string, HashSet<string>> interfaceMembers)
        {
            if (interfaceMembers.Count == 0)
                return new Dictionary<string, HashSet<string>>();

            var result = new Dictionary<string, HashSet<string>>();

            for (int i = 0; i < lines.Count; i++)
            {
                var trimmed = lines[i].TrimStart();
                if (!trimmed.Contains(" class ") && !trimmed.Contains(" struct "))
                    continue;
                if (!trimmed.Contains(':'))
                    continue;

                var typeName = ExtractTypeName(trimmed);
                if (typeName == null) continue;

                // Extract interface names from the declaration after ':'
                int colonIdx = trimmed.IndexOf(':');
                var afterColon = trimmed.Substring(colonIdx + 1);
                var protectedNames = new HashSet<string>();

                foreach (var token in afterColon.Split(','))
                {
                    var ifaceName = token.Trim();
                    // Strip generic parameters: IEquatable<Foo> → IEquatable
                    int angleIdx = ifaceName.IndexOf('<');
                    if (angleIdx >= 0)
                        ifaceName = ifaceName.Substring(0, angleIdx);
                    // Strip trailing { if on same line
                    ifaceName = ifaceName.TrimEnd('{', ' ');

                    if (interfaceMembers.TryGetValue(ifaceName, out var members))
                        protectedNames.UnionWith(members);
                }

                if (protectedNames.Count > 0)
                {
                    // Merge with existing entry for partial type declarations
                    if (result.TryGetValue(typeName, out var existing))
                        existing.UnionWith(protectedNames);
                    else
                        result[typeName] = protectedNames;
                }
            }

            return result;
        }

        /// <summary>
        /// Builds a line → containing type name map via forward brace-depth tracking.
        /// Handles nested types correctly (innermost type wins).
        /// </summary>
        private static string?[] BuildLineToTypeMap(List<string> lines)
        {
            var map = new string?[lines.Count];
            var typeStack = new Stack<(string name, int openDepth)>();
            int depth = 0;
            string? pendingType = null;

            for (int i = 0; i < lines.Count; i++)
            {
                var trimmed = lines[i].TrimStart();

                // Detect type declarations (not namespace, not interface)
                if ((trimmed.Contains(" class ") || trimmed.Contains(" struct ")) &&
                    !trimmed.StartsWith("namespace ", StringComparison.Ordinal) &&
                    !trimmed.StartsWith("public interface ", StringComparison.Ordinal))
                {
                    pendingType = ExtractTypeName(trimmed);
                }

                foreach (char c in lines[i])
                {
                    if (c == '{')
                    {
                        depth++;
                        if (pendingType != null)
                        {
                            typeStack.Push((pendingType, depth));
                            pendingType = null;
                        }
                    }
                    else if (c == '}')
                    {
                        depth--;
                        // Pop types whose scope has closed (openDepth > current depth)
                        while (typeStack.Count > 0 && typeStack.Peek().openDepth > depth)
                            typeStack.Pop();
                    }
                }

                map[i] = typeStack.Count > 0 ? typeStack.Peek().name : null;
            }

            return map;
        }

        /// <summary>
        /// Extracts a type name from a class/struct declaration line.
        /// </summary>
        private static string? ExtractTypeName(string trimmed)
        {
            var match = Regex.Match(trimmed, @"\b(?:class|struct)\s+(\w+)");
            return match.Success ? match.Groups[1].Value : null;
        }

        /// <summary>
        /// Extracts a member name from an interface declaration line.
        /// Handles both methods ("void DidReceive(ServerEvent e);") and
        /// properties ("string ModeName { get; }").
        /// </summary>
        private static string? ExtractInterfaceMemberName(string trimmed)
        {
            int parenIdx = trimmed.IndexOf('(');
            if (parenIdx > 0)
            {
                var beforeParen = trimmed.Substring(0, parenIdx).TrimEnd();
                int lastSpace = beforeParen.LastIndexOf(' ');
                if (lastSpace >= 0)
                {
                    var name = beforeParen.Substring(lastSpace + 1);
                    if (name.Length > 0 && char.IsLetter(name[0]))
                        return name;
                }
            }

            int braceIdx = trimmed.IndexOf('{');
            if (braceIdx > 0)
            {
                var beforeBrace = trimmed.Substring(0, braceIdx).TrimEnd();
                int lastSpace = beforeBrace.LastIndexOf(' ');
                if (lastSpace >= 0)
                {
                    var name = beforeBrace.Substring(lastSpace + 1);
                    if (name.Length > 0 && char.IsLetter(name[0]))
                        return name;
                }
            }

            return null;
        }

        /// <summary>
        /// Finds P/Invoke names that must NOT be stripped because their callers are non-strippable.
        /// A caller is non-strippable if it:
        /// - Has a DllNotFoundException fallback (GetMetadata pattern)
        /// - Implements an interface member on its containing type
        /// </summary>
        private static HashSet<string> FindExemptedPInvokes(
            List<string> lines, IEnumerable<string> candidateNames,
            Dictionary<string, HashSet<string>> typeProtectedMembers,
            string?[] lineToType)
        {
            var exempted = new HashSet<string>();

            int i = 0;
            while (i < lines.Count)
            {
                var trimmed = lines[i].TrimStart();
                if (IsTypeOrNamespaceDeclaration(trimmed)) { i++; continue; }
                if (!IsPotentialMemberDeclaration(trimmed)) { i++; continue; }

                int braceOpenLine = FindOpeningBrace(lines, i);
                if (braceOpenLine < 0 || braceOpenLine > i + 3) { i++; continue; }

                int blockEnd = FindBlockEnd(lines, braceOpenLine);
                if (blockEnd < braceOpenLine) { i++; continue; }

                // Check exemption: DllNotFoundException fallback
                bool hasFallback = false;
                for (int j = i; j <= blockEnd; j++)
                {
                    if (lines[j].Contains("DllNotFoundException"))
                    {
                        hasFallback = true;
                        break;
                    }
                }

                // Check exemption: interface member on the containing type.
                // For property helpers (Count_Get), derive the property name (Count).
                // Only exempt if the CONTAINING TYPE actually implements an interface
                // that declares this member.
                bool isInterfaceMember = false;
                if (typeProtectedMembers.Count > 0)
                {
                    var containingType = i < lineToType.Length ? lineToType[i] : null;
                    if (containingType != null &&
                        typeProtectedMembers.TryGetValue(containingType, out var protectedNames))
                    {
                        var memberName = ExtractMemberName(trimmed);
                        if (memberName != null)
                        {
                            var publicApiName = IsPropertyHelperName(memberName)
                                ? memberName.Substring(0, memberName.LastIndexOf('_'))
                                : memberName;
                            isInterfaceMember = protectedNames.Contains(publicApiName);
                        }
                    }
                }

                if (hasFallback || isInterfaceMember)
                {
                    foreach (var name in candidateNames)
                    {
                        for (int j = i; j <= blockEnd; j++)
                        {
                            if (ContainsCallTo(lines[j], name))
                            {
                                exempted.Add(name);
                                break;
                            }
                        }
                    }
                }

                i = blockEnd + 1;
            }

            return exempted;
        }

        /// <summary>
        /// Finds P/Invoke method names that appear more than once in the file (across class scopes).
        /// These names are unsafe for file-wide caller detection — e.g., "PInvoke_eq" may appear
        /// in 15 different types, and stripping one would false-match callers in the other 14.
        /// </summary>
        private static HashSet<string> FindAmbiguousMethodNames(
            List<string> lines, IEnumerable<string> candidateNames)
        {
            var nameCounts = new Dictionary<string, int>();
            foreach (var name in candidateNames)
                nameCounts[name] = 0;

            for (int i = 0; i < lines.Count; i++)
            {
                // Look for partial method declarations that match candidate names
                var line = lines[i];
                if (!line.Contains(" partial ") || !line.TrimEnd().EndsWith(";"))
                    continue;

                foreach (var name in candidateNames)
                {
                    if (ContainsCallTo(line, name))
                    {
                        nameCounts[name]++;
                    }
                }
            }

            var ambiguous = new HashSet<string>();
            foreach (var (name, count) in nameCounts)
            {
                if (count > 1)
                    ambiguous.Add(name);
            }
            return ambiguous;
        }

        /// <summary>
        /// Checks if a line is a LibraryImport attribute targeting a wrapper library.
        /// Wrapper libraries are named "{ModuleName}SwiftBindings" or just "SwiftBindings".
        /// Correctly excludes native library names like "SwiftBindingsTestLib".
        /// </summary>
        internal static bool IsWrapperLibraryImportLine(string line)
        {
            return line.Contains("EntryPoint") && WrapperLibraryImportRegex.IsMatch(line);
        }

        private static int FindPartialDeclaration(List<string> lines, int fromLine)
        {
            for (int j = fromLine; j < Math.Min(fromLine + 5, lines.Count); j++)
            {
                if (lines[j].Contains(" partial ") && lines[j].TrimEnd().EndsWith(";"))
                    return j;
            }
            return -1;
        }

        /// <summary>
        /// Extracts the method name from a partial method declaration.
        /// Pattern: ... static partial ReturnType MethodName(...);
        /// </summary>
        internal static string? ExtractMethodNameFromPartialDecl(string line)
        {
            int parenIdx = line.IndexOf('(');
            if (parenIdx <= 0) return null;

            var beforeParen = line.Substring(0, parenIdx).TrimEnd();
            int lastSpace = beforeParen.LastIndexOf(' ');
            if (lastSpace < 0) return null;

            var name = beforeParen.Substring(lastSpace + 1);
            // Handle explicit interface (e.g., ISwiftObject.GetTypeMetadata)
            int dotIdx = name.LastIndexOf('.');
            if (dotIdx >= 0)
                name = name.Substring(dotIdx + 1);

            return name.Length > 0 ? name : null;
        }

        #endregion

        #region Step B/C: Caller Detection

        private static void FindAndMarkCallers(
            List<string> lines, HashSet<string> targetNames,
            HashSet<string> foundCallerNames, HashSet<int> removals)
        {
            int i = 0;
            while (i < lines.Count)
            {
                if (removals.Contains(i)) { i++; continue; }

                var trimmed = lines[i].TrimStart();

                // Skip type/namespace declarations
                if (IsTypeOrNamespaceDeclaration(trimmed)) { i++; continue; }

                // Only process lines that look like member declarations
                if (!IsPotentialMemberDeclaration(trimmed)) { i++; continue; }

                // Find the opening brace (same line or next non-blank line)
                int braceOpenLine = FindOpeningBrace(lines, i);
                if (braceOpenLine < 0 || braceOpenLine > i + 3) { i++; continue; }

                // Find the matching closing brace
                int blockEnd = FindBlockEnd(lines, braceOpenLine);
                if (blockEnd < braceOpenLine) { i++; continue; }

                // Check if the block body contains a call to any target name
                bool referencesTarget = false;
                for (int j = i; j <= blockEnd && !referencesTarget; j++)
                {
                    if (removals.Contains(j)) continue;
                    var lineText = lines[j];
                    foreach (var name in targetNames)
                    {
                        if (ContainsCallTo(lineText, name))
                        {
                            referencesTarget = true;
                            break;
                        }
                    }
                }

                if (!referencesTarget)
                {
                    i = blockEnd + 1;
                    continue;
                }

                // DllNotFoundException callers are already handled by the exemption
                // in Step A2 — the P/Invoke itself is kept, so the caller compiles.
                // But if we reach here, the P/Invoke WAS stripped (not exempted),
                // so this caller must also be stripped.

                // Extract the member name for Level 2 transitive stripping.
                // Only add property helper names (e.g., "Value_Get", "Name_Set") — these
                // are the only Level 1 members with Level 2 forwarders (property declarations).
                // Constructors and public methods are direct API surface with no forwarders,
                // and their names (class names, method names) are too generic and risk
                // false-matching other members like NewFromPayload or overloaded constructors.
                var memberName = ExtractMemberName(trimmed);
                if (memberName != null && (IsPropertyHelperName(memberName) ||
                    memberName.StartsWith("CreateSwiftInstance_", StringComparison.Ordinal)))
                    foundCallerNames.Add(memberName);

                // Mark for removal (including preamble: attributes, doc comments)
                int preambleStart = ScanBackwardForPreamble(lines, i);
                for (int j = preambleStart; j <= blockEnd; j++)
                    removals.Add(j);

                i = blockEnd + 1;
            }
        }

        /// <summary>
        /// Checks if a line contains a call to the given method name.
        /// Uses "name(" token matching with word-boundary check to avoid:
        /// - Suffix collisions: PInvoke_foo_ABC won't match PInvoke_foo_ABC123
        /// - Prefix collisions: Value_Get won't match DatabaseValue_Get
        /// The preceding character (if any) must NOT be a letter, digit, or underscore.
        /// </summary>
        private static bool ContainsCallTo(string line, string name)
        {
            var needle = name + "(";
            int idx = 0;
            while (idx < line.Length)
            {
                int pos = line.IndexOf(needle, idx, StringComparison.Ordinal);
                if (pos < 0)
                    return false;
                // Check word boundary: preceding char must not be identifier char
                if (pos == 0 || !IsIdentifierChar(line[pos - 1]))
                    return true;
                idx = pos + 1;
            }
            return false;
        }

        private static bool IsIdentifierChar(char c)
        {
            return char.IsLetterOrDigit(c) || c == '_';
        }

        private static bool IsTypeOrNamespaceDeclaration(string trimmed)
        {
            return trimmed.StartsWith("namespace ", StringComparison.Ordinal) ||
                   trimmed.Contains(" class ") || trimmed.Contains(" struct ") ||
                   trimmed.Contains(" enum ") || trimmed.Contains(" interface ");
        }

        private static bool IsPotentialMemberDeclaration(string trimmed)
        {
            return trimmed.StartsWith("public ", StringComparison.Ordinal) ||
                   trimmed.StartsWith("private ", StringComparison.Ordinal) ||
                   trimmed.StartsWith("internal ", StringComparison.Ordinal) ||
                   trimmed.StartsWith("protected ", StringComparison.Ordinal) ||
                   trimmed.StartsWith("static ", StringComparison.Ordinal);
        }

        private static int FindOpeningBrace(List<string> lines, int fromLine)
        {
            if (lines[fromLine].Contains('{'))
                return fromLine;

            for (int j = fromLine + 1; j < Math.Min(fromLine + 4, lines.Count); j++)
            {
                var trimmed = lines[j].TrimStart();
                if (trimmed.StartsWith("{", StringComparison.Ordinal))
                    return j;
                if (!string.IsNullOrWhiteSpace(trimmed) && !trimmed.StartsWith("[", StringComparison.Ordinal))
                    break;
            }

            return -1;
        }

        /// <summary>
        /// Extracts the member name from a declaration line.
        /// Methods/constructors: identifier before (. Properties: last identifier on line.
        /// </summary>
        internal static string? ExtractMemberName(string trimmed)
        {
            // Methods/constructors: identifier before (
            int parenIdx = trimmed.IndexOf('(');
            if (parenIdx > 0)
            {
                var beforeParen = trimmed.Substring(0, parenIdx).TrimEnd();
                int lastSpace = beforeParen.LastIndexOf(' ');
                if (lastSpace >= 0)
                {
                    var name = beforeParen.Substring(lastSpace + 1);
                    int dotIdx = name.LastIndexOf('.');
                    if (dotIdx >= 0)
                        name = name.Substring(dotIdx + 1);
                    if (name.Length > 0 && !IsKeyword(name))
                        return name;
                }
            }

            // Properties: last identifier on line (no parens on declaration line)
            var cleaned = trimmed.TrimEnd();
            int lastSpaceP = cleaned.LastIndexOf(' ');
            if (lastSpaceP >= 0)
            {
                var name = cleaned.Substring(lastSpaceP + 1);
                if (name.Length > 0 && char.IsLetter(name[0]) && !IsKeyword(name))
                    return name;
            }

            return null;
        }

        /// <summary>
        /// Returns true if the name follows the property helper naming convention.
        /// Generated property helpers are always named "{PropertyName}_Get" or "{PropertyName}_Set".
        /// Only these names are propagated to Level 2 (property forwarder) stripping.
        /// </summary>
        private static bool IsPropertyHelperName(string name)
        {
            return name.EndsWith("_Get", StringComparison.Ordinal) ||
                   name.EndsWith("_Set", StringComparison.Ordinal);
        }

        private static bool IsKeyword(string name)
        {
            return name is "public" or "private" or "internal" or "protected" or
                   "static" or "virtual" or "override" or "sealed" or
                   "unsafe" or "partial" or "async" or "new" or "readonly" or
                   "abstract" or "extern" or "void" or "class" or "struct";
        }

        /// <summary>
        /// Strips expression-bodied ToString() methods that reference properties removed by prior steps.
        /// Pattern: "public override string ToString() => PropertyName;" where PropertyName was stripped.
        /// </summary>
        private static void StripDanglingToString(List<string> lines, HashSet<int> removals)
        {
            for (int i = 0; i < lines.Count; i++)
            {
                if (removals.Contains(i)) continue;
                var trimmed = lines[i].TrimStart();

                // Match expression-bodied ToString(): "public override string ToString() => X;"
                if (!trimmed.StartsWith("public override string ToString() =>", StringComparison.Ordinal))
                    continue;

                // Extract the referenced member name from "=> PropertyName;"
                int arrowIdx = trimmed.IndexOf("=>", StringComparison.Ordinal);
                if (arrowIdx < 0) continue;

                var expr = trimmed.Substring(arrowIdx + 2).Trim().TrimEnd(';').Trim();
                if (string.IsNullOrEmpty(expr) || !char.IsLetter(expr[0])) continue;

                // Check if the referenced property was stripped by scanning nearby removed lines
                // for a property declaration with the same name.
                if (IsPropertyRemoved(lines, removals, i, expr))
                {
                    int preambleStart = ScanBackwardForPreamble(lines, i);
                    for (int j = preambleStart; j <= i; j++)
                        removals.Add(j);
                }
            }
        }

        /// <summary>
        /// Checks if a property with the given name was removed in the same class scope.
        /// Scans for removed lines containing a property declaration for the name.
        /// </summary>
        private static bool IsPropertyRemoved(List<string> lines, HashSet<int> removals, int contextLine, string propertyName)
        {
            // Find enclosing class scope boundaries
            int scopeStart = FindEnclosingClassStart(lines, contextLine);
            int scopeEnd = FindBlockEnd(lines, scopeStart);

            // Look for a removed property declaration with the target name within this scope
            var propertyToken = $" {propertyName}";
            for (int j = scopeStart; j <= scopeEnd && j < lines.Count; j++)
            {
                if (!removals.Contains(j)) continue;
                var trimmed = lines[j].TrimStart();
                // Property declarations: "public TYPE Name" or "public static TYPE Name"
                // They don't have parentheses (distinguishing them from methods)
                if (trimmed.Contains(propertyToken) && !trimmed.Contains("(") &&
                    (trimmed.StartsWith("public ", StringComparison.Ordinal) ||
                     trimmed.StartsWith("internal ", StringComparison.Ordinal)))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Finds the opening brace of the enclosing class/struct declaration.
        /// </summary>
        private static int FindEnclosingClassStart(List<string> lines, int fromLine)
        {
            int depth = 0;
            for (int j = fromLine; j >= 0; j--)
            {
                foreach (char c in lines[j])
                {
                    if (c == '}') depth++;
                    else if (c == '{') depth--;
                }
                if (depth < 0)
                {
                    // Found an unmatched '{' — this is our enclosing scope
                    return j;
                }
            }
            return 0;
        }


        #endregion

        #region Shared Helpers

        /// <summary>
        /// Scans backward from a line to include contiguous attribute ([...]) and doc comment (///) lines.
        /// Stops at blank lines or non-preamble content.
        /// </summary>
        private static int ScanBackwardForPreamble(List<string> lines, int fromLine)
        {
            int start = fromLine;
            for (int j = fromLine - 1; j >= 0; j--)
            {
                var trimmed = lines[j].TrimStart();
                if (trimmed.StartsWith("[", StringComparison.Ordinal) ||
                    trimmed.StartsWith("///", StringComparison.Ordinal))
                {
                    start = j;
                }
                else
                {
                    break;
                }
            }
            return start;
        }

        /// <summary>
        /// Finds the end of a brace-delimited block starting at the given line.
        /// </summary>
        internal static int FindBlockEnd(List<string> lines, int start)
        {
            int depth = 0;
            bool foundOpen = false;
            for (int j = start; j < lines.Count; j++)
            {
                foreach (char c in lines[j])
                {
                    if (c == '{') { depth++; foundOpen = true; }
                    else if (c == '}') depth--;
                }
                if (foundOpen && depth <= 0)
                    return j;
            }
            return lines.Count - 1;
        }

        /// <summary>
        /// Counts parameters in a method declaration by counting commas between parentheses.
        /// Returns 0 for no-arg methods, 1 for single-arg, etc.
        /// Used to match narrowing overloads to their specific delegate target.
        /// </summary>
        private static int CountParameters(string line)
        {
            int parenStart = line.IndexOf('(');
            int parenEnd = line.LastIndexOf(')');
            if (parenStart < 0 || parenEnd <= parenStart)
                return 0;
            var inside = line.Substring(parenStart + 1, parenEnd - parenStart - 1).Trim();
            if (inside.Length == 0)
                return 0;
            // Count commas at depth 0 (skip nested generics/parens)
            int count = 1;
            int depth = 0;
            foreach (char c in inside)
            {
                if (c == '<' || c == '(') depth++;
                else if (c == '>' || c == ')') depth--;
                else if (c == ',' && depth == 0) count++;
            }
            return count;
        }

        /// <summary>
        /// Counts parameters in an indexer declaration by counting commas between brackets.
        /// Returns 1 for single-param indexers like "this[int x]", 2 for "this[string a, nint b]", etc.
        /// Used to match narrowing indexer overloads to their specific delegate target.
        /// </summary>
        private static int CountIndexerParameters(string line)
        {
            int bracketStart = line.IndexOf('[');
            int bracketEnd = line.IndexOf(']');
            if (bracketStart < 0 || bracketEnd <= bracketStart)
                return 0;
            var inside = line.Substring(bracketStart + 1, bracketEnd - bracketStart - 1).Trim();
            if (inside.Length == 0)
                return 0;
            int count = 1;
            int depth = 0;
            foreach (char c in inside)
            {
                if (c == '<' || c == '(') depth++;
                else if (c == '>' || c == ')') depth--;
                else if (c == ',' && depth == 0) count++;
            }
            return count;
        }

        /// <summary>
        /// Splits content into lines, preserving line endings.
        /// </summary>
        private static List<string> SplitLines(string content)
        {
            var lines = new List<string>();
            int start = 0;
            for (int i = 0; i < content.Length; i++)
            {
                if (content[i] == '\n')
                {
                    lines.Add(content.Substring(start, i - start + 1));
                    start = i + 1;
                }
                else if (content[i] == '\r')
                {
                    if (i + 1 < content.Length && content[i + 1] == '\n')
                    {
                        lines.Add(content.Substring(start, i - start + 2));
                        start = i + 2;
                        i++;
                    }
                    else
                    {
                        lines.Add(content.Substring(start, i - start + 1));
                        start = i + 1;
                    }
                }
            }
            if (start < content.Length)
                lines.Add(content.Substring(start));
            return lines;
        }

        /// <summary>
        /// Finds method names that appear as declarations (not just calls) in multiple locations
        /// in the file. Unlike FindAmbiguousMethodNames (which checks partial declarations only),
        /// this checks all method declarations. Used by the proxy co-gater to avoid false-matching
        /// property helpers like "Subscript_Get" that exist in multiple types.
        /// </summary>
        private static HashSet<string> FindAmbiguousMethodDeclarations(
            List<string> lines, IEnumerable<string> candidateNames)
        {
            var nameCounts = new Dictionary<string, int>();
            foreach (var name in candidateNames)
                nameCounts[name] = 0;

            for (int i = 0; i < lines.Count; i++)
            {
                var trimmed = lines[i].TrimStart();
                if (!IsPotentialMemberDeclaration(trimmed))
                    continue;

                // Extract member name and check against candidates
                var memberName = ExtractMemberName(trimmed);
                if (memberName != null && nameCounts.ContainsKey(memberName))
                    nameCounts[memberName]++;
            }

            var ambiguous = new HashSet<string>();
            foreach (var (name, count) in nameCounts)
            {
                if (count > 1)
                    ambiguous.Add(name);
            }
            return ambiguous;
        }

        /// <summary>
        /// Scope-aware variant of FindAndMarkCallers. Only strips callers of the given method
        /// name if they are within one of the specified containing types.
        /// Prevents false-matching "Subscript_Get" in TypeB when only TypeA's version was stripped.
        /// </summary>
        private static void FindAndMarkCallersInScopes(
            List<string> lines, string methodName, HashSet<string> allowedTypes,
            string?[] lineToType, HashSet<int> removals)
        {
            int i = 0;
            while (i < lines.Count)
            {
                if (removals.Contains(i)) { i++; continue; }

                var trimmed = lines[i].TrimStart();
                if (IsTypeOrNamespaceDeclaration(trimmed)) { i++; continue; }
                if (!IsPotentialMemberDeclaration(trimmed)) { i++; continue; }

                int braceOpenLine = FindOpeningBrace(lines, i);
                if (braceOpenLine < 0 || braceOpenLine > i + 3) { i++; continue; }

                int blockEnd = FindBlockEnd(lines, braceOpenLine);
                if (blockEnd < braceOpenLine) { i++; continue; }

                // Check scope: only strip if in an allowed type
                var containingType = i < lineToType.Length ? lineToType[i] : null;
                if (containingType == null || !allowedTypes.Contains(containingType))
                {
                    i = blockEnd + 1;
                    continue;
                }

                // Check if the block body calls the method
                bool callsMethod = false;
                for (int j = i; j <= blockEnd; j++)
                {
                    if (ContainsCallTo(lines[j], methodName))
                    {
                        callsMethod = true;
                        break;
                    }
                }

                if (callsMethod)
                {
                    int preambleStart = ScanBackwardForPreamble(lines, i);
                    for (int j = preambleStart; j <= blockEnd; j++)
                        removals.Add(j);
                }

                i = blockEnd + 1;
            }
        }

        /// <summary>
        /// Strips orphaned narrowing overloads (int/uint → nint/nuint convenience wrappers)
        /// whose delegate target was stripped or never emitted. Handles:
        /// - Single-line indexers: "this[int x] => this[(nint)x];"
        /// - Multi-line indexers: "this[int x] { get => this[(nint)x]; set => ... }"
        /// - Expression-bodied methods: "Method(int x) => Method((nint)x);"
        /// Uses lineToType to scope the search to the containing type.
        /// </summary>
        private static void StripOrphanedNarrowingOverloads(List<string> lines, HashSet<int> removals, string?[] lineToType)
        {
            for (int i = 0; i < lines.Count; i++)
            {
                if (removals.Contains(i)) continue;
                var trimmed = lines[i].TrimStart();

                // === Indexer narrowing ===
                if (trimmed.Contains("this[int ") || trimmed.Contains("this[uint "))
                {
                    int blockEnd = i;
                    bool isNarrowingIndexer = false;

                    // Single-line: "this[int x] => this[(nint)x];"
                    if (trimmed.Contains("=> this[(nint)") || trimmed.Contains("=> this[(nuint)"))
                    {
                        isNarrowingIndexer = true;
                    }
                    // Multi-line: "this[int x]" with body using "this[(nint)x]"
                    else if (!trimmed.Contains("=>"))
                    {
                        int braceOpenLine = FindOpeningBrace(lines, i);
                        if (braceOpenLine >= 0 && braceOpenLine <= i + 3)
                        {
                            blockEnd = FindBlockEnd(lines, braceOpenLine);
                            for (int j = i + 1; j <= blockEnd; j++)
                            {
                                if (lines[j].Contains("this[(nint)") || lines[j].Contains("this[(nuint)"))
                                {
                                    isNarrowingIndexer = true;
                                    break;
                                }
                            }
                        }
                    }

                    if (isNarrowingIndexer)
                    {
                        // Must match param count to avoid false positives from unrelated indexers
                        // (e.g., this[string, nint] should not satisfy this[int] => this[(nint)x]).
                        var containingType = i < lineToType.Length ? lineToType[i] : null;
                        int indexerParamCount = CountIndexerParameters(trimmed);
                        bool targetExists = false;
                        for (int j = 0; j < lines.Count; j++)
                        {
                            if (removals.Contains(j)) continue;
                            if (j >= i && j <= blockEnd) continue;
                            if (containingType != null && j < lineToType.Length && lineToType[j] != containingType)
                                continue;
                            if ((lines[j].Contains("this[nint ") || lines[j].Contains("this[nuint ")) &&
                                CountIndexerParameters(lines[j].TrimStart()) == indexerParamCount)
                            {
                                targetExists = true;
                                break;
                            }
                        }
                        if (!targetExists)
                        {
                            int preambleStart = ScanBackwardForPreamble(lines, i);
                            for (int j = preambleStart; j <= blockEnd; j++)
                                removals.Add(j);
                        }
                    }
                    continue;
                }

                // === Method narrowing ===
                // Expression-bodied methods with (nint)/(nuint) casts calling same method name
                if (!IsPotentialMemberDeclaration(trimmed)) continue;

                // Find the expression body extent (may span multiple lines for wrapped signatures)
                int arrowLine = -1;
                int methodBlockEnd = i;
                for (int j = i; j < Math.Min(i + 5, lines.Count); j++)
                {
                    if (lines[j].Contains("{") && !lines[j].Contains("=>")) break; // Block-bodied
                    if (lines[j].Contains("=>"))
                    {
                        arrowLine = j;
                        break;
                    }
                }
                if (arrowLine < 0) continue;

                // Find the semicolon ending the expression body
                for (int j = arrowLine; j < Math.Min(arrowLine + 5, lines.Count); j++)
                {
                    if (lines[j].TrimEnd().TrimEnd('\r', '\n').TrimEnd().EndsWith(";"))
                    {
                        methodBlockEnd = j;
                        break;
                    }
                }

                // Check for narrowing cast in the expression body
                bool hasNarrowingCast = false;
                for (int j = arrowLine; j <= methodBlockEnd; j++)
                {
                    if (lines[j].Contains("(nint)") || lines[j].Contains("(nuint)"))
                    {
                        hasNarrowingCast = true;
                        break;
                    }
                }
                if (!hasNarrowingCast) continue;

                // Extract method name from declaration
                var declName = ExtractMemberName(trimmed);
                if (declName == null) continue;

                // Check if the expression body calls the same method name
                bool callsSelf = false;
                for (int j = arrowLine; j <= methodBlockEnd; j++)
                {
                    int arrowIdx = lines[j].IndexOf("=>", StringComparison.Ordinal);
                    var searchText = arrowIdx >= 0 ? lines[j].Substring(arrowIdx + 2) : lines[j];
                    if (ContainsCallTo(searchText, declName))
                    {
                        callsSelf = true;
                        break;
                    }
                }
                if (!callsSelf) continue;

                // Check if the target method (with nint/nuint params and same param count) exists in same type.
                // Must match param count to avoid false positives from unrelated overloads
                // (e.g., Foo(string, nint) should not satisfy the target for Foo(int) => Foo((nint)x)).
                var containingTypeM = i < lineToType.Length ? lineToType[i] : null;
                int declParamCount = CountParameters(trimmed);
                bool targetMethodExists = false;
                for (int j = 0; j < lines.Count; j++)
                {
                    if (removals.Contains(j)) continue;
                    if (j >= i && j <= methodBlockEnd) continue;
                    if (containingTypeM != null && j < lineToType.Length && lineToType[j] != containingTypeM)
                        continue;
                    var jTrimmed = lines[j].TrimStart();
                    if (!IsPotentialMemberDeclaration(jTrimmed)) continue;
                    var jName = ExtractMemberName(jTrimmed);
                    if (jName == declName && (jTrimmed.Contains("nint ") || jTrimmed.Contains("nuint ")) &&
                        CountParameters(jTrimmed) == declParamCount)
                    {
                        targetMethodExists = true;
                        break;
                    }
                }
                if (!targetMethodExists)
                {
                    int preambleStart = ScanBackwardForPreamble(lines, i);
                    for (int j = preambleStart; j <= methodBlockEnd; j++)
                        removals.Add(j);
                }
            }
        }

        #endregion

        #region Proxy Reference Co-Gating

        /// <summary>
        /// Processes a single C# source file, removing method bodies that construct
        /// suppressed proxy classes (whose EveryProtocol conformance was not emitted).
        /// Uses the same transitive closure approach as the main co-gater.
        /// Interface member implementations are protected: their bodies are replaced with
        /// throw NotSupportedException instead of being stripped (prevents CS0535).
        /// </summary>
        public static CoGatingResult ProcessSuppressedProxyReferences(string content, IReadOnlySet<string> suppressedProxyClassNames)
        {
            if (suppressedProxyClassNames.Count == 0 || string.IsNullOrEmpty(content))
                return new CoGatingResult { Content = content, StrippedMemberCount = 0 };

            var lines = SplitLines(content);
            var removals = new HashSet<int>();
            var replacements = new Dictionary<int, (int blockStart, int blockEnd, string indent, bool isCallback, bool isVoidReturn)>();

            // Build interface member protection (same as main co-gater)
            var interfaceMembers = ParseInterfaceMembers(lines);
            var typeProtectedMembers = BuildTypeProtectedMembers(lines, interfaceMembers);
            var lineToType = BuildLineToTypeMap(lines);

            // Find method bodies that construct suppressed proxy classes.
            // Pattern: "new {ProxyClassName}(" in a method/property body.
            var strippedCallerNames = new HashSet<string>();
            var strippedCallerScopes = new List<(string name, string type)>();
            int i = 0;
            while (i < lines.Count)
            {
                if (removals.Contains(i)) { i++; continue; }

                var trimmed = lines[i].TrimStart();
                if (IsTypeOrNamespaceDeclaration(trimmed)) { i++; continue; }
                if (!IsPotentialMemberDeclaration(trimmed)) { i++; continue; }

                int braceOpenLine = FindOpeningBrace(lines, i);
                if (braceOpenLine < 0 || braceOpenLine > i + 3) { i++; continue; }

                int blockEnd = FindBlockEnd(lines, braceOpenLine);
                if (blockEnd < braceOpenLine) { i++; continue; }

                // Check if the block body contains a proxy construction
                bool referencesProxy = false;
                for (int j = i; j <= blockEnd && !referencesProxy; j++)
                {
                    foreach (var proxyName in suppressedProxyClassNames)
                    {
                        if (lines[j].Contains($"new {proxyName}(", StringComparison.Ordinal) ||
                            lines[j].Contains($"new SwiftInterop.{proxyName}(", StringComparison.Ordinal))
                        {
                            referencesProxy = true;
                            break;
                        }
                    }
                }

                if (!referencesProxy)
                {
                    i = blockEnd + 1;
                    continue;
                }

                // [UnmanagedCallersOnly] methods are proxy receiver callbacks referenced by
                // function pointers in vtable assignments. They can't be stripped (breaks vtable),
                // but their body may reference a suppressed proxy type. Replace the body with a
                // no-op stub (these are always static void callbacks from Swift).
                bool hasUnmanagedCallersOnly = false;
                for (int j = ScanBackwardForPreamble(lines, i); j < i; j++)
                {
                    if (lines[j].Contains("UnmanagedCallersOnly", StringComparison.Ordinal))
                    {
                        hasUnmanagedCallersOnly = true;
                        break;
                    }
                }
                if (hasUnmanagedCallersOnly)
                {
                    var declLine = lines[i];
                    var indent = new string(' ', declLine.Length - declLine.TrimStart().Length);
                    // Detect return type: non-void callbacks (e.g. returning IntPtr) need
                    // a return statement to avoid CS0161 (not all code paths return a value).
                    bool isVoidReturn = declLine.Contains(" void ", StringComparison.Ordinal);
                    replacements[i] = (braceOpenLine, blockEnd, indent, isCallback: true, isVoidReturn);
                    i = blockEnd + 1;
                    continue;
                }

                // Check if this is an interface member implementation
                bool isInterfaceMember = false;
                if (typeProtectedMembers.Count > 0)
                {
                    var containingType = i < lineToType.Length ? lineToType[i] : null;
                    if (containingType != null &&
                        typeProtectedMembers.TryGetValue(containingType, out var protectedNames))
                    {
                        var memberName = ExtractMemberName(trimmed);
                        if (memberName != null)
                        {
                            var publicApiName = IsPropertyHelperName(memberName)
                                ? memberName.Substring(0, memberName.LastIndexOf('_'))
                                : memberName;
                            isInterfaceMember = protectedNames.Contains(publicApiName);
                        }
                    }
                }

                if (isInterfaceMember)
                {
                    // Replace body with throw instead of stripping — preserves interface compliance.
                    // Compute the indentation from the declaration line.
                    var declLine = lines[i];
                    var indent = new string(' ', declLine.Length - declLine.TrimStart().Length);
                    replacements[i] = (braceOpenLine, blockEnd, indent, isCallback: false, isVoidReturn: false);
                }
                else
                {
                    // Extract member name and containing type for scope-aware Level 2 stripping
                    var memberName = ExtractMemberName(trimmed);
                    if (memberName != null && IsPropertyHelperName(memberName))
                    {
                        var containingType = i < lineToType.Length ? lineToType[i] : null;
                        strippedCallerNames.Add(memberName);
                        if (containingType != null)
                            strippedCallerScopes.Add((memberName, containingType));
                    }

                    int preambleStart = ScanBackwardForPreamble(lines, i);
                    for (int j = preambleStart; j <= blockEnd; j++)
                        removals.Add(j);
                }

                i = blockEnd + 1;
            }

            // Level 2: strip property forwarders that delegate to stripped helpers.
            // Use scope-aware matching when the same method name appears in multiple types
            // to avoid false-matching innocent methods in other types. E.g., "Subscript_Get"
            // may exist in both PersistenceContainer (proxy ref) and Row (no proxy) —
            // the Row version must be preserved.
            if (strippedCallerNames.Count > 0)
            {
                var ambiguous = FindAmbiguousMethodDeclarations(lines, strippedCallerNames);
                // For non-ambiguous names, use standard file-wide matching
                var safeNames = new HashSet<string>(strippedCallerNames);
                safeNames.ExceptWith(ambiguous);
                if (safeNames.Count > 0)
                {
                    var _ = new HashSet<string>();
                    FindAndMarkCallers(lines, safeNames, _, removals);
                }
                // For ambiguous names, use scope-aware matching
                foreach (var ambName in ambiguous)
                {
                    var types = strippedCallerScopes
                        .Where(s => s.name == ambName)
                        .Select(s => s.type)
                        .ToHashSet();
                    if (types.Count == 0) continue;
                    FindAndMarkCallersInScopes(lines, ambName, types, lineToType, removals);
                }
            }

            // Level 3: Remove orphaned subscript/property narrowing overloads.
            // These are single-line forwarders like "this[int x] => this[(nint)x];" that
            // delegate to a broader overload. If the target was removed, the narrowing
            // becomes a compile error (CS1503). Strip them.
            StripOrphanedNarrowingOverloads(lines, removals, lineToType);

            if (removals.Count == 0 && replacements.Count == 0)
                return new CoGatingResult { Content = content, StrippedMemberCount = 0 };

            var sb = new StringBuilder();
            for (int j = 0; j < lines.Count; j++)
            {
                if (removals.Contains(j))
                    continue;

                // Check if this line starts a replacement block
                if (replacements.TryGetValue(j, out var replacement))
                {
                    // Keep the declaration line(s) up to the opening brace
                    for (int k = j; k <= replacement.blockStart; k++)
                        sb.Append(lines[k]);
                    if (replacement.isCallback)
                    {
                        // UnmanagedCallersOnly callback: no-op stub (called from Swift vtable).
                        sb.Append($"{replacement.indent}    // Protocol proxy unavailable — no-op callback\n");
                        // Non-void callbacks (e.g. returning IntPtr) need a return to avoid CS0161.
                        if (!replacement.isVoidReturn)
                            sb.Append($"{replacement.indent}    return default;\n");
                    }
                    else
                    {
                        // Interface member: throw to preserve interface compliance
                        sb.Append($"{replacement.indent}    throw new NotSupportedException(\"Protocol proxy not available: EveryProtocol conformance was not emitted.\");\n");
                    }
                    // Keep the closing brace
                    sb.Append(lines[replacement.blockEnd]);
                    j = replacement.blockEnd;
                    continue;
                }

                sb.Append(lines[j]);
            }

            int totalAffected = removals.Count > 0 ? 1 + strippedCallerNames.Count + replacements.Count : replacements.Count;
            return new CoGatingResult
            {
                Content = sb.ToString(),
                StrippedMemberCount = totalAffected > 0 ? totalAffected : 1
            };
        }

        /// <summary>
        /// Processes all .cs files in a directory, removing methods that reference suppressed proxy classes.
        /// Files are modified in-place.
        /// </summary>
        public static int ProcessSuppressedProxyReferencesInDirectory(string directory, IReadOnlySet<string> suppressedProxyClassNames, ILogger? logger = null)
        {
            if (suppressedProxyClassNames.Count == 0)
                return 0;

            var csFiles = Directory.GetFiles(directory, "*.cs");
            int totalStripped = 0;

            foreach (var file in csFiles)
            {
                var content = File.ReadAllText(file);
                var result = ProcessSuppressedProxyReferences(content, suppressedProxyClassNames);
                if (result.StrippedMemberCount > 0)
                {
                    File.WriteAllText(file, result.Content);
                    totalStripped += result.StrippedMemberCount;
                    logger?.LogInformation("  Co-gated {Count} proxy reference(s) from {File}",
                        result.StrippedMemberCount, Path.GetFileName(file));
                }
            }

            if (totalStripped > 0)
                logger?.LogInformation("Co-gated {Count} total method(s) referencing suppressed proxy classes.",
                    totalStripped);

            return totalStripped;
        }

        #endregion
    }
}
