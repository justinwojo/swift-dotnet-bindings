// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// Self-contained nullable context so this file compiles identically whether built in the Nuke
// build assembly or link-compiled into the unit-test project.
#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using SwiftBindings.InterpIdentity;

/// <summary>
/// One class's lane witness, as the RuntimeTestsApp recorded it under <c>--lane-identity</c>: the
/// image and symbol of the code that made the class's probe call into Swift.
/// </summary>
/// <param name="Address">The caller's return address; 0 when the Swift probe found no caller frame.</param>
/// <param name="ImageBase">The load address of the caller's image; 0 when the app did not record it.</param>
public sealed record LaneWitness(string ClassName, string Image, string? Symbol, ulong Address, ulong ImageBase = 0)
{
    public string Describe() => $"{Image}!{Symbol ?? "?"}";
}

/// <summary>One symbol of an image's table: its offset from the image base and its name.</summary>
public readonly record struct ImageSymbol(ulong Offset, string Name);

/// <summary>How a .NET 11 app's managed code made its Swift calls.</summary>
public enum RuntimeExecutionLane
{
    /// <summary>The CoreCLR interpreter (an ordinary Debug app on iOS or tvOS).</summary>
    Interpreter,
    /// <summary>Precompiled ReadyToRun code in the app's composite image (a Release CoreCLR app).</summary>
    ReadyToRun,
    /// <summary>A NativeAOT executable.</summary>
    NativeAot,
}

/// <summary>Everything one lane run recorded about how its classes executed.</summary>
/// <param name="AppName">The app's assembly and executable name.</param>
/// <param name="ClassesWithResults">Every class with at least one app-emitted result row.</param>
/// <param name="Witnesses">Every lane witness, one per class per launch attempt.</param>
/// <param name="Inventory">The crossgen2 composite map of the app build; null when the build ran no
/// crossgen2 (NativeAOT) or left no map.</param>
public sealed record RuntimeLaneEvidence(
    string AppName, IReadOnlyCollection<string> ClassesWithResults, IReadOnlyList<LaneWitness> Witnesses,
    ReadyToRunInventory? Inventory);

/// <summary>The lane each class proved, and every reason the run disagrees with its claim.</summary>
public sealed record RuntimeLaneVerdict(
    RuntimeExecutionLane Claimed, IReadOnlyDictionary<string, RuntimeExecutionLane?> ByClass, IReadOnlyList<string> Problems)
{
    public bool Accepted => Problems.Count == 0;
}

/// <summary>
/// Proves, per test class, that a .NET 11 runtime lane ran the way it claims. The process banner
/// reads the same for the CoreCLR interpreter and ReadyToRun, so the evidence is method-level:
/// <list type="bullet">
///   <item><description>Each class's witness: the caller of a primitive Swift probe made through the
///   generated binding. The interpreter calls it from its call stub (<c>CallJittedMethod*</c>),
///   ReadyToRun from a precompiled body in <c>&lt;App&gt;.r2r[.dylib]</c>, NativeAOT from the app
///   executable.</description></item>
///   <item><description>The crossgen2 map: the interpreter lane must hold no precompiled app bodies,
///   and every ReadyToRun witness must be one of the mapped bodies.</description></item>
/// </list>
/// Every class with results must carry a witness, and every witness must agree with the claim, so a
/// lane that silently ran the other mode fails. A ReadyToRun claim is still per call, not per app:
/// methods crossgen2 refuses (an unmanaged <c>calli</c>) run interpreted inside a Release app, and
/// the witness, which is precompiled, does not see them.
/// </summary>
public static class RuntimeLaneIdentity
{
    const string InterpreterStubPrefix = "CallJittedMethod";

    /// <summary>The runtime's image: a loose dylib in a simulator app, a framework binary in a device app.</summary>
    static bool IsInterpreterImage(string image) => image is "libcoreclr.dylib" or "libcoreclr";

