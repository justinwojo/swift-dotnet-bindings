// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// The binding report has to account for every public name the generator invented. These tests pin
/// that for the case-only rename arm across a CONTAINED RETRY — the path where one declaration
/// faults, the module is rewound, and the whole render runs a second time.
/// </summary>
/// <remarks>
/// <para>
/// The rename decision is made by a pre-emission pass that stamps the chosen name onto the
/// declaration and publishes a row to the report in the same step — but it publishes only when the
/// stamp actually changes, because the pass reaches a protocol twice (once as a protocol, once as a
/// type) and booking one decision as two would make the ledger a fact about the traversal instead of
/// about the surface. A retry restarts the report session, so a stamp that survived the rewind makes
/// the second pass a silent no-op: the disambiguated name still ships, and nothing in the report says
/// where it came from. That is a hole no compile can see, and it is exactly what an artifact gate
/// reading the ledger is there to catch.
/// </para>
/// <para>
/// The oracle is therefore a PAIRING, not a count: every case-only name in the emitted C# must have a
/// ledger row, whether the render settled on its first attempt or its second.
/// </para>
/// </remarks>
public class CaseOnlyRenameRetryTests : IDisposable
{
    private readonly List<string> _scratchDirs = new();

    public void Dispose()
    {
        foreach (var dir in _scratchDirs)
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { /* best effort */ }
        }
    }

    /// <summary>
    /// The control. One attempt, no withdrawal: the pass stamps, publishes, and the name and its row
    /// agree. Anchors what the retry case below has to reproduce.
    /// </summary>
    [Fact]
    public void SingleAttempt_PublishesALedgerRowForEveryCaseOnlyNameItEmits()
    {
        var outcome = Emit();

        Assert.True(outcome.Poison.IsEmpty, "no fault was injected, so the render must settle on its first attempt");
        AssertCaseOnlyNamesAreAccountedFor(outcome);
    }

    /// <summary>
    /// The regression. A withdrawal elsewhere in the module forces a second attempt, and the pre-pass
    /// stamp must not survive the rewind — otherwise the retry re-emits the disambiguated name while
    /// its ledger row is lost with the discarded attempt's report session.
    /// </summary>
    [Fact]
    public void ContainedRetry_StillPublishesALedgerRowForEveryCaseOnlyNameItEmits()
    {
        var outcome = Emit(injectFaultOn: module =>
            DeclIdFactory.ForMethod(FindMethod(module, "Registry", "register", parameterName: "third")));

        // Anti-vacuity: without a recorded fault the render never ran a second attempt, so the
        // assertion below would be the single-attempt control in disguise.
        Assert.Single(outcome.Poison.Faults);
        AssertCaseOnlyNamesAreAccountedFor(outcome);
    }

    /// <summary>
    /// The retry must not double-count either: rerunning the pass over a rewound tree republishes the
    /// same decisions, not a second copy of them. A ledger that grows per attempt would report two
    /// renames for one member and misstate the surface just as badly as losing the row.
    /// </summary>
    [Fact]
    public void ContainedRetry_LeavesTheCaseOnlyLedgerIdenticalToACleanRun()
    {
        var clean = Emit();
        var retried = Emit(injectFaultOn: module =>
            DeclIdFactory.ForMethod(FindMethod(module, "Registry", "register", parameterName: "third")));

        Assert.Single(retried.Poison.Faults);
        Assert.Equal(DescribeCaseOnlyLedger(clean), DescribeCaseOnlyLedger(retried));
    }

    /// <summary>
    /// The mechanism, isolated from emission. The decl snapshot has to put the pre-pass stamp back,
    /// because the pass short-circuits on an unchanged stamp — so a stamp that survives the rewind
    /// suppresses both the decision and the row it publishes.
    /// </summary>
    [Fact]
    public void DeclSnapshotRestore_LetsTheCaseOnlyPassReStampAndRePublish()
    {
        var module = BuildCaseOnlyModule("SnapshotRetryFixture");
        var typeDatabase = FixtureModuleFactory.BuildTypeDatabase(module);
        var snapshot = DeclEmissionStateSnapshot.Capture(module);

        var firstPassRows = RunPassAndCollectRows(module, typeDatabase);
        Assert.NotEmpty(firstPassRows);
        // Only the loser is stamped — the declaration-order elder keeps the natural projection and
        // needs no stamp at all, which is why "every property is stamped" would be the wrong oracle.
        Assert.NotNull(CaseOnlyLoser(module).CaseDisambiguatedName);

        snapshot.Restore();

        // The rewind is what re-arms the pass: the stamp is back to its pre-attempt value.
        Assert.All(CaseOnlyProperties(module), p => Assert.Null(p.CaseDisambiguatedName));

        var secondPassRows = RunPassAndCollectRows(module, typeDatabase);
        Assert.Equal(firstPassRows, secondPassRows);
    }

    /// <summary>
    /// Guards the test above from proving nothing. Without the rewind the pass IS a no-op the second
    /// time — that is the defect — so this pins the short-circuit as the reason the rewind matters,
    /// rather than assuming it.
    /// </summary>
    [Fact]
    public void WithoutARestore_TheCaseOnlyPassRepublishesNothing()
    {
        var module = BuildCaseOnlyModule("NoRestoreFixture");
        var typeDatabase = FixtureModuleFactory.BuildTypeDatabase(module);

        Assert.NotEmpty(RunPassAndCollectRows(module, typeDatabase));
        Assert.Empty(RunPassAndCollectRows(module, typeDatabase));
    }

    // ── oracle ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Every case-only name the render chose must be declared in the emitted C# AND carry a ledger
    /// row naming the Swift member it came from. Reading both sides keeps the assertion semantic:
    /// it fails if the row is lost, and equally if a row is published for a name nothing emitted.
    /// </summary>
    private static void AssertCaseOnlyNamesAreAccountedFor(EmissionOutcome outcome)
    {
        var report = outcome.Report;
        Assert.NotNull(report);

        var emittedNames = CaseOnlyProperties(outcome.Module)
            .Select(p => p.CaseDisambiguatedName)
            .Where(n => n is not null)
            .Select(n => n!)
            .ToList();

        // The fixture declares one colliding pair, so the pass must have moved exactly one of them.
        Assert.Single(emittedNames);

        var csharp = string.Concat(outcome.Files
            .Where(f => f.Key.EndsWith(".cs", StringComparison.Ordinal))
            .OrderBy(f => f.Key, StringComparer.Ordinal)
            .Select(f => f.Value));

        foreach (var name in emittedNames)
        {
            // The name has to be DECLARED, not merely mentioned: a substring match would also be
            // satisfied by a comment or a tombstone for a member that never bound.
            Assert.Matches($@"\bstring\s+{Regex.Escape(name)}\b", csharp);

            var row = Assert.Single(
                report!.CaseOnlyRenames,
                r => string.Equals(r.EmittedName, name, StringComparison.Ordinal));
            Assert.Equal(CaseOnlySwiftName, row.SwiftName);
            Assert.Equal("Registry", row.DeclaringName);
            Assert.Equal("Url", row.NaturalName);
            Assert.Equal(nameof(NameCollisionScheme.CaseOnlyMemberCollision), row.Scheme);
        }
    }

    private static string DescribeCaseOnlyLedger(EmissionOutcome outcome) =>
        string.Join(Environment.NewLine, Render(outcome.Report?.CaseOnlyRenames));

    /// <summary>
    /// Runs the pre-pass alone against a fresh report session and returns its published rows,
    /// rendered — the report items are reference-equal only, so comparing the renderings is what
    /// makes "the same decisions came back" an assertion about content.
    /// </summary>
    private static IReadOnlyList<string> RunPassAndCollectRows(ModuleDecl module, TypeDatabase typeDatabase)
    {
        ReportCollector.Reset();
        ReportCollector.Start(module);
        try
        {
            CaseOnlyCollisionPass.Precompute(module, typeDatabase);
            return Render(ReportCollector.Complete()?.CaseOnlyRenames);
        }
        finally
        {
            ReportCollector.Reset();
        }
    }

    private static IReadOnlyList<string> Render(IEnumerable<CaseOnlyRenameItem>? rows) =>
        (rows ?? Enumerable.Empty<CaseOnlyRenameItem>())
            .Select(r => $"{r.DeclaringName}.{r.SwiftName}->{r.EmittedName}|{r.NaturalName}|{r.Scheme}")
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

    // ── fixture ────────────────────────────────────────────────────────────────────────────────

    /// <summary>The Swift spelling that loses the natural projection to its declaration-order elder.</summary>
    private const string CaseOnlySwiftName = "URL";

    private static IEnumerable<PropertyDecl> CaseOnlyProperties(ModuleDecl module) =>
        module.Types.Single(t => t.Name == "Registry").Properties
            .Where(p => p.Name is "url" or CaseOnlySwiftName);

    /// <summary>The half of the pair the pass has to move, so the half that carries the stamp.</summary>
    private static PropertyDecl CaseOnlyLoser(ModuleDecl module) =>
        CaseOnlyProperties(module).Single(p => p.Name == CaseOnlySwiftName);

    /// <summary>
    /// The shared containment fixture plus one case-only pair on <c>Registry</c>. Building on the
    /// shared module rather than a bespoke one keeps the render broad — the pair has to survive the
    /// same collision allocators, dedup registries and file splitting every other member goes through.
    /// </summary>
    private static ModuleDecl BuildCaseOnlyModule(string moduleName)
    {
        var module = FixtureModuleFactory.BuildModule(moduleName);
        var registry = module.Types.Single(t => t.Name == "Registry");
        var stringSpec = new NamedTypeSpec("Swift.String");

        // Declaration order is load-bearing: the first spelling keeps the natural `Url`, the second
        // is the one the pass has to move and publish.
        foreach (var name in new[] { "url", CaseOnlySwiftName })
        {
            var property = TestDecls.Property(name, stringSpec, module: moduleName);
            property.ParentDecl = registry;
            property.ModuleDecl = module;
            foreach (var accessor in property.Accessors)
            {
                accessor.Method.ParentDecl = registry;
                accessor.Method.ModuleDecl = module;
            }
            registry.Properties.Add(property);
        }

        return module;
    }

    // ── harness ────────────────────────────────────────────────────────────────────────────────

    private sealed record EmissionOutcome(
        Dictionary<string, string> Files,
        EmitterPoisonList Poison,
        BindingReport? Report,
        ModuleDecl Module);

    /// <summary>
    /// Runs the real containment loop over the case-only fixture and returns the settled render
    /// together with the report the run actually produced.
    /// </summary>
    private EmissionOutcome Emit(Func<ModuleDecl, DeclId>? injectFaultOn = null)
    {
        var scratch = NewScratchDir();
        var moduleDecl = BuildCaseOnlyModule("CaseOnlyRetryFixture");
        var typeDatabase = FixtureModuleFactory.BuildTypeDatabase(moduleDecl);
        var emissionContext = new ModuleEmissionContext();

        var target = injectFaultOn?.Invoke(moduleDecl);
        IDisposable? hook = injectFaultOn is null
            ? null
            : EmitterFaultInjector.Install(subject =>
                string.Equals(subject.Canonical, target!.Value.Canonical, StringComparison.Ordinal)
                    ? new InvalidOperationException("injected emitter fault")
                    : null);

        try
        {
            var poison = ContainedModuleEmission.Run(
                moduleDecl,
                emissionContext,
                typeDatabase,
                NullLogger.Instance,
                newEmitter: () => new StringEmitter(scratch, typeDatabase, new NullLoggerFactory()));

            // Read the report the settled attempt left behind, before the session is discarded.
            return new EmissionOutcome(ReadOutput(scratch), poison, ReportCollector.Complete(), moduleDecl);
        }
        finally
        {
            hook?.Dispose();
            ReportCollector.Reset();
        }
    }

    private string NewScratchDir()
    {
        var scratch = Path.Combine(Path.GetTempPath(), "swiftbind-caseonly-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        _scratchDirs.Add(scratch);
        return scratch;
    }

    private static Dictionary<string, string> ReadOutput(string scratch) =>
        Directory.EnumerateFiles(scratch, "*", SearchOption.AllDirectories)
            .ToDictionary(
                path => Path.GetRelativePath(scratch, path),
                File.ReadAllText,
                StringComparer.Ordinal);

    private static MethodDecl FindMethod(ModuleDecl module, string typeName, string methodName, string parameterName) =>
        module.Types.Single(t => t.Name == typeName).Methods.Single(m =>
            m.Name == methodName && m.CSSignature.Any(p => p.Name == parameterName));
}
