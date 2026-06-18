// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Text;
using Swift.Runtime;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Finding 59 corner 2 (architecture review §2009–2022) golden grammar gate for the Swift
/// mangled-name symbolic-reference byte ranges.
///
/// The metadata field-record parser (<c>FieldRecord.GetMangledNameSymbol</c> /
/// <c>GetContextDescriptorAddress</c>) walks a mangled name that can splice in symbolic
/// references — control bytes <c>0x01–0x17</c> (relative, 4-byte payload) and <c>0x18–0x1F</c>
/// (absolute, pointer-sized payload). Until now those four magic bounds were duplicated as bare
/// literals across four sites and NO test asserted them; an Apple grammar change would silently
/// mis-skip bytes. <see cref="SymbolicReferenceGrammar"/> now owns the ranges and the four sites
/// delegate to it.
///
/// A live BindingTests probe was rejected as the instrument: the field-record parser has zero
/// runtime callers (so a drift corrupts nothing today and there is no navigation to drive), and
/// picking a stdlib type whose field-record mangled name *reliably* embeds a symbolic reference
/// across Xcode versions is fragile. This host-side golden pins the same classification against
/// synthetic blobs — robust, deterministic, and exercising every boundary byte — which is the
/// fallback the S16 plan authorized, upgraded to drive the real grammar rather than assert a
/// literal against the ABI doc.
/// </summary>
public class SymbolicReferenceGrammarTests
{
    [Fact]
    public void RangeConstants_MatchSwiftManglingGrammar()
    {
        // The golden bounds from docs/ABI/Mangling.rst. A one-file, one-test review point for an
        // upstream grammar change.
        Assert.Equal(0x01, SymbolicReferenceGrammar.RelativeRangeMin);
        Assert.Equal(0x17, SymbolicReferenceGrammar.RelativeRangeMax);
        Assert.Equal(0x18, SymbolicReferenceGrammar.AbsoluteRangeMin);
        Assert.Equal(0x1F, SymbolicReferenceGrammar.AbsoluteRangeMax);

        // The ranges are contiguous and disjoint: absolute begins exactly where relative ends.
        Assert.Equal(SymbolicReferenceGrammar.RelativeRangeMax + 1, SymbolicReferenceGrammar.AbsoluteRangeMin);
    }

    [Fact]
    public void Classification_CoversEveryByteValue()
    {
        // Independently recompute the expected classification for all 256 byte values from the
        // documented ranges, then assert the grammar agrees on every one — including both
        // boundaries of each range and the 0x20 character just past the absolute range.
        for (int v = 0; v <= 0xFF; v++)
        {
            byte b = (byte)v;
            bool expectRelative = v >= 0x01 && v <= 0x17;
            bool expectAbsolute = v >= 0x18 && v <= 0x1F;
            bool expectSymbolic = expectRelative || expectAbsolute;

            Assert.Equal(expectRelative, SymbolicReferenceGrammar.IsRelative(b));
            Assert.Equal(expectAbsolute, SymbolicReferenceGrammar.IsAbsolute(b));
            Assert.Equal(expectSymbolic, SymbolicReferenceGrammar.IsSymbolicReference(b));

            SymbolicReferenceGrammar.Component expectedComponent =
                b == 0 ? SymbolicReferenceGrammar.Component.Null
                : expectSymbolic ? SymbolicReferenceGrammar.Component.SymbolicReference
                : SymbolicReferenceGrammar.Component.Normal;
            Assert.Equal(expectedComponent, SymbolicReferenceGrammar.ComponentOf(b));

            int expectedLength =
                b == 0 ? 0
                : expectRelative ? 1 + sizeof(int)
                : expectAbsolute ? 1 + IntPtr.Size
                : 1;
            Assert.Equal(expectedLength, SymbolicReferenceGrammar.ByteLength(b));
        }
    }

    [Theory]
    [InlineData(0x00, 0)]   // terminator
    [InlineData(0x01, 5)]   // relative low bound: control + int32
    [InlineData(0x17, 5)]   // relative high bound
    [InlineData(0x18, 9)]   // absolute low bound: control + pointer (8 on 64-bit)
    [InlineData(0x1F, 9)]   // absolute high bound
    [InlineData(0x20, 1)]   // ' ' — first ordinary character past the ranges
    [InlineData('A', 1)]    // ordinary character
    public void ByteLength_AtBoundaries(int controlByte, int expectedLength)
    {
        // The absolute-reference width is pointer-sized; this test is host-only (64-bit), where
        // IntPtr.Size is 8, matching the ABI for the arm64/x86_64 targets we ship.
        int expected = expectedLength == 9 ? 1 + IntPtr.Size : expectedLength;
        Assert.Equal(expected, SymbolicReferenceGrammar.ByteLength((byte)controlByte));
    }

    [Fact]
    public void ComposedWalk_SkipsSymbolicReferencesAndCollectsNormalBytes()
    {
        // Demonstrates the grammar composes into the symbol-extraction the field-record parser
        // performs: ordinary bytes accrue to the symbol, a relative reference consumes its 4
        // payload bytes, and an absolute reference consumes its pointer-sized payload — so the
        // recovered symbol is exactly the ordinary characters. This is the same component/stride
        // contract FieldRecord.GetMangledNameSymbol relies on, exercised without live metadata.
        var blob = new List<byte>();
        blob.AddRange(Encoding.ASCII.GetBytes("AB"));
        blob.Add(0x01);                              // relative symbolic reference
        blob.AddRange(new byte[] { 9, 9, 9, 9 });    // ...its 4-byte payload (skipped)
        blob.AddRange(Encoding.ASCII.GetBytes("CD"));
        blob.Add(0x18);                              // absolute symbolic reference
        blob.AddRange(new byte[IntPtr.Size]);        // ...its pointer-sized payload (skipped)
        blob.AddRange(Encoding.ASCII.GetBytes("EF"));
        blob.Add(0x00);                              // terminator
        byte[] data = blob.ToArray();

        var symbol = new StringBuilder();
        int index = 0;
        while (index < data.Length)
        {
            byte next = data[index];
            switch (SymbolicReferenceGrammar.ComponentOf(next))
            {
                case SymbolicReferenceGrammar.Component.Null:
                    index = data.Length; // terminator ends the walk
                    break;
                case SymbolicReferenceGrammar.Component.SymbolicReference:
                    index += SymbolicReferenceGrammar.ByteLength(next);
                    break;
                default:
                    symbol.Append((char)next);
                    index += SymbolicReferenceGrammar.ByteLength(next);
                    break;
            }
        }

        Assert.Equal("ABCDEF", symbol.ToString());
    }
}
