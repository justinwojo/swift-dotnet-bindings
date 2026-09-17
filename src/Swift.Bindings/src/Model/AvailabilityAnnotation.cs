// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Represents a parsed @available annotation from a .swiftinterface file.
/// </summary>
public record AvailabilityAnnotation(
    string? Platform,
    string? IntroducedVersion,
    string? DeprecatedVersion,
    string? ObsoletedVersion,
    bool IsUnconditionallyDeprecated,
    bool IsUnconditionallyUnavailable,
    string? Message,
    string? Renamed
)
{
    /// <summary>
    /// True when the floor was not declared by the library but derived from what the binding
    /// needs at runtime to marshal the member's signature — a parameterized protocol existential
    /// such as <c>any Requestable&lt;Value, Failure&gt;</c> has no runtime metadata below iOS 16.
    /// Swift can call such a member at the library's own deployment target, so a witness of a
    /// protocol requirement cannot carry this floor as <c>@available</c> (Swift rejects a witness
    /// less available than its requirement); it guards the witness body instead.
    /// </summary>
    public bool IsRuntimeSupportFloor { get; init; }
}
