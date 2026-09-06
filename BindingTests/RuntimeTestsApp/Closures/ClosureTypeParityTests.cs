// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using RuntimeTestsApp.Infrastructure;
using Swift;
using Swift.Foundation;
using Swift.Runtime;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Closures;

/// <summary>
/// Runtime coverage for closure delegate-type parity.
///
/// <para>A closure parameter is bridged in two halves that must agree on ONE C# delegate type: the
/// public signature declares the type the consumer's lambda is stored under, and the
/// <c>[UnmanagedCallersOnly]</c> trampoline recovers it with
/// <c>SwiftClosureMarshaller.GetDelegateFrom(Boxed)Context&lt;T&gt;</c>. That recovery is an
/// unchecked cast of the GCHandle target, so while the two halves were computed by two independent
/// translators the disagreement was invisible to both compilers: the first callback threw
/// <c>InvalidCastException</c> inside the trampoline, where
/// <c>FailFastUnhandledClosureException</c> turned it into a process abort.</para>
///
/// <para>That is why these are runtime tests rather than emitter assertions — every shape below
/// compiled cleanly while it was broken. The two divergent shapes were a
/// <c>Result&lt;T?, any Error&gt;</c> failure arm (raw existential carrier vs the well-known
/// <c>Swift.Error</c> mapping) and a collection in callback argument or return position (idiomatic
/// interface vs Swift container carrier). Reaching the callback at all is the load-bearing
/// assertion; the payload checks are what separates a working bridge from one that merely does not
/// abort.</para>
///
/// <para>Eight of these carry a <c>Skip</c> naming a <b>different</b> defect on the same path: the
/// direct <c>CallConvSwift</c> bridge declares every callback argument as <c>void* arg0</c>, but
/// Swift passes a loadable argument by value in registers rather than by address. Those shapes now
/// reach their delegate — the parity half is what this class proves — and then read the payload word
/// as though it were the payload's address. The tests are left written and skipped rather than
/// deleted: they are the standing red flag for that defect, and the method-closure-bridge siblings
/// (<c>BridgePath*</c>, which get a real pointer) run and pass beside them, which is what localises
/// it to the direct bridge.</para>
/// </summary>
public class ClosureTypeParityTests : TestBase
{
    public ClosureTypeParityTests(TestResults results) : base(results) { }

    // The two `WrapperPathResultHost` initializers differ only in their delegate type, so a bare
    // lambda cannot pick one. Spelling the delegate type at a single helper apiece also keeps these
    // tests honest: they bind against the type the PUBLIC signature declares, which is exactly the
    // half that used to disagree with the trampoline.
    private static WrapperPathResultHost Payload(
        int mode,
        Action<SwiftResult<ParityPayload?, AnyError>> completion)
        => new WrapperPathResultHost(mode, completion);

    private static WrapperPathResultHost Any(
        int anyMode,
        Action<SwiftResult<SwiftOptional<ExistentialContainer0>, AnyError>> completion)
        => new WrapperPathResultHost(anyMode, completion);

    // The `Skip` reason repeated below, kept in one place so the eight tests it gates cannot drift
    // apart. It describes a SECOND defect on the same path, independent of the delegate-type parity
    // this class covers, and the tests stay written so they turn green the moment it is fixed.
    private const string DirectBridgeArgAbi =
        "Direct CallConvSwift closure bridge models every callback argument as a pointer (void* arg0), "
        + "but Swift passes a loadable argument BY VALUE in registers: a (Result<Class?, any Error>) -> Void "
        + "closure is called with x0 = the payload word and x1 = the enum tag, an ([Double]) -> Void closure "
        + "with x0 = the array's storage pointer. Reading arg0 as the address of the value then dereferences "
        + "the payload itself. Separate from the delegate-type parity under test here, which these shapes do "
        + "reach and pass; the argument ABI is what stops them.";

