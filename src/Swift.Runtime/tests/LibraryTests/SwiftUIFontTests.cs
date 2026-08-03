// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.Versioning;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Construction tests for the <see cref="SwiftUI.Font"/> marshalling shell — the real
/// SwiftUI value-type wrapper, NOT the plain-data <c>Swift.SwiftFont</c> theme-bridge DTO
/// covered by <see cref="SwiftFontTests"/>.
/// </summary>
/// <remarks>
/// These exercise the <c>SBW_SwiftUI_Font_System</c> cdecl shim in the runtime library.
/// The weight/design cases matter beyond smoke coverage: the numeric codes are the ABI
/// contract with the shim's switch, so constructing every declared enum member proves the
/// managed enum and the Swift mapping agree on the full range.
/// </remarks>
[UnsupportedOSPlatform("maccatalyst")]
public class SwiftUIFontTests
{
    [Fact]
    public void System_ReturnsLivePayload()
    {
        using var font = SwiftUI.Font.System(17.0);

        Assert.NotNull(font);
        Assert.False(font.Payload.IsInvalid);
    }

    [Theory]
    [InlineData(SwiftUI.Font.Weight.UltraLight)]
    [InlineData(SwiftUI.Font.Weight.Thin)]
    [InlineData(SwiftUI.Font.Weight.Light)]
    [InlineData(SwiftUI.Font.Weight.Regular)]
    [InlineData(SwiftUI.Font.Weight.Medium)]
    [InlineData(SwiftUI.Font.Weight.Semibold)]
    [InlineData(SwiftUI.Font.Weight.Bold)]
    [InlineData(SwiftUI.Font.Weight.Heavy)]
    [InlineData(SwiftUI.Font.Weight.Black)]
    public void System_EveryDeclaredWeight_Constructs(SwiftUI.Font.Weight weight)
    {
        using var font = SwiftUI.Font.System(14.0, weight);

        Assert.False(font.Payload.IsInvalid);
    }

    [Theory]
    [InlineData(SwiftUI.Font.Design.Default)]
    [InlineData(SwiftUI.Font.Design.Serif)]
    [InlineData(SwiftUI.Font.Design.Rounded)]
    [InlineData(SwiftUI.Font.Design.Monospaced)]
    public void System_EveryDeclaredDesign_Constructs(SwiftUI.Font.Design design)
    {
        using var font = SwiftUI.Font.System(14.0, SwiftUI.Font.Weight.Regular, design);

        Assert.False(font.Payload.IsInvalid);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void System_InvalidSize_Throws(double size)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SwiftUI.Font.System(size));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(9)]
    public void System_UndeclaredWeight_Throws(int weightCode)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SwiftUI.Font.System(14.0, (SwiftUI.Font.Weight)weightCode));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    public void System_UndeclaredDesign_Throws(int designCode)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SwiftUI.Font.System(14.0, SwiftUI.Font.Weight.Regular, (SwiftUI.Font.Design)designCode));
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var font = SwiftUI.Font.System(12.0, SwiftUI.Font.Weight.Bold);

        font.Dispose();
        font.Dispose();
    }

    [Fact]
    public void System_ManyInstances_DoNotCorruptEachOther()
    {
        var fonts = new SwiftUI.Font[32];
        for (int i = 0; i < fonts.Length; i++)
        {
            fonts[i] = SwiftUI.Font.System(10.0 + i, (SwiftUI.Font.Weight)(i % 9), (SwiftUI.Font.Design)(i % 4));
        }

        var handles = new HashSet<IntPtr>();
        foreach (var font in fonts)
        {
            Assert.False(font.Payload.IsInvalid);
            Assert.True(handles.Add(font.Payload.DangerousGetHandle()));
        }

        foreach (var font in fonts)
        {
            font.Dispose();
        }
    }
}
