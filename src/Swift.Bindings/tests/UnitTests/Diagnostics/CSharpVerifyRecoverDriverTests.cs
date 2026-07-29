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
/// End-to-end coverage for the C# leg of the production <see cref="InEmissionDriver"/>: the same driver
/// the Swift wrapper loop uses, now with a C# verifier wired in. Each round compiles the Swift wrapper
/// first (stubbed clean here), then verifies the emitted C#; a C# compile error is attributed through the
/// C#-plane interval map to the exact emitted member, flows through the SAME monotonic denylist the Swift
/// loop uses, and the next round re-renders pristine — re-verifying Swift before it can converge. The
/// render itself (restore → rebuild → journal-undo → seed → emit → attribute) is the real production
/// path; only swiftc and the C# build are stubbed.
/// </summary>
/// <remarks>
/// <para>
/// This is the driver-level counterpart to <see cref="WrapperRecoveryControllerTests"/>'s pure-loop
/// coverage and <see cref="CSharpIntervalMapProvenanceStepTests"/>'s attribution-primitive coverage. It
/// pins the four seams those cannot reach through the real driver: (b) a positioned C# diagnostic lands
/// on the emitted member's recovery unit via the live per-render fragment map; (c) the withdrawal leaves
/// the binding through the one skip channel wearing the distinct C# wording ("Withdrawn by C# verify-
/// recover") in the settled render's tombstone; (d) the Swift wrapper is re-verified after a C#
/// withdrawal before the joint state settles; and the inconclusive verdict's two arms — round-0
/// pass-through vs fail-closed after a withdrawal.
/// </para>
/// <para>
/// The C# verifier stub keys its verdict on the denylist the current render was handed (observed through
/// the recording <see cref="RenderCompileAttribute"/> seam), so it models a real verifier: the culprit
/// member fails the C# compile while it is emitted, and the compile goes clean once the member is
/// withdrawn. The diagnostic's line/column are computed from the culprit fragment's real position in the
/// live render, so the attribution runs against the genuine emitted tiling, not a hand-placed span.
/// </para>
/// </remarks>
public class CSharpVerifyRecoverDriverTests : IDisposable
{
    private readonly List<string> _scratchDirs = new();

