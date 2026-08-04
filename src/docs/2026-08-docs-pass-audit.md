# 2026-08 docs-pass upstream — session audit record (2026-08-04)

Audit of the 18-session `2026-08-docs-pass-upstream` program (range `8b65164c^..24e23da6`),
run per the standard session-audit shape: 8 Opus reviewers (pair + cross-cutting) →
consolidation → 9-agent fix wave → integrated gates → full-corpus runtime validation →
downstream rebuild → independent Grok+Codex end-to-end pass. Companion evidence doc:
`2026-08-docs-pass-evidence.md` (ledgers, rename tables, owner register).

## Verdict summary

- **Program code quality**: sessions 02, 04, 06, 08, 14, 18 verified clean. The rest shipped
  real value with defects concentrated in two areas: unrecorded API-shape reshaping (S16
  reconciler inputs) and reporting/visibility gaps (case-only renames, silent declines).
- **Downstream impact before fixes**: NOT downstream-clean — 7/9 third-party libraries failed
  to generate against the program HEAD (S16 `ApiSurfaceReconciler` hard-fail on unrecorded
  reshapes; Stripe nested-View qualification).
- **Gate honesty**: zero `--permissive`, zero test skips, no assertion weakening across all 18
  phases — but the full-corpus simulator gate never reached PASSED in any phase, quoted sim
  counts came from failed/truncated runs, and phase 17 quoted a pass count from a run it had
  killed. The post-fix validation in this audit therefore required one clean, unfiltered
  full-corpus simulator run (first of the program).
- **ObjC rename ruling** (owner-delegated, decided 2026-08-04): the S17/S12 ObjC-lane renames
  were intentional and a net win for the C# binding surface — **ship as breaking**, no compat
  shims, no revert. Rename tables in the evidence doc § S17 become migration notes in the next
  release notes.

## Findings → fixes (all on main, cherry-picked from 9 parallel fix branches)

