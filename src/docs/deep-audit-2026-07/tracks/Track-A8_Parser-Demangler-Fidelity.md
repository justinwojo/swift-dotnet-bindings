# Track A8 — Parser / Demangler / Interface-Facts Fidelity

| Field | Value |
|-------|--------|
| **Wave** | 5 |
| **Track** | A8 |
| **Date** | 2026-07-15 |
| **Mode** | Read-only (production code not modified) |
| **Risk rating** | **3 / 5** (core ABI ingestion + async demangle + composition/DependentMember are mature; residual risk is **visibility misclassification** dual-oracle: `PublicMemberNames` shape gates vs `IsModuleInternal` consumers, with protocol-req already-known workarounds and broader nonisolated/subscript gaps) |
| **Confidence** | **high** on already-known protocol-req path + workarounds, HasAsyncMarker, DependentMember/ProtocolComposition, ingestion contract; **medium** on nonisolated negative-space reachability volume and internal-subscript leak frequency |
| **Lenses** | L1 (wrong skip / wrong keep from misparse), L2 (fixture honesty), L3 (emit-then-break vs honest skip from parse flags), L4 (dual visibility oracles), L5 (walker shape-gate AI hazard) |

## Headline

**Parser/demangler fidelity is not a silent-ABI minefield today** — DependentMember, ProtocolComposition `printedName`, `funcSelfKind`→`IsMutating`, Finding-17 async tree walk, ABI `json_format_version` + unknown-kind census, and `ParseReconciliation` are solid and unit-pinned. The live residual is **visibility**: `MemberCollectionWalker` only records members whose source line carries an explicit `public`/`open` keyword, then `IsInternalFromPublicMemberNames` treats absence as internal. Protocol requirements (implicit public) are **already-known** (`roadmap.md`) with intentional downstream workarounds (ProtocolHandler / EveryProtocol / KeyPath `allowAbstract`). The **same shape gate** also excludes `public nonisolated …` (and other after-access modifiers), which can false-flag real public surface — especially properties dropped by `MemberEmissionValidator.CanEmitProperty`. Subscripts never get the method/property internal classifier at all.

---

## 1. Method

1. Read methodology, codebase map (A8 mega-files), prior-art (PM-RULES, DES-SYMB, demangler NO-GO), G1 graceful-degradation map, roadmap medium/latent parser rows.  
2. Strategic read of `SwiftABIParser` visibility stack, type-spec arms, method/property/subscript create paths, HandleNode reconciliation.  
3. Read `MemberCollectionWalker` + sibling walkers, `SwiftSyntaxInterfaceFactsProducer` / `InterfaceFactsAggregator`.  
4. Read `Swift5Demangler.HasAsyncMarker` + `Y*` cases, `GenericSignatureParser`, unit ParserTests / DemanglerTests.  
5. Tag already-known; under-claim new candidates.

---

## 2. Files reviewed-deep

| Path | LOC (ledger ~) | Why |
|------|----------------|-----|
| `src/Swift.Bindings/src/Parser/SwiftABIParser.cs` | ~4.2k | Visibility, CreateTypeSpec, methods/properties/subscripts, HandleNode |
| `src/Swift.Bindings/src/Parser/GenericSignatureParser.cs` | ~300 | ParseGenericSignature vs ParseSignature dual model |
| `src/Swift.Bindings/src/Parser/Producers/*` | — | Facts aggregation / SwiftSyntax host |
| `src/Swift.Bindings/src/Parser/SwiftInterfaceFacts.cs` | — | Fact surface |
| `tools/SwiftInterfaceParser/.../MemberCollectionWalker.swift` | ~490 | Public/internal member sets |
| `tools/SwiftInterfaceParser/.../SubscriptLabelsWalker.swift` | — | public-only labels; bare protocol skip |
| `tools/SwiftInterfaceParser/.../SignatureFactsWalker.swift` | — | defaults/autoclosure; protocol-req relaxation partial |
| `src/Swift.Bindings/src/Demangler/Swift5Demangler.cs` | ~3.4k | Async / Y* / generic sig |
| `src/Swift.Bindings/src/Emitter/.../MemberEmissionValidator.cs` | — | `CanEmitProperty` + `IsModuleInternal` |
| `src/Swift.Bindings/src/Emitter/.../MemberValidationPipeline.cs` | — | Method internal gates 3c/3d |
| `src/Swift.Bindings/src/Emitter/.../EveryProtocolEmitter.cs` | — | Intentional `IsModuleInternal` ignore on reqs |
| `src/Swift.Bindings/src/Emitter/.../Handler/KeyPathBagWalker.cs` | — | `allowAbstract` workaround |
| Unit: `ParserTests/*`, `DemanglerTests/AsyncMarkerTests` | — | Pins |

