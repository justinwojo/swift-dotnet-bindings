// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// A Debug .NET 11 lane runs the CoreCLR interpreter and earns its runtime-identity floor from its
/// own run: proven interpreter identity, every registered negative control present, run and graded
/// as expected, and no unexplained lowering against its previous Debug floor or its Release floor.
/// The last group executes the production seed method itself and shows that nothing is written
/// unless all of that holds.
/// </summary>
public class RuntimeIdentitySeedPolicyTests
{
    private static NegativeControlClass Probe => InterpreterNegativeControls.Find("SretSelfProbeTests")!;

    private static readonly IReadOnlyList<string> Inventory = ["SretSelfProbeTests", "StructTests", "ClosureTests"];

    private static RuntimeIdentitySeedProof Proven(IReadOnlyCollection<string>? inventory = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? grades = null, RuntimeExecutionLane? lane = RuntimeExecutionLane.Interpreter)
        => new(lane, inventory ?? Inventory,
            grades ?? new Dictionary<string, IReadOnlyList<string>> { ["SretSelfProbeTests"] = [] });

    // ---- The proof ----

    [Fact]
    public void DebugSeed_IsAllowedWithFullProof()
        => Assert.Empty(RuntimeIdentitySeedPolicy.ProofRefusals("Debug", Proven(), [Probe]));

    [Fact]
    public void ReleaseSeed_NeedsNoInterpreterProof()
        => Assert.Empty(RuntimeIdentitySeedPolicy.ProofRefusals("Release", null, [Probe]));

    [Fact]
    public void DebugSeed_RefusesWithoutLaneProof()
    {
        // macOS proves no lane; a run in which no class started has no evidence either.
        Assert.Contains(RuntimeIdentitySeedPolicy.ProofRefusals("Debug", null, [Probe]),
            r => r.Contains("did not prove its execution lane", StringComparison.Ordinal));
        Assert.Contains(RuntimeIdentitySeedPolicy.ProofRefusals("Debug", Proven(lane: null), [Probe]),
            r => r.Contains("did not prove its execution lane", StringComparison.Ordinal));
    }

    [Fact]
    public void DebugSeed_RefusesALaneProvenOtherThanInterpreter()
        => Assert.Contains(RuntimeIdentitySeedPolicy.ProofRefusals("Debug", Proven(lane: RuntimeExecutionLane.ReadyToRun), [Probe]),
            r => r.Contains("proved ReadyToRun", StringComparison.Ordinal));

    [Fact]
    public void DebugSeed_RefusesAControlMissingFromTheApp()
    {
        // The suite intersects the registry with the inventory, so a removed class would otherwise
        // vanish without a trace.
        var refusal = Assert.Single(RuntimeIdentitySeedPolicy.ProofRefusals("Debug",
            Proven(inventory: ["StructTests"], grades: new Dictionary<string, IReadOnlyList<string>>()), [Probe]));
        Assert.Contains("SretSelfProbeTests", refusal);
        Assert.Contains("not a test class of the app", refusal);
    }

    [Fact]
    public void DebugSeed_RefusesAControlThatDidNotRun()
    {
        var refusal = Assert.Single(RuntimeIdentitySeedPolicy.ProofRefusals("Debug",
            Proven(grades: new Dictionary<string, IReadOnlyList<string>>()), [Probe]));
        Assert.Contains("did not run", refusal);
    }

    [Fact]
    public void DebugSeed_RefusesAControlThatMisbehaved()
    {
        var problem = "SretSelfProbeTests.TestCombineDirectHeapBuffers passed: dotnet/runtime#136521 no longer reproduces";
        var refusal = Assert.Single(RuntimeIdentitySeedPolicy.ProofRefusals("Debug",
            Proven(grades: new Dictionary<string, IReadOnlyList<string>> { ["SretSelfProbeTests"] = [problem] }), [Probe]));
        Assert.Contains("did not behave as expected", refusal);
        Assert.Contains(problem, refusal);
    }

    [Fact]
    public void DebugSeed_RefusesAnEmptyControlRegistry()
        => Assert.Contains(RuntimeIdentitySeedPolicy.ProofRefusals("Debug", Proven(), []),
            r => r.Contains("no negative control is registered", StringComparison.Ordinal));

    [Fact]
    public void Registry_IsNotEmpty()
        // A Debug seed refuses an empty registry; this keeps the shipped registry seedable.
        => Assert.NotEmpty(InterpreterNegativeControls.Classes);

    // ---- Lowering ----

    private static RuntimeIdentityBaseline.PlatformIdentities Entry(int pass, params string[] skips) => new()
    {
        PassCount = pass,
        Skips = skips.Select(s => new RuntimeIdentityBaseline.TestId { Class = s.Split('.')[0], Method = s.Split('.')[1], Reason = "r" }).ToList(),
    };

    [Fact]
    public void Lowerings_NoneWhenTheIsolatedControlsReconcileTheReleaseCount()
        // The Release lane runs the four control tests as ordinary passes; the Debug lane runs them apart.
        => Assert.Empty(RuntimeIdentitySeedPolicy.Lowerings(Entry(96, "A.b"), null, Entry(100, "A.b"), 4));

