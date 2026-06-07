# Grok Phase 2 — Remaining Hardening Candidates (Post-Remediation Verification)

**Date:** 2026-06-06  
**Mode:** Strictly read-only audit. No code changes, no builds, no `nuke` execution, no simulator/device runs. All validation via `read_file`, `grep`, `list_dir`, and parallel subagents performing targeted source + test + artifact inspection.  
**Scope:** Re-extraction + adversarial re-validation of the surviving deferred candidates (from all 14 Track reports) + REMEDIATION-PLAN.md §6 "OPEN" / discovered-out-of-scope items that were **not** resolved, promoted, or absorbed during the 10 sessions + explicit follow-up cleanup (per AUDIT-RELEASE-NOTES.md and prior synthesis).  
**Goal:** Provide a clean, decision-grade view of what is *actually* still latent after the ~104 confirmed fixes. Focus on items with real user impact for "users running real bindings" (third-party libs via `nuke validate`, Apple frameworks, common patterns: protocols/extensions/DIMs, closures, async, existentials, generics/PAT/CSM, SwiftUI views, optionals, structs, internal detection). Prioritize crashes (SIGSEGV/SIGABRT/UAF/double-free), wrong ABI (silent garbage/mis-marshaling), or build/runtime failures (DllNotFound, CS0111, stripped symbols) that can affect packed/consumer scenarios or real Apple/third-party surfaces.  
**Methodology (adversarial, no false positives):** 
- Started from full deferred lists in Track-*.md §3 + §6 OPEN bullets in REMEDIATION-PLAN.md.
- Cross-referenced against prior grok-audit.md synthesis, subagent source reads (parser/classification/packaging/existential/C2/M1/M4 clusters), direct reads of current source (cited files under `src/Swift.Bindings/src/`, `src/Swift.Bindings.Sdk/`, BindingTests, generated `output/`, `apple-frameworks.json` + *Database.xml, unit tests, manifests).
- Checked for post-remediation signals: new guards, state machines, "will be produced" comments, WasEmitted counts, suppression comments in generated output, explicit "§6"/"audit P1-"/"P1-21" markers, test updates (LifetimeTracker, SiblingMethodDispatch, GenericClosureBridgeTests, etc.), and whether the exact poison shape (e.g., colliding identifier, short prefix, raw brace count on string, static slice default) is still verbatim in code.
- Status labels: **Resolved/hardened in 10 + follow-up** (not remaining), **Still latent (reachable on real shapes)**, **Latent-gated/low-reach** (gated by current emission paths or rare input), **Docs-only / taxonomy** (erodes gate trust but not direct shipped defect).
- False-positive filter: If a subagent or direct read showed a new gate, skip, suppression, or structural change that makes the old claim no longer match current emission, it is not listed as "still latent." Many original §6 items (GenericClosureBridge self-register + class return, intra-protocol async/sync slots, frozen sub-word Optionals, throwing closure + by-value struct arg, UCO promotions, core dedup key propertyNames threading, etc.) were cleaned and are *not* repeated here.
- Yield/impact filter: High = can cause process crash, silent corruption, or DllNotFound/wrong-ABI on reachable patterns in common real bindings or Apple frameworks. Medium = build/runtime friction or silent drops for third-party consumers. Low = edge or maintainability-only.

**Key context (from prior work + docs):** The raw ~280+ deferred + §6 pool has shrunk via the plan's "same-shape absorption" + grep-sweeps + owner-map promotions + explicit post-campaign follow-up (AUDIT-RELEASE-NOTES explicitly lists several §6 items as cleaned). Original audit recall was ~40-60% per run; "not found ≠ not present." No full live `nuke binding-tests` / consumer round-trips were part of the original verification for most deferreds. L1 (docs-drift) and L2/L3 tracks were never run. Surviving items closely track the explicit deferred lists in the Tracks; none appear to have been silently dropped.

---

## 1. Packaging / Wrapper / SDK / Bridge / Arch / Co-gater (M2 + related M1/M4 deferreds + §6)

These have the highest direct user impact for "real bindings" consumers (third-party SwiftUI-bridged packages, Apple-framework bindings with Views, packed NuGets, x64-sim/Rosetta/Catalyst consumers). Can produce DllNotFound (missing/wrong-arch bridge slice or dangling P/Invoke after strip), pack hard-errors, or build failures in consumer targets.

