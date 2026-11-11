// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift.Runtime;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Operators;

/// <summary>
/// Class operators that take an operand <c>consuming</c> or are declared <c>throws</c>. A class
/// operator is a static member of the class and reaches Swift through the same native thunk as the
/// class's other static members, which is ownership-transparent: a consuming operand has to be
/// handed across at +1 exactly as on an ordinary member, and a thrown error has to be checked and
/// released before the result register is read.
///
/// <para>The deinit counter is the deterministic signal for the ownership half: an operand passed
/// borrowed to a callee that consumes it is destroyed during the call, while the caller still owns
/// it. The operands and the thrown error each hold a <see cref="LifetimeTracker"/>-counted ref, so a
/// lost release (an error box nobody frees, a hand-over that never completes) keeps the live count
/// up and an over-release traps in the Swift runtime.</para>
/// </summary>
public class ClassOperatorOwnershipTests : TestBase
{
    public ClassOperatorOwnershipTests(TestResults results) : base(results) { }

    #region Consuming operands

    public void TestConsumingOperandStaysOwnedByTheCaller()
    {
        LifetimeTracker.RunWithLeakCheck(() => RunOnFinishedThread(() =>
        {
            int start = TestLibFunctions.GetOperatorTokenDeinitCount();
            var lhs = new OperatorToken(30);
            var rhs = new OperatorToken(12);
            try
            {
                AssertEqual(42, lhs + rhs, "consuming operator returns the sum");
                AssertEqual(start, TestLibFunctions.GetOperatorTokenDeinitCount(),
                    "the operator must not destroy an operand the caller still owns");
                AssertEqual(30, lhs.Value, "the consumed operand remains readable after the call");
                AssertEqual(42, lhs + rhs, "the consumed operand can be passed again");
                AssertEqual(start, TestLibFunctions.GetOperatorTokenDeinitCount(),
                    "repeated consuming calls still leave the operand to the caller");
            }
            finally
            {
                lhs.Dispose();
                rhs.Dispose();
            }
            AssertEqual(start + 2, TestLibFunctions.GetOperatorTokenDeinitCount(),
                "each operand deinitializes exactly once, at the caller's Dispose");
        }), "consuming class operator, scalar result");
    }

    public void TestConsumingOperandOnClassReturningOperator()
    {
        LifetimeTracker.RunWithLeakCheck(() => RunOnFinishedThread(() =>
        {
            int start = TestLibFunctions.GetOperatorTokenDeinitCount();
            var lhs = new OperatorToken(50);
            var rhs = new OperatorToken(8);
            try
            {
                using (var result = lhs - rhs)
                {
                    AssertEqual(42, result.Value, "consuming class-returning operator builds the difference");
                    AssertEqual(start, TestLibFunctions.GetOperatorTokenDeinitCount(),
                        "the operator must not destroy an operand the caller still owns");
                    AssertEqual(50, lhs.Value, "the consumed operand remains readable after the call");
                }
                AssertEqual(start + 1, TestLibFunctions.GetOperatorTokenDeinitCount(),
                    "disposing the result releases only the result");
            }
            finally
            {
                lhs.Dispose();
                rhs.Dispose();
            }
            AssertEqual(start + 3, TestLibFunctions.GetOperatorTokenDeinitCount(),
                "the operands and the result each deinitialize exactly once");
        }), "consuming class operator, class result");
    }

    public void TestConsumingOperandSurvivesARejectedCall()
    {
        // A borrowed operand that is already disposed fails the call before native entry. The +1
        // taken for the consuming operand must be rolled back, not stranded or handed over.
        LifetimeTracker.RunWithLeakCheck(() => RunOnFinishedThread(() =>
        {
            int start = TestLibFunctions.GetOperatorTokenDeinitCount();
            var lhs = new OperatorToken(5);
            var rhs = new OperatorToken(6);
            try
            {
                rhs.Dispose();
                AssertEqual(start + 1, TestLibFunctions.GetOperatorTokenDeinitCount(), "only the disposed operand deinitialized");
                AssertThrows<ObjectDisposedException>(() => _ = lhs + rhs,
                    "a disposed operand is rejected before the operator runs");
                AssertEqual(start + 1, TestLibFunctions.GetOperatorTokenDeinitCount(),
                    "the rejected call left the consuming operand alone");
                AssertEqual(5, lhs.Value, "the consuming operand remains readable after the rejected call");
            }
            finally
            {
                lhs.Dispose();
            }
            AssertEqual(start + 2, TestLibFunctions.GetOperatorTokenDeinitCount(),
                "the consuming operand deinitializes exactly once, at the caller's Dispose");
        }), "consuming class operator, rejected call");
    }

