// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Serilog;

/// <summary>
/// Strips known-broken Swift wrapper code before compilation.
/// Ports the Python stripping logic from build-async-wrapper.sh.
/// The generator emits code for ALL features, including unsupported ones that
/// produce uncompilable Swift. This class strips those sections so the good
/// async method wrappers can compile.
/// </summary>
public static class SwiftSourceStripper
{
    // Protocols to preserve for runtime testing.
    // EveryProtocol conformances for these protocols are kept so proxy dispatch works at runtime.
    private static readonly HashSet<string> PreservedProtocols = new()
    {
        "HasValue", "ExistentialParamDelegate",
        "ProcessingMode",
        "Describable", "TestIdentifiable", "Displayable",
        "Nameable", "Ageable", "Addable", "Subtractable", "Multipliable", "Dividable",
        "Named", "Prioritized",
        "TaskDescriptor", "StringProcessor",
        "StatusHandler", "PriorityHandler",
        "URLProcessorDelegate",
        "EventDelegate",
        // Auto-wrap regression for justinwojo/swift-dotnet-bindings#16 (GDPerformanceView).
        // AutoWrappedDelegateTests drives Swift→C# callbacks through the property setter,
        // constructor arg, and method arg emit sites, so the EveryProtocol conformance and
        // its witness table accessor must survive wrapper stripping.
        "AutoWrappedMonitorDelegate",
        // Multi-protocol auto-wrap cache regression: same C# instance is wrapped for two
        // distinct protocols and dispatched via two distinct witness tables in the same
        // call. Both protocol conformances on EveryProtocol must survive stripping.
        "AutoWrappedSecondaryDelegate",
        // Proxy lifetime regression: ProxyLifetimeTests exercises the impl-anchored
        // EveryProtocol release path (tracker + Swift deinit callback). The fixture
        // lives in BindingTests/Sources/SwiftBindingsTestLib/Lifetime/ProxyLifetimeFixture.swift
        // and dispatches Swift→C# via a blittable ping() method.
        "ProxyLifetimeReceiver",
        // Vtable-slot-collision regression (Session 4a): DataLoadingDelegate has a
        // non-dispatchable closure method (onDataLoaded with multi-arg closure) plus a
        // non-closure method (sourceIdentifier). The C# proxy struct must omit the
        // closure slot to match Swift's omission; the runtime test drives this via
        // loader.Delegate = proxy → sourceIdentifier() round trip, so the EveryProtocol
        // conformance and witness table getter must survive stripping.
        "DataLoadingDelegate",
    };

    private static readonly Regex PreservedProtocolPattern = new(
        @"\b(" + string.Join("|", PreservedProtocols.Select(Regex.Escape)) + @")\b",
        RegexOptions.Compiled);

    private static readonly Regex ClosureParamPattern = new(
        @",\s*\w+:\s*\([^)]*\)\s*->", RegexOptions.Compiled);

    private static readonly Regex EveryProtocolExtensionHeader = new(
        @"^\s*extension\s+EveryProtocol\s*:\s*(?:[\w.]+\.)?(\w+)\b", RegexOptions.Compiled);

    // Captures `public [modifiers] func name(params)`, `public [modifiers] var/let name`,
    // or `public [modifiers] subscript`. `kind` discriminates so cross-extension witness
    // checks only match like-with-like (var-vs-func with the same bare name is a redeclaration,
    // not a witness — see `ProvidesCrossExtensionWitness`).
    private static readonly Regex DeclaredMember = new(
        @"\bpublic\s+(?:static\s+|nonisolated\s+|final\s+)*(?:(?<kind>func|var|let)\s+(?<name>\w+)|(?<sub>subscript)\b)",
        RegexOptions.Compiled);

    // `fileprivate struct <Protocol>_vtable {` — protocol witness vtable struct header.
    private static readonly Regex VtableStructHeader = new(
        @"^\s*fileprivate\s+struct\s+(\w+)_vtable\s*\{", RegexOptions.Compiled);

    // `var func_<barename>_get|set|<digit>` inside a vtable struct.
    // Suffix `_get`/`_set` → property (var); suffix `_<digit>` → method (func).
    // Greedy `.+` so names containing underscores (e.g. `snake_case`) survive intact.
    private static readonly Regex VtableField = new(
        @"\bvar\s+func_(?<name>.+)_(?<suffix>get|set|\d+)\b", RegexOptions.Compiled);

