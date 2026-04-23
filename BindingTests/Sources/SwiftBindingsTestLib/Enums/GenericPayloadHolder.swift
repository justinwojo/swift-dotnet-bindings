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

// Apple-framework-shape sibling fixture. The ABI typespec name for the payload
// in `verified(SignedType)` resolves to NamedTypeSpec("SignedType") — a
// multi-character generic parameter name that is NOT in the simple-letter
// shortlist used by TypeSpecHelpers.IsGenericTypeParameter (`T`, `U`, `K`, …).
// `Holder<T>` above hides this regression because the single letter `T` does
// match the shortlist; with `T` renamed to `SignedType`, the same pattern
// reproduces the StoreKit2.VerificationResult<SignedType> shape end-to-end.
//
// Extraction (TryGetWrapped) goes through the same value-witness-table copy +
// runtime class-vs-struct dispatch as `Holder<T>` — Mono JIT and NativeAOT
// have historically diverged on generic-enum payload marshalling, so we
// re-exercise both runtimes with the Apple-shape resolution path.

/// Generic enum whose generic parameter has a multi-character SUGARED name,
/// matching the Apple framework ABI shape (e.g. VerificationResult<SignedType>).
public enum AppleHolder<SignedType> {
    case verified(SignedType)
    case invalid
}

public func makeVerifiedAppleString(_ value: String) -> AppleHolder<String> {
    return .verified(value)
}

public func makeInvalidAppleString() -> AppleHolder<String> {
    return .invalid
}

public func makeVerifiedAppleIntBox(_ value: Int32) -> AppleHolder<IntBox> {
    return .verified(IntBox(value: value))
}

public func makeInvalidAppleIntBox() -> AppleHolder<IntBox> {
    return .invalid
}
