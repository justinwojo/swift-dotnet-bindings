// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Async;

/// <summary>
/// Async methods whose parameter list carries an Optional closure followed by a non-optional
/// trailing parameter.
///
/// The trailing value is the load-bearing assertion, not decoration: the closure crosses the
/// @_cdecl boundary as a (funcPtr, context) pointer PAIR, so if the C# P/Invoke and the generated
/// Swift wrapper disagree on how many C ABI words the parameter occupies, every later
/// register-passed argument — the trailing value, and `self` — shifts by one. A returned sum that
/// still equals base + trailing is the observable proof the register layout lines up, and the
/// receiver-derived value (the struct's bias) proves `self` did too.
///
/// The callback fires after a real suspension inside the Swift body, so it also exercises the
/// carrier's lifetime: the GCHandle rooting the delegate is owned by a Swift ARC box that outlives
/// the @_cdecl return, not by the C# call frame.
/// </summary>
public class AsyncOptionalClosureParamTests : TestBase
{
    public AsyncOptionalClosureParamTests(TestResults results) : base(results) { }

    public async Task TestInstanceNullCallbackTrailingValueSurvives()
    {
        var carrier = new AsyncOptionalClosureCarrier();
        var result = await WithTimeout(
            carrier.SumWithProgressAsync(11, null, 31),
            DefaultAsyncTimeout);
        AssertEqual(42L, result, "explicit-null callback must not shift the trailing parameter");
    }

    public async Task TestInstanceOmittedCallbackTrailingValueSurvives()
    {
        // The defaulted-parameter trim overload drops the closure at the call site entirely; the
        // wrapper still declares both carrier words, so the same register layout has to hold.
        var carrier = new AsyncOptionalClosureCarrier();
        var result = await WithTimeout(
            carrier.SumWithDefaultedProgressAsync(70, 5),
            DefaultAsyncTimeout);
        AssertEqual(75L, result, "omitted callback must not shift the trailing parameter");
    }

    public async Task TestInstanceCallbackInvokedAfterSuspension()
    {
        var carrier = new AsyncOptionalClosureCarrier();
        long seenBase = -1;
        long seenTrailing = -1;
        int calls = 0;

        var result = await WithTimeout(
            carrier.SumWithProgressAsync(100, (b, t) => { seenBase = b; seenTrailing = t; calls++; }, 23),
            DefaultAsyncTimeout);

        AssertEqual(123L, result, "non-null callback must not shift the trailing parameter");
        AssertEqual(1, calls, "callback invoked exactly once");
        AssertEqual(100L, seenBase, "callback saw the base argument");
        AssertEqual(23L, seenTrailing, "callback saw the trailing argument");
    }

    public async Task TestInstanceCallbackWithClassTypedArgument()
    {
        var carrier = new AsyncOptionalClosureCarrier();
        long seenTokenValue = -1;

        var result = await WithTimeout(
            carrier.SumWithTokenCallbackAsync(40, token => seenTokenValue = token.Value, 2),
            DefaultAsyncTimeout);

        AssertEqual(42L, result, "class-arg callback must not shift the trailing parameter");
        AssertEqual(42L, seenTokenValue, "callback received the Swift-constructed class payload");
    }

    public async Task TestTwoOptionalCallbacksBothInvoked()
    {
        // Two carriers back to back: the trailing parameter now sits four C ABI words past the
        // first closure, so a one-word-per-closure disagreement is doubly visible.
        var carrier = new AsyncOptionalClosureCarrier();
        long seenFirst = -1;
        long seenSecond = -1;

        var result = await WithTimeout(
            carrier.SumWithTwoProgressBlocksAsync(9, v => seenFirst = v, v => seenSecond = v, 33),
            DefaultAsyncTimeout);

        AssertEqual(42L, result, "two callbacks must not shift the trailing parameter");
        AssertEqual(9L, seenFirst, "first callback saw the base argument");
        AssertEqual(33L, seenSecond, "second callback saw the trailing argument");
    }

    public async Task TestTwoOptionalCallbacksMixedNull()
    {
        var carrier = new AsyncOptionalClosureCarrier();
        long seenSecond = -1;

        var result = await WithTimeout(
            carrier.SumWithTwoProgressBlocksAsync(9, null, v => seenSecond = v, 33),
            DefaultAsyncTimeout);

        AssertEqual(42L, result, "null-then-non-null callbacks must not shift the trailing parameter");
        AssertEqual(33L, seenSecond, "second callback still saw the trailing argument");
    }

