// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// Test-only enforcement file for the emitted Swift. Never part of a binding.
//
// The generated wrapper is compiled into a module that imports the bound library, and much of it
// sits inside `extension <BoundType>` and `extension EveryProtocol: <BoundProtocol>` bodies. There,
// any member the library declares — a property, a static func, a typealias in a protocol
// extension — outranks a standard-library name of the same spelling, so a bare `Unmanaged`,
// `MemoryLayout` or `max(` in emitted code silently binds to the library's member instead. Emitted
// Swift therefore spells every standard-library name it relies on as `Swift.X` (or
// `_Concurrency.X`), which no member can capture.
//
// This file makes that invariant compiler-checked. The compile-only gate typechecks each emitted
// wrapper together with this file in the wrapper's own module, where these unavailable
// declarations outrank the imported standard library for any unqualified lookup: a bare use of
// one of these names fails to typecheck, while a qualified `Swift.X` resolves past them.
// Functions overload rather than shadow, so each is declared with the standard library's exact
// signature; with nothing left to rank the two apart, the same-module declaration wins the
// unqualified call.
//
// What it cannot enforce: `type(of:)`, a compiler special form no declaration can shadow, and
// type sugar (`[T]`, `[K: V]`, `T?`, `()`), which names the standard-library types without
// looking them up. The element and wrapped types inside the sugar are still checked.
//
// The gate only typechecks; nothing here is linked or shipped, and the library under test never
// sees these declarations.

// MARK: Types

