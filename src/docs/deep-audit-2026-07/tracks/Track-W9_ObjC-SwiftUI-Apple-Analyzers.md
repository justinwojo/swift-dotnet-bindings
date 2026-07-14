# Track W9 — ObjC pipeline + SwiftUI bridge + Apple supplement + Analyzers

| Field | Value |
|-------|--------|
| **Wave** | 9 (M1 + L2 ObjC + AP1) |
| **Track** | W9 |
| **Date** | 2026-07-16 |
| **Mode** | Read-only (production code not modified) |
| **Risk rating** | **2 / 5** (no novel P0; Phase-1 mixed bridge + single-registration packaging are mature; residual risk is capability gaps + day-1 mixed abort + latent CreateAsync compile-break) |
| **Confidence** | **high** on ObjC protocol gap, mixed fail-closed, CreateAsync dual-path, dual availability lift, analyzer intentional FPs; **medium** on SB1002 property-receiver FN without fixture probe |
| **Lenses** | L1 (CreateAsync latent CS0030; dual-oracle drift risk), **L3** (mixed abort vs partial success; ObjC member drops), **L4** (dual async/sync param models; dual Catalyst lift), L2 (analyzer test honesty) |
| **Headline** | Secondary full-surface is **capability-limited, not crash-prone**: ObjC Phase-1 mixed bridge + packaging single-registration are solid; protocol/container Phase-2 and CreateAsync parity remain **already-known latents**; G1-002 mixed abort is the real day-1 L3 tax; analyzers are honest lightweight guidance with documented FPs and one property-receiver FN candidate. |

---

## 1. Method

1. Methodology + codebase-map Wave-9 seeds + G1 map (A20–A22, G1-002) + prior-art index (ObjC Phase 2, CreateAsync, ApiDefinition dedup).  
2. Deep-read ObjC tree: `ObjCPipeline`, `ObjCBridgeRecordFactory`/`Rekeyer`, `ApiDefinitionEmitter` WouldEmit/dedup, `ObjCAvailability*`, model decls.  
3. Deep-read SwiftUI bridge: detector, collector, `SwiftUIBridgeEmitter` (+ InitAnalyzer / AsyncPattern / Lifecycle), non-async BoundEnum/BoundType emit vs async flatten.  
4. Apple supplement: hand-rolled `Sources/**`, `AppleSupplementModuleInit`, packaging notes; mixed packaging via `ObjCBindingProjectEmitter` + Sdk mixed companion targets.  
5. Analyzers SB1001/SB1002 + tests + Runtime pack path.  
6. Tag already-known (roadmap / G1); file residual candidates; mark refuted-clean where guards hold.

---

## 2. Files reviewed-deep

