// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#if SWIFTUI_BRIDGE

using RuntimeTestsApp.Infrastructure;

namespace RuntimeTestsApp.SwiftUIBridge;

/// <summary>
/// Runtime gate for the async-bridge result payload channel: the result callback carries the
/// value its Swift result case was constructed with, not just an outcome code. Both ownership
/// shapes are covered — a class payload (Swift hands over a retained object pointer) and a
/// resilient struct payload (Swift lends a value-witness-initialized carrier the managed side
/// copies out of). Each shape is exercised twice so the declared Bool default is observable:
/// omitted → Swift sees <c>false</c>, passed → Swift sees what was passed.
///
/// The payload handed to the typed callback is the callback's to release; every test here
/// disposes it and then asserts the shared allocation counters returned to zero, so a missing
/// release on either side of the ABI fails rather than merely passing quietly.
/// </summary>
public class AsyncResultPayloadBridgeTests : TestBase
{
    public AsyncResultPayloadBridgeTests(TestResults results) : base(results) { }

    // ────────────────────────────────────────────────────────────────
    // Class payload — Unmanaged.passRetained → MarshalFromSwiftObject
    // ────────────────────────────────────────────────────────────────

    public async Task TestClassPayload_DefaultBoolObservedAsFalse()
    {
        LifetimeTracker.Reset();

        var received = new PayloadCapture();
        // preferFastPath is omitted: the pattern declares its default, and the Swift side
        // echoes whichever value it actually received into the payload's code.
        using (var session = await WithTimeout(
            global::SwiftBindingsTestLib.AsyncClassPayloadResultViewSession.CreateAsync(
                "class-default",
                onResult: c => received.RecordScalar(c),
                onResultPayload: (c, p) => CaptureClassPayload(received, c, p)),
            DefaultAsyncTimeout))
        {
            AssertTrue(session.GetViewController() != IntPtr.Zero, "class-payload session GetVC != 0");
            await WithTimeout(received.Completion, DefaultAsyncTimeout);
        }

        AssertEqual(0, received.PayloadCode, "omitted preferFastPath reached Swift as false");
        AssertEqual("class-default", received.Text, "class payload label round-tripped");
        AssertEqual(0, received.ResultCode, "completed case delivered result code 0");
        AssertEqual(1, received.ScalarCalls, "scalar onResult channel still fired exactly once");
        AssertEqual(0, received.ScalarCode, "scalar onResult carried the same code");

        LifetimeTracker.AssertNoLeaks("class payload: one retain in, one release out");
        TestLogger.Info("AsyncClassPayloadResultView: default Bool + class payload round-tripped");
    }

    public async Task TestClassPayload_ExplicitBoolObservedAsTrue()
    {
        LifetimeTracker.Reset();

        var received = new PayloadCapture();
        using (var session = await WithTimeout(
            global::SwiftBindingsTestLib.AsyncClassPayloadResultViewSession.CreateAsync(
                "class-explicit",
                preferFastPath: true,
                onResultPayload: (c, p) => CaptureClassPayload(received, c, p)),
            DefaultAsyncTimeout))
        {
            await WithTimeout(received.Completion, DefaultAsyncTimeout);
        }

        AssertEqual(1, received.PayloadCode, "explicit preferFastPath: true reached Swift as true");
        AssertEqual("class-explicit", received.Text, "class payload label round-tripped");

        LifetimeTracker.AssertNoLeaks("class payload (explicit flag): no retain stranded");
        TestLogger.Info("AsyncClassPayloadResultView: explicit Bool overrode the default");
    }

    public async Task TestClassPayload_DisposedSessionRejectsUse()
    {
        var received = new PayloadCapture();
        var session = await WithTimeout(
            global::SwiftBindingsTestLib.AsyncClassPayloadResultViewSession.CreateAsync(
                "class-dispose",
                onResultPayload: (c, p) => CaptureClassPayload(received, c, p)),
            DefaultAsyncTimeout);

        await WithTimeout(received.Completion, DefaultAsyncTimeout);
        session.Dispose();

        AssertThrows<ObjectDisposedException>(
            () => session.GetViewController(),
            "disposed session rejects GetViewController");
        // Double dispose is a no-op, not a double-free of the Swift session.
        session.Dispose();
        TestLogger.Info("AsyncClassPayloadResultView: disposal cycle passed");
    }

    // ────────────────────────────────────────────────────────────────
    // Struct payload — Swift-owned carrier → InitializeWithCopy into managed storage
    // ────────────────────────────────────────────────────────────────

