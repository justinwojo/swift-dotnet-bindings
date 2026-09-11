// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// A type whose ONLY public initializer takes a closure — the "stranded class" shape.
//
// Observed in an image-loading SDK: the loader's single initializer takes a validation
// block, and every other member of the type is ordinary and fully typed. If that one
// initializer refuses, the type binds as a shell — its methods are emitted but no consumer
// can build a receiver to call them on, so the whole type is dead surface behind one skip.
// The protocol the loader conforms to is the only other way in, which makes the existential
// arm the second, independent half of the same red.
//
// The block hands its parameter back mutated rather than returning a fresh value, which is
// what pushed the reported initializer off the supported set: the same write-back shape the
// builder hooks use, reached through a constructor instead of a method.

import Foundation

/// Protocol the loader conforms to — the existential route into the same instance method.
public protocol ResourceLoading {
    func load(path: String, onStatus: @escaping (Int32) -> Void) -> Int32
}

/// Loader constructible ONLY through a closure-taking initializer. `load` is ordinary and
/// fully typed; its reachability depends entirely on that initializer binding.
public final class ValidatingResourceLoader: ResourceLoading {
    private let configuration: Int32
    private let validate: (inout Int32) -> Bool

    /// Default validation block, the initializer's default argument in the reported shape.
    /// Clamps the status into range and reports whether it was already acceptable.
    public static func defaultValidate(status: inout Int32) -> Bool {
        if status >= 400 {
            status = 400
            return false
        }
        return true
    }

    /// The only public initializer.
    public init(configuration: Int32 = 0,
                validate: @escaping (inout Int32) -> Bool = ValidatingResourceLoader.defaultValidate) {
        self.configuration = configuration
        self.validate = validate
    }

    /// Fully typed instance method — reachable only once the initializer above binds.
    public func load(path: String, onStatus: @escaping (Int32) -> Void) -> Int32 {
        var status = Int32(path.count) + configuration
        onStatus(status)
        let accepted = validate(&status)
        return accepted ? status : -status
    }

    /// Drives the stored validation block directly so a consumer can observe both of its
    /// outcomes — the mutation and the verdict — without going through `load`.
    public func validateStatus(_ status: Int32) -> Int32 {
        var working = status
        let accepted = validate(&working)
        return accepted ? working : -working
    }
}

/// Sibling whose initializer block takes its parameter by value — the control that isolates
/// the write-back shape, rather than "a closure in an initializer", as the deciding factor.
public final class CountingResourceLoader {
    private let weigh: (Int32) -> Int32

    public init(weigh: @escaping (Int32) -> Int32) {
        self.weigh = weigh
    }

    public func weight(of value: Int32) -> Int32 {
        weigh(value)
    }
}

/// Consumer of the existential, so the protocol route has a member to reach through.
public func runResourceLoader(_ loader: any ResourceLoading, path: String) -> Int32 {
    loader.load(path: path, onStatus: { _ in })
}
