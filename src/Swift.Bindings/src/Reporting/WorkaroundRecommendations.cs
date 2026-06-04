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
        SkipReason.SwiftUIView =>
            "SwiftUI View type detected. Auto-generated bridge files are available in the output directory.",
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
        SkipReason.GenericTypeCallback =>
            "Write a Swift wrapper that avoids closures or async in generic type members.",
        SkipReason.ActorIsolatedAsyncStream =>
            "Custom actor AsyncStream properties cannot be wrapped (requires async dispatch through actor executor). Use a Swift wrapper that accesses the property through actor method dispatch.",
        SkipReason.StaticProtocolMember =>
            "Static protocol members cannot be dispatched through witness tables. Use a Swift wrapper.",
        SkipReason.SynthesizedCodable =>
            "Synthesized Codable members are pruned. Use NSCoding or manual serialization from C#.",
        SkipReason.UnderscorePrefixInternal =>
            "Underscore-prefixed type treated as internal. If this type is needed, write a Swift wrapper that exposes the functionality through a public non-underscored type.",
        SkipReason.MissingHandler =>
            "No handler exists for this declaration kind.",
        SkipReason.UnsupportedType =>
            "Ensure the type is exported in the module's public ABI.",
        SkipReason.AncestorSkipped =>
            "Parent type was skipped; nested declarations are unreachable until the parent is supported.",
        SkipReason.ActorIsolatedConstructor =>
            "Constructor is on a custom global-actor-isolated type. The synchronous @_cdecl wrapper cannot call into the actor's executor. Construct the type from a Swift wrapper that hops to the actor, or expose a nonisolated factory.",
        SkipReason.MissingWrapperSymbol =>
            "The Swift @_cdecl wrapper symbol was stripped during wrapper compilation, so the corresponding C# P/Invoke was suppressed to avoid runtime DllNotFoundException. Inspect the wrapper post-processor output for the underlying cause.",
        SkipReason.SuppressedProxyMethodBody =>
            "The method body referenced a proxy class whose EveryProtocol conformance was not emitted. Once the proxy can be emitted (add support for the missing requirements), the method body is restored.",
        SkipReason.Pattern2InternalTypeReach =>
            "Member signature exposes a @usableFromInline internal type. Refactor the Swift API to use a public type, or expose the functionality through a public Swift wrapper.",
        SkipReason.IndeterminateStructLayout =>
            "The frozen struct has a stored field that is a generic value-type instantiation (e.g. ClosedRange<Int>, Result<T,E>) whose inline size depends on its type arguments and cannot be derived cross-compile. Write a Swift wrapper that exposes the data through a supported, concretely-sized type.",
        SkipReason.Unknown =>
            "Investigate the specific member in the generator output.",
        _ => null,
    };

    /// <summary>
    /// Returns a short human-readable description of the given skip reason
    /// for console output, or null if no description is available.
    /// </summary>
    public static string? GetDescription(SkipReason reason) => reason switch
    {
        SkipReason.UnsupportedExistential =>
            "protocol-typed parameter/return not yet projected",
        SkipReason.AnyTypeFallback =>
            "type could not be resolved to a concrete projection",
        SkipReason.UnsupportedSignature =>
            "parameter or return type not yet supported",
        SkipReason.AsyncProperty =>
            "async properties require wrapper conversion",
        SkipReason.SwiftUIConstraint =>
            "generic constraint on SwiftUI View type",
        SkipReason.SwiftUIView =>
            "SwiftUI View type (bridge file generated instead)",
        SkipReason.CombineFramework =>
            "Combine framework type excluded",
        SkipReason.GenericProtocolConstraint =>
            "protocol with associated types used as constraint",
        SkipReason.UnsatisfiedGenericConstraint =>
            "generic constraint could not be satisfied",
        SkipReason.UnsupportedClosure =>
            "closure signature not yet supported",
        SkipReason.UnsupportedAsyncStream =>
            "async stream element type not supported",
        SkipReason.DuplicateSignature =>
            "C# signature collides with another member",
        SkipReason.GenericTypeCallback =>
            "closure or async in generic type member",
        SkipReason.ActorIsolatedAsyncStream =>
            "custom actor AsyncStream property (requires async dispatch)",
        SkipReason.StaticProtocolMember =>
            "static protocol member cannot be dispatched",
        SkipReason.SynthesizedCodable =>
            "synthesized Codable member pruned by design",
        SkipReason.UnderscorePrefixInternal =>
            "underscore-prefixed type treated as internal",
        SkipReason.MissingHandler =>
            "no handler for this declaration kind",
        SkipReason.UnsupportedType =>
            "type not exported in the module's public ABI",
        SkipReason.AncestorSkipped =>
            "nested type whose parent was skipped",
        SkipReason.ActorIsolatedConstructor =>
            "constructor on a custom global-actor-isolated type (synchronous wrapper unsafe)",
        SkipReason.MissingWrapperSymbol =>
            "P/Invoke removed because the Swift wrapper symbol was stripped during wrapper compilation",
        SkipReason.SuppressedProxyMethodBody =>
            "method body removed because it constructed a proxy class whose conformance was suppressed",
        SkipReason.Pattern2InternalTypeReach =>
            "member signature reaches an @usableFromInline internal (or otherwise-suppressed) type",
        SkipReason.IndeterminateStructLayout =>
            "frozen struct stored field has an indeterminate cross-compile Buffer layout (generic value-type instantiation)",
        SkipReason.Unknown =>
            "unclassified skip reason",
        _ => null,
    };
}
