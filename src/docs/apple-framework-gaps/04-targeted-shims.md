# Session 04 — Targeted member shims & SwiftUI bridge

The cleanup sweep: independent bounded fixes, each a single broken/missing member on one shipping framework. They don't share a subsystem, but they share a *size and shape* — small-to-medium, single-framework impact, each unblocks one specific user-visible API. **Lead with the ProximityReader regen spike** to resolve the campaign's one open unknown.

## Why grouped

Each fix is independent and short, but bundling them into one session amortizes review cycles (one paired Codex+Grok end-of-session pass) and the binding-tests / regen overhead. Forcing them into Sessions 01-03 would break those sessions' subsystem cohesion more than it gains.

## Task order

1. **ProximityReader spike + fix** — first, because the spike either confirms a ≤3-file fix or surfaces something bigger; resolve the unknown before committing to the rest of the session shape.
2. **MusicKit `init(term:types:)` array-shim** — mechanical Swift wrapper once scoped.
3. **FamilyActivityPicker via `Binding<Codable Struct>`** — the largest single task; build on the existing SwiftUI bridge.
4. **TipKit guide-accuracy correction** — free rider, zero code cost.

---

### Task 1 — RC‑CDECL‑PARITY (ProximityReader, spike-then-fix)

**Bug.** `MobileDocumentReaderError.GetErrorDescription()` throws `EntryPointNotFoundException` at runtime (`apple-frameworks/ProximityReader/obj/.../ProximityReader.cs:15926`). The C# `[LibraryImport]` is emitted but the Swift `@_cdecl` wrapper is missing — the C# and Swift sides use *different* eligibility checks for a computed property on an enum conforming to `LocalizedError`. The working `FamilyControlsError` takes the atomic simple-enum path (`EnumHandler.SimpleEnum.cs`, both sides paired); `MobileDocumentReaderError` diverges, and §6 research couldn't pin which side without running the generator.

**Step 1 — Spike.** Regen ProximityReader:

```bash
dotnet run --project src/Swift.Bindings/src -- \
  --xcframework <path>/ProximityReader.xcframework \
  -o <out-dir>/
```

