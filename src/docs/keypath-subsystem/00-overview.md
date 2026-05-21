# Swift `KeyPath` interop subsystem

Project, not a session. Owner: TBD. Target SDK: TBD (not 0.11.0).

This folder is the execution plan for adding Swift `KeyPath` support to the bindings generator and runtime. The original proposal lives below; the per-session execution plans (each a self-contained, shippable, end-to-end commit) live in the numbered files alongside this one.

## Session index

| # | File | Purpose | Depends on |
|---|------|---------|------------|
| 0 | `00-overview.md` | This doc — proposal, scope, ABI ground truth, design decision | — |
| 1 | `01-property-drop-bug.md` | Co-deferred gap 3 — surface silent property-drop on PAT-constrained generic parents with tombstones; fix root cause | — |
| 2 | `02-parent-only-sync-csm.md` | Co-deferred gap 1 — relax `ownParams.Count > 0` so plain sync methods on PAT-constrained generic parents emit | — |
| 3 | `03-keypath-foundation.md` | KeyPath foundation: type records for `AnyKeyPath` / `PartialKeyPath` / `KeyPath` / `WritableKeyPath` / `ReferenceWritableKeyPath`, `KeyPathProjection`, `SwiftKeyPath` SafeHandle, end-to-end opaque pass-through fixture | — |
| 4 | `04-typed-singleton-emission.md` | Generator emits per-`(Root, Property, Value)` Swift trampoline returning the retained KeyPath + C# `static readonly` typed singleton field for closed conformers | 03 |
| 5 | `05-parent-only-async-csm.md` | Co-deferred gap 2 — async CSM emission inside per-conformer `*CsmExtensions`, with parent-generic return-type substitution before async callback generation | 02 |
| 6 | `06-musiclibraryrequest-re-enablement.md` | Wire MusicKit; re-enable `MusicLibraryRequest<T>`'s 11 surface members end-to-end (composition test of sessions 1–5) | 01–05 |
| 7 | `07-foundation-kvo-attributedstring.md` | Foundation KVO publisher/observer (`NSObject` extensions) + `AttributedString` `@dynamicMemberLookup` with `KeyPath<AttributeDynamicLookup, K>` | 03–04 |
| 8 | `08-appintents-productionization.md` | AppIntents — biggest consumer (704 lines, 240 `WritableKeyPath`). `EntityProperty` getter/getSetter; `AppShortcutParameterPresentation` constrained generic | 03–04 |
| 9 | `09-swiftui-productionization.md` | SwiftUI + SwiftUICore — environment modifiers, view tree, `@dynamicMemberLookup` on `Binding` / `ObservedObject` (`ReferenceWritableKeyPath` heavy) | 03–04 |
| 10 | `10-residual-consumers-cleanup.md` | Charts + SwiftData + Combine + UIKit + Observation. Final pass; doc/wiki update; close A-1 | 03–04 |

Session 1 and Session 2 are independent of KeyPath itself — they're co-deferred gaps that block `MusicLibraryRequest<T>` re-enable. They land first because they're small, low-risk, and restore diagnostic visibility/engine fidelity that downstream sessions depend on for correct emission.

After Session 6, the consumer-productionization sessions (7–10) can run in any order; they only depend on the foundation + singleton emission being in place.

## ABI ground truth (verified via SIL/asm probe — 2026-05-18)

These facts have been verified against `swiftc -emit-sil -O` and `swiftc -emit-assembly -O` output for Swift 6.2.4 (Xcode 26.3). They override the "to be confirmed" hedging in the design space below.

