# Track A2 — Struct layout / register passing / value witness

**Wave**: 1  
**Date**: 2026-07-15  
**Mode**: Read-only finder+reporter  
**Risk (headline)**: **Medium** — core frozen/non-frozen projection, VWT ownership, optional EI, and eightbyte TypeLowering are largely hardened with BindingTests + unit gates; residual risk clusters in (1) mixed-indirect generic tuple returns, (2) dual-oracle CGFloat/Optional spelling domains, (3) HasFloatFields/HasBoolFields not unwrapping Optional, (4) incomplete frozen-flag demotion for non-struct nested kinds, (5) stale roadmap/fixture comments around inout writeback (now partially fixed and runtime-proven for cdecl blittable frozen).

---

## Executive summary

| Metric | Count |
|--------|------:|
| Confirmed (new) | 1 |
| Candidate | 4 |
| Already-known | 4 |
| Refuted / verified-clean | 5 |
| Degrade-opportunity | 1 |
| Simplification (L4) | 2 |
| Coverage gaps (taxonomy) | see §Shape taxonomy |

**Headline**: The generator’s struct ABI story is three coordinated machines — **projection kind** (frozen POD / ClassWithBufferStruct / ClassWithOpaquePayload), **register lowering** (`TypeLowering` + `AbiFieldLayout`), and **runtime VWT** (`ValueWitnessTable` / `SwiftMarshal` / `SwiftOptional`) — with `SwiftValueLayout` as the intended single spare-bit oracle. The dangerous historical bugs (Optional tag inflation, sub-word optional by-value packing, frozen-as-class wire-buffer retain leaks, mixed float/int eightbyte thunk returns) are largely **closed with fail-closed skips or @_cdecl routing**. What remains is mostly **roadmap-known latents**, **flag/oracle domain drift**, and **fixture gaps** (mixed-indirect tuples, KeyPath `inout` writeback e2e, nested non-struct demotion).

---

## Shape taxonomy × BindingTests coverage

