// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift;
using Swift.Runtime;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.ErrorHandling;

/// <summary>
/// Regression coverage for Issue D.1 (Result&lt;(), E&gt; tombstone).
///
/// Swift.Result&lt;(), E&gt; has an empty-tuple Success. Before the fix,
/// BoundGenericsHandler.HasNonSwiftObjectGenericArg returned true for the
/// empty tuple, and every property / return / parameter typed
/// Result&lt;(), E&gt; was silently tombstoned at emission time.
/// The bypass lets the member through; SwiftResult&lt;TSuccess, TFailure&gt;
/// has no ISwiftObject constraint, so the projection handles marshalling.
///
/// These tests exercise both the struct-property path (a stored Result&lt;(), E&gt;
/// property) and the free-function return path, on both success and
/// failure branches.
/// </summary>
public class ResultOfVoidTests : TestBase
{
    public ResultOfVoidTests(TestResults results) : base(results) { }

    public void TestCacheWriteOutcome_SuccessfulWrite()
    {
        using var outcome = CacheWriteOutcome.GetSuccessfulWrite();
        using var result = outcome.Result;
        AssertTrue(result.IsSuccess, "successfulWrite() should be success case");
        AssertEqual(SwiftResultCase.Success, result.Case, "Case == Success");
    }

    public void TestCacheWriteOutcome_FailedWrite_DiskFull()
    {
        using var err = StoreWriteError.DiskFull;
        using var outcome = CacheWriteOutcome.FailedWrite(err);
        using var result = outcome.Result;
        AssertTrue(result.IsFailure, "failedWrite(.diskFull) should be failure case");
        AssertEqual(SwiftResultCase.Failure, result.Case, "Case == Failure");

        AssertTrue(result.TryGetFailure(out var extracted), "TryGetFailure should return true");
        using (extracted)
        {
            AssertEqual(StoreWriteError.CaseTag.DiskFull, extracted!.Tag,
                $"Extracted failure tag should be DiskFull, got {extracted.Tag}");
        }
    }

    public void TestMakeCacheWriteResult_Success_FreeFunction()
    {
        using var result = TestLibFunctions.MakeCacheWriteResult(true);
        AssertTrue(result.IsSuccess, "makeCacheWriteResult(true) should be success case");
    }

    public void TestMakeCacheWriteResult_Failure_FreeFunction()
    {
        using var result = TestLibFunctions.MakeCacheWriteResult(false);
        AssertTrue(result.IsFailure, "makeCacheWriteResult(false) should be failure case");

        AssertTrue(result.TryGetFailure(out var extracted), "TryGetFailure should return true");
        using (extracted)
        {
            AssertEqual(StoreWriteError.CaseTag.DiskFull, extracted!.Tag,
                $"Extracted failure tag should be DiskFull, got {extracted.Tag}");
        }
    }
}
