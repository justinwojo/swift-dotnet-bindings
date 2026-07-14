# Data Pack — Unit-Test “Theater” Metrics

**Scope:** `src/Swift.Bindings/tests/**/*Tests.cs`  
**Method:** Top 10 files by **line count** (EOF line). Assert counts via `Assert.Contains` / `Assert.Equal` / `Assert.True` / `Assert.False` / `Assert.Throws` (xUnit).  
**“Contains%”** = `Contains / (Contains + Equal + True + False + Throws)` — higher = more substring/snippet theater vs value/boolean/exception semantics.  
**Not counted in Contains%:** `Assert.DoesNotContain`, `Assert.Single`/`Empty`/`Null`/`NotNull`/`All` (noted separately where heavy).  
**Date basis:** workspace snapshot 2026-07-16. No production edits.

---

## 1. Top 10 largest unit test files (by lines)

| Rank | Lines | Path (under `UnitTests/`) | Contains | Equal | True | False | Throws | **Contains%** |
|-----:|------:|---------------------------|---------:|------:|-----:|------:|-------:|-------------:|
| 1 | **10 715** | `EmitterTests/SwiftUIBridgeEmitterTests.cs` | **606** | 254 | 69 | 17 | 0 | **64.1%** |
| 2 | **7 332** | `EmitterTests/ProtocolProxyEmitterTests.cs` | **449** | 9 | 45 | 4 | 0 | **88.6%** |
| 3 | **5 271** | `EmitterTests/EveryProtocolEmitterTests.cs` | **220** | 16 | 31 | 33 | 0 | **73.3%** |
| 4 | **5 000** | `EmitterTests/MethodWrapperEmitterTests.cs` | 105 | 4 | 73 | 59 | 0 | **43.6%** |
| 5 | **4 128** | `EmitterTests/ConcreteSpecializationEngineTests.cs` | 65 | 32 | 36 | 31 | 0 | **39.6%** |
| 6 | **3 952** | `MarshalerTests/BoundGenericsHandlerTests.cs` | 43 | 30 | 58 | 57 | 0 | **22.9%** |
| 7 | **2 780** | `EmitterTests/WitnessDispatchEmitterTests.cs` | **150** | 48 | 28 | 22 | 0 | **60.5%** |
| 8 | **2 694** | `EmitterTests/ClosureEmitterDirectTests.cs` | **184** | 4 | 17 | 3 | 0 | **88.5%** |
| 9 | **2 651** | `EmitterTests/AsyncSwiftWrapperTests.cs` | **115** | 2 | 0 | 0 | 0 | **98.3%** |
| 10 | **2 355** | `EmitterTests/MemberValidationPipelineTests.cs` | 43 | 47 | 30 | 51 | 0 | **25.1%** |

**Sum of top-10 lines:** **46 878**  
**Sum of Contains in top-10:** **1 980**  
**Sum of Equal+True+False+Throws in top-10:** **968**  
**Top-10 aggregate Contains%:** `1980 / (1980+968)` = **67.2%**

### Mega-file Contains% callouts (headline)

| File | Lines | Contains% | Theater note |
|------|------:|----------:|--------------|
| **AsyncSwiftWrapperTests** | 2 651 | **98.3%** | Almost pure emit-string scanning; 2× `Assert.Equal`, 0 True/False/Throws |
| **ProtocolProxyEmitterTests** | 7 332 | **88.6%** | 449× Contains vs 58 semantic; +150 DoesNotContain |
| **ClosureEmitterDirectTests** | 2 694 | **88.5%** | 184× Contains vs 24 semantic; +53 DoesNotContain |
| **EveryProtocolEmitterTests** | 5 271 | **73.3%** | Swift extension body snippets; +96 DoesNotContain |
| **SwiftUIBridgeEmitterTests** | 10 715 | **64.1%** | Largest file; mixed with many Equal on bridge param kinds |
| **WitnessDispatchEmitterTests** | 2 780 | **60.5%** | Heavy emit Contains + solid Equal enum-kind gates |
| **MethodWrapperEmitterTests** | 5 000 | **43.6%** | Better balance — many ShouldEmitWrapper True/False |
| **ConcreteSpecializationEngineTests** | 4 128 | **39.6%** | CSM/engine logic + some emit Contains |
| **MemberValidationPipelineTests** | 2 355 | **25.1%** | SkipReason Equal + ShouldEmit True/False dominant |
| **BoundGenericsHandlerTests** | 3 952 | **22.9%** | Best of the mega set — predicate True/False |

### Supplementary string-scan pressure (DoesNotContain)