**High**
- **Apple-framework SwiftUI-bridge second-slice / lipo / staging (distinct MSBuild path, unaudited for atomicity/interrupt)** — §6 OPEN (surfaced during S9), Track-M2 deferred #1/related, grok-audit high-yield note.  
  Current source (`Sdk.targets` `_CompileAppleFrameworkSecondBridgeSlice` + `_AFB_*` staging dirs/properties, lipo + hand-rolled plist write + xcodebuild merge + post-merge incomplete check that can drop bridge + flip HasBridge flags). Separate from generator-side `WrapperXCFrameworkMerger` (now transactional with `.merge-staging` + plist-before-commit + atomic rename) and `_CompileSwiftUIBridge` arch threading (S9 P1-23).  
  **Status:** Still latent (matches logged description verbatim; no equivalent transactional hardening/recovery for the `_AFB_*` path).  
  **Impact:** High. Apple-framework (or Apple-style) bindings that emit SwiftUI bridges (public `View`) on fat sim sources can hit DllNotFound for the opposite slice (device or non-primary-sim) at NativeReference / pack / consumer `dotnet build -r` after partial failure or interrupt. Bridge is intentionally non-fatal (SWIFTBIND052), but a half-written bridge is worse than shipping none. Not exercised by X64SimGate Apple legs (no SwiftUI bridge in those fixtures).  
  **Verification rec:** BindingTests fixture or X64SimGate extension with Apple-framework + SwiftUI View on fat sim source; assert both wrapper *and* bridge slices are fat (or consistent) after normal + injected-failure paths. Re-audit the `_AFB_*` path for atomic commit.

- **Co-gater brace-walker fragility + limited scanners (beyond the fixed P0 DllImport case)** — Track-M2 deferred #9/15/18/19 + C1 overlap (raw `FindBlockEnd` / `BuildLineToTypeMap` / `FindEnclosingClassStart` count only `{`/`}` with no string/char/`//`/`/* */`/escape state; FindPartial bounded to +5 lines + requires `;` on one line; type detection limited to ` class ` / ` struct ` (misses record/enum/namespace/interface)).  
  **Status:** P0 (DllImport-shaped dangling) **resolved/hardened** (regex now covers `DllImport` + `static extern`; comments call out the 4 emitters that produce the shape; `IsWrapperLibraryImportLine` + partial finder updated). Brace P2s **still latent** (verbatim match to deferred descriptions; no state machine added post-S9).  
  **Impact:** Medium-High. Can cause missed/over-broad co-gating on complex generated C# (strings/comments containing braces inside emitted members) → dangling `[DllImport(EntryPoint=...)]` or `static extern` after strip (KeyPath/AppEntity/enum-metadata + internal-type wrappers) → runtime EntryPointNotFound/DllNotFound for any lib using those emitters. Compounds with limited FindPartial (multi-line sigs or long attribute preambles evade).  
  **Verification rec:** Unit + co-gater test driving a stripped symbol from a KeyPath/AppEntity-style emitter with poison strings/comments in the generated body; assert the P/Invoke is correctly removed. Add adversarial generated-C# fixture.

- **Remaining bridge/arch/fingerprint/consumer-targets P2s on non-standard sim slices / explicit pins / complex deps** (e.g., ResolveAutoArchBasis blanket `catch {}`, explicit `--target-architectures x86_64,arm64` primary selection without arm-pinning, `_AFW_OtherIsFatSim` string-Contains logic for single x86_64 pin, Apple-direct bridge reuses mutated extraArchs + no generator/SDK handshake, bridge `-F` dep asymmetry, ConsumerTargetsEmitter.SanitizeModuleName only handles . - space, SliceVariant.WithArchitecture substring fragility on arm64e, InvokeSwiftCompiler giant arg string / ARG_MAX risk).  
  **Status:** Core "bridge second-class" + arch threading + emission parity + "will be produced" signaling **addressed** in S9 + follow-up (arch now threaded for bridge, fingerprints include architectures, direct vs. SDK emission parity closed, transactional merger, generalized resync not gated to AppleFramework only). These specific P2s **still latent** (verbatim in current source + deferred lists).  
  **Impact:** Medium-High for real consumers. Can cause wrong-arch bridge slices (DllNotFound on iossimulator-x64/Rosetta for SwiftUI-bridged third-party or Apple-framework-with-View bindings), pack hard-errors on renamed/ x86_64-only sim slices (SWIFTBIND031), or consumer MSBuild errors (invalid target names). Bounded for common arm64+fat cases but exactly the M2 risk area.  
  **Verification rec:** Per Track-M2 §6 recs (third-party SwiftUI View on fat sim via SDK + `dotnet build -r iossimulator-x64`; standalone direct Apple bridge pack; x86_64-only-sim SWIFTBIND031 trigger; co-gater DllImport + internal type). Include explicit single-pin + complex dep graphs.

**Medium**
- Bridge has no post-processing / symbol co-gating / stripping (unlike wrapper) — a broken `@_cdecl` in `.SwiftUIBridge.swift` fails the whole bridge (all-or-nothing); co-gater only targets `...SwiftBindings`.
- ConsumerTargets / BindingProject macOS exclusion + Exists() guards for bridge (intentional but can surprise).
- Various low-confidence P2s (e.g., SliceVariant arm64e substring, giant arg string, primary-restore edge cases after extra-arch strip).

