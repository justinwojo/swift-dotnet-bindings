// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// MARK: - OS symbol inventory probes (load-safety P2)

// The BindingTests harness generates this module's binding against an OS symbol inventory that
// lists every symbol the module exports except the ones of the members named `…AbsentOnInventoryOs`
// and of `InventoryAbsentValue`, at a floor of 99.0. The generator must floor exactly those, the
// binding must throw PlatformNotSupportedException before it calls one, the dependency's wrapper
// must import them weakly, and every sibling must keep working. Nothing here declares @available:
// the floors come from the inventory alone, as they do for an SDK member added without a floor.

public struct InventoryProbe {
    public var seed: Int

    public init(seed: Int) {
        self.seed = seed
    }

    public var presentOnInventoryOs: Int { seed + 1 }

    public var absentOnInventoryOs: Int { seed + 2 }

    public func presentMethod() -> Int { seed * 2 }

    public func absentMethodOnInventoryOs() -> Int { seed * 3 }
}

public struct InventoryAbsentValue {
    public var seed: Int

    public init(seed: Int) {
        self.seed = seed
    }

    public func doubled() -> Int { seed * 2 }
}
