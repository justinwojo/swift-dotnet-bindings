// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Parameters;

/// <summary>
/// Pairs with <c>BindingTests/Sources/SwiftBindingsTestLib/Parameters/ZeroWitnessExistentialParameters.swift</c>.
/// A bare <c>Any</c> or <c>any Sendable</c> parameter of an @_cdecl-wrapped method takes a plain C#
/// value typed <c>object</c>. Swift describes what it received as <c>&lt;type&gt;:&lt;value&gt;</c>, so
/// each assertion checks both that the call reached Swift and that the value arrived with the Swift
/// type it boxes to.
/// </summary>
public class ZeroWitnessExistentialParameterTests : TestBase
{
    public ZeroWitnessExistentialParameterTests(TestResults results) : base(results) { }

    public void TestAnyParameterTakesPlainString()
    {
        using var sink = new ZeroWitnessParameterSink();
        AssertEqual("String:hello", sink.AcceptAny("hello"), "string arrives as Swift String");
        AssertEqual("String:hello", sink.LastDescription, "Swift recorded the same value");
    }

    public void TestAnyParameterTakesHeapString()
    {
        // Longer than the 15-byte small-string form, so the boxed container holds a retained buffer.
        var text = new string('z', 80);
        using var sink = new ZeroWitnessParameterSink();
        for (int i = 0; i < 32; i++)
            AssertEqual($"String:{text}", sink.AcceptAny(text), $"iter {i}: heap string round-trips");
    }

    public void TestAnyParameterTakesPrimitives()
    {
        using var sink = new ZeroWitnessParameterSink();
        AssertEqual("Int:42", sink.AcceptAny(42L), "long arrives as Swift Int");
        AssertEqual("Int32:7", sink.AcceptAny(7), "int arrives as Swift Int32");
        AssertEqual("Double:2.5", sink.AcceptAny(2.5), "double arrives as Swift Double");
        AssertEqual("Bool:true", sink.AcceptAny(true), "bool arrives as Swift Bool");
    }

    public void TestAnyParameterTakesSwiftStruct()
    {
        using var sink = new ZeroWitnessParameterSink();
        using var point = new ZeroWitnessParamPoint(3, 4);
        AssertEqual("ZeroWitnessParamPoint:ZeroWitnessParamPoint(x: 3, y: 4)", sink.AcceptAny(point),
            "Swift struct arrives with its own type");
    }

    public void TestAnyParameterReleasesBoxedClassReference()
    {
        // Boxing a class instance retains it for the call. The caller owns that retain and must drop
        // it afterwards: a leak keeps the token alive after the C# wrapper is disposed, and a double
        // release crashes.
        int baseline = ZeroWitnessParamToken.LiveCount;
        using (var sink = new ZeroWitnessParameterSink())
        using (var token = new ZeroWitnessParamToken(5))
        {
            for (int i = 0; i < 16; i++)
                AssertEqual("ZeroWitnessParamToken:token#5", sink.AcceptAny(token), $"iter {i}: class instance round-trips");
            AssertEqual(baseline + 1, ZeroWitnessParamToken.LiveCount, "only the C# wrapper keeps the token alive");
        }
        ForceGC();
        AssertEqual(baseline, ZeroWitnessParamToken.LiveCount, "token is freed once the wrapper is disposed");
    }

    public void TestSendableParameterTakesPlainValues()
    {
        using var sink = new ZeroWitnessParameterSink();
        AssertEqual("String:sent", sink.AcceptSendable("sent"), "string arrives through any Sendable");
        AssertEqual("Int:9", sink.AcceptSendable(9L), "long arrives through any Sendable");
        AssertEqual("Double:0.25", sink.AcceptSendable(0.25), "double arrives through any Sendable");
    }

    public void TestZeroWitnessParameterBetweenOrdinaryParameters()
    {
        using var sink = new ZeroWitnessParameterSink();
        AssertEqual("pre|String:mid|3", sink.AcceptLabeled("pre", "mid", 3),
            "neighbouring parameters keep their positions");
    }

    public void TestVoidMethodStoresValue()
    {
        using var sink = new ZeroWitnessParameterSink();
        sink.Store(11L);
        AssertEqual("Int:11", sink.LastDescription, "void method delivered the value");
    }

    public async Task TestAsyncAnyParameterTakesPlainString()
    {
        using var sink = new ZeroWitnessParameterSink();
        var text = new string('a', 40);
        AssertEqual($"String:{text}", await sink.AcceptAnyAsync(text), "async Any parameter round-trips");
    }

    public async Task TestAsyncSendableParameterTakesPlainValue()
    {
        using var sink = new ZeroWitnessParameterSink();
        AssertEqual("Int:21", await sink.AcceptSendableAsync(21L), "async any Sendable parameter round-trips");
    }

    public async Task TestAsyncAnyParameterReleasesBoxedClassReference()
    {
        int baseline = ZeroWitnessParamToken.LiveCount;
        using (var sink = new ZeroWitnessParameterSink())
        using (var token = new ZeroWitnessParamToken(8))
        {
            for (int i = 0; i < 8; i++)
                AssertEqual("ZeroWitnessParamToken:token#8", await sink.AcceptAnyAsync(token), $"iter {i}: async class instance round-trips");
        }
        ForceGC();
        AssertEqual(baseline, ZeroWitnessParamToken.LiveCount, "async call releases its boxed retain");
    }
}
