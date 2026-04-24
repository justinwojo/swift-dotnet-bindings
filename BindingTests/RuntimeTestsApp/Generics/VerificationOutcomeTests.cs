// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift;
using Swift.Runtime;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// Regression coverage for the nested-type-on-generic-outer emission bug that
/// caused <c>VerificationOutcome&lt;SignedType&gt;.Failure</c> — and StoreKit2's
/// <c>VerificationResult&lt;T&gt;.Unverified</c>/<c>TryGetUnverified</c> — to fail
/// with CS0693/CS0305. The generator now subtracts ancestor-inherited generic
/// parameters from nested types so the nested <c>Failure</c> declares no own
/// params, and the outer's <c>.unverified</c> case (tuple payload referencing
/// the nested type) compiles and round-trips values correctly.
///
/// Must pass on Mono JIT (sim) AND NativeAOT (device): tuple-payload marshalling
/// and generic enum metadata differ enough between runtimes that single-runtime
/// coverage is insufficient.
/// </summary>
public class VerificationOutcomeTests : TestBase
{
    public VerificationOutcomeTests(TestResults results) : base(results) { }

    public void TestUnverifiedFactoryPreservesStringAndFailureTag()
    {
        using var outcome = TestLibFunctions.MakeUnverifiedOutcomeString(
            "payload",
            VerificationOutcome<SwiftString>.Failure.Expired);

        AssertEqual(VerificationOutcome<SwiftString>.CaseTag.Unverified, outcome.Tag, "Tag == Unverified");

        AssertTrue(outcome.TryGetUnverified(out var payload, out var failure), "TryGetUnverified returns true");
        using (payload)
        using (failure)
        {
            AssertEqual("payload", payload!.ToString(), "String payload round-trips");
            AssertEqual(VerificationOutcome<SwiftString>.Failure.CaseTag.Expired, failure!.Tag, "Failure tag == Expired");
        }
    }

    public void TestTryGetUnverifiedDestructuresTupleCorrectly()
    {
        using var outcome = TestLibFunctions.MakeUnverifiedOutcomeString(
            "payload",
            VerificationOutcome<SwiftString>.Failure.Expired);

        AssertTrue(outcome.TryGetUnverified(out var value, out var reason), "TryGetUnverified returns true");
        using (value)
        using (reason)
        {
            AssertEqual("payload", value!.ToString(), "Tuple element 0 is the string payload");
            AssertEqual(VerificationOutcome<SwiftString>.Failure.CaseTag.Expired, reason!.Tag, "Tuple element 1 is the Failure.Expired case");
        }
    }

    public void TestUnverifiedRoundTripsAllFailureCases()
    {
        var cases = new[]
        {
            (VerificationOutcome<SwiftString>.Failure.Expired, VerificationOutcome<SwiftString>.Failure.CaseTag.Expired, "expired"),
            (VerificationOutcome<SwiftString>.Failure.Malformed, VerificationOutcome<SwiftString>.Failure.CaseTag.Malformed, "malformed"),
            (VerificationOutcome<SwiftString>.Failure.NotAuthorized, VerificationOutcome<SwiftString>.Failure.CaseTag.NotAuthorized, "notAuthorized"),
        };

        foreach (var (failure, expectedTag, label) in cases)
        {
            using var outcome = TestLibFunctions.MakeUnverifiedOutcomeString(label, failure);
            AssertEqual(VerificationOutcome<SwiftString>.CaseTag.Unverified, outcome.Tag, $"Tag == Unverified for {label}");

            AssertTrue(outcome.TryGetUnverified(out var value, out var reason), $"TryGetUnverified true for {label}");
            using (value)
            using (reason)
            {
                AssertEqual(label, value!.ToString(), $"String payload round-trips for {label}");
                AssertEqual(expectedTag, reason!.Tag, $"Failure tag == {expectedTag}");
            }
        }
    }

    public void TestUnverifiedHandlesLargeStringPayload()
    {
        var large = new string('x', 64 * 1024);
        using var outcome = TestLibFunctions.MakeUnverifiedOutcomeString(
            large,
            VerificationOutcome<SwiftString>.Failure.Malformed);

        AssertEqual(VerificationOutcome<SwiftString>.CaseTag.Unverified, outcome.Tag, "Tag == Unverified");

        AssertTrue(outcome.TryGetUnverified(out var value, out var reason), "TryGetUnverified returns true");
        using (value)
        using (reason)
        {
            AssertEqual(large.Length, value!.ToString().Length, "Large string length round-trips");
            AssertEqual(large, value.ToString(), "Large string contents round-trip");
            AssertEqual(VerificationOutcome<SwiftString>.Failure.CaseTag.Malformed, reason!.Tag, "Failure tag == Malformed");
        }
    }

    public void TestVerifiedStillWorksAfterNestedTypeFix()
    {
        using var outcome = TestLibFunctions.MakeVerifiedOutcomeString("verified-payload");
        AssertEqual(VerificationOutcome<SwiftString>.CaseTag.Verified, outcome.Tag, "Tag == Verified");

        AssertTrue(outcome.TryGetVerified(out var value), "TryGetVerified returns true");
        using (value)
        {
            AssertEqual("verified-payload", value!.ToString(), "Verified payload round-trips");
        }

        AssertFalse(outcome.TryGetUnverified(out var leftover, out var failure), "TryGetUnverified returns false on Verified");
        leftover?.Dispose();
        failure?.Dispose();
    }
}
