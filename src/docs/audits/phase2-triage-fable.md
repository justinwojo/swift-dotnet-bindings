# Phase-2 Adversarial Triage — Independent Re-verification (Fable, 2026-06-09)

**What this is:** a read-only re-verification of the latent/deferred defect claims in
`src/docs/audits/REMAINING-WORK.md` (§2.1–2.6 and the §4 hardening pool), re-measured against the
tree at `120b4c9e` with **freshly regenerated** `BindingTests/output` (`nuke binding-tests
--compile-only`, exit 0, generator Debug dll rebuilt first). No source was edited; this file is the
only artifact. Validation-library generated output was **not** available (`/tmp/binding-validation-*`
empty) — all generated-output evidence is from the BindingTests surface plus the checked-in
`artifacts/x64-sim-gate` StoreKit output.

Verification depth is marked per row: **[V]** = verified directly in the main line,
**[A+S]** = agent-verified, key lines spot-checked in the main line, **[A]** = agent-verified only.

---

## 1. Verdict table

Verdict ∈ {Reachable, Unreachable-confirmed, Inconclusive, New-sibling-found}. "Reachable" means a
concrete emission site or generated-output hit exists in the current surface (or the activation is a
runtime condition, not a generator-input condition).

### §2 — the six latent logged defects

| Claim | Verdict | Evidence (current file:line + grep) | Conf. | Recommended action |
|---|---|---|---|---|
| §2.1 ClosureProjection escaping-param branch | **Unreachable-confirmed** [V] | `CallbackDeclarations` production readers = 0 (grep: only unit tests + the 4 definitions). `MethodMarshalPlanBuilder.cs:332-362` routes closures via `ClosureHandler`. `WrapperEmitter.Marshalling.cs:473-500` (`EmitTypeConversions`) gates closure params out before `GetParameterPlan` at `:705`. `EnumHandler.CaseConstruction.cs:285` routes `ClosureProjection` to the direct-lowering `else`; the `:299` factory path is gated `NamedTypeSpec`-only (`:293`). `AsyncMethodGenericBridgeEmitter.cs:1216-1228` iterates `setArgs` only. `OptionalProjection.GetParameterPlan` delegates to inner only on `UsesObjCContainerBridge` (false for closures). Output: `s_closureCallback` static field = 0 hits (the `func_closureCallback_get/set` hits at `SwiftBindingsTestLibDependency.cs:6205-6214` are vtable fields for a fixture property literally named `closureCallback`). | High | Keep latent |
| §2.2 Owned existential collection-element carrier fall-through | **Unreachable-confirmed** [V] | Both fall-through branches still present: `ExistentialProjection.cs:162-186` (`GetArrayElementCarrierConversion` — class-bound and EC1-with-proxy take minting paths; null-proxy EC1 and composition fall to `GetParameterElementConversion`). Re-ran the doc's greps on fresh output: `Select(.*GetExistentialContainer())` = **0**, `FromEnumerable<…ExistentialContainer[23]` = **0**. Owned-carrier minting count drifted **19 → 23** (`CreateOwnedExistential1` = 13, `CreateOwnedClassCarrier` = 10 in `SwiftBindingsTestLib.cs`) — all on minting paths. | High | Keep latent |
| §2.3 Async `CreateAsync` raw-IntPtr surface | **Unreachable-confirmed** (mechanism live, zero emission) [A+S] | File moved: `src/Swift.Bindings/src/Emitter/StringEmitter/SwiftUIBridgeEmitter.AsyncPattern.cs` (not `Handler/`). Type-switch fallback now `:1151`, null-check `:1191-1192`, raw forward `:1277`; non-async typed conversion `SwiftUIBridgeEmitter.cs:3053-3055`. Output: `grep "CreateAsync(.*IntPtr" BindingTests/output/*.cs` = 0; all 4 emitted `CreateAsync` overloads are string/int/bool-only. "Works via `.Handle`" assertion re-checked and still holds (Swift side converts eagerly pre-Task, `AsyncPattern.cs:751-760`; Task closure captures strongly). Ergonomics-only verdict upheld. | High | Keep latent — but see **F4** (sibling found on the same path is compile-break severity, not ergonomics) |
| §2.4 Typed-closure ObjC-bridgeable **class** arg decode split | **Unreachable-confirmed** [A] | Split confirmed at `SwiftUIBridgeEmitter.cs:3686-3695` (BoundStruct+ObjC → `GetNSObject`, `:3688`; BoundType+ObjC → `MarshalFromSwift`, `:3695`) vs Result-branch `:3966-3983` (BoundType+ObjC → `GetNSObject` + `passUnretained`). Decisive: `TypeRecordFlags.ObjCBridgeable` has exactly ONE assignment site (`TypeDatabase.cs:532`, from XML `objcBridgeable`); the attribute appears on exactly 3 XML entries, all `kind="struct"` (URL, URLRequest, Decimal — `FoundationDatabase.xml`), 0 on `kind="class"`. So BoundType+IsObjCBridgeable is data-unreachable today (not structurally impossible — a future XML entry would activate it). Swift/C# sides of each path are internally consistent pairs. | High | Keep latent; add a guard test if curated XML ever grows `kind="class" objcBridgeable` |
| §2.5 Same-signature closure/async fan-out gap | **Unreachable-confirmed** (mechanism live) [V] | `EveryProtocolEmitter.cs:1426-1434`: owner/sibling plan applies to ALL methods (non-owner skips), but `methodPlan` is threaded only into `EmitMethodImplementation` (`:1520`); the closure/async paths (`:1468/:1470/:1472`) don't receive it. Owner closure body force-unwraps its OWN vtable: `EveryProtocolEmitter.cs:4022` (`{{vtableInstanceName}}.{{fieldName}}!(…)`) — loud nil-unwrap trap, as claimed. C# receiver sibling fallback gated off: `ProtocolProxyEmitter.Receivers.cs:976` (`!method.IsAsync && !hasDispatchableClosureParamForFallback`). No fixture/validation shape produces the activating collision. The doc's old anchor `:3732` has drifted to `:4022`. | High (mechanism) / Med (absence of reaching shape — empirical) | Keep latent |
| §2.6 Apple-framework SwiftUI-bridge second-slice atomicity | **Reachable** (narrow), claim partially overstated + **New-sibling-found** [A+S] | `Sdk.targets:1144` (`_CompileAppleFrameworkSecondBridgeSlice`); commit is `RemoveDir` + `mv` (`:1257-1261`) — NOT the merger's rename-aside protocol (`WrapperXCFrameworkMerger.cs:51-67, 173-184`). BUT "never audited" is wrong: a drop-incomplete repair (SWIFTBIND052, `:1273-1312`) + per-build metadata re-probe exist since `8099d434` (2026-05-30). Residual windows: kill between RemoveDir and mv → **silent** bridge loss (repair is `Exists()`-gated so it never fires); kill mid-RemoveDir → torn tree can pass the top-level-dir-count guard (`:1294`). Fingerprint stamp is written pre-swap (`:639-643`) so stale state persists across incremental builds. X64SimGate gap confirmed empirically (StoreKit leg has `HasBridge*=False` in its checked-in `binding-metadata.props`; 0 "bridge" mentions in `Build.X64SimGate.cs`). The **wrapper** sibling is worse — see **F2**. | High (mechanism) / Med (severity) | Promote to session **together with F2** (same fix shape: adopt the merger's staging→atomic-swap + recovery in both targets) |

