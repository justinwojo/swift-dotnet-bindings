// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using RuntimeTestsApp.Infrastructure;
using Swift.Runtime;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.ErrorHandling;

/// <summary>
/// A synchronous plain <c>throws</c> member whose thrown error is a registered
/// Error-conforming type of the module must surface as <c>SwiftException&lt;TError&gt;</c>
/// carrying the marshalled payload — the same outcome the asynchronous plain-throws path
/// already produces — while an error from outside the registry keeps the untyped
/// <c>SwiftException</c> with only the Swift description.
///
/// The members exercised here deliberately span every route a sync <c>throws</c> member can
/// take, because each route reports the thrown error differently and the dispatch has to be
/// wired into all of them:
/// <list type="bullet">
///   <item>native thunk — error through an explicit out-pointer (<c>SyncCascadeDivide</c>,
///         and the two lifetime probes);</item>
///   <item><c>@_cdecl</c> wrapper — also an explicit out-pointer, but a different emitter
///         (<c>SyncCascadeParse</c>, <c>SyncCascadeLoadConfig</c>, <c>SyncCascadeScan</c>);</item>
///   <item>direct call — error in the dedicated Swift error register, reached by a
///         <c>get throws</c> computed property (<c>SyncCascadeGate.CheckedValue</c>).</item>
/// </list>
/// Every cascade payload shape is covered: simple enum (copied by value), complex enum and
/// non-frozen struct (carrier owned by a SafeHandle), Swift class (retained pointer), and a
/// frozen struct with a heap-typed field (carrier copied, so its value-witness retains have
/// to be destroyed before the buffer is freed).
/// </summary>
public class SyncErrorCascadeTests : TestBase
{
    public SyncErrorCascadeTests(TestResults results) : base(results) { }

    // ── Simple enum payload, native thunk route ─────────────────────────────────────────

    public void TestSimpleEnumErrorSurfacesTypedExceptionThroughThunkRoute()
    {
        AssertEqual(5, TestLibFunctions.SyncCascadeDivide(10, 2),
            "the non-throwing outcome must be unaffected");

        var byZero = CatchTyped<SyncCascadeSimpleError>(() => TestLibFunctions.SyncCascadeDivide(1, 0));
        AssertEqual(SyncCascadeSimpleError.DivisionByZero, byZero.Error,
            "the thrown enum case must survive marshalling");
        AssertTrue(byZero.ErrorHandle != IntPtr.Zero,
            "a typed sync throw must carry the live error box, matching the untyped sync throw");

        // A second case of the same type proves the payload is really read rather than a
        // default-constructed value that happens to match the first declared case.
        var negative = CatchTyped<SyncCascadeSimpleError>(() => TestLibFunctions.SyncCascadeDivide(-1, 2));
        AssertEqual(SyncCascadeSimpleError.NegativeInput, negative.Error,
            "a second case of the same error type must marshal distinctly");
    }

    // ── Complex enum payload, @_cdecl wrapper route ─────────────────────────────────────

    public void TestComplexEnumErrorSurfacesTypedExceptionThroughWrapperRoute()
    {
        AssertEqual(4, TestLibFunctions.SyncCascadeParse("abcd"),
            "the non-throwing outcome must be unaffected");

        var eof = CatchTyped<SyncCascadeComplexError>(() => TestLibFunctions.SyncCascadeParse(""));
        using (var payload = eof.Error)
        {
            AssertNotNull(payload, "complex-enum payload must be marshalled");
            AssertEqual(SyncCascadeComplexError.CaseTag.UnexpectedEOF, payload!.Tag,
                "the thrown case must survive marshalling");
            AssertTrue(payload.TryGetUnexpectedEOF(out var at), "associated value must be readable");
            AssertEqual(42, at, "the associated value must survive marshalling");
        }

        var malformed = CatchTyped<SyncCascadeComplexError>(() => TestLibFunctions.SyncCascadeParse("!x"));
        using (var payload = malformed.Error)
        {
            AssertNotNull(payload, "complex-enum payload must be marshalled");
            AssertEqual(SyncCascadeComplexError.CaseTag.Malformed, payload!.Tag,
                "a second case of the same error type must marshal distinctly");
            AssertTrue(payload.TryGetMalformed(out var reason), "associated value must be readable");
            AssertTrue(reason!.Contains("leading punctuation"),
                $"the associated String must survive marshalling, got: {reason}");
        }
    }

    // ── Non-frozen struct payload, @_cdecl wrapper route ────────────────────────────────

    public void TestStructErrorSurfacesTypedException()
    {
        AssertEqual(4, TestLibFunctions.SyncCascadeLoadConfig("/etc"),
            "the non-throwing outcome must be unaffected");

        var failure = CatchTyped<SyncCascadeStructError>(() => TestLibFunctions.SyncCascadeLoadConfig("/etc/bad"));
        using (var payload = failure.Error)
        {
            AssertNotNull(payload, "struct payload must be marshalled");
            AssertEqual("/etc/bad", payload!.Path, "String field must survive marshalling");
            AssertEqual(7, payload.LineNumber, "Int32 field must survive marshalling");
        }
    }

