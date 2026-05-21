# Session 6 — MusicKit `MusicLibraryRequest<T>` full re-enablement

**Status:** code-side complete; doc-sync + verification items outstanding. The original 11-surface re-enablement was split across three sub-sessions — 6 (initial wiring), 6b (CSM filter machinery), 6c (sort + existential admission) — all of which have shipped on `keypath-subsystem`. See "Outstanding after code ship" below for the specific exit-criteria items still unchecked.

The composition session. After it ships, all 11 surface members of `MusicLibraryRequest<T>` emit and pass end-to-end tests. `MusicLibrarySectionedRequest<SectionType, MusicItemType>` is *out of scope* for this session — its 17 surface members tombstone on the two-PAT-generic-parent + per-method `where SectionType : MusicLibraryRequestable` shape, which neither current CSM filter machinery nor Route C (`RouteCSortShapeEligibility.cs:72` single-generic-parent gate) handle; tracked as an architectural follow-up in the Status section below, not in roadmap.md. This is the user-visible deliverable that closes the A-1 deferral from `sdk-0.11.0-remaining.md`.

## Goal

Bind MusicKit. Specifically:

1. Add MusicKit to validation libraries.
2. Re-enable `MusicLibraryRequest<T>` (and structurally similar `MusicLibrarySectionedRequest<T>`) in `AppleFrameworkRegistry` / `MusicKitDatabase.xml`.
3. Verify all 11 surface members emit by composing the work shipped in Sessions 1–5:
   - **7 KeyPath filter overloads** + **1 sort overload** — Sessions 3 (foundation) + 4 (singletons for `Album.LibraryFilter`, `Song.LibraryFilter`, etc.).
   - **`filter(text:)`** (1 sync method) — Session 2 (parent-only sync CSM).
   - **`response() async throws -> MusicLibraryResponse<MusicItemType>`** (1 async method) — Session 5 (parent-only async CSM).
   - **`limit`, `offset`, `includeOnlyDownloadedContent`** (3 properties) — Session 1 (property-drop fix).
4. Smoke-test against real MusicKit (build a minimal app — or BindingTests fixture using a mock of `MusicLibraryRequestable`-conforming types — that exercises filter/sort/response end-to-end).

## Why this session

- Closes the user-visible A-1 deferral.
- Validates that Sessions 1–5 compose correctly. Bugs in any of the five upstream sessions surface here, in one shape, on a real Apple SDK.
- Establishes the binding shape for any future PAT-constrained generic Apple SDK type.

## Dependencies

- **Session 1** (property-drop bug) — for `limit` / `offset` / `includeOnlyDownloadedContent` to emit.
- **Session 2** (parent-only sync CSM) — for `filter(text:)` to emit.
- **Session 3** (KeyPath foundation) — for KeyPath types in marshalling.
- **Session 4** (typed singleton emission) — for `Album.LibraryFilter.title` etc. to be reachable from C#.
- **Session 5** (parent-only async CSM) — for `response()` to emit.

All five must have shipped (committed, baseline ratcheted) before Session 6 begins. The composition test is the regression catcher.

## Pre-flight checklist

Before starting Session 6, confirm:

- `nuke binding-tests` green for all prior sessions' fixtures (`PatParentPlainPropertiesTests`, `PatParentSyncMethodsTests`, `KeyPathFoundationTests`, `KeyPathSingletonTests`, `PatParentAsyncMethodsTests`) on both sim and device.
- `.validation-baseline.json` has been ratcheted after each prior session.
- No open follow-up items from Sessions 1–5 that could regress mid-Session-6.

## Session 6 work breakdown

### Phase 6.1 — Add MusicKit to the binding set

