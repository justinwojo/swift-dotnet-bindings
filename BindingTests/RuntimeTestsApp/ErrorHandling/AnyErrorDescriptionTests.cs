// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using RuntimeTestsApp.Infrastructure;
using Swift;
using Swift.Foundation;
using Swift.Runtime;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.ErrorHandling;

/// <summary>
/// Tests that AnyError.LocalizedDescription extracts error descriptions from
/// Swift errors via existential containers. Uses @_cdecl helpers to create
/// valid error containers, bypassing the closure callback path (which crashes
/// due to missing @_cdecl wrappers for existential closure parameters).
///
/// These tests validate the runtime-level property that Stripe delegate
/// callback handlers need: when a payment failure arrives as AnyError,
/// the developer can read the error description string.
/// </summary>
public class AnyErrorDescriptionTests : TestBase
{
    public AnyErrorDescriptionTests(TestResults results) : base(results) { }

    [DllImport("SwiftBindingsTestLib", EntryPoint = "SBW_Test_CreateMathErrorContainer")]
    private static unsafe extern void CreateMathErrorContainer(ExistentialContainer1* buffer);

    [DllImport("SwiftBindingsTestLib", EntryPoint = "SBW_Test_CreateNSErrorContainer")]
    private static unsafe extern void CreateNSErrorContainer(ExistentialContainer1* buffer);

    [DllImport("SwiftBindingsTestLib", EntryPoint = "SBW_Test_CreateValidationErrorContainer")]
    private static unsafe extern void CreateValidationErrorContainer(ExistentialContainer1* buffer);

    /// <summary>
    /// Tests that a Swift enum error (MathError.divisionByZero) produces a
    /// description containing the case name via String(describing:).
    /// </summary>
    public unsafe void TestAnyErrorDescription_SwiftEnum()
    {
        var container = new ExistentialContainer1();
        CreateMathErrorContainer(&container);
        var error = new AnyError(container);

        var desc = error.LocalizedDescription;
        TestLogger.Info($"AnyError.LocalizedDescription (MathError) = \"{desc}\"");
        AssertTrue(desc.Contains("divisionByZero"),
            $"Expected 'divisionByZero' in description, got: \"{desc}\"");
    }

    /// <summary>
    /// Tests that an NSError with a localized description produces the
    /// expected description string through AnyError.LocalizedDescription.
    /// </summary>
    public unsafe void TestAnyErrorDescription_NSError()
    {
        var container = new ExistentialContainer1();
        CreateNSErrorContainer(&container);
        var error = new AnyError(container);

        var desc = error.LocalizedDescription;
        TestLogger.Info($"AnyError.LocalizedDescription (NSError) = \"{desc}\"");
        // String(describing:) on NSError gives the full description including domain and userInfo
        AssertTrue(desc.Contains("Test error description") || desc.Contains("TestDomain"),
            $"Expected NSError description content, got: \"{desc}\"");
    }

    /// <summary>
    /// Tests that an enum with associated values (ValidationError.tooLong)
    /// produces a description including the case name and payload.
    /// </summary>
    public unsafe void TestAnyErrorDescription_AssociatedValue()
    {
        var container = new ExistentialContainer1();
        CreateValidationErrorContainer(&container);
        var error = new AnyError(container);

        var desc = error.LocalizedDescription;
        TestLogger.Info($"AnyError.LocalizedDescription (ValidationError) = \"{desc}\"");
        AssertTrue(desc.Contains("tooLong"),
            $"Expected 'tooLong' in description, got: \"{desc}\"");
    }

    // ────────────────────────────────────────────────────────────────────
    // Closure callback path (Fix 3): exercise the MCB wrapper that bridges
    // `(any Error) -> Void` closures — the Swift side hands an existential
    // container pointer to the @_cdecl callback, which constructs a Swift.Foundation.AnyError
    // the C# lambda can read. This is the pattern Stripe completion handlers
    // (Result<T, any Error>) rely on.
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// MCB closure bridge: Swift invokes the callback with a MathError.divisionByZero,
    /// C# reconstructs an AnyError and reads its description.
    /// </summary>
    public void TestAnyErrorClosure_MathError()
    {
        using var fixture = new AnyErrorCallbackFixture();
        string? captured = null;
        fixture.ReportMathError(err => captured = err.LocalizedDescription);
        TestLogger.Info($"Callback received MathError description = \"{captured}\"");
        AssertNotNull(captured, "Callback was not invoked");
        AssertTrue(captured!.Contains("divisionByZero"),
            $"Expected 'divisionByZero' in callback description, got: \"{captured}\"");
    }

    /// <summary>
    /// MCB closure bridge: associated-value enum round-trips through the callback.
    /// </summary>
    public void TestAnyErrorClosure_ValidationError()
    {
        using var fixture = new AnyErrorCallbackFixture();
        string? captured = null;
        fixture.ReportValidationError(err => captured = err.LocalizedDescription);
        TestLogger.Info($"Callback received ValidationError description = \"{captured}\"");
        AssertNotNull(captured, "Callback was not invoked");
        AssertTrue(captured!.Contains("tooLong"),
            $"Expected 'tooLong' in callback description, got: \"{captured}\"");
    }

    /// <summary>
    /// MCB closure bridge: NSError (ObjC-bridged) round-trips with its
    /// localized description intact.
    /// </summary>
    public void TestAnyErrorClosure_NSError()
    {
        using var fixture = new AnyErrorCallbackFixture();
        string? captured = null;
        fixture.ReportNSError(err => captured = err.LocalizedDescription);
        TestLogger.Info($"Callback received NSError description = \"{captured}\"");
        AssertNotNull(captured, "Callback was not invoked");
        AssertTrue(captured!.Contains("Callback error description") || captured!.Contains("CallbackDomain"),
            $"Expected NSError description content, got: \"{captured}\"");
    }

