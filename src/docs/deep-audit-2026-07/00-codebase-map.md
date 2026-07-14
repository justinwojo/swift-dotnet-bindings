# Codebase Map — Deep Audit 2026-07

**Wave**: 0 synthesis  
**Date**: 2026-07-15  
**Sources**: [M0-A](waves/W0-map/M0-A-generator-pipeline.md), [M0-B](waves/W0-map/M0-B-runtime-map.md), [M0-C](waves/W0-map/M0-C-build-sdk-gates.md), [M0-D](waves/W0-map/M0-D-test-landscape.md), [prior-art](00-prior-art-index.md), [ledger](00-file-coverage-ledger.md)

This is the architecture-level map. Deep waves use it for targeting; they do not re-derive the pipeline from scratch.

---

## 1. What the tool is (one paragraph)

SwiftBindings turns compiled Swift (xcframework + ABI JSON, optional `.swiftinterface` / symbol graph) into C# bindings + optional `@_cdecl` wrapper / SwiftUI bridge / ObjC companion for .NET 10 on Apple platforms. The **generator** admits or skips each public member, emits C# + Swift glue, then the **SDK/Nuke** package and validate paths compile wrappers, pack NuGet, and run BindingTests (sim Mono JIT / device NativeAOT). The **runtime** (`Swift.Runtime` + native `SwiftBindingsRuntime`) owns ARC, existentials, VWT, collections, and async/closure trampolines.

---

## 2. Scale (ledger-accurate)

| Surface | Files | LOC |
|---------|------:|----:|
| Generator source | 435 | 220,677 |
| Generator **unit tests** | 394 | **305,288** |
| Runtime managed | 100 | 20,709 |
| Runtime tests | 44 | 9,735 |
| SDK | 10 | 5,160 |
| Nuke/build | 65 | 26,234 |
| BindingTests Swift + C# | 699 | 104,311 |
| Other (Apple, analyzers, tools, rules) | 52 | ~12,800 |
| **Total in-scope** | **1,799** | **~705k** |

**Critical observation for L2/L4:** unit tests alone exceed generator source LOC. Several single test files are larger than production mega-files (e.g. `SwiftUIBridgeEmitterTests.cs` ~10.7k). Wave 8 must treat **test mass vs test meaning** as a first-class problem, not a footnote.

---

## 3. Pipeline (normal generate)

```text
CLI → resolve inputs (xcframework / -a/-d/-t)
    → optional mixed ObjC pre-parse (fail-closed on systemic ObjC failure)
    → TypeDB bootstrap + TBD demangle + interface facts
    → SwiftABIParser → ModuleProcessor → Freeze TypeDB
    → ModuleEmissionContext + MarshalingContext + CSM engine
    → EmitModule (TypeSkipPrePass → handlers → wrappers → reverse dispatch)
    → reports + WrapperSymbolIntegrityGate
    → optional wrapper compile + strip co-gater
    → optional ObjC companion emit
    → project / consumer targets / metadata
```

**SDK two-pass:** pass 1 generate with `--skip-wrapper-compilation`; pass 2 `--compile-wrapper-only` after ProjectReferences resolve. Consumer `NativeReference` must use **will-be-produced** signal, not “exists now.”

Detail: [M0-A §1–2](waves/W0-map/M0-A-generator-pipeline.md).

---

## 4. Complexity heat map

### Production T0 (branch-level only — split agents by *subsystem*, not “whole file”)

| File / cluster | LOC | Wave tracks |
|----------------|----:|-------------|
| EveryProtocolEmitter | 7.4k | A5 |
| ProtocolProxy Receivers / InterfaceImpl / StaticInit / Vtables | multi-k | A5 / A5b / A5c |
| SwiftUIBridgeEmitter* | 4.2k+ | M1 |
| SwiftABIParser | 4.2k | A8 |
| ConcreteProtocolSpecializationEmitter | 3.6k | A6 |
| Sdk.targets | 3.8k | M2 / G1 |
| Swift5Demangler | 3.4k | A8 |
| Build.RuntimeTests | 3.5k | B1 |
| SwiftWrapperCompiler | 3.0k | M2 / G1 |
| WitnessDispatch / WrapperValidation / MethodHandler / NameProvider | 2–2.6k | A1 / A5 / C2 |
| SwiftMarshal / ExistentialContainer / TypeMetadata | runtime T0 | Wave 6 |

### Intertwining (highest dual-oracle risk)

1. **Vtable slot layout** (`VtableLayout`) vs **projected C# method keys** vs **emitted signature dedup** — three axes; conflating them is crash-class  
2. **TypeSkipPrePass** vs handler-level skip — report honesty  
3. **Wrapper eligibility / strip / co-gater / integrity gate** — admission vs post-hoc repair  
4. **CompileWrapperForArchitectures** + SDK compile-wrapper-only + consumer targets “will produce”  
5. **Mixed ObjC** `IsMixedFramework` / bridge rekey / companion packaging lockstep  
6. **IsOptionalObjCBridged** vs `TypeProjectionFactory` parity  

---

## 5. Graceful degradation (G1 seed from Wave 0)

### What already exists (good)