**Out of deep scope (mapped only):** full demangler reduction corpus, ObjC clang-AST parser (separate track), TypeSpecParser grammar internals beyond CreateTypeSpec consumers.

---

## 3. Architecture map (fidelity stack)

```
.xcframework / .abi.json ──► SwiftABIParser.ParseModule
       │                         ├─ GateAbiFormatVersion (SWIFTBIND033)
       │                         ├─ HandleNode allowlist (SWIFTBIND034 / 046)
       │                         ├─ Create*Decl + CreateTypeSpec
       │                         └─ ParseReconciliation (Parsed = Emitted+Skipped+Dropped)
       │
.swiftinterface ──► SwiftSyntaxInterfaceFactsProducer (host binary)
       │                 └─ InterfaceFactsAggregator (first-coverage-wins)
       │                         PublicMemberNames / InternalMemberKeys / …
       │
       └─► IsNodeModuleInternal (ABI attrs)
           + IsInternalFromSwiftInterface (InternalMemberKeys + Inlinable disambig)
           + IsInternalFromPublicMemberNames (negative space)
                 ──► MethodDecl/PropertyDecl.IsModuleInternal
                         ──► emitters / CanEmitProperty / MVP gates / wrappers
```

**Product non-goal respected:** demangler replacement spike is **NO-GO** (prior-art). L4 here = tighten visibility SSOT / walker shapes, not rewrite demangle.

---

## 4. Hunt results by theme

### 4.1 Visibility / `@usableFromInline` / protocol implicit public

| Mechanism | Status |
|-----------|--------|
| `IsNodeModuleInternal`: `IsInternal`, `UsableFromInline` always internal, `Inlinable` without AccessControl **only when** `PublicMemberNames` empty, SPI attrs | Sound for ABI-only path |
| `IsInternalFromSwiftInterface` + dual-set `Inlinable` disambiguation (StoreKit `custom(key:value:)` shape) | Documented, careful |
| Negative space via `PublicMemberNames` | **Load-bearing dual oracle** with SwiftSyntax host |
| Protocol requirements lack `public` keyword on member line | **Misclassified internal** when facts present |

**Downstream workarounds (intentional, not fixes):**

- `ProtocolHandler` / `MemberGateEvaluator` do **not** hard-skip on `property/method.IsModuleInternal`.
- `EveryProtocolEmitter.HasSuppressedRequiredMember` checks **only** `IsSpiProtected` — comment + unit test pin that `IsModuleInternal` must not skip conformances.
- `KeyPathBagWalker.WhyPropertyNotEmittable`: `IsModuleInternal` ignored when `allowAbstract` (protocol bag).

Any **new** consumer of `IsModuleInternal` on protocol requirements will re-break surface (L5).

### 4.2 Async mangling (`Ya` / Finding 17)

| Item | Verdict |
|------|---------|
| `FunctionReduction.IsAsync` primary | OK |
| Fallback `Swift5Demangler.HasAsyncMarker` → raw tree `AsyncAnnotation` | **Fixed** vs old substring `"Ya"` |
| Unit `AsyncMarkerTests` (real BindingTests symbols, Yak/Yacht false-positive, opaque `Qr` return) | Strong L2 |
| `Y` cases: `Ya` async, `Yb` concurrent, `YK` typed throws; unknown → `DemangleIdentifier` | **F11 already-known**: `Yt` (`_const`) etc. can fail demangle → async/variadic fall to heuristic (benign today) |

### 4.3 ABI JSON type-spec fidelity

