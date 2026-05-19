// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.KeyPath;

/// <summary>
/// End-to-end opaque pass-through tests for the five-class Swift KeyPath family
/// (AnyKeyPath, PartialKeyPath, KeyPath, WritableKeyPath, ReferenceWritableKeyPath).
///
/// <para>What this exercises:</para>
/// <list type="bullet">
///   <item>OUT path — Swift factory returns +1 retained class pointer; C# SafeHandle adopts.</item>
///   <item>IN path — C# borrows handle to Swift; read via <c>subscript(keyPath:)</c>.</item>
///   <item>Optional and array composition: <c>KeyPath?</c> and <c>[KeyPath]</c>.</item>
///   <item>Round-trip identity — value-equality (AnyKeyPath.==) is preserved, NOT pointer identity.</item>
///   <item>Subclass identity — declared static type drives the C# wrapper class.</item>
///   <item>Disposal — explicit Dispose runs through Arc.Release without crashing.</item>
/// </list>
///
/// <para>Pointer identity is intentionally NOT asserted: cross-module compilation can
/// emit two distinct AnyKeyPath instances for the same logical path. Value-equality
/// via Swift's <c>AnyKeyPath.==</c> (dispatched through SBW_AnyKeyPath_Equals) is the
/// contract; assertions check that.</para>
/// </summary>
public class KeyPathFoundationTests : TestBase
{
    public KeyPathFoundationTests(TestResults results) : base(results) { }

    // ---------------------------------------------------------------------------------------
    // OUT path
    // ---------------------------------------------------------------------------------------

    public void TestMakePointXPath_ReturnsTypedKeyPath()
    {
        using var kp = KeyPathFactory.MakePointXPath();
        AssertNotNull(kp, "MakePointXPath should return non-null KeyPath");
        AssertFalse(kp.IsInvalid, "KeyPath handle should be valid (non-null pointer)");
        AssertTrue(kp is global::Swift.KeyPath<PointKP, nint>, "Static type is KeyPath<PointKP, nint>");
    }

    public void TestMakeWritablePointXPath_IsWritableKeyPathSubclass()
    {
        using var wkp = KeyPathFactory.MakeWritablePointXPath();
        AssertNotNull(wkp, "MakeWritablePointXPath returns non-null");
        AssertTrue(wkp is global::Swift.WritableKeyPath<PointKP, nint>, "Static type is WritableKeyPath");
        AssertTrue(wkp is global::Swift.KeyPath<PointKP, nint>, "Also is-a KeyPath (inheritance chain)");
        AssertTrue(wkp is global::Swift.PartialKeyPath<PointKP>, "Also is-a PartialKeyPath");
        AssertTrue(wkp is global::Swift.AnyKeyPath, "Also is-a AnyKeyPath");
    }

    public void TestMakeReferenceWritableBoxNPath_IsRefWritableSubclass()
    {
        using var rwkp = KeyPathFactory.MakeReferenceWritableBoxNPath();
        AssertNotNull(rwkp, "MakeReferenceWritableBoxNPath returns non-null");
        AssertTrue(rwkp is global::Swift.ReferenceWritableKeyPath<BoxKP, nint>, "Static type");
        AssertTrue(rwkp is global::Swift.WritableKeyPath<BoxKP, nint>, "Inherits WritableKeyPath");
        AssertTrue(rwkp is global::Swift.KeyPath<BoxKP, nint>, "Inherits KeyPath");
        AssertTrue(rwkp is global::Swift.PartialKeyPath<BoxKP>, "Inherits PartialKeyPath");
        AssertTrue(rwkp is global::Swift.AnyKeyPath, "Inherits AnyKeyPath");
    }

    public void TestMakePartialPointXPath_TypeErasedValue()
    {
        using var partial = KeyPathFactory.MakePartialPointXPath();
        AssertNotNull(partial, "MakePartialPointXPath returns non-null");
        AssertTrue(partial is global::Swift.PartialKeyPath<PointKP>, "Static type");
        AssertTrue(partial is global::Swift.AnyKeyPath, "Inherits AnyKeyPath");
    }

    public void TestMakeAnyPointXPath_FullyTypeErased()
    {
        using var any = KeyPathFactory.MakeAnyPointXPath();
        AssertNotNull(any, "MakeAnyPointXPath returns non-null");
        AssertTrue(any is global::Swift.AnyKeyPath, "Static type AnyKeyPath");
    }

    public void TestMaybePath_NoneReturnsNull()
    {
        var maybe = KeyPathFactory.MaybePath(false);
        AssertNull(maybe, "MaybePath(false) returns null");
    }

    public void TestMaybePath_SomeReturnsKeyPath()
    {
        var maybe = KeyPathFactory.MaybePath(true);
        AssertNotNull(maybe, "MaybePath(true) returns non-null KeyPath");
        maybe!.Dispose();
    }

    public void TestGetAllPointPaths_ReturnsTwoTypedPaths()
    {
        var paths = KeyPathFactory.GetAllPointPaths();
        AssertNotNull(paths, "GetAllPointPaths returns non-null list");
        AssertEqual(2, paths.Count, "List has 2 elements");
        foreach (var kp in paths)
        {
            AssertNotNull(kp, "Each element is non-null");
            AssertTrue(kp is global::Swift.KeyPath<PointKP, nint>, "Each is KeyPath<PointKP, nint>");
            kp.Dispose();
        }
    }

    // ---------------------------------------------------------------------------------------
    // IN path
    // ---------------------------------------------------------------------------------------

