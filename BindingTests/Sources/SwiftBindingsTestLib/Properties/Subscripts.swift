// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - String-Keyed Subscript

/// Class with string-keyed subscript returning optional string.
public class KeyValueStore {
    private var storage: [String: String] = [:]

    public init() {}

    public subscript(key: String) -> String? {
        get { storage[key] }
        set { storage[key] = newValue }
    }

    public func count() -> Int32 { Int32(storage.count) }
    public func removeAll() { storage.removeAll() }
    public func allKeys() -> [String] { Array(storage.keys.sorted()) }
    public func allValues() -> [String] { Array(storage.keys.sorted().compactMap { storage[$0] }) }
}

// MARK: - Int-Keyed Subscript (Blittable Comparison)

/// Class with int-keyed subscript returning int.
public class IndexedStore {
    private var items: [Int32]

    public init(capacity: Int32) {
        items = Array(repeating: 0, count: Int(capacity))
    }

    public subscript(index: Int32) -> Int32 {
        get { items[Int(index)] }
        set { items[Int(index)] = newValue }
    }

    public func count() -> Int32 { Int32(items.count) }
}

// MARK: - Optional-Existential Subscript on a Value Type
//
// A *value-type* struct whose subscript element is an optional protocol existential
// `(any P)?` routes its accessors through OptionalPointerWrapperEmitter: the optional
// existential return is "large", and the struct setter is emitted as a through-pointer
// assignment `_self.assumingMemoryBound(to: T.self).pointee[key] = newValue`. The setter's
// value binding is synthesized by Swift as `newValue` — which is ALSO a reserved synthetic
// wrapper-parameter name. The emitter formerly reserved-escaped that value parameter's
// DECLARATION to `__newValue` while the assignment body still referenced bare `newValue`, so
// swiftc rejected the wrapper ("cannot find 'newValue' in scope") and the build SILENTLY
// stripped it — leaving a missing entry point that crashes when the setter is called. This
// path is distinct from `KeyValueStore` above (a *class* with a `String?` subscript, which
// goes through the subscript wrapper rather than the optional-pointer wrapper), which is why
// that fixture never caught the regression.

// `describe()` deliberately collides with the shared method signature that the
// existential-proxy emitter materializes on its `EveryProtocol` catch-all (many other
// test-lib protocols declare `func describe() -> String`). `StoredItem` is therefore a
// "loser" in that same-signature group: the emitter emits an EMPTY
// `extension EveryProtocol: StoredItem {}` and routes its `describe()` witness into the
// owner's body via Swift cross-extension resolution. This name also drives the
// stripper/fan-out end-to-end regression: the build-harness source stripper formerly stripped that
// empty extension while leaving `Get_EveryProtocol_StoredItem_WitnessTable` orphaned, which
// failed to compile and triggered a coarse retry-strip cascade that silently dropped
// unrelated symbols (e.g. `ItemBag_init`) → EntryPointNotFoundException at runtime. With the
// stripper fix (orphaned-getter strip in lock-step) and the method fan-out (Bug #2), the
// natural `describe()` name is safe here, so the fixture also exercises the real collision
// rather than dodging it with a unique name.
public protocol StoredItem {
    func describe() -> String
}

public struct BaggedItem: StoredItem {
    public let name: String
    public init(name: String) { self.name = name }
    public func describe() -> String { return "BaggedItem(\(name))" }
}

/// Value-type (struct) container with an optional-existential subscript. The dictionary
/// field makes it non-frozen, so it projects as a C# class over an opaque payload; the
/// subscript setter mutates through the payload pointer, so a set followed by a get
/// round-trips through the same backing memory.
public struct ItemBag {
    private var storage: [String: any StoredItem] = [:]
    public init() {}

    public subscript(key: String) -> (any StoredItem)? {
        get { storage[key] }
        set { storage[key] = newValue }
    }

    /// Known-good readback path (plain String return) so the subscript SETTER can be
    /// verified independently of the optional-existential subscript getter.
    public func describeItem(_ key: String) -> String {
        return storage[key]?.describe() ?? "none"
    }

    public func count() -> Int32 { Int32(storage.count) }
}