    [Fact]
    public void Lowerings_ThePassCountBelowTheReleaseFloor()
        => Assert.Contains(RuntimeIdentitySeedPolicy.Lowerings(Entry(95, "A.b"), null, Entry(100, "A.b"), 4),
            l => l.Contains("below the Release floor's 100", StringComparison.Ordinal));

    [Fact]
    public void Lowerings_ThePassCountBelowThePreviousDebugFloor()
        => Assert.Contains(RuntimeIdentitySeedPolicy.Lowerings(Entry(97), Entry(98), Entry(90), 4),
            l => l.Contains("previous Debug floor's 98", StringComparison.Ordinal));

    [Fact]
    public void Lowerings_ASkipTheReleaseFloorDoesNotHave()
    {
        var lowering = Assert.Single(RuntimeIdentitySeedPolicy.Lowerings(Entry(100, "A.b", "C.d"), null, Entry(100, "A.b"), 4));
        Assert.Contains("C.d", lowering);
        Assert.Contains("Release floor", lowering);
    }

    [Fact]
    public void Lowerings_ASkipThePreviousDebugFloorDoesNotHave()
    {
        var lowering = Assert.Single(RuntimeIdentitySeedPolicy.Lowerings(Entry(100, "A.b"), Entry(100), Entry(100, "A.b"), 4));
        Assert.Contains("A.b", lowering);
        Assert.Contains("previous Debug floor", lowering);
    }

    [Fact]
    public void Lowerings_FewerSkipsAndMorePassesLowerNothing()
        => Assert.Empty(RuntimeIdentitySeedPolicy.Lowerings(Entry(105), Entry(100, "A.b"), Entry(100, "A.b"), 4));

    // ---- The production seed: every check before the write ----

    private const string DebugKey = "net11.0_simulator_iossimulator-arm64_coreclr_debug";
    private const string ReleaseKey = "net11.0_simulator_iossimulator-arm64_coreclr_release";

    private delegate string Seeder(string path, string configuration, string jsonl, RuntimeIdentitySeedProof? proof, string? justification);
    private static readonly Lazy<Seeder> Seed = new(CompileSeeder);

    /// <summary>Six passes and one skip the Release floor shares: with the four isolated control
    /// tests added back, exactly the Release floor of ten.</summary>
    private static string Results(int passes = 6, string skip = "A.b")
    {
        var rows = Enumerable.Range(0, passes).Select(i => $"{{\"class\":\"StructTests\",\"test\":\"Test{i}\",\"status\":\"pass\"}}");
        var parts = skip.Split('.');
        rows = rows.Append($"{{\"class\":\"{parts[0]}\",\"test\":\"{parts[1]}\",\"status\":\"skip\",\"reason\":\"by design\"}}");
        return string.Join("\n", rows) + $"\n{{\"done\":true,\"total\":{passes + 1},\"passed\":{passes},\"failed\":0,\"skipped\":1}}\n";
    }

    private static string WithBaseline(Func<string, string> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), "runtime-seed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "runtime-identity-net11-baseline.json");
            new RuntimeIdentityBaseline().WithPlatform(ReleaseKey, Entry(10, "A.b")).Save(path);
            return action(path);
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void AssertRefusedWithoutWrite(string configuration, string jsonl, RuntimeIdentitySeedProof? proof,
        string? justification, string expected)
    {
        WithBaseline(path =>
        {
            var before = File.ReadAllText(path);
            var error = Seed.Value(path, configuration, jsonl, proof, justification);
            Assert.Contains(expected, error);
            Assert.Equal(before, File.ReadAllText(path));
            return "";
        });
    }

    [Fact]
    public void ProductionSeed_WritesAProvenDebugFloor()
    {
        WithBaseline(path =>
        {
            Assert.Equal("", Seed.Value(path, "Debug", Results(), Proven(), null));
            var entry = RuntimeIdentityBaseline.Load(path).Platforms[DebugKey];
            Assert.Equal(6, entry.PassCount);
            Assert.Null(entry.Justification);
            Assert.Equal("fixture", RuntimeIdentityBaseline.Load(path).GitSha);
            return "";
        });
    }

    [Fact]
    public void ProductionSeed_AMisbehavingControlWritesNothing()
        // The ordering hole: the suite is reported, and could seed, before the run fails on its
        // control problems. The seed itself must refuse.
        => AssertRefusedWithoutWrite("Debug", Results(),
            Proven(grades: new Dictionary<string, IReadOnlyList<string>> { ["SretSelfProbeTests"] = ["TestCombineViaCdeclControl recorded fail, expected pass"] }),
            null, "did not behave as expected");

    [Fact]
    public void ProductionSeed_AControlRemovedFromTheAppWritesNothing()
        => AssertRefusedWithoutWrite("Debug", Results(),
            Proven(inventory: ["StructTests"], grades: new Dictionary<string, IReadOnlyList<string>>()),
            null, "not a test class of the app");

    [Fact]
    public void ProductionSeed_NoLaneProofWritesNothing()
        => AssertRefusedWithoutWrite("Debug", Results(), null, null, "did not prove its execution lane");

