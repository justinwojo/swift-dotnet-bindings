# SwiftUI bridge and KeyPaths — reference notes

Searchable findings to consult when working in this area. Nothing here is queued or newly authorized.
[How to use and maintain these notes](README.md). Recorded evidence and counts describe their original investigation; recheck against current source before acting.

<a id="swiftbind052-stays-an-error-rather-than-a-warning-confirmation-wanted"></a>

## `SWIFTBIND052` stays an error rather than a warning — confirmation wanted

The bridge-required diagnostic has an Error arm and a Warning arm and defaults to required-and-error. The error arm has since fired on a real consumer surface for an unrelated reason (an unqualified nested SwiftUI View name), which is how the underlying naming bug was found — the diagnostic behaved correctly. Recommendation: keep it an error; a silently missing bridge fails at runtime with a `DllNotFound`-shaped symptom, which is strictly worse to diagnose than a build stop. Absent an answer, error stands. **Trigger:** the error blocks a build where the bridge genuinely is not needed, or the owner asks for the warning arm to become the default.

<a id="swiftui-beyond-current-level"></a>

## SwiftUI beyond current level

The hosting bridge (`SwiftUIBridgeEmitter`) now hands back a typed `UIKit.UIViewController` (`ViewController` accessor) for embedding in UIKit /.NET MAUI. The KeyPath-binding productionization for SwiftUI / SwiftUICore (`Binding<T>`/`ObservedObject` `@dynamicMemberLookup`, `EnvironmentValues`, `ForEach(_:id:)`) is **declined**, not deferred — view composition is blocked by the `@ViewBuilder` / result-builder wall (see *Explicitly Out of Scope* below), so the projection surface has no C# caller. Same applies to the residual KeyPath consumers (Charts / SwiftData / Combine / UIKit / Observation): macro-driven DSLs, framework-boundary cases, or `dotnet/macios` duplication. Framework-selection decisions live in `Design/apple-framework-portfolio.md`. **Trigger:** consumer feedback names a specific SwiftUI surface as blocking — the result-builder wall means the projection has no C# caller today, so a named blocked surface is what would change the arithmetic.

<a id="property-wrappers-keypaths-beyond-foundation-kvo-attributedstring"></a>

## Property wrappers / KeyPaths beyond Foundation KVO + AttributedString

Foundation KVO `observe(_:options:changeHandler:)` ships via the per-class `KvoExtensionEmitter` (Session 7); `AttributedString` ships a hand-rolled partial with public ctor + `LanguageIdentifier` via the Apple Supplement xcframework (Session 7). Property wrappers + broader KeyPath surfaces (e.g. `@FocusState`, `@Environment`, SwiftUI-side keypath subscripts) remain low-frequency in public API surfaces and stay unscoped. **Trigger:** a shipping library's *primary* surface is gated behind a property wrapper or keypath subscript — low frequency is the whole reason this is unscoped, so one real blocked surface is what changes the arithmetic.

<a id="async-createasync-parity-gaps-raw-intptr-surface-dropped-issimpleenum"></a>

## Async `CreateAsync` parity gaps (raw-IntPtr surface + dropped `IsSimpleEnum`)

Two gaps on the `SwiftUIBridgeEmitter.AsyncPattern.cs` flatten path the non-async factory doesn't have. (1) Every flattened `BoundType`/`BoundStruct` init param surfaces as raw `IntPtr` (`:1151` switch, `:1191` null-check, `:1277` forward) vs the non-async typed conversion (`SwiftUIBridgeEmitter.cs:3053`) — ergonomics only, works via `.Handle`. (2) The flattener drops `IsSimpleEnum` (no such field on `AsyncFlatParam`) and casts unconditionally (`:1271`), so a complex (class-projected) raw-value enum leaf emits `(int)<class instance>` — a CS0030-shape **compile break** (non-async splits correctly at `:3040`). Zero emission today (all 4 emitted `CreateAsync` overloads are string/int/bool-only). **Trigger:** an async-pattern View whose construction-chain leaf init param is a `BoundType`/`BoundStruct` (e.g. `Foundation.URL`) or a complex raw-value enum. Fix: mirror the non-async typed/`IsSimpleEnum` split onto the async switch/null-check/forward.

