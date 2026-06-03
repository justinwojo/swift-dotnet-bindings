// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift;
using Swift.Runtime;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Async;

/// <summary>
/// Coverage for the StoreKit2 `Product.purchase&lt;some UIScene&gt;(confirmIn:, options: Set&lt;…&gt; = [])`
/// shape — class-bound non-CSM generic + Set-valued default + async + throws.
///
/// Validates both layers of the fix:
///   • <see cref="AsyncMethodGenericBridgeEmitter"/> emits the primary @_cdecl
///     overload (existential opening on the class-bound generic).
///   • <c>DefaultParameterOverloadEmitter</c> threads the method-own generic
///     header + where-clause through the trim @_silgen_name shim so the trim
///     overload (omitting the Set default) compiles and binds.
/// </summary>
public class AsyncMethodGenericDefaultsTests : TestBase
{
    public AsyncMethodGenericDefaultsTests(TestResults results) : base(results) { }

    public async Task TestPrimary_PassesPresenter_ReturnsResult()
    {
        // Primary overload: caller passes both confirmIn and options. The class-bound
        // existential is opened on the Swift side via Unmanaged<AnyObject>.fromOpaque,
        // and the non-frozen struct return travels back through the bridge's
        // `cbTakesOwnership` ComplexValue branch: Swift `UnsafeMutableRawPointer.allocate`
        // + `initializeMemory(as:)` produces a `_resultBuf`; the C# callback `NativeMemory
        // .Alloc`s a fresh `__resultBuf`, `InitializeWithCopy`s the carrier into it, then
        // VWT-`Destroy`s the original carrier; the SafeHandle owns `__resultBuf` (freed in
        // `ReleaseHandle`), and the per-module `SBW_Free` helper deallocates the Swift
        // carrier in `finally` (allocator-paired with Swift's `.deallocate()`).
        using var product = new AsyncGenericProduct(title: "TestProduct");
        using var presenter = new AsyncGenericPresenterImpl(presenterId: "ABC");

        var options = new HashSet<nint> { 7, 13 };

        var result = await product.PurchaseAsync(presenter, options);

        AssertTrue(result.Succeeded, "primary — succeeded should be true");
        AssertEqual(2, (int)result.OptionCount,
            "primary — Swift should observe caller's options.count=2");
        // 'A' (65) + 'B' (66) + 'C' (67) = 198
        AssertEqual(198L, (long)result.PresenterIdHash,
            "primary — presenterId hash should round-trip from existential opening");
    }

    public async Task TestTrim_OmitsOptions_FillsSwiftDefault()
    {
        // Trim overload: caller omits `options`. Swift fills the empty-Set default.
        // Method-own generic <S: AsyncGenericPresenter> + where-clause must be
        // threaded through the @_silgen_name shim (Layer 1) for this to compile,
        // and the trim's C# entry must bind to it correctly.
        using var product = new AsyncGenericProduct(title: "TestProduct");
        using var presenter = new AsyncGenericPresenterImpl(presenterId: "Z");

        var result = await product.PurchaseAsync(presenter);

        AssertTrue(result.Succeeded, "trim — succeeded should be true");
        AssertEqual(0, (int)result.OptionCount,
            "trim — Swift default should fill empty Set (count=0)");
        AssertEqual(90L, (long)result.PresenterIdHash,  // 'Z' = 90
            "trim — presenterId hash should round-trip");
    }

    public async Task TestPrimary_Throws_PropagatesError()
    {
        // Empty presenterId triggers the Swift-side throw. The async error cascade
        // must propagate the SwiftException through to the awaiting Task.
        using var product = new AsyncGenericProduct(title: "TestProduct");
        using var presenter = new AsyncGenericPresenterImpl(presenterId: "");

        try
        {
            _ = await product.PurchaseAsync(presenter, new HashSet<nint>());
            AssertTrue(false, "primary throws — expected exception was not raised");
        }
        catch (Swift.Runtime.SwiftException)
        {
            // Expected — typed cascade or untyped fallback both surface as SwiftException
        }
    }

    public async Task TestTrim_Throws_PropagatesError()
    {
        using var product = new AsyncGenericProduct(title: "TestProduct");
        using var presenter = new AsyncGenericPresenterImpl(presenterId: "");

        try
        {
            _ = await product.PurchaseAsync(presenter);
            AssertTrue(false, "trim throws — expected exception was not raised");
        }
        catch (Swift.Runtime.SwiftException)
        {
            // Expected
        }
    }

    public async Task TestReserve_UserParamNamedI_RoundTripsAlongsideExistential()
    {
        // Loop-index collision regression (S2): `reserve<S>(confirmIn:, i: Int)` has a
        // trailing parameter named `i`. The bridge inlines a holder-cleanup loop into the
        // public ReserveAsync body; before the SyntheticNameScope guard its index hard-coded
        // `i`, self-shadowing the parameter (CS0136) so the binding would not compile. Reaching
        // this assertion at runtime already proves it compiled; the value check confirms the
        // primitive `i` argument is marshalled correctly alongside the opened existential.
        using var product = new AsyncGenericProduct(title: "TestProduct");
        using var presenter = new AsyncGenericPresenterImpl(presenterId: "ABC");

        var result = await product.ReserveAsync(presenter, 5);

        // presenterId "ABC".count (3) + i (5) = 8
        AssertEqual(8L, (long)result,
            "reserve — primitive `i` must round-trip alongside the opened existential");
    }

    public async Task TestPrimary_Cancellation_Cancels()
    {
        // Pre-cancel before the async call — the harness should immediately
        // return a cancelled Task without firing the Swift Task body.
        using var product = new AsyncGenericProduct(title: "TestProduct");
        using var presenter = new AsyncGenericPresenterImpl(presenterId: "ABC");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        try
        {
            _ = await product.PurchaseAsync(
                presenter,
                new HashSet<nint>(),
                cts.Token);
            AssertTrue(false, "primary cancel — expected TaskCanceledException");
        }
        catch (OperationCanceledException)
        {
            // Expected
        }
    }
}
