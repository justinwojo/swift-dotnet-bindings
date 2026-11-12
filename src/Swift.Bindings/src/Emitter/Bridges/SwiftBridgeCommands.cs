// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Immutable;
using System.Text;
using Microsoft.Extensions.Logging;

namespace BindingsGeneration;

/// <summary>
/// The plan sets of the hand-written transition tables (the runtime's and the Apple supplement's):
/// planned by the same planner as a generated binding's transitions, from the table's facts and the
/// assembly's own C#, and written as the same plan sidecar, so the bridge gate and emitter read them
/// exactly as they read a binding's.
/// </summary>
public static class SwiftHandWrittenPlans
{
    /// <summary>The standard-library and Foundation databases the layout resolver reads for a table's types.</summary>
    public static TypeDatabase StandardLibrary()
    {
        var database = new TypeDatabase();
        foreach (var name in new[] { "SwiftDatabase.xml", "FoundationDatabase.xml" })
            database.LoadModuleDatabaseFromFile(Path.Combine(AppContext.BaseDirectory, "Swift", name)).Wait();
        return database;
    }

    /// <summary>
    /// The C# sources under <paramref name="directories"/>, build output excluded, in ordinal path order.
    /// <paramref name="excluded"/> (full paths) are left out too: a table's own bridge products, which
    /// are written from its declarations and must not be read back as more of them.
    /// </summary>
    public static IReadOnlyList<string> Sources(IEnumerable<string> directories, IEnumerable<string>? excluded = null)
    {
        var separator = Path.DirectorySeparatorChar;
        var skip = (excluded ?? []).Select(Path.GetFullPath).ToHashSet(StringComparer.Ordinal);
        return directories
            .SelectMany(d => Directory.EnumerateFiles(d, "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{separator}obj{separator}", StringComparison.Ordinal) && !f.Contains($"{separator}bin{separator}", StringComparison.Ordinal))
            .Where(f => !skip.Contains(Path.GetFullPath(f)))
            .Order(StringComparer.Ordinal)
            .Select(File.ReadAllText)
            .ToList();
    }

    /// <summary>The plan set of the table at <paramref name="tablePath"/> over the assembly sources <paramref name="sources"/>.</summary>
    public static SwiftCallPlanSet Plan(string tablePath, IEnumerable<string> sources, TypeDatabase standardLibrary, out SwiftHandWrittenTransitionTable table)
    {
        table = SwiftHandWrittenTransitionTable.Load(tablePath);
        return Plan(table, sources, standardLibrary);
    }

    private static SwiftCallPlanSet Plan(SwiftHandWrittenTransitionTable table, IEnumerable<string> sources, TypeDatabase standardLibrary)
    {
        var ledger = table.Ledger(sources);
        var lowering = table.LoweringOver(new SwiftLayoutValueLowering(Array.Empty<ModuleDecl>(), standardLibrary));
        return SwiftCallPlanSet.Build(ledger, new SwiftCallPlanner(ledger.Types, SwiftArchitectureModel.Default(lowering)));
    }

    /// <summary>
    /// Everything a table produces: its plan sidecar and, when the table routes through bridges
    /// (<see cref="SwiftHandWrittenTransitionTable.Bridges"/>) and <paramref name="evidence"/> is given,
    /// the routing over its declarations, the compiled C# and each architecture's bridge assembly.
    /// A table without bridges is planned exactly as before, with no evidence joined.
    /// </summary>
    public static SwiftHandWrittenProducts Produce(
        string tablePath, IEnumerable<string> sourceDirectories, TypeDatabase standardLibrary, SwiftLoweringEvidence? evidence)
    {
        var table = SwiftHandWrittenTransitionTable.Load(tablePath);
        var directory = Path.GetDirectoryName(Path.GetFullPath(tablePath))!;
        var bridges = table.Bridges;
        var excluded = bridges is null ? [] : new[] { Path.Combine(directory, bridges.Managed) };
        var set = Plan(table, Sources(sourceDirectories, excluded), standardLibrary);
        if (bridges is null || evidence is null)
            return new SwiftHandWrittenProducts(table, set, SwiftBridgeRouting.None, null, ImmutableDictionary<SwiftArchitecture, string>.Empty);

        var declarationsPath = Path.Combine(directory, bridges.Declarations);
        if (!File.Exists(declarationsPath))
            throw new FileNotFoundException($"{tablePath} routes through bridges declared in {declarationsPath}, which does not exist", declarationsPath);
        var declarations = File.ReadAllText(declarationsPath);
        set = set.WithEvidence(evidence);
        var routing = SwiftBridgeRouting.Plan(set, declarations, bridges.Library);
        var managed = ManagedHeader(table, bridges) + WithoutLeadComment(routing.Rewrite(declarations));
        var native = SwiftBridgeRouting.Architectures.ToImmutableDictionary(
            a => a,
            a => routing.Assembly(a)?.Source ?? NoBridges(a));
        return new SwiftHandWrittenProducts(table, routing.Annotate(set), routing, managed, native);
    }

