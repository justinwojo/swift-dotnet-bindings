# Usability Roadmap

**Revised**: February 2026 (all scheduled sessions complete, remaining items only)
**Goal**: Push average binding quality score from ~3.82 toward 4.0+
**Scoring reference**: `binding-review-v3.md` — 18-library quality review, 10-category scorecards
**Completed work**:
- `Completed/usability-roadmap-sessions-1-10.md` — Sessions 1–10B (v2→v3 roadmap)
- `Completed/usability-roadmap-ergonomic-polish-sessions.md` — Ergonomic Polish Sessions 1–3, Existential Sessions 4–6

---

## Where We Are

### Current Scores (v3)

| Library | Score | Top Remaining Blocker |
|---------|:-----:|----------------------|
| SmartCardIO | 4.56 | Minor — `object _params` existential |
| MicroblinkPlatform | 4.44 | Minor — naming collisions |
| Mappedin | 4.30 | ~~SCREAMING_CASE names~~ — fixed |
| Lottie | 4.10 | ~~AnyType in \~22 locations~~ — existential tuple elements now supported |
| Nuke | 3.80 | `Create_529DA596` mangled name, naming polish |
| BlinkID | 3.70 | `DateResult<SwiftString>` in MRZ properties |
| BlinkIDUX | 3.70 | Empty `IUXThemeProtocol` (21 members skipped) |
| KeychainAccess | 3.65 | No protocol interfaces emitted |
| Stripe (14) | 3.55 | Complex enum closures in callbacks |
| Starscream | 3.45 | Runtime event delivery impossible (compile-only) |
| CryptoSwift | 3.44 | `ArraySlice<UInt8>` → AnyType (14 occurrences) |
| SnapKit | 3.40 | `GetEqualTo` naming, async false positives |
| Alamofire | 3.30 | Foundation.Data + generic closure callbacks |
| Mixpanel | 3.25 | `[String: any MixpanelType]` dict-existential |
| SkeletonView | 3.25 | Collections: `SwiftSet<AnyType>`, limited customization |
| GRDB | 3.20 | `ResultCode` as class (not enum), async APIs missing |
| RxSwift | 2.75 | Deprioritized — unlikely .NET iOS use case |

**Overall average**: 3.62 (range: 2.75 RxSwift — 4.56 SmartCardIO)

> **Note**: Scores above are from the v3 review. Completed sessions (EP1–3, S4–6) are projected to have improved the average to ~3.82. A v4 review would confirm actual scores.

### Weakest Categories (v3 column averages)

| Category | v3 Avg | Gap to 4.0 | Key Lever |
|----------|:------:|:----------:|-----------|
| Protocol/Interface | 3.28 | 0.72 | Constrained generics |
| Overall Usability | 3.28 | 0.72 | Library-specific critical workflows |
| Noise/Leakage | 3.28 | 0.72 | Remaining async false positives |
| Error Handling | 3.44 | 0.56 | Throwing methods not emitted |
| Type Fidelity | 3.53 | 0.47 | AnyType in generics |
| Naming | 3.61 | 0.39 | Remaining naming issues |
| Completeness | 3.61 | 0.39 | Closure callbacks, existential params |

---

## Remaining Items (not yet sessionized)

These are real improvements with lower effort-to-impact ratios than the completed sessions. They can be bundled into future sessions or addressed opportunistically.

### Medium Effort

