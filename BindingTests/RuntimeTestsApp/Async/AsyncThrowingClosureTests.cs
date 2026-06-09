// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;
using Swift.Runtime;

namespace RuntimeTestsApp.Async;

/// <summary>
/// Generator-emitted baseline async-closure bridge.
/// Matches the hand-written spike shape in <c>AsyncClosureSpikeTests</c>,
/// but exercises the wrapper produced by the emitter for
/// <c>callAsyncThrowingClosure(_ closure: @escaping () async throws -&gt; Int32) async throws -&gt; Int32</c>.
/// </summary>
public class AsyncThrowingClosureTests : TestBase
{
    public AsyncThrowingClosureTests(TestResults results) : base(results) { }

    public async Task TestBaselineAsyncClosureReturns42()
    {
        Func<Task<int>> userLambda = () => Task.FromResult(42);
        var result = await WithTimeout(
            Functions.CallAsyncThrowingClosureAsync(userLambda),
            DefaultAsyncTimeout);
        AssertEqual(42, result, "Baseline async-throwing closure should return 42");
        TestLogger.Info($"AsyncThrowingClosure.HappyPath = {result}");
    }

    public async Task TestBaselineAsyncClosurePropagatesError()
    {
        Func<Task<int>> userLambda = async () =>
        {
            await Task.Yield();
            throw new InvalidOperationException("baseline-boom");
        };

        try
        {
            await WithTimeout(
                Functions.CallAsyncThrowingClosureAsync(userLambda),
                DefaultAsyncTimeout);
            throw new AssertionException("Expected SwiftException but no exception was thrown");
        }
        catch (SwiftException ex)
        {
            if (!ex.Message.Contains("baseline-boom"))
                throw new AssertionException($"Expected 'baseline-boom' in message, got: {ex.Message}");
            TestLogger.Info($"AsyncThrowingClosure.ErrorPath threw SwiftException: {ex.Message}");
        }
    }

    /// <summary>
    /// Invoke the baseline bridge repeatedly to validate GCHandle lifetime.
    /// The context handle is owned by the Swift-side <c>_SBClosureCtx</c> box
    /// and freed when Swift releases the adapter; this test guards the
    /// happy path — a premature free or wrong-handle-target would surface as a
    /// crash / wrong result on the Nth call. The collectible-after-call
    /// assertion lives in <c>AsyncClosureContextLifetimeTests</c> (device-only).
    /// </summary>
    public async Task TestBaselineAsyncClosureMultipleInvocations()
    {
        const int iterations = 16;
        var sum = 0;
        for (int i = 0; i < iterations; i++)
        {
            int local = i;
            Func<Task<int>> userLambda = () => Task.FromResult(local * 2);
            var result = await WithTimeout(
                Functions.CallAsyncThrowingClosureAsync(userLambda),
                DefaultAsyncTimeout);
            AssertEqual(local * 2, result, $"Iteration {local} should return {local * 2}");
            sum += result;
        }
        AssertEqual((iterations - 1) * iterations, sum,
            "Sum of 2*i for i in [0..iterations) should match arithmetic series");
        TestLogger.Info($"AsyncThrowingClosure.MultiInvoke sum={sum}");
    }

    /// <summary>
    /// Invokes the SAME closure value twice within a single outer Swift call.
    /// The per-iteration loop above only exercises single-invoke adapter
    /// lifetime; this covers the case where the adapter must build a fresh
    /// <c>CheckedContinuation</c> and continuation box on each await, rather
    /// than reusing stale per-invocation state.
    /// </summary>
    public async Task TestBaselineAsyncClosureSameClosureInvokedTwice()
    {
        int callCount = 0;
        Func<Task<int>> userLambda = () =>
        {
            int n = System.Threading.Interlocked.Increment(ref callCount);
            return Task.FromResult(n);
        };

        var result = await WithTimeout(
            Functions.CallAsyncThrowingClosureTwiceAsync(userLambda),
            DefaultAsyncTimeout);

        AssertEqual(2, callCount, "Closure should have been invoked exactly twice");
        AssertEqual(3, result, "Sum of 1 + 2 should be 3");
        TestLogger.Info($"AsyncThrowingClosure.SameClosureTwice sum={result} callCount={callCount}");
    }