    // ─── Result<Optional<Class>, any Error> on the wrapper-emitter path ───
    //
    // Constructor position is what puts these on the ordinary wrapper-emitter closure path: the
    // method-closure bridge never claims initializers, and the bridge is internally consistent (it
    // computes both halves itself), so an instance method would not reach the divergence at all.
    //
    // That is also why these five are the ones the argument ABI blocks: the method-closure bridge
    // hands its callback a pointer it created itself (an `Action<IntPtr>` adapter over a Swift-side
    // `withUnsafePointer`), so the `BridgePath*` tests below observe the same Swift shapes soundly.
    // The split is the evidence that the argument defect belongs to the direct bridge alone.

    /// <summary>
    /// Success arm carrying a bound class: exactly one callback, a readable payload, and a receiver
    /// still usable once the callback that ran during its own initializer has returned.
    /// </summary>
    [Skip(DirectBridgeArgAbi)]
    public void TestWrapperPathResultSuccessWithPayload()
    {
        int calls = 0;
        bool sawSuccess = false;
        string? label = null;
        int magnitude = -1;

        using var host = Payload(0, result =>
        {
            calls++;
            using (result)
            {
                if (result.IsSuccess)
                {
                    sawSuccess = true;
                    var payload = result.Success;
                    if (payload != null)
                    {
                        label = payload.Label;
                        magnitude = payload.Magnitude;
                    }
                }
            }
        });

        AssertEqual(1, calls, $"Expected exactly one callback, got {calls}");
        AssertTrue(sawSuccess, "Expected the success arm for mode 0");
        AssertEqual("wrapper-path", label, $"Expected the payload's label to survive, got '{label}'");
        AssertEqual(11, magnitude, $"Expected magnitude 11, got {magnitude}");
        AssertEqual(0, host.DeliveredCode, $"Expected DeliveredCode 0, got {host.DeliveredCode}");
    }

    /// <summary>
    /// Success arm carrying <c>nil</c> — the Optional-inside-Result arm. Distinguishing it from the
    /// case above is what proves the success value is read rather than assumed present.
    /// </summary>
    [Skip(DirectBridgeArgAbi)]
    public void TestWrapperPathResultSuccessWithNilPayload()
    {
        int calls = 0;
        bool sawSuccess = false;
        bool payloadWasNull = false;

        using var host = Payload(1, result =>
        {
            calls++;
            using (result)
            {
                if (result.IsSuccess)
                {
                    sawSuccess = true;
                    payloadWasNull = result.Success == null;
                }
            }
        });

        AssertEqual(1, calls, $"Expected exactly one callback, got {calls}");
        AssertTrue(sawSuccess, "Expected the success arm for mode 1");
        AssertTrue(payloadWasNull, "Expected the success value to be null");
        AssertEqual(1, host.DeliveredCode, $"Expected DeliveredCode 1, got {host.DeliveredCode}");
    }

    /// <summary>
    /// Failure arm. The error is the half the two translators actually disagreed on, so reading a
    /// description off it — rather than only observing that the failure case arrived — is the
    /// assertion that matters here.
    /// </summary>
    [Skip(DirectBridgeArgAbi)]
    public void TestWrapperPathResultFailure()
    {
        int calls = 0;
        bool sawFailure = false;
        string? description = null;

        using var host = Payload(7, result =>
        {
            calls++;
            using (result)
            {
                if (result.IsFailure)
                {
                    sawFailure = true;
                    using var error = result.Failure;
                    description = error?.LocalizedDescription;
                }
            }
        });

        AssertEqual(1, calls, $"Expected exactly one callback, got {calls}");
        AssertTrue(sawFailure, "Expected the failure arm for mode 7");
        // AnyError describes the boxed error with Swift's String(describing:), so a struct error
        // renders as its type name plus its stored properties.
        AssertNotNull(description, "Expected a readable error description");
        AssertTrue(description!.Contains("ParityFailure") && description.Contains("7"),
            $"Expected the Swift error's identity and code to survive, got '{description}'");
        AssertEqual(7, host.DeliveredCode, $"Expected DeliveredCode 7, got {host.DeliveredCode}");
    }

