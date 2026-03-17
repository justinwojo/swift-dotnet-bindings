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
