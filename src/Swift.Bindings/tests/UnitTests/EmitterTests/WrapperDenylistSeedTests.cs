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
/// Proves the Gate-0 seed contract: a wrapper verify-recover denylist re-renders the module with every
/// denied unit refused up front, and each refusal reads honestly as a withdrawal — never as an emitter
/// exception that never happened.
/// </summary>
public class WrapperDenylistSeedTests : IDisposable
{
    private readonly List<string> _scratchDirs = new();

    public void Dispose()
    {
        foreach (var dir in _scratchDirs)
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { /* best effort */ }
        }
    }

    // ── the honest-origin record ────────────────────────────────────────────────────────────────

    /// <summary>
    /// A recovery withdrawal is not a thrown exception, so its details string must not claim one. The
    /// same string lands in the generated <c>// Unsupported:</c> tombstone and the skip report row, so
    /// a false "Emitter threw" here would misdirect every maintainer who reads it.
    /// </summary>
    [Fact]
    public void RecoveryWithdrawalRecord_ReadsAsAWithdrawalNotAThrow()
    {
        var record = EmitterFaultRecord.ForRecoveryWithdrawal(
            SomeDecl(), RecoveryScope.LeafApi, "withdrawn to recover the wrapper compile");

        Assert.Equal(EmitterFaultOrigin.RecoveryWithdrawal, record.Origin);
        Assert.Contains("Withdrawn by wrapper verify-recover", record.Details, StringComparison.Ordinal);
        Assert.Contains("withdrawn to recover the wrapper compile", record.Details, StringComparison.Ordinal);
        Assert.DoesNotContain("Emitter threw", record.Details, StringComparison.Ordinal);
    }

    /// <summary>
    /// A C# verify-recover withdrawal reads as its own kind of withdrawal — distinct prefix from the
    /// wrapper one — so the tombstone and skip row tell a triager the emitted C# was withdrawn to reach
    /// a clean C# compile, not the Swift wrapper. The two prefixes must not be interchangeable: a shared
    /// string would collapse the two stages the report distinguishes.
    /// </summary>
    [Fact]
    public void CSharpRecoveryWithdrawalRecord_ReadsAsADistinctCSharpWithdrawal()
    {
        var record = EmitterFaultRecord.ForRecoveryWithdrawal(
            SomeDecl(), RecoveryScope.LeafApi, "withdrawn to recover the C# compile",
            origin: EmitterFaultOrigin.CSharpRecoveryWithdrawal);

        Assert.Equal(EmitterFaultOrigin.CSharpRecoveryWithdrawal, record.Origin);
        Assert.Contains("Withdrawn by C# verify-recover", record.Details, StringComparison.Ordinal);
        Assert.Contains("withdrawn to recover the C# compile", record.Details, StringComparison.Ordinal);
        Assert.DoesNotContain("Emitter threw", record.Details, StringComparison.Ordinal);
        // Distinct from the wrapper wording — the two withdrawal planes are not interchangeable.
        Assert.DoesNotContain("Withdrawn by wrapper verify-recover", record.Details, StringComparison.Ordinal);
    }

    /// <summary>
    /// The default origin is unchanged: a record built from a live exception still reads as a throw,
    /// so the existing emitter-fault tombstones keep their fingerprint-bearing wording.
    /// </summary>
    [Fact]
    public void ExceptionRecord_StillReadsAsAThrow()
    {
        var record = EmitterFaultRecord.From(
            SomeDecl(), RecoveryScope.LeafApi, new InvalidOperationException("boom"));

        Assert.Equal(EmitterFaultOrigin.EmitterException, record.Origin);
        Assert.Contains("Emitter threw", record.Details, StringComparison.Ordinal);
        Assert.Contains(nameof(InvalidOperationException), record.Details, StringComparison.Ordinal);
    }

    // ── the seed builder ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Every unit in the denylist is poisoned under its own declaration — the key the emission gate
    /// looks a member up by — and every seeded fault reads as a withdrawal.
    /// </summary>
    [Fact]
    public void Build_PoisonsEveryUnitUnderItsDeclarationWithAWithdrawalRecord()
    {
        var method = RecoveryUnitId.Create(SomeDecl("register"), RecoveryScope.LeafApi);
        var accessor = RecoveryUnitId.ForAccessorGroup(SomeDecl("name"));
        var denylist = new HashSet<RecoveryUnitId> { method, accessor };

        var poison = WrapperDenylistSeed.Build(denylist);

        Assert.True(poison.IsPoisoned(method.Decl));
        Assert.True(poison.IsPoisoned(accessor.Decl));
        Assert.All(poison.Faults, f =>
        {
            Assert.Equal(EmitterFaultOrigin.RecoveryWithdrawal, f.Origin);
            Assert.Contains("Withdrawn by wrapper verify-recover", f.Details, StringComparison.Ordinal);
        });
    }

    /// <summary>An empty denylist produces an empty seed — the shape of a round that withdrew nothing.</summary>
    [Fact]
    public void Build_OnAnEmptyDenylist_ProducesAnEmptyPoisonList()
    {
        Assert.True(WrapperDenylistSeed.Build(new HashSet<RecoveryUnitId>()).IsEmpty);
    }

    /// <summary>
    /// The C# verify-recover loop shares the one monotonic denylist with the Swift wrapper loop, but a
    /// unit withdrawn to fix the C# compile must read as a C# withdrawal in its tombstone and report row,
    /// not a wrapper one. The per-unit <c>originOf</c> overload carries that plane: the same seed builder
    /// stamps each denied unit with the wording of the verifier that first named it, so both planes flow
    /// through the one Gate-0 channel while staying distinguishable to a triager.
    /// </summary>
    [Fact]
    public void Build_WithPerUnitOrigin_StampsEachUnitWithItsVerifiersWording()
    {
        var swiftUnit = RecoveryUnitId.Create(SomeDecl("swiftBroken"), RecoveryScope.LeafApi);
        var csharpUnit = RecoveryUnitId.Create(SomeDecl("csharpBroken"), RecoveryScope.LeafApi);
        var denylist = new HashSet<RecoveryUnitId> { swiftUnit, csharpUnit };

        EmitterFaultOrigin OriginOf(RecoveryUnitId u) =>
            u == csharpUnit ? EmitterFaultOrigin.CSharpRecoveryWithdrawal : EmitterFaultOrigin.RecoveryWithdrawal;

        var poison = WrapperDenylistSeed.Build(denylist, OriginOf);

        Assert.True(poison.IsPoisoned(swiftUnit.Decl));
        Assert.True(poison.IsPoisoned(csharpUnit.Decl));

        // The C#-withdrawn unit reads as a C# withdrawal — distinct prefix, distinct plane word — while
        // the Swift-withdrawn unit keeps the wrapper wording, from the one shared builder.
        var csharpFault = Assert.Single(poison.Faults, f => f.Origin == EmitterFaultOrigin.CSharpRecoveryWithdrawal);
        Assert.Contains("Withdrawn by C# verify-recover", csharpFault.Details, StringComparison.Ordinal);
        Assert.Contains("recover the C# compile", csharpFault.Details, StringComparison.Ordinal);
        Assert.Contains(csharpUnit.Describe(), csharpFault.Details, StringComparison.Ordinal);

        var swiftFault = Assert.Single(poison.Faults, f => f.Origin == EmitterFaultOrigin.RecoveryWithdrawal);
        Assert.Contains("Withdrawn by wrapper verify-recover", swiftFault.Details, StringComparison.Ordinal);
        Assert.Contains("recover the wrapper compile", swiftFault.Details, StringComparison.Ordinal);
        Assert.Contains(swiftUnit.Describe(), swiftFault.Details, StringComparison.Ordinal);
    }

    /// <summary>
    /// The origin-agnostic <see cref="WrapperDenylistSeed.Build(IReadOnlySet{RecoveryUnitId})"/> overload
    /// keeps the wave-1 default: every unit is a wrapper withdrawal. This pins that adding the C# plane
    /// did not silently reclassify the Swift-only loop's withdrawals.
    /// </summary>
    [Fact]
    public void Build_DefaultOverload_KeepsEveryUnitAWrapperWithdrawal()
    {
        var unit = RecoveryUnitId.Create(SomeDecl("member"), RecoveryScope.LeafApi);
        var poison = WrapperDenylistSeed.Build(new HashSet<RecoveryUnitId> { unit });

        var fault = Assert.Single(poison.Faults);
        Assert.Equal(EmitterFaultOrigin.RecoveryWithdrawal, fault.Origin);
        Assert.Contains("recover the wrapper compile", fault.Details, StringComparison.Ordinal);
    }

    // ── dual-index routing: coarse scopes never collapse their enclosing declaration ─────────────

    /// <summary>
    /// A coarse shared-helper-bundle seed lands in the unit-keyed index, queryable by its full recovery
    /// -unit identity, and pointedly does NOT poison the bundle's bare <see cref="DeclId"/> (the module).
    /// Poisoning the module DeclId would tell the whole-declaration skip gate the module is withdrawn; the
    /// unit index keeps the withdrawal to the one bundle.
    /// </summary>
    [Fact]
    public void Build_CoarseSharedHelperSeed_LandsInUnitIndexNotDeclIndex()
    {
        var helper = RecoveryUnitId.ForSharedHelper(HelperModuleDecl(),"utf8");
        var poison = WrapperDenylistSeed.Build(new HashSet<RecoveryUnitId> { helper });

        Assert.True(poison.IsPoisoned(helper), "coarse unit must be poisoned in the unit index");
        Assert.False(poison.IsPoisoned(helper.Decl), "coarse seed must not poison its bare DeclId (the module)");
        Assert.False(poison.IsEmpty);
    }

    /// <summary>
    /// A leaf seed stays on the bare-DeclId index exactly as before — it is a whole-declaration
    /// withdrawal — and is absent from the unit index. This is the routing half that keeps every existing
    /// leaf/accessor/type poisoning byte-identical.
    /// </summary>
    [Fact]
    public void Build_LeafSeed_StaysOnDeclIndexAndIsAbsentFromUnitIndex()
    {
        var leaf = RecoveryUnitId.Create(SomeDecl("register"), RecoveryScope.LeafApi);
        var poison = WrapperDenylistSeed.Build(new HashSet<RecoveryUnitId> { leaf });

        Assert.True(poison.IsPoisoned(leaf.Decl), "leaf must stay on the bare-DeclId index");
        Assert.False(poison.IsPoisoned(leaf), "leaf must not appear in the coarse unit index");
    }

    /// <summary>
    /// Directly at the poison list: recording a coarse unit and a leaf shows the two indexes are
    /// independent — the coarse unit answers only its unit query, the leaf only its DeclId query, and
    /// neither leaks into the other's index.
    /// </summary>
    [Fact]
    public void PoisonList_RoutesCoarseAndWholeDeclarationScopesToSeparateIndexes()
    {
        var helper = RecoveryUnitId.ForSharedHelper(HelperModuleDecl(),"closure");
        var leaf = RecoveryUnitId.Create(SomeDecl("m"), RecoveryScope.LeafApi);

        var poison = new EmitterPoisonList();
        Assert.True(poison.Record(helper, EmitterFaultRecord.ForRecoveryWithdrawal(
            helper.Decl, helper.Scope, "coarse")));
        Assert.True(poison.Record(leaf, EmitterFaultRecord.ForRecoveryWithdrawal(
            leaf.Decl, leaf.Scope, "leaf")));

        Assert.True(poison.IsPoisoned(helper));
        Assert.False(poison.IsPoisoned(helper.Decl));
        Assert.True(poison.IsPoisoned(leaf.Decl));
        Assert.False(poison.IsPoisoned(leaf));

        // Re-recording the same coarse unit makes no progress — the controller, not the poison list,
        // decides a coarse unit's escalation against the recovery graph.
        Assert.False(poison.Record(helper, EmitterFaultRecord.ForRecoveryWithdrawal(
            helper.Decl, helper.Scope, "again")));
    }

    // ── end to end: the seed drives the real re-render ──────────────────────────────────────────

    /// <summary>
    /// The seed, handed to the real containment loop, denies the units up front: the module still
    /// generates, each denied member is tombstoned as a withdrawal (not a throw) under its own
    /// identity, and the untouched siblings emit exactly as they do in a clean render.
    /// </summary>
    /// <remarks>
    /// Two units are denied on purpose — a <c>register</c> method overload and the <c>name</c> property
    /// — so the test can prove each reaches output independently rather than settling for "some
    /// withdrawal comment appears somewhere". Each unit's <see cref="RecoveryUnitId.Describe"/> is
    /// unique and rides into the tombstone through the fault message, so asserting both are present
    /// pins one tombstone to each unit. Sibling survival is measured against a clean render of the same
    /// fixture, not asserted by inspection.
    /// </remarks>
    [Fact]
    public void Build_SeedsTheContainmentLoop_ToWithdrawTheDeniedUnitsHonestly()
    {
        var methodUnit = RecoveryUnitId.Create(
            DeclIdFactory.ForMethod(RegistryMethod("register", "third")), RecoveryScope.LeafApi);
        var propertyUnit = RecoveryUnitId.ForAccessorGroup(
            DeclIdFactory.ForProperty(RegistryProperty("name")));
        var denylist = new HashSet<RecoveryUnitId> { methodUnit, propertyUnit };

        var seeded = Render(WrapperDenylistSeed.Build(denylist));
        var clean = Render(seed: null);

        // Each denied unit reaches the consumer-facing tombstone under its OWN identity — not one
        // generic withdrawal comment standing in for both.
        Assert.Contains(methodUnit.Describe(), seeded, StringComparison.Ordinal);
        Assert.Contains(propertyUnit.Describe(), seeded, StringComparison.Ordinal);

        // And every tombstone reads honestly: a withdrawal, never an exception that did not happen.
        Assert.DoesNotContain("Emitter threw", seeded, StringComparison.Ordinal);
        Assert.Contains(SkipReason.EmitterFault.ToString(), seeded, StringComparison.Ordinal);

        // The denied property is actually gone from the emitted API surface, not merely commented on:
        // the clean render carries a real `Name` member; the seeded one does not.
        Assert.True(MemberCount(clean, "Name") > 0, "fixture emitted no Name member, so its loss proves nothing");
        Assert.Equal(0, MemberCount(seeded, "Name"));

        // A sibling the withdrawal must not touch — the contended `Count` property — emits identically
        // in both renders. If the seed had shifted or dropped surrounding surface, this would move.
        Assert.True(MemberCount(clean, "Count") > 0, "fixture emitted no Count member, so its survival proves nothing");
        Assert.Equal(MemberCount(clean, "Count"), MemberCount(seeded, "Count"));
    }

    // ── harness ─────────────────────────────────────────────────────────────────────────────────

    private static DeclId SomeDecl(string name = "member") =>
        DeclId.Create("M", "T", BindingItemKind.Method, name);

    private static DeclId HelperModuleDecl() =>
        DeclId.Create("M", string.Empty, BindingItemKind.Module, "M");

    private static MethodDecl RegistryMethod(string name, string parameterName)
    {
        var module = FixtureModuleFactory.BuildModule("ContainmentFixture");
        var registry = module.Types.Single(t => t.Name == "Registry");
        return registry.Methods.Single(m =>
            m.Name == name && m.CSSignature.Any(p => p.Name == parameterName));
    }

    private static PropertyDecl RegistryProperty(string name)
    {
        var module = FixtureModuleFactory.BuildModule("ContainmentFixture");
        var registry = module.Types.Single(t => t.Name == "Registry");
        return registry.Properties.Single(p => p.Name == name);
    }

    /// <summary>
    /// Renders the shared fixture through the real containment loop and returns the concatenated C#
    /// output. A null <paramref name="seed"/> is a clean render (the sibling-survival baseline).
    /// </summary>
    private string Render(EmitterPoisonList? seed)
    {
        var scratch = NewScratchDir();
        var module = FixtureModuleFactory.BuildModule("ContainmentFixture");
        var typeDatabase = FixtureModuleFactory.BuildTypeDatabase(module);

        ReportCollector.Reset();
        try
        {
            ContainedModuleEmission.Run(
                module,
                new ModuleEmissionContext(),
                typeDatabase,
                NullLogger.Instance,
                newEmitter: () => new StringEmitter(scratch, typeDatabase, new NullLoggerFactory()),
                seed: seed);
        }
        finally
        {
            ReportCollector.Complete();
            ReportCollector.Reset();
        }

        return string.Concat(ReadOutput(scratch)
            .Where(f => f.Key.EndsWith(".cs", StringComparison.Ordinal))
            .Select(f => f.Value));
    }

    /// <summary>Counts emitted C# member declarations named <paramref name="name"/> (property or method).</summary>
    private static int MemberCount(string csharp, string name) =>
        System.Text.RegularExpressions.Regex.Matches(csharp, $@"\b{name}\s*(?:\{{|=>|\()").Count;

    private string NewScratchDir()
    {
        var scratch = Path.Combine(Path.GetTempPath(), "swiftbind-denyseed-" + Guid.NewGuid().ToString("N"));
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
}
