// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Requirement satisfied only by an unconstrained extension default with an UNLABELED parameter
//
// Shape observed in an image-pipeline library's processor/encoder protocols: the protocol
// declares an overload pair, and only the *shorter* overload is implemented by each conforming
// type. The longer one — whose FIRST parameter is unlabeled (`_`) — is satisfied for every
// conformer by an unconstrained protocol extension default.
//
// The extension-defaults index is keyed on the swiftinterface's printed name, where an unlabeled
// parameter renders as `_` (`apply(_:context:)`). The validator/interface-emitter side rebuilds
// that key from the parsed `MethodDecl`, whose unlabeled parameters carry the parser's synthesized
// `argN` placeholder name. Rendering `argN` as a literal label produces `apply(arg1:context:)`,
// which can never match — so the requirement looked unsatisfiable and the ENTIRE conformance was
// dropped from the conforming type. Conformers that spell the member out explicitly kept theirs,
// which is the asymmetry that makes the defect look type-specific rather than key-specific.
//
// The parameter/return types are deliberately non-frozen structs so the extension default is not
// eligible for injection as a real member: recovery here has to come from the interface's default
// member, not from a synthesized witness. `throws` is present on one defaulted requirement and
// absent on the other — both drop today, which pins the axis as the label, not the effect.

/// Non-frozen struct payload — projects as an opaque-handle C# class, and (deliberately) fails the
/// extension-method injection return gate.
public struct StageOutcome {
    public let width: Int32

    public init(width: Int32) {
        self.width = width
    }
}

/// Second non-frozen struct, used as the labeled trailing parameter.
public struct StageContext {
    public let scale: Int32

    public init(scale: Int32) {
        self.scale = scale
    }
}

/// Protocol whose two longer overloads are satisfied for most conformers by the unconstrained
/// extension defaults below.
public protocol FrameStage {
    var stageIdentifier: String { get }

    /// Every conformer implements this one explicitly.
    func apply(_ value: Int32) -> Int32

    /// Throwing requirement — unlabeled first parameter, defaulted in the extension below.
    func apply(_ outcome: StageOutcome, context: StageContext) throws -> StageOutcome

    /// Non-throwing sibling with the same unlabeled-first-parameter shape, also defaulted.
    func measure(_ outcome: StageOutcome, context: StageContext) -> Int32
}

extension FrameStage {
    public func apply(_ outcome: StageOutcome, context: StageContext) throws -> StageOutcome {
        return StageOutcome(width: apply(outcome.width) * context.scale)
    }

    public func measure(_ outcome: StageOutcome, context: StageContext) -> Int32 {
        return apply(outcome.width) + context.scale
    }
}

/// Conformer that relies on BOTH extension defaults — the type that loses its whole conformance
/// when the defaults index cannot be matched.
public struct DoublingStage: FrameStage {
    public init() {}

    public var stageIdentifier: String { return "doubling" }

    public func apply(_ value: Int32) -> Int32 { return value * 2 }
}

/// Second default-relying conformer, so the recovery is not a single-type coincidence.
public struct OffsetStage: FrameStage {
    public let offset: Int32

    public init(offset: Int32) {
        self.offset = offset
    }

    public var stageIdentifier: String { return "offset" }

    public func apply(_ value: Int32) -> Int32 { return value + offset }
}

/// Control conformer: spells both defaulted requirements out explicitly, so its conformance
/// survives with or without the index fix and its members override the interface defaults.
public struct ExplicitStage: FrameStage {
    public init() {}

    public var stageIdentifier: String { return "explicit" }

    public func apply(_ value: Int32) -> Int32 { return value + 1 }

    public func apply(_ outcome: StageOutcome, context: StageContext) throws -> StageOutcome {
        return StageOutcome(width: outcome.width + context.scale)
    }

    public func measure(_ outcome: StageOutcome, context: StageContext) -> Int32 {
        return outcome.width - context.scale
    }
}

/// Reads the conformance through the protocol existential — exercised from C# by passing a
/// recovered conformer through the generated interface.
public func describeStage(_ stage: any FrameStage) -> String {
    return stage.stageIdentifier
}

/// Applies the SHORT (explicitly implemented) overload through the existential, proving the
/// recovered conformance really dispatches rather than merely compiling.
public func applyStage(_ stage: any FrameStage, value: Int32) -> Int32 {
    return stage.apply(value)
}

public func makeDoublingStage() -> DoublingStage { return DoublingStage() }

public func makeOffsetStage(offset: Int32) -> OffsetStage { return OffsetStage(offset: offset) }

public func makeExplicitStage() -> ExplicitStage { return ExplicitStage() }
