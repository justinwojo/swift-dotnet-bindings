// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.IO;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// A member is written into the C# buffer and the Swift wrapper buffer by the same call, so the
/// recovery path for a mid-emission contract failure has to undo both or neither. These tests pin
/// that pairing and — more importantly — the one case where undoing the Swift side is the wrong
/// move: when the same span also committed module-shared Swift helpers, which are registered as
/// "already emitted" with no way to un-register, so truncating them would leave later members
/// referring to definitions nothing will write again.
/// </summary>
public class WriterRollbackPairingTests
{
    private static (CSharpWriter Cs, StringWriter CsBuffer, SwiftWriter Swift, StringWriter SwiftBuffer) MakeWriters()
    {
        var csBuffer = new StringWriter();
        var swiftBuffer = new StringWriter();
        return (new CSharpWriter(csBuffer), csBuffer, new SwiftWriter(swiftBuffer), swiftBuffer);
    }

    [Fact]
    public void Rollback_WithoutSharedHelperCommit_DiscardsBothBuffers()
    {
        var (cs, csBuffer, swift, swiftBuffer) = MakeWriters();
        var context = new ModuleEmissionContext();

        cs.WriteLine("// kept C#");
        swift.WriteLine("// kept Swift");

        var transaction = MemberEmissionTransaction.Begin(cs, swift, context);
        cs.WriteLine("public void DoWork() { }");
        swift.WriteLine("@_cdecl(\"SBW_DoWork\")");
        swift.WriteLine("public func SBW_DoWork() { }");

        Assert.Equal(MemberEmissionTransaction.SwiftKeep.RolledBack, transaction.Rollback());

        Assert.DoesNotContain("DoWork", csBuffer.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("SBW_DoWork", swiftBuffer.ToString(), StringComparison.Ordinal);
        Assert.Contains("kept C#", csBuffer.ToString(), StringComparison.Ordinal);
        Assert.Contains("kept Swift", swiftBuffer.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Rollback_AfterSharedSwiftHelperCommit_DiscardsCSharpButKeepsSwift()
    {
        var (cs, csBuffer, swift, swiftBuffer) = MakeWriters();
        var context = new ModuleEmissionContext();

        var transaction = MemberEmissionTransaction.Begin(cs, swift, context);
        cs.WriteLine("public void DoWork() { }");

        // Stands in for any module-shared helper written into the wrapper source and recorded so
        // no later member re-emits it. The registration has no undo, so the text must survive.
        swift.WriteLine("public struct SBW_Utf8Slice { }");
        context.Utf8SliceStructEmitted = true;
        swift.WriteLine("@_cdecl(\"SBW_DoWork\")");
        swift.WriteLine("public func SBW_DoWork() { }");

        Assert.False(transaction.SwiftRollbackIsSafe);
        // The reason is asserted, not just the outcome: blaming a committed shared helper when the
        // real cause was a missing writer or context would send a reader hunting for the wrong thing.
        Assert.Equal(MemberEmissionTransaction.SwiftKeep.SharedHelperCommitted, transaction.Rollback());

        Assert.DoesNotContain("DoWork", csBuffer.ToString(), StringComparison.Ordinal);
        Assert.Contains("SBW_Utf8Slice", swiftBuffer.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Rollback_WithUnknownEmissionContext_KeepsSwift()
    {
        var (cs, csBuffer, swift, swiftBuffer) = MakeWriters();

        var transaction = MemberEmissionTransaction.Begin(cs, swift, context: null);
        cs.WriteLine("public void DoWork() { }");
        swift.WriteLine("public func SBW_DoWork() { }");

        // Without a context there is no epoch to compare, so nothing proves the Swift span is
        // member-private. Keeping it is the direction that cannot break the wrapper build.
        Assert.Equal(MemberEmissionTransaction.SwiftKeep.NoEmissionContext, transaction.Rollback());
        Assert.DoesNotContain("DoWork", csBuffer.ToString(), StringComparison.Ordinal);
        Assert.Contains("SBW_DoWork", swiftBuffer.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Rollback_WithoutSwiftWriter_StillRollsBackCSharp()
    {
        var (cs, csBuffer, _, _) = MakeWriters();

        var transaction = MemberEmissionTransaction.Begin(cs, swiftWriter: null, new ModuleEmissionContext());
        cs.WriteLine("public void DoWork() { }");

        Assert.Equal(MemberEmissionTransaction.SwiftKeep.NoSwiftWriter, transaction.Rollback());
        Assert.DoesNotContain("DoWork", csBuffer.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Rollback_RestoresIndentOnBothWriters()
    {
        var (cs, _, swift, _) = MakeWriters();
        cs.Indent = 2;
        swift.Indent = 3;

        var transaction = MemberEmissionTransaction.Begin(cs, swift, new ModuleEmissionContext());
        cs.Indent = 7;
        swift.Indent = 9;
        cs.WriteLine("nested");
        swift.WriteLine("nested");

        Assert.Equal(MemberEmissionTransaction.SwiftKeep.RolledBack, transaction.Rollback());
        Assert.Equal(2, cs.Indent);
        Assert.Equal(3, swift.Indent);
    }

    [Fact]
    public void Checkpoint_RolledBackOnADifferentWriter_Throws()
    {
        var (cs, _, swift, swiftBuffer) = MakeWriters();
        var swiftCheckpoint = swift.Checkpoint();
        swift.WriteLine("public func SBW_DoWork() { }");

        // C# and Swift checkpoints are the same type now that both writers share a base, so a
        // mixed-up pair would otherwise truncate an unrelated buffer to a meaningless offset.
        Assert.Throws<InvalidOperationException>(() => cs.RollbackTo(swiftCheckpoint));
        Assert.Contains("SBW_DoWork", swiftBuffer.ToString(), StringComparison.Ordinal);
    }
}

/// <summary>
/// The paired rollback is only sound while every registry whose commit implies module-shared
/// Swift text bumps <see cref="ModuleEmissionContext.SharedSwiftArtifactEpoch"/>. A shared helper
/// that commits without bumping would let a rollback truncate it while the registry still reports
/// it emitted — so this pins the current set. A new shared Swift helper registry belongs here.
/// </summary>
public class ModuleEmissionContextEpochTests
{
    public static TheoryData<string, Action<ModuleEmissionContext>> SharedSwiftArtifactCommits() => new()
    {
        { "Utf8SliceStruct", c => c.Utf8SliceStructEmitted = true },
        { "Utf8SliceFree", c => c.Utf8SliceFreeEmitted = true },
        { "SwiftErrorMintHelper", c => c.SwiftErrorMintHelperEmitted = true },
        { "AsyncClosureBridgeError", c => c.AsyncClosureBridgeErrorEmitted = true },
        { "ClosureContextHelpers", c => c.ClosureContextHelpersEmitted = true },
        { "NcbInnerBoxRelease", c => c.TryAddNcbInnerBoxReleaseSymbol("SBW_ReleaseBox_Mod") },
        { "TypedErrorExtractor", c => c.TryAddTypedErrorExtractor("Mod.MyError") },
        { "AsyncClosureSwiftWrapper", c => c.TryAddAsyncClosureSwiftWrapperKey("Mod:Int") },
        { "CancellationInfrastructure", c => c.CancellationInfrastructureEmitted = true },
        { "ErrorDescInfrastructure", c => c.ErrorDescInfrastructureEmitted = true },
        { "ErrorRegistryHelperSwift", c => c.ErrorRegistryHelperEmittedSwift = true },
        // Per-type Swift singletons. Each is consulted as a dedup gate (`if (!TryAdd…) return;`)
        // by an emitter that then writes an @_cdecl definition into the Swift writer, so a later
        // member takes the early return and never writes it again.
        { "MetadataWrapper", c => c.TryAddMetadataWrapperSymbol("SBW_GetMetadata_Mod_Thing") },
        { "MetadataAccessorHelper", c => c.TryAddMetadataAccessorHelper("$s3Mod5ThingVD") },
        { "EqualityWrapper", c => c.TryAddEqualityWrapperSymbol("SBW_Equals_Mod_Thing") },
        { "OptionalTagHelper", c => c.TryAddOptionalTagHelperSymbol("SBW_GetOptionalTag_Mod_Thing") },
        { "EnumRawRepWrapper", c => c.TryAddEnumRawRepWrapperSymbol("SBW_Mod_Suit_init_rawValue") },
    };

    [Theory]
    [MemberData(nameof(SharedSwiftArtifactCommits))]
    public void CommittingASharedSwiftHelper_AdvancesTheEpoch(string name, Action<ModuleEmissionContext> commit)
    {
        var context = new ModuleEmissionContext();
        var before = context.SharedSwiftArtifactEpoch;

        commit(context);

        Assert.True(
            context.SharedSwiftArtifactEpoch > before,
            $"Committing the shared Swift helper '{name}' left the epoch unchanged, so a rolled-back " +
            "member would truncate its text while the registry still reports it emitted.");
    }

    [Theory]
    [MemberData(nameof(SharedSwiftArtifactCommits))]
    public void RecommittingTheSameSharedSwiftHelper_DoesNotAdvanceTheEpoch(string name, Action<ModuleEmissionContext> commit)
    {
        var context = new ModuleEmissionContext();
        commit(context);
        var afterFirst = context.SharedSwiftArtifactEpoch;

        commit(context);

        Assert.True(
            context.SharedSwiftArtifactEpoch == afterFirst,
            $"Re-committing '{name}' advanced the epoch even though it wrote no new text, which would " +
            "needlessly suppress a safe rollback.");
    }

    [Fact]
    public void ANewContext_StartsAtAStableEpoch()
    {
        Assert.Equal(new ModuleEmissionContext().SharedSwiftArtifactEpoch, new ModuleEmissionContext().SharedSwiftArtifactEpoch);
    }

    [Fact]
    public void RegistriesThatWriteNoSwiftText_DoNotAdvanceTheEpoch()
    {
        var context = new ModuleEmissionContext();
        var before = context.SharedSwiftArtifactEpoch;

        // C#-side P/Invoke dedup registries: their commits imply managed text, not wrapper text,
        // so they must not suppress a Swift rollback.
        context.TryAddUtf8SliceFreePInvoke("Mod.Thing");
        context.TryAddErrorDescPInvoke("Mod.Thing");
        context.TryAddSwiftErrorMintPInvoke("Mod.Thing");
        context.TryAddExtractorPInvoke("Mod.Thing");

        Assert.Equal(before, context.SharedSwiftArtifactEpoch);
    }

    /// <summary>
    /// The load-bearing negative case. A per-member wrapper symbol is the member's OWN Swift body —
    /// it is written inside the speculative span and rolls back with it, and nothing outside the
    /// member refers to it. Bumping the epoch here would mean every member that registers its own
    /// wrapper suppresses its own rollback, silently reducing the paired transaction back to the
    /// C#-only behaviour it replaced: the orphaned-wrapper bug would return with all its tests
    /// still green.
    /// </summary>
    [Fact]
    public void PerMemberWrapperSymbols_DoNotAdvanceTheEpoch()
    {
        var context = new ModuleEmissionContext();
        var before = context.SharedSwiftArtifactEpoch;

        context.TryAddMethodWrapperSymbol("SBW_Mod_Thing_doWork");
        context.TryAddPropertyWrapperSymbol("SBW_Mod_Thing_value_get");
        context.TryAddConstructorWrapperSymbol("SBW_Mod_Thing_init");
        context.TryAddObjCPropertyWrapperSymbol("SBW_Mod_Thing_objcValue_get");

        Assert.Equal(before, context.SharedSwiftArtifactEpoch);
    }

    /// <summary>
    /// Two more shapes that must stay out. The direct-helper bucket is a bare registration — every
    /// caller discards its result — so it gates no one's re-emission. The protocol/foreign
    /// extension buckets accumulate their wrapper text into separate line lists that never enter
    /// the Swift writer, so a buffer truncation cannot reach them either.
    /// </summary>
    [Fact]
    public void NonGatingAndSideListRegistries_DoNotAdvanceTheEpoch()
    {
        var context = new ModuleEmissionContext();
        var before = context.SharedSwiftArtifactEpoch;

        context.TryAddDirectHelperWrapperSymbol("SBW_CreateError_Mod");
        context.TryAddProtocolExtSymbol("SBW_Mod_P_ext_doWork");
        context.TryAddForeignExtSymbol("SBW_Mod_Foreign_ext_doWork");

        Assert.Equal(before, context.SharedSwiftArtifactEpoch);
    }
}
