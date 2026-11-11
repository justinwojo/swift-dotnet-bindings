// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using SwiftBindings.InterpIdentity;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// The lane-identity harness's engine: the console receipt protocol, the crossgen2 receipts, and
/// the classifier that turns them into a lane verdict. The harness itself runs only under
/// <c>binding-tests --interp-identity</c>; these tests pin the decisions it makes from receipts.
/// </summary>
public class InterpIdentityHarnessTests
{
    const string App = "InterpIdentityApp";
    const string Binding = "InterpIdentityFixture.Swift.iOS";

    // MARK: - Console receipts

    static string Console(string fixture, string? verdict, bool ended = true, params string[] frames)
    {
        var lines = new List<string>
        {
            "2026-11-10 12:00:00.000 InterpIdentityApp[123:456] IIF START fixture=" + fixture + " framework=.NET_11.0 arch=Arm64 configuration=Debug",
            "IIF BEGIN " + fixture,
            "IIF OBSERVE " + fixture + " receiver=0x1000 word0=0x2000",
        };
        // The Swift fixture writes its capture during the call, before the app reports.
        lines.AddRange(Capture(1, frames));
        if (verdict != null) lines.Add($"IIF RESULT {fixture} {verdict}");
        if (ended) lines.Add("IIF END " + fixture);
        lines.Add("RESULTS FLUSHED");
        return string.Join("\n", lines);
    }

    [Fact]
    public void Parse_ResultLine_GivesPassWithObservationsFramesAndStart()
    {
        var r = ConsoleReceiptParser.Parse("primitive", Console("primitive", "PASS", true,
            "InterpIdentityFixture $s21InterpIdentityFixture10iifCaptureyyF", "libcoreclr.dylib CallJittedMethodRetVoid"));
        Assert.Equal(FixtureOutcome.Pass, r.Outcome);
        Assert.Equal("", r.Detail);
        Assert.Single(r.Observations);
        Assert.Equal(2, r.Frames.Count);
        Assert.Equal("libcoreclr.dylib", r.Frames[1].Image);
        Assert.Equal("CallJittedMethodRetVoid", r.Frames[1].Symbol);
        Assert.Equal(0x1001UL, r.Frames[1].Address);
        Assert.StartsWith("fixture=primitive", r.Start);
    }

    [Fact]
    public void Parse_FailLine_KeepsTheAppsReason()
    {
        var r = ConsoleReceiptParser.Parse("self-receiver", Console("self-receiver", "FAIL call 1: Swift saw self 0x0; call 2: point.x is 0"));
        Assert.Equal(FixtureOutcome.Fail, r.Outcome);
        Assert.Equal("call 1: Swift saw self 0x0; call 2: point.x is 0", r.Detail);
    }

    [Fact]
    public void Parse_BeginWithoutResult_IsACrash()
    {
        var r = ConsoleReceiptParser.Parse("reverse-error-mid", Console("reverse-error-mid", null, ended: false));
        Assert.Equal(FixtureOutcome.Crash, r.Outcome);
    }

    static IEnumerable<string> Capture(int serial, params string[] frames) =>
        frames.Select((f, i) => $"IIF CAPTURE {serial} {i} 0x{0x1000 + i:x} {f}");

