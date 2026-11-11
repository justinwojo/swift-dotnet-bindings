// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// A message authenticator generic over a protocol with an associated type, whose
// members hand back a result type closed over the parent's own parameter:
//
//   MacAuthenticator<Algorithm: MacAlgorithm>
//     func finalize() -> MacTag<Algorithm>                                  (instance)
//     static func tag<Message: MacMessage>(for:key:) -> MacTag<Algorithm>   (static, own parameter)
//
// The open generic form cannot be bound (the parent's constraint has an associated
// type), so each member exists for a consumer only as its per-conformer
// specialization. Two things have to hold for that:
//
// - The instance `finalize()` returns a type that names the parent parameter. It is
//   specialized like a static: the parent parameter is substituted with the conformer.
// - The static's own parameter is the first one at its depth (`Message` is τ_1_0) and
//   the parent's is the first at its depth (`Algorithm` is τ_0_0). The two must stay
//   distinct parameters: the return type mentions only the parent's, the argument only
//   the method's.

public protocol MacAlgorithm {
    associatedtype Word
    static var seed: Int32 { get }
}

public struct MacSum: MacAlgorithm {
    public typealias Word = Int32
    public static var seed: Int32 { 11 }
    public init() {}
}

public struct MacXor: MacAlgorithm {
    public typealias Word = UInt8
    public static var seed: Int32 { 5 }
    public init() {}
}

/// The message protocol of the static's own generic parameter, with module-local
/// conformers so the conformer set comes from the module itself.
public protocol MacMessage {
    var macBytes: [UInt8] { get }
}

@frozen
public struct MacText: MacMessage {
    public let text: String
    public init(text: String) { self.text = text }
    public var macBytes: [UInt8] { Array(text.utf8) }
}

/// The authentication result, closed over the algorithm that produced it. Resilient,
/// so it comes back through an indirect result.
public struct MacTag<Algorithm: MacAlgorithm> {
    public let value: Int32
    public init(value: Int32) { self.value = value }
}

public struct MacAuthenticator<Algorithm: MacAlgorithm> {
    public typealias Tag = MacTag<Algorithm>

    var state: Int32

    public init(key: Int32) {
        state = Algorithm.seed &* 1_000 &+ key
    }

    public mutating func absorb(_ value: Int32) {
        state = state &* 31 &+ value
    }

    public func finalize() -> Tag {
        return MacTag(value: state)
    }

    public static func tag<Message: MacMessage>(for message: Message, key: Int32) -> Tag {
        var authenticator = MacAuthenticator(key: key)
        for byte in message.macBytes {
            authenticator.absorb(Int32(byte))
        }
        return authenticator.finalize()
    }
}

public func makeMacSumAuthenticator(key: Int32) -> MacAuthenticator<MacSum> {
    return MacAuthenticator<MacSum>(key: key)
}

public func makeMacXorAuthenticator(key: Int32) -> MacAuthenticator<MacXor> {
    return MacAuthenticator<MacXor>(key: key)
}
