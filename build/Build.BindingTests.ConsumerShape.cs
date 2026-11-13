// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Build.BindingTests.ConsumerShape.cs — the consumer-shape equivalence gate for
// `nuke binding-tests --compile-only`.
//
// The BindingTests regen gives the generator the dependency fixture as an xcframework and nothing
// else. A consumer never does: the SDK builds a binding of a binding-backed dependency in two
// passes (generate with --sdk-mode --skip-wrapper-compilation, then compile the wrapper), and it
// hands the generator the dependency twice — as its xcframework AND as the module database the
// dependency's own binding published. Every gate downstream of the regen compiles and runs the first
// shape only, so a difference in what the second shape emits ships unseen. One did: with the
// database loaded, the dependency's declarations were never read, and a local protocol refining a
// dependency protocol got an EveryProtocol conformance without the parent's witnesses — a wrapper
// that swiftc rejects in every consumer build while the repo's own gates stayed green.
//
// The gate regenerates the main fixture in the consumer's shape, from the same arguments plus the
// consumer's, and requires the emitted C#, Swift and thunk assembly to be identical to what the
// rest of the gate compiles and runs. Equality, not a compile, is the right check: it covers every
// difference the consumer shape can introduce (dependency input, SDK mode, the missing verify-recover
// loop) in one comparison, and lets every later gate stand in for the consumer path.
//
// Fail-closed by construction, with no --permissive arm: an equivalence, not a baseline. A positive
// control keeps it from passing vacuously — the consumer run must report that it took the
// dependency's records from the database and its declarations from the xcframework.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tooling;
using Serilog;

partial class Build
{
    AbsolutePath BtConsumerShapeOutputDir => BindingTestsDir / "output-consumer-shape";

    static readonly string[] ConsumerShapeComparedExtensions = { ".cs", ".swift", ".s" };

    /// <summary>
    /// Regenerates the main fixture the way an SDK consumer of a binding-backed dependency does and
    /// asserts the emitted source is identical to the gated regen's. Invoked from the --compile-only
    /// regen once the dependency has been generated and before its outputs are folded into
    /// <see cref="BtOutputDir"/>, so the comparison sees only the main module's files.
    /// </summary>
    void RunConsumerShapeGate(IReadOnlyList<string> mainGenArgs, AbsolutePath depOutputDir, int mainExitCode)
    {
        Log.Information("=========================================");
        Log.Information(" Consumer-shape equivalence gate");
        Log.Information("=========================================");

        var depDatabase = depOutputDir / $"{DepModuleName}Database.xml";
        if (!File.Exists(depDatabase))
            throw new Exception(
                $"Consumer-shape gate: the dependency generation published no module database at {depDatabase}. " +
                "A consumer of the dependency's binding receives exactly this file, so without it the consumer " +
                "shape cannot be reproduced.");

        var consumerDir = BtConsumerShapeOutputDir;
        if (Directory.Exists(consumerDir))
            consumerDir.DeleteDirectory();
        consumerDir.CreateDirectory();

        // The gated regen's arguments, re-pointed at the scratch directory, plus exactly what the
        // SDK adds for a binding whose dependency is itself a binding.
        var args = mainGenArgs.Where(a => !a.StartsWith("-o ", StringComparison.Ordinal)).ToList();
        args.Add($"-o \"{consumerDir}\"");
        args.Add($"--module-database \"{depDatabase}\"");
        args.Add("--sdk-mode");
        args.Add("--skip-wrapper-compilation");

        var process = ProcessTasks.StartProcess(
            "dotnet", string.Join(" ", args),
            workingDirectory: BindingTestsDir,
            logOutput: false);
        process.WaitForExit();
        if (process.ExitCode != mainExitCode)
        {
            foreach (var line in process.Output)
                Log.Information("[consumer-shape] {Text}", line.Text);
            throw new Exception(
                $"Consumer-shape gate: the consumer-shaped generation of {ModuleName} exited {process.ExitCode}, " +
                $"the gated regen exited {mainExitCode}. Output kept at {consumerDir}.");
        }

        AssertConsumerShapeReadDependencyFromDatabase(consumerDir);

        var differences = CompareConsumerShapeSources(BtOutputDir, consumerDir);
        if (differences.Count > 0)
        {
            foreach (var difference in differences.Take(40))
                Log.Error("  {Difference}", difference);
            throw new Exception(
                $"Consumer-shape gate: {differences.Count} emitted source file(s) differ between the gated regen " +
                $"({BtOutputDir}) and the consumer-shaped regen ({consumerDir}). An SDK consumer of {ModuleName} " +
                "would build source no gate compiled or ran. Output kept for inspection.");
        }

        consumerDir.DeleteDirectory();
        Log.Information("Consumer-shape gate: emitted sources identical with {Dependency} supplied as a published binding.",
            DepModuleName);
    }