    private static string ManagedHeader(SwiftHandWrittenTransitionTable table, SwiftHandWrittenBridges bridges) =>
        $"// <auto-generated>\n" +
        $"// Written by `nuke swift-bridge-products` from {Path.GetFileName(bridges.Declarations)} and the {table.Assembly}\n" +
        $"// transition table: each Swift-convention import the lowering evidence proves is called through its\n" +
        $"// native bridge in {bridges.Library}; any other is compiled as declared. Do not edit; edit the\n" +
        $"// declarations and rerun the target.\n" +
        $"// </auto-generated>\n";

    /// <summary>
    /// The declarations without their leading comment block, which describes the declarations file and
    /// would misdescribe the generated one; its copyright and licence lines are kept.
    /// </summary>
    private static string WithoutLeadComment(string source)
    {
        var lines = source.Replace("\r\n", "\n").Split('\n');
        var body = 0;
        while (body < lines.Length && (lines[body].TrimStart().StartsWith("//", StringComparison.Ordinal) || lines[body].Trim().Length == 0))
            body++;
        var kept = lines.Take(body).Where(l => l.Contains("Copyright", StringComparison.Ordinal) || l.Contains("Licensed under", StringComparison.Ordinal));
        return string.Join("\n", kept.Append(string.Empty).Concat(lines.Skip(body)));
    }

    /// <summary>A bridge assembly with no bridge: the native build assembles every architecture's file unconditionally.</summary>
    private static string NoBridges(SwiftArchitecture architecture) =>
        $"/* No transition of this assembly is routed through a bridge on {architecture}. */\n";

    /// <summary><c>--emit-hand-written-plans</c>: writes the table's plan sidecar into <paramref name="outputDirectory"/>, and its bridge products beside the table.</summary>
    public static int Run(string tablePath, IReadOnlyList<string> sourceDirectories, string outputDirectory, string? evidencePath, ILogger logger)
    {
        if (!File.Exists(tablePath) || sourceDirectories.Count == 0 || sourceDirectories.Any(d => !Directory.Exists(d)))
        {
            logger.LogError("--emit-hand-written-plans needs an existing --transition-facts table and at least one existing --plan-sources directory");
            return 1;
        }
        if (evidencePath is not null && !File.Exists(evidencePath))
        {
            logger.LogError("the lowering evidence {Path} does not exist: the swift-lowering-oracle target records it", evidencePath);
            return 1;
        }
        var probe = SwiftHandWrittenTransitionTable.Load(tablePath);
        if (probe.Bridges is not null && evidencePath is null)
        {
            // Without evidence nothing is executable, so the products would route nothing: writing them
            // would silently put every bridged transition back on its direct import.
            logger.LogError("{Table} routes through bridges; --emit-hand-written-plans needs --swift-lowering-evidence for it", tablePath);
            return 1;
        }
        var evidence = evidencePath is null ? null : SwiftLoweringEvidence.Load(evidencePath);
        var products = Produce(tablePath, sourceDirectories, StandardLibrary(), evidence);
        var (table, set) = (products.Table, products.Plans);
        Directory.CreateDirectory(outputDirectory);
        var path = Path.Combine(outputDirectory, SwiftCallPlanSet.FileName(table.Assembly));
        File.WriteAllText(path, set.ToJson());
        var refused = set.Count(SwiftPlanStatus.Refused);
        logger.LogInformation("{Assembly}: {Planned} planned, {Refused} refused, {Plans} plan(s) -> {Path}",
            table.Assembly, set.Count(SwiftPlanStatus.Planned), refused, set.Plans.Length, path);
        if (products.Managed is { } managed && table.Bridges is { } bridges)
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(tablePath))!;
            File.WriteAllText(Path.Combine(directory, bridges.Managed), managed);
            foreach (var (architecture, source) in products.Native)
                File.WriteAllText(Path.Combine(directory, bridges.NativeFile(architecture)), source);
            logger.LogInformation("{Assembly}: {Bridged} transition(s) called through a bridge in {Library}",
                table.Assembly, products.Routing.Routes.Count(r => r.IsBridged), bridges.Library);
            // Fail-closed reroute: an executable transition the shim cannot reproduce keeps its direct
            // import and is reported; one the evidence does not prove is reported as not executable.
            foreach (var route in products.Routing.Routes.Where(r => !r.IsBridged))
                logger.LogWarning("{Code}: {Member} ({EntryPoint}) keeps its direct import: {Reason}",
                    SwiftBridgeRouting.RefusedCode, route.Member, route.EntryPoint, route.Refusal);
            foreach (var entry in set.Coverage.Where(e => e.Status == SwiftPlanStatus.Planned && !e.Executable))
                logger.LogWarning("{Member} ({EntryPoint}) keeps its direct import: not executable ({Evidence})",
                    entry.Member, entry.EntryPoint, entry.Evidence);
        }
        if (refused > 0)
        {
            foreach (var entry in set.Coverage.Where(e => e.Status == SwiftPlanStatus.Refused))
                logger.LogError("{Member} {EntryPoint}: {Refusals}", entry.Member, entry.EntryPoint, string.Join("; ", entry.Refusals));
            return 1;
        }
        return 0;
    }
}

