// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace Swift;

/// <summary>
/// Represents a font for SwiftUI/UIKit theme bridging.
/// Passed to Swift as: font name (UTF-8), size, weight enum, design enum, isSystem flag.
/// Values are not validated — they produce the same result as calling the Swift API directly.
/// </summary>
public readonly struct SwiftFont
{
    /// <summary>The font family name, or null for system font.</summary>
    public string? FontName { get; }

    /// <summary>The font size in points.</summary>
    public double Size { get; }

    /// <summary>The font weight.</summary>
    public SwiftFontWeight Weight { get; }

    /// <summary>The font design.</summary>
    public SwiftFontDesign Design { get; }

    /// <summary>Whether this is a system font (FontName is null).</summary>
    public bool IsSystem => FontName == null;

    private SwiftFont(string? fontName, double size, SwiftFontWeight weight, SwiftFontDesign design)
    {
        FontName = fontName;
        Size = size;
        Weight = weight;
        Design = design;
    }

    /// <summary>
    /// Creates a custom font with the specified family name and size.
    /// </summary>
    public static SwiftFont Custom(string name, double size) =>
        new(name, size, SwiftFontWeight.Regular, SwiftFontDesign.Default);

    /// <summary>
    /// Creates a system font with the specified size, weight, and design.
    /// </summary>
    public static SwiftFont System(double size, SwiftFontWeight weight = SwiftFontWeight.Regular,
        SwiftFontDesign design = SwiftFontDesign.Default) =>
        new(null, size, weight, design);

    /// <summary>Large title (34pt regular).</summary>
    public static SwiftFont LargeTitle => System(34, SwiftFontWeight.Regular);

    /// <summary>Title (28pt regular).</summary>
    public static SwiftFont Title => System(28, SwiftFontWeight.Regular);

    /// <summary>Title 2 (22pt regular).</summary>
    public static SwiftFont Title2 => System(22, SwiftFontWeight.Regular);

    /// <summary>Title 3 (20pt regular).</summary>
    public static SwiftFont Title3 => System(20, SwiftFontWeight.Regular);

    /// <summary>Headline (17pt semibold).</summary>
    public static SwiftFont Headline => System(17, SwiftFontWeight.Semibold);

    /// <summary>Body (17pt regular).</summary>
    public static SwiftFont Body => System(17, SwiftFontWeight.Regular);

    /// <summary>Callout (16pt regular).</summary>
    public static SwiftFont Callout => System(16, SwiftFontWeight.Regular);

    /// <summary>Subheadline (15pt regular).</summary>
    public static SwiftFont Subheadline => System(15, SwiftFontWeight.Regular);

    /// <summary>Footnote (13pt regular).</summary>
    public static SwiftFont Footnote => System(13, SwiftFontWeight.Regular);

    /// <summary>Caption (12pt regular).</summary>
    public static SwiftFont Caption => System(12, SwiftFontWeight.Regular);

    /// <summary>Caption 2 (11pt regular).</summary>
    public static SwiftFont Caption2 => System(11, SwiftFontWeight.Regular);
}

/// <summary>
/// Font weight values matching SwiftUI.Font.Weight cases.
/// </summary>
public enum SwiftFontWeight : int
{
    UltraLight = 0,
    Thin,
    Light,
    Regular,
    Medium,
    Semibold,
    Bold,
    Heavy,
    Black
}

/// <summary>
/// Font design values matching SwiftUI.Font.Design cases.
/// </summary>
public enum SwiftFontDesign : int
{
    Default = 0,
    Rounded,
    Monospaced,
    Serif
}