    // MARK: - Arg-bearing async-throwing closures

    /// <summary>
    /// Arity-1 primitive arg: the C# closure receives the Int32 sent in from Swift
    /// and returns a value derived from it. Validates the Start-thunk marshals the
    /// primitive arg synchronously before Task.Run captures it.
    /// </summary>
    public async Task TestOneArgPrimitiveClosureReceivesValue()
    {
        Func<int, Task<int>> userLambda = v => Task.FromResult(v * 3);
        var result = await WithTimeout(
            Functions.CallAsyncThrowingClosureOneArgAsync(14, userLambda),
            DefaultAsyncTimeout);
        AssertEqual(42, result, "OneArg closure should return 14 * 3");
        TestLogger.Info($"AsyncThrowingClosure.OneArgPrimitive = {result}");
    }

    /// <summary>
    /// Arity-1 primitive arg, error path: confirms the per-arity bridge routes
    /// continuation.resume(throwing:) back through SwiftBindingsBridgeError when
    /// args are present (not just the no-args baseline).
    /// </summary>
    public async Task TestOneArgPrimitiveClosurePropagatesError()
    {
        Func<int, Task<int>> userLambda = async v =>
        {
            await Task.Yield();
            throw new InvalidOperationException($"oneArg-boom-{v}");
        };

        try
        {
            await WithTimeout(
                Functions.CallAsyncThrowingClosureOneArgForErrorAsync(7, userLambda),
                DefaultAsyncTimeout);
            throw new AssertionException("Expected SwiftException but no exception was thrown");
        }
        catch (SwiftException ex)
        {
            if (!ex.Message.Contains("oneArg-boom-7"))
                throw new AssertionException($"Expected 'oneArg-boom-7' in message, got: {ex.Message}");
            TestLogger.Info($"AsyncThrowingClosure.OneArgPrimitive.Error threw: {ex.Message}");
        }
    }

    /// <summary>
    /// Arity-2 mixed (Int32, String): confirms SwiftString survives the
    /// Swift→C# synchronous marshal (withUnsafePointer + borrowed marshal)
    /// before Task.Run captures the managed `string` value.
    /// </summary>
    public async Task TestTwoArgMixedClosureReceivesString()
    {
        Func<int, string, Task<int>> userLambda = (n, tag) =>
        {
            // Mix the args: return n + length of the tag string.
            return Task.FromResult(n + tag.Length);
        };

        var result = await WithTimeout(
            Functions.CallAsyncThrowingClosureTwoArgsAsync(40, "ab", userLambda),
            DefaultAsyncTimeout);

        AssertEqual(42, result, "TwoArgs closure should return 40 + len(\"ab\")");
        TestLogger.Info($"AsyncThrowingClosure.TwoArgs = {result}");
    }

    /// <summary>
    /// Arity-3 with a Swift class arg (Int32, String, AsyncClosureArgBox):
    /// confirms the Swift class arg round-trips via Unmanaged.passUnretained
    /// → Arc.Retain → MarshalFromSwift, and that the C# closure sees the
    /// correct instance payload.
    /// </summary>
    public async Task TestThreeArgsClassArgRoundTrips()
    {
        using var originalBox = new AsyncClosureArgBox(tag: 99);

        Func<int, string, AsyncClosureArgBox, Task<int>> userLambda = (n, tag, box) =>
        {
            TestLogger.Info($"ThreeArgs.Lambda: n={n} tag='{tag}' box.Tag={box.Tag} handle=0x{((Swift.Runtime.ISwiftObject)box).SwiftHandle.ToInt64():X}");
            return Task.FromResult(n + tag.Length + box.Tag);
        };

        TestLogger.Info($"ThreeArgs.OriginalBox handle=0x{((Swift.Runtime.ISwiftObject)originalBox).SwiftHandle.ToInt64():X} tag={originalBox.Tag}");

        var result = await WithTimeout(
            Functions.CallAsyncThrowingClosureThreeArgsAsync(10, "xyz", originalBox, userLambda),
            DefaultAsyncTimeout);

        AssertEqual(10 + 3 + 99, result, "ThreeArgs closure should return n + len(tag) + box.Tag");
        TestLogger.Info($"AsyncThrowingClosure.ThreeArgs = {result}");
    }

