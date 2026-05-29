// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Regression fixture for parent-generic constructors with a non-empty
// concrete-typed parameter list. Mirrors the CryptoKit `HMAC<H : HashFunction>(key:
// SymmetricKey)` shape: a non-throwing `init` whose only non-self parameter is
// a concrete (non-generic) Swift type, on a generic struct constrained by a
// PAT. The existing PatParent fixtures (`CubbyBag<Item: Cubby>`,
// `TaggedBag<Item: Tagger>`) cover only the zero-arg `init()` path; this one
// extends to one and two concrete args so the CSM emitter is exercised on the
// shape that CryptoKit consumers actually call.
//
// Expected emission: a per-conformer factory `From{Conformer}(<concrete args>)`
// on the `{Type}{Conformer}CsmExtensions` static partial class — same emission
// path that already produces `FromStringTagger()`, but with parameters threaded
// through.

public protocol KeyTag {
    associatedtype Marker
}

public struct StringKeyTag: KeyTag {
    public typealias Marker = String
    public init() {}
}

public struct IntKeyTag: KeyTag {
    public typealias Marker = Int32
    public init() {}
}

public struct KeyedBag<Item: KeyTag> {
    public var seedLength: Int32
    public var bonus: Int32

    /// Parent-generic ctor with a single concrete (non-generic) `Swift.String`
    /// argument — the HMAC<H>(key:) shape in miniature. The CSM emitter should
    /// produce `From{Conformer}(string)` factories on the per-conformer
    /// extension class.
    public init(seed: String) {
        self.seedLength = Int32(seed.count)
        self.bonus = 0
    }

    /// Parent-generic ctor with two concrete args — `Swift.String` + `Int32`.
    /// Exercises the multi-param threading through the CSM ctor factory.
    public init(seed: String, bonus: Int32) {
        self.seedLength = Int32(seed.count)
        self.bonus = bonus
    }

    /// Witness reader so the runtime test can verify the ctor actually ran.
    public func length() -> Int32 {
        return seedLength &+ bonus
    }
}
