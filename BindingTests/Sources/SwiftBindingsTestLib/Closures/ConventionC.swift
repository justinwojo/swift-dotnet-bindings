// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - @convention(c) Closures

/// Calls a C-convention function pointer with an Int32.
public func callCFunction(_ fn: @convention(c) (Int32) -> Int32) -> Int32 {
    return fn(42)
}

/// Calls a C-convention void function pointer.
public func callCVoidFunction(_ fn: @convention(c) () -> Void) {
    fn()
}

/// Calls a C-convention function with two arguments.
public func callCBinaryFunction(_ fn: @convention(c) (Int32, Int32) -> Int32) -> Int32 {
    return fn(10, 20)
}

/// Calls a C-convention predicate.
public func callCPredicate(_ fn: @convention(c) (Int32) -> Bool, value: Int32) -> Bool {
    return fn(value)
}

// MARK: - Escaping @convention(c) closures (non-optional)

/// Calls a NON-optional `@escaping @convention(c)` closure twice in a row. The C# binding marshals
/// this through a per-method `[ThreadStatic]` delegate slot wrapped in a save/restore discipline: a
/// reentrant call into this same function (with a different closure) during the first invocation
/// installs its own delegate and then restores the outer delegate in its finally, so the second
/// invocation here still calls the original closure. Returns `first + second`.
public func applyConventionCTwice(_ fn: @escaping @convention(c) (Int32) -> Int32, _ input: Int32) -> Int32 {
    let first = fn(input)
    let second = fn(input)
    return first &+ second
}

// MARK: - Constructor taking a non-optional @convention(c) closure (skip-regression guard)

/// A type whose initializer takes a non-optional `@convention(c)` closure. This combination has no
/// ABI-correct binding surface: the closure parameter denies the init a native thunk and blocks the
/// `@_cdecl` constructor wrapper, leaving only a direct CallConvSwift call against the raw init
/// symbol — which cannot deliver an allocating class init's hidden metatype nor decode a failable
/// `Optional<Self>` return. The generator must therefore SKIP this member (emit an Unsupported
/// comment, not a callable factory) so the rest of the binding still compiles; emitting it would
/// produce code that compiles but faults at runtime. This fixture is the end-to-end regression guard
/// for that skip: a real compiled-Swift init of this shape must not break the generated binding.
public class ConventionCValidatedLoader {
    public let value: Int32

    public init?(_ validate: @convention(c) (Int32) -> Int32, seed: Int32) {
        let v = validate(seed)
        guard v >= 0 else { return nil }
        self.value = v
    }
}

/// Same broken shape as `ConventionCValidatedLoader`, but with a trailing compiler-injected debug
/// parameter (`file: StaticString = #fileID`). The debug parameter drives the generator's debug-param
/// wrapper, which marks the constructor as wrapper-backed; the conv-c skip must still fire (it runs
/// before the debug-param wrapper and does not depend on the wrapper flag), so a `#file`/`#line`
/// default cannot route the broken init around the skip. End-to-end guard: a real init of this shape
/// must keep the generated binding compiling rather than emitting a slot restore with no matching save.
public class ConventionCDebugParamLoader {
    public let value: Int32

    public init?(_ validate: @convention(c) (Int32) -> Int32, seed: Int32, file: StaticString = #fileID) {
        let v = validate(seed)
        guard v >= 0 else { return nil }
        self.value = v
    }
}

/// Same broken shape, but the constructor takes TWO closures — one non-optional `@convention(c)` and
/// one ordinary Swift closure. ABI JSON omits the convention attribute, so the only conv-c signal is
/// the demangled CFunctionPointer node; the per-parameter classifier suppresses that signal once a
/// method has more than one closure, so the skip relies on the whole-method check instead. End-to-end
/// guard that a multi-closure init of this shape is still skipped rather than emitted as a broken
/// direct call.
public class ConventionCMultiClosureLoader {
    public let value: Int32

    public init?(_ validate: @convention(c) (Int32) -> Int32, onReady ready: (Int32) -> Void, seed: Int32) {
        let v = validate(seed)
        guard v >= 0 else { return nil }
        ready(v)
        self.value = v
    }
}
