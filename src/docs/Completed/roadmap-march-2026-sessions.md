# Completed Roadmap Sessions (March 2026)

**Archived**: March 21, 2026
**Source**: Moved from `roadmap.md` — these sessions are fully complete with no remaining action items.

---

## Coverage Baseline (March 16, 2026)

Measured across 90 validation targets (56 libraries). This was the starting point for the March sessions.

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

---

## Session 0: BindingTests Generator Bug Fixes

Completed March 17, 2026. All 4 generator bugs fixed, Swift source restored, C# tests written. 90/90 validation, 480 runtime tests pass (477→480).

---

## Session 1: Struct & Closure Boundary Expansion

Completed March 17, 2026. 152 compile errors eliminated across 21 libraries. 90/90 validation, 4 new runtime tests passing on simulator.

**What shipped:**
- **Non-primitive frozen struct params** — Custom frozen structs pass as `UnsafeRawPointer` in `@_cdecl` wrappers, reconstructed via `.load(as: T.self)`. System framework types (CoreGraphics, Foundation) remain by-value. C# side: blittable structs use `stackalloc + MarshalToSwift`; memory-managed use `Payload.DangerousGetHandle()`. Skip gates removed from 6 files.
- **Closures with frozen struct params** — `IsCdeclCompatibleType` now accepts frozen structs. Swift adapter uses heap allocation (`initializeMemory`) with defer cleanup. C# callback receives via `MarshalFromSwift`.
- **Complex enums in closures** — Pure gate lift in `IsCdeclCompatibleType`. C# callback and Swift adapter heap allocation already existed.
- **Foundation.Data** — Investigation confirmed implementation already complete (`DataProjection.cs`). No action needed.

**Deferred:**
- **Optional\<Primitive/Enum\> in closures** — Risky ABI change (tag-byte layout vs pointer-based Optional). Needs runtime verification before enabling.
- **Async frozen struct params** — `stackalloc` not safe after `await`. Gate retained in async @_cdecl eligibility.

**Libraries improved:** DeviceKit, Swinject, PhoneNumberKit (59→0 errors), Valet, SwiftyBeaver (20→0), NVActivityIndicatorView, ObjectMapper, BonMot, Parchment, KeychainSwift, SwipeCellKit (13→0), Quick, AMPopTip, XMLCoder (12→0), SVGView (12→0), plus 6 Stripe modules.

---

## Session 2: Actor Isolation — @MainActor Sync Gate Lift

Completed March 17, 2026. Lifted the `@MainActor` skip gate so `@MainActor`-isolated members are emitted as synchronous C# APIs, following the Xamarin.iOS precedent (consumer manages thread affinity via `MainThread.BeginInvokeOnMainThread()`). Custom actors (`actor Counter`) remain blocked.

~852 `@MainActor` skips recovered across 21 libraries. Swift wrapper: 40/56 (net -2 from 42/56 due to 2 pre-existing bugs exposed by gate lift; both fixed post-session).

---

## Session 3: Existential Types & Error Ergonomics

Completed March 17, 2026. `any Sendable` marker protocol visibility. SwiftException for sync throws. Duplicate `[Obsolete]` fix. 90/90 compile gate, 7818 unit tests, 48/56 swift wrapper.

---

## Session 4: Protocol Emission Improvements (Sub-task 1)

Completed March 18, 2026. Static protocol members as `static virtual` in C# interfaces. `StaticProtocolMember` skips reduced to 1 (only `init` constructors remain). 90/90 compile gate, 7840 unit tests.

---

## Sessions 5–7: NativeAOT & CallConvSwift Migration

Completed March 18-19, 2026. NativeAOT investigation revealed 4 of 6 original @_cdecl architecture motivations were our bugs. Infrastructure cleanup, generator fixes, CallConvSwift migration, regression fixes, verification. Complete runtime test cleanup. Final state: 638 passed, 56 skipped on simulator. 7921 unit tests. 90/90 validation.

---

## Sessions 8–14: Skip Recovery & Device Parity

March 19-20, 2026. Progressively fixed remaining generator bugs and achieved device/simulator parity.

| Session | Focus | Tests Recovered |
|---------|-------|---------------:|
| 8 | Complex enum return @_cdecl, unary operators, existential ref, SkipOnDevice infra | +5 |
| 9 | Non-frozen struct instance @_cdecl, GetSwiftRawValueType fix | +3 |
| 10 | Optional<Int32> None implicit operator bug | +1 |
| 11 | Decomposed Optional property + generic metatype dispatch | +9 |
| 12 | NativeAOT device parity: metadata pre-registration + tuple marshalling | +42 device |
| 13 | Device bridge build + operator @_cdecl for NativeAOT | +43 device |
| 14 | Generic struct constructors, async optional/typed-throws, Optional array layout | +7 |

Final state: 663 passed, 31 skipped on simulator. 661 passed, 33 skipped on device.

---

## Architecture Stability Audit

March 20-21, 2026. Multi-phase audit to solve Mono JIT/NativeAOT stability.

- **Audit Phases 1-3**: Emission path tracing, ABI contract analysis, context divergence analysis
- **Research 4A-4C**: Bug taxonomy (53 bugs cataloged), predicate dry-run (100% recall on 3,152 P/Invokes), consolidation recommendation (Option B: keep dual paths + consolidate internals + gen-time static analysis)
- **Impl Phase 0** (`f963797e`): Cross-module extension Tj dispatch bug fix
- **Impl Phase 1** (`956d35ec`): Gen-time ABI contract checker (SWIFTBIND090-093)
- **Impl Phase 2** (`a5b725dc`): Emitter internal consolidation (shared marshalling helpers + unified skip gate)
- **Impl Phase 3** (`89d8bf14`): Runtime Limitation Registry with three-way runtime detection

---

## Post-Audit Fixes (March 21, 2026)

| Fix | Commit | Impact |
|-----|--------|--------|
| CC-001 SafeHandle-in-CallConvSwift | `8ba6daf8` | Class params routed through @_cdecl wrappers. 11 violations fixed across Nuke, BlinkID, BindingTests. |
| PWT Parameter Mismatch | `a08d3c45` | MethodWrapperEmitter CdeclPhase.Metadata missing PWT params. 1 test recovered (ConstrainedBox.getDescription). |
| Closure Return Invoke Thunk | `694f06e4` | Fixed `unsafeBitCast` ARC bug. New `ClosureEmitter.InvokeThunk.cs`. 8 tests recovered. 741 runtime tests passing. |
| ConfigurationValue Naming Collision | `9ab694c5` | Nested types renamed with "Type" suffix instead of properties with "Value" suffix. 3 libraries improved. |
| Existential ref→IntPtr Fix + Skip Audit | `3a6db2d9` | Fixed `ref EC1` → `IntPtr` across 3 emission paths. 12 library validation improvements. 1 stale skip recovered. 742 runtime tests passing, 27 skipped. |

---

## Final State (March 21, 2026)

- **Runtime tests**: 742 pass, 27 skip (simulator); comparable on device
- **Unit tests**: 7921+
- **Validation**: 90/90 compile gate (82 ok, 5 fail, 2 skip — baseline from `9ab694c5`)
- **Swift wrapper**: 51/56 ok
