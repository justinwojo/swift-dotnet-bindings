// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using RuntimeTestsApp.Infrastructure;
using Swift;
using Swift.Runtime;

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
}
