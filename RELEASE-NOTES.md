0.17.0 makes binding third-party Objective-C and mixed ObjC+Swift frameworks work end to end — the generator now emits, links, and runs bindings for real-world ObjC-heavy SDKs instead of aborting at generation or crashing on class/selector registration at launch. It also widens the set of generic and existential Swift APIs that bind, and changes how a nested type whose name collides with a sibling property is renamed — a breaking change to some generated names, gated to this minor bump.

## Highlights

- **Objective-C and mixed ObjC+Swift frameworks bind** — Third-party pure-Objective-C xcframeworks and mixed ObjC+Swift frameworks — the shapes behind Google AdMob, MapLibre, and the Facebook SDK — now generate, link, and run end to end, where before they aborted generation, failed to compile, or crashed at launch.
- **Kind-aware names for colliding nested types (breaking)** — When a nested type's name collides with a sibling property, the generator now renames the type with a semantic suffix — `Kind` for an enum, `Info` for a struct or class — instead of the old stuttering `…Type`/`…TypeType` scheme. Consumers that referenced the old names must update; see the worked examples under *Generator improvements*.
- **Objective-C typedefs reach C#** — Objective-C typedef shapes now bridge as typed C# surfaces instead of degrading, dropping, or emitting ordinal-only values.
- **More generics and existentials bind** — Common generic and existential Swift APIs that third-party SDKs expose now survive generation instead of disappearing from the C# surface.
- **Crash fixes in reverse-dispatch marshalling** — Several `SIGSEGV`, over-release, and double-free bugs on the boundary where C# implements an `@objc` Swift protocol are fixed.

## Objective-C and mixed frameworks

- **Third-party Objective-C xcframeworks bind end to end** — Duplicate ObjC selectors that aborted the class registrar at launch are flattened, protocol-typed block return types that failed to compile (`CS1503`, the AdMob shape) are widened to `NSObject`, and a batch of emitter codegen defects — own-protocol member miscasts, phantom P/Invokes for absent symbols, missing cross-framework imports, dropped conformance witnesses, and ARC-qualifier token mangling — are cleared, so an ObjC-heavy binding (MapLibre-shaped) regenerates clean and runs on the simulator.
- **Mixed ObjC+Swift frameworks resolve across the boundary** — Swift members that reference ObjC-defined types no longer silently degrade to `object` or get dropped; type resolution now crosses the ObjC/Swift boundary, an enum-only ObjC companion is classified correctly so the SDK actually builds and references it, and a `RuntimeIdentifier` leak that dropped the ObjC companion project reference in `ProjectReference` consumers is fixed — validated on iOS Simulator and physical device.
- **Objective-C typedef bridges** — `NS_OPTIONS` bitmasks are bridged into the Swift type database as option-set values, `NS_TYPED_EXTENSIBLE_ENUM` typedefs bind as `ObjCBridgeable` newtypes, and an `@objc` `RawRepresentable` integral enum emits its real declared raw value (parsed from the interface) rather than a declaration-order ordinal, so `(long)MyEnum.Case` matches the bridged constant and round-trips.
- **Existential and value marshalling across reverse dispatch** — An `Optional` ObjC-bridgeable value parameter is now read as a one-word pointer instead of misreading a multi-word layout (`SIGSEGV`), a scalar ObjC-bridgeable return transfers its `+1` correctly, `@objc` class-bound existentials route through reverse-dispatch receiver elements, a plain C# conformer (not just an `ISwiftObject`) can be passed through an `Optional` existential parameter, and a nested `@objc` existential requirement that over-read its carrier is dropped fail-closed rather than corrupting memory.
- **Honest skip reporting and fail-closed guards** — Objective-C skip diagnostics now feed the binding report and are triaged into actionable tiers instead of being dropped silently, and a misconfigured ObjC `ApiDefinition` used without binding-project mode now fails with `SWIFTBIND005` rather than emitting broken output.

## Generator improvements

- **More generic APIs bind concretely** — A concrete subclass that closes all of a bound-generic base's type parameters now surfaces the base's methods; frozen trivially-copyable struct returns and parameters get concrete overloads (not just `Foundation.Data`); void-returning `async` methods on generic value-type parents (`Activity<T>.update`/`end`, `Tips.Event<T>.donate`) emit; and closures carrying method-generic type parameters in argument position — not just return position — now bridge.
- **Existentials project in more positions** — Marker-only existentials such as `any Sendable` and `Result<T, any Error>` now project into C# in return, property, and collection position instead of being dropped, and a protocol too ABI-constrained for two-way dispatch gets a read-only forward proxy instead of throwing `NotSupportedException` at runtime (`Result` stays read-only by design so no meaningless payload is marshalled back).
- **Protocol defaults and method-name collisions** — Read-only protocol-extension default properties now surface as synthetic getters on concrete conformers instead of vanishing; protocol methods that differ only by argument label (delegate callbacks like `captureSession(_:didAdd:)` versus `didChange:`) get distinct selector-style C# names instead of collapsing into one member; and method-name shaping is now mutating-aware, so a protocol requirement and its concrete conformer always derive the same C# name and an unsatisfiable mutating requirement drops the conformance rather than emitting code that won't compile.
- **Kind-aware nested-type disambiguation (breaking)** — A nested type whose PascalCased name collides with a sibling property is renamed with a semantic suffix chosen from its kind — `Kind` for an enum, `Info` for a struct or class — with a numeric suffix kept only as a last-resort re-collision guard, replacing the old scheme that stacked a literal `Type` suffix and could stutter into `…TypeType`. A sibling method that would collide with the renamed type takes a `Method` suffix. This renames some previously-generated members, so it is a breaking change gated to this minor bump: StoreKit's `Transaction.OwnershipType` enum, once emitted as `OwnershipTypeType`, is now `OwnershipTypeKind`, and the old `Transaction.OfferTypeType` / `OfferTypeTypeType` stutter is split into `Transaction.OfferInfo` for the `Offer` struct and `Transaction.OfferTypeKind` for the `OfferType` enum. Consumers referencing the old names must update to the new ones.
- **Foundation projection** — `LocalizedStringResource` now projects to `string` instead of being dropped under a mis-attributed SwiftUI/Combine skip reason, and a Foundation module-attribution misclassification that hid the type is corrected.

## Bug fixes and robustness

- **Per-platform minimum-OS floor** — The emitted minimum-OS version is now derived per platform rather than hardcoded to iOS 15, so macOS 12–14 consumers are no longer blocked by an over-high floor.
- **EveryProtocol carrier partitioning** — Two identically-signed protocols routed to different umbrella carriers no longer collapse into one emission plan, which previously left one carrier's extension empty and failed to compile with a conformance error.
- **Build robustness** — A wrapper-compile timeout under contended CI can no longer leave a half-built `.xcframework` that packaging then fails on with an opaque error.

## Packages

| Package | Version |
|---------|---------|
| SwiftBindings.Runtime | 0.17.0 |
| SwiftBindings.Sdk | 0.17.0 |
| SwiftBindings.Templates | 0.17.0 |

`SwiftBindings.Apple` is unchanged and stays at `26.2.8` — it declares its Runtime dependency as a floor-only range, so the published supplement rides forward to Runtime 0.17.0 without a republish.

See the [GitHub wiki](https://github.com/justinwojo/swift-dotnet-bindings/wiki) for installation and usage.
