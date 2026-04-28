// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Bug 16 fixture: protocol with a required `@_spi` Var

/// Reproduces the RealityFoundation `MaterialFunction` pattern: a public protocol
/// whose requirement set includes a `@_spi`-protected `var` (e.g. `__linkSPI`).
/// The Var requirement parses cleanly into the model, but PropertyHandler skips
/// SPI properties by returning `SkipReason.ModuleInternal`. Without the Bug 16
/// gate the generator still emitted `extension EveryProtocol: MaterialFunction { ... }`
/// missing the SPI witness, and Swift's type-checker rejected the conformance —
/// the wrapper failed to compile.
///
/// The generalized "required-but-suppressed" gate skips the EveryProtocol
/// conformance for any protocol whose required member is dropped by parser-time
/// validation (today: `@_spi`, in principle: any future suppression reason).
/// This fixture's protocol, conformer, and consumer should round-trip cleanly
/// because the proxy class is suppressed via the existing
/// `EveryProtocolConformanceSkipped` propagation path while the C# interface
/// itself is still emitted.
public protocol Bug16SpiRequirementProtocol {
    var publicLabel: String { get }

    @_spi(Internal) var __linkSPI: Int32 { get }
}

@_spi(Internal)
extension Bug16SpiRequirementProtocol {
    public var __linkSPI: Int32 { 0 }
}

/// Concrete conformer used to feed the consumer below. Provides both the public
/// requirement and the SPI-protected requirement; only the public side is
/// reachable from C#.
public final class Bug16SpiRequirementConformer: Bug16SpiRequirementProtocol {
    public var publicLabel: String { "spi-required" }

    @_spi(Internal) public var __linkSPI: Int32 { 42 }

    public init() {}
}

/// Consumer class: holds a conformer and exposes the public requirement via an
/// instance method. The presence of this class proves the wrapper compiled past
/// the protocol's EveryProtocol conformance site (it would have failed before the
/// fix). Instance methods give us a stable surface to call from C# without
/// needing the proxy.
public final class Bug16SpiRequirementConsumer {
    private let conformer: Bug16SpiRequirementConformer

    public init() {
        self.conformer = Bug16SpiRequirementConformer()
    }

    public func label() -> String {
        conformer.publicLabel
    }
}