### §4 Tier 1

| Item | Verdict | Evidence | Conf. | Recommended action |
|---|---|---|---|---|
| T1-1 (= §2.6) | see §2.6 row | — | — | Promote (with F2) |
| T1-2 Co-gater brace-walker | **Unreachable-confirmed on current corpora; premise REFUTED (new evidence)** [A+S] | Walkers still stateless: `CSharpWrapperCoGater.cs:1992-2007` (`FindBlockEnd`), `:1942-1959`, `:527-585`. Corpus scan (~375k generated lines, 4 corpora incl. StoreKit): 1,886 brace-in-literal/comment lines, **0 unbalanced**. BUT the "generated C# never carries unbalanced braces in literals" premise has an open channel: Swift string **default values** pass verbatim — `SwiftInterfaceAccessParser.cs:5378` (substring after `" = "`) → `SwiftDefaultValueMapper.cs:75-76` (`return expr;`, spot-checked) → emitted decl line (output `:20757` `greeting = "Hello"` proves it end-to-end). A third-party `= "{"` default emits an unbalanced literal brace TODAY. This is new evidence, stated explicitly per the re-chase rule. | High | Keep latent but **log the default-value channel** next to the §3 entry; cheapest hardening is brace-escaping/balancing awareness in the walker or a guard in the default-value mapper |
| T1-3 Apple prefix / enum-kind / collection-fallback drifts | **Unreachable-confirmed** (a, b) + **New-sibling-found** (c) [A] | (a) Only `Swift` module lacks an `apple-frameworks.json` entry (handled by `IsSwiftSystemModule`, `AppleFrameworkRegistry.cs:609`); Metal `MTLTextureSwizzleChannels`/`MTLPackedFloat3` gap (§3) still present but Metal is in no validation lib / fixture. (b) Zero `kind="enum"` entries in optionalFallback modules missing from `valueTypes` (both XML trees checked; `CGBlendMode` mismatch is in non-fallback CoreGraphics — inert). (c) Optional-vs-element fallback guard chains are 9/9 identical post-`76608c2a` (`TypeProjectionFactory.cs:199-207` vs `:592-600`) — but see **F6** (concrete-class fallback asymmetry) and **F15** (`IsOptionalObjCBridged` missing 3 of the factory's guards, 1 theoretical). | High | Keep latent except **F6** (needs deeper pass — tier-1 validation libs are in its blast radius) |
| T1-4 Parser duplication + modifier completeness | **Unreachable-confirmed**; doc count corrected [A] | Duplication re-measured: **~18**, not 23 (15 `typeStack` loops + 2 braceDepth-only in `SwiftInterfaceAccessParser.cs`, 1 in co-gater). All structural differences intentional (`IndexOf` vs `LastIndexOf` documented; `IsProtocol` field) — no diverged sibling pair found. Modifier regexes: `nonisolated`-first orderings handled via prefix-strip (`:2839`) or unanchored match; only `nonisolated(unsafe)` is missed (documented "semantic cliff"; the one fixture using it — `SharedState.counter` — has no isolation context, zero observable effect). No current decl any regex fails on. | High | Keep latent / drop from Tier 1 (yield is maintainability only) |
| T1-5 Protocol key divergences, subscripts, cross-pool | **Mostly latent-confirmed; 4 narrow new siblings** [A] | Three key builders' key-shape lines still identical and all thread `propertyNames` (`IHandler.cs:670`, `ProtocolSignatureHelper.cs:154`, `DefaultParameterOverloadEmitter.cs:763`); `.WasEmitted = true;` count = 23 (matches constraint). Divergence beyond the constraint: see **F7** (ProtocolSignatureHelper lacks CancellationToken injection), **F8** (Vtables skip-key gap), **F9** (duplicate-subscript proxy skip gap), **F10** (subscripts have no WasEmitted/override parity), **F14** (CSM trim-pool null propertyNames). Extension manual key: clean post-`313b2a2d` (`ProtocolExtensionEmitter.cs:344/:362` use the central builder). | High (mechanisms) / Med (all activation shapes are fixture-absent) | Keep latent; if a protocol-emitter session opens, take F7–F10 as a batch |
| T1-6 SwiftUI async parity + identifier guard | (a)(b) **Unreachable-confirmed**, (c) **Reachable (input-conditional)** [A+S] | (a) No sync-de-duped name the async path misses — each path guards its own synthetic-name set (`SwiftUIBridgeEmitter.cs:2749-2755` vs `AsyncPattern.cs:670-676/:941-948/:1176-1182`); Swift closure shadowing is legal. (b) Parity matrix complete: every param class is handled-in-both or explicitly gated (`BridgeParamToFlatParam` → null → chain returns false — deterministic, not silent). (c) **Identifier guard absent** — zero keyword-escaping in all three bridge emitter files; param names flow raw from ABI (`SwiftABIParser.cs:2181`) to emitted C# param list (`SwiftUIBridgeEmitter.cs:2720-2722`, spot-checked: `$"{type} {param.Name}"`). A View init param named `event`/`delegate`/`string` breaks the generated C#. Other emitters DO guard (`MethodWrapperEmitter.cs:977`, `ConstructorWrapperEmitter.cs:1249`, `EveryProtocolEmitter.cs:5411`). | High | (c) **Promote** — cheap fix (apply the existing escape helpers), real third-party exposure; (a)(b) drop |
| T1-7 Skip taxonomy / coverage matrix / docs drift | **Confirmed still open** (factual sub-claims verified) [A] | `[MonoJitCrash]` attribute usages = 0 (the only code hits are the `SimCtl.IsMonoJitCrash` crash-detection helper). In-use skips: `[Skip]`×18, `[SkipOnSimulator]`×10, `[SkipOnMonoJit]`×2; `[SkipOnDevice]`/`[SkipOnCatalystX64]` defined but 0 usages; no `SkipOnNativeAOT` exists. `coverage-matrix.json`: producible only by manual `coverage-report.py` run — never produced by any nuke target. Stale scripts: only soft-stale (`skip-metrics.py` references the throwaway internal repo + a nonexistent optional baseline; handled gracefully). Upstream count drift confirmed: `roadmap.md` says "exactly 4" but its Blocked table rows are {1, 2, 3, comment} — Issue 4 (Catalyst x64) present in memory + `upstream-issues-README.md` but missing from the roadmap table AND from the `RuntimeLimitations` enum. | High | Keep as cleanup pool; the roadmap/enum Issue-4 drift is a 10-minute doc fix worth folding into any docs pass |

### §4 Tier 2

| Item | Verdict | Evidence | Conf. | Recommended action |
|---|---|---|---|---|
| EC2+ composition owned-return finalizer destroy | **Reachable — live emission in current output** [V] | See **F1**. This was listed as "still latent"; it is not — emission sites exist now. | High | **Promote to session** (top of list) |
| Async non-throwing hang | **Unreachable-confirmed (mitigated — claim outdated)** [A] | `AsyncHarnessEmitter.cs:1242` (`BuildAsyncCallbackFaultCatch`) applied at all 7 TCS-resolving sites; output: 303 × "awaiter cannot hang"; sibling async emitters carry their own guards; Swift non-throwing wrapper always invokes the callback. Residual shape is abort-not-hang (exception escaping `[UnmanagedCallersOnly]` cleanup). | High | Drop from pool |
| Box-GCHandle fallbacks | **Reachable mechanism, by-design degraded branch** [A] | 5 emitted `TryAllocateBoxedContext` + fallback ternaries (`SwiftBindingsTestLib.cs:9686…236872`); fallback fires only when the runtime dylib is absent — documented leak-over-crash contract (`SwiftClosure.cs:204-213`). | High | Keep latent (working as designed) |
| Demangler remaining Y* | **Reachable — real-output hit, benign today** [A] | Only `Ya`/`Yb`/`YK` handled (`Swift5Demangler.cs:552-566`); **`Yt` (`_const`) appears 4× in the current ABI JSON** (fixture `ConstLiteralInit.swift:21-30`, bound ctors P/Invoked at output `:273419/:273437`) → demangle fails → `IsAsync` falls to the mangled-name heuristic, variadic detection lost. No misbehavior today (no `Ya` in those symbols; const facts come from swiftinterface). `Ya` mid-identifier false-positive residual: no current identifier contains "Ya". | High | Keep latent; cheap robustness: treat unknown `Y?` as ignorable annotation instead of failing the demangle |
| GenericSignatureParser gaps | **Split** [A] | Inline `<T : P>`: unreachable — all constraint-bearing no-`where` sigs in the ABI JSON are on Protocol TypeDecls, excluded at `SwiftABIParser.cs:1034`. Same-type-dependent: claim **partially refuted** — the reaching fixture (`MethodLevelGenerics.swift:190`) parses into a mis-typed-but-harmless representation and emits correct, compiling output via the CSM pairing filter. Value generics (`<let N : Int>`): zero presence AND **no gate** — would reproduce the pre-gate malformed-identifier cascade (parameter packs have the `each` gate at `GenericTypeEmitter.cs:483-489`; value generics have nothing). See **F17**. | High/Med/High | Keep latent; add a value-generics gate opportunistically when touching `GenericTypeEmitter` |
| EveryProtocol walker defaults / identifier emission | **New-sibling-found (latent)** [A] | 11 raw member-name emission sites (`EveryProtocolEmitter.cs:2441…4241`), zero `ParserNameToSwift`/escape usage for member NAMES (parameter names/labels ARE escaped — the guarded-label path is tested via `SiblingPropertyDispatch.swift:212`). Keyword-named protocol member → invalid Swift (`public var default:`) or non-conforming rename (`_default`). No reaching fixture. See **F13**. | High | Keep latent; same fix family as T1-6(c) |
| Arch/fingerprint/consumer-target P2s | **Core rule satisfied + 3 new gaps** [A] | `$(SwiftTargetArchitectures)` confirmed in BOTH fingerprint echoes (`Sdk.targets:602`, `:625`). New unfingerprinted generation inputs: see **F12**. | High / Med-High (gaps) | Fold F12 into any SDK-targets session (e.g. the §2.6/F2 one) |

---

## 2. New findings

Ordered by value. Each has a concrete emission site or output hit plus the reproducing condition.

### F1 — EC2+ composition owned-return proxies run a direct CallConvSwift VWT destroy on the finalizer thread **[V — verified end-to-end in main line]**

- **Emission site:** `ModuleHandler.cs:1950` — composition-proxy `ReleaseAdoptedSwiftContainer()` calls
  `SwiftMarshal.DestroyWireBufferRetains(...)` (direct `ValueWitnessTable->Destroy`, CallConvSwift —
  `SwiftMarshal.cs:189`) and is invoked from BOTH `Dispose()` and the finalizer.
- **Contrast:** the EC1 proxy emitter, `ProtocolProxyEmitter.SwiftObject.cs:98`, routes the identical
  body through `DestroyWireBufferRetainsFinalizerSafe` (the `SBW_VWTDestroy` `@_cdecl` trampoline)
  with an emitted comment naming the exact hazard: *"A direct VWT Destroy (CallConvSwift) from the
  finalizer thread crashes Mono with the !ji->async assertion."*
- **Live in current output:** `SwiftBindingsTestLib.cs` — 3 composition proxies
  (`AgeableAndNameableProxy` `:277333`, `DescribableAndTestIdentifiableProxy` `:277449`,
  `LabelableAndRenderableProxy` `:277568`), each with `~Proxy()` → `ReleaseAdoptedSwiftContainer()`
  → `DestroyWireBufferRetains` (`:277422/:277538/:277657`), and **owned-return constructions**
  (`ownsContainer: true`) at `:968, :1091, :18829, :27702` etc.
- **Reproducing condition:** on Mono (simulator/Catalyst), obtain an owned EC2/EC3 composition
  existential return, drop it without `Dispose()`, force GC + finalization. This is a *runtime*
  condition, not a generator-input condition — the only reason gates stay green is deterministic
  disposal in tests.
- **Why the audit missed the upgrade:** the Tier-2 entry recorded the variant split but not that the
  owned-return EC2 construction paths already emit in the BindingTests surface.
- **Fix shape:** swap `ModuleHandler.cs:1950` to the FinalizerSafe variant (the EC1 contract),
  gate with a finalizer-path probe mirroring the EC1 one. CC-adjacent → `--device` run per policy.

### F2 — Wrapper second-slice commit is non-atomic with NO incomplete-drop guard (worse sibling of §2.6) **[A+S — commit steps + zero-guard greps spot-checked]**

- **Site:** `Sdk.targets` `_CompileAppleFrameworkSecondWrapperSlice` (target at `:884`): merge into
  staging is fine, but the commit is `RemoveDir` (`:1103-1104`) + `mv` (`:1105-1107`). Greps for
  `_AFW_WrapperIncomplete` / `SWIFTBIND053` = **0** — the bridge path's drop-incomplete repair
  (SWIFTBIND052) has no wrapper analog.
- **Reproducing condition:** hard kill between the RemoveDir and the mv → the **wrapper**
  xcframework is gone; the fingerprint stamp was written pre-swap (`:639-643`), so the next
  incremental build is "up to date", re-probes existence, records
  `_SwiftBindingHasWrapperXCFramework=False`, and silently drops the consumer `NativeReference` →
  `DllNotFoundException` for every wrapper-backed API (the exact blast radius the
  "will-be-produced signal" constraint exists to prevent).
- **Fix shape:** adopt `WrapperXCFrameworkMerger.MergeFatSlices`' rename-aside + `.superseded`
  recovery protocol in BOTH second-slice targets (bridge + wrapper), or shell `mv aside && mv in &&
  rm aside` so no window has zero live trees.

### F3 — SwiftUI bridge emits user param names with no keyword/identifier guard (T1-6c, promoted) **[A+S]**

- **Site:** `SwiftUIBridgeEmitter.cs:2720-2722` (`$"{type} {param.Name}"` — spot-checked), names raw
  from `SwiftABIParser.cs:2181`; zero escape-helper hits in all three bridge emitter files, while
  `MethodWrapperEmitter.cs:977`, `ConstructorWrapperEmitter.cs:1249`, `EveryProtocolEmitter.cs:5411`
  all guard.
- **Reproducing condition:** any third-party View with an init param named a C# keyword —
  `init(event: ...)`, `init(delegate: ...)`, `init(string: ...)` — produces uncompilable generated
  C# (missing `@`). Swift-keyword names break the Swift side symmetrically.
- **Fix shape:** apply the existing escape helpers at the bridge param-emission sites; fixture with a
  keyword-named View param as the gate.

### F4 — Async CreateAsync drops `IsSimpleEnum`: complex enum leaf emits `(int)<class instance>` **[A+S — both sides of the split spot-checked]**

- **Site:** `AsyncPattern.cs:1271-1274` casts unconditionally (`({param.CSharpPInvokeType}){param.Name}`);
  the flattener (`:394-396`) doesn't propagate `IsSimpleEnum` (no such field on `AsyncFlatParam`).
  Non-async splits correctly at `SwiftUIBridgeEmitter.cs:3040-3051` (`IsSimpleEnum` → cast, else
  `.RawValue`) — the exact trap the repo's swiftui-bridge rule documents.
- **Reproducing condition:** async-pattern View whose construction-chain leaf is a complex
  (class-projected) raw-value enum → generated C# compile error (CS0030 shape). Same
  zero-current-fixture status as §2.3, but compile-break severity rather than ergonomics. Found by
  the §2.3 verification (sibling on the same path).

### F5 — Co-gater "never emits" premise refuted: verbatim string-default passthrough **[A+S — mapper + output line spot-checked]**

- **Channel:** `SwiftInterfaceAccessParser.cs:5378` (verbatim substring after `" = "`) →
  `SwiftDefaultValueMapper.cs:75-76` (`return expr;`) → C# decl line (proven end-to-end by output
  `:20757` `greeting = "Hello"` ← `Defaults.swift:9`).
