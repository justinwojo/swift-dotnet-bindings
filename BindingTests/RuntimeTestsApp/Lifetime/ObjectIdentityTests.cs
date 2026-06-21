// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Generic;
using RuntimeTestsApp.Infrastructure;
using Swift;
using Swift.Runtime;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Lifetime;

/// <summary>
/// F34 (deliverable A): a non-<c>Equatable</c>, heap-backed Swift class projects OBJECT IDENTITY
/// into C# — two distinct C# wrappers over the SAME Swift instance compare equal and hash alike,
/// while wrappers over distinct instances do not, even with identical field values. The equality
/// is keyed on the live Swift handle, guarded so a disposed (zero-handle) wrapper falls back to
/// reference identity rather than colliding every dead wrapper onto <c>IntPtr.Zero</c>.
///
/// <para>This is the end-to-end runtime gate for the emitted handle-identity
/// <c>Equals</c>/<c>GetHashCode</c> (the emitted SHAPE is pinned by the unit-level
/// <c>ClassIdentityEmitterTests</c>). It uses <c>identity()</c> — which returns the same Swift
/// instance — to obtain two wrappers over one object, and <c>clone()</c> for a genuinely distinct
/// instance. Runs on both Simulator (Mono) and device (NativeAOT); the guard reads the stored
/// pointer only and never dereferences a freed object.</para>
/// </summary>
public class ObjectIdentityTests : TestBase
{
    public ObjectIdentityTests(TestResults results) : base(results) { }

    public void TestSameInstanceWrappersAreEqual()
    {
        var obj = TestLibFunctions.CreateTrackedObject(1);
        // identity() returns the SAME Swift instance as a fresh, distinct C# wrapper.
        var same = TestLibFunctions.Identity(obj);

        AssertFalse(ReferenceEquals(obj, same), "identity() yields a distinct C# wrapper object");
        AssertTrue(obj.Equals(same), "two wrappers over the same Swift instance are equal (handle identity)");
        AssertTrue(same.Equals(obj), "handle identity is symmetric");
        AssertEqual(obj.GetHashCode(), same.GetHashCode(), "same Swift instance hashes alike");

        same.Dispose();
        obj.Dispose();
        TestLogger.Info("Same-instance non-Equatable wrappers compare equal and hash alike");
    }

    public void TestDistinctInstancesAreNotEqual()
    {
        var obj = TestLibFunctions.CreateTrackedObject(7);
        // clone() returns a NEW Swift instance copying the source's fields.
        var other = TestLibFunctions.Clone(obj);

        AssertFalse(obj.Equals(other), "distinct Swift instances are not equal under handle identity");

        other.Dispose();
        obj.Dispose();
        TestLogger.Info("Distinct non-Equatable instances are not equal");
    }

    public void TestHashSetMembershipBySwiftIdentity()
    {
        var obj = TestLibFunctions.CreateTrackedObject(3);
        var same = TestLibFunctions.Identity(obj);
        var distinct = TestLibFunctions.Clone(obj);

        var set = new HashSet<TrackedObject> { obj };
        AssertTrue(set.Contains(same), "HashSet finds a different wrapper over the same Swift instance");
        AssertFalse(set.Contains(distinct), "HashSet does not match a distinct Swift instance");

        same.Dispose();
        distinct.Dispose();
        obj.Dispose();
        TestLogger.Info("HashSet membership honors Swift object identity");
    }

    public void TestDisposedWrapperFallsBackToReferenceEquals()
    {
        var obj = TestLibFunctions.CreateTrackedObject(5);
        var same = TestLibFunctions.Identity(obj);

        // Live, same-instance wrappers are equal by handle identity.
        AssertTrue(obj.Equals(same), "live same-instance wrappers equal before dispose");

        // Disposing one wrapper zeroes ITS handle (obj still owns the live instance). Equality must
        // fall back to reference identity, not collapse the disposed wrapper onto the live peer.
        same.Dispose();
        AssertFalse(same.Equals(obj), "disposed wrapper is not equal to its live peer (reference fallback)");
        AssertFalse(obj.Equals(same), "a live wrapper is not equal to a disposed peer");
        AssertTrue(same.Equals(same), "a disposed wrapper still equals itself (reference identity)");

        // GetHashCode on a disposed wrapper does not throw (defers to the object-identity hash).
        var hash = same.GetHashCode();
        AssertTrue(hash != 0 || hash == 0, "disposed wrapper GetHashCode does not throw");

        obj.Dispose();
        TestLogger.Info("Disposed non-Equatable wrapper falls back to reference identity");
    }

    public void TestHashCodeStableAcrossDispose()
    {
        var obj = TestLibFunctions.CreateTrackedObject(11);

        // Capture the live (handle-based) hash and add to a set while live.
        var liveHash = obj.GetHashCode();
        var set = new HashSet<TrackedObject> { obj };

        // Disposing zeroes the handle. The identity hash must NOT change — .NET requires a key's hash
        // to stay stable while it is in a collection. The cached identity hash keeps the disposed
        // wrapper findable; without caching it would switch to the reference-identity hash and miss.
        obj.Dispose();

        AssertEqual(liveHash, obj.GetHashCode(), "GetHashCode is stable across Dispose (hash-key immutability)");
        AssertTrue(set.Contains(obj), "a disposed wrapper remains findable in a HashSet it was added to while live");
        TestLogger.Info("Identity hash is immutable across Dispose");
    }

    public void TestTwoDisposedWrappersAreNotEqual()
    {
        var a = TestLibFunctions.CreateTrackedObject(8);
        var b = TestLibFunctions.CreateTrackedObject(9);

        // After disposal both wrappers carry a zero handle — they must NOT collide on IntPtr.Zero.
        a.Dispose();
        b.Dispose();

        AssertFalse(a.Equals(b), "two distinct disposed wrappers are not equal (no IntPtr.Zero collision)");
        AssertTrue(a.Equals(a), "a disposed wrapper equals itself");
        TestLogger.Info("Distinct disposed wrappers do not collide on the zero handle");
    }
}
