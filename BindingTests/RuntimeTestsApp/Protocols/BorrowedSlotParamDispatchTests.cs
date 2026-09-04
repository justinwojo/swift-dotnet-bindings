// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Protocols;

/// <summary>
/// Reverse-dispatch receiver parameters that arrive in a BORROWED Swift slot: the conformance
/// copies the argument into its own local, passes that local's address, and deinitializes it as
/// soon as the receiver returns.
///
/// <para>
/// Two parameter families are unsound to read bitwise out of such a slot. Managed-wrapper value
/// types — a non-frozen struct, an associated-value enum, or an Optional of either — project to
/// C# wrapper <b>classes</b>, so a raw read reinterprets Swift's first payload word as a managed
/// object reference and faults the first time the value is used. Payload-free enums carry a
/// one-byte discriminator in Swift while their C# carrier is <c>enum : int</c>, so a four-byte
/// read reports a case the caller never passed. Both must instead be copied out of the borrowed
/// slot through the value witness, leaving the source intact.
/// </para>
///
/// <para>
/// Each test therefore asserts on <b>both sides</b> of the callback: the C# implementation must
/// see the value the driver sent, and the Swift driver's summary of the ORIGINAL — taken after
/// the receiver returned — must still be correct, which is what a receiver that consumed or
/// destroyed the borrowed source would break.
/// </para>
/// </summary>
public class BorrowedSlotParamDispatchTests : TestBase
{
    public BorrowedSlotParamDispatchTests(TestResults results) : base(results) { }

