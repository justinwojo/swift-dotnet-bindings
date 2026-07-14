# Data Pack — Emit-Then-Break / Compile-But-Dead / Poison-API Inventory

**Date**: 2026-07-16  
**Mode**: Evidence extraction (no production edits)  
**Scope**: Generator emission surfaces that (a) write public C# then degrade it, (b) leave compile-clean but unusable API, (c) poison with SB000x, or (d) hard-fail integrity after emission.

**Corpus context** (from [`00-skipreason-catalog.md`](./00-skipreason-catalog.md)): BindingTests ≈ **32 produce-throw** / **~28 consume-degraded** / **3 receiver-failfast** under `SuppressedProxyMemberDegraded` (63 total); **0** wrapper strip on current BindingTests.

---

## Classification key

| Class | Meaning |
|-------|---------|
| **emit-then-skip-OK** | Member/type is attempted or partially written, then **rolled back / stripped / comment-only**. No public dead API. Report row honest. |
| **emit-then-throw-public** | **Public (or discoverable) surface remains** with body that always throws / fail-fasts / is compile-poisoned. Compile-clean module; unusable for that call. |
| **emit-then-compile-break** | Generated C# would **fail to compile** (CS\*) if the hazard fires. Defect class — must stay closed. |
| **integrity-hard-fail** | Generator **exits non-zero** rather than ship dangling symbols / package lies. Correct for integrity. |
| **already-fixed-admission** | Former emit-then-break / silent-trap path; **admission or poison now decides at emit time**. Residual product pain may remain under another class. |

---

## Counts by class

Numbered sites **#1–#50** plus integrity rows **H1–H2**. Already-fixed **mechanisms A–G** overlap some of #13–15/#40 and are **not** double-counted in the total.

| Class | Distinct sites | Site IDs |
|-------|---------------:|----------|
| **emit-then-throw-public** | **36** | #1–12, #16–39 |
| **emit-then-skip-OK** | **6** | #41–43, #45 (co-gate half), #46–47 |
| **already-fixed-admission** | **4** (live emission) + **7** (retired mechanisms A–G) | #13–15, #40; §9 A–G |
| **integrity-hard-fail** | **2** | #44, H2 (mixed ObjC abort); #45 integrity half / H1 same as #44 |
| **emit-then-compile-break** | **3** (all **latent**, 0 live broken templates) | #48–50 |
| **Total unique emission loci** | **51** | #1–50 + H2 (H1≡#44) |

---

## 1. Produce-throw / SB0006 poison (suppressed EveryProtocol proxy)

Central helpers:

| Item | Path:line | Notes |
|------|-----------|-------|
| Site enum + report | `Reporting/SuppressedProxyReporting.cs:22–66` | `ProduceThrow` / `ConsumeDegraded` / `ReceiverFailFast` → `SkipReason.SuppressedProxyMemberDegraded` |
| Message + SB0006 id | `Emitter/StringEmitter/Handler/WrapperEmitter.cs:842–876` | `ProxySuppressedMessage`, `ProxySuppressedDiagnosticId = "SB0006"`, `EmitSuppressedProxyReadPoison` (`Obsolete(..., error: true)`) |
| Throw body | `WrapperEmitter.cs:885–890` | `EmitProxySuppressedThrowBody` → `NotSupportedException` |
| Accessor side table | `ModuleEmissionContext.cs:1198–1243` | `_produceThrowAccessors` / `_produceThrowGetters` — private accessor **not** poisoned so `get => Name_Get()` still compiles; **public** getter gets SB0006 |

### 1.A Public produce-throw sites (SB0006 + throw body)