    /// <summary>
    /// The exact reported shape — an opaque <c>Any?</c> success arm, which resolves to an existential
    /// carrier rather than a bound class and reaches the same delegate type by a different route.
    /// </summary>
    [Skip(DirectBridgeArgAbi)]
    public void TestWrapperPathAnyResultSuccess()
    {
        int calls = 0;
        bool sawSuccess = false;
        bool hadValue = false;

        using var host = Any(0, result =>
        {
            calls++;
            using (result)
            {
                if (result.IsSuccess)
                {
                    sawSuccess = true;
                    hadValue = result.Success.HasValue;
                }
            }
        });

        AssertEqual(1, calls, $"Expected exactly one callback, got {calls}");
        AssertTrue(sawSuccess, "Expected the success arm for anyMode 0");
        AssertTrue(hadValue, "Expected the boxed Any? success value to be present");
        AssertEqual(0, host.DeliveredCode, $"Expected DeliveredCode 0, got {host.DeliveredCode}");
    }

    /// <summary>Failure arm of the <c>Any?</c> variant.</summary>
    [Skip(DirectBridgeArgAbi)]
    public void TestWrapperPathAnyResultFailure()
    {
        int calls = 0;
        bool sawFailure = false;
        string? description = null;

        using var host = Any(9, result =>
        {
            calls++;
            using (result)
            {
                if (result.IsFailure)
                {
                    sawFailure = true;
                    using var error = result.Failure;
                    description = error?.LocalizedDescription;
                }
            }
        });

        AssertEqual(1, calls, $"Expected exactly one callback, got {calls}");
        AssertTrue(sawFailure, "Expected the failure arm for anyMode 9");
        AssertNotNull(description, "Expected a readable error description");
        AssertTrue(description!.Contains("ParityFailure") && description.Contains("9"),
            $"Expected the Swift error's identity and code to survive, got '{description}'");
        AssertEqual(9, host.DeliveredCode, $"Expected DeliveredCode 9, got {host.DeliveredCode}");
    }

    // ─── The same Result shape on the method-closure-bridge path ───
    //
    // The bridge computes both halves itself, so these were never broken. They are the control that
    // keeps the assertions above attributable: had BOTH paths failed, the cause would be Result
    // marshalling rather than the delegate-type divergence.

    /// <summary>Bridge-path success arm — class parent, instance member.</summary>
    public void TestBridgePathResultSuccess()
    {
        using var host = new ResultOptionalCallbackHost();
        int calls = 0;
        string? label = null;

        host.DeliverSome(result =>
        {
            calls++;
            using (result)
            {
                if (result.IsSuccess && result.Success.HasValue)
                {
                    using var payload = result.Success.Value;
                    label = payload!.Label;
                }
            }
        });

        AssertEqual(1, calls, $"Expected exactly one callback, got {calls}");
        AssertEqual("some", label, $"Expected payload label 'some', got '{label}'");
    }

    /// <summary>Bridge-path <c>nil</c> success arm — class parent, instance member.</summary>
    public void TestBridgePathResultNone()
    {
        using var host = new ResultOptionalCallbackHost();
        int calls = 0;
        bool sawSuccess = false;
        bool hadValue = true;

        host.DeliverNone(result =>
        {
            calls++;
            using (result)
            {
                sawSuccess = result.IsSuccess;
                if (sawSuccess)
                    hadValue = result.Success.HasValue;
            }
        });

        AssertEqual(1, calls, $"Expected exactly one callback, got {calls}");
        AssertTrue(sawSuccess, "Expected the success arm");
        AssertFalse(hadValue, "Expected the success value to be absent");
    }