/// <summary>
/// The products of one hand-written transition table: its plan set (annotated with each route when
/// it routes through bridges), the routing, and, when it routes, the compiled C# and each
/// architecture's bridge assembly.
/// </summary>
public sealed record SwiftHandWrittenProducts(
    SwiftHandWrittenTransitionTable Table,
    SwiftCallPlanSet Plans,
    SwiftBridgeRouting Routing,
    string? Managed,
    ImmutableDictionary<SwiftArchitecture, string> Native);

/// <summary>
/// The bridge gate (<c>--swift-bridges</c>): reads plan sidecars, joins the swiftc lowering evidence,
/// emits every forward plan's bridge on arm64 and x86_64, and validates each assembly. It fails when
/// an executable plan has no bridge, when any bridge's frame or unwind information is wrong or a
/// reference is strong, and when nothing is executable at all (an empty gate proves nothing).
/// </summary>
public static class SwiftBridgeGate
{
    /// <summary>The joined plan sets of the sidecars and the plans of all of them, one per symbol.</summary>
    public static (ImmutableArray<SwiftCallPlanSet> Sets, ImmutableArray<SwiftCallPlan> Plans, ImmutableHashSet<string> Executable) Load(
        IEnumerable<string> planFiles, SwiftLoweringEvidence evidence)
    {
        var sets = planFiles.Select(f => SwiftCallPlanSet.From(SwiftCallPlanSet.Read(f)).WithEvidence(evidence)).ToImmutableArray();
        var plans = sets.SelectMany(s => s.Plans).GroupBy(p => p.Symbol, StringComparer.Ordinal).Select(g => g.First())
            .OrderBy(p => p.Symbol, StringComparer.Ordinal).ToImmutableArray();
        var executable = sets.SelectMany(s => s.Coverage).Where(e => e.Executable && e.Symbol is not null)
            .Select(e => e.Symbol!).ToImmutableHashSet(StringComparer.Ordinal);
        return (sets, plans, executable);
    }