- **Reproducing condition:** a third-party Swift default like `prefix: String = "{"` puts an
  unbalanced brace inside a string literal on a generated decl line → every stateless brace walk in
  `CSharpWrapperCoGater.cs` (`:1992`, `:1942`, `:527`) miscounts for the rest of the file when
  co-gating fires. Current corpora are clean (0 unbalanced in ~375k lines, 4 corpora) — the
  unreachability is corpus-contingent, not structural. NEW evidence per the §4 re-chase rule.
- **Siblings:** `SwiftSourceStripper.cs:657-670/:884-885` (stateless walks over **Swift** input that
  already contains 35 per-line-unbalanced comment-brace lines — net-balanced today, so no observed
  corruption); `SwiftWrapperPostProcessor.cs:323`, `SimulatorOnlyMemberDetector.cs:479` (clean
  corpora); co-gater `BuildLineToTypeMap` comment-blind type detection (1,212 doc-comment lines
  match the type regex in current output — consequence currently contained to body-line keying).

### F6 — Concrete-class fallback asymmetry: `Optional<Entity>` projects, `[Entity]` drops the member **[A]**

- **Site:** `TypeProjectionFactory.cs:239` (Optional path consults
  `IsConcreteClassFallbackModule` → `ClassProjection`), but `TryProjectObjCElement`
  (`:590-611`) does not — element fallback only honors `IsOptionalFallbackModule` +
  `HasObjCClassPrefix`.
