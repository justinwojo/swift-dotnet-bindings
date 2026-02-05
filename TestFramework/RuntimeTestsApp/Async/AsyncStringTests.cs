// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;

namespace RuntimeTestsApp.Async;

/// <summary>
/// Tests for async string return + UTF-8 validation (Phase 58 regression).
///
/// STATUS: DEFERRED — No async methods exist in the current generated bindings.
/// The async Swift test files are in TestFramework/Sources/SwiftBindingsTestLib/Async.disabled/
/// and are compiled out of the test library.
///
/// When async Swift sources are re-enabled and bindings regenerated, implement:
/// - TestAsyncStringReturn: async method returning a string, verify UTF-8 round-trip
/// - TestAsyncEmptyStringReturn: async method returning empty string
/// - TestAsyncUnicodeStringReturn: async method returning unicode (CJK, emoji)
/// - TestAsyncStringTimeout: verify WithTimeout() works with async string methods
///
/// These tests should use Tier 2 and WithTimeout(DefaultAsyncTimeout).
/// </summary>
[TestTier(TestTier.Tier2)]
public class AsyncStringTests : TestBase
{
    public AsyncStringTests(TestResults results) : base(results) { }

    // TODO: Implement when async Swift sources are re-enabled in the test library.
    // The Async.disabled/ directory contains: Methods.swift, AsyncComplexTypes.swift, etc.
    // Once re-enabled, regenerate bindings and add tests for:
    //
    // [TestTier(TestTier.Tier2)]
    // public async Task TestAsyncStringReturn()
    // {
    //     var result = await WithTimeout(
    //         SomeAsyncType.AsyncStringMethod(),
    //         DefaultAsyncTimeout);
    //     AssertNotNull(result, "Async string return not null");
    //     AssertEqual("expected", result, "Async string round-trip");
    // }
}