    public void Dispose()
    {
        foreach (var dir in _scratchDirs)
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { /* best effort */ }
        }
    }

    // ── round-0 joint convergence: both planes clean ────────────────────────────────────────────

    [Fact]
    public void CleanSwiftAndCleanCSharp_ConvergesJointly_HavingActuallyVerifiedBoth()
    {
        using var harness = new JointDriverHarness(this, CSharpBehavior.AlwaysClean);

        var result = WrapperRecoveryController.Run(harness);

        Assert.True(result.Converged);
        Assert.Empty(result.Denylist);
        Assert.Equal(1, result.Rounds);
        // Convergence is JOINT, not Swift-only: the C# verifier ran on the clean Swift round and agreed.
        Assert.Equal(1, harness.SwiftCompileCalls);
        Assert.Equal(1, harness.CSharpVerifyCalls);
        // The ledger's honest C#-compile proof: the verifier actually returned Clean, so the signal the
        // publication ledger reads is true here (obligations 9/11 proven by verifier).
        Assert.True(harness.CSharpVerifiedClean);
    }

    [Fact]
    public void InconclusiveCSharp_RoundZeroPassThrough_DoesNotClaimTheCSharpWasVerified()
    {
        // A round-0 inconclusive verdict converges (pass-through to the post-generate gate) but the C#
        // was NEVER proven clean — so the ledger signal must stay false. This is the exact path the
        // pre-fix code mislabeled proven from the mere presence of a verifier delegate.
        using var harness = new JointDriverHarness(this, CSharpBehavior.AlwaysInconclusive);

        var result = WrapperRecoveryController.Run(harness);

        Assert.True(result.Converged);
        Assert.Equal(1, harness.CSharpVerifyCalls); // the verifier ran...
        Assert.False(harness.CSharpVerifiedClean);  // ...but never reached Clean, so no proof is claimed.
    }

    // ── the joint fixed-point through the real driver: C# error → withdraw → re-verify Swift ────

    [Fact]
    public void CSharpCompileError_AttributesToItsMember_WithdrawnThenReVerifiesSwiftToConverge()
    {
        using var harness = new JointDriverHarness(this, CSharpBehavior.ErrorUntilWithdrawn);

        var result = WrapperRecoveryController.Run(harness);

        // The C# culprit is attributed to its emitted member and withdrawn; the second render re-verifies
        // Swift (clean) and then the emitted C# (now clean, the culprit gone) — the joint fixed-point.
        Assert.True(result.Converged);
        var withdrawn = Assert.Single(result.Denylist);
        Assert.True(
            WrapperRecoveryController.IsLeafRecoverable(withdrawn.Scope),
            "the withdrawn unit must be a leaf/accessor scope — the C#-plane attribution landed on a real member");
        Assert.Equal(2, result.Rounds);

        // Non-vacuity: the stub actually produced a C# compile error that was attributed and withdrawn,
        // rather than the loop converging because attribution silently found nothing.
        Assert.True(harness.CSharpErrored, "the C# verifier never reported the compile error under test");

        // (d) Swift was re-verified AFTER the C# withdrawal: two Swift compiles (round 0 and the
        // post-withdrawal round 1) and two C# verifies (round 0 error, round 1 clean).
        Assert.Equal(2, harness.SwiftCompileCalls);
        Assert.Equal(2, harness.CSharpVerifyCalls);

        // (c) One channel, C# wording: the settled render's tombstone for the withdrawn member reads as a
        // C# verify-recover withdrawal — distinct from the Swift-wrapper wording — proving the C#-plane
        // origin was recorded and reproduced by the next round's Gate-0 seed end to end.
        var settled = harness.ReadEmittedCSharp();
        Assert.Contains(EmitterFaultRecord.CSharpWithdrawalDetailsPrefix, settled, StringComparison.Ordinal);
        Assert.DoesNotContain(EmitterFaultRecord.WithdrawalDetailsPrefix, settled, StringComparison.Ordinal);
    }

    // ── inconclusive verdict: round-0 pass-through vs fail-closed after a withdrawal ────────────

    [Fact]
    public void InconclusiveCSharp_WithNothingWithdrawn_PassesThroughToConverge()
    {
        // Round 0, empty denylist: an inconclusive C# verdict cannot confirm a compile, but nothing has
        // been withdrawn, so there is no reduction to prove sound. The loop passes through to the
        // post-generate publication gate exactly as the Swift-only path does — it converges.
        using var harness = new JointDriverHarness(this, CSharpBehavior.AlwaysInconclusive);

        var result = WrapperRecoveryController.Run(harness);

        Assert.True(result.Converged);
        Assert.Empty(result.Denylist);
        Assert.Equal(1, result.Rounds);
    }

    [Fact]
    public void InconclusiveCSharp_AfterAWithdrawal_FailsClosed_RatherThanShippingAnUnprovenReduction()
    {
        // Once the loop HAS withdrawn a member, an inconclusive verdict can no longer prove the reduction
        // sound — shipping a reduced binding on an unproven C# compile would be an over-withdrawal we
        // cannot see. Rendering directly under a non-empty denylist (what round ≥1 does) must fail closed:
        // a global input-configuration classification the controller reads as a non-recoverable cause.
        using var harness = new JointDriverHarness(this, CSharpBehavior.AlwaysInconclusive);
        var denylist = harness.WithdrawableUnits();

        var attribution = harness.RenderCompileAttribute(denylist);

        Assert.NotNull(attribution);
        var decision = Assert.Single(attribution!.Diagnostics);
        Assert.Equal(AttributionKind.Classification, decision.Kind);
        // Same shape WrapperRecoveryControllerTests proves maps to WrapperRecoveryFailureCause
        // .InputConfiguration: a classification owned by the input configuration, no attributed culprit.
        Assert.Equal(CauseOwner.InputConfiguration, decision.Owner);
        Assert.Empty(attribution.Culprits);
    }

    // ── verifier throws: an infra fault must route exactly as an inconclusive verdict ───────────

    [Fact]
    public void ThrowingCSharpVerifier_WithNothingWithdrawn_PassesThroughToConverge()
    {
        // A verifier throw (a build-runner timeout, a project-emission IO fault) is an infrastructure
        // failure, not a C# verdict. With nothing withdrawn it must behave exactly like an inconclusive
        // result — pass through to the post-generate publication gate — instead of escaping the driver and
        // failing an otherwise healthy round-0 generation.
        using var harness = new JointDriverHarness(this, CSharpBehavior.ThrowsInfrastructureFailure);

        var result = WrapperRecoveryController.Run(harness);

        Assert.True(result.Converged);
        Assert.Empty(result.Denylist);
        Assert.Equal(1, result.Rounds);
        // Non-vacuity: the verifier really was invoked (and threw) — the driver caught it rather than the
        // scenario converging because the verifier was never reached.
        Assert.Equal(1, harness.CSharpVerifyCalls);
    }

    [Fact]
    public void ThrowingCSharpVerifier_AfterAWithdrawal_FailsClosed_JustLikeAnInconclusiveVerdict()
    {
        // The same throw, but rendered under a non-empty denylist (what round >= 1 does): folded into an
        // inconclusive verdict, it can no longer prove the reduction sound, so it must fail closed with the
        // same input-configuration classification an inconclusive result produces — never escape to fail
        // generation with an unclassified exception.
        using var harness = new JointDriverHarness(this, CSharpBehavior.ThrowsInfrastructureFailure);
        var denylist = harness.WithdrawableUnits();

        var attribution = harness.RenderCompileAttribute(denylist);

        Assert.NotNull(attribution);
        var decision = Assert.Single(attribution!.Diagnostics);
        Assert.Equal(AttributionKind.Classification, decision.Kind);
        Assert.Equal(CauseOwner.InputConfiguration, decision.Owner);
        Assert.Empty(attribution.Culprits);
    }

    // ── C#-only loop: the mode with no in-generation wrapper compile (Apple system frameworks) ──

    [Fact]
    public void CSharpOnlyLoop_CleanCSharp_ConvergesWithoutAnyWrapperCompile()
    {
        // The Apple system-framework direct shape: no wrapper plane at all, because that mode's wrapper
        // is built from the on-device SDK slice after emission returns. The loop still runs — round 0
        // renders and verifies the emitted C# — and converges on a clean verdict, having genuinely
        // verified the one plane it has.
        using var harness = new JointDriverHarness(this, CSharpBehavior.AlwaysClean, wrapperPlane: false);

        var result = WrapperRecoveryController.Run(harness);

        Assert.True(result.Converged);
        Assert.Empty(result.Denylist);
        Assert.Equal(1, result.Rounds);
        Assert.Equal(0, harness.SwiftCompileCalls);   // no wrapper plane was fabricated
        Assert.Equal(1, harness.CSharpVerifyCalls);
        Assert.True(harness.CSharpVerifiedClean);
    }

    [Fact]
    public void CSharpOnlyLoop_CompileError_WithdrawsTheCulpritInsteadOfFailingTheModule()
    {
        // The whole point of wiring the loop into this mode: a C# compile error that used to reach only
        // the single-shot publication gate (which fails the binding outright) is now attributed to the
        // emitted member, withdrawn, and the module re-rendered until the C# compiles clean.
        using var harness = new JointDriverHarness(
            this, CSharpBehavior.ErrorUntilWithdrawn, wrapperPlane: false);

        var result = WrapperRecoveryController.Run(harness);

        Assert.True(result.Converged);
        var withdrawn = Assert.Single(result.Denylist);
        Assert.True(
            WrapperRecoveryController.IsLeafRecoverable(withdrawn.Scope),
            "the withdrawn unit must be a leaf/accessor scope — the C#-plane attribution landed on a real member");
        Assert.Equal(2, result.Rounds);
        Assert.Equal(0, harness.SwiftCompileCalls);
        Assert.Equal(2, harness.CSharpVerifyCalls);

        // Non-vacuity: a real compile error drove the withdrawal.
        Assert.True(harness.CSharpErrored, "the C# verifier never reported the compile error under test");

        // The withdrawal leaves the binding through the same single skip channel, wearing the C# wording.
        var settled = harness.ReadEmittedCSharp();
        Assert.Contains(EmitterFaultRecord.CSharpWithdrawalDetailsPrefix, settled, StringComparison.Ordinal);
    }

    [Fact]
    public void CSharpOnlyLoop_UnattributableError_FailsClosed_RatherThanShippingAnUnprovenBinding()
    {
        // Recovery is not a licence to ship whatever compiles. A positioned-nowhere C# error cannot be
        // attributed to any member, and with no wrapper plane the bounded bisection has no vacuity
        // signal to search under, so the loop must fail the module closed — the same outcome this mode
        // has today, never a silent pass.
        using var harness = new JointDriverHarness(
            this, CSharpBehavior.UnattributableError, wrapperPlane: false);

        var result = WrapperRecoveryController.Run(harness);

        Assert.False(result.Converged);
        Assert.Empty(result.Denylist);
    }

    [Fact]
    public void CSharpOnlyLoop_InconclusiveAfterAWithdrawal_FailsClosed()
    {
        // Once a member has been withdrawn, an inconclusive verdict can no longer prove the reduction
        // sound. The C#-only path must fail closed exactly as the joint path does rather than shipping a
        // narrowed binding on an unverified compile.
        using var harness = new JointDriverHarness(
            this, CSharpBehavior.AlwaysInconclusive, wrapperPlane: false);
        var denylist = harness.WithdrawableUnits();

        var attribution = harness.RenderCompileAttribute(denylist);

        Assert.NotNull(attribution);
        var decision = Assert.Single(attribution!.Diagnostics);
        Assert.Equal(AttributionKind.Classification, decision.Kind);
        Assert.Equal(CauseOwner.InputConfiguration, decision.Owner);
        Assert.Empty(attribution.Culprits);
    }

    [Fact]
    public void DriverWithNeitherPlane_IsRefused_RatherThanCertifyingAnUnverifiedRender()
    {
        // A driver with no wrapper compile and no C# verifier would return null from every round — the
        // controller reads that as convergence, so the loop would certify whatever it rendered without
        // compiling anything. Construction must refuse it.
        Assert.Throws<ArgumentException>(() =>
            new JointDriverHarness(this, CSharpBehavior.AlwaysClean, wrapperPlane: false, csharpPlane: false));
    }

    // ── harness: the real driver with a C# verifier, driven as an IWrapperRecoveryDriver ────────

    private enum CSharpBehavior
    {
        /// <summary>The emitted C# compiles first try — the verifier always returns clean.</summary>
        AlwaysClean,

        /// <summary>One emitted member fails the C# compile until it is withdrawn, then clean.</summary>
        ErrorUntilWithdrawn,

        /// <summary>The verifier can never reach a verdict (a restore/infrastructure failure).</summary>
        AlwaysInconclusive,

        /// <summary>
        /// The verifier throws instead of returning a verdict — the shape a command-runner timeout or a
        /// project-emission IO fault takes. The driver must fold this into an Inconclusive result, not let
        /// it escape and fail generation.
        /// </summary>
        ThrowsInfrastructureFailure,

        /// <summary>
        /// A genuine C# compile error that no member owns — positioned in a file the fragment map does
        /// not tile (shared scaffolding). Attribution resolves nothing, which the controller must read as
        /// an unattributed error and fail closed.
        /// </summary>
        UnattributableError,
    }

    /// <summary>
    /// Wraps the production <see cref="InEmissionDriver"/> and records the denylist each render is handed,
    /// so the injected C# verifier can decide its verdict from the current render's withdrawal state —
    /// the same way a real MSBuild+SARIF verifier would see the emitted surface change as members are
    /// withdrawn. The report session is opened and closed around each render exactly as the module
    /// boundary does.
    /// </summary>
    private sealed class JointDriverHarness : IWrapperRecoveryDriver, IDisposable
    {
        private readonly string _scratch;
        private readonly ModuleDecl _module;
        private readonly TypeDatabase _typeDatabase;
        private readonly ModuleEmissionContext _context;
        private readonly InEmissionDriver _inner;
        private readonly CSharpBehavior _behavior;

        private IReadOnlySet<RecoveryUnitId> _currentDenylist = new HashSet<RecoveryUnitId>();
        private RecoveryUnitId? _target;

        public int SwiftCompileCalls { get; private set; }
        public int CSharpVerifyCalls { get; private set; }
        public bool CSharpErrored { get; private set; }

        /// <summary>The driver's honest C# verdict signal — true only when the verifier ran and returned
        /// Clean at convergence. The publication ledger reads exactly this.</summary>
        public bool CSharpVerifiedClean => _inner.CSharpVerifiedClean;

        /// <param name="wrapperPlane">
        /// Wire the Swift wrapper compile. False models a generation mode with no in-generation wrapper
        /// compile (the Apple system-framework direct path), where the loop runs the C# plane alone.
        /// </param>
        /// <param name="csharpPlane">Wire the C# verifier. False only to prove the no-plane refusal.</param>
        public JointDriverHarness(
            CSharpVerifyRecoverDriverTests owner,
            CSharpBehavior behavior,
            bool wrapperPlane = true,
            bool csharpPlane = true)
        {
            _behavior = behavior;
            _scratch = Path.Combine(Path.GetTempPath(), "swiftbind-csharploop-" + Guid.NewGuid().ToString("N"));
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

            // Always-clean Swift compile: the driver reaches its C# verifier every round. The counter
            // proves the Swift plane is re-verified after each C# withdrawal.
            Func<WrapperRecoveryCompileRequest, WrapperCompileDiagnostics> compileWrapper = _ =>
            {
                SwiftCompileCalls++;
                return WrapperCompileDiagnostics.Clean(
                    result: null,
                    Array.Empty<WrapperSliceDiagnostics>(),
                    Array.Empty<WrapperFileProvenance>());
            };

            var request = new WrapperRecoveryCompileRequest(
                _scratch,
                InternalTypeNames: null,
                ModuleNameForCollision: null,
                NestedTypesInCollidingClass: null,
                new DepModuleCollisionDetector.SlicedCollisionResult(
                    Array.Empty<string>(), Array.Empty<string>()));

            _inner = new InEmissionDriver(
                _module, _context, _typeDatabase, NullLogger.Instance,
                newEmitter: newEmitter,
                rebuildCollaborators: rebuildCollaborators,
                compileWrapper: wrapperPlane ? compileWrapper : null,
                request: request,
                preRender: ClearScratch,
                verifyCsharp: csharpPlane ? VerifyCsharp : null);
        }

        /// <inheritdoc />
        /// <remarks>Records the denylist so the C# verifier can react to it, and brackets the render in a
        /// report session exactly as the module boundary does.</remarks>
        public AttributionResult? RenderCompileAttribute(IReadOnlySet<RecoveryUnitId> denylist)
        {
            _currentDenylist = denylist;
            ReportCollector.Reset();
            try
            {
                return _inner.RenderCompileAttribute(denylist);
            }
            finally
            {
                ReportCollector.Complete();
                ReportCollector.Reset();
            }
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

        /// <summary>The concatenated emitted C# of the latest render still on disk.</summary>
        public string ReadEmittedCSharp() =>
            string.Concat(Directory
                .EnumerateFiles(_scratch, "*", SearchOption.AllDirectories)
                .Where(p => p.EndsWith(".cs", StringComparison.Ordinal))
                .OrderBy(p => p, StringComparer.Ordinal)
                .Select(File.ReadAllText));

        // The injected C# verifier. Its verdict is a pure function of the current render's denylist, so
        // it models a real verifier watching the emitted surface change as members are withdrawn. The
        // driver now hands the denylist to the verifier (so a caching layer can fingerprint it); the stub
        // keeps reading _currentDenylist, which RenderCompileAttribute set to the same set.
        private CSharpVerificationResult VerifyCsharp(IReadOnlySet<RecoveryUnitId> denylist)
        {
            CSharpVerifyCalls++;
            switch (_behavior)
            {
                case CSharpBehavior.AlwaysClean:
                    return Clean();

                case CSharpBehavior.AlwaysInconclusive:
                    return Inconclusive();

                case CSharpBehavior.ThrowsInfrastructureFailure:
                    // A real infrastructure fault: the external build's command runner times out. The
                    // driver, not this stub, is responsible for turning it into an Inconclusive verdict.
                    throw new TimeoutException("dotnet build exceeded the verification timeout");

                case CSharpBehavior.UnattributableError:
                    // A real compile error in a file the fragment map never tiled — no member owns it.
                    CSharpErrored = true;
                    return new CSharpVerificationResult(
                        CSharpVerificationOutcome.CompileErrors,
                        new[]
                        {
                            new CSharpCompileDiagnostic(
                                Id: "CS0246",
                                Severity: CSharpDiagnosticSeverity.Error,
                                FilePath: "SharedScaffolding.g.cs",
                                Line: 7, Column: 5, EndLine: 7, EndColumn: 9,
                                Message: "The type or namespace name 'Nope' could not be found"),
                        });

                default:
                    return ErrorUntilWithdrawn();
            }
        }

        private CSharpVerificationResult ErrorUntilWithdrawn()
        {
            // Lock onto the first withdrawable member that has an emitted C#-plane fragment, once, so the
            // scenario is a single deterministic culprit rather than whichever member surfaces first each
            // round.
            var withdrawable = WithdrawableUnits();
            _target ??= FindCSharpFragment(u => withdrawable.Contains(u))?.Unit;
            if (_target == null)
                return Clean();   // no withdrawable C# fragment — the non-vacuity assert will catch it

            // Once the culprit is withdrawn its member is a tombstone, not compilable code, so the C#
            // compile goes clean — the joint state can settle.
            if (_currentDenylist.Contains(_target.Value))
                return Clean();

            // The culprit is still emitted this round: fail the C# compile on it, positioned at its real
            // fragment in the LIVE render so the driver's interval-map attribution names its recovery unit.
            var at = FindCSharpFragment(u => u.Equals(_target.Value));
            if (at == null)
                return Clean();

            CSharpErrored = true;
            return new CSharpVerificationResult(
                CSharpVerificationOutcome.CompileErrors,
                new[]
                {
                    new CSharpCompileDiagnostic(
                        Id: "CS0103",
                        Severity: CSharpDiagnosticSeverity.Error,
                        FilePath: at.Value.File,
                        Line: at.Value.Line,
                        Column: at.Value.Column,
                        EndLine: at.Value.Line,
                        EndColumn: at.Value.Column + 1,
                        Message: "The name 'Nope' does not exist in the current context"),
                });
        }

        // Locates a C#-plane fragment in the current render whose owning recovery unit matches the
        // predicate, returning its file leaf name and 1-based UTF-16 line/column — the shape a Roslyn/
        // SARIF diagnostic carries.
        private (RecoveryUnitId Unit, string File, int Line, int Column)? FindCSharpFragment(
            Func<RecoveryUnitId, bool> match)
        {
            var set = _context.FragmentSet;
            if (set == null)
                return null;

            foreach (var kv in set.Files.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                foreach (var interval in kv.Value.Intervals)
                {
                    var fragment = interval.Fragment;
                    if (fragment.Plane != OutputPlane.CSharp)
                        continue;
                    if (!match(fragment.Owner.Unit))
                        continue;

                    var (line, column) = OffsetToLineColumn(kv.Key, interval.Start);
                    return (fragment.Owner.Unit, kv.Key, line, column);
                }
            }
            return null;
        }

        // Converts a fragment's character offset to a 1-based (line, UTF-16 column) against the on-disk
        // file — identical bytes to the map's content on the loop path (no post-publish rewrite).
        private (int Line, int Column) OffsetToLineColumn(string leafName, int offset)
        {
            var path = Directory
                .EnumerateFiles(_scratch, leafName, SearchOption.AllDirectories)
                .First();
            var content = File.ReadAllText(path);

            int line = 1, column = 1;
            for (int i = 0; i < offset && i < content.Length; i++)
            {
                if (content[i] == '\n')
                {
                    line++;
                    column = 1;
                }
                else
                {
                    column++;
                }
            }
            return (line, column);
        }

        private static CSharpVerificationResult Clean() =>
            new(CSharpVerificationOutcome.Clean, Array.Empty<CSharpCompileDiagnostic>());

        private static CSharpVerificationResult Inconclusive() =>
            new(
                CSharpVerificationOutcome.Inconclusive,
                new[]
                {
                    new CSharpCompileDiagnostic(
                        Id: "NU1101",
                        Severity: CSharpDiagnosticSeverity.Error,
                        FilePath: null,
                        Line: 0, Column: 0, EndLine: 0, EndColumn: 0,
                        Message: "Unable to find package"),
                },
                "build failed before the C# compile with 1 restore/infrastructure error(s) (first: NU1101)");

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
