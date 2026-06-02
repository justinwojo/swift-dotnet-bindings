# Remediation Gameplan — Fixing the Audit Backlog

> The execution plan for fixing the ~104 confirmed defects in `STATE-OF-THE-CODEBASE.md`. Ten sessions, sequential, each grouped by **fix-shape** so related bugs are knocked out together. Claude drives; Grok pairs on plan/design, Codex pairs on code review; every fix is fixture-first TDD.
>
> Read `STATE-OF-THE-CODEBASE.md` for the per-bug mechanism and `file:line`. This doc is the *queue and the rules* — it references backlog IDs (P0-NN / P1-NN) rather than re-deriving them.

---

## 0. Phase model — Phase 2 is a FUTURE discussion, not in scope here

> **For a future session to raise with the owner — do not start Phase 2 work off this note.**

This plan is **Phase 1 only**: the ten sessions that fix the **~104 *confirmed*** defects (the ones that passed the audit's adversarial verify gate).

Beyond the 104 sits a **~280 deferred-candidate pool** plus three **un-run tracks** (L1 docs-drift, L2 ObjC interop, L3 perf) and the audit's own ~40–60% per-run recall ceiling. **These are unverified leads, not confirmed bugs** — direct evidence (the refuted tier, e.g. the `nonmutating`/`indirect enum` false alarms in Track A8) shows a real fraction would wash out as unreachable, inert, or duplicate on inspection. See `STATE-OF-THE-CODEBASE.md` §6.

**If we choose to pursue the tail, it is a separate Phase 2 with its own discipline — never an extension of these ten:**
1. **Re-measure first.** Phase 1's grep-sweeps (the "fix the *pattern*, not the instance" rule) will already have absorbed every *same-shape* deferred sibling, so the surviving pool is materially smaller than 280. Count what's actually left before deciding anything.
2. **Verify before fixing — no patch-on-suspicion.** Run the surviving leads back through the audit harness (`.claude/workflows/codebase-audit.js`, per `RESUME.md`) so the verify gate sorts confirmed / inconclusive / refuted. Fixing a deferred candidate without re-verification is the line we don't cross — it's what makes the 104 trustworthy and the 280 not.
3. **Pick by yield, stop when it drops.** Highest-expected-yield pools (capstone §7): A4 GenericClosureBridge, A7 `WrapperEmitter.Async` live/dead boundary, C1 parser brace/scope, M3 classification family.
4. **The survivors become their own capped fix-plan**, gated exactly like this one (§1).

**This is a decision made ONCE, after Phase 1 lands and the tail is re-measured — not a mid-stream cascade.** Until then, the ~280 are neither ignored nor open season: they are logged, unverified, and out of scope.

---

## 1. Scope discipline (read this before every session)

**This is a hard contract, not aspirational. It exists because plans like this reliably balloon to 20+ sessions through deferral and cascade.**

1. **Ten sessions is the ceiling.** Not a starting estimate. The number does not grow. It compresses to ~8 or splits to ~12 *only* along the levers in §2 — and a split (10a/10b) still counts as **one** of the ten.

2. **Same-shape discovery is absorbed, never deferred.** The sessions are grouped by fix-shape precisely so that finding more sites of the same bug is *expected*. If Session 2 finds 12 unguarded UCO callbacks instead of 8, you fix all 12 in Session 2. Finding more is the grouping succeeding — it is never a reason to spawn a follow-on session, file a roadmap item, or close "partial with deferral." (See memory `feedback_no_session_cascade.md`, `feedback_no_autonomous_defer.md`.)

3. **The ~280 deferred candidates are OUT of scope.** This plan fixes the **confirmed ~104 only**. We do not chase the deferred pool here. Pulling deferred candidates into a session is the #1 way this plan hits 20 — don't.

4. **New confirmed bugs found mid-session route by file/shape, not by spawning:**
   - Same file *and* same fix-shape as the session's scope → **absorb it** (fix it now).
   - Anything else → log one line in §6 (Discovered / Out-of-Scope). **Do not** spawn a session, do not fix it now, do not let it expand the current session's review surface. §6 gets triaged *after* the ten land, as a deliberate decision.

5. **Splitting is not a lever; compression is.** When a session feels too big, the default response is to *compress* — drop its P2s to §6, sequence its sub-steps inside the one session — **not** to split. A 10a/10b split is permitted only under all four of these conditions, together: (a) it divides the session's **existing** scope along a review-surface seam and adds **zero net new scope**; (b) **both halves land before the next session starts** — 10b is never deferred, never queued, never carried; (c) it has explicit **owner (user) sign-off** — never an engineer's or reviewer's self-judgment at plan-check; (d) a written compression attempt was made first and is recorded as having failed. "The diff is large" is not, by itself, sufficient — large is the *expected* shape of a meaty session. A split still counts as **one** of the ten. The plan pre-authorizes **no** splits; S3 and S4 in particular are explicitly *not* pre-cleared to divide.

6. **"Done" is not negotiable down.** A session closes only when: every backlog ID in its scope is fixed (not weakened, not skipped), fixtures are green, `nuke test` + `nuke binding-tests` pass count ≥ baseline, the codebase is grep-swept for *all* instances of the pattern fixed, and the Codex re-review loop is clean. No "will fix later" for items in scope. (Root-cause, not symptom — `CLAUDE.md` Working Guidelines.)

---

## 2. The ten sessions

Hard ordering is only **1 → {2,3,4} → 10**: prep first, capstone last (it verifies 2/3/4). Sessions 5–9 slot anywhere in between.

| # | Session | Size | P0s | Other backlog | Gate |
|---|---|---|---|---|---|
| 1 | Prep & de-risk | S | — | Cluster 7 live/dead; identifier-guard helper | `nuke test` + compile |
| 2 | UCO-escape hardening | L | P0-01, P0-02 | P1-12, P1-16, P1-17, P1-18, cdecl-invoke-thunk | `--device` |
| 3 | ABI core | L | P0-05/06/07/08 | P1-09/10/11/13, P2 enum+struct | `--device` |
| 4 | Existential + runtime struct safety | L | P0-09, P0-10 | P1-01..08, P1-14, P1-15 | `--device` |
| 5 | SwiftUI bridge | M–L | P0-03, P0-04, P0-13 | P1-19, P1-20, trampoline, dup-param guard | `--sim` |
| 6 | Generics / specialization | M | P0-11, P0-12 | P1-28, BoundGenerics P2 | `--device` |
| 7 | Name/key consistency | M | — | P1-21, identifier-guard rollout | `nuke validate` |
| 8 | Input fidelity | M | — | P1-26, P1-27, classifier theories | `nuke validate` |
| 9 | Packaging / SDK + co-gater | M | P0-14 | P1-23/24/25 | pack-gate + consumer round-trip |
| 10 | Gate-hygiene capstone | M | P0-15 | false-"Issue 1" purge, meta-test, P2 coverage | `--sim` + `--device` |

**Coverage check:** every P0-01..15 and P1-01..28 is placed; the §4-"most important" P2s ride their owning session. Nothing in the confirmed backlog is unassigned.

**Levers (compression only):** compress to ~9 by merging 8+9; to ~8 by also folding 1 into the front of 2. **There is no expansion lever.** A session that feels too big gets a compression plan first (drop its P2s to §6, sequence sub-steps within the one session); it splits only with explicit owner sign-off under §1.5's four conditions. The plan does **not** pre-authorize splitting S3 or S4 — the earlier framing of "split runtime-ARC vs emitter-proxy" was a loophole and is withdrawn (Grok H3). S4's runtime+emitter coupling is precisely why it stays whole.

---

### Session 1 — Prep & de-risk ✅ COMPLETE
- **Status:** Done — gates green (`nuke test` unit 12223/0, runtime 575/0; `nuke binding-tests --compile-only` regression gate passed — emitter changes, no compile regression), Codex+Grok paired review signed off. See §5 tracker + the §Session-1 live/dead map below.
- **Scope:** Resolve every live/dead emitter boundary the audit flagged (Cluster 7): async dead duplicate (the `EmitAsyncWrapper*` copy in `WrapperEmitter.Async.cs`) vs live `AsyncHarnessEmitter`; shadowed-but-wired `MethodGenericBridgeEmitter`; EveryProtocol skip-ladder where the `HasNoncopyableMember` gate sat at the emission ladder (`EmitProtocolConformance`) but was missing from the pre-scan ladder (`WillSkipConformance`). Delete the dead paths; add a structural test asserting exactly one live emitter per family. Build the shared **reserved-synthetic-name guard helper** (infra for the P1-22 identifier family rolled out across later sessions).
- **Why first:** §7 of the synthesis is explicit — resolve the live/dead boundary *before* patching, or Cluster 1/Generics fixes land in dead code. This is the one risk code-level confidence does not cover.
- **Done-when:** live/dead map written into this doc; dead emitters deleted; one-live-emitter tests green; guard helper merged with unit coverage.

#### Session 1 live/dead map (Cluster 7 — RESOLVED)

Three emitter-family boundaries resolved. Structural guard: `EmitterFamilyLivenessTests` (reflection over the generator assembly + `MethodHandler.BridgeEmitters`) pins each partition so a refactor can't silently re-split a family into a live half + a dead-shadow half.

**1. Async wrapper emission — two complementary halves, both LIVE, one per file (the duplication was a *cross-copy*, not a single dead emitter).**

| Half | Live owner | Entry / call site |
|---|---|---|
| C# async callback plumbing (TCS, GCHandle, `UnmanagedCallersOnly` callbacks) | `AsyncHarnessEmitter` | `EmitAsyncWrapper` (+ `EmitAsyncWrapperForTuple/String/ArrayString/ComplexType/Collection`); invoked at `WrapperEmitter.cs:527` |
| Swift `@_cdecl`/`@_silgen_name` async wrapper body | `WrapperEmitter` (`.Async` partial) | `EmitAsync` (`WrapperEmitter.Async.cs:39`) → `BuildSwiftAsyncWrapperCode` (`:1437`) / `BuildSwiftCatchBody` (`:1359`); invoked at `WrapperEmitter.cs:553` |

- **DEAD (deleted):** the cross-duplicates — the C# `EmitAsyncWrapper*` copy that lived in `WrapperEmitter.Async.cs` (−1187 lines) and the Swift `BuildSwiftAsyncWrapperCode`/`BuildSwiftCatchBody`/`BuildAvailabilityLines` copy that lived in `AsyncHarnessEmitter.cs` (−~230 lines). Each file now keeps exactly one half. `AsyncHarnessEmitter`'s class docstring was corrected — it no longer claims to emit Swift.
- **Note for S2:** P0-02's async-callback line refs (`AsyncHarnessEmitter.cs:410/497/539/912/1187/1289/1291`) are the **C# plumbing** half — that is the live UCO-escape surface. The Swift body half is `WrapperEmitter.Async`.

**2. `MethodGenericBridgeEmitter` (sync method-generic bridge) — CONDITIONALLY LIVE, KEPT (not dead, not a duplicate of the async sibling).**
Reachable for a sync, non-throwing method with exactly one method-own generic param constrained to a **class-bound** protocol (explicit `AnyObject` or transitive `TypeRecordFlags.ClassBound`), the generic param in direct parameter positions only, on a **non-generic** parent, in **XCFramework** mode. Wired once as `MethodGenericBridgeAdapter` (`MethodHandler.cs:761`), dispatched in the bridge loop (`:907`, first non-null short-circuits). CSM (`ConcreteProtocolSpecializationEmitter`) does **not** pre-consume this shape: its suppression (`MemberValidationPipeline.IsCsmSyncEligibleForGenericParent`) requires `parent.IsGeneric`, and CSM runs *after* the method pass and is additive for the non-generic-parent sync route. No end-to-end fixture exercises sync emission today — only direct `TryEmit`/`IsEligible` unit tests and the async sibling (`AsyncMethodGenericBridgeAdapter`, `_XMA`).
- **Consequence: P0-12 (fixed-256B buffer overflow + double-free) and P1-28 (frozen-ref variant) are real latent bugs on the live path, NOT moot.** This resolves the §Session-6 prerequisite (this doc, Session 6 "Prereq"). Session 6 owns the buffer/ownership fix **and** the first end-to-end BindingTests fixture for sync MGB.
- Doc-drift fixed: `IsEligible`'s docstring claimed a `MemberEmissionValidator` placeholder-gate consumer that does not exist (zero production call sites — only unit tests; sibling bridges *do* have that wiring). Corrected, with the missing placeholder-gate wiring logged as P1-28/S6, not S1.
- Concurrence: Codex + Grok independent reviews and direct code-reading all classified CONDITIONALLY LIVE.

**3. EveryProtocol skip-ladder triplication — SYNCED (single live emitter; the three gate sites now move in lockstep).**
`EveryProtocolEmitter` is the sole emitter. The `HasNoncopyableMember` skip gate — present at the emission ladder (`EmitProtocolConformance`) but **missing** from the pre-scan ladder (`WillSkipConformance`) — was added at the pre-scan site so Pass-1 `PreScanProtocols` seeds the noncopyable-member skip that Pass-2 propagates through `genericSig` constraints (`HasUnsatisfiedProtocolConstraintInGenericSig`). Both `HasNoncopyableMember(protocolDecl)` call sites now move in lockstep (referenced by method name, not a drift-prone line number). Covered by `PreScan_ChildBeforeNoncopyableParent_StillSkipsChild`. (Per §3, behavioral `EveryProtocolEmitter` *leak* fixes remain S4-owned; S1's edit here is the mechanical gate-sync only.)

**Infra also delivered:** the reserved-synthetic-name guard — `NameProvider.MakeNonCollidingSyntheticName` + `SyntheticNameScope` (escapes a synthetic emitter local with a `__` prefix when a projected user identifier collides — CS0136/CS0100). Built + 25 unit tests here (incl. the verbatim-`@` no-collision / empty-reserved edges — the result is never `@`-prefixed on any path — and rejection of a prefix-only `@` that strips to an empty identifier); **applied** at the per-site owners in later sessions (§3 P1-22 table). Not yet wired into any emitter — Session 1 builds the tool only.

### Session 2 — UCO-escape hardening (Cluster 1)
- **Scope:** P0-01 (closure callbacks `ClosureEmitter.cs:132`, `.Throwing.cs:86`, `.IndirectReturn.cs:83`, `.SwiftWrapper.cs:492`), P0-02 (async callbacks `AsyncHarnessEmitter.cs:410/497/539/912/1187/1289/1291`, `AsyncMethodGenericBridgeEmitter.cs:824/866`), P1-16 (nested-closure box leak), P1-17 (cancellation key recycle), P1-18 (async GCHandle leak), P1-12 (`@convention(c)` Bool param). Wire the dead cdecl invoke thunk (unblocks Session 10). **Apply the S1 identifier guard at `MethodClosureBridge.cs:385` + `NestedClosureBridge.cs:326`** (P1-22 sites in files this session already edits — in-scope by §3, not an expansion).
- **Fix-shapes (two, both closure-family):** (a) *UCO-escape hardening* — every UCO callback body wrapped try/catch → existing `SwiftError*`/`TrySetException` channel; check `errorPtr` *before* consuming any result pointer; zero/skip indirect buffers on throw; free buffers in catch. (b) *Closure param-direction bridging* — P1-12 + the cdecl-invoke-thunk wiring are a distinct return/param-path fix-shape, not UCO-escape (Grok M1); they ride this session by file-ownership (`WrapperEmitter.Marshalling` is closure code), and are called out separately so the grouping stays honest.
- **Prereq:** Session 1 live/dead async map.
- **Done-when:** fixture where the C# delegate throws (non-primitive / indirect / async success+error) asserts graceful fault, no SIGABRT, on `--device`.

### Session 3 — ABI core (A1/A2 codegen)
- **Scope:** P0-05 (throwing-ctor register swap), P0-06 (`consuming` double-free), P0-07 (eightbyte mis-count → silent garbage), P0-08 (x8 sret loss), P1-09 (null-layout small return), P1-10 (`Foundation.Data` decompose), P1-11 (PWT culture-order), **P1-13 first** (parser ownership — upstream enabler of P0-06). P2: enum-tag truncation, nested/packed struct alignment, `AlignmentMask`.
- **Fix-shape:** drive register placement from `CdeclSignatureContract` phase order; bucket fields into eightbytes; `IsIndirect`-aware x8 bridge; `StringComparer.Ordinal` for PWT.
- **Prereq:** P1-13 before P0-06 within the session.
- **Done-when:** `consuming` deinit-runs-exactly-once fixture; `{Int8×5,Int64,Int64}` by-value return fixture; both green on `--device`.

### Session 4 — Existential ownership + runtime struct safety
- **Scope:** P0-09 (opaque `any P` double-release), P0-10 (finalizer-thread direct VWT), P1-01 (`Arc.Retain` ObjC no-op, both twins), P1-02 (borrowed SafeHandle), P1-03 (existential box leak), P1-04 (optional-existential nil stub), P1-05 (value-return leak ×3), P1-06 (int/long box drift), P1-07 (collection-element leak), P1-08 (class-bound array stride), P1-14 (`SwiftHandle` sub-8B over-read), P1-15 (`Optional<T>` inline size).
- **Fix-shape:** the existential **+1/Destroy contract is one fix** spanning runtime (`ExistentialContainer`/`SwiftMarshal`) and emitters (`ProtocolProxyEmitter.*`) — balanced *conditionally* (the proxy path is already balanced; a blanket Destroy over-releases). That coupling is why this is one session, not two.
- **Done-when:** ObjC-backed class via Optional/Result/tuple asserts retain balance; owned opaque `any P` dropped without Dispose + `WaitForPendingFinalizers` asserts single deinit; `--device`.
- **⚠️ Coordination — external in-flight fix (do NOT duplicate).** Branch `fix/protocol-proxy-class-param-receiver` (customer issue [#40](https://github.com/justinwojo/swift-dotnet-bindings/issues/40), cut from `main`; plan at `src/docs/protocol-proxy-class-param-receiver-fix.md` *on that branch*) lands ahead of this session and overlaps it:
  - **P1-01 is being CLOSED there** (`SwiftMarshal.cs:466` + `:1466` → `Arc.UnknownObjectRetain`, both twins). When merged: **mark P1-01 done in §5 and verify it — do not re-implement.** Drops Session 4 to 11 owned items.
  - **The §7 #7 ObjC-NSObject retain-balance fixture is created there** (`ClassParamCallback.swift`, `ClassParamCallbackTests.cs`) plus a `ProtocolProxyEmitterTests` assertion. Build on them; don't re-author the reverse-callback variant.
  - **Start this session from post-merge `main`.** That branch also adds a *new, non-backlog* concrete Swift-class / `Optional<class>` receiver fix in **the files this session owns** (`ProtocolProxyEmitter.Receivers.cs`, `…SwiftObject.cs`) — see §6. Its new branch sits **adjacent to P1-04** (`Receivers.cs:931`, optional-existential nil stub — keep distinct) and shares `SwiftObject.cs` with **P0-10** (`:92`, finalizer — different concern).
  - **Still ours (the fix branch scopes these OUT):** P0-09, P0-10, P1-02, P1-04, P1-08. Be merge-aware — the files diverge, the findings don't.

### Session 5 — SwiftUI bridge
- **Scope:** P0-03 (`init(rawValue:)!` trap, 6 sites), P0-04 (ObjC-pointer-as-struct-bytes), P0-13 (async dup trailing param), P1-19 (Data→NSData UAF), P1-20 (frozen-with-ref closure leak), the Cluster 1 trampoline try/catch (`SwiftUIBridgeEmitter.cs:3468`), and the SwiftUI identifier-dup (apply Session 1 guard).
- **Fix-shape:** `guard let … else` graceful surface uniformly; `IsObjCBridgeable` branch (closure path already correct at `:3854-3873`); de-dup trailing synthetic params vs user params.
- **Note:** single 3,962-line file → all SwiftUI bugs land here regardless of cluster.

### Session 6 — Generics / specialization (A6)
- **Scope:** P0-11 (class-conformer carrier-wrap UAF), P0-12 (fixed-256B buffer overflow + double-free), P1-28 (frozen-ref variant), P2 (`BoundGenericsHandler` leaf-arg drop). Apply identifier guard at `ConcreteProtocolSpecializationEmitter.cs:1585`.
- **Fix-shape:** read class conformers via `*(IntPtr*)resultPtr`; size buffers via `GetSwiftTypeSize<T>()`; discriminate ownership before freeing. The CSM emitter is the correct reference — port its discrimination to the bridge.
- **Prereq:** Session 1 resolved whether `MethodGenericBridgeEmitter` is the live path.

### Session 7 — Name/key consistency (C2)
- **Scope:** P1-21 (emitted-name/dedup-key divergence: `IHandler.cs:528`, `IEnvironment.cs:149`, `WrapperEmitter.Signature.cs:570`). Own the P1-22 sites in files **no other session edits** (`ModuleEmissionContext.cs:82` + any orphans), then **verify** all P1-22 sites are covered across sessions (verify-only — never re-open a file S2/S4/S5/S6 already closed; see §3 table). Refresh the stale `constraints.md` WasEmitted inventory (doc claims 13/6; live ~37 mentions across ~20 files — Grok H1).
- **Fix-shape:** thread `propertyNames` + `EmittedCSharpName` into every key/verifier in lockstep.
- **Gate:** `nuke validate` — changes dedup keys → broad output blast. Run after emitter sessions settle so it rebases on their output.

### Session 8 — Input fidelity (M3 + A8)
- **Scope:** P1-26 (classification drift: objcPrefix, rawValueType, NSString-typedef-as-blittable, Date tuple, forced ObjC-class — `apple-frameworks.json`, `*Database.xml`, `AppleFrameworkRegistry.cs`), P1-27 (parser/demangler: typed-throws last-match, `@Sendable`/`YK`, `where …: AnyObject` decl-drop, paren-in-string EOF, protocol-requirement misclassification). Add the missing classifier `[InlineData]` theories (currently zero coverage).
- **Gate:** `nuke validate` — data-table changes touch many libs.

### Session 9 — Packaging / SDK + co-gater (M2)
- **Scope:** P0-14 (co-gater blind to `[DllImport]`+`static extern`), P1-23 (bridge xcframework arm64-only → DllNotFound on x64-sim/Rosetta), P1-24 (Guard 2a static slice-id), P1-25 (non-transactional fat-fold).
- **Fix-shape:** broaden co-gater regex + partial-decl finder; thread `targetArchitectures`/`--target-architectures` through the bridge compile exactly like the wrapper; atomic lipo-overwrite.
- **Caution:** `constraints.md` carries dense hard-won wrapper-arch invariants — high-trap session. Honor every one.
- **Gate:** pack-gate + `dotnet build -r` consumer round-trip on `iossimulator-x64`.

### Session 10 — Gate-hygiene capstone (M4)
- **Scope:** P0-15 (live wrong-ABI `AsyncGenericContainer` method behind a false-reason `[Skip]`), purge every "Issue 1"/"!ji->async" skip on a pure-Cdecl path (`ClosureEdgeCaseTests.cs:224/234`, `SwiftBindingsTestLib.cs:25006` + `:169929`, `Build.RuntimeTests.cs:1849`), un-skip the tests Sessions 2/3/4 un-gated, add the **meta-test invariant** (any such skip must have ≥1 CallConvSwift P/Invoke on its path), fix P2s (`coverage-report.py:1037` runtime-coverage gap, `TestDiscoveryGenerator.cs:149` async-void detached).
- **Why last:** it verifies the earlier fixes and restores the `feedback_mono_jit_blame.md` trust contract. Depends on 2/3/4.

---

## 3. Single-owner file map

A file has one owning session (sequential execution relaxes this, but keep it for review coherence — don't re-open a file gratuitously). Hot multi-cluster files resolved to one owner: `SwiftUIBridgeEmitter.*` → S5; `EveryProtocolEmitter.cs` → S1 (skip-ladder) + S4 (leaks); `ConcreteProtocolSpecializationEmitter.cs` → S6; `WrapperEmitter.Async.cs` → S2; the runtime struct files (`SwiftHandle`/`SwiftOptional`/`FrozenStructHandler`) → S4.

**S1's edits to later-owned files are bounded to mechanical de-risk only** (delete dead `WrapperEmitter.Async` duplicates; sync the `EveryProtocolEmitter` skip-ladder `HasNoncopyableMember` gate; build the guard helper). The *behavioral* fixes to those files stay with the owning session (S2 async UCO, S4 Every leaks). So S1↔S2/S4 touch the same files at non-overlapping intent — that bounds the review surface Grok flagged in H2.

### Identifier-guard (P1-22) — explicit per-site ownership (resolves Grok H1)

The P1-22 family does **not** localize to one file, so the helper is built once in S1 and **each site is owned by the session that already edits its file**. Applying the guard at a P1-22 site in a file a session is *already touching* is **in-scope by definition — not a scope expansion** (this is the one cross-cutting exception §1.2's "absorb same-shape" explicitly covers). S7 owns only the C2 key-divergence (P1-21) plus the orphan sites in files **no other session edits**, and then **verifies** total coverage — it never re-opens a file another session already closed.

| P1-22 site | File | Owning session |
|---|---|---|
| `MethodClosureBridge.cs:385` + `NestedClosureBridge.cs:326` synthetic names | closure-bridge | **S2** (already edits both for P0-01/P1-16) |
| `SwiftUIBridgeEmitter.cs:2776` | SwiftUI | **S5** |
| `ConcreteProtocolSpecializationEmitter.cs:1585` / `AsyncGenericParent.cs:895` | generics | **S6** |
| `EveryProtocolEmitter.cs` synthetic locals | existential | **S4** |
| `ModuleEmissionContext.cs:82` (+ any orphan sites) | — | **S7** (sole owner) |
| coverage verification across all sites | — | **S7** (verify only) |

Note: `constraints.md:18`'s WasEmitted inventory is stale (claims 13 points/6 files; live tree is ~37 mentions across ~20 files — Grok H1). S7 refreshes it as part of the rollout-verify.

---

## 4. Per-session workflow (the cadence)

1. **Plan check (Grok)** — sanity-check the session's scope and fix-shape before writing code. Confirm up front whether the session decomposes into >1 emission mechanism (the §1.5 split test).
2. **Fixture-first TDD** — write the BindingTests fixture, verify it goes **red** (this is the live reproduction the audit never did), fix, verify **green**. Maximum-case fixtures, not minimum-repro (`feedback_tdd_for_regression_fixes.md`).
3. **Grep-sweep** — before closing, grep the whole codebase for every other instance of the pattern fixed (`CLAUDE.md`: fix the bug *pattern*, not the instance).
4. **Code review (Codex)** — run the review → fix → re-review loop until clean.
5. **Gates** — `nuke test` + `nuke binding-tests` every session; `--device` where the session row says so (calling-convention/marshalling/ARC changes — Mono and NativeAOT have different bugs); `nuke validate` only for S7/S8/S9.
6. **Zero-regression** — pass counts ≥ baseline before the session closes. No exceptions for in-scope items.

---

## 5. Status tracker

| # | Session | Status | Fixed / Scope | Gates | Notes |
|---|---|---|---|---|---|
| 1 | Prep & de-risk | ✅ done | Cluster 7 live/dead map + guard helper | `nuke test` (12223 unit / 575 runtime, 0 fail) + `binding-tests --compile-only` (no regression) | Async cross-duplicates deleted; MGB CONDITIONALLY LIVE (kept) → P0-12/P1-28 real; EveryProtocol skip-ladder synced; `MakeNonCollidingSyntheticName`/`SyntheticNameScope` (verbatim-`@` contract hardened, empty-strip rejected) + `EmitterFamilyLivenessTests` added. Codex+Grok r1/r2 paired review clean (1 convergent code finding fixed: `@`-prefix leak). See §Session-1 live/dead map. |
| 2 | UCO-escape hardening | ⬜ not started | 0 / 6 | — | |
| 3 | ABI core | ⬜ not started | 0 / 8 | — | |
| 4 | Existential + runtime struct | ⬜ not started | 0 / 12 | — | P0-09/10 + P1-01..08 + P1-14/15. **P1-01 closing externally on `fix/protocol-proxy-class-param-receiver` (#40) — verify, don't redo; see session note + §6.** |
| 5 | SwiftUI bridge | ⬜ not started | 0 / 7 | — | |
| 6 | Generics / specialization | ⬜ not started | 0 / 4 | — | |
| 7 | Name/key consistency | ⬜ not started | 0 / 2 | — | |
| 8 | Input fidelity | ⬜ not started | 0 / 3 | — | |
| 9 | Packaging / SDK + co-gater | ⬜ not started | 0 / 4 | — | |
| 10 | Gate-hygiene capstone | ⬜ not started | 0 / 3 | — | |

---

## 6. Discovered / out-of-scope (logged, NOT queued)

> New confirmed bugs found mid-session that are **not** same-file-same-shape go here as one line each. This list is triaged **after** the ten sessions land — as a deliberate decision, never by spawning a session mid-stream. Keeping items here instead of in the queue is what holds the plan at ten.

- **[COVERED — handled externally, do not re-find/re-fix]** Protocol-proxy *reverse-callback* marshals a concrete **Swift-class** / `Optional<class>` receiver param with a naive `Unsafe.Read<T>` (per-proxy helper `ProtocolProxyEmitter.SwiftObject.cs:~279`; 4 receiver sites in `…Receivers.cs` — method `~1075`, property-setter `marshalExpr` `~247`, subscript getter `~654`, subscript setter `~731/~757`) → reinterprets a Swift heap-object pointer as a managed ref → **SIGSEGV** on first use. **Not in the audit backlog** (our `Receivers.cs` findings P1-04/P1-08 are existential, not concrete-class — a recall miss). Being fixed on branch `fix/protocol-proxy-class-param-receiver` (issue #40) alongside P1-01. Logged here so Session 4 doesn't re-discover it; it is **not** deferred and **not** a new session.