| # | Site | Path:line | Class | Notes |
|---|------|-----------|-------|-------|
| 1 | Method body PRODUCE gate | `WrapperEmitter.cs:687–750`, `:885–890` | **emit-then-throw-public** | Checkpoint signature → body; catch `SuppressedProxyReferenceException` → throw stub + SB0006 (non-accessor). Accessors only side-table. |
| 2 | Async collection return suppressed | `AsyncHarnessEmitter.cs:257–268` | **emit-then-throw-public** | Sets `AsyncReturnProxySuppressed`; faulting Task + WrapperEmitter SB0006. |
| 3 | Async scalar existential return | `AsyncHarnessEmitter.cs:772–839` | **emit-then-throw-public** | Faulting `?: throw NSE` + SB0006 via env flag. |
| 4 | Async optional-collection existential | `AsyncHarnessEmitter.cs:881–891` | **emit-then-throw-public** | Same pattern as #2. |
| 5 | Property getter (scalar existential via accessor) | `PropertyHandler.cs:1049–1060` | **emit-then-throw-public** | `WasAccessorProduceThrow` → SB0006 on public getter; body still `get => Name_Get()`. |
| 6 | Property getter (collection/optional element) | `PropertyHandler.cs:1091–1114` | **emit-then-throw-public** | Probe-first projection throw → SB0006 + `get => throw NSE`. |
| 7 | CS0542 interface bridge getter | `PropertyHandler.cs:1013–1022` | **emit-then-throw-public** | If public getter poisoned, bridge uses **runtime** throw (not SB0006) to avoid CS0619. |
| 8 | Subscript getter (scalar via accessor) | `SubscriptHandler.cs:519–531` | **emit-then-throw-public** | SB0006 + throw; setter usable. |
| 9 | Subscript getter (collection element) | `SubscriptHandler.cs:546–566` | **emit-then-throw-public** | Probe-first; SB0006 + throw. |
| 10 | Existential bypass existential return | `ExistentialBypassEmitter.cs:934–963` | **emit-then-throw-public** | SB0006 on public; accessor → side-table only. |
| 11 | Enum `TryGet*` single-payload | `EnumHandler.CaseInspection.cs:194–311` | **emit-then-throw-public** | Checkpoint + SB0006 + throw body. |
| 12 | Enum `TryGet*` tuple payload | `EnumHandler.CaseInspection.cs:389–497` | **emit-then-throw-public** | Same as #11. |

**Product note:** SB0006 makes these **compile-poisoned** (CS0619 on call), not silent runtime traps. Still G1-003 “compile-but-dead” surface (member present, unusable). Ideal end-state: omit or `EditorBrowsable(Never)` + report only.

### 1.B Projection throws that *drive* produce-throw (generator-side, not consumer C#)

| # | Site | Path:line | Class | Notes |
|---|------|-----------|-------|-------|
| 13 | `ExistentialProjection` PRODUCE | `Marshaler/Projection/ExistentialProjection.cs:44–45`, `:264–266`, `:457`, `:483` | **already-fixed-admission** | Throws `SuppressedProxyReferenceException` at emit time → member boundary stubs. Replaces retired CoGater body rewrite. |
| 14 | `ExistentialHandler` proxy construct | `Marshaler/ExistentialHandler.cs:1161–1170` | **already-fixed-admission** | Same. |
| 15 | `ClosureHandler` suppressed proxy | `Marshaler/ClosureHandler.cs:2427–2441` | **already-fixed-admission** | Same for closure payload paths. |

### 1.C Proxy *interface-impl* produce-throw **without** SB0006

These keep CS0535 satisfaction on `{P}Proxy` (class is `EditorBrowsable(Never)` at `ProtocolProxyEmitter.cs:269`) but are still always-throw for Swift-backed containers:

| # | Site | Path:line | Class | Notes |
|---|------|-----------|-------|-------|
| 16 | Existential return method body | `ProtocolProxyEmitter.InterfaceImpl.cs:2027–2036` | **emit-then-throw-public** | Bare `throw NSE(ProxySuppressedMessage)` — **no** SB0006. |
| 17 | Existential return property getter | `ProtocolProxyEmitter.InterfaceImpl.cs:1950–1961` | **emit-then-throw-public** | Same. |
| 18 | Collection return element suppressed | `ProtocolProxyEmitter.InterfaceImpl.cs:2676–2683` | **emit-then-throw-public** | Same. |
| 19 | Collection property getter suppressed | `ProtocolProxyEmitter.InterfaceImpl.cs:1128–1140` | **emit-then-throw-public** | Same. |