| Shape | Verdict |
|-------|---------|
| `ProtocolComposition` via children **or** `printedName` `"any P & Q"` | **OK** — unit `ParseModule_ProtocolComposition_*` |
| `DependentMember` under `TypeNominal` name (not dead case arm) | **OK** — `DependentMemberParserTests` |
| Opaque return (`OpaqueTypeArchetype`) / param `some P` synthesis | Present; subscript `some P` index still degrade path (R2 already-known) |
| `funcSelfKind` → `IsMutating` / Consuming / Borrowing | **OK** — `SwiftABIParserRuntimeTests` region |
| Nested enum paths `BuildTypeQualifiedPath` for case labels | OK |
| ObjC nested enum **names** in ABI (`AVCaptureSession.Preset` style) | Constraints note: nested form in ABI — TypeDB/Apple registry concern more than parser print; no new A8 defect claimed |

### 4.4 Generic signatures

| API | Role |
|-----|------|
| `ParseGenericSignature` | Model for `GenericArgumentDecl`; drops constructed-generic / marker targets (null, not throw whole decl) |
| `ParseSignature` | Lossless `GenericSignatureModel` (Finding 19) for predicates |

**Already-known (roadmap / A6):** CSM `ParseMethodLevelConstraints` treats `T : P & Q` as one opaque target; dependent-member `T.Element : Foo` clauses discarded — engine undercount / late swiftc, L3 degrade form is engine reject preference (G1).

### 4.5 Interface-facts host

| Item | Verdict |
|------|---------|
| Single producer `SwiftSyntaxInterfaceFactsProducer`; fail-loud on non-macOS / missing binary | Integrity-correct |
| Aggregator first-coverage-wins | Ready for multi-producer; one today |
| `MemberCollectionWalker` public set requires explicit access modifier | Root of protocol-req + nonisolated gaps |
| `SubscriptLabelsWalker` also requires public/open (bare protocol subscripts omitted) | Labels may be incomplete for protocol reqs; ABI printedName often sufficient |
| `internalMemberKeys` covers func/var/init **only** — not subscript | Pairs with subscript classifier gap |

### 4.6 Graceful degradation (L3) from parse

| Mode | Behavior |
|------|----------|
| Member skip from true internal / SPI | Honest skip + report (good L3) |
| Misclassified protocol req as internal | **Mitigated** for interface/EveryProtocol; residual for other consumers |
| Misclassified public nonisolated property as internal | **Skip via CanEmitProperty** — silent undercount, not emit-then-break |
| HandleNode exception | DroppedWithError + warning; optional AbiJson degradation for 034/046 |
| Catch-all exception on node | Logged; **not** always InputResolution degradation (Finding 14a census still counts DroppedWithError) |
| Unknown digester kind | SWIFTBIND034 + degradation (strict-inputs fail-closed) |

---

## 5. Findings

### DA-W5-A8-001: Protocol-requirement public-visibility heuristic (`MemberCollectionWalker` / negative space)

- **Severity**: P1 (surface undercount / wrong flag) with **mitigations** reducing runtime crash class to undercount  
- **Status**: **already-known**  
- **Confidence**: high  
- **Lenses**: L1, L3, L5  
- **Reachability**: emission-live (MusicKit KeyPath bag path; EveryProtocol unit pins)  
- **Claim**: Protocol requirements in a `public protocol` body are implicitly public and rarely carry a `public` keyword. `MemberCollectionWalker` only records explicit `public`/`open` members into `PublicMemberNames`. `IsInternalFromPublicMemberNames` then sets `IsModuleInternal = true` on those requirements.  
- **Evidence**:  
  - Roadmap medium row “Protocol-requirement public-visibility heuristic…”  
  - `MemberCollectionWalker.swift` public-set contract (access keyword required)  
  - `SwiftABIParser.CreateMethodDecl` / `CreatePropertyDecl` negative-space arms  
  - `EveryProtocolEmitter.HasSuppressedRequiredMember` + `WillSkipConformance_RequiredModuleInternalProperty_DoesNotSkip`  
  - `KeyPathBagWalker.WhyPropertyNotEmittable` `allowAbstract` comment  
- **Probe**: Regen with `--swiftinterface` on a module with `public protocol P { var x: Int { get } }`; assert `IsProtocolRequirement && IsModuleInternal` on the property without workarounds.  
- **Suggested fixture**: Minimal protocol bag + KeyPath singleton already partially covered; add parser unit that asserts **desired** future: reqs not module-internal when parent protocol is public.  
- **Prior art**: `roadmap.md` medium; `parser-marshaler.md` UsableFromInline layers  

---

