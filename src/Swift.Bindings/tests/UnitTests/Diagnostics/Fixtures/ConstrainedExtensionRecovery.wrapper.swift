// Compiler-legality recovery fixture: the generated conformance is intentionally unconditional,
// while the witness exists only under a marker-constrained extension. The origin anchor makes the
// symbol-less conformance block attributable to the member that caused it.
private protocol ConstrainedExtensionRecoveryRequirement {
    func inspect() -> Int32
}

private struct ConstrainedExtensionRecoveryBox<Value> {
    let value: Value
}

extension ConstrainedExtensionRecoveryBox where Value: BitwiseCopyable {
    func inspect() -> Int32 { 42 }
}

// SBW-ORIGIN: Fixture||Method|SBW_ConstrainedExtensionRecovery_broken||None|||/swift-wrapper
extension ConstrainedExtensionRecoveryBox: ConstrainedExtensionRecoveryRequirement {
}

@_cdecl("SBW_ConstrainedExtensionRecovery_healthy")
public func SBW_ConstrainedExtensionRecovery_healthy(_ value: Int32) -> Int32 {
    value + 1
}
