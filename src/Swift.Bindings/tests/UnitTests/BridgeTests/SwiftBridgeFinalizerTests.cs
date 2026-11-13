// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using Xunit;
using static BindingsGeneration.Tests.SwiftCallPlanTests;

namespace BindingsGeneration.Tests;

/// <summary>
/// The wrapper compile's bridge routing (<see cref="SwiftBridgeFinalizer"/>): from the receipt the
/// compile just collected, a module's C# is routed exactly as generation would route it under that
/// receipt, scoped to the receipt's own slices; the wrapper is relinked whenever its bridges differ;
/// a failed relink leaves generation's C# and the wrapper the receipt describes; the routing is undone
/// when no longer asked for; C# generation routed itself is never touched; and C# the routing journal
/// does not describe fails the build rather than ship.
/// </summary>
public sealed class SwiftBridgeFinalizerTests : IDisposable
{
    private const string Library = "MSwiftBindings";
    private const string SelfTarget = "$s1M1CC1fyS2iF";
    private const string ThrowingTarget = "$s1M1tyS2iKF";
    private static readonly string Compiler = SwiftLoweringEvidenceTests.QualifiedCompilers[0];

    /// <summary>The slices a wrapper with no x86_64 simulator slice ships.</summary>
    private static readonly ImmutableArray<string> Shipped = ["arm64-apple-ios15.0", "arm64-apple-ios15.0-simulator"];

    private const string MainFile = "M.cs";
    private const string SplitFile = "M.Types.D.cs";

    private const string MainSource = """
        namespace M
        {
            public static unsafe partial class C
            {
                public static long Call(long a) => PInvoke_f(a, default);

                [global::System.Runtime.InteropServices.UnmanagedCallConv(CallConvs = new global::System.Type[] { typeof(global::System.Runtime.CompilerServices.CallConvSwift) })]
                [global::System.Runtime.InteropServices.LibraryImport("MSwiftBindings", EntryPoint = "$s1M1CC1fyS2iF", SetLastError = false)]
                private static partial long PInvoke_f(long a, global::System.Runtime.InteropServices.Swift.SwiftSelf self);
            }
        }
        """;