### DA-W5-A8-002: After-access modifiers (`public nonisolated` / etc.) excluded from `PublicMemberNames`

> **[Verification 2026-07-16: REFUTED — see [`../00-AUDIT-VERIFICATION.md`](../00-AUDIT-VERIFICATION.md) §4.1]** The mechanism is real but **unreachable**. The failing shape is only `public nonisolated var` (isolation modifier *trailing* the access keyword); swiftc canonicalizes modifier order in generated `.swiftinterface` and always emits `nonisolated public`, which `advanceToAccess` matches fine. The walker consumes only `.swiftinterface`, never `.swift` source. Corpus sweep: 0 hits for the failing order across the iOS 26.2 SDK + all 1,675 repo `.swiftinterface` files. Do NOT widen the allow-lists (mild precision loss, no real-input gain). A code comment recording this now lives at `MemberCollectionWalker.swift` (the BroadPublic matchers).

- **Severity**: ~~P1~~ **REFUTED** (property undercount on actor / MainActor-heavy APIs) / P2 for method-only shapes that still emit via CallConvSwift  
- **Status**: **candidate** (mechanism confirmed in walker + consumers; corpus volume not re-swept this pass)  
- **Confidence**: medium–high on mechanism; medium on emission-live frequency  
- **Lenses**: L1, L3, L5  
- **Reachability**: fixture-reachable; BindingTests uses `public nonisolated var unownedExecutor` (`CustomGlobalActor.swift`)  
- **Claim**: `matchesBroadPublicFuncShape` / `Var` / etc. disallow `nonisolated` **after** the access modifier (`public nonisolated func` fails). Order `nonisolated public` passes. Negative space marks the former internal. `MemberEmissionValidator.CanEmitProperty` returns `SkipReason.ModuleInternal` for all internal properties → **ClassHandler / FrozenStruct / Enum / NonFrozen** drop them before `PropertyHandler`. Wrapper path also rejects `module_internal`. Async/closure methods hit MVP gate 3d.  
- **Evidence**:  
  - `MemberCollectionWalker.swift` ~63–66, ~399–413, ~419–433 (disallows nonisolated after public)  
  - Same pattern documented in `SignatureFactsWalker` / `ExtensionsWalker`  
  - `MemberEmissionValidator.CanEmitProperty` lines 84–88  
  - `ClassHandler.cs` ~300–308  
  - `WrapperValidation.GetMemberRejectionReason` arm 2 `isModuleInternal`  
- **Probe**: Unit parse of a class with `public nonisolated var x: Int` + non-empty `PublicMemberNames` set that omits `Type.x` → expect `IsModuleInternal` today; emit path should skip.  
- **Suggested simplification**: Extend BroadPublic* allowed-after sets with `nonisolated` (and audit `dynamic`/`optional` if needed) **or** treat membership in `NonisolatedMembers` as proof-of-public when present.  
- **Prior art**: none as named roadmap row; related to A8-001 dual oracle  

---

### DA-W5-A8-003: Subscript visibility not classified (no `IsModuleInternal` / no UsableFromInline / no negative space)

- **Severity**: P2  
- **Status**: **candidate**  
- **Confidence**: medium  
- **Lenses**: L1, L3, L4  
- **Reachability**: latent / fixture-reachable  
- **Claim**: `CreateSubscriptDecl` never calls `IsNodeModuleInternal`, never sets SPI/internal flags, and `SubscriptDecl` has **no** `IsModuleInternal` field. `internalMemberKeys` deliberately excludes subscripts. A `@usableFromInline internal subscript` that appears in ABI can still reach `SubscriptHandler` if other gates pass — **leak** opposite of A8-001/002 undercount. Conversely, protocol bare subscripts are fine for labels (ABI printedName) but never enter `PublicMemberNames` (would not matter without a classifier).  
- **Evidence**:  
  - `CreateSubscriptDecl` ~3205–3270 (no visibility)  
  - `SubscriptDecl.cs` fields  
  - `MemberCollectionWalker` internal-set kinds list  
  - `SubscriptHandler` gates: Pattern2 reach, static, modules, AnyType — not visibility  
- **Probe**: ABI fixture with `@usableFromInline internal subscript(i: Int) -> Int` on a public type; assert whether C# indexer emits.  
- **Suggested fix shape**: Add visibility to subscript create path mirroring properties (or gate on parent + UsableFromInline on node / InternalMemberKeys if extended).  
- **Prior art**: none  