**Notes:** X64SimGate + pack artifacts show fat wrapper coverage + x64 consumers, but Apple legs historically lacked SwiftUI bridge (coverage gap noted in grok-audit). No evidence of regression on the fixed surface.

---

## 2. TypeDatabase / Apple Classification / Projection Parity (M3 + related)

Silent drops or wrong marshalling on real Apple framework surfaces (validate libs + any consumer of CoreNFC, CoreMedia, Metal, SceneKit, Social, AVFoundation enums, NaturalLanguage, ManagedSettings, simd, Foundation typedefs, etc.). Can cause member loss or ARC/UAF for NSString-typedefs / ObjC-bridged values.

**High**
- **Short / over-broad / missing prefixes in global union + per-module gaps (Metal "MT" vs real "MTL", SceneKit "SC" vs "SCN", Social "SL", others)** + cross-module leakage for prefix-less optionalFallback modules.  
  **Status:** Some core fixes in S8 (CoreNFC "NFC", CM/CT/CS/WC/PDF backfills, valueTypes + typeRemaps for _LocationEssentials.CLLocationCoordinate2D, rawValueType="Int" for certain NSInteger enums). **Still latent** for the specific deferred items (verbatim in `apple-frameworks.json` + `AppleFrameworkRegistry.cs:430` longest-first global union + `HasObjCClassPrefix`; per-module `objcPrefixes` still incomplete for some; global pollutes).  
  **Impact:** High for real Apple frameworks. `Optional<NFCTag>` / `CM*?` / `MT*` / `SCN*` etc. degrade to opaque or drop; wrong prefix can misclassify value structs as ObjC-bridged (or vice-versa).  
  **Verification rec:** Unit `[InlineData]` theories on `HasObjCClassPrefix` / `IsObjCBridgedTypeName` / `IsOptionalFallbackModule` for the exact short/missing cases + end-to-end generation + BindingTests round-trips for affected Optionals + NSString-typedefs under autorelease drain.

- **Enum kind/rawValueType inconsistencies + value-type vs. ObjC-bridged misclass (some AVFoundation enums as "struct", NLTokenUnit as struct, Foundation.Data nativeType contradiction, more NS_TYPED_ENUM typedefs).**  
  **Status:** Some hardened (sibling consistency fixes). **Still latent** for the listed deferreds (verbatim in XMLs + registry paths).  
  **Impact:** Medium-High. 4-vs-8-byte width truncation on returns, or blittable pass-through (no ARC) for what should be class-bridged (UAF after pool drain).  
  **Verification rec:** Reflection + generation + runtime round-trip (value preservation + retain count) for the exact enums/typedefs.

