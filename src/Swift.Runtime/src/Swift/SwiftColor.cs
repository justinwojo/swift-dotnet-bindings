// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace Swift;

/// <summary>
/// Represents a color for SwiftUI/UIKit theme bridging.
/// Passed to Swift as four Double values (RGBA). Values are not clamped —
/// SwiftUI.Color and UIColor handle out-of-range values at render time.
/// </summary>
public readonly record struct SwiftColor(double R, double G, double B, double A = 1.0)
{
    /// <summary>
    /// Creates a color from a hex value (0xRRGGBB) with full opacity.
    /// </summary>
    public static SwiftColor FromHex(uint hex) =>
        new((hex >> 16 & 0xFF) / 255.0, (hex >> 8 & 0xFF) / 255.0, (hex & 0xFF) / 255.0);

    /// <summary>
    /// Creates a color from a hex value (0xRRGGBB) with the specified alpha.
    /// </summary>
    public static SwiftColor FromHex(uint hex, double alpha) =>
        new((hex >> 16 & 0xFF) / 255.0, (hex >> 8 & 0xFF) / 255.0, (hex & 0xFF) / 255.0, alpha);

    /// <summary>White (1, 1, 1, 1).</summary>
    public static SwiftColor White => new(1, 1, 1);

    /// <summary>Black (0, 0, 0, 1).</summary>
    public static SwiftColor Black => new(0, 0, 0);

    /// <summary>Transparent (0, 0, 0, 0).</summary>
    public static SwiftColor Clear => new(0, 0, 0, 0);

    /// <summary>Red (1, 0, 0, 1).</summary>
    public static SwiftColor Red => new(1, 0, 0);

    /// <summary>Green (0, 1, 0, 1).</summary>
    public static SwiftColor Green => new(0, 1, 0);

    /// <summary>Blue (0, 0, 1, 1).</summary>
    public static SwiftColor Blue => new(0, 0, 1);
}
