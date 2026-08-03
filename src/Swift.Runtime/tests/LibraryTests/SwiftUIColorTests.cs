// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.Versioning;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Construction tests for the <see cref="SwiftUI.Color"/> marshalling shell — the real
/// SwiftUI value-type wrapper, NOT the plain-data <c>Swift.SwiftColor</c> theme-bridge DTO
/// covered by <see cref="SwiftColorTests"/>.
/// </summary>
/// <remarks>
/// These exercise the <c>SBW_SwiftUI_Color_Create</c> cdecl shim in the runtime library.
/// The test project copies the macOS xcframework slice next to the host, so the shim
/// resolves on a plain console host. The class-level attribute matches the factory's
/// <c>[UnsupportedOSPlatform("maccatalyst")]</c>; the host TFM is not Catalyst.
/// </remarks>
[UnsupportedOSPlatform("maccatalyst")]
public class SwiftUIColorTests
{
    [Fact]
    public void Create_ReturnsLivePayload()
    {
        using var color = SwiftUI.Color.Create(0.25, 0.5, 0.75, 1.0);

        Assert.NotNull(color);
        Assert.False(color.Payload.IsInvalid);
    }

    [Fact]
    public void Create_DefaultOpacity_IsOpaque()
    {
        // Exercises the optional-parameter path: three-argument construction must still
        // reach the four-argument shim.
        using var color = SwiftUI.Color.Create(1.0, 0.0, 0.0);

        Assert.False(color.Payload.IsInvalid);
    }

    [Theory]
    [InlineData(0.0, 0.0, 0.0, 0.0)]
    [InlineData(1.0, 1.0, 1.0, 1.0)]
    // Out-of-range components are extended-range sRGB to SwiftUI, not an error.
    [InlineData(-0.5, 1.5, 0.5, 0.5)]
    public void Create_AcceptsComponentRange(double red, double green, double blue, double opacity)
    {
        using var color = SwiftUI.Color.Create(red, green, blue, opacity);

        Assert.False(color.Payload.IsInvalid);
    }

    [Theory]
    [InlineData(double.NaN, 0.0, 0.0, 1.0)]
    [InlineData(0.0, double.PositiveInfinity, 0.0, 1.0)]
    [InlineData(0.0, 0.0, double.NegativeInfinity, 1.0)]
    [InlineData(0.0, 0.0, 0.0, double.NaN)]
    public void Create_NonFiniteComponent_Throws(double red, double green, double blue, double opacity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SwiftUI.Color.Create(red, green, blue, opacity));
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var color = SwiftUI.Color.Create(0.1, 0.2, 0.3, 0.4);

        color.Dispose();
        color.Dispose();
    }

    [Fact]
    public void Create_ManyInstances_DoNotCorruptEachOther()
    {
        // Each instance owns a distinct buffer; the value-witness destroy on disposal must
        // release the refcounted color provider without touching a sibling's storage.
        var colors = new SwiftUI.Color[32];
        for (int i = 0; i < colors.Length; i++)
        {
            colors[i] = SwiftUI.Color.Create(i / 32.0, 1.0 - (i / 32.0), 0.5, 1.0);
        }

        var handles = new HashSet<IntPtr>();
        foreach (var color in colors)
        {
            Assert.False(color.Payload.IsInvalid);
            Assert.True(handles.Add(color.Payload.DangerousGetHandle()));
        }

        foreach (var color in colors)
        {
            color.Dispose();
        }
    }
}
