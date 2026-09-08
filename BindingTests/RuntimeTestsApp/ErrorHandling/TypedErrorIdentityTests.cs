// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift.Runtime;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.ErrorHandling;

public class TypedErrorIdentityTests : TestBase
{
    public TypedErrorIdentityTests(TestResults results) : base(results) { }

    public void TestSyncOverloadIdentityAndHealthyControls()
    {
        using var worker = new TypedErrorIdentityWorker();
        AssertThrows<SwiftException<IdentityFirstError>>(() => worker.Run(-1));
        AssertThrows<SwiftException<IdentitySecondError>>(() => worker.Run(""));
        AssertEqual(11, worker.Run(1));
        AssertEqual(23, worker.Run("abc"));
    }

    public async Task TestAsyncOverloadIdentityAndHealthyControls()
    {
        using var worker = new TypedErrorIdentityWorker();
        await ExpectError(worker.PerformAsync(-1), IdentityFirstError.First);
        await ExpectError(worker.PerformAsync(""), IdentitySecondError.Second);
        AssertEqual(31, await WithTimeout(worker.PerformAsync(1), DefaultAsyncTimeout));
        AssertEqual(43, await WithTimeout(worker.PerformAsync("abc"), DefaultAsyncTimeout));
        AssertEqual(50, await WithTimeout(worker.PerformAsync(true), DefaultAsyncTimeout));
        // The plain-throws registry may refine this independently; it must never be
        // statically miscast to the String sibling's IdentitySecondError.
        await ExpectError(worker.PerformAsync(false), IdentityFirstError.First);
    }

    public async Task TestNestedOwnersKeepDistinctErrors()
    {
        using var first = new SwiftBindingsTestLib.TypedErrorOwnerA.Inner();
        using var second = new SwiftBindingsTestLib.TypedErrorOwnerB.Inner();
        AssertThrows<SwiftException<IdentityFirstError>>(() => first.Check());
        AssertThrows<SwiftException<IdentitySecondError>>(() => second.Check());
        await ExpectError(first.GetWorkAsync(), IdentityFirstError.First);
        await ExpectError(second.GetWorkAsync(), IdentitySecondError.Second);
    }

    private async Task ExpectError<T>(Task<int> operation, T expected)
    {
        try
        {
            await WithTimeout(operation, DefaultAsyncTimeout);
            throw new AssertionException("Expected typed Swift error");
        }
        catch (SwiftException<T> ex)
        {
            AssertEqual(expected, ex.Error, "typed error case must survive marshalling");
        }
    }
}
