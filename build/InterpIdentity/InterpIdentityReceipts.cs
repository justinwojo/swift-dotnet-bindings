// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace SwiftBindings.InterpIdentity;

/// <summary>One return address the Swift fixture recorded and resolved with <c>dladdr</c>.</summary>
public sealed record FrameReceipt(int Index, ulong Address, string Image, string? Symbol);

/// <summary>What one fixture process reported.</summary>
/// <param name="Fixture">The fixture name.</param>
/// <param name="Outcome">Pass, fail, crash, indeterminate, or missing.</param>
/// <param name="Detail">The app's reason text on a result line, or why the outcome was inferred.</param>
/// <param name="Observations">Free-form measurements the fixture recorded.</param>
/// <param name="Frames">The return addresses of the fixture's last capture, innermost first.</param>
/// <param name="Start">The app's start line (framework, architecture, configuration), if it printed one.</param>
public sealed record FixtureReceipt(
    string Fixture, FixtureOutcome Outcome, string Detail,
    IReadOnlyList<string> Observations, IReadOnlyList<FrameReceipt> Frames, string? Start);

/// <summary>The image and symbol that made a transition into the fixture.</summary>
public sealed record CallerReceipt(string Fixture, string Image, string? Symbol);

/// <summary>Parses the app's <c>IIF</c> console protocol.</summary>
public static class ConsoleReceiptParser
{
    /// <summary>Native images that belong to the fixture itself; the caller is the first frame outside them.</summary>
    public static IReadOnlyList<string> FixtureImages { get; } = ["InterpIdentityFixture", "InterpIdentityFixtureSwiftBindings"];

    /// <summary>Strips any launcher or NSLog prefix and returns the protocol lines.</summary>
    public static IEnumerable<string> ProtocolLines(string console)
    {
        foreach (var raw in console.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var at = line.IndexOf("IIF ", StringComparison.Ordinal);
            if (at >= 0)
                yield return line[at..];
        }
    }

    /// <summary>Reads one fixture process's console.</summary>
    /// <param name="fixture">The fixture the process was asked to run.</param>
    /// <param name="console">Everything the process wrote.</param>
    /// <param name="timedOut">True when the harness gave up waiting and killed the process.</param>
    public static FixtureReceipt Parse(string fixture, string console, bool timedOut = false)
    {
        string? start = null, result = null;
        bool began = false, ended = false;
        var observations = new List<string>();
        // Capture lines carry no fixture name (one fixture per process); keep the last capture
        // made after the fixture began.
        var captures = new SortedDictionary<int, List<FrameReceipt>>();
        foreach (var line in ProtocolLines(console))
        {
            var parts = line.Split(' ', 5, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            var verb = parts[1];
            if (verb == "START") { start ??= line[10..]; continue; }
            if (verb == "CAPTURE")
            {
                if (began && !ended)
                {
                    var (serial, frame) = ParseCapture(line);
                    if (!captures.TryGetValue(serial, out var frames)) captures[serial] = frames = [];
                    frames.Add(frame);
                }
                continue;
            }
            if (parts.Length < 3 || parts[2] != fixture) continue;
            var rest = parts.Length > 3 ? string.Join(' ', parts.Skip(3)) : "";
            switch (verb)
            {
                case "BEGIN": began = true; break;
                case "OBSERVE": observations.Add(rest); break;
                case "RESULT": result ??= rest; break;
                case "END": ended = true; break;
            }
        }
        IReadOnlyList<FrameReceipt> last = captures.Count == 0 ? [] : captures.Last().Value;

        if (result != null)
        {
            var space = result.IndexOf(' ');
            var verdict = space < 0 ? result : result[..space];
            var detail = space < 0 ? "" : result[(space + 1)..];
            var outcome = verdict switch
            {
                "PASS" => FixtureOutcome.Pass,
                "FAIL" => FixtureOutcome.Fail,
                _ => throw new FormatException($"Unknown result verdict '{verdict}' for fixture {fixture}."),
            };
            if (!ended)
            {
                // A reported failure stands. A pass the process did not survive to finish is not a pass.
                if (outcome == FixtureOutcome.Pass)
                    return new FixtureReceipt(fixture, FixtureOutcome.Indeterminate,
                        timedOut ? "reported PASS, then timed out" : "reported PASS, then the process died", observations, last, start);
                detail = (detail + (timedOut ? " (timed out after reporting)" : " (process died after reporting)")).Trim();
            }
            return new FixtureReceipt(fixture, outcome, detail, observations, last, start);
        }
        if (began)
            return timedOut
                ? new FixtureReceipt(fixture, FixtureOutcome.Indeterminate, "timed out before reporting", observations, last, start)
                : new FixtureReceipt(fixture, FixtureOutcome.Crash, "process died before reporting", observations, last, start);
        return new FixtureReceipt(fixture, FixtureOutcome.Missing, start == null ? "app never started" : "fixture never began",
            observations, last, start);
    }

    // IIF CAPTURE <serial> <index> 0x<address> <image> <symbol|->
    static (int Serial, FrameReceipt Frame) ParseCapture(string line)
    {
        var p = line.Split(' ', 7, StringSplitOptions.RemoveEmptyEntries);
        if (p.Length < 6 || !p[4].StartsWith("0x", StringComparison.Ordinal))
            throw new FormatException($"Malformed capture line: {line}");
        var symbol = p.Length > 6 && p[6] != "-" ? p[6] : null;
        return (int.Parse(p[2], CultureInfo.InvariantCulture), new FrameReceipt(int.Parse(p[3], CultureInfo.InvariantCulture),
            ulong.Parse(p[4][2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture), p[5], symbol));
    }

    /// <summary>The first recorded frame outside the fixture's own images, or null when there is none.</summary>
    public static CallerReceipt? Caller(FixtureReceipt receipt)
    {
        var frame = receipt.Frames.OrderBy(f => f.Index).FirstOrDefault(f => !FixtureImages.Contains(f.Image, StringComparer.Ordinal));
        if (frame == null) return null;
        // A caller is only meaningful when the fixture's own frames came first.
        if (!receipt.Frames.Any(f => f.Index < frame.Index && FixtureImages.Contains(f.Image, StringComparer.Ordinal)))
            return null;
        return new CallerReceipt(receipt.Fixture, frame.Image, frame.Symbol);
    }
}

/// <summary>Precompiled method bodies in a crossgen2 composite image, from its <c>--mapcsv</c> output.</summary>
/// <param name="Symbols">Every precompiled method symbol, without the leading underscore.</param>
public sealed record ReadyToRunInventory(IReadOnlySet<string> Symbols)
{
    public int Total => Symbols.Count;

    /// <summary>Bodies belonging to <paramref name="assemblyName"/>. crossgen2 prefixes each
    /// composite symbol with the owning assembly's name, dots replaced by underscores.</summary>
    public int BodiesFor(string assemblyName)
    {
        var prefix = SymbolPrefix(assemblyName);
        return Symbols.Count(s => s.StartsWith(prefix, StringComparison.Ordinal));
    }

    public static string SymbolPrefix(string assemblyName) => assemblyName.Replace('.', '_') + "_";

    public bool Contains(string symbol) => Symbols.Contains(symbol.TrimStart('_'));

    public static ReadyToRunInventory ParseMapCsv(string csv)
    {
        using var reader = new StringReader(csv);
        var header = reader.ReadLine() ?? throw new FormatException("Empty crossgen2 map.");
        var columns = SplitCsv(header);
        int section = columns.IndexOf("Section"), nodeType = columns.IndexOf("Node Type"), symbol = columns.IndexOf("Symbol");
        if (section < 0 || nodeType < 0 || symbol < 0)
            throw new FormatException($"crossgen2 map header lacks Section, Node Type or Symbol: {header}");
        var symbols = new HashSet<string>(StringComparer.Ordinal);
        for (var line = reader.ReadLine(); line != null; line = reader.ReadLine())
        {
            var row = SplitCsv(line);
            if (row.Count <= Math.Max(section, Math.Max(nodeType, symbol))) continue;
            if (row[section] != "__managedcode__TEXT" || row[nodeType] != "MethodWithGCInfo") continue;
            symbols.Add(row[symbol].TrimStart('_'));
        }
        return new ReadyToRunInventory(symbols);
    }

    static List<string> SplitCsv(string line)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else field.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { fields.Add(field.ToString()); field.Clear(); }
            else field.Append(c);
        }
        fields.Add(field.ToString());
        return fields;
    }
}

