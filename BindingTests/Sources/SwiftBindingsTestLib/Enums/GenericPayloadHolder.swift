// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// Generic enum with a bare-generic-type-parameter payload.
//
// Mirrors StoreKit2.VerificationResult<TSignedType> shape at the ABI level:
// a payload case whose associated value is a raw type-parameter reference
// (`τ_0_0` in ABI JSON), plus a no-payload case. The factory direction has
// long emitted correctly (stackalloc by TypeMetadata<T>.Size + MarshalToSwift),
// but Issue E.1 adds the extraction mirror so TryGetWrapped returns the payload
// rather than leaving the enum as read-only tag + DebugDescription.
//
// Exercised with both a frozen-struct T (String — SwiftString frozen struct,
// stresses fixed-size VWT copy) and a class T (IntBox — ARC retain/release),
// on both Mono JIT and NativeAOT. Generic-enum payload marshalling stresses
// value-witness-table copies and metadata-accessor resolution, both of which
// have hit runtime-specific bugs in the past. Primitive Swift types (Int32)
// can't be used as T here because the emitted Holder<T> carries a
// `where T : ISwiftObject` constraint and `Int32` does not satisfy it.

/// Generic enum with a single generic-typed associated value + an empty case.
public enum Holder<T> {
    case wrapped(T)
    case empty
}

/// Frozen-struct T fixture: Holder<String>. SwiftString projects to an
/// ISwiftObject-implementing frozen struct, exercising the fixed-size payload
/// extraction path without the reference-counting noise of a class T.
public func makeWrappedString(_ value: String) -> Holder<String> {
    return .wrapped(value)
}

public func makeEmptyString() -> Holder<String> {
    return .empty
}

/// Simple class payload to exercise reference-type T. Classes carry retained
/// references in the enum payload — different VWT layout than a frozen struct.
public class IntBox {
    public let value: Int32
    public init(value: Int32) {
        self.value = value
    }
}

public func makeWrappedIntBox(_ value: Int32) -> Holder<IntBox> {
    return .wrapped(IntBox(value: value))
}

public func makeEmptyIntBox() -> Holder<IntBox> {
    return .empty
}