| Path | Why |
|------|-----|
| `src/Swift.Bindings/src/ObjC/Pipeline/ObjCPipeline.cs` | Parse → eligibility → FilterAndEmit; mixed empty-surface skip; native-symbol fail-closed |
| `src/Swift.Bindings/src/ObjC/Pipeline/ObjCBridgeRecordFactory.cs` | Phase-1 records; **protocols out of scope** |
| `src/Swift.Bindings/src/ObjC/Pipeline/ObjCBridgeRecordRekeyer.cs` | ABI-harvest Swift-import rekey (inventory) |
| `src/Swift.Bindings/src/ObjC/Emitter/ApiDefinitionEmitter.cs` | WouldEmit, dedup, delegate protocols |
| `src/Swift.Bindings/src/ObjC/Emitter/ObjCAvailabilityEmitter.cs` | Annotate-not-drop; dual Catalyst lift |
| `src/Swift.Bindings/src/ObjC/Parser/ObjCAvailabilityParser.cs` | Source-offset recovery + known degrade paths |
| `src/Swift.Bindings/src/ObjC/Model/ObjCDeclarations.cs` | `ObjCProtocolDecl` lacks `SwiftName` |
| `src/Swift.Bindings/src/BindingsGeneratorCommand.cs` | Mixed pre-parse abort before Swift gen |
| `src/Swift.Bindings/src/Emitter/StringEmitter/SwiftUIViewDetector.cs` | View module set |
| `src/Swift.Bindings/src/Emitter/StringEmitter/SwiftUIBridgeCollector.cs` | Static collector (post-1.0 → PipelineContext seed) |
| `src/Swift.Bindings/src/Emitter/StringEmitter/SwiftUIBridgeEmitter.cs` | Non-async typed BoundType/BoundEnum (`IsSimpleEnum`) |
| `src/Swift.Bindings/src/Emitter/StringEmitter/SwiftUIBridgeEmitter.AsyncPattern.cs` | CreateAsync flatten + factory surface |
| `src/Swift.Bindings/src/Emitter/StringEmitter/SwiftUIBridgeEmitter.InitAnalyzer.cs` | `BridgeParameter.IsSimpleEnum` from TypeDB |
| `src/Swift.Bindings/src/Emitter/StringEmitter/SwiftUIBridgeEmitter.Lifecycle.cs` | Lifecycle / universal modifiers |
| `src/Swift.Bindings/src/Marshaler/ExistentialHandler.cs` | `[any objcP]` container fail-closed oracle |
| `src/Swift.Bindings/src/ObjC/Emitter/ObjCBindingProjectEmitter.cs` | Gap-2 source NativeReference drop |
| `src/Swift.Bindings.Sdk/Sdk/Sdk.targets` | Mixed companion build/reference/SWIFTBIND039 class |
| `src/Swift.Bindings.Apple/Sources/**` + `AppleSupplementModuleInit.cs` | Hand-rolled ISwiftObject + factory/payload registration |
| `src/Swift.Analyzers/*` + `src/Swift.Analyzers.Tests/*` | SB1001/SB1002 semantics + intentional limits |
| Prior: G1-002, roadmap Medium/Latent rows, A7-017, M0-C mixed gates |

---

## 3. Architecture inventory

### 3.1 ObjC pipeline (L2)

```
XCFrameworkResolver.ObjC resolution
  → ObjCPipeline.Parse
       umbrella → clang -ast-dump=json → ClangAstParser
       4c platform stubs | 4f class native-symbol | 4g free-symbol
       namespace (+ Binding suffix collision)
  → [mixed] ObjCBridgeRecordFactory → ModuleTypeDatabase (Swift-wins)
  → Swift GenerateBindings
  → [mixed] FilterAndEmit(exclude = swift-types.json ownership)
       4b mixed dedup (drop Swift-owned classes/protocols; extract categories)
       4e delegate-protocol mark
       4d foreign categories (pure-ObjC only)
       ApiDefinition + StructsAndEnums + companion csproj + metadata props
```

**Integrity posture:** systemic parse / SWIFTBIND028 nm-all-failed → non-zero exit. Mixed systemic fail **aborts entire package before Swift** (`BindingsGeneratorCommand` ~837–851; second gate via `ShouldAbortForFailedMixedObjC`). Member-level drops (unresolvable types, missing symbols, mixed dedup) continue with `ObjCBindingDiagnostics` → report projection.

**Phase-1 bridge surface (emits TypeRecords):** classes → ObjCBridged; NS_ENUM / NS_OPTIONS → SimpleEnum (+ OptionSet); NSString-backed NS_TYPED* → ObjCBridgeable. **Protocols / categories-as-named-types: not bridged.**

### 3.2 SwiftUI bridge (M1)

```
IHandler.HandleBaseDecl
  → SwiftUIViewDetector (SwiftUI | SwiftUICore).View
  → type skip SwiftUIView + SwiftUIBridgeCollector.Collect
ModuleEmitter end
  → SwiftUIBridgeEmitter (simple | inferred async chain | KnownAsyncPatterns dict | template)
  → .SwiftUIBridge.swift (#if canImport(UIKit)) + .SwiftUIBridge.cs (!macos TFM)
Bridge compile non-fatal to main binding (SWIFTBIND052 family / Program compile-bridge exit 0)
```

Product decision (locked): **View → bridge, not direct binding.** macOS native excluded (UIKit hosting).

### 3.3 Apple supplement (AP1)

Hand-rolled `Swift.Foundation.*` / `SwiftUI.Text` / ActivityKit / ManagedSettings tokens + `SBApple.xcframework` shims. ModuleInitializer registers NewFromPayload factories + payload semantics (NativeAOT trim resistance). Blast-radius gate keeps supplement from expanding framework/symbol surface accidentally.