**Gap vs concrete types:** Public concrete getters get SB0006; proxy InterfaceImpl paths often only runtime-throw (mitigated by proxy `EditorBrowsable(Never)`).

---

## 2. SB0003 / always-throw proxy interface stubs (CS0535 keep-alive)

| # | Site | Path:line | Class | Notes |
|---|------|-----------|-------|-------|
| 20 | `EmitAlwaysThrowingStubDiagnostics` | `ProtocolProxyEmitter.InterfaceImpl.cs:206–223` | **emit-then-throw-public** | Warning `[Obsolete]` + `EditorBrowsable(Never)` — **not** `error:true`. |
| 21 | Static abstract property/method stubs | `InterfaceImpl.cs:225–359` | **emit-then-throw-public** | Always throw; IntelliSense-hidden. |
| 22 | Inherited property/method stubs | `InterfaceImpl.cs:547–604` | **emit-then-throw-public** | “dispatch via parent protocol proxy”. |
| 23 | Mixed-generic method stub | `InterfaceImpl.cs:109–127` | **emit-then-throw-public** | SB0003 reason string + `EmitNotSupportedMethodStub`. |
| 24 | Closure-skipped method/property stubs | `InterfaceImpl.cs:165–177`, `:2810–2863` | **emit-then-throw-public** | SB0003; `_csharpImpl` path still works. |
| 25 | Non-dispatchable property get/set | `InterfaceImpl.cs:947–956`, `:1199–1279` | **emit-then-throw-public** | SB0003 when any accessor non-dispatchable; body throws on Swift container. |
| 26 | Non-dispatchable method body | `InterfaceImpl.cs:1534–1541`, `:1720–1758` | **emit-then-throw-public** | Same pattern. |
| 27 | Subscript not-yet-supported | `InterfaceImpl.cs:1308–1346` | **emit-then-throw-public** | SB0003 + throw both accessors. |
| 28 | `EmitNotSupportedMethodStub` | `InterfaceImpl.cs:2870–2948` | **emit-then-throw-public** | Shared SB0003 + throw (closure / mixed-generic / …). |
| 29 | Covariant refined return explicit stub | `InterfaceImpl.cs:764–784` | **emit-then-throw-public** | Explicit interface impl throws; reported `CovariantReturnNotRepresentable`. |

---

## 3. Other public throw / poison APIs (non–produce-throw)

| # | Site | Path:line | Class | Notes |
|---|------|-----------|-------|-------|
| 30 | Closure-param tombstone (SB0005) | `ClosureParamTombstoneEmitter.cs:1–34`, `:162–247`; wired `MethodHandler.cs:108–110`, `:1000–1002` | **emit-then-throw-public** | Public member kept; `object?` params; warning Obsolete SB0005 + `UnsupportedSwiftType` + body throws. Visibility-over-omit policy. |
| 31 | Interface static virtual defaults | `ProtocolHandler.cs:773–796`, `:1105–1116` | **emit-then-throw-public** | **Deliberately no Obsolete** (override dispatch must stay clean). Throws if called on interface default without override. |
| 32 | Empty interface SB0004 | `ProtocolHandler.cs:491–501` | **emit-then-throw-public** | Interface still public; Obsolete warning when zero members emitted. Underscore protocols also `EditorBrowsable(Never)` (`:503–504`). |
| 33 | Composition existential stubs | `ModuleHandler.cs:2380–2452` | **emit-then-throw-public** | Bare NSE on all inherited members of composition proxy — no SB000x. |
| 34 | Read-only proxy C#→Swift ctor | `ProtocolProxyEmitter.Receivers.cs:2319–2339` | **emit-then-throw-public** | Always throw before dangling Create P/Invoke. Forward-read path OK. |
| 35 | `@objc` existential reverse param | `ExistentialProjection.cs:110–117` | **emit-then-throw-public** | Emitted `as … ?? throw NSE` for non–Swift-vended conformers (fail-closed reverse). |
| 36 | SB0001 JIT-risk / SB0002 tombstone | `WrapperEmitter.Signature.cs:309–384` | **emit-then-throw-public** | Warning Obsolete; SB0001 also `EditorBrowsable(Never)`. Member still **callable**. Soft poison, not hard dead. |
| 37 | Optional-ref projection fallback (proxy) | `InterfaceImpl.cs:1179` | **emit-then-throw-public** | Loud NSE if Optional\<ref\> projection fails — generator invariant breach, not normal path. |

