// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Foundation;
using RuntimeTestsApp.Infrastructure;
using Swift.Runtime;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Protocols;

/// <summary>
/// Pairs with <c>BindingTests/Sources/SwiftBindingsTestLib/Protocols/AnyObjectExistential.swift</c>.
/// A Swift <c>AnyObject</c> is typed <c>object</c> in C#, and crosses the @_cdecl wrapper through a
/// pointer to a one-word cell holding the reference, in every position: argument, result, stored
/// property, initializer argument, subscript, static property, throwing and async results, and
/// reverse dispatch. Swift checks each value with <c>===</c>, and the live count of
/// <see cref="AnyObjectLaneToken"/> shows a leaked or over-released reference.
/// </summary>
public class AnyObjectExistentialTests : TestBase
{
    public AnyObjectExistentialTests(TestResults results) : base(results) { }

    private static void Release(object? value) => (value as IDisposable)?.Dispose();

    public void TestASwiftObjectArgumentArrivesAsItself()
    {
        int baseline = AnyObjectLaneToken.LiveCount;
        using (var token = new AnyObjectLaneToken(11))
        using (var other = new AnyObjectLaneToken(12))
        {
            AssertEqual(11, TestLibFunctions.AnyObjectLaneIdentifier(token), "Swift reads the token C# passed");
            AssertEqual("AnyObjectLaneToken", TestLibFunctions.AnyObjectLaneTypeName(token), "the argument has the token's dynamic type");
            AssertTrue(TestLibFunctions.AnyObjectLaneSame(token, token), "the same object passed twice is identical in Swift");
            AssertFalse(TestLibFunctions.AnyObjectLaneSame(token, other), "two objects stay distinct");
            for (int i = 0; i < 32; i++)
                AssertEqual(12, TestLibFunctions.AnyObjectLaneIdentifier(other), $"iter {i}: repeated calls read the same object");
            AssertEqual(baseline + 2, AnyObjectLaneToken.LiveCount, "an argument is borrowed: no call keeps a reference");
        }
        AssertEqual(baseline, AnyObjectLaneToken.LiveCount, "the tokens are freed once their wrappers are disposed");
    }

    public void TestAReturnedObjectIsTheObjectSwiftMade()
    {
        int baseline = AnyObjectLaneToken.LiveCount;
        for (int i = 0; i < 16; i++)
        {
            object made = TestLibFunctions.AnyObjectLaneMake(20 + i);
            AssertEqual(baseline + 1, AnyObjectLaneToken.LiveCount, $"iter {i}: the result owns the new token");
            AssertEqual(20 + i, TestLibFunctions.AnyObjectLaneIdentifier(made), $"iter {i}: the result passes back as the same token");
            AssertEqual("AnyObjectLaneToken", TestLibFunctions.AnyObjectLaneTypeName(made), $"iter {i}: the result keeps its dynamic type");
            Release(made);
            AssertEqual(baseline, AnyObjectLaneToken.LiveCount, $"iter {i}: disposing the result frees the token");
        }
    }

    public void TestAnEchoedObjectKeepsItsIdentity()
    {
        int baseline = AnyObjectLaneToken.LiveCount;
        using (var token = new AnyObjectLaneToken(30))
        {
            object echoed = TestLibFunctions.AnyObjectLaneEcho(token);
            AssertTrue(TestLibFunctions.AnyObjectLaneSame(echoed, token), "the echo returns the very object C# passed");
            object again = TestLibFunctions.AnyObjectLaneEcho(echoed);
            AssertTrue(TestLibFunctions.AnyObjectLaneSame(again, token), "a returned object passes back in as itself");
            Release(again);
            Release(echoed);
            AssertEqual(baseline + 1, AnyObjectLaneToken.LiveCount, "releasing the echoes leaves only the C# wrapper's reference");
        }
        AssertEqual(baseline, AnyObjectLaneToken.LiveCount, "the token is freed once its wrapper is disposed");
    }

    public void TestAMarkerCompositionCrossesLikeAnyObject()
    {
        // `any AnyObject & Sendable` adds no witness table, so it crosses in the same one-word cell.
        int baseline = AnyObjectLaneToken.LiveCount;
        using (var token = new AnyObjectLaneToken(35))
        {
            AssertEqual(35, TestLibFunctions.AnyObjectLaneSendableIdentifier(token), "Swift reads the token through the composition");
            object echoed = TestLibFunctions.AnyObjectLaneSendableEcho(token);
            AssertTrue(TestLibFunctions.AnyObjectLaneSame(echoed, token), "the composition result is the very object C# passed");
            AssertEqual(35, TestLibFunctions.AnyObjectLaneSendableIdentifier(echoed), "the result passes back in as itself");
            Release(echoed);
            AssertEqual(baseline + 1, AnyObjectLaneToken.LiveCount, "releasing the result leaves only the C# wrapper's reference");
        }
        AssertEqual(baseline, AnyObjectLaneToken.LiveCount, "the token is freed once its wrapper is disposed");
    }