| Item | Impact | Libraries | Notes |
|------|--------|-----------|-------|
| String enum raw values from swiftinterface | GRDB `ResultCode` as enum | GRDB | ABI JSON lacks raw values; parse from swiftinterface |
| `Optional<Primitive/Enum>` in closures | Various closure-accepting APIs | Broad | Different ABI from pointer-based Optional. Also covers E1 (`SwiftOptional<T>` in closure params from preview.14 review) |
| Complex enums in closures | Various | Broad | Structural emitter change |
| Multi-closure params per method | Rare today | Deferred from S3 | No real-world library currently requires it |
| ~~Protocol conformance with default extension implementations~~ | ~~Lottie `IAnyInterpolatable` constraint~~ | ~~Lottie~~ | **Done** — `ProtocolExtensionDefaultsIndex` indexes unconstrained extension defaults with transitive inheritance; `ProtocolConformanceValidator` recognizes extension-satisfied requirements; extension-defaulted interface members emitted as DIMs with `NotSupportedException` bodies; property defaults enforce getter/setter accessor contracts |
| Method bypass with marshalled passthrough params | Theoretical | None today | 0 real-world methods currently bypass |
| `AnyError` → Exception-based error handling (E3) | Error handling ergonomics | Broad | Add `SwiftException : Exception` wrapping `AnyError`, or `ToException()` conversion. Runtime design decision |
| `ConfigurationValue` property name collision (E14) | Readability of core APIs | Nuke, others | Alternative disambiguation strategy when property type name == property name |
| `Array<ObjCClass>` properties not bound (E17) | Testing convenience APIs | StripeIdentity | Extend collection projection for ObjC-bridged element types |
| Cross-module protocol conformances (E18) | Polymorphic use through cross-module interfaces | StripeIdentity | Thread conformance declarations across module boundaries |

### Small Effort

| Item | Impact | Libraries | Notes |
|------|--------|-----------|-------|
| ~~ExistentialContainer0 in tuples~~ | ~~\~22 AnyType locations~~ | ~~Lottie~~ | **Done** — Extended ClosureEmitter tuple branches for element-wise existential + simple enum conversion; removed `HasClosureUnsafeTupleElements` gate |
| `async throws(ErrorType)` free functions | Guarded, rare | Various | `_payload`/`this` in static context |
| ~~SCREAMING_CASE naming~~ | ~~`THING_KEY` → `ThingKey`~~ | ~~Mappedin~~ | **Done** — `ToPascalCaseForTypeName` in NameProvider; applied at registration + C# output points |
| `_object` parameter naming | Already partially fixed in S8b | Mappedin | Small polish |

### Runtime Blockers (not generator work)

| Item | Impact | Notes |
|------|--------|-------|
| Runtime existential callback delivery | Starscream, others | Generator correct (S6). Blocked by Mono JIT SIGSEGV on proxy through CallConvSwift. NativeAOT device builds expected to work. |

---

## To Reach 4.0+

The projected average after all completed sessions is **~3.82** (range 3.75–3.90). Reaching 4.0+ would require:

- **String enum raw values** — GRDB `ResultCode` as a proper enum
- **Deeper ObjC integration** — Lottie `IInterpolatable` existentials
- **`Optional<Primitive/Enum>` in closures** — Different ABI than pointer-based Optional
- **More complete protocol extension coverage** — Constrained generics, async, throwing
- **v4 binding review** — Confirm actual score improvements from EP1–3 and S4–6

RxSwift-specific features (Map value-type generics, flatMap constrained generics) were deprioritized as unlikely .NET iOS use cases.

---

## Issues Carried Forward

| Issue | Origin |
|-------|--------|
| `Optional<Primitive/Enum>` in closures | Q3 (Phase 2) |
| Complex enums in closures | Q3 (Phase 2) |
| ~~ExistentialContainer0 in tuple elements~~ | ~~Pre-existing~~ — **Completed** |
| `async throws(ErrorType)` free functions | Pre-existing |
| Multi-closure params per method | Session 3 (deferred) |
| Runtime existential callbacks | Session 6 (Mono JIT blocker) |

---

## Completed Work Reference

- `Completed/usability-roadmap-sessions-1-10.md` — Sessions 1–10B (v2→v3 roadmap, all complete)
- `Completed/usability-roadmap-ergonomic-polish-sessions.md` — Ergonomic Polish Sessions 1–3, Existential Sessions 4–6
- `Completed/roadmap-completed-feb2026.md` — Phase 2 sessions Q1–Q4
- `Completed/binding-review-feb-23.md` — v1 binding review
- `binding-review-v2.md` — v2 binding review (post-Phase 2)
- `binding-review-v3.md` — v3 binding review (post-usability roadmap Sessions 1–10B)