- `build/validation-libraries.json` — add MusicKit entry. Tier 1 or 2 depending on how the file's tiering is organised; inspect existing Apple-framework entries for the convention.
- `src/Swift.Runtime/src/Swift/apple-frameworks.json` — register `MusicKit` per `AppleFrameworkRegistry` rules (constraint #31). Determine whether `MusicKit` is AutoBridge or OptionalFallback by inspecting the framework's ObjC/Swift surface ratio.
- `src/Swift.Runtime/src/Swift/MusicKitDatabase.xml` (new file or extend existing) — register any Apple value-type remaps needed (constraint #32 — `kind="struct"`, not enum).

### Phase 6.2 — Regen baseline

Run the generator against MusicKit. The first pass output will be incomplete (or wrong, or both). Inspect:

- `MusicLibraryRequest<T>` C# class shape — confirm class exists, generic param `T` correctly constrained to `IMusicLibraryRequestable` (or whatever the projected protocol is).
- All 11 members enumerated in the pre-image swiftinterface (extract from `xcrun --show-sdk-path/.../MusicKit.framework/Modules/MusicKit.swiftmodule/...swiftinterface`).
- Map each pre-image member to its expected emission shape:
  - 3 properties → plain C# auto-property pattern via PropertyHandler (Session 1).
  - `filter(text:)` → C# extension method on `MusicLibraryRequest<Album>` etc. per closed conformer (Session 2).
  - 7 KeyPath filter overloads + sort → C# extension method per (closed conformer × overload) where the KeyPath param is `KeyPath<Album.LibraryFilter, …>` etc. (Sessions 3 + 4 — singletons for the bag, foundation for the param).
  - `response()` → C# `async Task<MusicLibraryResponse<Album>>` extension method per closed conformer (Session 5 + closure for `MusicLibraryResponse<T>` projection — likely already exists for closed `T`).

### Phase 6.3 — Closed conformer enumeration

MusicKit's `MusicLibraryRequestable` has these public conformers (from swiftinterface; reconfirm at session time):
- `Album`
- `Artist`
- `Genre`
- `MusicVideo`
- `Playlist`
- `RadioShow`
- `RecordLabel`
- `Song`
- `Station`
(9 conformers, per `00-overview.md`.)

Each gets:
- `AlbumLibraryFilterKeyPaths` (or whatever Session 4's naming settled on) with one typed singleton per stored property of `Album.LibraryFilter`.
- Similarly for `AlbumLibrarySortPropertiesKeyPaths`.

Total typed singletons emitted: 9 conformers × ~10 filter properties × 1 sort key set = on the order of ~100 `static readonly KeyPath` fields. Confirm count post-regen.

### Phase 6.4 — Hand-verify generated output

For each closed conformer (start with `Album`, the most-common pattern), inspect the generated `.cs` and confirm:

- `MusicLibraryRequest<Album>` extension methods for all 11 surface members.
- No tombstone comments (those should be absent now — every member emits successfully).
- The `KeyPath`-taking filter methods accept `KeyPath<Album.LibraryFilter, String>` etc. — not the open `KeyPath<MusicItemType.LibraryFilter, …>` form.
- The `response() async` returns `Task<MusicLibraryResponse<Album>>` (closed substitution).
- The `filter(text:)` is on `MusicLibraryRequest<Album>` (not the open generic).
- Properties (`limit`, `offset`, `includeOnlyDownloadedContent`) are present as plain C# properties.

If any of these fail: the failure traces back to one of Sessions 1–5. Open a hot-fix follow-up session before proceeding (don't paper over).

### Phase 6.5 — BindingTests fixture

Two paths:

**Path A (mocking)** — replicate the `MusicLibraryRequest<T>` shape in BindingTests without actually depending on MusicKit. A `MockMusicLibraryRequest<Item: MockMusicLibraryRequestable>` struct that mirrors the 11-member surface. Pros: doesn't need a real MusicKit-capable test environment, runs deterministically. Cons: doesn't verify the *real* MusicKit binding.

**Path B (real MusicKit smoke)** — extend `swift-dotnet-packages` or the smoke-test apps in BindingTests to actually instantiate a `MusicLibraryRequest<Album>`, set `limit = 10`, call `filter(matching: AlbumLibraryFilterKeyPaths.Title, contains: "love")`, and call `response()` against an actual user library. Pros: real-world validation. Cons: needs Music auth, a populated library, and a physical device for some surfaces.

**Recommendation: ship Path A in this session, defer Path B to the regression-validation skill flow.** Path A is sufficient for the BindingTests gate. Path B becomes part of the pre-release regression sweep (`regression-validation` skill — see `apple-framework-portfolio.md` for the smoke-app pattern).

Path A fixture: `BindingTests/Sources/SwiftBindingsTestLib/MusicKit/MockMusicLibraryRequest.swift` — exact replica of the surface shape. C# test in `BindingTests/RuntimeTestsApp/MusicKit/MockMusicLibraryRequestTests.cs` exercising every member.

### Phase 6.6 — Validate sweep

Run `nuke validate`. Baseline expected to **ratchet up** on three axes:
- New MusicKit entry: `cs_compile` count increases by however many MusicKit types now bind.
- `swift_compile` correspondingly.
- No existing libs regress.

Per `feedback_no_redundant_validate_rerun.md`: one validate run, accept the baseline write, don't rerun.

### Phase 6.7 — Update docs

- `src/docs/sdk-0.11.0-remaining.md` — mark A-1 closed. (Or whichever the active release doc is at Session 6 time.)
- `src/docs/keypath-subsystem/00-overview.md` — status section updated.
- Public wiki — `Known Limitations` no longer lists MusicLibraryRequest. (Per `MEMORY.md`, the wiki lives at `/Users/wojo/Dev/swift-dotnet-packages.wiki`; coordinate the wiki edit with the wiki repo's normal flow.)
- This file — mark exit criteria checked off.

## Validation gates

| Gate | Expected |
|---|---|
| `nuke test` | Baseline holds |
| `nuke binding-tests --sim` | All prior fixtures + `MockMusicLibraryRequestTests` pass |
| `nuke binding-tests --device` | Same |
| `nuke validate` | Ratchet up; MusicKit now in baseline |
| Hand-inspect generated MusicKit `.cs` | All 11 surface members present per closed conformer |

## Exit criteria

Current state after the 6c code ship (`62ec673e`) and the follow-up doc-sync pass. `[x]` = verified; `[~]` = closed with caveat (see notes); `[ ]` = still outstanding.

- `[x]` MusicKit in `build/validation-libraries.json`; `nuke validate --filter MusicKit` ok across ios/macos/maccatalyst/tvos.
- `[x]` All 11 surface members of `MusicLibraryRequest<T>` emit (filter ×7, sort ×N per bag, response, filter(text:), limit/offset/includeOnlyDownloadedContent). Spot-checked: 22 Route C Sort overloads across 7 conformer extensions in regenerated `MusicKit.cs`.
- `[~]` `MusicLibrarySectionedRequest<SectionType, MusicItemType>` parity — **0/17 surface members emit** (empirical, regen inspected). Generator emits 64 empty `MusicLibrarySectionedRequest{Section}{Item}CsmExtensions` cartesian classes plus 17 tombstone comments: `filterItems` ×8, `sortItems` ×1, `filterSections` ×7, `sortSections` ×1, `response` ×1 — all reasoned "protocol with associated types used as constraint". Root cause: per-method `where SectionType : MusicLibraryRequestable` clauses combined with two PAT-constrained parent generics aren't handled by current CSM filter machinery or Route C (`RouteCSortShapeEligibility.cs:72` gates on single-generic-parent). Full parity is a multi-generic-parent CSM follow-up; not promoted to roadmap until a consumer asks.
- `[x]` Mock/MusicKit fixture passes on sim (`nuke binding-tests --skip-regen`: 2202 pass vs 2201 baseline).
- `[x]` Device gate (`nuke binding-tests --device`). First post-6c device run surfaced 3 crashes in `CsmKeyPathParamTests` — root-caused to a CSM emitter double-free: the `FromX()` factory was emitting `Marshal.AllocHGlobal` + `try/finally { Marshal.FreeHGlobal(resultPtr) }` around `SwiftMarshal.MarshalFromSwift<T>(resultPtr)`, but for non-frozen-struct conformers `MarshalFromSwift` wraps the pointer in a `SwiftSafeHandle` (doesn't copy), so the explicit `FreeHGlobal` raced the wrapper's later `NativeMemory.Free` on `Dispose()` → "pointer being freed was not allocated" (SIGABRT). Fixed in `ConcreteProtocolSpecializationEmitter.cs:1402-1440` by discriminating cleanup by the return type's `NewFromPayload` contract (resolved from the parent's `TypeRecord` for struct ctors, the conformer's `TypeRecord` for `returnsGenericParam` returns, or the substituted return spec otherwise): (1) **direct-wrap** (non-frozen struct, complex enum) → `NativeMemory.Alloc` + no `try/finally` since the returned SafeHandle owns the buffer (matches its `NativeMemory.Free`); (2) **copy-out** (`IsFrozenStructProjectedAsClass` = frozen + `RequiresMemoryManagement`) → `Marshal.AllocHGlobal` + `try/finally{FreeHGlobal}` + `SwiftMarshal.DestroyWireBufferRetains<T>` before the free, so the wire's `+1` retains on internal refs don't leak; (3) **pure value** (frozen, no `RequiresMemoryManagement`) → `Marshal.AllocHGlobal` + `try/finally{FreeHGlobal}` (no wire-destroy needed; copy is a byte copy with no retain semantics). Post-fix: 0 crashes, +14 over baseline (2213 → 2227); ValidateAndReturnTagged on `TaggedSearchItem` (non-frozen-struct conformer through `returnsGenericParam`) also recovers (+1 on sim, 2201 → 2202). Soundness: when the conformer's `TypeRecord` can't be resolved, the discriminator defers to `ClassifyConformerForCSharp` (same source preflight uses to admit the pairing) — `NonFrozenStruct` → ownership-transfer, everything else → legacy alloc+free — so frozen-with-memory cross-module conformers don't silently leak through an over-broad direct-wrap default. Two latent follow-ups remain: (a) `MethodGenericBridgeEmitter.cs` lines 731/741/834-836/861 has the same alloc+free antipattern but isn't exercised by current tests (no method-generic-bridge test exercises a wrap-and-dispose round trip); (b) class-conformer `returnsGenericParam` returns route through `MarshalFromSwift<Class>(resultPtr)`, where Swift writes the class pointer into `resultPtr` via `initializeMemory` but the C# `NewFromPayload(handle)` wraps the *carrier* address — pre-existing, unchanged by this fix, no current test exercises it. Both tracked here for the next session that touches CSM result-pointer marshalling.
- `[~]` A-1 deferral. No `src/docs/sdk-0.11.0-remaining.md` exists in the tree; nothing in `src/docs/` outside `keypath-subsystem/` mentions an A-1 deferral. The deferral doc was retired before 0.11.0 shipped. No edit required.
- `[~]` Public wiki. Current `Known-Limitations.md` (wiki repo `/Users/wojo/Dev/swift-dotnet-bindings.wiki` — last commit `0b393e4`) contains no "no sort for `MusicLibraryRequest`-style PAT generics" caveat to retract. MusicKit appears in `Apple-Frameworks.md`, `FAQ.md`, and `Supported-Features.md` as a fully-bound framework. No edit required.
- `[x]` `00-overview.md` status section refreshed; per-session shipped-at commits + `MusicLibrarySectionedRequest` architectural-exclusion note recorded.
- `[~]` Validate baseline (`.validation-baseline.json`) — not re-ratcheted via a full `nuke validate` after Route C. Per `feedback_validate_is_opt_in.md` this is opt-in. `nuke validate --filter MusicKit` is the verified gate; the full sweep stays deferred unless cross-cutting regression suspected.

## Risks specific to Session 6

- **Risk A (closed conformer enumeration mismatch)** — if `MusicLibraryRequestable`'s public conformers in iOS 26.2 differ from `00-overview.md`'s list (Apple SDK drift), some conformers may be missing or new ones present. **Diagnostic:** extract the conformer list from `MusicKit.swiftinterface` at Session 6 time, compare to expected; reconcile.
- **Risk B (KeyPath singleton container naming collision)** — if `Album.LibraryFilter` and some unrelated type both project to a container named `AlbumLibraryFilterKeyPaths`, generation fails. Session 4's symbol-collision diagnostic should catch this, but verify post-regen.
- **Risk C (`MusicLibraryResponse<T>` projection)** — the `response()` return type is `MusicLibraryResponse<MusicItemType>`. If `MusicLibraryResponse<T>` itself binds incorrectly (e.g. its properties suppressed for unrelated reasons), the async return projects as an unusable wrapper. **Diagnostic:** before relying on `response()`, hand-verify `MusicLibraryResponse<Album>` shape.
- **Risk D (real MusicKit smoke gap)** — Path A in Phase 6.5 doesn't validate against real Apple frameworks. A subtle ABI bug (e.g. mismatched calling convention on a method that only exists in real MusicKit, not the mock) ships undetected. **Mitigation:** explicit follow-up entry on the next session's exit checklist to run Path B via the `regression-validation` skill before the SDK ships.
- **Risk E (filter/sort overload disambiguation)** — `MusicLibraryRequest<Album>.filter` has 7 KeyPath-taking overloads differing in the comparison kind (`equalTo:`, `contains:`, `lessThan:`, `memberOf:`, etc.) and the Value generic constraint. Method-overload disambiguation across these (constraint #16 — `DuplicateSignature` / overload-key consistency) must hold. **Diagnostic:** count emitted C# overloads, expect exactly 7 + 1 sort per conformer.
- **Risk F (open associated-type-rooted KeyPath leaks)** — the doc design decision excludes open associated-type-rooted KeyPath. Verify by regen: no method on `MusicLibraryRequest<Album>` accepts `KeyPath<MusicItemType.LibraryFilter, …>` — only `KeyPath<Album.LibraryFilter, …>` after closed substitution. If the engine emits the open form, it's a Session 4 bug; hot-fix before completing Session 6.

## References

- `00-overview.md` — scope and design
- `src/docs/sdk-0.11.0-remaining.md` — A-1 deferral
- Sessions 1, 2, 3, 4, 5 (all five are prerequisites)
- `apple-frameworks.json` registration (constraint #31)
- `MEMORY.md` — wiki repo path for documentation update