---

## 4. Consume-degraded / receiver-failfast (not throw-public, related)

| # | Site | Path:line | Class | Notes |
|---|------|-----------|-------|-------|
| 38 | Receiver fail-fast stub | `ProtocolProxyEmitter.Receivers.cs:163–178`, `:220–240` | **emit-then-throw-public**\* | Private `[UnmanagedCallersOnly]` keeps **vtable slot**; `FailFastSuppressedProxyReceiver`. Layout-critical — do not omit without VtableLayout analysis. \*Not public consumer API; still “dead reverse channel.” |
| 39 | Receiver getter consume-degrade | `Receivers.cs:181–201` | **emit-then-throw-public**\* | Silent drop of C#→Swift wrap fallback; reported `consume-degraded`. Member appears to work for Swift-vended only. |
| 40 | CONSUME arms in projection | `ExistentialProjection.cs` (wrap fallback drop), `MarshalingContext.cs:66` | **already-fixed-admission** | Emit-time drop vs post-pass CoGater rewrite. |

---

## 5. MissingWrapperSymbol / co-gater / strip reconcile

| # | Site | Path:line | Class | Notes |
|---|------|-----------|-------|-------|
| 41 | In-band contract gate | `WrapperSymbolContractGate.cs:40–102` | **emit-then-skip-OK** | Predict-or-rollback → `// Unsupported:` + `SkipReason.MissingWrapperSymbol` + log. No orphan public body. |
| 42 | Contract exception rollback callers | `MethodHandler.cs:665–667`, `:740–741`, `:1107–1108`, `:1691–1692`; `PInvokeEmitter.cs:1235` | **emit-then-skip-OK** | Transactional C# rollback before `HandleSkip`. |
| 43 | Strip → C# reconciler | `Configuration/StrippedSymbolCSharpReconciler.cs:1–95`, `:230+` | **emit-then-skip-OK** | Sole surviving **post-hoc** co-gater leg (trigger #2/#3 retired). Removes P/Invoke + 3-level callers. Projected as MissingWrapperSymbol Review (`BindingReportProjection.cs:55–67`). |
| 44 | Integrity text-vs-text gate | `WrapperSymbolIntegrityGate.cs:12–66` | **integrity-hard-fail** | SWIFTBIND108; dangling `EntryPoint` ⊆ defs → generator fail. Independent of per-emit `EnforceWrapperContract`. |
| 45 | Program / BindingsGenerator wiring | `Program.cs:675–676`, `:1341–1345`; `BindingsGeneratorCommand.cs:1099–1103`, `:1266–1272` | **emit-then-skip-OK** + **integrity-hard-fail** | Co-gate process directory; integrity check after emit. |

**Admission vs residual:** Each strip/co-gate hit means **emission admission missed**. Healthy product: MissingWrapperSymbol → 0 on corpus (BindingTests currently 0 strip). Growth is Review-tier tripwire.

---

## 6. `// Unsupported:` vs still-emitted public members