- **`KeyPath` at `@_cdecl` boundary: single pointer.** `@_cdecl` rejects `KeyPath<Root, Value>` directly (`"method cannot be marked '@_cdecl' because its result type cannot be represented in Objective-C"`). The cdecl-compatible spelling is `UnsafeRawPointer` (return) / `UnsafeMutableRawPointer` (param), with `Unmanaged.passRetained(...).toOpaque()` for OUT and `Unmanaged.fromOpaque(...).takeUnretainedValue()` for IN.
- **Return is `+1` (retained), parameter is `@guaranteed`.** SIL signatures: `() -> @owned KeyPath<...>` for returning; `(@guaranteed KeyPath<...>, ...) -> ...` for consuming. The C# wrapper must mirror this: SafeHandle ctor accepts a retained pointer (no extra retain on construction); for IN parameters, `DangerousAddRef` / `DangerousRelease` around the P/Invoke to extend the borrow.
- **`\Type.prop` literal compiles to `WritableKeyPath` when the rooted property is `var`**, then `upcast` to `KeyPath` where the static type narrows. `keypath` SIL instruction emits a TU-local descriptor (`l_keypath` in `__TEXT,__const`); the runtime materialises the KeyPath object via `swift_getKeyPath(descriptor, nullptr)` with a per-descriptor once-token.
- **Interning is per-descriptor-pointer, not per-pattern-value.** Same `\Point.x` literal site, called N times in one TU, returns identity-equal KeyPath. The same path written in a separate TU/module returns *value-equal* but *not identity-equal* KeyPath. Cross-process identity is not meaningful. **Consequence: equality must dispatch to `AnyKeyPath.==` (value equality on path content), never to pointer comparison.**
- **WritableKeyPath / ReferenceWritableKeyPath are subtypes** — same single-pointer ABI at the function boundary; SIL distinguishes them as `$@guaranteed WritableKeyPath<...>` vs `$@guaranteed KeyPath<...>` at the type-tag level; `upcast` is the SIL op for the narrowing direction. The difference at use sites is which runtime helper is called: `swift_getAtKeyPath` (read), `swift_setAtWritableKeyPath` (mutate value-type), `swift_setAtReferenceWritableKeyPath` (mutate reference-type property).
- **Runtime construction surface: `swift_getKeyPath` only.** There is no exported `swift_keyPath_create` or component-wise builder in `libswiftCore.dylib`. All KeyPath construction goes through a `keypath` SIL instruction (compiler-emitted descriptor) feeding `swift_getKeyPath`. **Consequence: C# cannot originate a KeyPath at runtime; it must call a generator-emitted Swift trampoline that contains the `keypath` instruction. This is the structural reason Option 2 (typed singletons) is required.**
- **`@_inheritsConvenienceInitializers`** on all five KeyPath classes; no public designated initializer is exposed in `Swift.swiftinterface`. Confirms construction is exclusively via key-path-expression in Swift source.