| File | DoesNotContain |
|------|---------------:|
| ProtocolProxyEmitterTests | 150 |
| EveryProtocolEmitterTests | 96 |
| SwiftUIBridgeEmitterTests | 70 |
| AsyncSwiftWrapperTests | 68 |
| ClosureEmitterDirectTests | 53 |
| MethodWrapperEmitterTests | 39 |
| WitnessDispatchEmitterTests | 31 |

Theater often ships as **Contains + DoesNotContain pairs** (assert fragment present *and* anti-pattern absent). Pairing inflates confidence without checking structure/AST/semantics.

---

## 2. What “theater” means here

**Theater tests** assert that a generated C#/Swift **string blob** contains a fragile substring (often several per Fact), rather than:

- a projected type / skip reason / enum / count (`Assert.Equal`);
- a boolean gate (`Assert.True`/`False` on a pure helper);
- an expected exception (`Assert.Throws`).

They are **real regression nets** for emission bugs, but they:

- couple tests to formatting/order of emitted code;
- multiply assertions per scenario (one Fact → 5–15 Contains);
- under-test **behavior** that only BindingTests / runtime can prove;
- create **false green** when a wrong branch still emits the same token.

Project guidance (`Claude.md`): *“Assert behavior, not implementation… Prefer semantic checks… over exact string matches of generated code.”* The top-10 profile shows large emitter suites still lean heavily on string Contains.

---

## 3. Contrast samples — small high-signal tests

### 3a. `VtableLayoutBuilderTests.cs` (~565 lines)

Path: `UnitTests/EmitterTests/VtableLayoutBuilderTests.cs`

- **Assert.Contains:** **0**
- Style: `Assert.Equal` on `SlotIndex` / `SlotVerdict` / widths; `Assert.True`/`False` on `Included`; `Assert.Single` / `Assert.Empty`
- Pins **one oracle** (`VtableLayoutBuilder`) with **index/width invariants** — the exact class of bug that only SIGSEGVs on NativeAOT if wrong
- Example shape:

```csharp
var slot = Assert.Single(layout.IncludedMethods);
Assert.Equal(0, slot.SlotIndex);
Assert.True(slot.Included);
Assert.Equal(SlotVerdict.Included, slot.Verdict);
```

**Contrast:** zero emit-string theater; every assert is a layout invariant.

### 3b. `PublicMethodNameContextTests.cs` (~265 lines)

Path: `UnitTests/MarshalerTests/PublicMethodNameContextTests.cs`

- **Assert.Contains:** **0**
- Style: `Assert.Equal` on shaped public names; `True`/`False` on context fields; parity between context overload and positional shim
- Locks AF05 Target C collision axes without grepping generated modules

### 3c. `ProtocolVtableMembersInvariantTests.cs`

Path: `UnitTests/EmitterTests/ProtocolVtableMembersInvariantTests.cs`

- **Assert.Contains:** **0** (flag matrix uses Equal/True/False)
- Style: Theory over `IsStatic × IsObjCOptional × IsProtocolRequirement × IsFromExtension` — predicate **must agree** with emitted `{P}_vtable` field presence
- Documents Finding 30/31 / Bug #21 class failures without string-scraping full wrappers

**Takeaway:** high-quality suites are **small, oracle-shaped, and equal/boolean-heavy**. Mega theater files are large because they re-snapshot every emission surface instead of isolating pure helpers.

---

## 4. `[Collection("ReportCollector")]` inventory

**15 files** use `[Collection("ReportCollector")]` (forces **serial** execution within that collection under xUnit):

| # | Path |
|---|------|
| 1 | `ReportingTests/ReportCollectorTests.cs` |
| 2 | `ReportingTests/SuppressedProxyReportingTests.cs` |
| 3 | `EmitterTests/ExistentialBypassEmitterTests.cs` |
| 4 | `EmitterTests/PropertyHandlerSkipReportingTests.cs` |
| 5 | `EmitterTests/SpiMemberFilteringTests.cs` |
| 6 | `EmitterTests/ConcreteSpecializationEngineTests.cs` |
| 7 | `EmitterTests/ConstrainedExistentialBridgeTests.cs` |
| 8 | `EmitterTests/ObjCRootedInheritedPropertyDriftTests.cs` |
| 9 | `EmitterTests/ClassObjCRootedTests.cs` |
| 10 | `EmitterTests/SwiftUIBridgeEmitterTests.cs` |
| 11 | `EmitterTests/TypeSkipPrePassTests.cs` |
| 12 | `EmitterTests/ClassInheritanceEmissionTests.cs` |
| 13 | `EmitterTests/ArraySliceNormalizationEmitterTests.cs` |
| 14 | `EmitterTests/RealityFrameworkRemapFixTests.cs` |
| 15 | `ParserTests/SourceProvenanceTests.cs` |

