// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Projected-key builder: protocol-path parentTypeName omission (AF05 ruling a)
//
// The projected-C# overload-dedup key is built from each method's emitted public name. On the
// CLASS path that name folds in the CS0542 "parent-name collision" rename: a method whose
// PascalCase name equals its enclosing TYPE name is renamed `Foo` → `GetFoo` (C# forbids a member
// named identically to its enclosing type). On the PROTOCOL path the enclosing emitted type is an
// INTERFACE named `I{Name}` (here `IKeyRegion`), so a member named `KeyRegion` never actually
// collides with its container — applying the raw-parent-name rename would SPURIOUSLY mangle a
// perfectly legal interface member. Ruling (a): the shared key builder keeps the protocol path
// opted OUT of parentTypeName, and emission agrees.
//
// This fixture pins that. `keyRegion(_:)` PascalCase-equals the protocol name `KeyRegion`, so the
// rename WOULD fire if the protocol path supplied parentTypeName. It does not, so the emitted
// interface member is `int KeyRegion(int)` — NOT `GetKeyRegion`. The C# impl below implements
// `int KeyRegion(int)`; if a future edit re-applied the rename, the interface would declare
// `GetKeyRegion` instead and the impl would fail to satisfy `IKeyRegion` (CS0535) — a compile-time
// red. The runtime round-trip proves the member dispatches. `: AnyObject` so the proxy is
// class-backed (reverse dispatch into the C# conformer).
public protocol KeyRegion: AnyObject {
    func keyRegion(_ x: Int32) -> Int32
}

// Reverse-dispatch driver: routes `keyRegion(_:)` through the proxy's vtable slot back into the C#
// impl. Compiles and dispatches only because the interface member is named `KeyRegion` (matching
// the impl), proving the protocol path did not apply the parent-name rename.
public func callKeyRegion(_ r: any KeyRegion, _ x: Int32) -> Int32 {
    return r.keyRegion(x)
}
