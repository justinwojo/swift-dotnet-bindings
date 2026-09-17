// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.ObjCInterop;

/// <summary>
/// End-to-end gate for an @objc protocol mixing async requirements, a completion-handler twin,
/// closures whose @MainActor/@Sendable attributes are spelled through a typealias, and a plain
/// synchronous requirement. The synthesized conformance used to fail to type-check (a sync witness
/// for an @objc async requirement, and a plain closure witness for an attributed alias), which
/// withdrew the whole protocol. A C# conformer handed to Swift proves the conformance compiled
/// and dispatches.
/// </summary>
public class ObjCAsyncRequirementDispatchTests : TestBase
{
    public ObjCAsyncRequirementDispatchTests(TestResults results) : base(results) { }

    public void TestSyncRequirementDispatchesBesideAsyncRequirements()
    {
        AssertEqual((nint)7, TestLibFunctions.StorefrontServiceRegionCount(new ManagedStorefrontService()),
            "Swift dispatched regionCount() into the C# conformer of the @objc protocol");
    }

    private sealed class ManagedStorefrontService : IStorefrontService
    {
        public void Storefront(Action<string?> completion) => completion("US");
        public Task<string?> GetStorefrontAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>("US");
        public void CountryCode(string region, Action<string?> completion) => completion(region);
        public Task<string> LogInAsync(string userID, CancellationToken cancellationToken = default) => Task.FromResult(userID);
        public Task<nint> GetProductCountAsync(CancellationToken cancellationToken = default) => Task.FromResult((nint)3);
        public void Refresh(Action<string?>? completion) => completion?.Invoke(null);
        public nint GetRegionCount() => 7;
    }
}
