// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Closures;

/// <summary>
/// Tests for closure bridge expansion:
/// 1. MCB on generic parent types (MethodClosureBridge with @_silgen_name extension)
/// 2. GenericClosureBridge with non-closure parameters
/// </summary>
public class GenericClosureBridgeTests : TestBase
{
    public GenericClosureBridgeTests(TestResults results) : base(results) { }

    // ─── MCB on Generic Parent ────────────────────────────────────────

    [Skip("MCB entry points not exported from dylib for generic parent classes")]
    public void TestGenericProcessorRun()
    {
        // GenericProcessor<T> is a generic class — MCB must use @_silgen_name extension
        // to inherit the generic context, with CallConvSwift + SwiftSelf on the C# side.
        var processor = new GenericProcessor<SwiftString>(label: "test", initialValue: (SwiftString)"hello");
        ProcessResult? captured = null;
        processor.Run(result => { captured = result; });
        AssertNotNull(captured, "Run callback was called");
        AssertTrue(TestLibFunctions.ProcessResultIsSuccess(captured!),
            "Run result is success");
        AssertEqual(42, TestLibFunctions.ProcessResultValue(captured!),
            "Run result value is 42");
        TestLogger.Info("GenericProcessor.Run MCB generic parent test passed");
    }

    [Skip("MCB entry points not exported from dylib for generic parent classes")]
    public void TestGenericProcessorRunWithFilter()
    {
        var processor = new GenericProcessor<SwiftString>(label: "filter", initialValue: (SwiftString)"world");
        bool result = processor.RunWithFilter(r => TestLibFunctions.ProcessResultIsSuccess(r));
        AssertTrue(result, "RunWithFilter returns true for success result");
        TestLogger.Info("GenericProcessor.RunWithFilter MCB generic parent test passed");
    }

    // ─── Optional MCB closure (Nuke/GRDB/Kingfisher pattern) ───────────

    /// <summary>
    /// MCB bridges an `Optional<(any Error) -> Void>` with a non-nil callback:
    /// Swift invokes it with a MathError, C# reconstructs AnyError.
    /// Wrapper uses `funcPtr.map { __fp in ... }` to round-trip non-nil through.
    /// </summary>
    public void TestOptionalErrorCallback_NonNull()
    {
        using var fixture = new OptionalErrorCallbackFixture();
        string? captured = null;
        int invoked = fixture.ReportIfPresent(err => captured = err.LocalizedDescription);
        AssertEqual(1, invoked, "ReportIfPresent returned invocation count");
        AssertNotNull(captured, "Optional callback was invoked");
        AssertTrue(captured!.Contains("divisionByZero"),
            $"Expected 'divisionByZero' in captured description, got: \"{captured}\"");
        TestLogger.Info("Optional MCB closure non-null round-trip passed");
    }

    /// <summary>
    /// MCB Optional closure with null: must round-trip as nil, skip GCHandle.Alloc,
    /// return 0 from Swift, no crash. This is the Nuke/GRDB/Kingfisher shape.
    /// </summary>
    public void TestOptionalErrorCallback_Null()
    {
        using var fixture = new OptionalErrorCallbackFixture();
        int invoked = fixture.ReportIfPresent(null);
        AssertEqual(0, invoked, "ReportIfPresent returned 0 for nil callback");
        TestLogger.Info("Optional MCB closure null round-trip passed");
    }
}