    /// <summary>
    /// Result of stripping a single file.
    /// </summary>
    public record StripResult(string OutputPath, int StrippedCount);

    /// <summary>
    /// Strips broken wrapper code from a Swift source file and writes the cleaned version.
    /// Returns the number of blocks stripped.
    /// </summary>
    public static StripResult StripFile(string inputPath, string outputPath)
    {
        var lines = File.ReadAllLines(inputPath);
        var outputLines = new List<string>();
        int removedCount = 0;
        int i = 0;
        bool seenUtf8Slice = false;
        bool seenEmptyBuffer = false;

        // Pre-scan: figure out which bare member names a preserved EveryProtocol conformance
        // depends on but doesn't declare in its own extension body. EveryProtocolEmitter dedups
        // same-signature witnesses across protocols by emitting the body in only one extension
        // and leaving siblings without it; Swift normally satisfies the empty conformance via
        // cross-extension method visibility. If the *only* extension declaring the witness is
        // a non-preserved one, stripping it here breaks the preserved sibling's conformance
        // with no recoverable error pattern. We keep a non-preserved extension only when it
        // declares at least one of those missing required names.
        var crossExtensionRequired = CollectCrossExtensionRequiredNames(lines);

        while (i < lines.Length)
        {
            var line = lines[i];
            var stripped = line.Trim();

            // Pattern 1: Skip EveryProtocol conformance extensions and class definition,
            // EXCEPT those for preserved protocols needed for runtime testing.
            if (stripped.StartsWith("extension EveryProtocol") || stripped.StartsWith("class EveryProtocol"))
            {
                int end = FindBlockEnd(lines, i);
                var body = ScanBlockBody(lines, i, end);
                if (!ReferencesPreservedProtocol(body) &&
                    !ProvidesCrossExtensionWitness(body, crossExtensionRequired))
                {
                    removedCount++;
                    i = end + 1;
                    continue;
                }
            }

            // Pattern 2: Skip @_silgen_name + function blocks that have broken patterns.
            if (stripped.StartsWith("@_silgen_name("))
            {
                int end = FindBlockEnd(lines, i);
                var body = ScanBlockBody(lines, i, end);

                if (IsBrokenSilgenBlock(lines, i, end, body))
                {
                    removedCount++;
                    i = end + 1;
                    continue;
                }
            }

            // Pattern 3: Skip extension blocks that contain broken code.
            if (stripped.StartsWith("extension ") && !stripped.StartsWith("extension EveryProtocol"))
            {
                int end = FindBlockEnd(lines, i);
                var body = ScanBlockBody(lines, i, end);

                if (IsBrokenExtensionBlock(body))
                {
                    removedCount++;
                    i = end + 1;
                    continue;
                }
            }

            // Pattern 4: Standalone public func blocks (without @_silgen_name prefix)
            if (stripped.StartsWith("public func SBW_") || stripped.StartsWith("public func PInvoke_"))
            {
                int end = FindBlockEnd(lines, i);
                var body = ScanBlockBody(lines, i, end);

                bool broken = false;
                if (body.Contains("EveryProtocol()"))
                {
                    if (!ReferencesPreservedProtocol(body))
                        broken = true;
                }
                if (!broken && body.Contains("let existential") && body.Contains("existential.") && body.Contains(".load(as: (any "))
                    broken = true;

                if (broken)
                {
                    removedCount++;
                    i = end + 1;
                    continue;
                }
            }

            // Fix: Strip @escaping from return type position
            if (line.Contains(") -> @escaping "))
                line = line.Replace(") -> @escaping ", ") -> ");

            // Fix: Strip @escaping from .load(as:) type context
            if (line.Contains(".load(as: @escaping "))
                line = line.Replace(".load(as: @escaping ", ".load(as: ");

            // Dedup: Skip duplicate SBW_Utf8Slice / _sbw_emptyBuffer declarations
            bool isUtf8SliceBlock = false;
            if (stripped.StartsWith("public struct SBW_Utf8Slice"))
            {
                isUtf8SliceBlock = true;
            }
            else if (stripped == "@frozen" && i + 1 < lines.Length && lines[i + 1].Contains("SBW_Utf8Slice"))
            {
                isUtf8SliceBlock = true;
            }

            if (isUtf8SliceBlock)
            {
                if (seenUtf8Slice)
                {
                    int end = FindBlockEnd(lines, i);
                    i = end + 1;
                    continue;
                }
                if (stripped.StartsWith("public struct SBW_Utf8Slice"))
                    seenUtf8Slice = true;
            }

            if (stripped.StartsWith("fileprivate var _sbw_emptyBuffer") || stripped.StartsWith("private var _sbw_emptyBuffer"))
            {
                if (seenEmptyBuffer)
                {
                    i++;
                    continue;
                }
                seenEmptyBuffer = true;
            }

            outputLines.Add(line);
            i++;
        }

        File.WriteAllLines(outputPath, outputLines);
        return new StripResult(outputPath, removedCount);
    }

