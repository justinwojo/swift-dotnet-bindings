// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

private nonisolated(unsafe) var stringOwnershipAllocations: Int32 = 0
private nonisolated(unsafe) var stringOwnershipDeinits: Int32 = 0
private nonisolated(unsafe) var stringOwnershipEntries: Int32 = 0
public func getStringOwnershipAllocationCount() -> Int32 { stringOwnershipAllocations }
public func getStringOwnershipDeinitCount() -> Int32 { stringOwnershipDeinits }
public func getStringOwnershipEntryCount() -> Int32 { stringOwnershipEntries }

private final class StringOwnershipToken {
    let number: Int32
    init(_ number: Int32) { self.number = number; stringOwnershipAllocations += 1 }
    deinit { stringOwnershipDeinits += 1 }
}

// Nonfrozen and nontrivial: the wrapper copies this value through typed Swift storage.
public struct StringOwnershipValue {
    private let token: StringOwnershipToken
    public init(_ number: Int32) { token = StringOwnershipToken(number) }
    public var number: Int32 { token.number }
}

public final class StringOwnershipLater {
    public let number: Int32
    public init(_ number: Int32) { self.number = number }
}

public enum StringOwnershipFailure: Error { case expected }

// A stored nonzero bias makes the receiver payload observable, unlike the old empty receiver.
public struct StringOwnershipReceiver {
    public let bias: Int32
    public init(_ bias: Int32) { self.bias = bias }
    public static var defaultSuffix: String { "|default" }
    public static var defaultExtra: Int32 { 3 }

    @inline(never) public func consume(_ value: consuming StringOwnershipValue, text: inout String) -> Int32 {
        stringOwnershipEntries += 1
        text += "|changed"
        return value.number + bias
    }

    @inline(never) public func borrow(_ value: borrowing StringOwnershipValue, text: inout String) -> Int32 {
        stringOwnershipEntries += 1
        text += "|changed"
        return value.number + bias
    }

    @inline(never) public func consumeAndThrow(_ value: consuming StringOwnershipValue, text: inout String) throws -> Int32 {
        stringOwnershipEntries += 1
        text += "|changed"
        if value.number < 0 { throw StringOwnershipFailure.expected }
        return value.number + bias
    }

    @inline(never) public func consumeWithLater(_ value: consuming StringOwnershipValue, text: inout String,
                                              later: borrowing StringOwnershipLater) -> Int32 {
        stringOwnershipEntries += 1
        text += "|changed"
        return value.number + bias + later.number
    }

    @inline(never) public func replace(_ value: consuming StringOwnershipValue, text: inout String,
                                     replacement: String) -> Int32 {
        stringOwnershipEntries += 1
        text = replacement
        return value.number + bias
    }

    @inline(never) public func stringNeighbors(_ before: String, text: inout String, after: String) -> Int32 {
        text = before + "[" + text + "]" + after
        return bias
    }

    @inline(never) public func replaceBoth(_ first: inout String, second: inout String) -> Int32 {
        let previousFirst = first
        first = second + "|first"
        second = previousFirst + "|second"
        return bias
    }

    @inline(never) public func afterScalars(_ a: Int32, _ b: Int32, _ c: Int32, _ d: Int32,
                                          _ e: Int32, _ f: Int32, _ g: Int32, text: inout String) -> Int32 {
        text += "|scalars"
        return a + b + c + d + e + f + g + bias
    }

    // The original wrapper and inherited trimmed wrappers all transport the inout address.
    @inline(never) public func withDefaults(_ value: consuming StringOwnershipValue, text: inout String,
                                          suffix: String = StringOwnershipReceiver.defaultSuffix,
                                          extra: Int32 = StringOwnershipReceiver.defaultExtra) -> Int32 {
        stringOwnershipEntries += 1
        text += suffix
        return value.number + bias + extra
    }

    // Recovery contract: the full closure wrapper is outside the new admission and may be
    // withdrawn. Its independently eligible no-callback trim must survive that withdrawal.
    @inline(never) public func droppingClosureDefault(_ text: inout String, callback: (() -> Void)? = {}) -> Int32 {
        text += "|trimmed"
        callback?()
        return bias
    }

    @inline(never) public func debugDefault(_ text: inout String, file: StaticString = #fileID) -> Int32 {
        text += "|debug"
        return file.utf8CodeUnitCount > 0 ? bias : -1
    }

    @inline(never) public static func staticText(_ text: inout String) -> Int32 {
        text += "|static"
        return 23
    }
}

@inline(never) public func stringOwnershipFreeText(_ text: inout String) -> Int32 {
    text += "|free"
    return 29
}
