// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

namespace Swift;

/// <summary>
/// Records the original Swift type name for parameters and return values that were projected
/// as <see cref="AnyType"/> because the generator could not resolve the concrete type.
/// This allows consumers to see what the original Swift type was for diagnostic purposes.
/// </summary>
[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.ReturnValue, AllowMultiple = false)]
public sealed class OriginalSwiftTypeAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="OriginalSwiftTypeAttribute"/> class.
    /// </summary>
    /// <param name="swiftTypeName">The original Swift type name that could not be projected.</param>
    public OriginalSwiftTypeAttribute(string swiftTypeName)
    {
        SwiftTypeName = swiftTypeName;
    }

    /// <summary>
    /// Gets the original Swift type name that could not be projected.
    /// </summary>
    public string SwiftTypeName { get; }
}
