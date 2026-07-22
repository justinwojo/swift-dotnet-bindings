// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Protocols;

/// <summary>
/// Regression tests for the tuple-parameter reverse-callback receiver.
///
/// <para>
/// A protocol method parameter that is a tuple with <b>projected</b> elements — e.g.
/// <c>(Date, Date)</c>, which crosses the ABI as <c>ValueTuple&lt;double, double&gt;</c> but is
/// surfaced on the generated interface as <c>(DateTimeOffset, DateTimeOffset)</c> — used to be
/// passed to the C# implementation as the raw ABI carrier (CS1503 at binding compile time).
/// The fix lifts each element through its own Swift→C# conversion inside the receiver.
/// </para>
///
/// <para>
/// These tests implement the generated receiver interface, let a synchronous Swift driver
/// call back into it, and assert the received <c>DateTimeOffset</c> values are the exact
/// Swift-epoch offsets the driver was given — proving the per-element lift converts
/// correctly, not merely that it compiles. The pure-blittable <c>(Int32, Int32)</c> callback
/// guards the passthrough shape the lift must leave untouched.
/// </para>
/// </summary>
public class TupleParamCallbackTests : TestBase
{
    public TupleParamCallbackTests(TestResults results) : base(results) { }

    /// <summary>Swift's reference date — the zero point of <c>Date(timeIntervalSinceReferenceDate:)</c>.</summary>
    private static readonly DateTimeOffset SwiftEpoch = new(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private sealed class DateRangeReceiverImpl : IDateRangeReceiver
    {
        public bool RangeCalled;
        public DateTimeOffset LastStart;
        public DateTimeOffset LastEnd;

        public bool CountsCalled;
        public int LastFirst;
        public int LastSecond;

        public void DidReceiveRange((DateTimeOffset, DateTimeOffset) range)
        {
            RangeCalled = true;
            (LastStart, LastEnd) = range;
        }

        public void DidReceiveCounts((int, int) counts)
        {
            CountsCalled = true;
            (LastFirst, LastSecond) = counts;
        }
    }

    /// <summary>
    /// The core repro: a <c>(Date, Date)</c> tuple flows Swift → C# through the reverse-callback
    /// receiver. Both elements must arrive as exact Swift-epoch-anchored DateTimeOffsets.
    /// </summary>
    public void TestDateTupleParamReceivedPerElementConverted()
    {
        var impl = new DateRangeReceiverImpl();
        var driver = new DateRangeDriver();

        driver.DriveRange(impl, startSeconds: 86_400.0, endSeconds: 172_800.5);

        AssertTrue(impl.RangeCalled, "didReceiveRange(_:) fired into the C# impl");
        AssertEqual(SwiftEpoch.AddSeconds(86_400.0), impl.LastStart, "tuple element 1 converted Date → DateTimeOffset");
        AssertEqual(SwiftEpoch.AddSeconds(172_800.5), impl.LastEnd, "tuple element 2 converted Date → DateTimeOffset");
        GC.KeepAlive(impl);
    }

    /// <summary>Zero-offset boundary: both elements exactly at the Swift epoch.</summary>
    public void TestDateTupleParamReceivedAtEpoch()
    {
        var impl = new DateRangeReceiverImpl();
        var driver = new DateRangeDriver();

        driver.DriveRange(impl, startSeconds: 0.0, endSeconds: 0.0);

        AssertTrue(impl.RangeCalled, "didReceiveRange(_:) fired into the C# impl");
        AssertEqual(SwiftEpoch, impl.LastStart, "epoch element 1 arrives as the Swift reference date");
        AssertEqual(SwiftEpoch, impl.LastEnd, "epoch element 2 arrives as the Swift reference date");
        GC.KeepAlive(impl);
    }

    /// <summary>
    /// Passthrough guard: a pure-blittable tuple must keep its direct shape — the per-element
    /// lift only engages when an element actually projects.
    /// </summary>
    public void TestBlittableTupleParamPassthrough()
    {
        var impl = new DateRangeReceiverImpl();
        var driver = new DateRangeDriver();

        driver.DriveCounts(impl, first: 7, second: -13);

        AssertTrue(impl.CountsCalled, "didReceiveCounts(_:) fired into the C# impl");
        AssertEqual(7, impl.LastFirst, "blittable tuple element 1 passed through");
        AssertEqual(-13, impl.LastSecond, "blittable tuple element 2 passed through");
        GC.KeepAlive(impl);
    }
}
