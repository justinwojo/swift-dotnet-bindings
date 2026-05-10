// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Marshalling;

/// <summary>
/// Tests for `Swift.Set&lt;T&gt;` projection at the parameter boundary, with empty-literal
/// defaults. Regression coverage for Bundle 04 #9
/// (`gap-0.10.0-swift-set-parameter-becomes-ienumerable-default-lost.md`).
///
/// Pre-fix: Swift `func f(_ values: Set&lt;Int&gt; = [])` projected to
/// `f(IEnumerable&lt;nint&gt; values)` — uniqueness invariant dropped at the public API
/// surface, and the `[]` default was silently elided so callers had to construct an
/// empty enumerable explicitly.
///
/// Post-fix:
///   1. Parameter projects as `IReadOnlySet&lt;nint&gt;` (uniqueness preserved at the
///      type-system level — caller must materialise an actual set).
///   2. `[]` default surfaces as a trim overload `f()` that calls Swift's defaulted
///      free function, so callers can omit the parameter entirely.
///
/// Both invariants are covered for sync and async free-function shapes (StoreKit's
/// `Product.purchase(options: Set&lt;PurchaseOption&gt; = [])` is the canonical async
/// case). Element type is `Int` (Swift) / `nint` (C#) rather than `Int32` because
/// `SwiftSet&lt;int&gt;.Add` falls through to the runtime's CallConvSwift fallback,
/// which is documented as broken on Mono Simulator. The `nint` path uses the
/// working `SBW_SetInt_Insert` `@_cdecl` wrapper.
/// </summary>
public class SetParameterDefaultTests : TestBase
{
    public SetParameterDefaultTests(TestResults results) : base(results) { }

    #region Sync — explicit Set parameter

    public void TestSetMembershipCountEmpty()
    {
        // Explicit empty IReadOnlySet — the round-trip materialises the buffer
        // through SwiftSet<nint>.FromEnumerable(values) on the wrapper side.
        var count = TestLibFunctions.SetMembershipCount(new HashSet<nint>());
        AssertEqual((nint)0, count, "Empty IReadOnlySet<nint> count = 0");
        TestLogger.Info("SetMembershipCount(empty HashSet) = 0");
    }

    public void TestSetMembershipCountPopulated()
    {
        var values = new HashSet<nint> { 1, 2, 3, 4, 5 };
        var count = TestLibFunctions.SetMembershipCount(values);
        AssertEqual((nint)5, count, "Populated IReadOnlySet<nint> count = 5");
        TestLogger.Info($"SetMembershipCount({{1..5}}) = {count}");
    }

    public void TestSetMembershipCountDeduplicates()
    {
        // HashSet<nint> de-duplicates on .Add, so this proves the C# side preserves
        // Swift's Set semantics — same as Swift's `Set<Int>([1, 1, 2, 2, 3])`.
        var values = new HashSet<nint>();
        foreach (var v in new nint[] { 1, 1, 2, 2, 3 })
            values.Add(v);
        var count = TestLibFunctions.SetMembershipCount(values);
        AssertEqual((nint)3, count, "HashSet collapses duplicates → Swift sees count = 3");
        TestLogger.Info($"SetMembershipCount({{1,1,2,2,3}}) → unique count = {count}");
    }

    public void TestSetMembershipSumPopulated()
    {
        // Round-trip evidence the post-fix payload is still routed correctly when the
        // caller passes an explicit set (sum = 1+2+3+4+5 = 15).
        var values = new HashSet<nint> { 1, 2, 3, 4, 5 };
        var sum = TestLibFunctions.SetMembershipSum(values);
        AssertEqual((nint)15, sum, "Sum of {1..5} = 15");
        TestLogger.Info($"SetMembershipSum({{1..5}}) = {sum}");
    }

    #endregion

    #region Sync — empty-literal default trim overload

    public void TestSetMembershipCountDefaultTrimOverload()
    {
        // The Swift `= []` default surfaces as a no-arg trim overload. Pre-fix the
        // default was silently dropped — caller had to construct an empty IEnumerable.
        var count = TestLibFunctions.SetMembershipCount();
        AssertEqual((nint)0, count, "SetMembershipCount() with default = 0");
        TestLogger.Info("SetMembershipCount() (defaulted) = 0");
    }

    public void TestSetMembershipSumDefaultTrimOverload()
    {
        var sum = TestLibFunctions.SetMembershipSum();
        AssertEqual((nint)0, sum, "SetMembershipSum() with default = 0");
        TestLogger.Info("SetMembershipSum() (defaulted) = 0");
    }

    #endregion

    #region Async — StoreKit Product.purchase shape

    // Async-with-set: exercises the SwiftSet container lifetime hand-off
    // across the async suspension point — the container must outlive the
    // foreground frame so the Swift continuation can dereference its
    // payload buffer.

    public async Task TestSetMembershipCountAsyncEmpty()
    {
        // Async variant of the StoreKit `purchase(options: Set<…> = []) async`
        // shape. Confirms the trim-overload generator handles async parameters.
        var count = await TestLibFunctions.SetMembershipCountAsync(new HashSet<nint>());
        AssertEqual((nint)0, count, "Async empty set count = 0");
        TestLogger.Info("SetMembershipCountAsync(empty) = 0");
    }

    public async Task TestSetMembershipCountAsyncPopulated()
    {
        var values = new HashSet<nint> { 10, 20, 30 };
        var count = await TestLibFunctions.SetMembershipCountAsync(values);
        AssertEqual((nint)3, count, "Async populated set count = 3");
        TestLogger.Info($"SetMembershipCountAsync({{10,20,30}}) = {count}");
    }

    public async Task TestSetMembershipCountAsyncDefaultTrimOverload()
    {
        // Async no-arg overload — the canonical StoreKit purchase-with-default case.
        var count = await TestLibFunctions.SetMembershipCountAsync();
        AssertEqual((nint)0, count, "Async defaulted set count = 0");
        TestLogger.Info("SetMembershipCountAsync() (defaulted) = 0");
    }

    #endregion
}
