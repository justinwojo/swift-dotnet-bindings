# Data Pack 08 — PartialSuccessKitchen Fixture Design

**Date**: 2026-07-16  
**Mode**: **Design only** — do **not** implement production code, fixtures, or gates in this pass.  
**Closes design gap for**: G1-004 / DA-W8-T-G1-001 (product-scenario “unsupported shapes → clean partial”).  
**Inputs**: [`00-skipreason-catalog.md`](./00-skipreason-catalog.md) (BindingTests skip density + seed shapes), [`04-validation-corpus-skip-heatmap.md`](./04-validation-corpus-skip-heatmap.md) (real-lib histogram), Track G1 / T / M2 packaging notes.

---

## 1. Product question this fixture answers

If a third-party drops a **tiny** pure-Swift library that intentionally contains a few hard shapes, do they get:

1. **Generator exit 0**
2. **Compile-clean C#** (no CS\*, no dangling EntryPoint → SWIFTBIND108)
3. **Honest `binding-report.json` rows** with expected `SkipReason` + disposition
4. **Wrapper**: either compiles (Scenario A) **or** fails loudly under a documented soft SDK path (Scenario B)

…without relying on the full BindingTests kitchen-sink compile as the only product signal?

**Answer this pack designs for:** yes — via a dedicated minimal module + dual wrapper scenarios + report budget assertions.

---

## 2. Placement & packaging (implementation later)

| Item | Recommendation |
|------|----------------|
| Module name | `PartialSuccessKitchen` |
| Layout | Standalone mini-lib (preferred) under e.g. `BindingTests/Sources/PartialSuccessKitchen/` **or** a single file in BindingTests gated by a nuke class/filter — **prefer standalone** so skip budgets are not polluted by the 4k-member test lib |
| Outputs | Own `output/PartialSuccessKitchen/` with `binding-report.json`, emission report, generated `.cs` + wrapper `.swift` |
| Gate host | New nuke sibling or `nuke binding-tests --partial-success-kitchen` / unit harness that shells generator + `dotnet build` |
| Runtime | Optional Phase 2: construct positive-control type only |

**Out of scope for v1 of this fixture:** mixed ObjC systemic fail (G1-002), produce-throw omit policy (G1-003 implementation), changing default `SwiftWrapperRequired`.

---

## 3. Global expectations (both scenarios)

| Check | Expected |
|-------|----------|
| Generator process exit | **0** |
| Integrity SWIFTBIND108 | **Must not fire** (dangling EntryPoint is always a hard fail — not soft) |
| Generated C# | **Compiles** (`dotnet build` on generated csproj succeeds) |
| Positive-control surface | Types/members in §5 **emitted** and present in C# |
| Intentional skip shapes (§4) | Each produces **≥1** `SkippedItems` row (type and/or member) with the **Expected reason** (or allowed alias set) |
| `SkipTriage.ReviewCount` | **0** for Scenario A; Scenario B may allow Review only if a deliberate strip residual is asserted ≤ budget (default **0** preferred) |
| Unexpected Review reasons | Empty set: no `MissingHandler`, no unexplained `Unknown`, no surprise `MissingWrapperSymbol` on Scenario A |
| Wrapper strip tripwire | `wrapper_stripped_count == 0` on Scenario A (admission-time skips, not post-processor strip) |
| SWIFTBIND060/061 | May warn on skip counts; **must not** fail the build by themselves |

---

## 4. Intentional skip shapes (minimal Swift)

Each shape is **minimal**: one public declaration (plus the smallest supporting type if the language requires it). Names are stable identifiers for harness mapping.

**Shape count (skip-honest):** **12**  
**Positive-control shapes (§5):** **2**  
**Scenario-B-only poison (§7):** **1** (not counted in the 12 — it forces wrapper compile fail, not an honest emission skip)

### Shape matrix

