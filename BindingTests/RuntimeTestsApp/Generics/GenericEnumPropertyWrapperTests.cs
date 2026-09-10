// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// Round-trip coverage for a concrete-typed property read off a generic <b>enum</b> parent.
///
/// The generic static-dispatch property wrapper reconstructs the parent's bound metatype at
/// runtime and calls the accessor through a private protocol requirement. Nothing in that
/// mechanism is class- or struct-specific, so an enum parent is admitted too — but an enum
/// is the one parent kind whose `self` is a tag-plus-payload value rather than a pointer to
/// a fixed layout, and the wrapper reads it as <c>Self</c> through the reconstructed
/// metatype. Reading the wrong tag is silent: <c>isSuccess</c> would just answer the
/// opposite question, which no compile gate can see. Both cases are asserted so a
/// tag-misread cannot pass by agreeing with one of them.
/// </summary>
public class GenericEnumPropertyWrapperTests : TestBase
{
    public GenericEnumPropertyWrapperTests(TestResults results) : base(results) { }

    public void TestSuccessCaseReadsThroughGenericEnumWrapper()
    {
        using var result = TestLibFunctions.MakeGenericResultSuccess(42);
        AssertTrue(result.IsSuccess, "GenericResult<Int32>.success reports IsSuccess == true");
    }

    public void TestFailureCaseReadsThroughGenericEnumWrapper()
    {
        using var result = TestLibFunctions.MakeGenericResultFailure("boom");
        AssertFalse(result.IsSuccess, "GenericResult<Int32>.failure reports IsSuccess == false");
    }

    public void TestRepeatedReadsAreStable()
    {
        // The wrapper dlsym's the parent's metadata accessor on every call. A cached-or-not
        // difference between the first and later calls would show up here as a flipped answer.
        using var success = TestLibFunctions.MakeGenericResultSuccess(1);
        using var failure = TestLibFunctions.MakeGenericResultFailure("nope");

        for (int i = 0; i < 8; i++)
        {
            AssertTrue(success.IsSuccess, $"success stays true on read {i}");
            AssertFalse(failure.IsSuccess, $"failure stays false on read {i}");
        }
    }
}