    // ────────────────────────────────────────────────────────────────────
    // Pattern A: Optional<any Error> closure parameter.
    // Matches Stripe PaymentSheet.FlowController.update completion shape.
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// `(any Error)?` closure parameter: success branch passes null, the
    /// C# callback observes AnyError? == null.
    /// </summary>
    public void TestOptionalAnyErrorClosure_Success()
    {
        using var fixture = new AnyErrorCallbackFixture();
        AnyError? captured = new AnyError(); // sentinel non-null
        bool invoked = false;
        fixture.ReportOptionalError(shouldSucceed: true, err => { captured = err; invoked = true; });
        AssertTrue(invoked, "Callback was not invoked (success branch)");
        AssertFalse(captured.HasValue, "Expected null AnyError on success branch");
    }

    /// <summary>
    /// `(any Error)?` closure parameter: failure branch delivers a MathError.
    /// Reads LocalizedDescription inside the callback — the borrowed existential
    /// container lives on Swift's stack for the duration of the callback only.
    /// </summary>
    public void TestOptionalAnyErrorClosure_Failure()
    {
        using var fixture = new AnyErrorCallbackFixture();
        string? capturedDesc = null;
        bool sawError = false;
        fixture.ReportOptionalError(shouldSucceed: false, err =>
        {
            if (err.HasValue)
            {
                sawError = true;
                capturedDesc = err.Value.LocalizedDescription;
            }
        });
        AssertTrue(sawError, "Expected non-null AnyError on failure branch");
        TestLogger.Info($"Pattern A failure desc = \"{capturedDesc}\"");
        AssertTrue(capturedDesc!.Contains("divisionByZero"),
            $"Expected 'divisionByZero' in description, got: \"{capturedDesc}\"");
    }

    /// <summary>
    /// 3-arg closure `(Int32, Int32, (any Error)?)` — success branch: (1234, 1, null).
    /// </summary>
    public void TestMultiArgOptionalAnyErrorClosure_Success()
    {
        using var fixture = new AnyErrorCallbackFixture();
        int pin = -1;
        int status = -1;
        AnyError? err = new AnyError();
        bool invoked = false;
        fixture.ReportPinDetails(0, (p, s, e) => { pin = p; status = s; err = e; invoked = true; });
        AssertTrue(invoked, "Callback was not invoked (success branch)");
        AssertEqual(1234, pin, $"Expected pin 1234, got {pin}");
        AssertEqual(1, status, $"Expected status 1, got {status}");
        AssertFalse(err.HasValue, "Expected null error on success branch");
    }

    /// <summary>
    /// 3-arg closure `(Int32, Int32, (any Error)?)` — failure branch: (0, 0, MathError).
    /// Reads LocalizedDescription inside the callback (borrowed-container lifetime).
    /// </summary>
    public void TestMultiArgOptionalAnyErrorClosure_Failure()
    {
        using var fixture = new AnyErrorCallbackFixture();
        int pin = -1;
        int status = -1;
        bool sawError = false;
        string? errDesc = null;
        fixture.ReportPinDetails(1, (p, s, e) =>
        {
            pin = p;
            status = s;
            if (e.HasValue)
            {
                sawError = true;
                errDesc = e.Value.LocalizedDescription;
            }
        });
        AssertEqual(0, pin, $"Expected pin 0 on failure, got {pin}");
        AssertEqual(0, status, $"Expected status 0, got {status}");
        AssertTrue(sawError, "Expected non-null error on failure branch");
        AssertTrue(errDesc!.Contains("divisionByZero"),
            $"Expected 'divisionByZero' in error description, got: \"{errDesc}\"");
    }

    // ────────────────────────────────────────────────────────────────────
    // Pattern B: Result<T, any Error> closure parameter.
    // Matches Stripe completion handlers like
    //   (Result<PaymentSheet.FlowController, any Error>) -> Void
    // Swift wraps the Result enum via withUnsafePointer; C# heap-copies
    // the payload into a SafeHandle-owned SwiftResult<T, ExistentialContainer1>.
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Success branch: Result.success(42) round-trips as SwiftResult with Case == Success
    /// and Success == 42.
    /// </summary>
    public void TestResultClosure_Success()
    {
        using var fixture = new AnyErrorCallbackFixture();
        int successValue = -1;
        bool observedSuccess = false;
        fixture.ReportResult(shouldSucceed: true, result =>
        {
            if (result.TryGetSuccess(out var v))
            {
                observedSuccess = true;
                successValue = v;
            }
            result.Dispose();
        });
        AssertTrue(observedSuccess, "Expected success branch");
        AssertEqual(42, successValue, $"Expected 42, got {successValue}");
    }

    /// <summary>
    /// Failure branch: Result.failure(MathError.divisionByZero) round-trips as SwiftResult
    /// with Case == Failure and failure existential carrying MathError.
    /// </summary>
    public void TestResultClosure_Failure()
    {
        using var fixture = new AnyErrorCallbackFixture();
        bool observedFailure = false;
        string? errDesc = null;
        fixture.ReportResult(shouldSucceed: false, result =>
        {
            if (result.TryGetFailure(out var container))
            {
                observedFailure = true;
                var err = new Swift.Foundation.AnyError(container);
                errDesc = err.LocalizedDescription;
            }
            result.Dispose();
        });
        AssertTrue(observedFailure, "Expected failure branch");
        TestLogger.Info($"Pattern B failure desc = \"{errDesc}\"");
        AssertTrue(errDesc!.Contains("divisionByZero"),
            $"Expected 'divisionByZero' in description, got: \"{errDesc}\"");
    }
}
