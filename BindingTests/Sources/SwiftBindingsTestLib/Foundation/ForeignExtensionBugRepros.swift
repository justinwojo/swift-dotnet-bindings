// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Foreign Type Extension Bug Repros (ForeignTypeExtensionEmitter)
//
// `NSObject` is a foreign (module-external) ObjC root class — not declared in this
// module — so members added via `extension NSObject { ... }` route through
// ForeignTypeExtensionEmitter's self_-reconstruction path
// (`Unmanaged<NSObject>.fromOpaque(self_).takeUnretainedValue()`), a DIFFERENT emission
// path than the same-module MethodHandler/PropertyHandler these other domain files
// exercise. Three corpus-sweep repros land here because they share that one
// emitter/gate.

/// A frozen, non-generic `Int32` raw-value enum — a "SimpleEnum" per
/// `TypeRecordFlags.SimpleEnum` classification — used as a foreign-extension method
/// parameter below. The raw type must be non-String: `TryGetSimpleEnumLowering` only
/// lowers a SimpleEnum across this silgen boundary via its raw scalar for non-String
/// raw values.
public enum ForeignExtensionClassification: Int32 {
    case unclassified = 0
    case flagged = 1
    case verified = 2
}

extension NSObject {
    /// Bug (a) sub-case a-2: a SimpleEnum parameter on a foreign-type extension method
    /// (CoreStore-adjacent shape). Pre-fix, `ForeignTypeExtensionEmitter` treated any
    /// non-primitive parameter type as a class pointer and emitted
    /// `Unmanaged<AnyObject>.fromOpaque(...).takeUnretainedValue() as! T` for the
    /// raw-value enum — illegal Swift (`Unmanaged` requires a class type). The fix lowers
    /// a SimpleEnum parameter across the boundary as its raw `Int32` scalar and
    /// reconstructs it via `T(rawValue:)` inside the wrapper.
    public func classify(status: ForeignExtensionClassification) -> ForeignExtensionClassification {
        return status == .unclassified ? .flagged : .verified
    }

    /// Bug (c): a parameter literally named `extension` — a contextual Swift keyword —
    /// with no separate external label (rive-ios's exact repro, 3 sites). Pre-fix, the
    /// emitter's own hand-rolled keyword table didn't cover `extension`, so the internal
    /// Swift wrapper binding was emitted unescaped (`_ extension: Int32`), which swiftc
    /// rejects. The fix routes through the shared `CdeclParamMapper.BuildSwiftBindingName`
    /// core, which renames ANY Swift keyword (not just a curated subset) to `{name}Param`.
    public func tagged(extension: Int32) -> Int32 {
        return `extension` + 1
    }

    /// Bug (h): a variadic parameter on a foreign-type extension method (Stevia's
    /// `UIView...` shape, substituting `Int32` since this file has no UIKit import).
    /// Pre-fix, the raw-text parameter parser had no notion of a variadic marker,
    /// silently folded the trailing `...` into the type name, and downstream emission
    /// force-cast the corrupted type (`as! Int32` on what was actually an array),
    /// crashing wrapper compilation. The fix detects the trailing `...` before it reaches
    /// the type parser and declines the member outright: a clean skip (member simply
    /// absent from the generated binding) beats a corrupted `as!` cast.
    public func total(_ values: Int32...) -> Int32 {
        return values.reduce(0, +)
    }
}
