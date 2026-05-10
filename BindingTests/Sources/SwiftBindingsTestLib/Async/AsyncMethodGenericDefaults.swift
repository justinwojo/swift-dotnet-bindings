// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Async / throws method-level generics with default-valued parameters
// Mirrors StoreKit2 `Product.purchase<S: UIScene>(confirmIn:, options: Set<...> = [])`:
// a class-bound (non-CSM) generic parameter constrained to a protocol, alongside
// a Set-valued defaulted parameter, on an async throws method that returns a
// complex value type.
//
// The generator should emit:
//   1. An async-method-generic bridge for the primary overload (uses Swift 5.7+
//      implicit existential opening on the class-bound generic param).
//   2. A trim default-parameter overload that omits `options` and threads the
//      method-own generic header + where-clause through the @_silgen_name shim.

/// Class-bound presenter protocol — analog to `UIScene`. Marked `AnyObject` so
/// the bridge's existential opening (`Unmanaged<AnyObject>.fromOpaque`) is sound.
public protocol AsyncGenericPresenter: AnyObject {
    var presenterId: String { get }
}

/// Concrete presenter conformer used by the runtime tests.
public class AsyncGenericPresenterImpl: AsyncGenericPresenter {
    public let presenterId: String

    public init(presenterId: String) {
        self.presenterId = presenterId
    }
}

/// Complex value-type return. The Swift struct is non-frozen (the default in
/// Swift 5+ for non-`@frozen` library-evolution-stable types), so the C# side
/// projects it as `ClassWithOpaquePayload` (a partial class backed by a
/// `SwiftSafeHandle`). The async-bridge ComplexValue path exercises the
/// `cbTakesOwnership` branch: Swift heap-allocates the carrier via
/// `UnsafeMutableRawPointer.allocate` + `initializeMemory(as:)`; the C#
/// callback `NativeMemory.Alloc`s a fresh `__resultBuf`, `InitializeWithCopy`s
/// the carrier into it, then VWT-`Destroy`s the original carrier (releasing
/// its +1) — the SafeHandle takes ownership of `__resultBuf` and frees it via
/// `NativeMemory.Free` in `ReleaseHandle`; the Swift carrier itself is freed
/// in the callback's `finally` via the per-module `SBW_Free` helper
/// (`ptr?.deallocate()`, allocator-paired with the original `.allocate`).
/// Mirrors `Product.PurchaseResult`'s "small struct" shape.
public struct AsyncGenericPurchaseResult: Equatable {
    public let presenterIdHash: Int64
    public let optionCount: Int32
    public let succeeded: Bool

    public init(presenterIdHash: Int64, optionCount: Int32, succeeded: Bool) {
        self.presenterIdHash = presenterIdHash
        self.optionCount = optionCount
        self.succeeded = succeeded
    }
}

public enum AsyncGenericPurchaseError: Error {
    case presenterMissing
    case cancelled
}

/// Non-generic class with a class-bound async generic method that has a
/// Set-valued defaulted parameter — the StoreKit2 `Product.purchase` shape.
public class AsyncGenericProduct {
    public let title: String

    public init(title: String) {
        self.title = title
    }

    /// Primary async generic method:
    ///   • `<S: AsyncGenericPresenter>` is class-bound non-CSM — the bridge opens
    ///     it via `Unmanaged<AnyObject>.fromOpaque`.
    ///   • `options: Set<Int> = []` is a defaulted non-trivial value type — the
    ///     trim overload omits it and lets Swift fill the default. `Int` element
    ///     keeps the runtime-side Hashable witness lookup on the well-known
    ///     primitive path (HashableConformanceRegistry); custom-enum elements
    ///     would require an orthogonal generator change to register a
    ///     conformance descriptor for plain `enum : int` projections.
    ///   • Returns a non-frozen struct projected as `ClassWithOpaquePayload`
    ///     — exercises the bridge's `cbTakesOwnership` branch (carrier copy
    ///     into `__resultBuf`, VWT-Destroy of the original carrier, Swift
    ///     `SBW_Free` of the carrier allocation).
    public func purchase<S: AsyncGenericPresenter>(
        confirmIn scene: S,
        options: Set<Int> = []
    ) async throws -> AsyncGenericPurchaseResult {
        try? await Task.sleep(nanoseconds: 1_000_000)

        // Use scene + options so the call materialises both inputs and we can
        // distinguish primary vs trim overload from the C# side.
        if scene.presenterId.isEmpty {
            throw AsyncGenericPurchaseError.presenterMissing
        }

        let hash = scene.presenterId.unicodeScalars
            .map { Int64($0.value) }
            .reduce(Int64(0), &+)
        return AsyncGenericPurchaseResult(
            presenterIdHash: hash,
            optionCount: Int32(options.count),
            succeeded: true)
    }
}
