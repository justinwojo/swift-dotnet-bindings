# Roadmap

**Updated**: March 16, 2026

---

## Coverage Baseline (March 16, 2026)

Measured across 90 validation targets (56 libraries). This is the starting point — each session below projects its impact against these numbers.

| Metric | Count | % |
|--------|------:|--:|
| Total Swift types | 2,584 | |
| Types emitted | 2,174 | 84% |
| Total Swift members | 14,109 | |
| Members emitted (usable) | 9,521 | 67% |
| Members skipped | 4,588 | 33% |

Of the 9,521 emitted members:
- **6,320 (66%)** use safe `@_cdecl` wrappers (works on all runtimes)
- **2,691 (28%)** use `CallConvSwift` (SB0001 — NativeAOT only, Mono JIT crashes)
- **549 (6%)** are `SB0003` stubs (protocol members that throw `NotSupportedException` at runtime)

**Projected cumulative coverage after sessions 1–4: 67% → ~80%** (~1,900 members recovered)

---

## Prioritized Sessions

### Session 0: TestFramework Generator Bug Fixes

**Coverage impact**: Minimal skip recovery — unblocks already-written test patterns (M3, Q2, X1, S1).
**Effort**: Small (targeted fixes, not architectural)

Generator bugs discovered during TestFramework Pass 2 testing. Swift source and C# tests are already written — these fixes turn them green.

