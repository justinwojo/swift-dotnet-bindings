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
        SkipReason.UnsupportedThrowingAsyncStream =>
            "AsyncThrowingStream is now bound directly (it rethrows through await foreach); no workaround is needed. This reason is retired and should not appear in new reports.",
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
        SkipReason.ParentTypeSuppressed =>
            "The type declaring this member is suppressed as a whole, so the member was never reached by a member gate. Act on the declaring type's own skip row — this entry exists so the member is accounted for rather than silently missing from the report.",
        SkipReason.ActorIsolatedConstructor =>
            "Constructor is on a custom global-actor-isolated type. The synchronous @_cdecl wrapper cannot call into the actor's executor. Construct the type from a Swift wrapper that hops to the actor, or expose a nonisolated factory.",
        SkipReason.MissingWrapperSymbol =>
            "A C# P/Invoke was planned against a Swift @_cdecl wrapper symbol that does not exist in the compiled wrapper, so it was suppressed to avoid a runtime DllNotFoundException/EntryPointNotFoundException. Two causes: (1) the symbol was stripped during wrapper compilation (inspect the wrapper post-processor / strip reconciler output); or (2) a wrapper-emit path bailed after the symbol was claimed and the in-band contract gate rolled the member back (a defense-in-depth backstop — the planning-time gates should now catch these before the claim). Inspect the recorded Details for which cause fired.",
        SkipReason.ConstrainedExtensionWrapper =>
            "A method on a generic type could not be exposed through an unconditional conformance wrapper: either an unconstrained extension method collides with a same-name overload on the parent, or the method carries generic constraints narrower than its parent declares (e.g. `extension Mapper where N: ImmutableMappable`). Conditional-conformance wrapper extensions are not yet supported. Call the method on a concrete generic instantiation from a Swift wrapper, or move it onto the parent type without the extra constraint.",
        SkipReason.GenericEnumCaseConstructor =>
            "A generic enum's payload-carrying case constructor needs a per-instantiation @_cdecl wrapper that the generator does not emit for open-generic enum cases. Construct the case from a Swift wrapper on a concrete instantiation, or expose a factory returning the concrete enum.",
        SkipReason.SuppressedProxyMethodBody =>
            "The method body referenced a proxy class whose EveryProtocol conformance was not emitted. Once the proxy can be emitted (add support for the missing requirements), the method body is restored.",
        SkipReason.Pattern2InternalTypeReach =>
            "Member signature exposes a @usableFromInline internal type. Refactor the Swift API to use a public type, or expose the functionality through a public Swift wrapper.",
        SkipReason.ParentModuleInternalNoFallback =>
            "Public async/closure-bearing method or frozen-struct operator on a @usableFromInline internal parent type. Its wrapper must name the internal parent and no direct CallConvSwift fallback exists, so the member is dropped. Move the member onto a public type, or expose it through a public Swift wrapper.",
        SkipReason.IndeterminateStructLayout =>
            "The frozen struct has a stored field that is a generic value-type instantiation (e.g. ClosedRange<Int>, Result<T,E>) whose inline size depends on its type arguments and cannot be derived cross-compile. Write a Swift wrapper that exposes the data through a supported, concretely-sized type.",
        SkipReason.NetUnavailableType =>
            "The Swift type is auto-bridged but not yet present in the .NET Foundation assembly. Write a Swift wrapper that exposes the data through a supported type (e.g. a plain String).",
        SkipReason.AbsentFrameworkType =>
            "The member references a framework type that no .NET binding visible to the generator declares. Two authorities were consulted, and both are scoped: the loaded type databases (this module's dependencies and the Apple supplement), and the platform reference assembly for the target platform (Microsoft.iOS/macOS/tvOS/MacCatalyst), which is consulted only for namespaces it binds at least one type in — a namespace it binds nothing in is left to name synthesis rather than called absent. Neither authority sees sibling binding packages the consuming project references, so if the type IS declared by a binding package you already reference, this is a generator-side false absence worth reporting. Otherwise: add a binding for the framework that declares the type, or write a Swift wrapper that exposes the data through a supported type.",
        SkipReason.SuppressedProxyMemberDegraded =>
            "This member reverse-dispatches through a protocol whose {Protocol}Proxy conformance could not be synthesized, so a C#-authored conformer cannot be marshalled (a produce getter throws, a consume setter/parameter accepts only Swift-vended values, or a receiver fail-fasts). Consume a Swift-vended conformer, or expose the functionality through a Swift wrapper that avoids requiring C#-side conformance to this protocol.",
        SkipReason.ObjCUnresolvableType =>
            "The ObjC member references a type absent from the ObjC type registry. Register the Apple/ObjC type in objc-type-mappings.json (or bind the framework that declares it) so the member resolves.",
        SkipReason.ObjCUnavailableApi =>
            "The ObjC API is marked unavailable on this platform (NS_UNAVAILABLE / deprecated-unavailable); no binding is emitted by design.",
        SkipReason.ObjCUnsupportedConstruct =>
            "The ObjC construct is not yet supported by the generator. Expose the functionality through a supported ObjC declaration shape.",
        SkipReason.ObjCAccessibilityConflict =>
            "The ObjC member was dropped to resolve a name/accessibility conflict. Rename the conflicting member in the source framework to disambiguate.",
        SkipReason.ObjCDuplicateSignature =>
            "The ObjC member's projected C# signature collides with another member. Rename one selector in the source framework to disambiguate the projected signature.",
        SkipReason.ObjCVariadicFunction =>
            "ObjC variadic functions/methods are not representable as a P/Invoke. Expose a non-variadic wrapper that takes an explicit array or count.",
        SkipReason.ObjCEmptyCategory =>
            "The ObjC category contributed no bindable members, so nothing was emitted; no action needed.",
        SkipReason.ObjCMissingNativeSymbol =>
            "The ObjC declaration has no matching exported native symbol in any linked binary (header-only / static-inline / unexported global); binding it would fail to link.",
        SkipReason.ObjCDuplicateSelector =>
            "Duplicate selectors across the ObjC type hierarchy are flattened to a single member by design; no action needed.",
        SkipReason.ConformanceNotFullyImplementable =>
            "The type still emits, but without this conformance: at least one protocol requirement has no representable C# member on it, so the `: I{Protocol}` entry was dropped rather than emitted with a hole. Details names the first unmet requirement — make that one requirement bindable (a supported signature, a satisfiable constraint, or an unconstrained extension default the validator can see) and regenerate to reveal the next blocker, or expose the protocol-typed usage through a Swift wrapper that takes the concrete type.",
        SkipReason.ProtocolWitnessNotDispatchable =>
            "The member IS declared and still callable on a concrete instance — only calls through a protocol-typed value throw (SB0003). The requirement's shape has no witness-table lowering: a non-blittable parameter or return, a closure parameter, a subscript, or a requirement set that mixes generic and non-generic members. Call it on the concrete type, or add a Swift wrapper requirement whose signature is blittable so the protocol-typed path becomes dispatchable.",
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
        SkipReason.UnsupportedThrowingAsyncStream =>
            "AsyncThrowingStream now bound directly (retired reason)",
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
        SkipReason.ParentTypeSuppressed =>
            "member of a type that was suppressed as a whole (never reached by a member gate)",
        SkipReason.ActorIsolatedConstructor =>
            "constructor on a custom global-actor-isolated type (synchronous wrapper unsafe)",
        SkipReason.MissingWrapperSymbol =>
            "P/Invoke removed because the Swift wrapper symbol was stripped during wrapper compilation",
        SkipReason.SuppressedProxyMethodBody =>
            "method body removed because it constructed a proxy class whose conformance was suppressed",
        SkipReason.Pattern2InternalTypeReach =>
            "member signature reaches an @usableFromInline internal (or otherwise-suppressed) type",
        SkipReason.ParentModuleInternalNoFallback =>
            "public async/closure/operator member on a @usableFromInline internal parent type with no direct CallConvSwift fallback",
        SkipReason.IndeterminateStructLayout =>
            "frozen struct stored field has an indeterminate cross-compile Buffer layout (generic value-type instantiation)",
        SkipReason.NetUnavailableType =>
            "Foundation type not yet available in .NET",
        SkipReason.AbsentFrameworkType =>
            "framework type has no .NET binding available",
        SkipReason.SuppressedProxyMemberDegraded =>
            "reverse-dispatch member degraded (proxy conformance not synthesized): produce-throw / consume-only / fail-fast receiver",
        SkipReason.ObjCUnresolvableType =>
            "ObjC type not in the ObjC type registry",
        SkipReason.ObjCUnavailableApi =>
            "ObjC API unavailable on this platform (by design)",
        SkipReason.ObjCUnsupportedConstruct =>
            "ObjC construct not yet supported",
        SkipReason.ObjCAccessibilityConflict =>
            "ObjC member dropped to resolve a name/accessibility conflict",
        SkipReason.ObjCDuplicateSignature =>
            "ObjC member's projected C# signature collides with another member",
        SkipReason.ObjCVariadicFunction =>
            "ObjC variadic function/method not representable",
        SkipReason.ObjCEmptyCategory =>
            "ObjC category contributed no bindable members",
        SkipReason.ObjCMissingNativeSymbol =>
            "ObjC declaration has no exported native symbol",
        SkipReason.ObjCDuplicateSelector =>
            "ObjC duplicate selector flattened by design",
        SkipReason.ConformanceNotFullyImplementable =>
            "conformance dropped — a protocol requirement has no representable C# member on the conforming type",
        SkipReason.ProtocolWitnessNotDispatchable =>
            "member declared but not dispatchable through a protocol-typed value (witness stub throws)",
        SkipReason.Unknown =>
            "unclassified skip reason",
        _ => null,
    };
}
