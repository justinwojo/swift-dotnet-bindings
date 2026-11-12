// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// Callees that are not synchronous swiftcc calls. The oracle confirms swiftc gives each a different
// convention and that the predictor refuses each one, with its reason, instead of lowering it.

import Foundation

public enum RefusalError: Error { case failed }

public func refusalAsync(_ a: Int) async -> Int { a }
public func refusalAsyncThrows() async throws -> Int { 0 }
public func refusalTypedThrows(_ a: Int) throws(RefusalError) -> Int { a }
public func refusalPack<each T>(_ values: repeat each T) -> Int { 0 }

public final class RefusalClass {
    public init() {}
    public func asyncMethod() async -> Int { 0 }
    public var asyncProperty: Int { get async { 0 } }
}

public actor RefusalActor {
    public init() {}
    public func isolated() -> Int { 0 }
}

// Sent as Objective-C messages: no Swift entry point and no dispatch thunk.
@objc public protocol RefusalObjCProtocol {
    func objcRequirement(_ x: Int32) -> Int32
    var objcProperty: Int32 { get }
}
