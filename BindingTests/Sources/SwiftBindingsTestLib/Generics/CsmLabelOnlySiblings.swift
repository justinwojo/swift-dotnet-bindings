// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Static generic overloads that differ only by an argument label, on a parent whose constraint is
// a protocol with an associated type — the shape of CryptoKit's
// `HKDF<H: HashFunction>.deriveKey(inputKeyMaterial:info:outputByteCount:)` beside
// `deriveKey(inputKeyMaterial:salt:outputByteCount:)`. Both method generics are constrained to
// `DataProtocol`, so each projects to the same closed C# signature per conformer
// (`(int, Data, int)` / `(int, byte[], int)`), and the open-generic form cannot carry either:
// the constraint has associated types. Each overload is a different operation, so both must
// reach the closed `…CsmExtensions` classes. The first-declared one keeps the plain `DeriveKey`
// name — CryptoKit's `info:` overload shipped under it — and the later one takes its labels.
//
// The two-generic overload carries a distinct closed signature and keeps the plain name. So does
// the overload generic over `Tagger` (CryptoKit's `signature<D: Digest>(for:)` beside
// `signature<D: DataProtocol>(for:)`): it closes over `Tagger` conformers, never to the
// `DataProtocol` siblings' parameter types, so it is not in their family and contends for no name.

import Foundation

public struct TaggedKeyDeriver<Item: Tagger> {
    public static func deriveKey<Info: DataProtocol>(material: Int32, info: Info, outputByteCount: Int32) -> Int32 {
        return 1000 &+ material &+ Int32(info.count) &* 10 &+ outputByteCount &* 100
    }

    public static func deriveKey<Salt: DataProtocol>(material: Int32, salt: Salt, outputByteCount: Int32) -> Int32 {
        return 2000 &+ material &+ Int32(salt.count) &* 10 &+ outputByteCount &* 100
    }

    public static func deriveKey<Salt: DataProtocol, Info: DataProtocol>(
        material: Int32, salt: Salt, info: Info, outputByteCount: Int32
    ) -> Int32 {
        return 3000 &+ material &+ Int32(salt.count) &* 10 &+ Int32(info.count) &* 10 &+ outputByteCount &* 100
    }

    public static func deriveKey<Tag: Tagger>(material: Int32, info: Tag, outputByteCount: Int32) -> Int32 {
        return 4000 &+ material &+ (Tag.self == StringTagger.self ? 1 : 2) &* 10 &+ outputByteCount &* 100
    }
}