### 3.4 Analyzers (AP1)

| ID | Severity | Intent |
|----|----------|--------|
| **SB1001** | Info | Undisposed `ISwiftObject` local — not correctness-required (finalizer/VWT path exists) |
| **SB1002** | Warning | Self-capturing lambda on Swift object callback (stored setter or method arg) |

Packaged into `SwiftBindings.Runtime` nupkg (`analyzers/dotnet/cs`). Explicit Compile items when `EnableDefaultCompileItems=false` (binding csproj global property trap).

### 3.5 Mixed packaging (light)

| Concern | Status |
|---------|--------|
| Static source + wrapper → companion drops source `NativeReference` | Correct (Gap 2 / issue #40 single-registration) |
| Dynamic source keeps local NativeReference `Pack=false` | Documented as inert / deduped |
| Companion embed in Swift nupkg `lib/` | Path a PackageReference |
| SDK-direct companion `<Reference>` | Path b (`_ReferenceMixedObjCCompanion`) |
| SWIFTBIND039 class (mixed metadata without companion) | Integrity fail-closed |
| Runtime gates | `--mixed-pack` (sim+device), `--mixed-direct` (sim) — opt-in heavyweight |

---

## 4. Findings

### DA-W9-L2-001: ObjC protocol / `[any objcP]` Phase-2 gap

- **Severity**: P2 (capability; owner-classified off primary FB surface)  
- **Status**: already-known  
- **Confidence**: high  
- **Lenses**: L3 (honest skip), L1 only if someone “fixed” with wrong EC carrier  
- **Reachability**: emission-live (~15 FBSDK members off primary surface); MapLibre pure-ObjC unaffected  
- **Claim**: `ObjCBridgeRecordFactory` synthesizes class/enum/typed-enum records only — comment states “ObjC protocols remain out of scope” (`ObjCBridgeRecordFactory.cs:54`). `ObjCProtocolDecl` has no `SwiftName` (`ObjCDeclarations.cs:107–126`). Swift members typed `any P` / nested `[any P]` where `P` is ObjC fail-close or degrade; container positions use `HasUnsupportedObjCProtocolExistentialPosition` → `UnsupportedExistential` skip (not crash). Everyday **delegate** protocols still bind via bgen companion `[Model]` interfaces.  
- **Evidence**: factory docblock + CreateRecords loops; `ExistentialHandler.cs:506–509`; MVP/gate evaluators call the same oracle; roadmap Medium row.  
- **Probe**: Mixed fixture with Swift `func f(_ p: any ObjCProtocol)` / `[any ObjCProtocol]` → skip row + no C# type; companion still has protocol interface.  
- **Suggested fixture**: Pair protocol-record synthesis **with** container-position marshalling (FB-2) — synthesis alone won’t recover most of the 15.  
- **Prior art**: roadmap; prior-art-index Medium; G1 A20.

### DA-W9-L2-002: Mixed ObjC systemic failure aborts Swift-only partial

- **Severity**: P1 (day-1 mixed libraries)  
- **Status**: degrade-opportunity / already-known (G1-002)  
- **Confidence**: high  
- **Lenses**: L3  
- **Reachability**: emission-live (FBSDK-class mixed frameworks)  
- **Claim**: On mixed detection, ObjC parse failure refuses any Swift binding before generation (`BindingsGeneratorCommand.cs:837–851`). `ShouldAbortForFailedMixedObjC` (`:1826–1827`) has no permissive escape. Prevents silent ObjC drop and SWIFTBIND039 bypass — correct integrity — blocks day-1 Swift-only try.  
- **Evidence**: G1-002; early-abort comments:800–812.  
- **Probe**: Mixed fixture broken umbrella → exit ≠ 0, no usable Swift `.cs`.  
- **Risk notes**: Soft path must mark Swift-only / Mixed-degraded, never claim Mixed; opt-in flag safer than default soften.  
- **Prior art**: DA-W7-G1-002; codebase-map § ObjC systemic fail.

### DA-W9-L2-003: ObjC availability — annotate-not-drop with graceful degrade holes

- **Severity**: P2 (CA1416 completeness, not wrong-version emit)  
- **Status**: already-known / degrade-opportunity (positive posture)  
- **Confidence**: high  
- **Lenses**: L3, L4  
- **Reachability**: emission-live for recovered macros; latent for nested wrapper macros  
- **Claim**: ObjC path recovers availability from header **source offsets** (not AST `platform` keys — clang omits them) and emits the same Supported/Obsoleted/UnsupportedOSPlatform shape as Swift. Policy is **annotate, don’t drop** (`ObjCAvailabilityEmitter.cs:19–21`). Nested user wrapper macros, `API_TO_BE_DEPRECATED` sentinel, exotic spellings → **no attribute** (not a wrong one).  
- **Evidence**: roadmap S20/Finding-22 row; `ObjCAvailabilityParser` API_* / attribute forms; emitter + macCatalyst floor lift.  
- **Probe**: Header with `MYLIB_AVAILABLE = API_AVAILABLE(ios(15.0))` → no CA1416 attribute on decl.  
- **Prior art**: roadmap Low; ObjCAvailabilityEmitterTests parity with Swift lift.

### DA-W9-L2-004: ApiDefinition dedup / WouldEmit omit `delegateProtocolNames`

- **Severity**: P3 (cosmetic over-rename only)  
- **Status**: already-known latent  
- **Confidence**: high  
- **Lenses**: L4 (dual MapType view), L1 **refuted** for CS0111  
- **Reachability**: latent (no validation-library repro)  
- **Claim**: Emit path maps delegate protocols as `Foo`; dedup/WouldEmit map as `IFoo`. Writers share the omit → **no missed collision**. Only reachable effect: spurious rename when delegate-typed and regular-protocol-typed params collide on projected key.  
- **Evidence**: `ApiDefinitionEmitter.cs` ResolveMethodNameWithDedup ~926 vs EmitParameters with delegate set ~698; roadmap Latent.  
- **Prior art**: roadmap; prior-art-index.

### DA-W9-M1-001: Async `CreateAsync` parity gaps (raw IntPtr + dropped `IsSimpleEnum`)

- **Severity**: P1 **if reached** (complex enum → CS0030 compile break); P2 ergonomics for BoundType today  
- **Status**: already-known latent  
- **Confidence**: high on mechanism; high on zero current emission  
- **Lenses**: L1, L4 (dual param models)  
- **Reachability**: latent — all known CreateAsync overloads are string/int/bool only  
- **Claim**: Non-async factory uses typed BoundType (`.Handle` / `.Payload`) and splits BoundEnum on `IsSimpleEnum` (`SwiftUIBridgeEmitter.cs:3069–3085`). Async path:  
  1. Factory public surface: BoundType/BoundStruct → raw `CSharpPInvokeType` (`IntPtr`) (`AsyncPattern.cs:1157–1162`).  
  2. Null check compares to `IntPtr.Zero` (`:1198–1203`).  
  3. BoundEnum always `(CSharpPInvokeType)value` (`:1282–1284`) — **no** `IsSimpleEnum`; complex class enum → CS0030.  
  4. `AsyncFlatParam` record has **no** `IsSimpleEnum` field (`:1392–1402`); `BridgeParamToFlatParam` drops it for BoundEnum (`:398–400`) and drops `CSharpTypeName`/`IsObjCBridgeable` for BoundType (`:391–393` vs BoundStruct `:394–397`).  
- **Evidence**: dual switch bodies; InitAnalyzer sets `IsSimpleEnum` only on `BridgeParameter` (`InitAnalyzer.cs:456`); A7-017; roadmap Latent.  
- **Probe**: Async-pattern View leaf init taking complex raw-value enum or `Foundation.URL` → regen → C# compile.  
- **Suggested fix shape**: Mirror non-async typed/`IsSimpleEnum` onto async factory + call args; extend `AsyncFlatParam` (or collapse models — see L4-002).  
- **Prior art**: roadmap Latent CreateAsync; DA-W4-A7-017.

### DA-W9-M1-002: KnownAsyncPatterns hardcode + inference dual path

- **Severity**: P3  
- **Status**: candidate / product limit  
- **Confidence**: high  
- **Lenses**: L4, L5  
- **Reachability**: emission-live for BlinkIDUX only as dict entry; inference covers other async chains  
- **Claim**: `KnownAsyncPatterns` is a hand-maintained BlinkIDUX-only dictionary (`AsyncPattern.cs:18–94`) while `InferAsyncPattern` exists for generic chains. Divergence risk is intentional specialization, but new async Views can silently fall to template if inference ranking picks poorly.  
- **Evidence**: file header “Explicit support for BlinkIDUXView pattern only”; rules/swiftui-bridge.md precedence dict → inference → simple → template.  
- **Prior art**: product non-goal “SwiftUI beyond bridge.”

### DA-W9-AP1-001: SB1002 false negative — property / `this` member-access receivers

- **Severity**: P2 (analyzer completeness; not runtime)  
- **Status**: candidate  
- **Confidence**: medium (code-path clear; no dedicated test proving silence)  
- **Lenses**: L2, L1 (leak guidance gap)  
- **Reachability**: fixture-reachable (common UI code)  
- **Claim**: `GetSwiftObjectReceiver` only accepts `ILocalSymbol` / `IParameterSymbol` / `IFieldSymbol` (`SwiftRetainCycleAnalyzer.cs:148–160`). A receiver that is a **property** (`this.Session`, `ViewModel.Proxy`) resolves to `IPropertySymbol` → no diagnostic, even when the lambda clearly self-captures. Dominant binding cycle shape is covered for **locals** (`obj.Handler = …`); property-backed sessions miss.  
- **Evidence**: switch excludes properties; tests cover local only (`SwiftRetainCycleAnalyzerTests`).  
- **Probe**:  
  ```csharp
  FooProxy Proxy { get; set; }
  void M() { Proxy.Handler = () => Proxy.DoWork(); } // expect SB1002 today: silent
  ```  
- **Suggested fix**: Treat instance properties (and optionally `this` typed as ISwiftObject) as receivers when symbol identity matches capture.  
- **Prior art**: none as filed finding.

### DA-W9-AP1-002: Analyzer intentional false positives (documented design)

- **Severity**: P3  
- **Status**: confirmed (by design)  
- **Confidence**: high  
- **Lenses**: L2  
- **Reachability**: emission-live (any consumer with ownership transfer / sync ForEach-style APIs)  
- **Claim**:  
  - **SB1001**: field store, helper-takes-ownership, conditional dispose → Info FP (`SwiftObjectDisposeAnalyzer` doc `:20–25`); severity Info + finalizer-safe story reduces harm.  
  - **SB1002**: sync-invoked callbacks (ForEach) cannot be distinguished from stored → Warning FP (`:29–32`).  
- **Evidence**: analyzer docs + tests for conditional dispose still reporting.  
- **Verdict**: Acceptable lightweight guidance; do not “fix” with CFG without product ask.

### DA-W9-AP1-003: Apple supplement ModuleInitializer / factory registration — reviewed sound

- **Severity**: n/a  
- **Status**: reviewed (no defect)  
- **Confidence**: high  
- **Lenses**: L1  
- **Claim**: `AppleSupplementFactoryRegistration` registers NewFromPayload + payload semantics for non-generic reference types; generics self-register; Data is value-type Inline short-circuit. Circular Runtime dep correctly avoided.  
- **Evidence**: `AppleSupplementModuleInit.cs:25–45`.  
- **Prior art**: none as bug.

### DA-W9-M2-001: Mixed packaging single-registration — reviewed sound (residual test gap)

- **Severity**: P3 residual (coverage)  
- **Status**: already-known residual + reviewed packaging  
- **Confidence**: high on policy; medium on untested container bridge  
- **Lenses**: L1 (issue #40 class), L2  
- **Claim**: Companion `ShouldIncludeSourceXcframework` bake matches Swift `NativePackagingPolicy` (`ObjCBindingProjectEmitter.cs:58–69`). Static+wrapper drops source NativeReference. Roadmap residual: **no runtime test** for `Set<BridgedClass>` / `[BridgedClass]` of Phase-1 bridged types (scalar/Optional covered in PackGate MixedFixture).  
- **Prior art**: roadmap “Mixed-binding bridged-type container-position test coverage.”

### DA-W9-L4-001: Dual `LiftMacCatalystFloorToIOS` (Swift vs ObjC models)

- **Severity**: P3  
- **Status**: simplification  
- **Confidence**: high  
- **Lenses**: L4, L5  
- **Reachability**: integrity-gate (parity tests exist both sides)  
- **Claim**: `AvailabilityHelpers.LiftMacCatalystFloorToIOS` (Swift `AvailabilityAnnotation`) and `ObjCAvailabilityEmitter.LiftMacCatalystFloorToIOS` (ObjC model) are **algorithmically mirrored copies** with separate types. Drift risk if one lift rule changes.  
- **Suggested simplification**: Shared pure function over `(platform, introduced, deprecated, obsoleted)` tuples + adapters; risk class **behavior-preserving** with existing dual unit tests as oracle.  
- **Do not**: Force one model type across ObjC/Swift without adapter — different availability sources.  
- **Prior art**: ObjCAvailabilityEmitterTests parity comments.

### DA-W9-L4-002: Dual SwiftUI param models (`BridgeParameter` vs `AsyncFlatParam`)

- **Severity**: P2 (root of M1-001)  
- **Status**: simplification / hazard  
- **Confidence**: high  
- **Lenses**: L4, L1  
- **Reachability**: latent until async Bound* leaf  
- **Claim**: Async flatten rebuilds a thinner record, dropping `IsSimpleEnum` and (for BoundType) `CSharpTypeName`/`IsObjCBridgeable`, then reimplements factory/call emission. Non-async keeps full `BridgeParameter` fidelity. Classic dual-oracle hazard.  
- **Suggested simplification**: Flatten to `BridgeParameter[]` for async factories too, or promote full fields onto `AsyncFlatParam` in one PR with fixture. Risk: **needs fixture** (CreateAsync BoundEnum + BoundType).  
- **Prior art**: M1-001; constraints dual-path pattern.

### DA-W9-L3-001: ObjC member-level degradation is healthy; package-level is not

- **Severity**: P2 product tension (same as G1)  
- **Status**: confirmed inventory  
- **Confidence**: high  
- **Lenses**: L3  
- **Claim**: WouldEmit / native-symbol / mixed-dedup / unresolvable types → drop with diagnostics (good). Systemic parse / mixed abort / wrapper-required → package death (contested). Bridge compile remains non-fatal (good).  
- **Evidence**: G1 A20–A22, A23–A24; Program bridge exit 0.

### DA-W9-L4-003: Static collectors (SwiftUIBridgeCollector)

- **Severity**: P3  
- **Status**: already-known maintainability  
- **Confidence**: high  
- **Lenses**: L5, L4  
- **Claim**: Thread-static-ish static list reset in ModuleEmitter — codebase-map seed “Static collectors → PipelineContext (post-1.0)”. Same class as ReportCollector. No live bug if Reset always runs; AI hazard on parallel/module re-entry.  
- **Prior art**: codebase-map § static collectors.

---

## 5. Refuted / verified-clean (this wave)

| ID | Topic | Why clean |
|----|-------|-----------|
| R-W9-01 | Mixed Phase-1 class/enum/typed bridge “missing entirely” | Factory + rekeyer + PackGate mixed fixture cover scalar/Optional |
| R-W9-02 | Availability drops unavailable APIs silently | Annotate UnsupportedOSPlatform; does not strip |
| R-W9-03 | ApiDefinition delegate MapType dual view → CS0111 | Dedup writers consistent; only cosmetic rename |
| R-W9-04 | Companion always double-registers ObjC classes | Gap-2 static drop + Pack=false + mixed-pack/direct gates |
| R-W9-05 | SB1001 required for correctness | Analyzer description: finalizer/VWT path safe; Info only |
| R-W9-06 | SwiftUI View direct-bind crash | Product skip + collector; UIKit-gated bridge |
| R-W9-07 | ObjC `[any P]` nested mis-marshal as EC1 | Fail-closed UnsupportedExistential before emit |

---

## 6. File coverage (this track)

| Status | Paths |
|--------|--------|
| **reviewed-deep** | ObjCPipeline, ObjCBridgeRecordFactory, ApiDefinitionEmitter (WouldEmit/dedup/availability call sites), ObjCAvailabilityEmitter, BindingsGeneratorCommand mixed abort, SwiftUIBridgeEmitter + AsyncPattern + InitAnalyzer key arms, SwiftUIViewDetector/Collector, ExistentialHandler ObjC position oracle, ObjCBindingProjectEmitter, AppleSupplementModuleInit, both analyzers + tests |
| **reviewed** | ObjC model decls, ClangAstParser protocol merge notes, StructsAndEnumsEmitter (via factory coupling), Lifecycle partial, Sdk.targets mixed companion block (sample), Runtime.csproj analyzer pack |
| **inventory / deferred** | Full ClangAstParser mega-file branch matrix; every Apple Sources/*.cs marshal body; ThemeBridgeEmitter; full ObjC rekeyer; every SDK mixed MSBuild edge — not load-bearing for W9 hunt list |
| **out-of-scope** | bin/obj, generated AppleTypes under obj/, third-party |

---

## 7. Counts

| Bucket | Count |
|--------|-------|
| Findings total (numbered DA-W9-*) | **14** |
| already-known | 6 (L2-001, L2-002, L2-003, L2-004, M1-001, M2 residual) |
| confirmed / inventory | 3 (AP1-002 design FP, AP1-003 clean, L3-001) |
| candidate (new-ish) | 2 (AP1-001 property FN; M1-002 hardcode dual) |
| simplification | 3 (L4-001 dual lift; L4-002 dual param model; L4-003 static collector) |
| refuted-clean checks | 7 |
| **P0 novel** | **0** |
| **P1** | 1 product (G1-002 mixed abort); 1 latent conditional (CreateAsync CS0030 if reached) |
| **P2** | protocol Phase-2, availability completeness, dual param model, SB1002 FN candidate |
| **P3** | dedup cosmetic, BlinkIDUX dict, dual lift, static collector, container test gap |

---

## 8. Risk rating rationale

**2 / 5** because:

- Phase-1 mixed bridge + single-registration packaging + ObjC availability annotate policy are **production-hardened**.  
- Remaining issues are **capability / product** (no protocols, mixed abort, CreateAsync latent) or **analyzer guidance** — not silent ABI corruption on the shipping path.  
- CreateAsync CS0030 is real mechanism but **zero emission site** today → not day-1 risk.  
- Elevates toward 3 only if owner prioritizes FB `[any objcP]` or softens mixed abort without integrity markup.

---

## 9. Suggested backlog (owner-gated; no implement)

| Priority | Item | Track link |
|----------|------|------------|
| Product | Opt-in mixed Swift-only / Mixed-degraded flag (never silent Mixed claim) | G1-002 / W9-L2-002 |
| Capability | Phase-2 ObjC protocol records + container `[any objcP]` together | W9-L2-001 |
| Correctness latent | CreateAsync mirror non-async typed/`IsSimpleEnum` (collapse dual model) | W9-M1-001 / L4-002 |
| Analyzer | SB1002 property-receiver identity | W9-AP1-001 |
| Coverage | Mixed fixture container of bridged companion types | W9-M2-001 |
| Simplify | Shared Catalyst-floor lift core | W9-L4-001 |
| Do not re-chase | ApiDefinition CS0111 from delegate MapType; View direct bind; EC1 nested objcP marshal |

---

## 10. Cross-links

- G1: `tracks/Track-G1_Graceful-Degradation.md` (A20–A22, G1-002)  
- A7: CreateAsync latent DA-W4-A7-017  
- M2: packaging / SWIFTBIND039 / wrapper-required  
- Roadmap: Medium ObjC Phase-2; Latent CreateAsync + ApiDefinition dedup; Low availability wrapper macros  
- Rules: `.claude/rules/swiftui-bridge.md`, constraints mixed composition existential  
- Gates: `nuke binding-tests --mixed-pack` / `--mixed-direct`; PackGate MixedFixture  

---

*End Track W9 — read-only.*