| Shape | Projection / path | Emission gate | BindingTests / unit coverage | Notes |
|-------|-------------------|---------------|------------------------------|-------|
| `@frozen` POD scalars only | C# `struct` Sequential, by-value / SwiftSelf | TypeLowering slots or @_cdecl | **Strong** — `Types/Structs.swift` (`FrozenPoint`, mixed-register suite), `Parameters/Inout.swift` + `ParameterTests.TestIncrementPoint` | Mixed int/float eightbyte decline proven (`MixedSmall`/`WidePair`/…) |
| `@frozen` + ref fields (String/class) | **ClassWithBufferStruct** (class + nested `Buffer`) | `IsFrozenStructProjectedAsClass` | **Strong** — `LeakDetection`, `StructVwtDestroyLeakTests`, `AsyncComplexTypeTests`, SwiftUI `FrozenRefArg` | Wire-buffer `DestroyWireBufferRetains` required |
| Non-`@frozen` struct | **ClassWithOpaquePayload** (SafeHandle, indirect) | `NonFrozenStructHandler` | **Strong** — `LibraryEvolution.swift`, `OrphanedGetterShapes`, reverse-dispatch inout | Opaque pointer ABI; not by-value |
| Non-frozen + sub-word `Bool?` fields | Opaque class (must **not** hit sub-word skip) | `HasSubWordOptionalLayoutMismatch` gates on `IsFrozen` | **Strong** — `EnumDemotionAndOptionalSkip.ToggleOptions` | Prior false-positive skip fixed |
| `@frozen` + sub-word / tag-overpad `Optional` fields (by-value) | Skip type (`IndeterminateStructLayout`) if offsets diverge | `HasSubWordOptionalLayoutMismatch` + TypeSkipPrePass | **Unit + skip path** | Precision Buffer emitter deferred |
| `@frozen` + only tag-adding scalar Optionals | By-value; `AbiFieldLayout` width path | `SwiftValueLayout.HasAppendedOptionalTag` | **Strong** — `FrozenOptionalAbiWidth.FrozenScalarOptionalPair` | Width fence for Int32?/Float? |
| `@frozen` + `Optional<Bool>` (EI decline) | Whole struct → @_cdecl indirect | ClassifyFieldType declines EI optional | **Strong** — `FrozenOptionalBoolHolder` | Decline must not fabricate `{inner},i1` |
| Optional of class / String | EI, size==inner; VWT / specialized paths | `OptionalMarshalClassifier` | **Strong** — runtime SwiftOptional tests + collections | Runtime Mono EI workarounds |
| Optional of non-frozen / complex enum | DecomposedBuffers | `IsDecomposedOptionalType` | Present in optional/enum fixtures | Opaque payload |
| Multi-element bare-generic tuple return `(T,U)` | Per-element `@out` | `IsMultiElementGenericTupleIndirectReturn` | Partial — generic async tuples | Mixed shapes **already-known gap** |
| Mixed/bound-generic tuple return `(T,Int)`, `(Array<T>,T)`, … | Legacy `SwiftIndirectResult` **wrong shape** | Fall-through (no classifier) | **Gap** — no BindingTests max-case | Roadmap medium |
| Named tuple with String elements | CS0029 risk on projection | Fixture removed shape | **Known bug residual** — `Tuples/Named.swift` comment | Generator quality |
| Tuple of class / String params | @_cdecl buffer slot write | Supported path | **Strong** — `TupleOfClassParam.swift` | KeepAlive model |
| Tuple under throws / async throws / Optional element | Effects paths | Wrapper plans | **Strong** — `TupleUnderEffects.swift` + ABI grid | Grid expect-green |
| `inout` primitive | `ref` P/Invoke | Direct | **Strong** — `ParameterTests` | |
| `inout` frozen blittable (cdecl) | stackalloc + **post-call `MarshalFromSwift` writeback** | `EmitCdeclFrozenStructMarshalling` | **Strong** — `TestIncrementPoint` | Roadmap entry **partially stale** |
| `inout` KeyPath-consumer frozen | Worked around (return copy) | KeyPath fixtures avoid inout | **Gap** — still no e2e `inout PointKP` | Fixture comment stale vs cdecl path |
| Empty / 0-size frozen | 0 slots, not indirect | TypeLowering empty layout | Unit TypeLowering | C# disallows 0-size; VWT size used when present |
| Non-copyable (`~Copyable`) | Flag `NonCopyable`; limited surface | ModuleProcessor Escapable∧¬Copyable | `Types/Noncopyable.swift` | VWT trap on copy |
| Cross-module extension on foreign struct | Extension static class (not re-projection) | `CrossModuleExtensionEmitter` | CrossModule fixtures | Extension node lacks `@frozen` → NonFrozen factory then re-route |
| Nested frozen inside frozen | Flatten `AbiFieldLayout` | ComputeAbiFieldLayout recursive | Unit + nested frozen leak tests | |
| Existential stored field in frozen | Flag calc **skips** field | CacluateFlags continue | **Gap** | Candidate mis-flag |

---

## Findings

### DA-W1-A2-001: `HasFloatFields` / `HasBoolFields` do not unwrap `Optional<T>` (or other bound generics)

- **Severity**: P2  
- **Status**: candidate  
- **Confidence**: medium  
- **Lenses**: L1, L5  
- **Reachability**: fixture-reachable  
- **Claim**: `ModuleProcessor.CacluateFlags` only sets `HasFloatFields` / `HasBoolFields` when the stored property’s **named type is exactly** `Swift.Float`/`Swift.Double`/`CGFloat`/`Swift.Bool`, or a nested **struct** record already carries the flag. A field typed `Optional<Float>` / `Optional<Bool>` / `Array<Float>` does **not** set the flag, so `WrapperValidation.HasIncompatibleFields` / `IsSelfTypeCdeclRequired` can under-force `@_cdecl` for instance members on such frozen structs (SwiftSelf by-value register class mismatch risk on Mono/NativeAOT).  
- **Evidence**:
  - `src/Swift.Bindings/src/Parser/ModuleProcessor.cs:313–337` — direct name checks only; no Optional unwrap.
  - `src/Swift.Bindings/src/Emitter/StringEmitter/WrapperValidation.cs:1834–1868` — consumers of the flags for CallConvSwift safety.
  - Counterexample fixture `FrozenScalarOptionalPair` (`BindingTests/.../FrozenOptionalAbiWidth.swift:29–37`) has `Float?` but is only exercised as a free-function by-value round-trip (not as SwiftSelf instance parent).  
