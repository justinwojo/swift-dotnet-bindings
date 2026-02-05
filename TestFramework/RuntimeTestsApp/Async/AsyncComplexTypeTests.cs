// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;

namespace RuntimeTestsApp.Async;

/// <summary>
/// Tests for async enum/struct/class returns (Phase 60 regression).
///
/// STATUS: DEFERRED — No async methods exist in the current generated bindings.
/// The async Swift test files are in TestFramework/Sources/SwiftBindingsTestLib/Async.disabled/
/// and are compiled out of the test library.
///
/// When async Swift sources are re-enabled and bindings regenerated, implement:
/// - TestAsyncEnumReturn: async method returning an enum, verify case tag
/// - TestAsyncStructReturn: async method returning a struct, verify field values
/// - TestAsyncClassReturn: async method returning a class, verify properties
/// - TestAsyncOptionalReturn: async method returning nil/some optional
/// - TestAsyncComplexTypeLifetime: verify async result survives past completion
///
/// These tests should use Tier 2 and WithTimeout(DefaultAsyncTimeout).
/// </summary>
[TestTier(TestTier.Tier2)]
public class AsyncComplexTypeTests : TestBase
{
    public AsyncComplexTypeTests(TestResults results) : base(results) { }

    // TODO: Implement when async Swift sources are re-enabled in the test library.
    // The Async.disabled/ directory contains: AsyncComplexTypes.swift
    // Once re-enabled, regenerate bindings and add tests for:
    //
    // [TestTier(TestTier.Tier2)]
    // public async Task TestAsyncEnumReturn()
    // {
    //     var result = await WithTimeout(
    //         SomeAsyncType.AsyncEnumMethod(),
    //         DefaultAsyncTimeout);
    //     AssertNotNull(result, "Async enum return not null");
    //     AssertEqual(ExpectedEnum.CaseTag.SomeCase, result.Tag, "Async enum case");
    // }
    //
    // [TestTier(TestTier.Tier2)]
    // public async Task TestAsyncClassReturn()
    // {
    //     var result = await WithTimeout(
    //         SomeAsyncType.AsyncClassMethod(),
    //         DefaultAsyncTimeout);
    //     AssertNotNull(result, "Async class return not null");
    //     // Verify properties are accessible after async completion
    // }
}