    public void TestStoredPropertyInitializerAndExchange()
    {
        int baseline = AnyObjectLaneToken.LiveCount;
        using (var first = new AnyObjectLaneToken(40))
        using (var second = new AnyObjectLaneToken(41))
        {
            using (var sink = new AnyObjectLaneSink(first))
            {
                AssertTrue(sink.Holds(first), "the initializer stores the object C# passed");

                object stored = sink.Stored;
                AssertTrue(TestLibFunctions.AnyObjectLaneSame(stored, first), "the getter returns the stored object");
                Release(stored);

                sink.Stored = second;
                AssertTrue(sink.Holds(second), "the setter stores the object C# passed");

                object previous = sink.Exchange(first);
                AssertTrue(TestLibFunctions.AnyObjectLaneSame(previous, second), "exchange returns the object it replaced");
                AssertTrue(sink.Holds(first), "exchange stores its argument");
                Release(previous);

                object viaSubscript = sink[0];
                AssertTrue(TestLibFunctions.AnyObjectLaneSame(viaSubscript, first), "the subscript getter returns the stored object");
                Release(viaSubscript);
                sink[0] = second;
                AssertTrue(sink.Holds(second), "the subscript setter stores the object C# passed");

                AssertEqual(baseline + 2, AnyObjectLaneToken.LiveCount, "no member leaked a reference");
            }
            AssertEqual(baseline + 2, AnyObjectLaneToken.LiveCount, "freeing the sink released only its own reference");
        }
        AssertEqual(baseline, AnyObjectLaneToken.LiveCount, "both tokens are freed");
    }

    public void TestStaticStoredObject()
    {
        object original = AnyObjectLaneSink.Shared;
        AssertEqual(900, TestLibFunctions.AnyObjectLaneIdentifier(original), "the static getter returns Swift's initial object");
        using (var token = new AnyObjectLaneToken(42))
        {
            AnyObjectLaneSink.Shared = token;
            object read = AnyObjectLaneSink.Shared;
            AssertTrue(TestLibFunctions.AnyObjectLaneSame(read, token), "the static setter stores the object C# passed");
            Release(read);
            AnyObjectLaneSink.Shared = original;
        }
        object restored = AnyObjectLaneSink.Shared;
        AssertTrue(TestLibFunctions.AnyObjectLaneSame(restored, original), "a returned object stores back as itself");
        Release(restored);
        Release(original);
    }

    public void TestThrowingResult()
    {
        int baseline = AnyObjectLaneToken.LiveCount;
        using (var token = new AnyObjectLaneToken(50))
        using (var sink = new AnyObjectLaneSink(token))
        {
            object result = sink.StoredOrThrow(false);
            AssertTrue(TestLibFunctions.AnyObjectLaneSame(result, token), "a throwing method returns the stored object");
            Release(result);
            AssertThrows<SwiftException>(() => sink.StoredOrThrow(true), "the thrown error surfaces instead of a result");
            AssertTrue(sink.Holds(token), "the failed call left the stored object alone");
        }
        AssertEqual(baseline, AnyObjectLaneToken.LiveCount, "neither path leaked a reference");
    }

    public async Task TestAsyncResultAndArgument()
    {
        int baseline = AnyObjectLaneToken.LiveCount;
        using (var token = new AnyObjectLaneToken(60))
        using (var sink = new AnyObjectLaneSink(token))
        {
            object result = await sink.GetStoredAsync();
            AssertTrue(TestLibFunctions.AnyObjectLaneSame(result, token), "an async method returns the stored object");
            Release(result);
            AssertTrue(await sink.HoldsAsync(token), "an async method reads its argument as the object C# passed");
        }
        AssertEqual(baseline, AnyObjectLaneToken.LiveCount, "the async calls leaked no reference");
    }

    public void TestValueTypeStoredObject()
    {
        int baseline = AnyObjectLaneToken.LiveCount;
        using (var first = new AnyObjectLaneToken(70))
        using (var second = new AnyObjectLaneToken(71))
        {
            using (var box = new AnyObjectLaneBox(first))
            {
                AssertTrue(box.Holds(first), "the struct initializer stores the object C# passed");
                object item = box.Item;
                AssertTrue(TestLibFunctions.AnyObjectLaneSame(item, first), "the struct getter returns the stored object");
                Release(item);
                box.Item = second;
                AssertTrue(box.Holds(second), "the struct setter stores the object C# passed");
            }
            AssertEqual(baseline + 2, AnyObjectLaneToken.LiveCount, "freeing the struct released only its own reference");
        }
        AssertEqual(baseline, AnyObjectLaneToken.LiveCount, "both tokens are freed");
    }

