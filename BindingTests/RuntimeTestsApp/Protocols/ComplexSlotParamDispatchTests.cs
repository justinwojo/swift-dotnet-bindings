// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Protocols;

/// <summary>
/// The borrowed-slot copy-out arms that had no end-to-end exercise: a <c>@frozen</c> struct that
/// owns memory, <c>Foundation.Data</c>, a tuple mixing a scalar with a reference-backed element, a
/// tuple of two wrapper-projected elements, and Optionals of the memory-owning ones.
///
/// <para>
/// Every one of these arrives as the ADDRESS of a slot Swift owns and deinitializes the moment the
/// receiver returns, and every one projects to a managed wrapper rather than a blittable value. A
/// bitwise read reinterprets Swift's first payload word — a String's COW storage pointer, a
/// <c>Data</c> backing store, a tuple element's reference — as a managed object reference, which
/// faults the first time the value is touched. So each test asserts on BOTH sides: what the C#
/// implementation saw, and the Swift driver's description of the ORIGINAL taken after the receiver
/// returned, which is what a receiver that consumed or destroyed the borrowed source would break.
/// </para>
/// </summary>
public class ComplexSlotParamDispatchTests : TestBase
{
    public ComplexSlotParamDispatchTests(TestResults results) : base(results) { }

    internal static void DrainFinalizers()
    {
        for (int i = 0; i < 4; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        GC.Collect();
    }

    // ---- @frozen struct that owns memory ----

    /// <summary>
    /// A <c>@frozen</c> struct with a String field: frozen layout, but not POD. Frozen-ness is
    /// exactly what makes this arm tempting to read bitwise, and the String field is what makes
    /// that unsound.
    /// </summary>
    public void TestFrozenWithMemoryParamReceived()
    {
        var impl = new ComplexSlotReceiverImpl();
        var driver = new ComplexSlotDriver();

        var originalAfter = driver.DriveFrozenLabel(impl, text: "row", weight: 17);

        AssertTrue(impl.FrozenLabelCalled, "onFrozenLabel(_:) fired into the C# impl");
        AssertEqual("row@17", impl.LastFrozenLabelSummary,
            "frozen-with-memory struct read correctly out of the borrowed slot");
        AssertEqual("row", impl.LastFrozenLabelText, "String field of the frozen struct is readable in C#");
        AssertEqual(17, impl.LastFrozenLabelWeight, "scalar field of the frozen struct survived the copy-out");
        AssertEqual("row@17", originalAfter, "Swift's original frozen struct still intact after the callback");
        GC.KeepAlive(impl);
    }

    // ---- Foundation.Data ----

    /// <summary>
    /// <c>Foundation.Data</c> owns a heap buffer. The receiver materializes it to a
    /// <c>byte[]</c> synchronously, so both the length word and the bytes themselves have to have
    /// survived the copy-out — a short read reports the right count with wrong bytes, and a
    /// reinterpreted read reports neither.
    /// </summary>
    public void TestDataParamReceived()
    {
        var impl = new ComplexSlotReceiverImpl();
        var driver = new ComplexSlotDriver();

        var originalAfter = driver.DriveData(impl, count: 10);

        AssertTrue(impl.DataCalled, "onData(_:) fired into the C# impl");
        AssertEqual(10, impl.LastDataLength, "Data byte count survived the copy-out");
        AssertEqual("10:45", impl.LastDataSummary, "Data bytes survived the copy-out (0..9 sums to 45)");
        AssertEqual("10:45", originalAfter, "Swift's original Data still intact after the callback");
        GC.KeepAlive(impl);
    }

    /// <summary>
    /// A larger buffer, so the arm is exercised past anything that could fit inline in the
    /// existential or be recovered by luck from a single word.
    /// </summary>
    public void TestDataParamReceived_LargeBuffer()
    {
        var impl = new ComplexSlotReceiverImpl();
        var driver = new ComplexSlotDriver();

        var originalAfter = driver.DriveData(impl, count: 4096);

        AssertEqual(4096, impl.LastDataLength, "4 KiB Data byte count survived the copy-out");
        AssertEqual(originalAfter, impl.LastDataSummary,
            "4 KiB Data content the receiver saw matches what Swift still holds");
        GC.KeepAlive(impl);
    }

    // ---- Mixed tuples ----

    /// <summary>
    /// <c>(Int32, String)</c>: bitwise-unreadable because of the String, so the WHOLE tuple has to
    /// go through the runtime element walk — including the scalar element, which a partial fix
    /// would still get right by accident.
    /// </summary>
    public void TestScalarTupleParamReceived()
    {
        var impl = new ComplexSlotReceiverImpl();
        var driver = new ComplexSlotDriver();

        var originalAfter = driver.DriveScalarTuple(impl, number: 42, text: "tup");

        AssertTrue(impl.ScalarTupleCalled, "onScalarTuple(_:) fired into the C# impl");
        AssertEqual(42, impl.LastScalarTupleNumber, "scalar tuple element survived the copy-out");
        AssertEqual("tup", impl.LastScalarTupleText, "String tuple element survived the copy-out");
        AssertEqual("42/tup", originalAfter, "Swift's original tuple still intact after the callback");
        GC.KeepAlive(impl);
    }

    /// <summary>
    /// <c>(FrozenSlotLabel, ComplexSlotTone)</c>: two wrapper-projected elements, so neither
    /// element can carry the read on its own. The payload-free enum element is also the one whose
    /// one-byte Swift discriminator over-reads at the C# carrier's four-byte width.
    /// </summary>
    public void TestCompositeTupleParamReceived_Warm()
    {
        AssertCompositeTupleRoundTrips(warm: true, expectedTone: ComplexSlotTone.Warm, expected: "comp@7/warm");
    }

    /// <summary>The other enum case, so a stray zero cannot pass the tone assertion by accident.</summary>
    public void TestCompositeTupleParamReceived_Cool()
    {
        AssertCompositeTupleRoundTrips(warm: false, expectedTone: ComplexSlotTone.Cool, expected: "comp@7/cool");
    }

    private void AssertCompositeTupleRoundTrips(bool warm, ComplexSlotTone expectedTone, string expected)
    {
        var impl = new ComplexSlotReceiverImpl();
        var driver = new ComplexSlotDriver();

        var originalAfter = driver.DriveCompositeTuple(impl, text: "comp", weight: 7, warm: warm);

        AssertTrue(impl.CompositeTupleCalled, "onCompositeTuple(_:) fired into the C# impl");
        AssertEqual(expected, impl.LastCompositeTupleSummary,
            "both wrapper-projected tuple elements read correctly out of the borrowed slot");
        AssertEqual(expectedTone, impl.LastCompositeTupleTone,
            "one-byte enum tuple element read at the Swift width, not the C# carrier width");
        AssertEqual(expected, originalAfter, "Swift's original composite tuple intact after the callback");
        GC.KeepAlive(impl);
    }

    // ---- Optionals of the memory-owning arms ----

    /// <summary>Optional frozen-with-memory struct, non-nil.</summary>
    public void TestOptionalFrozenWithMemoryParamReceived_NonNil()
    {
        var impl = new ComplexSlotReceiverImpl();
        var driver = new ComplexSlotDriver();

        var originalAfter = driver.DriveOptionalFrozenLabel(impl, text: "opt", weight: 4);

        AssertTrue(impl.OptionalFrozenLabelCalled, "onOptionalFrozenLabel(_:) fired into the C# impl");
        AssertTrue(impl.LastOptionalFrozenLabelPresent, "optional frozen struct delivered non-nil");
        AssertEqual("opt@4", impl.LastOptionalFrozenLabelSummary,
            "Optional<@frozen struct owning memory> read correctly out of the borrowed slot");
        AssertEqual("opt@4", originalAfter, "Swift's original optional frozen struct intact after the callback");
        GC.KeepAlive(impl);
    }

    /// <summary>
    /// Optional frozen-with-memory struct, nil. A struct that owns memory has no spare bits, so the
    /// Optional carries an out-of-line tag — the read must consult it rather than testing the
    /// payload word for zero.
    /// </summary>
    public void TestOptionalFrozenWithMemoryParamReceived_Nil()
    {
        var impl = new ComplexSlotReceiverImpl();
        var driver = new ComplexSlotDriver();

        driver.DriveNilFrozenLabel(impl);

        AssertTrue(impl.OptionalFrozenLabelCalled, "onOptionalFrozenLabel(_:) fired into the C# impl");
        AssertFalse(impl.LastOptionalFrozenLabelPresent, "optional frozen struct delivered nil");
        AssertEqual("nil", impl.LastOptionalFrozenLabelSummary, "nil optional described as nil by Swift");
        GC.KeepAlive(impl);
    }

    /// <summary>Optional <c>Data</c>, non-nil.</summary>
    public void TestOptionalDataParamReceived_NonNil()
    {
        var impl = new ComplexSlotReceiverImpl();
        var driver = new ComplexSlotDriver();

        var originalAfter = driver.DriveOptionalData(impl, count: 5);

        AssertTrue(impl.OptionalDataCalled, "onOptionalData(_:) fired into the C# impl");
        AssertTrue(impl.LastOptionalDataPresent, "optional Data delivered non-nil");
        AssertEqual("5:10", impl.LastOptionalDataSummary,
            "Optional<Data> read correctly out of the borrowed slot (0..4 sums to 10)");
        AssertEqual("5:10", originalAfter, "Swift's original optional Data intact after the callback");
        GC.KeepAlive(impl);
    }

    /// <summary>Optional <c>Data</c>, nil — the case that must not fault on the tag read.</summary>
    public void TestOptionalDataParamReceived_Nil()
    {
        var impl = new ComplexSlotReceiverImpl();
        var driver = new ComplexSlotDriver();

        driver.DriveNilData(impl);

        AssertTrue(impl.OptionalDataCalled, "onOptionalData(_:) fired into the C# impl");
        AssertFalse(impl.LastOptionalDataPresent, "optional Data delivered nil");
        GC.KeepAlive(impl);
    }

    // ---- Repeated dispatch of the memory-owning arms ----

    /// <summary>
    /// The String storage behind a frozen-with-memory struct is heap memory LifetimeTracker cannot
    /// count, so the assertion available here is that the ORIGINAL still describes correctly on the
    /// last of many iterations: an under-retaining copy-out frees storage the Swift original still
    /// points at, and the iteration count makes a stale-but-lucky read implausible.
    /// </summary>
    public void TestFrozenWithMemoryRepeatedDispatchKeepsOriginalIntact()
    {
        var impl = new ComplexSlotReceiverImpl();
        var driver = new ComplexSlotDriver();

        var lastOriginal = driver.DriveFrozenLabelRepeatedly(impl, iterations: 200, text: "loop");

        AssertEqual("loop@199", lastOriginal,
            "Swift's frozen struct still describes correctly after 200 copy-outs");
        AssertEqual("loop@199", impl.LastFrozenLabelSummary,
            "the last dispatched frozen struct arrived intact in C#");
        GC.KeepAlive(impl);
    }

    /// <summary>
    /// Same shape for <c>Data</c>, and here the SAME buffer is dispatched every iteration — so an
    /// under-retaining copy-out that frees the shared backing store shows up on a later read
    /// rather than only at teardown.
    /// </summary>
    public void TestDataRepeatedDispatchKeepsOriginalIntact()
    {
        var impl = new ComplexSlotReceiverImpl();
        var driver = new ComplexSlotDriver();

        var lastOriginal = driver.DriveDataRepeatedly(impl, iterations: 200, count: 64);

        AssertEqual(64, impl.LastDataLength, "Data still 64 bytes after 200 copy-outs");
        AssertEqual(lastOriginal, impl.LastDataSummary,
            "Swift's shared Data buffer and the C# view of it still agree after 200 copy-outs");
        GC.KeepAlive(impl);
    }
}

/// <summary>
/// ARC balance for the borrowed copy-out, measured against an exact live-object count. The arms in
/// <see cref="ComplexSlotParamDispatchTests"/> own heap storage the tracker cannot see (String COW
/// buffers, <c>Data</c> backing stores); this one carries a tracked CLASS inside the borrowed value
/// so over-retaining is one stranded object per callback and under-retaining is a premature
/// deallocation.
/// </summary>
public class TrackedSlotLeakTests : TestBase
{
    public TrackedSlotLeakTests(TestResults results) : base(results) { }

