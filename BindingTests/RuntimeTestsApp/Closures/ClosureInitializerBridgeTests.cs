// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Closures;

/// <summary>
/// Runtime tests for callback-bearing closures in INITIALIZER position. The nested-closure
/// bridge already carried this closure shape on an ordinary method, but every closure bridge
/// refused constructors — so a type whose only initializer took such a closure bound as a shell:
/// present in the C# surface, impossible to construct, taking every member that needs it along.
///
/// These pin the recovery end to end: the initializer is a real C# constructor, the outer closure
/// fires with the caller's arguments, the inner completion carries a value back into Swift, and
/// the constructed instance is a live object the rest of the surface can consume.
/// </summary>
public class ClosureInitializerBridgeTests : TestBase
{
    public ClosureInitializerBridgeTests(TestResults results) : base(results) { }

    public void TestEscapingInitClosureRoundTrips()
    {
        int seenAmount = -1;
        var config = new DeferredIntentConfiguration(3, (amount, complete) =>
        {
            seenAmount = amount;
            complete(amount * 2);
        });

        AssertEqual(3, config.Mode, "constructed configuration carries its non-closure arg");

        int result = config.Confirm(21);
        AssertEqual(21, seenAmount, "outer closure received the Swift-side argument");
        AssertEqual(42, result, "inner completion carried the value back into Swift");
    }

    public void TestEscapingInitClosureSurvivesConstructor()
    {
        // The handler is STORED by the initializer, so its GCHandle context must outlive the
        // constructor call rather than being freed when the P/Invoke returns.
        int calls = 0;
        var config = new DeferredIntentConfiguration(1, (amount, complete) =>
        {
            calls++;
            complete(amount + 1);
        });

        AssertEqual(12, config.ConfirmTwice(5), "handler invoked twice after the constructor returned");
        AssertEqual(2, calls, "outer closure fired once per Swift-side call");
    }

    public void TestConstructedInstanceFlowsIntoDownstreamType()
    {
        // The half of the reported shape that made the type graph unreachable: a downstream type
        // that takes the previously-unconstructible one as a plain constructor argument.
        var config = new DeferredIntentConfiguration(9, (amount, complete) => complete(amount - 1));
        var controller = new DeferredIntentController(config);

        // `configuredMode()` takes no arguments and reads as a noun, so it emits with the
        // generator's `Get` prefix.
        AssertEqual(9, controller.GetConfiguredMode(), "downstream type read through the constructed instance");
        AssertEqual(99, controller.Run(100), "downstream call drove the closure round-trip");
    }

    public void TestNonEscapingInitClosureRunsDuringConstruction()
    {
        // The non-escaping branch: the closure is invoked inside the initializer and never
        // stored, so the GCHandle is freed unconditionally on return.
        bool fired = false;
        var config = new ImmediateConfirmationConfiguration(4, (seed, complete) =>
        {
            fired = true;
            complete(seed * 10);
        });

        AssertTrue(fired, "non-escaping outer closure ran during construction");
        AssertEqual(40, config.Resolved, "value the inner completion supplied was stored by the initializer");
    }

    public void TestConstructedInstancesAreIndependent()
    {
        var first = new DeferredIntentConfiguration(1, (amount, complete) => complete(amount));
        var second = new DeferredIntentConfiguration(2, (amount, complete) => complete(amount * 100));

        AssertEqual(1, first.Mode, "first instance kept its own state");
        AssertEqual(2, second.Mode, "second instance kept its own state");
        AssertEqual(5, first.Confirm(5), "first instance dispatched to its own handler");
        AssertEqual(500, second.Confirm(5), "second instance dispatched to its own handler");
    }
}
