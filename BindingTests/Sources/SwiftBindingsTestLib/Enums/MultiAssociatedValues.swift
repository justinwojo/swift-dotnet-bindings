// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Multi-Associated-Value Enums (S8 pattern)

/// Enum cases with multiple labeled associated values.
/// Tests that the generator correctly emits factory functions and
/// case-inspection methods for multi-payload enum cases.
public enum NetworkResponse {
    case success(statusCode: Int32, body: String)
    case redirect(from: String, to: String, permanent: Bool)
    case clientError(statusCode: Int32, message: String)
    case serverError(statusCode: Int32, retryAfter: Int32)
    case noContent
}

/// Inspects the status code from any response case that has one.
public func responseStatusCode(_ response: NetworkResponse) -> Int32 {
    switch response {
    case .success(let code, _): return code
    case .redirect(_, _, _): return 302
    case .clientError(let code, _): return code
    case .serverError(let code, _): return code
    case .noContent: return 204
    }
}

/// Returns the body text if the response is a success, otherwise nil.
public func responseBody(_ response: NetworkResponse) -> String? {
    switch response {
    case .success(_, let body): return body
    default: return nil
    }
}

// MARK: - ObjC-Bridged Payload Enum (Issue 5 regression coverage)

/// Enum whose payload cases use ObjC-bridged Foundation.URL (bridges to NSURL).
/// This exercises the Issue 5 narrowing: `ContainsRemappedObjCTypeInGenericArgs`
/// must NOT suppress case factories when the container is a stdlib
/// `Swift.Optional` / `Swift.Array` (no `where T : ISwiftObject` constraint).
///
/// Before the fix, `.replace(URL?)` and `.loadAll([URL])` case factories were
/// stripped because the generator flagged any ObjC-prefixed (NS*/UI*) type in
/// generic args — even when the outer container had no constraint to violate.
///
/// See: `EnumHandler.CaseConstruction.cs` — `ContainsRemappedObjCTypeInGenericArgs` +
/// `IsStdlibContainerWithoutISwiftObjectConstraint`.
public enum UpdatingStrategy {
    case none
    case keep
    /// Optional ObjC-bridged payload: `Swift.Optional<Foundation.URL>` → URL bridges
    /// to `NSURL`, matching `HasObjCClassPrefix("NS")`. The stdlib `Optional` has
    /// no ISwiftObject constraint, so the case factory must be emitted.
    case replace(URL?)
    /// Array of ObjC-bridged payload: `Swift.Array<Foundation.URL>`. Same rationale:
    /// `SwiftArray<Element>` has no ISwiftObject constraint.
    case loadAll([URL])
    /// Double-wrapped: `Swift.Optional<Swift.Array<Foundation.URL>>`. Swift nil-pointer-
    /// optimizes this to a single IntPtr (nil = IntPtr.Zero, some = NSArray handle).
    /// Before the fix, the enum-payload fast path only detected a flat ObjC container
    /// bridge; it missed the nested case, producing a MarshalFromSwift<SwiftOptional<SwiftArray<IntPtr>>>
    /// call that read the wrong ABI.
    case maybeLoadAll([URL]?)
}

/// Accepts `.replace(url)` and returns the stored URL's absolute string, or "<nil>"
/// for `.replace(nil)`, or "<other>" for other cases.
public func describeUpdatingStrategy(_ strategy: UpdatingStrategy) -> String {
    switch strategy {
    case .none: return "<none>"
    case .keep: return "<keep>"
    case .replace(let url):
        if let url = url { return url.absoluteString }
        return "<nil>"
    case .loadAll(let urls):
        return "count=\(urls.count)"
    case .maybeLoadAll(let urls):
        if let urls = urls { return "maybe=\(urls.count)" }
        return "maybe=<nil>"
    }
}