    // ── Class payload, @_cdecl wrapper route ────────────────────────────────────────────

    public void TestClassErrorSurfacesTypedException()
    {
        AssertEqual(2, TestLibFunctions.SyncCascadeScan("ok"),
            "the non-throwing outcome must be unaffected");

        var denied = CatchTyped<SyncCascadeClassError>(() => TestLibFunctions.SyncCascadeScan("denied"));
        using (var payload = denied.Error)
        {
            AssertNotNull(payload, "class payload must be marshalled");
            AssertEqual(403, payload!.Code, "Int32 field must survive marshalling");
            AssertEqual("scanning denied", payload.Detail, "String field must survive marshalling");
        }
    }

    // ── Frozen struct with a heap-typed field, @_cdecl wrapper route ────────────────────

    public void TestFrozenStructWithHeapFieldErrorSurfacesTypedException()
    {
        // This payload shape is projected as a class over a copy of the wire carrier, so the
        // carrier keeps value-witness retains on the heap-typed field that the dispatch has to
        // destroy before freeing it. Reading both fields back proves the copy reached the caller
        // whole rather than being taken from a buffer released without that destroy. The async
        // twin covers the same error type; this is the synchronous side of it.
        AssertEqual(2, TestLibFunctions.SyncCascadeOpenResource("ok"),
            "the non-throwing outcome must be unaffected");

        var denied = CatchTyped<PlainThrowsFrozenWithMemoryError>(
            () => TestLibFunctions.SyncCascadeOpenResource("denied"));
        using (var payload = denied.Error)
        {
            AssertNotNull(payload, "frozen-struct payload must be marshalled");
            AssertEqual("secrets.json", payload!.ResourceName,
                "the heap-typed field must survive marshalling");
            AssertEqual(13, payload.Attempts, "Int32 field must survive marshalling");
        }

        // A second throw with a different payload shows the fields are really read from the
        // carrier each time rather than recovered from a value that outlived the first destroy.
        var missing = CatchTyped<PlainThrowsFrozenWithMemoryError>(
            () => TestLibFunctions.SyncCascadeOpenResource(""));
        using (var payload = missing.Error)
        {
            AssertNotNull(payload, "frozen-struct payload must be marshalled");
            AssertEqual("config.plist", payload!.ResourceName,
                "a second throw of the same error type must marshal distinctly");
            AssertEqual(7, payload.Attempts, "a second throw of the same error type must marshal distinctly");
        }
    }

    // ── Instance members ────────────────────────────────────────────────────────────────

    public void TestInstanceMembersDispatchTypedErrors()
    {
        using var worker = new SyncCascadeWorker(12);
        AssertEqual(4, worker.Divide(3), "the non-throwing outcome must be unaffected");

        var divide = CatchTyped<SyncCascadeSimpleError>(() => worker.Divide(0));
        AssertEqual(SyncCascadeSimpleError.DivisionByZero, divide.Error,
            "a struct instance method must dispatch typed errors too");

        var parse = CatchTyped<SyncCascadeComplexError>(() => worker.Parse(""));
        using (var payload = parse.Error)
        {
            AssertNotNull(payload, "complex-enum payload must be marshalled");
            AssertTrue(payload!.TryGetUnexpectedEOF(out var at), "associated value must be readable");
            AssertEqual(12, at, "the associated value must come from the receiver's state");
        }

        using var service = new SyncCascadeService();
        AssertEqual(3, service.Load("abc"), "the non-throwing outcome must be unaffected");
        var load = CatchTyped<SyncCascadeClassError>(() => service.Load(""));
        using (var payload = load.Error)
        {
            AssertNotNull(payload, "class payload must be marshalled");
            AssertEqual(404, payload!.Code, "a class instance method must dispatch typed errors too");
        }
    }

    // ── Direct route: the error arrives in the Swift error register ─────────────────────

    public void TestThrowingGetterDispatchesTypedErrorThroughErrorRegisterRoute()
    {
        using (var open = new SyncCascadeGate(true))
        {
            AssertEqual(7, open.CheckedValue, "the non-throwing outcome must be unaffected");
        }

        using var closed = new SyncCascadeGate(false);
        var blocked = CatchTyped<SyncCascadeSimpleError>(() => _ = closed.CheckedValue);
        AssertEqual(SyncCascadeSimpleError.NegativeInput, blocked.Error,
            "the register-reported error must dispatch through the same registry");
        AssertTrue(blocked.ErrorHandle != IntPtr.Zero,
            "the register route must carry the live error box too");
    }

    // ── Registry miss keeps the untyped exception ───────────────────────────────────────

