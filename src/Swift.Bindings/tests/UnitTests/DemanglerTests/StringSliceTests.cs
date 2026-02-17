// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using BindingsGeneration.Demangling;
using Xunit;

namespace BindingsGeneration.Tests;

public class StringSliceTests
{
    // --- Construction ---

    [Fact]
    public void Construction_SetsPositionToZero()
    {
        var slice = new StringSlice("hello");
        Assert.Equal(0, slice.Position);
        Assert.Equal(5, slice.Length);
        Assert.Equal("hello", slice.Original);
        Assert.False(slice.IsAtEnd);
    }

    [Fact]
    public void Construction_EmptyString()
    {
        var slice = new StringSlice("");
        Assert.Equal(0, slice.Position);
        Assert.Equal(0, slice.Length);
        Assert.True(slice.IsAtEnd);
    }

    [Fact]
    public void Construction_SingleChar()
    {
        var slice = new StringSlice("x");
        Assert.Equal(1, slice.Length);
        Assert.Equal('x', slice.Current);
        Assert.False(slice.IsAtEnd);
    }

    // --- Current ---

    [Fact]
    public void Current_ReturnsFirstChar()
    {
        var slice = new StringSlice("abc");
        Assert.Equal('a', slice.Current);
    }

    [Fact]
    public void Current_ThrowsAtEnd()
    {
        var slice = new StringSlice("");
        Assert.Throws<ArgumentException>(() => slice.Current);
    }

    // --- Indexer ---

    [Fact]
    public void Indexer_ReturnsCharRelativeToPosition()
    {
        var slice = new StringSlice("abcdef");
        Assert.Equal('a', slice[0]);
        Assert.Equal('c', slice[2]);
        slice.Advance();
        Assert.Equal('b', slice[0]);
        Assert.Equal('d', slice[2]);
    }

    [Fact]
    public void Indexer_ThrowsOutOfRange()
    {
        var slice = new StringSlice("ab");
        Assert.Throws<IndexOutOfRangeException>(() => slice[2]);
    }

    // --- Advance() single char ---

    [Fact]
    public void Advance_ReturnsCurrent_AdvancesPosition()
    {
        var slice = new StringSlice("abc");
        Assert.Equal('a', slice.Advance());
        Assert.Equal(1, slice.Position);
        Assert.Equal('b', slice.Advance());
        Assert.Equal(2, slice.Position);
        Assert.Equal('c', slice.Advance());
        Assert.True(slice.IsAtEnd);
    }

    [Fact]
    public void Advance_ThrowsAtEnd()
    {
        var slice = new StringSlice("");
        Assert.Throws<IndexOutOfRangeException>(() => slice.Advance());
    }

    // --- Advance(int n) ---

    [Fact]
    public void AdvanceN_ReturnsSubstring()
    {
        var slice = new StringSlice("abcdef");
        Assert.Equal("abc", slice.Advance(3));
        Assert.Equal(3, slice.Position);
        Assert.Equal("de", slice.Advance(2));
        Assert.Equal(5, slice.Position);
    }

    [Fact]
    public void AdvanceN_Zero_ReturnsEmpty()
    {
        var slice = new StringSlice("abc");
        Assert.Equal("", slice.Advance(0));
        Assert.Equal(0, slice.Position);
    }

    [Fact]
    public void AdvanceN_EntireString()
    {
        var slice = new StringSlice("abc");
        Assert.Equal("abc", slice.Advance(3));
        Assert.True(slice.IsAtEnd);
    }

    [Fact]
    public void AdvanceN_NegativeThrows()
    {
        var slice = new StringSlice("abc");
        Assert.Throws<ArgumentOutOfRangeException>(() => slice.Advance(-1));
    }

    [Fact]
    public void AdvanceN_PastEndThrows()
    {
        var slice = new StringSlice("abc");
        Assert.Throws<ArgumentOutOfRangeException>(() => slice.Advance(4));
    }

    // --- Rewind ---

    [Fact]
    public void Rewind_GoesBackOneChar()
    {
        var slice = new StringSlice("abc");
        slice.Advance();
        slice.Advance();
        Assert.Equal(2, slice.Position);
        slice.Rewind();
        Assert.Equal(1, slice.Position);
        Assert.Equal('b', slice.Current);
    }

    [Fact]
    public void Rewind_AtStartThrows()
    {
        var slice = new StringSlice("abc");
        Assert.Throws<InvalidOperationException>(() => slice.Rewind());
    }

    // --- StartsWith(char) ---

    [Fact]
    public void StartsWithChar_MatchesCurrentChar()
    {
        var slice = new StringSlice("abc");
        Assert.True(slice.StartsWith('a'));
        Assert.False(slice.StartsWith('b'));
    }

    [Fact]
    public void StartsWithChar_ReturnsFalseAtEnd()
    {
        var slice = new StringSlice("");
        Assert.False(slice.StartsWith('x'));
    }

    // --- StartsWith(string) ---

    [Fact]
    public void StartsWithString_Matches()
    {
        var slice = new StringSlice("$s10Module");
        Assert.True(slice.StartsWith("$s"));
        Assert.False(slice.StartsWith("$S"));
    }

    [Fact]
    public void StartsWithString_EmptyStringAlwaysTrue()
    {
        var slice = new StringSlice("abc");
        Assert.True(slice.StartsWith(""));
    }

    [Fact]
    public void StartsWithString_LongerThanRemainingReturnsFalse()
    {
        var slice = new StringSlice("ab");
        Assert.False(slice.StartsWith("abc"));
    }