---

### DA-W5-A8-004: Demangler unknown `Y*` annotations (incl. `Yt` `_const`)

- **Severity**: P3  
- **Status**: **already-known**  
- **Confidence**: high  
- **Lenses**: L1 (benign miss), L2  
- **Reachability**: latent (few symbols; roadmap: 4 current)  
- **Claim**: Only `Ya`/`Yb`/`YK` handled under `Y`; other `Y?` fall through to identifier demangle and may fail tree build → `HasAsyncMarker` false.  
- **Evidence**: `Swift5Demangler.cs` ~682–696; `roadmap.md` low-yield F11  
- **Prior art**: F11  

---

### DA-W5-A8-005: CSM / GenericSignature composition & dependent-member where clauses

- **Severity**: P2 (undercount / late wrapper fail)  
- **Status**: **already-known** (owned by A6 / roadmap medium)  
- **Confidence**: high  
- **Lenses**: L3, L4  
- **Claim**: `ParseConstraint` / CSM method-level filter store `P & Q` as single target; dependent-member LHS with `.` skipped. Prefer engine reject over wrapper-compile death (G1).  
- **Evidence**: `roadmap.md` CSM rows; `GenericSignatureParser.ParseConstraint`; A6 track  
- **Prior art**: roadmap medium ×2  

---

### DA-W5-A8-006: DependentMember + ProtocolComposition parse (positive / refuted as open bugs)

- **Severity**: n/a  
- **Status**: **refuted** as open defects  
- **Confidence**: high  
- **Lenses**: L1  
- **Claim**: Historical DependentMember dead-arm and composition-without-children are fixed.  
- **Evidence**: `CreateTypeSpec` ~3551–3572; `CreateProtocolCompositionTypeSpec` ~3809–3847; unit tests  

---

### DA-W5-A8-007: ABI ingestion contract (positive)

- **Severity**: n/a (integrity keep)  
- **Status**: **refuted** as fail-open hole for format version / unknown kinds  
- **Confidence**: high  
- **Lenses**: L3 integrity, L2  
- **Claim**: `GateAbiFormatVersion`, `KnownAbiNodeKinds`, SWIFTBIND033/034/046, `ParseReconciliation` + artifact manifest threading make digester drift loud under `--strict-inputs`.  
- **Evidence**: `SwiftABIParser` ~200–246, ~1010–1046, ~1092–1188; `AbiIngestionContractTests`  
- **Note (already-known latent R1)**: version read only on root `ABIRoot` — if digester moves stamp, spurious 033  

---

### DA-W5-A8-008: `public indirect enum` / same-line-`{` scope gates drop type from member-key scopes

- **Severity**: P2  
- **Status**: **candidate**  
- **Confidence**: medium  
- **Lenses**: L3, L5  
- **Reachability**: latent  
- **Claim**: Type scope push requires access + optional `final` only, and same-line `{`. `public indirect enum`, multi-line body open, etc. do not push → members may not enter `PublicMemberNames` with the expected prefix → false internal via negative space (methods) or missing defaults/labels (facts walkers share similar gates).  
- **Evidence**: `MemberCollectionWalker` `matchesTypeDeclShape` ~195–207; docs in walker header  
- **Probe**: `public indirect enum E { public var x: Int }` in a synthetic swiftinterface extract.  

---

### DA-W5-A8-009: L4 dual visibility oracles (simplification)

- **Severity**: P2  
- **Status**: **simplification**  
- **Confidence**: high  
- **Lenses**: L4, L5  
- **Claim**: Access truth is split across ABI DeclAttributes, `InternalMemberKeys`, `PublicMemberNames` negative space, and ad-hoc emitter exceptions (protocol). Safe consolidation: single `VisibilityClassifier` taking `(node, parent, facts, isProtocolRequirement)` returning public/internal/spi with unit matrix covering protocol-req, nonisolated both orders, UFI internal, inlinable disambig.  
- **Suggested shape**: behavior-preserving extraction first; then fix A8-001/002 in one place.  
- **Do not do if**: merging without fixtures for StoreKit dual-set overloads and protocol-req workarounds.  

---

## 6. Test honesty (L2)