    /// <summary>The lane a .NET 11 build must run: NativeAOT by engine, else Debug interprets and Release precompiles.</summary>
    public static RuntimeExecutionLane Expected(string engine, string configuration) => engine switch
    {
        "nativeaot" => RuntimeExecutionLane.NativeAot,
        "coreclr" => configuration switch
        {
            "Debug" => RuntimeExecutionLane.Interpreter,
            "Release" => RuntimeExecutionLane.ReadyToRun,
            _ => throw new ArgumentException($"Unknown configuration '{configuration}'.", nameof(configuration)),
        },
        _ => throw new ArgumentException($"Unknown .NET 11 engine '{engine}'.", nameof(engine)),
    };

    /// <summary>The lane one witness shows, or null when its caller is none of the three.</summary>
    /// <remarks>The interpreter stub may live in <c>libcoreclr.dylib</c> or, where the runtime is
    /// linked statically, in the app executable; the stub's symbol is what identifies it.</remarks>
    public static RuntimeExecutionLane? Classify(LaneWitness witness, string appName)
    {
        if (witness.Address == 0)
            return null;
        if (witness.Symbol != null && witness.Symbol.StartsWith(InterpreterStubPrefix, StringComparison.Ordinal)
            && (IsInterpreterImage(witness.Image) || witness.Image == appName))
            return RuntimeExecutionLane.Interpreter;
        if (CompositeImage(appName).IsMatch(witness.Image))
            return RuntimeExecutionLane.ReadyToRun;
        if (witness.Image == appName)
            return RuntimeExecutionLane.NativeAot;
        return null;
    }