    public async Task TestStructPayload_DefaultBoolObservedAsFalse()
    {
        LifetimeTracker.Reset();

        var received = new PayloadCapture();
        using (var session = await WithTimeout(
            global::SwiftBindingsTestLib.AsyncStructPayloadResultViewSession.CreateAsync(
                "struct-default",
                onResult: c => received.RecordScalar(c),
                onResultPayload: (c, p) => CaptureStructPayload(received, c, p)),
            DefaultAsyncTimeout))
        {
            AssertTrue(session.GetViewController() != IntPtr.Zero, "struct-payload session GetVC != 0");
            await WithTimeout(received.Completion, DefaultAsyncTimeout);
        }

        AssertEqual(0, received.PayloadCode, "omitted preferFastPath reached Swift as false");
        AssertEqual("struct-default", received.Text, "struct payload name round-tripped");
        AssertEqual(0, received.ResultCode, "completed case delivered result code 0");
        AssertEqual(1, received.ScalarCalls, "scalar onResult channel still fired exactly once");

        // The struct carries a counted reference, so a copy that was never destroyed — on either
        // side — keeps the live count above zero here.
        LifetimeTracker.AssertNoLeaks("struct payload: every copy of the carrier destroyed once");
        TestLogger.Info("AsyncStructPayloadResultView: default Bool + struct payload round-tripped");
    }

    public async Task TestStructPayload_ExplicitBoolObservedAsTrue()
    {
        LifetimeTracker.Reset();

        var received = new PayloadCapture();
        using (var session = await WithTimeout(
            global::SwiftBindingsTestLib.AsyncStructPayloadResultViewSession.CreateAsync(
                "struct-explicit",
                preferFastPath: true,
                onResultPayload: (c, p) => CaptureStructPayload(received, c, p)),
            DefaultAsyncTimeout))
        {
            await WithTimeout(received.Completion, DefaultAsyncTimeout);
        }

        AssertEqual(1, received.PayloadCode, "explicit preferFastPath: true reached Swift as true");
        AssertEqual("struct-explicit", received.Text, "struct payload name round-tripped");

        LifetimeTracker.AssertNoLeaks("struct payload (explicit flag): no copy stranded");
        TestLogger.Info("AsyncStructPayloadResultView: explicit Bool overrode the default");
    }

    public async Task TestStructPayload_RepeatedSessionsDoNotAccumulate()
    {
        LifetimeTracker.Reset();

        for (int i = 0; i < 8; i++)
        {
            var received = new PayloadCapture();
            using var session = await WithTimeout(
                global::SwiftBindingsTestLib.AsyncStructPayloadResultViewSession.CreateAsync(
                    $"struct-loop-{i}",
                    onResultPayload: (c, p) => CaptureStructPayload(received, c, p)),
                DefaultAsyncTimeout);
            await WithTimeout(received.Completion, DefaultAsyncTimeout);
            AssertEqual($"struct-loop-{i}", received.Text, "loop iteration payload name round-tripped");
        }

        // One stranded copy per iteration is the shape a mismatched allocate/deallocate pairing
        // takes; a single round would hide it behind drain latency, eight rounds will not.
        LifetimeTracker.AssertNoLeaks("struct payload: 8 sessions left nothing behind");
        TestLogger.Info("AsyncStructPayloadResultView: repeated payload delivery stayed balanced");
    }

    // ────────────────────────────────────────────────────────────────
    // Capture helpers
    // ────────────────────────────────────────────────────────────────

    // The typed callback OWNS the payload it is handed — reading it and then disposing it here
    // is the contract both ownership shapes are built around.
    private static void CaptureClassPayload(
        PayloadCapture capture, int code, global::SwiftBindingsTestLib.AsyncResultClassPayload? payload)
    {
        using (payload)
        {
            capture.RecordPayload(code, payload?.Code ?? int.MinValue, payload?.Label);
        }
    }

    private static void CaptureStructPayload(
        PayloadCapture capture, int code, global::SwiftBindingsTestLib.AsyncResultStructPayload? payload)
    {
        using (payload)
        {
            capture.RecordPayload(code, payload?.Count ?? int.MinValue, payload?.Name);
        }
    }

    private sealed class PayloadCapture
    {
        private readonly TaskCompletionSource<bool> _tcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Completion => _tcs.Task;
        public int ResultCode { get; private set; } = int.MinValue;
        public int PayloadCode { get; private set; } = int.MinValue;
        public string? Text { get; private set; }
        public int ScalarCode { get; private set; } = int.MinValue;
        public int ScalarCalls { get; private set; }

        public void RecordScalar(int code)
        {
            ScalarCode = code;
            ScalarCalls++;
        }

        public void RecordPayload(int resultCode, int payloadCode, string? text)
        {
            ResultCode = resultCode;
            PayloadCode = payloadCode;
            Text = text;
            _tcs.TrySetResult(true);
        }
    }
}

#endif
