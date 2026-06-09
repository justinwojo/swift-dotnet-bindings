// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Protocol with a required `@_spi` Var

/// Public protocol whose requirement set includes a `@_spi`-protected `var`
/// (mirrors the original RealityFoundation `MaterialFunction` pattern that
/// motivated the fix). Under the current toolchain `swift-api-digester` and
/// the swiftinterface printer both strip `@_spi` requirements from the public
/// ABI surface, so the requirement is invisible to the parser and the
/// protocol that reaches `EveryProtocolEmitter` looks like a single-member
/// protocol (`publicLabel`). The conformance, interface, and proxy emit
/// normally and the wrapper compiles.
///
/// The `HasSuppressedRequiredMember` gate (`EveryProtocolEmitter`) remains in
/// place as defense-in-depth for any future toolchain that surfaces an
/// `@_spi` requirement before PropertyHandler skips it as
/// `SkipReason.ModuleInternal`.
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
