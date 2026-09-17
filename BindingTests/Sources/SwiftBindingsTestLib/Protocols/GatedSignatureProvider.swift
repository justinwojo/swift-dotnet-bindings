// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Requirement whose SIGNATURE TYPES are newer than the protocol
//
// Sibling to StaggeredAvailabilityDelegate, and a different failure. There the
// requirement itself carried the later floor while its signature was made of
// types available everywhere, so an under-annotated forwarder still compiled and
// merely mis-dispatched. Here the requirement's parameter and return types are
// themselves gated above the protocol's floor, so an under-annotated declaration
// does not compile at all:
//
//     'GatedPayload' is only available in iOS 17.0 or newer
//
// The reverse-dispatch conformance is where this bites. The generated
// `extension EveryProtocol: GatedSignatureProvider { ... }` carries availability
// merged from the protocol and its ancestors, which is the protocol's floor —
// and every witness inside it is declared in that context. A witness mentioning
// GatedPayload therefore needs its own, stricter `@available`, or the whole
// conformance fails to compile and takes the module's binding with it.
//
// A protocol adding a requirement that traffics in a type introduced alongside
// it is the ordinary way an already-shipped protocol grows: the feature and the
// types it speaks in arrive in the same SDK.

/// Value type introduced one OS version after the protocol that traffics in it.
/// Deliberately trivial — the availability floor is the whole point, not the layout.
@available(iOS 17.0, *)
public struct GatedPayload {
    public let seed: Int32
    public let doubled: Int32

    public init(seed: Int32) {
        self.seed = seed
        self.doubled = seed &* 2
    }
}

/// Protocol introduced before the payload type its later requirements use.
@available(iOS 16.0, *)
public protocol GatedSignatureProvider: AnyObject {
    /// Control: available at the protocol's own floor, signature made of types that
    /// are available everywhere. This witness must keep emitting exactly as it does
    /// today — the fix must annotate the gated members only.
    func baselineValue() -> Int32

    /// Gated return type. The witness cannot name GatedPayload at the protocol's floor.
    @available(iOS 17.0, *)
    func makeGatedPayload(seed: Int32) -> GatedPayload

    /// Gated parameter type, exercising the other half of the signature.
    @available(iOS 17.0, *)
    func consumeGatedPayload(_ payload: GatedPayload) -> Int32

    /// Gated property type. Properties take a different emission path from methods,
    /// so a fix applied only to the method path would leave this one red.
    @available(iOS 17.0, *)
    var currentGatedPayload: GatedPayload { get }

    /// Gated subscript. Subscripts are the third witness-emission path, separate from
    /// both methods and properties, and an instance subscript over a plain index with a
    /// struct return takes a real vtable slot rather than a stub — so a fix wired into
    /// only two of the three loops leaves this one red.
    @available(iOS 17.0, *)
    subscript(gatedIndex index: Int32) -> GatedPayload { get }
}

/// Harness that stores a provider and calls back into it from the Swift side, so a
/// C# conformer can be reached through the witness table rather than only inspected.
@available(iOS 16.0, *)
public class GatedSignatureHarness {
    /// Plain strong storage — this fixture is about availability, not lifetime.
    public var provider: GatedSignatureProvider?

    public init() {}

    /// Control path: no widening needed.
    public func invokeBaselineFromSwift() -> Int32 {
        return provider?.baselineValue() ?? -1
    }

    /// Round-trips a payload out of and back into the conformer. The `if #available`
    /// widens this call site to the payload's floor, which is the context the generated
    /// witness has to reproduce on its own declaration.
    public func roundTripGatedFromSwift(seed: Int32) -> Int32 {
        guard let provider = provider else { return -1 }
        if #available(iOS 17.0, *) {
            let payload = provider.makeGatedPayload(seed: seed)
            return provider.consumeGatedPayload(payload)
        }
        return -1
    }

    /// Reads the gated property through the witness table.
    public func readGatedPayloadFromSwift() -> Int32 {
        guard let provider = provider else { return -1 }
        if #available(iOS 17.0, *) {
            return provider.currentGatedPayload.doubled
        }
        return -1
    }

    /// Reads the gated subscript through the witness table, so the third emission path
    /// is exercised at runtime and not only compiled.
    public func readGatedSubscriptFromSwift(index: Int32) -> Int32 {
        guard let provider = provider else { return -1 }
        if #available(iOS 17.0, *) {
            return provider[gatedIndex: index].doubled
        }
        return -1
    }
}
