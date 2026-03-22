// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

namespace Swift;

/// <summary>
/// Marks generated members whose signatures contain unsupported Swift types that were degraded to
/// <see cref="AnyType"/>. Check the <see cref="Reason"/> property for why the type could not be
/// projected, and <see cref="SwiftType"/> for the original Swift type name.
/// See the binding report (<c>binding-report.json</c>) for full details on all type fallbacks.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Method | AttributeTargets.Parameter)]
public sealed class UnsupportedSwiftTypeAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="UnsupportedSwiftTypeAttribute"/> class.
    /// </summary>
    /// <param name="reason">Reason for the fallback projection.</param>
    /// <param name="swiftType">The original Swift type that could not be represented directly.</param>
    public UnsupportedSwiftTypeAttribute(string reason, string? swiftType = null)
    {
        Reason = reason;
        SwiftType = swiftType;
    }

    /// <summary>
    /// Gets the reason for the fallback projection.
    /// </summary>
    public string Reason { get; }

    /// <summary>
    /// Gets the original Swift type that triggered the fallback.
    /// </summary>
    public string? SwiftType { get; }
}