| Area | Coverage | Gap |
|------|----------|-----|
| Async marker | Strong BindingTests symbols | Unknown `Y*` not unit-pinned beyond F11 note |
| Mutating | Runtime parser tests | Mutating-divergent witness naming is roadmap (conformance drop) — not A8 parse bug |
| Protocol composition / DependentMember | Unit | Good |
| Visibility negative space | Foreign extension, inlinable disambig | **No** unit asserting protocol req stays public; **no** nonisolated public property matrix |
| EveryProtocol internal-req | Explicit pin that misclassification must not skip | Documents bug rather than fixing parser |
| Ingestion 033/034/046 | AbiIngestionContractTests | Solid |
| Operators / `_operatorChars` | CreateOperatorDecl unit | Overflow ops covered by handler reject path (parser-marshaler rule) — not re-audited as open parse bug |

---

## 7. File coverage (A8 scope)

| Cluster | Ledger suggestion |
|---------|-------------------|
| `Parser/SwiftABIParser.cs` | `reviewed-deep` / **hazard** (visibility dual oracle) |
| `Parser/GenericSignatureParser.cs` | `reviewed-deep` |
| `Parser/Producers/*` | `reviewed` |
| `tools/SwiftInterfaceParser/MemberCollectionWalker.swift` | `hazard` |
| Other walkers (spot) | `inventory` → `reviewed` as touched |
| `Demangler/Swift5Demangler.cs` | `reviewed-deep` (async arm + F11 residual) |
| Unit ParserTests / DemanglerTests | `reviewed` |

---

## 8. Prior-art / already-known index (do not re-chase)

| Item | Tag |
|------|-----|
| Protocol-req public visibility heuristic | roadmap medium → **DA-W5-A8-001** |
| Internal-receiver emission (UFI parent) resolved to emission gates | roadmap RESOLVED |
| CSM `P & Q` + dependent-member where | roadmap medium → **DA-W5-A8-005** |
| F11 demangler `Y*` | roadmap low-yield → **DA-W5-A8-004** |
| R1 `json_format_version` root-only | roadmap latent |
| R2 EOF-strict `some` / TypeSpec | roadmap latent |
| Demangler replacement | prior-art **NO-GO** |
| Mutating witness vs req naming divergence | roadmap (conformance validator drop) — emission naming, not ABI parse |

---

## 9. Ranked backlog (owner-gated)

| Pri | Item | Action class |
|-----|------|----------------|
| 1 | Fix protocol-req public membership in walker **or** skip negative-space when `protocolReq` / parent is `ProtocolDecl` | Correctness + delete workarounds later |
| 2 | Allow `nonisolated` (and audited peers) after `public`/`open` in BroadPublic* shapes | Correctness for actor APIs |
| 3 | Classify subscript visibility consistently | Leak/undercount hygiene |
| 4 | `VisibilityClassifier` SSOT (L4) | Simplification after 1–2 fixtures |
| 5 | Demangler ignore unknown `Y?` annotations (F11) | Low-yield robustness |
| 6 | CSM composition / dependent-member where (A6) | Already tracked |

---

## 10. Counts & risk summary

| Metric | Count |
|--------|------:|
| Findings total | **9** |
| Confirmed / already-known / refuted-positive | 001 known, 004 known, 005 known, 006 refuted-open, 007 positive integrity |
| New candidates | **3** (002 nonisolated, 003 subscript, 008 type-scope gates) |
| Simplification | **1** (009 visibility SSOT) |
| P0 | **0** |
| P1 | **2** (001 known + 002 candidate) |
| P2 | **4** (003, 005, 008, 009) |
| P3 | **1** (004) |
| **Risk rating** | **3 / 5** |

**Headline repeat:** Parse/demangle **core fidelity is strong**; residual risk is **visibility dual-oracle** (protocol-req already-known + workarounds; nonisolated/subscript gaps still open). Not a wholesale ABI/demangler rewrite problem.

---

## 11. G1 cross-link

From `synthesis/graceful-degradation-map.md`: misparse that sets wrong `IsModuleInternal` tends to **skip-and-continue** (usable partial) rather than emit-then-break — aligned with product L3 for undercount, misaligned for **false undercount of public API**. Prefer fixing the classifier over adding more emitter special cases (every new special case is L5 hazard).