    public void TestAnObjectiveCObjectCrossesAsItself()
    {
        // An NSObject is an object Swift can hold, so it crosses without a Swift wrapper.
        using var native = new NSObject();
        AssertEqual("NSObject", TestLibFunctions.AnyObjectLaneTypeName(native), "Swift sees the Objective-C object");
        AssertTrue(TestLibFunctions.AnyObjectLaneSame(native, native), "the object keeps its identity");
        AssertEqual(-1, TestLibFunctions.AnyObjectLaneIdentifier(native), "Swift does not mistake it for a token");

        using var sink = new AnyObjectLaneSink(native);
        AssertTrue(sink.Holds(native), "Swift stores the Objective-C object");
        object stored = sink.Stored;
        AssertTrue(TestLibFunctions.AnyObjectLaneSame(stored, native), "the stored Objective-C object comes back as itself");
        Release(stored);
    }

    public void TestAPlainManagedObjectIsRejected()
    {
        // A managed object that is not a Swift or Objective-C object has no reference Swift can hold.
        AssertThrows<ArgumentException>(() => TestLibFunctions.AnyObjectLaneIdentifier(new object()),
            "a plain C# object is rejected before the call");
        AssertThrows<ArgumentException>(() => TestLibFunctions.AnyObjectLaneIdentifier("text"),
            "a C# string is rejected before the call");
    }

    public void TestANativeHandleThatIsNotAnObjectIsRejected()
    {
        // A selector is a native handle but not an object: Swift would read its first word as an isa.
        ObjCRuntime.INativeObject selector = new ObjCRuntime.Selector("description");
        AssertThrows<ArgumentException>(() => TestLibFunctions.AnyObjectLaneTypeName(selector),
            "a native handle that is not an Objective-C object is rejected before the call");
    }

    public void TestASubclassOfAGeneratedClassCrossesAsItsObject()
    {
        // A C# subclass of a generated class wraps the same Swift object as its base.
        int baseline = AnyObjectLaneToken.LiveCount;
        using (var token = new ManagedLaneToken(41))
        {
            AssertEqual(41, TestLibFunctions.AnyObjectLaneIdentifier(token), "Swift reads the object the subclass wraps");
            AssertTrue(TestLibFunctions.AnyObjectLaneSame(token, token), "the object keeps its identity");
            using var sink = new AnyObjectLaneSink(token);
            AssertTrue(sink.Holds(token), "Swift stores the object the subclass wraps");
        }
        AssertEqual(baseline, AnyObjectLaneToken.LiveCount, "the object is freed once its wrappers are disposed");
    }

    private sealed class ManagedLaneToken : AnyObjectLaneToken
    {
        public ManagedLaneToken(int identifier) : base(identifier) { }
    }

    // Records every object Swift hands it, so the test can release them once Swift is done.
    private sealed class Exchanger : IAnyObjectLaneExchanger
    {
        public readonly List<object> Received = new();
        private object _current;

        public Exchanger(object current) { _current = current; }

        public object LaneCurrent
        {
            get => _current;
            set { Received.Add(value); _current = value; }
        }

        public object LaneSwap(object incoming)
        {
            Received.Add(incoming);
            var previous = _current;
            _current = incoming;
            return previous;
        }
    }

    public void TestReverseDispatchHandsObjectsBothWays()
    {
        int baseline = AnyObjectLaneToken.LiveCount;
        using (var held = new AnyObjectLaneToken(80))
        using (var incoming = new AnyObjectLaneToken(81))
        {
            var exchanger = new Exchanger(held);
            AssertEqual("ok", TestLibFunctions.AnyObjectLaneDrive(exchanger, incoming),
                "Swift reads, swaps and writes the conformer's object and sees the same objects back");
            AssertEqual(2, exchanger.Received.Count, "the swap and the write each handed C# an object");
            AssertTrue(TestLibFunctions.AnyObjectLaneSame(exchanger.Received[0], incoming), "the swap received the object Swift passed");
            AssertTrue(TestLibFunctions.AnyObjectLaneSame(exchanger.Received[1], held), "the write received the object Swift read first");
            foreach (var value in exchanger.Received)
                Release(value);
        }
        AssertEqual(baseline, AnyObjectLaneToken.LiveCount, "every reference crossing the protocol was balanced");
    }
}
