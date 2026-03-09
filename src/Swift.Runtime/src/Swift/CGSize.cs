// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.InteropServices;

namespace Swift;

/// <summary>
/// Represents CoreGraphics.CGSize type - a structure that contains width and height values.
/// https://developer.apple.com/documentation/coregraphics/cgsize
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct CGSize
{
    /// <summary>
    /// A width value.
    /// </summary>
    public double Width;

    /// <summary>
    /// A height value.
    /// </summary>
    public double Height;

    /// <summary>
    /// The size whose width and height are both zero.
    /// </summary>
    public static readonly CGSize Zero = new(0, 0);

    /// <summary>
    /// Creates a size with the specified width and height.
    /// </summary>
    /// <param name="width">A width value.</param>
    /// <param name="height">A height value.</param>
    public CGSize(double width, double height)
    {
        Width = width;
        Height = height;
    }

    /// <summary>
    /// Returns a string representation of the size.
    /// </summary>
    public override string ToString() => $"({Width} x {Height})";

    /// <summary>
    /// Determines whether two sizes are equal.
    /// </summary>
    public static bool operator ==(CGSize left, CGSize right) =>
        left.Width == right.Width && left.Height == right.Height;

    /// <summary>
    /// Determines whether two sizes are not equal.
    /// </summary>
    public static bool operator !=(CGSize left, CGSize right) => !(left == right);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is CGSize size && this == size;

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Width, Height);

#if IOS || TVOS || MACCATALYST || MACOS
    /// <summary>
    /// Implicitly converts a CoreGraphics.CGSize to a Swift.CGSize.
    /// </summary>
    public static implicit operator CGSize(CoreGraphics.CGSize size) => new(size.Width, size.Height);

    /// <summary>
    /// Implicitly converts a Swift.CGSize to a CoreGraphics.CGSize.
    /// </summary>
    public static implicit operator CoreGraphics.CGSize(CGSize size) => new(size.Width, size.Height);
#endif
}