Diff the emitted Swift wrapper against the C# `[LibraryImport]` for `GetErrorDescription`. Identify which side drops:
- `PropertyWrapperEmitter.ShouldEmitWrapper` (Swift-wrapper side) — does it emit the `@_cdecl`?
- `WrapperValidation.CanEmitMember` (C# side) — does it permit the `[LibraryImport]`?
- Or some other eligibility gate.

**Step 2 — Fix.** Bring the dropping side into parity with the working `FamilyControlsError` simple-enum path. Expected ≤3 emitter files.

**Categorical check before patching path-by-path.** If the spike surfaces a category — i.e. multiple `LocalizedError`-conforming enums across shipping frameworks all diverge in the same way — enumerate via an Explore agent and fix in one pass (`feedback_no_session_cascade.md`). Don't re-scope silently; if the category is clearly its own session, drop this task from Session 04 and reschedule with the actual evidence.

**Out of scope here:** ProximityReader's *other* gap, `requestDocument` (RC‑PAT, `ProximityReader.cs:16078`). It's likely app-defined-conformer / source-gen territory; **verify the conformer source** (Apple-finite vs consumer-authored) before any future work on it.

**Tests.**
- Unit: a `LocalizedError`-conforming enum with computed `errorDescription` emits both the C# `[LibraryImport]` and the Swift `@_cdecl` (parity assertion).
- BindingTests: round-trip a `LocalizedError`-conforming enum's `errorDescription` both directions (catches the parity bug categorically).
- After ProximityReader regen: `MobileDocumentReaderError.GetErrorDescription()` no longer throws (sim + device).

---

### Task 2 — RC‑MISSING (MusicKit `MusicCatalogSearchRequest.init(term:types:)`) — IMPLEMENTED

**Bug.** Not constructible from C#. Actual ABI shape (per `binding-report.json`) is `init(term: String, types: [any MusicCatalogSearchable.Type])` — an array of existential metatypes, which the generator skips as `UnsupportedExistential` (not the `repeat each MusicType` parameter pack the original description suggested). The same shape applies to two sibling inits: `MusicLibrarySearchRequest.init(term:types:)` and `MusicCatalogSearchSuggestionsRequest.init(term:includingTopResultsOfTypes:)`.

**Fix shipped.** Per-framework wrapper-Swift directory shim, NOT the cross-cutting AppleSupplement. Paired Codex + Grok consult converged on the same placement (per-framework) and encoding (`[Flags] UInt32` bitmask decoded inside the Swift `@_cdecl`) — the AppleSupplement is structurally wrong here because its charter forbids new `-framework` link lines (Build.BindingTests.cs:838-841) and it is referenced unconditionally by every SwiftBindings.Apple consumer.

Files landed in `swift-dotnet-packages/apple-frameworks/MusicKit/`:

- `Shims/MusicKitShims.swift` — three `@_cdecl` trampolines (`SBW_MusicKitShims_{CatalogSearchRequest,LibrarySearchRequest,CatalogSearchSuggestionsRequest}_Init`) taking `(utf8Ptr, utf8Len, mask: UInt32, outBuffer)` and returning `Int32` (0 success, -1 unknown bits). Bit-to-type tables are append-only ABI; later-OS conformers (Curator/MusicVideo/RadioShow at iOS 15.4) are wrapped in `if #available` blocks inside the decoder so the iOS 15.0 baseline still loads the symbol.
- `Shims/MusicKitShims.cs` — two public `[Flags] enum`s (`MusicCatalogSearchTypes`, `MusicLibrarySearchTypes`) and three `public partial class` `Create(string term, ...)` static factories. Pattern matches the AttributedString reference: `SwiftObjectHelper<T>.GetTypeMetadata()` → `NativeMemory.Alloc(metadata.Size)` → shim writes via `assumingMemoryBound(to:).initialize(to:)` → `new T(new SwiftHandle((IntPtr)heap))` so `SwiftSafeHandle` owns the slot and `ValueWitnessTable->Destroy` runs on Dispose. Unknown bits → `ArgumentException`.
- `SwiftBindings.Apple.MusicKit.csproj` — `<Target Name="_StageMusicKitShims" BeforeTargets="_ComputeSwiftFingerprint">` stages `Shims/MusicKitShims.swift` → `$(_SwiftBindingIntermediateDir)MusicKitShims.Wrapper.swift` for the SDK second-slice find pickup, maintains a sidecar `.sha256`, and deletes `swift-binding.stamp` on hash mismatch (the SDK fingerprint does not cover user-provided shim sources, so the stamp would otherwise stay valid across shim edits).
- `tests/Tests.cs` — Tests 38–40 exercise factory success, unknown-bits guard, and the suggestions variant.

**Validation.** Shim Swift compiles clean and the partial-class C# compiles clean. End-to-end binding-package build is blocked by an *orthogonal*, pre-existing `SwiftBindings.Sdk/0.11.2` generator regression where `MusicItemProxy` is referenced by downstream `RunClassConstructor(typeof(MusicItemProxy).TypeHandle)` ancestor-ordering calls but never emitted (the marker-protocol skip incorrectly elides the proxy class for `MusicItem : Sendable` even though it carries a real `id: MusicItemID` requirement). Out of scope for this campaign — file separately.

**Why not promote to a generator feature.** Codex/Grok analysis: the gap is `[any P.Type]` (existential metatype array), not a parameter pack. Apple-closed conformer sets per protocol (~5–9 each) make a stable bitmask the right encoding. The same pattern applies cleanly to any future `[any P.Type]` constructor in other frameworks with zero generator work and no supplement coupling. Promoting to an emitter feature would require existential-metatype-array introspection (a much wider scope) without solving more than this surface.

**Lands in:** MusicKit per-framework wrapper-Swift directory (`swift-dotnet-packages/apple-frameworks/MusicKit/Shims/`). NOT in `SwiftBindings.Apple` supplement.

**Tests.**
- Per-package smoke tests: Tests 38–40 in `apple-frameworks/MusicKit/tests/Tests.cs` cover the happy path, the unknown-bits guard, and the suggestions ctor.
- BindingTests: heap-allocated value-type ownership shape (`NativeMemory.Alloc` + VWT initialize + private `T(SwiftHandle)` ctor + VWT destroy on Dispose) is already covered end-to-end by the AppleSupplement AttributedString shim, so the MusicKit shim does not duplicate it.

---

### Task 3 — RC‑SWIFTUI (FamilyActivityPicker via `Binding<Codable Struct>`) — IMPLEMENTED

**Bug.** `FamilyActivityPicker` not presentable from C# (`apple-frameworks/FamilyControls/obj/.../FamilyControls.cs:1110`). The SwiftUI bridge already emits create/get/free `@_cdecl` symbols around `UIHostingController` and returns a typed `UIViewController` (`SwiftUIBridgeEmitter.cs:2342`, `:700`). It binds primitives, strings, enums, classes, and some `Binding<T>` — it did NOT bind non-optional `Binding<Struct>`. FamilyActivityPicker becomes pure once that lands (per `src/docs/Design/apple-framework-portfolio.md:169`).

**Fix shipped.** Extended the SwiftUI bridge to recognise `Binding<CodableStruct>` (non-frozen, non-generic, non-module-internal struct conforming to both Encodable and Decodable — the same gate as `CodableJsonEmitter.ShouldEmit`). The struct is ferried across the boundary as JSON UTF-8 (ptr + length):

- **Mapping (`SwiftUIBridgeEmitter.InitAnalyzer.cs`).** `MapBindingType` accepts a `BoundStruct` inner whose `StructProjection == NonFrozen` when `IsCodableStructForBinding` passes; sets `IsBinding = true, IsBindingCodableStruct = true` on the resulting `BridgeParameter`. Gate mirrors `CodableJsonEmitter.ShouldEmit` exactly so the generated C# wrapper is guaranteed to carry `EncodeToJson()` / `static DecodeFromJson(byte[])`.
- **Swift bridge.** Create / Update take `(utf8Ptr, utf8Len)`; the bridge decodes via `JSONDecoder().decode(T.self, from: Data(...))` and stores the value on `@Published var <name>: T` so SwiftUI's `$state.<name>` projection works unchanged. A per-param `SBW_<Module>_<View>_Read<Param>Json(handle, outLen) -> UnsafeMutablePointer<UInt8>?` @_cdecl exposes the current value back to C#; a per-view `SBW_<Module>_<View>_FreeJsonBuffer(ptr)` @_cdecl deallocates it. Both run on `SBW_onMainThread`.
- **C# bridge.** Create / Update pin the byte[] (`fixed (byte* p = bytes)`) and pass `(IntPtr)p, bytes.Length`. `Read<Param>() -> T` calls the native reader, copies the bytes out, calls `T.DecodeFromJson(bytes)`, and unconditionally invokes `FreeJsonBuffer` in a `finally`.

**Files touched.** `src/Swift.Bindings/src/Emitter/StringEmitter/SwiftUIBridgeEmitter.{cs,InitAnalyzer.cs}` (mapping + emission), plus tests.

**Lands in:** `src/Swift.Bindings/src/Emitter/StringEmitter/SwiftUIBridgeEmitter.cs` (and `.InitAnalyzer.cs`).

**Tests.**
- Unit: `BindingCodableStruct_MapParameterType_FlagsIsBindingCodableStruct` + `BindingNonCodableStruct_MapParameterType_DoesNotFlagCodable` in `SwiftUIBridgeEmitterTests.cs` assert the mapper flags Codable structs and rejects plain structs.
- BindingTests: `CodableProfileEditorView` (non-frozen `CodableProfile: Codable, Equatable` with `String` + `Int32`) + `TestCodableProfileEditorView_{CreateRoundTrip,UpdateRoundTrip}` in `ValidationPatternBridgeTests.cs` cover the C#-driven round-trip — Create with a seed value then Read returns the seed; Update with a new value then Read reflects the update. Sim **and** device gates green (+4 on each; device baseline 2448 → 2452).
- After FamilyControls regen: `FamilyActivityPicker(selection:)` becomes presentable from C# in consumer packages.

**Tested vs. untested.** The shipped tests exercise the C#-driven path: `C# → JSON UTF-8 → Swift JSONDecoder → @Published state → SwiftUI `$state` projection`, plus the reverse `C# Read → JSONEncoder → buffer → C# DecodeFromJson`. They do **not** programmatically observe a bridge-internal mutation (e.g. a child SwiftUI view writing through `$profile.wrappedValue`) propagating back to C#. Architecturally, **`UpdateProfile` and any SwiftUI-internal mutation land on the same `@Published var profile` setter** — the setter is the unit of correctness, and the C#-driven round-trip exercises it end-to-end (write via decode, observe via encode). What is genuinely missing is an in-process UI-driven trigger (button tap inside the view body writing `$profile.wrappedValue`); that requires XCUI-level test infrastructure BindingTests does not host. For FamilyActivityPicker, the picker's SwiftUI-internal selection mutations land on `@Published var profile`; C# observes them via `Read<Param>()`. If a real consumer surfaces flakiness, the next step is a fixture in `swift-dotnet-packages` that mounts the picker via UIHostingController and round-trips a programmatic selection — out of scope for this session.

---

### Task 4 — TipKit guide-accuracy correction (free rider)

Update `apple-frameworks/TipKit/TIPKIT-GUIDE.md` in `swift-dotnet-packages`:

- `ITip.ShouldDisplay` (`TipKit.cs:9801`) and `Options` (`:9766`) **do** dispatch via real witness-table thunks. They are *not* in the throwing-default set as the guide currently states.
- Only `Status` / `Invalidate` (and the SwiftUI-typed members) throw.

**Hold the `swift-dotnet-packages` commit** until the new SDK is published per `feedback_no_commit_packages.md`. Stage the doc edit in the working copy.

---

## Frameworks unblocked

- **ProximityReader (🔴 → mostly 🟢):** `GetErrorDescription()` works. The remaining `requestDocument` RC‑PAT is out of scope for this campaign (likely source-gen territory).
- **MusicKit (🟠 → close to 🟢):** `MusicCatalogSearchRequest` constructible via the array-shim.
- **FamilyControls (🟢 → fully clean):** picker presentable from C#.

## Consult points

- **Codex** on Task 3 (the `Binding<Struct>` bridge extension — the only real design question in this session). Ask: "The SwiftUI bridge emits create/get/free `@_cdecl` symbols around `UIHostingController` and currently handles primitives, strings, enums, classes, and some `Binding<T>` patterns. To support non-optional `Binding<Codable Struct>`, what's the minimal extension that: (a) doesn't reentrancy-corrupt SwiftUI's binding propagation, (b) handles the C# value/reference-type boundary cleanly, (c) plays nicely with `@available` propagation? Inspect `SwiftUIBridgeEmitter.cs` for the existing `Binding<T>` paths and propose the minimal extension." Pair with your own read.
- **Grok** for the ProximityReader spike — have it independently propose where the C#/Swift eligibility could diverge for `LocalizedError`-conforming enums, so the spike isn't anchored to one hypothesis. Also: categorical sweep across shipping frameworks for any `LocalizedError`-conforming enum that today throws on a computed property.
- **End-of-session paired review.**

## Test gate

- **ProximityReader fix**: sim **+ device** (calling-convention parity).
- **MusicKit shim**: sim sufficient (pure Swift wrapper signature change).
- **FamilyActivityPicker bridge extension**: **device** (SwiftUI lifecycle / `UIViewController` presentation, plus any NativeAOT differences in the bridge marshalling).

Per `CLAUDE.md`, `nuke binding-tests --skip-regen` is enough after the C# bridge change if bindings haven't changed; do a full regen for ProximityReader after the parity fix and FamilyControls after the bridge extension.

## Risks / re-scope triggers

- **ProximityReader spike surfaces a categorical `LocalizedError` divergence** across multiple shipping frameworks → enumerate via Explore and fix categorically within this session **if** the count is small (≤3 sites). If clearly its own session-scale work, drop the fix from this session, document the spike's findings as the deliverable, and reschedule. Don't silently expand.
- **`Binding<Codable Struct>` discovers SwiftUI lifecycle issues** (reentrancy, value-vs-reference identity in the binding) → land ProximityReader + MusicKit + TipKit doc-fix as this session and defer FamilyActivityPicker with the actual evidence.
- **MusicKit variadic-pack pattern shows up in ≥2 other shipping APIs** during the Grok sweep → promote to an emitter feature, but treat that promotion as its own scope (don't bolt onto this session).

## References

- `src/docs/apple-framework-binding-gaps.md` §6b (RC‑CDECL‑PARITY, RC‑MISSING MusicKit, RC‑SWIFTUI FamilyActivityPicker detail).
- `src/Swift.Bindings/src/Emitter/StringEmitter/SwiftUIBridgeEmitter.cs:700, 2342` — existing bridge entry points.
- `src/Swift.Bindings/src/Emitter/StringEmitter/EnumHandler.SimpleEnum.cs` — the `LocalizedError` simple-enum path that works for `FamilyControlsError`; reference for the parity fix.
- `src/Swift.Bindings/src/Emitter/StringEmitter/PropertyWrapperEmitter.cs` — Swift-side wrapper emission; one of the two candidates for the divergent gate.
- `src/Swift.Bindings/src/Emitter/StringEmitter/WrapperValidation.cs` — C#-side validation; the other candidate.
- `src/docs/Design/apple-framework-portfolio.md:169` — FamilyActivityPicker "becomes pure once `Binding<Codable Struct>` lands" entry.
- Memory: `feedback_no_session_cascade.md`, `feedback_no_commit_packages.md`, `feedback_codex_design_partner.md`.
