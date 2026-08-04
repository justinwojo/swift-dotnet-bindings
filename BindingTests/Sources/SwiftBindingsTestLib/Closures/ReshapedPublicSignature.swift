// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Members whose EMITTED C# signature differs from the declared one
//
// Some emitters do not write the signature the parsed declaration describes. The closure bridge
// omits every defaulted non-closure parameter (the Swift shim calls the real declaration without
// them, so Swift evaluates its own default) and re-spells each closure parameter as an
// `Action<…>`/`Func<…>` delegate. The API manifest — the document a consumer reads to learn what
// they can call — is keyed from the DECLARED shape unless the writer says otherwise, so a member
// bound this way is described by a signature that appears nowhere in the emitted C#.
//
// These fixtures pin that: each declares a defaulted parameter sitting BEFORE the trailing closure,
// so the emitted parameter list is strictly shorter than the declared one and the divergence is
// observable. Provenance: this is the shape an image-loading pipeline's
// `loadData(with:queue:progress:completion:)` and a social-SDK login manager's
// `refreshLimitedLogin(handler:)` take — both real third-party libraries whose whole binding failed
// to generate on this divergence.

/// The error a bridged completion callback reports. Its presence in the callback's argument list is
/// what routes the member through the closure bridge rather than the plain `@_cdecl` path.
public enum LoaderFailure: Error {
    case unavailable
}

/// One interior defaulted parameter ahead of an escaping completion closure that carries an
/// `any Error` existential. The emitted C# is `LoadValue(int, Action<int, ...>)` — the `retries`
/// parameter is not written at all.
public final class ReshapedClosureLoader {
    public init() {}

    public func loadValue(
        _ key: Int32,
        retries: Int32 = 2,
        completion: @escaping (Int32, (any Error)?) -> Void
    ) {
        if key < 0 {
            completion(0, LoaderFailure.unavailable)
        } else {
            completion(key * retries, nil)
        }
    }

    /// Two interior defaults, so the emitted list is two parameters shorter than the declared one
    /// and a naive parameter-count match cannot accidentally reconcile it.
    public func loadPair(
        _ key: Int32,
        scale: Int32 = 3,
        offset: Int32 = 5,
        completion: @escaping (Int32, (any Error)?) -> Void
    ) {
        completion(key * scale + offset, nil)
    }
}