    private static void DrainFinalizers()
    {
        for (int i = 0; i < 4; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        GC.Collect();
    }

    // ---- Non-frozen struct with a class field and a String field ----

    /// <summary>
    /// The core managed-wrapper repro: a non-frozen struct mixing a class field with a String
    /// field. Describing the received value dereferences both reference fields.
    /// </summary>
    public void TestNonFrozenStructParamReceived()
    {
        var impl = new BorrowedSlotReceiverImpl();
        var driver = new BorrowedSlotDriver();

        var originalAfter = driver.DriveRecord(impl, name: "row", code: 17, tag: 5);

        AssertTrue(impl.RecordCalled, "onRecord(_:) fired into the C# impl");
        AssertEqual("row#17/5", impl.LastRecordSummary,
            "non-frozen struct with class + String fields read correctly out of the borrowed slot");
        AssertEqual("row#17/5", originalAfter,
            "Swift's original struct still intact after the receiver returned");
        GC.KeepAlive(impl);
    }

    // ---- Associated-value enum (class-payload and struct-payload cases) ----

    /// <summary>Associated-value enum, class-payload case.</summary>
    public void TestAssociatedValueEnumParamReceived_ClassPayload()
    {
        var impl = new BorrowedSlotReceiverImpl();
        var driver = new BorrowedSlotDriver();

        var originalAfter = driver.DriveTrackedItem(impl, tag: 9);

        AssertTrue(impl.ItemCalled, "onItem(_:) fired into the C# impl");
        AssertEqual("tracked/9", impl.LastItemSummary,
            "class-payload enum case read correctly out of the borrowed slot");
        AssertEqual("tracked/9", originalAfter, "Swift's original enum still intact after the callback");
        GC.KeepAlive(impl);
    }

    /// <summary>Associated-value enum, struct-payload case (a wrapper nested in a wrapper).</summary>
    public void TestAssociatedValueEnumParamReceived_StructPayload()
    {
        var impl = new BorrowedSlotReceiverImpl();
        var driver = new BorrowedSlotDriver();

        var originalAfter = driver.DriveRecordItem(impl, name: "cell", code: 3, tag: 8);

        AssertTrue(impl.ItemCalled, "onItem(_:) fired into the C# impl");
        AssertEqual("record/cell#3/8", impl.LastItemSummary,
            "struct-payload enum case read correctly out of the borrowed slot");
        AssertEqual("record/cell#3/8", originalAfter, "Swift's original enum still intact after the callback");
        GC.KeepAlive(impl);
    }

    /// <summary>Associated-value enum, payload-free case — no reference to copy, but the same carrier.</summary>
    public void TestAssociatedValueEnumParamReceived_PayloadFreeCase()
    {
        var impl = new BorrowedSlotReceiverImpl();
        var driver = new BorrowedSlotDriver();

        var originalAfter = driver.DriveBlankItem(impl);

        AssertTrue(impl.ItemCalled, "onItem(_:) fired into the C# impl");
        AssertEqual("blank", impl.LastItemSummary, "payload-free case of an associated-value enum");
        AssertEqual("blank", originalAfter, "Swift's original enum still intact after the callback");
        GC.KeepAlive(impl);
    }

    // ---- Payload-free one-byte enum ----

    /// <summary>
    /// Every case of a one-byte payload-free enum. A discriminator read at the C# carrier's
    /// four-byte width reports whatever follows the slot, so each case has to be checked — the
    /// first case is the one a stray zero would still get right by accident.
    /// </summary>
    public void TestPayloadFreeEnumParamReceived()
    {
        var driver = new BorrowedSlotDriver();

        AssertEnumCaseRoundTrips(driver, BorrowedSlotKind.Alpha, "alpha");
        AssertEnumCaseRoundTrips(driver, BorrowedSlotKind.Beta, "beta");
        AssertEnumCaseRoundTrips(driver, BorrowedSlotKind.Gamma, "gamma");
    }

    private void AssertEnumCaseRoundTrips(BorrowedSlotDriver driver, BorrowedSlotKind kind, string expected)
    {
        var impl = new BorrowedSlotReceiverImpl();

        var originalAfter = driver.DriveKind(impl, kind: kind);

        AssertTrue(impl.KindCalled, $"onKind(_:) fired into the C# impl for {expected}");
        AssertEqual(kind, impl.LastKind, $"one-byte enum discriminator '{expected}' read at the Swift width");
        AssertEqual(expected, impl.LastKindSummary,
            $"received '{expected}' case still names its own case when handed back to Swift");
        AssertEqual(expected, originalAfter, $"Swift's original '{expected}' case intact after the callback");
        GC.KeepAlive(impl);
    }

    // ---- Optionals of the two managed-wrapper shapes ----

    /// <summary>Optional non-frozen struct, non-nil.</summary>
    public void TestOptionalNonFrozenStructParamReceived_NonNil()
    {
        var impl = new BorrowedSlotReceiverImpl();
        var driver = new BorrowedSlotDriver();

        var originalAfter = driver.DriveOptionalRecord(impl, name: "opt", code: 4, tag: 2);

        AssertTrue(impl.OptionalRecordCalled, "onOptionalRecord(_:) fired into the C# impl");
        AssertTrue(impl.LastOptionalRecordPresent, "optional struct delivered non-nil");
        AssertEqual("opt#4/2", impl.LastOptionalRecordSummary,
            "Optional<non-frozen struct> read correctly out of the borrowed slot");
        AssertEqual("opt#4/2", originalAfter, "Swift's original optional struct intact after the callback");
        GC.KeepAlive(impl);
    }

    /// <summary>Optional non-frozen struct, nil — the case that must not fault on the tag read.</summary>
    public void TestOptionalNonFrozenStructParamReceived_Nil()
    {
        var impl = new BorrowedSlotReceiverImpl();
        var driver = new BorrowedSlotDriver();

        driver.DriveNilRecord(impl);

        AssertTrue(impl.OptionalRecordCalled, "onOptionalRecord(_:) fired into the C# impl");
        AssertFalse(impl.LastOptionalRecordPresent, "optional struct delivered nil");
        GC.KeepAlive(impl);
    }

    /// <summary>Optional associated-value enum, non-nil.</summary>
    public void TestOptionalAssociatedValueEnumParamReceived_NonNil()
    {
        var impl = new BorrowedSlotReceiverImpl();
        var driver = new BorrowedSlotDriver();

        var originalAfter = driver.DriveOptionalItem(impl, tag: 6);

        AssertTrue(impl.OptionalItemCalled, "onOptionalItem(_:) fired into the C# impl");
        AssertTrue(impl.LastOptionalItemPresent, "optional enum delivered non-nil");
        AssertEqual("tracked/6", impl.LastOptionalItemSummary,
            "Optional<associated-value enum> read correctly out of the borrowed slot");
        AssertEqual("tracked/6", originalAfter, "Swift's original optional enum intact after the callback");
        GC.KeepAlive(impl);
    }

    /// <summary>Optional associated-value enum, nil.</summary>
    public void TestOptionalAssociatedValueEnumParamReceived_Nil()
    {
        var impl = new BorrowedSlotReceiverImpl();
        var driver = new BorrowedSlotDriver();

        driver.DriveNilItem(impl);

        AssertTrue(impl.OptionalItemCalled, "onOptionalItem(_:) fired into the C# impl");
        AssertFalse(impl.LastOptionalItemPresent, "optional enum delivered nil");
        GC.KeepAlive(impl);
    }

    // ---- ARC balance across repeated dispatch ----

    /// <summary>
    /// The copy-out must take exactly one independent reference that the C# wrapper releases when
    /// it drains. Over-retaining strands one tracked payload per callback; under-retaining frees
    /// storage the Swift original still owns, which the driver's post-callback read would have
    /// caught above. Either way the live count must return to zero.
    /// </summary>
    public void TestBorrowedSlotParamRepeatedDispatchDoesNotLeak()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        DriveRecordCallbacks(200);
        DrainFinalizers();

        LifetimeTracker.AssertNoLeaks(
            "repeated non-frozen-struct reverse-callback must not leak the copied-out payload");
        TestLogger.Info("borrowed-slot struct reverse-callback: 200 payloads copied out and released");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void DriveRecordCallbacks(int n)
    {
        var impl = new BorrowedSlotReceiverImpl();
        var driver = new BorrowedSlotDriver();
        driver.DriveRecordRepeatedly(impl, iterations: n, tag: 1);
        GC.KeepAlive(impl);
    }
}

/// <summary>
/// C# implementation of the generated <c>IBorrowedSlotReceiver</c>. Each callback hands the value
/// straight back to Swift for description, which is what proves the copy-out produced a usable
/// value rather than a reinterpreted word — and it deliberately keeps no reference to the payload
/// afterwards, so the leak test can watch the wrappers drain.
/// </summary>
internal sealed class BorrowedSlotReceiverImpl : IBorrowedSlotReceiver
{
    public bool RecordCalled { get; private set; }
    public bool ItemCalled { get; private set; }
    public bool KindCalled { get; private set; }
    public bool OptionalRecordCalled { get; private set; }
    public bool OptionalItemCalled { get; private set; }

