// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Marshalling;

/// <summary>
/// Round-trips frozen structs returned by value whose fields land in different register files than
/// the C ABI expects. A small (≤16B) {Int32, Float} struct is returned field-wise by swiftcc (Int32
/// in a general-purpose register, Float in a vector register) but packed into one integer register by
/// the C ABI — so a tail-call thunk would surface the Float as garbage. A >16B mixed-width struct goes
/// through the field-wise return bridge, which must store each register at its natural offset rather
/// than a uniform 8-byte stride. Both shapes are silent ABI hazards that only a value round-trip
/// catches: the wrong-register read does not crash, it just returns the wrong number.
/// </summary>
public class MixedRegisterReturnTests : TestBase
{
    public MixedRegisterReturnTests(TestResults results) : base(results) { }

    public void TestMixedSmallFactoryRoundTrip()
    {
        // {Int32, Float} returned by value from a free function. The Float must survive — if the
        // thunk tail-called swiftcc's field-wise return, Value would read uninitialized bytes.
        var s = TestLibFunctions.MakeMixedSmall(7, 3.5f);
        AssertEqual(7, s.Tag, "MixedSmall.Tag");
        AssertEqual(3.5f, s.Value, "MixedSmall.Value");
        TestLogger.Info($"MakeMixedSmall(7, 3.5) = (tag={s.Tag}, value={s.Value})");
    }

    public void TestMixedSmallInstanceMethodRoundTrip()
    {
        // Same return shape on an instance method (self in the swiftself register).
        var scaled = TestLibFunctions.MakeMixedSmall(10, 2.0f).Scaled(4.0f);
        AssertEqual(11, scaled.Tag, "Scaled MixedSmall.Tag = 10 + 1");
        AssertEqual(8.0f, scaled.Value, "Scaled MixedSmall.Value = 2.0 * 4.0");
    }

    public void TestMixedWideFactoryRoundTrip()
    {
        // >16B mixed-width struct: each field must land at its natural offset through the return
        // bridge. A uniform 8-byte stride would corrupt the 4-byte Float and the following Int64.
        var w = TestLibFunctions.MakeMixedWide(3, 0.5f, 9_000_000_000L, 1.25);
        AssertEqual(3, w.Tag, "MixedWide.Tag");
        AssertEqual(0.5f, w.Scale, "MixedWide.Scale");
        AssertEqual(9_000_000_000L, w.Count, "MixedWide.Count");
        AssertEqual(1.25, w.Weight, "MixedWide.Weight");
        TestLogger.Info($"MakeMixedWide = (tag={w.Tag}, scale={w.Scale}, count={w.Count}, weight={w.Weight})");
    }

    public void TestWidePairFactoryRoundTrip()
    {
        // {Int64, Double} (16B): each field owns its own eightbyte, so x86_64 agrees — but arm64
        // AAPCS64 returns this non-HFA aggregate in GPRs while swiftcc returns the Double in d0. A
        // tail-call thunk would read Weight from the wrong register on arm64; the @_cdecl wrapper is
        // correct. The iOS Simulator runs arm64, so this round-trip catches the divergence directly.
        var p = TestLibFunctions.MakeWidePair(9_000_000_000L, 2.5);
        AssertEqual(9_000_000_000L, p.Count, "WidePair.Count");
        AssertEqual(2.5, p.Weight, "WidePair.Weight");
        TestLogger.Info($"MakeWidePair(9e9, 2.5) = (count={p.Count}, weight={p.Weight})");
    }

    public void TestWidePairInstanceMethodRoundTrip()
    {
        // Same {Int64, Double} return shape on an instance method (self in the swiftself register).
        var scaled = TestLibFunctions.MakeWidePair(10, 3.0).Scaled(4.0);
        AssertEqual(11L, scaled.Count, "Scaled WidePair.Count = 10 + 1");
        AssertEqual(12.0, scaled.Weight, "Scaled WidePair.Weight = 3.0 * 4.0");
    }

    public void TestWideFloatDoubleFactoryRoundTrip()
    {
        // {Float, Double} (16B): each field owns its own eightbyte (so x86_64 SysV agrees), but the
        // mixed FP widths mean it is not an HFA — arm64 AAPCS64 returns it in GPRs (Float in w0,
        // Double in x1) while swiftcc returns s0/d1. A tail-call thunk gated only on
        // each-owns-an-eightbyte would read both fields from the wrong register file on arm64; the
        // homogeneity gate routes it to the @_cdecl wrapper. The Simulator runs arm64 — this catches it.
        var w = TestLibFunctions.MakeWideFloatDouble(1.5f, 2.5);
        AssertEqual(1.5f, w.Scale, "WideFloatDouble.Scale");
        AssertEqual(2.5, w.Weight, "WideFloatDouble.Weight");
        TestLogger.Info($"MakeWideFloatDouble(1.5, 2.5) = (scale={w.Scale}, weight={w.Weight})");
    }

    public void TestWideFloatDoubleInstanceMethodRoundTrip()
    {
        // Same {Float, Double} return shape on an instance method (self in the swiftself register).
        var scaled = TestLibFunctions.MakeWideFloatDouble(2.0f, 3.0).Scaled(4.0);
        AssertEqual(3.0f, scaled.Scale, "Scaled WideFloatDouble.Scale = 2.0 + 1.0");
        AssertEqual(12.0, scaled.Weight, "Scaled WideFloatDouble.Weight = 3.0 * 4.0");
    }

    public void TestLargeScalarFactoryTailCallRoundTrip()
    {
        // 40-byte (5 × Int64) struct returned INDIRECTLY by a free function — the indirect-result
        // tail-call thunk. On x86_64 the cdecl sret pointer arrives in %rdi and must be moved to
        // swiftcc's %rax with the explicit args shifted down; a missing or mis-shifted buffer pointer
        // would read garbage, so reading back five distinct ascending values proves the bridge.
        var s = TestLibFunctions.MakeLargeScalarStruct(100);
        AssertEqual(100L, s.A, "LargeScalar.A");
        AssertEqual(101L, s.B, "LargeScalar.B");
        AssertEqual(102L, s.C, "LargeScalar.C");
        AssertEqual(103L, s.D, "LargeScalar.D");
        AssertEqual(104L, s.E, "LargeScalar.E");
        TestLogger.Info($"MakeLargeScalarStruct(100) = ({s.A}, {s.B}, {s.C}, {s.D}, {s.E})");
    }

    public void TestLargeScalarInstanceMethodIndirectRoundTrip()
    {
        // Same 40-byte indirect return from a final-class INSTANCE method — the full-frame indirect
        // path: self lands in the swiftself register and the sret pointer is bridged into %rax across
        // the call. The free-function variant above cannot exercise this (no self → tail call). Five
        // correct ascending fields prove self and the result buffer were both routed correctly.
        var s = new LargeScalarStructFactory(seed: 200).Make();
        AssertEqual(200L, s.A, "factory LargeScalar.A");
        AssertEqual(201L, s.B, "factory LargeScalar.B");
        AssertEqual(202L, s.C, "factory LargeScalar.C");
        AssertEqual(203L, s.D, "factory LargeScalar.D");
        AssertEqual(204L, s.E, "factory LargeScalar.E");
        TestLogger.Info($"LargeScalarStructFactory(200).Make() = ({s.A}, {s.B}, {s.C}, {s.D}, {s.E})");
    }
}