    /// <summary>
    /// Where a generation given the evidence disagrees with it. Every executable forward plan must be
    /// called through its twin, or say why it kept its direct import; a bridged one must be executable
    /// under the evidence the gate holds, or the generation and the gate read different evidence.
    /// </summary>
    public static IEnumerable<string> RoutingDisagreements(SwiftCallPlanSet routed)
    {
        var forward = routed.Plans.Where(p => p.Body.Kind == SwiftPlanKind.Forward).Select(p => p.Symbol)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var entry in routed.Coverage)
        {
            if (entry.Bridged == true && !entry.Executable)
                yield return $"{routed.Module}: {entry.Member} {entry.EntryPoint} is called through {entry.Symbol}, which the evidence does not prove executable ({entry.Evidence ?? "no evidence"})";
            else if (entry.Executable && entry.Symbol is { } symbol && forward.Contains(symbol) && entry.Bridged != true)
                yield return entry.BridgeRefusal is { } refusal
                    ? $"{routed.Module}: {entry.Member} {entry.EntryPoint} kept its direct import although {symbol} is executable: {refusal}"
                    : $"{routed.Module}: {entry.Member} {entry.EntryPoint} is executable through {symbol} but the generation did not route it; regenerate with this evidence";
        }
    }

    /// <summary>
    /// Where the routed generations as a whole disagree with the evidence: each one's
    /// <see cref="RoutingDisagreements"/>, and a generation that calls nothing through a bridge at
    /// all, since a routing check over no bridged transition passes without checking anything.
    /// </summary>
    public static IEnumerable<string> RoutedFindings(IReadOnlyList<SwiftCallPlanSet> routed)
    {
        foreach (var set in routed)
        {
            foreach (var disagreement in RoutingDisagreements(set))
                yield return disagreement;
        }
        if (routed.Count > 0 && !routed.Any(s => s.Coverage.Any(e => e.Bridged == true)))
            yield return "no routed generation calls any transition through a bridge: the routing check checked nothing";
    }

    public static int Run(IReadOnlyList<string> planFiles, IReadOnlyList<string> routedPlanFiles, string? evidencePath, string outputDirectory, ILogger logger)
    {
        var routedCount = routedPlanFiles.Count;
        planFiles = planFiles.Concat(routedPlanFiles).ToList();
        if (planFiles.Count == 0 || planFiles.Any(f => !File.Exists(f)))
        {
            logger.LogError("--swift-bridges needs at least one existing --swift-call-plans sidecar; missing: {Missing}",
                string.Join(", ", planFiles.Where(f => !File.Exists(f))));
            return 1;
        }
        if (evidencePath is null)
        {
            logger.LogError("--swift-bridges needs --swift-lowering-evidence <file>; without it no plan is executable");
            return 1;
        }
        if (!File.Exists(evidencePath))
        {
            logger.LogError("the lowering evidence {Path} does not exist: the swift-lowering-oracle target records it", evidencePath);
            return 1;
        }
        var toolchain = SwiftBridgeToolchain.Locate();
        if (toolchain is null)
        {
            logger.LogError("--swift-bridges needs xcrun with the iPhoneOS and iPhoneSimulator SDKs");
            return 1;
        }

        var evidence = SwiftLoweringEvidence.Load(evidencePath);
        var (sets, plans, executable) = Load(planFiles, evidence);
        Directory.CreateDirectory(outputDirectory);
        var report = new StringBuilder();
        var failures = new List<string>();

        // Load keeps the files' order, one set per file, so the routed sets are the trailing ones.
        var routedSets = sets.Skip(planFiles.Count - routedCount).ToList();
        foreach (var routed in routedSets)
            report.AppendLine($"{routed.Module}: routed generation, {routed.Coverage.Count(e => e.Bridged == true)} transition(s) called through a bridge");
        failures.AddRange(RoutedFindings(routedSets));

        foreach (var set in sets)
        {
            var planned = set.Coverage.Count(e => e.Status == SwiftPlanStatus.Planned);
            var line = $"{set.Module}: {set.Coverage.Length} transition(s), {planned} planned, {set.Coverage.Count(e => e.Executable)} executable";
            report.AppendLine(line);
            logger.LogInformation("{Line}", line);
            foreach (var group in set.Coverage.Where(e => e.Status == SwiftPlanStatus.Planned && !e.Executable)
                .GroupBy(e => Category(e.Evidence)).OrderBy(g => g.Key, StringComparer.Ordinal))
                report.AppendLine($"  not executable ({group.Count()}): {group.Key}");
        }
        // A prediction swiftc contradicts is a planner bug, not a plan to leave unwired.
        foreach (var entry in sets.SelectMany(s => s.Coverage).Where(e => e.Evidence?.Contains("the plan predicts", StringComparison.Ordinal) == true))
            failures.Add($"{entry.Member} {entry.EntryPoint}: {entry.Evidence}");
        if (executable.IsEmpty)
            failures.Add("no plan is executable: the evidence joins nothing, and a gate over no bridges proves nothing");

        var frames = 0;
        foreach (var architecture in new[] { SwiftArchitecture.Arm64, SwiftArchitecture.X86_64 })
        {
            var assembly = SwiftBridgeEmitter.Emit(plans.Where(p => p.Body.Kind != SwiftPlanKind.Reverse), architecture);
            var name = architecture.ToString().ToLowerInvariant();
            File.WriteAllText(Path.Combine(outputDirectory, $"bridges.{name}.s"), assembly.Source);
            foreach (var refusal in assembly.Refused.Where(r => executable.Contains(r.Symbol)))
                failures.Add($"{architecture}: the executable plan {refusal.Symbol} has no bridge: {refusal.Reason}");
            var missing = executable.Where(s => !assembly.Bridges.Contains(s) && !assembly.Refused.Any(r => r.Symbol == s)).ToList();
            foreach (var symbol in missing)
                failures.Add($"{architecture}: the executable plan {symbol} is not in any sidecar's plans");

            var validation = SwiftBridgeValidator.Validate(assembly, plans, toolchain, Path.Combine(outputDirectory, name));
            frames += validation.Functions.Length;
            var line = $"{architecture}: {assembly.Bridges.Length} bridge(s) ({assembly.Bridges.Count(executable.Contains)} executable), " +
                       $"{assembly.Refused.Length} without a template, {validation.Failures.Length} validation failure(s)";
            report.AppendLine(line);
            logger.LogInformation("{Line}", line);
            foreach (var refusal in assembly.Refused)
                report.AppendLine($"  no template: {refusal.Symbol}: {refusal.Reason}");
            failures.AddRange(validation.Failures.Select(f => $"{architecture}: {f}"));
        }

        report.AppendLine(failures.Count == 0 ? "green" : $"{failures.Count} failure(s):");
        foreach (var failure in failures)
            report.AppendLine("  " + failure);
        File.WriteAllText(Path.Combine(outputDirectory, "bridge-gate.txt"), report.ToString());
        foreach (var failure in failures)
            logger.LogError("{Failure}", failure);
        logger.LogInformation("Swift bridge gate: {Executable} executable plan(s), {Frames} bridge frame(s) validated, {Failures} failure(s); report {Report}",
            executable.Count, frames, failures.Count, Path.Combine(outputDirectory, "bridge-gate.txt"));
        return failures.Count == 0 ? 0 : 1;
    }

    /// <summary>A not-executable reason with its per-symbol detail dropped, for grouping.</summary>
    private static string Category(string? evidence)
    {
        if (evidence is null)
            return "no evidence";
        var first = evidence.Split("; ")[0];
        int on = first.IndexOf(" on ", StringComparison.Ordinal);
        if (first.StartsWith("swiftc shows no ", StringComparison.Ordinal))
            return "swiftc shows no definition or declaration of the target";
        if (first.StartsWith("swiftc gives ", StringComparison.Ordinal))
            return "swiftc's lowering differs from the plan's prediction";
        return on > 0 ? first[..on] : first;
    }
}

