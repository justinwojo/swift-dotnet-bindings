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

/// Pure-Swift class handed back by the generated-local collision repro below. A class
/// return is read back through a local the emitter declares itself.
public class ForeignExtensionReceipt {
    public let code: Int32

    public init(code: Int32) {
        self.code = code
    }
}

/// Resilient (non-frozen) struct handed back by the same repro. A resilient return is
/// materialized through the type's value witness, which needs three locals of the
/// emitter's own — the metadata, the buffer it sizes, and the indirect-result handle.
public struct ForeignExtensionSummary {
    public var label: String
    public var score: Int32

    public init(label: String, score: Int32) {
        self.label = label
        self.score = score
    }
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

    /// Generated-local collision on the foreign-extension return marshalling. A class
    /// return is read back through a local declared straight into the body that already
    /// holds this method's parameters — and this parameter is spelled exactly that way.
    /// The returned value folds the parameter in, so a body that read the wrong identifier
    /// would change the answer rather than merely fail to compile.
    public func receipt(result: Int32) -> ForeignExtensionReceipt {
        return ForeignExtensionReceipt(code: result * 3)
    }

    /// Same shape on the resilient-struct return arm, which declares the metadata, the
    /// buffer and the indirect-result handle as locals of its own. Each of these parameters
    /// is spelled exactly like one of them, and all three are folded into the returned
    /// value with distinct weights.
    public func summarize(metadata: Int32, buffer: Int32, indirectResult: Int32) -> ForeignExtensionSummary {
        return ForeignExtensionSummary(label: "summary", score: metadata * 100 + buffer * 10 + indirectResult)
    }

    /// The same class return on the PROPERTY arm, which is a second emission site in this
    /// emitter. A computed property builds its result inside the accessor, so the value the
    /// caller receives is one nothing else is holding on to.
    public var receiptStamp: ForeignExtensionReceipt {
        return ForeignExtensionReceipt(code: 7)
    }

    /// A resilient return whose parameter is spelled like the indirect-result register's own
    /// name on the NATIVE declaration rather than in the body. The declaration carries the
    /// register handle and the caller's arguments in one parameter list, so a member shaped
    /// this way puts two identically named parameters in it unless the generated one moves.
    public func stamped(result: Int32) -> ForeignExtensionSummary {
        return ForeignExtensionSummary(label: "stamped", score: result * 2)
    }

    /// `self` is a legal Swift argument label, so a member can project a parameter spelled
    /// exactly like the receiver the extension method declares — here on the foreign (ObjC
    /// root) receiver, the third of the three receiver kinds that share this behaviour. The
    /// returned score folds the parameter in, so reading the wrong identifier changes the
    /// answer rather than merely failing to compile.
    public func scoredWithSelfLabel(self: Int32) -> Int32 {
        return self * 5 + 1
    }
}