| Mechanism | Role |
|-----------|------|
| `MemberValidationPipeline` + handler gates | Skip unbindable members with `SkipReason` |
| `TypeSkipPrePass` / type-level skips | Avoid emitting impossible types |
| Wrapper strip + C# co-gater | Recover from uncompilable wrapper blocks |
| `binding-report.json` / emission report / skip triage | Consumer-visible “what’s missing” |
| Compile-only fail-closed on *integrity* (wrapper give-up, generator non-zero when strict) | CI honesty |
| Suppressed-proxy / produce-throw / SB000x patterns | Prefer fail-loud over silent wrong |

### What G1 must still map (Wave 7 + threaded)

| Risk class | Question |
|------------|----------|
| **Emit-then-break** | Which paths still emit C#/Swift that cannot compile? |
| **Hard exit on partial** | Which SWIFTBIND*/MSBuild errors kill the whole package for one bad member? |
| **Compile-but-dead** | Public APIs that compile but always throw / never fire (BindingAudit EveryProtocol theme — already-known; track residual) |
| **ObjC systemic fail** | Mixed parse fail-closed → no Swift-only binding — intentional? acceptable for day-1 new lib? |
| **SDK vs CLI** | Does `dotnet build` on a binding project leave usable outputs after partial skip? |
| **Sharpie analogue** | Skip report + compile-clean package as the “edit surface,” not ApiDefinitions |

**Integrity must stay hard:** plan/emit symbol mismatch, false wrapper metadata, TN2435 packaging lies, RuntimeContract fraud.

---

## 6. Simplification seed (L4 / S1)

From Wave 0 maps (candidates only — not confirmed safe consolidations):

| Theme | Notes |
|-------|--------|
| Dual oracles → one core | Projected keys already partially unified (AF05); vtable hand-allocators still separate |
| ExistentialContainer0..8 | ~copy-paste structs (post-1.0 roadmap already lists) |
| Parallel async emitters | **Merge rejected** by roadmap — L4 may extract *exact* duplicates only |
| Mega unit tests | 10k-line test files; likely string-blob / exhaustiveness bloat — Wave 8 L2+L4 |
| Mega Nuke targets | Build.RuntimeTests / Validation / Sdk.targets decomposition (mechanical) |
| Static collectors | ReportCollector / SwiftUIBridgeCollector → PipelineContext (post-1.0 list) |
| TypeSpec translators | Five call-site policies — partial centralization already studied; don’t force-merge policy |
| Mono/AOT dual factories | Collections — behavior-preserving template only |

S1 rollup in Wave 10; do not implement during audit.

---

## 7. Test landscape (L2 seed)

| Layer | Proves | Does not prove |
|-------|--------|----------------|
| Unit tests (~305k LOC) | Generator logic, naming, skip disposition, many emission shapes | Real ABI / CallConv / device |
| BindingTests sim | Mono JIT marshalling | NativeAOT-only bugs |
| BindingTests device | NativeAOT + CallConv edges | macOS-only / Catalyst-x64 |
| compile-only | Regen + C# compile + strip tripwire | Runtime correctness |
| mixed-pack / mixed-direct / appstore-hygiene | Packaging / single registration / TN2435 | Everyday inner loop |

**Skip honesty doctrine:** only 4 confirmed upstream Mono issues; everything else is ours until proven. `[MonoJitCrash]` is dead.

**Graceful degradation test gap (M0-D):** skip disposition unit tests exist; weaker coverage of “new library → compile-clean partial binding” as a product scenario.

Detail: [M0-D](waves/W0-map/M0-D-test-landscape.md).

---

## 8. Prior art (do not re-chase)

See [00-prior-art-index.md](00-prior-art-index.md).

Headline already-known:

- EveryProtocol compile-but-dead class (BA/BSA)  
- Roadmap latents with zero emission site  
- Async-emitter merge rejected  
- Input-poor thesis for *new P0 yield*, not for map completeness  
- 0.18 path signed (not 1.0 contract)  
- R1–R6 refuted log in SB-Backup  

---

## 9. Wave 1+ track splits (finalized)

| Wave | Tracks | Agent split notes |
|------|--------|-------------------|
| **1** | A1, A2, A3 | 2–3 tracks parallel max; A1 samples BindingTests wrappers |
| **2** | A5, A5b, A5c | **Three agents on reverse-dispatch cluster** — never one agent on whole EveryProtocolEmitter |
| **3** | A6, M3 | CSM + TypeDB; tag already-known roadmap rows |
| **4** | A4, A7 | Closures; async *divergence* not merge proposal |
| **5** | A8 | Parser + demangler + interface facts |
| **6** | Runtime line-complete | Feasible true depth (~21k managed) |
| **7** | M2, **G1**, B1 | G1 is owner-priority graceful degradation mini-audit |
| **8** | T1–T4 | Unit mass honesty + BindingTests + gates |
| **9** | M1, L2 ObjC, AP1 | Secondary full-surface |
| **10** | C1, C2, **S1**, L1 docs | Hazard map + simplification catalog |
| **11** | Synthesis | Backlog + degrade map + simplification + exec summary |

---

## 10. Wave 0 exit checklist

| Deliverable | Status |
|-------------|--------|
| Methodology + orchestration (L3/L4 locked) | ✅ |
| Generator pipeline map (M0-A) | ✅ |
| Runtime map (M0-B) | ✅ |
| Build/SDK gates (M0-C) | ✅ |
| Test landscape (M0-D) | ✅ |
| Prior-art index (M0-E) | ✅ |
| File ledger 1799 files `inventory` (M0-F reseed) | ✅ |
| This synthesis map | ✅ |
| Ready for Wave 1 | ✅ |
