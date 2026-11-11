// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Properties;

/// <summary>
/// A subscript spelled <c>mutating get</c> takes <c>self</c> inout, like a <c>mutating func</c>.
/// The indexer has to hand the getter the receiver's own storage: a borrowed copy would lose the
/// getter's write, and for a frozen struct, whose ordinary getter takes <c>self</c> by value in
/// registers, it would be the wrong calling convention. Each test reads the indexer more than
/// once and then reads the receiver through a separate accessor, so a write that never reached
/// the receiver shows up as a stale count. The <c>Tick</c> property is the <c>mutating get</c>
/// computed-property counterpart on the same type.
/// </summary>
public class MutatingSubscriptGetterTests : TestBase
{
    public MutatingSubscriptGetterTests(TestResults results) : base(results) { }

    public void TestResilient_MutatingGetSubscript_WritesBackToReceiver()
    {
        using var counter = new MutatingSubscriptCounter(0);

        AssertEqual(2, counter[2], "First read returns the advanced total");
        AssertEqual(5, counter[3], "Second read advances the SAME receiver, not a fresh copy");
        AssertEqual(5, counter.Reads, "The receiver observes both getter writes");
    }

    public void TestResilient_MutatingGetSubscriptWithSetter_RoundTrips()
    {
        using var counter = new MutatingSubscriptCounter(0);

        counter[slot: 7] = 40;
        AssertEqual(40, counter.Reads, "The setter replaces the counter");
        AssertEqual((nint)4107, counter[slot: 7], "The getter counts its read on top of the set value");
        AssertEqual(41, counter.Reads, "The getter's write reached the receiver");
    }

    public void TestResilient_MutatingGetProperty_WritesBackToReceiver()
    {
        using var counter = new MutatingSubscriptCounter(10);

        AssertEqual(11, counter.Tick, "First tick");
        AssertEqual(12, counter.Tick, "Second tick advances the same receiver");
        AssertEqual(12, counter.Reads, "The receiver observes both ticks");
    }

    public void TestFrozen_MutatingGetSubscript_WritesBackToReceiver()
    {
        var counter = new FrozenMutatingSubscriptCounter(0, 9);

        AssertEqual(4, counter[4], "First read returns the advanced total");
        AssertEqual(8, counter[4], "Second read advances the same receiver");
        AssertEqual(9, counter[4], "Third read is capped by the limit field, read through the same storage");
        AssertEqual(9, counter.Reads, "The C# struct observes every getter write");
        AssertEqual(9, counter.Limit, "The neighbouring field is untouched");
    }

    public void TestFrozen_MutatingGetProperty_WritesBackToReceiver()
    {
        var counter = new FrozenMutatingSubscriptCounter(3, 100);

        AssertEqual(4, counter.Tick, "First tick");
        AssertEqual(5, counter.Tick, "Second tick advances the same receiver");
        AssertEqual(5, counter.Reads, "The C# struct observes both ticks");
    }

    public void TestFrozenWithReference_MutatingGetSubscripts_WriteBackToReceiver()
    {
        using var counter = new FrozenRefMutatingSubscriptCounter("start", 0);

        AssertEqual(3, counter[3], "Int subscript advances the counter");
        AssertEqual("next#4", counter["next"], "String subscript stores the label and counts the read");
        AssertEqual(4, counter.Reads, "The receiver observes both getter writes");
        AssertEqual("next", counter.Name, "The String subscript's write to the reference field reached the receiver");
    }
}