    public static RuntimeLaneVerdict Verify(RuntimeExecutionLane claimed, RuntimeLaneEvidence evidence)
    {
        var problems = new List<string>();
        var byClass = new SortedDictionary<string, RuntimeExecutionLane?>(StringComparer.Ordinal);
        if (evidence.Witnesses.Count == 0)
            problems.Add("no class recorded a lane witness");

        var witnesses = evidence.Witnesses.GroupBy(w => w.ClassName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var classes = evidence.ClassesWithResults.Where(c => c.Length > 0).Concat(witnesses.Keys)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        foreach (var cls in classes)
        {
            if (!witnesses.TryGetValue(cls, out var own))
            {
                problems.Add($"{cls}: ran without a lane witness, so its lane is unknown");
                byClass[cls] = null;
                continue;
            }
            var derived = new HashSet<RuntimeExecutionLane?>();
            foreach (var w in own)
            {
                var lane = Classify(w, evidence.AppName);
                derived.Add(lane);
                if (w.Address == 0)
                    problems.Add($"{cls}: the lane probe found no caller frame, so its lane is unknown");
                else if (lane == null)
                    problems.Add($"{cls}: caller {w.Describe()} is neither the interpreter's call stub, the {evidence.AppName} composite image, nor the {evidence.AppName} executable");
                else if (lane != claimed)
                    problems.Add($"{cls}: claimed {claimed}, but the caller {w.Describe()} is {lane}");
                else if (lane == RuntimeExecutionLane.ReadyToRun && evidence.Inventory != null
                         && (w.Symbol == null || !evidence.Inventory.Contains(w.Symbol)))
                    problems.Add($"{cls}: caller {w.Describe()} is not a precompiled body in the composite map");
            }
            byClass[cls] = derived.Count == 1 ? derived.Single() : null;
        }

        var appBodies = evidence.Inventory?.BodiesFor(evidence.AppName);
        switch (claimed)
        {
            case RuntimeExecutionLane.Interpreter:
                if (appBodies == null)
                    problems.Add("claimed Interpreter, but the build left no crossgen2 map to show the app was not precompiled");
                else if (appBodies > 0)
                    problems.Add($"claimed Interpreter, but the composite holds {appBodies} precompiled {evidence.AppName} bodies");
                break;
            case RuntimeExecutionLane.ReadyToRun:
                if (appBodies == null)
                    problems.Add("claimed ReadyToRun, but the build left no crossgen2 map to place the callers in");
                else if (appBodies == 0)
                    problems.Add($"claimed ReadyToRun, but the composite holds no {evidence.AppName} bodies");
                break;
            case RuntimeExecutionLane.NativeAot:
                if (evidence.Inventory != null)
                    problems.Add("claimed NativeAot, but the build ran crossgen2, which only a CoreCLR app does");
                break;
        }
        return new RuntimeLaneVerdict(claimed, byClass, problems);
    }

    /// <summary>
    /// The anti-vacuity check: the same evidence presented under every other claim. A verdict that
    /// accepts one of these means the classifier cannot tell the lanes apart, and the run fails.
    /// </summary>
    public static IReadOnlyList<RuntimeLaneVerdict> Mislabels(RuntimeExecutionLane claimed, RuntimeLaneEvidence evidence) =>
        Enum.GetValues<RuntimeExecutionLane>().Where(l => l != claimed).Select(l => Verify(l, evidence)).ToList();

    /// <summary>
    /// Re-resolves each witness's symbol on the host, from the unstripped build copy of its image.
    /// </summary>
    /// <remarks>
    /// A device app is stripped of local symbols when it is packaged, so on the phone <c>dladdr</c>
    /// names only the nearest exported symbol: the interpreter's <c>CallJittedMethod*</c> stubs and
    /// every ReadyToRun body are local. The build keeps the unstripped binary of each image it
    /// bundles, so the host resolves the caller's offset from the recorded image base there. A
    /// witness whose image has no symbol table, or that recorded no image base, keeps the app's own
    /// symbol, and an offset no symbol covers leaves none; either way the verdict judges what remains.
    /// </remarks>
    /// <param name="symbolsFor">The address-sorted symbol table of an image's unstripped copy, or null
    /// when the build kept none.</param>
    public static IReadOnlyList<LaneWitness> Symbolicate(IReadOnlyList<LaneWitness> witnesses,
        Func<string, IReadOnlyList<ImageSymbol>?> symbolsFor)
    {
        var tables = new Dictionary<string, IReadOnlyList<ImageSymbol>?>(StringComparer.Ordinal);
        return witnesses.Select(w =>
        {
            if (w.Address == 0 || w.ImageBase == 0 || w.Address <= w.ImageBase)
                return w;
            if (!tables.TryGetValue(w.Image, out var table))
                tables[w.Image] = table = symbolsFor(w.Image);
            // A return address can sit one past the end of its caller, as in the app's own lookup.
            return table == null ? w : w with { Symbol = ResolveSymbol(table, w.Address - 1 - w.ImageBase) };
        }).ToList();
    }

    /// <summary>The symbol covering <paramref name="offset"/>, from a table sorted by address.</summary>
    public static string? ResolveSymbol(IReadOnlyList<ImageSymbol> sortedSymbols, ulong offset)
    {
        int lo = 0, hi = sortedSymbols.Count - 1, found = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (sortedSymbols[mid].Offset <= offset) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return found < 0 ? null : sortedSymbols[found].Name;
    }

    /// <summary>
    /// Parses <c>nm -n --defined-only</c> output into a table sorted by address. A dylib's or
    /// framework's <c>__TEXT</c> segment starts at 0, so the addresses are offsets from the image base.
    /// Mach-O prefixes C symbol names with an underscore that <c>dladdr</c> drops; so does this.
    /// </summary>
    public static IReadOnlyList<ImageSymbol> ParseNmSymbols(string nmOutput)
    {
        var symbols = new List<ImageSymbol>();
        foreach (var line in nmOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3 || !ulong.TryParse(parts[0], System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out var address))
                continue;
            var name = parts[2].StartsWith('_') ? parts[2][1..] : parts[2];
            symbols.Add(new ImageSymbol(address, name));
        }
        return symbols.OrderBy(s => s.Offset).ToList();
    }

    static Regex CompositeImage(string appName) =>
        new($"^{Regex.Escape(appName)}\\.r2r(\\.dylib)?$", RegexOptions.CultureInvariant);
}

/// <summary>A launch that ended in a crash or timeout, and the test (or class setup) it interrupted, when known.</summary>
public sealed record RuntimeLaneInterruption(int Attempt, string Result, string? ClassName, string? TestName, string EvidenceDirectory);

