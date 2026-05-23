// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// MARK: - PAT/self-requirement-protocol generic-host constructor coverage
//
// Regression coverage for the AppIntents 0.12.0 site #4 shape that's NOT covered
// by `BoundGenericOfParentCtor.swift`:
// `IntentParameterSummary<Intent>.init(_: ParameterSummaryString<Intent>, …)`.
//
// The parent generic constraint here is a PAT protocol (`SelfReqProto` has an
// `associatedtype`, so it is rejected by the static-PWT path's
// `HasUnresolvableTypeConformances` gate). Pre-doc-14-extension the wrapper-helper
// path for GSF / parent-generic constructors only threaded PWTs for resolvable
// (no-associated-type, no-self-requirement) protocols, so a constructor whose
// parent is PAT-constrained fell through to direct `CallConvSwift` (SB0001).
//
// With the dynamic-PWT extension landed in MetatypeHelperEmitter (counts all
// conformances that have a protocol descriptor symbol, including PAT/self-req),
// the generated C# field-init/`PInvoke_init_*` site supplies the PAT witness
// table via `SwiftConformance.GetWitnessTableOrThrow`, the @_cdecl wrapper
// signature declares matching `_pwt0..N` params, and the `_sbw_meta_*` helper
// forwards them into the dlsym'd `Ma` accessor.

public protocol SelfReqProto {
    associatedtype Output
    static var label: String { get }
}

public struct ConcreteSelfReqA: SelfReqProto {
    public typealias Output = String
    public static var label: String { "A" }
    public init() {}
}

public struct ConcreteSelfReqB: SelfReqProto {
    public typealias Output = Int
    public static var label: String { "B" }
    public init() {}
}

public struct SelfReqBox<TInner: SelfReqProto> {
    public let stored: TInner
    public init(stored: TInner) { self.stored = stored }
}

public struct SelfReqHost<TInner: SelfReqProto> {
    public let box: SelfReqBox<TInner>
    public init(boxed box: SelfReqBox<TInner>) {
        self.box = box
    }
    public var labelDescription: String { "host:\(TInner.label)" }
}