- **Probe**: Add `@frozen struct S { var f: Float? }` with instance method; check whether generated self path is CallConvSwift without float-field cdecl gate; compare disassembly / NativeAOT device.  
- **Suggested fixture**: Frozen struct with `Float?` + `Bool?` stored fields and instance methods returning `Self` / mutating; assert wrapper uses cdecl / no GPR/FPR corruption.  
- **Prior art**: none as this specific Optional-unwrap gap; related float-field CallConvSwift work is established in `Types/Structs.swift` mixed-register suite.

---

### DA-W1-A2-002: Frozen demotion only considers nested **structs**, not nested non-frozen enums / existentials

- **Severity**: P2  
- **Status**: candidate  
- **Confidence**: medium  
- **Lenses**: L1, L3  
- **Reachability**: latent (Swift library-evolution usually forbids public `@frozen` containing non-frozen nested ABI; still a generator-side incompleteness)  
- **Claim**: `CacluateFlags` demotes `Frozen` only when a property is `TypeRecordKind.Struct && !Frozen`. Nested **non-frozen / complex enums**, **ProtocolList**, and **IsAny** existentials are either unchecked or skipped entirely (`continue` for non-NamedTypeSpec / IsAny), so a struct can remain `Frozen` + lack `RequiresMemoryManagement` despite resilient or heap-bearing fields. Emission then may attempt by-value Buffer/TypedField layout.  
- **Evidence**:
  - `ModuleProcessor.cs:287–311` — demotion arm is Struct-only; classes only set RequiresMemoryManagement.
  - `ModuleProcessor.cs:224–235` — ProcessStructProperties skips ProtocolList / IsAny without flag impact.
  - `ModuleProcessor.cs:289–290` — non-NamedTypeSpec properties skipped in flag walk.  
- **Probe**: Construct ABI JSON / fixture with `@frozen` outer + non-frozen enum field if toolchain allows; or force TypeRecord non-frozen enum child and assert demotion.  
- **Suggested fixture**: If product wants defense-in-depth: frozen outer with existential `any P` stored property → expect skip or opaque projection, not POD frozen.  
- **Prior art**: AF13 parser write-backs (roadmap hygiene) — related mutation channel, not this demotion gap.

---

### DA-W1-A2-003: CGFloat / Optional tag domain asymmetry across three oracles (documented dual-oracle)

- **Severity**: P2  
- **Status**: candidate (L4 simplification + latent L1 if CoreGraphics spelling reaches wrong consumer)  
- **Confidence**: high (asymmetric domains are unit-pinned)  
- **Lenses**: L4, L5, L1  
- **Reachability**: emission-live for both CGFloat modules in Apple graphs  
- **Claim**: Spare-bit / size truth is intentionally split across domains that are **not** complements:
  - `SwiftValueLayout.HasAppendedOptionalTag` — qualified-strict, **both** `CoreFoundation.CGFloat` and `CoreGraphics.CGFloat`.
  - `OptionalMarshalClassifier.GetSwiftTagByteOffset` — CoreFoundation + bare `CGFloat`, **not** CoreGraphics.
  - `AppleFrameworkRegistry.IsCGFloat` — both modules (field float flag + AbiFieldLayout).  
  Documented in `SwiftValueLayout.cs:50–59` and pinned by `SwiftValueLayoutTests` (CoreGraphics deliberately false for fixed-width size). Risk: any new consumer that mixes “tag-adding?” with “fixed-width size?” without reading the domain remarks can mis-size Optional\<CGFloat\> under one spelling.  
- **Evidence**: `SwiftValueLayout.cs:61–68`, `OptionalMarshalStrategy.cs:180–189`, `SwiftValueLayoutTests.cs:196–199, 258`.  
- **Probe**: Emit frozen Buffer field `Optional<CoreGraphics.CGFloat>` cross-compile; assert InlineSize path / no one-word clamp.  
- **Suggested simplification (L4)**: Route all three through `AppleFrameworkRegistry.IsCGFloat` + single tag-adding set; risk class **behavior-preserving** with existing unit pins.  
- **Prior art**: constraints.md “AppleFrameworkRegistry is SSOT”; this is residual incomplete adoption.

