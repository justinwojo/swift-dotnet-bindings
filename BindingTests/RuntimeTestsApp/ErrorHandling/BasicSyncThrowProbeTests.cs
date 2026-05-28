// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift.Runtime;

namespace RuntimeTestsApp.ErrorHandling;

/// <summary>
/// Discriminator probe for the Catalyst-x64 "first sync throw after async warmup" crash.
/// Alphabetically positioned between BasicProtocolDispatchTests and BasicThrowingTests so
/// these run immediately before BasicThrowingTests.TestDivideByZeroThrows in the full suite.
///
/// If any of these crashes on Catalyst-x64 with the same fault as TestDivideByZeroThrows,
/// the bug is in Mono's exception unwinder state after async warmup, NOT in the Swift→cdecl
/// thunk path or in SwiftMarshal.ThrowSwiftError. Each test isolates one progression step.
/// </summary>
public class BasicSyncThrowProbeTests : TestBase
{
    public BasicSyncThrowProbeTests(TestResults results) : base(results) { }

    /// <summary>
    /// Pure managed throw/catch. Zero Swift, zero P/Invoke. If this crashes on Catalyst-x64,
    /// the failure has nothing to do with our Swift wrapper code.
    /// </summary>
    public void TestPureManagedThrowAndCatch()
    {
        var caught = false;
        try
        {
            throw new InvalidOperationException("probe1");
        }
        catch (InvalidOperationException ex)
        {
            caught = true;
            AssertTrue(ex.Message == "probe1", $"message: {ex.Message}");
        }
        AssertTrue(caught, "expected catch to fire");
    }

    /// <summary>
    /// Pure managed throw of SwiftException specifically. Same exception type as the
    /// failing test, but no Swift native call precedes it.
    /// </summary>
    public void TestPureSwiftExceptionThrowAndCatch()
    {
        var caught = false;
        try
        {
            throw new SwiftException("probe2-divisionByZero");
        }
        catch (SwiftException ex)
        {
            caught = true;
            AssertTrue(ex.Message.Contains("divisionByZero"), $"message: {ex.Message}");
        }
        AssertTrue(caught, "expected catch to fire");
    }

    /// <summary>
    /// Mirrors the exact try/finally shape of SwiftMarshal.ThrowSwiftError: throw inside try,
    /// arbitrary side effect in finally. Still no Swift native call.
    /// </summary>
    public void TestThrowWithFinallySideEffect()
    {
        var finallyRan = false;
        var caught = false;
        try
        {
            try
            {
                throw new SwiftException("probe3-divisionByZero");
            }
            finally
            {
                finallyRan = true;
            }
        }
        catch (SwiftException ex)
        {
            caught = true;
            AssertTrue(ex.Message.Contains("divisionByZero"), $"message: {ex.Message}");
        }
        AssertTrue(finallyRan, "expected finally to run");
        AssertTrue(caught, "expected catch to fire");
    }

    /// <summary>
    /// Same try/finally shape, but the finally executes a P/Invoke (NativeMemory.Free of a
    /// zero pointer is a safe no-op on all platforms). This is the closest approximation
    /// to ThrowSwiftError without going through SwiftMarshal.
    /// </summary>
    public unsafe void TestThrowWithFinallyPInvoke()
    {
        var finallyRan = false;
        var caught = false;
        try
        {
            try
            {
                throw new SwiftException("probe4-divisionByZero");
            }
            finally
            {
                // P/Invoke a known-safe noop: free of nullptr is documented no-op.
                System.Runtime.InteropServices.NativeMemory.Free(null);
                finallyRan = true;
            }
        }
        catch (SwiftException ex)
        {
            caught = true;
            AssertTrue(ex.Message.Contains("divisionByZero"), $"message: {ex.Message}");
        }
        AssertTrue(finallyRan, "expected finally to run");
        AssertTrue(caught, "expected catch to fire");
    }
}