    public async Task TestStaticCallbackInvoked()
    {
        // Static parent: no `self` word after the trailing parameter, so this isolates the
        // parameter-list shift from the receiver.
        long seenBase = -1;
        var result = await WithTimeout(
            AsyncOptionalClosureCarrier.StaticSumWithProgressAsync(20, (b, _) => seenBase = b, 22),
            DefaultAsyncTimeout);

        AssertEqual(42L, result, "static method must not shift the trailing parameter");
        AssertEqual(20L, seenBase, "static callback saw the base argument");
    }

    public async Task TestStaticNullCallback()
    {
        var result = await WithTimeout(
            AsyncOptionalClosureCarrier.StaticSumWithProgressAsync(20, null, 22),
            DefaultAsyncTimeout);
        AssertEqual(42L, result, "static null callback must not shift the trailing parameter");
    }

    public async Task TestStructReceiverCallbackInvoked()
    {
        // Value-typed receiver: the result folds in the struct's stored `bias`, so a corrupted
        // `self` word shows up as a wrong sum rather than only as a crash.
        using var carrier = new AsyncOptionalClosureValueCarrier(1000);
        long seenTrailing = -1;

        var result = await WithTimeout(
            carrier.SumWithProgressAsync(30, (_, t) => seenTrailing = t, 12),
            DefaultAsyncTimeout);

        AssertEqual(1042L, result, "struct receiver: bias + base + trailing all survived");
        AssertEqual(12L, seenTrailing, "struct-receiver callback saw the trailing argument");
    }

    public async Task TestStructReceiverNullCallback()
    {
        using var carrier = new AsyncOptionalClosureValueCarrier(1000);
        var result = await WithTimeout(
            carrier.SumWithProgressAsync(30, null, 12),
            DefaultAsyncTimeout);
        AssertEqual(1042L, result, "struct receiver with null callback kept bias, base and trailing");
    }

    // The tests above prove the null arm does not crash and does not shift a register. These prove
    // the stronger property the crash tests cannot see: that Swift observed a genuine `nil`, not a
    // non-nil closure that happens to do nothing. `progress?(…)` returns the same value either way,
    // so the fixture folds the observation into the SIGN of the result and each null case is paired
    // with a non-null control — without the control a hardcoded sign would pass.

    public async Task TestInstanceNullCallbackObservedAsNilBySwift()
    {
        var carrier = new AsyncOptionalClosureCarrier();
        var result = await WithTimeout(
            carrier.ObservedNilnessSumAsync(11, null, 31),
            DefaultAsyncTimeout);
        AssertEqual(-42L, result, "Swift must observe an explicit-null callback as nil");
    }

    public async Task TestInstanceOmittedCallbackObservedAsNilBySwift()
    {
        var carrier = new AsyncOptionalClosureCarrier();
        var result = await WithTimeout(
            carrier.ObservedNilnessSumAsync(11, 31),
            DefaultAsyncTimeout);
        AssertEqual(-42L, result, "Swift must observe an omitted callback as nil");
    }

    public async Task TestInstanceNonNullCallbackObservedAsPresentBySwift()
    {
        var carrier = new AsyncOptionalClosureCarrier();
        int calls = 0;
        var result = await WithTimeout(
            carrier.ObservedNilnessSumAsync(11, (_, _) => calls++, 31),
            DefaultAsyncTimeout);

        AssertEqual(42L, result, "Swift must observe a supplied callback as non-nil");
        AssertEqual(1, calls, "the supplied callback still ran");
    }

    public async Task TestStaticNullCallbackObservedAsNilBySwift()
    {
        var result = await WithTimeout(
            AsyncOptionalClosureCarrier.StaticObservedNilnessSumAsync(20, null, 22),
            DefaultAsyncTimeout);
        AssertEqual(-42L, result, "static method: Swift must observe a null callback as nil");
    }

    public async Task TestStaticNonNullCallbackObservedAsPresentBySwift()
    {
        int calls = 0;
        var result = await WithTimeout(
            AsyncOptionalClosureCarrier.StaticObservedNilnessSumAsync(20, (_, _) => calls++, 22),
            DefaultAsyncTimeout);

        AssertEqual(42L, result, "static method: Swift must observe a supplied callback as non-nil");
        AssertEqual(1, calls, "the supplied static callback still ran");
    }

