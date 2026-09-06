// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using Swift.Runtime;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// The conversion a generated narrowed property reads through. Both ends of each range matter:
/// the value that fits has to arrive unchanged, and the one that does not has to be reported
/// rather than wrapped — including the signed low end, which a range check written as an upper
/// bound alone would let through as a wrapped positive.
/// </summary>
public class NativeIntegerNarrowingTests
{
    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(-1L)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    public void ToInt32_ValueInsideRange_ReturnsItUnchanged(long value)
    {
        Assert.Equal((int)value, NativeIntegerNarrowing.ToInt32((nint)value, "Offset", "OffsetNative"));
    }

    [Theory]
    [InlineData((long)int.MaxValue + 1)]
    [InlineData(4294967296L)]
    [InlineData(long.MaxValue)]
    public void ToInt32_ValueAboveRange_Throws(long value)
    {
        if (IntPtr.Size < 8)
            return; // A 32-bit nint cannot hold the value, so there is nothing to narrow.

        Assert.Throws<OverflowException>(
            () => NativeIntegerNarrowing.ToInt32((nint)value, "Offset", "OffsetNative"));
    }

    [Theory]
    [InlineData((long)int.MinValue - 1)]
    [InlineData(-4294967296L)]
    [InlineData(long.MinValue)]
    public void ToInt32_ValueBelowRange_Throws(long value)
    {
        if (IntPtr.Size < 8)
            return;

        // Wrapping would turn each of these into a plausible-looking positive or small negative,
        // which is exactly the reading a caller cannot detect.
        Assert.Throws<OverflowException>(
            () => NativeIntegerNarrowing.ToInt32((nint)value, "Offset", "OffsetNative"));
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(1UL)]
    [InlineData(uint.MaxValue)]
    public void ToUInt32_ValueInsideRange_ReturnsItUnchanged(ulong value)
    {
        Assert.Equal((uint)value, NativeIntegerNarrowing.ToUInt32((nuint)value, "Limit", "LimitNative"));
    }

    [Theory]
    [InlineData((ulong)uint.MaxValue + 1)]
    [InlineData(1UL << 40)]
    [InlineData(ulong.MaxValue)]
    public void ToUInt32_ValueAboveRange_Throws(ulong value)
    {
        if (IntPtr.Size < 8)
            return;

        Assert.Throws<OverflowException>(
            () => NativeIntegerNarrowing.ToUInt32((nuint)value, "Limit", "LimitNative"));
    }

    [Fact]
    public void ToInt32_NullOptional_PassesNullThrough()
    {
        Assert.Null(NativeIntegerNarrowing.ToInt32((nint?)null, "Offset", "OffsetNative"));
    }

    [Fact]
    public void ToUInt32_NullOptional_PassesNullThrough()
    {
        Assert.Null(NativeIntegerNarrowing.ToUInt32((nuint?)null, "Limit", "LimitNative"));
    }

    [Fact]
    public void ToInt32_PresentOptional_NarrowsLikeTheNonNullableOverload()
    {
        Assert.Equal(7, NativeIntegerNarrowing.ToInt32((nint?)(nint)7, "Offset", "OffsetNative"));
    }

    [Fact]
    public void ToUInt32_PresentOptional_NarrowsLikeTheNonNullableOverload()
    {
        Assert.Equal(7u, NativeIntegerNarrowing.ToUInt32((nuint?)(nuint)7, "Limit", "LimitNative"));
    }

    [Fact]
    public void ToInt32_PresentOptionalAboveRange_Throws()
    {
        if (IntPtr.Size < 8)
            return;

        long aboveInt = (long)int.MaxValue + 1;
        Assert.Throws<OverflowException>(
            () => NativeIntegerNarrowing.ToInt32((nint?)(nint)aboveInt, "Offset", "OffsetNative"));
    }

    [Fact]
    public void ToUInt32_PresentOptionalAboveRange_Throws()
    {
        if (IntPtr.Size < 8)
            return;

        ulong aboveUInt = (ulong)uint.MaxValue + 1;
        Assert.Throws<OverflowException>(
            () => NativeIntegerNarrowing.ToUInt32((nuint?)(nuint)aboveUInt, "Limit", "LimitNative"));
    }

    [Fact]
    public void OverflowMessage_NamesTheMember_TheValue_AndTheCompanion()
    {
        if (IntPtr.Size < 8)
            return;

        // The message is the only place a caller learns a lossless read exists, so all three
        // pieces have to be in it.
        long twoToThe32 = 4294967296L;
        var ex = Assert.Throws<OverflowException>(
            () => NativeIntegerNarrowing.ToInt32((nint)twoToThe32, "SizeLimit", "SizeLimitNative"));

        Assert.Contains("SizeLimit", ex.Message);
        Assert.Contains("4294967296", ex.Message);
        Assert.Contains("SizeLimitNative", ex.Message);
        Assert.Contains("int", ex.Message);
    }

    [Fact]
    public void OverflowMessage_WithoutACompanion_SaysWhyRatherThanNamingNothing()
    {
        if (IntPtr.Size < 8)
            return;

        // Some narrowed reads have no companion to point at — a protocol proxy's interface
        // property, for instance. The message still has to explain the loss.
        ulong aboveUInt = (ulong)uint.MaxValue + 1;
        var ex = Assert.Throws<OverflowException>(
            () => NativeIntegerNarrowing.ToUInt32((nuint)aboveUInt, "Position"));

        Assert.Contains("Position", ex.Message);
        Assert.Contains("pointer-width", ex.Message);
        Assert.DoesNotContain("Read '", ex.Message);
    }
}
