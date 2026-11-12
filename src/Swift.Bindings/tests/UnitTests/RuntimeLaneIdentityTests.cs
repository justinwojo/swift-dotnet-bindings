// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SwiftBindings.InterpIdentity;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// The per-class lane proof of the .NET 11 RuntimeTestsApp lanes: <c>--coreclr-interp</c> selection,
/// witness classification, the verdict (a lane that silently ran another mode fails), the JSONL
/// witness records and the lane report.
/// </summary>
public class RuntimeLaneIdentityTests
{
    const string App = "RuntimeTestsApp";
    const string Stub = "CallJittedMethodRetVoid";

    static LaneWitness Interp(string cls, string image = "libcoreclr.dylib") => new(cls, image, Stub, 0x1000);
    static LaneWitness R2R(string cls, string symbol = "RuntimeTestsApp_LaneProbe__Capture") => new(cls, $"{App}.r2r.dylib", symbol, 0x2000);
    static LaneWitness Aot(string cls) => new(cls, App, "RuntimeTestsApp_LaneProbe__Capture", 0x3000);

    static ReadyToRunInventory Map(params string[] symbols) => new(new HashSet<string>(symbols, StringComparer.Ordinal));
    static readonly ReadyToRunInventory CoreLibOnly = Map("S_P_CoreLib_System_String__Concat");
    static readonly ReadyToRunInventory WithApp = Map("S_P_CoreLib_System_String__Concat", "RuntimeTestsApp_LaneProbe__Capture");

    static RuntimeLaneEvidence Evidence(ReadyToRunInventory? map, string[] classes, params LaneWitness[] witnesses) =>
        new(App, classes, witnesses, map);

    // ---- --coreclr-interp selection ----

    [Fact]
    public void CoreClrInterp_RequiresTheNet11Framework()
    {
        var ex = Assert.Throws<ArgumentException>(() => RuntimeTestLane.Create(null, null, null, false, false, false, coreClrInterp: true));
        Assert.Contains("net11", ex.Message);
    }

