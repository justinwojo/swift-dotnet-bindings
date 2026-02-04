// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Maps skip reasons to actionable workaround recommendations for the binding report.
/// </summary>
public static class WorkaroundRecommendations
{
    /// <summary>
    /// Returns a human-readable workaround recommendation for the given skip reason,
    /// or null if no specific recommendation is available.
    /// </summary>
    public static string? GetRecommendation(SkipReason reason) => reason switch
    {
        SkipReason.UnsupportedExistential =>
            "Write a Swift wrapper that accepts concrete types or use a simplified constructor.",
        SkipReason.AnyTypeFallback =>
            "Use concrete bound generic types instead of Any where possible.",
        SkipReason.UnsupportedSignature =>
            "Write a Swift wrapper with a simplified signature.",
        SkipReason.AsyncProperty =>
            "Expose the async property as an async method via a Swift wrapper.",
        SkipReason.SwiftUIConstraint =>
            "SwiftUI types are excluded. Use Swift wrappers to bridge SwiftUI functionality.",
        SkipReason.CombineFramework =>
            "Combine types are excluded. Use Swift wrappers that convert to async/callback APIs.",
        SkipReason.GenericProtocolConstraint =>
            "Use a Swift wrapper with type erasure for protocols with associated types.",
        SkipReason.UnsatisfiedGenericConstraint =>
            "Use a Swift wrapper that constructs the generic type internally.",
        SkipReason.UnsupportedClosure =>
            "Write a Swift wrapper that converts to a supported closure shape.",
        SkipReason.UnsupportedAsyncStream =>
            "Write a Swift wrapper that converts stream elements to a supported type.",
        SkipReason.DuplicateSignature =>
            "Rename one member via a Swift extension to disambiguate.",
        SkipReason.MissingHandler =>
            "No handler exists for this declaration kind.",
        SkipReason.UnsupportedType =>
            "Ensure the type is exported in the module's public ABI.",
        SkipReason.Unknown =>
            "Investigate the specific member in the generator output.",
        _ => null,
    };
}
