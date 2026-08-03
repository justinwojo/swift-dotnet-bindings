// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// A class-bound protocol whose stored-property requirement is ALSO declared statically on one of
// its conformers. Swift lets a type carry `static let keySize` and `let keySize` side by side; C#
// has no such type/instance split, so the type emitters keep whichever the module declares first
// and drop the other. When the survivor is the static one, nothing on that type can implement the
// instance interface requirement — a static member is not a candidate implementation, so claiming
// the conformance produces "does not implement instance interface member ... because it is static"
// no matter what the interface itself provides.
//
// The protocol also carries an unlabeled-first-parameter method requirement satisfied only by an
// unconstrained extension default. That is the shape that makes this reachable at all: once the
// defaults index recognises the default, both conformers below become conformance candidates, and
// the dual-declaration one has to be turned away for the static/instance reason rather than
// accidentally surviving on the method question. Found by the real-world validation sweep, where a
// cipher type declared its key size both ways.

import Foundation

// Deliberately NOT class-bound, even though the library this shape came from constrains it to
// AnyObject: a class-bound existential has its own two-word layout, and passing a Swift-backed
// conformer into one is broken today for reasons that have nothing to do with the collision this
// fixture exists to pin.
public protocol KeyedCipher {
    var keySize: Int32 { get }
    func encrypt(_ bytes: Int32, rounds: Int32) -> Int32
}

extension KeyedCipher {
    public func encrypt(_ bytes: Int32, rounds: Int32) -> Int32 {
        bytes &* keySize &+ rounds
    }
}

/// Declares `keySize` BOTH statically and per-instance — the C# name collides, so the emitted
/// member is whichever came first and the conformance cannot be claimed.
public final class DualKeySizeCipher: KeyedCipher {
    public static let keySize: Int32 = 16
    public let keySize: Int32

    public init(keySize: Int32) {
        self.keySize = keySize
    }
}

/// Control: the same protocol, the same reliance on the extension default, but only an instance
/// `keySize` — so this one keeps its conformance.
public final class InstanceKeySizeCipher: KeyedCipher {
    public let keySize: Int32

    public init(keySize: Int32) {
        self.keySize = keySize
    }
}

public func cipherKeySize(_ cipher: any KeyedCipher) -> Int32 {
    cipher.keySize
}

public func cipherEncrypt(_ cipher: any KeyedCipher, bytes: Int32, rounds: Int32) -> Int32 {
    cipher.encrypt(bytes, rounds: rounds)
}

public func makeDualKeySizeCipher(keySize: Int32) -> DualKeySizeCipher {
    DualKeySizeCipher(keySize: keySize)
}

public func makeInstanceKeySizeCipher(keySize: Int32) -> InstanceKeySizeCipher {
    InstanceKeySizeCipher(keySize: keySize)
}
