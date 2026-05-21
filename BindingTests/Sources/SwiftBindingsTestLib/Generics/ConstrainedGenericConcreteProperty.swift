// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// Regression coverage for the property/subscript wrapper crash on generic
// classes whose generic parameter carries a protocol constraint.
//
// Pre-fix: the @_cdecl wrapper for a concrete-typed property/subscript on
// `Container<T: Constraint>` emitted `_metadata{i}` but not `_pwt{i}` in the
// Metadata phase. The C# P/Invoke side (HandleProtocolConformance) emits both,
// so the PWT pointer arrived where Swift expected `self_`, and the wrapper's
// `Unmanaged.fromOpaque(self_).takeUnretainedValue() as! any _SBW_PG_<hash>`
// cast walked garbage in the Swift runtime's conformance hash table → SIGSEGV.
//
// Removing the constraint avoided the bug because then there were no PWT
// pointers to drop and the slots realigned.

public protocol CgcpMarker {}

public class CgcpPlainConformer: CgcpMarker {
    public init() {}
}

public class CgcpPlainBox<T: CgcpMarker> {
    // `content` deliberately avoids the C# `Item`/`Payload` naming collision —
    // C# indexers are internally `Item` and `Payload` is reserved by ISwiftObject.
    public let content: T
    public private(set) var label: String = "plain"
    // Public read-write storage exercises the setter half of the wrapper ABI
    // contract end-to-end. Unit tests cover param order at emit time, but only
    // a round-trip write catches a real P/Invoke-vs-@_cdecl signature mismatch
    // (the original SIGSEGV shape) on the setter path.
    public var tag: String = "tagless"
    // Distinct key type from the get-only `subscript(index: Int)` below so the
    // two C# indexer overloads don't collide (C# indexers don't differentiate
    // by argument label — only by parameter type).
    private var storage: [String: String] = [:]

    public init(content: T) {
        self.content = content
    }

    // Concrete-typed subscript on the same constrained-generic class shape —
    // exercises SubscriptWrapperEmitter's Metadata-phase PWT plumbing on the
    // getter side.
    public subscript(index: Int) -> String {
        return "plain[\(index)]"
    }

    // Settable string-keyed subscript: same constrained-generic shape, with a
    // setter routed through the wrapper. Pre-fix the setter would have hit the
    // same PWT-into-self_ slide on write.
    public subscript(key: String) -> String {
        get { return storage[key] ?? "missing[\(key)]" }
        set { storage[key] = newValue }
    }
}