- **Reproducing condition:** `RealityFoundation.Entity` (module not in optionalFallback) or
  `RealityKit.AnchorEntity` (no `RE` prefix match) as an Array/Dictionary/Set ELEMENT → projection
  null → member dropped, while the same type as `Optional<T>` emits correctly. RealityKit and
  RealityFoundation are **tier-1 validation libraries**, so a validate sweep over them can hit this.
  Severity: silent member drop (coverage), not a crash.

### F7–F10 — Protocol-emitter narrow latents (batch) **[A]**

- **F7:** `ProtocolSignatureHelper.GetProjectedCSharpMethodKey` lacks the CancellationToken
  injection both sibling builders perform (`IHandler.cs:665-668`,
  `DefaultParameterOverloadEmitter.cs:757-760`) → a protocol declaring async `fetch()` AND sync
  `fetch(cancellationToken:)` emits a CS0111 interface. Beyond the constraints-file contract
  (which tracks only `propertyNames`).
- **F8:** `ProtocolProxyEmitter.Vtables.cs` (~`:80`) consults only `_closureSkippedMethodKeys`, not
  `_skippedMethodKeys` (which `StaticInit.cs:224` does honor) → an interface-skipped method still
  gets a vtable struct field that StaticInit never assigns, while the Swift side fills the slot →
  null-fn-pointer dispatch. Triggering shape needs a property/method PascalCase collision trio.
