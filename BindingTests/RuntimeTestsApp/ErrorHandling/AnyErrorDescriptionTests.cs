// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using RuntimeTestsApp.Infrastructure;
using Swift;
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
    // container pointer to the @_cdecl callback, which constructs a Swift.AnyError
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
}
