// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - ObjC-bridgeable value types crossing a C#-supplied closure
//
// `URL`, `URLRequest`, `IndexPath` and `Locale` are Swift STRUCTS whose C# projection is their
// Foundation class peer (`NSUrl`, `NSUrlRequest`, `NSIndexPath`, `NSLocale`). When Swift calls a
// C#-supplied closure with one of them, the closure's native slot holds the Swift value, not an
// object pointer: the binding has to bridge it to the peer (`as AnyObject`) before the managed
// handler can read it, and has to bridge a returned peer back into the value. Each handler below is
// invoked synchronously on the calling thread, and the Swift side reports what it saw so the C#
// side can assert the content round-tripped rather than merely that nothing crashed.

/// The values every probe below hands over; the C# assertions spell the same literals.
enum BridgeableValueClosureProbe {
    static let requestUrl = "https://example.invalid/bridge/request?q=1"
    static let requestMethod = "PATCH"
    static let requestTimeout: Double = 42.5
    static let headerName = "X-Bridge-Probe"
    static let headerValue = "probe-7"
    static let urlText = "https://example.invalid/bridge/url#frag"
    static let localeIdentifier = "fr_CA"

    static func makeRequest(_ suffix: Int32 = 0) -> URLRequest {
        var request = URLRequest(url: URL(string: suffix == 0 ? requestUrl : "\(requestUrl)&n=\(suffix)")!)
        request.httpMethod = requestMethod
        request.timeoutInterval = requestTimeout
        request.setValue(headerValue, forHTTPHeaderField: headerName)
        return request
    }
}

/// Bridgeable value types as closure arguments, closure returns, Optional, throwing and escaping.
public class BridgeableValueClosureLab {
    private var requestHandlers: [(URLRequest) -> Void] = []

    public init() {}

    // MARK: Non-optional arguments

    /// A request whose URL, method, timeout and header all have to survive the bridge.
    public func deliverRequest(_ handler: (URLRequest) -> Void) {
        handler(BridgeableValueClosureProbe.makeRequest())
    }

    public func deliverURL(_ handler: (URL) -> Void) {
        handler(URL(string: BridgeableValueClosureProbe.urlText)!)
    }

    /// A multi-element index path, a value with no single-reference layout.
    public func deliverIndexPath(_ handler: (IndexPath) -> Void) {
        handler(IndexPath(indexes: [3, 1, 4]))
    }

    public func deliverLocale(_ handler: (Locale) -> Void) {
        handler(Locale(identifier: BridgeableValueClosureProbe.localeIdentifier))
    }

    /// A bridgeable value beside ordinary arguments, so the slot order has to hold.
    public func deliverMixed(_ handler: (Int32, URLRequest, URL) -> Void) {
        handler(17, BridgeableValueClosureProbe.makeRequest(2), URL(string: BridgeableValueClosureProbe.urlText)!)
    }

    // MARK: Optional arguments

    public func deliverOptionalRequest(present: Bool, _ handler: (URLRequest?) -> Void) {
        handler(present ? BridgeableValueClosureProbe.makeRequest() : nil)
    }

    public func deliverOptionalURL(present: Bool, _ handler: (URL?) -> Void) {
        handler(present ? URL(string: BridgeableValueClosureProbe.urlText)! : nil)
    }

    // MARK: Throwing

    /// Reports "ok" when the handler returned and "threw" when it threw.
    public func deliverRequestThrowing(_ handler: (URLRequest) throws -> Void) -> String {
        do {
            try handler(BridgeableValueClosureProbe.makeRequest(5))
            return "ok"
        } catch {
            return "threw"
        }
    }

    // MARK: Escaping, fired later

    public func onRequest(_ handler: @escaping (URLRequest) -> Void) {
        requestHandlers.append(handler)
    }

    /// Calls every stored handler with a request whose URL carries `n`; returns how many ran.
    public func fireRequests(_ n: Int32) -> Int32 {
        let request = BridgeableValueClosureProbe.makeRequest(n)
        for handler in requestHandlers { handler(request) }
        return Int32(requestHandlers.count)
    }

    // MARK: Returns

    /// The handler rewrites the request; Swift reports the rewritten URL and method.
    public func rewriteRequest(_ handler: (URLRequest) -> URLRequest) -> String {
        let result = handler(BridgeableValueClosureProbe.makeRequest())
        return "\(result.httpMethod ?? "nil") \(result.url?.absoluteString ?? "nil")"
    }

    public func produceURL(_ handler: () -> URL) -> String {
        return handler().absoluteString
    }

    public func produceOptionalURL(_ handler: () -> URL?) -> String {
        return handler()?.absoluteString ?? "nil"
    }

    /// A throwing closure's return comes back beside the error slot rather than through a buffer.
    public func produceURLThrowing(_ handler: () throws -> URL) -> String {
        do {
            return try handler().absoluteString
        } catch {
            return "threw"
        }
    }

    public func produceOptionalURLThrowing(_ handler: () throws -> URL?) -> String {
        do {
            return try handler()?.absoluteString ?? "nil"
        } catch {
            return "threw"
        }
    }

    // MARK: Escaping return, read later

    private var urlProvider: (() -> URL)?

    public func setURLProvider(_ provider: @escaping () -> URL) {
        urlProvider = provider
    }

    /// Calls the stored provider twice, after the registering call has returned.
    public func readProvidedURL() -> String {
        guard let provider = urlProvider else { return "unset" }
        let first = provider().absoluteString
        let second = provider().absoluteString
        return first == second ? first : "\(first) != \(second)"
    }

    // MARK: Stored closure properties

    /// A closure property the C# side assigns directly rather than through a registering method.
    public var requestProperty: (URLRequest) -> Void = { _ in }

    /// A closure property whose return is the bridgeable value.
    public var urlProperty: () -> URL = { URL(string: "https://example.invalid/default")! }

    /// The Optional form of `requestProperty`, so both property shapes are exercised side by side.
    public var optionalRequestProperty: ((URLRequest) -> Void)?

    /// Calls `requestProperty` with a request whose URL carries `n`.
    public func fireRequestProperty(_ n: Int32) {
        requestProperty(BridgeableValueClosureProbe.makeRequest(n))
    }

    public func readURLProperty() -> String {
        return urlProperty().absoluteString
    }

    /// Calls `optionalRequestProperty` when it is set; reports whether it was.
    public func fireOptionalRequestProperty(_ n: Int32) -> Bool {
        guard let handler = optionalRequestProperty else { return false }
        handler(BridgeableValueClosureProbe.makeRequest(n))
        return true
    }

    // MARK: Async member

    /// An async member whose closure argument is a bridgeable value.
    public func deliverRequestAsync(_ handler: @escaping (URLRequest) -> Void) async -> String {
        await Task.yield()
        handler(BridgeableValueClosureProbe.makeRequest(11))
        return "delivered"
    }
}