- **F9:** duplicate-subscript interface skip (`ProtocolHandler.cs:276-282`) increments the index but
  doesn't record into `skippedSubscriptIndices`; the proxy (`InterfaceImpl.cs:44`) would emit both →
  CS0111 indexers. Needs two same-projected-param subscripts on one protocol.
- **F10:** `SubscriptDecl` has no `WasEmitted`/`IsOverride`; no `HasSubscriptInResolvedAncestors`
  reader; `SubscriptHandler` never emits `override` → derived-class subscript override → CS0108.
  All four: mechanism confirmed, zero reaching fixtures.

### F11 — Demangler `Yt` unhandled, hit by real output **[A]**

`Swift5Demangler.cs:552-566` handles only `Ya/Yb/YK`; `Yt` (`_const`) appears in 4 current mangled
names (`ConstLiteralBox` ctors, P/Invoked at output `:273419/:273437`) → demangle returns
ReductionError → async/variadic detection falls to heuristics. Benign today; logged so the next
`Y*` Swift adds (`Yc` global actor, `Yi` isolated…) don't surprise.

### F12 — Three unfingerprinted generation inputs in Sdk.targets **[A]**

`$(SwiftFrameworkType)` missing from the XCFramework-mode echo (`:602`) though it flips the whole
pipeline (`--objc`, `:1491`) — the Apple-mode echo hashes its analog explicitly;
`%(SwiftAppleFrameworkTarget.NamespacePattern)` missing from the Apple-mode echo (`:625`) though
passed via `--namespace-pattern` (`:736/:771`); `$(SwiftAppleSupplementVersion)` missing from both
echoes though it feeds `--apple-version` in all three command builders. Each → stale-reuse on an
in-place flip. Same family as the constraints-file fingerprint trap.

