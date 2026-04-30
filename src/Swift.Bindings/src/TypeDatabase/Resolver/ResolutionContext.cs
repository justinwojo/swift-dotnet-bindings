// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Context threaded into <see cref="IResolutionStrategy"/> implementations.
/// Carries the database the strategy may consult plus optional cross-module
/// signals that drift today as nullable arguments through the legacy
/// <c>TryGetTypeRecord</c>/<c>GetTypeRecordOrAnyType</c> paths.
/// </summary>
/// <param name="Database">Type database the strategy may consult for follow-up lookups.</param>
/// <param name="CurrentlyGeneratingModule">
/// Module currently being emitted, when known. Always null on the legacy paths
/// today (the supplement regeneration uses a separate pipeline). Reserved so
/// later strategies (Apple supplement, ObjC bridging) can carry the value
/// without re-shaping the context.
/// </param>
public sealed record ResolutionContext(
    ITypeDatabase Database,
    string? CurrentlyGeneratingModule = null);
