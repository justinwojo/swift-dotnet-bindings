// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// Exercises the protocol-witness (existential) return path for containers whose element is
// ObjC-bridgeable (Foundation.URL). Such a container crosses the @_cdecl boundary as a whole
// NS* collection (NSSet/NSArray/NSDictionary) at +1 via `result as AnyObject`, NOT as a native
// Swift container box — the same "design b" the concrete class path uses. Before the fix, the
// witness accessor boxed a native Swift Set and the C# proxy tried to read it as an NSSet,
// producing an uncompilable getter (empty first argument to GetINativeObject). C# obtains the
// conformer as `any URLContainerProvider`, so the generated `URLContainerProviderProxy` dispatches
// each requirement through the witness table — the exact path FBSDKLoginKit's `Set<LoggingBehavior>`
// (an NS_TYPED_ENUM element) surfaced once its typedef synthesized an ObjCBridgeable record.

/// Protocol whose requirements return containers of an ObjC-bridgeable element (URL). Drives the
/// witness-dispatch proxy for a bridged-container property getter AND method returns (Set, Array,
/// Dictionary).
public protocol URLContainerProvider {
    /// Property getter returning an ObjC-bridgeable whole-container (Set<URL> → NSSet).
    var providedURLSet: Set<URL> { get }

    /// Method returning an ObjC-bridgeable whole-container (Array<URL> → NSArray).
    func provideURLArray() -> [URL]

    /// Method returning an ObjC-bridgeable whole-container (Dictionary<String, URL> → NSDictionary).
    func provideURLDictionary() -> [String: URL]
}

final class URLContainerProviderConformer: URLContainerProvider {
    var providedURLSet: Set<URL> {
        Set([URL(string: "https://set-a.example.com")!, URL(string: "https://set-b.example.com")!])
    }

    func provideURLArray() -> [URL] {
        [URL(string: "https://array-0.example.com")!, URL(string: "https://array-1.example.com")!]
    }

    func provideURLDictionary() -> [String: URL] {
        ["home": URL(string: "https://dict-home.example.com")!,
         "api": URL(string: "https://dict-api.example.com")!]
    }
}

/// Vends the conformer as an existential so C# wraps it in the generated `URLContainerProviderProxy`
/// and dispatches each requirement through the witness table.
public func makeURLContainerProvider() -> any URLContainerProvider {
    URLContainerProviderConformer()
}

// ─────────────────────────────────────────────────────────────────────────────
// SCALAR (non-container) sibling of the above: a protocol whose requirements return a single
// ObjC-bridgeable value (Foundation.URL), NOT a container of them. This exercises the SCALAR
// reverse-dispatch ownership contract: the C# receiver hands the ObjC pointer back at +1
// (`Arc.UnknownObjectRetain(url.Handle)`) and this side consumes the transferred retain
// (`takeRetainedValue`), symmetric with the whole-container path above. Before that fix the scalar
// handoff was +0 (`takeUnretainedValue`), so a FRESHLY allocated wrapper the C# getter returns could
// be freed by a GC in the handoff window → use-after-free. The C# conformer below returns a fresh
// wrapper each call, so a correct round-trip under GC pressure proves the +1 transfer holds the
// object alive across the boundary.

/// Protocol whose requirements return a SCALAR ObjC-bridgeable value (URL). Drives the reverse
/// EveryProtocol vtable for a bridged scalar property getter, method return, and their Optional forms.
public protocol URLScalarProvider {
    /// Property getter returning a scalar ObjC-bridgeable value (URL).
    var providedURL: URL { get }

    /// Method returning a scalar ObjC-bridgeable value (URL).
    func provideURL() -> URL

    /// Property getter returning an Optional ObjC-bridgeable value (URL?), `.some` case.
    var maybeURL: URL? { get }

    /// Method returning an Optional ObjC-bridgeable value (URL?), `.some` case.
    func provideMaybeURL() -> URL?

    /// Property getter returning an Optional ObjC-bridgeable value (URL?) whose reverse conformer
    /// yields `.none` — exercises the nil arm (C# deposits IntPtr.Zero; Swift's `.map` no-ops to nil).
    var maybeNilURL: URL? { get }

    /// Method returning an Optional ObjC-bridgeable value (URL?) whose reverse conformer yields
    /// `.none` — exercises the nil arm on the method-return emission site.
    func provideMaybeNilURL() -> URL?
}

final class URLScalarProviderConformer: URLScalarProvider {
    var providedURL: URL { URL(string: "https://scalar-prop.example.com")! }
    func provideURL() -> URL { URL(string: "https://scalar-method.example.com")! }
    var maybeURL: URL? { URL(string: "https://scalar-maybe-prop.example.com") }
    func provideMaybeURL() -> URL? { URL(string: "https://scalar-maybe-method.example.com") }
    var maybeNilURL: URL? { nil }
    func provideMaybeNilURL() -> URL? { nil }
}

/// Vends the conformer as an existential so C# wraps it in the generated `URLScalarProviderProxy`
/// and dispatches each requirement through the witness table (forward direction).
public func makeURLScalarProvider() -> any URLScalarProvider {
    URLScalarProviderConformer()
}

