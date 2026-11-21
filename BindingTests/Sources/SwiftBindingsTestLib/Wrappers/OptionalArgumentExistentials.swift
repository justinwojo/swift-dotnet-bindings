// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Existentials through the optional-argument wrapper
//
// Every member here takes a `String?`, which is too large to pass as one pointer, so the generator
// routes it through the optional-argument wrapper (`_optbuf`): a `@_silgen_name` Swift function that
// takes the optional by address and forwards the rest. An existential in the signature keeps that
// wrapper on the Swift convention, so the C# import must declare what Swift's own lowering puts in
// the registers: a boxed `any Error` is one reference (taken and returned directly), and a single
// class-bound protocol is the two-word [reference][witness table] pair. Declaring the five-word
// opaque container instead returns the result through an indirect-result register the callee never
// writes, and passes the address of a managed copy where the callee reads the box.
//
// The errors are `TrackedError` (MemoryManagement/ExistentialReturnLeak.swift), so each test also
// checks that the +1 a returned error carries is released, and that a borrowed argument is not
// released on the callee's behalf.

/// An error factory whose members each take an optional message: the shape of a framework's
/// internal error-construction type.
public final class OptionalArgumentErrorFactory {
    public init() {}

    /// Returns a fresh tracked error whose tag is `code`, plus the underlying error's tag when it is one.
    public func error(code: Int32, message: String?, underlying: (any Error)?) -> any Error {
        let base = (underlying as? TrackedError)?.tag ?? 0
        return TrackedError(tag: code + base + Int32(message?.count ?? 0))
    }

    /// The static form, which Swift passes the metatype for. The optional existential keeps it on the
    /// Swift convention.
    public static func makeError(code: Int32, message: String?, underlying: (any Error)?) -> any Error {
        let base = (underlying as? TrackedError)?.tag ?? 0
        return TrackedError(tag: code + base + Int32(message?.count ?? 0))
    }

    /// Reads a borrowed error back: its tag when it is a tracked error, otherwise the fallback's length.
    public func code(of error: any Error, fallback: String?) -> Int32 {
        (error as? TrackedError)?.tag ?? Int32(fallback?.count ?? -1)
    }

    /// The same read with an optional existential beside it, which keeps the wrapper on the Swift
    /// convention: the borrowed error then travels as the one box reference.
    public func code(of error: any Error, fallback: String?, underlying: (any Error)?) -> Int32 {
        ((error as? TrackedError)?.tag ?? Int32(fallback?.count ?? -1)) + ((underlying as? TrackedError)?.tag ?? 0)
    }
}

/// The same shapes on a struct, which passes its value rather than a reference as `self`.
public struct OptionalArgumentErrorMaker {
    public let offset: Int32

    public init(offset: Int32) {
        self.offset = offset
    }

    public func make(message: String?, underlying: (any Error)?) -> any Error {
        let base = (underlying as? TrackedError)?.tag ?? 0
        return TrackedError(tag: offset + base + Int32(message?.count ?? 0))
    }
}

/// A class-bound protocol: its existential is the two-word [reference][witness table] pair.
public protocol OptionalArgumentTagged: AnyObject {
    var tag: Int32 { get }
}

public final class OptionalArgumentTaggedBox: OptionalArgumentTagged {
    public let tag: Int32

    public init(tag: Int32) {
        self.tag = tag
    }
}

/// Returns a class-bound existential through the optional-argument wrapper. The optional error keeps
/// the wrapper on the Swift convention, where the pair is returned in two registers.
public func makeOptionalArgumentTagged(_ tag: Int32, label: String?, underlying: (any Error)?) -> any OptionalArgumentTagged {
    OptionalArgumentTaggedBox(tag: tag + Int32(label?.count ?? 0) + ((underlying as? TrackedError)?.tag ?? 0))
}

/// Takes a class-bound existential through the optional-argument wrapper and reads it back.
public func optionalArgumentTagValue(_ value: any OptionalArgumentTagged, label: String?, underlying: (any Error)?) -> Int32 {
    value.tag + Int32(label?.count ?? 0) + ((underlying as? TrackedError)?.tag ?? 0)
}