    [Fact]
    public void StartsWithString_RespectsPosition()
    {
        var slice = new StringSlice("abc");
        slice.Advance();
        Assert.True(slice.StartsWith("bc"));
        Assert.False(slice.StartsWith("ab"));
    }

    // --- AdvanceIfEquals ---

    [Fact]
    public void AdvanceIfEquals_AdvancesOnMatch()
    {
        var slice = new StringSlice("abc");
        Assert.True(slice.AdvanceIfEquals('a'));
        Assert.Equal(1, slice.Position);
    }

    [Fact]
    public void AdvanceIfEquals_NoAdvanceOnMismatch()
    {
        var slice = new StringSlice("abc");
        Assert.False(slice.AdvanceIfEquals('x'));
        Assert.Equal(0, slice.Position);
    }

    [Fact]
    public void AdvanceIfEquals_AtEnd_ReturnsFalse()
    {
        var slice = new StringSlice("");
        Assert.False(slice.AdvanceIfEquals('x'));
    }

    // --- AdvanceIf ---

    [Fact]
    public void AdvanceIf_AdvancesWhenPredicateTrue()
    {
        var slice = new StringSlice("abc");
        Assert.True(slice.AdvanceIf(sl => !sl.IsAtEnd && char.IsLetter(sl.Current)));
        Assert.Equal(1, slice.Position);
    }

    [Fact]
    public void AdvanceIf_NoAdvanceWhenPredicateFalse()
    {
        var slice = new StringSlice("abc");
        Assert.False(slice.AdvanceIf(sl => !sl.IsAtEnd && char.IsDigit(sl.Current)));
        Assert.Equal(0, slice.Position);
    }

    [Fact]
    public void AdvanceIf_AtEnd_ReturnsFalse()
    {
        var slice = new StringSlice("");
        Assert.False(slice.AdvanceIf(sl => true));
    }

    // --- ToString ---

    [Fact]
    public void ToString_ReturnsFullStringAtStart()
    {
        var slice = new StringSlice("hello");
        Assert.Equal("hello", slice.ToString());
    }

    [Fact]
    public void ToString_ReturnsRemainingAfterAdvance()
    {
        var slice = new StringSlice("hello");
        slice.Advance(2);
        Assert.Equal("llo", slice.ToString());
    }

    [Fact]
    public void ToString_ReturnsEmptyAtEnd()
    {
        var slice = new StringSlice("hi");
        slice.Advance(2);
        Assert.Equal("", slice.ToString());
    }

    [Fact]
    public void ToString_SS1BugRegression_DoesNotUseCharCodePoint()
    {
        // Before fix: ToString() used Current (a char) as Substring argument.
        // 'S' has code point 83, so slice.Substring(83) would be called
        // instead of slice.Substring(5). This verifies the fix.
        var slice = new StringSlice("abcdeSfgh");
        slice.Advance(5); // position=5, Current='S' (code point 83)
        Assert.Equal("Sfgh", slice.ToString());
    }

    // --- ConsumeRemaining ---

    [Fact]
    public void ConsumeRemaining_ReturnsRestAndAdvancesToEnd()
    {
        var slice = new StringSlice("abcdef");
        slice.Advance(3);
        Assert.Equal("def", slice.ConsumeRemaining());
        Assert.True(slice.IsAtEnd);
    }

    [Fact]
    public void ConsumeRemaining_EmptyAtEnd()
    {
        var slice = new StringSlice("ab");
        slice.Advance(2);
        Assert.Equal("", slice.ConsumeRemaining());
    }

    // --- IsNameNext ---

    [Fact]
    public void IsNameNext_TrueForDigit()
    {
        var slice = new StringSlice("5abc");
        Assert.True(slice.IsNameNext);
    }

    [Fact]
    public void IsNameNext_TrueForX()
    {
        var slice = new StringSlice("X3abc");
        Assert.True(slice.IsNameNext);
    }

    [Fact]
    public void IsNameNext_FalseForLetter()
    {
        var slice = new StringSlice("abc");
        Assert.False(slice.IsNameNext);
    }

    [Fact]
    public void IsNameNext_FalseAtEnd()
    {
        var slice = new StringSlice("");
        Assert.False(slice.IsNameNext);
    }

    // --- ExtractSwiftString ---

    [Fact]
    public void ExtractSwiftString_RegularName()
    {
        var slice = new StringSlice("5Hello");
        var result = slice.ExtractSwiftString(out bool isPunyCode);
        Assert.Equal("Hello", result);
        Assert.False(isPunyCode);
        Assert.True(slice.IsAtEnd);
    }

    [Fact]
    public void ExtractSwiftString_PunyCodePrefix()
    {
        var slice = new StringSlice("X3abc");
        var result = slice.ExtractSwiftString(out bool isPunyCode);
        Assert.Equal("abc", result);
        Assert.True(isPunyCode);
        Assert.True(slice.IsAtEnd);
    }

    [Fact]
    public void ExtractSwiftString_ReturnsNullWhenNotName()
    {
        var slice = new StringSlice("abc");
        var result = slice.ExtractSwiftString(out bool isPunyCode);
        Assert.Null(result);
        Assert.False(isPunyCode);
    }

    [Fact]
    public void ExtractSwiftString_MultiDigitLength()
    {
        var slice = new StringSlice("10abcdefghij");
        var result = slice.ExtractSwiftString(out bool isPunyCode);
        Assert.Equal("abcdefghij", result);
        Assert.False(isPunyCode);
    }

    // --- Substring ---

    [Fact]
    public void Substring_ReturnsSliceOfOriginal()
    {
        var slice = new StringSlice("abcdef");
        Assert.Equal("cde", slice.Substring(2, 3));
    }
}