/// <summary>
/// The linked check of the bridges an image ships (<c>--validate-linked-bridges</c>): the runtime
/// framework, or a generated binding's wrapper as an app embeds it. The bridge gate validates each
/// bridge as assembled into a scratch image; the unwinder of a running app reads the unwind
/// information the image's own link wrote, so this holds every bridge to that, on every
/// architecture the image carries. For each one, the image's exported <c>SBSR_</c> functions must be
/// exactly the bridges the sidecars route through (a missing bridge fails the twin's import at run
/// time; a bridge no sidecar routes through is stale), and each must pass the validator against its
/// plan's frame.
/// </summary>
public static class SwiftBridgeLinkedCheck
{
    public const string BridgePrefix = "SBSR_";

    /// <summary>The bridges the sidecars route through, with each one's plan, by symbol (no underscore).</summary>
    public static ImmutableSortedDictionary<string, SwiftCallPlan> Routed(IEnumerable<SwiftCallPlanSet> sets)
    {
        var routed = ImmutableSortedDictionary.CreateBuilder<string, SwiftCallPlan>(StringComparer.Ordinal);
        foreach (var set in sets)
        {
            var plans = set.Plans.ToDictionary(p => p.Symbol, StringComparer.Ordinal);
            foreach (var entry in set.Coverage.Where(e => e.Bridged == true && e.Symbol is not null))
            {
                if (!plans.TryGetValue(entry.Symbol!, out var plan))
                    throw new InvalidOperationException($"{set.Module}: {entry.Member} is routed through {entry.Symbol}, which the sidecar does not plan");
                routed[entry.Symbol!] = plan;
            }
        }
        return routed.ToImmutable();
    }