| Sub-task | Affects | Effort | Notes |
|----------|---------|--------|-------|
| **SBW_Free in generic classes** (CS7042) | M3, Q2 patterns | Small | `SBW_Free` `[LibraryImport]` is emitted inside the generic class body. The .NET `LibraryImportGenerator` generates `[DllImport]` inside the generic type, which is illegal. Fix: move `SBW_Free` to the `_PInvoke` companion class (non-generic static class). Unlocks generic class + protocol conformance and generic class inheritance. |
| **Payload property collision on generic subclass** (CS0108) | Q2 pattern | Small | When generic `TypedEntity<T> : BaseEntity`, both emit `public ... Payload => _handle;`. The child's `Payload` type is `SwiftClassHandle<TypedEntity<T>>` while parent's is `SwiftClassHandle<BaseEntity>`, causing CS0108 hide warning treated as error. Fix: emit `new` keyword on derived class `Payload`, or suppress. Non-generic inheritance (`Dog : Animal`) correctly skips re-emission. |
| **Failable init on non-frozen struct** (CS8625) | S1 patterns | Small | `TryCreate` emits `result = default;` for the `None` case. For non-frozen structs (emitted as `class` in C#), `default` is null, violating the non-nullable `out` parameter. Fix: use `default!` or `Unsafe.NullRef<T>()`. Frozen structs (emitted as `struct`) are unaffected — `default` is valid for value types. |
| **SwiftAsyncStream\<int\> ISwiftObject constraint** (CS0315) | X1 pattern | Small | `int` doesn't satisfy `where TElement : ISwiftObject` on `SwiftAsyncStream<T>`. Fix: relax constraint or add blittable primitive specializations. Unlocks `AsyncStream` with primitive element types (e.g., Nuke `ImageTask.progress`). |

**Key source locations for each fix:**
- SBW_Free emission: `PropertyHandler.cs:564`, `MethodHandler.cs:891` — emits inside class body for string-returning members
- TryCreate `result = default`: `WrapperEmitter.FailableFactory.cs:108`
- SwiftAsyncStream constraint: `src/Swift.Runtime/src/Swift/SwiftAsyncStream.cs:28`
- Payload emission: `ClassHandler.cs:372`

**Reproduction**: Run `cd TestFramework && ./build-and-test.sh` after re-enabling the commented-out Swift source in `Generics/Types.swift` (search for "NOTE: Removed" comments for M3 and Q2) or `Initializers/Failable.swift` (re-add `NonEmptyString`). The CS errors appear at the compile-check step.

**After fixing, re-enable:**

| Fix | Swift source to restore | C# tests to write/update |
|-----|------------------------|--------------------------|
| SBW_Free (M3) | `Generics/Types.swift:99` — uncomment `GenericNamedBox<T>: Named` class (~10 lines) | Write tests in `Generics/BasicGenericTests.cs` for GenericNamedBox construction + Name property. Likely `[MonoJitCrash]` (CallConvSwift on generic class). |
| SBW_Free + Payload (Q2) | `Generics/Types.swift:117` — uncomment `BaseEntity` + `TypedEntity<T>: BaseEntity` (~20 lines). Rename `payload` → anything except `payload` to avoid Payload collision. | Write tests in `Generics/BasicGenericTests.cs` for TypedEntity construction + inherited property access. Likely `[MonoJitCrash]`. |
| Failable init (S1) | `Initializers/Failable.swift:26` — re-add `NonEmptyString` struct with `init?(_ string: String)` | Write tests in `ErrorHandling/ThrowingMethodTests.cs` for `NonEmptyString.TryCreate("hello")` success + `TryCreate("")` failure. Likely `[MonoJitCrash]` (CallConvSwift TryCreate). |
| AsyncStream (X1) | `Async/AsyncProperties.swift:79` — change `AsyncStream<String>` back to `AsyncStream<Int32>` (or add a second `Int32` property alongside). | Write test in `Async/` for `AsyncValueSource` int stream iteration. Likely `[MonoJitCrash]` (async). |

**Not a generator bug** (Mono JIT issue, not fixable):
- SwiftArray in enum payload (L1) — `MediaSource.Playlist()` crashes Mono JIT. Works on NativeAOT. Test is marked `[MonoJitCrash]`.

---

### Session 1: Struct & Closure Boundary Expansion

**Coverage impact**: ~480 skips recovered → 67% → ~70%
**Libraries affected**: 33+

The core blocker is that non-primitive frozen structs can't cross the `@_cdecl` wrapper boundary today. The approach (`UnsafeRawPointer` + `pointee`) already exists in the codebase for indirect returns. Once struct params work, several closure sub-shapes are directly unlocked, and Foundation.Data uses the identical pattern.

| Sub-task | Skips | Effort | Notes |
|----------|------:|--------|-------|
| **Non-primitive frozen struct params** | 288 (27 libs) | Medium | Core pattern: pass structs via `UnsafeRawPointer` in `@_cdecl` wrapper, reconstruct via `.load(as: T.self)`. Affects NVActivityIndicatorView (56), Alamofire (33), GRDB (33), CryptoSwift (28), Lottie (24), Nuke (19), Kingfisher (15). |
| **Foundation.Data projection** | — | Medium | Same `UnsafeRawPointer + nint` pattern as `DateProjection`. Blocks `WebSocketEvent.Binary(byte[])` and `WebSocketEvent.Ping(Data?)` in Starscream. |
| **Closures with frozen struct params** | subset of 333 | Medium | Directly unlocked by struct param work. e.g., `(CGRect) -> Void`, `(LottieColor) -> Void`. |
| **Optional\<Primitive/Enum\> in closures** | subset of 333 | Medium | Different ABI from pointer-based Optional. Affects various closure-accepting APIs. |
| **Complex enums in closures** | subset of 333 | Medium | Structural emitter change for enum payloads in closure params. |

**Example APIs unlocked:**
- Methods taking `LottieColor` as param (Lottie)
- Methods taking config structs (Alamofire `URLRequest` wrappers)
- Methods taking crypto operation structs (CryptoSwift)
- `WebSocketEvent.Binary(byte[])` (Starscream)
- Callbacks with `CGRect`, enum, and optional primitive params across 33 libraries

---

### Session 2: Actor Isolation

**Coverage impact**: ~852 skips recovered → ~70% → ~76%
**Libraries affected**: 21

The single largest gap and growing as libraries adopt Swift concurrency. Every UIKit-facing property/method on modern Swift view classes gets `@MainActor` isolation. This session would design and implement async wrapper trampolines for isolated members.

| Sub-task | Effort | Notes |
|----------|--------|-------|
| **@MainActor wrapper trampoline design** | Medium | Emit `@MainActor`-annotated `@_cdecl` wrapper functions that dispatch to the isolated member |
| **Async P/Invoke integration** | Medium | C# side needs to call these via async interop, respecting SafeHandle lifetime constraints |
| **Custom actor support** | Medium | Extend beyond `@MainActor` to library-defined actors (lower frequency) |

**Approach** — emit `@MainActor`-annotated async wrappers:
```swift
@MainActor
@_cdecl("SBW_MyView_title_Get")
func wrapper_title_Get(_ self: UnsafeMutableRawPointer) async -> SBW_Utf8Slice {
    let obj = Unmanaged<MyView>.fromOpaque(self).takeUnretainedValue()
    return obj.title.toUtf8Slice()
}
```

**Affected libraries:** Parchment (160), Lottie (103), StripePaymentsUI (96), Kingfisher (66), PhoneNumberKit (63), FSPagerView (59), AMPopTip (58), Mappedin (40)

**Example APIs unlocked:**
- All properties/methods on `PagingViewController` (Parchment)
- All properties on `LottieAnimationView` (Lottie)
- All UI configuration properties on `NVActivityIndicatorView`
- Essentially all UIKit-facing view class APIs

---

### Session 3: Existential Types & Error Ergonomics

**Coverage impact**: ~300 skips recovered + ergonomics → ~76% → ~78%
**Libraries affected**: 33+

The broadest gap by library count (33 libs). Post-Swift 6, `any Sendable` is pervasive. This session tackles the tractable subset: `any Sendable` as opaque pass-through, `[String: Any]` as dictionary, ExistentialContainer API cleanup, and wrapping `AnyError` in a proper `Exception` type.

| Sub-task | Skips | Effort | Notes |
|----------|------:|--------|-------|
| **`any Sendable` as opaque** | subset of 593 | Medium | Most common existential pattern. Pass as `UnsafeRawPointer`. |
| **`[String: Any]` as dictionary** | subset of 593 | Medium | JSON-like config patterns (Alamofire, Mixpanel). |
| **ExistentialContainer API cleanup** | — | Medium | Remove internal marshalling types from public API surfaces (IntelliSense pollution). See `Future/binding-api-future-work.md` R6. Gated on `AllProtocolsHaveTypeRecords()`. |
| **AnyError → SwiftException** | — | Medium | `SwiftException : Exception` wrapping `AnyError`. C# consumers get standard try/catch instead of opaque error handles. |

**Affected libraries:** StripePayments (236), RxSwift (100), Alamofire (43), GRDB (39), ObjectMapper (25), Mixpanel (22), Lottie (18)

**Example APIs unlocked:**
- `URLEncoding.encode(urlRequest, with: [String: any Sendable]?)` (Alamofire)
- `MixpanelInstance.track(properties: [String: any MixpanelType]?)` (Mixpanel)
- Nearly all ObjectMapper transformation methods

---

### Session 4: Protocol Emission Improvements

**Coverage impact**: ~254 skips recovered → ~78% → ~80%
**Libraries affected**: 16+

Two related improvements to how protocols surface in C#: emitting static members (currently impossible in C# interfaces) and threading conformance declarations across module boundaries.

| Sub-task | Skips | Effort | Notes |
|----------|------:|--------|-------|
| **Static protocol members** | 254 (16 libs) | Medium | Emit companion static class alongside the C# interface. Clear pattern, no unknowns. |
| **Cross-module protocol conformances** | — | Medium | Thread conformance declarations across module boundaries. Enables polymorphic use of types through interfaces defined in other modules. |

---

### Session 5: NativeAOT Device Polish & Misc Fixes

**Coverage impact**: Minimal skip recovery — this is stability and ergonomics work.

NativeAOT device target is met (373 pass, 0 fail, 14/15 libraries). These items increase per-library test coverage within already-passing libraries, plus small generator quality-of-life fixes.

| Sub-task | Affects | Effort | Notes |
|----------|---------|--------|-------|
| Struct singleton second-access crash | Alamofire `URLEncoding.Default` | Unknown | SIGBUS on second access. `initializeMemory` + `deinitialize` may corrupt ARC reference counts. Needs investigation. |
| Enum case dispose crash (cumulative) | Starscream `WebSocketEvent.ViabilityChanged` | Unknown | SIGSEGV after ~3 enum cases created/destroyed. May relate to struct singleton issue. |
| CallConvSwift `URL.init(string:)` wrapper | Starscream WebSocket construction | Small | `MarshalDirectiveException` on NativeAOT. Needs `@_cdecl` wrapper. |
| `ConfigurationValue` property name collision | Nuke readability | Small | Alternative disambiguation strategy. |
| SwiftUI type public construction | Consumer ergonomics | Small | `SwiftUI.Color(red, green, blue)` like `SwiftColor`; current stubs are opaque. |
| Stripe validation config fixes | StripeCryptoOnramp, StripeIssuing | Small | Config-only: add `StripeCameraCore` transitive dep, add `Stripe3DS2` as `--framework-dependency`. |

---

## SwiftUI Bridge (4 remaining sessions)

Active roadmap: `swiftui-roadmap.md`. Sessions 1A–3 + 4A + 4C already cover the vast majority of real-world SwiftUI views. These remaining sessions are diminishing returns — schedule as needed, not as a block.

| Session | Focus | Priority |
|---------|-------|----------|
| **1B** | Closure non-primitive returns (String, class) | Medium |
| **4B** | Constrained generics (`<T: Identifiable>`, `<T: Hashable>`) | Medium |
| **5** | Lifecycle (`onAppear`/`onDisappear`), presentation helpers | Medium-low |
| **6** | Observable binding (C# → Swift reactivity), corpus tracking | Low |

---

## Hard / Deferred

High skip counts but architecturally difficult. Not scheduled unless a specific consumer need drives them.

| Item | Skips | Libraries | Why Deferred |
|------|------:|----------:|-------------|
| **Unsupported signatures** (associated type refs, placeholder types) | 353 | 37 | Requires associated type resolution through conformance graph |
| **Generic type contexts** (generic parent leaks into wrapper) | 349 | 14 | Needs type-erased dispatch for non-final generic class members |
| **Method-level generics** (`func foo<T>(...)`) | 179 | 13 | Requires specialization or type-erased wrappers |
| **Protocol extension associated type context** | — | GRDB | 666 errors contained by gate. Needs full generic constraint context from protocol definition into wrapper signatures. See EC-17. |
| **Architectural generic closures** | ~45 methods | RxSwift, Alamofire | RxSwift `subscribe`/`flatMap`, Alamofire interceptors. Deferred from P3.5. |
| **String enum raw values** | — | GRDB, CryptoSwift | **Blocked** — no data source in compiled xcframeworks |
| **ObjC-bridged optional setters** | 90 | 11 | Setter paths for optional ObjC-bridged types |
| **Unsupported generic containers** | 71 | 20 | `Result<T,E>`, `Optional<existential>` |
| **Async methods** | 28 | 5 | Methods with `async` keyword (partially gated by actor isolation work) |
| **Async properties** | 14 | 5 | Properties with `async get` |
| **inout parameters** | 14 | 2 | `inout` write-back semantics |

---

## Not Worth Addressing

| Skip Reason | Count | Why Not |
|-------------|------:|---------|
| @_spi / internal members | 795 | Correct behavior — private API should not be bound |
| Synthesized Codable | 155 | .NET consumers use own serialization (`System.Text.Json`, etc.) |
| SwiftUI/Combine dependencies | 60 | Framework boundary — consumers use SwiftUI bridge instead |
| Generic protocol constraints / PATs | 68 | Architecturally blocked by associated type erasure — fundamental limitation |
| Unsatisfied ISwiftObject | 104 | Fundamental type system constraint — generic args must be projectable |

---

## Future Vision

Detailed plans in `Future/`. Consolidated priority in `Future/future-roadmap.md`.

| Item | Effort | Design Doc |
|------|--------|------------|
| **Upstream bug reports** (4 issues) | Trivial (filing) | `Future/upstream-bug-reports-draft.md`, `Future/upstream-nativeaot-simulator-issue.md` — blocked on repo going public |
| **Multi-platform support** (macOS, Mac Catalyst, tvOS) | Large (3+ sessions) | `Future/dx-multi-framework-auto-detection.md` |
| **SPM package support** (source → xcframework → bind) | Large | `Future/sdk-future-work.md` |
| **Performance benchmarks** | Medium | `Future/interop-performance-validation-plan.md` |
| **API snapshot tooling** (detect API surface drift) | Medium | `Future/api-snapshot-tooling.md` |
| **Emitter architecture redesign** | Very Large | `Future/emitter-redesign-proposal.md` — right long-term direction, wrong near-term investment |

---

## Runtime

| Item | Effort | Notes |
|------|--------|-------|
| Bulk retain/release helpers | Low-medium | Perf win for large collections. Deferred — do when relevant. |

---

## Contributor Onboarding

| Item | Effort | Notes |
|------|--------|-------|
| `CONTRIBUTING.md` | 0.5 session | Architecture overview, issue/PR templates. Currently good AI docs but nothing for human contributors. |

---

## Explicitly Out of Scope

| Item | Reason |
|------|--------|
| Full Swift type graph infrastructure | Over-engineered for current needs |
| Deep generic signature / associated type constraint emission | C# generics can't express Swift's full type system |
| Result builder (`@resultBuilder`) projection | Compile-time Swift feature, no ABI JSON representation |
| `@dynamicMemberLookup` / KeyPath projection | Affects <5 types across 53 validation libraries |
| Ownership semantics (`consume`/`borrow`) | Swift 6 feature with unclear ABI impact |
| Composing SwiftUI view trees from C# | Result builders are a compiler feature |
| Structs projected as C# value types | Only safe for frozen+blittable subset; marginal benefit |

---

## Recently Completed

| Item | Completed | Notes |
|------|-----------|-------|
| Apple framework XML database expansion | `ac39a4f7` (Mar 16) | ~473 skips resolved (nested + unresolvable Apple types). 90/90 validation. |
| NativeAOT device stability target | Mar 15 | 373 pass, 0 fail, 14/15 libraries. See `Completed/nativeaot-stability-sessions.md`. |
| C# keyword escaping in enum case labels | `51efaeec` (Mar 14) | FilterScope.swift fixture enabled. |
| ObjC binding integration | Mar 2026 | 34 ObjC framework targets validated. See `Completed/objc-binding-comparison.md`. |
| ObjC static method wrapper generation | `eb303ab4` (Mar 11) | Universal `@_cdecl` wrappers for static + instance methods. |
| Roslyn analyzer (SB1001) | Mar 2026 | Undisposed `ISwiftObject` warning + code fix in `SwiftBindings.Runtime` NuGet. |
| Ownership automation | Feb 2026 | See `Completed/ownership-automation-design.md`. |
