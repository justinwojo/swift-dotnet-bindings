# Track M2 — Wrapper / SDK / Packaging (G1-elevated)

| Field | Value |
|-------|--------|
| **Wave** | 7 (elevated by G1 packaging day-1 risk) |
| **Track** | M2 |
| **Date** | 2026-07-15 |
| **Mode** | Read-only (production code not modified) |
| **Risk rating** | **3 / 5** (integrity packaging is mature and fail-closed; **default wrapper-required package kill** remains the dominant day-1 partial-success blocker; residual fingerprint/stale hazards are real but mitigated in common paths) |
| **Confidence** | **high** on SWIFTBIND050→051 kill chain, will-be-produced NativeReference, shared arch fold + primary-restore; **medium** on stamp-before-success residual without a live MSBuild probe |
| **Lenses** | **L3 primary** (partial package); L1 integrity (false wrapper metadata / DllNotFound); L4 mega Sdk.targets / compiler; L2 gates (pack MAX_PATH / PackGate) |

## Product question (packaging slice of G1)

When wrapper compile fails or packaging is partial, does the consumer get:

1. A **usable nupkg / app** with honest warnings + report, or  
2. A **hard MSBuild Error** (or a silent DllNotFound from false/missing `NativeReference`)?

**Answer today:** Generator *wants* (1) in SDK mode (SWIFTBIND050 exit 0). SDK *re-hardens* to (2) by default (`SwiftWrapperRequired=true` → SWIFTBIND051 Error). Soft path exists but is opt-out, not day-1 default. Integrity holes that once dropped every-arch `NativeReference` are largely **closed** (primary-restore swallow, will-be-produced flags, SWIFTBIND040).

---

## 1. Method