| ID | Minimal Swift (sketch) | Expected `SkipReason` | Disposition | Emission-report cause (if any) | Why this shape (corpus) |
|----|------------------------|----------------------|-------------|-------------------------------|-------------------------|
| **S01** | `public struct KitchenView: View { public init() {}; public var body: some View { EmptyView() } }` | `SwiftUIView` | **ExpectedStructural** | (type routed to bridge / skipped from main binding) | Pack 00 seed; validate has 53 View rows |
| **S02** | `public protocol KitchenPAT { associatedtype Item; func item() -> Item }` + `public func useKitchenPAT(_ p: any KitchenPAT) {}` | Member: `GenericProtocolConstraint` and/or `UnsupportedExistential`; type may still emit as empty/`IKitchenPAT` with reverse suppressed | **KnownLimitation** (existential/PAT); EP suppress → often **Review** default refined by Details | PAT / existential path | Pack 00 seed; validate 392 `GenericProtocolConstraint` + 95 existential |
| **S03** | Multi-requirement PAT that cannot reverse-dispatch cleanly: `public protocol KitchenMultiPAT { associatedtype A; associatedtype B; func pair() -> (A, B) }` + public method returning `any KitchenMultiPAT` on a public class | `EveryProtocolConformanceSkipped` and/or per-member `SuppressedProxyMemberDegraded` | **Review** (EP default) / **KnownLimitation** (degraded members) | suppressedProxy\* counters | Pack 00 largest KnownLimitation on BindingTests (63 degraded); validate 356 EP skipped |
| **S04** | `public final class KitchenAsyncVoidClosure { public init() {}; public func open(_ h: @escaping (Int32) async -> Void) {} }` | `UnsupportedClosure` | **KnownLimitation** | `closure_params` | Pack 00/04 UnsupportedClosure (600 validate); mirrors YouTubePlayerKit OpenURLAction residual |
| **S05** | `public final class KitchenClosureOptExistential { public init() {}; public func check(_ f: @escaping (Int32) -> (any KitchenSignal)?) -> Bool { f(0) != nil } }` + tiny `public protocol KitchenSignal { func tag() -> Int32 }` | `UnsupportedClosure` | **KnownLimitation** | `closure_params` | Distinct closure-return gate (`Optional<any P>`) — second UnsupportedClosure bucket |
| **S06** | Method-level generic: `public final class KitchenMethodGeneric { public init() {}; public func map<T: KitchenSignal>(_ v: T) -> Int32 { v.tag() } }` | Often **emits** via MCB/CSM path when constraint is simple; **if skipped**: `GenericProtocolConstraint` / `UnsatisfiedGenericConstraint` / emission `method_level_generics` | **KnownLimitation** if skip; else mark as **must-emit overload** and drop from skip budget | `method_level_generics` when wrapper skipped | Pack 00 emission top cause (63); validate signature/generics heavy — **harness must accept emit-or-honest-skip** (see §4.1) |
| **S07** | Parameter pack type: `public struct KitchenPack<each R> { public init() {} }` (deployment-gated like BindingTests pack fixture) | `UnsupportedType` (type-level pack gate) | **KnownLimitation** | type skip | Honest type admission; AppIntents-class pack shape without Apple deps |
| **S08** | Free pack method if type pack insufficient: `public func kitchenPackCall<each T>(_ xs: repeat each T) {}` | `UnsupportedSignature` (“variadic generic parameter pack”) | **KnownLimitation** | pack gate | Pack 04 #1 reason (1420 UnsupportedSignature) — **force at least one honest Signature skip** |
| **S09** | `@usableFromInline internal final class KitchenInternalParent { public init() {}; public func describeAsync() async -> Int32 { 1 }; public func transform(using f: @escaping (Int32) -> Int32) -> Int32 { f(1) } }` | Async + closure members: `ParentModuleInternalNoFallback` | **ExpectedNonPublic** | `parent_module_internal` | Pack 00 seed; emission-time drop (not strip) |
| **S10** | Public host + internal method: `public final class KitchenPublicHost { public init() {}; @usableFromInline internal func register(_ x: Int32) {} }` | `Pattern2InternalTypeReach` **or** `ModuleInternal` | **ExpectedStructural** / **ExpectedNonPublic** | internal reach | Validate ModuleInternal 874 + Pattern2 216 — expected, not “fix” |
| **S11** | Actor-isolated stream property on custom actor type (minimal): custom `@globalActor` + `public var ticks: AsyncStream<Int> { get }` on actor-isolated type **if** current gate still classifies as skip | `ActorIsolatedAsyncStream` | **KnownLimitation** | `actor_isolated` | Pack 00 seed; rare but distinct KnownLimitation |
| **S12** | Codable synthesize surface: `public struct KitchenCodable: Codable { public var n: Int; public init(n: Int) { self.n = n } }` | Synthesized members `encode(to:)` / `init(from:)` → `SynthesizedCodable` | **ExpectedStructural** | — | Pack 04 #2 skip reason (971) — prove “don’t treat as bug” |

### 4.1 S06 acceptance rule (emit-or-skip)

S06 is intentionally **dual-outcome tolerant**:

- **Preferred product outcome today:** method **emits** (closed/generic bridge) and does **not** appear in skip budget.
- **Acceptable degrade:** honest skip with one of the listed reasons + KnownLimitation.
- **Forbidden:** emit then CS\* / emit then MissingWrapperSymbol / silent omit without report row.