---

### DA-W1-A2-004: Mixed-indirect generic tuple returns still fall to legacy `SwiftIndirectResult`

- **Severity**: P1 (when hit) / P2 (product frequency)  
- **Status**: already-known  
- **Confidence**: high  
- **Lenses**: L1, L2  
- **Reachability**: latent (no validation-lib repro; mechanism live)  
- **Claim**: Only **all bare generic** multi-element tuple returns use the correct multi-`@out` shape (`IsMultiElementGenericTupleIndirectReturn` → `TupleHandler.AllElementsAreBareGenericTypeParameter`). Mixed shapes (`(T, Int)`, `(Array<T>, T)`, `(Optional<T>, T)`, …) keep the legacy single `SwiftIndirectResult` layout and mis-bind registers.  
- **Evidence**:
  - Roadmap medium: mixed-indirect generic tuple returns.
  - `MarshallingHelpers.cs:529–542`, `TupleHandler.cs:92–105`, `MethodMarshalPlanBuilder.cs:328–337`, `PInvokeEmitter` / `WrapperEmitter.Return` branches.  
- **Probe**: BindingTests max-case `func f<T>(_ t: T) -> (T, Int)` on device NativeAOT.  
- **Suggested fixture**: As roadmap — bare vs mixed vs bound-generic matrix.  
- **Prior art**: roadmap medium; upstream-issues README Issue 8 closed (bare-generic fixed).

---

### DA-W1-A2-005: `inout` C# writeback for blittable frozen — **partially mitigated**; docs/fixtures lag

- **Severity**: P2 (residual paths) / was P1  
- **Status**: already-known (roadmap low) with **new reachability note: cdecl path fixed + runtime green**  
- **Confidence**: high for cdecl frozen blittable; medium for residual paths  
- **Lenses**: L1, L2  
- **Reachability**: emission-live (cdecl path proven); KeyPath path still workaround  
- **Claim**: Roadmap and `KeyPathFoundation.swift` still claim “C# never reads back.” Current code **does** collect post-call writebacks for cdecl blittable frozen structs and BindingTests **asserts** them. Residual risk is any non-cdecl / projection path that still marshals into a stack buffer without readback (e.g. KeyPath fixtures still avoid `inout`).  
- **Evidence**:
  - Writeback emit: `WrapperEmitter.Marshalling.cs:849–917`, flush `1640–1646`.
  - Signature `ref`: `MethodSignature.cs:737–744`.
  - Runtime: `ParameterTests.cs:49–55` (`IncrementPoint`).
  - Stale: `KeyPathFoundation.swift:73–78`, roadmap low-priority inout row.  
- **Probe**: Re-enable `inout PointKP` on KeyPathConsumer; assert mutation; grep CallConvSwift frozen inout for missing writeback.  
- **Prior art**: roadmap **inout round-trip for blittable structs**; ABI grid “inout writeback observability”.

---

### DA-W1-A2-006: Named tuple + String element projects to CS0029 (`SwiftString.Buffer` vs `SwiftString`)

- **Severity**: P2  
- **Status**: already-known (fixture-suppressed)  
- **Confidence**: high  
- **Lenses**: L1, L3  
- **Reachability**: fixture-reachable (shape deliberately removed from BindingTests)  
- **Claim**: `Tuples/Named.swift` documents generator bug for named tuples containing String; fixture deleted rather than fixed.  
- **Evidence**: `BindingTests/Sources/SwiftBindingsTestLib/Tuples/Named.swift:23–24`.  
- **Suggested fixture**: Reintroduce under skip-or-fix with fail-closed emission if still broken.  
- **Prior art**: BindingTests comment only; not a roadmap row.

---

### DA-W1-A2-007: Sub-word / over-padded Optional layout in by-value frozen structs — fail-closed skip (verified-clean mitigation)

- **Severity**: was P0  
- **Status**: refuted as open defect (mitigation confirmed)  
- **Confidence**: high  
- **Lenses**: L1, L3  
- **Claim**: Rather than emit wrong Sequential layout, `HasSubWordOptionalLayoutMismatch` + TypeSkipPrePass skip the type (`IndeterminateStructLayout`). Non-frozen structs correctly excluded so opaque projection still emits factories (`ToggleOptions`).  
- **Evidence**: `FrozenStructHandler.cs:117–134, 605–686`; `TypeSkipPrePass.cs:110–128`; `EnumDemotionAndOptionalSkip.swift`.  
- **Prior art**: constraints / emission comments; not re-open.