    [Fact]
    public void Parse_FailWithoutEnd_IsTheReportedVerdictAndSaysTheProcessDied()
    {
        var r = ConsoleReceiptParser.Parse("self-error", Console("self-error", "FAIL lost error", ended: false));
        Assert.Equal(FixtureOutcome.Fail, r.Outcome);
        Assert.Contains("process died after reporting", r.Detail);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Parse_PassWithoutEnd_IsIndeterminate(bool timedOut)
    {
        var r = ConsoleReceiptParser.Parse("primitive", Console("primitive", "PASS", ended: false), timedOut);
        Assert.Equal(FixtureOutcome.Indeterminate, r.Outcome);
    }

    [Fact]
    public void Parse_BeginThenTimeout_IsIndeterminateNotACrash()
    {
        // A hung control must not count as reproducing its defect.
        var r = ConsoleReceiptParser.Parse("reverse-error-mid", Console("reverse-error-mid", null, ended: false), timedOut: true);
        Assert.Equal(FixtureOutcome.Indeterminate, r.Outcome);
        Assert.False(InterpIdentityMatrix.Satisfies(FixtureExpectation.Reproduce, r.Outcome));
    }

    [Fact]
    public void Parse_ACrashAfterTheCaptureStillHasItsCaller()
    {
        var r = ConsoleReceiptParser.Parse("reverse-error-mid", Console("reverse-error-mid", null, false,
            "InterpIdentityFixture capture", "libcoreclr.dylib CallJittedMethodRetVoid"));
        Assert.Equal(FixtureOutcome.Crash, r.Outcome);
        Assert.Equal("CallJittedMethodRetVoid", ConsoleReceiptParser.Caller(r)!.Symbol);
    }

    [Fact]
    public void Parse_KeepsTheLastCaptureMadeAfterBegin()
    {
        var console = string.Join("\n", new[] { "IIF START fixture=self-receiver x" }
            .Concat(Capture(1, "InterpIdentityFixture early", "Stale.dylib before"))
            .Append("IIF BEGIN self-receiver")
            .Concat(Capture(2, "InterpIdentityFixture capture", "libcoreclr.dylib CallJittedMethodRetI8"))
            .Concat(Capture(3, "InterpIdentityFixture capture", "libcoreclr.dylib CallJittedMethodRetVoid"))
            .Append("IIF RESULT self-receiver PASS").Append("IIF END self-receiver")
            .Concat(Capture(4, "InterpIdentityFixture late", "After.dylib end")));
        var r = ConsoleReceiptParser.Parse("self-receiver", console);
        Assert.Equal(2, r.Frames.Count);
        Assert.Equal("CallJittedMethodRetVoid", ConsoleReceiptParser.Caller(r)!.Symbol);
    }

    [Fact]
    public void Parse_MalformedCapture_Throws()
    {
        Assert.Throws<FormatException>(() => ConsoleReceiptParser.Parse("primitive",
            "IIF BEGIN primitive\nIIF CAPTURE 1 0 1000 image symbol\nIIF RESULT primitive PASS"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("dyld: Library not loaded")]
    public void Parse_NoBegin_IsMissing(string console)
    {
        var r = ConsoleReceiptParser.Parse("primitive", console);
        Assert.Equal(FixtureOutcome.Missing, r.Outcome);
    }

    [Fact]
    public void Parse_IgnoresOtherFixturesLines()
    {
        var r = ConsoleReceiptParser.Parse("string", Console("primitive", "PASS"));
        Assert.Equal(FixtureOutcome.Missing, r.Outcome);
        Assert.Equal("fixture never began", r.Detail);
    }

    [Fact]
    public void Parse_UnknownVerdict_Throws()
    {
        Assert.Throws<FormatException>(() => ConsoleReceiptParser.Parse("primitive", Console("primitive", "MAYBE")));
    }

    [Fact]
    public void Caller_IsTheFirstFrameOutsideTheFixtureImages()
    {
        var r = ConsoleReceiptParser.Parse("primitive", Console("primitive", "PASS", true,
            "InterpIdentityFixture capture", "InterpIdentityFixtureSwiftBindings thunk",
            "InterpIdentityApp.r2r InterpIdentityApp_Program__Primitive", "libcoreclr.dylib CallJittedMethodRetVoid"));
        var caller = ConsoleReceiptParser.Caller(r);
        Assert.NotNull(caller);
        Assert.Equal("InterpIdentityApp.r2r", caller!.Image);
        Assert.Equal("InterpIdentityApp_Program__Primitive", caller.Symbol);
    }

    [Fact]
    public void Caller_RequiresAFixtureFrameFirst()
    {
        // Frames that never entered the fixture cannot say who called it.
        var r = ConsoleReceiptParser.Parse("primitive", Console("primitive", "PASS", true,
            "libcoreclr.dylib CallJittedMethodRetVoid", "InterpIdentityFixture capture"));
        Assert.Null(ConsoleReceiptParser.Caller(r));
    }

    [Fact]
    public void Caller_UnresolvedSymbolIsNull()
    {
        var r = ConsoleReceiptParser.Parse("primitive", Console("primitive", "PASS", true,
            "InterpIdentityFixture capture", "InterpIdentityApp.r2r.dylib -"));
        Assert.Null(ConsoleReceiptParser.Caller(r)!.Symbol);
    }

    // MARK: - crossgen2 receipts

    const string MapCsv = """
        Rva,Length,Relocs,Section,Symbol,Node Type
        0x00000000,12,0,__text__TEXT,_DelayLoadHelper->EagerImports(ImportSection:EagerImports,Kind:Eager),ImportThunk
        0x00000090,160,0,__managedcode__TEXT,_S_P_CoreLib_Interop__CheckIo_0,MethodWithGCInfo
        0x00000130,96,0,__managedcode__TEXT,_InterpIdentityFixture_Swift_iOS_InterpIdentityFixture_Functions__IifIdentityProbe,MethodWithGCInfo
        0x00000190,96,0,__managedcode__TEXT,"_InterpIdentityApp_Program__Main,quoted",MethodWithGCInfo
        0x00000200,8,0,__data__DATA,_InterpIdentityFixture_Swift_iOS_Data,MethodWithGCInfo
        0x00000300,8,0,__managedcode__TEXT,_InterpIdentityFixture_Swift_iOS_Fixup,DelayLoadHelperImport
        """;

    [Fact]
    public void Map_CountsOnlyManagedCodeMethodBodies_PerOwningAssembly()
    {
        var inv = ReadyToRunInventory.ParseMapCsv(MapCsv);
        Assert.Equal(3, inv.Total);
        Assert.Equal(1, inv.BodiesFor(Binding));
        Assert.Equal(1, inv.BodiesFor("S.P.CoreLib"));
        Assert.Equal(1, inv.BodiesFor(App));
        Assert.True(inv.Contains("_InterpIdentityApp_Program__Main,quoted"));
        Assert.False(inv.Contains("InterpIdentityFixture_Swift_iOS_Data"));
    }

    [Fact]
    public void Map_WithoutTheExpectedColumns_Throws()
    {
        Assert.Throws<FormatException>(() => ReadyToRunInventory.ParseMapCsv("Rva,Length\n0x0,1"));
    }

    static string Crossgen2Log(params string[] inputs) =>
        "  Task \"Crossgen2Compilation\"\n" +
        "    /usr/local/share/dotnet/packs/microsoft.netcore.app.crossgen2/tools/crossgen2 --targetos:ios\n" +
        string.Concat(inputs.Select(i => "      " + i + "\n")) +
        "  Done executing task \"Crossgen2Compilation\".\n";

    [Fact]
    public void Crossgen2_SeparatesRootedInputsReferencesAndUnrootedInputs()
    {
        var log = Crossgen2Log("--targetarch:arm64", "--composite", "-O",
            "\"/obj/R2R/System.Private.CoreLib.dll\"", $"-u:/obj/{Binding}.dll", "-r:/packs/ref/System.Runtime.dll", "--map", "--mapcsv");
        var inv = Assert.Single(Crossgen2Invocation.ParseAll(log));
        Assert.Equal(["System.Private.CoreLib.dll"], inv.Rooted);
        Assert.Equal([$"{Binding}.dll"], inv.Unrooted);
        Assert.Equal(["System.Runtime.dll"], inv.References);
        Assert.Contains("--composite", inv.Options);
        Assert.Contains("--targetos:ios", inv.Options);
        Assert.False(inv.IsRooted(Binding));
        Assert.True(inv.IsRooted("System.Private.CoreLib"));
    }

    [Fact]
    public void Crossgen2_ReadsEveryInvocation()
    {
        var log = Crossgen2Log("/a/A.dll") + "unrelated line\n" + Crossgen2Log("/b/B.dll");
        var all = Crossgen2Invocation.ParseAll(log);
        Assert.Equal(2, all.Count);
        Assert.Equal(["B.dll"], all[1].Rooted);
    }

    // MARK: - Lane classifier

    static ReadyToRunInventory Inventory(params string[] symbols) => new(new HashSet<string>(symbols));

    static Crossgen2Invocation Crossgen(bool bindingRooted) => bindingRooted
        ? new([$"{Binding}.dll", $"{App}.dll", "System.Private.CoreLib.dll"], [], [], [])
        : new(["System.Private.CoreLib.dll"], [], [$"{Binding}.dll", $"{App}.dll"], []);

    static readonly string BindingBody = $"{ReadyToRunInventory.SymbolPrefix(Binding)}Functions__IifIdentityProbe";
    static readonly string AppBody = $"{ReadyToRunInventory.SymbolPrefix(App)}Program__Primitive";

    static LaneEvidence InterpreterEvidence() => new(App, Binding,
        [new("primitive", "libcoreclr.dylib", "CallJittedMethodRetI8"), new("string", "libcoreclr.dylib", "CallJittedMethodRetVoid")],
        Inventory("S_P_CoreLib_Interop__CheckIo_0"), Crossgen(bindingRooted: false));

    static LaneEvidence ReadyToRunEvidence(string image = App + ".r2r") => new(App, Binding,
        [new("primitive", image, AppBody), new("string", image, BindingBody)],
        Inventory("S_P_CoreLib_Interop__CheckIo_0", BindingBody, AppBody), Crossgen(bindingRooted: true));

    [Fact]
    public void Interpreter_EvidenceProvesTheInterpreterLane()
    {
        var v = LaneClassifier.Classify(ExecutionLane.Interpreter, InterpreterEvidence());
        Assert.True(v.Accepted, string.Join("; ", v.Problems));
        Assert.Equal(ExecutionLane.Interpreter, v.Derived);
    }

    [Theory]
    [InlineData(App + ".r2r")]
    [InlineData(App + ".r2r.dylib")]
    public void ReadyToRun_EvidenceProvesTheReadyToRunLane(string image)
    {
        var v = LaneClassifier.Classify(ExecutionLane.ReadyToRun, ReadyToRunEvidence(image));
        Assert.True(v.Accepted, string.Join("; ", v.Problems));
        Assert.Equal(ExecutionLane.ReadyToRun, v.Derived);
    }

    [Fact]
    public void SeededMislabel_InterpreterReceiptsClaimedAsReadyToRun_AreRejected()
    {
        var v = LaneClassifier.Classify(ExecutionLane.ReadyToRun, InterpreterEvidence());
        Assert.False(v.Accepted);
        Assert.Equal(ExecutionLane.Interpreter, v.Derived);
        Assert.Contains(v.Problems, p => p.Contains("interpreter's call stub"));
        Assert.Contains(v.Problems, p => p.Contains("no " + Binding + " bodies"));
        Assert.Contains(v.Problems, p => p.Contains("did not compile"));
    }

    [Fact]
    public void SeededMislabel_ReadyToRunReceiptsClaimedAsInterpreter_AreRejected()
    {
        var v = LaneClassifier.Classify(ExecutionLane.Interpreter, ReadyToRunEvidence());
        Assert.False(v.Accepted);
        Assert.Equal(ExecutionLane.ReadyToRun, v.Derived);
        Assert.Contains(v.Problems, p => p.Contains("precompiled code"));
        Assert.Contains(v.Problems, p => p.Contains("rooted input"));
    }

    [Fact]
    public void Interpreter_OneCallerFromPrecompiledCode_IsRejected()
    {
        // A partial R2R cell: crossgen2 precompiled one caller even though the claim is interpreter.
        var e = InterpreterEvidence() with { Callers = [new("primitive", "libcoreclr.dylib", "CallJittedMethodRetI8"), new("string", App + ".r2r", AppBody)] };
        var v = LaneClassifier.Classify(ExecutionLane.Interpreter, e);
        Assert.False(v.Accepted);
        Assert.Null(v.Derived);
    }

    [Fact]
    public void ReadyToRun_CallerSymbolAbsentFromTheMap_IsRejected()
    {
        var e = ReadyToRunEvidence() with { Callers = [new("primitive", App + ".r2r", "SomethingElse")] };
        var v = LaneClassifier.Classify(ExecutionLane.ReadyToRun, e);
        Assert.False(v.Accepted);
        Assert.Contains(v.Problems, p => p.Contains("not a precompiled body"));
    }

    [Fact]
    public void ReadyToRun_BindingBodiesMissingFromTheComposite_IsRejected()
    {
        var e = ReadyToRunEvidence() with { Inventory = Inventory(AppBody) };
        var v = LaneClassifier.Classify(ExecutionLane.ReadyToRun, e);
        Assert.False(v.Accepted);
    }

    [Fact]
    public void Interpreter_BindingBodiesInTheComposite_IsRejected()
    {
        var e = InterpreterEvidence() with { Inventory = Inventory(BindingBody) };
        var v = LaneClassifier.Classify(ExecutionLane.Interpreter, e);
        Assert.False(v.Accepted);
        Assert.Contains(v.Problems, p => p.Contains("precompiled " + Binding + " bodies"));
    }

    [Theory]
    [InlineData("libcoreclr.dylib", "ThePreStub")]
    [InlineData("libSystem.B.dylib", "start")]
    [InlineData("libcoreclr.dylib", null)]
    public void UnrecognisedCaller_IsRejectedUnderEitherClaim(string image, string? symbol)
    {
        var e = InterpreterEvidence() with { Callers = [new("primitive", image, symbol)] };
        Assert.False(LaneClassifier.Classify(ExecutionLane.Interpreter, e).Accepted);
        Assert.False(LaneClassifier.Classify(ExecutionLane.ReadyToRun, e).Accepted);
    }

    // A ReadyToRun cell whose indirect-result call crossgen2 left to the runtime.
    static readonly ReadyToRunGap Gap = new("InterpIdentityFixture_IifFailable__TryCreate", "unmanaged calli");
    static readonly string GapSymbol = ReadyToRunInventory.SymbolPrefix(Binding) + Gap.Method;
    static readonly CallerReceipt InterpretedGapCaller = new("indirect-result", "libcoreclr.dylib", "CallJittedMethodRetVoid");

    static LaneEvidence ReadyToRunEvidenceWithGap() => ReadyToRunEvidence() with
    {
        Callers = [.. ReadyToRunEvidence().Callers, InterpretedGapCaller],
        Gaps = new Dictionary<string, ReadyToRunGap> { ["indirect-result"] = Gap },
    };

    [Fact]
    public void ReadyToRun_ADeclaredGapThatIsInterpreted_StillProvesTheLane()
    {
        var v = LaneClassifier.Classify(ExecutionLane.ReadyToRun, ReadyToRunEvidenceWithGap());
        Assert.True(v.Accepted, string.Join("; ", v.Problems));
        Assert.Equal(ExecutionLane.ReadyToRun, v.Derived);
    }

    [Fact]
    public void ReadyToRun_AnInterpretedCallerWithoutADeclaredGap_IsRejected()
    {
        var e = ReadyToRunEvidenceWithGap() with { Gaps = null };
        var v = LaneClassifier.Classify(ExecutionLane.ReadyToRun, e);
        Assert.False(v.Accepted);
        Assert.Null(v.Derived);
        Assert.Contains(v.Problems, p => p.StartsWith("indirect-result: claimed ReadyToRun, but the caller is the interpreter", StringComparison.Ordinal));
    }

    [Fact]
    public void ReadyToRun_AGapWhoseCallerIsNowPrecompiled_IsStale()
    {
        var e = ReadyToRunEvidenceWithGap() with
        {
            Callers = [.. ReadyToRunEvidence().Callers, new("indirect-result", App + ".r2r", BindingBody)],
        };
        var v = LaneClassifier.Classify(ExecutionLane.ReadyToRun, e);
        Assert.False(v.Accepted);
        Assert.Contains(v.Problems, p => p.Contains("gap no longer holds") && p.Contains("precompiled code"));
    }

    [Fact]
    public void ReadyToRun_AGapWhoseMethodIsNowInTheMap_IsStale()
    {
        var e = ReadyToRunEvidenceWithGap();
        e = e with { Inventory = new ReadyToRunInventory(new HashSet<string>(e.Inventory.Symbols) { GapSymbol }) };
        var v = LaneClassifier.Classify(ExecutionLane.ReadyToRun, e);
        Assert.False(v.Accepted);
        Assert.Contains(v.Problems, p => p.Contains(Gap.Method + " is precompiled"));
    }

    [Fact]
    public void ReadyToRun_ADeclaredGapWithNoCaller_IsNotExcusedVacuously()
    {
        // The gap fixture ran (it crashed, say) but left no frame: nothing shows it was interpreted.
        var e = ReadyToRunEvidenceWithGap() with { Callers = ReadyToRunEvidence().Callers, Unattributed = ["indirect-result"] };
        var v = LaneClassifier.Classify(ExecutionLane.ReadyToRun, e);
        Assert.False(v.Accepted);
        Assert.Contains(v.Problems, p => p.Contains("no caller frame shows the interpreter"));
        Assert.Contains(v.Problems, p => p.Contains("recorded no caller frame, so its lane is unknown"));
    }

    [Theory]
    [InlineData(ExecutionLane.Interpreter)]
    [InlineData(ExecutionLane.ReadyToRun)]
    public void AFixtureThatRanWithoutACaller_DisprovesEitherClaim(ExecutionLane claim)
    {
        var e = (claim == ExecutionLane.Interpreter ? InterpreterEvidence() : ReadyToRunEvidence()) with { Unattributed = ["reverse-error-mid"] };
        var v = LaneClassifier.Classify(claim, e);
        Assert.False(v.Accepted);
        Assert.Null(v.Derived);
    }

    [Fact]
    public void CellClassifier_ACrashWithoutCaptureLeavesTheLaneUnproven()
    {
        var receipts = new List<FixtureReceipt>
        {
            ConsoleReceiptParser.Parse("primitive", Console("primitive", "PASS", true,
                "InterpIdentityFixture capture", "libcoreclr.dylib CallJittedMethodRetI8")),
            ConsoleReceiptParser.Parse("reverse-error-mid", Console("reverse-error-mid", null, ended: false)),
        };
        var v = CellClassifier.Classify(InterpIdentityMatrix.M1, App, Binding, receipts,
            Inventory("S_P_CoreLib_Interop__CheckIo_0"), Crossgen(bindingRooted: false));
        Assert.Equal(["reverse-error-mid"], v.Evidence.Unattributed);
        Assert.False(v.Lane.Accepted);
    }

    [Fact]
    public void ReadyToRun_OnlyGapCallers_ProveNothing()
    {
        var e = ReadyToRunEvidenceWithGap() with { Callers = [InterpretedGapCaller] };
        var v = LaneClassifier.Classify(ExecutionLane.ReadyToRun, e);
        Assert.False(v.Accepted);
        Assert.Contains(v.Problems, p => p.Contains("every caller is a declared crossgen2 gap"));
    }

    [Fact]
    public void SeededMislabel_InterpreterReceiptsClaimedAsReadyToRun_AreRejectedEvenWithAGap()
    {
        // The gap only excuses its own fixture; every other interpreted caller still disproves the claim.
        var e = InterpreterEvidence() with
        {
            Callers = [.. InterpreterEvidence().Callers, InterpretedGapCaller],
            Gaps = new Dictionary<string, ReadyToRunGap> { ["indirect-result"] = Gap },
        };
        var v = LaneClassifier.Classify(ExecutionLane.ReadyToRun, e);
        Assert.False(v.Accepted);
        Assert.Equal(ExecutionLane.Interpreter, v.Derived);
    }

    [Fact]
    public void SeededMislabel_ReadyToRunReceiptsWithAGapClaimedAsInterpreter_AreRejected()
    {
        var v = LaneClassifier.Classify(ExecutionLane.Interpreter, ReadyToRunEvidenceWithGap());
        Assert.False(v.Accepted);
        Assert.Equal(ExecutionLane.ReadyToRun, v.Derived);
    }

    [Fact]
    public void NoCallers_ProvesNothing()
    {
        var e = InterpreterEvidence() with { Callers = [] };
        var v = LaneClassifier.Classify(ExecutionLane.Interpreter, e);
        Assert.False(v.Accepted);
        Assert.Null(v.Derived);
    }

    // MARK: - Matrix and expectations

    [Fact]
    public void ReadyToRunCells_PassEveryPrecompiledFixture_AndGapsCarryTheInterpreterExpectation()
    {
        foreach (var cell in InterpIdentityMatrix.Cells.Where(c => c.ClaimedLane == ExecutionLane.ReadyToRun))
        {
            var interp = InterpIdentityMatrix.Cells.Single(c => c.ClaimedLane == ExecutionLane.Interpreter && c.RuntimeIdentifier == cell.RuntimeIdentifier);
            foreach (var f in InterpIdentityMatrix.Fixtures)
                Assert.Equal(f.ReadyToRunGap == null ? FixtureExpectation.Pass : InterpIdentityMatrix.ExpectationFor(interp, f.Name),
                    InterpIdentityMatrix.ExpectationFor(cell, f.Name));
        }
        // The measured gap: on x86_64 the Release app makes the indirect-result call from the
        // interpreter, so it reproduces the same defect as the Debug app.
        Assert.Equal(FixtureExpectation.Reproduce, InterpIdentityMatrix.ExpectationFor(InterpIdentityMatrix.M4, "indirect-result"));
        Assert.Equal(FixtureExpectation.Pass, InterpIdentityMatrix.ExpectationFor(InterpIdentityMatrix.M2, "indirect-result"));
    }

    [Fact]
    public void CellClassifier_CarriesTheMatrixGapsIntoTheEvidence()
    {
        var v = CellClassifier.Classify(InterpIdentityMatrix.M2, App, Binding, [],
            Inventory(BindingBody), Crossgen(bindingRooted: true));
        Assert.NotNull(v.Evidence.Gaps);
        Assert.Equal(InterpIdentityMatrix.Fixtures.Where(f => f.ReadyToRunGap != null).Select(f => f.Name), v.Evidence.Gaps!.Keys);
    }

    [Fact]
    public void InterpreterCells_ReproduceEveryNegativeControlTheyCarry()
    {
        // Every fixture that exercises a direct transition is a control on at least one interpreter
        // cell; a control that every cell expects to pass would never catch anything.
        var interp = InterpIdentityMatrix.Cells.Where(c => c.ClaimedLane == ExecutionLane.Interpreter).ToList();
        foreach (var f in InterpIdentityMatrix.Fixtures.Where(f => f.DirectMembers.Count > 0))
            Assert.Contains(interp, c => InterpIdentityMatrix.ExpectationFor(c, f.Name) != FixtureExpectation.Pass);
        foreach (var f in InterpIdentityMatrix.Fixtures.Where(f => f.DirectMembers.Count == 0))
            Assert.All(interp, c => Assert.Equal(FixtureExpectation.Pass, InterpIdentityMatrix.ExpectationFor(c, f.Name)));
    }

    [Theory]
    [InlineData(FixtureExpectation.Pass, FixtureOutcome.Pass, true)]
    [InlineData(FixtureExpectation.Pass, FixtureOutcome.Fail, false)]
    [InlineData(FixtureExpectation.Pass, FixtureOutcome.Crash, false)]
    [InlineData(FixtureExpectation.Reproduce, FixtureOutcome.Fail, true)]
    [InlineData(FixtureExpectation.Reproduce, FixtureOutcome.Crash, true)]
    [InlineData(FixtureExpectation.Reproduce, FixtureOutcome.Pass, false)]
    [InlineData(FixtureExpectation.Reproduce, FixtureOutcome.Missing, false)]
    [InlineData(FixtureExpectation.Pass, FixtureOutcome.Indeterminate, false)]
    [InlineData(FixtureExpectation.Reproduce, FixtureOutcome.Indeterminate, false)]
    public void Satisfies_MatchesTheExpectationTable(FixtureExpectation expectation, FixtureOutcome outcome, bool satisfied)
    {
        Assert.Equal(satisfied, InterpIdentityMatrix.Satisfies(expectation, outcome));
    }

    [Theory]
    [InlineData(null, 4)]
    [InlineData(" ", 4)]
    [InlineData("M1, m1 ,M3", 2)]
    public void SelectCells_BlankIsEveryCell_AndNamesAreDeduplicated(string? spec, int count)
    {
        Assert.Equal(count, InterpIdentityMatrix.SelectCells(spec).Count);
    }

    [Theory]
    [InlineData(",")]
    [InlineData(" , ")]
    [InlineData("M1,M9")]
    public void SelectCells_ASpecThatNamesNoKnownCell_Throws(string spec)
    {
        Assert.Throws<ArgumentException>(() => InterpIdentityMatrix.SelectCells(spec));
    }

    [Fact]
    public void UnknownCellOrFixture_Throws()
    {
        Assert.Throws<ArgumentException>(() => InterpIdentityMatrix.Cell("M9"));
        Assert.Throws<ArgumentException>(() => InterpIdentityMatrix.ExpectationFor(InterpIdentityMatrix.M1, "nope"));
        Assert.Same(InterpIdentityMatrix.M3, InterpIdentityMatrix.Cell("m3"));
    }

    [Fact]
    public void CellClassifier_AMissingFixtureFailsTheCell()
    {
        var receipts = new List<FixtureReceipt>
        {
            ConsoleReceiptParser.Parse("primitive", Console("primitive", "PASS", true,
                "InterpIdentityFixture capture", "libcoreclr.dylib CallJittedMethodRetI8")),
        };
        var v = CellClassifier.Classify(InterpIdentityMatrix.M1, App, Binding, receipts,
            Inventory("S_P_CoreLib_Interop__CheckIo_0"), Crossgen(bindingRooted: false));
        Assert.True(v.Lane.Accepted, string.Join("; ", v.Lane.Problems));
        Assert.False(v.Passed);
        Assert.Contains(v.Fixtures, f => f.Fixture.Name == "string" && f.Receipt.Outcome == FixtureOutcome.Missing && !f.Satisfied);
    }

    // MARK: - Direct-lane coverage

    static IEnumerable<(string Rule, string Member)> AllDirectMembers() =>
        InterpIdentityMatrix.Fixtures.SelectMany(f => f.DirectMembers.Select(m => (m.Rule, $"InterpIdentityFixture.{m.Member}(...)")));

    [Fact]
    public void DirectLaneCoverage_EveryControlStillDirect_IsComplete()
    {
        Assert.Empty(DirectLaneCoverage.Missing(AllDirectMembers()));
    }

    [Fact]
    public void DirectLaneCoverage_AControlReroutedOffTheDirectLane_IsReported()
    {
        var violations = AllDirectMembers().Where(v => !v.Member.Contains("PInvoke_stamp_", StringComparison.Ordinal)).ToList();
        var missing = DirectLaneCoverage.Missing(violations);
        Assert.Equal(["self-receiver: no R1 transition on a member matching 'IifSlot::PInvoke_stamp_'"], missing);
    }

    [Fact]
    public void DirectLaneCoverage_TheRuleMustMatchToo()
    {
        var violations = AllDirectMembers().Select(v => v.Member.Contains("iifCallThrowing", StringComparison.Ordinal) ? ("R1", v.Member) : v);
        Assert.Contains(DirectLaneCoverage.Missing(violations), m => m.StartsWith("reverse-error-mid", StringComparison.Ordinal));
    }
}
