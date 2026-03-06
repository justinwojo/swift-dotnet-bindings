// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration.ObjC;

public sealed record ObjCAvailability
{
    public required string Platform { get; init; }
    public string? IntroducedVersion { get; init; }
    public string? DeprecatedVersion { get; init; }
    public string? ObsoletedVersion { get; init; }
    public bool IsUnavailable { get; init; }
    public string? Message { get; init; }
}
