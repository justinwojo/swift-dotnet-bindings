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

    // ─── Swift.String as MCB non-closure param ──────

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

    // ─── Optional MCB closure ───────────

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
    /// return 0 from Swift, no crash. Optional MCB closure with a nil callback.
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
    // The synthetic-name guard on the GenericClosureBridge path is proven at the emitter
    // layer by GenericClosureBridgeEmitterTests.TryEmit_UserParamNamedCdecl_* /
    // TryEmit_UserParamNamedUnderscoreSelf_* (assert `cdecl`→`__cdecl` and `_self`→`___self`
    // transitive renames in the generated Swift) plus the compile gate (the renamed wrappers
    // compile — a collision would be an "invalid redeclaration" → stripped symbol). The four
    // round-trip tests below are the *runtime* proof for the two GenericClosureBridge defect fixes:
    //   (1) Self-register ABI: the Swift `@_silgen_name` wrapper is a *free function* that takes the
    //       receiver as an ordinary trailing parameter (a regular GPR), but the C# P/Invoke declared
    //       it `SwiftSelf self_` (pinned to the self register x20 under CallConvSwift) — the wrapper
    //       read garbage from a GPR the C# side never set. Fixed by emitting the receiver as a plain
    //       `IntPtr __self`, which lands in the regular-GPR slot the wrapper expects (the `$s…` entry
    //       point keeps the call on CallConvSwift, so a throwing method's `out SwiftError` still maps
    //       to the error register x21).
    //   (2) Class-typed return: the result is written into a caller-owned `resultBuf` via
    //       `MarshalToSwift` (an `InitializeWithCopy` that stores the object pointer *inside* the
    //       buffer at +1), then read back. The old read passed the buffer *address* to
    //       `MarshalFromSwift<T>`, so for a class `T` the wrapper's handle became the buffer address,
    //       which the `finally` then freed → use-after-free on first member access. Fixed by reading
    //       via `MarshalMovedValueFromSlot<T>`, which dereferences the slot for a true class, reads
    //       bytes for POD, and copy-then-Destroys for non-POD structs — transferring the slot's +1.

    /// <summary>
    /// Baseline for the GenericClosureBridge void path (method-generic, noescape, throwing closure
    /// with a non-closure class param). Identity-forwards the param into the closure; its Name must
    /// round-trip. Exercises the self-register ABI fix (receiver passed as a regular-GPR IntPtr).
    /// </summary>
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
    /// User param `cdecl` collides with the GenericClosureBridge synthetic func-ptr
    /// local (`let cdecl = unsafeBitCast(...)`), which the guard renames to `__cdecl` (asserted at
    /// the emitter layer in GenericClosureBridgeEmitterTests). This is the runtime proof: the
    /// renamed wrapper round-trips the user `cdecl` param through the closure.
    /// </summary>
    public void TestGenericCdeclParamCollision_RoundTrips()
    {
        using var source = new DatabaseReader("primary");
        using var reader = new DatabaseReader("outer");
        string? captured = null;
        reader.ReadWithCdecl(db => captured = db.Name, source);
        AssertEqual("primary", captured, "user param `cdecl` reaches the closure despite colliding with the synthetic func-ptr local");
        TestLogger.Info("generic cdecl collision (void) passed");
    }

    /// <summary>
    /// User param `_self` collides with the GenericClosureBridge synthetic self-pointer
    /// param, which the guard renames transitively to `___self` (asserted at the emitter layer in
    /// GenericClosureBridgeEmitterTests). This is the runtime proof: the renamed wrapper round-trips
    /// the user `_self` param through the closure — and the receiver (now a regular-GPR `__self`)
    /// coexists with the user param `_self` without collision.
    /// </summary>
    public void TestGenericSelfParamCollision_RoundTrips()
    {
        using var source = new DatabaseReader("primary");
        using var reader = new DatabaseReader("outer");
        string? captured = null;
        reader.ReadWithSelf(db => captured = db.Name, source);
        AssertEqual("primary", captured, "user param `_self` reaches the closure despite colliding with the synthetic self-pointer param");
        TestLogger.Info("generic _self collision (void) passed");
    }

    /// <summary>
    /// The GenericClosureBridge generic-RETURN overload (<c>Read&lt;T&gt;(Func&lt;…,T&gt;, …)</c>)
    /// with a class-typed <c>T</c>. The closure result is written into a caller-owned
    /// <c>resultBuf</c> via <c>MarshalToSwift</c> (an <c>InitializeWithCopy</c> that stores the
    /// object pointer *inside* the buffer at +1); the read-back goes through
    /// <c>MarshalMovedValueFromSlot&lt;T&gt;</c>, which dereferences the slot for a true class (the
    /// buffer *contains* the object pointer, it is not itself the instance) and transfers the +1 to
    /// the returned wrapper. Regression guard for the class-return buffer-deref fix: the old
    /// <c>MarshalFromSwift&lt;T&gt;(new IntPtr(resultBuf))</c> handed the buffer *address* to
    /// <c>NewFromPayload</c>, so the wrapper's handle became the buffer address — freed by the
    /// method's <c>finally</c> → use-after-free on the first member access.
    /// </summary>
    public void TestGenericRead_ClassReturn_RoundTrips()
    {
        using var source = new DatabaseReader("primary");
        using var reader = new DatabaseReader("outer");
        var result = reader.Read<DatabaseReader>(db => db, source);
        AssertEqual("primary", result.Name, "GenericClosureBridge returns the forwarded source object");
        TestLogger.Info("GenericClosureBridge read<T> class-return round-trip passed");
    }

    // ─── GenericClosureBridge gate (c): generic type parameter in closure ARGUMENT position ───
    //
    // `apply<T>(_ value: T, _ transform: (T) throws -> T) rethrows -> T` puts the method's own generic
    // parameter in closure-input position. Historically gated out: the C# [UnmanagedCallersOnly]
    // callback declared one void* only per CONCRETE arg, so the Swift cdecl callback (one void* per
    // arg, generic included) passed more void* than C# expected — an ABI mismatch. The fix counts ALL
    // closure args in the callback and passes the generic argument as a value-witness buffer pointer:
    // C# allocates a T-sized buffer, marshals the value (+1) in, and the Swift wrapper forwards that
    // pointer to the closure, which the callback reads back to T with a borrowed +1
    // (MarshalBorrowedValueFromSlot). These are the runtime proofs that the value round-trips IN
    // through the buffer, the C# closure transforms it, and the result flows back OUT through resultBuf.

    /// <summary>
    /// The generic value is handed to the C# closure, transformed, and the new value flows back as the
    /// method result. Proves the input buffer carries the real <c>T</c> (not garbage) and the result
    /// slot carries the transformed object: <c>21 → ×2 → 42</c>.
    /// </summary>
    public void TestGenericArgInput_Apply_TransformsValue()
    {
        using var fixture = new GenericArgClosureFixture();
        using var input = new LevelKnob(21);
        using var result = fixture.Apply<LevelKnob>(k => new LevelKnob(k.Level * 2), input);
        AssertEqual(42, result.Level, "generic closure-arg value round-tripped IN and the transform flowed OUT");
        TestLogger.Info("GenericClosureBridge gate (c) apply transform round-trip passed");
    }

    /// <summary>
    /// Identity-forward through a generic argument: the borrowed read hands the SAME object to the
    /// closure, which returns it unchanged. The forwarded value must be observable, not corrupted.
    /// </summary>
    public void TestGenericArgInput_Apply_Identity()
    {
        using var fixture = new GenericArgClosureFixture();
        using var input = new LevelKnob(7);
        using var result = fixture.Apply<LevelKnob>(k => k, input);
        AssertEqual(7, result.Level, "identity-forwarded generic argument preserves the value");
        TestLogger.Info("GenericClosureBridge gate (c) apply identity round-trip passed");
    }

    /// <summary>
    /// Two generic arguments in input position (<c>(T, T) throws -> T</c>) — exercises the multi-arg
    /// void* count in the callback: each generic argument becomes its own buffer pointer / void*.
    /// </summary>
    public void TestGenericArgInput_Combine_TwoArgs()
    {
        using var fixture = new GenericArgClosureFixture();
        using var a = new LevelKnob(20);
        using var b = new LevelKnob(22);
        using var result = fixture.Combine<LevelKnob>((x, y) => new LevelKnob(x.Level + y.Level), a, b);
        AssertEqual(42, result.Level, "both generic arguments reached the closure in order and merged");
        TestLogger.Info("GenericClosureBridge gate (c) combine two-arg round-trip passed");
    }

    /// <summary>
    /// The generic argument is a <b>Move</b>-semantics type (<c>SwiftString</c>): the borrowed read in
    /// the callback must take an INDEPENDENT <c>+1</c> on the heap-backed storage, not bitwise-transfer
    /// the value-buffer's only reference. A large (&gt;15-byte) string forces the heap-allocated,
    /// reference-counted storage path (small strings are inline/POD and carry no reference): the buffer
    /// holds a <c>+1</c> on <c>_StringStorage</c>, the borrowed wrapper handed to the closure must own a
    /// separate <c>+1</c>, and the method's <c>finally</c> Destroys the buffer's reference. Reading the
    /// borrowed string back to a correct value — and surviving a forced finalizer drain afterwards —
    /// proves the borrowed read did not alias-then-double-release the storage (over-release / UAF).
    /// </summary>
    public void TestGenericArgInput_Apply_StringMovePath_RoundTrips()
    {
        using var fixture = new GenericArgClosureFixture();
        const string large = "this string is definitely longer than fifteen bytes";
        using var input = (SwiftString)large;
        using var result = fixture.Apply<SwiftString>(
            s => (SwiftString)s.ToString().ToUpperInvariant(), input);
        AssertEqual(large.ToUpperInvariant(), result.ToString(),
            "Move-semantics generic argument round-tripped through a borrowed +1 read");

        // Surface any over-release of the borrowed string's heap storage: a bitwise-transfer borrow
        // would leave the wrapper and the value-buffer sharing one reference, and the finally Destroy
        // plus the wrapper's finalizer would double-free it.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        TestLogger.Info("GenericClosureBridge gate (c) SwiftString Move-path round-trip passed");
    }

    /// <summary>
    /// The closure is throwing (<c>(T) throws -> T</c>) and the method is <c>rethrows</c>: a C# closure
    /// that throws must surface back to the caller (the callback mints a Swift error, Swift rethrows,
    /// C# re-raises). The value-buffer must still be released on the error path (covered for leaks by
    /// <see cref="GenericClosureBridgeLeakTests"/>).
    /// </summary>
    public void TestGenericArgInput_ThrowingClosure_Surfaces()
    {
        using var fixture = new GenericArgClosureFixture();
        using var input = new LevelKnob(1);
        bool threw = false;
        try
        {
            fixture.Apply<LevelKnob>(k => throw new InvalidOperationException("boom"), input);
        }
        catch (Exception)
        {
            threw = true;
        }
        AssertTrue(threw, "a throwing C# closure surfaces back through the rethrows generic bridge");
        TestLogger.Info("GenericClosureBridge gate (c) throwing-closure surface passed");
    }
}
