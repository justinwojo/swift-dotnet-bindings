# SwiftBindings 0.18.1

0.18.1 is a small patch over **[SwiftBindings SDK 0.18.0](https://github.com/justinwojo/swift-dotnet-bindings/releases/tag/sdk-v0.18.0)** — **Start with the 0.18.0 notes for everything new in this train.**

## Highlights

- **A framework importing a forward-declared ObjC protocol builds again** — a forward declaration (`@protocol Foo;`) was read as a real definition, so the importing framework re-emitted types another module already owns. The duplicate names failed the build on device and under NativeAOT, and silently disabled the entire assembly on the simulator. There was no consumer-side workaround.
- **ObjC C-array parameters bind as arrays, not a single `out` value** — an API handing back a run of coordinates or structs returned zeroes. A pointer with an adjacent `count:` keyword now projects as an array overload.
- **Wide `Optional` values no longer read uninitialized memory** — on some call paths only one machine word was reserved for the value, so `String?`, `Int?`, `Double?`, existentials and bridged types like `URL?` and `Date?` were partly never transferred — including the part that decides whether the value reads as `nil`.
- **Members that can't be called safely throw instead of crashing** — they keep their declaration so conformances still compile, and throw `NotSupportedException` with the reason, marked `SB0009`.

## ObjC binding fixes

- **Category instance properties are no longer dropped** — they vanished from the binding *and* from the binding report. They now emit as `Get{Name}`/`Set{Name}` methods, recovering whole families of category-declared accessors.
- **Apple SDK types in ObjC signatures resolve** — members typed by `NSRange` or an `NS_ENUM` used to drop out of the binding. 68 system enums are now recognized, and a pointer to one is passed by address rather than copied by value.
- **Inherited properties are no longer narrowed** — a conformed protocol restating an ancestor's read-write property as read-only hid the inherited setter (`CS0200` on assignment). The subclass now re-declares the full accessor set.
- **Pointers with no safe projection are withdrawn with a recorded reason** instead of shipping as a callable that misbehaves.

## Marshalling and packaging

- **Wide optionals travel in a correctly sized carrier** where the layout can be proven, and are withdrawn where it can't. Regenerating will withdraw a few members 0.18.0 emitted — those calls could not have worked.
- **An ObjC class reference passed to a Swift setter is retained** at the call site; it was previously under-retained.
- **Pure-ObjC binding packages no longer ship the framework twice** — the nupkg carried a second, byte-for-byte copy of the vendor xcframework under `runtimes/` that nothing on that lane ever read, roughly doubling the package for no benefit. Only the sidecar copy the consumer's build actually extracts and embeds is packed now.
- **Pure-ObjC packs are guarded** — that lane had no pack guard at all, so a pack that lost its native payload shipped green and failed later at the consumer's link or `dyld`. `SWIFTBIND074` now matches the packed sidecar against the source and fails at pack time if a slice went missing.
- **`SWIFTBIND080` no longer warns about dependencies that are satisfied** — binding projects not named after their module now resolve by directory, and when the package identity genuinely isn't knowable the warning says so instead of printing an id that can't be followed.
- **Embedded frameworks are checked for their privacy manifest** — a dropped `PrivacyInfo.xcprivacy` shows up as `ITMS-91053` at App Store submission rather than at build time, so the hygiene gate now catches it first.

## Packages

| Package                  | Version |
|--------------------------|---------|
| SwiftBindings.Runtime    | 0.18.1  |
| SwiftBindings.Sdk        | 0.18.1  |
| SwiftBindings.Templates  | 0.18.1  |

`SwiftBindings.Apple` is unchanged at `26.2.8` — it declares a floor-only Runtime range, so the published supplement rides forward without a republish.

See the [GitHub wiki](https://github.com/justinwojo/swift-dotnet-bindings/wiki) for installation and usage.
