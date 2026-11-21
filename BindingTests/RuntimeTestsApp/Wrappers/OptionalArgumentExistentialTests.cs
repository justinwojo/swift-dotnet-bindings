// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift.Foundation;
using Swift.Runtime;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Wrappers;

/// <summary>
/// Existentials through the optional-argument wrapper (<c>_optbuf</c>), which keeps the Swift
/// convention whenever an existential is in the signature. A boxed <c>any Error</c> is one reference
/// and a single class-bound protocol a two-word pair, both in registers; an import that declared the
/// opaque five-word container instead read its result from an indirect-result buffer the callee never
/// writes (an uninitialized <c>AnyError</c>) and handed the callee the address of a managed copy where
/// it reads the box. Each test reads the value back through Swift, so a wrong carrier shows up as a
/// wrong tag, not only as a crash, and the error tests count the tracked instances so the +1 a
/// returned error carries is released and a borrowed argument is not released for the callee.
/// </summary>
public class OptionalArgumentExistentialTests : TestBase
{
    public OptionalArgumentExistentialTests(TestResults results) : base(results) { }

    private static void DrainFinalizers()
    {
        for (int i = 0; i < 4; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        GC.Collect();
    }

    public void TestInstanceMethodReturnsTheErrorItMade()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        LifetimeTracker.RunIsolated(() =>
        {
            var factory = new OptionalArgumentErrorFactory();
            using var plain = factory.Error(7, null, null);
            using var withMessage = factory.Error(7, "abc", null);
            using var wrapped = factory.Error(100, "x", plain);

            // Read back through Swift: the tag proves the box is the one Swift returned.
            AssertEqual(7, factory.Code(plain, null), "error(code:message:underlying:) with no optionals");
            AssertEqual(10, factory.Code(withMessage, "ignored"), "error(code:message:underlying:) with a message");
            AssertEqual(108, factory.Code(wrapped, null), "error(code:message:underlying:) with an underlying error");
        });
        DrainFinalizers();

        LifetimeTracker.AssertNoLeaks("an `any Error` returned through the optional-argument wrapper must be released once");
        TestLogger.Info("instance optbuf `any Error` results round-trip and release");
    }

    public void TestStaticMethodReturnsTheErrorItMade()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        LifetimeTracker.RunIsolated(() =>
        {
            var factory = new OptionalArgumentErrorFactory();
            using var underlying = factory.Error(1000, null, null);
            for (int i = 0; i < 50; i++)
            {
                using var error = OptionalArgumentErrorFactory.MakeError(i, i % 2 == 0 ? null : "ab", i % 3 == 0 ? underlying : null);
                AssertEqual((i % 2 == 0 ? i : i + 2) + (i % 3 == 0 ? 1000 : 0), factory.Code(error, null, null), $"makeError(code: {i})");
            }
        });
        DrainFinalizers();

        LifetimeTracker.AssertNoLeaks("a static optbuf `any Error` result must be released once");
    }

    public void TestBorrowedErrorArgumentIsNotReleasedForTheCallee()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        LifetimeTracker.RunIsolated(() =>
        {
            var factory = new OptionalArgumentErrorFactory();
            using var error = factory.Error(42, null, null);
            using var underlying = factory.Error(3, null, null);
            // Many borrowed passes of one box: a release on the callee's behalf would free it early.
            // The two-argument read goes through a C-convention wrapper, the three-argument one keeps
            // the Swift convention, where the box travels as itself.
            for (int i = 0; i < 100; i++)
            {
                AssertEqual(42, factory.Code(error, i % 2 == 0 ? null : "fallback"), $"code(of:fallback:) pass {i}");
                AssertEqual(42, factory.Code(error, i % 2 == 0 ? null : "fallback", null), $"code(of:fallback:underlying:) pass {i}");
                AssertEqual(45, factory.Code(error, null, underlying), $"code(of:fallback:underlying:) with an underlying error, pass {i}");
            }
            LifetimeTracker.AssertLiveCount(2, "the borrowed errors are still alive while C# owns them");
        });
        DrainFinalizers();

        LifetimeTracker.AssertNoLeaks("a borrowed `any Error` argument must leave its owner's +1 alone");
    }

    public void TestStructMethodReturnsTheErrorItMade()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        LifetimeTracker.RunIsolated(() =>
        {
            var maker = new OptionalArgumentErrorMaker(1000);
            var factory = new OptionalArgumentErrorFactory();
            using var inner = maker.Make(null, null);
            using var outer = maker.Make("abcd", inner);

            AssertEqual(1000, factory.Code(inner, null), "struct make(message:underlying:) with no optionals");
            AssertEqual(2004, factory.Code(outer, null), "struct make(message:underlying:) with both");
        });
        DrainFinalizers();

        LifetimeTracker.AssertNoLeaks("a struct optbuf `any Error` result must be released once");
    }

    // The planner cannot yet find the class-bound protocol, so these members have no bridge plan (on
    // any platform, routed or not) and refuse before Swift runs on the engines that mishandle a
    // signature it did not lower.
    private const SwiftTransitionFacts ClassBoundShapes = SwiftTransitionFacts.LoweredSignature;

    /// <summary>
    /// Runs <paramref name="call"/>, which must work wherever the engine runs its direct transition,
    /// and must refuse before Swift runs everywhere else.
    /// </summary>
    private void Run(string member, Action call)
    {
        if (SwiftTransitionGuard.IsSupported(ClassBoundShapes))
        {
            call();
            return;
        }
        SwiftTransitionUnsupportedException? refusal = null;
        try
        {
            call();
        }
        catch (SwiftTransitionUnsupportedException ex)
        {
            refusal = ex;
        }
        AssertNotNull(refusal, $"{member} must refuse on an engine that mishandles its direct transition");
        AssertTrue(refusal!.Member.Contains(member, StringComparison.Ordinal), $"refusal names the member: {refusal.Member}");
    }

    public void TestClassBoundExistentialResultIsThePair()
    {
        Run("MakeOptionalArgumentTagged", () =>
        {
            var tagged = TestLibFunctions.MakeOptionalArgumentTagged(5, "abc", null);
            AssertEqual(8, tagged.Tag, "the class-bound existential Swift returned");
        });
    }

    public void TestClassBoundExistentialArgumentIsThePair()
    {
        using var box = new OptionalArgumentTaggedBox(8);
        Run("OptionalArgumentTagValue", () =>
        {
            AssertEqual(8, TestLibFunctions.OptionalArgumentTagValue(box, null, null), "the class-bound existential passed in");
            AssertEqual(10, TestLibFunctions.OptionalArgumentTagValue(box, "ab", null), "the class-bound existential passed in with a label");
        });
        AssertEqual(8, box.Tag, "the box outlives the borrowed passes");
    }
}