    // MARK: - Foundation.Data return

    /// <summary>
    /// Async-throwing closure returning Foundation.Data. A 1MB byte
    /// buffer is produced in C#, ferried C# → Swift via the
    /// DataAsyncClosureHelper.RunDataAsync path + AsyncBoxData Swift box, then
    /// reduced to a byte-sum checksum on the Swift side. Both sides compute the
    /// same sum; mismatch means bytes were lost or corrupted in the bridge.
    /// (Outer method returns Int64, not Data, because async-method-returning-Data
    /// is a separate pre-existing gap — see AsyncClosures.swift for rationale.)
    /// </summary>
    public async Task TestDataReturnClosureRoundTripsOneMegabyte()
    {
        const int size = 1024 * 1024;
        var payload = new byte[size];
        long expectedSum = 0;
        for (int i = 0; i < size; i++)
        {
            byte b = (byte)(i & 0xFF);
            payload[i] = b;
            expectedSum += b;
        }

        Func<Task<byte[]>> userLambda = () => Task.FromResult(payload);

        var actualSum = await WithTimeout(
            Functions.CallAsyncThrowingDataClosureAsync(userLambda),
            DefaultAsyncTimeout);

        AssertEqual(expectedSum, actualSum,
            $"Swift-side byte-sum over the 1MB payload should match C#-side sum (expected={expectedSum})");
        TestLogger.Info($"AsyncThrowingClosure.DataReturn round-tripped {size} bytes, checksum={actualSum}");
    }

    /// <summary>
    /// Edge case: empty Data round-trip. C# pins a zero-length byte
    /// array (AddrOfPinnedObject returns IntPtr.Zero for empty arrays), and the
    /// Swift bridge calls Data(bytes: bytesPtr, count: 0). Asserts the bridge
    /// doesn't deref the null pointer when length is zero, and that the byte
    /// sum is 0 (vacuous loop on the Swift side).
    /// </summary>
    public async Task TestDataReturnClosureRoundTripsEmptyData()
    {
        Func<Task<byte[]>> userLambda = () => Task.FromResult(Array.Empty<byte>());

        var actualSum = await WithTimeout(
            Functions.CallAsyncThrowingDataClosureAsync(userLambda),
            DefaultAsyncTimeout);

        AssertEqual(0L, actualSum, "Empty Data payload should sum to 0");
        TestLogger.Info("AsyncThrowingClosure.DataReturn empty payload round-tripped without crash");
    }

    // MARK: - Swift.String return

    /// <summary>
    /// Async-throwing closure returning Swift.String. C# delegate returns
    /// a UTF-8 managed string, StringAsyncClosureHelper pins the UTF-8 bytes, Swift
    /// resumes the continuation with a decoded String. The outer method returns the
    /// round-tripped string so the test asserts full byte-level parity.
    /// </summary>
    public async Task TestStringReturnClosureRoundTripsSimpleString()
    {
        Func<Task<string>> userLambda = () => Task.FromResult("hello-session-F");

        var actual = await WithTimeout(
            Functions.CallAsyncThrowingStringClosureAsync(userLambda),
            DefaultAsyncTimeout);

        AssertEqual("hello-session-F", actual, "String-return async-throwing closure should round-trip ASCII payload");
        TestLogger.Info($"AsyncThrowingClosure.StringReturn = '{actual}'");
    }

    /// <summary>
    /// Edge case: empty String round-trip. Zero-length byte[] has
    /// <c>AddrOfPinnedObject == IntPtr.Zero</c>; Swift must not deref on length=0.
    /// </summary>
    public async Task TestStringReturnClosureRoundTripsEmptyString()
    {
        Func<Task<string>> userLambda = () => Task.FromResult(string.Empty);

        var actual = await WithTimeout(
            Functions.CallAsyncThrowingStringClosureAsync(userLambda),
            DefaultAsyncTimeout);

        AssertEqual(string.Empty, actual, "Empty string payload should round-trip unchanged");
        TestLogger.Info("AsyncThrowingClosure.StringReturn empty payload round-tripped without crash");
    }