    public void TestConsumingObjCRootedOperandStaysOwnedByTheCaller()
    {
        // An NSObject-rooted operand crosses as its object handle. Handed over at +0, the first call
        // would release the caller's only count and deinitialize the operand mid-call.
        int start = TestLibFunctions.GetObjCOperatorTokenDeinitCount();
        using var lhs = new ObjCOperatorToken(30);
        using var rhs = new ObjCOperatorToken(12);
        for (int i = 0; i < 3; i++)
            AssertEqual(42, lhs + rhs, "consuming ObjC-rooted operator returns the sum");
        AssertEqual(start, TestLibFunctions.GetObjCOperatorTokenDeinitCount(),
            "the operator must not destroy an operand the caller still owns");
        AssertEqual(30, lhs.Value, "the consumed operand remains readable after the calls");
    }

    #endregion

    #region Throwing operators

    public void TestThrowingScalarOperatorReturnsOnSuccess()
    {
        using var lhs = new OperatorToken(6);
        using var rhs = new OperatorToken(7);
        AssertEqual(42, lhs * rhs, "throwing operator returns its result when it does not throw");
    }

    public void TestThrowingScalarOperatorSurfacesTheSwiftError()
    {
        using var lhs = new OperatorToken(6);
        using var rhs = new OperatorToken(-3);
        var expectedType = OrdinaryMemberExceptionType();

        var caught = CatchSwiftException(() => _ = lhs * rhs, "throwing scalar operator");
        AssertTrue(caught.GetType() == expectedType,
            $"operator surfaces the same exception type an ordinary throwing member does: expected {expectedType.Name}, got {caught.GetType().Name}");
        var typed = (SwiftException<OperatorTokenFailure>)caught;
        using var payload = typed.Error;
        AssertNotNull(payload, "the thrown error is marshalled");
        AssertEqual(-3, payload!.Code, "the payload carries the thrown value");
    }

    public void TestThrowingClassOperatorReturnsOnSuccess()
    {
        using var lhs = new OperatorToken(84);
        using var rhs = new OperatorToken(2);
        using var result = lhs / rhs;
        AssertEqual(42, result.Value, "throwing class-returning operator returns its result when it does not throw");
    }

    public void TestThrowingClassOperatorSurfacesTheSwiftError()
    {
        using var lhs = new OperatorToken(84);
        using var rhs = new OperatorToken(0);
        var expectedType = OrdinaryMemberExceptionType();

        var caught = CatchSwiftException(() => { using var _ = lhs / rhs; }, "throwing class-returning operator");
        AssertTrue(caught.GetType() == expectedType,
            $"operator surfaces the same exception type an ordinary throwing member does: expected {expectedType.Name}, got {caught.GetType().Name}");
        var typed = (SwiftException<OperatorTokenFailure>)caught;
        using var payload = typed.Error;
        AssertNotNull(payload, "the thrown error is marshalled");
        AssertEqual(0, payload!.Code, "the payload carries the thrown value");
    }

    public void TestThrowingOperatorsReleaseTheErrorBoxExactlyOnce()
    {
        // The whole churn runs on a thread that has exited before collection, so a conservative
        // stack slot cannot pin an exception (which owns its error box) and fake a leak.
        LifetimeTracker.RunWithLeakCheck(() => RunOnFinishedThread(() =>
        {
            using var lhs = new OperatorToken(9);
            using var negative = new OperatorToken(-1);
            using var zero = new OperatorToken(0);
            for (int i = 0; i < 10; i++)
            {
                DisposePayload(CatchSwiftException(() => _ = lhs * negative, "throwing scalar operator"));
                DisposePayload(CatchSwiftException(() => { using var _ = lhs / zero; }, "throwing class-returning operator"));
            }
        }), "throwing class operators");
        var stats = LifetimeTracker.GetStats();
        AssertEqual(23, stats.allocations, "three operands and twenty thrown errors allocated a tracked ref");
        AssertEqual(23, stats.deallocations, "every operand and error box was released exactly once");
    }

    #endregion

    // The exception type an ordinary throwing static member of the same class produces for the same
    // Swift error — the contract an operator has to match.
    private static System.Type OrdinaryMemberExceptionType()
    {
        using var lhs = new OperatorToken(1);
        using var zero = new OperatorToken(0);
        try
        {
            using var _ = OperatorToken.CheckedDivide(lhs, zero);
        }
        catch (SwiftException ex)
        {
            if (ex is SwiftException<OperatorTokenFailure> typed)
                typed.Error?.Dispose();
            return ex.GetType();
        }
        throw new AssertionException("CheckedDivide should have thrown");
    }

    // Returns the exception with its typed payload still live; the caller disposes it.
    private static SwiftException CatchSwiftException(Action action, string context)
    {
        try
        {
            action();
        }
        catch (SwiftException ex)
        {
            return ex;
        }
        throw new AssertionException($"{context}: expected a SwiftException, but the call returned");
    }

    private static void DisposePayload(SwiftException ex)
    {
        if (ex is SwiftException<OperatorTokenFailure> typed)
            typed.Error?.Dispose();
    }
}
