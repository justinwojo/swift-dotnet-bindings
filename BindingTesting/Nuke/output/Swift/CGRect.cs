// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.InteropServices;

namespace Swift;

/// <summary>
/// Represents CoreGraphics.CGRect type - a structure that contains the location and dimensions of a rectangle.
/// https://developer.apple.com/documentation/coregraphics/cgrect
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct CGRect
{
    /// <summary>
    /// A point that specifies the coordinates of the rectangle's origin.
    /// </summary>
    public CGPoint Origin;

    /// <summary>
    /// A size that specifies the height and width of the rectangle.
    /// </summary>
    public CGSize Size;

    /// <summary>
    /// The rectangle whose origin and size are both zero.
    /// </summary>
    public static readonly CGRect Zero = new(CGPoint.Zero, CGSize.Zero);

    /// <summary>
    /// Creates a rectangle with the specified origin and size.
    /// </summary>
    /// <param name="origin">A point that specifies the coordinates of the rectangle's origin.</param>
    /// <param name="size">A size that specifies the height and width of the rectangle.</param>
    public CGRect(CGPoint origin, CGSize size)
    {
        Origin = origin;
        Size = size;
    }

    /// <summary>
    /// Creates a rectangle with the specified coordinate and size values.
    /// </summary>
    /// <param name="x">The x-coordinate of the origin.</param>
    /// <param name="y">The y-coordinate of the origin.</param>
    /// <param name="width">A width value.</param>
    /// <param name="height">A height value.</param>
    public CGRect(double x, double y, double width, double height)
    {
        Origin = new CGPoint(x, y);
        Size = new CGSize(width, height);
    }

    /// <summary>
    /// Returns the x-coordinate of the origin.
    /// </summary>
    public double X => Origin.X;

    /// <summary>
    /// Returns the y-coordinate of the origin.
    /// </summary>
    public double Y => Origin.Y;

    /// <summary>
    /// Returns the width of the rectangle.
    /// </summary>
    public double Width => Size.Width;

    /// <summary>
    /// Returns the height of the rectangle.
    /// </summary>
    public double Height => Size.Height;

    /// <summary>
    /// Returns the minimum x-value of the rectangle.
    /// </summary>
    public double MinX => Math.Min(Origin.X, Origin.X + Size.Width);

    /// <summary>
    /// Returns the minimum y-value of the rectangle.
    /// </summary>
    public double MinY => Math.Min(Origin.Y, Origin.Y + Size.Height);

    /// <summary>
    /// Returns the maximum x-value of the rectangle.
    /// </summary>
    public double MaxX => Math.Max(Origin.X, Origin.X + Size.Width);

    /// <summary>
    /// Returns the maximum y-value of the rectangle.
    /// </summary>
    public double MaxY => Math.Max(Origin.Y, Origin.Y + Size.Height);

    /// <summary>
    /// Returns the x-value of the rectangle's midpoint.
    /// </summary>
    public double MidX => Origin.X + Size.Width / 2;

    /// <summary>
    /// Returns the y-value of the rectangle's midpoint.
    /// </summary>
    public double MidY => Origin.Y + Size.Height / 2;

    /// <summary>
    /// Returns a string representation of the rectangle.
    /// </summary>
    public override string ToString() => $"(({Origin.X}, {Origin.Y}), ({Size.Width}, {Size.Height}))";

    /// <summary>
    /// Determines whether two rectangles are equal.
    /// </summary>
    public static bool operator ==(CGRect left, CGRect right) =>
        left.Origin == right.Origin && left.Size == right.Size;

    /// <summary>
    /// Determines whether two rectangles are not equal.
    /// </summary>
    public static bool operator !=(CGRect left, CGRect right) => !(left == right);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is CGRect rect && this == rect;

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Origin, Size);
}