---

### DA-W1-A2-008: Optional EI vs tag-byte — TypeLowering declines spare-inhabitant payloads (verified-clean)

- **Severity**: was P0 (historical over-wide Optional\<Bool\> layout)  
- **Status**: refuted as open defect  
- **Confidence**: high  
- **Lenses**: L1, L4  
- **Claim**: `TypeLowering.LowerOptional` only tag-extends when `SwiftValueLayout.HasAppendedOptionalTag`; Bool/pointer/enum/struct Optionals return null → @_cdecl. Runtime `SwiftOptional` has separate Mono-safe EI fast paths.  
- **Evidence**: `TypeLowering.cs:360–418`; `SwiftValueLayout.cs:14–42`; `SwiftOptional.cs:39–68, 120–149, 270–387`; unit `TypeLoweringTests.LowerReturnType_OptionalBool_DeclinesToCdecl`, `SwiftValueLayoutTests`.  
- **Prior art**: S08 F44 / SwiftValueLayout consolidation notes in prior-art index.

---

### DA-W1-A2-009: ClassWithBufferStruct vs ClassWithOpaquePayload ownership algebra is intentional and consistent in AsyncResultPlan

- **Severity**: n/a  
- **Status**: refuted as inconsistency  
- **Confidence**: high  
- **Lenses**: L1, L5  
- **Claim**: Non-frozen → callback takes ownership; frozen-as-class → carrier needs destroy but different adopt/copy shape; documented that `RequiresMemoryManagement` is **not** set on non-frozen. Matches `PayloadConstructionSemantics` (Copy vs Adopt).  
- **Evidence**: `AsyncResultPlan.cs:55–109`; `SwiftMarshal.cs:200–217`; M0-B runtime map §1.3.  
- **Prior art**: Design binding-structs; async-result-carrier-leak plan (post-1.0 inventory).

---

### DA-W1-A2-010: Cross-module extension `StructDecl.IsFrozen == false` is intentional re-route, not misclassification

- **Severity**: n/a  
- **Status**: refuted  
- **Confidence**: high  
- **Lenses**: L5  
- **Claim**: Extension ABI nodes lack `@frozen`; factory routes to NonFrozenStructHandler then `CrossModuleExtensionEmitter` using **canonical** TypeRecord Frozen flag.  
- **Evidence**: `NonFrozenStructHandler.cs:112–135`.  

---

### DA-W1-A2-011: VWT `AlignmentMask` width corrected (0xFF not 0xFFFF)

- **Severity**: was latent wrong  
- **Status**: refuted (fixed; reserved byte zero today)  
- **Confidence**: high  
- **Evidence**: `ValueWitnessTable.cs:18–22, 132`.  

---

### DA-W1-A2-012: Indeterminate generic value-type Buffer fields fail closed (degrade-opportunity already taken)

- **Severity**: P2 product surface  
- **Status**: degrade-opportunity (already implemented)  
- **Confidence**: high  
- **Lenses**: L3  
- **Claim**: `HasIndeterminateBufferLayout` / `TryResolveReferenceFieldSize` fail closed on per-instantiation sizes (e.g. `ClosedRange<Int>`) instead of guessing IntPtr. Good L3 pattern.  
- **Evidence**: `FrozenStructHandler.cs:99–114, 578–602`; `SwiftValueLayout.cs:219–258`; `WrapperCoverage/AbiSafety.swift`.  

---

### DA-W1-A2-013: `GetSwiftTagByteOffset` lists Bool as size 1 while also used as “tag offset” API name (L4 hazard)

- **Severity**: P3  
- **Status**: simplification  
- **Confidence**: high  
- **Lenses**: L4, L5  
- **Claim**: Name says tag offset; Bool entry is inner **size** and would be wrong as Optional tag offset. Safe today because `IsBlittablePrimitiveSwiftType` excludes Bool before BlittableFastPath. Rename/split APIs to `GetPrimitiveByteSize` vs `GetAppendedOptionalTagOffset` would reduce AI/edit hazard.  
- **Evidence**: `OptionalMarshalStrategy.cs:180–189` vs `CdeclParamMapper.IsBlittablePrimitiveSwiftType` excluding Bool (`:903–919`).  

