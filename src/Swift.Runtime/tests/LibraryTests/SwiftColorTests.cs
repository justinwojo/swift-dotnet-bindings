// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Swift;
using Xunit;

namespace BindingsGeneration.Tests;

public class SwiftColorTests
{
    [Fact]
    public void Constructor_DefaultAlpha_IsOne()
    {
        var color = new SwiftColor(0.5, 0.6, 0.7);
        Assert.Equal(0.5, color.R);
        Assert.Equal(0.6, color.G);
        Assert.Equal(0.7, color.B);
        Assert.Equal(1.0, color.A);
    }

    [Fact]
    public void Constructor_ExplicitAlpha()
    {
        var color = new SwiftColor(0.1, 0.2, 0.3, 0.4);
        Assert.Equal(0.1, color.R);
        Assert.Equal(0.2, color.G);
        Assert.Equal(0.3, color.B);
        Assert.Equal(0.4, color.A);
    }

    [Fact]
    public void FromHex_ParsesRGB()
    {
        var color = SwiftColor.FromHex(0xFF8000);
        Assert.Equal(1.0, color.R);
        Assert.Equal(128.0 / 255.0, color.G, precision: 10);
        Assert.Equal(0.0, color.B);
        Assert.Equal(1.0, color.A);
    }

    [Fact]
    public void FromHex_WithAlpha()
    {
        var color = SwiftColor.FromHex(0x1A73E8, 0.5);
        Assert.Equal(26.0 / 255.0, color.R, precision: 10);
        Assert.Equal(115.0 / 255.0, color.G, precision: 10);
        Assert.Equal(232.0 / 255.0, color.B, precision: 10);
        Assert.Equal(0.5, color.A);
    }

    [Fact]
    public void FromHex_Black()
    {
        var color = SwiftColor.FromHex(0x000000);
        Assert.Equal(0.0, color.R);
        Assert.Equal(0.0, color.G);
        Assert.Equal(0.0, color.B);
        Assert.Equal(1.0, color.A);
    }

    [Fact]
    public void FromHex_White()
    {
        var color = SwiftColor.FromHex(0xFFFFFF);
        Assert.Equal(1.0, color.R);
        Assert.Equal(1.0, color.G);
        Assert.Equal(1.0, color.B);
    }

    [Fact]
    public void NamedColor_White()
    {
        var c = SwiftColor.White;
        Assert.Equal(new SwiftColor(1, 1, 1), c);
    }

    [Fact]
    public void NamedColor_Black()
    {
        var c = SwiftColor.Black;
        Assert.Equal(new SwiftColor(0, 0, 0), c);
    }

    [Fact]
    public void NamedColor_Clear()
    {
        var c = SwiftColor.Clear;
        Assert.Equal(new SwiftColor(0, 0, 0, 0), c);
    }

    [Fact]
    public void NamedColor_Red()
    {
        var c = SwiftColor.Red;
        Assert.Equal(1.0, c.R);
        Assert.Equal(0.0, c.G);
        Assert.Equal(0.0, c.B);
    }

    [Fact]
    public void NamedColor_Green()
    {
        var c = SwiftColor.Green;
        Assert.Equal(0.0, c.R);
        Assert.Equal(1.0, c.G);
        Assert.Equal(0.0, c.B);
    }

    [Fact]
    public void NamedColor_Blue()
    {
        var c = SwiftColor.Blue;
        Assert.Equal(0.0, c.R);
        Assert.Equal(0.0, c.G);
        Assert.Equal(1.0, c.B);
    }

    [Fact]
    public void OutOfRange_PassesThrough()
    {
        // SwiftUI.Color accepts out-of-range values; bridge should not clamp
        var color = new SwiftColor(1.5, -0.5, 2.0, 3.0);
        Assert.Equal(1.5, color.R);
        Assert.Equal(-0.5, color.G);
        Assert.Equal(2.0, color.B);
        Assert.Equal(3.0, color.A);
    }

    [Fact]
    public void RecordEquality_Works()
    {
        var a = new SwiftColor(0.1, 0.2, 0.3, 0.4);
        var b = new SwiftColor(0.1, 0.2, 0.3, 0.4);
        var c = new SwiftColor(0.1, 0.2, 0.3, 0.5);
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }
}
