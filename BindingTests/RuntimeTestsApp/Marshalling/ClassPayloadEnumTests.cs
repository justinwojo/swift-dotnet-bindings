// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Marshalling;

/// <summary>
/// Regression coverage for the class-payload deref path in EnumHandler.Marshalling.
/// A non-generic enum with a concrete Swift class associated value must dereference
/// the class pointer out of the enum's payload bytes and Arc.UnknownObjectRetain for +1
/// C# ownership (isa-dispatch — swift_retain for pure-Swift, objc_retain for @objc:NSObject).
/// Wrapping the buffer address directly in SwiftClassHandle&lt;T&gt; would ARC-release a
/// bogus pointer on dispose. Exercised on Mono JIT (sim) and NativeAOT (device) because
/// the class-pointer path is a distinct branch from the value-buffer heap-alloc path.
/// </summary>
public class ClassPayloadEnumTests : TestBase
{
    public ClassPayloadEnumTests(TestResults results) : base(results) { }

    /// <summary>
    /// Drain for <c>@objc:NSObject</c> peers whose native <c>dealloc</c> is deferred to the
    /// main-thread finalization queue (Microsoft.iOS) — a plain GC drain runs the C# finalizer
    /// but the native dealloc (and <c>recordTrackedDeallocation</c>) only fires on a runloop
    /// iteration. Mirrors <c>ClassParamCallbackTests.DrainObjCFinalizers</c>.
    /// </summary>
    private static void DrainObjCFinalizers()
    {
        for (int i = 0; i < 6; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            Foundation.NSRunLoop.Current.RunUntil((Foundation.NSDate)Foundation.NSDate.Now.AddSeconds(0.05));
        }
    }

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

    /// Regression test for the enum-case payload-extractor-missing bug.
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

    // ---- @objc:NSObject enum payloads (issue #40 — enum direction) ----
    //
    // The pure-Swift tests above route the same extraction sites, but for them swift_retain and
    // swift_unknownObjectRetain are indistinguishable. These variants carry an @objc:NSObject
    // payload, where a native-only swift_retain touches the wrong refcount word: the C# wrapper
    // then objc_releases on dispose, underflowing the object's true ARC count. The extraction
    // MUST use the isa-dispatching Arc.UnknownObjectRetain. ObjCClassParamPayload feeds the shared
    // LifetimeTracker counters, so the no-leak tests assert ARC *balance*, not just crash-absence.

    /// <summary>E2 site: concrete @objc:NSObject enum payload via <c>EmitPayloadMarshal</c>.</summary>
    public void TestObjCClassOutcome_Delivered_ExtractsObjCPayload()
    {
        using var outcome = TestLibFunctions.MakeObjCDeliveredOutcome(7, "objc");
        AssertEqual(ObjCClassOutcome.CaseTag.Delivered, outcome.Tag, "Tag == Delivered");

        AssertTrue(outcome.TryGetDelivered(out var payload), "TryGetDelivered returns true");
        using (payload)
        {
            AssertEqual(7, payload!.Code, "Extracted @objc payload .Code round-trips");
            AssertEqual("objc", payload!.Label.ToString(), "Extracted @objc payload .Label round-trips");
        }
    }

    /// <summary>E1 site: @objc:NSObject element in a tuple payload via <c>EmitPayloadMarshalWithOffset</c>.</summary>
    public void TestObjCTaggedDelivery_Shipped_ExtractsTupleWithObjCElement()
    {
        using var delivery = TestLibFunctions.MakeObjCShippedDelivery(3, 42, "objc");
        AssertEqual(ObjCTaggedDelivery.CaseTag.Shipped, delivery.Tag, "Tag == Shipped");

        AssertTrue(delivery.TryGetShipped(out var tag, out var payload), "TryGetShipped returns true");
        using (payload)
        {
            AssertEqual(3, tag, "Tuple element 0 (Int32 tag) round-trips");
            AssertEqual(42, payload!.Code, "Tuple element 1 (@objc payload) .Code round-trips");
        }
    }

    /// <summary>E3 site: bare-generic-parameter @objc:NSObject payload via
    /// <c>EmitGenericTypeParameterPayloadExtraction</c> (<c>Holder&lt;ObjCClassParamPayload&gt;</c>).</summary>
    public void TestGenericHolder_ObjCPayload_ExtractsWrapped()
    {
        using var holder = TestLibFunctions.MakeWrappedObjCPayload(55, "objc");

        AssertTrue(holder.TryGetWrapped(out var payload), "TryGetWrapped returns true");
        using (payload)
        {
            AssertEqual(55, payload!.Code, "Extracted generic @objc payload .Code round-trips");
        }
    }

    /// <summary>
    /// ARC balance for all three @objc enum-payload extraction sites (E1/E2/E3). Each iteration
    /// allocates one tracked @objc payload Swift-side, extracts an independent +1 copy, then
    /// disposes both the extracted copy and the enum carrier. With the UnknownObjectRetain fix
    /// the retains/releases balance to zero; native swift_retain on an NSObject subclass would
    /// fail to register the copy's +1, so the carrier dispose over-releases and skews the count.
    /// </summary>
    public void TestObjCEnumPayloadExtraction_NoLeak()
    {
        DrainObjCFinalizers();
        LifetimeTracker.Reset();

        ExtractObjCEnumPayloads(150);
        DrainObjCFinalizers();

        LifetimeTracker.AssertNoLeaks("@objc enum-payload extraction (E1/E2/E3) must balance ARC (UnknownObjectRetain)");
        TestLogger.Info("@objc enum-payload extraction: 150 payloads copied out and released across E1/E2/E3");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ExtractObjCEnumPayloads(int n)
    {
        for (int i = 0; i < n; i++)
        {
            using (var outcome = TestLibFunctions.MakeObjCDeliveredOutcome(i, "x"))
            {
                if (outcome.TryGetDelivered(out var payload))
                    payload!.Dispose();
            }
            using (var delivery = TestLibFunctions.MakeObjCShippedDelivery(i, i, "x"))
            {
                if (delivery.TryGetShipped(out _, out var payload))
                    payload!.Dispose();
            }
            using (var holder = TestLibFunctions.MakeWrappedObjCPayload(i, "x"))
            {
                if (holder.TryGetWrapped(out var payload))
                    payload!.Dispose();
            }
        }
    }
}
