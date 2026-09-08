// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

private nonisolated(unsafe) var ownershipDeinits: Int32 = 0
public func getOwnershipDeinitCount() -> Int32 { ownershipDeinits }
public final class OwnershipToken {
    public let number: Int32
    public init(_ number: Int32) { self.number = number }
    deinit { ownershipDeinits += 1 }
}
public func consumeOwnershipToken(_ value: consuming OwnershipToken) -> Int32 { value.number }
public func borrowOwnershipToken(_ value: borrowing OwnershipToken) -> Int32 { value.number }
public func consumeOwnershipPair(_ first: consuming OwnershipToken, _ second: consuming OwnershipToken) -> Int32 {
    first.number + second.number
}
public struct OwnershipConsumer {
    public init() {}
    public func consume(_ value: consuming OwnershipToken) -> Int32 { value.number }
    public func borrow(_ value: borrowing OwnershipToken) -> Int32 { value.number }
}
public enum OwnershipFailure: Error { case failed }
public func consumeOwnershipThenThrow(_ value: consuming OwnershipToken) throws -> Int32 {
    if value.number < 0 { throw OwnershipFailure.failed }
    return value.number
}
public struct CallbackOwnedValue {
    private let token: OwnershipToken
    public var number: Int32 { token.number }
    public init(_ number: Int32) { token = OwnershipToken(number) }
}
public struct CallbackPodValue {
    public let number: Int32
    public init(_ number: Int32) { self.number = number }
}
public final class OwnershipCallbackOwner {
    public var callback: (CallbackOwnedValue) -> Void = { _ in }
    public var podCallback: (CallbackPodValue) -> Void = { _ in }
    public var classCallback: (OwnershipToken) -> Void = { _ in }
    public var optionalCallback: ((CallbackOwnedValue) -> Void)?
    public init() {}
    public func invoke() { callback(CallbackOwnedValue(71)) }
    public func invokePod() { podCallback(CallbackPodValue(72)) }
    public func invokeClass() { classCallback(OwnershipToken(73)) }
    public func invokeOptional() { optionalCallback?(CallbackOwnedValue(74)) }
    public func ordinary(_ body: (CallbackOwnedValue) -> Void) { body(CallbackOwnedValue(75)) }
}
public struct OwnershipCallbackFactory {
    public let number: Int32
    public init?(_ body: (CallbackOwnedValue) -> Void, valid: Bool) {
        body(CallbackOwnedValue(76))
        if !valid { return nil }
        number = 76
    }
}

private nonisolated(unsafe) var ownershipStructDeinits: Int32 = 0
private nonisolated(unsafe) var ownershipStructCalls: Int32 = 0
public func getOwnershipStructDeinitCount() -> Int32 { ownershipStructDeinits }
public func getOwnershipStructCallCount() -> Int32 { ownershipStructCalls }
private final class OwnershipStructToken {
    let number: Int32
    init(_ number: Int32) { self.number = number }
    deinit { ownershipStructDeinits += 1 }
}
// Deliberately non-frozen, address-only across the library-evolution ABI, and nontrivial.
public struct OwnershipStructValue {
    private let token: OwnershipStructToken
    public init(_ number: Int32) { token = OwnershipStructToken(number) }
    public var number: Int32 { token.number }
}
public struct OwnershipStructConsumer {
    public init() {}
    @inline(never) public func consume(_ value: consuming OwnershipStructValue) -> Int32 {
        ownershipStructCalls += 1
        return value.number
    }
    @inline(never) public func borrow(_ value: borrowing OwnershipStructValue) -> Int32 {
        ownershipStructCalls += 1
        return value.number
    }
    @inline(never) public func consumeAndThrow(_ value: consuming OwnershipStructValue) throws -> Int32 {
        ownershipStructCalls += 1
        if value.number < 0 { throw OwnershipFailure.failed }
        return value.number
    }
    @inline(never) public func consumeWithLater(_ value: consuming OwnershipStructValue,
                                               later: borrowing OwnershipToken) -> Int32 {
        ownershipStructCalls += 1
        return value.number + later.number
    }
}

// These historical Direct names predate synchronous String method-wrapper support. They now
// qualify the static Cdecl String/value-copy route; the tests assert the new native strategy.
// Independently retained direct specimens preserve transfer-lease coverage and original instance
// failure evidence. These wrappers do not establish correctness of that direct instance boundary.
public extension OwnershipStructConsumer {
    @inline(never) static func consumeDirect(_ value: consuming OwnershipStructValue, text: inout String) -> Int32 {
        ownershipStructCalls += 1
        text += "!"
        return value.number
    }
    @inline(never) static func borrowDirect(_ value: borrowing OwnershipStructValue, text: inout String) -> Int32 {
        ownershipStructCalls += 1
        text += "!"
        return value.number
    }
    @inline(never) static func consumeDirectAndThrow(_ value: consuming OwnershipStructValue, text: inout String) throws -> Int32 {
        ownershipStructCalls += 1
        text += "!"
        if value.number < 0 { throw OwnershipFailure.failed }
        return value.number
    }
    @inline(never) static func consumeDirectWithLater(_ value: consuming OwnershipStructValue, text: inout String,
                                             later: borrowing OwnershipToken) -> Int32 {
        ownershipStructCalls += 1
        text += "!"
        return value.number + later.number
    }
}