---

### DA-W1-A2-014: AF13-adjacent parser write-back of `structDecl.IsFrozen` after flag demotion

- **Severity**: P3  
- **Status**: already-known (roadmap AF13 hygiene)  
- **Confidence**: high  
- **Lenses**: L5  
- **Claim**: `structDecl.IsFrozen = (flags & Frozen) != 0` after `CacluateFlags` — necessary so FrozenStructHandler factory matches effective frozenness; still an in-place parser write-back.  
- **Evidence**: `ModuleProcessor.cs:204–205`; roadmap AF13 parser-phase write-backs.  

---

## L3 — Graceful degradation notes

| Area | Behavior | Verdict |
|------|----------|---------|
| Indeterminate Buffer (generic value fields) | Type skip + TypeSkipPrePass member prune | Good fail-closed usability |
| Sub-word optional by-value packing | Type skip | Correct; future precise Buffer would **raise** surface |
| TypeLowering unknown / mixed eightbyte | null → @_cdecl | Good degrade |
| Mixed-indirect tuples | Still emit wrong shape | **Bad** — emit-then-wrong-ABI (integrity issue) |
| Named String tuples | Compile break / fixture removed | Prefer emission skip over CS0029 |
| Non-copyable | Limited / skip surfaces | Acceptable |

---

## L4 — Simplification opportunities

1. **Unify CGFloat + Optional tag domains** onto `AppleFrameworkRegistry.IsCGFloat` + one tag-adding set (DA-W1-A2-003). Risk: behavior-preserving with existing tests.  
2. **Split `GetSwiftTagByteOffset`** into size vs appended-tag oracles (DA-W1-A2-013).  
3. **Do not** merge async emitters (roadmap rejected).  
4. Capability-typed projection model remains deferred (roadmap; 642 `IsFrozen` queries).

---

## L2 — Test honesty notes

| Positive | Gap |
|----------|-----|
| AbiLayoutTripwire vs live Swift MemoryLayout | F44 “frozen-struct ABI fixtures” residual may still want consumer-reroute cases beyond tripwire |
| Mixed-register frozen returns on both arches | Mixed-indirect generic tuple **no** BindingTests |
| Inout frozen writeback runtime-proven | KeyPath still documents old gap; roadmap not updated |
| Optional EI unit + FrozenOptionalAbiWidth | Instance-method SwiftSelf + Optional float fields untested |
| ClassWithBuffer destroy/leak suite | Named tuple String regression untracked as report skip |

---

## Prior art crosswalk (do not re-chase)

| ID | Topic | This track treatment |
|----|--------|----------------------|
| Roadmap medium | Mixed-indirect generic tuples | already-known DA-W1-A2-004 |
| Roadmap low | inout blittable writeback | already-known DA-W1-A2-005; **partial fix noted** |
| Roadmap low | Capability TypeCapabilities | not reopened |
| Roadmap AF13 | Parser IsFrozen write-back | already-known DA-W1-A2-014 |
| DES-STRUCT / DES-VWT | Design | baseline, still accurate for VWT layout |
| AR S08b F44 | SwiftValueLayout + ABI fixtures | layout oracle consolidated; tripwire present; residual consumer fixtures optional |
| Upstream Issue 8 | Multi-result tuple | closed for bare-generic; mixed remains |

---

## File coverage ledger (reviewed-deep for Track A2)

Paths reviewed at branch level for this track (absolute):

