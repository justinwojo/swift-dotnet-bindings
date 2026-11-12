// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Linq;
using RuntimeTestsApp.Infrastructure;
using Swift;
using Swift.Runtime;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Closures;

/// <summary>
/// Members taking <c>@convention(c)</c> callbacks, reached through their <c>@_cdecl</c> wrapper: the
/// wrapper takes self as an ordinary pointer and passes the C function pointer straight through, so
/// no Swift-convention transition is made and no self register is involved. Each shape the wrapper
/// handles around a C callback has its own check: an error with a scalar result, a frozen result
/// written through the result buffer, owned class references in both directions, a Swift closure
/// beside a C callback, an optional C callback, a re-entering callback, initializers, and the
/// static and generic members of a resilient struct.
/// </summary>
public class ConventionCWrapperTests : TestBase
{
    public ConventionCWrapperTests(TestResults results) : base(results) { }

    /// <summary>
    /// Every member takes the wrapper route on every platform: one import per member, named by a
    /// wrapper symbol and declared with the C convention, with no Swift-convention import and no
    /// native bridge twin beside it.
    /// </summary>
    public void TestConventionCMembersRouteThroughCdeclWrappers()
    {
        foreach (var member in new[] { "accumulate", "spread", "derive", "blend", "mapIfPresent", "applyTwice" })
            ImportRoutes.AssertCdeclWrapper(typeof(CCallbackLedger), member);
        ImportRoutes.AssertCdeclWrapper(typeof(CCallbackSeeded), "init");
        ImportRoutes.AssertCdeclWrapper(typeof(CCallbackSeed), "init");
        foreach (var member in new[] { "init", "scaled", "combine" })
            ImportRoutes.AssertCdeclWrapper(typeof(CCallbackTally), member);
    }

    // ---- error + scalar result -------------------------------------------------------------------

    /// <summary>
    /// Success, error, success on one receiver: the error surfaces as a managed exception, the
    /// receiver's state is left as it was, and the receiver keeps working.
    /// </summary>
    public void TestAccumulate_SuccessErrorSuccessKeepsTheReceiver()
    {
        using var ledger = new CCallbackLedger(total: 5);

        AssertEqual(12L, ledger.Accumulate(7, v => v), "first success adds the mapped delta");
        AssertThrows<SwiftException>(() => ledger.Accumulate(4, v => -v), "a negative mapped delta throws");
        AssertEqual(12L, ledger.Total, "the throwing call leaves the receiver unchanged");
        AssertEqual(15L, ledger.Accumulate(3, v => v), "the receiver keeps working after the error");
        AssertEqual(15L, ledger.Total, "the receiver holds the second success");
    }

    /// <summary>Errors and successes alternate many times, so a stale error or receiver shows up.</summary>
    public void TestAccumulate_AlternatingErrorsNeverLeak()
    {
        using var ledger = new CCallbackLedger(total: 0);
        long expected = 0;
        for (int i = 1; i <= 200; i++)
        {
            if (i % 3 == 0)
            {
                AssertThrows<SwiftException>(() => ledger.Accumulate(i, v => -v), $"call {i} throws");
            }
            else
            {
                expected += i;
                AssertEqual(expected, ledger.Accumulate(i, v => v), $"call {i} succeeds");
            }
        }
        AssertEqual(expected, ledger.Total, "running total after the alternation");
    }

    // ---- frozen result through the result buffer -------------------------------------------------

    public void TestSpread_FrozenResultReadsBackEveryInput()
    {
        using var ledger = new CCallbackLedger(total: 1000);
        var result = ledger.Spread(10, 20, v => v * 3);

        AssertEqual(1030L, result.A, "a = total + map(x)");
        AssertEqual(1060L, result.B, "b = total + map(y)");
        AssertEqual(90L, result.C, "c = map(x + y)");
        AssertEqual(10L, result.D, "d = x");
        AssertEqual(20L, result.E, "e = y");
        AssertEqual(1000L, ledger.Total, "spread leaves the receiver unchanged");
    }

    /// <summary>Each result names its own receiver across many receivers.</summary>
    public void TestSpread_EachReceiverIsItsOwnSelf()
    {
        var ledgers = Enumerable.Range(1, 16).Select(i => new CCallbackLedger(total: i * 100L)).ToArray();
        try
        {
            for (int i = 0; i < ledgers.Length; i++)
            {
                var result = ledgers[i].Spread(i, -i, v => v);
                AssertEqual((i + 1) * 100L + i, result.A, $"receiver {i}: a");
                AssertEqual((i + 1) * 100L - i, result.B, $"receiver {i}: b");
                AssertEqual(0L, result.C, $"receiver {i}: c");
            }
        }
        finally
        {
            foreach (var ledger in ledgers)
                ledger.Dispose();
        }
    }

