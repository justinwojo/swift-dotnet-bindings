# Grok Audit — Remaining Audit Docs + Deferred / Lingering Candidates Review (Read-Only)

**Date:** 2026-06-06 (post-Sessions 1–10 + follow-up cleanup)  
**Mode:** Strictly read-only. No code changes, no builds, no execution of nuke targets or probes that would modify the tree. All inspection via `read_file`, `grep`, `list_dir`, and parallel subagent source reads of cited paths. Output is this single new document only.  
**Scope:** Review of every file under `src/docs/audits/` (except this one). Primary focus: confirm the ~100 verified / ~200+ unverified split, then validate the status of un-tackled deferred candidates and REMEDIATION-PLAN.md §6 "Discovered / Out-of-Scope (logged, NOT queued)" items against current source (post-remediation edits). Pure audit / synthesis task.  
**Inputs:** `REMEDIATION-PLAN.md`, `STATE-OF-THE-CODEBASE.md`, `AUDIT-RELEASE-NOTES.md`, `RESUME.md`, `README.md`, `SESSION-7-PREP.md`, `CLOSURE-REGRESSION-TEST-GAP.md`, all `Track-*.md` (A1–A8, C1–C2, M1–M4) + A1 run-reports, plus targeted reads/greps of the exact `file:line` sites named in those docs (generator + runtime + BindingTests + Sdk + tests). One completed parallel subagent provided exhaustive §6 OPEN triage + current-source status for the lingering items (A1–A3 subagent and GCB-focused subagent were still running at synthesis time; their partial progress was consistent with the completed triage).

---

## Verification status — Claude, 2026-06-07

A follow-up **code-trace verification pass** (read-only; not a compile/runtime repro for most) re-checked the 8 highest-impact "still latent" High clusters from `grok-phase2-remaining-hardening-candidates.md` against current source. **Most resolved as false-positive, already-mitigated, or latent-but-unreachable on real bindings** — Apple short-prefixes (MT/SC/SL), enum width-truncation, co-gater brace-walker, parser comment/string blindness, SwiftUI reserved-name collisions, SwiftUI ObjC-closure UAF/leak, and demangler Ya/Yb/YK. These should **not** be re-chased without new evidence.

**Three real, reachable survivors** remain (all SHOULD-FIX-SOON; none a launch-blocking process crash) — worth a fixture/compile-probe + fix before release:
1. **`ProtocolExtensionEmitter` hand-rolled overload key** → CS0111 consumer compile break on protocol-extension defaults with `Optional<class>` params (`ProtocolExtensionEmitter.cs:300-314`); Kingfisher `ImageTransformable` shape.
2. **`consuming` / `borrowing` missing from public-func regexes** → public noncopyable methods degrade to `[Obsolete]` SB0001 raw `CallConvSwift` (ABI risk); already degrading 6 methods in committed BindingTests output (`SwiftInterfaceAccessParser.cs:158`).
3. **Collection-element ObjC fallback (Foundation+UIKit only vs 62 Optional-fallback modules)** → silent member DROP for `Array<unregistered-ObjC-class>` from non-Foundation/UIKit modules (`TypeProjectionFactory.cs:584`).

Full per-item verdict table: `grok-phase2-remaining-hardening-candidates.md` §0. Per the "verify before fixing" rule, the 3 survivors should be reproduced with a fixture before any fix.

---

## 1. Confirmation of counts and structure

**User understanding is accurate and directly supported by the source docs.**

