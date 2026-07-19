// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Microsoft.Extensions.Logging.Abstractions;

using BindingsGeneration.Diagnostics;

using Xunit;

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
/// stays output-stable. They are deliberately NOT the per-gap leak detector — the shared fixture's
/// emission facts and specialization graph do not change re-emission output, so byte-identity here holds
/// even if a single restoration channel were removed; the leak channels that DO change output only
/// surface on a corpus module with rejected specialization pairings or output-affecting emission facts,
/// which is wave-2 territory. The engine-rebuild check below is the one structural gap-#1 assertion this
/// layer can make directly.
/// </para>
/// <para>
/// The wrapper compile is stubbed to report "all slices clean" so <see cref="InEmissionDriver.
/// RenderCompileAttribute"/> takes its converged (return-null) path after writing the render to disk;
/// the render itself — restore, rebuild, journal-undo, seed, emit — is the real production code. Each
/// render's output is captured as the concatenation of the emitted C# files, so a difference is a real
/// difference in generated surface, not compile-side noise.
/// </para>
/// </remarks>
public class InEmissionDriverRestorationTests : IDisposable
{
    private readonly List<string> _scratchDirs = new();

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
    /// restore → rebuild → journal-undo → emit sequence carries nothing from render 1 into render 2 that
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
        Assert.Contains("Register2", csharp, StringComparison.Ordinal);
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

        public DriverHarness(InEmissionDriverRestorationTests owner)
        {
            _scratch = Path.Combine(Path.GetTempPath(), "swiftbind-driverrestore-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_scratch);
            owner._scratchDirs.Add(_scratch);

            _module = FixtureModuleFactory.BuildModule("ContainmentFixture");
            _typeDatabase = FixtureModuleFactory.BuildTypeDatabase(_module);
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

            // Always-clean compile: the render (restore → rebuild → journal-undo → seed → emit) is the
            // real production path; only the swiftc call is stubbed, so the driver converges after
            // writing each render to disk and we can read that render's output back.
            Func<WrapperRecoveryCompileRequest, WrapperCompileDiagnostics> compileWrapper = _ =>
                WrapperCompileDiagnostics.Clean(
                    result: null,
                    Array.Empty<WrapperSliceDiagnostics>(),
                    Array.Empty<WrapperFileProvenance>());

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