    /// <summary>
    /// Strips broken functions from cleaned files based on compilation error line numbers.
    /// Used in the retry loop when initial compilation fails.
    /// </summary>
    public static int StripErrorFunctions(string cleanedDir, string compileErrors)
    {
        // Parse error line numbers per file
        var fileErrorLines = new Dictionary<string, HashSet<int>>();
        var errorPattern = new Regex(@"(.+\.swift):(\d+):\d+: error:");

        foreach (var errorLine in compileErrors.Split('\n'))
        {
            var match = errorPattern.Match(errorLine);
            if (match.Success)
            {
                var filename = Path.GetFileName(match.Groups[1].Value);
                var lineno = int.Parse(match.Groups[2].Value);
                if (!fileErrorLines.ContainsKey(filename))
                    fileErrorLines[filename] = new HashSet<int>();
                fileErrorLines[filename].Add(lineno);
            }
        }

        int totalStripped = 0;
        foreach (var (filename, errorLines) in fileErrorLines)
        {
            var filepath = Path.Combine(cleanedDir, filename);
            if (!File.Exists(filepath))
                continue;

            var lines = File.ReadAllLines(filepath);

            // Identify function blocks containing error lines
            var blocksToStrip = new HashSet<(int Start, int End)>();
            int idx = 0;
            while (idx < lines.Length)
            {
                var strippedLine = lines[idx].Trim();
                if (strippedLine.StartsWith("@_cdecl(") || strippedLine.StartsWith("@_silgen_name(")
                    || strippedLine.StartsWith("public func SBW_") || strippedLine.StartsWith("public func PInvoke_")
                    || strippedLine.StartsWith("public func _sbw_"))
                {
                    int end = FindBlockEnd(lines, idx);
                    foreach (var eline in errorLines)
                    {
                        // Error lines are 1-based, our indices are 0-based
                        if (idx + 1 <= eline && eline <= end + 1)
                        {
                            blocksToStrip.Add((idx, end));
                            break;
                        }
                    }
                    idx = end + 1;
                }
                else
                {
                    idx++;
                }
            }

            if (blocksToStrip.Count == 0)
                continue;

            // Walk backwards to include decorators and comments
            var expandedBlocks = new HashSet<(int Start, int End)>();
            foreach (var (start, end) in blocksToStrip)
            {
                int actualStart = start;
                while (actualStart > 0)
                {
                    var prev = lines[actualStart - 1].Trim();
                    if (prev.StartsWith("@_cdecl(") || prev.StartsWith("@_silgen_name(")
                        || prev.StartsWith("//") || prev.StartsWith("@MainActor"))
                    {
                        actualStart--;
                    }
                    else
                    {
                        break;
                    }
                }
                expandedBlocks.Add((actualStart, end));
            }

            var skipLines = new HashSet<int>();
            foreach (var (start, end) in expandedBlocks)
            {
                for (int j = start; j <= end; j++)
                    skipLines.Add(j);
            }

            var outputLines = lines.Where((_, j) => !skipLines.Contains(j)).ToArray();
            File.WriteAllLines(filepath, outputLines);

            totalStripped += expandedBlocks.Count;
            Log.Debug("Stripped {Count} broken function(s) from {File}", expandedBlocks.Count, filename);
        }

        return totalStripped;
    }

    /// <summary>
    /// Find the end of a brace-delimited block starting at `start`.
    /// </summary>
    private static int FindBlockEnd(string[] lines, int start)
    {
        int depth = 0;
        bool seenOpen = false;
        for (int j = start; j < lines.Length; j++)
        {
            depth += lines[j].Count(c => c == '{') - lines[j].Count(c => c == '}');
            if (lines[j].Contains('{'))
                seenOpen = true;
            if (seenOpen && depth <= 0 && j > start)
                return j;
        }
        return lines.Length - 1;
    }

