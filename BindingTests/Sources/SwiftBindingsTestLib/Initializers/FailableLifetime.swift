// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

private var failablePayloadDeinits: Int32 = 0
private var failableEntries: Int32 = 0

public func readFailablePayloadDeinits() -> Int32 { failablePayloadDeinits }
public func readFailableEntries() -> Int32 { failableEntries }

private final class FailablePayload {
    deinit { failablePayloadDeinits += 1 }
}

public struct FailableLifetimeArgument {
    public let number: Int32
    public init(_ number: Int32) { self.number = number }
}

public enum FailableLifetimeError: Error { case rejected }

public struct FailableLifetimeValue {
    private let payload: FailablePayload
    public let number: Int32

    // String inout retains the direct CallConvSwift route already qualified by P4.
    // A resilient argument supplies a real managed pre-entry SafeHandle failure.
    public init?(_ text: inout String, argument: borrowing FailableLifetimeArgument, mode: Int32) throws {
        failableEntries += 1
        text = "failable initializer entered"
        if mode == 2 { throw FailableLifetimeError.rejected }
        if mode == 0 { return nil }
        payload = FailablePayload()
        number = argument.number
    }
}