    /// <summary>
    /// Multi-byte UTF-8 round-trip (emoji + non-ASCII). Proves the
    /// Swift-side <c>String(decoding: buffer, as: UTF8.self)</c> reconstructs
    /// code points identical to the managed input.
    /// </summary>
    public async Task TestStringReturnClosureRoundTripsUtf8Payload()
    {
        const string payload = "🚀 héllo ☃ wörld";
        Func<Task<string>> userLambda = () => Task.FromResult(payload);

        var actual = await WithTimeout(
            Functions.CallAsyncThrowingStringClosureAsync(userLambda),
            DefaultAsyncTimeout);

        AssertEqual(payload, actual, "UTF-8 payload should survive the String bridge intact");
        TestLogger.Info($"AsyncThrowingClosure.StringReturn UTF-8 round-trip ok ({payload.Length} code units)");
    }

    /// <summary>
    /// C# lambda throws; Swift error channel should surface it as a
    /// <c>SwiftException</c> with the original message preserved.
    /// </summary>
    public async Task TestStringReturnClosurePropagatesError()
    {
        Func<Task<string>> userLambda = async () =>
        {
            await Task.Yield();
            throw new InvalidOperationException("session-F-boom");
        };

        try
        {
            await WithTimeout(
                Functions.CallAsyncThrowingStringClosureAsync(userLambda),
                DefaultAsyncTimeout);
            throw new AssertionException("Expected SwiftException but no exception was thrown");
        }
        catch (SwiftException ex)
        {
            if (!ex.Message.Contains("session-F-boom"))
                throw new AssertionException($"Expected 'session-F-boom' in message, got: {ex.Message}");
            TestLogger.Info($"AsyncThrowingClosure.StringReturn error path threw SwiftException: {ex.Message}");
        }
    }

    /// <summary>
    /// Same closure invoked twice inside one outer call. Verifies a
    /// fresh CheckedContinuation + box is built per invocation — if the adapter
    /// reused state, the second await would hit a resumed continuation.
    /// </summary>
    public async Task TestStringReturnClosureInvokedTwice()
    {
        int callCount = 0;
        Func<Task<string>> userLambda = () =>
        {
            var n = Interlocked.Increment(ref callCount);
            return Task.FromResult($"call-{n}");
        };

        var joined = await WithTimeout(
            Functions.CallAsyncThrowingStringClosureTwiceAsync(userLambda),
            DefaultAsyncTimeout);

        AssertEqual("call-1|call-2", joined, "Twice variant should concatenate two independent invocations");
        AssertEqual(2, callCount, "User lambda should have been invoked exactly twice");
        TestLogger.Info($"AsyncThrowingClosure.StringReturn twice = '{joined}'");
    }

    /// <summary>
    /// Arity-1: closure receives Int32 arg, returns a String formed
    /// from it. Exercises the arg-bearing Start thunk path (String-return was
    /// previously Data-like zero-arg only).
    /// </summary>
    public async Task TestStringReturnClosureArity1()
    {
        Func<int, Task<string>> userLambda = x => Task.FromResult($"arg={x}");

        var actual = await WithTimeout(
            Functions.CallAsyncThrowingStringClosureArity1Async(userLambda),
            DefaultAsyncTimeout);

        AssertEqual("arg=42", actual, "Arity-1 String-return closure should receive 42 from Swift");
        TestLogger.Info($"AsyncThrowingClosure.StringReturn arity-1 = '{actual}'");
    }

    /// <summary>
    /// Arity-2: closure receives (Int32, String), returns a String.
    /// Shape matches PaymentSheet.IntentConfiguration confirm handlers — the
    /// exact pattern that needed to unblock for StripePaymentSheet.
    /// </summary>
    public async Task TestStringReturnClosureArity2()
    {
        Func<int, string, Task<string>> userLambda = (n, s) => Task.FromResult($"{s}:{n}");

        var actual = await WithTimeout(
            Functions.CallAsyncThrowingStringClosureArity2Async(userLambda),
            DefaultAsyncTimeout);

        AssertEqual("hello:7", actual, "Arity-2 String-return closure should round-trip (Int32, String) args");
        TestLogger.Info($"AsyncThrowingClosure.StringReturn arity-2 = '{actual}'");
    }
}
