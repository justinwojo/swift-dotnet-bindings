// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift.Runtime;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.FoundationInterop;

/// <summary>
/// Foundation KVO observe(_:options:changeHandler:) round-trip through the
/// SBW_KVO_<Class>_observe<Prop> @_cdecl shim and the C# extension method
/// dispatcher. Prototype validates the ABI shape that the
/// KvoExtensionEmitter will codegen.
/// </summary>
public class FoundationKvoTests : TestBase
{
    public FoundationKvoTests(TestResults results) : base(results) { }

    public void TestObserveCounter_FiresOnInitial()
    {
        using var obj = TestLibFunctions.MakeTestNSObservable();
        var observed = new List<nint>();
        using var token = obj.ObserveCounter(
            SbwKvoOptions.Initial | SbwKvoOptions.New,
            (_, v) => observed.Add(v));

        AssertEqual(1, observed.Count, "Initial fires once on subscribe");
        AssertEqual((nint)0, observed[0], "Initial fire reports current value (0)");
    }

    public void TestObserveCounter_FiresOnMutate()
    {
        using var obj = TestLibFunctions.MakeTestNSObservable();
        var observed = new List<nint>();
        using var token = obj.ObserveCounter(
            SbwKvoOptions.New,
            (_, v) => observed.Add(v));

        TestLibFunctions.MutateCounter(obj, 42);
        TestLibFunctions.MutateCounter(obj, 99);

        AssertEqual(2, observed.Count, "Two mutations produce two callbacks");
        AssertEqual((nint)42, observed[0], "First callback sees 42");
        AssertEqual((nint)99, observed[1], "Second callback sees 99");
    }

    public void TestObserveCounter_DisposeStopsCallbacks()
    {
        using var obj = TestLibFunctions.MakeTestNSObservable();
        var observed = new List<nint>();
        var token = obj.ObserveCounter(
            SbwKvoOptions.New,
            (_, v) => observed.Add(v));

        TestLibFunctions.MutateCounter(obj, 7);
        token.Dispose();
        TestLibFunctions.MutateCounter(obj, 8);

        AssertEqual(1, observed.Count, "Post-dispose mutation does NOT fire callback");
        AssertEqual((nint)7, observed[0], "Pre-dispose callback fired with 7");
    }

    public void TestObserveCounter_ReceiverIdentity()
    {
        using var obj = TestLibFunctions.MakeTestNSObservable();
        TestNSObservable? receivedObj = null;
        using var token = obj.ObserveCounter(
            SbwKvoOptions.New,
            (root, _) => receivedObj = root);

        TestLibFunctions.MutateCounter(obj, 1);

        AssertTrue(receivedObj is not null, "Callback received non-null receiver");
        AssertEqual(
            ((ISwiftObject)obj).SwiftHandle,
            ((ISwiftObject)receivedObj!).SwiftHandle,
            "Callback receiver wraps same native handle as observed obj");
    }

    // The Bool ABI shape (single-byte C# bool over the @convention(c) callback)
    // is the trickiest of the v1 primitive whitelist — non-trivial register vs
    // single-byte zero/non-zero conversion at the unmanaged boundary. Validate
    // round-trip independently from the nint path above.
    public void TestObserveBool_RoundTripsTrueThenFalse()
    {
        using var obj = TestLibFunctions.MakeTestNSObservable();
        var observed = new List<bool>();
        using var token = obj.ObserveEnabled(
            SbwKvoOptions.New,
            (_, v) => observed.Add(v));

        TestLibFunctions.MutateEnabled(obj, true);
        TestLibFunctions.MutateEnabled(obj, false);
        TestLibFunctions.MutateEnabled(obj, true);

        AssertEqual(3, observed.Count, "Three mutations produce three Bool callbacks");
        AssertTrue(observed[0], "First mutation observed as true");
        AssertTrue(!observed[1], "Second mutation observed as false");
        AssertTrue(observed[2], "Third mutation observed as true");
    }

    /// <summary>
    /// A KVO change handler is a non-throwing UnmanagedCallersOnly callback with no error
    /// channel (Finding 38). An unhandled managed exception escaping it must trigger
    /// <c>Environment.FailFast</c> — loud and attributable — rather than the previous
    /// print-and-continue, which silently swallowed a consumer bug and kept observing.
    /// Asserting an actual FailFast requires running the offending code in a subprocess so the
    /// host survives; the in-process iOS runtime-test runner cannot do that (mirrors
    /// <c>AsyncClosureTests.TestAsyncClosure_UnhandledException_FailsFast</c>). The emission is
    /// pinned at the unit layer by
    /// <c>UcoGuardEmitterTests.KvoDispatchTrampoline_FailFastsOnEscapingException_NotSwallowed</c>;
    /// tracked here so coverage shows the runtime behaviour is intentionally policy-only.
    /// </summary>
    [Skip("FailFast on an unhandled managed exception in a KVO change handler requires a subprocess harness; not feasible from the in-process iOS test runner.")]
    public void TestObserveCounter_HandlerThrows_FailsFast()
    {
    }
}