    /// <summary>
    /// A single dispatch, to establish that the tracker is actually observing this fixture before
    /// the loop test asserts a zero. A tracker wired to nothing reports zero leaks forever.
    /// </summary>
    public void TestTrackedBoxParamReceivedAndTracked()
    {
        ComplexSlotParamDispatchTests.DrainFinalizers();
        LifetimeTracker.Reset();

        var summary = DriveOneTrackedBox();

        AssertEqual("solo#11", summary, "tracked box arrived intact and Swift's original still describes correctly");

        var (allocations, _, _) = LifetimeTracker.GetStats();
        AssertTrue(allocations > 0,
            "positive control: the tracked payload really did reach the tracker (a silent zero would " +
            "make the leak assertion below vacuous)");

        ComplexSlotParamDispatchTests.DrainFinalizers();
        LifetimeTracker.AssertNoLeaks("single tracked-box reverse-callback");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string DriveOneTrackedBox()
    {
        var impl = new TrackedSlotReceiverImpl();
        var driver = new TrackedSlotDriver();
        var summary = driver.DriveTrackedBox(impl, tag: 11, note: "solo");
        AssertBothSidesAgree(impl, summary);
        GC.KeepAlive(impl);
        return summary;
    }

    private static void AssertBothSidesAgree(TrackedSlotReceiverImpl impl, string originalAfter)
    {
        // Deliberately not a TestBase assertion: this runs inside the NoInlining frame so the
        // wrapper stays collectible; the caller re-asserts the value.
        if (impl.LastBoxSummary != originalAfter)
        {
            throw new InvalidOperationException(
                $"tracked box mismatch: receiver saw '{impl.LastBoxSummary}', Swift original is '{originalAfter}'");
        }
    }

    /// <summary>
    /// The SAME box is dispatched on every iteration, so exactly one tracked object exists for the
    /// whole loop. Over-retaining strands one per callback; under-retaining would have shown up as
    /// a corrupted description from the driver, which the loop also returns.
    /// </summary>
    public void TestTrackedBoxRepeatedDispatchDoesNotLeak()
    {
        ComplexSlotParamDispatchTests.DrainFinalizers();
        LifetimeTracker.Reset();

        DriveTrackedBoxCallbacks(200);
        ComplexSlotParamDispatchTests.DrainFinalizers();

        LifetimeTracker.AssertNoLeaks(
            "repeated tracked-box reverse-callback must not strand a copied-out payload per dispatch");
        TestLogger.Info("tracked-slot reverse-callback: 200 borrowed copy-outs of one payload, balanced");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void DriveTrackedBoxCallbacks(int n)
    {
        var impl = new TrackedSlotReceiverImpl();
        var driver = new TrackedSlotDriver();
        var last = driver.DriveTrackedBoxRepeatedly(impl, iterations: n, tag: 3);
        if (last != "loop#3")
        {
            throw new InvalidOperationException($"tracked box original corrupted after {n} copy-outs: '{last}'");
        }
        GC.KeepAlive(impl);
    }
}

/// <summary>
/// The key-path arm, held apart because it is the one carrier whose copy-out takes the borrowed
/// CLASS fast path: a Swift key path is a class, so the runtime dereferences the instance pointer
/// in the slot and takes an independent retain rather than extracting a value-witness payload.
/// Isolating it keeps the two ownership shapes independently runnable under <c>--class-filter</c>.
/// </summary>
public class KeyPathSlotParamDispatchTests : TestBase
{
    public KeyPathSlotParamDispatchTests(TestResults results) : base(results) { }

    /// <summary>
    /// The received key path is USED — applied to a bag built in C# — which is what separates "the
    /// pointer looked plausible" from "the object is live and functional".
    /// </summary>
    public void TestKeyPathParamReceivedAndApplied()
    {
        var impl = new KeyPathSlotReceiverImpl();
        var driver = new KeyPathSlotDriver();

        var originalAfter = driver.DriveTitleKeyPath(impl, title: "kp-title");

        AssertTrue(impl.KeyPathCalled, "onTitleKeyPath(_:) fired into the C# impl");
        AssertEqual("applied-title", impl.LastAppliedTitle,
            "the key path the receiver copied out reads its Root's String property");
        AssertEqual("kp-title", originalAfter,
            "Swift's original key path still applies correctly after the callback");
        GC.KeepAlive(impl);
    }

    /// <summary>
    /// Repeated dispatch of the same key path. The class fast path takes one retain per callback,
    /// so a missing release strands one key-path object per dispatch; the assertion available is
    /// that the original still applies on the far side of many of them.
    /// </summary>
    public void TestKeyPathRepeatedDispatchKeepsOriginalUsable()
    {
        var impl = new KeyPathSlotReceiverImpl();
        var driver = new KeyPathSlotDriver();

        for (int i = 0; i < 50; i++)
        {
            var originalAfter = driver.DriveTitleKeyPath(impl, title: "kp-" + i);
            if (originalAfter != "kp-" + i)
            {
                AssertEqual("kp-" + i, originalAfter, $"key path still usable on iteration {i}");
                return;
            }
        }

        AssertEqual("applied-title", impl.LastAppliedTitle, "key path still applies after 50 copy-outs");
        AssertEqual(50, impl.CallCount, "every dispatch reached the C# impl");
        GC.KeepAlive(impl);
    }
}

/// <summary>
/// <c>Result</c> in the one reverse-dispatch position it can legally occupy. A <c>Result</c>-typed
/// ARGUMENT is dropped by the bound-generic gate, so there is no receiver-parameter arm to cover;
/// a getter requirement travels the other way — C# hands a <c>Result</c> back and Swift reads it.
///
/// <para>
/// The conformer returns a Swift-originated value on purpose. The managed <c>FromSuccess</c> /
/// <c>FromFailure</c> factories build C#-only results with no native payload, and marshalling one
/// INTO Swift throws inside an <c>UnmanagedCallersOnly</c> frame, which fail-fasts the process
/// rather than failing a test.
/// </para>
/// </summary>
public class ResultOutcomeDispatchTests : TestBase
{
    public ResultOutcomeDispatchTests(TestResults results) : base(results) { }

    /// <summary>Success case: Swift reaches the payload's class field and String field.</summary>
    public void TestResultOutcomeSuccessReadBySwift()
    {
        var impl = ResultOutcomeReceiverImpl.Success(label: "won", amount: 12, tag: 4);
        var driver = new ResultOutcomeDriver();

        var described = driver.DescribeOutcome(impl);

        AssertEqual("ok/won#12/4", described, "Swift read the success Result the C# conformer returned");
        GC.KeepAlive(impl);
    }

    /// <summary>Failure case, carrying an associated value rather than a bare discriminator.</summary>
    public void TestResultOutcomeFailureReadBySwift()
    {
        var impl = ResultOutcomeReceiverImpl.Failure(code: 77);
        var driver = new ResultOutcomeDriver();

        var described = driver.DescribeOutcome(impl);

        AssertEqual("err/invalid#77", described, "Swift read the failure Result the C# conformer returned");
        GC.KeepAlive(impl);
    }

    /// <summary>
    /// Repeated reads of the same getter. Each read hands Swift a value the C# side still owns, so
    /// an unbalanced hand-off is either a strand per read or a premature release that corrupts a
    /// later one.
    /// </summary>
    public void TestResultOutcomeRepeatedReadsStayStable()
    {
        var impl = ResultOutcomeReceiverImpl.Success(label: "rep", amount: 1, tag: 2);
        var driver = new ResultOutcomeDriver();

        var last = driver.DescribeOutcomeRepeatedly(impl, iterations: 100);

        AssertEqual("ok/rep#1/2", last, "the 100th read of the Result getter still describes correctly");
        AssertEqual(100, impl.ReadCount, "every read reached the C# getter");
        GC.KeepAlive(impl);
    }
}

/// <summary>
/// C# implementation of the generated <c>IComplexSlotReceiver</c>. Each callback records what it
/// received both directly (fields it can read in C#) and by handing the value back to Swift for
/// description — the second is what proves the copy-out produced a usable value rather than a
/// reinterpreted word. Nothing is retained afterwards, so a leak probe can watch wrappers drain.
/// </summary>
internal sealed class ComplexSlotReceiverImpl : IComplexSlotReceiver
{
    public bool FrozenLabelCalled { get; private set; }
    public bool DataCalled { get; private set; }
    public bool ScalarTupleCalled { get; private set; }
    public bool CompositeTupleCalled { get; private set; }
    public bool OptionalFrozenLabelCalled { get; private set; }
    public bool OptionalDataCalled { get; private set; }

    public string LastFrozenLabelSummary { get; private set; } = "";
    public string LastFrozenLabelText { get; private set; } = "";
    public int LastFrozenLabelWeight { get; private set; }

    public int LastDataLength { get; private set; } = -1;
    public string LastDataSummary { get; private set; } = "";

    public int LastScalarTupleNumber { get; private set; }
    public string LastScalarTupleText { get; private set; } = "";

    public string LastCompositeTupleSummary { get; private set; } = "";
    public ComplexSlotTone LastCompositeTupleTone { get; private set; }

    public bool LastOptionalFrozenLabelPresent { get; private set; }
    public string LastOptionalFrozenLabelSummary { get; private set; } = "";

    public bool LastOptionalDataPresent { get; private set; }
    public string LastOptionalDataSummary { get; private set; } = "";

    public void OnFrozenLabel(FrozenSlotLabel label)
    {
        FrozenLabelCalled = true;
        LastFrozenLabelText = label.Text;
        LastFrozenLabelWeight = label.Weight;
        LastFrozenLabelSummary = TestLibFunctions.DescribeFrozenSlotLabel(label);
    }

    public void OnData(byte[] payload)
    {
        DataCalled = true;
        LastDataLength = payload.Length;
        LastDataSummary = SummarizeBytes(payload);
    }

    public void OnScalarTuple((int, string) pair)
    {
        ScalarTupleCalled = true;
        LastScalarTupleNumber = pair.Item1;
        LastScalarTupleText = pair.Item2;
    }

    public void OnCompositeTuple((FrozenSlotLabel, ComplexSlotTone) pair)
    {
        CompositeTupleCalled = true;
        LastCompositeTupleTone = pair.Item2;
        LastCompositeTupleSummary =
            TestLibFunctions.DescribeFrozenSlotLabel(pair.Item1)
            + "/" + TestLibFunctions.DescribeComplexSlotTone(pair.Item2);
    }

    public void OnOptionalFrozenLabel(FrozenSlotLabel? label)
    {
        OptionalFrozenLabelCalled = true;
        LastOptionalFrozenLabelPresent = label != null;
        LastOptionalFrozenLabelSummary = TestLibFunctions.DescribeOptionalFrozenSlotLabel(label);
    }

    public void OnOptionalData(byte[]? payload)
    {
        OptionalDataCalled = true;
        LastOptionalDataPresent = payload != null;
        LastOptionalDataSummary = payload == null ? "nil" : SummarizeBytes(payload);
    }

    /// <summary>
    /// Renders bytes exactly the way the Swift fixture's <c>describeSlotData</c> does, so the two
    /// sides are directly comparable without a second trip across the boundary.
    /// </summary>
    private static string SummarizeBytes(byte[] payload)
    {
        long sum = 0;
        foreach (byte b in payload)
        {
            sum += b;
        }
        return payload.Length + ":" + sum;
    }
}

/// <summary>C# implementation of the generated <c>ITrackedSlotReceiver</c>.</summary>
internal sealed class TrackedSlotReceiverImpl : ITrackedSlotReceiver
{
    public string LastBoxSummary { get; private set; } = "";
    public int CallCount { get; private set; }

    public void OnTrackedBox(TrackedSlotBox box)
    {
        CallCount++;
        LastBoxSummary = TestLibFunctions.DescribeTrackedSlotBox(box);
    }
}

/// <summary>
/// C# implementation of the generated <c>IKeyPathSlotReceiver</c>. It APPLIES the key path it
/// received to a bag it builds itself, which is the operation a merely-plausible pointer fails.
/// </summary>
internal sealed class KeyPathSlotReceiverImpl : IKeyPathSlotReceiver
{
    public bool KeyPathCalled { get; private set; }
    public int CallCount { get; private set; }
    public string LastAppliedTitle { get; private set; } = "";

    public void OnTitleKeyPath(Swift.KeyPath<ComplexSlotBag, string> keyPath)
    {
        KeyPathCalled = true;
        CallCount++;
        using var bag = new ComplexSlotBag("applied-title", 0);
        LastAppliedTitle = TestLibFunctions.ReadComplexSlotBagTitle(bag, keyPath);
    }
}

/// <summary>
/// C# implementation of the generated <c>IResultOutcomeReceiver</c>. The value it returns is one
/// Swift built, held for the object's lifetime, because a C#-constructed <c>SwiftResult</c> has no
/// native payload to hand back.
/// </summary>
internal sealed class ResultOutcomeReceiverImpl : IResultOutcomeReceiver
{
    private readonly Swift.SwiftResult<ComplexSlotPayload, ComplexSlotFault> _outcome;

    private ResultOutcomeReceiverImpl(Swift.SwiftResult<ComplexSlotPayload, ComplexSlotFault> outcome)
    {
        _outcome = outcome;
    }

    public int ReadCount { get; private set; }

    public Swift.SwiftResult<ComplexSlotPayload, ComplexSlotFault> Outcome
    {
        get
        {
            ReadCount++;
            return _outcome;
        }
    }

    public static ResultOutcomeReceiverImpl Success(string label, int amount, int tag)
        => new ResultOutcomeReceiverImpl(TestLibFunctions.MakeComplexSlotSuccess(label, amount, tag));

    public static ResultOutcomeReceiverImpl Failure(int code)
        => new ResultOutcomeReceiverImpl(TestLibFunctions.MakeComplexSlotFailure(code));
}