    /// <summary>
    /// The positive control: the consumer-shaped run must say it took the dependency's type records from
    /// the module database and its declarations from the xcframework. A run that silently ignored the
    /// database would emit the raw-ABI output and pass the comparison without exercising the shape.
    /// </summary>
    void AssertConsumerShapeReadDependencyFromDatabase(AbsolutePath consumerDir)
    {
        var manifestPath = consumerDir / "binding-artifact-manifest.json";
        if (!File.Exists(manifestPath))
            throw new Exception($"Consumer-shape gate: no binding-artifact-manifest.json at {manifestPath}.");

        using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
        if (!doc.RootElement.TryGetProperty("InputResolution", out var inputResolution) ||
            !inputResolution.TryGetProperty("Decisions", out var decisions) ||
            decisions.ValueKind != JsonValueKind.Array)
            throw new Exception($"Consumer-shape gate: {manifestPath} carries no InputResolution decisions.");

        var dependencyDecisions = decisions.EnumerateArray()
            .Where(d => d.GetProperty("Category").GetString() == "Dependency" &&
                        (d.GetProperty("Detail").GetString() ?? "").Contains($"'{DepModuleName}'", StringComparison.Ordinal))
            .ToList();
        var degraded = dependencyDecisions.FirstOrDefault(d => d.GetProperty("Severity").GetString() != "Info");
        if (degraded.ValueKind != JsonValueKind.Undefined)
            throw new Exception(
                $"Consumer-shape gate: the consumer-shaped run degraded its dependency input: {degraded.GetProperty("Detail").GetString()}");
        if (!dependencyDecisions.Any(d => (d.GetProperty("Detail").GetString() ?? "").Contains("module database", StringComparison.Ordinal)))
            throw new Exception(
                $"Consumer-shape gate: the consumer-shaped run did not record reading {DepModuleName}'s declarations " +
                "alongside its published module database, so the comparison would not exercise the consumer shape.");
    }

    /// <summary>
    /// Byte-compares the main module's emitted C#, Swift and thunk-assembly files (top level only — the
    /// wrapper's build directory and the dependency's subdirectory are not emitted source of this module).
    /// </summary>
    static List<string> CompareConsumerShapeSources(AbsolutePath gatedDir, AbsolutePath consumerDir)
    {
        static SortedSet<string> Sources(AbsolutePath dir) => new(
            Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly)
                .Where(p => ConsumerShapeComparedExtensions.Contains(Path.GetExtension(p), StringComparer.Ordinal))
                .Select(Path.GetFileName)!,
            StringComparer.Ordinal);

        var gated = Sources(gatedDir);
        var consumer = Sources(consumerDir);
        if (gated.Count == 0)
            throw new Exception($"Consumer-shape gate: no emitted sources found in {gatedDir}.");

        var differences = new List<string>();
        differences.AddRange(gated.Except(consumer).Select(f => $"only in the gated regen: {f}"));
        differences.AddRange(consumer.Except(gated).Select(f => $"only in the consumer shape: {f}"));
        foreach (var file in gated.Intersect(consumer))
        {
            var gatedText = File.ReadAllLines(gatedDir / file);
            var consumerText = File.ReadAllLines(consumerDir / file);
            if (gatedText.SequenceEqual(consumerText, StringComparer.Ordinal))
                continue;
            var line = Enumerable.Range(0, Math.Min(gatedText.Length, consumerText.Length))
                .FirstOrDefault(i => !string.Equals(gatedText[i], consumerText[i], StringComparison.Ordinal),
                    Math.Min(gatedText.Length, consumerText.Length));
            differences.Add($"differs: {file} (first at line {line + 1}; {gatedText.Length} vs {consumerText.Length} lines)");
        }
        return differences;
    }
}