/// <summary>The crossgen2 command the app build ran, from a normal-verbosity MSBuild log.</summary>
/// <param name="Rooted">Inputs compiled into the composite (assembly file names).</param>
/// <param name="References">Inputs passed with <c>-r:</c>.</param>
/// <param name="Unrooted">Inputs passed with <c>-u:</c>.</param>
/// <param name="Options">Every other option, in order.</param>
public sealed record Crossgen2Invocation(
    IReadOnlyList<string> Rooted, IReadOnlyList<string> References, IReadOnlyList<string> Unrooted, IReadOnlyList<string> Options)
{
    public bool IsRooted(string assemblyName) => Rooted.Contains(assemblyName + ".dll", StringComparer.Ordinal);

    /// <summary>Every crossgen2 invocation in <paramref name="log"/>. The task logs the tool path
    /// followed by one argument per indented line.</summary>
    public static IReadOnlyList<Crossgen2Invocation> ParseAll(string log)
    {
        var result = new List<Crossgen2Invocation>();
        var lines = log.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        for (var i = 0; i < lines.Count; i++)
        {
            var head = lines[i];
            var at = head.IndexOf("crossgen2 --targetos", StringComparison.Ordinal);
            if (at < 0) continue;
            var args = new List<string> { head[(at + "crossgen2 ".Length)..].Trim() };
            for (var j = i + 1; j < lines.Count; j++)
            {
                var arg = lines[j].Trim();
                if (arg.Length == 0 || !char.IsWhiteSpace(lines[j], 0) || arg.StartsWith("Done executing task", StringComparison.Ordinal))
                    break;
                args.Add(arg);
                i = j;
            }
            result.Add(FromArguments(args));
        }
        return result;
    }

    public static Crossgen2Invocation FromArguments(IReadOnlyList<string> args)
    {
        static string FileName(string value) => Path.GetFileName(value.Trim('"'));
        var rooted = new List<string>();
        var references = new List<string>();
        var unrooted = new List<string>();
        var options = new List<string>();
        foreach (var arg in args)
        {
            if (arg.StartsWith("-r:", StringComparison.Ordinal)) references.Add(FileName(arg[3..]));
            else if (arg.StartsWith("-u:", StringComparison.Ordinal)) unrooted.Add(FileName(arg[3..]));
            else if (arg.StartsWith('-')) options.Add(arg);
            else rooted.Add(FileName(arg));
        }
        return new Crossgen2Invocation(rooted, references, unrooted, options);
    }
}