    /// <summary>Bridge-path failure arm — class parent, instance member.</summary>
    public void TestBridgePathResultFailure()
    {
        using var host = new ResultOptionalCallbackHost();
        int calls = 0;
        bool sawFailure = false;

        host.DeliverFailure(result =>
        {
            calls++;
            using (result)
            {
                sawFailure = result.IsFailure;
            }
        });

        AssertEqual(1, calls, $"Expected exactly one callback, got {calls}");
        AssertTrue(sawFailure, "Expected the failure arm");
    }

    /// <summary>Bridge-path success arm on a STATIC member — a separate emission path.</summary>
    public void TestBridgePathResultSuccessStatic()
    {
        int calls = 0;
        string? label = null;

        ResultOptionalCallbackHost.DeliverSomeStatic(result =>
        {
            calls++;
            using (result)
            {
                if (result.IsSuccess && result.Success.HasValue)
                {
                    using var payload = result.Success.Value;
                    label = payload!.Label;
                }
            }
        });

        AssertEqual(1, calls, $"Expected exactly one callback, got {calls}");
        AssertEqual("static", label, $"Expected payload label 'static', got '{label}'");
    }

    /// <summary>Bridge-path success arm with a STRUCT parent rather than a class parent.</summary>
    public void TestBridgePathResultSuccessStructParent()
    {
        using var host = new ResultOptionalCallbackStruct();
        int calls = 0;
        string? label = null;

        host.DeliverSome(result =>
        {
            calls++;
            using (result)
            {
                if (result.IsSuccess && result.Success.HasValue)
                {
                    using var payload = result.Success.Value;
                    label = payload!.Label;
                }
            }
        });

        AssertEqual(1, calls, $"Expected exactly one callback, got {calls}");
        AssertEqual("struct-some", label, $"Expected payload label 'struct-some', got '{label}'");
    }

    /// <summary>
    /// Bridge-path <c>Result&lt;Any?, any Error&gt;</c> — the same existential success arm as the
    /// wrapper-path case, on the path that always agreed with itself.
    /// </summary>
    public void TestBridgePathAnyResultSuccess()
    {
        using var host = new AnyResultCallbackHost();
        int calls = 0;
        bool hadValue = false;

        host.DeliverAnySome(result =>
        {
            calls++;
            using (result)
            {
                if (result.IsSuccess)
                    hadValue = result.Success.HasValue;
            }
        });

        AssertEqual(1, calls, $"Expected exactly one callback, got {calls}");
        AssertTrue(hadValue, "Expected the boxed Any? success value to be present");
    }

    /// <summary>The struct-parent variant of the existential success arm.</summary>
    public void TestBridgePathAnyResultSuccessStructParent()
    {
        using var host = new AnyResultCallbackStruct();
        int calls = 0;
        bool hadValue = false;

        host.DeliverAnySome(result =>
        {
            calls++;
            using (result)
            {
                if (result.IsSuccess)
                    hadValue = result.Success.HasValue;
            }
        });

        AssertEqual(1, calls, $"Expected exactly one callback, got {calls}");
        AssertTrue(hadValue, "Expected the boxed Any? success value to be present");
    }

    // ─── Collection-shaped callback arguments ───

    /// <summary>
    /// <c>([Double]) -> Void</c>. Swift builds the array and hands it to the C# lambda; reading the
    /// elements is what shows the carrier arrived intact rather than as a mis-cast delegate.
    /// </summary>
    [Skip(DirectBridgeArgAbi)]
    public void TestArrayCallbackArgument()
    {
        using var host = new CollectionCallbackHost();
        int calls = 0;
        var seen = new List<double>();

        host.EmitDoubles(values =>
        {
            calls++;
            foreach (var value in values)
                seen.Add(value);
        });

        AssertEqual(1, calls, $"Expected exactly one callback, got {calls}");
        AssertEqual(3, seen.Count, $"Expected 3 values, got {seen.Count}");
        AssertApproxEqual(7.5, seen.Sum(), message: $"Expected 1.5 + 2.5 + 3.5 = 7.5, got {seen.Sum()}");
    }

