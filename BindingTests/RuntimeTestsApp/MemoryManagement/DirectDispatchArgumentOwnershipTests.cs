// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.MemoryManagement;

/// <summary>
/// Deterministic regression probe for argument ownership on the DIRECT CallConvSwift arm — the
/// arm whose P/Invoke names Swift's own <c>$s…</c> symbol with no <c>@_cdecl</c> wrapper frame in
/// between.
///
/// <para>SILGen lowers an initializer as <c>(@owned A, …) -&gt; @owned Self</c> and a setter as
/// <c>(@owned Value, @owned Index…, @inout self) -&gt; ()</c>: the callee RELEASES what it was
/// handed. A plain method borrows instead — <c>(@guaranteed A, @guaranteed self)</c> — and a
/// Swift-source wrapper is a borrowing frame too, because SILGen mints the transfer itself when
/// that frame forwards to a consuming callee. So the direct arm is the only one that has to mint
/// the transfer on the C# side, and before that mint existed it passed a reference-bearing frozen
/// struct (or a Swift String) as a borrowed bitwise copy into a slot the callee then released —
/// an under-retain that freed the payload while the caller's wrapper still owned it.</para>
///
/// <para>The class payload is what makes this deterministic rather than a crash. It feeds the same
/// allocation counters <see cref="LifetimeTracker"/> reads, so a missing hand-over shows up
/// synchronously as the payload dying while a live C# wrapper still owns it, instead of as heap
/// corruption that surfaces later as a finalizer-thread SIGSEGV in an unrelated type. Every string
/// here is deliberately past 15 UTF-8 bytes: at or below that a Swift String is the inline small
/// form with no refcount to get wrong, which is exactly why the whole corpus stayed green through
/// this defect.</para>
///
/// <para>The borrowing arms are covered too, as negative controls: minting a transfer where the
/// callee only borrows leaks the argument — the opposite failure, and just as silent.</para>
/// </summary>
public class DirectDispatchArgumentOwnershipTests : TestBase
{
    public DirectDispatchArgumentOwnershipTests(TestResults results) : base(results) { }

    // Past the 15-byte inline small-string boundary, so the note's bytes live on a refcounted
    // storage object rather than inside the value.
    private const string LongNote = "owned-argument-note-well-past-the-small-string-form";
    private const string OtherNote = "owned-argument-replacement-note-also-past-that-form";

    private static void DrainFinalizers()
    {
        for (int i = 0; i < 4; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        GC.Collect();
    }

    /// <summary>
    /// Initializer arm. <c>OwnedArgInitHost.init(payload:)</c> takes the nested frozen carrier over
    /// the direct symbol, so it consumes the carrier's references. The C# wrapper for the witness is
    /// deliberately still alive at the assertion: after the host and the carrier are both disposed,
    /// exactly ONE reference must remain — the wrapper's own. Without the hand-over the callee's
    /// release takes a count nobody transferred and the live count reads 0.
    /// </summary>
    public void TestInitializerHandsOverTheFrozenCarrierReferences()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        var witness = new OwnedArgWitness(11);
        LifetimeTracker.AssertLiveCount(1, "the witness object is live once its C# wrapper exists");

        using (var carrier = new OwnedArgInitHost.Carrier(witness, LongNote))
        using (var host = new OwnedArgInitHost(carrier))
        {
            AssertEqual(LongNote, host.GetHostNote(), "the host must read back the note it was constructed with");
            AssertEqual(11, host.GetWitnessTag(), "the host must read back the witness it was constructed with");
            AssertEqual(LongNote, carrier.ReadNote(), "the caller's own carrier must survive the initializer intact");
        }

        DrainFinalizers();
        LifetimeTracker.AssertLiveCount(1,
            "a consuming initializer must be handed a reference of its own; the C# witness wrapper still owns one");

        AssertTrue(witness.IsAlive(), "the witness must still be callable after the host and carrier are gone");

        witness.Dispose();
        DrainFinalizers();
        LifetimeTracker.AssertLiveCount(0, "disposing the last C# wrapper must release the final reference");

        TestLogger.Info("direct initializer: the frozen carrier's references survived the callee's release");
    }

    /// <summary>
    /// Setter arm, kept separate from the initializer so a fix covering construction but not
    /// assignment still leaves a red here. A subscript setter consumes its new value AND its
    /// indices; the assignment overwrites the slot, so afterwards each witness is owned solely by
    /// its own C# wrapper and both must survive disposal of the host and the carriers.
    /// </summary>
    public void TestSetterHandsOverTheFrozenCarrierReferences()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        var first = new OwnedArgWitness(1);
        var second = new OwnedArgWitness(2);
        LifetimeTracker.AssertLiveCount(2, "both witness objects are live once their C# wrappers exist");

