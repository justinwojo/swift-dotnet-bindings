// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Pins the type-database undo log that makes a discarded (or looped) emission attempt leave the frozen
/// registry bit-identical — the one mutation channel <see cref="ModuleEmissionStateSnapshot"/> and
/// <see cref="DeclEmissionStateSnapshot"/> do not cover, and the exact seam the verify-recover driver
/// rewinds with <c>_outerJournal.RestoreInto(_typeDatabase)</c> before every render.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="RegistryContractTests"/> proves <see cref="ITypeDatabase.ApplyEmissionResult"/> stamps a
/// fact onto a frozen record; it does not prove the stamp can be undone. These tests close that gap
/// directly: they capture a real pre-image through an in-flight <see cref="EmissionAttempt"/>, stamp,
/// and assert <see cref="EmissionFactsJournal.RestoreInto"/> rolls the record back to exactly what it
/// was. If the journal regressed to a no-op or dropped a pre-image, a discarded attempt's stamp would
/// survive into the render that replaces it — the precise leak gap #2 exists to prevent — and these go
/// red. The driver-level integration counterpart cannot catch this on the current fixture, whose
/// emission facts do not alter re-emission output; this primitive test does.
/// </para>
/// </remarks>
public class EmissionFactsJournalTests
{
    private static SwiftTypeName Name(string moduleQualified) =>
        SwiftTypeName.FromModuleQualifiedName(moduleQualified);

