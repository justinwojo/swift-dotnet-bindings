// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Regression: Swift 5.9+ variadic generic parameter packs
//
// AppIntents 0.12.0 declared `public struct Specification<Output, each R>`,
// a Swift 5.9+ variadic generic. The ABI digester surfaces the pack member
// with an `each ` prefix in `genericSig`, which `NameProvider` would sugar
// into the invalid C# identifier `Teach R`:
//
//     public partial class Specification<TOutput, Teach R> : ISwiftObject…
//
// producing CS1003/CS1044/CS1525 cascades around the malformed token. C# has
// no parameter-pack equivalent, so any type whose own generic parameters
// include a variadic pack is unbindable. The fix gates such types at the
// type-handler level — ClassHandler, FrozenStructHandler, NonFrozenStructHandler,
// and EnumHandler all consult `GenericTypeEmitter.TryGetVariadicGenericParameter`
// and emit a skipped-type comment plus a ReportCollector entry instead of
// trying to render the malformed identifier.
//
// The runtime test in VariadicGenericPackTests.cs verifies that:
//   1. The library compiles (no `Teach R` / CS1003 / CS1525 cascades in
//      generated C#).
//   2. The variadic-pack type itself does NOT exist as an emitted C# type
//      (reflection asserts absence) — proving the gate fires at the type
//      level rather than producing a malformed surface.

public protocol VariadicMember {
    var label: String { get }
}

public struct VariadicCarrier: VariadicMember {
    public let label: String
    public init(label: String) {
        self.label = label
    }
}

// Two type parameters: one ordinary (`Output`) and one variadic pack (`each R`).
// `each R` mirrors the AppIntents `Specification<Output, each R>` shape that
// triggered the bug. The protocol constraint on the pack (`repeat each R:
// VariadicMember`) is preserved so the digester emits the same shape as the
// real Apple framework declaration.
//
// Variadic generic parameter packs need Swift 5.9+ runtime support; the AppIntents
// declaration is gated to iOS 18 / macOS 15, but iOS 17 / macOS 14 is the earliest
// version with the runtime metadata machinery — match that so the fixture compiles
// against the package's iOS 15 / macOS 13 deployment floor.
@available(iOS 17.0, macOS 14.0, tvOS 17.0, *)
public struct VariadicSpec<Output, each R: VariadicMember> {
    public let output: Output
    public init(output: Output) {
        self.output = output
    }
}