    [Fact]
    public void ProductionSeed_AnUnjustifiedLoweringWritesNothing()
        => AssertRefusedWithoutWrite("Debug", Results(passes: 5), Proven(), null, "below the Release floor's 10");

    [Fact]
    public void ProductionSeed_AJustifiedLoweringRecordsItsJustification()
    {
        WithBaseline(path =>
        {
            Assert.Equal("", Seed.Value(path, "Debug", Results(passes: 6, skip: "C.d"), Proven(), "  C.d skips on the interpreter by design  "));
            var entry = RuntimeIdentityBaseline.Load(path).Platforms[DebugKey];
            Assert.Equal("C.d skips on the interpreter by design", entry.Justification);
            Assert.Contains("\"justification\"", File.ReadAllText(path));
            return "";
        });
    }

    [Fact]
    public void ProductionSeed_AJustificationIsRecordedOnlyWhereItExplainsALowering()
    {
        // One justification serves a run that seeds several lanes; a lane that lowers nothing keeps none.
        WithBaseline(path =>
        {
            Assert.Equal("", Seed.Value(path, "Debug", Results(), Proven(), "explains another lane"));
            Assert.Null(RuntimeIdentityBaseline.Load(path).Platforms[DebugKey].Justification);
            Assert.DoesNotContain("\"justification\"", File.ReadAllText(path));
            return "";
        });
    }

    [Fact]
    public void ProductionSeed_ADebugFloorNeedsItsReleaseFloor()
    {
        var directory = Path.Combine(Path.GetTempPath(), "runtime-seed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "runtime-identity-net11-baseline.json");
            new RuntimeIdentityBaseline().Save(path);
            var before = File.ReadAllText(path);
            Assert.Contains("seed the Release lane first", Seed.Value(path, "Debug", Results(), Proven(), null));
            Assert.Equal(before, File.ReadAllText(path));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void ProductionSeed_AReleaseFloorKeepsItsRunLevelPreconditions()
    {
        WithBaseline(path =>
        {
            // Release lanes need no interpreter proof and are not checked for lowering.
            Assert.Equal("", Seed.Value(path, "Release", Results(passes: 3), null, null));
            var entry = RuntimeIdentityBaseline.Load(path).Platforms[ReleaseKey];
            Assert.Equal(3, entry.PassCount);
            Assert.DoesNotContain("\"justification\"", File.ReadAllText(path));
            return "";
        });
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Test-only dynamically compiled production method, retained in full.")]
    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "Test-only dynamically compiled fixture entry, retained in full.")]
    private static Seeder CompileSeeder()
    {
        var root = LocateRepoRoot();
        var production = CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root, "build", "Build.RuntimeTests.cs")))
            .GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.Text == "SeedNet11RuntimeBaseline");
        // The production method is inserted verbatim; only the run's switches, the git read and the
        // log are replaced. The models come from this test assembly.
        var fixture = $$"""
            #nullable enable
            using System;
            using System.Collections.Generic;
            using System.IO;
            using System.Linq;
            public class SeedFixture {
                sealed record Context(bool IsIsolated);
                Context ProfileContext = new(true);
                bool MonoAot => false;
                bool ReusedAppUngraded => false;
                string? ClassFilter => null;
                string? IdentityFloorJustification;
                RuntimeTestLane RuntimeLane = RuntimeTestLane.Create("net11.0", "net11.0", null, false, false, false);
                static string ReadHeadShaShort() => "fixture";
                static class Log {
                    public static void Information(string text, params object?[] args) { }
                    public static void Warning(string text, params object?[] args) { }
                    public static void Error(string text, params object?[] args) { }
                }
                {{production}}
                public static string Run(string path, string configuration, string jsonl, RuntimeIdentitySeedProof? proof, string? justification) {
                    var fixture = new SeedFixture {
                        IdentityFloorJustification = justification,
                        RuntimeLane = RuntimeTestLane.Create("net11.0", "net11.0", configuration, false, false, false),
                    };
                    var lane = fixture.RuntimeLane;
                    try {
                        fixture.SeedNet11RuntimeBaseline("Simulator", lane.BaselineKey("simulator", "iossimulator-arm64", false),
                            lane.ReleaseSiblingKey("simulator", "iossimulator-arm64", false), path, JsonlTestResults.Parse(jsonl),
                            false, false, proof);
                        return "";
                    } catch (InvalidOperationException error) { return error.Message; }
                }
            }
            """;
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator).Select(p => MetadataReference.CreateFromFile(p));
        var compilation = CSharpCompilation.Create("RuntimeSeed" + Guid.NewGuid().ToString("N"),
            [CSharpSyntaxTree.ParseText(fixture)], references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var stream = new MemoryStream();
        var emitted = compilation.Emit(stream);
        Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));
        return Assembly.Load(stream.ToArray()).GetType("SeedFixture")!.GetMethod("Run")!.CreateDelegate<Seeder>();
    }

    private static string LocateRepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "build", "Build.RuntimeTests.cs"))) return d.FullName;
        throw new InvalidOperationException("Could not locate the production runtime seed source.");
    }
}
