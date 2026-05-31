// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Class-superclass-constrained existential proxy (RC-PROXY Failure B, read-only path)
//
// End-to-end ABI gate for a protocol whose `Self` is constrained to a Swift class superclass.
// `EntityRootedProbe: Entity` models the shape of RealityKit's Entity-rooted protocols (the
// `EntityGestureRecognizer` family, the `HasCollision`-style entity protocols) using a
// pure-Swift `Entity` stand-in, so the gate stays portable across every BindingTests cell
// (iOS sim, device/NativeAOT, macOS, Catalyst, and tvOS, which has no RealityKit). This
// mirrors the pure-Swift stand-in convention used by `GestureHostBase` for the class-bound
// array repros.
//
// IMPORTANT: this stand-in is NOT `RealityFoundation.Entity`. The generator's Entity-carrier
// detection (`IsRealityFoundationEntityName`) matches the simple name `Entity` only when its
// qualified spelling is `RealityFoundation.Entity`, `RealityKit.Entity`, or a bare unqualified
// `Entity`. Our stand-in's superclass is spelled `SwiftBindingsTestLib.Entity` in the ABI
// (module-qualified), which matches none of those, so the protocol is classified as an ordinary
// class-superclass requirement and routed through the *read-only* proxy path
// (`RecordSkip("ClassSuperclassRequired")`), NOT the `EveryEntityProtocol` carrier. The
// EveryEntityProtocol carrier itself (the real-`Entity` case, where the full vtable-backed
// conformance + witness getter ARE emitted) is covered by the generator unit tests; this
// fixture pins the *runtime* behaviour of the read-only path that any non-RealityFoundation
// class-superclass protocol falls into:
//   * RETURN   — Swift vends `any EntityRootedProbe`; C# reads it through a proxy that
//                dispatches via the existential's own witness table.            (supported)
//   * ACCEPT   — C# passes a Swift-vended proxy back into Swift.                (supported)
//   * CALLBACK — C# *implements* the protocol and passes it back to Swift. The wrapper exports
//                no `Get_EveryProtocol_{P}_WitnessTable` getter for a skipped class-superclass
//                conformance, so the generated proxy fails clean with NotSupportedException at
//                the C#→Swift boundary instead of an EntryPointNotFoundException. (unsupported)

/// Pure-Swift stand-in for a class superclass requirement (shaped like a subclassable framework
/// base such as `RealityFoundation.Entity`). `open` + a public `required init()` mirrors a base
/// class that a synthesized helper could subclass.
open class Entity {
    public required init() {}
}

/// Class-superclass-constrained protocol that adds its own requirements (`marker`, `ping`).
public protocol EntityRootedProbe: Entity {
    var marker: String { get }
    func ping(_ value: String) -> String
}

/// Swift conformer: a concrete `Entity` subclass conforming to `EntityRootedProbe`.
public final class EntityRootedProbeImpl: Entity, EntityRootedProbe {
    private let markerValue: String
    public init(marker: String) {
        self.markerValue = marker
        super.init()
    }
    public required init() {
        self.markerValue = "swift-default"
        super.init()
    }
    public var marker: String { markerValue }
    public func ping(_ value: String) -> String { "swift:\(markerValue):\(value)" }
}

/// Vendor exercising every proxy direction across the Entity-rooted carrier.
public final class EntityRootedProbeVendor {
    public init() {}

    /// RETURN: Swift vends `any EntityRootedProbe`; C# materialises a proxy off the carrier.
    public func makeProbe(marker: String) -> any EntityRootedProbe {
        EntityRootedProbeImpl(marker: marker)
    }

    /// Optional RETURN — covers the `isOptionalReturn` branch (nil → null; non-nil → proxy).
    public func makeProbeIf(present: Bool, marker: String) -> (any EntityRootedProbe)? {
        present ? EntityRootedProbeImpl(marker: marker) : nil
    }

    /// ACCEPT: C# passes a Swift-vended `any EntityRootedProbe` into Swift; Swift dispatches the
    /// protocol requirements through the existential's own witness table. (A C# *implementation*
    /// passed here instead fails clean with NotSupportedException at the C#→Swift boundary — the
    /// read-only proxy has no witness-table getter to synthesize the existential. See the C# test.)
    public func describe(_ p: any EntityRootedProbe) -> String {
        "\(p.marker)#\(p.ping("call"))"
    }

    /// Round-trip: accept then re-vend the same existential, proving the carrier survives a
    /// Swift round-trip without over-release.
    public func echo(_ p: any EntityRootedProbe) -> any EntityRootedProbe { p }
}