/// <summary>One class's row in a lane report.</summary>
/// <param name="Status">pass, fail or skip from its results; crash or timeout when a launch was
/// interrupted inside it; not-run when it never recorded a result.</param>
public sealed record RuntimeLaneClassRow(string ClassName, string Status, int Pass, int Fail, int Skip,
    string? InterruptedTest, string? Witness, RuntimeExecutionLane? Lane);

/// <summary>What one runtime lane ran, class by class, and whether it proved its execution lane.</summary>
public sealed record RuntimeLaneReport(
    string LaneLabel, string Lane, RuntimeExecutionLane Claimed, bool IdentityAccepted, IReadOnlyList<string> Problems,
    IReadOnlyList<RuntimeExecutionLane> AcceptedMislabels, string? MapPath, int? AppBodies, string FinalResult,
    string AttemptDirectory, IReadOnlyList<RuntimeLaneClassRow> Classes, IReadOnlyList<RuntimeLaneInterruption> Interruptions)
{
    public static RuntimeLaneReport Build(string laneLabel, string lane, RuntimeExecutionLane claimed, RuntimeLaneVerdict verdict,
        IReadOnlyList<RuntimeExecutionLane> acceptedMislabels, string? mapPath, int? appBodies, IEnumerable<string> eligible,
        JsonlTestResults results, IReadOnlyList<RuntimeLaneInterruption> interruptions, string finalResult, string attemptDirectory,
        IReadOnlyList<LaneWitness>? witnesses = null) =>
        new(laneLabel, lane, claimed, verdict.Accepted && acceptedMislabels.Count == 0, verdict.Problems, acceptedMislabels,
            mapPath, appBodies, finalResult, attemptDirectory, Rows(eligible, results, interruptions, verdict, witnesses), interruptions);

    /// <summary>One row per eligible class and per class with results, in name order.</summary>
    /// <param name="judgedWitnesses">The witnesses the verdict judged, after host symbolication; the
    /// app's own records when null.</param>
    public static IReadOnlyList<RuntimeLaneClassRow> Rows(IEnumerable<string> eligible, JsonlTestResults results,
        IReadOnlyList<RuntimeLaneInterruption> interruptions, RuntimeLaneVerdict? verdict,
        IReadOnlyList<LaneWitness>? judgedWitnesses = null)
    {
        var classes = eligible.ToHashSet(StringComparer.Ordinal);
        var tests = results.Tests.GroupBy(t => AttributedClass(t, classes.Contains), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var witnesses = (judgedWitnesses ?? results.LaneWitnesses).GroupBy(w => w.ClassName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => string.Join(" | ", g.Select(w => w.Describe()).Distinct()), StringComparer.Ordinal);
        var rows = new List<RuntimeLaneClassRow>();
        foreach (var cls in classes.Concat(tests.Keys.Where(k => k.Length > 0)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var own = tests.TryGetValue(cls, out var t) ? t : new List<JsonlTestResults.TestEntry>();
            int pass = own.Count(x => x.Status == "pass"), fail = own.Count(x => x.Status == "fail"), skip = own.Count(x => x.Status == "skip");
            var interruption = interruptions.LastOrDefault(i => i.ClassName == cls);
            var status = interruption != null ? interruption.Result.ToLowerInvariant()
                : fail > 0 || own.Any(x => x.Status == "crash") ? "fail"
                : pass > 0 ? "pass"
                : skip > 0 ? "skip"
                : "not-run";
            RuntimeExecutionLane? lane = null;
            verdict?.ByClass.TryGetValue(cls, out lane);
            rows.Add(new(cls, status, pass, fail, skip, interruption?.TestName,
                witnesses.TryGetValue(cls, out var w) ? w : null, lane));
        }
        return rows;
    }

    /// <summary>
    /// The class a result row belongs to. A class whose setup throws records one failure named after
    /// the class itself, with no class part (<c>results.Fail(descriptor.Name, ...)</c>); that row is
    /// the class's. Any other row without a class (a suite-level failure) belongs to none.
    /// </summary>
    public static string AttributedClass(JsonlTestResults.TestEntry test, Func<string, bool> isClass) =>
        test.ClassName.Length > 0 ? test.ClassName : isClass(test.TestName) ? test.TestName : "";

    public string ToJson()
    {
        using var stream = new System.IO.MemoryStream();
        using (var w = new System.Text.Json.Utf8JsonWriter(stream, new System.Text.Json.JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber("schema", 1);
            w.WriteString("laneLabel", LaneLabel);
            w.WriteString("lane", Lane);
            w.WriteString("claimed", Claimed.ToString());
            w.WriteBoolean("identityAccepted", IdentityAccepted);
            w.WriteStartArray("problems");
            foreach (var p in Problems) w.WriteStringValue(p);
            w.WriteEndArray();
            w.WriteStartArray("acceptedMislabels");
            foreach (var m in AcceptedMislabels) w.WriteStringValue(m.ToString());
            w.WriteEndArray();
            w.WriteString("mapPath", MapPath);
            if (AppBodies is int bodies) w.WriteNumber("appBodies", bodies); else w.WriteNull("appBodies");
            w.WriteString("finalResult", FinalResult);
            w.WriteString("attemptDirectory", AttemptDirectory);
            w.WriteStartArray("interruptions");
            foreach (var i in Interruptions)
            {
                w.WriteStartObject();
                w.WriteNumber("attempt", i.Attempt);
                w.WriteString("result", i.Result);
                w.WriteString("class", i.ClassName);
                w.WriteString("test", i.TestName);
                w.WriteString("evidence", i.EvidenceDirectory);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("classes");
            foreach (var c in Classes)
            {
                w.WriteStartObject();
                w.WriteString("class", c.ClassName);
                w.WriteString("status", c.Status);
                w.WriteNumber("pass", c.Pass);
                w.WriteNumber("fail", c.Fail);
                w.WriteNumber("skip", c.Skip);
                w.WriteString("interruptedTest", c.InterruptedTest);
                w.WriteString("witness", c.Witness);
                w.WriteString("lane", c.Lane?.ToString());
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    public string ToText()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# {LaneLabel} {Lane}: claimed {Claimed}, identity {(IdentityAccepted ? "PROVEN" : "NOT PROVEN")}, final result {FinalResult}");
        sb.AppendLine($"# crossgen2 map: {MapPath ?? "none"}; app bodies in the composite: {AppBodies?.ToString() ?? "-"}");
        foreach (var p in Problems) sb.AppendLine($"# identity problem: {p}");
        foreach (var m in AcceptedMislabels) sb.AppendLine($"# evidence also accepted as {m}");
        var byStatus = Classes.GroupBy(c => c.Status).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key} {g.Count()}");
        sb.AppendLine($"# classes: {Classes.Count} ({string.Join(", ", byStatus)}); tests: pass {Classes.Sum(c => c.Pass)}, fail {Classes.Sum(c => c.Fail)}, skip {Classes.Sum(c => c.Skip)}");
        foreach (var i in Interruptions)
            sb.AppendLine($"# attempt {i.Attempt}: {i.Result} in {(i.ClassName == null ? "no attributable test" : $"{i.ClassName}.{i.TestName}")} ({i.EvidenceDirectory})");
        sb.AppendLine("class\tstatus\tpass\tfail\tskip\tinterrupted\tlane\twitness");
        foreach (var c in Classes)
            sb.AppendLine($"{c.ClassName}\t{c.Status}\t{c.Pass}\t{c.Fail}\t{c.Skip}\t{c.InterruptedTest ?? "-"}\t{c.Lane?.ToString() ?? "-"}\t{c.Witness ?? "-"}");
        return sb.ToString();
    }

    /// <summary>The report directory name: platform, framework, engine and configuration.</summary>
    public static string Slug(string laneLabel, string framework, string engine, string configuration) =>
        $"{Regex.Replace(laneLabel.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-')}-{framework}-{engine}-{configuration.ToLowerInvariant()}";
}
