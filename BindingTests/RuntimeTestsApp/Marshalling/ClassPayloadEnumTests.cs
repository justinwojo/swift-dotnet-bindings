// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Marshalling;

/// <summary>
/// Regression coverage for the class-payload deref path in EnumHandler.Marshalling.
/// A non-generic enum with a concrete Swift class associated value must dereference
/// the class pointer out of the enum's payload bytes and Arc.Retain for +1 C# ownership.
/// Wrapping the buffer address directly in SwiftClassHandle&lt;T&gt; would ARC-release a
/// bogus pointer on dispose. Exercised on Mono JIT (sim) and NativeAOT (device) because
/// the class-pointer path is a distinct branch from the value-buffer heap-alloc path.
/// </summary>
public class ClassPayloadEnumTests : TestBase
{
    public ClassPayloadEnumTests(TestResults results) : base(results) { }

    public void TestClassOutcome_Delivered_ExtractsBoxedCounter()
    {
        using var outcome = TestLibFunctions.MakeDeliveredOutcome(7);
        AssertEqual(ClassOutcome.CaseTag.Delivered, outcome.Tag, "Tag == Delivered");

        AssertTrue(outcome.TryGetDelivered(out var payload), "TryGetDelivered returns true");
        using (payload)
        {
            AssertEqual(7, payload!.Count, "Extracted BoxedCounter.count round-trips");
        }
    }

    public void TestClassOutcome_Dropped_TryGetFails()
    {
        using var outcome = TestLibFunctions.MakeDroppedOutcome();
        AssertEqual(ClassOutcome.CaseTag.Dropped, outcome.Tag, "Tag == Dropped");

        AssertFalse(outcome.TryGetDelivered(out var payload), "TryGetDelivered returns false on Dropped");
        payload?.Dispose();
    }

    public void TestClassOutcome_RepeatedExtraction_NoDoubleFree()
    {
        using var outcome = TestLibFunctions.MakeDeliveredOutcome(99);

        for (int i = 0; i < 8; i++)
        {
            AssertTrue(outcome.TryGetDelivered(out var payload), $"iter {i}: TryGetDelivered returns true");
            using (payload)
            {
                AssertEqual(99, payload!.Count, $"iter {i}: count still 99");
            }
        }
    }

    public void TestTaggedDelivery_Shipped_ExtractsTupleWithClassElement()
    {
        using var delivery = TestLibFunctions.MakeShippedDelivery(3, 42);
        AssertEqual(TaggedDelivery.CaseTag.Shipped, delivery.Tag, "Tag == Shipped");

        AssertTrue(delivery.TryGetShipped(out var tag, out var counter),
            "TryGetShipped returns true");
        using (counter)
        {
            AssertEqual(3, tag, "Tuple element 0 (Int32 tag) round-trips");
            AssertEqual(42, counter!.Count, "Tuple element 1 (BoxedCounter) round-trips");
        }
    }

    public void TestTaggedDelivery_Pending_TryGetFails()
    {
        using var delivery = TestLibFunctions.MakePendingDelivery();
        AssertEqual(TaggedDelivery.CaseTag.Pending, delivery.Tag, "Tag == Pending");

        AssertFalse(delivery.TryGetShipped(out var tag, out var counter),
            "TryGetShipped returns false on Pending");
        counter?.Dispose();
    }
}