@available(*, unavailable, message: "emitted Swift must qualify this standard-library name") public struct UnsafeMutableRawPointer {}
@available(*, unavailable, message: "emitted Swift must qualify this standard-library name") public struct UnsafeRawPointer {}
@available(*, unavailable, message: "emitted Swift must qualify this standard-library name") public struct UnsafeMutableRawBufferPointer {}
@available(*, unavailable, message: "emitted Swift must qualify this standard-library name") public struct UnsafeRawBufferPointer {}
@available(*, unavailable, message: "emitted Swift must qualify this standard-library name") public struct OpaquePointer {}
@available(*, unavailable, message: "emitted Swift must qualify this standard-library name") public struct UTF8 {}
@available(*, unavailable, message: "emitted Swift must qualify this standard-library name") public struct UTF16 {}
@available(*, unavailable, message: "emitted Swift must qualify this standard-library name") public struct Int {}
@available(*, unavailable, message: "emitted Swift must qualify this standard-library name") public struct Int8 {}
@available(*, unavailable, message: "emitted Swift must qualify this standard-library name") public struct Int16 {}
@available(*, unavailable, message: "emitted Swift must qualify this standard-library name") public struct Int32 {}
@available(*, unavailable, message: "emitted Swift must qualify this standard-library name") public struct Int64 {}
@available(*, unavailable, message: "emitted Swift must qualify this standard-library name") public struct UInt {}
@available(*, unavailable, message: "emitted Swift must qualify this standard-library name") public struct UInt8 {}
@available(*, unavailable, message: "emitted Swift must qualify this standard-library name") public struct UInt16 {}
@available(*, unavailable, message: "emitted Swift must qualify this standard-library name") public struct UInt32 {}
@available(*, unavailable, message: "emitted Swift must qualify this standard-library name") public struct UInt64 {}
@available(*, unavailable, message: "emitted Swift must qualify this standard-library name") public struct Double {}
@available(*, unavailable, message: "emitted Swift must qualify this standard-library name") public struct Float {}
@available(*, unavailable, message: "emitted Swift must qualify this standard-library name") public struct Bool {}
@available(*, unavailable, message: "emitted Swift must qualify this standard-library name") public struct String {}
@available(*, unavailable, message: "emitted Swift must qualify this standard-library name") public struct Character {}
@available(*, unavailable, message: "emitted Swift must qualify this standard-library name") public struct Substring {}
@available(*, unavailable, message: "emitted Swift must qualify this standard-library name") public struct StaticString {}
@available(*, unavailable, message: "emitted Swift must qualify this standard-library name") public struct ObjectIdentifier {}
@available(*, unavailable, message: "emitted Swift must qualify this standard-library name") public struct AnyHashable {}
@available(*, unavailable, message: "emitted Swift must qualify this standard-library name") public struct AnyKeyPath {}
@available(*, unavailable, message: "emitted Swift must qualify this standard-library name") public struct CancellationError {}
@available(*, unavailable, message: "emitted Swift must qualify this standard-library name") public struct UnsafeMutablePointer<Pointee> {}
@available(*, unavailable, message: "emitted Swift must qualify this standard-library name") public struct UnsafePointer<Pointee> {}
@available(*, unavailable, message: "emitted Swift must qualify this standard-library name") public struct UnsafeMutableBufferPointer<Element> {}
@available(*, unavailable, message: "emitted Swift must qualify this standard-library name") public struct UnsafeBufferPointer<Element> {}
@available(*, unavailable, message: "emitted Swift must qualify this standard-library name") public struct MemoryLayout<T> { public static var size: Swift.Int { 0 }; public static var stride: Swift.Int { 0 }; public static var alignment: Swift.Int { 0 } }
@available(*, unavailable, message: "emitted Swift must qualify this standard-library name") public struct Unmanaged<Instance> {}
@available(*, unavailable, message: "emitted Swift must qualify this standard-library name") public struct SIMD2<Scalar> {}
@available(*, unavailable, message: "emitted Swift must qualify this standard-library name") public struct SIMD3<Scalar> {}
@available(*, unavailable, message: "emitted Swift must qualify this standard-library name") public struct SIMD4<Scalar> {}
@available(*, unavailable, message: "emitted Swift must qualify this standard-library name") public struct Array<Element> {}
@available(*, unavailable, message: "emitted Swift must qualify this standard-library name") public struct ContiguousArray<Element> {}
@available(*, unavailable, message: "emitted Swift must qualify this standard-library name") public struct ArraySlice<Element> {}
@available(*, unavailable, message: "emitted Swift must qualify this standard-library name") public struct Set<Element> {}
@available(*, unavailable, message: "emitted Swift must qualify this standard-library name") public struct Optional<Wrapped> {}
@available(*, unavailable, message: "emitted Swift must qualify this standard-library name") public struct PartialKeyPath<Root> {}
@available(*, unavailable, message: "emitted Swift must qualify this standard-library name") public struct Dictionary<Key, Value> {}
@available(*, unavailable, message: "emitted Swift must qualify this standard-library name") public struct Result<Success, Failure> {}
@available(*, unavailable, message: "emitted Swift must qualify this standard-library name") public struct KeyPath<Root, Value> {}
@available(*, unavailable, message: "emitted Swift must qualify this standard-library name") public struct WritableKeyPath<Root, Value> {}
@available(*, unavailable, message: "emitted Swift must qualify this standard-library name") public struct ReferenceWritableKeyPath<Root, Value> {}
@available(*, unavailable, message: "emitted Swift must qualify this standard-library name") public struct CheckedContinuation<T, E> {}
@available(*, unavailable, message: "emitted Swift must qualify this standard-library name") public struct UnsafeContinuation<T, E> {}
@available(*, unavailable, message: "emitted Swift must qualify this standard-library name") public struct Task<Success, Failure> {}
@available(*, unavailable, message: "emitted Swift must qualify this standard-library name") public protocol Error {}
@available(*, unavailable, message: "emitted Swift must qualify this standard-library name") public protocol Sendable {}
@available(*, unavailable, message: "emitted Swift must qualify this standard-library name") public protocol Copyable {}
@available(*, unavailable, message: "emitted Swift must qualify this standard-library name") public protocol Escapable {}
@available(*, unavailable, message: "emitted Swift must qualify this standard-library name") public protocol CustomStringConvertible {}
@available(*, unavailable, message: "emitted Swift must qualify this standard-library name") public protocol Hashable {}
@available(*, unavailable, message: "emitted Swift must qualify this standard-library name") public protocol Equatable {}
@available(*, unavailable, message: "emitted Swift must qualify this standard-library name") public protocol Comparable {}
@available(*, unavailable, message: "emitted Swift must qualify this standard-library name") public typealias Void = Swift.Never
@available(*, unavailable, message: "emitted Swift must qualify this standard-library name") public typealias Never = Swift.Never
@available(*, unavailable, message: "emitted Swift must qualify this standard-library name") public typealias AnyObject = Swift.Never
@available(*, unavailable, message: "emitted Swift must qualify this standard-library name") public typealias CChar = Swift.Never

// MARK: Functions (exact standard-library signatures)

