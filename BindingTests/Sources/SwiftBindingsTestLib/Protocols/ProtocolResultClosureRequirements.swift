// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// A protocol requirement whose parameter is a `(Result<T, any Error>) -> Void` closure, satisfied
// by a public final class. The interface declaration and the conformer's method are produced by
// different translators (the protocol signature path spells the closure from its projection; the
// class member goes through the method-closure bridge), and the generic arguments of the Result
// are where they can disagree: the failure arm is an existential, and `Data` has an idiomatic
// C# projection (`byte[]`) that is not the carrier the container marshals through. When the two
// spellings differ the class declares the interface but never implements the member as declared
// (CS0535), so the whole binding fails to compile.
//
// Provenance: a database access library's `DatabaseReader.asyncRead(_:)` requirement over
// `Result<Database, Error>` and an image caching library's `ImageDataProvider.data(handler:)`
// over `Result<Data, Error>`, both satisfied by several public conformers.

public final class ResultCallbackPayload {
    public let label: String
    public let magnitude: Int32

    public init(label: String, magnitude: Int32) {
        self.label = label
        self.magnitude = magnitude
    }
}

public struct ResultCallbackError: Error {
    public let code: Int32

    public init(code: Int32) {
        self.code = code
    }
}

public protocol ResultCallbackSource {
    /// Class success arm — a bound class on the success side, an existential on the failure side.
    func load(_ completion: @escaping (Result<ResultCallbackPayload, Error>) -> Void)

    /// `Data` success arm — the element whose public projection differs from its container carrier.
    func loadData(handler: @escaping (Result<Data, Error>) -> Void)
}

public final class ResultCallbackFileSource: ResultCallbackSource {
    public let shouldFail: Bool

    public init(shouldFail: Bool) {
        self.shouldFail = shouldFail
    }

    public func load(_ completion: @escaping (Result<ResultCallbackPayload, Error>) -> Void) {
        if shouldFail {
            completion(.failure(ResultCallbackError(code: 7)))
        } else {
            completion(.success(ResultCallbackPayload(label: "loaded", magnitude: 42)))
        }
    }

    public func loadData(handler: @escaping (Result<Data, Error>) -> Void) {
        if shouldFail {
            handler(.failure(ResultCallbackError(code: 9)))
        } else {
            handler(.success(Data([1, 2, 3, 4])))
        }
    }
}
