// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - MCB Non-Closure Carrier Admissibility
//
// The closure bridge only emits a @_cdecl wrapper when EVERY parameter of the
// method is passable — the closure arguments AND the ordinary "carrier" arguments
// riding alongside them. A method whose closure shape the bridge fully supports is
// still rejected outright when one plain parameter cannot cross the C boundary,
// and the member then falls back to a direct CallConvSwift P/Invoke marked SB0001.
//
// A complex enum (associated values) marshals exactly like a non-frozen struct:
// the C# projection is a class over an opaque payload buffer, so the bridge can
// pass the payload pointer and reload the value on the Swift side. These fixtures
// pin that carrier shape end to end.
//
// Provenance: this is the parameter shape behind the Mappedin SDK's
// `getMapData(options:completion:)` family, where a `Result<_, any Error>`
// completion rides next to enum-typed request options.

/// Complex enum used as a plain (non-closure) parameter alongside a closure.
/// Associated values keep it off the simple-enum path, so it projects to a C#
/// class with an opaque payload rather than to a C# enum over an integer.
public enum FetchScope {
    case everything
    case limited(max: Int32)
    case rejected(reason: String)
}

/// Complex enum used as a closure-adjacent carrier in the `(any Error)?` shape.
public enum WriteMode {
    case append(offset: Int32)
    case truncate
}

public final class EnumCarrierClosureBridge {
    public init() {}

    /// Complex-enum carrier + `Result<T, any Error>` completion.
    /// `.everything` → `.success(-1)`; `.limited(max:)` → `.success(max * 2)`;
    /// `.rejected` → `.failure(MathError.divisionByZero)`, so a single call site
    /// exercises both arms of the Result.
    public func fetch(scope: FetchScope, completion: (Result<Int32, any Error>) -> Void) {
        switch scope {
        case .everything:
            completion(.success(-1))
        case .limited(let max):
            completion(.success(max * 2))
        case .rejected:
            completion(.failure(MathError.divisionByZero))
        }
    }

    /// Complex-enum carrier + `(any Error)?` completion — the other existential
    /// closure shape the bridge already supports, riding the same carrier axis.
    /// `.truncate` reports success (nil error); `.append(offset:)` reports failure
    /// when the offset is negative and success otherwise.
    public func write(mode: WriteMode, completion: ((any Error)?) -> Void) {
        switch mode {
        case .truncate:
            completion(nil)
        case .append(let offset):
            completion(offset < 0 ? MathError.divisionByZero : nil)
        }
    }

    /// Two complex-enum carriers plus a primitive, ahead of the completion —
    /// pins that carrier admissibility is per-parameter, not first-parameter-only.
    public func combine(
        scope: FetchScope,
        mode: WriteMode,
        multiplier: Int32,
        completion: (Result<Int32, any Error>) -> Void
    ) {
        var base: Int32
        switch scope {
        case .everything: base = 1
        case .limited(let max): base = max
        case .rejected: base = 0
        }
        switch mode {
        case .truncate: break
        case .append(let offset): base += offset
        }
        if base == 0 {
            completion(.failure(MathError.divisionByZero))
        } else {
            completion(.success(base * multiplier))
        }
    }

    /// Carrier whose external Swift label is also a C# keyword. The parser rewrites such a
    /// label to a legal C# identifier (`for` → `_for`), so the Swift call the bridge writes
    /// has to recover the raw label — spelling the rewritten one addresses an argument the
    /// callee does not have, and that Swift compile error takes the whole wrapper library
    /// down with it, not just this member.
    ///
    /// Provenance: `retryResult(for:dueTo:completion:)` on a networking SDK's session type,
    /// where a keyword label rides alongside an enum error carrier and a completion handler.
    public func retryResult(
        for scope: FetchScope,
        dueTo mode: WriteMode,
        completion: (Result<Int32, any Error>) -> Void
    ) {
        switch scope {
        case .rejected:
            completion(.failure(MathError.divisionByZero))
        case .everything:
            completion(.success(7))
        case .limited(let max):
            switch mode {
            case .truncate: completion(.success(max))
            case .append(let offset): completion(.success(max + offset))
            }
        }
    }
}

/// Free-function form of the same carrier shape — the bridge's static path.
public func fetchWithScope(_ scope: FetchScope, completion: (Result<Int32, any Error>) -> Void) {
    switch scope {
    case .everything:
        completion(.success(100))
    case .limited(let max):
        completion(.success(max))
    case .rejected:
        completion(.failure(MathError.divisionByZero))
    }
}