**Why it exists** (from `bindingtests.md` / collector design): `ReportCollector` is **static process-wide state** (`Start` / `Complete` / `Reset` / skip+emit counters). Parallel tests that touch it race or double-count. The collection attribute is the intentional mitigation — **not optional decoration**.

Also serializes **SwiftUIBridgeEmitterTests** (largest file) with the rest of the collection → **throughput cost** for the biggest theater suite.

---

## 5. Parallel / shared-state hazards

| Hazard | Mechanism | Risk if ignored |
|--------|-----------|-----------------|
| **`ReportCollector` static** | Module report session, skip lists, bridge summary | Cross-test pollution, flaky counts, silent suppressions |
| **`SwiftUIBridgeCollector` static** | `Collect` / `Reset` / `GetCollectedViews` | Dedup state bleeds across Facts without Reset |
| **`ModuleEmissionContext` / emission side tables** | Per-module dictionaries (e.g. method emission symbols); some paths use shared Default | Cross-test claim collisions, structural-claim guards |
| **`[Collection("ReportCollector")]`** | xUnit: one collection = one test class at a time (per collection definition) | Correctness win; **latency** when mega files share the collection |
| **TypeDatabase / XML load in tests** | Some tests `await LoadModuleDatabaseFromFile` | I/O + mutable DB; usually local instances — still avoid static DB |
| **Default xUnit parallelization** | Classes **outside** ReportCollector collection run in parallel | Fine for pure unit tests; unsafe if new static collectors appear without a Collection |
| **Nested `Assert.All` + string Equal** | e.g. SwiftUI bridge report rows | Not theater Contains, but still multiplies assertions |

**Rules of thumb for new tests:**

1. Prefer pure helpers + Equal/True/False (VtableLayout / PublicMethodName style).  
2. If emitting strings is unavoidable, prefer **one semantic invariant** (count of symbols, entry-point hash presence via structured API) over 10 Contains of body fragments.  
3. Any new static collector → **new Collection attribute** (or inject instance).  
4. Never drop `[Collection("ReportCollector")]` from a class that calls `ReportCollector.*` without replacing the isolation mechanism.

---

## 6. Interpretation for audit tracks

| Signal | Implication |
|--------|-------------|
| **AsyncSwift 98% / Proxy 89% / Closure 89%** | Highest theater density — emit-string gold-plating; candidates for structured assertions or BindingTests-only coverage |
| **SwiftUI 10.7k + Collection serial** | Double tax: largest suite *and* forced serial with ReportCollector siblings |
| **BoundGenerics / MemberValidation ~23–25%** | Healthier pattern: gates and reasons, not body dumps |
| **0 Assert.Throws in all top-10** | Almost no exception-path unit coverage in the mega files (throws may live elsewhere) |
| **Contrast suite = 0 Contains** | Proves the codebase *can* write high-signal tests; theater is habit + emitter surface size, not lack of alternatives |

### Suggested follow-ups (non-binding)

1. Cap new Facts in mega files: max **N Contains per test** (e.g. 3) or require a companion Equal on a typed result.  
2. Extract pure classifiers already tested via True/False out of emit-and-grep Facts.  
3. Split `SwiftUIBridgeEmitterTests` by region (init analyzer / async / modifiers / bindings) so ReportCollector-serial cost is less monolythic.  
4. Treat **DoesNotContain anti-patterns** as second-class: prefer a single positive structural assert.

---

## 7. Methodology notes / caveats

- Line counts are **source lines including helpers/boilerplate**, not Fact count.  
- `Assert.Contains(collection, predicate)` is counted as Contains (collection membership, not always string theater). Partition greps show the bulk of mega-file hits are still **string** Contains.  
- `Assert.Equal` on skip reasons / dispatch kinds is **not** theater — desired.  
- Exact Contains for mega files partitioned by first character of string argument + non-string forms (tool result-cap ~200 lines per query).  
- Next file outside top-10 by size was ~1.9k (`ClassInheritanceEmissionTests`) — not re-scored here.

---

**Worker note:** Top-10 unit-test mass is ~47k lines with **aggregate Contains% ≈ 67%**. Extreme density: **AsyncSwiftWrapperTests 98.3%**, **ProtocolProxy 88.6%**, **ClosureEmitterDirect 88.5%**. Contrast suites (`VtableLayoutBuilderTests`, `PublicMethodNameContextTests`, `ProtocolVtableMembersInvariantTests`) run **0 Contains**. Fifteen files share `[Collection("ReportCollector")]` for static-session isolation; that collection includes the single largest theater file.
