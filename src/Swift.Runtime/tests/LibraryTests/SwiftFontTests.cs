// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Swift;
using Xunit;

namespace BindingsGeneration.Tests;

public class SwiftFontTests
{
    [Fact]
    public void Custom_SetsNameAndSize()
    {
        var font = SwiftFont.Custom("Helvetica", 14);
        Assert.Equal("Helvetica", font.FontName);
        Assert.Equal(14.0, font.Size);
        Assert.Equal(SwiftFontWeight.Regular, font.Weight);
        Assert.Equal(SwiftFontDesign.Default, font.Design);
        Assert.False(font.IsSystem);
    }

    [Fact]
    public void System_DefaultWeightAndDesign()
    {
        var font = SwiftFont.System(16);
        Assert.Null(font.FontName);
        Assert.Equal(16.0, font.Size);
        Assert.Equal(SwiftFontWeight.Regular, font.Weight);
        Assert.Equal(SwiftFontDesign.Default, font.Design);
        Assert.True(font.IsSystem);
    }

    [Fact]
    public void System_WithWeightAndDesign()
    {
        var font = SwiftFont.System(20, SwiftFontWeight.Bold, SwiftFontDesign.Rounded);
        Assert.Null(font.FontName);
        Assert.Equal(20.0, font.Size);
        Assert.Equal(SwiftFontWeight.Bold, font.Weight);
        Assert.Equal(SwiftFontDesign.Rounded, font.Design);
    }

    [Fact]
    public void Preset_Body()
    {
        var font = SwiftFont.Body;
        Assert.True(font.IsSystem);
        Assert.Equal(17.0, font.Size);
        Assert.Equal(SwiftFontWeight.Regular, font.Weight);
    }

    [Fact]
    public void Preset_Headline()
    {
        var font = SwiftFont.Headline;
        Assert.True(font.IsSystem);
        Assert.Equal(17.0, font.Size);
        Assert.Equal(SwiftFontWeight.Semibold, font.Weight);
    }

    [Fact]
    public void Preset_LargeTitle()
    {
        var font = SwiftFont.LargeTitle;
        Assert.Equal(34.0, font.Size);
    }

    [Fact]
    public void Preset_Title()
    {
        var font = SwiftFont.Title;
        Assert.Equal(28.0, font.Size);
    }

    [Fact]
    public void Preset_Caption()
    {
        var font = SwiftFont.Caption;
        Assert.Equal(12.0, font.Size);
    }

    [Fact]
    public void Preset_Caption2()
    {
        var font = SwiftFont.Caption2;
        Assert.Equal(11.0, font.Size);
    }

    [Fact]
    public void FontWeight_EnumValues()
    {
        Assert.Equal(0, (int)SwiftFontWeight.UltraLight);
        Assert.Equal(3, (int)SwiftFontWeight.Regular);
        Assert.Equal(5, (int)SwiftFontWeight.Semibold);
        Assert.Equal(6, (int)SwiftFontWeight.Bold);
        Assert.Equal(8, (int)SwiftFontWeight.Black);
    }

    [Fact]
    public void FontDesign_EnumValues()
    {
        Assert.Equal(0, (int)SwiftFontDesign.Default);
        Assert.Equal(1, (int)SwiftFontDesign.Rounded);
        Assert.Equal(2, (int)SwiftFontDesign.Monospaced);
        Assert.Equal(3, (int)SwiftFontDesign.Serif);
    }
}