    private const string SplitSource = """
        namespace M
        {
            public static unsafe partial class D
            {
                [global::System.Runtime.InteropServices.UnmanagedCallConv(CallConvs = new global::System.Type[] { typeof(global::System.Runtime.CompilerServices.CallConvSwift) })]
                [global::System.Runtime.InteropServices.LibraryImport("MSwiftBindings", EntryPoint = "$s1M1tyS2iKF", SetLastError = false)]
                private static partial long PInvoke_t(long a, out global::System.Runtime.InteropServices.Swift.SwiftError error);
            }
        }
        """;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"sbfin_{Guid.NewGuid():N}");
    private readonly CapturingLoggerFactory _log = new();
    private readonly SwiftCallPlanSet _generated;
    private readonly string _selfCallee;
    private readonly string _throwingCallee;
    private readonly SwiftTransitionLedger _ledger;

    private readonly List<string> _events = new();
    private Func<SwiftLoweringReceipt, string?> _relink = _ => null;

    public SwiftBridgeFinalizerTests()
    {
        Directory.CreateDirectory(_dir);
        var self = Planner().PlanForward(ClassMethod("Swift.Int", "Swift.Int"), Signature("long", "long", "SwiftSelf"), SelfTarget);
        var throwing = Planner().PlanForward(Throwing(Free("Swift.Int", "Swift.Int")), Signature("long", "long", "SwiftError*"), ThrowingTarget);
        _selfCallee = self.Callee!;
        _throwingCallee = throwing.Callee!;
        _generated = new SwiftCallPlanSet
        {
            Module = "M",
            Coverage = [Entry(self, "M.C::PInvoke_f", SelfTarget), Entry(throwing, "M.D::PInvoke_t", ThrowingTarget)],
            Plans = [Planned(self), Planned(throwing)],
        };
        _ledger = SwiftTransitionLedger.Build("M", new[] { MainSource, SplitSource }, Array.Empty<SwiftTransitionRecord>());
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private static SwiftPlanCoverageEntry Entry(SwiftPlanOutcome outcome, string member, string target)
    {
        var plan = Planned(outcome);
        return new SwiftPlanCoverageEntry(SwiftTransitionKind.ForwardPInvoke, member, target, SwiftPlanStatus.Planned)
        {
            Target = target,
            Symbol = plan.Symbol,
            Hash = plan.Hash,
            Callee = outcome.Callee,
            Evidence = SwiftCallPlanSet.NoEvidenceReason,
        };
    }

    private string PathOf(string file) => Path.Combine(_dir, file);
    private string Read(string file) => File.ReadAllText(PathOf(file));
    private string JournalPath => PathOf(SwiftBridgeRoutingJournal.FileName("M"));
    private string AssemblyPath(SwiftArchitecture architecture) => PathOf(SwiftBridgeRouting.AssemblyFileName("M", architecture));

    /// <summary>What generation writes without evidence: its C#, an unjudged plan sidecar and the ledger.</summary>
    private void WriteGeneration()
    {
        File.WriteAllText(PathOf(MainFile), MainSource);
        File.WriteAllText(PathOf(SplitFile), SplitSource);
        File.WriteAllText(PathOf(SwiftCallPlanSet.FileName("M")), _generated.ToJson());
        File.WriteAllText(PathOf(SwiftTransitionLedger.FileName("M")), _ledger.ToJson());
    }

    /// <summary>A receipt of M under the build's compiler on <paramref name="slices"/>, showing each target as the plans predict unless told otherwise.</summary>
    private SwiftLoweringEvidence Receipt(IEnumerable<string>? slices = null, string? selfShows = null) =>
        SwiftLoweringEvidenceTests.Record("M", new[] { Compiler }, slices ?? Shipped, new Dictionary<string, string>
        {
            [SelfTarget] = selfShows ?? "swiftcc " + _selfCallee,
            [ThrowingTarget] = "swiftcc " + _throwingCallee,
        });

    private static (SwiftEvidenceExpectation? Expectation, string? Why) Expect(SwiftArtifactScope scope) =>
        (new SwiftEvidenceExpectation(scope, Compiler, SwiftLoweringContract.Current,
            scope.Slices.ToImmutableSortedDictionary(t => t, SwiftLoweringEvidenceTests.Sdk, StringComparer.Ordinal)), null);

    private SwiftBridgeFinalizer.Outcome Finalize(SwiftLoweringEvidence? receipt, bool route = true,
        Func<SwiftArtifactScope, (SwiftEvidenceExpectation?, string?)>? expect = null) =>
        SwiftBridgeFinalizer.Finalize(
            new SwiftBridgeFinalizer.Request(_dir, "M", Library, route, receipt, expect ?? (scope => Expect(scope))),
            r =>
            {
                _events.Add("relink");
                return _relink(r);
            },
            () => _events.Add("before-relink"),
            _log.CreateLogger("test"));

    /// <summary>What generation itself writes when handed <paramref name="receipt"/> as its evidence.</summary>
    private (Dictionary<string, string> CSharp, string Plans, string Ledger, string? Arm64, SwiftBridgeRouting Routing) GenerationUnder(SwiftLoweringEvidence receipt)
    {
        var scope = new SwiftArtifactScope("M", receipt.ReceiptsOf("M").SelectMany(r => r.Slices.Keys).Distinct());
        var judged = _generated.WithEvidence(SwiftLoweringAuthorization.Authorize(Expect(scope).Expectation!, receipt));
        var routing = SwiftBridgeRouting.Plan(judged, [new SwiftBridgeSource(MainFile, MainSource), new SwiftBridgeSource(SplitFile, SplitSource)], Library);
        return (
            new Dictionary<string, string> { [MainFile] = routing.Rewrite(MainSource), [SplitFile] = routing.Rewrite(SplitSource) },
            routing.Annotate(judged).ToJson(),
            _ledger.WithBridges(routing.Routes).ToJson(),
            routing.Assembly(SwiftArchitecture.Arm64)?.Source,
            routing);
    }

    private Dictionary<string, string> Snapshot() =>
        Directory.GetFiles(_dir).ToDictionary(p => Path.GetFileName(p)!, p => File.ReadAllText(p), StringComparer.Ordinal);

    // ---- routing from the receipt ----

    [Fact]
    public void AnOrdinaryModule_IsRoutedFromItsReceipt_ExactlyAsGenerationWouldRouteIt_AndTheWrapperRelinked()
    {
        WriteGeneration();
        var receipt = Receipt();
        _relink = r =>
        {
            // Everything the relink compiles is on disk before it runs.
            Assert.True(File.Exists(AssemblyPath(SwiftArchitecture.Arm64)));
            Assert.Contains($"EntryPoint = \"{SwiftCallPlan.ForwardPrefix}", Read(MainFile));
            Assert.True(File.Exists(JournalPath));
            Assert.Equal(Shipped.ToArray(), r.Slices.Keys.ToArray());
            return null;
        };

        Assert.Equal(SwiftBridgeFinalizer.Outcome.Relinked, Finalize(receipt));

        Assert.Equal(new[] { "before-relink", "relink" }, _events);
        var expected = GenerationUnder(receipt);
        Assert.Equal(2, expected.Routing.Bridged.Length);
        Assert.Equal(expected.CSharp[MainFile], Read(MainFile));
        Assert.Equal(expected.CSharp[SplitFile], Read(SplitFile));
        Assert.Equal(expected.Plans, Read(SwiftCallPlanSet.FileName("M")));
        Assert.Equal(expected.Ledger, Read(SwiftTransitionLedger.FileName("M")));
        Assert.Equal(expected.Arm64, File.ReadAllText(AssemblyPath(SwiftArchitecture.Arm64)));
        Assert.DoesNotContain("CallConvSwift", Read(MainFile) + Read(SplitFile));

        // The judgement claims exactly the slices the receipt holds: no x86_64 simulator.
        var sidecar = SwiftCallPlanSet.From(SwiftCallPlanSet.Read(PathOf(SwiftCallPlanSet.FileName("M"))));
        Assert.Equal(Shipped.ToArray(), sidecar.EvidenceScope!.Slices.ToArray());
        Assert.All(sidecar.Coverage, e => Assert.True(e.Bridged, e.BridgeRefusal ?? e.Evidence));

        // The journal records each rewrite in the file it was made in.
        var journal = SwiftBridgeRoutingJournal.Read(JournalPath);
        Assert.Equal("M", journal.Module);
        Assert.Equal(Library, journal.Library);
        Assert.Equal(new[] { MainFile, SplitFile }.Order(StringComparer.Ordinal), journal.Rewrites.Select(r => r.File));
    }

    [Fact]
    public void TheNextCompileOfAnUnchangedModule_ChangesNothing_AndNeverRelinks()
    {
        WriteGeneration();
        Assert.Equal(SwiftBridgeFinalizer.Outcome.Relinked, Finalize(Receipt()));
        var before = Snapshot();
        _events.Clear();

        Assert.Equal(SwiftBridgeFinalizer.Outcome.Unchanged, Finalize(Receipt()));

        Assert.Empty(_events);
        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public void AReceiptThatContradictsAPlan_KeepsThatImport_AndTheWrapperIsRelinkedWithoutItsBridge()
    {
        WriteGeneration();
        Assert.Equal(SwiftBridgeFinalizer.Outcome.Relinked, Finalize(Receipt()));
        _events.Clear();

        var contradicting = Receipt(selfShows: "swiftcc i64 (i64, i64)");
        Assert.Equal(SwiftBridgeFinalizer.Outcome.Relinked, Finalize(contradicting));

        // The linked wrapper defines a bridge nothing calls any more: an export no route accounts for,
        // so it is relinked from bridges that define only the one left.
        Assert.Equal(new[] { "before-relink", "relink" }, _events);
        var expected = GenerationUnder(contradicting);
        Assert.Equal(MainSource, Read(MainFile));
        Assert.Equal(expected.CSharp[SplitFile], Read(SplitFile));
        Assert.Equal(expected.Plans, Read(SwiftCallPlanSet.FileName("M")));
        Assert.Equal(expected.Arm64, File.ReadAllText(AssemblyPath(SwiftArchitecture.Arm64)));
        Assert.Single(expected.Routing.Bridged);
        var self = SwiftCallPlanSet.From(SwiftCallPlanSet.Read(PathOf(SwiftCallPlanSet.FileName("M")))).Coverage.Single(e => e.EntryPoint == SelfTarget);
        Assert.False(self.Executable);
        Assert.Contains("the plan predicts", self.Evidence);
        Assert.Equal(new[] { SplitFile }, SwiftBridgeRoutingJournal.Read(JournalPath).Rewrites.Select(r => r.File));
    }

    [Fact]
    public void BridgesThatDifferFromWhatTheWrapperWasLinkedFrom_RelinkIt_EvenWithTheSameSymbols()
    {
        WriteGeneration();
        Assert.Equal(SwiftBridgeFinalizer.Outcome.Relinked, Finalize(Receipt()));
        var expected = File.ReadAllText(AssemblyPath(SwiftArchitecture.Arm64));
        // The wrapper was linked from bridges with the same exports but another body.
        File.WriteAllText(AssemblyPath(SwiftArchitecture.Arm64), expected + "\n    nop\n");
        _events.Clear();

        Assert.Equal(SwiftBridgeFinalizer.Outcome.Relinked, Finalize(Receipt()));

        Assert.Equal(new[] { "before-relink", "relink" }, _events);
        Assert.Equal(expected, File.ReadAllText(AssemblyPath(SwiftArchitecture.Arm64)));
    }

    [Fact]
    public void AReceiptOfOneSlice_ClaimsOnlyThatSlice()
    {
        WriteGeneration();
        var simulatorOnly = Receipt(new[] { "arm64-apple-ios15.0-simulator" });

        Assert.Equal(SwiftBridgeFinalizer.Outcome.Relinked, Finalize(simulatorOnly));

        var sidecar = SwiftCallPlanSet.From(SwiftCallPlanSet.Read(PathOf(SwiftCallPlanSet.FileName("M"))));
        Assert.Equal(new[] { "arm64-apple-ios15.0-simulator" }, sidecar.EvidenceScope!.Slices.ToArray());
        Assert.Contains("on 1 triple(s)", sidecar.Coverage[0].Evidence);
    }

    [Fact]
    public void AnUnidentifiedToolchain_RoutesNothing_AndNeverRelinks()
    {
        WriteGeneration();

        var outcome = Finalize(Receipt(), expect: _ => (null, "xcrun swiftc --version failed"));

        Assert.Equal(SwiftBridgeFinalizer.Outcome.Rewritten, outcome);
        Assert.Empty(_events);
        Assert.Equal(MainSource, Read(MainFile));
        Assert.Equal(SplitSource, Read(SplitFile));
        Assert.False(File.Exists(AssemblyPath(SwiftArchitecture.Arm64)));
        var sidecar = SwiftCallPlanSet.From(SwiftCallPlanSet.Read(PathOf(SwiftCallPlanSet.FileName("M"))));
        Assert.All(sidecar.Coverage, e => Assert.False(e.Executable));
        Assert.Contains("xcrun swiftc --version failed", sidecar.Coverage[0].Evidence);
        Assert.Contains(_log.Warnings, w => w.Contains("does not authorize", StringComparison.Ordinal));
    }

    // ---- what fails ----

    [Fact]
    public void AFailedRelink_LeavesGenerationsCSharp_AndTheBridgesTheWrapperWasLinkedFrom()
    {
        WriteGeneration();
        _relink = _ => "swiftc exited 1";

        Assert.Equal(SwiftBridgeFinalizer.Outcome.Withdrawn, Finalize(Receipt()));

        Assert.Equal(new[] { "before-relink", "relink" }, _events);
        Assert.Equal(MainSource, Read(MainFile));
        Assert.Equal(SplitSource, Read(SplitFile));
        Assert.False(File.Exists(AssemblyPath(SwiftArchitecture.Arm64)));
        Assert.False(File.Exists(AssemblyPath(SwiftArchitecture.X86_64)));
        Assert.Empty(SwiftBridgeRoutingJournal.Read(JournalPath).Rewrites);
        Assert.Equal(_ledger.ToJson(), Read(SwiftTransitionLedger.FileName("M")));
        var sidecar = SwiftCallPlanSet.From(SwiftCallPlanSet.Read(PathOf(SwiftCallPlanSet.FileName("M"))));
        Assert.All(sidecar.Coverage, e =>
        {
            Assert.True(e.Executable);
            Assert.False(e.Bridged);
            Assert.Contains("swiftc exited 1", e.BridgeRefusal);
        });
        Assert.Contains(_log.Warnings, w => w.Contains(SwiftBridgeFinalizer.FailedCode, StringComparison.Ordinal));
    }

    [Fact]
    public void CSharpTheJournalDoesNotDescribe_FailsTheBuild_AndIsLeftAsFound()
    {
        WriteGeneration();
        Assert.Equal(SwiftBridgeFinalizer.Outcome.Relinked, Finalize(Receipt()));
        // Something other than the generator edits a routed import.
        var rewrite = SwiftBridgeRoutingJournal.Read(JournalPath).Rewrites.Single(r => r.File == MainFile);
        File.WriteAllText(PathOf(MainFile), Read(MainFile).Replace(rewrite.Replacement, rewrite.Replacement.Replace("nint __self", "nint __other"), StringComparison.Ordinal));
        var before = Snapshot();
        _events.Clear();

        Assert.Equal(SwiftBridgeFinalizer.Outcome.Failed, Finalize(Receipt()));

        Assert.Empty(_events);
        Assert.Equal(before, Snapshot());
        Assert.Contains(_log.Warnings, w => w.Contains(SwiftBridgeFinalizer.FailedCode, StringComparison.Ordinal) && w.Contains("Regenerate", StringComparison.Ordinal));
    }

    [Fact]
    public void AnUnreadableJournal_FailsTheBuild()
    {
        WriteGeneration();
        File.WriteAllText(JournalPath, "{ \"schema\": 99 }");

        Assert.Equal(SwiftBridgeFinalizer.Outcome.Failed, Finalize(Receipt()));
        Assert.Equal(MainSource, Read(MainFile));
    }

    // ---- what is left alone, and what is undone ----

    [Fact]
    public void CSharpGenerationRoutedItself_IsLeftAlone()
    {
        WriteGeneration();
        var expected = GenerationUnder(Receipt());
        File.WriteAllText(PathOf(MainFile), expected.CSharp[MainFile]);
        File.WriteAllText(PathOf(SplitFile), expected.CSharp[SplitFile]);
        File.WriteAllText(PathOf(SwiftCallPlanSet.FileName("M")), expected.Plans);
        var before = Snapshot();

        // Even under a receipt that would route nothing.
        Assert.Equal(SwiftBridgeFinalizer.Outcome.NotApplicable, Finalize(Receipt(selfShows: "swiftcc i64 (i64, i64)")));

        Assert.Empty(_events);
        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public void WithRoutingNoLongerAskedFor_AnEarlierRoutingIsUndone()
    {
        WriteGeneration();
        Assert.Equal(SwiftBridgeFinalizer.Outcome.Relinked, Finalize(Receipt()));
        Assert.True(SwiftBridgeFinalizer.Applies(_dir, route: false));
        _events.Clear();

        Assert.Equal(SwiftBridgeFinalizer.Outcome.Rewritten, Finalize(receipt: null, route: false));

        // With no receipt to verify a relink against, the bridges stay inert in this wrapper and
        // are gone from the next compile.
        Assert.Empty(_events);
        Assert.Equal(MainSource, Read(MainFile));
        Assert.Equal(SplitSource, Read(SplitFile));
        Assert.Equal(_generated.ToJson(), Read(SwiftCallPlanSet.FileName("M")));
        Assert.Equal(_ledger.ToJson(), Read(SwiftTransitionLedger.FileName("M")));
        Assert.False(File.Exists(AssemblyPath(SwiftArchitecture.Arm64)));
        Assert.Empty(SwiftBridgeRoutingJournal.Read(JournalPath).Rewrites);
        Assert.Contains(_log.Entries, e => e.Message.Contains("no longer calls", StringComparison.Ordinal));
    }

    [Fact]
    public void WithRoutingNoLongerAskedFor_AndAReceipt_TheWrapperIsRelinkedWithoutTheBridges()
    {
        WriteGeneration();
        Assert.Equal(SwiftBridgeFinalizer.Outcome.Relinked, Finalize(Receipt()));
        _events.Clear();

        Assert.Equal(SwiftBridgeFinalizer.Outcome.Relinked, Finalize(Receipt(), route: false));

        Assert.Equal(new[] { "before-relink", "relink" }, _events);
        Assert.Equal(MainSource, Read(MainFile));
        Assert.Equal(SplitSource, Read(SplitFile));
        Assert.Equal(_generated.ToJson(), Read(SwiftCallPlanSet.FileName("M")));
        Assert.False(File.Exists(AssemblyPath(SwiftArchitecture.Arm64)));
        Assert.False(File.Exists(AssemblyPath(SwiftArchitecture.X86_64)));
    }

    [Fact]
    public void WithNothingAskedForAndNoJournal_NothingApplies()
    {
        WriteGeneration();
        Assert.False(SwiftBridgeFinalizer.Applies(_dir, route: false));
        var before = Snapshot();

        Assert.Equal(SwiftBridgeFinalizer.Outcome.NotApplicable, Finalize(Receipt(), route: false));
        Assert.Equal(SwiftBridgeFinalizer.Outcome.NotApplicable, Finalize(receipt: null));

        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public void ARoutingInterruptedAfterItsJournal_IsCompletedByTheNextCompile()
    {
        WriteGeneration();
        Assert.Equal(SwiftBridgeFinalizer.Outcome.Relinked, Finalize(Receipt()));
        var routed = Snapshot();
        // The build stopped after the journal and the bridges were written, before the C#.
        File.WriteAllText(PathOf(MainFile), MainSource);
        File.WriteAllText(PathOf(SplitFile), SplitSource);
        _events.Clear();

        Assert.Equal(SwiftBridgeFinalizer.Outcome.Rewritten, Finalize(Receipt()));

        Assert.Empty(_events);
        Assert.Equal(routed, Snapshot());
    }

    [Fact]
    public void ARoutingThatBridgesLess_InterruptedBeforeItsCSharp_IsCompletedByTheNextCompile()
    {
        WriteGeneration();
        Assert.Equal(SwiftBridgeFinalizer.Outcome.Relinked, Finalize(Receipt()));
        var contradicting = Receipt(selfShows: "swiftcc i64 (i64, i64)");
        // The build stops at the first C# file: the journal and the bridges are written, and the
        // file whose import is no longer routed still holds its rewrite.
        var blocker = PathOf(MainFile) + $".{Environment.ProcessId}.tmp";
        Directory.CreateDirectory(blocker);
        Assert.ThrowsAny<Exception>(() => Finalize(contradicting));
        Directory.Delete(blocker);
        Assert.Contains($"EntryPoint = \"{SwiftCallPlan.ForwardPrefix}", Read(MainFile));

        Finalize(contradicting);

        var expected = GenerationUnder(contradicting);
        Assert.Equal(MainSource, Read(MainFile));
        Assert.Equal(expected.CSharp[SplitFile], Read(SplitFile));
        Assert.Equal(new[] { SplitFile }, SwiftBridgeRoutingJournal.Read(JournalPath).Rewrites.Select(r => r.File));
    }

    // ---- the journal ----

    [Fact]
    public void Journal_RoundTrips_AndRevertsEachFileItRewrote()
    {
        var routing = GenerationUnder(Receipt()).Routing;
        var journal = new SwiftBridgeRoutingJournal
        {
            Schema = SwiftBridgeRoutingJournal.SchemaVersion,
            Module = "M",
            Library = Library,
            Rewrites = routing.Rewrites,
        };
        journal.Write(JournalPath);
        var read = SwiftBridgeRoutingJournal.Read(JournalPath);

        Assert.Equal(journal.ToJson(), read.ToJson());
        Assert.Equal(MainSource, read.Revert(MainFile, routing.Rewrite(MainSource)));
        Assert.Equal(SplitSource, read.Revert(SplitFile, routing.Rewrite(SplitSource)));
        // A file whose rewrite was never written is already generation's own.
        Assert.Equal(MainSource, read.Revert(MainFile, MainSource));
        // A file the journal records nothing in is left as it is.
        Assert.Equal("anything", read.Revert("M.Types.E.cs", "anything"));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void Journal_RecordingAnImportUnderTwoRoutings_RevertsWhicheverTheFileHolds()
    {
        var routing = GenerationUnder(Receipt()).Routing;
        var main = routing.Rewrites.Single(r => r.File == MainFile);
        var other = main with { Symbol = "SBSR_0000000000000000", Replacement = main.Replacement.Replace(main.Symbol, "SBSR_0000000000000000", StringComparison.Ordinal) };
        // Either order of the two in the journal.
        foreach (var rewrites in new[] { ImmutableArray.Create(main, other), ImmutableArray.Create(other, main) })
        {
            var journal = new SwiftBridgeRoutingJournal { Schema = SwiftBridgeRoutingJournal.SchemaVersion, Module = "M", Library = Library, Rewrites = rewrites };
            Assert.Equal(MainSource, journal.Revert(MainFile, routing.Rewrite(MainSource)));
            Assert.Equal(MainSource, journal.Revert(MainFile, MainSource.Replace(main.Original, other.Replacement, StringComparison.Ordinal)));
            Assert.Equal(MainSource, journal.Revert(MainFile, MainSource));
        }
    }

    [Fact]
    public void Journal_RefusesAFileThatHoldsNeitherFormOfARewrite()
    {
        var routing = GenerationUnder(Receipt()).Routing;
        var journal = new SwiftBridgeRoutingJournal { Schema = SwiftBridgeRoutingJournal.SchemaVersion, Module = "M", Library = Library, Rewrites = routing.Rewrites };
        var mainRewrite = routing.Rewrites.Single(r => r.File == MainFile);

        Assert.Throws<InvalidDataException>(() => journal.Revert(MainFile, MainSource.Replace(mainRewrite.Original, "", StringComparison.Ordinal)));
        Assert.Throws<InvalidDataException>(() => journal.Revert(MainFile, routing.Rewrite(MainSource) + mainRewrite.Replacement));
    }

    [Fact]
    public void Journal_OfAnotherSchema_IsNotRead()
    {
        File.WriteAllText(JournalPath, $"{{ \"schema\": {SwiftBridgeRoutingJournal.SchemaVersion + 1}, \"module\": \"M\", \"library\": \"L\", \"rewrites\": [] }}");
        Assert.Throws<InvalidDataException>(() => SwiftBridgeRoutingJournal.Read(JournalPath));
    }

    // ---- the sidecars the routing rewrites ----

    [Fact]
    public void WithoutEvidence_IsThePlanSetGenerationRecordsUnjudged()
    {
        var expected = GenerationUnder(Receipt());
        var judgedAndRouted = SwiftCallPlanSet.From(SwiftCallPlanSet.Read(WriteText("judged.json", expected.Plans)));
        Assert.NotNull(judgedAndRouted.EvidenceScope);

        Assert.Equal(_generated.ToJson(), judgedAndRouted.WithoutEvidence().ToJson());
    }

    [Fact]
    public void LedgerWithBridges_WritesWhatGenerationWrites_AndClearsWhatNoLongerRoutes()
    {
        var routing = GenerationUnder(Receipt()).Routing;
        var unrouted = _ledger.ToJson();
        var routed = _ledger.WithBridges(routing.Routes).ToJson();
        Assert.NotEqual(unrouted, routed);

        Assert.Equal(routed, SwiftTransitionLedger.WithBridges(unrouted, routing.Routes));
        Assert.Equal(routed, SwiftTransitionLedger.WithBridges(routed, routing.Routes));
        Assert.Equal(unrouted, SwiftTransitionLedger.WithBridges(routed, Array.Empty<SwiftBridgeRoute>()));
        Assert.Equal(unrouted, SwiftTransitionLedger.WithBridges(routed, routing.Withdraw("withdrawn").Routes));
        Assert.Throws<InvalidDataException>(() => SwiftTransitionLedger.WithBridges("{ \"schema\": 0 }", routing.Routes));
    }

    private string WriteText(string file, string text)
    {
        File.WriteAllText(PathOf(file), text);
        return PathOf(file);
    }
}