    public async Task TestStructReceiverNullCallbackObservedAsNilBySwift()
    {
        using var carrier = new AsyncOptionalClosureValueCarrier(1000);
        var result = await WithTimeout(
            carrier.ObservedNilnessSumAsync(30, null, 12),
            DefaultAsyncTimeout);
        AssertEqual(-1042L, result, "struct receiver: Swift must observe a null callback as nil");
    }

    public async Task TestStructReceiverNonNullCallbackObservedAsPresentBySwift()
    {
        using var carrier = new AsyncOptionalClosureValueCarrier(1000);
        int calls = 0;
        var result = await WithTimeout(
            carrier.ObservedNilnessSumAsync(30, (_, _) => calls++, 12),
            DefaultAsyncTimeout);

        AssertEqual(1042L, result, "struct receiver: Swift must observe a supplied callback as non-nil");
        AssertEqual(1, calls, "the supplied struct-receiver callback still ran");
    }

    public async Task TestBareEscapingCallbackRidesTheSameCarrier()
    {
        // Non-optional `@escaping` sibling. The carrier is keyed on escaping-ness, not on
        // Optional-ness, so this shape goes through the identical (funcPtr, context) pair and
        // owner token — and it was previously skipped outright as ABI-unsafe, so this is also the
        // runtime proof that the un-skipped surface actually works.
        var carrier = new AsyncOptionalClosureCarrier();
        long seenBase = -1;
        long seenTrailing = -1;

        var result = await WithTimeout(
            carrier.SumWithEscapingProgressAsync(15, (b, t) => { seenBase = b; seenTrailing = t; }, 27),
            DefaultAsyncTimeout);

        AssertEqual(42L, result, "bare escaping callback must not shift the trailing parameter");
        AssertEqual(15L, seenBase, "bare escaping callback saw the base argument");
        AssertEqual(27L, seenTrailing, "bare escaping callback saw the trailing argument");
    }

    public async Task TestCancellationWhileCallbackCarrierIsLive()
    {
        // The Swift body sleeps well past the cancel, so the closure carrier is still held by the
        // in-flight task when the token fires. The callback must never run, and the awaiter must
        // observe cancellation rather than the sum.
        var carrier = new AsyncOptionalClosureCarrier();
        int calls = 0;
        using var cts = new CancellationTokenSource();

        var work = carrier.CancellableSumWithProgressAsync(1, (_, _) => calls++, 2, cts.Token);
        await Task.Delay(50);
        cts.Cancel();

        try
        {
            await WithTimeout(work, DefaultAsyncTimeout);
            AssertTrue(false, "cancelled call — expected OperationCanceledException");
        }
        catch (OperationCanceledException)
        {
            // Expected.
        }

        AssertEqual(0, calls, "cancelled Swift body must not invoke the optional callback");
    }

    public async Task TestUncancelledThrowingOverloadStillReturnsSum()
    {
        // Positive control for the cancellable shape: without a cancel it runs to completion and
        // the trailing parameter is still intact on the throwing @_cdecl signature (which carries
        // an extra error-callback word ahead of the user parameters).
        var carrier = new AsyncOptionalClosureCarrier();
        long seenTrailing = -1;
        var result = await WithTimeout(
            carrier.CancellableSumWithProgressAsync(2, (_, t) => seenTrailing = t, 40),
            TimeSpan.FromSeconds(10));

        AssertEqual(42L, result, "throwing async overload kept the trailing parameter");
        AssertEqual(40L, seenTrailing, "throwing async overload's callback saw the trailing argument");
    }

    public async Task TestRepeatedCallsDoNotCorruptCarrier()
    {
        // Exercises the per-call GCHandle/context lifetime: each call allocates its own root and
        // hands ownership to Swift, so a stale or double-freed context would surface here as a
        // wrong value or a crash rather than a leak nobody observes.
        var carrier = new AsyncOptionalClosureCarrier();
        for (int i = 0; i < 25; i++)
        {
            long seen = -1;
            var expected = i + 1000L;
            var result = await WithTimeout(
                carrier.SumWithProgressAsync(i, (_, t) => seen = t, 1000),
                DefaultAsyncTimeout);
            AssertEqual(expected, result, $"iteration {i}: sum");
            AssertEqual(1000L, seen, $"iteration {i}: callback trailing argument");
        }
    }
}
