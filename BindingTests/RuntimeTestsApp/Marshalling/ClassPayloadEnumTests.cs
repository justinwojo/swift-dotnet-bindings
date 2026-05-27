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

    /// Regression test for bug-0.10.0-enum-case-payload-extractor-missing.md.
    /// Locks in the StripeFinancialConnections.Result emission shape: a Result-style
    /// enum with a *labeled* class success payload, a no-payload cancel case, and a
    /// labeled `any Swift.Error` failure case. Pre-fix only the AnyError-payload case
    /// in the same enum got factory + TryGet; the labeled-class-payload `completed`
    /// case got just the CaseTag. The compile-time fact that
    /// `LabeledClassResult.Completed(...)` and `TryGetCompleted` resolve below is
    /// itself the structural assertion — pre-fix the fixture would not compile.
    public void TestLabeledClassResult_Completed_ExtractsLabeledSession()
    {
        using var result = TestLibFunctions.MakeLabeledCompletedResult("session-42");
        AssertEqual(LabeledClassResult.CaseTag.Completed, result.Tag, "Tag == Completed");

        AssertTrue(result.TryGetCompleted(out var session), "TryGetCompleted returns true");
        using (session)
        {
            AssertEqual("session-42", session!.Id, "Labeled class payload (FCSession.id) round-trips");
        }
    }

    public void TestLabeledClassResult_Failed_ExtractsAnyError()
    {
        using var result = TestLibFunctions.MakeLabeledFailedResult("denied");
        AssertEqual(LabeledClassResult.CaseTag.Failed, result.Tag, "Tag == Failed");

        AssertTrue(result.TryGetFailed(out var error), "TryGetFailed returns true on Failed");
        // AnyError is a reference type that owns the extracted box's +1, so dispose it
        // once the structural extraction assertion (above) has fired.
        error?.Dispose();
        // C#-side authored Completed factory must round-trip too — exercises the path
        // that was missing pre-fix.
        AssertFalse(result.TryGetCompleted(out var bogusSession),
            "TryGetCompleted returns false on Failed");
        bogusSession?.Dispose();
    }

    public void TestLabeledClassResult_Canceled_NoPayloadExtraction()
    {
        using var result = TestLibFunctions.MakeLabeledCanceledResult();
        AssertEqual(LabeledClassResult.CaseTag.Canceled, result.Tag, "Tag == Canceled");

        AssertFalse(result.TryGetCompleted(out var bogusSession),
            "TryGetCompleted returns false on Canceled");
        bogusSession?.Dispose();
        AssertFalse(result.TryGetFailed(out var bogusError),
            "TryGetFailed returns false on Canceled");
    }

    /// Locks in the C#-side factory path for the labeled-class case. Pre-fix the
    /// `Completed` factory wasn't emitted at all, so consumers couldn't construct
    /// a `Result.completed(session: ...)` from C#. Round-trips the C#-built instance
    /// through Tag + TryGet to confirm the factory's PInvoke shape is correct.
    public void TestLabeledClassResult_Completed_FactoryRoundTrip()
    {
        using var session = new LabeledFCSession("c#-built");
        using var result = LabeledClassResult.Completed(session);
        AssertEqual(LabeledClassResult.CaseTag.Completed, result.Tag,
            "C#-built Completed has correct tag");

        AssertTrue(result.TryGetCompleted(out var roundTrip),
            "TryGetCompleted returns true on C#-built Completed");
        using (roundTrip)
        {
            AssertEqual("c#-built", roundTrip!.Id,
                "C#-side factory round-trips the labeled class payload");
        }
    }
}
