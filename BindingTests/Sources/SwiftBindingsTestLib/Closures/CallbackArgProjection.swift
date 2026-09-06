// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Callback-arg projection asymmetry
//
// Regression for the callback-arg projection asymmetry bug.
// Shape: a callback closure whose argument is a tuple containing types that have
// non-trivial projections in the rest of the SDK
// (`Foundation.Data` → `byte[]`, `Foundation.URLResponse` → `Foundation.NSUrlResponse`,
// `Optional<T>` → `T?`, `Swift.String` → `string`).
//
// Pre-fix, the closure-arg-tuple translator (`TupleHandler.TranslateElementTypeToCSharp`)
// short-circuited to `typeRecord.CSharpTypeName.FullyQualifiedName` and skipped every
// projection rule that the top-level closure-arg translator
// (`ClosureHandler.TranslateTypeSpecToCSharp`) applies. Result: callback-overload
// emit produced `Action<(Swift.Foundation.Data, Swift.SwiftOptional<IntPtr>, …)>`
// while the equivalent async-return-tuple emit produced
// `Task<(byte[], Foundation.NSUrlResponse?, …)>` — same Swift types, two different
// C# projections. Consumers picking the callback overload had to manually unwrap
// runtime-shaped types.

public class CallbackArgProjectionLab {
    public init() {}

    /// Single-element tuple via Foundation.Data — simplest case.
    /// Pre-fix: `Action<Swift.Foundation.Data>`. Post-fix: `Action<byte[]>`.
    public func loadBytes(completion: @escaping (Foundation.Data) -> Void) {
        let bytes: [UInt8] = [0x42, 0x43, 0x44]
        completion(Foundation.Data(bytes))
    }

    /// Tuple of (Foundation.Data, Foundation.URLResponse?) — mixed-projection closure-arg tuple shape.
    /// Pre-fix: `Action<(Swift.Foundation.Data, Swift.SwiftOptional<IntPtr>)>`.
    /// Post-fix: `Action<(byte[], Foundation.NSUrlResponse?)>`.
    public func loadResponse(completion: @escaping (Foundation.Data, Foundation.URLResponse?) -> Void) {
        let payload = Foundation.Data([0x10, 0x20, 0x30])
        let response = Foundation.URLResponse(
            url: URL(string: "https://example.invalid")!,
            mimeType: "application/octet-stream",
            expectedContentLength: payload.count,
            textEncodingName: nil
        )
        completion(payload, response)
    }

    /// Tuple containing String + Optional<String> + Bool — exercises Swift.String → string
    /// and Optional<primitive> → T? projections inside a closure-arg tuple.
    /// Pre-fix: `Action<(Swift.SwiftString, Swift.SwiftOptional<…>, …)>`. Post-fix
    /// matches the equivalent async-return-tuple shape: `Action<(string, string?, bool)>`.
    public func loadDescriptor(completion: @escaping (String, String?, Bool) -> Void) {
        completion("kind", "label-A", true)
    }

    /// NON-optional ObjC-backed class in a callback-arg slot: `(Data, URLResponse) -> Void`.
    /// Swift fills that slot with ONE borrowed object pointer, exactly as it does for the
    /// `URLResponse?` neighbour above. The concrete instance is an `HTTPURLResponse` so the
    /// managed side can prove the pointer was read as the OBJECT (its isa names
    /// `NSHTTPURLResponse`, and the status code / URL are readable) rather than as the ADDRESS
    /// OF a slot holding one — dereferencing it once more yields the isa word, which wraps as a
    /// garbage peer.
    /// Provenance: a third-party image loader's `loadData(request:didReceiveData:completion:)`.
    public func loadDirectResponse(completion: @escaping (Foundation.Data, Foundation.URLResponse) -> Void) {
        let payload = Foundation.Data([0x71, 0x72])
        let response = Foundation.HTTPURLResponse(
            url: URL(string: CallbackArgProjectionProbe.responseUrl)!,
            statusCode: CallbackArgProjectionProbe.responseStatus,
            httpVersion: "HTTP/1.1",
            headerFields: nil
        )!
        completion(payload, response)
    }

    /// NON-optional `NSURL` — an ObjC class (not the `URL` value type) with no Swift metadata
    /// of its own. Same slot shape, a second bridged Foundation peer.
    public func loadDirectUrl(completion: @escaping (NSURL) -> Void) {
        completion(NSURL(string: CallbackArgProjectionProbe.urlText)!)
    }

    /// NON-optional generator-bound `@objc … : NSObject` class — the ObjC-ROOTED neighbour of
    /// the bridged peers above. It carries Swift class metadata, so its slot is read by the
    /// isa-aware marshal; it is the positive control that the shared classifier keeps routing
    /// each reference flavour to its own adapter.
    public func loadDirectMarker(completion: @escaping (DirectCallbackMarker) -> Void) {
        completion(DirectCallbackMarker(tag: CallbackArgProjectionProbe.markerTag))
    }

    /// NON-optional pure-Swift class in the same slot — the third reference flavour.
    public func loadDirectToken(completion: @escaping (DirectCallbackToken) -> Void) {
        completion(DirectCallbackToken(label: CallbackArgProjectionProbe.tokenLabel))
    }
}

/// Struct parent for the same non-optional reference-arg shapes: the callback trampoline is
/// emitted per member, so a struct parent is a separate emission path from a class parent.
public struct CallbackArgProjectionStructLab {
    public init() {}

    public func loadDirectResponse(completion: @escaping (Foundation.Data, Foundation.URLResponse) -> Void) {
        let payload = Foundation.Data([0x81])
        let response = Foundation.HTTPURLResponse(
            url: URL(string: CallbackArgProjectionProbe.responseUrl)!,
            statusCode: CallbackArgProjectionProbe.responseStatus,
            httpVersion: "HTTP/1.1",
            headerFields: nil
        )!
        completion(payload, response)
    }

    public func loadDirectMarker(completion: @escaping (DirectCallbackMarker) -> Void) {
        completion(DirectCallbackMarker(tag: CallbackArgProjectionProbe.markerTag))
    }
}

/// Known values the managed assertions compare against.
public enum CallbackArgProjectionProbe {
    public static let responseUrl = "https://example.invalid/direct-response"
    public static let responseStatus = 418
    public static let urlText = "https://example.invalid/direct-url"
    public static let markerTag = "marker-A"
    public static let tokenLabel = "token-A"
}

/// `@objc … : NSObject` class — ObjC-rooted, carries Swift class metadata.
@objc public class DirectCallbackMarker: NSObject {
    @objc public let tag: String

    @objc public init(tag: String) {
        self.tag = tag
        super.init()
    }
}

/// Pure-Swift class — no ObjC root, no ObjC bridge.
public final class DirectCallbackToken {
    public let label: String

    public init(label: String) {
        self.label = label
    }
}
