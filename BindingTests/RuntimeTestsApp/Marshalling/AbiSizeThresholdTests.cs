// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Marshalling;

/// <summary>
/// Finding 59 corner 4 (architecture review §2009–2022) — runtime wrapper-marshalling coverage for
/// <c>@frozen</c> integer structs of 8, 16, and 24 bytes used as <c>self</c> and as by-value params.
///
/// <para><b>What this does NOT test.</b> It does not pin the calling-convention size thresholds
/// (<c>AbiSizeLimits.MaxSelfSize = 8</c>, <c>MaxParamSize = 16</c>). Those constants gate wrapper
/// selection only in the generator's no-wrapper fallback path; in a normal binding (and always in
/// BindingTests) every eligible instance method gets an <c>@_cdecl</c> wrapper unconditionally
/// (<c>WrapperValidation.DetermineMethodWrapperDecision</c> never consults the size threshold once
/// <c>ShouldEmitWrapper</c> is true). So all four methods below — under OR over the boundary — route
/// through <c>SBW_*</c> <c>@_cdecl</c> wrappers called over <c>CallConvCdecl</c>; a runtime round-trip
/// cannot reach the path that consults the threshold, and these tests would still pass if the
/// constants were wrong. The threshold DECISION is pinned at the exact ±1 boundary by the emitter
/// unit tests in <c>EmitterTests/AbiSafetyTests.cs</c> (self 8→false/9→true, param 16→false/17→true).
///
/// <para><b>What this DOES test.</b> The <c>@_cdecl</c> wrapper path must correctly marshal frozen
/// integer structs of 8/16/24 bytes as both <c>self</c> and by-value params, round-tripping every
/// field. Each struct carries distinct per-field sentinels summed in the return — a dropped, zeroed,
/// or transposed field changes the sum, and an over-sized struct mismarshalled by the wrapper
/// SIGSEGVs. Run on <c>--device</c> (NativeAOT) where the large-struct SIGSEGV class actually bites.
/// </summary>
public class AbiSizeThresholdTests : TestBase
{
    public AbiSizeThresholdTests(TestResults results) : base(results) { }

    #region 8-byte self

    public void TestSelf8WrapperRoundTrip()
    {
        // 8-byte frozen-integer self, marshalled through the @_cdecl wrapper. The full 64-bit
        // sentinel must survive into selfValue()'s read of self.a.
        const long a = unchecked((long)0x1122334455667788);
        var s = new AbiThresholdSelf8(a: a);
        AssertEqual(a, s.GetSelfValue(), "8-byte self round-trips through the @_cdecl wrapper");
        TestLogger.Info("AbiThresholdSelf8.GetSelfValue() (8-byte self via @_cdecl wrapper) passed");
    }

    #endregion

    #region 16-byte self

    public void TestSelf16WrapperRoundTrip()
    {
        // 16-byte (two-word) frozen-integer self through the @_cdecl wrapper. Both self words must
        // survive; the sum catches either word being dropped.
        const long a = unchecked((long)0x0A0B0C0D0E0F1011);
        const long b = unchecked((long)0x1213141516171819);
        var s = new AbiThresholdSelf16(a: a, b: b);
        AssertEqual(unchecked(a + b), s.GetSelfValue(), "16-byte self round-trips through the @_cdecl wrapper");
        TestLogger.Info("AbiThresholdSelf16.GetSelfValue() (16-byte self via @_cdecl wrapper) passed");
    }

    #endregion

    #region 16-byte by-value param

    public void TestParam16WrapperRoundTrip()
    {
        // 16-byte by-value integer-struct param through the @_cdecl wrapper. Distinct sentinels for
        // self and both param fields; the sum is load-bearing on all three.
        const long selfA = unchecked((long)0x0102030405060708);
        const long pA = unchecked((long)0x2122232425262728);
        const long pB = unchecked((long)0x3132333435363738);
        var s = new AbiThresholdSelf8(a: selfA);
        var p = new AbiThresholdParam16(a: pA, b: pB);
        AssertEqual(unchecked(selfA + pA + pB), s.AcceptParam16(p),
            "16-byte by-value param round-trips through the @_cdecl wrapper");
        TestLogger.Info("AbiThresholdSelf8.AcceptParam16() (16-byte param via @_cdecl wrapper) passed");
    }

    #endregion

    #region 24-byte by-value param

    public void TestParam24WrapperRoundTrip()
    {
        // 24-byte by-value integer-struct param through the @_cdecl wrapper. All four contributing
        // words (self + three param fields) must survive.
        const long selfA = unchecked((long)0x1011121314151617);
        const long pA = unchecked((long)0x4142434445464748);
        const long pB = unchecked((long)0x5152535455565758);
        const long pC = unchecked((long)0x6162636465666768);
        var s = new AbiThresholdSelf8(a: selfA);
        var p = new AbiThresholdParam24(a: pA, b: pB, c: pC);
        AssertEqual(unchecked(selfA + pA + pB + pC), s.AcceptParam24(p),
            "24-byte by-value param round-trips through the @_cdecl wrapper");
        TestLogger.Info("AbiThresholdSelf8.AcceptParam24() (24-byte param via @_cdecl wrapper) passed");
    }

    #endregion
}
