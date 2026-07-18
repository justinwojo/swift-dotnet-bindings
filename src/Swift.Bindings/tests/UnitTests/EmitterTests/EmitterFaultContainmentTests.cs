// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Proves the containment contract: when the emitter throws while lowering one declaration, the
/// generation still succeeds, the faulting declaration is tombstoned, and everything else is
/// byte-for-byte what it would have been anyway.
/// </summary>
/// <remarks>
/// <para>
/// The oracle is deliberately <b>not</b> "the output looks reasonable" and deliberately not "the
/// declaration was never there". It is: <i>a run where the emitter throws on X must produce exactly
/// the same bytes as a run where X was denied before emission started</i>. That equality is the whole
/// property. Anything weaker would let a contained fault leave a residue — a dedup counter one higher,
/// a collision suffix shifted, a vtable slot missing — in an unrelated part of the module, and those
/// are precisely the failures that survive a compile and only surface at runtime on a device.
/// </para>
/// <para>
/// The faults are injected rather than harvested from real defects. A test written against a real
/// emitter bug stops testing recovery the moment the bug is fixed; an injected fault keeps proving it
/// forever.
/// </para>
/// </remarks>
public class EmitterFaultContainmentTests : IDisposable
{
    private readonly List<string> _scratchDirs = new();