- **Collection element fallback narrowness (only Foundation+UIKit) vs. Optional fallback (62 modules) + related gate parities (IsOptionalObjCBridged vs. factory vs. wrapper gates missing branches for ObjCRooted / ContainsGenericParameters / IsPointerType / IsStdlibContainer).**  
  **Status:** **Still latent** (deferred #7/10 + related; no full backfill).  
  **Impact:** Medium. Silent member drops for Optional<T> / collection elements from optionalFallback-only modules (CM/CT/CS/PDF/WC + others).  
  **Verification rec:** Generation + report checks for affected types; BindingTests fixture for a collection/Optional from a non-Foundation/UIKit optionalFallback module.

**Medium**
- Self generic-param resolution raw-name fallback (CS0246 risk).
- `GetRefAliasVariant` unconditional "Ref" append (can mis-resolve unrelated records).
- `protocolConformances` / `SuperclassTypeName` parser drops on `<` (over-suppresses members; ancestor walk breaks).
- `ManagedSettings` / `simd` autoBridge with incomplete valueTypes / no objcPrefixes (misclass for non-value types).
- Bare-Any `Kind=Existential` vs. `AnyType` `Kind=Protocol` + `Flags` divergence (downstream kind-switches).
- Foundation.Data mangled-name malformation (shadowed but exposed if absent from supplement).

**Notes:** S8 brought the no-XML registry path into better parity with XMLs for exercised cases; reflection + validate caught many. Unit coverage for exact classifier decisions remains thin. No runtime ARC/retain-count fixtures for the typedef cases in the inspected artifacts.

---

## 3. Parser / Demangler / Internal Detection / GenericSignature (A8 + C1 overlap)

Affects public/internal classification (wrong wrappers against internal symbols), decl drops, wrong error types, mangling-based signals (async/variadic), and scope (public/internal + actor-isolated extraction). Compile-invisible until a real lib hits the poison shape.

**High**
- **Negative-space / bare-protocol / modifier gaps (public protocol requirements now handled in some paths but consuming/borrowing/nonisolated/override ordering / extension/foreign still narrow in Broad*/Bare*/Internal*Regexes and `line.Contains("mutating func ")` sites; IsModuleInternal false positives on public overloads sharing printed names).**  
  **Status:** Public protocol reqs + operators **resolved/hardened** (inProtocol tracking + Bare*Regexes + explicit operator capture in S8). **Still latent** for consuming/borrowing + leading nonisolated + extension/foreign paths + Internal*Regex narrowness (verbatim in SwiftInterfaceAccessParser.cs + SwiftABIParser negative-space application).  
  **Impact:** High for real libs (CocoaLumberjackSwift-style @inlinable internal mutating + public overload; consuming noncopyable public methods). Wrapper denial → raw CallConvSwift [Obsolete] degradation (ABI risk).  
  **Verification rec:** Unit theories + `--interface-facts-producer regex` + auto end-to-end with real consuming public + bare protocol req + nonisolated public shapes; assert cdecl wrapper emitted + !IsModuleInternal.

- **Brace / scope / paren / continuation duplication + comment/string blindness (CountBraces / HasUnmatchedOpenParen + ~23 duplicated typeStack/braceDepth loops in GetInternalMembers/GetPublic/GetTypedThrows/etc.; still ignore //, /* */, """, \(, raw strings; type push gated on same-line openBraces; GetInternalMembers continuation tracking diverges from siblings; 3 conformance angle-depth scanners mis-decrement on ->).**  
  **Status:** Unbalanced paren in string default **resolved** (full inString state added). Duplication + comment/string gaps + continuation/angle divergence **still latent** (verbatim + C1 "largest unverified surface"; no shared abstraction or full state machine).  
  **Impact:** High. Single mis-count desyncs entire file's public/internal classification + scope stack → wrong IsModuleInternal, dropped members, or actor-isolated / enum / subscript extraction errors. Affects any complex .swiftinterface (multiline where, strings with parens/braces, comments).  
  **Verification rec:** Adversarial unit + parser test feeding poison fragments (// {, /* } */, """ ( """, \( " ) ", multi-line where + public member after, continuation brace, angle on ->); assert correct publicMemberNames + no scope desync. Grep-sweep all CountBraces/HasUnmatched + typeStack sites.

- **Demangler Y* + fallback heuristics + "DefaultIndicies" typo + DetectAsyncFromMangledName "Ya" substring (only a/b/K handled; others PushBack + DemangleIdentifier; "Ya" false-positive on mid-identifier or Yb/YK names; SI substitution name mismatch).**  
  **Status:** Yb (@Sendable)/YK (typed-throws) **resolved/hardened** (explicit nodes + Pop order in S8). **Still latent** for other Y*/differentiability/impl flags, "DefaultIndicies", "Ya" heuristic risk where demangler is weakest.  
  **Impact:** Medium-High. @Sendable closure params (pervasive in modern concurrency) lose FunctionReduction + variadic/async signals; typed-throws error type loss; wrong IsAsync for some ctors.  
  **Verification rec:** Demangler unit roundtrips for remaining Y* + variadic + Sendable + typed-throws; BindingTests shape with @Sendable closure param + typed-throws + variadic.

**Medium**
- GenericSignatureParser inline-constraint (no "where") + same-type to dependent member (τ_0_0 == τ_0_1.Element) + value-generics / InlineArray / Swift 6 mangling support.
- AnyObject / keyword constraint hardening is good but inline forms + protocol nodes skipping ParseGenericSignature may hide cases.

**Notes:** S8 + unit tests (GenericSignatureParserTests) strengthened the common paths. No BindingTests fixture exercising the exact poison inputs for negative-space or demangler fallbacks on real .swiftinterface shapes.

---

## 4. Key / Dedup / Override / WasEmitted Invariants (C2 + C1 overlap)

Can produce CS0111 (duplicate members), CS0535 (proxy doesn't implement declared member), or silent wrong override slot binding (especially same-module + suffix/rename + inheritance + property collision + async).

**High**
- **Protocol interface/DIM vs. proxy key divergence (propertyNames, isSelfReturning, parentTypeName, async CancellationToken handling, NormalizeParamTypeForOverloadIdentity trim narrowness); subscript key duplication in receiver (EveryProtocolEmitter GetSubscript* vs. ProtocolProxyEmitter.Receivers inline reimplementation); ProtocolExtensionEmitter manual key (hand-rolled, bypasses normalizations + label escaping differences).**  
  **Status:** Core property-rename axis for main builders (IHandler + DefaultParameterOverloadEmitter + ProtocolSignatureHelper) **resolved/hardened** (P1-21 + threading in lockstep + tombstone pre-pass + adopted override reservation; "Align emitted C# names..." commit). **Still latent** for the listed interface-vs-proxy, subscript dupe, extension manual, cross-pool (e.g., ConformanceValidator dual-compute workarounds), ancestor raw SwiftTypeSpec.ToString() compares, GetPublicMethodName consistency on all axes, subscript WasEmitted gap (no flag / HasSubscriptInResolvedAncestors), EveryProtocol non-requirement property/subscript fallback + position-dependent global dedup.  
  **Impact:** High for protocol-heavy real bindings (GRDB/Kingfisher-style: protocols + extensions + DIMs + inheritance + subscripts + same-name siblings + property collisions + generics). CS0111 at consumer build or (worse) silent mis-dispatch at runtime.  
  **Verification rec:** BindingTests fixtures for property-collision dedup (var conflict + func conflict + conflictMethod), completion-handler async vs. native async on colliding property, same-module override of collision-suffixed base, intra-protocol subscript + non-requirement property, extension with self-returning / parent-name collision. Unit guard for WasEmitted count + every emitter that contributes to override chain sets the flag on the live ClassDecl.Methods/.Properties/.Subscripts.

**Medium**
- Remaining GetProjectedOverloadKey tombstone/try-catch/trim `Get` prefix flip / ancestor override / cross-pool items (some backstopped but still divergent constructions).
- EveryProtocol skip-ladder asymmetry (HasNoncopyableMember present in emission but not fully in prescan) + walker defaults (ContainsClosureType etc. default:false).

**Notes:** P1-21 + S7 work + tests (Collisions/, Protocols/, SiblingMethodDispatch, IntraProtocolEffectOverload) addressed the headline. No exhaustive new fixtures locking all the listed axes.

---

## 5. SwiftUI Bridge (M1 + C1 identifier hazards + M4 overlap)

Compile breaks or runtime traps/leaks/crashes for any real SwiftUI-bridged binding (public `View` conformance, async views, BoundEnum state/updaters/modifiers/arrays/optionals, ObjC-bridged structs like URL/Data in inits/closures, user-chosen labels colliding with internals).

**High**
- **Async `_Create` appends fixed trailing reserved params (onReady/onError/onResult/userData + Ptr/Len variants) after user flattened params — no de-dup across C# P/Invoke / Swift @_cdecl / public CreateAsync factory.** Sync `Create` factory still declares hardcoded `handle`/`session`/`closureHandles`/`h`. No EscapeSwiftKeyword / SanitizeForCSharp / @-verbatim or reserved collision guard anywhere in the SwiftUI family (0 hits).  
  **Status:** Some P0s (ObjC-bridgeable struct type confusion, multi-site BoundEnum force-unwrap) **partially mitigated** (IsObjCBridgeable branches + failable `guard let ... else { return nil }` + C# InvalidOperationException surfacing added in harden commit). Identifier append/collision + sync hardcoded + full parity (typed-closure ObjC asymmetry, Result/frozen-ref leaks on some paths, UCO on some trampolines) **still latent**.  
  **Impact:** High. User param named `userData` / `onError` / `handle` / `session` etc. (common in SwiftUI/UIKit patterns) → duplicate decl (CS0100 / Swift redeclaration) or CS0136/CS0841. Affects any bridged View with colliding init/closure labels.  
  **Verification rec:** SwiftUI View (async + sync) whose init/closure has a reserved label + ObjC-bridged struct + BoundEnum out-of-range + frozen-with-ref closure arg; assert compile + runtime round-trip + no leak/trap.

- **Remaining typed-closure / Result / frozen-ref / async Bound* ObjC + ownership asymmetries + UCO trampolines lacking full try/catch.**  
  **Status:** Partial hardening; specific asymmetries **still latent** per M1 deferred + subagent.  
  **Impact:** Medium-High. UAF (passUnretained over fresh bridge temp), buffer + ARC leak (no defer dealloc for ClassWithBufferStruct), wrong refcount or type confusion (BoundType+IsObjC vs. BoundStruct), crash on managed throw from trampoline.  
  **Verification rec:** Fixtures exercising the exact shapes (ResultClosure with ObjC-bridged struct, typed closure with ObjC class arg, frozen-ref closure arg, async view with Bound* leaf).

**Medium**
- Collector dedups by unqualified Name (cross-module hazard).
- SwiftUIViewDetector only direct conformance (transitive + non-literal module names missed).
- Some async inference / chain leaf keying / OnReadyTrampoline guards / non-determinism remain.

**Notes:** Existing SwiftUIBridge tests + AuditSession5 provide partial coverage. No full new fixtures for all M1 recs visible.

---

## 6. BindingTests Gate / Skip Taxonomy / Coverage / Docs Drift (M4 + L1 + rules)

Erodes trust in the end-to-end gate; misattributes our bugs to "upstream"; overstates runtime coverage; confuses contributors.

**High (for process/trust, even if not direct shipped crash)**
- **Residual stale/misattributed [Skip] reasons (Issue-1 on non-CallConvSwift paths or our own bugs, platform gates leaking on CoreCLR, variadic / cross-host / non-frozen failable candidates, some GCB skips still pre-existing different-shape).** macOS/Catalyst gates still map to --platform simulator / TestPlatform.Simulator (skips [SkipOnSimulator] on CoreCLR where the cited limitation cannot fire).  
  **Status:** Major P0 (AsyncGenericContainer) **addressed** (suppressed at source + meta-invariant in S10). Failable coverage improved. **Still present** for the listed residuals (visible in generated manifests/registry + Build.RuntimeTests.cs + comments; S10 purged many false ones but not the full inventory).  
  **Impact:** High for gate reliability. Hides real ABI bugs or reduces device/sim/Catalyst coverage on generics+async, failable inits, protocol generics. Misleads on "will this run on Mono?" for consumers.  
  **Verification rec:** Meta-test invariant (any skip citing "Issue 1" must have ≥1 CallConvSwift P/Invoke); remove over-broad [SkipOnSimulator] on pure-cdecl paths; add TestPlatform value for macOS/Catalyst so leak-fallback skips can gate on "dylib-absent" vs. sim proxy. Full [SkipOnSimulator] inventory vs. individual generated P/Invoke CC.

- **Coverage matrix / runtime blindness + docs/scripts drift (coverage-matrix.json still unproduced and derives "passing" from Swift source + generator skips, never reads RuntimeTestsApp/*.cs for live assertions; README + bindingtests.md + rules still document [MonoJitCrash] (0 live source usages), old non-nuke scripts (build-and-test.sh, run-runtime-tests.sh), incomplete classification table (omits real SkipOn* attrs), "only 4" upstream (Issue 4 exists + [SkipOnCatalystX64]), coverage-matrix as output).**  
  **Status:** **Still present** (no nuke target produces the matrix; docs lag; bindingtests.md undercounts upstream). S10 + prior added SBTD001 (async void) + some hardening.  
  **Impact:** Medium-High. Overstates runtime coverage for features with only vacuous [Skip] tests. Docs drift hurts contributors and erodes "the gate is trustworthy."  
  **Verification rec:** Delete [MonoJitCrash] mentions everywhere (replace with specific [Skip]); add nuke target or explicit note that coverage-matrix is aspirational (or implement runtime-aware version per M4 rec); inventory all current [SkipOnSimulator] reasons; update upstream count + Issue 4 references; make bindingtests.md / README / rules match current nuke + attribute reality.

**Medium**
- TestDiscoveryGenerator: class-level [SkipOnCatalystX64] read but per-audit may have propagation limits; [Slow] cosmetic; GetAttributeReason simple class-name match only; async void now diagnosed (SBTD001) but IsAsyncMethod still only name-checks Task/ValueTask.
- Some cross-host / nested-of-parent / leak-fallback / wrapper-stripping candidates remain in manifests with thin bodies or imprecise reasons.

**Notes:** S10 meta-test + suppression + diagnostic + purge of many false Issue-1 skips was load-bearing. The gate is better, but the taxonomy + docs authority still lag.

---

## 7. Existential / Ownership / ARC / Lifetime / Closure / Async Residuals (A3/A5/A4/A7 + §6)

**High (if reachable) / mostly Low-reach per validation (fresh subagent verification 2026-06-06)**
- Residual carrier-conversion fall-throughs for owned nested existentials / compositions (ExistentialProjection.GetArrayElementCarrierConversion has exactly two mint/donate branches; EC1 null-proxy + EC2+ fall to non-owning alias).  
  **Status (current source + comments):** Main recursive owned-return (Array/Dict/Set now recurse `GetOwnedReturnElementConversion`; top-level balanced by S4 probes + LifetimeTracker + `CreateOwned*Carrier` + `ownsContainer: true`) **addressed/hardened**. Residuals still possible for certain deep-nested owned existentials/compositions inside collections (or mixed with Optional/async). Fall-through to non-owning path (or bare `GetParameterElementConversion`) leaks the payload +1 (or risks UAF/double-destroy). Source comments explicitly label "pre-existing deferred wire-carrier gap" (ExistentialProjection.cs:197) and "audit P1-08 opaque sibling". Compositions (EC2+) use separate emission (no single-proxy `ownsContainer` ctor in some paths) + ObjC filtering size guards. Class-bound vs. opaque paths gated but have special cases.  
  **Impact for real bindings:** Silent leak (most common; payload +1 orphaned until process exit) or (rarer) UAF/double-destroy crash if a non-owning path is taken on an owned carrier or vice-versa. Not a direct crash on every call.  
  **Verification rec:** LifetimeTracker + `AssertNoLeaks` / `AssertLiveCount` probes forcing the alias tail (deep `[[any P]]` / composition-in-collection owned returns); inspect generated output for `CreateOwned*` vs. bare paths.

- Lingering proxy finalizer, box leak, borrowed-handle, or class retain issues (not fully covered by P1-01/P1-02).  
  **Status (current source + comments):** Core class retain (P1-01: `Arc.Retain` → `Arc.UnknownObjectRetain` + "audit P1-01" comments), borrowed SafeHandle (P1-02: `!ownsContainer` + SuppressFinalize + `NewFromPayload` helpers), opaque proxy finalizer contract (P0-10: `ownsContainer` gating + `SBW_VWTDestroy` trampoline + try/catch + `SwiftExitGuard`), box +1 leaks (P1-03), collection element owned returns (P1-07/08) **largely addressed** in S2/S4 + follow-up.  
  Specific residual: Composition owned-return proxies (EC2+) on the finalizer path (no explicit Dispose) use **direct `DestroyWireBufferRetains`** (not the `...FinalizerSafe` variant used by single-EC1 opaque + class-bound paths). Borrowed handles well-gated. C#-impl box leaks closed by `ProxyLifetimeTracker` (CWT + atomic `Released` race + deinit callback + exit guard).  
  **Impact:** Potential Mono finalizer crash (`!ji->async` after CallConvSwift contamination) for owned EC2+ composition existential return + GC proxy collection (rare path; swallowed but still executes bad P/Invoke). Leaks prevented for covered paths.  
  **Verification rec:** `LifetimeTracker` + `GC.WaitForPendingFinalizers` + device probes on owned composition existential returns dropped without Dispose; assert single deinit + no crash.

- ClosureProjection dead branch or MCB/NCB exception handling (if still relevant).  
  **Status:** No obvious dead branches in current `ClosureProjection.cs` (escaping GCHandle "leak" is intentional for storage beyond P/Invoke e.g. EventHandler; "the caller's finally block handles it"; non-escaping PassThrough; castable-PInvoke fallbacks). Centralized FailFast in `ClosureEmitter.FailFastCatch` (non-throwing UCO `catch { FailFastUnhandledClosureException; throw; }` to prevent SIGABRT unwind or fabricated return on uninit indirect buffer). MCB/NCB are test-specific shapes.  
  **Impact:** GCHandle "leak" intentional for escaping (stored callbacks); error paths have cleanup. Wrong result or crash if exception escapes non-throwing path without FailFast.  
  **Coverage:** Strong in `BindingTests/RuntimeTestsApp/Closures/*` + `Async/*Closure*` + leak probes + `LifetimeTracker`.

- Async non-throwing hang risks or specific GCHandle/buffer leaks on error paths.  
  **Status:** Async existential returns explicitly adopt +1 (`ownsContainer: true` or equivalent; class-bound uses `ReadHeapCell`). Error paths have explicit finally blocks (e.g. `DestroyWireBufferRetains` + `SBW_Free` before plain dealloc). GCHandle primarily in (escaping) closures (by-design for storage); async uses dedicated state/continuations. Non-throwing async harness paths exist (no error channel).  
  **Impact:** Buffer leak on error path if Destroy skipped before finally (or exception in marshal before cleanup); hang if non-throwing async continuation never fired. Rare in practice due to harness structure.  
  **Coverage:** Extensive `RuntimeTestsApp/Async/*` (throwing, existential array, stream ownership, complex types, closure spikes) + `Lifetime/AsyncClosureContextLifetimeTests.cs` + Tracker in some.

**Overall for A3/A5/A4/A7 + §6 residuals (fresh subagent 2026-06-06):** Substantial hardening since original audits (ownsContainer everywhere for owned returns, `CreateOwned*` carriers for collection element balance, FinalizerSafe + trampolines + exit guards + atomic races for proxies/tracker, recursive owned threading in projections, FailFast/UCO/finally for closures/async errors, explicit "audit P1-0x" comments + gaps called out in source). Specific residuals remain possible/untested per the code's own comments ("deferred wire-carrier gap", composition finalizer using non-FinalizerSafe Destroy vs. EC1 path): primarily silent leaks for unexercised deep nested owned existentials in collections, or (Mono-specific) finalizer crash for owned composition proxies. Real bindings hitting these would see leaks/crashes rather than silent wrong values in most cases. Coverage via LifetimeTracker + dedicated fixtures is solid for exercised paths and generated output matches the hardened paths; not exhaustive for every §6 nesting/composition+collection+async+error combo. Per zero-regression policy and BindingTests as the authoritative gate (per Claude.md).

**Notes:** GCB (the standout "least-verified" in STATE §7) is now round-tripping with explicit comments + tests (post-follow-up). Many A3/A5 P0/P1s were the core of the 104. No violations of key constraints (projection parity via `IProjectionVisitor`, mixed-composition `filteredCount == originalCount` guards, `ownsContainer` + `Destroy*FinalizerSafe` + `SwiftExitGuard`, closure two-layer gate, etc.). All per direct reads of current state.

---

## 8. Cross-Cutting Maintainability / Identifier Emission (C1 + parser-marshaler + emitter)

Dominant repeated hazard across the original audit: user Swift names projected verbatim + hardcoded synthetic locals/params/trailers with no collision guard.

**High (plausible trigger, compile or silent value corruption)**
- Remaining unguarded identifier emission in hot emitters (EveryProtocol conformance body locals + {name}Copy aliasing; parser brace/scope trackers; any closure/CSM/Wrapper paths not fully covered by S1 P1-22 helper + S6 user-param escape; ModuleEmissionContext regex still broad mid-path).  
  **Status:** Core dedup-related shaping + S6 whole-category user-param collision + P1-22 applications (MethodClosureBridge, Nested, ProtocolExtensionClosureBridge, SwiftUI, CSM/AsyncGenericParent, MGB/AMGBE/GCB, etc.) + structural elimination of some async-cleanup synthetics **largely mitigated**. Broader unguarded sites + dupe walkers (EveryProtocol skip 3x, brace 23x) + regex mid-path corruption **still latent** per C1 deferred + subagent.  
  **Impact:** High for any lib with "normal" Swift param names colliding with synthetics (`handle`/`result`/`session`/`self_`/`tcs`/`userData`/`onReady` etc. in methods/closures/views/protocols/generics/SwiftUI). CS0136/CS0100/CS0103 or silent aliasing (value corruption).  
  **Verification rec:** BindingTests shapes with colliding labels across the remaining families + unit guard that reserved synthetic names are escaped.

**Medium**
- Duplicated skip-ladders / emission paths / walker defaults (EveryProtocol, brace counting, TypeSpec walkers) — maintainability multiplier + regression surface.
- ModuleEmissionContext Default singleton accumulation (latent for multi-module-in-one-process infra symbols).

---

## 9. Prioritized Recommendations for Hardening Investment

**Tier 1 (highest blast radius for real bindings + crashes/wrong-ABI; tackle first)**
1. Apple-framework SwiftUI-bridge second-slice atomicity (M2 §6 + deferred).
2. Co-gater brace-walker state machine + widened scanners (M2 + C1).
3. Remaining short/missing prefixes + enum kind/rawValueType + collection fallback classification drifts (M3).
4. Parser brace/scope/paren duplication + full comment/string state + negative-space modifier completeness (A8/C1).
5. Protocol key/subscript/extension manual key / cross-pool / WasEmitted-for-subscripts parity (C2).
6. SwiftUI async reserved-name de-dup + complete ObjC/typed-closure/Result/frozen/ UCO parity + identifier guard (M1 + C1).
7. Full skip taxonomy cleanup + runtime-aware coverage matrix + docs/rules alignment (M4 + L1).

**Tier 2 (important but lower daily reach or already partially mitigated)**
- Remaining M2 arch/fingerprint/consumer-target P2s on edge sim/pin cases.
- Demangler remaining Y*/fallback + "Ya" heuristic + SI substitution (A8).
- Existential residual carriers / box/GCHandle fallbacks / async non-throwing hang (A3/A5/A4/A7).
- GenericSignatureParser inline/same-type-dependent / value-generics support (A8).
- EveryProtocol conformance body locals + skip-ladder + walker defaults (C1).

**Tier 3 (maintainability / low-reach / docs)**
- Duplicated ladders/walkers across the codebase.
- L2 ObjC interop pipeline (never run).
- L3 perf / API-drift readiness.
- Full L1 docs-drift sweep (beyond M4/C2 surface).

**Process recommendations (per original plan + capstone):**
- Re-measure first (grep-sweep the current tree for any same-shape siblings of the above).
- Verify before fixing (run surviving leads back through the `.claude/workflows/codebase-audit.js` harness or equivalent finder+adversarial-verify; majority vote; default to inconclusive).
- Pick by yield; stop when it drops. Add the missing BindingTests fixtures the Tracks already recommended (they are the durable gates).
- Zero-regression: `nuke test` + `nuke binding-tests` (sim + device where calling-convention/ARC/marshalling changed) + pass counts ≥ baseline.
- Update this doc + the original grok-audit.md with verdicts as verification lands.
- Owner sign-off before any Phase 2 capped fix plan.

**Provenance:** Synthesized from the 14 Track deferred lists + REMEDIATION-PLAN §6 + subagent validations (parser/classification/packaging/existential/C2/M1/M4) + direct current-source reads/greps (cited files + generated artifacts + tests + manifests) + prior grok-audit synthesis. All claims trace to specific file:line or audit section. "Still latent" means the mechanism + lack of contradicting gate matches the logged description in current code. Original audit caveats (recall, no full runtime repros for most, "not found ≠ not present") apply.

*This is the post-10-sessions + follow-up snapshot as of 2026-06-06. The pool is smaller and more focused than the raw 200+; many headline §6 items were the right "wrapping up" work. These are the survivors worth re-verifying before any further investment.*

---

*No existing audit docs were modified. This is a new, self-contained companion to `grok-audit.md`.*