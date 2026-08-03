// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Closures;

/// <summary>
/// Round-trips a complex enum riding alongside an existential-error closure through the
/// closure bridge's @_cdecl wrapper.
///
/// The bridge only emits a wrapper when every parameter is passable, so a complex-enum
/// "carrier" parameter used to reject the whole method and drop it onto the direct
/// CallConvSwift path. A complex enum projects exactly like a non-frozen struct — a C#
/// class over an opaque payload — so the payload pointer crosses the boundary and Swift
/// reloads the value; these tests assert that value actually survives the trip, in both
/// arms of the completion.
/// </summary>
public class ClosureBridgeEnumCarrierTests : TestBase
{
    public ClosureBridgeEnumCarrierTests(TestResults results) : base(results) { }

    /// <summary>
    /// Payload-carrying case: `.limited(max: 21)` must arrive intact, so the success
    /// value is 42 rather than the -1 the `.everything` case would produce.
    /// </summary>
    public void TestEnumCarrierResultSuccess()
    {
        using var bridge = new EnumCarrierClosureBridge();
        using var scope = FetchScope.Limited(21);
        int successValue = -999;
        bool observedSuccess = false;
        bridge.Fetch(scope, result =>
        {
            if (result.TryGetSuccess(out var v))
            {
                observedSuccess = true;
                successValue = v;
            }
            result.Dispose();
        });
        AssertTrue(observedSuccess, "Expected the success arm for .limited(max: 21)");
        AssertEqual(42, successValue, $"Expected 21 * 2 = 42, got {successValue}");
    }

    /// <summary>
    /// No-payload case: `.everything` selects the -1 branch, proving the tag itself
    /// (not just an associated value) discriminates correctly on the Swift side.
    /// </summary>
    public void TestEnumCarrierResultSuccessNoPayloadCase()
    {
        using var bridge = new EnumCarrierClosureBridge();
        int successValue = -999;
        bool observedSuccess = false;
        bridge.Fetch(FetchScope.Everything, result =>
        {
            if (result.TryGetSuccess(out var v))
            {
                observedSuccess = true;
                successValue = v;
            }
            result.Dispose();
        });
        AssertTrue(observedSuccess, "Expected the success arm for .everything");
        AssertEqual(-1, successValue, $"Expected -1 for .everything, got {successValue}");
    }

    /// <summary>
    /// Failure arm: `.rejected` carries a String payload and selects the error branch,
    /// so the existential error crosses back alongside the enum that selected it.
    /// </summary>
    public void TestEnumCarrierResultFailure()
    {
        using var bridge = new EnumCarrierClosureBridge();
        using var scope = FetchScope.Rejected("nope");
        bool observedFailure = false;
        string? errDesc = null;
        bridge.Fetch(scope, result =>
        {
            if (result.TryGetFailure(out var container))
            {
                observedFailure = true;
                errDesc = new Swift.Foundation.AnyError(container).LocalizedDescription;
            }
            result.Dispose();
        });
        AssertTrue(observedFailure, "Expected the failure arm for .rejected");
        AssertTrue(errDesc!.Contains("divisionByZero"),
            $"Expected 'divisionByZero' in description, got: \"{errDesc}\"");
    }

    /// <summary>
    /// Same carrier axis against the `(any Error)?` completion shape — nil arm.
    /// </summary>
    public void TestEnumCarrierOptionalErrorNil()
    {
        using var bridge = new EnumCarrierClosureBridge();
        bool invoked = false;
        Swift.Foundation.AnyError? captured = null;
        bridge.Write(WriteMode.Truncate, err => { invoked = true; captured = err; });
        AssertTrue(invoked, "Completion was not invoked for .truncate");
        AssertTrue(captured is null, "Expected a nil error for .truncate");
    }

    /// <summary>
    /// Same carrier axis against the `(any Error)?` completion shape — non-nil arm,
    /// selected by the enum's associated value (offset &lt; 0).
    /// </summary>
    public void TestEnumCarrierOptionalErrorNonNil()
    {
        using var bridge = new EnumCarrierClosureBridge();
        using var mode = WriteMode.Append(-3);
        bool sawError = false;
        string? errDesc = null;
        bridge.Write(mode, err =>
        {
            if (err != null)
            {
                sawError = true;
                errDesc = err.LocalizedDescription;
            }
        });
        AssertTrue(sawError, "Expected a non-nil error for .append(offset: -3)");
        AssertTrue(errDesc!.Contains("divisionByZero"),
            $"Expected 'divisionByZero' in description, got: \"{errDesc}\"");
    }