    public void TestReadInt_ThroughKeyPath()
    {
        using var kpX = KeyPathFactory.MakePointXPath();
        var p = new PointKP(7, 42);
        var x = KeyPathConsumer.ReadInt(p, kpX);
        AssertEqual<nint>(7, x, "ReadInt via x KeyPath returns 7");
    }

    public void TestReadInt_DistinctPathsReadDistinctFields()
    {
        using var kpX = KeyPathFactory.MakePointXPath();
        using var kpY = KeyPathFactory.MakePointYPath();
        var p = new PointKP(11, 22);
        AssertEqual<nint>(11, KeyPathConsumer.ReadInt(p, kpX), "x slot");
        AssertEqual<nint>(22, KeyPathConsumer.ReadInt(p, kpY), "y slot");
    }

    public void TestWriteInt_MutatesValueTypeViaWritableKeyPath()
    {
        using var wkp = KeyPathFactory.MakeWritablePointXPath();
        using var kpX = KeyPathFactory.MakePointXPath();
        var p = new PointKP(0, 0);

        var pMut = KeyPathConsumer.WriteInt(p, wkp, 99);
        AssertEqual<nint>(99, pMut.X, "WritableKeyPath subscript assignment is reflected in returned PointKP");
        AssertEqual<nint>(99, KeyPathConsumer.ReadInt(pMut, kpX), "Read-back of returned value via KeyPath also returns 99");
        AssertEqual<nint>(0, p.X, "Original value-type p is untouched (Swift took a copy)");
    }

    public void TestWriteIntRef_MutatesReferenceTypePropertyInPlace()
    {
        using var rwkp = KeyPathFactory.MakeReferenceWritableBoxNPath();
        using var b = new BoxKP();
        AssertEqual(0, b.N, "Initial N = 0");

        KeyPathConsumer.WriteIntRef(b, rwkp, 1234);
        AssertEqual(1234, (int)b.N, "After WriteIntRef, N = 1234");
    }

    public void TestReadOrDefault_WithOptionalKeyPathNull()
    {
        var p = new PointKP(5, 6);
        var v = KeyPathConsumer.ReadOrDefault(p, null, -1);
        AssertEqual<nint>(-1, v, "Null KeyPath returns the default");
    }

    public void TestReadOrDefault_WithOptionalKeyPathPresent()
    {
        var p = new PointKP(5, 6);
        using var kpY = KeyPathFactory.MakePointYPath();
        var v = KeyPathConsumer.ReadOrDefault(p, kpY, -1);
        AssertEqual<nint>(6, v, "Present KeyPath returns the value (6)");
    }

    // ---------------------------------------------------------------------------------------
    // Round-trip + equality
    // ---------------------------------------------------------------------------------------

    public void TestRoundTrip_PreservesValueEquality()
    {
        using var a = KeyPathFactory.MakePointXPath();
        using var b = KeyPathConsumer.RoundTrip(a);
        AssertNotNull(b, "RoundTrip returns non-null");
        AssertTrue(a.Equals(b), "Round-tripped KeyPath value-equals the original");
        AssertEqual(a.GetHashCode(), b.GetHashCode(), "Equal KeyPaths hash equal");
    }

    public void TestCrossInstanceEquality_SameFactoryTwiceValueEquals()
    {
        using var a = KeyPathFactory.MakePointXPath();
        using var b = KeyPathFactory.MakePointXPath();
        AssertTrue(a.Equals(b), "Two calls to MakePointXPath produce value-equal KeyPaths");
        AssertEqual(a.GetHashCode(), b.GetHashCode(), "Equal KeyPaths hash equal");
    }

    public void TestInequality_DifferentFieldsNotEqual()
    {
        using var a = KeyPathFactory.MakePointXPath();
        using var b = KeyPathFactory.MakePointYPath();
        AssertFalse(a.Equals(b), "x and y KeyPaths are not value-equal");
    }

    public void TestSamePath_SwiftEqualityRoundTrips()
    {
        using var a = KeyPathFactory.MakeAnyPointXPath();
        using var b = KeyPathFactory.MakeAnyPointXPath();
        AssertTrue(KeyPathConsumer.SamePath(a, b), "Swift-side AnyKeyPath.== agrees with C# Equals");
    }

    // ---------------------------------------------------------------------------------------
    // Disposal & finalizer
    // ---------------------------------------------------------------------------------------

    public void TestDispose_IsIdempotent()
    {
        var kp = KeyPathFactory.MakePointXPath();
        kp.Dispose();
        kp.Dispose();
        AssertTrue(kp.IsClosed || kp.IsInvalid, "After Dispose, handle is closed/invalid");
    }

    public void TestFinalizer_NoCrashUnderGCPressure()
    {
        // Allocate, drop the reference, force GC. Finalizer routes through
        // SwiftReleaseTrampoline.Release; this must not crash on either Mono JIT
        // (simulator) or NativeAOT (device).
        for (int i = 0; i < 200; i++)
        {
            _ = KeyPathFactory.MakePointXPath();
        }
        ForceGC();
        // If we reach here without segfaulting, the finalizer path is sound.
    }

    public void TestRepeatedRoundTrip_NoLeakOrCrash()
    {
        // Stress the @guaranteed inbound + +1 retained outbound path. If the round-trip
        // wrapper mishandles ARC (e.g. by NOT retaining the returned argument), the
        // caller eventually receives a dangling pointer.
        using var seed = KeyPathFactory.MakePointXPath();
        for (int i = 0; i < 500; i++)
        {
            using var trip = KeyPathConsumer.RoundTrip(seed);
            AssertTrue(seed.Equals(trip), $"Iteration {i}: round-trip preserves equality");
        }
    }
}
