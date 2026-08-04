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
/// The API manifest — and the api-surface doc rendered from it — promises a consumer what they can
/// call. Its entries are accumulated from the declared model at emission chokepoints, while the
/// member itself is written later by whichever emitter claims it, and those emitters legitimately
/// reshape what they write. A phantom entry breaks nothing: the C# compiles, the tests pass, and the
/// lie surfaces only when a consumer calls what the document promised.
///
/// <para>These tests cover both halves of the guard. The matcher tests pin its rules directly; the
/// whole-render tests drive the real <see cref="StringEmitter.EmitModule"/> path, which is the only
/// way to observe that the check is actually wired into emission — a test that hand-feeds a
/// <see cref="ModuleEmissionContext"/> and calls the renderer cannot tell a wired check from an
/// unwired one.</para>
/// </summary>
public class ApiSurfaceReconcilerTests : IDisposable
{
    private readonly List<string> _scratchDirs = new();

    public void Dispose()
    {
        foreach (var dir in _scratchDirs)
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    // ── whole-render coverage ──────────────────────────────────────────────────────────────

    [Fact]
    public void EmitModule_RealRender_ReconcilesEveryManifestEntry()
    {
        // The fixture module spans protocols, frozen and non-frozen structs, classes with colliding
        // overloads, closures, async and throwing members and free functions — each a different
        // emitter family, and so a different chance for a recorded key to disagree with what was
        // written. Emission completing without throwing IS the assertion.
        var context = new ModuleEmissionContext();

        EmitFixtureModule(context);

        // Guard the guard: an empty manifest would make the check vacuous.
        Assert.NotEmpty(context.ApiManifestEntries);
    }

    [Fact]
    public void EmitModule_ManifestEntryWithNoEmittedMember_FailsTheGenerator()
    {
        // A member the manifest claims but no emitter wrote is exactly the phantom this exists to
        // catch, and it must stop the build rather than ship a document that lies.
        var context = new ModuleEmissionContext();
        context.RecordApiManifestEntry("Registry.MemberThatWasNeverEmitted(int)", "sym_phantom");

        var ex = Assert.Throws<ApiSurfaceReconciliationException>(() => EmitFixtureModule(context));

        Assert.Contains("Registry.MemberThatWasNeverEmitted(int)", ex.Message);
        Assert.Contains("MemberThatWasNeverEmitted", string.Join("\n", ex.UnreconciledEntries));
    }

    [Fact]
    public void EmitModule_ManifestEntryWithWrongArgumentCount_FailsTheGenerator()
    {
        // The likelier phantom in practice: the member exists, but under a different shape than the
        // key describes. Matching on the name alone would let that through.
        var context = new ModuleEmissionContext();
        context.RecordApiManifestEntry("Point.Magnitude(int,int,int,int,int,int,int)", "sym_wrong_arity");

        var ex = Assert.Throws<ApiSurfaceReconciliationException>(() => EmitFixtureModule(context));

        Assert.Contains("Magnitude", ex.Message);
    }

    [Fact]
    public void EmitModule_RecordedEmittedShapeIsNotAnEscapeHatch()
    {
        // An emitter that reshapes what it writes records the reshaped form so the manifest keys the
        // member a consumer can actually call. That must not degrade into "recording anything
        // reconciles it": a recorded shape the emitted C# does not contain is the same phantom as an
        // unrecorded one, and the check has to keep failing on it.
        var context = new ModuleEmissionContext();
        var key = ModuleEmissionContext.BuildApiManifestKey(
            parent: null,
            csharpName: "Reshaped",
            projectedKey: "Reshaped(int)",
            typeDatabase: null,
            emitted: new ModuleEmissionContext.EmittedApiShape("Reshaped", "(int,int,int)"));
        context.RecordApiManifestEntry(key, "sym_reshaped_phantom");

        var ex = Assert.Throws<ApiSurfaceReconciliationException>(() => EmitFixtureModule(context));

        Assert.Contains("Reshaped(int,int,int)", ex.Message);
    }

    // ── matcher rules ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Reconcile_MethodMatchesOnNameAndArgumentCount()
    {
        const string emitted = @"
            public sealed class Widget
            {
                public int Present(nint handle) => 0;
                public void Configure(string name, int value) { }
            }";

        Assert.Empty(ApiSurfaceReconciler.FindUnreconciledEntries(
            new[] { "Widget.Present(nint)", "Widget.Configure(string,int)" }, emitted));

        // Same names, wrong shapes.
        Assert.Equal(
            new[] { "Widget.Present(nint,int)", "Widget.Absent(string)" },
            ApiSurfaceReconciler.FindUnreconciledEntries(
                new[] { "Widget.Present(nint,int)", "Widget.Absent(string)" }, emitted));
    }

    [Fact]
    public void Reconcile_ZeroArgumentMethodDoesNotMatchAnArgumentBearingOne()
    {
        const string emitted = "public sealed class Widget { public void Reset(int depth) { } }";

        Assert.Equal(
            new[] { "Widget.Reset()" },
            ApiSurfaceReconciler.FindUnreconciledEntries(new[] { "Widget.Reset()" }, emitted));
    }

    [Fact]
    public void Reconcile_PropertyMatchesOnlyWhereABodyBegins()
    {
        const string emitted = @"
            public sealed class Widget
            {
                public int Amount { get; }
                public string Label => ""x"";
                public void Use() { Consume(Nothing); }
            }";

        Assert.Empty(ApiSurfaceReconciler.FindUnreconciledEntries(
            new[] { "Widget.Amount", "Widget.Label" }, emitted));

        // `Nothing` appears in the text, but only as an argument — never as a declared member.
        Assert.Equal(
            new[] { "Widget.Nothing" },
            ApiSurfaceReconciler.FindUnreconciledEntries(new[] { "Widget.Nothing" }, emitted));
    }

    [Fact]
    public void Reconcile_SubscriptMatchesOnIndexCount()
    {
        const string emitted = @"
            public sealed class Grid
            {
                public int this[int row] => 0;
                public int this[int row, int column] => 0;
            }";

        Assert.Empty(ApiSurfaceReconciler.FindUnreconciledEntries(
            new[] { "Grid.this[int]", "Grid.this[int,int]" }, emitted));

        Assert.Equal(
            new[] { "Grid.this[int,int,int]" },
            ApiSurfaceReconciler.FindUnreconciledEntries(new[] { "Grid.this[int,int,int]" }, emitted));
    }

    [Fact]
    public void Reconcile_TwoEntriesSharingAShapeNeedTwoDeclarations()
    {
        // Matching is by supply: the one emitted `Save(1-arg)` can be claimed once, so a second
        // entry of the same shape — here a phantom on a type that emitted nothing of the kind —
        // has nothing left to reconcile against. Entries claim in manifest order, so the phantom
        // is the one reported only because it sorts after the real member; when several entries
        // share an under-supplied shape the check knows the count is wrong, not which entry lied.
        const string emitted = @"
            public sealed class Gadget
            {
                public void Save(int slot) { }
            }
            public sealed class Zombie
            {
            }";

        Assert.Empty(ApiSurfaceReconciler.FindUnreconciledEntries(new[] { "Gadget.Save(int)" }, emitted));

        Assert.Equal(
            new[] { "Zombie.Save(int)" },
            ApiSurfaceReconciler.FindUnreconciledEntries(
                new[] { "Gadget.Save(int)", "Zombie.Save(int)" }, emitted));

        // Two declarations supply two entries.
        const string bothEmitted = @"
            public sealed class Gadget { public void Save(int slot) { } }
            public sealed class Zombie { public void Save(int slot) { } }";
        Assert.Empty(ApiSurfaceReconciler.FindUnreconciledEntries(
            new[] { "Gadget.Save(int)", "Zombie.Save(int)" }, bothEmitted));
    }

    [Fact]
    public void Reconcile_SupplyRunsShortForPropertiesAndSubscriptsToo()
    {
        const string emitted = @"
            public sealed class Gadget
            {
                public int Amount { get; }
                public int this[int row] => 0;
            }";

        Assert.Equal(
            new[] { "Zombie.Amount" },
            ApiSurfaceReconciler.FindUnreconciledEntries(
                new[] { "Gadget.Amount", "Zombie.Amount" }, emitted));

        Assert.Equal(
            new[] { "Zombie.this[int]" },
            ApiSurfaceReconciler.FindUnreconciledEntries(
                new[] { "Gadget.this[int]", "Zombie.this[int]" }, emitted));
    }

    [Fact]
    public void Reconcile_IgnoresCommentsAndStringLiterals()
    {
        // A member named only in an XML doc comment or a string literal was not emitted; treating
        // either as a declaration would make the check unable to see the phantoms it exists for.
        const string emitted = @"
            public sealed class Widget
            {
                /// <summary>See Ghost(int) for details.</summary>
                public void Real() { var s = ""Phantom(int)""; }
            }";

        Assert.Equal(
            new[] { "Widget.Ghost(int)", "Widget.Phantom(int)" },
            ApiSurfaceReconciler.FindUnreconciledEntries(
                new[] { "Widget.Ghost(int)", "Widget.Phantom(int)" }, emitted));
        Assert.Empty(ApiSurfaceReconciler.FindUnreconciledEntries(new[] { "Widget.Real()" }, emitted));
    }

    [Theory]
    // A generic method's arity marker qualifies the member, not the shape being matched.
    [InlineData("Widget.Combine(T,T)`1", "Combine(T,T)")]
    [InlineData("Widget.Present(nint)", "Present(nint)")]
    [InlineData("Widget.Amount", "Amount")]
    [InlineData("Widget.this[global::System.IntPtr]", "this[global::System.IntPtr]")]
    // A free function has no parent path, so the whole key is the member.
    [InlineData("MakeClient(string,int)", "MakeClient(string,int)")]
    public void MemberPortion_StripsParentPathAndArityMarker(string key, string expected)
    {
        Assert.Equal(expected, ApiSurfaceReconciler.MemberPortion(key));
    }

    [Theory]
    [InlineData("()", 0)]
    [InlineData("(  )", 0)]
    [InlineData("(int)", 1)]
    [InlineData("(int, string)", 2)]
    // Generic commas are counted rather than skipped — deliberately, and identically on both sides.
    [InlineData("(Func<int, int>)", 2)]
    [InlineData("(delegate*<int,void>, int)", 3)]
    // A nested argument list does not leak its commas into the outer count.
    [InlineData("(int, Wrap(a,b))", 2)]
    public void CountArguments_CountsTopLevelSeparators(string text, int expected)
    {
        Assert.Equal(expected, ApiSurfaceReconciler.CountArguments(text, 0));
    }

    // ── harness ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs the fixture module through the real <see cref="StringEmitter"/> — the same path the
    /// generator takes — so the reconciliation check runs where production runs it, against the text
    /// that was actually written to disk.
    /// </summary>
    private void EmitFixtureModule(ModuleEmissionContext context)
    {
        var scratch = Path.Combine(Path.GetTempPath(), "swiftbind-reconcile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        _scratchDirs.Add(scratch);

        var moduleDecl = FixtureModuleFactory.BuildModule("ReconcileFixture");
        var typeDatabase = FixtureModuleFactory.BuildTypeDatabase(moduleDecl);

        ReportCollector.Reset();
        ReportCollector.Start(moduleDecl);
        AppleSupplementReferences.Reset();
        try
        {
            var emitter = new StringEmitter(scratch, typeDatabase, new NullLoggerFactory());
            emitter.EmitModule(moduleDecl, context);
        }
        finally
        {
            ReportCollector.Complete();
            ReportCollector.Reset();
        }
    }
}
