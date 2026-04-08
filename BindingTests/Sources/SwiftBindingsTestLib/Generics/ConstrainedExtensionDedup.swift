// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Bug #2 regression: Constrained-extension property name conflict
//
// Multiple `extension X where Marker == Concrete` blocks on the same generic
// type each declare properties with the same Swift name. Each Swift
// specialization is monomorphized into its own per-witness mangled symbol
// (here `markerLabel` returns "alpha" or "beta"), but they all map to a
// single C# property on the merged generic class — C# does not permit
// overloading by specialization. Pre-fix the C# emit produced duplicate
// property and `_Get` accessor methods (CS0102/CS0111).
//
// The fix detects this conflict in `MemberEmissionValidator.CanEmitProperty`
// (and the symmetric check in `MemberValidationPipeline.ValidatePropertyEmission`)
// and skips ALL conflicting copies. We deliberately do NOT pick a "winner":
// keeping one specialization would cause `<DedupMarkerBeta>.MarkerLabel` to
// silently dispatch to the alpha specialization's symbol, returning wrong
// data. C# generics cannot discriminate among closed instantiations at the
// dispatch site, so dropping the property is the only safe behavior.
//
// The runtime test in BasicGenericTests.cs verifies that:
//   1. The merged C# class compiles (no CS0102/CS0111).
//   2. The unconstrained `value` property still round-trips on both
//      specializations.
//   3. `markerLabel` is NOT emitted on either specialization (reflection
//      asserts the property is absent — i.e., the conflict is genuinely
//      skipped, not silently stubbed).
//
// See: src/docs/0.8.0-storekit2-followup-bugs.md Bug #2

public struct DedupMarkerAlpha {
    public init() {}
}

public struct DedupMarkerBeta {
    public init() {}
}

public struct ConstrainedExtensionWitness<Marker> {
    public let value: Int32

    public init(value: Int32) {
        self.value = value
    }
}

extension ConstrainedExtensionWitness where Marker == DedupMarkerAlpha {
    public var markerLabel: String {
        return "alpha"
    }
}

extension ConstrainedExtensionWitness where Marker == DedupMarkerBeta {
    public var markerLabel: String {
        return "beta"
    }
}