    public string LastRecordSummary { get; private set; } = "";
    public string LastItemSummary { get; private set; } = "";
    public string LastKindSummary { get; private set; } = "";
    public string LastOptionalRecordSummary { get; private set; } = "";
    public string LastOptionalItemSummary { get; private set; } = "";

    public BorrowedSlotKind LastKind { get; private set; }
    public bool LastOptionalRecordPresent { get; private set; }
    public bool LastOptionalItemPresent { get; private set; }

    public void OnRecord(BorrowedSlotRecord record)
    {
        RecordCalled = true;
        LastRecordSummary = TestLibFunctions.DescribeBorrowedRecord(record);
    }

    public void OnItem(BorrowedSlotItem item)
    {
        ItemCalled = true;
        LastItemSummary = TestLibFunctions.DescribeBorrowedItem(item);
    }

    public void OnKind(BorrowedSlotKind kind)
    {
        KindCalled = true;
        LastKind = kind;
        LastKindSummary = TestLibFunctions.DescribeBorrowedKind(kind);
    }

    public void OnOptionalRecord(BorrowedSlotRecord? record)
    {
        OptionalRecordCalled = true;
        LastOptionalRecordPresent = record != null;
        LastOptionalRecordSummary = TestLibFunctions.DescribeOptionalBorrowedRecord(record);
    }

    public void OnOptionalItem(BorrowedSlotItem? item)
    {
        OptionalItemCalled = true;
        LastOptionalItemPresent = item != null;
        LastOptionalItemSummary = TestLibFunctions.DescribeOptionalBorrowedItem(item);
    }
}
