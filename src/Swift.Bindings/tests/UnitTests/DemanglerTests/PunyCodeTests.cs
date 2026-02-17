// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using BindingsGeneration.Demangling;
using Xunit;

namespace BindingsGeneration.Tests;

public class PunyCodeTests
{
    private readonly PunyCode _punyCode = new PunyCode();

    // --- Valid decoding ---

    [Fact]
    public void Decode_AsciiOnly_NoDelimiter()
    {
        // Pure ASCII with no non-ASCII insertions — just the literal prefix
        // A string with only a delimiter and ASCII prefix returns the prefix
        var result = _punyCode.Decode("hello_");
        Assert.Equal("hello", result);
    }

    [Fact]
    public void Decode_SingleDigitA_DecodesControlChar()
    {
        // "a" = digit 0 with no delimiter: n starts at 128, i=0.
        // Inner loop: digit=0, t=1, 0<1 → break. n=128+0=128. i=0.
        // Inserts U+0080 (control character) at position 0.
        var result = _punyCode.Decode("a");
        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal('\u0080', result[0]);
    }

    [Fact]
    public void Decode_WithPrefix_InsertsSingleNonAscii()
    {
        // "abc_" has prefix "abc" and no encoded suffix → just returns "abc"
        // "abc_a" has prefix "abc" and encoded suffix "a" (digit=0)
        // digit=0, t=1, 0<1 → break. bias=Adapt(0,4,true)=0.
        // n=128+0/4=128, i=0%4=0. Insert U+0080 at position 0.
        // Result: "\u0080abc"
        var result = _punyCode.Decode("abc_a");
        Assert.Equal(4, result.Length);
        Assert.Equal('\u0080', result[0]);
        Assert.Equal("abc", result.Substring(1));
    }

    [Fact]
    public void Decode_AsciiPrefixOnly_TrailingDelimiter()
    {
        // "abc_" → prefix is "abc", no encoded suffix
        var result = _punyCode.Decode("abc_");
        Assert.Equal("abc", result);
    }

    [Fact]
    public void Decode_NoDelimiterPureAscii()
    {
        // With no delimiter at all, the entire string is treated as encoded digits.
        // "a" = digit 0: n=128+0=128, inserts char(128). This is valid algorithm behavior.
        // We just verify it doesn't crash.
        var result = _punyCode.Decode("a");
        Assert.NotNull(result);
        Assert.Single(result); // one character
    }

    // --- Bug fix regression tests ---

    [Fact]
    public void Decode_InvalidCharacter_ThrowsArgumentException()
    {
        // PC1 regression: characters outside a-z/A-J should throw, not KeyNotFoundException
        // 'K' is outside the valid range (A-J)
        var ex = Assert.Throws<ArgumentException>(() => _punyCode.Decode("K"));
        Assert.Contains("invalid character", ex.Message);
    }

    [Fact]
    public void Decode_InvalidCharacter_Digit_ThrowsArgumentException()
    {
        // Digits are not in the decode table
        var ex = Assert.Throws<ArgumentException>(() => _punyCode.Decode("0"));
        Assert.Contains("invalid character", ex.Message);
    }

    [Fact]
    public void Decode_InvalidCharacter_Symbol_ThrowsArgumentException()
    {
        // Symbols like '!' are not in the decode table
        Assert.Throws<ArgumentException>(() => _punyCode.Decode("!"));
    }

    [Fact]
    public void Decode_InvalidCharacter_InSuffix_ThrowsArgumentException()
    {
        // Valid prefix, but invalid character in encoded suffix
        Assert.Throws<ArgumentException>(() => _punyCode.Decode("hello_Z"));
    }

    [Fact]
    public void Decode_EmptyString_ReturnsEmpty()
    {
        var result = _punyCode.Decode("");
        Assert.Equal("", result);
    }

    [Fact]
    public void Decode_DelimiterOnly()
    {
        // Single '_': LastIndexOf('_') returns 0, which is not > 0, so no prefix extracted.
        // Then pos=0, and '_' is not in decode table → should throw
        Assert.Throws<ArgumentException>(() => _punyCode.Decode("_"));
    }

    [Fact]
    public void Decode_MultipleDelimiters_UsesLast()
    {
        // "a_b_cda": last delimiter at index 3, prefix = "a_b", encoded = "cda"
        var result = _punyCode.Decode("a_b_cda");
        // The prefix is "a_b" and the encoded portion "cda" inserts a non-ASCII char
        Assert.NotNull(result);
        Assert.True(result.Length >= 3); // at least the prefix chars plus insertions
    }

    // --- Valid alphabet boundary tests ---

    [Fact]
    public void Decode_ValidLowercaseRange()
    {
        // All lowercase letters a-z are valid
        // 'a' = 0, 'z' = 25
        // Just verify single valid chars don't throw (they produce some character)
        var result = _punyCode.Decode("a");
        Assert.NotNull(result);
    }

    [Fact]
    public void Decode_ValidUppercaseRange_A_Through_J()
    {
        // A-J are valid (digits 26-35). 'A' alone needs multiple chars in the
        // inner loop since digit(26) >= t(1). "Aa" terminates correctly:
        // k=36: digit=26, t=1. 26>=1, w=35. k=72: digit=0, t=1. 0<1, break.
        // i=26, outputLength=1. n=128+26=154. Insert U+009A at position 0.
        var result = _punyCode.Decode("Aa");
        Assert.NotNull(result);
        Assert.Single(result);
    }

    [Fact]
    public void Decode_UppercaseA_Alone_ThrowsTruncated()
    {
        // 'A' alone: digit=26 >= t=1, inner loop continues but pos overflows.
        // This is the PC1b fix regression test — truncated input throws clearly.
        var ex = Assert.Throws<ArgumentException>(() => _punyCode.Decode("A"));
        Assert.Contains("unexpected end of input", ex.Message);
    }

    [Fact]
    public void Decode_InvalidUppercase_K_Throws()
    {
        // 'K' is NOT in the decode table (only A-J)
        Assert.Throws<ArgumentException>(() => _punyCode.Decode("K"));
    }
}