### F13 — EveryProtocol member-NAME emission unguarded (label path is guarded) **[A]**

11 sites emit `public var/func {member.Name}` raw (`EveryProtocolEmitter.cs:2441…4241`); parameter
names/labels go through the escape helpers and have a passing fixture
(`SiblingPropertyDispatch.swift:212` → `Wrapper.swift:6708`). Keyword-named protocol member →
wrapper compile error or non-conforming witness. No reaching fixture.

### F14 — CSM trim-variant pool keys with null `siblingPropertyNames` **[A]**

`ConcreteProtocolSpecializationEmitter.cs:498` / `.Async.cs:446` pass null propertyNames into
`GetProjectedOverloadKey`; trim pool and main pool don't share emitted-signature state → a
property-renamed (`Foo`→`FooMethod`) CSM method with trailing defaults can collide cross-pool.
Narrow; documented-by-comment as intentional for the trimEnv, but the cross-pool seam is real.

### F15 — `IsOptionalObjCBridged` is missing 3 of the factory's 9 guards **[A]**

`MarshallingHelpers.cs:172-175` lacks `!ContainsGenericParameters`, `!IsStdlibContainer`,
`!IsPointerType` vs `TypeProjectionFactory.cs:199-207`. Two are masked by the module check; the
generic-parameters one is theoretical (the activating TypeSpec shape doesn't occur in Swift ABI
JSON). The constraints-file "must match exactly" assertion is literally false today — worth a
parity test even if behavior-neutral.