    [Fact]
    public void CoreClrInterp_RefusesRelease_AndNamesTheReadyToRunLeg()
    {
        var ex = Assert.Throws<ArgumentException>(() => RuntimeTestLane.Create(null, "net11.0", "Release", false, false, false, coreClrInterp: true));
        Assert.Contains("--binding-test-configuration Release", ex.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CoreClrInterp_IsTheDebugCoreClrInterpreterOnSimulatorAndDevice(bool device)
    {
        var lane = RuntimeTestLane.Create(null, "net11.0", null, false, device, false, coreClrInterp: true);
        Assert.True(lane.CoreClrInterpreter);
        Assert.Equal("coreclr", lane.Engine(device));
        Assert.Equal("Debug", lane.BuildConfiguration(device, false));
        Assert.Equal(RuntimeExecutionLane.Interpreter, lane.ExecutionLane(device));
    }

    [Fact]
    public void CoreClrInterp_RefusesMonoAot()
        => Assert.Throws<ArgumentException>(() => RuntimeTestLane.Create(null, "net11.0", null, false, true, true, coreClrInterp: true));

    [Theory]
    [InlineData("Release", false, false, RuntimeExecutionLane.ReadyToRun)]   // simulator Release
    [InlineData("Debug", false, false, RuntimeExecutionLane.Interpreter)]    // simulator Debug, no flag
    [InlineData("Release", true, true, RuntimeExecutionLane.ReadyToRun)]     // --device --core-clr Release
    [InlineData("Debug", true, false, RuntimeExecutionLane.NativeAot)]       // --device default
    public void ExecutionLane_FollowsEngineAndConfiguration(string configuration, bool device, bool coreClr, RuntimeExecutionLane expected)
        => Assert.Equal(expected, RuntimeTestLane.Create(null, "net11.0", configuration, coreClr, device, false).ExecutionLane(device));

    [Fact]
    public void Net10_HasNoExecutionLaneClaimAndKeepsItsRecoveryBudget()
    {
        var lane = RuntimeTestLane.Create(null, null, null, false, false, false);
        Assert.Null(lane.ExecutionLane(false));
        Assert.Equal(RuntimeTestLane.DefaultCrashRecoveryRetries, lane.CrashRecoveryRetries(400));
    }

    [Theory]
    [InlineData(1, 5)]
    [InlineData(5, 5)]
    [InlineData(312, 312)]
    public void Net11_RecoveryBudgetCoversEveryEligibleClass(int eligible, int expected)
        => Assert.Equal(expected, RuntimeTestLane.Create(null, "net11.0", null, false, false, false).CrashRecoveryRetries(eligible));

    // ---- Expected / Classify ----

    [Theory]
    [InlineData("coreclr", "Debug", RuntimeExecutionLane.Interpreter)]
    [InlineData("coreclr", "Release", RuntimeExecutionLane.ReadyToRun)]
    [InlineData("nativeaot", "Release", RuntimeExecutionLane.NativeAot)]
    public void Expected_MapsEngineAndConfiguration(string engine, string configuration, RuntimeExecutionLane expected)
        => Assert.Equal(expected, RuntimeLaneIdentity.Expected(engine, configuration));

    [Theory]
    [InlineData("mono", "Debug")]
    [InlineData("coreclr", "Profile")]
    public void Expected_RejectsUnknownEngineOrConfiguration(string engine, string configuration)
        => Assert.Throws<ArgumentException>(() => RuntimeLaneIdentity.Expected(engine, configuration));

    [Theory]
    [InlineData("libcoreclr.dylib", Stub, App, RuntimeExecutionLane.Interpreter)]
    [InlineData("libcoreclr", Stub, App, RuntimeExecutionLane.Interpreter)]               // device: the runtime as a framework
    [InlineData(App, Stub, App, RuntimeExecutionLane.Interpreter)]                       // statically linked runtime
    [InlineData("RuntimeTestsApp.r2r.dylib", "RuntimeTestsApp_X__Y", App, RuntimeExecutionLane.ReadyToRun)]
    [InlineData("RuntimeTestsApp.r2r", "RuntimeTestsApp_X__Y", App, RuntimeExecutionLane.ReadyToRun)]
    [InlineData("RuntimeTestsApp.tvOS.r2r.dylib", "RuntimeTestsApp_tvOS_X__Y", "RuntimeTestsApp.tvOS", RuntimeExecutionLane.ReadyToRun)]
    [InlineData(App, "RuntimeTestsApp_X__Y", App, RuntimeExecutionLane.NativeAot)]
    public void Classify_NamesTheLaneOfEachCaller(string image, string symbol, string app, RuntimeExecutionLane expected)
        => Assert.Equal(expected, RuntimeLaneIdentity.Classify(new LaneWitness("C", image, symbol, 0x10), app));

    [Theory]
    [InlineData("libSystem.dylib", Stub, App)]                                    // a stub-named symbol outside the runtime
    [InlineData("libcoreclr.dylib", "ProcessCLRException", App)]                  // the runtime, but not its call stub
    [InlineData("libcoreclr", "GetCLRRuntimeHost", App)]                          // a stripped device runtime, unresolved
    [InlineData("RuntimeTestsAppXtvOS.r2r.dylib", "x", "RuntimeTestsApp.tvOS")]   // the app name is matched literally
    [InlineData("RuntimeTestsApp.tvOS", "x", App)]                                // another app's executable
    [InlineData("Other.r2r.dylib", "x", App)]
    [InlineData("?", null, App)]
    public void Classify_RefusesAnyOtherCaller(string image, string? symbol, string app)
        => Assert.Null(RuntimeLaneIdentity.Classify(new LaneWitness("C", image, symbol, 0x10), app));

    [Fact]
    public void Classify_AddressZeroIsUnknown()
        => Assert.Null(RuntimeLaneIdentity.Classify(new LaneWitness("C", "libcoreclr.dylib", Stub, 0), App));

    // ---- Verify: acceptance ----

    [Fact]
    public void Interpreter_AcceptsEveryClassOnTheStubAndACoreLibOnlyComposite()
    {
        var evidence = Evidence(CoreLibOnly, ["A", "B"], Interp("A"), Interp("B"));
        var verdict = RuntimeLaneIdentity.Verify(RuntimeExecutionLane.Interpreter, evidence);
        Assert.True(verdict.Accepted, string.Join("; ", verdict.Problems));
        Assert.All(verdict.ByClass.Values, l => Assert.Equal(RuntimeExecutionLane.Interpreter, l));
        Assert.DoesNotContain(RuntimeLaneIdentity.Mislabels(RuntimeExecutionLane.Interpreter, evidence), v => v.Accepted);
    }

    [Fact]
    public void ReadyToRun_AcceptsMappedPrecompiledCallers()
    {
        var evidence = Evidence(WithApp, ["A"], R2R("A"));
        Assert.True(RuntimeLaneIdentity.Verify(RuntimeExecutionLane.ReadyToRun, evidence).Accepted);
        Assert.DoesNotContain(RuntimeLaneIdentity.Mislabels(RuntimeExecutionLane.ReadyToRun, evidence), v => v.Accepted);
    }

    [Fact]
    public void NativeAot_AcceptsExecutableCallersWithNoComposite()
    {
        var evidence = Evidence(null, ["A"], Aot("A"));
        Assert.True(RuntimeLaneIdentity.Verify(RuntimeExecutionLane.NativeAot, evidence).Accepted);
        Assert.DoesNotContain(RuntimeLaneIdentity.Mislabels(RuntimeExecutionLane.NativeAot, evidence), v => v.Accepted);
    }

    // ---- Verify: a lane that silently ran another mode fails ----

    [Fact]
    public void ReleaseClaim_ThatRanInterpreted_Fails()
    {
        // A Release build whose app was not precompiled: the classic silent fallback.
        var verdict = RuntimeLaneIdentity.Verify(RuntimeExecutionLane.ReadyToRun, Evidence(CoreLibOnly, ["A"], Interp("A")));
        Assert.False(verdict.Accepted);
        Assert.Contains(verdict.Problems, p => p.Contains("A: claimed ReadyToRun") && p.Contains("Interpreter"));
        Assert.Contains(verdict.Problems, p => p.Contains("no RuntimeTestsApp bodies"));
    }

    [Fact]
    public void InterpreterClaim_ThatRanPrecompiled_Fails()
    {
        var verdict = RuntimeLaneIdentity.Verify(RuntimeExecutionLane.Interpreter, Evidence(WithApp, ["A"], R2R("A")));
        Assert.False(verdict.Accepted);
        Assert.Contains(verdict.Problems, p => p.Contains("A: claimed Interpreter") && p.Contains("ReadyToRun"));
        Assert.Contains(verdict.Problems, p => p.Contains("1 precompiled RuntimeTestsApp bodies"));
    }

    [Fact]
    public void OneMislaneClass_FailsTheRunEvenWhenTheRestAgree()
    {
        var verdict = RuntimeLaneIdentity.Verify(RuntimeExecutionLane.Interpreter,
            Evidence(CoreLibOnly, ["A", "B"], Interp("A"), Aot("B")));
        Assert.False(verdict.Accepted);
        Assert.Equal(RuntimeExecutionLane.NativeAot, verdict.ByClass["B"]);
    }

    [Fact]
    public void ClassWithResultsButNoWitness_Fails()
    {
        var verdict = RuntimeLaneIdentity.Verify(RuntimeExecutionLane.Interpreter, Evidence(CoreLibOnly, ["A", "B"], Interp("A")));
        Assert.False(verdict.Accepted);
        Assert.Contains(verdict.Problems, p => p.StartsWith("B: ran without a lane witness"));
        Assert.Null(verdict.ByClass["B"]);
    }

    [Fact]
    public void NoWitnessAtAll_Fails()
        => Assert.Contains("no class recorded a lane witness",
            RuntimeLaneIdentity.Verify(RuntimeExecutionLane.Interpreter, Evidence(CoreLibOnly, [])).Problems);

    [Fact]
    public void ProbeWithNoCallerFrame_Fails()
    {
        var verdict = RuntimeLaneIdentity.Verify(RuntimeExecutionLane.Interpreter,
            Evidence(CoreLibOnly, ["A"], new LaneWitness("A", "?", null, 0)));
        Assert.Contains(verdict.Problems, p => p.Contains("found no caller frame"));
    }

    [Fact]
    public void UnknownCaller_Fails()
    {
        var verdict = RuntimeLaneIdentity.Verify(RuntimeExecutionLane.Interpreter,
            Evidence(CoreLibOnly, ["A"], new LaneWitness("A", "libSystem.dylib", "start", 0x10)));
        Assert.Contains(verdict.Problems, p => p.Contains("is neither the interpreter's call stub"));
    }

    [Fact]
    public void ClassWhoseAttemptsDisagree_HasNoSingleLaneAndFails()
    {
        var verdict = RuntimeLaneIdentity.Verify(RuntimeExecutionLane.Interpreter,
            Evidence(CoreLibOnly, ["A"], Interp("A"), R2R("A")));
        Assert.False(verdict.Accepted);
        Assert.Null(verdict.ByClass["A"]);
    }

    [Fact]
    public void InterpreterClaim_WithoutAMap_Fails()
        => Assert.Contains(RuntimeLaneIdentity.Verify(RuntimeExecutionLane.Interpreter, Evidence(null, ["A"], Interp("A"))).Problems,
            p => p.Contains("no crossgen2 map"));

    [Fact]
    public void ReadyToRunCaller_AbsentFromTheMap_Fails()
    {
        var verdict = RuntimeLaneIdentity.Verify(RuntimeExecutionLane.ReadyToRun,
            Evidence(WithApp, ["A"], R2R("A", "RuntimeTestsApp_Unmapped__Body")));
        Assert.Contains(verdict.Problems, p => p.Contains("not a precompiled body in the composite map"));
    }

    [Fact]
    public void ReadyToRunClaim_WithoutAMap_Fails()
        => Assert.Contains(RuntimeLaneIdentity.Verify(RuntimeExecutionLane.ReadyToRun, Evidence(null, ["A"], R2R("A"))).Problems,
            p => p.Contains("no crossgen2 map"));

    [Fact]
    public void NativeAotClaim_BesideACrossgen2Map_Fails()
        => Assert.Contains(RuntimeLaneIdentity.Verify(RuntimeExecutionLane.NativeAot, Evidence(CoreLibOnly, ["A"], Aot("A"))).Problems,
            p => p.Contains("ran crossgen2"));

    [Fact]
    public void ResultlessClassNamesAreIgnored()
    {
        // App-level failures ("Test Suite") carry no class; they cannot have a witness.
        var verdict = RuntimeLaneIdentity.Verify(RuntimeExecutionLane.Interpreter, Evidence(CoreLibOnly, ["", "A"], Interp("A")));
        Assert.True(verdict.Accepted, string.Join("; ", verdict.Problems));
    }

    // ---- JSONL witness records ----

    [Fact]
    public void Jsonl_ParsesWitnessRecords_AndMergeKeepsEveryAttempt()
    {
        var first = JsonlTestResults.Parse(
            "{\"run_token\":\"t\"}\n" +
            "{\"lane_class\":\"A\",\"image\":\"libcoreclr.dylib\",\"symbol\":\"CallJittedMethodRetVoid\",\"address\":\"0x1a2b\"}\n" +
            "{\"lane_class\":\"B\",\"image\":\"?\",\"symbol\":null,\"address\":\"0x0\"}\n" +
            "{\"lane_class\":\"C\",\"image\":\"x\",\"symbol\":\"s\",\"address\":\"0xnothex\"}\n" +
            "{\"class\":\"A\",\"test\":\"T\",\"status\":\"pass\"}\n");
        Assert.Equal(new[] { new LaneWitness("A", "libcoreclr.dylib", "CallJittedMethodRetVoid", 0x1a2b), new LaneWitness("B", "?", null, 0) },
            first.LaneWitnesses);
        Assert.Single(first.Tests); // a malformed witness line is skipped, not fatal

        var aggregate = new JsonlTestResults();
        aggregate.Merge(first);
        aggregate.Merge(JsonlTestResults.Parse("{\"lane_class\":\"A\",\"image\":\"libcoreclr.dylib\",\"symbol\":\"CallJittedMethodRetVoid\",\"address\":\"0x1a2b\"}"));
        Assert.Equal(3, aggregate.LaneWitnesses.Count);
    }

    [Fact]
    public void Jsonl_ParsesTheImageBase()
        => Assert.Equal(new LaneWitness("A", "libcoreclr", "GetCLRRuntimeHost", 0x105031240, 0x104c00000),
            JsonlTestResults.Parse("{\"lane_class\":\"A\",\"image\":\"libcoreclr\",\"symbol\":\"GetCLRRuntimeHost\"," +
                "\"address\":\"0x105031240\",\"image_base\":\"0x104c00000\"}").LaneWitnesses.Single());

    // ---- Host symbolication (stripped device images) ----

    const string NmOutput =
        "00000000003280e0 t _CallJittedMethodRet2I8\n" +
        "0000000000001000 T _GetCLRRuntimeHost\n" +
        "0000000000328178 t _CallJittedMethodRet2Double\n" +
        "garbage line\n" +
        "000000000049c090 d _A_ILBodyPrecodeImports_ImportBegin\n";

    [Fact]
    public void ParseNmSymbols_SortsByAddressAndDropsTheUnderscore()
    {
        var symbols = RuntimeLaneIdentity.ParseNmSymbols(NmOutput);
        Assert.Equal(["GetCLRRuntimeHost", "CallJittedMethodRet2I8", "CallJittedMethodRet2Double", "A_ILBodyPrecodeImports_ImportBegin"],
            symbols.Select(s => s.Name));
        Assert.Equal(0x3280e0UL, symbols[1].Offset);
    }

    [Theory]
    [InlineData(0xfffUL, null)]                              // before the first symbol
    [InlineData(0x1000UL, "GetCLRRuntimeHost")]
    [InlineData(0x3280e0UL, "CallJittedMethodRet2I8")]
    [InlineData(0x328177UL, "CallJittedMethodRet2I8")]       // inside a body
    [InlineData(0x328178UL, "CallJittedMethodRet2Double")]
    public void ResolveSymbol_NamesTheCoveringSymbol(ulong offset, string? expected)
        => Assert.Equal(expected, RuntimeLaneIdentity.ResolveSymbol(RuntimeLaneIdentity.ParseNmSymbols(NmOutput), offset));

    [Fact]
    public void StrippedDeviceInterpreterWitness_ResolvesOnTheHostAndProvesTheInterpreter()
    {
        const ulong Base = 0x104c00000;
        // On the phone dladdr sees only exported symbols: the stub reads as the nearest export.
        var stripped = new LaneWitness("A", "libcoreclr", "GetCLRRuntimeHost", Base + 0x3280e0 + 0x18 + 1, Base);
        Assert.False(RuntimeLaneIdentity.Verify(RuntimeExecutionLane.Interpreter, Evidence(CoreLibOnly, ["A"], stripped)).Accepted);

        var resolved = RuntimeLaneIdentity.Symbolicate([stripped],
            image => image == "libcoreclr" ? RuntimeLaneIdentity.ParseNmSymbols(NmOutput) : null);
        Assert.Equal("CallJittedMethodRet2I8", resolved.Single().Symbol);
        var evidence = Evidence(CoreLibOnly, ["A"], [.. resolved]);
        Assert.True(RuntimeLaneIdentity.Verify(RuntimeExecutionLane.Interpreter, evidence).Accepted);
        Assert.DoesNotContain(RuntimeLaneIdentity.Mislabels(RuntimeExecutionLane.Interpreter, evidence), v => v.Accepted);
    }

    [Fact]
    public void Symbolicate_ResolvesAStrippedReadyToRunCallerAgainstTheMap()
    {
        const ulong Base = 0x120000000;
        var stripped = new LaneWitness("A", $"{App}.r2r", "_mh_dylib_header", Base + 0x568e0 + 8, Base);
        var resolved = RuntimeLaneIdentity.Symbolicate([stripped], image => image == $"{App}.r2r"
            ? RuntimeLaneIdentity.ParseNmSymbols("00000000000568e0 s _RuntimeTestsApp_LaneProbe__Capture\n0000000000000000 T __mh_dylib_header\n")
            : null);
        Assert.True(RuntimeLaneIdentity.Verify(RuntimeExecutionLane.ReadyToRun, Evidence(WithApp, ["A"], [.. resolved])).Accepted);
    }

    [Fact]
    public void ReportRows_ShowTheWitnessTheVerdictJudged()
    {
        var results = JsonlTestResults.Parse(
            "{\"lane_class\":\"A\",\"image\":\"libcoreclr\",\"symbol\":\"GetCLRRuntimeHost\",\"address\":\"0x10\"}\n" +
            "{\"class\":\"A\",\"test\":\"T\",\"status\":\"pass\"}\n");
        var resolved = new[] { new LaneWitness("A", "libcoreclr", "CallJittedMethodRetI8", 0x10) };
        Assert.Equal("libcoreclr!CallJittedMethodRetI8", RuntimeLaneReport.Rows(["A"], results, [], null, resolved).Single().Witness);
        Assert.Equal("libcoreclr!GetCLRRuntimeHost", RuntimeLaneReport.Rows(["A"], results, [], null).Single().Witness);
    }

    [Fact]
    public void Symbolicate_KeepsTheAppsSymbolWithoutABaseOrATable_AndReadsEachTableOnce()
    {
        var lookups = 0;
        var noBase = new LaneWitness("A", "libcoreclr.dylib", Stub, 0x5000);
        var noTable = new LaneWitness("B", "libSystem.dylib", "x", 0x5000, 0x4000);
        var first = new LaneWitness("C", "libcoreclr", "GetCLRRuntimeHost", 0x4000 + 0x328180, 0x4000);
        var second = first with { ClassName = "D" };
        var resolved = RuntimeLaneIdentity.Symbolicate([noBase, noTable, first, second], image =>
        {
            lookups++;
            return image == "libcoreclr" ? RuntimeLaneIdentity.ParseNmSymbols(NmOutput) : null;
        });
        Assert.Equal(noBase, resolved[0]);
        Assert.Equal(noTable, resolved[1]);
        Assert.Equal("CallJittedMethodRet2Double", resolved[2].Symbol);
        Assert.Equal("CallJittedMethodRet2Double", resolved[3].Symbol);
        Assert.Equal(2, lookups); // libSystem.dylib and libcoreclr, each once
    }

    // ---- Lane report ----

    [Fact]
    public void Report_DerivesEachClassStatusAndCarriesTheWitness()
    {
        var results = JsonlTestResults.Parse(
            "{\"lane_class\":\"Crashy\",\"image\":\"libcoreclr.dylib\",\"symbol\":\"CallJittedMethodRetVoid\",\"address\":\"0x10\"}\n" +
            "{\"class\":\"Crashy\",\"test\":\"T1\",\"status\":\"pass\"}\n" +
            "{\"class\":\"Failing\",\"test\":\"T1\",\"status\":\"fail\"}\n" +
            "{\"class\":\"Failing\",\"test\":\"T2\",\"status\":\"pass\"}\n" +
            "{\"class\":\"Green\",\"test\":\"T1\",\"status\":\"pass\"}\n" +
            "{\"class\":\"Skipped\",\"test\":\"T1\",\"status\":\"skip\"}\n" +
            "{\"class\":\"\",\"test\":\"SetupThrows\",\"status\":\"fail\"}\n" +
            "{\"class\":\"\",\"test\":\"Test Suite\",\"status\":\"fail\"}\n");
        var interruptions = new[] { new RuntimeLaneInterruption(0, "Crash", "Crashy", "T2", "/tmp/a") };
        var rows = RuntimeLaneReport.Rows(["Crashy", "Failing", "Green", "Skipped", "NeverRan", "SetupThrows"], results, interruptions, null)
            .ToDictionary(r => r.ClassName);
        Assert.Equal("crash", rows["Crashy"].Status);
        Assert.Equal("T2", rows["Crashy"].InterruptedTest);
        Assert.Equal("libcoreclr.dylib!CallJittedMethodRetVoid", rows["Crashy"].Witness);
        Assert.Equal("fail", rows["Failing"].Status);
        Assert.Equal("pass", rows["Green"].Status);
        Assert.Equal("skip", rows["Skipped"].Status);
        Assert.Equal("not-run", rows["NeverRan"].Status);
        // A class whose factory throws records one failure named after the class; it is that class's.
        Assert.Equal(("fail", 1), (rows["SetupThrows"].Status, rows["SetupThrows"].Fail));
        Assert.Equal(6, rows.Count); // the class-less suite failure is no class's row

        var verdict = RuntimeLaneIdentity.Verify(RuntimeExecutionLane.Interpreter,
            new RuntimeLaneEvidence(App, ["Crashy"], results.LaneWitnesses, CoreLibOnly));
        var report = RuntimeLaneReport.Build("Simulator", "net11.0/coreclr/Debug", RuntimeExecutionLane.Interpreter, verdict, [],
            "/obj/map.csv", 0, ["Crashy", "Failing"], results, interruptions, "Crash", "/tmp/attempts");
        using var json = System.Text.Json.JsonDocument.Parse(report.ToJson());
        Assert.Equal("Interpreter", json.RootElement.GetProperty("claimed").GetString());
        Assert.Equal("crash", json.RootElement.GetProperty("classes")[0].GetProperty("status").GetString());
        Assert.Equal("Interpreter", json.RootElement.GetProperty("classes")[0].GetProperty("lane").GetString());
        Assert.Contains("Crashy\tcrash\t1\t0\t0\tT2\tInterpreter\t", report.ToText());
    }

    [Fact]
    public void ReportSlug_SeparatesPlatformFrameworkEngineAndConfiguration()
    {
        Assert.Equal("simulator-net11.0-coreclr-debug", RuntimeLaneReport.Slug("Simulator", "net11.0", "coreclr", "Debug"));
        Assert.Equal("device-coreclr-net11.0-coreclr-release", RuntimeLaneReport.Slug("Device/CoreCLR", "net11.0", "coreclr", "Release"));
        Assert.Equal("tvos-simulator-net11.0-coreclr-debug", RuntimeLaneReport.Slug("tvOS Simulator", "net11.0", "coreclr", "Debug"));
    }
}

/// <summary>The class-level resume fallback of <see cref="RuntimeTestAttempts.PlanRecovery"/>.</summary>
public sealed class RuntimeTestAttemptsClassRecoveryTests : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "runtime-class-recovery-tests-" + Guid.NewGuid().ToString("N"));
    readonly TestClassInventory inventory;
    readonly HashSet<string> eligible = new() { "FirstTests", "SetupTests", "LaterTests" };

