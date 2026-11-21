// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// Link-compiled into projects that do not enable nullable reference types, so the annotations are
// opted into locally rather than inherited from a csproj.
#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

/// <summary>
/// Which imports each export of a linked arm64 wrapper image can reach, read from the image itself
/// (load-safety P2: the gate derives what a P/Invoke into the wrapper can touch from the binary, not
/// from what the generator says it emitted).
///
/// <para>The graph's nodes are the image's symbols: every function (the wrappers keep their local
/// symbols) and every data object. A function's edges are its <c>bl</c>/<c>b</c> targets (a local
/// function, or an import through its stub), and every address an <c>adrp</c> pair forms
/// (<c>adrp</c> + <c>ldr</c> through a GOT slot to an import, or <c>adrp</c> + <c>add</c>/<c>ldr</c>
/// to a local function or data object). A data object's edges are its fixups: binds to imports, and
/// rebases to local functions or data (witness tables, metadata, closures' contexts). Reach is the
/// transitive closure from each exported symbol.</para>
///
/// <para>Fail-closed: <see cref="Unattributed"/> lists each import of interest no export reaches, whether
/// a node references it or none does, so a reference the graph does not follow (through a register, a
/// runtime registration record, a fixup the read does not parse) cannot leave an absent import
/// unaccounted for.</para>
/// </summary>
internal sealed class WrapperImportReach
{
    private readonly Dictionary<string, HashSet<string>> _importsByNode;
    private readonly Dictionary<string, HashSet<string>> _edges;

    /// <summary>Every exported symbol of the image (with the Mach-O leading underscore).</summary>
    public IReadOnlyCollection<string> Exports { get; }

    /// <summary>How many functions and data objects the graph holds.</summary>
    public int NodeCount => _edges.Count;

    /// <summary>How many instructions or fixups named an address the graph could not place.</summary>
    public int UnresolvedReferences { get; }

    private WrapperImportReach(
        Dictionary<string, HashSet<string>> edges, Dictionary<string, HashSet<string>> importsByNode,
        IReadOnlyCollection<string> exports, int unresolved)
    {
        _edges = edges;
        _importsByNode = importsByNode;
        Exports = exports;
        UnresolvedReferences = unresolved;
    }

    private static readonly Regex Label = new(@"^(\S.*):$", RegexOptions.Compiled);
    private static readonly Regex Instruction = new(@"^\s+([0-9a-f]+):\t(\S+)(?:\t([^;]*))?(?:;\s*(.*))?$", RegexOptions.Compiled);
    private static readonly Regex Fixup = new(@"^\s+(\S+)\s+(\S+)\s+0x([0-9A-Fa-f]+)\s+(bind|rebase)\s+(\S+)", RegexOptions.Compiled);
    private static readonly Regex Symbol = new(@"^([0-9a-f]+) ([A-Za-z]) (.+)$", RegexOptions.Compiled);
    private static readonly Regex Section = new(@"^\s*(\d+)\s+(\S+)\s+([0-9a-f]+)\s+([0-9a-f]+)", RegexOptions.Compiled);