### F16 — Enum payloads containing `Optional<Closure>` route through `OptionalProjection`'s generic fall-through **[V — Inconclusive]**

From the §2.1 verification: `Optional<Closure>` IS projectable (`TypeProjectionFactory.cs:172` →
`ProjectClosure`), and an enum TUPLE element of that shape would match the
`proj is OptionalProjection` arm (`EnumHandler.CaseConstruction.cs:262-283`) →
`OptionalProjection.GetParameterPlan` generic fall-through → `SwiftOptional<Func<…>>`-shaped code of
unverified compilability. The public-type path excludes closure-bearing bound generics
(`:869-870` `ContainsClosureTypeSpec`) but the construction-path factory calls (`:221`, `:299`) have
no such guard. No enum-with-closure-payload fixture exists anywhere in BindingTests, so this could
not be confirmed without writing a fixture (out of scope read-only). **Inconclusive — needs a
compile probe** before it's either a defect or refuted.

### F17 — Value generics have no gate (parameter packs do) **[A]**

`GenericTypeEmitter.TryGetVariadicGenericParameter` keys only on the `each ` prefix
(`:483-489`); a `<let N : Int>` signature would flow into the malformed-identifier cascade the
`each` gate was added to stop. Zero presence in any current input; logged as the missing sibling of
an already-shipped gate.