<a id="hint-and-async-pattern-tables-are-leaf-keyed-so-a-same-leaf-view-group-shares-one-entry"></a>

## Hint and async-pattern tables are leaf-keyed, so a same-leaf view group shares one entry

**Revisit when:** a consumer needs to hint or pattern one member of a same-leaf group differently from its siblings.

<details>
<summary>Recorded evidence and disposition</summary>

The 2026-08 audit fix wave made view collection module-qualified and derived every generated symbol from `ViewBridgeInfo.Identifier`, so same-leaf views under different enclosing types now all bridge. The lookup tables deliberately stayed leaf-keyed: `hints?.Views?.GetValueOrDefault(info.ViewName)` and `ResolveAsyncPattern($"{module}.{viewName}")` resolve the same entry for every member of a shared-leaf group. The one *colliding* consequence — both views emitting the manifest entry's authored Swift `SessionClassName` as one redeclared `final class` — was fixed at emission (`SwiftUIBridgeEmitter.cs:91`, a path-qualified view re-spells the session class from its own identifier; pinned by `EmitBridgeFiles_SameLeafAsyncViewsFromManifest_EmitDistinctSessionClasses`). What remains is the *sharing itself*: a skip/forceTemplate/preferredInit hint or an async-pattern descriptor keyed to a leaf applies to every view with that leaf, with no syntax to address one member of the group (`OuterA.ScanView` but not `OuterB.ScanView`). Compile-safe by construction — all shared fields except the session class are inputs, not declared symbols. Verified across both r2/r3 review rounds as by-design. **Fix shape:** accept module-qualified keys in the hints file and manifest (falling back to leaf), a loader + lookup change, not an emitter change. **Trigger:** a consumer needs to hint or pattern one member of a same-leaf group differently from its siblings.

</details>

<a id="font-design-crosses-the-abi-under-two-independent-numberings"></a>

## Font design crosses the ABI under two independent numberings

The same concept — SwiftUI `Font.Design` — has two C#↔Swift numbering lanes that disagree with each other while each being internally consistent. Lane A: `Swift.Runtime`'s `Font.Design` enum (`Font.cs:66`: Default=0, **Serif=1**, Rounded=2, Monospaced=3) paired with the runtime shim `sbwFontDesign` (`SwiftBindingsRuntime.swift:961`: 1→serif, 2→rounded, 3→monospaced). Lane B: `SwiftFontDesign` (`SwiftFont.cs:102`: Default=0, **Rounded=1**, Monospaced=2, Serif=3) paired with the generated theme bridge's `SBW_fontDesign` (`ThemeBridgeEmitter.cs:421`: 1→rounded, 2→monospaced, 3→serif). No live bug — each C# encoder feeds only its own Swift decoder. The trap is any future cross-wiring: a cast like `(SwiftFontDesign)(int)design`, or pointing one lane's encoder at the other's decoder, silently swaps serif/rounded/monospaced with no compile error and no crash — just the wrong typeface. **Fix shape:** unify on one numbering (mechanically: make `SwiftFontDesign` mirror `Font.Design`'s order and update `SBW_fontDesign` in the same commit; both sides of lane B regenerate together so it is not a compat break). **Trigger:** any change touching either design enum or either shim, or the first code path that converts between the two enums.

<a id="swiftui-value-types-still-declared-frozen-false-against-a-frozen-reality"></a>

## SwiftUI value types still declared `frozen="false"` against a frozen reality

`SwiftUICore` declares them `@frozen`; a binding that believes otherwise passes a buffer address where Swift wants the boxed value — a SIGSEGV, not a compile error. The SwiftUI value-constructibility change (`cb64a1b8`) corrected `Color` and `Font` only (`frozen="true" inlineSize="8" abiLayout="p8"` plus `Buffer`/`PayloadBuffer` on the shells) — and found the mis-declaration as a root cause, not as planned work: those two crashed until it was fixed. `EdgeInsets`, `Animation`, `Image`, `Text`, `AnyView` and `Binding` remain mis-declared in `SwiftUIDatabase.xml`. Unreachable today because nothing constructs them by value, so the wrong declaration cannot be observed; the per-type fix is the same attribute correction plus shells. **Trigger:** any of those six becomes constructible by value — a factory, a settable property, or a by-value parameter reaching a real call — correct its declaration in the same change.
