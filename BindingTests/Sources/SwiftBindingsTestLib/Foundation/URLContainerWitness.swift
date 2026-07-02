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
