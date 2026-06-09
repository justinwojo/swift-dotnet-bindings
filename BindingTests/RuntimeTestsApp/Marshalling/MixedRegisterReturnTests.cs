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

    public void TestByteQuintWideEightbyteGroupingRoundTrip()
    {
        // {Int8 × 5, Int64, Int64} = 24 bytes, laid out as three integer eightbytes:
        // [b0..b4 + 3 pad][first][second]. swiftcc returns it field-grouped across x0/x1/x2; a thunk
        // that miscounts the eightbytes — treating each Int8 as its own register slot, or stopping at
        // two slots — surfaces `first`/`second` as garbage while the five bytes still look correct.
        // Distinct ascending bytes plus two wide values that need their own eightbytes prove the
        // grouping survived. This is a different shape than the 5×Int64 LargeScalar struct above.
        var s = TestLibFunctions.MakeByteQuintWide(1, 2, 3, 4, 5, 6_000_000_000L, 7_000_000_000L);
        AssertEqual((sbyte)1, s.B0, "ByteQuintWide.B0");
        AssertEqual((sbyte)2, s.B1, "ByteQuintWide.B1");
        AssertEqual((sbyte)3, s.B2, "ByteQuintWide.B2");
        AssertEqual((sbyte)4, s.B3, "ByteQuintWide.B3");
        AssertEqual((sbyte)5, s.B4, "ByteQuintWide.B4");
        AssertEqual(6_000_000_000L, s.First, "ByteQuintWide.First survived eightbyte grouping");
        AssertEqual(7_000_000_000L, s.Second, "ByteQuintWide.Second survived eightbyte grouping");
        TestLogger.Info($"MakeByteQuintWide = (b0..b4={s.B0},{s.B1},{s.B2},{s.B3},{s.B4}, first={s.First}, second={s.Second})");
    }

    public void TestWideQuintetStaticMethodIndirectRoundTrip()
    {
        // 40-byte (5 × Int64) struct returned INDIRECTLY by a STATIC method. A static method has no
        // self, so the type-metadata accessor call (`bl` on arm64, `callq` on x86_64) is the call that
        // clobbers the sret register — x8 on arm64, %rdi on x86_64 — between the wrapper receiving the
        // sret pointer and the swiftcc call. The fix spills/reloads x8 around the accessor (arm64) and
        // stashes the sret in callee-saved %rbx (x86_64). The free-function (tail call) and
        // instance-method (self in swiftself) variants above cannot exercise the static+metadata case:
        // only here does the metadata accessor sit between sret receipt and the call. Five ascending
        // fields prove the result buffer pointer survived the accessor.
        var s = WideQuintet.Make(500);
        AssertEqual(500L, s.A, "WideQuintet.A — sret survived the metadata accessor");
        AssertEqual(501L, s.B, "WideQuintet.B");
        AssertEqual(502L, s.C, "WideQuintet.C");
        AssertEqual(503L, s.D, "WideQuintet.D");
        AssertEqual(504L, s.E, "WideQuintet.E");
        TestLogger.Info($"WideQuintet.Make(500) = ({s.A}, {s.B}, {s.C}, {s.D}, {s.E})");
    }
}