    /// <summary>
    /// Return concatenated text of lines[start..end].
    /// </summary>
    private static string ScanBlockBody(string[] lines, int start, int end)
    {
        return string.Join("\n", lines.Skip(start).Take(end - start + 1));
    }

    private static bool ReferencesPreservedProtocol(string body)
    {
        return PreservedProtocolPattern.IsMatch(body);
    }

    /// <summary>
    /// (Kind, Name) tuple identifying a witness slot. Kind is the normalised member shape:
    /// "method", "property", or "subscript". A non-preserved extension only counts as a
    /// cross-extension witness source if it declares a member with the *same* kind as the
    /// missing requirement — otherwise we'd over-preserve unrelated extensions whose bare
    /// name happens to collide.
    /// </summary>
    private readonly record struct WitnessKey(string Kind, string Name);

    /// <summary>
    /// Builds the set of (kind, bare-name) pairs that some preserved EveryProtocol conformance
    /// requires (per its `<Protocol>_vtable` struct) but that the preserved extension body
    /// does not itself declare. Those are the slots whose witness must come from a sibling
    /// extension via Swift's cross-extension method-visibility rule. Stripping the sibling
    /// would silently break the preserved conformance, so we keep any non-preserved extension
    /// that declares a kind+name match.
    /// </summary>
    private static HashSet<WitnessKey> CollectCrossExtensionRequiredNames(string[] lines)
    {
        // Step 1: vtable struct → required (kind, bare-name) pairs per protocol.
        var protocolRequired = new Dictionary<string, HashSet<WitnessKey>>(StringComparer.Ordinal);
        for (int idx = 0; idx < lines.Length; idx++)
        {
            var headerMatch = VtableStructHeader.Match(lines[idx]);
            if (!headerMatch.Success) continue;

            var protocolName = headerMatch.Groups[1].Value;
            int end = FindBlockEnd(lines, idx);
            var required = new HashSet<WitnessKey>();
            for (int j = idx + 1; j < end; j++)
            {
                var fieldMatch = VtableField.Match(lines[j]);
                if (!fieldMatch.Success) continue;
                var key = MakeVtableWitnessKey(fieldMatch.Groups["name"].Value, fieldMatch.Groups["suffix"].Value);
                required.Add(key);
            }
            if (required.Count > 0)
                protocolRequired[protocolName] = required;
            idx = end;
        }

        // Step 2: walk preserved extensions, subtract the (kind, name) pairs they actually
        // declare, collect the leftover requirements — those must be supplied cross-extension.
        var missing = new HashSet<WitnessKey>();
        for (int idx = 0; idx < lines.Length; idx++)
        {
            var match = EveryProtocolExtensionHeader.Match(lines[idx]);
            if (!match.Success) continue;

            int end = FindBlockEnd(lines, idx);
            var protocolName = match.Groups[1].Value;
            if (PreservedProtocols.Contains(protocolName)
                && protocolRequired.TryGetValue(protocolName, out var required))
            {
                var body = ScanBlockBody(lines, idx, end);
                var declared = CollectDeclaredWitnessKeys(body);
                foreach (var slot in required)
                    if (!declared.Contains(slot))
                        missing.Add(slot);
            }
            idx = end;
        }
        return missing;
    }

    /// <summary>
    /// Builds the WitnessKey for a vtable field. Properties surface as `func_<name>_get/_set`
    /// (suffix get/set, name = the property name). Methods surface as `func_<name>_<index>`
    /// (digit suffix). Subscripts are emitted by EveryProtocolEmitter as
    /// `func_subscript_<index>_get/_set`, so the parsed (name, suffix) is
    /// (<c>subscript_&lt;index&gt;</c>, <c>get|set</c>) — both name and kind are normalized to
    /// the literal "subscript" so they collate with the declared `public subscript` side.
    /// </summary>
    private static WitnessKey MakeVtableWitnessKey(string name, string suffix)
    {
        if (SubscriptVtableName.IsMatch(name))
            return new WitnessKey("subscript", "subscript");
        if (suffix == "get" || suffix == "set")
            return new WitnessKey("property", name);
        return new WitnessKey("method", name);
    }