- **~104 confirmed / verified defects fixed.** These are the ones that passed the audit's adversarial compile-probe verification gate (static + `/tmp` swiftc/SIL/disassembly probes; no full `nuke binding-tests` in the original audit for most). See:
  - `STATE-OF-THE-CODEBASE.md` §1: "~104 confirmed across 14 tracks (~22 P0, ~70 P1)"; per-track breakdown (A1:13, A2:4, A3:7, A4:7, A5:9, A6:5, A7:10, A8:6, C1:7, C2:5, M1:5, M2:11/9, M3:7, M4:8); "floor, not ceiling" due to ~40–60% per-run recall.
  - `AUDIT-RELEASE-NOTES.md`: "~104 confirmed defects fixed across 10 focused sessions, plus follow-up cleanup of the items logged during the campaign."
  - `REMEDIATION-PLAN.md` §0: "Phase 1 only: the ten sessions that fix the ~104 *confirmed* defects"; "Beyond the 104 sits a ~280 deferred-candidate pool plus three un-run tracks (L1 docs-drift, L2 ObjC interop, L3 perf)".
  - `README.md` §6 report index and `RESUME.md` status table mirror the same ~104 / 14-track accounting (with "done" checkboxes for all Tier-1 + Tier-2 tracks).

- **~280+ (or "200+") possibly-bugs but unverified.** This is the explicit "deferred-candidate pool":
  - Aggregate from per-track §3 "Deferred (candidate, unverified)" sections (A1 union ~13 + singles; A2:21; A3:12; A4:23+; A5:7; A6:32+; A7:~20–36; A8:28–36; C1:~30–47 same-shape; C2:19+1 inconclusive; M1:22–26; M2:25; M3:25–33; M4:21–45). STATE §6 calls the total "≈280+" and notes the per-run recall ceiling.
  - Plus the three un-run Tier-3 tracks (L1–L3) mentioned in README/RESUME/STATE.
  - Plus the many mid-session discoveries logged in `REMEDIATION-PLAN.md` §6 (22+ distinct OPEN bullets at logging time, some multi-group). These were deliberately kept out of the 10-session queue per §1.4 ("Anything else → log one line in §6... Do not spawn a session").

- **Key plan invariants that governed the split (still relevant for any future work):**
  - "The ~280 deferred candidates are OUT of scope" for Phase 1 (§1.3). "Re-measure first" after grep-sweeps (same-shape absorbed during the 10). "Verify before fixing — no patch-on-suspicion" (run back through `.claude/workflows/codebase-audit.js` harness).
  - "Same-shape discovery is absorbed, never deferred" (§1.2) — this is why many deferred siblings were cleaned during the sessions without expanding the 10-session count.
  - "New confirmed bugs found mid-session route by file/shape" — not by spawning new sessions. §6 is the post-Phase-1 triage log.

- **"Roughly 100" vs "200+" is a fair paraphrase of the floor numbers in the docs.** The exact ~104 / ~280+ figures (plus the §6 OPENs) are the authoritative ones.

The remediation (10 sessions + explicit "follow-up cleanup" of items logged during the campaign, per RELEASE-NOTES) tackled the verified ~104. User notes that lingering §6 + elsewhere items are currently being wrapped up locally (Claude). This audit reviewed the docs and spot-validated the un-tackled surface.

---

## 2. Review of the remaining docs in `src/docs/audits/`

All files were read (full or targeted offsets for long ones) + grepped for deferred / OPEN / inconclusive / "not reached" / coverage-gap language. Structure is consistent across the 14 Track reports (produced by the original parallel-audit workflow):

- Confirmed findings (the ~104 total; these were the verified ones fed into REMEDIATION-PLAN §2/§5).
- Inconclusive (split votes; real defect in some cases but mechanism/reachability/severity disputed).
- Deferred (candidate, unverified) — the ~280 pool. Almost all are framed as "same family as confirmed" at additional sites, or pre-existing latent traps a future change would activate. Many carry explicit "P1/P2", reachability caveats, and recommended BindingTests fixture shapes.
- Coverage gaps / what the track did NOT reach (explicitly calls out the per-track probe cap, lack of full `nuke binding-tests` runtime repros for most findings, un-probed subsystems, Mono vs. NativeAOT divergence, etc.).
- Refuted lists (precision wins; false alarms killed by the verify stage).

**Key supporting docs:**