| ID | Severity | Finding | Fix commit |
|---|---|---|---|
| F1 | HIGH | S16 ApiSurfaceReconciler hard-failed 7/9 downstream libs: reshaping emitters (closure bridge, generic bridge, constrained existential, protocol-extension closure) never recorded their emitted shapes; interior-default omission + async→callback rewrite unmatched | `9cfec468` |
| F4/M4 | HIGH/M | S10 all-defaults decline dropped silently; TCS overload producer unguarded/unreserved (CS0121 reachable) | `9cfec468` |
| M1/M2 | MED | Conformance drops without TypeRecord / with PATs unreported; co-gating reconcile keys could never match; 4 reason-token pairs split buckets; `_`-label recovery unified | `95a1dad7` |
| F3 | HIGH | Case-only collision renames invisible to the overload-name gate; new CaseOnlyRenames report lane + two-lane gate (case-only = non-failing category); double-walk publish bug fixed | `a66ceeb3` |
| HIGH-2 | HIGH | Stripe PaymentSheet: nested SwiftUI View emitted under bare name; now module-qualified via `ViewBridgeInfo.SwiftTypeReference` stamped once at analysis | `8879b317` |
| F7/M5 | HIGH/M | S15 payload machinery unwired in production (BlinkIDUX `CarriesPayload:false`); wired per vendor swiftinterface evidence + ownership/Dispose contract docs + scalar-disposal runtime tests | `c95e6755` |
| M7/M8 | MED | Six SwiftUI @frozen entries corrected in SwiftUIDatabase.xml (layout-bearing omissions deliberate); ObjC enum-case raw→emitted map threaded to Swift-side reference emitters (CS0117 fix); rule-1 token boundary; array `[Static]` forwarder | `342f50da` |
| F2 | HIGH* | S9 reserve-before-validate inverted (name burned by non-emitting property). *Predicate `EmitsStaticPropertyUnderRequirementName` RETAINED as documented policy exception — CS0736 is type-surface scope, not leaf-recoverable; see owner register (g) | `fcdaec35` |
| M3 | MED | SB1003 analyzer missed mutating-call-through-copying-struct-property (invocation arm added); ReturnsByRef FP guard | `9d8f44a3` |
| F6/M9/M10 | MED (docs) | Evidence rescued into tracked docs (36-row S03 ledger, S17 rename tables, owner register); 16 not-planned rows repointed; FBSDKLoginKit row re-filed as S16 regression; wiki SB1003 + case-only carve-out (local commit, push pending) | `f9417204` |
| audit-found | — | Skip-surface marker stutter introduced by `fcdaec35` (second constrained-extension sibling no longer swallowed → adjacent identical tombstones): fixed by exact-duplicate line dedup in `UnsupportedCommentEmitter`/`BufferedSourceWriter.TryWriteLineOnce` (rollback-safe), with unit pins | integration commit |
| audit-found | — | PackGate mixed-fixture staleness vs `403c0aeb` Swift-import renames (two sites): generation assertions now expect `GetNSObject<…Greeter>` under the Swift-import name AND `[BaseType(Name = "<raw>")]` registration retention; the gate's consumer app now references the declared name (`Greeter`) instead of the raw ObjC spelling (was CS0234 against the renamed companion) | integration commit |
| audit-found | HIGH | NS_TYPED_EXTENSIBLE_ENUM bridge regression from `403c0aeb`: the new `AcceptRenames` vet only accepted renames for declared classes/protocols/enums, silently dropping the harvested typedef mapping (`SbGap2StaticAuthType → AuthType`) — the typed-enum bridge record stayed keyed by the raw ObjC name, so every Swift member typed by the Swift-import name (PackGate `echoAuthType`, and downstream FBSDKLoginKit's `LoginAuthType` shape) degraded to a placeholder and was skipped. Fixed: entries the rewriter can never apply (no companion declaration) pass through to the rekeyer — their record projects onto `Foundation.NSString`, so the two-applier disagreement the vet exists to prevent cannot arise. Unit-pinned red→green in `ObjCSwiftImportNameRewriterTests`; PackGate typed-enum assertions are the e2e gate | integration commit |
| audit-found | HIGH | Mappedin downstream regression from `504c464a` (parent `a40f87c4` clean; pinned by per-commit sdk-mode A/B): that commit's `MethodClosureBridge.ClassifyParam` enum widening (legitimate — 18 new bridges) made `MapData.getByType/getById/getByExternalId` newly bridge-eligible, but `IsClosureArgSupported`'s `IsSwiftResultWithAnyErrorFailure` accepts a `Result` closure without inspecting the success payload, and MCB renders TypeSpecs verbatim — so the method's own generic landed in the wrapper as the raw `τ_0_0` archetype → SWIFTBIND051 for the whole library. In plain-CLI runs the verify-recover loop compiled, saw the error and withdrew the members post-hoc (SWIFTBIND112); sdk-mode skips wrapper compilation, so the broken wrapper shipped. Fixed by consulting the EXISTING `WrapperValidation.HasRawGenericTypeParams` gate in `MethodClosureBridge.IsEligible` (freeze-policy compliant — an existing gate applied at sites that lacked it, not a new predictor); same latent hole closed in `NestedClosureBridge`. Members now honestly skip up-front with recorded cause (`UnsupportedClosure`), matching pre-`504c464a` disposition; widening kept. Verified from integrated main: sdk-mode regen exit 0, reconciler passes, τ_0_0 code sites 3→0, `SBW_MCB_*` 249→246 (exactly the 3), plain-mode wrapper now compiles first-try with no withdrawal round; red→green unit pins (5 tests) with positive controls | integration commit |
| audit-found | HIGH | Stripe downstream regression from `7079e816` (parent `485eb7ae` clean; pinned by per-commit sdk-mode A/B): the arity-2 `AddressViewController.ConfigurationInfo(defaultValues, additionalFields)` vanishes from the default-parameter ladder (6/5/4/3/2 → 6/5/4/3/0) → downstream consumer CS1729. Root cause is NOT the CS0121 guard (its decline count is zero on Stripe): the commit's cap arithmetic charges the new all-defaults form against the `MaxOverloads` trim budget, and lowering the budget removes the DEEPEST trim — i.e. the shortest, most-used arity — a source break traded for a pure addition. Fixed: the all-defaults form is added on top of the budget (bounded: ≤1 extra per method, only when the ladder can't reach the shortest callable form); ladder restored as a strict superset (0,2,3,4,5,6), verified by sdk-mode regen from integrated main; red→green unit pins incl. a two-sided superset assertion with positive controls | integration commit |
| audit-found | HIGH (pre-program latent) | Lottie SWIFTBIND052: SwiftUI bridge modifier application passes a stored plain `Bool` to a modifier whose parameter is `Binding<Bool>` (`result.isOn(val)`), so the emitted bridge never compiles. The init-parameter path already had a `Binding` arm; the modifier path never grew one (`AnalyzeModifiers` gates on `Kind` only, and `MapBindingType` unwraps `Binding<T>` to its inner kind). NOT a program regression (emitted line character-identical at the pre-program base) but funded under the "all libraries green" mandate. Fixed with full `Binding<T>` modifier support: the call site now constructs a real two-way `Binding` over the existing `@Published` state field (SwiftUI-side writes update the view; the one-way-to-managed limit is documented on the emitted C# setter, matching the init-path fidelity). Red→green unit pins + first-party `ModifiableView.toggled(Binding<Bool>)` compile-gate fixture; regenerated Lottie bridge swiftc-typechecks clean (exit 0) against the real framework from integrated main | integration commit |
| audit-found | — | `Swift.Bindings.Apple` inner-TFM build race: each TFM's apple-types `dotnet run` child build writes the generator's + `Swift.Runtime`'s shared Release obj/bin; a generator source change invalidates every TFM's stamp at once, so the concurrent inner builds file-lock collide (MSB3026/CS2012 → PackGate red). Latent until this audit because the stamp normally short-circuits; fixed by serializing the outer cross-targeting dispatch (`BuildInParallel=false`, outer build only), verified by re-arming the race (Release outputs + all stamps wiped) and re-running PackGate | integration commit |

## Post-fix gate status (integrated main)

| Gate | Result |
|---|---|
| `nuke test` | GREEN — 17137 unit + 79 + 770, 0 failures (re-run after the typed-enum bridge fix; floor auto-ratcheted from 17039). Re-run again GREEN after the three downstream-regression fixes landed (Mappedin closure-bridge gate, Stripe cap arithmetic, Lottie Binding modifier — 11 new unit pins total) |
| `nuke binding-tests --compile-only` | GREEN — re-run after the typed-enum bridge fix: manifest green, overload gate 36 non-numeric, parity/resilience/ingestion kitchens green, skip-surface downward-or-flat. Re-run again GREEN (3:42) after the three downstream-regression fixes, no baseline movement; this pass also compile-proves the new `ModifiableView.toggled(Binding<Bool>)` fixture |
| `--skip-surface` | GREEN — 226 unique keys, downward-or-flat; baseline reseeded at `f9417204` (dedup fix also ratcheted `ArrayMetatypeStore.loadItems` 2→1, `CtorAdmBox.init` 3→1) |
| Full-corpus sim run (unfiltered) | DONE — 3409 pass / 41 skip / 6 known-environmental fails across all 372 classes (first full-corpus run of the program; floor was 3306). Sim floors reseeded to 3409 in `validation-baseline.json` + `runtime-identity-baseline.json` (skip identities verified byte-identical to baseline, 41/41). See "Sim-run crash dispositions" below. |
| `nuke validate` (+ PackGate) | Validate GREEN — 132/132 passed on a wiped cache, baseline updated (line-count churn from the tombstone dedup + program changes). PackGate went red three times, each a real audit find: (1) the inner-TFM build race (fixed, verified with the race deliberately re-armed), (2) the typed-enum bridge regression (generator fix above), (3) the gate's own consumer app still referencing the probe class by raw ObjC name post-rename (gate fixture updated to the declared `Greeter` name). Final PackGate run fully GREEN in 3:12 — typed-enum value + optional round-trip at runtime, single class registration, all structural legs |
| Downstream rebuild (9 libs) | 9/9 GENERATE (was 2/9 pre-fix); 5/9 fully green incl. MapLibre sim 62/0 and BlinkID sim 304/0. FBSDKLoginKit headline: generates end-to-end, **zero** AuthType skips (typed-enum fix proven on the real target), rename table resolved as legitimately ZERO rows (module emits no ObjC companion lane — evidence doc § 2.4 closed; FBSDKLoginKit release unblocked). Facebook: owner-ruled breaking-rename migration applied to the downstream test app (4 identifiers, word-boundary; interface + enum-case spellings untouched) → cell PASS 33/0/3, making it **6/9 green**. Provenance A/B vs the PRE-program base (8b65164c^, sdk-mode invocation — plain-CLI A/B provably under-reports, Mappedin τ-count 0 vs 3): **Lottie = pre-program latent** (identical SWIFTBIND052 line emitted at base), **Mappedin = program regression from `504c464a`** (parent a40f87c4 clean; the newly-emitted generic callback member writes the unsubstituted `τ_0_0` archetype into its `@_cdecl` adapter → wrapper SWIFTBIND051), **Stripe = program regression from `7079e816`** (parent 485eb7ae clean; the CS0121 decline drops the arity-2 `ConfigurationInfo` overload and an arity-0 ctor takes its slot → downstream CS1729). Both regressions were masked from `3d628a24` onward by the S16 reconciler hard-fail — the audit fix wave made them observable, it did not cause them. After the three fixes (findings table) landed on integrated main: repacked 0.18.1 feed, obj/bin wiped, all three cells re-run — **final tally 9 PASS / 0 ERROR** (Mappedin 155/0, Stripe 298/0, Lottie 89/0). Lottie's first re-run red was one more unmasked owner-ruled breaking rename (`LottieAnimationView.Play(fromProgress:…)`/`(fromFrame:…)` both collapse to one C# signature → label-derived `PlayFromProgressToProgressLoopModeCompletion`/`…FrameToFrame…`; the old `Play2` numeric suffix is exactly what the overload-name policy forbids) — migrated in the downstream test app under the Facebook precedent (1 call site). Two uncommitted consumer migrations now ride in swift-dotnet-packages (Facebook 4 renames/17 sites, Lottie 1/1) pending that repo's commit cadence |
| Grok + Codex end-to-end | COMPLETE — 3 rounds, both sessions resumed throughout. **r1** (full 29-commit range + fix-wave commit): findings consolidated into one fix commit `6f8b4482` (protocol `RungFits` parity, reservation release, tombstone qualification, plus doc/test items). **r2** (resume, full scope restated): Grok clean; Codex 1 High — same-leaf async views resolved one leaf-keyed async pattern and both emitted its authored Swift `SessionClassName` → Swift redeclaration (unmasked by the wave's module-qualified view collection). Fixed red-first in `e933ea90`: emission-site re-spell (`SwiftUIBridgeEmitter.cs:91` — path-qualified view re-derives the session class from its own `Identifier`), pinned by `EmitBridgeFiles_SameLeafAsyncViewsFromManifest_EmitDistinctSessionClasses`; unit floor 17173→17174; gates green. **r3** (resume, full 31-commit scope): Codex — no High/Medium/Low, round-2 High confirmed fully addressed. Grok — no High/Medium; 1 Low (dev-notes attributed `AssignBridgeIdentifiers` to `ModuleEmissionContext`; lives on `SwiftUIBridgeEmitter` — doc line corrected). Loop terminated per the severity rule (Medium/Low-only round → final fix pass, no verifying re-review). Latents surfaced by the reviews and judged not-now are recorded in `src/docs/not-planned.md` (leaf-keyed pattern/hint sharing, Font design dual numbering, vtable dual-walk key domains, validator matcher residuals, PropertyDecl emission signals, RecordReservation key misattribution, snapshot subscript gap, ClassHandler propertyNames over-seeding, BuildOverloadDeclCore clone hygiene) |

## Sim-run crash dispositions (2026-08-04, per audit mandate)

The run used the resume-on-crash pipeline: attempt 1 covered 270 classes and crashed, the
resume covered the remaining 101, and the crashed class completed 6/6 in isolation — total
coverage 372/372 classes, aggregate 3409 pass / 6 fail / 41 skip.

- **Six fails, all `LiveActivityTests` `request()`-path**: the documented environmental
  foreground-active precondition (CLI-launched simulator app cannot reach foreground-active).
  Not code defects; unchanged from prior classification.
- **Crash in `OptionalReferenceClosureArbiterTests` (2 methods)**: matched to the documented
  pre-existing finalizer-queue SIGSEGV (SwiftSafeHandle ReleaseHandle → VwtDestroy →
  `swift::RefCounts::doDecrementSlow` on the finalizer thread). Same victim class as the
  2026-08-01 clean-main control run; victim class varies run-to-run because the crashing test
  merely detects a finalizer queue poisoned earlier — it is not the culprit. The class passes
  6/6 in isolation. Caveat: this occurrence left no `.ips` and no ReportCrash unified-log
  entry, so the match rests on victim identity + shape + the clean-main control, not a stack.
- **`PatParentAsyncVoidMethodsTests`** (explicit disposition required by the mandate):
  runtime-detected SKIP on Mono JIT with a proven pure-managed upstream repro
  (`mono_arch_unwind_frame` assertion, mini-exceptions.c:488, zero Swift/binding frames on the
  faulting stack); still runs on CoreCLR (macOS) and NativeAOT (device). The skip is
  correctly scoped and remains in the 41-skip baseline set.

## Deferred / owner decisions (register lives in `2026-08-docs-pass-evidence.md`)

(a) enum-tag LCP persistence; (b) case-only naming fork (numeric vs derived); (c) S03
deprecated-pair collapse; B1 P1 recoveries (Mappedin GetMapData, FB LogIn) — fund or accept;
B17 hollow-module packing policy; (g) CS0736 prediction-gate policy exception; (h)
`--no-verify-csharp` on the main BindingTests regen (only kitchens exercise verify-recover);
wiki push (2 local commits); swift-dotnet-packages carries two uncommitted consumer
migrations (Facebook 4 renames, Lottie `Play`→`PlayFromProgressToProgressLoopModeCompletion`)
— commit that repo per its cadence (only once the referenced SDK version is published) and
carry both rename sets into the next release's migration notes. FBSDKLoginKit rename-table
precondition RESOLVED 2026-08-04: the module emits no ObjC companion lane, table is
legitimately zero rows (evidence doc § 2.4).
