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
}