        using (var firstCarrier = new OwnedArgInitHost.Carrier(first, LongNote))
        using (var host = new OwnedArgSetterHost(firstCarrier))
        using (var secondCarrier = new OwnedArgInitHost.Carrier(second, OtherNote))
        {
            AssertEqual(LongNote, host.NoteAt(0), "the host must start out holding the carrier it was built from");

            host[0] = secondCarrier;

            AssertEqual(OtherNote, host.NoteAt(0), "the assignment must land the replacement carrier in the slot");
            AssertEqual(2, host.TagAt(0), "the replacement carrier's witness must be the one in the slot");
            AssertEqual(OtherNote, secondCarrier.ReadNote(), "the caller's own carrier must survive the assignment intact");
        }

        DrainFinalizers();
        LifetimeTracker.AssertLiveCount(2,
            "a consuming setter must be handed a reference of its own; both C# witness wrappers still own one");

        AssertTrue(first.IsAlive(), "the overwritten carrier's witness must outlive the assignment");
        AssertTrue(second.IsAlive(), "the assigned carrier's witness must outlive the host");

        first.Dispose();
        second.Dispose();
        DrainFinalizers();
        LifetimeTracker.AssertLiveCount(0, "disposing the last C# wrappers must release the final references");

        TestLogger.Info("direct subscript setter: the frozen carrier's references survived the callee's release");
    }

    /// <summary>
    /// Bare-String arm. A String parameter is lowered through a transient <c>SwiftString</c> the
    /// emitted code disposes on the way out, so a consuming callee handed that transient's only
    /// count leaves the stored String pointing at storage the transient then frees. The churn loop
    /// is what turns that from latent into observable: it allocates and frees enough String storage
    /// to reuse a prematurely-freed block, after which the host must still read back its own text.
    /// </summary>
    public void TestFailableStringInitializerHandsOverTheStringStorage()
    {
        DrainFinalizers();

        AssertTrue(OwnedArgStringHost.TryCreate(LongNote, out var host),
            "a non-empty text must construct the host");

        using (host)
        {
            AssertEqual(LongNote, host.Read(), "the host must read back the text it was constructed with");

            for (int i = 0; i < 256; i++)
            {
                AssertTrue(OwnedArgStringHost.TryCreate($"{OtherNote}-churn-{i}", out var churn),
                    "the churn host must construct");
                churn.Dispose();
            }

            AssertEqual(LongNote, host.Read(),
                "the stored String storage must survive the transient's disposal and the allocation churn");
        }

        // The nil arm is a consuming call too: the callee owns the argument whether or not it
        // returns a value, so the failure path must release exactly what it was handed.
        AssertFalse(OwnedArgStringHost.TryCreate(string.Empty, out _),
            "an empty text must take the failable initializer's nil arm");

        TestLogger.Info("direct failable String initializer: stored storage survived transient disposal + churn");
    }

    /// <summary>
    /// Class arm. A class argument is the one carrier with no marshalling step of its own — the call
    /// site hands the object's payload handle straight to the P/Invoke — so the transfer has nowhere
    /// to be spelled by the marshalling of a value and has to be minted beside the call. Both
    /// witnesses are still owned by live C# wrappers at the assertion, so a missing hand-over on
    /// either the class argument or the carrier beside it reads as a count below 2.
    /// </summary>
    public void TestInitializerHandsOverAClassArgument()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        var direct = new OwnedArgWitness(21);
        var carried = new OwnedArgWitness(22);
        LifetimeTracker.AssertLiveCount(2, "both witness objects are live once their C# wrappers exist");

        using (var carrier = new OwnedArgInitHost.Carrier(carried, LongNote))
        using (var host = new OwnedArgClassHost(direct, carrier))
        {
            AssertEqual(21, host.GetStoredTag(), "the host must read back the class argument it was constructed with");
        }

        DrainFinalizers();
        LifetimeTracker.AssertLiveCount(2,
            "a consuming initializer must be handed a reference of its own for the class argument too");

        AssertTrue(direct.IsAlive(), "the class argument must still be callable after the host is gone");
        AssertTrue(carried.IsAlive(), "the carrier's witness must still be callable after the host is gone");

        direct.Dispose();
        carried.Dispose();
        DrainFinalizers();
        LifetimeTracker.AssertLiveCount(0, "disposing the last C# wrappers must release the final references");

        TestLogger.Info("direct initializer: the class argument survived the callee's release");
    }

    /// <summary>
    /// String-index arm. SILGen lowers a subscript setter as
    /// <c>(@owned NewValue, @owned Index…, @inout self)</c> — the indices are consumed alongside the
    /// new value, which an integer index can never show. The key here carries refcounted storage of
    /// its own and Swift's dictionary keeps it, so an under-retained index frees storage the
    /// dictionary still holds and the later lookups miss (or read through freed memory).
    /// </summary>
    public void TestSubscriptSetterHandsOverAStringIndex()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        var seed = new OwnedArgWitness(31);
        var assigned = new OwnedArgWitness(32);
        LifetimeTracker.AssertLiveCount(2, "both witness objects are live once their C# wrappers exist");

        const string SeedKey = "owned-argument-seed-key-past-the-small-string-form";

        using (var seedCarrier = new OwnedArgInitHost.Carrier(seed, LongNote))
        using (var host = new OwnedArgKeyedHost(SeedKey, seedCarrier))
        using (var replacement = new OwnedArgInitHost.Carrier(assigned, OtherNote))
        {
            AssertEqual(LongNote, host.NoteFor(SeedKey), "the host must start out keyed by the String it was built with");

            for (int i = 0; i < 64; i++)
                host[$"{OtherNote}-index-{i}"] = replacement;

            // Churn enough String storage to reuse any block a missing index hand-over freed, then
            // read every key back: a dead key misses the lookup instead of returning its slot.
            for (int i = 0; i < 256; i++)
            {
                using var churn = new OwnedArgInitHost.Carrier(assigned, $"{LongNote}-churn-{i}");
                AssertEqual($"{LongNote}-churn-{i}", churn.ReadNote(), "the churn carrier must read back its own note");
            }

            for (int i = 0; i < 64; i++)
                AssertEqual(OtherNote, host.NoteFor($"{OtherNote}-index-{i}"),
                    "every assigned key must still resolve to its slot after the churn");

            AssertEqual(LongNote, host.NoteFor(SeedKey), "the seed key must survive the assignments and the churn");
            AssertEqual(65, host.GetSlotCount(), "each distinct key must have landed its own slot");
        }

        DrainFinalizers();
        LifetimeTracker.AssertLiveCount(2,
            "a consuming setter must be handed a reference of its own for every assignment");

        seed.Dispose();
        assigned.Dispose();
        DrainFinalizers();
        LifetimeTracker.AssertLiveCount(0, "disposing the last C# wrappers must release the final references");

        TestLogger.Info("direct subscript setter: 64 String-keyed assignments survived the callee's release");
    }

    /// <summary>
    /// Negative control on the same carriers and the same direct route: a plain method BORROWS its
    /// argument. Minting a transfer here would leak one reference per call, so the loops make any
    /// spurious hand-over unmissable — the final live count reads non-zero instead of 0.
    ///
    /// <para>Every call below is on Swift's own <c>$s…</c> symbol, which is the point: a control
    /// driven through a <c>@_cdecl</c> frame would measure the already-correct wrapper arm and stay
    /// green no matter what the direct arm emits. The class-argument control is the one that goes
    /// red rather than merely staying green, since the tracker counts exactly the objects it
    /// borrows.</para>
    /// </summary>
    public void TestBorrowingCallsDoNotHandOverTheirArguments()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        var witness = new OwnedArgWitness(7);
        LifetimeTracker.AssertLiveCount(1, "the witness object is live once its C# wrapper exists");

        using (var carrier = new OwnedArgInitHost.Carrier(witness, LongNote))
        using (var host = new OwnedArgInitHost(carrier))
        using (var other = new OwnedArgInitHost.Carrier(witness, LongNote))
        {
            for (int i = 0; i < 128; i++)
                AssertTrue(host.MatchesNote(other), "the borrowing comparison must keep matching across repeats");

            // The bare-String borrow, on the direct arm rather than through a wrapper: the nested
            // carrier beside it is what declines the wrapper for this member.
            for (int i = 0; i < 128; i++)
                AssertTrue(host.NoteMatches(LongNote, other),
                    "the borrowing String comparison must keep matching across repeats");
        }

        using (var carrier = new OwnedArgInitHost.Carrier(witness, LongNote))
        using (var classHost = new OwnedArgClassHost(witness, carrier))
        {
            for (int i = 0; i < 128; i++)
                AssertEqual(14, classHost.BorrowedTag(witness, carrier),
                    "the borrowing class-argument call must keep returning the same sum across repeats");
        }

        DrainFinalizers();
        LifetimeTracker.AssertLiveCount(1,
            "borrowing calls leave the caller owning its argument; only the C# witness wrapper's reference remains");

        witness.Dispose();
        DrainFinalizers();
        LifetimeTracker.AssertLiveCount(0,
            "a borrowing call must not have minted a reference of its own; the leaked ones would still be live here");

        TestLogger.Info("direct borrowing calls: 128 repeats each left no extra reference behind");
    }
}
