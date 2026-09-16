// Compiler-legality recovery fixture: a generated concrete specialization can substitute an
// internal conformer after ordinary signature validation. The compiler, not a spelling predictor,
// must identify the owning wrapper while leaving its healthy sibling untouched.
import InternalConformerRecoveryDependency

@_cdecl("SBW_InternalConformerRecovery_broken")
public func SBW_InternalConformerRecovery_broken(
    _ value: UnsafeRawPointer
) -> Int32 {
    value.load(as: InternalConformerRecoveryDependency.Hidden.self).value
}

@_cdecl("SBW_InternalConformerRecovery_healthy")
public func SBW_InternalConformerRecovery_healthy(_ value: Int32) -> Int32 {
    value + 1
}