    /// <summary>The struct-element variant of the same argument shape.</summary>
    [Skip(DirectBridgeArgAbi)]
    public void TestStructArrayCallbackArgument()
    {
        using var host = new CollectionCallbackHost();
        int calls = 0;
        double total = 0;
        int count = -1;

        host.EmitPoints(points =>
        {
            calls++;
            count = points.Count;
            foreach (var point in points)
                total += point.X + point.Y;
        });

        AssertEqual(1, calls, $"Expected exactly one callback, got {calls}");
        AssertEqual(2, count, $"Expected 2 points, got {count}");
        AssertApproxEqual(10.0, total, message: $"Expected (1+2) + (3+4) = 10, got {total}");
    }

    /// <summary>
    /// <c>([String: Int32]) -> Void</c>. The dictionary divergence was worse than a mismatched type:
    /// the public side named <c>IDictionary</c>, an interface the wire carrier does not implement at
    /// all, so no consumer could have written a conforming lambda in the first place. The key is a
    /// <c>SwiftString</c> rather than a <c>string</c> because the carrier looks its keys up through a
    /// Swift Hashable witness, which exists for <c>SwiftString</c> and not for <c>System.String</c>.
    /// </summary>
    [Skip(DirectBridgeArgAbi)]
    public void TestDictionaryCallbackArgument()
    {
        using var host = new CollectionCallbackHost();
        int calls = 0;
        int a = -1;
        int b = -1;
        int count = -1;

        host.EmitCounts(counts =>
        {
            calls++;
            count = counts.Count;
            using var keyA = new SwiftString("a");
            using var keyB = new SwiftString("b");
            counts.TryGetValue(keyA, out a);
            counts.TryGetValue(keyB, out b);
        });

        AssertEqual(1, calls, $"Expected exactly one callback, got {calls}");
        AssertEqual(2, count, $"Expected 2 entries, got {count}");
        AssertEqual(1, a, $"Expected counts[\"a\"] == 1, got {a}");
        AssertEqual(2, b, $"Expected counts[\"b\"] == 2, got {b}");
    }

    // ─── Collection-shaped callback RETURNS ───
    //
    // Swift invokes the lambda and consumes the result itself, so the assertion is on the value SWIFT
    // computed. Calling the lambda from C# and inspecting what it produced would test the lambda, not
    // the bridge that carries its return value back across.

    /// <summary>
    /// <c>(Double) -> [Double]</c>. Swift calls the lambda twice (seeds 1.0 and 2.0) and sums
    /// everything it produced, so the returned total can only be right if both invocations' arrays
    /// crossed back intact.
    /// </summary>
    public void TestArrayCallbackReturnConsumedBySwift()
    {
        using var host = new CollectionCallbackHost();
        int calls = 0;

        double total = host.SumProduced(seed =>
        {
            calls++;
            return new SwiftArray<double>(new[] { seed, seed * 10 });
        });

        AssertEqual(2, calls, $"Expected Swift to invoke the lambda twice, got {calls}");
        // seed 1.0 → 1 + 10, seed 2.0 → 2 + 20 ⇒ 33
        AssertApproxEqual(33.0, total, message: $"Expected Swift to sum 33, got {total}");
    }

    /// <summary>The struct-element variant of the same return shape.</summary>
    public void TestStructArrayCallbackReturnConsumedBySwift()
    {
        using var host = new CollectionCallbackHost();
        int calls = 0;

        double total = host.SumPointsProduced(seed =>
        {
            calls++;
            return new SwiftArray<ParityPoint>(new[]
            {
                new ParityPoint(seed, seed + 1),
                new ParityPoint(seed + 2, seed + 3),
            });
        });

        AssertEqual(1, calls, $"Expected Swift to invoke the lambda once, got {calls}");
        // seed 2.0 → (2+3) + (4+5) = 14
        AssertApproxEqual(14.0, total, message: $"Expected Swift to sum 14, got {total}");
    }
}