    /// <summary>
    /// Builds the graph from one arm64 slice's <c>objdump --macho -h</c> section headers, <c>nm -n</c>
    /// symbols (address, type, name), <c>objdump --macho -d --no-show-raw-insn</c> disassembly and
    /// <c>dyld_info -fixups</c> output.
    /// </summary>
    public static WrapperImportReach Parse(
        IEnumerable<string> sectionHeaders, IEnumerable<string> nmLines, IEnumerable<string> disassembly, IEnumerable<string> fixups)
    {
        var sections = new List<(ulong Start, ulong End, string Name)>();
        foreach (var line in sectionHeaders)
        {
            var match = Section.Match(line);
            if (!match.Success)
                continue;
            var size = ulong.Parse(match.Groups[3].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            var start = ulong.Parse(match.Groups[4].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            sections.Add((start, start + size, $"{match.Groups[2].Value}#{match.Groups[1].Value}"));
        }

        // The image's symbols, sorted by address, so an address resolves to the node that holds it.
        var symbols = new List<(ulong Address, string Name)>();
        var exports = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in nmLines)
        {
            var match = Symbol.Match(line.TrimEnd());
            if (!match.Success)
                continue;
            var type = match.Groups[2].Value[0];
            if (type is 'U' or 'u' or 'I' or 'A' or 'a')
                continue;
            var address = ulong.Parse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            var name = match.Groups[3].Value;
            symbols.Add((address, name));
            if (char.IsUpper(type))
                exports.Add(name);
        }
        symbols.Sort((a, b) => a.Address.CompareTo(b.Address));
        var addresses = symbols.Select(s => s.Address).ToArray();

        // An address belongs to the last symbol at or before it in the same section; an address in a
        // section's leading anonymous bytes (string literals, Swift metadata records) belongs to one
        // node per section, and an address outside every section to none.
        string? NodeAt(ulong address)
        {
            var section = sections.FirstOrDefault(s => address >= s.Start && address < s.End);
            if (section.Name is null)
                return null;
            var index = Array.BinarySearch(addresses, address);
            if (index < 0)
                index = ~index - 1;
            return index >= 0 && symbols[index].Address >= section.Start ? symbols[index].Name : $"<{section.Name}>";
        }

        var edges = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var imports = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        HashSet<string> EdgesOf(string node) => edges.TryGetValue(node, out var set) ? set : edges[node] = new(StringComparer.Ordinal);
        HashSet<string> ImportsOf(string node) => imports.TryGetValue(node, out var set) ? set : imports[node] = new(StringComparer.Ordinal);
        foreach (var (_, name) in symbols)
            EdgesOf(name);

        // Fixups: a GOT slot binds an import that code loads; a bind or rebase anywhere else belongs to
        // the data object holding it.
        var got = new Dictionary<ulong, string>();
        int unresolved = 0;
        foreach (var line in fixups)
        {
            var match = Fixup.Match(line);
            if (!match.Success)
                continue;
            var section = match.Groups[2].Value;
            var address = ulong.Parse(match.Groups[3].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            var kind = match.Groups[4].Value;
            var target = match.Groups[5].Value;
            if (kind == "bind")
            {
                var slash = target.IndexOf('/');
                var import = slash >= 0 ? target[(slash + 1)..] : target;
                if (section is "__got" or "__auth_got" or "__la_symbol_ptr")
                    got[address] = import;
                else if (NodeAt(address) is { } holder)
                    ImportsOf(holder).Add(import);
                else
                    unresolved++;
            }
            else if (target.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                     && ulong.TryParse(target[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var to))
            {
                if (NodeAt(address) is { } from && NodeAt(to) is { } pointee)
                {
                    if (from != pointee)
                        EdgesOf(from).Add(pointee);
                }
                else
                    unresolved++;
            }
        }

        // Code: branches and adrp-formed addresses.
        var loadedSlots = new HashSet<ulong>();
        string? current = null;
        var pages = new Dictionary<string, ulong>(StringComparer.Ordinal);
        void Reference(string from, ulong address)
        {
            if (got.TryGetValue(address, out var import))
            {
                ImportsOf(from).Add(import);
                loadedSlots.Add(address);
            }
            else if (NodeAt(address) is { } target)
            {
                if (target != from)
                    EdgesOf(from).Add(target);
            }
            else
                unresolved++;
        }
        foreach (var line in disassembly)
        {
            var label = Label.Match(line);
            if (label.Success)
            {
                current = label.Groups[1].Value;
                pages.Clear();
                EdgesOf(current);
                continue;
            }
            if (current is null)
                continue;
            var match = Instruction.Match(line);
            if (!match.Success)
                continue;
            var mnemonic = match.Groups[2].Value;
            var operands = match.Groups[3].Value.Trim();
            var comment = match.Groups[4].Success ? match.Groups[4].Value.Trim() : "";
            var registers = operands.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            bool isBranch = mnemonic is "bl" or "b" || mnemonic.StartsWith("b.", StringComparison.Ordinal)
                || mnemonic is "cbz" or "cbnz" or "tbz" or "tbnz";
            if (isBranch)
            {
                const string stub = "symbol stub for: ";
                var at = comment.IndexOf(stub, StringComparison.Ordinal);
                if (at >= 0)
                {
                    ImportsOf(current).Add(comment[(at + stub.Length)..].Trim());
                    continue;
                }
                var target = registers.Length > 0 ? registers[^1] : "";
                if (target.StartsWith("0x", StringComparison.Ordinal))
                {
                    if (ulong.TryParse(target[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var to))
                        Reference(current, to);
                }
                else if (target.Length > 0 && target != current)
                    EdgesOf(current).Add(target);
                continue;
            }
            if (mnemonic == "adrp" && registers.Length >= 2)
            {
                // objdump appends the page address as a comment: "adrp x8, 36 ; 0x24000".
                if (comment.StartsWith("0x", StringComparison.Ordinal)
                    && ulong.TryParse(comment[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var page))
                    pages[registers[0]] = page;
                continue;
            }
            if (mnemonic == "adr" && comment.StartsWith("0x", StringComparison.Ordinal)
                && ulong.TryParse(comment[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var near))
            {
                Reference(current, near);
                continue;
            }
            if (registers.Length >= 2 && (mnemonic is "add" || mnemonic.StartsWith("ldr", StringComparison.Ordinal)))
            {
                // add xD, xN, #off   |   ldr xD, [xN, #off]   |   ldr xD, [xN]
                var baseRegister = registers[1].TrimStart('[').TrimEnd(']', '!');
                var offsetText = registers.Length >= 3 ? registers[2].TrimEnd(']', '!').TrimStart('#') : "0";
                if (pages.TryGetValue(baseRegister, out var basePage) && TryParseImmediate(offsetText, out var offset))
                    Reference(current, basePage + offset);
            }
            // Whatever the instruction, its first operand no longer holds a page. A store's source register
            // is cleared too; the compiler forms each address with adrp and its add or load back to back,
            // so a page is not kept live across a store of its own register.
            if (registers.Length > 0)
                pages.Remove(registers[0]);
        }

        // A GOT slot no instruction loads is reached through data the graph does not follow (a Swift
        // relative pointer): its import belongs to no node, so it can only surface as unattributed.
        foreach (var (address, import) in got)
            if (!loadedSlots.Contains(address))
                ImportsOf("<unloaded GOT>").Add(import);

        return new WrapperImportReach(edges, imports, exports, unresolved);
    }

    private static bool TryParseImmediate(string text, out ulong value)
    {
        value = 0;
        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return ulong.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        return ulong.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>
    /// Every import (with the Mach-O leading underscore) <paramref name="export"/> can reach, or
    /// empty when the image does not define it.
    /// </summary>
    public IReadOnlySet<string> ImportsReachedFrom(string export)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (!_edges.ContainsKey(export))
            return result;
        var seen = new HashSet<string>(StringComparer.Ordinal) { export };
        var queue = new Queue<string>();
        queue.Enqueue(export);
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            if (_importsByNode.TryGetValue(node, out var direct))
                result.UnionWith(direct);
            if (!_edges.TryGetValue(node, out var next))
                continue;
            foreach (var target in next)
                if (seen.Add(target))
                    queue.Enqueue(target);
        }
        return result;
    }

    /// <summary>
    /// Each of <paramref name="imports"/> that no export reaches, with the nodes that reference it: none
    /// when nothing the read follows references it at all (the image imports it, the graph never saw it).
    /// </summary>
    public IReadOnlyList<(string Import, IReadOnlyList<string> Holders)> Unattributed(IEnumerable<string> imports)
    {
        var reached = new HashSet<string>(StringComparer.Ordinal);
        foreach (var export in Exports)
            reached.UnionWith(ImportsReachedFrom(export));
        var holders = _importsByNode
            .SelectMany(kv => kv.Value.Select(i => (Import: i, Holder: kv.Key)))
            .ToLookup(x => x.Import, x => x.Holder, StringComparer.Ordinal);
        return imports
            .Where(i => !reached.Contains(i))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(i => i, StringComparer.Ordinal)
            .Select(i => (i, (IReadOnlyList<string>)holders[i].Distinct(StringComparer.Ordinal).OrderBy(h => h, StringComparer.Ordinal).ToList()))
            .ToList();
    }
}