    public RuntimeTestAttemptsClassRecoveryTests()
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "inventory.txt");
        File.WriteAllText(path, "FirstTests.TestA\nSetupTests.TestB\nLaterTests.TestC\n");
        inventory = TestClassInventory.Load(path);
    }

    RuntimeTestAttempts.RecoveryPlan Plan(string output, TestResult result = TestResult.Crash, JsonlTestResults? jsonl = null,
        HashSet<string>? excluded = null, int attempt = 0, int maxRetries = 5) =>
        RuntimeTestAttempts.PlanRecovery(new LaunchResult(result, output, null, null), jsonl, inventory, eligible,
            excluded ?? new HashSet<string>(), attempt, maxRetries, launcherAborts: 0);

    const string FirstRan =
        "[0.1s] [INFO] === FirstTests ===\n[0.1s] [TEST] --- FirstTests.TestA ---\n[0.2s] [PASS] FirstTests.TestA (1ms)\n";

    [Fact]
    public void CrashInClassSetup_NamesTheClassAndExcludesEveryStartedClass()
    {
        var plan = Plan(FirstRan + "[0.3s] [INFO] === SetupTests ===\nApp terminated due to signal 11.");
        Assert.True(plan.Resume);
        Assert.Equal(new RuntimeTestAttempts.TestIdentity("SetupTests", RuntimeTestAttempts.ClassSetup), plan.ActiveTest);
        Assert.Equal(new[] { "FirstTests", "SetupTests" }, plan.ClassesToExclude);
    }

    [Fact]
    public void ClassInitializerFailureAfterItsMarker_Resumes()
    {
        var plan = Plan("[INFO] === SetupTests ===\nUnhandled exception. System.TypeInitializationException", TestResult.Failure);
        Assert.True(plan.Resume);
        Assert.Equal("SetupTests", plan.ActiveTest!.ClassName);
    }

    [Fact]
    public void CrashBetweenTests_ExcludesStartedClassesWithoutNamingATest()
    {
        var plan = Plan(FirstRan + "App terminated due to signal 11.");
        Assert.True(plan.Resume);
        Assert.Null(plan.ActiveTest);
        Assert.Equal(new[] { "FirstTests" }, plan.ClassesToExclude);
    }

    [Fact]
    public void AClassRecordedComplete_IsNotNamedAsInterrupted()
    {
        var plan = Plan("[INFO] === SetupTests ===\nApp terminated due to signal 11.",
            jsonl: JsonlTestResults.Parse("{\"class_done\":\"SetupTests\"}"));
        Assert.Null(plan.ActiveTest);
        Assert.Equal(new[] { "SetupTests" }, plan.ClassesToExclude);
    }

    [Theory]
    [InlineData("App terminated due to signal 11.")]                                     // nothing started
    [InlineData("[INFO] === UnknownTests ===\nApp terminated due to signal 11.")]        // not in the inventory
    [InlineData("[INFO] === RUNTIME TESTS ===\nApp terminated due to signal 11.")]       // a banner, not a class
    public void NoInventoryClassStarted_StopsWithoutGuessing(string output)
    {
        var plan = Plan(output);
        Assert.False(plan.Resume);
        Assert.Empty(plan.ClassesToExclude);
    }

    [Fact]
    public void AnAlreadyExcludedClass_GivesNoProgressAndStops()
        => Assert.False(Plan("[INFO] === SetupTests ===\nApp terminated due to signal 11.",
            excluded: new HashSet<string> { "SetupTests" }).Resume);

    [Fact]
    public void MarkersAfterTheResultsFlush_AreIgnored()
        => Assert.False(Plan("RESULTS FLUSHED\n[INFO] === SetupTests ===\nApp terminated due to signal 11.").Resume);

    [Fact]
    public void LastClassCrash_StopsWithItsAttribution()
    {
        var plan = Plan("[INFO] === FirstTests ===\n[INFO] === SetupTests ===\n[INFO] === LaterTests ===\nApp terminated due to signal 11.");
        Assert.False(plan.Resume);
        Assert.Equal("LaterTests", plan.ActiveTest!.ClassName);
        Assert.Equal("No independent classes remain.", plan.Reason);
    }

    [Fact]
    public void ExhaustedBudget_Stops()
        => Assert.False(Plan("[INFO] === SetupTests ===\nApp terminated due to signal 11.", attempt: 5, maxRetries: 5).Resume);

    public void Dispose() => Directory.Delete(directory, recursive: true);
}
