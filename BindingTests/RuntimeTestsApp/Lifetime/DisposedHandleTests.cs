// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Lifetime;

/// <summary>
/// A disposed class wrapper reaching Swift. Its handle reads zero, which used to cross into Swift
/// as a null object: equality and hashing dereferenced it inside the Swift witness, an
/// NSObject-rooted receiver or argument crossed as null, and a disposed optional argument became
/// <c>nil</c>. Equality now answers by reference identity, hashing by identity, and every call
/// that hands Swift the object throws <see cref="ObjectDisposedException"/> before Swift runs.
/// Fixture: Lifetime/DisposedHandleFixtures.swift.
/// </summary>
public class DisposedHandleTests : TestBase
{
    public DisposedHandleTests(TestResults results) : base(results) { }

    public void TestSwiftClassEqualityWithDisposedOperand()
    {
        using var live = new DisposalProbe(1);
        var disposed = new DisposalProbe(1);
        AssertTrue(live.Equals(disposed), "Control: equal live values compare equal through Swift");
        disposed.Dispose();

        AssertFalse(live.Equals(disposed), "A disposed operand is not equal to a live value");
        AssertFalse(disposed.Equals(live), "Equality with a disposed receiver is not equal either");
        AssertFalse(((object)live).Equals(disposed), "Equals(object) agrees");
        AssertTrue(disposed.Equals(disposed), "A disposed wrapper still equals itself");
        AssertFalse(live == disposed, "== falls back to identity");
        AssertTrue(live != disposed, "!= falls back to identity");
        var sameWrapper = disposed;
        AssertTrue(disposed == sameWrapper, "== on one disposed wrapper is identity");
        AssertFalse(disposed != sameWrapper, "!= on one disposed wrapper is identity");
    }

    public void TestSwiftClassHashOfDisposedWrapperIsStable()
    {
        var disposed = new DisposalProbe(2);
        disposed.Dispose();
        int first = disposed.GetHashCode();
        AssertEqual(first, disposed.GetHashCode(), "A disposed wrapper hashes the same each time");
        using var live = new DisposalProbe(2);
        var set = new HashSet<DisposalProbe> { live, disposed };
        AssertEqual(2, set.Count, "A disposed wrapper is its own HashSet entry");
        AssertTrue(set.Contains(disposed), "A disposed wrapper is found again by identity");
    }

    public void TestSwiftClassDisposedArgumentsAreRefused()
    {
        using var live = new DisposalProbe(3);
        var disposed = new DisposalProbe(4);
        disposed.Dispose();

        AssertThrows<ObjectDisposedException>(() => disposed.ReadTag(), "A disposed receiver is refused");
        AssertThrows<ObjectDisposedException>(() => live.TagOf(disposed), "A disposed argument is refused");
        AssertThrows<ObjectDisposedException>(() => live.TagOrMinusOne(disposed), "A disposed optional argument is refused, not read as nil");
        AssertThrows<ObjectDisposedException>(() => TestLibFunctions.DisposalProbeTag(disposed), "A disposed free-function argument is refused");
        AssertThrows<ObjectDisposedException>(() => TestLibFunctions.DisposalProbeTagOrMinusOne(disposed), "A disposed optional free-function argument is refused");
        AssertEqual(-1, live.TagOrMinusOne(null), "Control: null is nil");
        AssertEqual(-1, TestLibFunctions.DisposalProbeTagOrMinusOne(null), "Control: null is nil");
        AssertEqual(3, live.TagOrMinusOne(live), "Control: a live optional argument");
    }

    public void TestObjCRootedEqualityWithDisposedOperand()
    {
        using var live = new DisposalObjCProbe(5);
        var disposed = new DisposalObjCProbe(5);
        AssertTrue(live == disposed, "Control: equal live values compare equal through Swift's ==");
        disposed.Dispose();

        AssertFalse(live == disposed, "== falls back to identity");
        AssertTrue(live != disposed, "!= falls back to identity");
        var sameWrapper = disposed;
        AssertTrue(disposed == sameWrapper, "== on one disposed wrapper is identity");
        AssertFalse(live.Equals(disposed), "Equals does not report a disposed wrapper equal to a live one");
        AssertEqual(disposed.GetHashCode(), disposed.GetHashCode(), "A disposed wrapper hashes the same each time");
    }

    public void TestObjCRootedDisposedArgumentsAreRefused()
    {
        using var live = new DisposalObjCProbe(6);
        var disposed = new DisposalObjCProbe(7);
        disposed.Dispose();

        AssertThrows<ObjectDisposedException>(() => disposed.ReadTag(), "A disposed NSObject-rooted receiver is refused");
        AssertThrows<ObjectDisposedException>(() => live.TagOf(disposed), "A disposed NSObject-rooted argument is refused");
        AssertThrows<ObjectDisposedException>(() => live.TagOrMinusOne(disposed), "A disposed optional NSObject-rooted argument is refused, not read as nil");
        AssertThrows<ObjectDisposedException>(() => TestLibFunctions.DisposalObjCProbeTag(disposed), "A disposed free-function argument is refused");
        AssertThrows<ObjectDisposedException>(() => TestLibFunctions.DisposalObjCProbeTagOrMinusOne(disposed), "A disposed optional free-function argument is refused");
        AssertEqual(-1, live.TagOrMinusOne(null), "Control: null is nil");
        AssertEqual(6, live.TagOrMinusOne(live), "Control: a live optional argument");
        AssertEqual(6, live.ReadTag(), "Control: a live receiver");
    }
}
