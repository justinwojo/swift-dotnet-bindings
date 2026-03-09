// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.InteropServices;

namespace Swift;

/// <summary>
/// Represents CoreGraphics.CGPoint type - a point in a two-dimensional coordinate system.
/// https://developer.apple.com/documentation/coregraphics/cgpoint
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct CGPoint
{
    /// <summary>
    /// The x-coordinate of the point.
    /// </summary>
    public double X;

    /// <summary>
    /// The y-coordinate of the point.
    /// </summary>
    public double Y;

    /// <summary>
    /// Creates a point with coordinates (0,0).
    /// </summary>
    public static readonly CGPoint Zero = new(0, 0);

    /// <summary>
    /// Creates a point with the specified coordinates.
    /// </summary>
    /// <param name="x">The x-coordinate of the point.</param>
    /// <param name="y">The y-coordinate of the point.</param>
    public CGPoint(double x, double y)
    {
        X = x;
        Y = y;
    }

    /// <summary>
    /// Returns a string representation of the point.
    /// </summary>
    public override string ToString() => $"({X}, {Y})";

    /// <summary>
    /// Determines whether two points are equal.
    /// </summary>
    public static bool operator ==(CGPoint left, CGPoint right) =>
        left.X == right.X && left.Y == right.Y;

    /// <summary>
    /// Determines whether two points are not equal.
    /// </summary>
    public static bool operator !=(CGPoint left, CGPoint right) => !(left == right);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is CGPoint point && this == point;

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(X, Y);

#if IOS || TVOS || MACCATALYST || MACOS
    /// <summary>
    /// Implicitly converts a CoreGraphics.CGPoint to a Swift.CGPoint.
    /// </summary>
    public static implicit operator CGPoint(CoreGraphics.CGPoint point) => new(point.X, point.Y);

    /// <summary>
    /// Implicitly converts a Swift.CGPoint to a CoreGraphics.CGPoint.
    /// </summary>
    public static implicit operator CoreGraphics.CGPoint(CGPoint point) => new(point.X, point.Y);
#endif
}
