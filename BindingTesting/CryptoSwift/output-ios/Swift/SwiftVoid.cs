// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace Swift;

/// <summary>
/// A unit type representing void in contexts where a type is required.
/// Used for throwing closures with void return types, e.g., <c>() throws -&gt; Void</c>
/// maps to <c>Func&lt;SwiftResult&lt;SwiftVoid, SwiftError&gt;&gt;</c>.
/// </summary>
public readonly struct SwiftVoid
{
    /// <summary>
    /// The singleton instance of SwiftVoid.
    /// </summary>
    public static readonly SwiftVoid Value = default;
}