| Path | Status |
|------|--------|
| `/Users/wojo/Dev/swift-bindings/src/Swift.Bindings/src/Emitter/StringEmitter/Handler/FrozenStructHandler.cs` | reviewed-deep |
| `/Users/wojo/Dev/swift-bindings/src/Swift.Bindings/src/Emitter/StringEmitter/Handler/NonFrozenStructHandler.cs` | reviewed-deep (factory + cross-module re-route; not every method line) |
| `/Users/wojo/Dev/swift-bindings/src/Swift.Bindings/src/Parser/ModuleProcessor.cs` | reviewed-deep (struct flags, AbiFieldLayout, RegisterStruct) |
| `/Users/wojo/Dev/swift-bindings/src/Swift.Bindings/src/TypeDatabase/SwiftValueLayout.cs` | reviewed-deep |
| `/Users/wojo/Dev/swift-bindings/src/Swift.Bindings/src/Emitter/ThunkEmitter/TypeLowering.cs` | reviewed-deep |
| `/Users/wojo/Dev/swift-bindings/src/Swift.Bindings/src/Marshaler/MarshallingHelpers.cs` | reviewed-deep (frozen/buffer/tuple helpers) |
| `/Users/wojo/Dev/swift-bindings/src/Swift.Bindings/src/Marshaler/TupleHandler.cs` | reviewed-deep (support + bare-generic) |
| `/Users/wojo/Dev/swift-bindings/src/Swift.Bindings/src/Emitter/StringEmitter/Handler/WrapperEmitter.Marshalling.cs` | reviewed-deep (cdecl frozen + inout writeback) |
| `/Users/wojo/Dev/swift-bindings/src/Swift.Bindings/src/Emitter/StringEmitter/WrapperValidation.cs` | reviewed-deep (inout ABI mismatch, incompatible fields, frozen param) |
| `/Users/wojo/Dev/swift-bindings/src/Swift.Bindings/src/Emitter/StringEmitter/OptionalMarshalStrategy.cs` | reviewed-deep |
| `/Users/wojo/Dev/swift-bindings/src/Swift.Bindings/src/Emitter/StringEmitter/Handler/AsyncResultPlan.cs` | reviewed-deep |
| `/Users/wojo/Dev/swift-bindings/src/Swift.Runtime/src/Swift/Runtime/ValueWitnessTable.cs` | reviewed-deep |
| `/Users/wojo/Dev/swift-bindings/src/Swift.Runtime/src/Swift/SwiftOptional.cs` | reviewed-deep (EI/tag paths) |
| `/Users/wojo/Dev/swift-bindings/src/Swift.Runtime/src/Swift/Runtime/InteropServices/SwiftMarshal.cs` | reviewed-deep (wire destroy/copy, optional marshal, blittable) |
| `/Users/wojo/Dev/swift-bindings/src/docs/Design/binding-structs.md` | reviewed |
| `/Users/wojo/Dev/swift-bindings/src/docs/Design/binding-value-witness-table.md` | reviewed |
| BindingTests fixtures under Types/Structs, Optionals, Tuples, Parameters/Inout, Metadata/AbiLayoutTripwire, MemoryManagement, KeyPath/KeyPathFoundation | inventory + spot-read |

**Not line-complete in this track** (handoff): full `NonFrozenStructHandler` emission body, all of `PInvokeEmitter` struct branches, native `SBW_VWTDestroy`, every projection visitor for frozen types, full `SwiftMarshal` (~2k LOC). Recommend Wave 6 runtime line-complete pick up remainder of `SwiftMarshal` / handles.

---

## Suggested synthesis backlog seeds (not implementation)

| Priority | Item |
|----------|------|
| P1 | Mixed-indirect generic tuple classifier + BindingTests matrix (roadmap already) |
| P2 | Unwrap Optional (and bound generics) when computing HasFloatFields/HasBoolFields |
| P2 | Broaden frozen demotion for nested enums / existentials (fail closed) |
| P2 | Unify CGFloat domains (L4) |
| P2 | Update roadmap + KeyPathFoundation comments for cdecl inout writeback; add KeyPath `inout` e2e |
| P3 | Named tuple String skip-or-fix; rename tag-offset API |

---

## Counts & headline (for orchestrator)

- **Risk**: **Medium**  
- **Counts**: confirmed new **1** (DA-001 candidate→treat as strongest new lead), candidates **4**, already-known **4**, refuted **5**, degrade already-in-place **1**, L4 **2**  
- **Headline**: Struct/VWT core is production-hardened (fail-closed layout skips, EI oracle, eightbyte TypeLowering, cdecl frozen inout writeback runtime-green); remaining value is dual-oracle hygiene, HasFloatFields Optional unwrap, nested-kind frozen demotion, and the known mixed-indirect tuple ABI hole—not a greenfield frozen/resilient rewrite.
