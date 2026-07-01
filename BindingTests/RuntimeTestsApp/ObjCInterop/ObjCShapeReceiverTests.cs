// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.ObjCInterop;

/// <summary>
/// End-to-end gate for an @objc class-bound protocol existential carried as a member of a
/// REVERSE-DISPATCH receiver — a C# class implementing a Swift protocol that Swift then
/// dispatches back into. The free-function fixtures in <see cref="ObjCClassBoundExistentialTests"/>
/// never reach the receiver element-conversion path; these do.
///
/// An @objc existential marshals as a single bare Objective-C object pointer (no Swift
/// witness-table word). The receiver thunks must get its ARC ownership right per direction:
///   • didReceive(shape:) — Swift hands the C# method a +0 BORROWED pointer. The proxy wraps
///     it with ownsContainer:false so the borrow is NOT adopted (adopting would run an
///     unknown-object release on storage Swift still owns → over-release/UAF).
///   • currentShape — the C# getter hands Swift a +1 OWNED pointer, minted through the owned
///     class carrier (Arc.UnknownObjectRetain) and adopted by Swift on load.
/// Repeat calls confirm the borrow was not over-released and each owned read mints an
/// independent +1 without disturbing the C# owner.
/// </summary>
public class ObjCShapeReceiverTests : TestBase
{
    public ObjCShapeReceiverTests(TestResults results) : base(results) { }

    /// <summary>
    /// Swift → C#: a +0 borrowed @objc existential parameter delivered into a C# receiver
    /// method. The second delivery exercises the proxy cache and confirms the first borrow
    /// was not adopted/over-released.
    /// </summary>
    public void TestReceiverParamBorrowsObjCExistential()
    {
        var receiver = new TrackedShapeReceiver(TestLibFunctions.MakeObjCShape(7));

        TestLibFunctions.FireObjCShapeReceiver(receiver, 42);
        AssertEqual(42, receiver.LastReceivedTag,
            "Swift delivered the @objc existential parameter into the C# receiver (+0 borrow)");

        TestLibFunctions.FireObjCShapeReceiver(receiver, 43);
        AssertEqual(43, receiver.LastReceivedTag,
            "second delivery still reads the borrowed conformer's witness (borrow not over-released)");
    }

    /// <summary>
    /// C# → Swift: a +1 owned @objc existential read back out of a C# receiver's getter and
    /// dispatched on. A repeat read mints an independent +1; the C# owner (CurrentShape)
    /// survives both reads.
    /// </summary>
    public void TestReceiverGetterOwnsObjCExistential()
    {
        var receiver = new TrackedShapeReceiver(TestLibFunctions.MakeObjCShape(7));

        AssertEqual(7, TestLibFunctions.ReadObjCShapeReceiverCurrentTag(receiver),
            "Swift read the @objc existential out of the C# getter and dispatched .tag (+1 owned)");

        AssertEqual(7, TestLibFunctions.ReadObjCShapeReceiverCurrentTag(receiver),
            "repeat read mints an independent +1 and still round-trips the conformer identity");
    }

    /// <summary>
    /// Leak probe for the OWNED getter-return direction (Codex review Low: the round-trip test
    /// detects premature release but not an unbalanced retain). The getter mints a +1 owned bare
    /// ObjC pointer per read (Arc.UnknownObjectRetain) which Swift must consume/release on load.
    /// A lifetime-tracked conformer makes a per-read over-retain observable: after the loop, the
    /// C# owner (receiver + its shape wrapper) is dropped and the GC drained, so a balanced path
    /// deallocs the conformer (live == 0). A leaked +1 per read pins it — live stays non-zero.
    /// This is the deterministic sibling to the existing round-trip test, not a retainCount hack.
    /// </summary>
    public void TestReceiverGetterOwnedExistentialDoesNotLeak()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        ExerciseOwnedGetter(32);

        DrainFinalizers();
        LifetimeTracker.AssertLiveCount(0,
            "owned @objc existential getter-return minted a +1 per read that Swift must release; " +
            "a leaked retain would pin the tracked conformer past the owner's disposal");

        TestLogger.Info("owned @objc existential getter-return: 32 reads left zero tracked conformers pinned");
    }

    /// <summary>
    /// Creates a receiver over a lifetime-tracked conformer and drives the owned getter-return
    /// path <paramref name="reads"/> times. Kept out-of-line so the receiver and its shape wrapper
    /// are unrooted on return, letting the subsequent GC drain finalize them.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ExerciseOwnedGetter(int reads)
    {
        var receiver = new TrackedShapeReceiver(TestLibFunctions.MakeTrackedObjCShape(7));
        for (int i = 0; i < reads; i++)
        {
            int tag = TestLibFunctions.ReadObjCShapeReceiverCurrentTag(receiver);
            if (tag != 7)
                throw new Exception($"owned getter read {i} returned {tag}, expected 7");
        }
        GC.KeepAlive(receiver);
    }

    private static void DrainFinalizers()
    {
        for (int i = 0; i < 4; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        GC.Collect();
    }

    /// <summary>
    /// C# receiver implementing the Swift protocol. Its <c>CurrentShape</c> is a Swift-vended
    /// @objc conformer (a real ObjC object), so the getter hands Swift a genuine bare pointer.
    /// </summary>
    private sealed class TrackedShapeReceiver : IObjCShapeReceiver
    {
        public int LastReceivedTag = -999;
        public IObjCClassBoundShape CurrentShape { get; }
        public TrackedShapeReceiver(IObjCClassBoundShape shape) => CurrentShape = shape;
        public void DidReceive(IObjCClassBoundShape shape) => LastReceivedTag = shape.Tag;
    }
}
