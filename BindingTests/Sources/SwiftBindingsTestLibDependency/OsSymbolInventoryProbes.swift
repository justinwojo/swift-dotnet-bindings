// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// MARK: - OS symbol inventory probes (load-safety P2)

// The BindingTests harness generates this module's binding against an OS symbol inventory that
// lists every symbol the module exports except the ones of the members named `…AbsentOnInventoryOs`,
// of `InventoryAbsentValue`, of `InventoryRawProbe.init(rawValue:)` and of the `==` of
// `InventoryEqualityProbe` and `InventoryEquatableValue`, at a floor of 99.0. The generator must floor
// exactly those, wherever the binding reaches them (a raw-value factory, Equals and the operators), the
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

// The inventory also omits this enum's `init(rawValue:)`, and nothing else of it: the enum ships, its
// cases and raw values work, and only the raw-value factory must refuse.
public enum InventoryRawProbe: String {
    case low = "LOW"
    case high = "HIGH"
}

// The inventory omits the `==` Swift resolves for two values of this enum, and keeps the `==` declared
// first, against an Int: the binding's equality must refuse with the floor of the operator its
// wrapper calls, not of the first `==` it finds.
public enum InventoryEqualityProbe: Equatable {
    case tagged(Int)
    case plain

    public static func == (lhs: InventoryEqualityProbe, rhs: Int) -> Bool {
        if case .tagged(let value) = lhs { return value == rhs }
        return false
    }

    public static func == (lhs: InventoryEqualityProbe, rhs: InventoryEqualityProbe) -> Bool {
        switch (lhs, rhs) {
        case (.plain, .plain): return true
        case let (.tagged(a), .tagged(b)): return a == b
        default: return false
        }
    }
}

// The inventory omits this struct's `==`: its Equals and operators must refuse, its other members work.
public struct InventoryEquatableValue: Equatable {
    public var seed: Int

    public init(seed: Int) {
        self.seed = seed
    }

    public static func == (lhs: InventoryEquatableValue, rhs: InventoryEquatableValue) -> Bool {
        lhs.seed == rhs.seed
    }
}
