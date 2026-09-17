// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// Protocol requirements that overload on return type alone: same base name, same labels, same
// parameter types, different result. Swift treats these as distinct requirements (a client picks
// one by type context), so the carrier needs one witness per requirement. Keying witness dedup on
// name and parameters only collapses them and leaves one requirement unsatisfied.

import Foundation

public struct EnrollmentTicket {
    public let code: Int32
    public init(code: Int32) { self.code = code }
}

public protocol EnrollmentClient: AnyObject {
    func enroll(token: String) -> Int32
    func enroll(token: String) -> EnrollmentTicket
    func enroll(token: String, email: String) -> Int32
}

/// Calls the `Int32`-returning overload through the existential.
public func enrollmentClientEnrollCode(_ client: any EnrollmentClient, token: String) -> Int32 {
    let code: Int32 = client.enroll(token: token)
    return code
}

/// Calls the struct-returning overload through the existential.
public func enrollmentClientEnrollTicket(_ client: any EnrollmentClient, token: String) -> Int32 {
    let ticket: EnrollmentTicket = client.enroll(token: token)
    return ticket.code
}
