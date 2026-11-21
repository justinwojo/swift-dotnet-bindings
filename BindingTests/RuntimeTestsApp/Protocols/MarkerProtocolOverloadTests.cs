// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Reflection;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Protocols;

/// <summary>
/// The typed overloads of a marker-protocol parameter (<c>LedgerAmountSink</c> over
/// <c>LedgerAmountTarget</c>, an empty protocol only primitives conform to). Each overload calls its
/// own wrapper, which takes the concrete value and lets Swift box it, so the member reads back the
/// dynamic type and value it was given. A member with another argument besides the marker must get
/// both across: an overload that passed only the marker left the wrapper reading the second
/// argument from whatever the register held.
/// </summary>
public class MarkerProtocolOverloadTests : TestBase
{
    public MarkerProtocolOverloadTests(TestResults results) : base(results) { }

    public void TestEachPrimitiveConformerHasItsOwnOverload()
    {
        foreach (var primitive in new[] { typeof(nint), typeof(double), typeof(float), typeof(int), typeof(bool) })
        {
            var overload = typeof(LedgerAmountSink).GetMethod(
                nameof(LedgerAmountSink.Record), BindingFlags.Public | BindingFlags.Instance, new[] { primitive });
            AssertNotNull(overload, $"Record({primitive.Name}) is declared");
        }
    }

    public void TestVoidOverloadsBoxTheirConformer()
    {
        using var sink = new LedgerAmountSink();
        sink.Record((nint)7);
        AssertEqual("Int:7", sink.LastRecorded.ToString(), "Record(nint) boxes a Swift Int");
        sink.Record(2.5);
        AssertEqual("Double:2.5", sink.LastRecorded.ToString(), "Record(double) boxes a Swift Double");
        sink.Record(1.5f);
        AssertEqual("Float:1.5", sink.LastRecorded.ToString(), "Record(float) boxes a Swift Float");
        sink.Record(-4);
        AssertEqual("Int32:-4", sink.LastRecorded.ToString(), "Record(int) boxes a Swift Int32");
        sink.Record(true);
        AssertEqual("Bool:true", sink.LastRecorded.ToString(), "Record(bool) boxes a Swift Bool");
        sink.Record(false);
        AssertEqual("Bool:false", sink.LastRecorded.ToString(), "Record(bool) boxes false as false");
    }

    public void TestOverloadWithASecondArgumentPassesBoth()
    {
        // Through reflection, so a missing overload is a failure here rather than a build break,
        // and so the old marker-only shape cannot satisfy it.
        AssertNull(typeof(LedgerAmountSink).GetMethod("RecordScaled", new[] { typeof(double) }),
            "no RecordScaled overload drops the scale");
        var overload = typeof(LedgerAmountSink).GetMethod("RecordScaled", new[] { typeof(double), typeof(int) });
        AssertNotNull(overload, "RecordScaled(double, int) is declared");
        using var sink = new LedgerAmountSink();
        foreach (var (amount, scale, expected) in new[] { (1.25, 10, "Double:1.25 x10"), (2.5, 20, "Double:2.5 x20"), (-0.75, 30, "Double:-0.75 x30") })
        {
            overload!.Invoke(sink, new object[] { amount, scale });
            AssertEqual(expected, sink.LastRecorded.ToString(), $"RecordScaled(double, int) passes the amount and the scale ({scale})");
        }
        sink.RecordScaled((nint)3, 4);
        AssertEqual("Int:3 x4", sink.LastRecorded.ToString(), "RecordScaled(nint, int) passes both");
        sink.RecordScaled(true, -1);
        AssertEqual("Bool:true x-1", sink.LastRecorded.ToString(), "RecordScaled(bool, int) passes both");
    }

    public void TestBuilderOverloadReturnsTheReceiver()
    {
        using var sink = new LedgerAmountSink();
        using var returned = sink.Adding(9.25);
        AssertEqual("adding Double:9.25", sink.LastRecorded.ToString(), "Adding(double) boxes a Swift Double");
        AssertEqual("adding Double:9.25", returned.LastRecorded.ToString(), "Adding returns the same Swift object");
        sink.Adding((nint)11).Dispose();
        AssertEqual("adding Int:11", returned.LastRecorded.ToString(), "a later call is seen through the returned reference");
    }

    public void TestStaticOverloadBoxesItsConformer()
    {
        LedgerAmountSink.RecordStatic((nint)21);
        AssertEqual("Int:21", LedgerAmountSink.LastStaticRecorded.ToString(), "RecordStatic(nint) boxes a Swift Int");
        LedgerAmountSink.RecordStatic(3.0f);
        AssertEqual("Float:3.0", LedgerAmountSink.LastStaticRecorded.ToString(), "RecordStatic(float) boxes a Swift Float");
    }

    public void TestRepeatedCallsStayIndependent()
    {
        using var sink = new LedgerAmountSink();
        for (var i = 0; i < 50; i++)
        {
            sink.RecordScaled(i, i + 1);
            AssertEqual($"Int32:{i} x{i + 1}", sink.LastRecorded.ToString(), $"RecordScaled(int, int) call {i}");
        }
    }
}