    /// <summary>The findings of one architecture of an image: <paramref name="thinImage"/> is that architecture alone.</summary>
    public static IEnumerable<string> Check(
        string thinImage, SwiftArchitecture architecture, IReadOnlyDictionary<string, SwiftCallPlan> routed, SwiftBridgeToolchain toolchain)
    {
        var exported = SwiftBridgeValidator.DefinedSymbols(toolchain.Run("nm", "--defined-only", "-g", thinImage).Output).Keys
            .Where(s => s.StartsWith("_" + BridgePrefix, StringComparison.Ordinal))
            .Select(s => s[1..])
            .ToHashSet(StringComparer.Ordinal);
        foreach (var missing in routed.Keys.Where(s => !exported.Contains(s)))
            yield return $"{architecture}: {missing} is routed through but the image does not export it";
        foreach (var stale in exported.Where(s => !routed.ContainsKey(s)).Order(StringComparer.Ordinal))
            yield return $"{architecture}: the image exports {stale}, which no sidecar routes through";

        var frames = routed.ToDictionary(
            r => "_" + r.Key,
            r => r.Value.Body.Architectures.Single(a => a.Architecture == architecture).Frame,
            StringComparer.Ordinal);
        var validation = SwiftBridgeValidator.ValidateImage(thinImage, architecture, frames, toolchain,
            routed.Keys.Where(exported.Contains).Select(s => "_" + s));
        foreach (var failure in validation.Failures)
            yield return $"{architecture}: {failure}";
    }

    public static int Run(IReadOnlyList<string> images, IReadOnlyList<string> planFiles, string outputDirectory, ILogger logger)
    {
        var missingFiles = images.Concat(planFiles).Where(f => !File.Exists(f)).ToList();
        if (images.Count == 0 || planFiles.Count == 0 || missingFiles.Count > 0)
        {
            logger.LogError("--validate-linked-bridges needs existing images and --swift-call-plans sidecars; missing: {Missing}",
                missingFiles.Count > 0 ? string.Join(", ", missingFiles) : images.Count == 0 ? "an image" : "a sidecar");
            return 1;
        }
        var toolchain = SwiftBridgeToolchain.Locate();
        if (toolchain is null)
        {
            logger.LogError("--validate-linked-bridges needs xcrun");
            return 1;
        }
        var routed = Routed(planFiles.Select(f => SwiftCallPlanSet.From(SwiftCallPlanSet.Read(f))));
        if (routed.Count == 0)
        {
            logger.LogError("no sidecar routes any transition through a bridge: a linked check over no bridges checks nothing");
            return 1;
        }

        Directory.CreateDirectory(outputDirectory);
        var failures = new List<string>();
        var checkedPairs = 0;
        foreach (var image in images)
        {
            var (code, archsOutput, error) = toolchain.Run("lipo", "-archs", image);
            if (code != 0)
            {
                failures.Add($"{image}: lipo -archs failed: {error.Trim()}");
                continue;
            }
            var architectures = archsOutput.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var name in architectures)
            {
                SwiftArchitecture? architecture = name switch { "arm64" => SwiftArchitecture.Arm64, "x86_64" => SwiftArchitecture.X86_64, _ => null };
                if (architecture is null)
                {
                    failures.Add($"{image}: {name} is not an architecture the bridges are planned for");
                    continue;
                }
                var thin = image;
                if (architectures.Length > 1)
                {
                    thin = Path.Combine(outputDirectory, $"{Path.GetFileName(image)}.{checkedPairs}.{name}");
                    var thinned = toolchain.Run("lipo", image, "-thin", name, "-output", thin);
                    if (thinned.Code != 0)
                    {
                        failures.Add($"{image}: lipo -thin {name} failed: {thinned.Error.Trim()}");
                        continue;
                    }
                }
                var findings = Check(thin, architecture.Value, routed, toolchain).ToList();
                failures.AddRange(findings.Select(f => $"{image}: {f}"));
                checkedPairs++;
                logger.LogInformation("{Image} {Architecture}: {Bridges} linked bridge(s), {Failures} failure(s)",
                    image, name, routed.Count, findings.Count);
            }
        }
        foreach (var failure in failures)
            logger.LogError("{Failure}", failure);
        logger.LogInformation("Linked bridge check: {Bridges} bridge(s) over {Pairs} image/architecture pair(s), {Failures} failure(s)",
            routed.Count, checkedPairs, failures.Count);
        return failures.Count == 0 && checkedPairs > 0 ? 0 : 1;
    }
}