Evidence probe: `/tmp/keypath-abi-probe/` (this session's research workspace; not checked in).

## Design decision (post-research)

**Pick Option 4 — hybrid — with these constraints:**

1. **OUT path** (KeyPath returned from Swift or read from a Swift property): opaque retained pointer ↦ C# `SwiftKeyPath<TRoot, TValue>` SafeHandle.
2. **IN path** (C# caller originates the KeyPath, passes to a Swift method): generated typed singletons. For each closed conformer of a generic-parent type that has a nested `KeyPath`-rooted bag (e.g. `Album.LibraryFilter`), the generator walks the bag's stored properties and emits one Swift `@_cdecl` trampoline per property returning a retained KeyPath. C# surfaces those as `public static readonly KeyPath<TRoot, TValue> PropertyName` initialised by a single-call P/Invoke at first access.
3. **Three distinct C# types** — `Swift.KeyPath<TRoot, TValue>`, `Swift.WritableKeyPath<TRoot, TValue>`, `Swift.ReferenceWritableKeyPath<TRoot, TValue>` — sharing an internal `SwiftKeyPathHandle` base. Static type safety: a read-only `KeyPath` cannot satisfy a `WritableKeyPath` parameter. `WritableKeyPath` *is-a* `KeyPath` via C# inheritance (mirrors the Swift class hierarchy).
4. **Equality and hashing** dispatch to `AnyKeyPath.==` / `AnyKeyPath.hashValue` via runtime trampolines in `Swift.Runtime`. Pointer-identity equality is forbidden (cross-module false negatives).
5. **`PartialKeyPath<Root>` is in scope** (added late to the plan after consumer-surface grep found heavy SwiftData + UIKit use). Same machinery, type-erased `Value` slot. Lives in Session 3 alongside the typed variants.
6. **`AnyKeyPath`** — exposed only as the base SafeHandle type for code paths that need a fully-erased reference. Not a primary user surface.
7. **Open associated-type-rooted KeyPath parameters** (`KeyPath<MusicItemType.LibraryFilter, ...>` where `MusicItemType` is the parent's associated type) are **out of scope for v1**. They resolve only at CSM time after the conformer substitutes the associated type; emission requires the closed conformer's `LibraryFilter` TypeDecl (which for MusicKit is a module-scope protocol bag, e.g. `MusicKit.LibraryAlbumFilter`, rather than a nested concrete struct — Session 4's protocol-bag extension covers this). Session 4 handles the closed-conformer case (sufficient for MusicKit's 8 conformers — Album, Artist, Genre, MusicVideo, Playlist, Playlist.Entry, Song, Track). Open associated-type parameters that *route* a KeyPath through a generic method but where the Root is the parent's associated type are explicitly tracked as a Phase-3+ follow-up; v1 emits them suppressed with a tombstone comment.

Options 1 (pure pass-through) and 3 (string/Mirror) are rejected as end-states. Option 1 alone leaves IN-path APIs unreachable from C#. Option 3 is not type-safe, cannot model `WritableKeyPath`, and ABI-incompatible with consumers that need the exact runtime KeyPath object.

## Consumer surface size (verified via swiftinterface grep on iOS SDK 26.2)

Sizing the work for Sessions 7–10:

| Framework | `KeyPath<` lines | WritableKeyPath | ReferenceWritableKeyPath | Note |
|---|---|---|---|---|
| **AppIntents** | 704 | 240 | 0 | Largest surface. `EntityProperty` getter/getSetter overloads, `AppShortcutParameterPresentation` |
| **SwiftUI + SwiftUICore** | 319 | 43 | 6 | Env modifiers, view tree id:, `@dynamicMemberLookup` on Binding/ObservedObject |
| **Foundation** | 218 | 5 | 0 | KVO bridging, `AttributedString` `@dynamicMemberLookup` |
| **Charts** | 49 | 0 | 0 | `VectorizedChartContent` protocol extensions; all read-only |
| **SwiftData** | 38 | 0 | 0 | Mostly `PartialKeyPath<T>` (schema/index metadata) |
| **MusicKit** | 26 | 0 | 0 | `MusicLibraryRequest<T>` filter/sort (immediate driver) |
| **Combine** | 14 | 4 | 4 | `Publisher.map`, `Publisher.assign(to:on:)` |
| **UIKit** | 12 | 2 | 2 | `UIPasteboard` `PartialKeyPath`, `@_enclosingInstance` subscripts |
| **Observation** | 4 | 0 | 0 | `ObservationRegistrar` |
| **StoreKit** | 0 | 0 | 0 | None (the doc's list overstates) |

Three shape families across all consumers:
- **(a) Generic method parameter on protocol extension** — AppIntents, SwiftUI, Charts, Observation
- **(b) `@dynamicMemberLookup` subscript projecting into a bag type** — SwiftUI `EnvironmentValues`, Foundation `AttributeDynamicLookup`, UIKit `AttributeScopes`
- **(c) Initializer parameter on generic struct/class descriptor type** — MusicKit, AppIntents `EntityProperty`, SwiftData `FetchDescriptor`

`PartialKeyPath` is a distinct binding surface — Session 3 covers it as a foundation-layer type alongside `KeyPath`.

---

This is a research-and-design proposal for adding Swift `KeyPath` support to the bindings generator and runtime. No implementation has started. The doc below was the original framing — sections that have been superseded by the verified findings above (ABI hedging, design-space narrowing) remain for historical context, but the **ABI ground truth** and **design decision** sections above are authoritative for the implementation sessions.

## Why this is a project, not a session

Swift `KeyPath<Root, Value>` is a first-class reference type representing a typed path from a root type to a value. It's used as a parameter in many high-level Apple framework APIs — for filtering, sorting, property observation, SwiftUI bindings, SwiftData predicates, AppIntents, Charts marks, and more. The bindings generator has no projection, no marshalling, no type-record treatment, and no runtime helper for it today. Only the demangler/parser knows the node names exist.

Supporting it correctly means designing a representation that Swift and C# both agree on, building the marshalling, projecting it into a usable C# API shape, and handling cross-module + ARC + value-witness concerns. This is the same scale of work as the closure subsystem or the protocol-existential subsystem — a multi-phase initiative, not a predicate flip.

## Scope of consumer impact

Swiftinterface grep across the iOS SDK shows public `KeyPath` surface in (at minimum):

- **MusicKit** — `MusicLibraryRequest<T>` filter/sort overloads (the immediate driver)
- **SwiftUI** and **SwiftUICore** — extensive: `@Binding`, `@Bindable`, `Picker(selection:)`, sort descriptors, animated property paths, `ForEach(id:)`, modifiers
- **SwiftData** — predicate construction, `#Predicate`, model property observation, query sort descriptors
- **Charts** — mark builders, `ChartContent`, axis content
- **AppIntents** — parameter definitions, `@Parameter` metadata, dynamic options
- **StoreKit** — transaction filtering
- **Combine** — `KeyPath`-based subscribers and publishers
- **Foundation** — `NSPredicate`/`NSSortDescriptor` bridging APIs, `Locale.Components`, `Calendar`
- **UIKit** — `UIBindable`, observability APIs, layout anchors via key path
- **RealityFoundation**, **Accessibility**, **Speech**, **TipKit**, **Testing**, **XCUIAutomation** — assorted

In short: KeyPath is not a MusicKit-only concern. It's structural for any Swift-idiomatic API surface that Apple ships on top of Observation / SwiftUI / SwiftData. Until this lands, those APIs surface as tombstones in the generated bindings.

## Co-deferred gaps that also block `MusicLibraryRequest<T>`

`MusicLibraryRequest<T>` has 11 surface members. KeyPath unblocks 8 of them (7 filter overloads + `sort`). To fully bind the remaining 3, three smaller architectural gaps must also land. They're independent of KeyPath but are co-deferred with this subsystem because the user-visible deferral is "the whole type is suppressed."

These are not part of the KeyPath subsystem itself, but they must be tracked alongside it so the type can be fully re-enabled when KeyPath ships.

### Co-deferred gap 1 — Parent-only sync CSM

Blocks `filter(text: String)` (1 surface member: a plain mutating method with no method-own generics, on a PAT-constrained generic parent).

The architectural reality: `ConcreteSpecializationEngine.FindSpecializableMethods` (`src/Swift.Bindings/src/Marshaler/ConcreteSpecializationEngine.cs:534`) currently requires `ownParams.Count > 0` — methods that have *only* parent-baseline generic params (no method-own generics) are filtered out and never reach the generic-parent CSM emitter. The `EmitConcreteSpecializationsForGenericParent` path (`ConcreteProtocolSpecializationEmitter.cs:2374-2499`) has a `methodParams.Count == 0` branch already, but nothing feeds it.

Touch points:
- `ConcreteSpecializationEngine.FindSpecializableMethods` — drop the `ownParams.Count > 0` restriction; propagate parent-only resolution through `ResolveParentSpecializableParams`.
- `IsCsmSyncEligibleForGenericParent` (`ConcreteProtocolSpecializationEmitter.Sync.cs`) — extend predicate to recognise parent-only methods.
- `MemberValidationPipeline` Phase 4a — route parent-only methods as `RoutedElsewhere`.
- BindingTests fixture — `Bag<T: PatProto>` with one parent-only sync method.

Risk: low. The emission infrastructure mostly exists; this is predicate alignment.

### Co-deferred gap 2 — Parent-only async CSM

Blocks `response() async throws -> MusicLibraryResponse<MusicItemType>` (1 surface member: async, no method generics, return type substitutes the parent generic).

Larger than gap 1 because the async CSM path explicitly rejects generic parents at two sites:
- `ConcreteProtocolSpecializationEmitter.Async.cs:483` — `PassesAsyncMethodLevelGuards` returns false when `parentTypeDecl.IsGeneric`.
- `ConcreteProtocolSpecializationEmitter.cs:2447` — generic-parent path has `if (method.IsAsync) continue;`.
- `IsCsmAsyncEligible` (`ConcreteProtocolSpecializationEmitter.Async.cs:633`) — requires `ownParamCount > 0`.

A correct fix is more than "delete the rejection." The async harness needs to emit *inside the per-conformer `*CsmExtensions` class* so the return type `MusicLibraryResponse<MusicItemType>` substitutes to the closed `MusicLibraryResponse<Album>` per conformer, and the `Task<…>`-typed callback is generated against the closed type.

Touch points:
- Gap 1's engine work, extended to async.
- Lift the two hard rejections above with corresponding predicate alignment.
- `AsyncHarnessEmitter` — generic-parent hoisting currently routes to a shared non-generic helper; per-concrete closed harnesses inside `*CsmExtensions` are a different emission site.
- Return-type substitution applied *before* async callback type generation.
- BindingTests fixture — `Bag<T: PatProto>` extended with one parent-only async method.

Risk: medium. New emission site for the closed-conformer async harness. Codex called it "medium-risk focused feature"; Grok called it "non-trivial extension of the CSM-async machinery."

### Co-deferred gap 3 — Property-drop bug

Blocks `limit`, `offset`, `includeOnlyDownloadedContent` (3 surface members: plain properties on the generic parent).

These properties are absent in the pre-image with **no tombstone comment** — meaning a code path is dropping them silently, not the named gates (which emit explanatory comments). Likely a `PropertyHandler` or accessor-level rejection that fires on PAT-constrained generic parents and skips emission without recording why.

Touch points:
- Trace the property-handler path for `limit`/`offset`/`includeOnlyDownloadedContent` on `MusicLibraryRequest<T>` until the silent-skip site is found.
- Fix at the actual site; ensure a tombstone is emitted (or the property is emitted correctly) for future visibility.
- BindingTests fixture — generic struct with PAT-constrained generic param and a plain property accessor.

Risk: low. Single-emitter trace; fix shape likely one-line guard relaxation once located.

### Bundling for shipping

For 0.11.0 ship purposes, these three gaps + the KeyPath subsystem are bundled as a single user-visible deferral: `MusicLibraryRequest<T>` is suppressed. Re-enabling the type requires all four to land. Gaps 1-3 can in principle land independently of the KeyPath subsystem (they'd unblock a subset of the type's surface), but there's no reason to prioritise them on their own — the type is only useful as a full unit, and the major architectural piece is KeyPath.

These gaps have no impact on Stripe, BlinkID, BlinkIDUX, Mappedin, or the other currently-bound Apple frameworks. They surface only on PAT-constrained generic types with no-method-generics methods — which `MusicLibraryRequest<T>` is the only currently-bound example of.

## What `KeyPath` actually is (background)

Background written from public documentation and the swiftinterface alone — not yet verified against SIL dumps. Phase 0 must confirm.

- `KeyPath<Root, Value>` is a generic *class* (reference type) in the standard library.
- Subclass hierarchy: `AnyKeyPath` → `PartialKeyPath<Root>` → `KeyPath<Root, Value>` → `WritableKeyPath<Root, Value>` → `ReferenceWritableKeyPath<Root, Value>`. The generator currently has no projection for any of them.
- Created in Swift via the `\.Root.path.to.value` expression syntax, or programmatically via runtime APIs.
- ABI: reference-typed, ARC-managed, passes as a pointer to the heap-allocated key path object. Equality / hashing is value-based on the path content.
- Sendable: `KeyPath` conforms to `Sendable` when `Root` and `Value` are Sendable.
- Cross-module: key paths created in one module are usable in another so long as the projected path's components are accessible. The closed-form representation is opaque pattern bytes.

## Design space (not yet decided)

Each option has real tradeoffs. Phase 1 picks one (or a hybrid).

### Option 1 — Opaque handle pass-through

C# represents `KeyPath<Root, Value>` as an opaque `SwiftSafeHandle<AnyKeyPath>`-style reference. C# can hold KeyPaths returned from Swift and pass them into other Swift APIs, but **cannot construct one from C#**.

Pros: minimum new surface, mirrors the existing closure / Swift-class pattern.

Cons: most KeyPath consumer APIs expect the C# caller to *originate* the path — `request.Filter(matching: \.someProperty, equalTo: x)`. With pass-through-only, those APIs are still unreachable from C#. This option is necessary but not sufficient.

### Option 2 — Generated typed KeyPath singletons per (Root, field, Value)

For each closed conformer where the bound generic's parent has a KeyPath-rooted nested type (e.g., `MusicKit.Album.LibraryFilter`, `MusicKit.Song.LibraryFilter`), walk the nested type's stored properties, emit a Swift trampoline per property that returns a pre-constructed KeyPath, and expose them as static C# fields:

```csharp
public static class AlbumLibraryFilter {
    public static readonly KeyPath<Album.LibraryFilter, string> Title = …;
    public static readonly KeyPath<Album.LibraryFilter, bool?> IsExplicit = …;
    // …
}
// Usage:
request.Filter(matching: AlbumLibraryFilter.Title, contains: "love");
```

Pros: type-safe at the C# call site, ergonomic for the common case (filter/sort with closed conformers), no runtime parsing.

Cons: code generation surface scales with (conformer × field × value-type) — for MusicKit alone that's 8 conformers × ~10 fields × multiple Value types each. Open generic forms (`KeyPath<MusicItemType.LibraryFilter, …>` parameter on an open-generic method) still need a different story. Doesn't solve SwiftUI/SwiftData where the Root type is user-defined.

### Option 3 — String/selector-based wrapper API

Generate C# methods that take property names as strings; the Swift wrapper looks the property up via Mirror or runtime metadata and constructs the KeyPath internally:

```csharp
request.Filter(matchingProperty: "title", contains: "love");
```

Pros: minimal generation surface, works for any KeyPath shape.

Cons: loses static type safety entirely. Mirror-based path resolution doesn't compose with all KeyPath APIs (some need the *exact* closed-form KeyPath object, not a runtime reconstruction). Probably ABI-incorrect for `WritableKeyPath` and `ReferenceWritableKeyPath` consumers.

### Option 4 — Hybrid (likely the answer, but design phase must confirm)

- Opaque handle pass-through for **OUT cases** (KeyPath returned from a Swift method or read from a Swift property).
- Typed generated singletons for **IN cases on closed conformers** where the cost/benefit pays off (MusicKit filter, sort descriptors).
- Possibly a C#-side `KeyPath<TRoot, TValue>` value type that wraps the opaque handle and is constructed via a typed factory.
- Open generics where the Root references a parent associated type (`KeyPath<MusicItemType.LibraryFilter, …>` on an open-generic method) remain unsupported until a separate pass — that's the same shape as the existing "associated-type reference in method generic" gap, not specific to KeyPath.

## Generator pieces required (rough)

Not yet committed. To be refined in Phase 1.

- **Type record** for `Swift.KeyPath` / `Swift.WritableKeyPath` / `Swift.ReferenceWritableKeyPath` in the type database, with reference-type flags and ARC ownership semantics.
- **Type projection** (`ITypeProjection`) implementing the chosen C# representation. Must satisfy the `Visit()` pattern across `AccessorGetterConversionVisitor`, `AccessorSetterConversionVisitor`, `OptionalAccessorGetterVisitor` (per `constraints.md`).
- **Marshalling** at parameter and return boundaries — cdecl mapping, `Unmanaged.passRetained` / `takeRetainedValue` for ARC if pointer-shaped.
- **Wrapper emission** in `WrapperEmitter` for Swift-side KeyPath construction (for Option 2's singleton trampolines).
- **C# runtime helper** in `Swift.Runtime` — at minimum, a `SwiftKeyPath` SafeHandle wrapper.
- **Optionality** — `KeyPath<…>?` parameters and returns must compose with the existing Optional infrastructure.
- **Cross-module rules** — a KeyPath created in module A and consumed in module B must survive the boundary (re: cross-module proxy class qualification rules in `constraints.md`).
- **Generic-parent surfaces** — the open-generic methods on `MusicLibraryRequest<T>` etc. that take a KeyPath rooted in an associated type need to compose with parent-only CSM (a separate piece of architecture that is also currently missing — see `sdk-0.11.0-session-2-findings.md`).

## Open questions for Phase 0 / Phase 1

These must be answered before any implementation:

- What is the precise ABI of `KeyPath` at the `@_cdecl` boundary? Single pointer? Two-word existential? Confirm via SIL dump for a minimal example.
- Are key paths interned by Swift, or freshly allocated per use? Affects whether the singleton pattern in Option 2 needs explicit lifetime management.
- Does `\.MyType.property` produce the same bytes in two compilation units, or does cross-module use require runtime construction?
- How does `WritableKeyPath` and `ReferenceWritableKeyPath` interact with C# mutability? Do we expose them as separate C# types or as flags on a single wrapper?
- For Option 2's typed singletons, are the field walks tractable at generation time? Some `LibraryFilter` shapes are themselves generic over the parent (`MusicItemType.LibraryFilter` is an associated type) — singleton-per-conformer means walking each conformer separately.
- Does the runtime support `KeyPath` construction from C# (e.g., via `_swift_keyPath_create` ABI) or is the singleton trampoline the only path?
- Sendable bridging: does the C# representation need to be thread-safe?

## Phased approach

No effort estimates. Each phase has a binary exit criterion.

### Phase 0 — Research

Goal: answer the open questions above with verified evidence (SIL dumps, runtime ABI inspection, swiftinterface analysis across the broader consumer surface).

Output: a follow-up findings doc, similar in shape to `sdk-0.11.0-session-2-findings.md`, that nails down the ABI shape and either confirms or eliminates each design option.

Exit: the design space is narrowed from four options to one (or one hybrid) with the tradeoffs documented.

### Phase 1 — Design proposal

Goal: a concrete spec for the C# API shape, the generator pieces touched, the cross-module behaviour, and the BindingTests fixture matrix.

Output: a design doc covering type-record / projection / marshalling / wrapper / runtime, with concrete code sketches for representative consumer surfaces (MusicKit filter, SwiftData predicate, SwiftUI binding).

Exit: design reviewed by Codex + Grok with zero High/Critical findings open. User sign-off on the chosen design before any implementation work begins.

### Phase 2 — Prototype

Goal: implement the minimum end-to-end path needed to bind *one* KeyPath consumer surface — likely `MusicLibraryRequest<T>.filter(matching: KeyPath<…>, contains: String)` on a single conformer. No other consumer libraries touched.

Output: a working BindingTests fixture exercising the prototype path; the type-record / projection / marshalling code in the generator; the runtime SafeHandle wrapper.

Exit: BindingTests fixture passes on sim and device. `nuke test`, `nuke binding-tests --sim --device` green. Validate stays at baseline.

### Phase 3 — Productionize

Goal: extend the prototype to the full KeyPath surface across consumer libraries — all `MusicLibraryRequest<T>` filter/sort overloads, then SwiftUI / SwiftData / Charts / AppIntents / Combine surfaces as they unblock.

Output: incremental commits per consumer-library surface, each with regen-and-grep verification against pre-image; updated BindingTests for the broader shape matrix.

Exit: validate baseline ratchets up across the consumer-library set. Per-library regen-and-grep verifies the previously-tombstoned KeyPath surface now emits.

### Phase 4 — Consumer surface re-enablement

Goal: revisit the suppressed types in 0.11.0 (and any others that were suppressed pending KeyPath) and re-enable them with the new infrastructure. Update the residual-gaps docs and the roadmap.

Exit: the deferred A-1 item is closed; consumer-facing wiki documentation updated.

## What this subsystem does NOT include

- **Parent-only CSM** (the "method with no own generics on a PAT-constrained generic parent" gap, which `MusicLibraryRequest<T>.filter(text:)` and `.response()` need independent of KeyPath). That's a separate piece of architecture — see `sdk-0.11.0-session-2-findings.md`. Some KeyPath-using methods may also need it, but the two efforts are orthogonal and one does not subsume the other.
- **Async generic-parent CSM** — same separate gap; `MusicLibraryRequest<T>.response()` needs it but `response()` does not itself take a KeyPath.
- **SwiftData macro / `#Predicate` macro expansion** — KeyPath is necessary but not sufficient for SwiftData. Predicate macros are a separate concern.
- **SwiftUI view-tree / `ViewBuilder` result builders** — KeyPath bindings work with SwiftUI but the result-builder DSL has its own unresolved gaps.

## Decision points the user owns

- Whether to commit to this project at all for 0.12.0 or later, vs. accept KeyPath-using surfaces remaining suppressed indefinitely.
- Whether Phase 0 / Phase 1 happen in this codebase or in a research worktree.
- The level of cross-module / cross-library KeyPath fidelity (a minimum-viable subsystem might cover only single-module closed-conformer cases first).

## Status

Sessions 1–6 shipped on the `keypath-subsystem` branch. Sessions 7–10 (consumer productionization — Foundation/KVO, AppIntents, SwiftUI, residual consumers) are next.

| # | Status | Landed at |
|---|---|---|
| 1 — Property-drop bug | shipped | `fef9c065` |
| 2 — Parent-only sync CSM | shipped | `48b08ec9` |
| 3 — KeyPath foundation | shipped | `26dcdcb6` |
| 4 — Typed singleton emission | shipped | `1e72be9c` (+ `53f8b5bd` protocol-bag broadening) |
| 5 — Parent-only async CSM | shipped | `504e482a` |
| 6 — MusicLibraryRequest re-enablement | shipped | `345dd701` (parent wiring) |
| 6b — CSM method-own generic machinery (filter KeyPath) | shipped | `c8cf1226` + `f7819e43` + `af26a62d` |
| 6c — Route C per-V sort specialization + `KeyPath<any P, V>` admission | shipped | `62ec673e` |
| 6c-followup — CSM `FromX()` / generic-param-return cleanup discriminated by NewFromPayload contract (direct-wrap / copy-out / pure value) | shipped, see `06-musiclibraryrequest-re-enablement.md` exit criteria | (uncommitted, awaiting review) |

Notes on 6's exit criteria:
- `MusicLibraryRequest<T>` 11-surface emission: verified on the regen — filter ×7 + filter(text:) + response() + limit/offset/includeOnlyDownloadedContent + sort (22 Route C overloads across 7 conformer extensions).
- `MusicLibrarySectionedRequest<SectionType, MusicItemType>`: 0/17 surface members emit. Empirical regen shows 64 cartesian `*CsmExtensions` classes are emitted but empty; all 17 surface methods (`filterItems` ×8, `sortItems` ×1, `filterSections` ×7, `sortSections` ×1, `response` ×1) tombstone with "protocol with associated types used as constraint". Cause: per-method `where SectionType : MusicLibraryRequestable` clauses on a two-PAT-generic-parent type aren't handled by current CSM filter machinery or Route C (`RouteCSortShapeEligibility.cs:72` gates on single-generic-parent). A follow-up session would design multi-generic-parent CSM + Route C extensions. Tracked here, not in roadmap.md, until a consumer asks.