| # | Site | Path:line | Class | Notes |
|---|------|-----------|-------|-------|
| 46 | Comment-drop emitter | `UnsupportedCommentEmitter.cs:15–58` | **emit-then-skip-OK** | Type/member skip → comment only + SWIFTBIND025. **Not** a public member. |
| 47 | SkipProperty / handler tombstones | e.g. `PropertyHandler.cs:94`; `ClassHandler.cs:304`; `EnumHandler.cs:467`, `:642`; `FrozenStructHandler.cs:363`; `NonFrozenStructHandler.cs:234` | **emit-then-skip-OK** | Mirror: leave `// Unsupported:` when skipping mid-body walk. |

**Contrast — still-emitted public “unsupported” surface (not comments):**

- SB0005 tombstones (#30)  
- SB0006 produce-throw (#1–12)  
- SB0003 proxy stubs (#20–29)  
- `[UnsupportedSwiftType]` on degraded params/returns while method still emits (`UnsupportedSwiftTypeSupport` + WrapperEmitter) — compilable fidelity loss, often with usable partial API  

Finding 53 / SWIFTBIND025–026 (`EmissionReportEmitter.cs:234–259`) makes comment-drops and bare-`object` loud; does **not** remove poison APIs.

---

## 7. EditorBrowsable(Never) stubs (intentional hide, not full omit)

| Pattern | Path:line | Class | Notes |
|---------|-----------|-------|-------|
| Always-throwing proxy stubs | `InterfaceImpl.cs:219–222` | **emit-then-throw-public** | Paired with warning Obsolete. |
| Proxy class type | `ProtocolProxyEmitter.cs:269` | hide machinery | Entire proxy type demoted. |
| SB0001 JIT members | `WrapperEmitter.Signature.cs:380–383` | soft hide | Still callable. |
| Interop helpers (payload, handles) | `TypeHandlerHelpers.cs:217+`; Class/Struct/Enum handlers | hide machinery | Not dead API. |
| `makeAsyncIterator` demotion | Class/Frozen/NonFrozen handlers + `AsyncSequenceEmitter.cs:18` | hide raw in favor of `IAsyncEnumerable` | Intentional dual surface. |
| Throwing-closure raw overload | `ThrowingClosureSimplificationEmitter.cs:20`; `WrapperEmitter.Signature.cs:398–403` | hide raw | Convenience overload is public. |

---

## 8. Latent emit-then-compile-break hazards (no live “emit broken C#” template)

| # | Hazard | Evidence | Class |
|---|--------|----------|-------|
| 48 | TypeSkipPrePass dual-oracle drift | Track G1 DA-W7-G1-005; `TypeSkipPrePass.cs` header | **emit-then-compile-break** (latent) — CS0234 if handler skip ≠ pre-pass |
| 49 | Dual-path `{name}Buffer` double-create | `.claude/rules/bindingtests.md` Emission Pipeline Dual-Path Hazard | **emit-then-compile-break** (latent) — CS redefinition |
| 50 | Strip without full public-member co-gate | Reconciler 3-level closure; IntegrityGate for EntryPoints | **emit-then-compile-break** if gap + integrity off; normally **emit-then-skip-OK** or **integrity-hard-fail** |

---

## 9. Already-fixed admission (retired CoGater / silent trap)

| # | Former defect | Fix site | Class |
|---|---------------|----------|-------|
| A | Generate-then-regex CoGater for suppressed `new {Proxy}(` | Emit-time `SuppressedProxyReferenceException` + checkpoint rewrite | **already-fixed-admission** (residual = throw-public #1–19) |
| B | Wrapper-symbol contract post-pass | `WrapperSymbolContractGate` + MethodHandler rollback | **already-fixed-admission** (residual strip leg #43) |
| C | Silent faulting async Task (no report, no poison) | AsyncHarnessEmitter Record + `AsyncReturnProxySuppressed` + SB0006 | **already-fixed-admission** → now #2–4 |
| D | RealityFoundation `Material`-class full suppress → every getter throw | `ModuleHandler.cs:1115–1131`, `:1340–1377` read-only proxy admission | **already-fixed-admission** (residual PAT-blocked still throw) |
| E | Module abort on uncaught receiver SuppressedProxy | `EmitReceiverOrDegrade` checkpoint | **already-fixed-admission** → #38 |
| F | Private accessor SB0006 breaking `get => Name_Get()` | Side-table + public-only poison | **already-fixed-admission** |
| G | Proxy/contract co-gate sections in manifest | `BindingReportProjection.cs:55–58` comment | **already-fixed-admission** |

---

## 10. Integrity hard-fail (package / generator)

| # | Site | Path:line | Class |
|---|------|-----------|-------|
| H1 | Wrapper symbol integrity | `WrapperSymbolIntegrityGate.cs` + `Program.cs:675` | **integrity-hard-fail** |
| H2 | Mixed ObjC systemic abort | `BindingsGeneratorCommand` mixed path (Track G1 A21) | **integrity-hard-fail** (package-level; blocks Swift-only salvage) |

Not inventoried as emit-then-break: parser/`InvalidOperationException` generator internals, XCFramework resolve failures, wrapper all-stripped SDK SWIFTBIND050/051 (package policy, separate G1 track).

---

## 11. Policy map (what “good” looks like)

| Outcome today | Prefer for day-1 drop-in |
|---------------|--------------------------|
| Member skip + `// Unsupported:` + report | **Keep** (emit-then-skip-OK) |
| MissingWrapperSymbol after strip | **Drive to 0**; treat growth as integrity regression |
| SWIFTBIND108 dangling EntryPoint | **Keep hard-fail** |
| SB0006 produce-throw public API | **G1-003**: omit or Never+report; keep layout receivers only |
| SB0003 proxy stubs | Acceptable while proxy is Never; prefer omit non-dispatchable if CS0535 allows |
| SB0005 tombstone | Product choice: visibility vs omit — document |
| Interface static virtual throw (no Obsolete) | **Keep bare** — override dispatch requires it |
| Consume-degraded | Report OK; document reverse-direction limit |
| Receiver fail-fast | **Keep** for slot parity |

---

## 12. Site index by class (quick)

### emit-then-throw-public (36)
- **#1–12** — SB0006 produce-throw on concrete / enum / async / property / subscript  
- **#16–19** — proxy InterfaceImpl produce-throw (bare NSE, no SB0006)  
- **#20–29** — SB0003 / static / inherited / non-dispatch / covariant stubs  
- **#30–37** — SB0005 tombstone, composition, SB0001/2/4, read-only ctor, @objc reverse, optional fallback  
- **#38–39** — receiver fail-fast + consume-degrade reverse channels  

### emit-then-skip-OK (6)
**#41–43**, **#45** (ProcessDirectory wiring), **#46–47** (`// Unsupported:` comment-only). Rollback callers under #42 share one gate (#41).

### already-fixed-admission
- Live: **#13–15**, **#40** (emit-time `SuppressedProxyReferenceException` / CONSUME drop)  
- Retired mechanisms: **§9 A–G** (CoGater, silent async trap, Material flood, accessor poison, …)

### integrity-hard-fail (2)
**#44** / H1 (SWIFTBIND108), **H2** (mixed ObjC package abort).

### emit-then-compile-break (3 latent)
**#48–50** — TypeSkip dual-oracle, dual-path Buffer, strip/co-gate gap.

---

## 13. Cross-links

- Track G1: `tracks/Track-G1_Graceful-Degradation.md` (A12–A18, DA-W7-G1-003/005/006)  
- Skip catalog: `data-pack/00-skipreason-catalog.md`  
- Diagnostic encyclopedia: `data-pack/01-diagnostic-encyclopedia.md` (SB0001–6, SWIFTBIND108)  
- BindingTests gates: `SuppressedProxyChannelTests.cs` (SB0006 surface), `SuppressedProxyPoisonSurfaceTests.cs`

---

*Inventory only — no production code changed.*