    public void Dispose()
    {
        foreach (var dir in _scratchDirs)
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { /* best effort */ }
        }
    }

    // ── the regeneration-consistency oracle ────────────────────────────────────────────────────

    /// <summary>
    /// A plain method. <c>Registry</c> declares three <c>register</c> overloads that collide on their
    /// projected C# name, so the poisoned member sits in the middle of the collision-suffix allocator
    /// — the machinery most likely to shift its siblings if an abandoned attempt left residue behind.
    /// </summary>
    [Fact]
    public void FaultInAMethodEmitter_MatchesARunThatDeniedItUpFront()
    {
        AssertContainedRunMatchesPreDeniedRun(
            module => DeclIdFactory.ForMethod(FindMethod(module, "Registry", "register", parameterName: "third")));
    }

    /// <summary>A property accessor: a different dispatch family, reached through the accessor group.</summary>
    [Fact]
    public void FaultInAPropertyAccessorEmitter_MatchesARunThatDeniedItUpFront()
    {
        AssertContainedRunMatchesPreDeniedRun(
            module => DeclIdFactory.ForProperty(FindProperty(module, "Registry", "name")));
    }

    /// <summary>
    /// Type infrastructure. Denying a whole type is the case where a residue would be most visible:
    /// every member of the type disappears at once, and any sibling type that references it has to
    /// degrade the same way it would in a clean run.
    /// </summary>
    [Fact]
    public void FaultInTypeInfrastructure_MatchesARunThatDeniedItUpFront()
    {
        AssertContainedRunMatchesPreDeniedRun(
            module => DeclIdFactory.ForType(FindType(module, "Box")));
    }

    /// <summary>
    /// The shared body of the three tests above, and the oracle the whole mechanism rests on.
    /// </summary>
    /// <remarks>
    /// Emits twice: once with the emitter made to throw on the subject, then once with that exact
    /// denial seeded from the start so no exception is ever thrown. The two output sets must be
    /// identical, file names and bytes. The reference run is seeded with the faults the contained
    /// run actually recorded rather than a synthetic stand-in, because a fault record carries the
    /// captured exception fingerprint into the tombstone text — seeding a hand-built record would
    /// compare two different denial *reasons* and fail for a reason that has nothing to do with
    /// containment. Holding the denylist identical is what isolates the property under test: that
    /// re-emitting after a fault adds nothing beyond the denial itself.
    /// </remarks>
    private void AssertContainedRunMatchesPreDeniedRun(Func<ModuleDecl, DeclId> selectSubject)
    {
        var contained = Emit(injectFaultOn: selectSubject);
        var reference = Emit(seedFaults: contained.Poison.Faults);

        AssertOutputSetsIdentical(reference.Files, contained.Files);

        // Guards the equality above from passing vacuously: if the subject had emitted in both runs,
        // the outputs would also match and the test would prove nothing.
        Assert.Single(contained.Poison.Faults);
        Assert.Contains(
            "Unsupported",
            string.Concat(contained.Files.Where(f => f.Key.EndsWith(".cs", StringComparison.Ordinal)).Select(f => f.Value)),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Denying a declaration must cost exactly that declaration. <c>Registry</c> declares a static and
    /// an instance <c>count</c>, which contend for a single C# <c>Count</c>; poisoning the one that
    /// wins that contention has to hand the name to the other, not consume it on the way out.
    /// </summary>
    /// <remarks>
    /// The failure this pins is quiet and expensive: a seam that denies a declaration only at the point
    /// of dispatch lets it first walk through the dedup registries and claim its C# name, so the
    /// sibling that projects to the same name is dropped as a duplicate of a member that was never
    /// emitted. One faulted declaration then removes two pieces of API, and the binding still compiles
    /// — nothing downstream can tell the difference between a member the generator withdrew and one the
    /// library never had. The oracle above cannot see this on its own, because the reference run denies
    /// the same declaration through the same path and loses the same sibling.
    /// </remarks>
    [Fact]
    public void DenyingAPropertyDoesNotConsumeTheCSharpNameItsSiblingNeeds()
    {
        int CountMembers(EmissionOutcome outcome) => outcome.Files
            .Where(f => f.Key.EndsWith(".cs", StringComparison.Ordinal))
            .Sum(f => System.Text.RegularExpressions.Regex.Matches(f.Value, @"\bCount\s*(?:\{|=>)").Count);

        var clean = Emit();
        var contained = Emit(injectFaultOn: module =>
            DeclIdFactory.ForProperty(FindType(module, "Registry").Properties.First(p => p.Name == "count")));

        // Anti-vacuity: the fixture has to actually be emitting a `Count` for its loss to mean anything.
        Assert.True(CountMembers(clean) > 0, "fixture emitted no Count member, so the test proves nothing");

        // The poisoned property is gone; the sibling that was waiting behind it on the same name is not.
        Assert.Single(contained.Poison.Faults);
        Assert.Equal(CountMembers(clean), CountMembers(contained));
    }

    // ── tombstone + report ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The fault must reach the consumer-facing channels, not just be swallowed quietly. A contained
    /// fault that leaves no trace is worse than a crash: the binding ships with a hole nobody knows
    /// about.
    /// </summary>
    [Fact]
    public void ContainedFault_RecordsTheSubjectWithTheCapturedExceptionFingerprint()
    {
        var result = Emit(injectFaultOn: module =>
            DeclIdFactory.ForMethod(FindMethod(module, "Registry", "register", parameterName: "third")));

        var fault = Assert.Single(result.Poison.Faults);

        // An emitter fault is always a generator defect discovered at emission — never something the
        // library did, and never something an earlier stage could have predicted.
        var attribution = SkipCauseClassifier.Classify(SkipReason.EmitterFault);
        Assert.Equal(CauseOwner.Generator, attribution.Owner);
        Assert.Equal(RecoveryStage.Emit, attribution.Stage);
        Assert.Equal(AttributionConfidence.High, attribution.Confidence);

        // The fingerprint is what makes the tombstone actionable — it names the exception type and the
        // emitter frame that threw, so a maintainer can go straight to the defect.
        Assert.Equal(nameof(InvalidOperationException), fault.ExceptionType);
        Assert.Contains("injected", fault.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(fault.Fingerprint));

        Assert.Contains(
            SkipReason.EmitterFault.ToString(),
            string.Concat(result.Files.Where(f => f.Key.EndsWith(".cs", StringComparison.Ordinal)).Select(f => f.Value)),
            StringComparison.Ordinal);
    }

    // ── the discarded attempt leaves nothing behind ────────────────────────────────────────────

    /// <summary>
    /// Emission stamps facts back onto the frozen type database, and those stamps are the one channel
    /// through which a discarded attempt could leak into the attempt that replaces it. A denied run
    /// and a contained run must leave the database in the same state — if the abandoned attempt's
    /// stamps survived, the retry would emit against a database no clean run ever sees.
    /// </summary>
    [Fact]
    public void DiscardedAttempt_LeavesNoEmissionFactResidueInTheTypeDatabase()
    {
        DeclId Subject(ModuleDecl module) => DeclIdFactory.ForType(FindType(module, "Box"));

        var contained = Emit(injectFaultOn: Subject);
        var reference = Emit(seedFaults: contained.Poison.Faults);

        Assert.Equal(
            DescribeEmissionFacts(reference.TypeDatabase, reference.Module),
            DescribeEmissionFacts(contained.TypeDatabase, contained.Module));
    }

    /// <summary>
    /// Guards the residue test above. If nothing ever stamped an emission fact, "no residue" would be
    /// trivially true and the test would be inert.
    /// </summary>
    [Fact]
    public void FixtureEmission_ActuallyStampsEmissionFacts()
    {
        var clean = Emit();

        Assert.NotEmpty(DescribeEmissionFacts(clean.TypeDatabase, clean.Module));
    }

    // ── non-recoverable classes are never converted to skips ───────────────────────────────────

    /// <summary>
    /// Cancellation means the caller asked to stop. Turning it into a tombstone would produce a
    /// binding that silently dropped whatever happened to be emitting when the token fired — a
    /// different, quieter kind of wrong than simply stopping.
    /// </summary>
    [Fact]
    public void CancellationDuringEmission_PropagatesInsteadOfBeingContained()
    {
        Assert.Throws<OperationCanceledException>(() => Emit(
            injectFaultOn: module => DeclIdFactory.ForType(FindType(module, "Box")),
            injected: () => new OperationCanceledException()));
    }

    /// <summary>
    /// An output-IO failure means the generator cannot write. Denying a declaration does not make the
    /// disk writable, so retrying would burn the attempt budget and then fail anyway with the fault
    /// misattributed to whichever declaration happened to be in flight.
    /// </summary>
    [Fact]
    public void IoFailureDuringEmission_PropagatesInsteadOfBeingContained()
    {
        Assert.Throws<IOException>(() => Emit(
            injectFaultOn: module => DeclIdFactory.ForType(FindType(module, "Box")),
            injected: () => new IOException("disk went away")));
    }

    /// <summary>
    /// Failures of the machine, of the operator's intent, or of the output channel. Denying a
    /// declaration cannot make any of them go away, so retrying only wastes the little headroom
    /// that is left and buries the real cause under a containment warning.
    /// </summary>
    public static TheoryData<Exception> NonRecoverableShapes() => new()
    {
        new OutOfMemoryException(),
        new StackOverflowException(),
        new OperationCanceledException(),
        new IOException("disk went away"),
        new UnauthorizedAccessException(),
    };

    /// <summary>
    /// The ordinary shapes of an emitter defect: a bad cast, an absent key, a null field. Every one
    /// of these is a bug in lowering one declaration, which is exactly what denying it routes around.
    /// </summary>
    public static TheoryData<Exception> ContainableShapes() => new()
    {
        new InvalidOperationException(),
        new NullReferenceException(),
        new IndexOutOfRangeException(),
        new KeyNotFoundException(),
        new FormatException(),
    };

    [Theory]
    [MemberData(nameof(NonRecoverableShapes))]
    public void NonRecoverableExceptionClasses_AreNeverConvertedToSkips(Exception exception)
    {
        Assert.True(NonRecoverableFault.Test(exception), $"{exception.GetType().Name} must not be containable.");
    }

    [Theory]
    [MemberData(nameof(ContainableShapes))]
    public void OrdinaryEmitterDefectShapes_AreContainable(Exception exception)
    {
        Assert.False(NonRecoverableFault.Test(exception), $"{exception.GetType().Name} is an ordinary defect shape.");
    }

    // ── the cap ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A fault the denylist cannot route around must fail the module loudly rather than spin. Throwing
    /// on every declaration is the pathological shape: each attempt discovers a new subject, so the
    /// list keeps growing and only the cap stops it.
    /// </summary>
    [Fact]
    public void FaultOnEveryDeclaration_FailsTheModuleAtTheAttemptCap()
    {
        var thrown = Assert.Throws<EmitterFaultLimitException>(() => Emit(
            injectFaultOn: _ => default,
            faultEverything: true));

        Assert.NotEmpty(thrown.Faults);
        Assert.Contains("SWIFTBIND110", thrown.Message, StringComparison.Ordinal);
    }

    // ── the happy path is untouched ────────────────────────────────────────────────────────────

    /// <summary>
    /// The containment loop wraps every emission, so its cost has to be zero when nothing throws.
    /// This is the regression that would bite every consumer rather than only the broken ones.
    /// </summary>
    [Fact]
    public void WithNoFaultInjected_ContainedEmissionProducesTheSameBytesAsPlainEmission()
    {
        var contained = Emit();
        var plain = EmitWithoutContainment();

        AssertOutputSetsIdentical(contained.Files, plain);
        Assert.True(contained.Poison.IsEmpty);
    }

    // ── harness ────────────────────────────────────────────────────────────────────────────────

    private sealed record EmissionOutcome(
        Dictionary<string, string> Files,
        EmitterPoisonList Poison,
        TypeDatabase TypeDatabase,
        ModuleDecl Module);

    /// <summary>
    /// Runs the real containment loop over the shared fixture module. <paramref name="seedFaults"/>
    /// denies declarations before emission starts (producing the reference render);
    /// <paramref name="injectFaultOn"/> makes the emitter throw when it reaches one.
    /// </summary>
    private EmissionOutcome Emit(
        Func<ModuleDecl, DeclId>? injectFaultOn = null,
        IReadOnlyList<EmitterFaultRecord>? seedFaults = null,
        Func<Exception>? injected = null,
        bool faultEverything = false)
    {
        var scratch = NewScratchDir();
        var moduleDecl = FixtureModuleFactory.BuildModule("ContainmentFixture");
        var typeDatabase = FixtureModuleFactory.BuildTypeDatabase(moduleDecl);
        var emissionContext = new ModuleEmissionContext();

        EmitterPoisonList? seed = null;
        if (seedFaults is { Count: > 0 })
        {
            // A DeclId is a canonical string, so records captured against one fixture instance
            // deny the same declarations in the freshly built one this run emits.
            seed = new EmitterPoisonList();
            foreach (var fault in seedFaults)
            {
                seed.Record(fault);
            }
        }

        var target = injectFaultOn?.Invoke(moduleDecl);
        var makeException = injected ?? (() => new InvalidOperationException("injected emitter fault"));

        IDisposable? hook = null;
        if (injectFaultOn is not null)
        {
            hook = EmitterFaultInjector.Install(subject =>
                faultEverything || string.Equals(subject.Canonical, target!.Value.Canonical, StringComparison.Ordinal)
                    ? makeException()
                    : null);
        }

        try
        {
            var poison = ContainedModuleEmission.Run(
                moduleDecl,
                emissionContext,
                typeDatabase,
                NullLogger.Instance,
                newEmitter: () => new StringEmitter(scratch, typeDatabase, new NullLoggerFactory()),
                seed: seed);

            return new EmissionOutcome(ReadOutput(scratch), poison, typeDatabase, moduleDecl);
        }
        finally
        {
            hook?.Dispose();
            ReportCollector.Complete();
            ReportCollector.Reset();
        }
    }

    /// <summary>
    /// Emission without the containment loop — the shape the generator had before poison-and-regenerate
    /// existed. The byte-identity baseline for "containment costs nothing on a healthy module".
    /// </summary>
    private Dictionary<string, string> EmitWithoutContainment()
    {
        var scratch = NewScratchDir();
        var moduleDecl = FixtureModuleFactory.BuildModule("ContainmentFixture");
        var typeDatabase = FixtureModuleFactory.BuildTypeDatabase(moduleDecl);

        ReportCollector.Reset();
        ReportCollector.Start(moduleDecl);
        AppleSupplementReferences.Reset();
        try
        {
            new StringEmitter(scratch, typeDatabase, new NullLoggerFactory())
                .EmitModule(moduleDecl, new ModuleEmissionContext());
        }
        finally
        {
            ReportCollector.Complete();
            ReportCollector.Reset();
        }

        return ReadOutput(scratch);
    }

    private string NewScratchDir()
    {
        var scratch = Path.Combine(Path.GetTempPath(), "swiftbind-containment-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        _scratchDirs.Add(scratch);
        return scratch;
    }

    /// <summary>
    /// The whole output tree keyed by path relative to the scratch root, so the comparison covers
    /// which files exist as well as what is in them.
    /// </summary>
    private static Dictionary<string, string> ReadOutput(string scratch) =>
        Directory.EnumerateFiles(scratch, "*", SearchOption.AllDirectories)
            .ToDictionary(
                path => Path.GetRelativePath(scratch, path),
                File.ReadAllText,
                StringComparer.Ordinal);

    /// <summary>
    /// Renders exactly the facts <c>ApplyEmissionResult</c> can stamp — the emitted member count, the
    /// surviving class methods, the metadata-P/Invoke flag and the collision-renamed C# type name — as
    /// a stable string. Those four ARE the post-freeze mutation surface, so equality over this
    /// rendering is equality over everything a discarded attempt could have left behind.
    /// </summary>
    private static string DescribeEmissionFacts(TypeDatabase typeDatabase, ModuleDecl moduleDecl)
    {
        var lines = new List<string>();
        foreach (var type in moduleDecl.Types.OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            if (type.SwiftTypeName is null || !typeDatabase.TryGetTypeRecord(type.SwiftTypeName, out var record))
                continue;

            var emittedMethods = record.EmittedClassMethods is null
                ? "<unset>"
                : string.Join(";", record.EmittedClassMethods
                    .Select(m => $"{m.SwiftName}->{m.CSharpName}({string.Join(",", m.ParameterSwiftTypes)})")
                    .OrderBy(m => m, StringComparer.Ordinal));

            lines.Add(
                $"{type.Name}|csharp={record.CSharpTypeName}|members={record.EmittedMemberCount?.ToString() ?? "<unset>"}" +
                $"|metadataPInvoke={record.EmittedMetadataPInvoke?.ToString() ?? "<unset>"}|methods={emittedMethods}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static MethodDecl FindMethod(ModuleDecl module, string typeName, string methodName, string parameterName) =>
        FindType(module, typeName).Methods.Single(m =>
            m.Name == methodName && m.CSSignature.Any(p => p.Name == parameterName));

    private static PropertyDecl FindProperty(ModuleDecl module, string typeName, string propertyName) =>
        FindType(module, typeName).Properties.Single(p => p.Name == propertyName);

    private static TypeDecl FindType(ModuleDecl module, string typeName) =>
        module.Types.Single(t => t.Name == typeName);

    private static void AssertOutputSetsIdentical(
        Dictionary<string, string> expected, Dictionary<string, string> actual)
    {
        Assert.Equal(
            expected.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray(),
            actual.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());

        foreach (var name in expected.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            if (string.Equals(expected[name], actual[name], StringComparison.Ordinal))
                continue;

            Assert.Fail($"'{name}' differs.{Environment.NewLine}{DescribeFirstDifference(expected[name], actual[name])}");
        }
    }

    private static void AssertOutputSetsIdentical(
        Dictionary<string, string> expected, EmissionOutcome actual) =>
        AssertOutputSetsIdentical(expected, actual.Files);

    private static string DescribeFirstDifference(string a, string b)
    {
        var limit = Math.Min(a.Length, b.Length);
        var offset = 0;
        while (offset < limit && a[offset] == b[offset])
            offset++;

        const int window = 200;
        var start = Math.Max(0, offset - window / 2);
        string Window(string s) => s.Substring(start, Math.Min(window, s.Length - start)).Replace("\n", "\\n");

        return $"  first difference at char {offset} (lengths {a.Length} vs {b.Length}){Environment.NewLine}" +
               $"  expected: …{Window(a)}…{Environment.NewLine}" +
               $"  actual:   …{Window(b)}…";
    }
}