    /// <summary>
    /// The positive control for the non-nil arm's selector: the same case with a
    /// non-negative offset must report success, so the previous test is reading the
    /// associated value rather than the case tag alone.
    /// </summary>
    public void TestEnumCarrierOptionalErrorPayloadSelectsArm()
    {
        using var bridge = new EnumCarrierClosureBridge();
        using var mode = WriteMode.Append(3);
        bool invoked = false;
        Swift.Foundation.AnyError? captured = null;
        bridge.Write(mode, err => { invoked = true; captured = err; });
        AssertTrue(invoked, "Completion was not invoked for .append(offset: 3)");
        AssertTrue(captured is null, "Expected a nil error for a non-negative offset");
    }

    /// <summary>
    /// Two enum carriers plus a primitive ahead of the completion: `.limited(max: 5)`
    /// and `.append(offset: 2)` combine to 7, times a multiplier of 3.
    /// </summary>
    public void TestTwoEnumCarriersAndPrimitive()
    {
        using var bridge = new EnumCarrierClosureBridge();
        using var scope = FetchScope.Limited(5);
        using var mode = WriteMode.Append(2);
        int successValue = -999;
        bool observedSuccess = false;
        bridge.Combine(scope, mode, 3, result =>
        {
            if (result.TryGetSuccess(out var v))
            {
                observedSuccess = true;
                successValue = v;
            }
            result.Dispose();
        });
        AssertTrue(observedSuccess, "Expected the success arm from Combine");
        AssertEqual(21, successValue, $"Expected (5 + 2) * 3 = 21, got {successValue}");
    }

    /// <summary>
    /// Free-function form — the bridge's static path, where there is no self pointer
    /// to anchor the call and the enum carrier is the only pointer argument.
    /// </summary>
    public void TestEnumCarrierFreeFunction()
    {
        using var scope = FetchScope.Limited(77);
        int successValue = -999;
        bool observedSuccess = false;
        TestLibFunctions.FetchWithScope(scope, result =>
        {
            if (result.TryGetSuccess(out var v))
            {
                observedSuccess = true;
                successValue = v;
            }
            result.Dispose();
        });
        AssertTrue(observedSuccess, "Expected the success arm from the free function");
        AssertEqual(77, successValue, $"Expected 77, got {successValue}");
    }

    /// <summary>
    /// Repeated calls with fresh carriers — the wrapper must not consume or invalidate
    /// the payload it borrows, so a second call with an equivalent value behaves the same.
    /// </summary>
    public void TestEnumCarrierRepeatedCalls()
    {
        using var bridge = new EnumCarrierClosureBridge();
        for (int i = 1; i <= 3; i++)
        {
            using var scope = FetchScope.Limited(i);
            int successValue = -999;
            bridge.Fetch(scope, result =>
            {
                if (result.TryGetSuccess(out var v))
                    successValue = v;
                result.Dispose();
            });
            AssertEqual(i * 2, successValue, $"Iteration {i}: expected {i * 2}, got {successValue}");
        }
    }

    /// <summary>
    /// One carrier instance reused across two calls — the borrow must leave the C#-side
    /// payload usable afterwards.
    /// </summary>
    public void TestEnumCarrierReusedInstance()
    {
        using var bridge = new EnumCarrierClosureBridge();
        using var scope = FetchScope.Limited(9);
        int first = -999;
        int second = -999;
        bridge.Fetch(scope, r => { if (r.TryGetSuccess(out var v)) first = v; r.Dispose(); });
        bridge.Fetch(scope, r => { if (r.TryGetSuccess(out var v)) second = v; r.Dispose(); });
        AssertEqual(18, first, $"First call: expected 18, got {first}");
        AssertEqual(18, second, $"Second call after reuse: expected 18, got {second}");
    }

    /// <summary>
    /// A carrier whose Swift argument label is a C# keyword. The label the bridge writes into
    /// its Swift call has to be the raw Swift one, not the identifier the parser rewrote it to,
    /// or the wrapper source does not compile — and a wrapper that does not compile costs every
    /// member in the module, not just this one. Reaching this assertion at all means the wrapper
    /// built; the values confirm both carriers arrived in the right positions.
    /// </summary>
    public void TestEnumCarrierKeywordArgumentLabel()
    {
        using var bridge = new EnumCarrierClosureBridge();
        using var scope = FetchScope.Limited(5);
        using var mode = WriteMode.Append(4);
        int successValue = -999;
        bool observedSuccess = false;
        bridge.RetryResult(scope, mode, result =>
        {
            if (result.TryGetSuccess(out var v))
            {
                observedSuccess = true;
                successValue = v;
            }
            result.Dispose();
        });
        AssertTrue(observedSuccess, "Expected the success arm from the keyword-labelled member");
        AssertEqual(9, successValue, $"Expected 5 + 4 = 9, got {successValue}");
    }
}
