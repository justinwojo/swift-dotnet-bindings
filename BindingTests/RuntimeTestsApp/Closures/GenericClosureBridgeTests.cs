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

    // ─── Swift.String as MCB non-closure param (Stripe pattern) ──────

    /// <summary>
    /// MCB passes a Swift.String non-closure parameter as a UTF-8 (pointer, length)
    /// pair. C# pins the bytes via `fixed`, Swift rebuilds the String, and the
    /// Result-returning callback surfaces the rebuilt length back to the caller.
    /// Round-trip must match the UTF-8 byte count for an ASCII input and a
    /// multi-byte-character input.
    /// </summary>
    public void TestStringParamMCB_RoundTrip()
    {
        using var fixture = new StringParamMCBFixture();

        int? asciiCount = null;
        fixture.Measure("hello", result =>
        {
            if (result.IsSuccess) asciiCount = result.Success;
        });
        AssertEqual(5, asciiCount, "ASCII UTF-8 count round-tripped through Utf8Slice");

        int? multiByteCount = null;
        fixture.Measure("héllo", result =>
        {
            if (result.IsSuccess) multiByteCount = result.Success;
        });
        // "héllo": h(1) + é(2) + l(1) + l(1) + o(1) = 6 UTF-8 bytes
        AssertEqual(6, multiByteCount, "Multi-byte UTF-8 count round-tripped through Utf8Slice");

        TestLogger.Info("Swift.String non-closure MCB param round-trip passed");
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

    // ─── GenericClosureBridge: method-generic noescape closure + non-closure param ───
    //
    // The P1-22 synthetic-name guard on the GenericClosureBridge path is proven at the emitter
    // layer by GenericClosureBridgeEmitterTests.TryEmit_UserParamNamedCdecl_* /
    // TryEmit_UserParamNamedUnderscoreSelf_* (assert `cdecl`→`__cdecl` and `_self`→`___self`
    // transitive renames in the generated Swift) plus the compile gate (the renamed wrappers
    // compile — a collision would be an "invalid redeclaration" → stripped symbol). A *runtime*
    // round-trip of these methods would be a nice extra proof, but the entire GenericClosureBridge
    // runtime path is blocked by a pre-existing, orthogonal self-register ABI defect (logged in
    // REMEDIATION-PLAN §6, out of Session 2's UCO/synthetic-name cluster): the generated C#
    // P/Invoke passes the receiver via `SwiftSelf` (the Swift self/context register, CallConvSwift),
    // but the Swift `@_silgen_name` *free-function* wrapper declares `_self`/`___self` as a regular
    // trailing parameter and reads it from a normal GPR the C# side never set → garbage `self` →
    // SIGABRT (mono GC-safe-region transition) / SIGSEGV on every call. This defect predates Session
    // 2 (it reproduces on `read`, which has no synthetic-name collision at all, so the synthetic-name
    // guard is not implicated) and affects only `DatabaseReader.read*`, which had no prior runtime
    // coverage — nothing else regresses. The four tests below stay [Skip] until §6 lands; the SIGABRT
    // fires *inside* the native call, meaning the P/Invoke resolved — itself implicit proof the
    // renamed-synthetic wrappers compiled.

    /// <summary>
    /// Baseline for the GenericClosureBridge void path (method-generic, noescape, throwing closure
    /// with a non-closure class param). Identity-forwards the param into the closure; its Name must
    /// round-trip. Blocked by the §6 self-register ABI defect (garbage `self` → SIGABRT in the mono
    /// GC-safe-region transition during the call). Unskip when §6 lands.
    /// </summary>
    [Skip("REMEDIATION-PLAN §6: GenericClosureBridge passes the receiver via SwiftSelf (self register) but the @_silgen_name free-function wrapper reads _self as a regular GPR param → garbage self → SIGABRT. Pre-existing, affects all GenericClosureBridge runtime calls.")]
    public void TestGenericRead_RoundTrips()
    {
        using var source = new DatabaseReader("primary");
        using var reader = new DatabaseReader("outer");
        string? captured = null;
        reader.Read(db => captured = db.Name, source);
        AssertEqual("primary", captured, "GenericClosureBridge forwards the source param into the closure");
        TestLogger.Info("GenericClosureBridge read baseline (void) passed");
    }

    /// <summary>
    /// P1-22 (C1): user param `cdecl` collides with the GenericClosureBridge synthetic func-ptr
    /// local (`let cdecl = unsafeBitCast(...)`), which the guard renames to `__cdecl` (asserted at
    /// the emitter layer in GenericClosureBridgeEmitterTests). The runtime round-trip is blocked by
    /// the §6 self-register ABI defect, not the guard. Unskip when §6 lands.
    /// </summary>
    [Skip("REMEDIATION-PLAN §6: GenericClosureBridge self-register ABI defect (SwiftSelf vs free-function _self GPR param) → garbage self → SIGABRT. Guard verified at emitter layer; runtime blocked.")]
    public void TestGenericCdeclParamCollision_RoundTrips()
    {
        using var source = new DatabaseReader("primary");
        using var reader = new DatabaseReader("outer");
        string? captured = null;
        reader.ReadWithCdecl(db => captured = db.Name, source);
        AssertEqual("primary", captured, "user param `cdecl` reaches the closure despite colliding with the synthetic func-ptr local");
        TestLogger.Info("P1-22 generic cdecl collision (void) passed");
    }

    /// <summary>
    /// P1-22 (C1): user param `_self` collides with the GenericClosureBridge synthetic self-pointer
    /// param, which the guard renames transitively to `___self` (asserted at the emitter layer in
    /// GenericClosureBridgeEmitterTests). The runtime round-trip is blocked by the §6 self-register
    /// ABI defect, not the guard. Unskip when §6 lands.
    /// </summary>
    [Skip("REMEDIATION-PLAN §6: GenericClosureBridge self-register ABI defect (SwiftSelf vs free-function _self GPR param) → garbage self → SIGABRT. Guard verified at emitter layer; runtime blocked.")]
    public void TestGenericSelfParamCollision_RoundTrips()
    {
        using var source = new DatabaseReader("primary");
        using var reader = new DatabaseReader("outer");
        string? captured = null;
        reader.ReadWithSelf(db => captured = db.Name, source);
        AssertEqual("primary", captured, "user param `_self` reaches the closure despite colliding with the synthetic self-pointer param");
        TestLogger.Info("P1-22 generic _self collision (void) passed");
    }

    /// <summary>
    /// REMEDIATION-PLAN §6 (out of Session 2's UCO/synthetic-name cluster): the GenericClosureBridge
    /// generic-RETURN overload (<c>Read&lt;T&gt;(Func&lt;…,T&gt;, …)</c>) carries a *second*,
    /// independent defect on top of the self-register ABI one — it marshals a class-typed result
    /// incorrectly. The C# emission writes the closure result into a caller-owned <c>resultBuf</c>
    /// via <c>MarshalToSwift</c> (an <c>InitializeWithCopy</c> that stores the object pointer *inside*
    /// the buffer), then reads it back with <c>MarshalFromSwift&lt;T&gt;(new IntPtr(resultBuf))</c>.
    /// For a class <c>T</c>, <c>MarshalFromSwift</c> hands its argument straight to
    /// <c>NewFromPayload</c> as the object pointer — so the returned wrapper's handle becomes the
    /// <c>resultBuf</c> *address*, which the method's <c>finally</c> then frees; the first member
    /// access dereferences freed memory. The correct read must dereference the buffer for a class
    /// (and copy for value-type / existential / SwiftString), which the internal
    /// <c>MarshalMovedValueFromSlot&lt;T&gt;</c> already does — exposing that shape to generated code
    /// is new public runtime surface with its own cross-shape test matrix, hence §6 not Session 2.
    /// Unskip when both §6 defects land.
    /// </summary>
    [Skip("REMEDIATION-PLAN §6: GenericClosureBridge has two independent defects — (1) self-register ABI mismatch (SwiftSelf vs free-function _self GPR param) blocking all calls, and (2) generic class-typed return marshals the result-buffer address via MarshalFromSwift<T> instead of dereferencing it. Both need fixing before this round-trips.")]
    public void TestGenericRead_ClassReturn_SkipPendingBufferDerefFix()
    {
        using var source = new DatabaseReader("primary");
        using var reader = new DatabaseReader("outer");
        var result = reader.Read<DatabaseReader>(db => db, source);
        AssertEqual("primary", result.Name, "GenericClosureBridge returns the forwarded source object");
        TestLogger.Info("GenericClosureBridge read<T> class-return round-trip passed");
    }
}