Harness assertion: `emit XOR skip-row` for the projected method name; never both broken and silent.

### 4.2 Shapes deliberately **not** in v1 kitchen

| Omitted | Why |
|---------|-----|
| `NetUnavailableType` | Needs Foundation/OS type absent from .NET; better as Apple-framework validate canary, not pure mini-lib |
| `MissingWrapperSymbol` as intentional | Review-tier integrity residual — Scenario A must stay **0**; do not seed strip |
| Produce-throw-only API without EP suppress | G1-003 product change; S03 covers reverse-dispatch degrade reporting if proxy suppresses |
| `AsyncProperty` as skip | Generator now projects many async properties as methods (`EmitAsyncPropertyAsMethods`); do not assert obsolete skip |
| Mixed ObjC abort | Separate G1-002 fixture |
| Forced `MissingHandler` | Would be a generator bug, not a consumer shape |

---

## 5. Positive-control shapes (must emit)

| ID | Minimal Swift | Expectation |
|----|---------------|-------------|
| **P01** | `public struct KitchenOk { public var x: Int; public init(x: Int) { self.x = x } }` | Frozen blittable struct emitted; init + property usable |
| **P02** | `public enum KitchenOkEnum: Int { case a = 1, b = 2 }` **or** `public final class KitchenOkClass { public init(); public func ping() -> Int32 { 7 } }` | Second must-emit surface so “partial” is not a single-type fluke |

Harness: generated C# contains these types; optional runtime: `new KitchenOk(x: 3)` / `Ping() == 7`.

---

## 6. Scenario A — Wrapper **succeeds**

**Goal:** Prove partial-success at the **member/type skip** layer with a healthy wrapper.

| Step | Expectation |
|------|-------------|
| Generate | Exit 0 |
| Wrapper compile | Success → `HasWrapperXCFramework=true` / metadata true |
| C# compile | Success |
| Report | All §4 shapes accounted; P01/P02 emitted |
| SDK | Default **`SwiftWrapperRequired=true`** is fine — build must **succeed** |
| Strip | 0 residual InternalType/NSInvocation for this module |
| ReviewCount | **0** |

**Assertions (harness):**

1. `generator_exit_code == 0` (live process, not theater baseline key alone).
2. `dotnet build` generated project → 0.
3. Parse `binding-report.json`:
   - `EmittedTypes` includes KitchenOk (+ P02).
   - For each S01–S12: `SkippedItems` contains expected reason ∈ allowed set (table §4).
   - `SkipTriage.ByDisposition` counts: ExpectedStructural ≥ (S01,S12,…); KnownLimitation ≥ (S04,S05,S08,…); ExpectedNonPublic ≥ S09/S10.
   - `ReviewCount == 0`.
   - `PublicSurfaceLost` ≥ number of consumer-visible KnownLimitation rows (informational floor, not exact).
4. Grep generated C#: **no** `EntryPoint` for skipped wrapper symbols; positive controls have valid P/Invokes or blittable layout.
5. `binding-emission-report.json` (optional): `closure_params` / `parent_module_internal` / pack causes present when corresponding shapes skip.
6. Optional runtime: construct P01; assert skipped types **absent** from assembly (`Type.GetType` null) or only present as documented bridge artifact (S01 View → bridge file, not main type).

---

## 7. Scenario B — Wrapper **fails** (soft package path)

**Goal:** Prove day-1 **packaging** partial-success (G1-001 / M2) without weakening integrity.

### 7.1 How to force wrapper fail (design choice)

Pick **one** poison mechanism (do not combine):

| Option | Mechanism | Pros | Cons |
|--------|-----------|------|------|
| **B1 (recommended)** | Extra source file `KitchenWrapperPoison.swift` compiled **only** into the generated wrapper inject path via a harness flag / `#if WRAPPER_POISON` **or** a second fixture module that emits an uncompilable `@_cdecl` block on purpose | Isolated; does not pollute honest skip shapes | Needs harness hook |
| **B2** | Post-generate edit of wrapper `.swift` inserting `this_is_not_valid_swift!!!` before swiftc | Simple for unit harness | Not a product-facing path |
| **B3** | Depend on residual strip class (NSInvocation-like) | Realistic | Flaky; fights strip→0 goal |

**Do not** use MissingWrapperSymbol growth as the success metric.

### 7.2 SDK / generator flags (soft path)

