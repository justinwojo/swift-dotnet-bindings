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

    /// <summary>The C# sources under <paramref name="directories"/>, build output excluded, in ordinal path order.</summary>
    public static IReadOnlyList<string> Sources(IEnumerable<string> directories)
    {
        var separator = Path.DirectorySeparatorChar;
        return directories
            .SelectMany(d => Directory.EnumerateFiles(d, "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{separator}obj{separator}", StringComparison.Ordinal) && !f.Contains($"{separator}bin{separator}", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .Select(File.ReadAllText)
            .ToList();
    }

    /// <summary>The plan set of the table at <paramref name="tablePath"/> over the assembly sources <paramref name="sources"/>.</summary>
    public static SwiftCallPlanSet Plan(string tablePath, IEnumerable<string> sources, TypeDatabase standardLibrary, out SwiftHandWrittenTransitionTable table)
    {
        table = SwiftHandWrittenTransitionTable.Load(tablePath);
        var ledger = table.Ledger(sources);
        var lowering = table.LoweringOver(new SwiftLayoutValueLowering(Array.Empty<ModuleDecl>(), standardLibrary));
        return SwiftCallPlanSet.Build(ledger, new SwiftCallPlanner(ledger.Types, SwiftArchitectureModel.Default(lowering)));
    }

    /// <summary><c>--emit-hand-written-plans</c>: writes the table's plan sidecar into <paramref name="outputDirectory"/>.</summary>
    public static int Run(string tablePath, IReadOnlyList<string> sourceDirectories, string outputDirectory, ILogger logger)
    {
        if (!File.Exists(tablePath) || sourceDirectories.Count == 0 || sourceDirectories.Any(d => !Directory.Exists(d)))
        {
            logger.LogError("--emit-hand-written-plans needs an existing --transition-facts table and at least one existing --plan-sources directory");
            return 1;
        }
        var set = Plan(tablePath, Sources(sourceDirectories), StandardLibrary(), out var table);
        Directory.CreateDirectory(outputDirectory);
        var path = Path.Combine(outputDirectory, SwiftCallPlanSet.FileName(table.Assembly));
        File.WriteAllText(path, set.ToJson());
        var refused = set.Count(SwiftPlanStatus.Refused);
        logger.LogInformation("{Assembly}: {Planned} planned, {Refused} refused, {Plans} plan(s) -> {Path}",
            table.Assembly, set.Count(SwiftPlanStatus.Planned), refused, set.Plans.Length, path);
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

    public static int Run(IReadOnlyList<string> planFiles, string? evidencePath, string outputDirectory, ILogger logger)
    {
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