    public void TestUnregisteredErrorKeepsUntypedException()
    {
        try
        {
            TestLibFunctions.GetSyncCascadeThrowUnregistered();
            throw new AssertionException("GetSyncCascadeThrowUnregistered should have thrown");
        }
        catch (SwiftException ex)
        {
            // The exact-type check is the assertion that matters: SwiftException<TError>
            // derives from SwiftException, so a `catch (SwiftException)` alone would pass
            // even if the classifier wrongly matched a registry arm.
            AssertTrue(ex.GetType() == typeof(SwiftException),
                $"an error outside the module registry must stay untyped, got {ex.GetType().Name}");
            AssertTrue(ex.Message.Contains("sync-fallthrough-sentinel-8888")
                    || ex.Message.Contains("SyncCascadeUnregisteredDomain"),
                $"the untyped fallback must keep the Swift description, got: {ex.Message}");
            AssertTrue(ex.ErrorHandle != IntPtr.Zero,
                "the untyped fallback must keep carrying the live error box");
        }
    }

    // ── Release-exactly-once probes ─────────────────────────────────────────────────────

    public void TestClassErrorBoxIsReleasedExactlyOnce()
    {
        // The thrown class error embeds a registry-tracked ref, so both a lost release (the
        // ref stays live forever) and an over-release (the Swift runtime traps) are observable
        // rather than merely "did not crash". Two independent +1s exist on this path — the
        // error box owned by the exception, and the retained class pointer owned by the
        // marshalled payload — so this also gates them not collapsing into a double free.
        LifetimeTracker.RunWithLeakCheck(() =>
        {
            for (int i = 0; i < 25; i++)
                ThrowAndDropTrackedClassError(i);
            DisplaceResidualCarrierReferences();
        }, "sync typed throw, class-shaped error");
    }

    public void TestStructErrorCarrierIsReleasedExactlyOnce()
    {
        // Same probe for the SafeHandle-owned carrier shape: the wire buffer holds a +1 on the
        // embedded ref, which only balances if the carrier is released exactly once.
        LifetimeTracker.RunWithLeakCheck(() =>
        {
            for (int i = 0; i < 25; i++)
                ThrowAndDropTrackedStructError(i);
            DisplaceResidualCarrierReferences();
        }, "sync typed throw, struct-shaped error");
    }

    // The collector scans this thread conservatively, so a reference to the churn's final
    // exception can survive in a spilled slot or a callee-saved register of the frames it ran
    // in — and that exception owns the error box, which owns the tracked ref, so the box would
    // still be alive when the assertion drains. Re-running both carrier shapes with errors that
    // embed nothing tracked overwrites those slots with untracked references. This only ever
    // drops references and allocates nothing tracked, so an unbalanced retain on the tracked
    // path still fails the assertion.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void DisplaceResidualCarrierReferences()
    {
        for (int i = 0; i < 4; i++)
        {
            var buffered = CatchTyped<SyncCascadeStructError>(
                () => TestLibFunctions.SyncCascadeLoadConfig("/etc/bad"));
            using (var bufferedPayload = buffered.Error)
                AssertNotNull(bufferedPayload, "the displacing throw must still marshal its payload");

            var retained = CatchTyped<SyncCascadeClassError>(
                () => TestLibFunctions.SyncCascadeScan("denied"));
            using (var retainedPayload = retained.Error)
                AssertNotNull(retainedPayload, "the displacing throw must still marshal its payload");
        }
    }

    // Kept out of the probe frame so neither the exception nor the payload is still rooted by
    // a live local when the leak assertion drains.
    private void ThrowAndDropTrackedClassError(int code)
    {
        var caught = CatchTyped<SyncCascadeTrackedClassError>(
            () => TestLibFunctions.SyncCascadeThrowTrackedClassError(code));
        using var payload = caught.Error;
        AssertNotNull(payload, "class payload must be marshalled");
        AssertEqual(code, payload!.Code, "the payload must carry the thrown value");
    }

    private void ThrowAndDropTrackedStructError(int code)
    {
        var caught = CatchTyped<SyncCascadeTrackedStructError>(
            () => TestLibFunctions.SyncCascadeThrowTrackedStructError(code));
        using var payload = caught.Error;
        AssertNotNull(payload, "struct payload must be marshalled");
        AssertEqual(code, payload!.Code, "the payload must carry the thrown value");
    }

    // Runs the action, requires it to throw, and requires the thrown exception to be the typed
    // shape for TError. Catching plain SwiftException and then type-testing gives a failure
    // message that names what actually arrived — which is the whole distinction under test.
    private SwiftException<TError> CatchTyped<TError>(Action action)
    {
        try
        {
            action();
        }
        catch (SwiftException<TError> typed)
        {
            return typed;
        }
        catch (SwiftException untyped)
        {
            throw new AssertionException(
                $"Expected SwiftException<{typeof(TError).Name}> but got untyped SwiftException: {untyped.Message}");
        }
        throw new AssertionException($"Expected SwiftException<{typeof(TError).Name}> but nothing was thrown");
    }
}
