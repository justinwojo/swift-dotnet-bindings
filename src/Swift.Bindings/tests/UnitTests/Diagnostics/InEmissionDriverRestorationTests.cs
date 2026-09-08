// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using Microsoft.Extensions.Logging.Abstractions;

using BindingsGeneration.Diagnostics;

using Xunit;
using Xunit.Abstractions;

namespace BindingsGeneration.Tests;

/// <summary>
/// End-to-end orchestration coverage for the production <see cref="InEmissionDriver"/>: it constructs
/// over a real module, runs multiple renders on ONE instance, converges each time, rebuilds its
/// collaborators per render, and produces stable non-trivial output — the seam both external reviewers
/// flagged as having no test against the real driver at all.
/// </summary>
/// <remarks>
/// <para>
/// Scope note, read before trusting these as leak detectors. The four mechanism-gap fixes are pinned as
/// leak detectors at the <em>primitive</em> layer, where a regression actually goes red:
/// <see cref="EmissionStateSnapshotCoverageTests"/> (decl + context snapshot round trips, gap #1's
/// reference restore and the context dedup registries), <see cref="EmissionFactsJournalTests"/> (the
/// type-database undo log, gap #2), and <see cref="RecoveryModelTests"/> (the droppable-alone rules the
/// driver's <c>DroppableGate</c> enforces, gap #4). Gap #3 — the compile provenance is captured before
/// the <c>.wrapper-build</c> staging tree is cleaned up — has no primitive behavioral pin yet: the
/// cleanup itself is covered (temp dir removed after a compile) but the ordering that captures provenance
/// first is only observed indirectly here, so a dedicated pin is wave-2. These driver-level tests
/// are the integration counterpart: they prove the real driver <em>orchestrates</em> those primitives
/// across renders without crashing, spinning, or accumulating stale output, and that a reused instance
/// stays output-stable. The default shared fixture's emission facts and specialization graph do not
/// change re-emission output, so its byte-identity assertions alone are not per-gap leak detectors.
/// The opt-in nested-conformer fixture is consequential: a real emission-time type rename changes the
/// next render's factory name if the specialization index is rebuilt before the journal is restored.
/// It pins both the cached naming token and live type reference through actual generated artifacts.
/// </para>
/// <para>
/// The wrapper compile is stubbed to report "all slices clean" so <see cref="InEmissionDriver.
/// RenderCompileAttribute"/> takes its converged (return-null) path after writing the render to disk;
/// the render itself — snapshot restore, journal-undo, rebuild, seed, emit — is the real production code. Each
/// render's output is captured as the concatenation of the emitted C# files, so a difference is a real
/// difference in generated surface, not compile-side noise.
/// </para>
/// </remarks>
public class InEmissionDriverRestorationTests : IDisposable
{
    private readonly List<string> _scratchDirs = new();
    private readonly ITestOutputHelper _output;

    public InEmissionDriverRestorationTests(ITestOutputHelper output) => _output = output;