    private static TypeRecord Record(SwiftTypeName name) =>
        new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("BindingsGeneration.Tests", name.Name),
            SwiftTypeName = name,
            MetadataAccessor = "acc",
            Flags = TypeRecordFlags.None,
            Kind = TypeRecordKind.Class,
        };

    /// <summary>A frozen database holding one registered, un-stamped record.</summary>
    private static (TypeDatabase Db, SwiftTypeName Name) FrozenDbWithOneType()
    {
        var db = new TypeDatabase();
        var module = new ModuleTypeDatabase("M", "/p");
        var name = Name("M.T");
        module.RegisterType(name, Record(name));
        db.AddModuleDatabase(module);
        db.Freeze();
        return (db, name);
    }

    private static EmissionAttempt BeginAttempt() =>
        EmissionAttempt.Begin(WrapperDenylistSeed.Build(new HashSet<RecoveryUnitId>()));

    // ── the round trip ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A stamp applied under an in-flight attempt is captured, and <see cref="EmissionFactsJournal.
    /// RestoreInto"/> puts the record back to its pre-stamp state — then empties the log. Several
    /// independent fact shapes are stamped at once and the WHOLE restored record is compared to the
    /// pre-image, so a restore that fixed one field but leaked another (a partial rollback) goes red,
    /// not just a total no-op.
    /// </summary>
    [Fact]
    public void RestoreInto_UndoesEveryStampedFact_LeavingTheRecordBitIdentical()
    {
        var (db, name) = FrozenDbWithOneType();
        Assert.True(db.TryGetTypeRecord(name, out var before));
        Assert.Null(before!.EmittedMemberCount);
        Assert.Null(before.EmittedMetadataPInvoke);

        using var attempt = BeginAttempt();

        // Stamp three independent emission facts so the round trip covers more than one field.
        db.ApplyEmissionResult(name, new TypeEmissionResult
        {
            EmittedMemberCount = 42,
            EmittedMetadataPInvoke = true,
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("BindingsGeneration.Tests", "Renamed"),
        });
        Assert.Equal(1, attempt.Journal.Count); // pre-image captured on first stamp
        Assert.True(db.TryGetTypeRecord(name, out var stamped));
        Assert.Equal(42, stamped!.EmittedMemberCount);
        Assert.NotEqual(before, stamped); // the stamp genuinely changed the record

        attempt.Journal.RestoreInto(db);

        Assert.True(db.TryGetTypeRecord(name, out var restored));
        Assert.Equal(before, restored);         // whole record back to the pre-stamp state
        Assert.Equal(0, attempt.Journal.Count); // log emptied by the restore
    }

    /// <summary>
    /// The undo covers the out-of-module record arm too: <see cref="ITypeDatabase.RestoreEmissionRecord"/>
    /// has a separate branch for out-of-module identities, and a discarded attempt that stamped one must
    /// see it rolled back just as an in-module record is.
    /// </summary>
    [Fact]
    public void RestoreInto_UndoesAStampOnAnOutOfModuleRecord()
    {
        var db = new TypeDatabase();
        db.AddModuleDatabase(new ModuleTypeDatabase("M", "/p"));
        var name = Name("Other.T");
        db.AddOutOfModuleTypes(new[] { (name, Record(name)) });
        db.Freeze();
        Assert.True(db.TryGetTypeRecord(name, out var before));

        using var attempt = BeginAttempt();

        db.ApplyEmissionResult(name, new TypeEmissionResult { EmittedMemberCount = 9 });
        Assert.Equal(1, attempt.Journal.Count);
        Assert.True(db.TryGetTypeRecord(name, out var stamped));
        Assert.Equal(9, stamped!.EmittedMemberCount);

        attempt.Journal.RestoreInto(db);

        Assert.True(db.TryGetTypeRecord(name, out var restored));
        Assert.Equal(before, restored);
    }

    /// <summary>
    /// Only the first stamp per type is captured, so a restore rolls all the way back to the original
    /// record — never to an intermediate stamp the same attempt wrote on top of its own work.
    /// </summary>
    [Fact]
    public void RestoreInto_RollsBackToTheOriginal_NotAnIntermediateStamp()
    {
        var (db, name) = FrozenDbWithOneType();

        using var attempt = BeginAttempt();

        db.ApplyEmissionResult(name, new TypeEmissionResult { EmittedMemberCount = 1 });
        db.ApplyEmissionResult(name, new TypeEmissionResult { EmittedMemberCount = 2 });
        Assert.Equal(1, attempt.Journal.Count); // first-write-per-type wins

        attempt.Journal.RestoreInto(db);

        Assert.True(db.TryGetTypeRecord(name, out var restored));
        Assert.Null(restored!.EmittedMemberCount); // the true pre-image, not 1
    }

    // ── the outer-journal transfer the driver relies on ─────────────────────────────────────────

    /// <summary>
    /// The exact mechanism gap #2 keys on: a settled render moves its pre-images into an outer journal
    /// instead of committing them, so the stamp stays on the record (the compile sees it) yet the outer
    /// loop can still undo it before the next render.
    /// </summary>
    [Fact]
    public void TransferTo_KeepsTheSettledStampButLeavesItUndoableByTheOuterJournal()
    {
        var (db, name) = FrozenDbWithOneType();
        var outer = new EmissionFactsJournal();

        using (var attempt = BeginAttempt())
        {
            db.ApplyEmissionResult(name, new TypeEmissionResult { EmittedMemberCount = 7 });
            Assert.Equal(1, attempt.Journal.Count);

            attempt.Journal.TransferTo(outer);

            Assert.Equal(0, attempt.Journal.Count); // inner log drained
            Assert.Equal(1, outer.Count);           // pre-image now owned by the outer loop
        }

        // The settled render's stamp survives on the record — it is what the wrapper compile reads.
        Assert.True(db.TryGetTypeRecord(name, out var stamped));
        Assert.Equal(7, stamped!.EmittedMemberCount);

        // Yet the outer journal can still rewind it to baseline before the loop's next render.
        outer.RestoreInto(db);

        Assert.True(db.TryGetTypeRecord(name, out var restored));
        Assert.Null(restored!.EmittedMemberCount);
    }

    /// <summary>
    /// First-write-per-type also governs the transfer destination: two renders that each stamp the same
    /// type transfer only the earliest pre-image, so restoring the outer journal returns the record to
    /// the true pre-loop baseline rather than to the first render's stamp.
    /// </summary>
    [Fact]
    public void TransferTo_KeepsTheEarliestPreImageAcrossRenders()
    {
        var (db, name) = FrozenDbWithOneType();
        var outer = new EmissionFactsJournal();

        using (var first = BeginAttempt())
        {
            db.ApplyEmissionResult(name, new TypeEmissionResult { EmittedMemberCount = 1 });
            first.Journal.TransferTo(outer);
        }

        using (var second = BeginAttempt())
        {
            db.ApplyEmissionResult(name, new TypeEmissionResult { EmittedMemberCount = 2 });
            second.Journal.TransferTo(outer);
        }

        Assert.Equal(1, outer.Count); // earliest pre-image only

        outer.RestoreInto(db);

        Assert.True(db.TryGetTypeRecord(name, out var restored));
        Assert.Null(restored!.EmittedMemberCount); // the pre-loop baseline, not render 1's stamp of 1
    }
}