1. Methodology L3 + L4; G1 report + `synthesis/graceful-degradation-map.md`; M0-C SDK two-pass map; constraints.md wrapper-arch notes.  
2. Deep-read: `Program.CompileWrapperForArchitectures` / `TryDecideWrapperArchitectures` / `ResolveAutoArchBasis`; `BindingsGeneratorCommand` packaging emit; `ConsumerTargetsEmitter` + `NativePackagingPolicy`; `SwiftWrapperCompiler` promote/restore; `StrippedSymbolCSharpReconciler` (C# co-gater); `WrapperBuildOutcome`; `Sdk.props` / `Sdk.targets` (050/051/052/056/062/080, fingerprints, pack).  
3. Cross-check dual signals: metadata “exists now” vs consumer “will be produced.”  
4. Tag G1-001 deepen; file residual packaging findings; refute closed traps where code holds.

---

## 2. Files reviewed-deep

| Path | Why |
|------|-----|
| `src/Swift.Bindings/src/Program.cs` (~2520 LOC) | Arch decision, `CompileWrapperForArchitectures` try/catch/finally, compile-wrapper-only, `HandleWrapperCompilationOutcome` |
| `src/Swift.Bindings/src/BindingsGeneratorCommand.cs` | Two-pass would-compile, packaging emit, mixed static drop, consumer targets |
| `src/Swift.Bindings/src/Configuration/SwiftWrapperCompiler.cs` (~2990 LOC) | Compile, strip, staged promote, empty-wrapper outcomes |
| `src/Swift.Bindings/src/Configuration/StrippedSymbolCSharpReconciler.cs` | Post-strip C# co-gater (“CSharpWrapperCoGater”) |
| `src/Swift.Bindings/src/Configuration/WrapperBuildOutcome.cs` | Fatal / 050 / 056 SSOT |
| `src/Swift.Bindings/src/Configuration/NativePackagingPolicy.cs` | Source drop vs WrapperAbsentFallback |
| `src/Swift.Bindings/src/Configuration/XCFrameworkMetadataExtractor.cs` | Metadata props + UpdateMetadataPropsWrapperStatus |
| `src/Swift.Bindings/src/Emitter/ConsumerTargetsEmitter.cs` | Will-be-produced NativeReference + Exists() |
| `src/Swift.Bindings.Sdk/Sdk/Sdk.props` | `SwiftWrapperRequired` default true |
| `src/Swift.Bindings.Sdk/Sdk/Sdk.targets` (~3800 LOC) | Two-pass, fingerprints, 050–080, pack, hooks |
| `src/Swift.Bindings.Sdk/Sdk/scripts/compile-wrapper-locked.sh` | Parallel fan-in lock (referenced) |
| `build/Build.WindowsPathGuard.cs` | MAX_PATH ship gate |
| Prior: G1, graceful-degradation-map, M0-C §2–5 |

---

## 3. Architecture inventory

### 3.1 Two-pass third-party (XCFramework) flow

```
_ComputeSwiftFingerprint  → stamp (+ UpToDate?)
_GenerateSwiftBindings    → --skip-wrapper-compilation --sdk-mode
                            emits C#, .swift, binding-metadata.props (HasWrapper=disk/now),
                            consumer .targets (HasWrapper=will-produce)
ResolveProjectReferences
_CompileSwiftWrapper      → --compile-wrapper-only (+ --target-architectures)
                            lock script; UpdateMetadataPropsWrapperStatus (disk truth)
_UpdateSwiftWrapperMetadata → re-XmlPeek HasWrapper / UnmetContractArchs
_ValidateSwiftWrapperCompilation → 051 if required & !HasWrapper; 056 if unmet contract
_ResolveSwiftNativeReferences → NativeReference(source?) + wrapper Exists()∧HasWrapper
_ConfigureSwiftBindingPack → TfmSpecificPackageFile + 038/039/040
```

**Why two-pass:** wrapper `-F` needs resolved ProjectReference dependency frameworks (`M0-C` / Sdk.targets comments).

### 3.2 Apple-framework flow (divergence)

| Aspect | Third-party XCFramework | AppleFramework |
|--------|-------------------------|----------------|
| Generate | Skip wrapper compile | Wrapper **inline** during generate |
| Pass-B `_CompileSwiftWrapper` | Active | **Skip** (`_SwiftWrapperSkip=true`) |
| Fat sim second slice | Via `--target-architectures` fold + source sim | `_CompileAppleFrameworkSecondWrapperSlice` / bridge twin |
| Auto arch basis | `ResolveAutoArchBasis` (re-resolve sim when device-pinned) | `ResolveAppleFrameworkAutoArchBasis` (synthetic arm64+x86_64 where applicable) |
| Consumer targets | Generator emit | Also `_SynthesizeAppleFrameworkConsumerTargets` post-compile |
| Source xcframework pack | Sliced + optional static drop | No source (OS framework) |

Divergence is **intentional**, not accidental dual-oracle — but it multiplies surface (second-slice atomic commit tests exist for DllNotFound class).

### 3.3 Dual HasWrapper signals (load-bearing)

| Signal | Written when | Meaning |
|--------|--------------|---------|
| `binding-metadata.props` `_SwiftBindingHasWrapperXCFramework` at generate | Pass A | **Exists now** (`hasWrapperXcfw`) — False under skip-wrapper |
| Same prop after `--compile-wrapper-only` | Pass B | **Disk truth** after compile (`UpdateMetadataPropsWrapperStatus`) |
| `ConsumerTargetsEmitter.HasWrapperXCFramework` | Pass A | **`hasWrapperXcfw \|\| wouldCompileWrapper`** (“will be produced”) |
| SDK NativeReference / pack items | After Pass B peek | Metadata **True** ∧ often **Exists()** |

**Integrity:** consumer nupkg `.targets` may *declare* wrapper NativeReference intent with `Exists()` guard — safe if pack only ships wrapper when metadata True after real compile. **SWIFTBIND040** fails closed if static source dropped (disk Exists wrapper) while metadata says HasWrapper≠True (stale dir + False metadata → no carrier).

### 3.4 Arch decision + fold (shared)

```
TryDecideWrapperArchitectures(auto | explicit)
  auto: x86_64-only → primary=x86_64;
        arm+x86_64 → primary=arm64|arm64e, extra=[x86_64];
        arm-only → primary=null (SelectArchitecture)
  explicit: every arch must be in source or SWIFTBIND052 fail *before* destructive fold

CompileWrapperForArchitectures(primary, extras)
  compile primary → move to .primary
  try { per-extra compile + MergeFatSlices } catch { swallow; log; keep primary }
  finally { restore primary to canonical (quarantine partial first) }
  return primary result (non-null if primary OK) + unmergedExtraArchs

contractualUnmet = explicit ? unmerged : []
WrapperBuildOutcome → SWIFTBIND056 if contractualUnmet non-empty (always fatal)
```

**Both** standalone generate and `--compile-wrapper-only` route through this (constraints.md trap closed). Bridge path mirrors arch fold for Rosetta/x64-sim parity.

### 3.5 Wrapper outcome severity

| Context | Primary fail | All-stripped | Explicit arch fold miss |
|---------|--------------|--------------|-------------------------|
| CLI standalone (`asyncLibraryAutoWired`) | Fatal exit 1 | Fatal | Fatal 056 |
| SDK generate / compile-wrapper (`sdkMode`) | **050 warn exit 0** | 050 | **056 hard** (even in SDK) |
| SDK MSBuild after metadata | — | — | 051 if `SwiftWrapperRequired` + !HasWrapper; 056 always |

Co-gater: `StrippedSymbolCSharpReconciler.ProcessDirectory` suppresses C# P/Invokes for stripped symbols → `MissingWrapperSymbol` report (not package death).

### 3.6 Native packaging policy (Gap 2)

| Source linkage | Wrapper present (intent or disk) | Source reference |
|----------------|----------------------------------|------------------|
| Dynamic | any | Always (Exists source) |
| Static | yes | **WrapperAbsentFallback** (`!Exists(wrapper) AND Exists(source)`) |
| Static | no | Always (source sole carrier) |

Pack/SDK-direct use **disk** `ShouldIncludeSourceXcframework`; frozen consumer targets use **will-be-produced** mode so soft wrapper fail does not leave zero native.

---

## 4. Hunt results (requested themes)

### 4.1 Stale wrapper fingerprints

| Hazard | Status | Notes |
|--------|--------|-------|
| `SwiftTargetArchitectures` missing from fingerprint | **refuted** | Both XCFramework + Apple echoes include pair; unit-tested adjacency |
| Fingerprint skip with partial `.xcframework` dir (parallel fan-in) | **mitigated** | Skip requires `HasWrapper=True` **and** Exists — not bare Exists (`Sdk.targets:2742–2758`) |
| Stamp written **before** successful generate | **candidate** | `_ComputeSwiftFingerprint` writes stamp when fingerprint **changes**, before `_GenerateSwiftBindings` runs (`:707–711` → `:1777`). Failed generate + unchanged inputs → `_SwiftBindingUpToDate=true` → skip regen on rebuild. Partial intermediates can linger. Mitigations: generate Exec fails build (no ContinueOnError); wrapper skip still needs HasWrapper; but confusing “clean” rebuilds possible without deleting stamp |
| Generator **binary** not in gen fingerprint | **candidate / already-known class** | Slice pack Inputs include `Swift.Bindings.dll`; gen fingerprint hashes `_SwiftBindingSdkVersion` only. NuGet consumers OK per package version; local/dev unstamped `0.0.0-dev` can reuse stale generated C# after generator edits (cousin of `EnsureGeneratorBuilt` stale-dll hazard) |
| ProjectReference-discovered module DBs unhashed | **already-known** | Comment in fingerprint (`:675–677`); incremental-only |
| `_CodeSignature` excluded from hash | **intentional + mitigated** | Slicer Inputs glob `CodeResources` so re-sign invalidates staged slice |

### 4.2 Missing NativeReference → DllNotFound

| Path | Status |
|------|--------|
| Will-be-produced under skip-wrapper | **correct** (`BindingsGeneratorCommand:1487–1500`; consumer Exists guards) |
| Fold failure nulling compilationResult | **refuted** — catch swallows; returns primary; comments document prior DllNotFound class (`Program.cs:996–1007`) |
| Promote housekeeping throw → HasWrapper=False | **refuted** — superseded delete is best-effort warn (`SwiftWrapperCompiler:1519–1537`) |
| Static drop + HasWrapper False | **fail-closed** SWIFTBIND040 (SDK-direct + pack + GetNativeManifest siblings) |
| Apple second-slice kill mid-commit | **mitigated** atomic `.superseded` + tests (`SdkPropsTargetsTests` second-slice) |

### 4.3 Arch option ignored on one path

**Refuted as current defect.** Shared `CompileWrapperForArchitectures`; both generate and compile-wrapper-only call `TryDecideWrapperArchitectures`; SDK injects `--target-architectures $(SwiftTargetArchitectures)` on generate + wrapper + bridge; fingerprints include the property twice.

Residual complexity (not ignore): Apple second-slice fat-sim vs generator active-slice natural archs (`GetAppleFrameworkSliceNaturalArchs`) — extra arches deferred to SDK merge, not generator lipo on device slice.

### 4.4 SwiftWrapperRequired default kill (G1-001 deepen)

**Confirmed; packaging-layer evidence:**

1. **Default:** `Sdk.props:68–69` — `SwiftWrapperRequired` defaults **true**. Comment anticipates “libraries with known internal type issues.”  
2. **Generator softens:** `HandleWrapperCompilationOutcome` SDK Fatal→`SWIFTBIND050` exit **0**, message *“C# bindings are still valid — wrapper-dependent methods will throw DllNotFoundException”* (`Program.cs:2257–2276`).  
3. **SDK re-hardens:** `_ValidateSwiftWrapperCompilation` Error when required ∧ HasWrapper≠True (`Sdk.targets:1978–1982`) — **SWIFTBIND051**. Soft flag only demotes to Warning (`:1984–1988`).  
4. **UX contradiction:** 050 claims “still valid”; default 051 then kills `dotnet build` / pack before consumer can try managed surface.  
5. **Cause visibility:** compile-wrapper-only failures are often null-code Warning (not 050) on stdout (low importance); `EchoWrapperFailurePreviewToStandardError` restores first-build diagnosis (`WrapperBuildOutcome:110–143`).  
6. **Soft-path packaging is actually thoughtful:** `NativePackagingPolicy` + source-include decision keep a native carrier when wrapper absent; Exists() guards prevent dangling refs. Soft mode is **not** “broken packaging” — it is **opt-in partial success** with honest DllNotFound on wrapper APIs.  
7. **What soft mode does *not* do:** hide/omit wrapper-dependent managed members; dual “managed-only” package id; analyzer on wrapper-required APIs; product scenario test asserting partial nupkg.

**G1 day-1 matrix (packaging row):** Pure Swift + wrapper fail → **High** under default props.

### 4.5 Primary-restore on fold failure

**Refuted as open bug** — implemented correctly:

- try fold / catch swallow / finally restore (`Program.cs:972–1050`)  
- Quarantine partial at canonical path before restore  
- Restore failure logs Error and **leaves `.primary`**  
- Unmerged extras reported; explicit → 056; auto → best-effort primary-only  

### 4.6 Apple vs third-party flow divergence

**Intentional multi-path**, well-commented. Risks are **complexity (L4/L5)** not unknown ignore:

- Inline vs deferred wrapper  
- Second-slice targets + bridge 052 degrade  
- Synthetic vs resolved auto arch basis  
- Apple consumer targets synthesis vs generator emit  

Integrity tests cover second-slice atomicity and fingerprint arch echoes.

### 4.7 Windows MAX_PATH

**already-known / gated** (`build/Build.WindowsPathGuard.cs`, issue #40):

- Authoritative ship gate on produced nupkgs (`AssertProducedNupkgsWindowsPathSafe`)  
- Early tripwire on Apple xcframework build  
- Budget models `C:\Users\<40>\.nuget\packages\<id>\<ver>\` + entry  

Third-party module names in consumer nupkgs inherit same layout risk; ship gate covers *this repo’s* pack output. Not an SDK MSBuild runtime path on macOS hosts. **No new defect** — keep as release integrity.

### 4.8 L3 packaging policy hooks for partial success

| Hook | Soft? | Role |
|------|-------|------|
| SWIFTBIND050 | Yes (SDK gen) | Wrapper give-up / fail; exit 0 |
| SWIFTBIND051 | **Default hard** | Required wrapper missing |
| SWIFTBIND052 | Yes (bridge / some arch msgs) | Bridge fail non-fatal; explicit arch missing can hard-fail at decide-time |
| SWIFTBIND056 | **Hard always** | Explicit arch contract unmet after fold |
| SWIFTBIND060/061 | Warn | Skip counts → report path |
| SWIFTBIND080 | Warn | Unresolved auto-deps |
| SWIFTBIND038/039/040 | Hard | Pack lies / mixed missing / static-drop without wrapper |
| SWIFTBIND062–065 | Hard | Hook disconnection |
| Co-gater + integrity 108 | Soft suppress / hard dangle | Member-level vs plan honesty |
| `SwiftWrapperRequired=false` | Opt-in soft package | Documented in 051 text |
| WrapperAbsentFallback | Soft heal | Static source when wrapper missing |
| Exists() on NativeReference | Soft omit ref | Avoid broken Include |

**Gap:** no first-class **“partial success package”** mode that (a) defaults soft for wrapper-only failure, (b) still hard-fails integrity, (c) surfaces report as primary artifact, (d) is gated by a product scenario test (G1-004).

### 4.9 L4 mega Sdk.targets / compiler

| Artifact | ~LOC | Hazard |
|----------|------|--------|
| `Sdk.targets` | ~3800 | Two modes × two-pass × pack × hooks × second-slice × mixed companion — high AI-edit risk |
| `SwiftWrapperCompiler.cs` | ~2990 | Compile/strip/link/promote |
| `Program.cs` arch/compile helpers | large slice of ~2520 | Shared helpers already extracted (good) |
| `NativePackagingPolicy` | small | **Good L4** — single formulas for dual shapes |

**Simplification opportunities** (capability-preserving):

| ID | Shape | Risk class |
|----|-------|------------|
| M2-S1 | Split Sdk.targets into `Sdk.Wrapper.targets` / `Sdk.Pack.targets` / `Sdk.Apple.targets` imports | Behavior-preserving if target order preserved |
| M2-S2 | Keep arch fold only in Program shared methods (already done) — document call graph in one diagram | Docs |
| M2-S3 | Single “HasWrapper decision” doc table (this report §3.3) into constraints or design doc | Docs |
| M2-S4 | Fingerprint echo as generated/shared string fragment (two hand-maintained echoes) | Needs fixture |

---

## 5. Findings

### DA-W7-M2-001: Default `SwiftWrapperRequired=true` package-kills after generator soft-fail (G1-001 deepen)

- **Severity**: P1 (day-1 packaging)  
- **Status**: confirmed / degrade-opportunity  
- **Confidence**: high  
- **Lenses**: L3  
- **Reachability**: integrity-gate / emission-live  
- **Claim**: SDK-mode generator converts wrapper Fatal→SWIFTBIND050 exit 0 (“C# still valid”), then default SDK validation Error SWIFTBIND051 blocks the entire binding project/package. Soft path (`SwiftWrapperRequired=false`) is complete enough for native carrier honesty (source fallback + Exists) but is opt-out.  
- **Evidence**: `Sdk.props:68–69`; `Program.cs:2257–2276`; `Sdk.targets:1978–1988`; `NativePackagingPolicy.cs:22–91`.  
- **Probe**: Binding with forced swiftc wrapper fail + default props → 051 Error; same + `false` → Warning 051 + build continues.  
- **Packaging policy recommendation**: see §7.  
- **Prior art**: G1-001; graceful-degradation-map contested row; M0-C consumer pain.

### DA-W7-M2-002: Fingerprint stamp committed before successful generate

- **Severity**: P2  
- **Status**: candidate  
- **Confidence**: medium  
- **Lenses**: L3, L5  
- **Reachability**: fixture-reachable  
- **Claim**: `_ComputeSwiftFingerprint` overwrites `swift-binding.stamp` when the hash changes *before* generate Exec. A failed generate leaves the new stamp; next build marks UpToDate and skips generate, risking stale/partial intermediates until clean.  
- **Evidence**: `Sdk.targets:707–711`, `:1724–1781`.  
- **Probe**: Force generate failure after fingerprint change; rebuild without clean; assert generate re-runs (expected fail if fixed: should re-run).  
- **Suggested fix shape**: write stamp only after successful generate+metadata, or delete stamp on generate failure; or gate UpToDate on Exists(metadata)∧success marker.  
- **Prior art**: second-slice tests already fear “stamp pre-swap + missing tree” class (`SdkPropsTargetsTests:542–549`).

### DA-W7-M2-003: Generation fingerprint omits generator binary hash

- **Severity**: P2 (dev/local) / P3 (NuGet consumers with versioned SDK)  
- **Status**: candidate  
- **Confidence**: high on mechanism; medium on production impact  
- **Lenses**: L5, L2  
- **Reachability**: latent for released SDK; emission-live for local 0.0.0-dev  
- **Claim**: Gen fingerprint includes `_SwiftBindingSdkVersion` not `Swift.Bindings.dll` content; slice pack *does* list the dll as Input. Local unstamped SDK can skip regen after generator-only fixes.  
- **Evidence**: `Sdk.targets:657`, `:690` vs `:3621`.  
- **Prior art**: constraints.md `EnsureGeneratorBuilt` stale-dll; M0-C §4.2.

### DA-W7-M2-004: Will-be-produced NativeReference + dual HasWrapper — correct, hazard if re-coupled

- **Severity**: P0 if regressed; currently **clean**  
- **Status**: refuted (as open bug) / hazard (maintain)  
- **Confidence**: high  
- **Lenses**: L1, L5  
- **Claim**: Consumer targets correctly use will-be-produced; metadata uses disk; SWIFTBIND040 bridges disk/meta disagreement. Re-coupling metadata generate emit to will-be-produced *without* Pass-B update, or gating consumer targets on exists-now under skip-wrapper, reopens DllNotFound for all PackageReference consumers.  
- **Evidence**: `BindingsGeneratorCommand:1487–1500`; `ConsumerTargetsEmitter:112–118`, `:136–137`; `XCFrameworkMetadataExtractor:448–481`; `Sdk.targets:3220–3232`, `:3778–3787`.  
- **Prior art**: constraints.md consumer-targets will-be-produced.

### DA-W7-M2-005: Primary-restore / arch-fold swallow — correct integrity

- **Severity**: n/a  
- **Status**: refuted (as defect)  
- **Confidence**: high  
- **Lenses**: L1, L3  
- **Claim**: Extra-arch fold failure degrades to primary-only, restores primary, returns non-null result so HasWrapper stays True; explicit arch list still fails 056. Do not rethrow from fold catch.  
- **Evidence**: `Program.cs:946–1061`; `WrapperBuildOutcome.cs:52–81`; `Sdk.targets:1990–1999`.

### DA-W7-M2-006: Shared arch decision paths — no ignored path today

- **Severity**: n/a  
- **Status**: refuted  
- **Confidence**: high  
- **Lenses**: L4, L5  
- **Claim**: Standalone generate and compile-wrapper-only both use `TryDecideWrapperArchitectures` + `CompileWrapperForArchitectures`.  
- **Evidence**: `BindingsGeneratorCommand:967–1070`; `Program.cs:1142–1311`, `:1446–1586`.

### DA-W7-M2-007: Soft partial package lacks product mode + gate

- **Severity**: P2  
- **Status**: degrade-opportunity  
- **Confidence**: high  
- **Lenses**: L2, L3  
- **Claim**: Mechanisms for partial package exist (050, optional 051, source fallback, Exists, co-gater) but no documented product “managed+source-only” mode, no BindingTests scenario asserting nupkg content under forced wrapper fail + soft required, no managed-surface marking of wrapper-dependent APIs.  
- **Evidence**: G1-004; absence of product scenario; soft props only.  
- **Prior art**: G1 ranked opportunity #1.

### DA-W7-M2-008: Mega Sdk.targets / SwiftWrapperCompiler complexity

- **Severity**: P2 (maintainability)  
- **Status**: simplification  
- **Confidence**: high  
- **Lenses**: L4, L5  
- **Claim**: ~3.8k-line Sdk.targets + ~3k-line SwiftWrapperCompiler concentrate packaging policy; accidental edits can re-break dual HasWrapper or fingerprint.  
- **Suggested simplification**: split targets by concern; freeze dual-signal table as constraints entry (from §3.3).  
- **Prior art**: M0-C B-S* mega notes.

### DA-W7-M2-009: Windows MAX_PATH packing

- **Severity**: P1 if regressed  
- **Status**: already-known / gated  
- **Confidence**: high  
- **Lenses**: L1, L2  
- **Claim**: Long xcframework entry paths break Windows restore; pack + early Apple tripwire enforce budget.  
- **Evidence**: `Build.WindowsPathGuard.cs`; PackGate call sites.  
- **Prior art**: issue #40.

### DA-W7-M2-010: SWIFTBIND052 dual numbering (bridge soft vs arch hard)

- **Severity**: P3  
- **Status**: candidate (UX/docs)  
- **Confidence**: high  
- **Lenses**: L5  
- **Claim**: Code “SWIFTBIND052” is used for bridge compile soft-fail *and* explicit missing-arch decide-time errors (`Program.cs:844` vs `BridgeBuildOutcome` / Sdk bridge). Different severities under one code confuse consumers and telemetry.  
- **Suggested**: split codes or document matrix in wiki; not a correctness bug.

---

## 6. What already works well (packaging integrity)

1. **Two-pass** dependency-aware wrapper compile with locked multi-TFM fan-in.  
2. **Will-be-produced** consumer targets + Exists() (constraints trap closed).  
3. **CompileWrapperForArchitectures** shared + primary-restore swallow (DllNotFound class closed).  
4. **SWIFTBIND056** explicit arch contract independent of WrapperRequired.  
5. **SWIFTBIND040** static-drop ⇔ wrapper-packed invariant (pack + runtime + GetNativeManifest).  
6. **NativePackagingPolicy** single formulas for disk vs frozen-consumer shapes.  
7. **Parallel fan-in** skip requires HasWrapper=True not bare Exists.  
8. **Atomic second-slice / promote** with `.superseded` rollback.  
9. **Hook wiring** 062–065 fail-closed.  
10. **Co-gater** after strip + SWIFTBIND108 integrity.  
11. **Fingerprint** includes `SwiftTargetArchitectures` on both modes (tested).  
12. **Windows MAX_PATH** ship gate.  
13. **stderr preview** for swallowed wrapper failures.  
14. **Pack empty-content** SWIFTBIND038.

---

## 7. Packaging policy recommendations (tied to G1)

These are **owner-gated product choices**, not implement-now.

### 7.1 Integrity must stay hard (do not soften)

| Keep hard | Why |
|-----------|-----|
| SWIFTBIND108 plan↔emit | Ships EntryPointNotFound |
| SWIFTBIND056 explicit arch | Contracted fat slice |
| SWIFTBIND038/039/040 pack invariants | Native-less / Mixed-less nupkg lies |
| SWIFTBIND062–065 hooks | Silent no-gen |
| False HasWrapper metadata | Drops NativeReference |
| TN2435 / MAX_PATH / pack slice completeness | Ship honesty |
| Mixed systemic ObjC abort (unless G1-002 opt-in) | Metadata honesty |

### 7.2 Usability degrade options for wrapper failure (G1-001)

| Option | Default change? | Pros | Cons |
|--------|-----------------|------|------|
| **A. Status quo** | none | No DllNotFound surprise on wrapper APIs | Day-1 total death on hard libs |
| **B. Soft default** `SwiftWrapperRequired=false` | **yes** | Matches generator 050 story; drop-and-try | Wrapper APIs DllNotFound unless loud UX |
| **C. Soft only when Review/strip budget high** | heuristic | Adaptive | Unpredictable |
| **D. Dual package** managed-only vs full | heavy | Clean product shape | Pack/CI cost |
| **E. Soft + mark** wrapper-dependent members Obsolete/EditorBrowsable + analyzer | recommended with B | Honest API surface | Needs emission tagging of UsesWrapperLibrary |

**Recommendation for synthesis (aligns G1 rank 1):**

1. **Short term (docs + opt-in ritual):** Wiki “partial success” recipe: set `SwiftWrapperRequired=false`, read `binding-report.json` / 050/051, expect DllNotFound on constructors/async/wrapper P/Invokes; static archives still ship via source fallback.  
2. **Medium term (product):** Prefer **E+B** or **E with soft default only for preview packages** — do **not** soft-fail 056/040/108.  
3. **Gate (G1-004 ∩ M2):** Fixture: lib with intentional uncompilable wrapper block → exit 0 under soft required → C# compiles → nupkg has source or wrapper per policy → SWIFTBIND051 is Warning not Error → ReviewItems ⊆ budget.  
4. **Never:** claim HasWrapper=True when disk missing; drop static source without wrapper; rethrow fold failure after primary restore.

### 7.3 Fingerprint / stale (M2-002/003)

| Action | Priority |
|--------|----------|
| Stamp only after successful generate (or success marker file) | P2 |
| Hash generator dll (or content stamp) in both fingerprint echoes for local-dev honesty | P2/P3 |
| Keep dual-echo test for SwiftTargetArchitectures | already |

### 7.4 L4 without capability loss

Split Sdk.targets by concern only with PackGate + binding-tests compile-only + mixed-pack as ratchet; no behavior change in dual HasWrapper semantics.

---

## 8. Counts

| Category | Count |
|----------|-------|
| Findings total | **10** (M2-001…010) |
| Confirmed defects / degrade-ops | **1** (M2-001) + deepen of G1-001 |
| Candidates | **3** (M2-002, M2-003, M2-010) |
| Refuted open bugs | **3** (M2-004 current, M2-005, M2-006) |
| Already-known / gated | **1** (M2-009) |
| Simplification | **1** (M2-008) + product gap M2-007 |
| Degrade-opportunity packaging | **2** (M2-001, M2-007) |
| SWIFTBIND codes in packaging path sampled | 050, 051, 052, 056, 038–040, 060–065, 080, 108 |
| Shared arch call sites | generate + compile-wrapper-only + bridge (+ Apple second-slice) |
| Risk | **3 / 5** |

---

## 9. Ledger status (M2)

| Path | Suggested ledger |
|------|------------------|
| `Program.cs` (arch/fold/outcome) | reviewed-deep / hazard (load-bearing comments) |
| `BindingsGeneratorCommand.cs` packaging | reviewed-deep |
| `SwiftWrapperCompiler.cs` | reviewed-deep / hazard (mega) |
| `StrippedSymbolCSharpReconciler.cs` | reviewed-deep |
| `WrapperBuildOutcome.cs` | reviewed-deep |
| `NativePackagingPolicy.cs` | reviewed-deep |
| `ConsumerTargetsEmitter.cs` | reviewed-deep |
| `XCFrameworkMetadataExtractor.cs` (props) | reviewed-deep |
| `Sdk.props` / `Sdk.targets` | reviewed-deep / hazard (mega) |
| `Build.WindowsPathGuard.cs` | reviewed (inventory + gate) |

Full ledger batch update deferred to program ledger pass.

---

## 10. Headline

**Packaging integrity is battle-hardened; packaging *policy* still defaults to total death on wrapper fail.**  
Will-be-produced NativeReference, primary-restore fold, SWIFTBIND040, and explicit-arch 056 closed the historic DllNotFound / false-metadata class. The remaining day-1 cliff is product-shaped: generator SWIFTBIND050 softens, default `SwiftWrapperRequired` re-kills (G1-001 / M2-001). Soft partial success is already *mechanically* supportable — it is not the default, not scenario-gated, and not surfaced as a first-class “usable package with holes” story.

**Risk: 3/5** — same band as G1 day-1 overall; packaging does not raise risk above G1, nor does it lower it until wrapper-required policy or product partial mode lands.
