// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// Tests for bound generic edge cases — multi-type-argument bound generics
/// with concrete type resolution.
///
/// Pair&lt;A, B&gt; is a generic struct; makeRefPair uses concrete class types
/// (CoordinateRef, LabelRef) that resolve through BoundGenericsHandler.
/// makePairDescription is a method-level generic and may not be emitted.
/// </summary>
public class BoundGenericEdgeCaseTests : TestBase
{
    public BoundGenericEdgeCaseTests(TestResults results) : base(results) { }

    #region CoordinateRef / LabelRef Construction (Tier 1)

    public void TestCoordinateRefConstruction()
    {
        var coord = new CoordinateRef(x: 10, y: 20);
        AssertNotNull(coord, "CoordinateRef constructed");
        TestLogger.Info("CoordinateRef(10, 20) construction passed");
    }

    public void TestLabelRefConstruction()
    {
        var label = new LabelRef(text: "hello");
        AssertNotNull(label, "LabelRef constructed");
        TestLogger.Info("LabelRef(\"hello\") construction passed");
    }

    #endregion

    #region MakeRefPair — Multi-Type-Arg Bound Generic (Tier 1)

    public void TestMakeRefPair()
    {
        // makeRefPair returns Pair<CoordinateRef, LabelRef> — two different class type args.
        // The generator now emits an @_cdecl wrapper (not a native thunk) for non-generic
        // functions returning bound generic structs.
        var coord = new CoordinateRef(x: 42, y: 99);
        var label = new LabelRef(text: "test");
        var pair = TestLibFunctions.MakeRefPair(coord, label);
        AssertNotNull(pair, "MakeRefPair returned non-null");
        TestLogger.Info("MakeRefPair construction passed");
    }

    #endregion

    #region MakePairDescription — Method-Level Generic

    public void TestMakePairDescriptionEmitted()
    {
        // makePairDescription<A, B> is a method-level generic free function.
        // The generator emits it as a C# generic method with [Obsolete] warning
        // (no @_cdecl wrapper — Swift can't express generic params in @_cdecl).
        // This test verifies the binding compiles and the method signature is correct.
        // Calling the method at runtime requires CallConvSwift with 2 type metadata
        // params, which is blocked by upstream NativeAOT issue #4.
        //
        // Compile-time check: reference the method so the test fails to build if removed.
#pragma warning disable CS0618 // Obsolete (expected — method-level generics have [Obsolete] warning)
        var method = typeof(TestLibFunctions).GetMethod(nameof(TestLibFunctions.MakePairDescription));
#pragma warning restore CS0618
        AssertNotNull(method, "MakePairDescription<A,B> method exists in generated bindings");
    }

    #endregion
}