| Layer | Flag / prop | Role |
|-------|-------------|------|
| **MSBuild SDK** | `<SwiftWrapperRequired>false</SwiftWrapperRequired>` | Demotes SWIFTBIND051 **Error → Warning**; allows pack/build without wrapper xcframework ([`Sdk.props`](../../../Swift.Bindings.Sdk/Sdk/Sdk.props) default is `true`; [`Sdk.targets`](../../../Swift.Bindings.Sdk/Sdk/Sdk.targets) `_ValidateSwiftWrapperCompilation`) |
| **Generator (SDK mode)** | Already softens wrapper Fatal → **SWIFTBIND050**, exit **0** (“C# bindings still valid — wrapper-dependent methods will throw DllNotFoundException”) | Must stay exit 0 under soft required |
| **Must remain hard** | SWIFTBIND108 integrity; SWIFTBIND056 explicit arch contract; pack lies 038/039/040; hook disconnect 062–065 | Soft wrapper ≠ soft integrity |
| **Not required for kitchen** | `--strict-inputs` (keep off for soft day-1 story) | Optional CI-strict variant later |
| **Document-only ritual** | Same `SwiftWrapperRequired=false` for consumer exploration of hard libraries | Tier-0 docs item; kitchen **gates** the soft path |

Hard-path control (optional negative test): same poison + **default** `SwiftWrapperRequired=true` → **MSBuild Error SWIFTBIND051** — proves the soft flag is load-bearing.

### 7.3 Scenario B expectations

| Check | Expected |
|-------|----------|
| Generator exit | **0** (SDK-mode 050 path) |
| C# compile | **Success** |
| Wrapper xcframework | **Absent** / `HasWrapper=False` |
| SWIFTBIND050 | Present (warn) |
| SWIFTBIND051 | **Warning** under soft; **Error** under default-required control |
| Skip shapes S01–S12 | Still honest (unchanged from A) |
| Positive controls | Compile; **runtime** may throw `DllNotFoundException` on wrapper-dependent APIs — kitchen may assert **compile-only** for B, or assert DllNotFound only on known wrapper-required call if runtime leg exists |
| Package (if pack leg) | nupkg produced; native carrier policy per `NativePackagingPolicy` / Exists() guards — no false “has wrapper” metadata |

### 7.4 Scenario B assertions (harness)

1. Soft props: build succeeds; log contains SWIFTBIND050 and Warning SWIFTBIND051.
2. Default props control: build fails with Error SWIFTBIND051 (optional but recommended).
3. Report still has full skip budget from §4; ReviewCount still 0 (poison is compile-time wrapper, not co-gate MissingWrapperSymbol).
4. No SWIFTBIND108.
5. Metadata: `_SwiftBindingHasWrapperXCFramework` / equivalent is False; consumer targets do not claim a present wrapper NativeReference without Exists guard.

---

## 8. Report budget (concrete numbers for implementers)

After first green implementation run, **freeze** exact counts in a small kitchen baseline (not BindingTests kitchen-sink budgets). Design-time floors:

| Metric | Scenario A floor / ceiling |
|--------|----------------------------|
| `ReviewCount` | **= 0** |
| `MissingWrapperSymbol` rows | **= 0** |
| `wrapper_stripped_count` | **= 0** |
| ExpectedStructural rows | **≥ 2** (S01 + S12 minimum) |
| ExpectedNonPublic rows | **≥ 1** (S09 or S10) |
| KnownLimitation rows | **≥ 3** (S04, S05, S08 at minimum) |
| Emitted positive types | **≥ 2** (P01 + P02) |
| Generator exit | **0** |
| C# compile | ok |

Exact per-reason multiset is **implementation-time sealed** once the generator’s current admission is observed on the mini-lib (S02/S03/S06 may emit multi-row or dual reasons — allowlisted aliases in §4).

---

## 9. Suggested single-file sketch (reference only — not to land in this task)

Workers implementing later may collapse S01–S12 + P01–P02 into one module. Illustrative skeleton (non-normative; IDs must match §4):

```swift
// PartialSuccessKitchen — product-scenario partial-success fixture (design sketch)
import SwiftUI

// P01
public struct KitchenOk {
    public var x: Int
    public init(x: Int) { self.x = x }
}

// P02
public final class KitchenOkClass {
    public init() {}
    public func ping() -> Int32 { 7 }
}

// S01
public struct KitchenView: View {
    public init() {}
    public var body: some View { EmptyView() }
}

// S02
public protocol KitchenPAT {
    associatedtype Item
    func item() -> Item
}
public func useKitchenPAT(_ p: any KitchenPAT) {}

// S03
public protocol KitchenMultiPAT {
    associatedtype A
    associatedtype B
    func pair() -> (A, B)
}
public final class KitchenMultiPATCarrier {
    public init() {}
    public func make() -> any KitchenMultiPAT { fatalError("fixture-only") }
}

// S04–S05
public protocol KitchenSignal { func tag() -> Int32 }
public final class KitchenAsyncVoidClosure {
    public init() {}
    public func open(_ h: @escaping (Int32) async -> Void) {}
}
public final class KitchenClosureOptExistential {
    public init() {}
    public func check(_ f: @escaping (Int32) -> (any KitchenSignal)?) -> Bool { f(0) != nil }
}

// S06 (emit-or-honest-skip)
public final class KitchenMethodGeneric {
    public init() {}
    public func map<T: KitchenSignal>(_ v: T) -> Int32 { v.tag() }
}

// S07–S08
public struct KitchenPack<each R> { public init() {} }
public func kitchenPackCall<each T>(_ xs: repeat each T) {}

// S09
@usableFromInline
internal final class KitchenInternalParent {
    public init() {}
    public func describeAsync() async -> Int32 { 1 }
    public func transform(using f: @escaping (Int32) -> Int32) -> Int32 { f(1) }
}

// S10
public final class KitchenPublicHost {
    public init() {}
    @usableFromInline
    internal func register(_ x: Int32) {}
}

// S11 — optional if isolation + AsyncStream still skip; else drop from sealed budget
// S12
public struct KitchenCodable: Codable {
    public var n: Int
    public init(n: Int) { self.n = n }
}
```

Scenario B poison lives **outside** this file (harness inject) so Scenario A wrapper stays green.

---

## 10. Harness outline (BindingTests vs unit)

| Layer | What it proves | Suggested host |
|-------|----------------|----------------|
| **Unit / generator integration** | Exit 0 + skip reasons on in-memory or temp ABI/module if available | xUnit + existing generator test helpers (fast) |
| **Product / BindingTests-style** | Real xcframework → generate → build → report parse | nuke target (compile-only sibling) |
| **SDK soft wrapper** | MSBuild props path 050/051 Warning | small SDK-direct csproj under test output |
| **Runtime (optional)** | P01 round-trip; absence of skipped types | sim class-filter only |

**Prefer product gate on real generate+compile**, not only string-assert unit tests (Track T anti-theater).

---

## 11. Non-goals / integrity keep-hard list

- Do **not** soft-fail SWIFTBIND108 or explicit-arch 056 for this fixture.
- Do **not** count success as “ReviewCount hidden by KnownLimitation produce-throw” — kitchen uses **omit-friendly** skip shapes; reverse degrade only via S03 if it appears.
- Do **not** change default `SwiftWrapperRequired` in the kitchen PR unless owner also lands G1-001 policy.
- Do **not** implement this design in the design-only pass.

---

## 12. Cross-links

| Doc | Role |
|-----|------|
| [`00-skipreason-catalog.md`](./00-skipreason-catalog.md) §5 seed shapes | BindingTests density seeds |
| [`04-validation-corpus-skip-heatmap.md`](./04-validation-corpus-skip-heatmap.md) | Real-lib priority (Signature, Closure, Codable, ModuleInternal, EP) |
| [`../tracks/Track-G1_Graceful-Degradation.md`](../tracks/Track-G1_Graceful-Degradation.md) | G1-001/004 product gaps |
| [`../tracks/Track-T_Tests-Gates-Honesty.md`](../tracks/Track-T_Tests-Gates-Honesty.md) | DA-W8-T-G1-001 recommended fixtures |
| [`../tracks/Track-M2_Wrapper-SDK-Packaging.md`](../tracks/Track-M2_Wrapper-SDK-Packaging.md) | Soft 050/051 packaging |
| [`../synthesis/graceful-degradation-map.md`](../synthesis/graceful-degradation-map.md) | Day-1 matrix |
| [`../synthesis/work-items-backlog.md`](../synthesis/work-items-backlog.md) | Tier 0 item #2 |

---

## 13. Shape count (return metric)

| Category | Count |
|----------|------:|
| **Honest skip shapes (S01–S12)** | **12** |
| Positive-control shapes (P01–P02) | 2 |
| Scenario-B wrapper poison (separate) | 1 |
| **Primary kitchen shape count (skips)** | **12** |

Workers implementing G1-004 should treat **12** as the sealed skip-shape budget unless S06/S11 are dropped after first observation (document any drop in the kitchen baseline commit message).

---

*End of design. No production code or fixtures were added by this pack.*