### Doc corrections found along the way

- §2.2's "19 emitted owned-carrier conversions" → **23** on the current output (13 + 10).
- T1-4's "23 duplicated loops" → **~18**.
- §2.6's "never audited for atomicity" → partial recovery shipped 2026-05-30 (`8099d434`); residual
  windows are as listed above.
- Tier-2 "async non-throwing hang" → **mitigated** (fault-catch at all 7 sites; 303 output guards).
- Tier-2 "same-type-dependent unsupported" → the current reaching shape parses (mis-typed
  representation) and emits correct output; only un-exercised dependent forms remain open.
- `roadmap.md` "exactly 4" upstream issues vs Blocked table rows {1, 2, 3, comment}: Issue 4
  (Catalyst x64) missing from the table and from the `RuntimeLimitations` enum.
- §2.4's InitAnalyzer comment (`InitAnalyzer.cs:481-483`) names a CLASS (`AVCaptureDevice`-style) as
  an `IsObjCBridgeable` example — stale prose; no class record can carry the flag today.

---

## 3. Coverage note (what was NOT reached)

- **§4 Tier 3 — not triaged at all**: duplicated ladders/walkers repo-wide (beyond the co-gater
  siblings incidentally found), the L2 ObjC interop pipeline audit, L3 perf/API-drift readiness,
  and the L1 docs-drift sweep. No verdicts exist for these.
- **Tier 2 partial depth:** "EveryProtocol conformance-body locals + skip-ladder" — only the
  identifier-emission leg was verified (F13); the skip-ladder structure and conformance-body
  LOCAL-variable shadowing were not independently traced. "Existential residual carriers" beyond
  the §2.2 greps and the box-GCHandle/finalizer items were not exhaustively enumerated.
- **No validation-library generated output existed** and `nuke validate` was not run (it dirties 8
  version-stamped source files — excluded by the read-only constraint). All "zero emission"
  claims are therefore proven on the BindingTests + StoreKit-gate corpora only; F6 in particular
  (RealityKit element drop) is exactly the kind of finding a validate-output grep would settle.
- **F16 needs a compile probe** (enum + `Optional<Closure>` payload fixture) — deliberately not
  built in this read-only pass.
- **§2.4, T1-3, T1-4, T1-5, T1-7, and the Tier-2 sweep** are agent-verified with main-line
  spot-checks only where marked [A+S]; rows marked [A] were not independently re-derived in the
  main line. Their grep commands are reproducible from the evidence column.
- Refuted-list items (Apple short-prefixes, enum width-truncation, parser comment/string blindness,
  SwiftUI reserved-name collisions, SwiftUI ObjC-closure UAF/leak, demangler Ya/Yb/YK) were **not
  re-chased**, with two explicit new-evidence exceptions: F5 (co-gater premise — a *build-helper*
  scanner, adjacent to but distinct from the refuted "parser comment/string blindness") and the
  `Yt` finding (F11 — a *different* Y-operator than the refuted three).

## Suggested session reshape (by yield)

1. **F1** (EC2+ finalizer destroy) — live emission, known crash class, one-line fix + probe.
2. **§2.6 + F2 + F12** as one SDK-targets hardening session (atomic swap protocol ×2 + fingerprint).
3. **F3 + F13** as one identifier-guard session (same fix family, existing helpers, two fixtures).
4. **F6** — needs a validate-output or targeted-generation probe first (RealityKit element shapes).
5. F4, F7–F10, F14–F17 — keep latent, logged here; batch opportunistically with subsystem sessions.