    public void Dispose()
    {
        foreach (var dir in _scratchDirs)
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { /* best effort */ }
        }
    }

    // ── gaps #1 (engine) + #2 (typeDB) jointly: same denylist ⇒ byte-identical ────────────────

    /// <summary>
    /// The master orchestration pin: the empty denylist rendered twice on the same driver must produce
    /// byte-identical output, so the render is a pure function of its denylist — the driver's
    /// restore → journal-undo → rebuild → emit sequence carries nothing from render 1 into render 2 that
    /// reaches the emitted surface. A non-trivial output guard keeps the byte-identity from passing
    /// vacuously. This does NOT on its own prove each restoration channel is load-bearing: on this fixture
    /// the emission facts and specialization graph do not alter re-emission output (see the class remark),
    /// so byte-identity would still hold if one channel were removed. The per-channel leak detectors live
    /// at the primitive layer — <see cref="EmissionFactsJournalTests"/> for the type-database undo log,
    /// <see cref="EmissionStateSnapshotCoverageTests"/> for the snapshots.
    /// </summary>
    [Fact]
    public void EmptyDenylistRenderedTwice_OnSameDriver_ProducesByteIdenticalOutput()
    {
        using var harness = new DriverHarness(this);

        var first = harness.Render(EmptyDenylist);
        var second = harness.Render(EmptyDenylist);

        AssertExercisedTheMachinery(first);
        AssertByteIdentical(first, second, "empty denylist rendered twice");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NestedConformerCollision_AcrossCompletedRenders_PreservesFactoryNameAndLiveType(
        bool withdrawUnrelatedMembers)
    {
        // The real module pre-pass renames Box.Entry to Box.EntryInfo. Conformance indexing
        // deliberately precedes that pass: synthetic factory names use the indexed spelling,
        // while their parameter types resolve the live renamed record. Rebuilding the index
        // before restoring the previous render's journal changes only the second factory name.
        using var harness = new DriverHarness(this, withNestedConformerCollision: true);
        try
        {
            harness.AssertCollisionProducerPrerequisites();
            var original = harness.Render(EmptyDenylist);
            var originalSwift = harness.SwiftOutput();
            AssertCollisionFactory(original, originalSwift);
            AssertCollisionNames(harness);

            if (withdrawUnrelatedMembers)
            {
                var recovered = harness.Render(harness.WithdrawableUnits());
                Assert.NotEqual(original, recovered);
                AssertCollisionFactory(recovered, harness.SwiftOutput());
                AssertCollisionNames(harness);
            }

            var repeated = harness.Render(EmptyDenylist);
            AssertCollisionFactory(repeated, harness.SwiftOutput());
            AssertCollisionNames(harness);
            AssertByteIdentical(original, repeated, "nested conformer factory after completed render");
            AssertByteIdentical(originalSwift, harness.SwiftOutput(), "nested conformer native wrapper after completed render");
        }
        catch
        {
            // Assert.Contains abbreviates its actual string. Keep the generated artifacts before
            // Dispose removes the scratch so a producer refusal remains independently inspectable.
            _output.WriteLine($"Nested collision failure artifacts: {harness.PreserveFailureArtifacts()}");
            throw;
        }
    }

    private static void AssertCollisionNames(DriverHarness harness)
    {
        var conformer = Assert.Single(harness.CurrentEngine!.GetConformers(
            SwiftTypeName.FromModuleQualifiedName("ContainmentFixture.EntryMaterial")));
        Assert.Equal("ContainmentFixture.Box.Entry", conformer.CSharpType);
        Assert.Equal("ContainmentFixture.Box.EntryInfo", harness.CollisionTypeName);
    }

    private static void AssertCollisionFactory(string csharp, string swift)
    {
        Assert.Contains("FromContainmentFixtureBoxEntry(", csharp, StringComparison.Ordinal);
        Assert.DoesNotContain("FromContainmentFixtureBoxEntryInfo(", csharp, StringComparison.Ordinal);
        Assert.Contains("ContainmentFixture.Box.EntryInfo source", csharp, StringComparison.Ordinal);
        var import = Regex.Match(csharp,
            "EntryPoint = \"(?<symbol>SBW_CSM_ContainmentFixture_Box_ContainmentFixture_Box_Entry_init_[A-F0-9]+)\"");
        Assert.True(import.Success, "the factory must own a real generated native specialization import");
        Assert.Contains($"@_cdecl(\"{import.Groups["symbol"].Value}\")", swift, StringComparison.Ordinal);
        Assert.Contains("ContainmentFixture.Box.Entry.self", swift, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same invariant under an active withdrawal: a non-empty denylist rendered twice must also be
    /// byte-identical. Rendering under a seed exercises the poison-and-tombstone path in addition to the
    /// plain emission, so this covers restoration of the state that path touches too.
    /// </summary>
    [Fact]
    public void NonEmptyDenylistRenderedTwice_OnSameDriver_ProducesByteIdenticalOutput()
    {
        using var harness = new DriverHarness(this);
        var denylist = harness.WithdrawableUnits();

        var clean = harness.Render(EmptyDenylist);
        var first = harness.Render(denylist);
        var second = harness.Render(denylist);

        // Non-vacuity: the denylist must actually withdraw surface, else "identical under a denylist"
        // proves nothing — the poison-and-tombstone path has to have done observable work.
        Assert.NotEqual(clean, first);
        AssertByteIdentical(first, second, "non-empty denylist rendered twice");
    }

    // ── gap #2 (typeDB pre-image): a withdrawal between two clean renders must not contaminate ──

    /// <summary>
    /// Empty → denylist → empty. The third render must match the first byte-for-byte — an intervening
    /// withdrawal leaves no residue in the later clean render's emitted surface — and the withdrawal
    /// render must actually differ from the clean one, otherwise the equality proves nothing because the
    /// denylist did no work. This is output-level order-independence, NOT a direct proof that the
    /// type-database pre-image was the channel restored (on this fixture emission facts do not change
    /// re-emission output); that proof is <see cref="EmissionFactsJournalTests"/>.
    /// </summary>
    [Fact]
    public void WithdrawalBetweenTwoCleanRenders_DoesNotContaminateTheLaterCleanRender()
    {
        using var harness = new DriverHarness(this);
        var denylist = harness.WithdrawableUnits();

        var cleanBefore = harness.Render(EmptyDenylist);
        var withdrawn = harness.Render(denylist);
        var cleanAfter = harness.Render(EmptyDenylist);

        // Non-vacuity: the withdrawal changed the surface, so restoring from it is a real test.
        Assert.NotEqual(cleanBefore, withdrawn);
        AssertByteIdentical(cleanBefore, cleanAfter, "clean render after an intervening withdrawal");
    }

    // ── gap #1 (engine not dirtied across rounds): order-independence ──────────────────────────

    /// <summary>
    /// Denylist → empty → denylist. The first and third renders both apply the same denylist and must be
    /// byte-identical, proving the specialization engine (which memoizes rejected pairings in place) was
    /// rebuilt to a pristine instance for the third render rather than carrying the second render's state.
    /// </summary>
    [Fact]
    public void SameDenylistReappliedAfterAnInterveningRender_IsByteIdenticalToItsFirstApplication()
    {
        using var harness = new DriverHarness(this);
        var denylist = harness.WithdrawableUnits();

        var firstApplication = harness.Render(denylist);
        harness.Render(EmptyDenylist);
        var reapplication = harness.Render(denylist);

        AssertByteIdentical(firstApplication, reapplication, "denylist reapplied after an intervening render");
    }

    // ── gap #1 (in-place engine shed by rebuild): a fresh engine per render ─────────────────────

    /// <summary>
    /// The specialization engine memoizes rejected pairings in place, so restoring its reference would
    /// reinstate the tainted instance; the driver therefore rebuilds a fresh engine each render. This
    /// pins that the rebuild actually fires — the engine the emission context carries after render 2 is
    /// a different instance than after render 1 — so a regression that dropped the rebuild (or swapped it
    /// for a reference-restore) is caught structurally.
    /// </summary>
    [Fact]
    public void EachRender_InstallsAFreshSpecializationEngine_OnTheEmissionContext()
    {
        using var harness = new DriverHarness(this);

        harness.Render(EmptyDenylist);
        var afterFirst = harness.CurrentEngine;
        harness.Render(EmptyDenylist);
        var afterSecond = harness.CurrentEngine;

        Assert.NotNull(afterFirst);
        Assert.NotNull(afterSecond);
        Assert.False(
            ReferenceEquals(afterFirst, afterSecond),
            "the driver reused the same specialization engine across renders; its in-place rejected-" +
            "pairing memo would carry the previous render's state into the next.");
    }

    // ── bisection soundness: a vacuous no-wrapper-surface probe must not confirm a culprit ──────

    /// <summary>
    /// A bisection probe "converges" (its render→compile returns null) on two DISTINCT terminals: a genuine
    /// clean joint compile, and a vacuous no-wrapper-surface outcome (the probe withdrew the last compilable
    /// member, so nothing was left to compile). Only the former is evidence the withdrawn subset contained
    /// the culprit. This pins that the real driver does NOT count a no-wrapper-surface convergence as a clean
    /// probe: a stub that raises no-wrapper-surface exactly when one specific candidate is withdrawn — the
    /// non-uniform shape that slips past the search's necessity gate (retaining the culprit leaves a real,
    /// still-failing compile) — must make the search DECLINE, never falsely isolate that innocent leaf.
    /// Without the guard the containment/sufficiency probe reads the vacuous convergence as clean and the
    /// search confirms a wrong culprit.
    /// </summary>
    [Fact]
    public void AttemptBisection_ProbeThatEmptiesTheWrapperSurface_DeclinesRatherThanFalselyIsolating()
    {
        RecoveryUnitId? culprit = null;
        var harness = new DriverHarness(this, compileOverride: req =>
        {
            // A candidate's withdrawal tombstone carries its Describe() text (scope token included), which
            // appears in the emitted C# ONLY when that unit was withdrawn this render. Withdrawing the
            // culprit empties the wrapper surface (a vacuous convergence); any other state leaves a real,
            // still-failing compile — so retaining the culprit while withdrawing others still fails, exactly
            // the non-uniform shape the necessity gate cannot catch on its own.
            var emitted = string.Concat(Directory
                .EnumerateFiles(req.OutputDirectory, "*.cs", SearchOption.AllDirectories)
                .Select(File.ReadAllText));
            var culpritWithdrawn = culprit is { } c && emitted.Contains(c.Describe(), StringComparison.Ordinal);
            return culpritWithdrawn
                ? WrapperCompileDiagnostics.NoWrapperSurfaceOutcome(
                    "no wrapper surface", result: null, Array.Empty<WrapperFileProvenance>())
                : WrapperCompileDiagnostics.Failed(
                    Array.Empty<WrapperSliceDiagnostics>(), Array.Empty<WrapperFileProvenance>());
        });

        // First render (culprit unset ⇒ a real failing compile) populates the FragmentSet the search reads
        // its candidate pool from; pick a culprit guaranteed to be in the pool the search will probe.
        harness.RenderRaw(EmptyDenylist);
        var pool = harness.CandidateGroups(EmptyDenylist);
        Assert.NotEmpty(pool); // the search has something to probe — else the test proves nothing
        culprit = pool[0][0];

        var outcome = harness.AttemptBisection(EmptyDenylist);

        Assert.False(
            outcome.DidIsolate,
            "the search isolated a leaf on a vacuous no-wrapper-surface convergence — a false confirmation, " +
            "not a decline");
        Assert.Empty(outcome.Isolated);
    }

    // ── bisection budget: probes are capped per MODULE, not per invocation ──────────────────────

    /// <summary>
    /// The mandate bounds bisection probes to a single digit PER MODULE. The controller may consult the seam
    /// once per unattributed round (up to the iteration cap), so a per-invocation budget would let a handful
    /// of rounds spend a multiple of the ceiling. This pins that the real driver accumulates probes across
    /// <see cref="InEmissionDriver.AttemptBisection"/> calls: repeated calls stop spending once the module's
    /// <see cref="BoundedBisectionSearch.DefaultProbeBudget"/> is exhausted, and further calls decline
    /// without probing at all.
    /// </summary>
    [Fact]
    public void AttemptBisection_ProbeBudget_IsCumulativePerModule_NotPerInvocation()
    {
        // A no-wrapper-surface verdict on every probe makes each search decline at its first (containment)
        // probe, so each invocation that runs spends exactly one probe — a clean unit of budget to count.
        var harness = new DriverHarness(this, compileOverride: _ =>
            WrapperCompileDiagnostics.NoWrapperSurfaceOutcome(
                "no wrapper surface", result: null, Array.Empty<WrapperFileProvenance>()));
        harness.RenderRaw(EmptyDenylist); // populate the FragmentSet the pool is read from
        Assert.NotEmpty(harness.CandidateGroups(EmptyDenylist));

        var budget = BoundedBisectionSearch.DefaultProbeBudget;
        var spent = 0;
        // Consult the seam far more times than the budget allows.
        for (int i = 0; i < budget + 5; i++)
            spent += harness.AttemptBisection(EmptyDenylist).ProbesUsed;

        Assert.Equal(budget, spent); // total probes never exceed the per-module ceiling, across every call
        // The next call is refused outright — the budget is gone, so it declines without spending a probe.
        Assert.Equal(0, harness.AttemptBisection(EmptyDenylist).ProbesUsed);
    }

    // ── assertions ────────────────────────────────────────────────────────────────────────────

    private static readonly HashSet<RecoveryUnitId> EmptyDenylist = new();

    private static void AssertByteIdentical(string expected, string actual, string what)
    {
        if (string.Equals(expected, actual, StringComparison.Ordinal))
            return;

        var limit = Math.Min(expected.Length, actual.Length);
        var offset = 0;
        while (offset < limit && expected[offset] == actual[offset])
            offset++;
        const int window = 160;
        var start = Math.Max(0, offset - window / 2);
        string Window(string s) => s.Substring(start, Math.Min(window, s.Length - start)).Replace("\n", "\\n");

        Assert.Fail(
            $"{what}: renders differ though the denylist was identical.{Environment.NewLine}" +
            $"  first difference at char {offset} (lengths {expected.Length} vs {actual.Length}){Environment.NewLine}" +
            $"  render A: …{Window(expected)}…{Environment.NewLine}" +
            $"  render B: …{Window(actual)}…");
    }

    /// <summary>
    /// Guards the byte-identity assertions from passing over a trivial output set: the fixture must have
    /// driven the emitter families whose ordering and name allocation are the plausible drift sources.
    /// </summary>
    private static void AssertExercisedTheMachinery(string csharp)
    {
        Assert.True(csharp.Length > 4000, $"C# output is too small to be meaningful ({csharp.Length} chars)");
        Assert.Contains("interface IShapeSink", csharp, StringComparison.Ordinal);
        Assert.Contains("LibraryImport", csharp, StringComparison.Ordinal);
        // The fixture's colliding `register` overloads are named from their own argument labels, so
        // seeing both proves the overload-collision resolver actually ran on this render.
        Assert.Contains("RegisterFirst", csharp, StringComparison.Ordinal);
        Assert.Contains("RegisterSecond", csharp, StringComparison.Ordinal);
    }

    // ── harness: the real driver, one instance, rendered repeatedly ─────────────────────────────

    /// <summary>
    /// Constructs the production <see cref="InEmissionDriver"/> over the shared fixture with the exact
    /// collaborators the command wires (<see cref="StringEmitter"/> factory, engine/marshalling rebuild,
    /// pre-render cleanup), and a stubbed compile that always reports clean so the driver takes its
    /// converged path after writing each render to disk. <see cref="Render"/> drives one round and
    /// returns that round's emitted C#.
    /// </summary>
    private sealed class DriverHarness : IDisposable
    {
        private readonly string _scratch;
        private readonly ModuleDecl _module;
        private readonly TypeDatabase _typeDatabase;
        private readonly ModuleEmissionContext _context;
        private readonly InEmissionDriver _driver;

        /// <summary>The specialization engine the emission context carries after the latest render.</summary>
        public ConcreteSpecializationEngine? CurrentEngine => _context.SpecializationEngine;

        public string CollisionTypeName
        {
            get
            {
                Assert.True(_typeDatabase.TryGetTypeRecord(
                    SwiftTypeName.FromModuleQualifiedName("ContainmentFixture.Box.Entry"), out var record));
                return record.CSharpTypeName.FullyQualifiedName;
            }
        }

        public DriverHarness(
            InEmissionDriverRestorationTests owner,
            Func<WrapperRecoveryCompileRequest, WrapperCompileDiagnostics>? compileOverride = null,
            bool withNestedConformerCollision = false)
        {
            _scratch = Path.Combine(Path.GetTempPath(), "swiftbind-driverrestore-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_scratch);
            owner._scratchDirs.Add(_scratch);

            _module = FixtureModuleFactory.BuildModule("ContainmentFixture");
            var nestedConformer = withNestedConformerCollision ? AddNestedConformerCollision(_module) : null;
            _typeDatabase = FixtureModuleFactory.BuildTypeDatabase(_module);
            if (nestedConformer != null)
            {
                // Native CSM emission requires the wrapper-library mode used by the actual
                // BindingTests generation. Leave the ordinary shared fixture's mode unchanged.
                _typeDatabase.AsyncLibraryName = "ContainmentFixtureSwiftBindings";
                // The shared fixture database registers its top-level types. Register the added
                // nested record in that same module before emission freezes or journals anything.
                _typeDatabase.RegisterCrossModuleType(nestedConformer.SwiftTypeName, new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName(_module.Name, "Box.Entry"),
                    SwiftTypeName = nestedConformer.SwiftTypeName,
                    MetadataAccessor = nestedConformer.MetadataAccessor,
                    Flags = TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement,
                    Kind = TypeRecordKind.Struct,
                });
            }
            _context = new ModuleEmissionContext();

            Func<StringEmitter> newEmitter = () =>
                new StringEmitter(_scratch, _typeDatabase, new NullLoggerFactory());

            Action rebuildCollaborators = () =>
            {
                var engine = new ConcreteSpecializationEngine(_typeDatabase, _module.Name);
                engine.IndexModuleConformances(_module);
                _context.SpecializationEngine = engine;
                _context.Marshaling = new MarshalingContext(_module, _typeDatabase, engine)
                {
                    EmissionContext = _context,
                };
            };

            // Always-clean compile by default: the render (restore → journal-undo → rebuild → seed → emit)
            // is the real production path; only the swiftc call is stubbed, so the driver converges after
            // writing each render to disk and we can read that render's output back. A test may inject a
            // different compile verdict (e.g. a no-wrapper-surface or failing outcome) to exercise the
            // bisection seam against the real driver.
            Func<WrapperRecoveryCompileRequest, WrapperCompileDiagnostics> compileWrapper =
                compileOverride ?? (_ =>
                    WrapperCompileDiagnostics.Clean(
                        result: null,
                        Array.Empty<WrapperSliceDiagnostics>(),
                        Array.Empty<WrapperFileProvenance>()));

            var request = new WrapperRecoveryCompileRequest(
                _scratch,
                InternalTypeNames: null,
                ModuleNameForCollision: null,
                NestedTypesInCollidingClass: null,
                new DepModuleCollisionDetector.SlicedCollisionResult(
                    Array.Empty<string>(), Array.Empty<string>()));

            _driver = new InEmissionDriver(
                _module, _context, _typeDatabase, NullLogger.Instance,
                newEmitter: newEmitter,
                rebuildCollaborators: rebuildCollaborators,
                compileWrapper: compileWrapper,
                request: request,
                // Clear the whole scratch each render so the snapshot is a pure function of that render
                // (a superset of production's wrapper-artifact cleanup, appropriate for isolating the
                // engine/typeDB restoration invariant these tests pin).
                preRender: ClearScratch);
        }

        /// <summary>The two withdrawable leaf/accessor units the ContainmentFixture exposes.</summary>
        public HashSet<RecoveryUnitId> WithdrawableUnits()
        {
            var registry = _module.Types.Single(t => t.Name == "Registry");
            var method = registry.Methods.Single(m =>
                m.Name == "register" && m.CSSignature.Any(p => p.Name == "third"));
            var property = registry.Properties.Single(p => p.Name == "name");
            return new HashSet<RecoveryUnitId>
            {
                RecoveryUnitId.Create(DeclIdFactory.ForMethod(method), RecoveryScope.LeafApi),
                RecoveryUnitId.ForAccessorGroup(DeclIdFactory.ForProperty(property)),
            };
        }

        /// <summary>
        /// Drives one render of the real driver under <paramref name="denylist"/> and returns the
        /// concatenated emitted C# for that round. The report session is opened and closed around the
        /// render exactly as the module boundary does, so each render's skip accounting is its own.
        /// </summary>
        public string Render(IReadOnlySet<RecoveryUnitId> denylist)
        {
            ReportCollector.Reset();
            try
            {
                var attribution = _driver.RenderCompileAttribute(denylist);
                Assert.Null(attribution); // stubbed compile is clean ⇒ converged, no attribution
            }
            finally
            {
                ReportCollector.Complete();
                ReportCollector.Reset();
            }

            // Prefix each file's content with its relative path so the snapshot also pins the FILE SET
            // and its names — a render that split, renamed, or moved a file (not just changed its text)
            // is a real difference the byte-identity assertions must catch.
            return string.Concat(Directory
                .EnumerateFiles(_scratch, "*", SearchOption.AllDirectories)
                .Where(p => p.EndsWith(".cs", StringComparison.Ordinal))
                .Select(p => Path.GetRelativePath(_scratch, p))
                .OrderBy(rel => rel, StringComparer.Ordinal)
                .Select(rel => $"// >>> {rel}\n{File.ReadAllText(Path.Combine(_scratch, rel))}"));
        }

        /// <summary>
        /// Runs one render WITHOUT asserting convergence, returning its attribution. Used to populate the
        /// FragmentSet under a non-clean stubbed compile before probing the bisection seam — the search's
        /// candidate pool is read from the last render's emitted artifacts.
        /// </summary>
        public AttributionResult? RenderRaw(IReadOnlySet<RecoveryUnitId> denylist)
        {
            ReportCollector.Reset();
            try { return _driver.RenderCompileAttribute(denylist); }
            finally { ReportCollector.Complete(); ReportCollector.Reset(); }
        }

        /// <summary>Runs the real driver's bounded-bisection seam under an open report session.</summary>
        public BisectionOutcome AttemptBisection(IReadOnlySet<RecoveryUnitId> denylist)
        {
            ReportCollector.Reset();
            try { return _driver.AttemptBisection(denylist); }
            finally { ReportCollector.Complete(); ReportCollector.Reset(); }
        }

        /// <summary>The candidate pool the search will actually probe for <paramref name="denylist"/>.</summary>
        public IReadOnlyList<ImmutableArray<RecoveryUnitId>> CandidateGroups(IReadOnlySet<RecoveryUnitId> denylist)
            => _driver.BuildBisectionCandidateGroups(denylist);

        public string SwiftOutput() => string.Concat(Directory
            .EnumerateFiles(_scratch, "*.swift", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal)
            .Select(File.ReadAllText));

        public void AssertCollisionProducerPrerequisites()
        {
            Assert.True(WrapperValidation.IsXCFrameworkMode(_typeDatabase),
                "the native CSM producer requires a configured wrapper library");
            var engine = new ConcreteSpecializationEngine(_typeDatabase, _module.Name);
            engine.IndexModuleConformances(_module);
            var conformer = Assert.Single(engine.GetConformers(
                SwiftTypeName.FromModuleQualifiedName("ContainmentFixture.EntryMaterial")));
            Assert.Equal(ConcreteProtocolSpecializationEmitter.StructuralEmitReject.None,
                ConcreteProtocolSpecializationEmitter.ClassifyConformerStructurally(conformer, _typeDatabase));
            var box = _module.Types.Single(t => t.Name == "Box");
            var constructor = Assert.Single(engine.FindSpecializableMethods(box), s => s.Method.IsConstructor);
            Assert.Single(constructor.SpecializableParams);
        }

        public string PreserveFailureArtifacts()
        {
            var destination = Path.Combine(Path.GetTempPath(), "swiftbind-driverrestore-failure-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(destination);
            foreach (var file in Directory.EnumerateFiles(_scratch, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(destination, Path.GetRelativePath(_scratch, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }
            return destination;
        }

        private static StructDecl AddNestedConformerCollision(ModuleDecl module)
        {
            var box = module.Types.Single(t => t.Name == "Box");
            var protocol = TestDecls.Protocol("EntryMaterial", module.Name,
                TestDecls.Method("material", returnType: new NamedTypeSpec("Swift.Int"), module: module.Name));
            protocol.ParentDecl = module;
            protocol.ModuleDecl = module;
            foreach (var method in protocol.Methods)
            {
                method.ParentDecl = protocol;
                method.ModuleDecl = module;
            }
            module.Types.Add(protocol);
            module.Protocols.Add(protocol);

            var entryName = SwiftTypeName.FromModuleQualifiedName($"{module.Name}.Box.Entry");
            var entry = new StructDecl
            {
                Name = "Entry", SwiftTypeName = entryName,
                MangledName = "$s18ContainmentFixture3BoxV5EntryVN",
                MetadataAccessor = "$s18ContainmentFixture3BoxV5EntryVMa",
                ParentDecl = box, ModuleDecl = module, IsFrozen = true,
                Properties = new(), Methods = new(), Types = new(), Operators = new(),
                Subscripts = new(), GenericParameters = new(),
                Conformances = new() { new(entryName, protocol.SwiftTypeName, "") },
            };
            var material = TestDecls.Method("material", returnType: new NamedTypeSpec("Swift.Int"), module: module.Name);
            material.ParentDecl = entry;
            material.ModuleDecl = module;
            entry.Methods.Add(material);
            // Match the existing CollisionVault.Entry fixture's supported class/Payload
            // projection: @frozen with stored String, rather than a trivial frozen value.
            var tag = TestDecls.Property("tag", new NamedTypeSpec("Swift.String"), module: module.Name);
            tag.HasStorage = true;
            tag.ParentDecl = entry;
            tag.ModuleDecl = module;
            foreach (var accessor in tag.Accessors)
            {
                accessor.Method.ParentDecl = entry;
                accessor.Method.ModuleDecl = module;
            }
            entry.Properties.Add(tag);
            box.Types.Add(entry);

            var property = TestDecls.Property("entry", new NamedTypeSpec(entryName.ToString()), module: module.Name);
            property.ParentDecl = box;
            property.ModuleDecl = module;
            foreach (var accessor in property.Accessors)
            {
                accessor.Method.ParentDecl = box;
                accessor.Method.ModuleDecl = module;
            }
            box.Properties.Add(property);

            var source = TestDecls.Param("source", new NamedTypeSpec("τ_0_0"));
            source.IsGeneric = true;
            var constructor = TestDecls.Method("init", methodType: MethodType.Static, isConstructor: true,
                parameters: new[] { source }, returnType: new NamedTypeSpec(box.SwiftTypeName.ToString()), module: module.Name);
            constructor.ParentDecl = box;
            constructor.ModuleDecl = module;
            constructor.GenericParameters.Add(new GenericArgumentDecl("τ_0_0", "T",
                new() { new(new[] { "τ_0_0" }, protocol.SwiftTypeName, ConformanceKind.Protocol) }, new()));
            box.Methods.Add(constructor);
            return entry;
        }

        private void ClearScratch()
        {
            foreach (var file in Directory.EnumerateFiles(_scratch, "*", SearchOption.AllDirectories))
            {
                try { File.Delete(file); } catch (IOException) { /* best effort */ }
            }
        }

        public void Dispose() { /* scratch dirs are cleaned by the owning test */ }
    }
}
