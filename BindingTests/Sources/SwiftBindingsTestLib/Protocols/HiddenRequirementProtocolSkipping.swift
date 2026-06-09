// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - `__`-prefixed protocol requirement that DOES survive into the ABI

/// Local newer-toolchain analogue of the RealityFoundation.MaterialFunction shape.
/// The protocol declares a public `__`-prefixed requirement (`__linkSPI`) with no
/// matching extension default. On Apple's framework toolchain, swift-api-digester
/// strips `__`-prefixed names from `abi.json` and the EveryProtocol emitter must
/// skip the conformance (the witness can't be synthesized). On the toolchain that
/// builds this test library, `swift-api-digester` keeps `__linkSPI` in the ABI,
/// so the gate must NOT fire and the proxy + EveryProtocol conformance must emit
/// normally — with both `publicLabel` and `__linkSPI` witnesses wired up.
///
/// Together with the `nuke validate` MaterialFunction coverage (digester-stripped
/// shape), this fixture proves the gate scopes correctly: skip only when the ABI
/// is actually missing the requirement, never just because the swiftinterface
/// declares a `__`-prefixed name.
public protocol Bug17HiddenRequirementProtocol {
    var publicLabel: String { get }

    var __linkSPI: Bool { get }
}

/// Concrete conformer providing both requirements.
public final class Bug17HiddenRequirementConformer: Bug17HiddenRequirementProtocol {
    public var publicLabel: String { "hidden-required" }

    public var __linkSPI: Bool { true }

    public init() {}
}

/// Consumer class: holds a conformer and exposes the public requirement via an
/// instance method, demonstrating round-trip through the generated wrapper.
public final class Bug17HiddenRequirementConsumer {
    private let conformer: Bug17HiddenRequirementConformer

    public init() {
        self.conformer = Bug17HiddenRequirementConformer()
    }

    public func label() -> String {
        conformer.publicLabel
    }
}