@available(*, unavailable, message: "emitted Swift must qualify this standard-library name") public func unsafeBitCast<T, U>(_ x: T, to type: U.Type) -> U { Swift.fatalError() }
@available(*, unavailable, message: "emitted Swift must qualify this standard-library name") public func unsafeDowncast<T: Swift.AnyObject>(_ x: Swift.AnyObject, to type: T.Type) -> T { Swift.fatalError() }
@available(*, unavailable, message: "emitted Swift must qualify this standard-library name") public func numericCast<T: Swift.BinaryInteger, U: Swift.BinaryInteger>(_ x: T) -> U { Swift.fatalError() }
@available(*, unavailable, message: "emitted Swift must qualify this standard-library name") public func fatalError(_ message: @autoclosure () -> Swift.String = Swift.String(), file: Swift.StaticString = #file, line: Swift.UInt = #line) -> Swift.Never { Swift.fatalError() }
@available(*, unavailable, message: "emitted Swift must qualify this standard-library name") public func preconditionFailure(_ message: @autoclosure () -> Swift.String = Swift.String(), file: Swift.StaticString = #file, line: Swift.UInt = #line) -> Swift.Never { Swift.fatalError() }
@available(*, unavailable, message: "emitted Swift must qualify this standard-library name") public func precondition(_ condition: @autoclosure () -> Swift.Bool, _ message: @autoclosure () -> Swift.String = Swift.String(), file: Swift.StaticString = #file, line: Swift.UInt = #line) { Swift.fatalError() }
@available(*, unavailable, message: "emitted Swift must qualify this standard-library name") public func max<T: Swift.Comparable>(_ x: T, _ y: T) -> T { Swift.fatalError() }
@available(*, unavailable, message: "emitted Swift must qualify this standard-library name") public func max<T: Swift.Comparable>(_ x: T, _ y: T, _ z: T, _ rest: T...) -> T { Swift.fatalError() }
@available(*, unavailable, message: "emitted Swift must qualify this standard-library name") public func min<T: Swift.Comparable>(_ x: T, _ y: T) -> T { Swift.fatalError() }
@available(*, unavailable, message: "emitted Swift must qualify this standard-library name") public func min<T: Swift.Comparable>(_ x: T, _ y: T, _ z: T, _ rest: T...) -> T { Swift.fatalError() }
@available(*, unavailable, message: "emitted Swift must qualify this standard-library name") public func withExtendedLifetime<T, E, Result>(_ x: borrowing T, _ body: () throws(E) -> Result) throws(E) -> Result where E: Swift.Error, T: ~Swift.Copyable, T: ~Swift.Escapable, Result: ~Swift.Copyable { Swift.fatalError() }
@available(*, unavailable, message: "emitted Swift must qualify this standard-library name") public func withExtendedLifetime<T, E, Result>(_ x: borrowing T, _ body: (borrowing T) throws(E) -> Result) throws(E) -> Result where E: Swift.Error, T: ~Swift.Copyable, T: ~Swift.Escapable, Result: ~Swift.Copyable { Swift.fatalError() }
@available(*, unavailable, message: "emitted Swift must qualify this standard-library name") public func withUnsafePointer<T, E, Result>(to value: borrowing T, _ body: (Swift.UnsafePointer<T>) throws(E) -> Result) throws(E) -> Result where E: Swift.Error, T: ~Swift.Copyable, Result: ~Swift.Copyable { Swift.fatalError() }
@available(*, unavailable, message: "emitted Swift must qualify this standard-library name") public func withUnsafePointer<T, E, Result>(to value: inout T, _ body: (Swift.UnsafePointer<T>) throws(E) -> Result) throws(E) -> Result where E: Swift.Error, T: ~Swift.Copyable, Result: ~Swift.Copyable { Swift.fatalError() }
@available(*, unavailable, message: "emitted Swift must qualify this standard-library name") public func withUnsafeMutablePointer<T, E, Result>(to value: inout T, _ body: (Swift.UnsafeMutablePointer<T>) throws(E) -> Result) throws(E) -> Result where E: Swift.Error, T: ~Swift.Copyable, Result: ~Swift.Copyable { Swift.fatalError() }
@available(*, unavailable, message: "emitted Swift must qualify this standard-library name") public func withUnsafeBytes<T, E, Result>(of value: inout T, _ body: (Swift.UnsafeRawBufferPointer) throws(E) -> Result) throws(E) -> Result where E: Swift.Error, T: ~Swift.Copyable, Result: ~Swift.Copyable { Swift.fatalError() }
@available(*, unavailable, message: "emitted Swift must qualify this standard-library name") public func withUnsafeBytes<T, E, Result>(of value: borrowing T, _ body: (Swift.UnsafeRawBufferPointer) throws(E) -> Result) throws(E) -> Result where E: Swift.Error, T: ~Swift.Copyable, Result: ~Swift.Copyable { Swift.fatalError() }
@available(*, unavailable, message: "emitted Swift must qualify this standard-library name") public func withUnsafeMutableBytes<T, E, Result>(of value: inout T, _ body: (Swift.UnsafeMutableRawBufferPointer) throws(E) -> Result) throws(E) -> Result where E: Swift.Error, T: ~Swift.Copyable, Result: ~Swift.Copyable { Swift.fatalError() }
@available(*, unavailable, message: "emitted Swift must qualify this standard-library name") public func withCheckedContinuation<T>(isolation: isolated (any _Concurrency.Actor)? = #isolation, function: Swift.String = #function, _ body: (_Concurrency.CheckedContinuation<T, Swift.Never>) -> Swift.Void) async -> sending T { Swift.fatalError() }
@available(*, unavailable, message: "emitted Swift must qualify this standard-library name") public func withCheckedThrowingContinuation<T>(isolation: isolated (any _Concurrency.Actor)? = #isolation, function: Swift.String = #function, _ body: (_Concurrency.CheckedContinuation<T, any Swift.Error>) -> Swift.Void) async throws -> sending T { Swift.fatalError() }
