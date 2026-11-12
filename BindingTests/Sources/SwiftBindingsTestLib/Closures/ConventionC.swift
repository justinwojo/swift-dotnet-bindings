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

// MARK: - Constructors taking a non-optional @convention(c) closure

/// A failable initializer that takes a non-optional `@convention(c)` closure. A direct
/// Swift-convention call cannot deliver an allocating class init's hidden metatype nor decode a
/// failable `Optional<Self>` return, so the binding must reach it through the `@_cdecl` constructor
/// wrapper, which takes the C function pointer under its own type and keeps the init ABI on the
/// Swift side. Returns nil when the callback maps the seed to a negative value.
public class ConventionCValidatedLoader {
    public let value: Int32

    public init?(_ validate: @convention(c) (Int32) -> Int32, seed: Int32) {
        let v = validate(seed)
        guard v >= 0 else { return nil }
        self.value = v
    }
}

/// The same shape as `ConventionCValidatedLoader` with a trailing debug parameter
/// (`file: StaticString = #fileID`). The constructor wrapper leaves the debug parameter out of its
/// own signature and its call, so Swift fills in the default at the wrapper, and the binding takes
/// only the callback and the seed.
public class ConventionCDebugParamLoader {
    public let value: Int32

    public init?(_ validate: @convention(c) (Int32) -> Int32, seed: Int32, file: StaticString = #fileID) {
        let v = validate(seed)
        guard v >= 0 else { return nil }
        self.value = v
    }
}

/// The same shape with TWO closures — one non-optional `@convention(c)` and one ordinary Swift
/// closure. ABI JSON omits the convention attribute, so the only conv-c signal is the demangled
/// CFunctionPointer node, which the parser reads per parameter. The constructor wrapper passes the C
/// function pointer through and adapts the Swift closure, which the initializer calls with the
/// validated value before it returns.
public class ConventionCMultiClosureLoader {
    public let value: Int32

    public init?(_ validate: @convention(c) (Int32) -> Int32, onReady ready: (Int32) -> Void, seed: Int32) {
        let v = validate(seed)
        guard v >= 0 else { return nil }
        ready(v)
        self.value = v
    }
}

// MARK: - Several closures in one signature, at least one @convention(c)

/// Two `@convention(c)` closures in one signature. A C function pointer is one word and a Swift
/// closure two, and ABI JSON prints both the same way; only the mangled name records which
/// parameter is which. Binding either closure here as a two-word Swift closure shifts every later
/// argument by a register, so Swift would call the second pointer through the first one's context.
/// Returns `first(input) * 100 + second(input)` so each callback's result is told apart.
public func combineCFunctions(
    _ first: @convention(c) (Int32) -> Int32,
    _ second: @convention(c) (Int32) -> Int32,
    _ input: Int32
) -> Int32 {
    return first(input) &* 100 &+ second(input)
}

/// An ordinary Swift closure followed by a `@convention(c)` one: only the second parameter is a
/// bare function pointer. Returns `swiftFn(input) * 100 + cFn(input)`.
public func combineSwiftAndCClosures(
    _ swiftFn: (Int32) -> Int32,
    _ cFn: @convention(c) (Int32) -> Int32,
    _ input: Int32
) -> Int32 {
    return swiftFn(input) &* 100 &+ cFn(input)
}

/// Two `@convention(c)` closures whose parameters are pointers. A C function pointer hands a pointer
/// over as one raw word, which the managed side receives as `IntPtr`. Both closures are called
/// before this returns. `measure` gets the text's C string and returns its length; `report` gets the
/// address of `length * 2` together with the length. Returns the length.
public func measureThroughCFunctions(
    _ measure: @convention(c) (UnsafePointer<CChar>) -> Int32,
    _ report: @convention(c) (UnsafeRawPointer, Int32) -> Void,
    _ text: String
) -> Int32 {
    return text.withCString { cString in
        let length = measure(cString)
        var doubled = length &* 2
        withUnsafeBytes(of: &doubled) { report($0.baseAddress!, length) }
        return length
    }
}
