// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Marshalling;

/// <summary>
/// P1-10: a <c>Foundation.Data</c> parameter that lands at or beyond the eighth integer register must
/// be decomposed into two explicit pointer-width words in the <c>@_cdecl</c> wrapper so the C#
/// P/Invoke matches the Swift wrapper's register layout.
///
/// <c>Data</c> lowers to a two-word inline value. With seven leading <c>Int64</c> arguments the first
/// six fill <c>x0..x5</c>, the seventh fills <c>x6</c>, and <c>Data</c>'s two words straddle <c>x7</c>
/// plus the stack. The fix emits two <c>nint</c> words (<c>payload_w0</c>/<c>payload_w1</c>) on both
/// sides; before it, the wrapper passed <c>Data</c> as a single by-value remapped struct, so the
/// second word and every following argument shifted by one slot and the byte payload came through as
/// garbage. <c>BlobPacker</c> exercises both the constructor wrapper and the instance-method wrapper
/// (the property/subscript path is intentionally excluded because <c>Data</c> never lands past x7
/// there). The fixture only passes <c>Data</c> as a parameter — no <c>Data</c> return, no
/// <c>Optional&lt;Data&gt;</c> — to stay on the supported marshalling path.
/// </summary>
public class DataRegisterStraddleTests : TestBase
{
    public DataRegisterStraddleTests(TestResults results) : base(results) { }

    public void TestConstructorDataAfterSevenIntsRoundTrip()
    {
        // Constructor wrapper path: seven Int64 args then a Data payload that straddles x7 + the stack.
        // leadingSum proves the seven ints landed; payloadSum proves every Data byte survived the
        // two-word decompose (a one-slot shift would corrupt the bytes and/or the trailing word).
        var payload = new byte[] { 10, 20, 30, 40 }; // byte sum = 100
        using var packer = new BlobPacker(1, 2, 3, 4, 5, 6, 7, payload);
        AssertEqual(28L, packer.LeadingSum, "leadingSum = 1+2+3+4+5+6+7");
        AssertEqual(100L, packer.PayloadSum, "payloadSum = sum of Data bytes survived the register straddle");
        TestLogger.Info($"new BlobPacker(1..7, [10,20,30,40]) → leadingSum={packer.LeadingSum}, payloadSum={packer.PayloadSum}");
    }

    public void TestMethodDataAfterSevenIntsRoundTrip()
    {
        // Instance-method wrapper path: self plus seven Int64 args then a Data payload past x7. The
        // return combines both the argument sum and the byte sum so a shift in either is caught.
        using var packer = new BlobPacker(0, 0, 0, 0, 0, 0, 0, Array.Empty<byte>());
        var extra = new byte[] { 1, 2, 3, 4, 5 }; // byte sum = 15
        var result = packer.Repack(10, 20, 30, 40, 50, 60, 70, extra);
        AssertEqual(280L + 15L, result, "repack = sum(b0..b6)=280 + sum(extra bytes)=15");
        TestLogger.Info($"BlobPacker.Repack(10..70, [1,2,3,4,5]) = {result}");
    }

    public void TestConstructorEmptyDataRoundTrip()
    {
        // Empty Data still occupies two words; the decompose must pass both even when the payload is
        // empty, leaving payloadSum at 0 while the leading ints remain intact.
        using var packer = new BlobPacker(100, 200, 300, 400, 500, 600, 700, Array.Empty<byte>());
        AssertEqual(2800L, packer.LeadingSum, "leadingSum = 100+200+...+700 with empty Data");
        AssertEqual(0L, packer.PayloadSum, "empty Data → payloadSum 0, no garbage from the second word");
    }
}
