// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace BindingsGeneration
{
    /// <summary>
    /// Reconciles generated C# against the Swift wrapper-compile strip leg: when wrapper
    /// compilation strips a Swift <c>@_cdecl</c> symbol, this removes the orphaned C# P/Invoke
    /// and its transitive callers (3-level closure: P/Invoke → caller → property forwarder),
    /// preventing a <see cref="System.DllNotFoundException"/> at runtime.
    /// <para>
    /// <b>7b liability — delete with the Swift wrapper strip leg.</b> This is the sole surviving
    /// slice of the retired generate-then-strip co-gater post-pass: trigger #2 (the in-band
    /// wrapper-symbol contract) and trigger #3 (suppressed proxy references) are now decided at
    /// emission, not clawed back over the emitted text. This component lives only to reconcile C#
    /// against the Swift strip leg (task 7b); it is dead the day that leg is retired and must not
    /// masquerade as live architecture.
    /// </para>
    /// </summary>
    public static class StrippedSymbolCSharpReconciler
    {
        /// <summary>
        /// Result of reconciling a single C# source file. <see cref="StrippedMembers"/> carries
        /// stable identities (one per member decision, never collapsed for overloads) so the
        /// rederived <see cref="BindingReport"/> reflects post-reconciliation reality. Mangled symbols
        /// are populated when the reconciliation path has them (P/Invoke EntryPoint); otherwise identity
        /// is heuristic and item 4 will tighten it.
        /// <para>
        /// <see cref="ContentChanged"/> is the write gate — independent of identity count because
        /// trampoline-only removals (a stripped private P/Invoke with no public caller) modify
        /// the file without producing any public-API identity. Callers must use this flag, not
        /// <see cref="StrippedMemberCount"/>, to decide whether to write the file back.
        /// </para>
        /// </summary>
        public sealed class CoGatingResult
        {
            public required string Content { get; init; }
            public required IReadOnlyList<CoGatedMember> StrippedMembers { get; init; }
            public required bool ContentChanged { get; init; }
            public int StrippedMemberCount => StrippedMembers.Count;

            internal static CoGatingResult Empty(string content) => new()
            {
                Content = content,
                StrippedMembers = Array.Empty<CoGatedMember>(),
                ContentChanged = false,
            };
        }

        private static readonly Regex EntryPointRegex = new(
            @"EntryPoint\s*=\s*""([^""]+)""",
            RegexOptions.Compiled);

        // Matches LibraryImport/DllImport("SwiftBindings" or "XyzSwiftBindings" or "libSwiftBindings"
        // but NOT "SwiftBindingsTestLib". The wrapper library name is always "{ModuleName}SwiftBindings",
        // "libSwiftBindings", or just "SwiftBindings". Both the source-generated LibraryImport+partial
        // shape and the older DllImport+`static extern` shape (KeyPath/AppEntity/enum-metadata emitters)
        // target the wrapper lib, so a stripped wrapper symbol behind either must be co-gated.
        private static readonly Regex WrapperLibraryImportRegex = new(
            @"(?:Library|Dll)Import\(""(\w*SwiftBindings)""",
            RegexOptions.Compiled);

        // Matches a single-line static function-pointer field initialized with the address of a
        // method group — "static ... <field> = &<method>;" (e.g. the closure-callback dispatch
        // field "static ... s_modifier_Set_Callback = &modifier_Set_Callback;"). Greedy ".*"
        // backtracks to the final "= &", so group 1 is the field name and group 2 the target
        // method. Used by Step B2 to detect fields orphaned by a stripped callback method.
        private static readonly Regex AddressOfFieldRegex = new(
            @"\bstatic\b.*\b(\w+)\s*=\s*&(\w+)\s*;",
            RegexOptions.Compiled);

        /// <summary>
        /// Processes a single C# source file, removing members that reference stripped wrapper symbols.
        /// Uses 3-level transitive closure: P/Invoke → caller → property forwarder.
        /// </summary>
        public static CoGatingResult Process(string content, IReadOnlySet<string> strippedSymbols)
        {
            if (strippedSymbols.Count == 0 || string.IsNullOrEmpty(content))
                return CoGatingResult.Empty(content);

            var lines = SplitLines(content);
            var removals = new HashSet<int>();

            // Step A: Find P/Invoke declarations targeting stripped wrapper symbols.
            // Collect candidates with their line ranges — don't apply removals yet,
            // because some P/Invokes may be exempted by GetMetadata fallback callers.
            var candidatePInvokes = new Dictionary<string, (int preambleStart, int declEnd)>();
            FindStrippedPInvokeCandidates(lines, strippedSymbols, candidatePInvokes);

            if (candidatePInvokes.Count == 0)
                return CoGatingResult.Empty(content);

            // Build line → qualified type path map. Must be computed before
            // BuildTypeProtectedMembers so both use the same qualified-path keys.
            var lineToType = BuildLineToTypeMap(lines);

            // Step A2: Build per-type interface member protection.
            // If stripping a member would remove an interface implementation, the type
            // would fail to compile (CS0535). Scoped to the actual interfaces each type implements.
            var interfaceMembers = ParseInterfaceMembers(lines);
            var typeProtectedMembers = BuildTypeProtectedMembers(lines, interfaceMembers, lineToType);

            // Step A3: Exempt P/Invokes whose callers are non-strippable:
            // - DllNotFoundException fallback (GetMetadata pattern)
            // - Interface implementations (member name matches an interface the containing type implements)
            var exemptedNames = FindExemptedPInvokes(lines, candidatePInvokes.Keys, typeProtectedMembers, lineToType);

            // Step A4: Detect scope-ambiguous method names.
            // If a P/Invoke method name (e.g., "PInvoke_eq") appears in multiple class scopes,
            // file-wide caller detection would false-match across scopes. Skip these entirely.
            var ambiguousNames = FindAmbiguousMethodNames(lines, candidatePInvokes.Keys);

            // Apply P/Invoke removals for non-exempted, non-ambiguous names. P/Invoke decls
            // are internal trampolines — track only enough to drive caller detection in
            // Steps B–G. Identity capture happens at the public-API level once all steps
            // have settled; consumers care which bound surface disappeared, not which
            // generated trampoline got stripped.
            var strippedPInvokeNames = new HashSet<string>();
            var publicDeclLines = new HashSet<int>();
            foreach (var (name, (preambleStart, declEnd)) in candidatePInvokes)
            {
                if (exemptedNames.Contains(name) || ambiguousNames.Contains(name))
                    continue;
                strippedPInvokeNames.Add(name);
                for (int j = preambleStart; j <= declEnd; j++)
                    removals.Add(j);
            }

            if (strippedPInvokeNames.Count == 0)
                return CoGatingResult.Empty(content);

            // Step A5: Reconcile the generated [ModuleInitializer] aggregator BEFORE the
            // generic caller walk. Its body is a flat list of independent, best-effort
            // registration statements (factory / payload-semantics / conformance / enum-metadata),
            // each emitted as a single self-contained `try { ... } catch { }` line. Exactly one
            // class of those — the enum-metadata `RegisterMetadata(typeof(X), FromHandle(
            // __GetEnumMetadata_Y()))` registration — calls a wrapper-symbol P/Invoke, so when
            // that accessor's symbol is stripped the registration line references a removed extern.
            // Step B would see that reference and delete the ENTIRE initializer as a "caller",
            // taking every unrelated factory/payload/conformance registration with it — which
            // silently un-registers types whose payload-construction semantics or metadata the
            // runtime then can't resolve on NativeAOT (the reflective fallback is trimmed). Remove
            // only the offending single-line statement(s) here; with the reference gone, Step B
            // leaves the initializer (and all its surviving registrations) intact.
            StripOrphanedModuleInitializerRegistrations(lines, strippedPInvokeNames, removals);

            // Step B: Find Level 1 callers (methods/constructors calling stripped P/Invokes).
            var strippedCallerNames = new HashSet<string>();
            var callerNameToTypes = new Dictionary<string, HashSet<string>>();
            FindAndMarkCallers(lines, strippedPInvokeNames, strippedCallerNames, removals,
                lineToType, callerNameToTypes, publicDeclLines);

            // Step B2: Strip orphaned closure-callback function-pointer fields and their readers.
            // When Step B strips an [UnmanagedCallersOnly] callback method (its body calls a
            // stripped wrapper-symbol P/Invoke such as the unregistered SBW_CreateError error-mint),
            // the sibling one-line field "static ... s_<cb> = &<cb>;" is NOT a block member, so
            // FindAndMarkCallers leaves it dangling on the now-missing method. The field name then
            // dangles in surviving readers ("new SwiftClosureData((IntPtr)s_<cb>, …)"). This closes
            // the stripping asymmetry — defense-in-depth;
            // the root cause is fixed by registering the error-mint helper, but symmetric stripping
            // keeps any future orphaned-field scenario producing compiling output instead of CS0103.
            // Runs after Step B (callbacks already removed) and before Step C so any
            // property-helper reader it surfaces feeds the transitive forwarder strip below.
            StripOrphanedClosureCallbackFields(lines, removals, lineToType, publicDeclLines,
                strippedCallerNames, callerNameToTypes);

            // Step C: Find Level 2 forwarders (properties delegating to stripped helpers).
            // SCOPE-AWARE: Only strip callers within the same type scope as the original
            // stripped member. Without this, a stripped "Id_Get" in TypeA would incorrectly
            // strip "Id_Get" references in TypeB/TypeC/etc. (property helper names like
            // "Id_Get" are not globally unique — multiple types can have properties named "id").
            foreach (var (callerName, types) in callerNameToTypes)
            {
                FindAndMarkCallersInScopes(lines, callerName, types, lineToType, removals, publicDeclLines);
            }

            // Step D: Strip orphaned lazy field accessors.
            // When a _lazy_X field is stripped (Step B — it calls PInvoke_CaseByIndex),
            // the expression-bodied property "Y => _lazy_X.Value;" becomes a dangling reference.
            // FindAndMarkCallers can't catch these because expression-bodied properties have no braces.
            StripOrphanedLazyAccessors(lines, removals, lineToType, publicDeclLines);

            // Step E: Strip dangling ToString() expression-bodied methods.
            // When the Description property is stripped (Steps B-D), the generated
            // "public override string ToString() => Description;" becomes a dangling reference.
            StripDanglingToString(lines, removals, publicDeclLines);

            // Step F: Strip orphaned narrowing overloads (int/uint → nint/nuint convenience wrappers)
            // whose delegate target was stripped. Handles single-line and multi-line indexers,
            // and expression-bodied method overloads.
            StripOrphanedNarrowingOverloads(lines, removals, lineToType, publicDeclLines);

            // Step G: Strip orphaned throwing-closure simplification facades.
            // ThrowingClosureSimplificationEmitter emits convenience overloads that call the
            // base overload by C# method name (not P/Invoke name), so they sit one hop outside
            // Step B's transitive closure. When the base is stripped, the facade's self-call
            // becomes CS1501/CS1503. Must run last — depends on Step B/C removals.
            StripOrphanedThrowingClosureFacades(lines, removals, lineToType, publicDeclLines);

            if (removals.Count == 0)
                return CoGatingResult.Empty(content);

            // Build output, skipping removed lines
            var sb = new StringBuilder();
            for (int i = 0; i < lines.Count; i++)
            {
                if (!removals.Contains(i))
                    sb.Append(lines[i]);
            }

            var identities = BuildPublicMemberIdentities(lines, publicDeclLines, lineToType);

            return new CoGatingResult
            {
                Content = sb.ToString(),
                StrippedMembers = identities,
                ContentChanged = true,
            };
        }

        /// <summary>
        /// Processes all .cs files in a directory, removing members targeting stripped wrapper symbols.
        /// Files are modified in-place. Returns the aggregate list of stripped member identities
        /// (overload-correct: duplicates preserved across files, ordinals scoped per file).
        /// </summary>
        public static IReadOnlyList<CoGatedMember> ProcessDirectory(string directory, IReadOnlySet<string> strippedSymbols, ILogger? logger = null)
        {
            if (strippedSymbols.Count == 0)
                return Array.Empty<CoGatedMember>();

            var csFiles = Directory.GetFiles(directory, "*.cs");
            var aggregate = new List<CoGatedMember>();

            foreach (var file in csFiles)
            {
                var content = File.ReadAllText(file);
                var result = Process(content, strippedSymbols);
                if (!result.ContentChanged)
                    continue;

                File.WriteAllText(file, result.Content);

                if (result.StrippedMemberCount == 0)
                    continue;

                var fileName = Path.GetFileName(file);
                foreach (var member in result.StrippedMembers)
                {
                    aggregate.Add(new CoGatedMember
                    {
                        Name = member.Name,
                        ContainingType = member.ContainingType,
                        Kind = member.Kind,
                        MangledSymbol = member.MangledSymbol,
                        Ordinal = member.Ordinal,
                        Confidence = member.Confidence,
                        SourceFile = fileName,
                    });
                }
                logger?.LogInformation("  Co-gated {Count} member(s) from {File}",
                    result.StrippedMemberCount, fileName);
            }

            if (aggregate.Count > 0)
                logger?.LogInformation("Co-gated {Count} total P/Invoke(s) and their callers from generated C#.",
                    aggregate.Count);

            return aggregate;
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
            List<string> lines, Dictionary<string, HashSet<string>> interfaceMembers,
            string?[]? lineToType = null)
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

                // Use qualified path from lineToType when available, leaf name as fallback.
                // lineToType may not have the value for this exact line yet (the opening
                // brace is on the next line), so scan forward a few lines.
                string? typeName = null;
                if (lineToType != null)
                {
                    for (int scan = i; scan < Math.Min(i + 3, lineToType.Length); scan++)
                    {
                        if (lineToType[scan] != null)
                        {
                            typeName = lineToType[scan];
                            break;
                        }
                    }
                }
                typeName ??= ExtractTypeName(trimmed);
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
        /// Builds a line → containing type path map via forward brace-depth tracking.
        /// Uses fully qualified dot-joined paths (e.g., "Transaction.AsyncIterator") so that
        /// nested types with the same leaf name in different parents are distinguishable.
        /// </summary>
        private static string?[] BuildLineToTypeMap(List<string> lines)
        {
            var map = new string?[lines.Count];
            var typeStack = new Stack<(string name, int openDepth)>();
            int depth = 0;
            string? pendingType = null;
            // Cache the current qualified path; rebuild when the stack changes.
            string? currentPath = null;
            bool pathDirty = false;
            // Carry block-comment nesting across lines so a brace inside a string/comment doesn't
            // corrupt the depth and mis-attribute later symbols to the wrong enclosing type.
            int blockCommentDepth = 0;

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

                StructuralBraceScanner.ScanLine(lines[i], ref blockCommentDepth, delta =>
                {
                    if (delta > 0)
                    {
                        depth++;
                        if (pendingType != null)
                        {
                            typeStack.Push((pendingType, depth));
                            pendingType = null;
                            pathDirty = true;
                        }
                    }
                    else
                    {
                        depth--;
                        // Pop types whose scope has closed (openDepth > current depth)
                        while (typeStack.Count > 0 && typeStack.Peek().openDepth > depth)
                        {
                            typeStack.Pop();
                            pathDirty = true;
                        }
                    }
                });

                if (pathDirty)
                {
                    currentPath = typeStack.Count > 0
                        ? string.Join(".", typeStack.Reverse().Select(t => t.name))
                        : null;
                    pathDirty = false;
                }

                map[i] = currentPath;
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
                if (braceOpenLine < 0 || braceOpenLine > i + 5) { i++; continue; }

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
                // Look for P/Invoke method declarations (partial or static-extern) that match candidate names
                var line = lines[i];
                if (!IsPInvokeSignatureLine(line))
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
        /// Checks if a line is a LibraryImport or DllImport attribute targeting a wrapper library.
        /// Wrapper libraries are named "{ModuleName}SwiftBindings", "libSwiftBindings", or just
        /// "SwiftBindings". Correctly excludes native library names like "SwiftBindingsTestLib".
        /// </summary>
        internal static bool IsWrapperLibraryImportLine(string line)
        {
            return line.Contains("EntryPoint") && WrapperLibraryImportRegex.IsMatch(line);
        }

        /// <summary>
        /// True when <paramref name="line"/> is a P/Invoke method signature line — either the
        /// source-generated <c>... static partial ...(...);</c> shape or the older
        /// <c>... static extern ...(...);</c> (DllImport) shape. Both terminate in a semicolon with
        /// no body. Centralizing the check keeps the decl scanners
        /// (<see cref="FindPartialDeclaration"/>, <see cref="FindAmbiguousMethodNames"/>)
        /// recognizing the same shapes in lockstep.
        /// <para>
        /// The check also requires <c> static </c> and a parameter list <c>(</c>: every emitted
        /// P/Invoke is a static method with a signature, and <c>partial</c> is a *contextual*
        /// keyword that can legally be an identifier (e.g. <c>var partial = Foo();</c>). Matching on
        /// the bare <c> partial </c>/<c> extern </c> token plus a trailing <c>;</c> alone would let
        /// such a body line — or a comment that happens to contain the token — be counted as a
        /// duplicate declaration by <see cref="FindAmbiguousMethodNames"/>,
        /// which inflates a name's decl count and then SUPPRESS the strip file-wide, leaving a dead
        /// wrapper symbol's P/Invoke (and its callers) behind. Requiring the static-method shape
        /// matches every real declaration while excluding those false positives.
        /// </para>
        /// </summary>
        private static bool IsPInvokeSignatureLine(string line)
            => line.Contains(" static ")
               && (line.Contains(" partial ") || line.Contains(" extern "))
               && line.Contains("(")
               && line.TrimEnd().EndsWith(";");

        private static int FindPartialDeclaration(List<string> lines, int fromLine)
        {
            for (int j = fromLine; j < Math.Min(fromLine + 5, lines.Count); j++)
            {
                if (IsPInvokeSignatureLine(lines[j]))
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
            HashSet<string> foundCallerNames, HashSet<int> removals,
            string?[]? lineToType = null,
            Dictionary<string, HashSet<string>>? callerNameToTypes = null,
            HashSet<int>? publicDeclLines = null,
            bool matchAsIdentifier = false)
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
                if (braceOpenLine < 0 || braceOpenLine > i + 5) { i++; continue; }

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
                        // Default: match "name(" call syntax. Identifier mode (Step B2) matches a
                        // bare identifier reference — orphaned function-pointer fields are read as
                        // address values ("(IntPtr)s_<cb>"), never invoked, so there's no "(".
                        bool hit = matchAsIdentifier
                            ? ContainsIdentifier(lineText, name)
                            : ContainsCallTo(lineText, name);
                        if (hit)
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
                {
                    foundCallerNames.Add(memberName);

                    // Track which type scope this caller belongs to, so Step C can
                    // restrict Level 2 stripping to the same scope. Without this,
                    // "Id_Get" stripped in PaymentMethodBinding would also strip
                    // "Id_Get" in Transaction, Storefront, etc.
                    if (lineToType != null && callerNameToTypes != null)
                    {
                        var containingType = i < lineToType.Length ? lineToType[i] : null;
                        if (containingType != null)
                        {
                            if (!callerNameToTypes.TryGetValue(memberName, out var types))
                            {
                                types = new HashSet<string>();
                                callerNameToTypes[memberName] = types;
                            }
                            types.Add(containingType);
                        }
                    }
                }

                // Mark for removal (including preamble: attributes, doc comments)
                int preambleStart = ScanBackwardForPreamble(lines, i);
                for (int j = preambleStart; j <= blockEnd; j++)
                    removals.Add(j);

                // Record the declaration line (not the preamble) for identity capture.
                // Only public surface — private/internal helpers are trampolines whose
                // names ("Value_Get", "PInvoke_*") are implementation noise to consumers.
                RecordPublicDecl(publicDeclLines, i, trimmed);

                i = blockEnd + 1;
            }
        }

        /// <summary>
        /// Removes the individual best-effort registration statement(s) inside the generated
        /// <c>[ModuleInitializer]</c> aggregator that call a stripped wrapper-symbol P/Invoke,
        /// leaving the initializer method (and every other registration in it) intact.
        /// <para>
        /// The initializer body is a flat list of self-contained single-line statements — the
        /// emitter writes each as one <c>WriteLines("...")</c> call (an optional availability
        /// <c>if (...) { ... }</c> guard is still single-line), so a removed line can never leave
        /// a dangling open brace. Of those statements only the enum-metadata registration
        /// (<c>RegisterMetadata(typeof(X), FromHandle(__GetEnumMetadata_Y()))</c>) calls a wrapper
        /// P/Invoke; when its accessor symbol is stripped, this drops just that one line. With the
        /// reference gone, the generic caller walk (Step B) no longer mistakes the whole aggregator
        /// for a thin forwarder and delete it — which would silently un-register every type's
        /// factory / payload-construction-semantics / conformance, breaking NativeAOT type
        /// resolution where the reflective fallback is trimmed.
        /// </para>
        /// </summary>
        private static void StripOrphanedModuleInitializerRegistrations(
            List<string> lines, HashSet<string> strippedPInvokeNames, HashSet<int> removals)
        {
            for (int i = 0; i < lines.Count; i++)
            {
                if (removals.Contains(i)) continue;
                if (!lines[i].TrimStart().StartsWith("[ModuleInitializer]", StringComparison.Ordinal))
                    continue;

                // The opening brace sits a line or two below the attribute (past the method
                // signature). Scan a short window rather than FindOpeningBrace, which bails on the
                // intervening signature line.
                int braceOpen = -1;
                for (int k = i; k < Math.Min(i + 4, lines.Count); k++)
                {
                    if (lines[k].Contains('{')) { braceOpen = k; break; }
                }
                if (braceOpen < 0) continue;

                int blockEnd = FindBlockEnd(lines, braceOpen);
                for (int j = braceOpen + 1; j < blockEnd; j++)
                {
                    if (removals.Contains(j)) continue;
                    if (!IsSelfContainedStatementLine(lines[j])) continue;
                    foreach (var name in strippedPInvokeNames)
                    {
                        if (ContainsCallTo(lines[j], name))
                        {
                            removals.Add(j);
                            break;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// True when <paramref name="line"/> is a complete, brace- and paren-balanced statement
        /// (e.g. a single-line <c>try { ... } catch { }</c> registration) so removing it on its own
        /// cannot orphan an open block. Returns false for block openers/closers (a leading or net
        /// unbalanced brace) so the initializer's own <c>{</c>/<c>}</c> are never dropped. The
        /// generated registration lines carry no brace/paren characters inside string literals, so a
        /// raw scan is sufficient here.
        /// </summary>
        private static bool IsSelfContainedStatementLine(string line)
        {
            int brace = 0, paren = 0;
            foreach (char c in line)
            {
                switch (c)
                {
                    case '{': brace++; break;
                    case '}': brace--; break;
                    case '(': paren++; break;
                    case ')': paren--; break;
                }
                if (brace < 0 || paren < 0) return false;
            }
            if (brace != 0 || paren != 0) return false;
            var trimmed = line.TrimEnd();
            return trimmed.EndsWith(";", StringComparison.Ordinal)
                || trimmed.EndsWith("}", StringComparison.Ordinal);
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

        /// <summary>
        /// Checks if a line references the given name as a standalone identifier (whole-word
        /// match, both sides). Unlike <see cref="ContainsCallTo"/> this does NOT require a
        /// trailing '(' — used by Step B2 to find reads of an orphaned function-pointer field
        /// (e.g. "(IntPtr)s_modifier_Set_Callback"), which are address values, not calls.
        /// Word boundaries on both ends prevent "s_X" from matching "s_X2" or "_s_X".
        /// </summary>
        private static bool ContainsIdentifier(string line, string name)
        {
            int idx = 0;
            while (idx < line.Length)
            {
                int pos = line.IndexOf(name, idx, StringComparison.Ordinal);
                if (pos < 0)
                    return false;
                bool beforeOk = pos == 0 || !IsIdentifierChar(line[pos - 1]);
                int after = pos + name.Length;
                bool afterOk = after >= line.Length || !IsIdentifierChar(line[after]);
                if (beforeOk && afterOk)
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

            for (int j = fromLine + 1; j < Math.Min(fromLine + 6, lines.Count); j++)
            {
                var trimmed = lines[j].TrimStart();
                if (trimmed.StartsWith("{", StringComparison.Ordinal))
                    return j;
                if (!string.IsNullOrWhiteSpace(trimmed) && !trimmed.StartsWith("[", StringComparison.Ordinal)
                    && !trimmed.StartsWith("where ", StringComparison.Ordinal))
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

        private static bool IsPropertyDeclaration(
            string trimmed,
            List<string> lines,
            int braceOpenLine,
            int blockEnd,
            out bool hasSetter)
        {
            var hasAccessor = false;
            hasSetter = false;

            for (int j = braceOpenLine + 1; j < blockEnd; j++)
            {
                var bodyTrimmed = lines[j].TrimStart();
                if (IsGetterAccessorLine(bodyTrimmed))
                    hasAccessor = true;
                if (IsSetterAccessorLine(bodyTrimmed))
                {
                    hasAccessor = true;
                    hasSetter = true;
                }
            }

            return hasAccessor || IsPropertyShapedDeclaration(trimmed);
        }

        private static bool IsGetterAccessorLine(string trimmed)
        {
            return trimmed.StartsWith("get ", StringComparison.Ordinal) ||
                   trimmed.StartsWith("get =>", StringComparison.Ordinal) ||
                   trimmed.StartsWith("get{", StringComparison.Ordinal) ||
                   trimmed == "get" || trimmed == "get;";
        }

        private static bool IsSetterAccessorLine(string trimmed)
        {
            return trimmed.StartsWith("set ", StringComparison.Ordinal) ||
                   trimmed.StartsWith("set =>", StringComparison.Ordinal) ||
                   trimmed.StartsWith("set{", StringComparison.Ordinal) ||
                   trimmed == "set" || trimmed == "set;";
        }

        private static bool IsPropertyShapedDeclaration(string trimmed)
        {
            if (trimmed.Contains('('))
                return false;

            // Events, delegates, and nested type declarations also lack '(' and have brace
            // bodies — exclude them so the heuristic does not misidentify them as
            // property-shaped and emit invalid get/set accessor replacements.
            if (ContainsKeywordToken(trimmed, "event") ||
                ContainsKeywordToken(trimmed, "class") ||
                ContainsKeywordToken(trimmed, "struct") ||
                ContainsKeywordToken(trimmed, "interface") ||
                ContainsKeywordToken(trimmed, "enum") ||
                ContainsKeywordToken(trimmed, "delegate"))
                return false;

            return ExtractMemberName(trimmed) != null;
        }

        private static bool ContainsKeywordToken(string line, string keyword)
        {
            int start = 0;
            while (true)
            {
                int idx = line.IndexOf(keyword, start, StringComparison.Ordinal);
                if (idx < 0) return false;
                char left = idx == 0 ? ' ' : line[idx - 1];
                int endIdx = idx + keyword.Length;
                char right = endIdx >= line.Length ? ' ' : line[endIdx];
                bool leftBoundary = !char.IsLetterOrDigit(left) && left != '_';
                bool rightBoundary = !char.IsLetterOrDigit(right) && right != '_';
                if (leftBoundary && rightBoundary) return true;
                start = idx + 1;
            }
        }

        private static bool IsKeyword(string name)
        {
            return name is "public" or "private" or "internal" or "protected" or
                   "static" or "virtual" or "override" or "sealed" or
                   "unsafe" or "partial" or "async" or "new" or "readonly" or
                   "abstract" or "extern" or "void" or "class" or "struct";
        }

        /// <summary>
        /// Strips expression-bodied properties that reference lazy fields removed by prior steps.
        /// Pattern: when "_lazy_X" field is stripped (it calls PInvoke_CaseByIndex which was stripped),
        /// "public static T Y => _lazy_X.Value;" becomes a dangling reference.
        /// These are expression-bodied (no braces), so FindAndMarkCallers can't detect them.
        /// </summary>
        private static void StripOrphanedLazyAccessors(List<string> lines, HashSet<int> removals, string?[] lineToType,
            HashSet<int>? publicDeclLines = null)
        {
            // Collect _lazy_ field names from removed lines, scoped by containing type.
            // Two enums in the same file can share _lazy_ field names (e.g., _lazy_none, _lazy_default),
            // so we must only strip accessors within the same type that owns the stripped field.
            var strippedLazyByType = new HashSet<(string type, string lazyName)>();
            for (int i = 0; i < lines.Count; i++)
            {
                if (!removals.Contains(i)) continue;
                var trimmed = lines[i].TrimStart();
                // Match lazy field declarations: "private static readonly Lazy<T> _lazy_fieldName = ..."
                if (!trimmed.Contains("_lazy_")) continue;
                var match = Regex.Match(trimmed, @"\b(_lazy_\w+)\b");
                if (match.Success)
                {
                    var containingType = i < lineToType.Length ? lineToType[i] : null;
                    if (containingType != null)
                        strippedLazyByType.Add((containingType, match.Groups[1].Value));
                }
            }

            if (strippedLazyByType.Count == 0)
                return;

            // Find expression-bodied members referencing stripped lazy fields within the same type
            for (int i = 0; i < lines.Count; i++)
            {
                if (removals.Contains(i)) continue;
                var line = lines[i];
                // Quick check: must contain "=>" and a lazy field name
                if (!line.Contains("=>")) continue;

                var accessorType = i < lineToType.Length ? lineToType[i] : null;
                if (accessorType == null) continue;

                foreach (var (type, lazyName) in strippedLazyByType)
                {
                    if (type == accessorType && line.Contains($"{lazyName}.Value"))
                    {
                        int preambleStart = ScanBackwardForPreamble(lines, i);
                        for (int j = preambleStart; j <= i; j++)
                            removals.Add(j);
                        RecordPublicDecl(publicDeclLines, i, line.TrimStart());
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// Step B2: Strips orphaned closure-callback function-pointer fields and the survivors
        /// that read them. When an <c>[UnmanagedCallersOnly]</c> callback method is stripped
        /// (its body calls a stripped wrapper-symbol P/Invoke), the sibling one-line field
        /// <c>static ... s_&lt;cb&gt; = &amp;&lt;cb&gt;;</c> is not a block member, so
        /// <see cref="FindAndMarkCallers"/> leaves it dangling. Its name then dangles in
        /// surviving readers (<c>(IntPtr)s_&lt;cb&gt;</c>), producing CS0103.
        /// <para>
        /// Strips the field plus any survivor referencing it by bare identifier. Property-helper
        /// readers (<c>X_Set</c>) feed <paramref name="callerNameToTypes"/> so the caller's Step C
        /// transitively strips the public forwarder. Each field is stripped only when its
        /// address-of target was a member removed FROM THE SAME TYPE, so a healthy build is a no-op,
        /// a stripped callback never leaves a compiling-but-dangling field behind, and a same-named
        /// live field in a sibling type (e.g. a synthesized-name hash collision) is preserved.
        /// </para>
        /// <para>
        /// Detection is line-based and assumes the generated field is emitted on a single line
        /// (<c>WriteLine</c> at every emission site today). This is best-effort defense-in-depth,
        /// not the primary guard: the root cause is fixed by registering the error-mint helper so
        /// the callback is never stripped in the first place. A future multi-line field emission
        /// would fall outside this detector, but the registration fix keeps the field from being
        /// orphaned at all, so it would not reintroduce CS0103.
        /// </para>
        /// </summary>
        private static void StripOrphanedClosureCallbackFields(
            List<string> lines, HashSet<int> removals, string?[] lineToType,
            HashSet<int>? publicDeclLines,
            HashSet<string> foundCallerNames,
            Dictionary<string, HashSet<string>> callerNameToTypes)
        {
            // Member names declared only on already-removed lines (stripped P/Invokes and their
            // [UnmanagedCallersOnly] callbacks), keyed by containing type. A function-pointer field
            // whose address-of target is one of these is dangling. Callback methods are private
            // static, so a field's "&<target>" always resolves within the field's own type — keying
            // by type lets the field gate below stay scope-restricted (no cross-type false strip on
            // a synthesized-name collision), mirroring Step B-scope / Step C. The flat union is the
            // fallback for the (defensive) case where a line resolves to no type.
            var strippedByType = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            var strippedUnion = new HashSet<string>(StringComparer.Ordinal);
            foreach (var idx in removals)
            {
                if (idx < 0 || idx >= lines.Count) continue;
                var name = ExtractMemberName(lines[idx].TrimStart());
                if (name == null) continue;
                strippedUnion.Add(name);
                var t = idx < lineToType.Length ? lineToType[idx] : null;
                if (t == null) continue;
                if (!strippedByType.TryGetValue(t, out var set))
                {
                    set = new HashSet<string>(StringComparer.Ordinal);
                    strippedByType[t] = set;
                }
                set.Add(name);
            }
            if (strippedUnion.Count == 0)
                return;

            // Surviving one-line "static ... <field> = &<method>;" fields whose target was stripped
            // from the same type. Track each orphaned field with its containing type so the reader
            // strip below stays scope-restricted (a sibling type may legitimately have a same-named
            // live field).
            var orphanedFieldsByType = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            var orphanedFieldsUnscoped = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < lines.Count; i++)
            {
                if (removals.Contains(i)) continue;
                var trimmed = lines[i].TrimStart();
                var m = AddressOfFieldRegex.Match(trimmed);
                if (!m.Success) continue;

                var target = m.Groups[2].Value;
                var containingType = i < lineToType.Length ? lineToType[i] : null;
                // Same-type match when the field's type is known; file-wide union only as the
                // defensive fallback for an unresolved type.
                bool targetStripped = containingType != null
                    ? strippedByType.TryGetValue(containingType, out var sameType) && sameType.Contains(target)
                    : strippedUnion.Contains(target);
                if (!targetStripped) continue;

                int preambleStart = ScanBackwardForPreamble(lines, i);
                for (int j = preambleStart; j <= i; j++)
                    removals.Add(j);

                var fieldName = m.Groups[1].Value;
                if (containingType != null)
                {
                    if (!orphanedFieldsByType.TryGetValue(fieldName, out var types))
                    {
                        types = new HashSet<string>(StringComparer.Ordinal);
                        orphanedFieldsByType[fieldName] = types;
                    }
                    types.Add(containingType);
                }
                else
                {
                    // A field that resolves to no type can't be scoped; fall back to a file-wide
                    // reader strip so the dangling read can't survive. Generated fields always sit
                    // inside a type, so this is a defensive path only.
                    orphanedFieldsUnscoped.Add(fieldName);
                }
            }
            if (orphanedFieldsByType.Count == 0 && orphanedFieldsUnscoped.Count == 0)
                return;

            // Strip survivors that read an orphaned field by bare identifier (the read site is an
            // address value, not a call). Scope to the field's own type — the reader is emitted
            // alongside the field + callback — so a same-named live field in a sibling type and its
            // reader survive (mirrors Step C's scope-awareness). Property-helper readers route into
            // callerNameToTypes so Step C strips their public forwarder.
            foreach (var (fieldName, types) in orphanedFieldsByType)
            {
                FindAndMarkCallersInScopes(lines, fieldName, types, lineToType, removals,
                    publicDeclLines, foundCallerNames, callerNameToTypes, matchAsIdentifier: true);
            }
            if (orphanedFieldsUnscoped.Count > 0)
            {
                FindAndMarkCallers(lines, orphanedFieldsUnscoped, foundCallerNames, removals,
                    lineToType, callerNameToTypes, publicDeclLines, matchAsIdentifier: true);
            }
        }

        /// <summary>
        /// Strips expression-bodied ToString() methods that reference properties removed by prior steps.
        /// Pattern: "public override string ToString() => PropertyName;" where PropertyName was stripped.
        /// </summary>
        private static void StripDanglingToString(List<string> lines, HashSet<int> removals,
            HashSet<int>? publicDeclLines = null)
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
                    RecordPublicDecl(publicDeclLines, i, trimmed);
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
        /// Step G: Strip orphaned throwing-closure simplification facades.
        /// <para>
        /// <c>ThrowingClosureSimplificationEmitter</c> emits public convenience overloads that
        /// wrap a caller-provided delegate and forward to the base throwing overload by C#
        /// method name (not P/Invoke name). Step B follows P/Invoke references only, so the
        /// facade's self-call sits one hop outside the transitive closure. When the base
        /// overload is stripped, the facade's <c>Method(_wrapped_x)</c> call becomes a
        /// dangling reference (CS1501/CS1503).
        /// </para>
        /// <para>
        /// This pass scans method declarations for facade-shaped bodies (wrapper-delegate
        /// setup plus a self-name call) whose same-name siblings in the same containing type
        /// have all been stripped, and removes those facades.
        /// </para>
        /// </summary>
        private sealed class FacadeMethodInfo
        {
            public int DeclStart;
            public int BlockEnd;
            public string ContainingType = "";
            public string MemberName = "";
            public bool IsFacade;
            public int FacadeCallArity;
            public Dictionary<string, string> WrappedVars = new(StringComparer.Ordinal);
            public Dictionary<string, string> FacadeParams = new(StringComparer.Ordinal);
            public List<string> SelfCallArgs = new();
            public bool HasSwiftResult;
            public int DeclArity;
            public string DeclarationText = "";
            public bool IsStripped;
        }

        private static void StripOrphanedThrowingClosureFacades(
            List<string> lines, HashSet<int> removals, string?[] lineToType,
            HashSet<int>? publicDeclLines = null)
        {
            var methods = new List<FacadeMethodInfo>();

            int i = 0;
            while (i < lines.Count)
            {
                var trimmed = lines[i].TrimStart();
                if (IsTypeOrNamespaceDeclaration(trimmed)) { i++; continue; }
                if (!IsPotentialMemberDeclaration(trimmed)) { i++; continue; }

                int braceOpenLine = FindOpeningBrace(lines, i);
                if (braceOpenLine < 0 || braceOpenLine > i + 5) { i++; continue; }

                int blockEnd = FindBlockEnd(lines, braceOpenLine);
                if (blockEnd < braceOpenLine) { i++; continue; }

                var containingType = i < lineToType.Length ? lineToType[i] : null;
                if (containingType == null) { i = blockEnd + 1; continue; }

                var memberName = ExtractMemberName(trimmed);
                if (memberName == null) { i = blockEnd + 1; continue; }

                var info = new FacadeMethodInfo
                {
                    DeclStart = i,
                    BlockEnd = blockEnd,
                    ContainingType = containingType,
                    MemberName = memberName,
                    IsStripped = removals.Contains(i),
                    DeclarationText = JoinDeclarationText(lines, i, braceOpenLine),
                    WrappedVars = ExtractFacadeWrappedVars(lines, braceOpenLine + 1, blockEnd),
                };
                if (info.WrappedVars.Count > 0)
                {
                    info.SelfCallArgs = TryExtractFacadeSelfCallArgs(
                        lines, braceOpenLine + 1, blockEnd, memberName);
                    info.FacadeCallArity = info.SelfCallArgs.Count;
                    info.FacadeParams = BuildParameterTypeMap(info.DeclarationText);
                }
                info.IsFacade = info.FacadeCallArity > 0;
                info.HasSwiftResult = !info.IsFacade && (
                    info.DeclarationText.Contains("SwiftResult<", StringComparison.Ordinal) ||
                    info.DeclarationText.Contains("Swift.SwiftResult<", StringComparison.Ordinal));
                info.DeclArity = CountParameters(info.DeclarationText);

                methods.Add(info);
                i = blockEnd + 1;
            }

            foreach (var group in methods.GroupBy(m => (m.ContainingType, m.MemberName)))
            {
                var liveFacades = group.Where(m => m.IsFacade && !m.IsStripped).ToList();
                if (liveFacades.Count == 0) continue;

                foreach (var f in liveFacades)
                {
                    // A valid call target for the facade must satisfy:
                    //   1. Carry a SwiftResult<...> closure marker — the emitter-stable
                    //      signature of the throwing base the facade forwards to.
                    //   2. Accept the same number of arguments as the facade's self-call.
                    //   3. At each ordinal where the facade passes a _wrapped_* variable,
                    //      the candidate's parameter at that ordinal must textually contain
                    //      the variable's declared delegate type. Matching positionally
                    //      (not just "somewhere in the declaration") is required because a
                    //      multi-closure facade can reuse the same delegate type for several
                    //      arguments, and a live overload that satisfies only one slot but
                    //      not the other would still emit CS1503 if the facade were kept.
                    var validTargets = group
                        .Where(m => !m.IsFacade && m.HasSwiftResult && m.DeclArity == f.FacadeCallArity)
                        .Where(m => FacadeSelfCallBindsPositionally(f, m))
                        .ToList();
                    if (validTargets.Count == 0) continue;
                    if (!validTargets.All(m => m.IsStripped)) continue;

                    int preambleStart = ScanBackwardForPreamble(lines, f.DeclStart);
                    for (int j = preambleStart; j <= f.BlockEnd; j++)
                        removals.Add(j);
                    RecordPublicDecl(publicDeclLines, f.DeclStart, lines[f.DeclStart].TrimStart());
                }
            }
        }

        /// <summary>
        /// Walks the facade body for lines declaring a <c>_wrapped_*</c> wrapper variable and
        /// returns a map of variable name → declared delegate type. The emitter writes each
        /// wrapper as <c>{originalType} _wrapped_{paramName} = (...) => ...;</c> — keeping
        /// the name→type association lets the positional matcher verify, per self-call
        /// argument position, that the base overload's parameter at that ordinal contains
        /// the delegate type the wrapper would pass.
        /// </summary>
        private static Dictionary<string, string> ExtractFacadeWrappedVars(List<string> lines, int bodyStart, int bodyEnd)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int j = bodyStart; j <= bodyEnd; j++)
            {
                var line = lines[j];
                int searchFrom = 0;
                while (true)
                {
                    int wIdx = line.IndexOf("_wrapped_", searchFrom, StringComparison.Ordinal);
                    if (wIdx < 0) break;

                    // Require an identifier char before the marker to anchor on the full token.
                    if (wIdx > 0 && IsIdentifierChar(line[wIdx - 1]))
                    {
                        searchFrom = wIdx + 1;
                        continue;
                    }

                    // End of the variable name: run of identifier chars starting at wIdx.
                    int nameEnd = wIdx;
                    while (nameEnd < line.Length && IsIdentifierChar(line[nameEnd])) nameEnd++;
                    var varName = line.Substring(wIdx, nameEnd - wIdx);

                    // A declaration uses `= ` after the identifier. Skip usages (pass-as-arg).
                    int eq = nameEnd;
                    while (eq < line.Length && char.IsWhiteSpace(line[eq])) eq++;
                    if (eq >= line.Length || line[eq] != '=')
                    {
                        searchFrom = nameEnd;
                        continue;
                    }

                    // Walk backward from the char before _wrapped_ to extract the declared type.
                    int end = wIdx - 1;
                    while (end >= 0 && char.IsWhiteSpace(line[end])) end--;
                    int start = end;
                    int depth = 0;
                    while (start >= 0)
                    {
                        char c = line[start];
                        if (c == '>' || c == ']' || c == ')') depth++;
                        else if (c == '<' || c == '[' || c == '(') depth--;
                        else if (depth == 0 && (char.IsWhiteSpace(c) || c == '=' || c == ';'))
                            break;
                        start--;
                    }
                    start++;
                    if (end >= start)
                    {
                        var type = line.Substring(start, end - start + 1).Trim();
                        if (type.Length > 0 && !map.ContainsKey(varName))
                            map[varName] = type;
                    }
                    searchFrom = nameEnd;
                }
            }
            return map;
        }

        /// <summary>
        /// Positionally verifies that the facade's self-call will bind to the candidate base
        /// overload. At each argument ordinal:
        /// <list type="bullet">
        /// <item><description>If the arg is a known <c>_wrapped_*</c> variable, the candidate's
        /// parameter at that ordinal must textually contain the variable's declared delegate
        /// type.</description></item>
        /// <item><description>If the arg is a simple identifier matching one of the facade's
        /// own parameters (a pass-through), the candidate's parameter at that ordinal must
        /// parse to the same declared type (exact match after normalization). Substring
        /// matching is unsafe here: <c>URL</c> is a prefix of <c>URLRequest</c> and would
        /// falsely report a bind. If either side cannot be parsed, return <c>false</c> so
        /// we fail closed instead of preserving on incomplete evidence.</description></item>
        /// <item><description>Anything else (literals, expressions) is accepted permissively.</description></item>
        /// </list>
        /// Arguments and parameter names are normalized against verbatim identifiers so that
        /// <c>@event</c> and <c>event</c> map to the same lookup key.
        /// </summary>
        private static bool FacadeSelfCallBindsPositionally(FacadeMethodInfo facade, FacadeMethodInfo candidate)
        {
            var baseParams = SplitDeclarationParameters(candidate.DeclarationText);
            if (baseParams.Count != facade.SelfCallArgs.Count) return false;
            for (int idx = 0; idx < facade.SelfCallArgs.Count; idx++)
            {
                var arg = NormalizeVerbatimIdentifier(facade.SelfCallArgs[idx].Trim());
                if (facade.WrappedVars.TryGetValue(arg, out var wrappedType))
                {
                    if (!baseParams[idx].Contains(wrappedType, StringComparison.Ordinal))
                        return false;
                    continue;
                }
                if (IsSimpleIdentifier(arg) && facade.FacadeParams.TryGetValue(arg, out var paramType))
                {
                    if (string.IsNullOrEmpty(paramType)) return false;
                    var (_, candidateType) = ParseDeclarationParam(baseParams[idx]);
                    if (string.IsNullOrEmpty(candidateType)) return false;
                    if (!string.Equals(candidateType, paramType, StringComparison.Ordinal))
                        return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Returns true if <paramref name="s"/> is a plain C# identifier (letter/underscore
        /// followed by letters/digits/underscores), with no namespace/generic/call/index
        /// characters. Pass-through matching only accepts such simple identifiers.
        /// </summary>
        private static bool IsSimpleIdentifier(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            char first = s[0];
            if (!(char.IsLetter(first) || first == '_' || first == '@')) return false;
            for (int i = 1; i < s.Length; i++)
            {
                if (!IsIdentifierChar(s[i])) return false;
            }
            return true;
        }

        /// <summary>
        /// Parses a method declaration's parameter list into a map of parameter name →
        /// declared type (with attribute/modifier prefixes stripped and any default value
        /// clause removed). Parameters that cannot be parsed are skipped so the caller can
        /// detect the absence and fail closed.
        /// </summary>
        private static Dictionary<string, string> BuildParameterTypeMap(string declText)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var slice in SplitDeclarationParameters(declText))
            {
                var (name, type) = ParseDeclarationParam(slice);
                if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(type))
                    map[name] = type;
            }
            return map;
        }

        /// <summary>
        /// Splits a single C# parameter declaration into <c>(name, type)</c>. Strips leading
        /// attribute blocks, common parameter modifier keywords, any trailing default value
        /// clause, and normalizes verbatim identifiers (<c>@event</c> → <c>event</c>) so
        /// callers can compare both sides uniformly. Returns empty strings when the shape
        /// is not recognized so callers can fail closed.
        /// </summary>
        private static (string name, string type) ParseDeclarationParam(string paramText)
        {
            var s = paramText.Trim();
            if (s.Length == 0) return ("", "");

            int depth = 0;
            int eqIdx = -1;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '<' || c == '(' || c == '[') depth++;
                else if (c == '>' || c == ')' || c == ']') depth--;
                else if (c == '=' && depth == 0) { eqIdx = i; break; }
            }
            var head = (eqIdx >= 0 ? s.Substring(0, eqIdx) : s).TrimEnd();
            if (head.Length == 0) return ("", "");

            int end = head.Length;
            int start = end;
            while (start > 0 && IsIdentifierChar(head[start - 1])) start--;
            if (start == end) return ("", "");
            if (start > 0 && head[start - 1] == '@') start--;

            var name = NormalizeVerbatimIdentifier(head.Substring(start, end - start));
            var typePart = head.Substring(0, start).TrimEnd();

            while (typePart.StartsWith("[", StringComparison.Ordinal))
            {
                int close = typePart.IndexOf(']');
                if (close < 0) break;
                typePart = typePart.Substring(close + 1).TrimStart();
            }

            foreach (var mod in new[] { "ref ", "out ", "in ", "params ", "this " })
            {
                while (typePart.StartsWith(mod, StringComparison.Ordinal))
                    typePart = typePart.Substring(mod.Length).TrimStart();
            }

            typePart = typePart.Trim();
            if (typePart.Length == 0) return ("", "");
            return (name, typePart);
        }

        /// <summary>
        /// Strips a leading <c>@</c> verbatim-identifier prefix. Used to align parameter
        /// names with self-call argument text so lookups on <c>FacadeParams</c> hit
        /// regardless of which side spells a keyword-named identifier with <c>@</c>.
        /// </summary>
        private static string NormalizeVerbatimIdentifier(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return s[0] == '@' ? s.Substring(1) : s;
        }

        /// <summary>
        /// Splits the parameter list of a method declaration into ordered depth-0 slices.
        /// Returns an empty list when the declaration has no parenthesized parameter list.
        /// </summary>
        private static List<string> SplitDeclarationParameters(string declText)
        {
            var parts = new List<string>();
            int parenStart = declText.IndexOf('(');
            int parenEnd = declText.LastIndexOf(')');
            if (parenStart < 0 || parenEnd <= parenStart) return parts;
            var inside = declText.Substring(parenStart + 1, parenEnd - parenStart - 1);
            if (inside.Trim().Length == 0) return parts;

            int depth = 0;
            int segStart = 0;
            for (int i = 0; i < inside.Length; i++)
            {
                char c = inside[i];
                if (c == '<' || c == '(' || c == '[') depth++;
                else if (c == '>' || c == ')' || c == ']') depth--;
                else if (c == ',' && depth == 0)
                {
                    parts.Add(inside.Substring(segStart, i - segStart).Trim());
                    segStart = i + 1;
                }
            }
            var tail = inside.Substring(segStart).Trim();
            if (tail.Length > 0) parts.Add(tail);
            return parts;
        }

        /// <summary>
        /// Returns true if a method's declaration (from <paramref name="declStart"/> through
        /// the line containing its opening brace) carries a <c>SwiftResult&lt;...&gt;</c> closure
        /// parameter — the emitter-stable marker for the throwing-closure base overload that
        /// <c>ThrowingClosureSimplificationEmitter</c> facades forward to.
        /// </summary>
        private static bool HasSwiftResultInDeclaration(List<string> lines, int declStart, int braceOpenLine)
        {
            for (int j = declStart; j <= braceOpenLine && j < lines.Count; j++)
            {
                var text = lines[j];
                if (text.Contains("SwiftResult<", StringComparison.Ordinal) ||
                    text.Contains("Swift.SwiftResult<", StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Joins declaration lines from <paramref name="declStart"/> through
        /// <paramref name="braceOpenLine"/> into a single string so that multi-line signatures
        /// (wrapped parameter lists, generic <c>where</c> clauses) can be parsed uniformly.
        /// </summary>
        private static string JoinDeclarationText(List<string> lines, int declStart, int braceOpenLine)
        {
            var sb = new StringBuilder();
            for (int j = declStart; j <= braceOpenLine && j < lines.Count; j++)
                sb.Append(lines[j]);
            return sb.ToString();
        }

        /// <summary>
        /// Detects a throwing-closure simplification facade body and returns the argument
        /// count of its self-call, or 0 if the block is not a facade.
        /// <para>
        /// The emitter produces a distinctive pair of body lines (see
        /// <c>ThrowingClosureSimplificationEmitter.EmitSimplifiedOverload</c>):
        /// <c>{OriginalDelegate} _wrapped_{name} = ...;</c> followed by a self-name call
        /// <c>{methodName}(..., _wrapped_{name}, ...)</c>. Requiring both signals within the
        /// same block — and requiring the self-call arguments to reference a <c>_wrapped_</c>
        /// variable — keeps ordinary methods from being misclassified. The returned arity is
        /// used to gate base-candidate matching so the facade is only removed when no
        /// live overload can actually satisfy its call.
        /// </para>
        /// </summary>
        private static List<string> TryExtractFacadeSelfCallArgs(
            List<string> lines, int bodyStart, int bodyEnd, string methodName)
        {
            var empty = new List<string>();
            bool hasWrappedSetup = false;
            for (int j = bodyStart; j <= bodyEnd; j++)
            {
                if (lines[j].Contains("_wrapped_", StringComparison.Ordinal))
                {
                    hasWrappedSetup = true;
                    break;
                }
            }
            if (!hasWrappedSetup) return empty;

            var needle = methodName + "(";
            for (int j = bodyStart; j <= bodyEnd; j++)
            {
                var line = lines[j];
                int idx = 0;
                while (idx < line.Length)
                {
                    int pos = line.IndexOf(needle, idx, StringComparison.Ordinal);
                    if (pos < 0) break;
                    if (pos != 0 && IsIdentifierChar(line[pos - 1]))
                    {
                        idx = pos + 1;
                        continue;
                    }
                    int argListStart = pos + methodName.Length;
                    int closing = FindMatchingParen(lines, j, argListStart, bodyEnd);
                    if (closing < 0) { idx = pos + 1; continue; }
                    var callText = ExtractCallArgs(lines, j, argListStart + 1, closing);
                    if (!callText.Contains("_wrapped_", StringComparison.Ordinal))
                    {
                        idx = pos + 1;
                        continue;
                    }
                    var args = SplitCallArgs(callText);
                    if (args.Count > 0) return args;
                    idx = pos + 1;
                }
            }
            return empty;
        }

        /// <summary>
        /// Splits a call's depth-0 comma-separated argument text into ordered trimmed slices.
        /// Returns an empty list when the argument text is blank.
        /// </summary>
        private static List<string> SplitCallArgs(string argText)
        {
            var result = new List<string>();
            var trimmed = argText.Trim();
            if (trimmed.Length == 0) return result;
            int depth = 0;
            int start = 0;
            for (int i = 0; i < trimmed.Length; i++)
            {
                char c = trimmed[i];
                if (c == '(' || c == '<' || c == '[') depth++;
                else if (c == ')' || c == '>' || c == ']') depth--;
                else if (c == ',' && depth == 0)
                {
                    result.Add(trimmed.Substring(start, i - start).Trim());
                    start = i + 1;
                }
            }
            var tail = trimmed.Substring(start).Trim();
            if (tail.Length > 0) result.Add(tail);
            return result;
        }

        /// <summary>
        /// Finds the offset of the paren that matches the <c>(</c> at the given position.
        /// The returned tuple is encoded as <c>line * LargeConstant + column</c>; callers
        /// should extract via the helper <see cref="FindMatchingParen"/> which returns a
        /// single linear offset. Scans lines starting at <paramref name="startLine"/>
        /// through <paramref name="lastLine"/> inclusive.
        /// </summary>
        private static int FindMatchingParen(List<string> lines, int startLine, int openCol, int lastLine)
        {
            int depth = 0;
            for (int j = startLine; j <= lastLine && j < lines.Count; j++)
            {
                int col = j == startLine ? openCol : 0;
                var line = lines[j];
                for (; col < line.Length; col++)
                {
                    char c = line[col];
                    if (c == '(') depth++;
                    else if (c == ')')
                    {
                        depth--;
                        if (depth == 0) return j * 100000 + col;
                    }
                }
            }
            return -1;
        }

        private static string ExtractCallArgs(List<string> lines, int startLine, int startCol, int closingEncoded)
        {
            int endLine = closingEncoded / 100000;
            int endCol = closingEncoded % 100000;
            var sb = new StringBuilder();
            for (int j = startLine; j <= endLine && j < lines.Count; j++)
            {
                var line = lines[j];
                int from = j == startLine ? startCol : 0;
                int to = j == endLine ? endCol : line.Length;
                if (from < 0) from = 0;
                if (to > line.Length) to = line.Length;
                if (to > from) sb.Append(line, from, to - from);
            }
            return sb.ToString();
        }

        /// <summary>
        /// Finds the opening brace of the enclosing class/struct declaration.
        /// </summary>
        private static int FindEnclosingClassStart(List<string> lines, int fromLine)
        {
            int depth = 0;
            for (int j = fromLine; j >= 0; j--)
            {
                // Walking backward, a line closes (+1) on each structural '}' and opens (-1) on each
                // structural '{'; that is the negation of the forward opens-minus-closes net delta.
                // Reset block-comment state per line — a reverse scan meets a '/* */' close before its
                // open, so cross-line comment nesting can't be tracked here; on brace-free strings and
                // comments this stays byte-identical to the prior raw count, and the per-line string and
                // line-comment awareness is strictly better than the old comment-blind scan.
                int blockCommentDepth = 0;
                bool sawOpen = false;
                depth -= StructuralBraceScanner.NetLineDelta(lines[j], ref blockCommentDepth, ref sawOpen);
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
        /// Finds the end of a brace-delimited block starting at the given line. Braces inside string
        /// literals and comments are ignored so an emitted string such as <c>"}"</c> does not close the
        /// block early.
        /// </summary>
        internal static int FindBlockEnd(List<string> lines, int start)
        {
            int depth = 0;
            bool foundOpen = false;
            int blockCommentDepth = 0;
            for (int j = start; j < lines.Count; j++)
            {
                depth += StructuralBraceScanner.NetLineDelta(lines[j], ref blockCommentDepth, ref foundOpen);
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
        /// Scope-aware variant of FindAndMarkCallers. Only strips callers of the given method
        /// name if they are within one of the specified containing types.
        /// Prevents false-matching "Subscript_Get" in TypeB when only TypeA's version was stripped.
        /// </summary>
        /// <remarks>
        /// When <paramref name="foundCallerNames"/> and <paramref name="callerNameToTypes"/>
        /// are supplied, the method also records property-helper-shaped callers (e.g.,
        /// <c>Value_Get</c>) and their containing types, mirroring <see cref="FindAndMarkCallers"/>.
        /// This feeds Step C's transitive cascade so a scope-restricted Step B (used by the
        /// Swift-strip reconciliation path) still reaches Level-2 forwarders.
        /// </remarks>
        private static void FindAndMarkCallersInScopes(
            List<string> lines, string methodName, HashSet<string> allowedTypes,
            string?[] lineToType, HashSet<int> removals,
            HashSet<int>? publicDeclLines = null,
            HashSet<string>? foundCallerNames = null,
            Dictionary<string, HashSet<string>>? callerNameToTypes = null,
            bool matchAsIdentifier = false)
        {
            int i = 0;
            while (i < lines.Count)
            {
                if (removals.Contains(i)) { i++; continue; }

                var trimmed = lines[i].TrimStart();
                if (IsTypeOrNamespaceDeclaration(trimmed)) { i++; continue; }
                if (!IsPotentialMemberDeclaration(trimmed)) { i++; continue; }

                int braceOpenLine = FindOpeningBrace(lines, i);
                if (braceOpenLine < 0 || braceOpenLine > i + 5) { i++; continue; }

                int blockEnd = FindBlockEnd(lines, braceOpenLine);
                if (blockEnd < braceOpenLine) { i++; continue; }

                // Check scope: only strip if in an allowed type
                var containingType = i < lineToType.Length ? lineToType[i] : null;
                if (containingType == null || !allowedTypes.Contains(containingType))
                {
                    i = blockEnd + 1;
                    continue;
                }

                // Check if the block body calls (or, in identifier mode, references) the method.
                bool callsMethod = false;
                for (int j = i; j <= blockEnd; j++)
                {
                    bool hit = matchAsIdentifier
                        ? ContainsIdentifier(lines[j], methodName)
                        : ContainsCallTo(lines[j], methodName);
                    if (hit)
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
                    RecordPublicDecl(publicDeclLines, i, trimmed);

                    // Mirror FindAndMarkCallers: capture property-helper-shaped callers so
                    // Step C's cascade can find Level-2 forwarders in the same scope.
                    if (foundCallerNames != null || callerNameToTypes != null)
                    {
                        var memberName = ExtractMemberName(trimmed);
                        if (memberName != null && (IsPropertyHelperName(memberName) ||
                            memberName.StartsWith("CreateSwiftInstance_", StringComparison.Ordinal)))
                        {
                            foundCallerNames?.Add(memberName);
                            if (callerNameToTypes != null)
                            {
                                if (!callerNameToTypes.TryGetValue(memberName, out var types))
                                {
                                    types = new HashSet<string>();
                                    callerNameToTypes[memberName] = types;
                                }
                                types.Add(containingType);
                            }
                        }
                    }
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
        private static void StripOrphanedNarrowingOverloads(List<string> lines, HashSet<int> removals, string?[] lineToType,
            HashSet<int>? publicDeclLines = null)
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
                        if (braceOpenLine >= 0 && braceOpenLine <= i + 5)
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
                            RecordPublicDecl(publicDeclLines, i, trimmed);
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
                    RecordPublicDecl(publicDeclLines, i, trimmed);
                }
            }
        }

        #endregion

        #region Identity Extraction

        /// <summary>
        /// Returns the containing-type qualified path for a given line, or null when
        /// <see cref="BuildLineToTypeMap"/> didn't resolve it.
        /// </summary>
        private static string? ContainingTypeAt(string?[] lineToType, int lineIndex)
        {
            if (lineIndex < 0 || lineIndex >= lineToType.Length)
                return null;
            return lineToType[lineIndex];
        }

        /// <summary>
        /// Records a declaration line as part of the public-API surface that disappeared.
        /// Filters out private/internal trampolines (property helpers, P/Invoke wrappers)
        /// — these are implementation noise the consumer-facing report should not surface.
        /// Operators are <c>public static</c> too, so the leading-token check covers them.
        /// </summary>
        private static void RecordPublicDecl(HashSet<int>? publicDeclLines, int declLine, string trimmedDecl)
        {
            if (publicDeclLines == null) return;
            if (!trimmedDecl.StartsWith("public ", StringComparison.Ordinal)) return;
            publicDeclLines.Add(declLine);
        }

        /// <summary>
        /// Heuristic kind classifier for a removed declaration line. Subscript (this[…])
        /// and Operator are detected by their distinguishing tokens; the property/method
        /// split hinges on whether the declaration carries a parameter list before any
        /// expression-body arrow. Constructors fall through to Method, which matches the
        /// rest of the report (constructors are tracked as Method, not Type).
        /// </summary>
        private static BindingItemKind ClassifyMemberKind(string trimmed)
        {
            if (ContainsKeywordToken(trimmed, "operator"))
                return BindingItemKind.Operator;
            if (trimmed.Contains("this[", StringComparison.Ordinal))
                return BindingItemKind.Subscript;

            int parenIdx = trimmed.IndexOf('(');
            if (parenIdx < 0)
                return BindingItemKind.Property;

            int arrowIdx = trimmed.IndexOf("=>", StringComparison.Ordinal);
            if (arrowIdx >= 0 && parenIdx > arrowIdx)
                return BindingItemKind.Property;

            return BindingItemKind.Method;
        }

        /// <summary>
        /// Walks the recorded public declaration line indices and produces stable identities
        /// in source order so per-file ordinals stay deterministic across runs. Identities
        /// are <see cref="IdentityConfidence.Heuristic"/> — no mangled symbol is available
        /// at the C# layer (a public method has no 1:1 wrapper symbol; the wrapper that
        /// triggered the cascade is an internal trampoline and item 4 will tighten the
        /// link). Subscripts canonicalize to the name <c>this</c> because they have no
        /// member identifier in C# source.
        /// </summary>
        private static List<CoGatedMember> BuildPublicMemberIdentities(
            List<string> lines, HashSet<int> publicDeclLines, string?[] lineToType)
        {
            var identities = new List<CoGatedMember>();
            if (publicDeclLines.Count == 0)
                return identities;

            int ordinal = 0;
            foreach (var declLine in publicDeclLines.OrderBy(x => x))
            {
                if (declLine < 0 || declLine >= lines.Count)
                    continue;
                var trimmed = lines[declLine].TrimStart();
                var kind = ClassifyMemberKind(trimmed);
                var name = kind == BindingItemKind.Subscript
                    ? "this"
                    : ExtractMemberName(trimmed) ?? $"<unknown@{declLine}>";

                identities.Add(new CoGatedMember
                {
                    Name = name,
                    ContainingType = ContainingTypeAt(lineToType, declLine),
                    Kind = kind,
                    MangledSymbol = null,
                    Ordinal = ordinal++,
                    Confidence = IdentityConfidence.Heuristic,
                });
            }

            return identities;
        }

        #endregion
    }
}