/// Reverse-dispatch driver: Swift receives a (typically C#-implemented) `URLScalarProvider` and
/// invokes each requirement, so the scalar ObjC-bridgeable return values cross C# → Swift through the
/// EveryProtocol vtable. Reading each URL's `absoluteString` immediately after the call would corrupt
/// if the object were released mid-handoff, so a correct deterministic summary proves the reverse ABI
/// + scalar +1 ARC contract round-trips. Each requirement is invoked twice so a fresh-per-call C#
/// wrapper is exercised more than once.
public func summarizeURLScalarProvider(_ provider: any URLScalarProvider) -> String {
    let prop = provider.providedURL.absoluteString
    let method = provider.provideURL().absoluteString
    _ = provider.providedURL.absoluteString
    _ = provider.provideURL().absoluteString
    let maybeProp = provider.maybeURL?.absoluteString ?? "nil"
    let maybeMethod = provider.provideMaybeURL()?.absoluteString ?? "nil"
    let maybeNilProp = provider.maybeNilURL?.absoluteString ?? "nil"
    let maybeNilMethod = provider.provideMaybeNilURL()?.absoluteString ?? "nil"
    return "prop=\(prop)|method=\(method)|maybeProp=\(maybeProp)|maybeMethod=\(maybeMethod)"
        + "|maybeNilProp=\(maybeNilProp)|maybeNilMethod=\(maybeNilMethod)"
}

// ─────────────────────────────────────────────────────────────────────────────
// READ-IN (param-in) mirror of the reverse-RETURN scalar tests above: a protocol whose requirements
// RECEIVE a scalar Optional ObjC-bridgeable VALUE (URL?) INTO the conformer — a settable property and
// a method param — so Optional<URL> crosses Swift → C# through the reverse EveryProtocol vtable. This
// exercises the READ direction the scalar-return fix left open: Optional<URL> is a 16-byte resilient
// value (URL.size == 16, URL?.size == 16), so passing `&copy` of that multi-word value and reading it
// C#-side as a one-word SwiftOptional<IntPtr> reinterprets URL's storage bytes as an ObjC pointer →
// corruption/crash. The fix bridges to a one-word optional ObjC pointer on both sides (nil ↔
// IntPtr.Zero), a +0 borrow that is the exact mirror of the reverse-RETURN +1 transfer. Observation is
// through String getters (the already-robust read direction), so a mis-read setter/param corrupts an
// observable round-trip rather than silently passing.

/// Protocol whose requirements RECEIVE a scalar Optional ObjC-bridgeable VALUE (URL?): a settable
/// property (reverse SETTER receiver) and a method param (reverse METHOD-PARAM receiver). The String
/// observation getters echo the last value each received, so a wrong read is observable.
public protocol URLOptionalSink {
    /// Settable Optional ObjC-bridgeable VALUE — drives the reverse property-SETTER receiver.
    var sinkURL: URL? { get set }

    /// Method taking an Optional ObjC-bridgeable VALUE param — drives the reverse METHOD-PARAM receiver.
    func acceptURL(_ url: URL?)

    /// absoluteString of the last value written through `sinkURL`'s setter, or "nil".
    var sinkDescription: String { get }

    /// absoluteString of the last value passed to `acceptURL(_:)`, or "nil".
    var acceptedDescription: String { get }
}

final class URLOptionalSinkConformer: URLOptionalSink {
    private var _sink: URL?
    private var _accepted: URL?
    var sinkURL: URL? {
        get { _sink }
        set { _sink = newValue }
    }
    func acceptURL(_ url: URL?) { _accepted = url }
    var sinkDescription: String { _sink?.absoluteString ?? "nil" }
    var acceptedDescription: String { _accepted?.absoluteString ?? "nil" }
}

/// Vends a Swift conformer as an existential (forward-direction control for the read-in protocol).
public func makeURLOptionalSink() -> any URLOptionalSink {
    URLOptionalSinkConformer()
}

/// Reverse-dispatch driver: Swift receives a (typically C#-implemented) `URLOptionalSink` and writes
/// BOTH a `.some(URL)` and a `.none` through the settable property AND the method param, reading the
/// String observation channel after each write. A correct summary proves the read-in Optional
/// ObjC-bridgeable VALUE param crosses Swift → C# with the right one-word optional-pointer layout for
/// both the `.some` and `.none` cases, at both the property-setter and method-param emission sites.
public func exerciseURLOptionalSink(_ sink: any URLOptionalSink) -> String {
    var s = sink
    s.sinkURL = URL(string: "https://cs-sink-some.example.com")
    let sinkSome = s.sinkDescription
    s.sinkURL = nil
    let sinkNone = s.sinkDescription
    s.acceptURL(URL(string: "https://cs-accept-some.example.com"))
    let acceptSome = s.acceptedDescription
    s.acceptURL(nil)
    let acceptNone = s.acceptedDescription
    return "sinkSome=\(sinkSome)|sinkNone=\(sinkNone)|acceptSome=\(acceptSome)|acceptNone=\(acceptNone)"
}

/// Reverse-dispatch driver: Swift receives a (typically C#-implemented) `URLContainerProvider`
/// existential and invokes each requirement, so the ObjC-bridgeable whole-container return values
/// cross C# → Swift through the EveryProtocol vtable — the reverse of `makeURLContainerProvider`'s
/// witness path. Each requirement returns a container the C# side builds as a fresh NS* collection
/// handed back at +1 (`Arc.UnknownObjectRetain` ↔ this side's `takeRetainedValue`); reading the
/// bridged native Swift containers here proves the reverse ABI + ARC contract round-trips. Returns
/// a deterministic summary the C# test asserts against.
public func summarizeURLContainerProvider(_ provider: any URLContainerProvider) -> String {
    let dict = provider.provideURLDictionary()
    let setURLs = provider.providedURLSet.map { $0.absoluteString }.sorted().joined(separator: ",")
    let arrayURLs = provider.provideURLArray().map { $0.absoluteString }.joined(separator: ",")
    let dictURLs = dict.keys.sorted().map { "\($0)=\(dict[$0]!.absoluteString)" }.joined(separator: ",")
    return "set=[\(setURLs)]|array=[\(arrayURLs)]|dict=[\(dictURLs)]"
}