    // ---- owned class references ------------------------------------------------------------------

    /// <summary>
    /// An owned class reference goes in and a new one comes out: once both managed wrappers are
    /// disposed every tracked Swift object is gone, and none goes early.
    /// </summary>
    public void TestDerive_OwnershipBalances()
    {
        LifetimeTracker.RunWithLeakCheck(() =>
        {
            using var ledger = new CCallbackLedger(total: 0);
            using var source = TestLibFunctions.CreateTrackedObject(7, "seed");
            for (int i = 0; i < 64; i++)
            {
                using var derived = ledger.Derive(source, v => v * 3);
                AssertEqual(21, derived.ObjectId, $"iteration {i}: derived id");
                AssertEqual("seed+", derived.Label.ToString(), $"iteration {i}: derived label");
                AssertEqual(7, source.ObjectId, $"iteration {i}: the argument stays alive and unchanged");
            }
            LifetimeTracker.AssertLiveCount(1, "only the source may be live once every derived object is disposed");
        }, "CCallbackLedger.Derive");
    }

    // ---- a Swift closure beside a C callback -----------------------------------------------------

    public void TestBlend_SwiftClosureBesideCCallback()
    {
        using var ledger = new CCallbackLedger(total: 0);
        AssertEqual(6021L, ledger.Blend(7, v => v - 1, v => v * 3), "adjust(7) * 1000 + map(7)");
        AssertEqual(-3009L, ledger.Blend(-3, v => v, v => v * 3), "adjust(-3) * 1000 + map(-3)");
    }

    // ---- optional C callback ---------------------------------------------------------------------

    public void TestMapIfPresent_WithAndWithoutCallback()
    {
        using var ledger = new CCallbackLedger(total: 0);
        AssertEqual(42L, ledger.MapIfPresent(21, v => v * 2), "a passed callback maps the value");
        AssertEqual(-1L, ledger.MapIfPresent(21, null), "no callback reads as none");
        AssertEqual(5L, ledger.MapIfPresent(4, v => v + 1), "a later callback replaces the earlier one");
    }

    // ---- re-entrancy -----------------------------------------------------------------------------

    /// <summary>
    /// The outer callback re-enters <c>ApplyTwice</c> on the same receiver with a different callback.
    /// The inner call must see its own callback, and the outer call's second invocation must still
    /// reach the outer callback once the inner call has returned.
    /// </summary>
    public void TestApplyTwice_ReentrantCallbackKeepsTheOuterCallback()
    {
        using var ledger = new CCallbackLedger(total: 0);
        bool reentered = false;
        long Outer(long v)
        {
            if (!reentered)
            {
                reentered = true;
                AssertEqual(28L, ledger.ApplyTwice(7, y => y * 2), "inner applyTwice(y * 2, 7) = 14 + 14");
            }
            return v;
        }

        AssertEqual(20L, ledger.ApplyTwice(10, Outer), "outer applyTwice(identity, 10) = 10 + 10");
        AssertTrue(reentered, "the outer callback re-entered the member");
    }

    // ---- initializers ----------------------------------------------------------------------------

    public void TestSeededClassInitializer()
    {
        using var seeded = new CCallbackSeeded(6, v => v * 7);
        AssertEqual(42L, seeded.Value, "the initializer stored map(seed)");
    }

    public void TestSeedFrozenStructInitializer()
    {
        var seed = new CCallbackSeed(9, v => v + 33);
        AssertEqual(42L, seed.Value, "the initializer stored map(seed)");
    }

    // ---- a resilient struct ----------------------------------------------------------------------

    public void TestTally_InitializerAndInstanceMember()
    {
        using var tally = new CCallbackTally(6, v => v + 1);
        AssertEqual(7L, tally.Count, "the initializer stored map(seed)");
        AssertEqual(70L, tally.Scaled(5, v => v * 2), "count * map(x)");
        AssertEqual(7L, tally.Count, "scaled leaves the value unchanged");
    }

    public void TestTally_StaticMember()
    {
        AssertEqual(23L, CCallbackTally.Combine(3, 20, (x, y) => x + y), "map(x, y)");
        AssertEqual(-17L, CCallbackTally.Combine(3, 20, (x, y) => x - y), "a second callback replaces the first");
    }

    /// <summary>
    /// The member's own type argument reaches Swift as metadata beside the C callback, so the
    /// argument's size tells which metadata arrived.
    /// </summary>
    public void TestTally_MemberWithItsOwnGenericParameter()
    {
        using var tally = new CCallbackTally(100, v => v);
        AssertEqual(8115L, tally.Weigh(12L, 5, v => v * 3), "an Int64 argument: 8 * 1000 + 100 + 15");
        AssertEqual(4115L, tally.Weigh(12, 5, v => v * 3), "an Int32 argument: 4 * 1000 + 100 + 15");
    }
}
