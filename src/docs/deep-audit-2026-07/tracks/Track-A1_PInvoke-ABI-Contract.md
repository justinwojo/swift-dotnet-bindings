# Track A1 — P/Invoke ABI Contract + x64 Thunks

| Field | Value |
|-------|--------|
| **Wave** | 1 |
| **Track** | A1 |
| **Date** | 2026-07-15 |
| **Mode** | Read-only (production code not modified) |
| **Risk rating** | **2 / 5** (residual dual-path drift + intentional dual conventions; no emission-live wrong-ABI pair found in sampled BindingTests corpus) |
| **Confidence** | **high** on sampled pairs and contract oracles; **medium** on unsampled emitter corners (async reverse-dispatch, protocol-extension silgen, multi-sret tuples) |
| **Lenses** | L1 primary; L3 emit-then-break; L5 dual paths |

## Scope

ABI contract between:

1. C# `LibraryImport` / `UnmanagedCallConv` / parameter list  
2. Swift `@_cdecl` wrappers and native cdecl→swiftcc thunks (ARM64 + x86_64 SysV)  
3. Direct `CallConvSwift` silgen paths (generics, protocol extensions, SB0001 fallbacks)

Hunt axes: CallConv mismatch, missing/extra params, SwiftSelf, sret/error-register, bool marshalling, generic metadata/PWT order, x86_64 thunk register/return drift.

---

## 1. Method

1. Read methodology / map / prior-art (`00-methodology.md`, `00-codebase-map.md`, `00-prior-art-index.md`).  
2. Inventory emission oracles for P/Invoke signatures and wrapper phase order.  
3. Sample **≥20** concrete C#↔Swift (or C#↔thunk assembly) pairs from BindingTests artifacts.  
4. Mechanically compare EntryPoint, CallConv, param count/types/order, self/error/sret placement.  
5. Flag dual sites that must stay in lockstep; tag already-known roadmap items.

### Artifact sources

| Artifact | Role |
|----------|------|
| `BindingTests/output/SwiftBindingsTestLib.Types.*.cs` | **Primary** current iOS split-file C# (preferred) |
| `BindingTests/output/SwiftBindingsTestLib.arm64.s` | ARM64 thunks |
| `BindingTests/output/SwiftBindingsTestLib.x86_64.s` | SysV x86_64 thunks |
| `BindingTests/output-macos/SwiftBindingsTestLib.Wrapper.swift` | Full `@_cdecl` wrapper source (macOS regen; Swift side of pairs) |
| `BindingTests/output-macos/SwiftBindingsTestLib.cs` | Monolithic macOS C# — **partially stale** vs iOS (see notes) |

iOS `BindingTests/output/` does **not** retain `SwiftBindingsTestLib.Wrapper.swift` (wrapper is compiled into the xcframework). Swift text for pairing was taken from `output-macos/…Wrapper.swift` and thunk `.s` files.

---

## 2. Files actually read (deep)

### Emission / contract

- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/PInvokeEmitter.cs` (return, args, self, error, metadata, entry-point)
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/MethodSignature.cs` (`SignatureHandler` phase loop, `Parameter` bool `MarshalAs`)
- `src/Swift.Bindings/src/Emitter/StringEmitter/CdeclSignatureContract.cs`
- `src/Swift.Bindings/src/Emitter/StringEmitter/CdeclParamMapper.cs` (+ `CdeclLoweringDescriptor.cs` remarks)
- `src/Swift.Bindings/src/Emitter/StringEmitter/PInvokeEmitHelper.cs` (`SelectCallingConvention`, format lines)
- `src/Swift.Bindings/src/Emitter/StringEmitter/WrapperValidation.cs` (`GetCallingConvention`)
- `src/Swift.Bindings/src/Emitter/StringEmitter/WrapperSymbolContractGate.cs`
- `src/Swift.Bindings/src/Emitter/StringEmitter/MethodWrapperEmitter.cs` (phase loop + `EmitGenericStaticDispatchMethod`)
- `src/Swift.Bindings/src/Emitter/StringEmitter/EnumCaseWrapperEmitter.cs`
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/EnumHandler.CaseConstruction.cs` (resultPtr-last C#)
- `src/Swift.Bindings/src/Emitter/StringEmitter/PInvokeHelperEmitter.cs` (metadata accessor CallConvSwift)
- `src/Swift.Bindings/src/Emitter/AbiContractChecker.cs` (CC-001…CC-004)
- `src/Swift.Bindings/src/Marshaler/Projection/MethodMarshalPlanBuilder.cs` (SwiftSelf kinds, parent-metadata injection)
- `src/Swift.Bindings/src/Emitter/ThunkEmitter/{TypeLowering,Arm64ThunkTarget,SysVThunkTarget,ThunkAssemblyEmitter,NativeThunkEmitter}.cs`

### Generated samples (partial list)

- `BindingTests/output/SwiftBindingsTestLib.Types.{SummableInt32,AcceptsSummable,ThrowingStruct,HashAlgorithm,DescribableBox}.cs`
- `BindingTests/output/SwiftBindingsTestLib.{arm64,x86_64}.s`
- `BindingTests/output-macos/SwiftBindingsTestLib.{cs,Wrapper.swift}` (pair text)

---

## 3. Inventory — key emission paths

| Path | CallConv | Entry-point shape | Param phase oracle |
|------|----------|-------------------|--------------------|
| `@_cdecl` method/property/ctor/subscript | `CallConvCdecl` | `SBW_…` | `CdeclSignatureContract.DetermineParameterOrder` on C# (`SignatureHandler`) and Swift emitters |
| Native thunk | `CallConvCdecl` | `thunk_Module_hash` | Same C# phase order; assembly bridges self (x20/r13) / error (x21/r12) / sret |
| Direct silgen / generic free fn | `CallConvSwift` | `$s…` | Swift ABI: sret=`SwiftIndirectResult`, self=`SwiftSelf`, error=`ref SwiftError`, trailing metadata+PWT |
| `@_silgen_name` non-C wrapper | `CallConvSwift` | `SBSW_…` (contract) | Prefix-enforced Swift CC |
| Enum case factory `@_cdecl` | `CallConvCdecl` | `SBW_…_caseHash` | **Intentional dual:** associated values then **`resultPtr` last** (not ResultPtr-first) |
| Metadata accessor `…Ma` | `CallConvSwift` | `$s…Ma` | Thin or buffer-mode helper; not cdecl phase table |

### Defense-in-depth (good)

1. **`PInvokeEmitHelper.SelectCallingConvention`**: `$s…` → force Swift; `SBW_…` + Swift → throw; `SBSW_…` + Cdecl → throw.  
2. **`WrapperSymbolContractGate` / `EnforceWrapperContract`**: refuse SBW_/SBSW_ P/Invokes never registered by wrapper-emit.  
3. **`AbiContractChecker`**: CC-001/002 non-blittable CallConvSwift; CC-003 SBW_ wrong library; CC-004 CallConvCdecl + mangled `$s`.  
4. **`CdeclSignatureContract`**: single phase order for method/property/ctor/subscript (not enum case factories).  
5. **Bool**: `AddParameter("bool")` → `MarshalledType.Bool` → `[MarshalAs(UnmanagedType.U1)]`; returns get `[return: MarshalAs(UnmanagedType.U1)]` in `PInvokeEmitHelper`.  
6. **Cdecl self/error**: plain `IntPtr` / `out IntPtr errorPtr` (not `SwiftSelf`/`SwiftError` under CallConvCdecl).  
7. **Cdecl sret**: plain `IntPtr resultPtr` (not `SwiftIndirectResult` under CallConvCdecl — avoids x0 vs x8 misplacement).

---

## 4. Sampled pairs (≥20) — mechanical comparison

Notation: **MATCH** = EntryPoint family, CallConv, arity, and role order agree.

### A. Free `@_cdecl` (Swift wrapper ↔ C# module free funcs)

| # | Symbol / method | C# signature (essence) | Swift `@_cdecl` essence | Verdict |
|---|-----------------|------------------------|-------------------------|---------|
| 1 | `acceptsAnyDescribable` | `void(resultPtr, item)` Cdecl, `SBW_…E3D925D9` | `(resultPtr, item: UnsafeRawPointer)` | MATCH |
| 2 | `makeDescribable` | `void(resultPtr, text_w0, text_w1)` | `(resultPtr, _sW0_text, _sW1_text)` | MATCH (2-word String) |
| 3 | `acceptsComposition` | `void(resultPtr, item)` | same | MATCH |
| 4 | `makeIdentifiableDescribable` | `void(resultPtr, id_w0/1, text_w0/1)` | 4× Int words | MATCH |
| 5 | `describeAll` | `void(resultPtr, items)` | array buffer ptr | MATCH |
| 6 | `makeRefPair` | `void(resultPtr, coord, label)` class ptrs | `Unmanaged` takeUnretained | MATCH |
| 7 | `acceptOptionalFrozenPoint` | `void(resultPtr, pointBuffer)` | Optional frozen load | MATCH |
| 8 | `makeOptionalFrozenPoint` | `void(resultPtr, double, double, bool U1)` | `returnNil: Int8` | MATCH (bool) |
| 9 | `makeOptionalBool` | `void(resultPtr, bool U1, bool U1)` | two `Int8` | MATCH |
| 10 | `acceptOptionalNonFrozenPoint` | `void(resultPtr, pointBuffer)` | optional opaque ptr map | MATCH |

### B. Instance / property `@_cdecl`

| # | Type / member | C# | Swift | Verdict |
|---|---------------|----|-------|---------|
| 11 | `SummableInt32.value` get | `int(_selfFixed)` → `SBW_Get_…_value` | `(_ self_: UnsafeRawPointer) -> Int32` | MATCH |
| 12 | `SummableInt32.init` | `void(resultPtr, int)` | `(resultPtr, value: Int32)` | MATCH |
| 13 | `SummableInt32.add` | `void(resultPtr, other, _selfFixed)` | `(resultPtr, other, self_)` | MATCH (args → self) |
| 14 | `AcceptsSummable.item` get (GSF) | `void(resultPtr, TMetadata, TSummablePWT, _self)` | `(resultPtr, _metadata0, _pwt0, self_)` | MATCH (meta before self) |
| 15 | `AcceptsSummable.init` GSF | `void(resultPtr, item, meta, pwt)` | same order | MATCH |
| 16 | `AcceptsSummable.addWith` | `void(resultPtr, other, meta, pwt, _self)` | same | MATCH |

### C. Thunk path (C# + arm64.s + x86_64.s)

| # | Member | C# P/Invoke | ARM64 thunk | SysV thunk | Verdict |
|---|--------|-------------|-------------|------------|---------|
| 17 | `throwDemoMissing` | `void(out errorPtr)` → `thunk_…15960cdb` | save x0→x19, clear x21, bl, str error | rdi→stack, r12 error | MATCH |
| 18 | `throwDemoTruncated` | same shape `thunk_…8db3b13f` | same | same | MATCH |
| 19 | `divide(a,b)` | `int(a,b,out errorPtr)` `thunk_…fe0427fa` | error at x2, args x0/x1 | error %rdx, args edi/esi | MATCH |
| 20 | `ThrowingStruct.divideBy` | `int(divisor, _self, out errorPtr)` `thunk_…e46abcff` | x20=self(x1), x19=error(x2), x0=arg | r13=self(%rsi), error %rdx | MATCH |
| 21 | `ThrowingStruct.validatePositive` | `int(_self, out errorPtr)` | x20=x0 self, error x1 | r13=rdi, error rsi | MATCH |
| 22 | `isHorizontal` (enum) | `bool(int)` + U1 return, thunk | arm64: tail `b` | x86_64: `movzbl %al` zero-extend | MATCH (SysV enum widen) |
| 23 | Large scalar sret free fn | (thunk `4a9efe53`) | — | `movq %rdi,%rax; movq %rsi,%rdi; jmp` (SysV sret shift) | MATCH design |

### D. Direct CallConvSwift

| # | Member | C# | Notes | Verdict |
|---|--------|----|-------|---------|
| 24 | `sumTwo` generic | `void(SwiftIndirectResult, aPayload, bPayload, TMetadata, TSummablePWT)` CallConvSwift `$s…` | sret + payloads + meta + PWT | MATCH shape |
| 25 | `describeConstrained` | `SwiftString.Buffer(item, TMetadata, …PWT)` CallConvSwift | SB0001 obsolete surface | MATCH shape |
| 26 | `DescribableBox` Ma | `TypeMetadata(request, tMeta, pwt)` CallConvSwift `$s…Ma` | current iOS split | MATCH |
| 27 | HashAlgorithm.sha2 case | `void(int variant, IntPtr resultPtr)` Cdecl | Swift: `(variant, resultPtr)` **last** | MATCH dual convention |
| 28 | HashAlgorithm.custom case | `void(int rounds, resultPtr)` | same | MATCH |

**Headline from sampling:** no emission-live arity/CallConv/sret/self/error mismatch found among the 28 shapes above. The ABI contract machinery is largely doing its job on BindingTests.

---

## 5. Confirmed findings

### DA-W1-A1-001: Enum case factories use `resultPtr`-last dual convention outside `CdeclSignatureContract`

- **Severity**: P2  
- **Status**: confirmed (hazard / dual-oracle — not a runtime mismatch today)  
- **Confidence**: high  
- **Lenses**: L1 (if unified incorrectly), L4, L5  
- **Reachability**: emission-live (BindingTests `HashAlgorithm`, media enums, etc.)  
- **Claim**: Method/property/ctor `@_cdecl` wrappers put indirect result **first** (`CdeclSignatureContract`). Enum case factories deliberately put `resultPtr` **last** on both Swift (`EnumCaseWrapperEmitter.cs:252–253`) and C# (`EnumHandler.CaseConstruction.cs:760`). Both sides agree; a future “unify all cdecl phases” edit that forces ResultPtr-first on case factories without updating the other side would silently wrong-ABI.  
- **Evidence**:
  - Swift: `public func _sbw_case_sha2_…(_ variant: Int32, _ resultPtr: UnsafeMutableRawPointer)`  
  - C#: `PInvoke_Sha2(int variant, IntPtr resultPtr)` → `SBW_…_HashAlgorithm_sha2_…`  
- **Probe**: already green in BindingTests enum case factories; refute by finding a case factory with resultPtr-first on only one side.  
- **Suggested fixture**: keep existing HashAlgorithm / PlaybackMode case factories; add a unit assert that case-factory P/Invoke parameter order ends with `resultPtr` and does **not** use `CdeclSignatureContract` ResultPtr-first.  
- **Suggested simplification (L4)**: either (a) document as permanent second contract with a named `EnumCaseCdeclSignatureContract`, or (b) migrate both sides to ResultPtr-first under a single gate + BindingTests. Risk class: **needs fixture**.  
- **Prior art**: none as open P0; related to dual-oracle theme in constraints/AF work.

### DA-W1-A1-002: Generic static-dispatch method wrappers hand-roll phase order

- **Severity**: P2  
- **Status**: confirmed (dual-path hazard; sampled AcceptsSummable pairs match)  
- **Confidence**: high  
- **Lenses**: L4, L5  
- **Reachability**: emission-live (`AcceptsSummable`, other GSF types)  
- **Claim**: Non-generic `MethodWrapperEmitter` loops `CdeclSignatureContract.DetermineParameterOrder`. `EmitGenericStaticDispatchMethod` **reimplements** `[ResultPtr][Arguments][Metadata][Self][ErrorOut]` by hand (`MethodWrapperEmitter.cs:993–1130`) with comments claiming parity. Today AcceptsSummable C#/Swift pairs match; the dual implementation is still a drift surface.  
- **Evidence**: `MethodWrapperEmitter.cs:381` vs `:993–1130`; sample pairs 14–16 above.  
- **Probe**: force a new phase (e.g. extra synthetic) into `CdeclSignatureContract` only — GSF path would desync.  
- **Suggested simplification**: route GSF through the same phase loop + shared param builders. Risk: **behavior-preserving** if tests cover throws + meta + self.  
- **Prior art**: dual-oracle theme; not a roadmap latent.

### DA-W1-A1-003: Swift `CdeclParamMapper` vs C# `PInvokeEmitter.HandleArguments` are dual classifiers by design

- **Severity**: P2  
- **Status**: confirmed (architectural dual oracle; intentional)  
- **Confidence**: high  
- **Lenses**: L4, L5  
- **Reachability**: emission-live (all cdecl methods)  
- **Claim**: `CdeclLoweringDescriptor` remarks (`CdeclLoweringDescriptor.cs:65–74`) state C# `MarshalledType` is **not** folded into the Swift descriptor; multi-word names (String `_w0/_w1`, RawBuffer Ptr/Len) agree only by **recomputation**. That is correct today for sampled pairs but remains the highest-volume dual-oracle surface for silent ABI drift on new type categories.  
- **Evidence**: descriptor remarks + parallel branches in `CdeclParamMapper.Describe` and `PInvokeEmitter.HandleArguments` (SIMD, Data decompose, String decompose, frozen struct IntPtr, etc.).  
- **Suggested fixture**: when adding a new cdecl-lowered type, require paired unit tests asserting Swift param text **and** C# P/Invoke param list.  
- **Suggested simplification**: share multi-word name contracts as pure functions both sides call (partial unification already for reserved labels). Risk: **behavior-preserving** for naming only.  
- **Prior art**: S08a F9 (CdeclLoweringDescriptor fields) in backup — related; not the same claim.

---

## 6. Candidates

### DA-W1-A1-C01: `ComputeEntryPoint(MethodDecl)` pre-AF13 overload still present

- **Severity**: P2  
- **Status**: candidate  
- **Confidence**: medium  
- **Lenses**: L5  
- **Claim**: AF13 moved promotion to `MethodEnvironment.EmissionSymbol`. `PInvokeEmitter.ComputeEntryPoint(MethodDecl)` still exists and reads `NameProvider.GetMangledName(methodDecl)` for wrapper paths. If any caller still uses the decl-only overload for emission, entry points could ignore promotion. Grep shows limited remaining use; main emit uses the env overload.  
- **Probe**: `grep ComputeEntryPoint` + ensure production emit only hits env overload; delete or obsolete decl overload.  
- **Prior art**: AF13 (constraints.md emission-symbol side table).

### DA-W1-A1-C02: macOS monolithic generated C# can lag iOS split (stale CallConv evidence)

- **Severity**: P3 (process / gate honesty)  
- **Status**: candidate  
- **Confidence**: medium  
- **Lenses**: L2  
- **Claim**: `output-macos/SwiftBindingsTestLib.cs` still contained `CallConvCdecl` + `$s…DescribableBoxO4wrap…` `PInvoke_Wrap`, while current `output/SwiftBindingsTestLib.Types.DescribableBox.cs` correctly **skips** Wrap (GenericEnumCaseConstructor) and only emits Ma under CallConvSwift. Audits that sample only macos monolithic risk false P0s.  
- **Probe**: timestamp compare / re-run `nuke binding-tests --macos`.  
- **Prior art**: none.

### DA-W1-A1-C03: TypeLowering is swiftcc/ARM64-shaped; SysV relies on decline gates

- **Severity**: P2  
- **Status**: candidate  
- **Confidence**: medium  
- **Lenses**: L1, L5  
- **Claim**: `TypeLowering` docs and slot model are ARM64/swiftcc-centric; x86_64 correctness depends on `SmallStructReturnDivergesFromCAbi`, `SysVThunkTarget.CanEmit` (≤6 int regs), and return zero-extend for enum tags. Residual unthunked edges are forced to `@_cdecl` (safe). No mismatched live thunk found; residual risk is an incomplete decline predicate.  
- **Probe**: expand SysV unit matrix for HFA-adjacent and 17–32B returns already unit-tested; add BindingTests free function returning `{Float,Float}` if not covered.  
- **Prior art**: thunk unit tests `SysVThunkTargetTests`, `NativeThunkEmitterTests`.

### DA-W1-A1-C04: Direct CallConvSwift SB0001 surfaces remain crash-class if misused

- **Severity**: P1 (product surface) / already mitigated with Obsolete  
- **Status**: candidate (product/degrade, not new ABI bug)  
- **Confidence**: medium  
- **Lenses**: L1, L3  
- **Claim**: Methods that cannot wrap (e.g. `describeConstrained`, some generic helpers) still emit CallConvSwift P/Invokes marked `[Obsolete(..., DiagnosticId = "SB0001")]`. Correct when types are blittable; still the historical crash class under Mono/NativeAOT for non-blittable shapes. AbiContractChecker covers some of this.  
- **Prior art**: upstream issues 1–2; BindingAudit “compile but dead” is EveryProtocol (different track).

---

## 7. Already-known (linked — do not re-chase as novel P0)

| ID | Title | Link | A1 note |
|----|-------|------|---------|
| Roadmap blocked 1–4 | Mono CallConvSwift async assertion; non-blittable CallConvSwift; Set.insert DONE_BLOCKING; Catalyst x64 | `roadmap.md` + `upstream-issue-0{1..4}` | Explains residual direct Swift-CC surface; not generator pairing bugs |
| R6-2 | Device-thunk retention integration test (infra) | `roadmap.md` latent | Thunk **filter** harness gap; pairing logic itself unit-tested |
| AF13 | Emission symbol side table / no `MangledName` mutation | constraints.md | Entry-point promotion model verified in `ComputeEntryPoint(MethodEnvironment)` |
| S08a F9 | CdeclLoweringDescriptor fields populated-but-unread | prior-art AR-SESS | Related to dual-classifier inventory |
| Optional\<ObjC value\> closure args | CallConvSwift reverse-closure wrong read | roadmap medium | A4 primary; A1 notes CC choice only |
| Internal-receiver | Sync fallback CallConvSwift / async drop | roadmap RESOLVED | Emission-gated; not ABI dual-emit mismatch |

---

## 8. Refuted / checked clean (this track)

| Hypothesis | Result |
|------------|--------|
| Widespread CallConvCdecl on `$s…` mangled entry points | **Refuted** for current iOS output; `SelectCallingConvention` + CC-004; metadata accessors explicitly CallConvSwift |
| Cdecl uses `SwiftSelf` / `SwiftError` (wrong register under cdecl) | **Refuted** — free-function style `IntPtr` self + `out IntPtr errorPtr` when UsesCdecl/UsesNativeThunk |
| Cdecl uses `SwiftIndirectResult` (x8 vs x0) | **Refuted** — `IntPtr resultPtr` under UsesCdeclWrapper |
| Bool missing `MarshalAs(U1)` on main method path | **Refuted** in sampled pairs; central `MarshalledType.Bool` + return attribute |
| Throwing free/instance thunks mis-place error/self on arm64 or SysV | **Refuted** for divide / throwDemo / ThrowingStruct.divideBy |
| AcceptsSummable GSF metadata/PWT/self order mismatch | **Refuted** for item/init/addWith |
| Enum case resultPtr first on one side only | **Refuted** — both last |
| String two-word vs UTF-8 property path confusion | **Checked intentional**: methods/ctors two-word; property wrappers UTF-8 ptr+len |

---

## 9. Coverage gaps (A1 did not fully reach)

- Async method `@_cdecl` harness (callback, cancel key, task handle ordering) — Wave A7.  
- Protocol extension silgen dual TypeMetadata (explicit + implicit trailing) end-to-end pairs.  
- Multi-element generic tuple multi-sret (`tupleResult{i}Ptr`) live pairs.  
- Closure cdecl funcPtr+context dual path vs async start-thunk (A4).  
- Witness-dispatch / EveryProtocol reverse P/Invokes (A5).  
- ObjC companion / mixed pack P/Invoke surface.  
- Property/subscript setter decomposed Optional (`payload` + `hasValue`) full matrix.  
- Full `nuke binding-tests --device` NativeAOT re-validation of thunks (infra uses unfiltered `.arm64.s` per R6-2).  
- Systematic CC-004 scan of **all** generated C# (only samples + spot DescribableBox).  
- `MethodMarshalPlanBuilder` body emission vs signature (lifetime/plan, not full L1).

---

## 10. Recommended BindingTests fixtures (no code fixes)

1. **Case-factory phase lock**: assert generated C# case factory ends with `resultPtr` and matching Swift last param (HashAlgorithm.sha2 already live — make order explicit in unit test).  
2. **GSF phase lock**: AcceptsSummable-style generic parent method with **throws** + self + one meta + one PWT — order must match CdeclSignatureContract regular layout.  
3. **SysV return zero-extend**: keep/expand enum tag returns that hit `movzbl` (isHorizontal-style).  
4. **Cdecl vs Swift sret pair**: one method that must use `IntPtr resultPtr` under cdecl and a sibling that still uses `SwiftIndirectResult` under CallConvSwift — document difference so refactors do not collapse them.  
5. **Throwing instance frozen/non-frozen**: already have ThrowingStruct; add **class** throwing instance if missing (self is class pointer under cdecl IntPtr).  
6. **Artifact hygiene**: prefer iOS split `Types.*.cs` + retained wrapper swift (or nm symbols) in audits; do not treat stale macos monolith as oracle.

---

## 11. L3 degrade notes

- Prefer **skip at emission** (GenericEnumCaseConstructor on generic enums) over emitting a dangling `$s` case factory — **already done** for DescribableBox.Wrap. Good L3 example.  
- SB0001 Obsolete CallConvSwift methods are fail-loud-ish for consumers but still compile; G1 should track whether these should be non-public / EditorBrowsable-only for partial-success packages.  
- Wrapper symbol contract throw is integrity fail-closed (correct); do not weaken for usability.

## 12. L4 simplification notes

| Opportunity | Risk class | Do not do if… |
|-------------|------------|----------------|
| Fold GSF method/property phase assembly into `CdeclSignatureContract` loop | behavior-preserving + fixtures | GSF needs different Self mutability encoding without tests |
| Named second contract for enum case factories | documentation / byte-identical | “just use ResultPtr-first” without dual-side migrate |
| Shared multi-word cdecl name helpers for String/Data/RawBuffer | behavior-preserving | forcing full MarshalledType↔CdeclLowering merge (explicitly rejected in code remarks) |
| Delete or obsolete `ComputeEntryPoint(MethodDecl)` | low | any remaining external/tooling callers |

---

## 13. Summary scores

| Metric | Value |
|--------|-------|
| **Risk rating** | **2 / 5** |
| **# confirmed** | **3** (all dual-path / hazard class; **0** emission-live wrong-ABI pair) |
| **# candidate** | **4** |
| **# already-known tagged** | **6** themes |
| **Headline issue** | **No live CallConv/arity/sret/self/error mismatch in ≥20 BindingTests pairs; residual risk is dual-oracle phase/type classifiers (enum case resultPtr-last, GSF hand-rolled phases, CdeclParamMapper vs PInvokeEmitter) rather than a single broken ABI path.** |

---

## Ledger files reviewed-deep

```
src/Swift.Bindings/src/Emitter/StringEmitter/Handler/PInvokeEmitter.cs
src/Swift.Bindings/src/Emitter/StringEmitter/Handler/MethodSignature.cs
src/Swift.Bindings/src/Emitter/StringEmitter/CdeclSignatureContract.cs
src/Swift.Bindings/src/Emitter/StringEmitter/CdeclParamMapper.cs
src/Swift.Bindings/src/Emitter/StringEmitter/CdeclLoweringDescriptor.cs
src/Swift.Bindings/src/Emitter/StringEmitter/PInvokeEmitHelper.cs
src/Swift.Bindings/src/Emitter/StringEmitter/WrapperValidation.cs
src/Swift.Bindings/src/Emitter/StringEmitter/WrapperSymbolContractGate.cs
src/Swift.Bindings/src/Emitter/StringEmitter/MethodWrapperEmitter.cs
src/Swift.Bindings/src/Emitter/StringEmitter/EnumCaseWrapperEmitter.cs
src/Swift.Bindings/src/Emitter/StringEmitter/Handler/EnumHandler.CaseConstruction.cs
src/Swift.Bindings/src/Emitter/StringEmitter/PInvokeHelperEmitter.cs
src/Swift.Bindings/src/Emitter/AbiContractChecker.cs
src/Swift.Bindings/src/Marshaler/Projection/MethodMarshalPlanBuilder.cs
src/Swift.Bindings/src/Emitter/ThunkEmitter/TypeLowering.cs
src/Swift.Bindings/src/Emitter/ThunkEmitter/Arm64ThunkTarget.cs
src/Swift.Bindings/src/Emitter/ThunkEmitter/SysVThunkTarget.cs
src/Swift.Bindings/src/Emitter/ThunkEmitter/ThunkAssemblyEmitter.cs
src/Swift.Bindings/src/Emitter/ThunkEmitter/NativeThunkEmitter.cs
src/Swift.Bindings/src/Parser/ManglingProbes.cs
```

Generated artifacts sampled (not production ledger):  
`BindingTests/output/SwiftBindingsTestLib.Types.{SummableInt32,AcceptsSummable,ThrowingStruct,HashAlgorithm,DescribableBox}.cs`,  
`BindingTests/output/SwiftBindingsTestLib.{arm64,x86_64}.s`,  
`BindingTests/output-macos/SwiftBindingsTestLib.{cs,Wrapper.swift}`.
