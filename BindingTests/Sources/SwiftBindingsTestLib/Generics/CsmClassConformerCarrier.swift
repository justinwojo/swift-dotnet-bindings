// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// Audit Track-A6 fixtures for the CSM (Concrete Specialization Mechanism) class-conformer
// return path. CarrierItem is a dedicated, hint-registered protocol (see
// specialization-hints.json) whose SOLE conformer is a `final class`, kept distinct from
// SearchableItem/ValidatableItem so it drives its own conformer-pairing run and does not
// ripple into the GenericContainer / ElementBoundContainer CSM matrices.
//
// The class conformer is the point: a generic method returning its own type parameter
// (`func f<T: CarrierItem>(_ t: T) -> T`) with a CLASS conformer routes through the
// `returnsGenericParam && TypeRecordKind.Class` carrier path. The Swift @_cdecl wrapper
// stores the instance pointer INTO the indirect-return carrier via `initializeMemory`
// (carrier owns +1); C# must read the slot's contents and adopt that +1, then raw-free the
// one-word carrier — NOT wrap the carrier address as the instance (audit P0-11: that was a
// use-after-free on the freed carrier plus a leak of the real instance).

public protocol CarrierItem {
    var carrierTag: Int32 { get }
}

/// `final class` conformer — the carrier path only triggers when the conformer's TypeRecord
/// is a class. A stored `carrierTag` lets the round-trip test pin payload survival (a
/// carrier-address UAF would read garbage, not the stored value). The instance feeds the
/// shared allocation counters (see Lifetime/OwnershipTests.swift) so the leak half of P0-11
/// is testable: the old carrier-wrap adopted the carrier ADDRESS, never the +1 the Swift
/// wrapper stored into the slot, so the real instance leaked (live count never returned to
/// zero). The fixed path adopts the slot's +1, so a release brings live back to zero.
public final class CarrierClass: CarrierItem {
    public let carrierTag: Int32
    public init(carrierTag: Int32) {
        self.carrierTag = carrierTag
        recordTrackedAllocation()
    }
    deinit { recordTrackedDeallocation() }
}

public struct CarrierBox {
    public init() {}

    /// P0-11: generic-parameter return with a class conformer. CSM emits the concrete
    /// overload `CarrierClass Carry(CarrierClass)`, exercising the class-carrier slot read.
    public func carry<T: CarrierItem>(_ item: T) -> T {
        return item
    }

    /// P1-22: a non-generic parameter whose Swift label is `resultPtr` — the exact spelling
    /// of the synthetic indirect-return local the CSM emitter hardcodes in the generated C#
    /// method body. Without the reserved-name guard the emitted `IntPtr resultPtr = …` local
    /// shadows this parameter (CS0136) and the generator ships uncompilable C# at exit 0.
    /// The class-carrier return makes the body allocate that synthetic, so this method both
    /// compiles AND round-trips only when the guard is active.
    public func relayThrough<T: CarrierItem>(resultPtr: Int32, item: T) -> T {
        _ = resultPtr
        return item
    }
}
