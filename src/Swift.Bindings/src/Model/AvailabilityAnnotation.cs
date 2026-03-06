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
);
