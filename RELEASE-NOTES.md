# SwiftBindings 0.19.0

This release gets more of your library into C#: conformances, callback-taking initializers, option sets and Objective-C constants that used to disappear now bind, and whatever still can't tells you why. Objective-C names change to match how Swift imports them, so expect compile errors on upgrade.

## Highlights

- **Objective-C bindings are renamed to match how Swift imports them — this breaks source** — the C# names change for types, enum cases, delegate methods and constants. Every rename surfaces as a compile error rather than a silent behavior change, and each follows a mechanical rule you can apply by hand.
- **SwiftUI values are passed the way Swift declares them** — `Color`, `Font`, `Text`, `Image`, `AnyView`, `EdgeInsets`, `Animation` and `Binding` are all `@frozen`, but we described them as resilient and handed Swift a pointer where it expected the value. Any library taking a `Color` or `Font` parameter crashed; the rest read the wrong memory.
- **A requirement met only by a protocol extension default is recognized again** — where that default's first argument is unlabeled, the lookup missed it and every conformer lost its whole `: IFoo` interface. Fixing it takes dropped conformances across our test corpus from 496 down to 400.
- **Overloads are named from their argument labels and types** — `Configure2` told you nothing, and the number tracked declaration order, so adding an upstream sibling quietly renamed everything after it. Overloads that nothing can tell apart are now refused and reported instead of shipped.
- **What can't bind correctly is caught before you ship it** — an ambiguous overload or a closure over a method's own generic is now declined and reported, so one bad shape no longer takes the whole wrapper library down with it. A P/Invoke naming a native the package doesn't ship fails the pack outright, instead of installing clean and throwing on first use.

## Objective-C bindings

These renames **break source, with no compatibility shims**, but each follows a mechanical rule, so the new name is derivable from the old: a type takes the name Swift imports it under, an enum case drops its registered module tag, a delegate method drops its receiver segment, and a module whose constants all share a prefix drops it. Expect `CS0115` on delegate overrides and `CS0117` on enum cases.

- **Extern constants return their real value** — they were get-only properties returning `null` or zero, forever, with nothing to warn you. `bgen` only wires up the reader when the constant comes from an `ApiDefinition` input, so they now emit there instead of in the core source. Free C functions move out to their own `{Module}Functions` type in the same change.
- **Category class methods no longer ask for a receiver** — `bgen` gives every member of a `[Category]` a receiver, `[Static]` ones included, so a class factory demanded an instance of the thing it exists to create. Those members now get a receiver-free overload.
- **A Swift default naming a stripped enum case resolves** — the case prefix was stripped at the declaration but not where it was referenced, so the default pointed at a member that was never declared.
- **Projected property accessors keep the original's memory semantic** — a `retain` or `copy` property was described as something weaker, so the generated contract promised less than the property it stands in for.

## Bindings that used to go missing

- **A C# name is claimed only once we know the member can be emitted** — Swift lets a type declare a static and an instance property under one name. The emitters claimed the name first and asked questions later, so a static sibling that couldn't be emitted anyway took the name and evicted a perfectly good instance sibling, leaving interface requirements with no witness.
- **Names differing only by case are settled once, up front** — `url` next to `URL` collapsed to one C# name and the second was dropped. Conformers now take the name their protocol picked; deciding separately still compiled, but read the wrong storage.
- **An initializer taking a callback binds as a real constructor** — closure bridges refused Swift constructors outright, so a type whose only initializer took a closure became a shell you couldn't build, stranding everything downstream of it.
- **`OptionSet` types get their operators** — `|`, `&`, `^`, `~` and membership come from stdlib extensions with no ABI symbols, so nothing could bind them and you wrote `new Style(a.RawValue | b.RawValue)` by hand. UIKit's launch and open-URL option keys also resolve now, so `[AnyHashable: Any]` app-delegate dictionaries project instead of skipping.
- **A same-type constraint on a constructed generic survives the parser** — `where T.ValueType == Measure<Duration>` disappeared silently, and the constructor was emitted against a type that can't satisfy it.
- **A Swift label that's also a C# keyword no longer breaks the build** — the closure bridge wrote the sanitized C# name into its Swift call site, and `swiftc` rejecting it took down the whole wrapper library, not just that member.

## SwiftUI

- **`Bool` defaults on the generated async View bridge follow the library's own default** — every `Bool` parameter used to default to `true` regardless of what the Swift type declared, so a parameter whose real default is `false` came out inverted. Defaults are now declared per parameter, and one shipped bridge parameter changes as a result. **Regenerating changes that parameter's behavior with no compile error**, so pass any `Bool` you were relying on explicitly.
- **`Color` and `Font` can be built from managed code** — their initializers are Swift-only with no C-callable symbol, so you could name either type but never make one. `Color.Create` and `Font.System` are now backed by cdecl shims.
- **An async View's callback can return the value it produced** — it could only report an integer status, so a View that computed something had no way to hand it back. The typed callback carries the result alongside the code, and sits next to the scalar one rather than replacing it.
- **A View nested inside another type gets its full name** — the bridge compiles as its own Swift module, so a bare leaf name got "cannot find 'X' in scope". Two Views sharing a leaf name also emitted the same session class twice.
- **A modifier taking `Binding<T>` gets a real two-way binding** instead of a value it can't accept.

## Diagnostics and build gates

- **`SB1003` warns when a write through a struct property hits a copy** — `owner.Prop.Field = x` compiles, runs, and changes nothing, and since non-frozen structs project as classes, nothing hints that the receiver is a temporary. Mutating calls like `owner.Settings.Bump(1)` are covered too.
- **`SB0010` marks a protocol nothing can call back** — you could implement `: ISomeDelegate` for a protocol whose every requirement falls out of reverse dispatch, and Swift got a table of nulls. It compiled, linked, ran, and never fired once. Partially dispatchable protocols are untouched.
- **A missing SwiftUI bridge native is an error** — `SWIFTBIND052`, opt out with `<SwiftBridgeRequired>false</SwiftBridgeRequired>`. Separately, the pack-time skipped-members warning moved from `SWIFTBIND061` to `SWIFTBIND066` to stop colliding with an unrelated generator warning; update `NoWarn` filters if you suppress it.
- **The report says what was lost and why** — conformances to a protocol with no type record, or with open associated types, used to disappear with no row anywhere. Skipped members now name the cause, `SB0001` carries the wrapper's reason into the report and the `[Obsolete]` text, and losses are ranked so the expensive ones surface first.
- **The documented API surface matches what's emitted** — the api-surface doc and manifest could name members the C# never declared. Writers that reshape a signature now record what they wrote, a reconciliation pass fails the build on anything left over, and properties and subscripts joined the manifest (2,993 → 4,385 entries). The doc ships as the binding package's README.

## Packages

| Package                  | Version |
|--------------------------|---------|
| SwiftBindings.Runtime    | 0.19.0  |
| SwiftBindings.Sdk        | 0.19.0  |
| SwiftBindings.Templates  | 0.19.0  |

`SwiftBindings.Apple` is unchanged at `26.2.8` — it declares a floor-only Runtime range, so the published supplement rides forward without a republish.

See the [GitHub wiki](https://github.com/justinwojo/swift-dotnet-bindings/wiki) for installation and usage.