    private static readonly Regex SubscriptVtableName = new(@"^subscript(_\d+)?$", RegexOptions.Compiled);

    private static HashSet<WitnessKey> CollectDeclaredWitnessKeys(string body)
    {
        var declared = new HashSet<WitnessKey>();
        foreach (Match m in DeclaredMember.Matches(body))
        {
            if (m.Groups["sub"].Success)
            {
                declared.Add(new WitnessKey("subscript", "subscript"));
                continue;
            }
            var kind = m.Groups["kind"].Value switch
            {
                "func" => "method",
                "var" or "let" => "property",
                _ => null,
            };
            if (kind == null) continue;
            declared.Add(new WitnessKey(kind, m.Groups["name"].Value));
        }
        return declared;
    }

    /// <summary>
    /// Returns true when a non-preserved EveryProtocol extension declares a (kind, bare-name)
    /// pair that some preserved sibling needs but doesn't declare itself. That makes this
    /// extension the cross-extension witness source — stripping it would break compile.
    /// Kind discrimination prevents an unrelated `describe` property in a non-preserved
    /// extension from being kept just because some preserved protocol needs a `describe()`
    /// method.
    /// </summary>
    private static bool ProvidesCrossExtensionWitness(string body, HashSet<WitnessKey> crossExtensionRequired)
    {
        if (crossExtensionRequired.Count == 0) return false;
        foreach (var key in CollectDeclaredWitnessKeys(body))
        {
            if (crossExtensionRequired.Contains(key))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Returns true when the line at <paramref name="start"/> is nested inside an
    /// `extension TypeName { ... }` block. Scans upward: the nearest unmatched `{`
    /// that sits on a line starting with `extension ` means we're in an extension.
    /// </summary>
    private static bool IsInsideExtension(string[] lines, int start)
    {
        int depth = 0;
        for (int j = start - 1; j >= 0; j--)
        {
            var trimmed = lines[j].Trim();
            foreach (var c in trimmed)
            {
                if (c == '}') depth++;
                else if (c == '{') depth--;
            }
            if (depth < 0)
            {
                return trimmed.StartsWith("extension ");
            }
        }
        return false;
    }

    private static bool IsBrokenSilgenBlock(string[] lines, int start, int end, string body)
    {
        // (a) EveryProtocol() — protocol witness dispatch for unimplemented conformances
        if (body.Contains("EveryProtocol()"))
        {
            if (!ReferencesPreservedProtocol(body))
                return true;
        }

        // (b) self.functionName() in free function (no _self: parameter).
        // NestedClosureBridge emits its wrapper inside `extension TypeName { @_silgen_name ... }`,
        // where `self` is a valid reference to the instance. Skip rule (b) when the silgen block
        // is nested inside an extension declaration.
        if (!body.Contains("_self:") && !body.Contains("_self :") && !IsInsideExtension(lines, start))
        {
            for (int j = start; j <= end; j++)
            {
                var s = lines[j].Trim();
                if (s.StartsWith("self.") || s.Contains(" self.") || s.Contains("\tself."))
                    return true;
            }
        }

        // (c) __self.init( — async init wrapper (invalid Swift)
        if (body.Contains("__self.init("))
            return true;

        // (d) mutating member on let existential
        if (body.Contains(".load(as: (any ") && body.Contains("existential.") && body.Contains("let existential"))
            return true;

        // (e) Non-escaping closure param passed to Task (async closure methods)
        if (body.Contains("Task {"))
        {
            int sigEnd = body.IndexOf('{');
            if (sigEnd > 0)
            {
                var sig = body[..sigEnd];
                if (ClosureParamPattern.IsMatch(sig))
                    return true;
            }
        }

        return false;
    }

    private static bool IsBrokenExtensionBlock(string body)
    {
        if (body.Contains("EveryProtocol()"))
        {
            if (!ReferencesPreservedProtocol(body))
                return true;
        }

        if (body.Contains("__self.init("))
            return true;

        // Non-escaping closure in Task
        if (body.Contains("Task {"))
        {
            int taskIdx = body.IndexOf("Task {");
            var beforeTask = body[..taskIdx];
            if (ClosureParamPattern.IsMatch(beforeTask))
                return true;
        }

        return false;
    }
}