- `README.md` + `RESUME.md`: The original plan, three-planner cross-check (Claude/Codex/Grok), execution model (heavy finder → adversarial verify → one Track report per track), "how to continue", hard-won lessons (verify routing/read-only invariant after every run; single heavy ~40–60% recall; etc.), and the live status table (all 14 tracks + synthesis marked done; L1–L3 not run).
- `STATE-OF-THE-CODEBASE.md`: The capstone synthesis. Executive summary, cross-track risk heatmap (5/5 critical for async/closures/existentials/P/Invoke/ARC/wrapper; 4/5 for struct/SwiftUI/TypeDB/coverage), 7 root-cause clusters (UCO-escape is THE headline; emitted-name/dedup-key divergence; ObjC/ownership confusion; classification drift; gate misattribution; bridge second-class; duplicated emission paths), single deduped P0→P2 backlog (P0-01..15, P1-01..28, selected P2s), top-20 files-to-touch-with-care (with invariants), §6 "What the audit did NOT reach" (deferred ~280+, entire L tracks un-run, no live consumer round-trips, severity disputes, subsystem gaps like GenericClosureBridge / non-frozen struct / Swift5Demangler internals), and recommended next moves (fix Cluster 1 first; then governance pair; highest-value BindingTests fixtures; deferred pools most deserving a second audit run: A4 GCB, A7 Wrapper.Async boundary, C1 parser, M3 classification, dedicated L1 docs-drift).
- `AUDIT-RELEASE-NOTES.md`: Public-facing summary of the ~104 + 10 sessions + follow-up. Highlights the UCO hardening (exceptions no longer crash the process), ABI fixes, leaks, naming/dedup, packaging (x64-sim/Rosetta), gate-hygiene (purged false "upstream" skips + meta-test invariant), and the follow-up cleanup items (explicitly names Generic closure bridge round-tripping, intra-protocol async/sync overloads, throwing closures + by-value struct, frozen sub-word optionals).
- `CLOSURE-REGRESSION-TEST-GAP.md`: Post-hoc analysis of three coupled defects that leaked to `main`/validate before the campaign. Explains why each leaked (no BindingTests fixture for the exact shape; no emitter unit test pinning the invariant; only expensive/late gates caught them) and what now closes the gaps (fixtures + unit tests at the right layers + DRY consolidation). Systemic lesson: ABI/symbol invariants need coverage at *both* emitter-unit (every `nuke test`) and BindingTests (the durable runtime gate). Residual gaps noted (throwing-closure behavior remains device-only; `ClosureProjection` escaping branch is still unguarded dead code).
- `SESSION-7-PREP.md`: S7-specific (C2 name/key). Re-pins lines post-S2–S6 edits; settles scope (owns P1-21 + P1-22 orphan verify + constraints.md refresh + folded-in §6 synthetic-LOCAL category); explicitly keeps the 19 C2 deferred + 1 inconclusive OUT ("Phase-2 work"; "the #1 way the ten-session ceiling breaks"); records design decisions (sibling-property threading is optional-param, not 30-caller ripple; regression risk LOW–MEDIUM; etc.). Confirms that even during S7 prep the C2 deferred pool was treated as unverified and out-of-scope.
- Individual `Track-*.md` (A1 consolidated from 3 runs with reliability analysis; others single heavy runs): All have substantial Deferred sections. Recurring theme: "real candidates not probed due to the per-track cap"; "listed so none is silently dropped"; many "same shape as confirmed at additional sites"; recommended fixtures that would lock them. A1 has extra "Cross-run reliability summary" (only 3 findings overlapped 3 runs; each run's headline P0 was unique to that run) and a "Checked & refuted" section (precision of the verify stage). Inconclusive items often split on reachability or "real defect but currently gated/masked."

The docs are internally consistent, well cross-referenced, and honest about limitations (recall ceilings, no full runtime repros in the audit phase, "not found ≠ not present").

---

## 3. Status of the un-tackled / lingering surface (deferred + REMEDIATION-PLAN §6 OPENs)

**High-level:** The 10 sessions + follow-up cleanup (explicitly called out in RELEASE-NOTES as addressing "the items logged during the campaign") absorbed a large fraction of the same-shape deferred siblings via the plan's "fix the pattern" + grep-sweep rule and the owner-map + promotion mechanics for cross-emitter UCO/identifier items. Many §6 OPENs that were "pre-existing, different shape" or "one mechanism over" were either promoted into an owning session's committed scope without spawning new sessions, cleaned in the post-campaign follow-up (e.g., the GenericClosureBridge pair, intra-protocol async/sync slots, throwing closure + by-value struct, frozen sub-word optionals), or reclassified as latent / unreachable / false-positive-primary during the work.

A completed parallel subagent performed an exhaustive enumeration + current-source validation (via `read_file`/`grep` on the exact owning files) of every §6 bullet. Key outcomes (paraphrased / grouped from the subagent output; full detail in the subagent transcript):

**Promoted / resolved during the 10 sessions (per plan rules):**
- Unguarded UCO groups (CrossModuleExtension, ProtocolProxy.Receivers every receiver impl/`_del()`, SwiftUI trampolines beyond just :3468) — SLOTTED/PROMOTED into S4/S5 committed scope (same fix-shape as P0-01/02; guards + fixtures applied).
- Several identifier/synthetic families via S6 whole-category user-param collision fix + P1-22 rollout (S2/S4/S5/S6 sites) + S7 verify.
- EveryProtocol same-sig fan-out + Bug#1/#2 (harness strip cascade + emitter conformance cycle) + cross-protocol async key work (S7 + fixtures); plain-method case resolved, closure-shaped/async case left as intentional boundary (loud failure).
- Existential +1/Destroy contract + carriers + stride + value leaks (S4; top-level + recursion improvements).
- Co-gater DllImport blind spot (S9), input-fidelity classification drift (S8), packaging/arch (S9), gate-hygiene meta-test + stale skips purge (S10), etc.

**Explicitly cleaned in "follow-up cleanup (post-campaign)" (AUDIT-RELEASE-NOTES lists them by name; aligns with high-yield pools called out in STATE §6/§7):**
- GenericClosureBridge self-register ABI mismatch (every method-generic closure; C# `SwiftSelf self_` vs. free-function Swift `_ _self` trailing param; DatabaseReader.read*) + the paired generic class-typed return buffer handling (`MarshalFromSwift<T>(new IntPtr(resultBuf))` treating address as payload + finalizer `AlignedFree`). Now fixed (IntPtr `__self` + rationale comments; `MarshalMovedValueFromSlot` + `resultSlotLive` + comments; round-trips in `GenericClosureBridgeTests`; BindingTests coverage). Pre-existing, different shape from S2's cluster; "least-verified closure engine" per STATE.
- Intra-protocol async/sync overloads (single protocol `func m()` + `func m() async` collapsed to one vtable slot because intra keys omitted `async` effect). Now distinct dispatch slots (includeAsyncEffect threaded; guards; "§6 #12" tests in ProtocolSignatureHelperTests / EveryProtocolEmitterTests / ProtocolProxyEmitterTests).
- Throwing closures that take a by-value struct argument (now compile and marshal correctly).
- Frozen structs with packed sub-word optionals (now detected and handled safely; `HasSubWordOptionalLayoutMismatch` gate + skip + TypeSkipPrePass mirror + "# §6 #5" unit tests).

**Still latent / still matches the logged §6 description (post all of the above; high-value unverified per the original plan):**
- Apple-framework SwiftUI-bridge lipo path (`Sdk.targets` `_CompileAppleFrameworkSecondBridgeSlice` + `_AFB_*` staging dirs/logic) — distinct non-transactional staging mechanism from S9's `WrapperXCFrameworkMerger` transactional rewrite + third-party fat-fold. Still present; unaudited for atomicity under interrupt; not exercised by relevant gates (X64SimGate Apple leg had no SwiftUI bridge). M2 family; potential packaging cousin of the confirmed bridge second-class issues.
- Async `CreateAsync` public C# API surface for flattened `BoundType`/`BoundStruct` init params (raw `IntPtr` via default `CSharpPInvokeType` + forward-unchanged vs. typed `.Handle`/`Payload.DangerousGetHandle()` on the non-async factory path). Still latent (AsyncPattern.cs paths unchanged); functional (consumer can pass `.Handle`) but drifted public surface + ergonomics; unexercised by current fixtures (per plan note). M1 family (same shape as P0-04 Swift-side ABI fix but different direction).
- Residual carrier-conversion fall-throughs in `ExistentialProjection.GetArrayElementCarrierConversion` (exactly two mint/donate branches; EC1 null-proxy + composition EC2+ fall to non-owning alias tail — same over-release shape as the opaque sibling P1-08 fixed for arity-1). Shape still present in code; low emission reach per S4 verification (0 aliasing tails in probes; 19/19 owned cases took mint/donate). Pre-existing deeper gap.
- Latent dead-code in `ClosureProjection` escaping-parameter branch (emits cdecl UCO with trailing IntPtr context but wires to SwiftClosureData with no cdecl adapter; context would arrive in self register). Still described as inert (zero `MethodMarshalPlan.CallbackDeclarations` read sites today; MethodMarshalPlanBuilder never populates/calls GetParameterPlan on closure *params* — only returns; projection path is not the live ClosureEmitter/Wrapper.Marshalling path). Logged to prevent re-discovery if ever wired. Track-A4 notes the real gaps are already captured in the live engines.
- Closure-shaped / async same-signature method fan-out gap (the resolved plain sync fan-out + owner-body + sibling-vtable does not cover dispatchable-closure-param / closure-return / async paths, which take different emit paths that read *only* the owner's vtable and deliberately disable sibling fallback via the `!method.IsAsync && !hasDispatchableClosureParamForFallback` gate in Receivers.cs; non-owner existential dispatch → loud nil vtable unwrap trap). Remains OPEN per design boundary (consistent with the resolved plain case; "fails LOUD"; "EMPIRICALLY UNEXERCISED").
- (Small blittable-optional cdecl fallback — RESOLVED + reclassified LATENT during/after S6; MethodWrapperEmitter preemption makes the bad branch unreachable today; defense-in-depth applied anyway.)
- (EveryProtocol witness-table getter leaf-name collision — primary claim verified FALSE POSITIVE + unreachable in S7 pass; narrow stripper residual reframed + logged.)
- (Optional escaping throwing Void co-gater strip + box/unbox skew — RESOLVED during S6 via handler-layer mint registration + useBoxedContext threading + fixtures.)

**Broader synthetic-name / identifier cross-cutting (C1/C2 + P1-22 family):** Largely mitigated. Async-cleanup subset structurally eliminated into runtime helpers (stronger than guarding the synthetic). S6 user-param collision whole-category + P1-22 applications at owned sites (MethodClosureBridge, Nested, ProtocolExtensionClosureBridge, SwiftUIBridge, CSM/AsyncGenericParent, MGB/AMGBE/GCB, etc.). S7 verified coverage + constraints.md refresh (13/6 → 23/12) + guard test. ModuleEmissionContext:82 was a FALSE POSITIVE (regex field, not synthetic emission). Any remaining bare-literal synthetic sites in non-S2/S6 emitters would be defense-in-depth.

**Other notes from Track deferred sections + source spot-checks:**
- Many deferred are "same family as confirmed" (unguarded-identifier emission across emitters, key-divergence omissions on additional axes, classification drift for more modules/prefixes/rawValueTypes/NSString-typedefs, buffer sizing / ownership discrimination gaps, brace/scope duplication in the 5250-line parser, etc.). The remediation's pattern-fixes + sweeps likely absorbed a material fraction.
- High-yield clusters repeatedly called out for a second audit run (STATE §7 + REM PLAN capstone): A4 GenericClosureBridge (now largely addressed by follow-up), A7 `WrapperEmitter.Async` live/dead boundary (dead duplicate already deleted in S1; divergence items addressed), C1 `SwiftInterfaceAccessParser` brace/scope duplication (largest unverified surface; feeds public/internal classification), M3 short-prefix + autoBridge-no-prefix classification families (plus the ones confirmed in S8), dedicated L1 docs-drift pass (M4 already surfaces stale `[MonoJitCrash]`, non-existent scripts, `coverage-matrix.json` never produced, README classification table omissions, etc.; C2 surfaced stale `constraints.md` counts).
- C2 deferred (19 + 1 inconclusive) were *explicitly* kept out of S7 per SESSION-7-PREP (same key-divergence family as the confirmed P1-21; "Phase-2 work"; "the #1 way the ten-session ceiling breaks"). S7 owned only the confirmed + orphan verify + refresh.
- A8 parser/demangler deferred (~28 items) are mostly latent / low-reach today (many gated on Swift 6+ value-generics / modern concurrency / specific manglings the digester rarely emits) but are "live wires" the moment those shapes become emittable. Confirmed items (typed-throws first-match, `Yb`/`YK` demangle gap, `AnyObject` decl-drop, public protocol req `IsModuleInternal`, paren-in-string EOF-swallow) are the ones with nearer-term surface.
- M4 deferred (21+) include many stale-skip / misattribution / vacuous-body / doc-drift angles that overlap the confirmed P0/P1s; the "binding-report.json suppression completeness" and full `[SkipOnSimulator]` inventory against individual ABIs were not exhaustively re-probed.
- Inconclusive items across tracks often split on reachability ("real mechanism but currently masked/gated/dead") or severity tier. The verify stage was precise (many false alarms killed).

**Source spot-checks (selected high-plausibility deferred / OPEN sites, read-only):** For the items inspected (A1/A2/A3/A4/A5/A6/A7/C1/C2/M1/M2/M3/M4 cited lines + the §6 OPEN owning files), current code either:
- Matches the logged defect description verbatim (latent cases above);
- Shows clear hardening / new guards / ownership discrimination / reserved-name usage / async-effect threading / sub-word detection that directly address the logged claim (the follow-up items);
- Or has the site in a file that was heavily edited by an owning session with related changes (synthetic guards, try/catch UCO hardening, buffer sizing via `GetSwiftTypeSize<T>()`, `StringComparer.Ordinal`, co-gater broadening, etc.).

No attempt was made to claim "fixed" without the subagent's or my direct evidence; "still latent" means the shape in the cited code still matches the description in the audit doc. GenericClosureBridge (the standout "wrapping up" item) is the clearest success story from the follow-up: both defects now have explicit comments, the correct primitives (`MarshalMovedValueFromSlot`, plain `IntPtr` self), and round-trip tests.

---

## 4. Cross-cutting observations and persistent gaps

- **The plan's discipline held.** No session cascade; 10-session ceiling respected; §6 was the deliberate "do not touch now" log; same-shape absorption + owner-map promotions kept the count at 10 while still addressing cross-emitter manifestations (UCO, identifiers). Follow-up cleanup was narrow, targeted at the high-yield pre-existing items surfaced during the campaign (explicitly listed in RELEASE-NOTES), and delivered measurable BindingTests pass-count growth + unit-test growth.
- **Recall and verification gaps from the original audit are still relevant.** STATE §6 is explicit: no live `dotnet build -r` consumer round-trips and no full `nuke binding-tests` for most confirmed findings in the audit phase; Mono vs. NativeAOT largely unprobed for deferred; severity not fully settled on some; subsystem gaps (GenericClosureBridge was the least-verified closure engine; non-frozen struct handler; Swift5Demangler internals; full `[SkipOnSimulator]` inventory; etc.). "Not found ≠ not present."
- **Docs drift is visible (M4 + C2 + scattered).** Stale `[MonoJitCrash]` in README/rules (0 usages in source); non-existent scripts; `coverage-matrix.json` documented but never produced; `bindingtests.md` under-counts confirmed upstream issues; `constraints.md` counts were stale until S7; classification tables omit real attributes. L1 (docs-drift) was never run.
- **The deferred pool is now smaller than the original ~280 but non-zero.** Absorption during the 10 + follow-up cleanup reduced the "same family" surface. The surviving high-value unverified items (per the subagent triage + Track deferred lists + STATE §6/§7 recommendations) cluster around: GenericClosureBridge (largely addressed), remaining bridge-arch / second-slice packaging (M2), async/Bound* surface drift (M1), residual existential carriers (A5), latent parser brace/scope + demangler Y*/value-generics (A8/C1), classification families (M3), and the un-run L tracks (especially L1 docs + L2 ObjC). Any Phase 2 would start with "re-measure first" (per plan) + re-running the audit harness on the surviving leads (now on post-remediation code).
- **Test-gap lesson (CLOSURE-REGRESSION-TEST-GAP) is durable.** ABI/symbol invariants need *both* emitter-unit tests (fast, every `nuke test`) *and* BindingTests fixtures (the real end-to-end gate that exercises the runtime ABI the unit layer cannot see). The campaign delivered many of the latter for the confirmed items.

---

## 5. Recommendations (as audit output only)

- **Re-measure + re-verify the surviving pool before any Phase 2 decisions.** Run the surviving deferred + remaining §6 OPEN leads back through the `.claude/workflows/codebase-audit.js` harness (per REMEDIATION-PLAN §0). The code has changed materially; some candidates will wash out, others may surface as newly reachable or newly moot.
- **Prioritize the clusters already flagged as highest-expected-yield (STATE §7 + REM PLAN capstone):** A4 GenericClosureBridge (post-follow-up verification that the round-trips are durable), A7 `WrapperEmitter.Async` boundary (if any residual divergence), C1 parser brace/scope duplication (largest unverified surface feeding public/internal), M3 classification families (short-prefix, autoBridge-no-prefix, more NSString-typedefs / rawValueType omissions), dedicated L1 docs-drift pass (stale skips, coverage matrix, README/rules drift, `feedback_mono_jit_blame.md` trust contract).
- **Add durable fixtures for the high-value unverified shapes** (the "Recommended BindingTests fixtures" sections in the Track reports are the starting point; many are already partially addressed by the campaign's new coverage). BindingTests (sim + device) + SurfaceArea corpus are the long-term gates.
- **Consider a narrow L1 (docs-drift) + targeted re-audit of the parser + classification layers** as the first post-Phase-1 increment. These are compile-invisible until they bite a real library and have zero (or near-zero) unit coverage for the exact classifier decisions.
- **Preserve the plan's anti-cascade rules** if/when Phase 2 is authorized. Re-measure, verify before fixing, absorb same-shape, route by file/shape, cap the work.
- **The "wrapping up lingering from §6" work (GenericClosureBridge cluster + related pre-existing items) is the correct next tranche.** It aligns with the plan's own post-Phase-1 triage guidance and the capstone's "deferred pools most deserving a second audit run."

---

## 6. Files inspected (for provenance)

- All top-level `src/docs/audits/*.md` + `Track-A1_run-reports/*`.
- Targeted source reads/greps at the exact `file:line` (or nearby) cited in the deferred/OPEN/inconclusive sections of the Track reports and REMEDIATION-PLAN §6 (generator emitters/handlers/marshalers/parsers under `src/Swift.Bindings/src/`, runtime under `src/Swift.Runtime/src/`, Sdk under `src/Swift.Bindings.Sdk/`, BindingTests fixtures/tests, `build/` scripts, `.claude/rules/*.md`).
- Parallel subagent outputs (one fully completed exhaustive §6 OPEN + source-status triage; two others in progress with high tool-call counts on A1–A3 + GCB/A4/A6/A7 deferred sites — consistent directionally with the completed triage).

No existing audit docs were modified. This document is the sole output.

*Provenance note: synthesized from the 14 Track reports + supporting docs listed above + direct source inspection of cited sites (read-only). The original audit's own caveats (40–60% recall, no full runtime repros for most, "not found ≠ not present") apply to this review as well. 2026-06-06.*