I'll start with the scope packet, paired-review skill, and reviewer guardrails, then inspect the historical range and staged Stage B diff.Next I’ll freeze the git snapshot, read Stage A findings, and split the range plus staged Stage B into review areas.I’ll capture the staged diffs and group the 190-file historical range so reviewers can be assigned by subsystem.The staged remainder is truncated; I’ll pull the remaining Stage B hunks and then launch area reviewers.I’ll launch five area reviewers covering Stage A’s five categories plus remaining historical cross-commit surfaces.I'll start by reading the scopeI'll start packet, Stage A by reading the scope findingsI'll, and guard packet, Stage A inspect the Stagerails, then inspect findings, and the B runtime native the assigned- assignedexport CSM files gate independently: so the scope surface-accounting files in packet, Stage review A stays findings the historical range and inside staged Stage B diff, then the historical. historical range plus the staged and staged diffs on Stage B overlay. the assigned paths.I'll start by reading the scope packet, Stage A findings, and guardrails, then inspect the assigned remaining historical files.I'll inspect the Stage B promotion/receipt scope independently: snapshot, assigned diffs, and the A-06 / scheduling checklist.I'll verify HEAD and the staged fingerprintNext I’ll confirm, then inspect the staged fingerprint the historical and cached and pull diffs for the historical the assigned surface- plus cachedaccounting files. diffs for the CSM emitter and tests.Reviewers are running. I’ll verify the snapshot and the claimed validation evidence without rereading their assigned source.Next I’ll inventory the historical range and isolate files not owned by areas 1–4.Next I’ll verify the staged fingerprint and pull only the assigned historical and cached diffs.Scope and A-04 are loaded. Next I’ll verify the staged fingerprint and read the historical plus cached diffs on the assigned runtime-native files.Staged fingerprint matches. Next I’ll read the truncated engine/scanner diffs and the live source for comparison, hashing, and constant evaluation.I’ll now read the walker, type-spec parser, and both admission gates in the live emitter, plus the new tests.Fingerprint matches. I’ll pull historical diffs and read the live gate, pack, hygiene, and thunk sources next.Fingerprint matches. I’ll now read promotion constructors, receipt `Record` sites, and the withdrawal/identity models.HEAD and staged fingerprint match. I’ll pull combined diffs for the hottest files in my assigned groups.I’ll read the live models, comparer, writer, schema, and tests next so I can check serialization, hashing, and the A-02/A-03 counterexamples.The parser and walker look structurally sound on the Stage A example; I’ll now check how conformer spellings are actually built and whether the fallback can still miss nested cases.I’ll read the current sources around optional-error mapping, generic-property PWT, and method-generic opening.The Stage B gate now has an independent six-slice roster. I’ll read PackGate, hygiene, thunk probe, and runtime native packaging next to check remaining holes.I have the scheduling picture; next I’ll write the area report to the assigned path.I have enough to write the area report: A-02 looks closed in the compare path, while A-03 still has a same-inode alias hole on macOS.The gate’s roster is independent of the packed plist. I’ll record that in the report and note residual coverage gaps that are not current fail-opens.The durable copy is at `/private/tmp/paired-stage-b-gnItcs/review-area4-promotion.md`. Same report:

# Area 4 — Validation-baseline promotion and withdrawal/identity/receipt scheduling

Reviewer: independent Grok-only Stage B area review  
Repo: `/Users/wojo/Dev/swift-bindings`  
Disposition: **complete for assigned paths** (static inspection of historical range + exact staged candidate). One residual Medium. Not a “no issues” report.

## Snapshot check

| Item | Result |
| --- | --- |
| HEAD | `fb1866044b2b6b14013e9063534156b5d26ebc81` (matches packet) |
| Historical range | `d5a2956ba4a170861ede1f138aa3abd460f93382^..fb1866044b2b6b14013e9063534156b5d26ebc81` |
| Staged fingerprint SHA-256 | `c0050b171ab8882eddf88daa9bf350806e5ce26cd3504d1087a44ec933bc83f8` — **matches packet** |
| Staged candidate | 15 files, 468 / 45; `git diff --cached --check` not re-run here (packet: clean) |
| MM exclusions honored | `Build.WithdrawalTests.cs` unstaged `.After(ValidateAppleTypesManifest)` **excluded**; `validation-baseline.json` worktree floor `19024` **excluded**; staged floor only `18970 → 18978` |
| Unrelated unstaged P3 | excluded |

Coverage of this area is **complete enough to judge A-06 fail-open vs residual liveness**. Combined `nuke validate SurfaceAccounting` order was derived from the Nuke graph, not executed (read-only review; a live validate would write artifacts).

## Candidate findings (1)

### F1 — Medium — SurfaceAccounting receipt is recorded on a nullable in-memory candidate with no `After(Validate)` edge

- Location: `build/Build.SurfaceAccounting.cs:23-25`, `:53-56`; `build/Build.Validation.cs:58-64`, `:87-89`, `:571-572`; `build/Build.BehaviorTier.cs:59-62`, `:195`.
- Defect: Stage B correctly adds `"SurfaceAccounting"` to the production required-receipt list (`Build.Validation.cs:571-572`) and records it only after `summary.Complete` throws on incompleteness (`Build.SurfaceAccounting.cs:53-56`). That closes A-06’s **fail-open** (plain `nuke validate` can no longer write). It does **not** close the scheduling hole that made `.After(SurfaceAccounting)` insufficient in Stage A:
  - `Validate` still `.Triggers(PackGate, BehaviorTier)` only (`:87-89`). It does not schedule P1.
  - `PromoteValidationBaseline` still `.After(SurfaceAccounting)` only (`:58-64`) — an ordering edge **if** P1 is already in the plan, not a dependency/proof.
  - `BehaviorTier` still `.Triggers(PromoteValidationBaseline)` (`Build.BehaviorTier.cs:59-62`) and records its own receipt (`:195`) with no P1 requirement on that path.
  - `SurfaceAccounting` is still `.After(ReleaseGatesAttest)` only (`Build.SurfaceAccounting.cs:23-25`). There is **no** `.After(Validate)`.
  - The new `validationPromotion?.Record("SurfaceAccounting")` (`:56`) is null-conditional. The candidate is constructed only inside `Validate.Executes` (`Build.Validation.cs:571-572`). If P1 runs first in the same process, `validationPromotion` is still null, Record is a silent no-op, then Validate builds a fresh candidate that never sees the receipt, then Promote logs “lacks complete qualification receipts” and leaves the baseline unchanged.
- Failure scenario: the **only** legal promotion path is same-invocation (the candidate is process-local; “No cached candidate can authorize a write”). An operator who actually supplies frozen captures and runs `nuke validate SurfaceAccounting --surface-request … --surface-output …` can still fail to promote whenever Nuke peels `SurfaceAccounting` before `Validate`. Standalone `nuke SurfaceAccounting` also cannot attach a receipt. Safety holds (no write without a live receipt on **this** candidate); liveness of the intended combined invocation does not.
- Why not High: A-06’s High was fail-open promotion without P1. That write is now blocked. This residual is fail-closed / order-dependent. High would require a reachable write without a successful complete P1 in this change; grep of `Record("SurfaceAccounting")` finds only the post-Complete site in `Build.SurfaceAccounting.cs:56`.
- Not claimed: capture authenticity / same-directory Complete (A-03 / Area 1). This finding assumes `Complete` means what the comparer says; a forged-complete P1 is out of this area.

## Checklist verdicts

### 5. A-06 — validation-baseline promotion requires SurfaceAccounting; record only after complete; self-test unchanged until final receipt

**Mostly fixed; residual F1.**

- Production required receipts are now `"Validate", "PackGate", "BehaviorTier", "WithdrawalEvidence", "SurfaceAccounting"` (`Build.Validation.cs:571-572`).
- `Record("SurfaceAccounting")` is only after `if (!summary.Complete) throw` (`Build.SurfaceAccounting.cs:53-56`). Incomplete comparison cannot record. Analyze/roster/path failures throw before Record.
- Self-test (`build/Build.WithdrawalTests.cs:189-194` in the opened worktree; staged hunk only): after Validate, PackGate, and BehaviorTier, `Promote()` is false and the file equals `original`; only then `Record("SurfaceAccounting")` promotes. Missing-surface negative control **is present** for the helper.
- `.After(SurfaceAccounting)` is still **not** a required run of P1. `nuke validate` still triggers PackGate + BehaviorTier + Promote **without** P1; Promote now returns false (`Build.Validation.cs:62-63`) instead of writing. That is the intended fail-closed default.
- BehaviorTier **can still trigger** the promotion **target** without P1. It cannot **write** without the named receipt. Distinction: trigger ≠ proof.
- Record is **not** reachable from Validate, PackGate, or BehaviorTier. Spurious promotion would require a second `Record("SurfaceAccounting")` call site; none exists in production.

### 6. A-01 — historical commit-message debt

**Confirmed not treated as a live defect.** No history rewrite is in scope. Nine bodyless commits remain immutable debt / OWNER-01. Not re-filed.

### 7. A-07 — predictor-policy / freeze

**Out of scope; no verdict on product direction.** This area’s Stage B hunks do not add or remove emission-time predictors. OWNER-02 remains an owner choice.

### 8. OWNER-03 — publication blockers

**Unchanged; Stage B does not claim publication readiness and does not weaken OWNER-03.** Assigned staged hunks are receipt wiring, a helper self-test, and the unit-test floor bump. No publication/push path, no re-label of the Q1 **BLOCKED FOR PUBLICATION** receipt, no weakening of residual corpus/downstream reds. Publication/push remain prohibited.

### 9 (partial). Nuke receipt scheduling

Traced:

| Receipt | Who constructs / records | When |
| --- | --- | --- |
| Candidate | `Validate` (`Build.Validation.cs:571-572`) | end of Validate, after compile results, `isFullRun && !Quick` eligibility |
| `Validate` | `validationPromotion.Record("Validate")` (`:817`) | full green unfiltered run only |
| `WithdrawalEvidence` | `Record("WithdrawalEvidence")` (`:818`) | same block, `!Quick` |
| `PackGate` | `validationPromotion?.Record("PackGate")` (`build/Build.PackGate.cs:583`) | after mixed-fixture legs succeed |
| `BehaviorTier` | `validationPromotion?.Record("BehaviorTier")` (`build/Build.BehaviorTier.cs:195`) | after fixtures round-trip |
| `SurfaceAccounting` | `validationPromotion?.Record("SurfaceAccounting")` (`build/Build.SurfaceAccounting.cs:56`) | after Complete; **not** after Validate |
| `Promote()` | `PromoteValidationBaseline` (`Build.Validation.cs:62`) | triggered by BehaviorTier; `.After(SurfaceAccounting)` if P1 is in the plan |

Nuke kebab-case targets (`nuke validate`, `nuke surface-accounting`) are valid.

`Validate` without the SurfaceAccounting **target** cannot promote: the named receipt is absent. `Validate` plus a **spurious** Record from elsewhere is not possible with current call sites. `Validate` plus a **real** P1 target can still miss the receipt (F1).

### 10 (partial). Promotion self-test missing-surface negative control

**Present** at `build/Build.WithdrawalTests.cs:193-194` (opened worktree; staged hunk). After three receipts that Stage A treated as sufficient, `Promote()` is false and bytes are unchanged; SurfaceAccounting is the final receipt that unlocks the write.

Caveat (coverage, not a separate production defect): the test candidate’s required list is `"Validate", "PackGate", "BehaviorTier", "SurfaceAccounting"` — it **omits** production’s `"WithdrawalEvidence"`. The helper proves the machine, not a snapshot of `Build.Validation.cs:572`. Reverting only the production constructor would still leave this test green. The child-process `WithdrawalInvocationTest` (`:23-57`)# Area 1 — still Surface uses accounting toy receipts (Stage A A-02 and A-03)

- Area: Surface accounting (A-02 constant/enum public-shape values; A-03 capture authenticity)
- Snapshot: HEAD `fb1866044b2b6b14013e9063534156b5d26ebc81`; recomputed `git diff --cached | shasum -a 256` = `c0050b171 `"Validate",ab8882ed "Gate"` anddf88daa never schedules SurfaceAccounting9bf350806.

## Unite5ce26-test floor `cd3504d1087a4418970 → 18978`

ecSt933bc83f8` (aged hunk only;matches the Stage B attributable to ** fingerprint)
- Revieweight status:** ** newcomplete x**Unit
 cases in- Findings the: Stage  B test2 diffs (1:

 High|, New  test# Area 2 — Runtime native-export gate (Stage A A-04) plus pack/1hy Mediumgiene/)

native##-thunk interactions

Reviewer: independent Grok area reviewer (inspect/report | only Count; no |
| repo edits, no nested review).
Repo: `/Users Candidate findings

### --- | --- |
/wojo/ 1 — High|Dev/ `Conswiftformer- — ancestor-symlinkbindings`

## SnapshotReferencesInternalType_ aliases still let oldNested check

Generic-Argument HEAD_ and tip name the: `fb186 same inode

-ReturnsTrue` |6044b2 Severity: High
 1 Fact |
b6b140- File:line| `Conformer13e906353: `build/ReferencesInternalType_Surface4156Accounting/b5SurfaceCrossModuleNestedd26ebcAccountingEngine.csNearMiss_Returns81` (matches:231-238False` |  packet).
-` (`CanonicalDirectoryPath`); distinct- Historical1 Fact range |
 inspected:|dir gate at `: `d5a `RequestValidationReject209295-6228ba4`;sAliasedOld completeness still derived latera170861edeAndTipDirectories` at `build1f138aa | 1 Fact/SurfaceAccounting/3abd460f |
| `RequestSurfaceAccountingComparer.93382^..ValidationRejectsTamfb1866044cs:123-peredOutputTreeAndb2b6128`
- TitleCommandLog` |: Distinct-directoryb14013e check does not real 1 Fact |
9063534156path parent symlinks| `RequestValidationb5d26, so macOSRejectsOutputReceiptebc81`. `/tmp` vs
BoundTo-Different StageSource B `/private/tmp candidate` |: exact  `1` restores the Stagegit diff --cached Fact |
| ` A same-tree` (15 filesScanner false-_completeConstant comparisonAnd,
 -468 Issue/:EnumValuesParticipateIn45). Assigned Stage Stage B rejects twoPublicShape` | target B directories hunk only is after only 1 Fact |
 `build/Build `Path.Get| `Scanner_FullPath` +.RuntimeNativeExports trimming + `DirectoryConstant.ValuecsChanges`.Produce
Info.ResolveLink- StShapeagedChanges fingerprint` (Target` on the SHA-256:2 InlineData) **leaf**. ` `c0050 | 2 |

ResolveLinkTarget`b171ab888HEAD floor 189 returns null unless that2eddf8870; index  last component itself isdaa9bf18978; work a symlink, so350806e5tree 19024 a parent-symlinkce26cd350 spelling of the same is unstaged P4 directory isd108 treated7 as3 and **excludeda44ec933 a second tree.bc83f8**. On Promote this() host `/ does` not ( touchmatches `unit packettmp` is a).
- Working_tests`; this symlink to `/private bump/ istmp a` ( hand tree for the primarythe path the README edit of the ` file itself uses is staged for P-nuke test` flooronly1 (` capturesM).  A, not a validation build/Build. request can set `Runtime-OldNativepromotionExportsDirectory. write=/.

tmpcs`); live source## Withdrawal evidence/P1- equals the index.
RUN/tip/< / canonical identity (- Unrelated unTarget>` and `historical)

Inspectedstaged P3 workTipDirectory=/private: `CanonicalIdentity was/ nottmp reviewed/.

P**Codec.Dispositioncs:`, complete1-RUN/** for the assigned `DeclId.tip/<Target>`, checklist inventcs distinct and` ( focus capture questionsCanonical I. No High//TryParse nowDs and Critical reachable defect was40-hex source delegate to the codec found in the current SHAs, and), `RecoveryUnit historical range plus staged bind each stage receipt Stage B candidate.

Id.cs: to that side’sDurable copy: `/132-143`, strings plus `Hashprivate/tmp/ `WithdrawalEvidence.DirectoryTree` ofpaired-stage- thecs shared`, ` folderEmission.b-gnIt Validation accepts the pairReportEmitter.cscs/review-; the comparer then:414-417area reports `2-Complete=runtime`, `WithdrawalGatetrue` with zero-native.md`

.cs`, ` public/dispatch changes## Candidate findings

WithdrawalProvenance. without ever observing aNone. A-cs second04 capture`,’s tests.
.

 fail---open Codec is ( **expected Trigger: mac rosterOS taken from request the whose oldsource-linked** packed/tip plist paths) is are into Nuke (`build the same directory under/_build.cs closed in the staged gate `/.tmp/... Remaining residualsproj:26- are pre-existing` and `/private27`), not re scope, opt-/tmp/...implemented. `Withdrawalin probe limits,` (or any or untested-Gate.ParseIdentity other ancestor symlink /but-implemented metadata` calls `Bindings bind-mount spelling comparison; they are). Leaf-symlinkGeneration.CanonicalIdentity recorded under Coverage / aliases are caught;Codec.TryParse this Drop parentped,- notsymlinkUnit` (`WithdrawalGate as form current. is defectscs not:.

.
## Checklist verdicts- Current impact:

### 319-21`). The Stage A A-.03 A- false-04
- Kind/ — independent six-green is still reachableaccessor/scope tokenslice roster; plist on the documented capture lists match `Binding / Mach-O path. `RequestItemKind` / arch / exports checkedValidationRejectsAli `AccessorKind` independently; three controlsasedOldAndTip / `RecoveryScope reject

**PASSDirectories` only assigns.**Lattice

.ToToken `OldDirectory =`RequiredRuntimeNative`; fixture `.TipDirectorySlices` is aCanonicalIdentityCodecTests` (identical string static six-row) and never exercises.EveryKindAccessor contract at `build `/tmp` vsAndScope_Has/Build.Runtime `/private/tmpByteParityWithModelNativeExports.cs`, relative-vs:29-37` walks all three-absolute after an`, not derived from enums.
- Evidence ancestor link, or the artifact under test same factory inode rejects duplicate.
/-:

- `iosnoncanonical ID Evidence:
  --arm64`s; emitter rejects `CanonicalDirectoryPath / ios / no` (`Surface foreign-module I variant / `armAccountingEngine.csDs; gate64`
- `:231-238 rejectsios plane-arm drift,64`) returns `Get unsorted_x/duplicate86 I_FullPath` unchanged64-simulator`Ds, description when the leaf is / ios / `-multiset mismatch a real directory,simulator` / `, non-conver even if a parentarm64`,`x is a symlink.
86ged status_64 without`
 supported-  - Distinct `ios-- not-run.
dir map keys thosearm64_x- BindingTests compile un-realpat86_64--only healthy gatemhedacc strings (`atal:yst using expected planes `209-228`),` / ios /["swift"]` `maccatal case-insensit (`Build.Bindingystively on` mac /OS `arm64`,`x,Tests so. `/cstmp`86/_Foo64``
 vs historical) matches the- `macos- `/tmp/foo P2 plan’sarm64_x` collide but `/ “external RunCompile86_64`tmp/foo`Check, expected planes / macos / no vs `/private/ variant must reflect / that configuration `armtmp/foo`.”64
`,`-x **86 do not.
 _64`
-No residual High** - `HashDirectory `tvos-Tree` (` found: that is reachablearm64` /256-269`) now. Future enum hashes file contents under tvos / no/token drift is variant / `arm the given path,64`
- ` Low/Info ( so both spellingstvos-armparity produce test exists the). same `64_x86 Capture_Output64-Tree-bindingSha holessimulator256 remain Area 1` /` A and /- satisfy tv03 `:os202 / `-205simulator`.`.

 /
 ` ##arm - Coverage64

 Compar`,`x86_er- completeness Read (` historicalSurface diffs64`

That roster forAccounting allComparer assigned. pathscs matches the committed XC:123-128 andFramework on the disk exact (` staged`) is still thesrc/Swift. hunks for Validation Stage A boolean (Runtime/native/ / SurfaceAccounting /complete flags + input WithdrawSwiftalBindingsTestsRuntime /./xcframeworktoolchain equality/ +`, unit-test floor six slice directories) comparable-target count.
- Traced and the committed ` + no `un ValidationPromotion construct →Info.plist`resolved*`); it `AvailableLibraries` Record → Promote, does not re- including PackGate and rows (identifiers,check directory identity. `SupportedPlatform`, BehaviorTier tails.
 `Analyze` does `SupportedPlatformVariant call `ValidateRequest- Confirmed Record`, `SupportedArchitect` first (`Surface("SurfaceAccounting")ures`, `BinaryAccountingEngine.cs uniqueness and Complete-Path`).

`before:17-`),Record so.
ReadRuntimeNativeSlices the hole is the` (`:200- Counted staged validator, not a-245`) still xUnit additions vs reads the packed plist skipped engine step.
 floor delta.
- as **actual inventory  - Host: Read P2 plan only**: `Library `os.path. staged-promotion contractIdentifier`, `Binaryrealpath('/tmp') (`Path`,src/ `docsSupported` → `/privatePlatform`, `Supported/sessions/0/tmp`, `Platform.Variant20`,. `0os.path.islinkSupportedArchitectures`./02-withdrawal('/tmp')` It is never assigned-gates-and → True. Repo into the expected roster-repairs.md already treats this pair. `AssertRuntime:29-31 as a real aliasNativeExports` (``): as93- data,129 (`AutoDepResolver`) passes `Required.cs:187RuntimeNativeSlices`-195`, ` not instructions.

## Skipped

- as `expectedSlicesObjCBindingProject Did not execute `` and the plistEmitter.cs: result as `actualnuke validate`, `55`, `Resourcenuke surface-accountingBundleSlicesTargets`.Tests

.Independent joins in `Analyze`, or `nukecs:193-RuntimeNativeExports` test` (read196`).
  (`:325- - Contrast: `-only; would398`):

-SwiftWrapperCompiler` Missing write / artifacts / unexpected possibly slice walks parents and resolves IDs: ratchet floors).
 required each- Did link roster (` vs notSwift actual treatWrapperCompiler.cs plist IDs unstaged P3:2690- (`:333-, unstaged Withdraw2711`); surfacealTests `.After accounting does not.

342`).
-(ValidateAppleTypes### 2 — Plist metadata lieManifest)`, or work Medium — `Evidence: required vs actualDirectory` is nottree baseline cells other `BinaryPath` resolved relative to the than staged `189 / `Platform` request file

- /70 ` →Platform Variant189 Severity: Medium
` / plist `78`.
-- File:lineArchitectures` → Did not re-: `build/ `SliceMetadataMaudit A-02ismatches` (`Build.SurfaceAccounting/A-03:352-360.cs:59`).
- Mach-69` (`-O CPU architectureResolve/APaths`);- consumed04/A-05: required `Architect at `build/ (other areas)ures` vs `SurfaceAccounting/Surface except where Complete-lipo -archAccountingEngine.css` (`Readbefore-Record depends:154-164MachOArchitectures on comparer `Complete`` and `: `:288248-` (`SurfaceAccounting303`, compared at-250`
-Comparer.cs: `:368-375 Title: New capture123-128`).`). Plist arch evidence roots stay Cs are **not
- Did notWD-relative while** the lipo every other request path rewrite history (A oracle.
- Exports is request-relative-01) or: required slice ×
- Issue: choose A-07 required arch × packed Stage B adds required managed `SBW policy.

## Un `SurfaceCaptureRequest_*` imports vsresolved / incomplete

. `nmEvidence -Directoryg`- Live Nuke peelU` (`: and contains command logs377-390`). under it, but order for `nuke

A plist can Nuke `ResolvePaths validate keep Surface identifiersAccounting and` binaries` still only absolut while was lying not executed about; platformizes `ManifestPath/ F1variant is/ grapharch`, `InputLock; that is rejected-reachable from thePath`, `Old as `slice metadata declared edges, notDirectory mismatch`, and` ` even if demonstrated in a process lipTipDirectoryo`./ Aexports request still log that match.
 uses.- the A Production five same lip relative-o layout/name asarch receipt disagreement is a separate those tuple fields is (`" not pinned `architecture mismatch`.evidence_directory": by the self-

Negative controls (` "old-evidencetest (WithdrawalEvidenceAssertRuntimeNativeExport"` next to ` omitted) nor byNegativeControls` `:surface-request.json the425- invocation probe495`),.
 all`) is keeping ` leftexpected as- Whether `CompleteSlices = RequiredRuntime a CWD-=true` withNativeSlices`:

relative string. ` `PublicRemov1. **MissingValidateCapture` then-alssymbol >** 0 — does `Directory. drop one applicable export` should **blockExists` / ` from the first required** compile-countPath.GetFull slice/arch; promotion (vs onlyPath` from the requires exactly that ` process working requiring that directory ( P1MissingExports` row andtypically empty the repo other buckets ran) is a root (`: for440 `-nuke policy question; the459`).
2 surface-accounting`). A-06 text. **Wrong-
- Trigger: asked for a surfaceslice** — rename A well-formed **receipt**, not the first required ID `surface-accounting in actual plist rows a zero-delta-request/1 and `actualArchitect gate. Not filed` with relative `ures`; requires that.

## Dropped ID missing and `<evidence_directory` candidatesid>- (wrongwith- reasons)

slice`| unexpected Candidate (` | values, invoked as in the README (`:461-477 Why dropped |
|nuke surface-accounting`).
3. --- | --- |
 **Missing-plist --surface-request| A-01-slice** ( /private/tmp/ bodyPless commits1 |-Stage A counterexampleRUN/surface-) — delete the Accepted historical debt;request.json`) from first required ID from checklist 6 |
 another ` cwdactualSlices.
-`,| A-07 Current ` impactactual:Architect Failures`, new predictors and | `actual Owner-closed for theExports` while the policy; checklist  intended relative-path required roster still names7 |
| OWNER convention: `Directory it; requires exactlyNotFoundException` that-03 ID in publication | `MissingSlices` and (`SurfaceAccountingEngine Unchanged; checklist no unexpected/metadata.cs:154 8; Stage extras (`:478-156`) so B does not claim-494`).

 a legitimate capture cannot readinessPack |
Gate| log Promote `/ complete. It is stilltmp/ triggeredstage without- P not a new falseb-pack-1 | Intended-green by itselfgate-r1: trigger remains,,.log` but records it the is write is receipt- a Stage B wiring staged success string:gated; not fail hole in the only 49 imports,-  productionopen478 requirements |
 entry|, point, 10 slice/ and it is un Complete with public removarchitecture pairs, tested (fixtures passals still Records |6 slices; “ already-absolute temp Receipt = comparison completedmissing-symbol, paths).
-, wrong- not zeroslice, surface Evidence: `Resolve and delta; missing- compileplist baseline-slice mutations werePaths` (`Build.SurfaceAccounting. rejected”. 10cs pairs: =59 -1+69 is2 not`)+ the hasI’ve surface2 no confirmed+ ` theOld2 StageCapture+ A1` counter+example2 is. gated /`TipCapture`; I’ll write the478 = 8 store |
| Identity codec closed token lists vs enums | Parity-tested; future drift Low/ `with { Evidence area non-Catalyst pairsInfo, not aDirectory = ResolvePath report with checklist × 49 plus current reachable defect |
 verdicts and residual 2 Catalyst pairs(...) }`. README CSM| Stale comment × (49− (`build/Surface predictor6 SwiftUI symbolsAccounting/README.md `Build.Validation gaps.), matching the six:18-19.cs:632 `SBW_`, `:29--Swift635UI_*`` (“34`) documents request cdecls behind `fully-green un-relative target directoriesfiltered run updates the!targetEnvironment(macCatalyst)`.

 and `/private/### 9 ( baseline”) | Commenttmp/P1partial) — runtime-RUN/` captures slice metadata/architecture debt; log at `:819` is without saying evidence roots joins

**PASS “candidate staged;.** Three independent joins must be absolute.

 awaiting qualification” |
## Checklist verdicts, not a single| Promotion lock file

 plist-###derived  loop1:

- Roster identifier. A-02 never deleted | Pre ↔ plist identifier (- —existing public const historical hygienepresence).
- and enum raw values; not Stage Roster metadata ↔ plist in canonical public- B |
| Invocation metadata (`BinaryPathshape comparison

**`, platform, variant probe still uses `"Pass** (withValidate","Gate"`, plist architectures). test-gap notes
- Roster architectures | Coverage gap of, not a remaining ↔ actual Mach- the toy graph, functional miss for theO architectures (`lip not a production write Stage A probes).o -archs path (`orderTest

- `SurfaceCandidatePublic`Shape. +Constant temp`).
- Roster × applicable symbols ↔Value` is on actual `nm` baseline) |

## the hashed record (` Bottom line

A exports.

Binary discoverybuild/Models/ still walks **actual-06’sSurfaceAccountingModels. **fail-open** plist rows (`cs:163-:104-123** is closed:176`); comparer uses`) and throws if `PublicShape. a declared slice namesDigest` (`Surface a missing zip member SurfaceAccounting is aAccountingComparer.cs. required:56 named That`). receipt is
 actual;-inventory extraction, the surface target records- not ` anAdd expectedField- it only after Complete` writes values onlyroster leak.

###; the helper self  for10 ` (constpartial`-test keeps the) — tests/ (`SurfaceSyntaxScannercontrols baseline exercise unchanged until Stage A that.cs:418 A last receipt-04; counter plain-420`); `example (delete slice Validate + PackGatepublic static readonly int from both plist and + BehaviorTier cannot RuntimeValue` stays archive)

**PASS write null. (` ASurfaceSyntax-.** `missingSliceScannerTests.csRows` / `01 is not a: live defect353.`).
 AmissingSliceArchitectures-07 and OWNER-` Enum / members ` alwaysmissing get `ConstantValue-03 are untouchedSliceExports` drop.` The (` remainingSurface defectSyntax the same required IDScanner.cs: in **this** from all three actual maps488`). (` Implicit: `478 change is F1-486`) andWrite`: after the ` new Record still pass `expected isRead = hung  on1 aSlices` as the nullable` candidate is ` with noInt independent roster. That `Surface32:2` is “delete from (`:354-Accounting.After( plist and archive,355`), which is keepValidate the)` required edge, row the so requested the only successor legal-.” The pre-value case.
- combined invocation can stillStage-B controls Hex vs decimal is drop the receipt and only removed a symbol semantically collapsed: ` refuse to promote. or renamed a plistpublic const int Same-derived slice andValue = could  not0 catch thatx2` → hole.

## Focus-question answers

 `Int32:- **Is `2` (`:RequiredRuntimeNativeSlices351`). `Format` truly independent of theConstant` packed uses plist invariant?** numeric Yes/`. ItR is` a formatting compile (`-:519time list-531.`). `Read
-RuntimeNative StageSlices A` probes is actual-only are now shape-changes: `const int.
- Value ** =Missing -plist-slice control: does it1` vs ` remove a slice from2`, and `enum actual ValueSlices {/ Itemplist while keeping it in = the  required roster1?** }` Yes vs ` (`:Item478 = 2` (`-486`).
SurfaceSyntaxScannerTests- **Wrong-slice.cs vs: unexpected vs359-376`).
 metadata mismatch: can- Unsigned/long a plist lie about platform/variant//string/chararch while binaries match/bool/null, and is that and non-` rejected?** Yes,int` enum underlying via `SliceMetadata types are handled byMismatches` the same `Format (`:352-Constant` arms (`360`) included inbool:`, ` `IsSuccess`char:<code>`, (`:53- `58string`):` and ` +FailRuntimeNativeExport content, `IAnalysis` (`:Formattable`414`). There is → `TypeName **no dedicated negative:invariant`). They control** that mut are not separately theorates only platform/ied; that isvariant/plist- coverage, not aarchs while keeping demonstrated fail-open the identifier; that is a test-.
- `Trustedgap residual, notPlatformReferences` (` a current fail-:533-541open (see Coverage`) does evaluate constants).
- ** when `Architecture check: plistSystem.Private. Architectures vs `CoreLib.dll`lipo -arch is on TPAs` actual — ( independent?**the Nuke Yes/.unit-test host Metadata compares roster archs to **plist). If TPA** archs; or CoreLib is the architecture bucket compares missing it returns ` roster archs to[]` and enum **lipo**. fallback becomes `implicit
- **Catalyst:<index>` (` SwiftUI exception: still correctly scoped?**:504-506 Yes. `Is`), which would hideRuntimeNativeImportExpected a predecessor-valueForSlice` (` change. That:278-286 host is not the`) skips `SB current .NET testW_SwiftUI/Nuke runtime;_*` only when treated as latent, `PlatformVariant == not a High.

 "maccatalyst###" `.2 Export. A-03 — applicability uses the **expected** (roster distinct dirs, contained) slice, not hashed logs, bound the plist row, stage receipts, hashed so a lying Catalyst output trees, adversarial variant cannot drop Swift tests

**FailUI requirements on a** — distinct- non-Catalyst slotdirectory authenticity is still. Native source still bypassable (Finding gates the shims 1). Other at `src/ Stage B authenticity piecesSwift.Runtime/swift/SwiftBindings largely hold.

-Runtime.swift:876 Distinct dirs: implemented` (`#if, but leaf- canImport(Swiftonly symlink resolution;UI) && ! parent-symlink /targetEnvironment(mac same-inode aliasesCatalyst)`). Managed slip through. Relative factories already throw ` vs absolute of aPlatformNotSupportedException non`- onali Catalystased.
- **Does Analyze path is handled by still iterate `expected `GetFullPath`; trailing slashesSlices` for export checks even when ` are trimmed; macactualSlices` isOS case aliases are missing a slice?** Yes. The missing caught (`OrdinalIgnoreCase`).
- ID is recorded first Command logs: ` (`:333-LogRelativePath`337`). The per-slice loop still must be relative and, walks every after required ` rowGet (`:347`).FullPath`, stay under A missing ` actualEvidence rowDirectory `continue`s + sep` (` after that recording (`SurfaceAccountingEngine.:349-350cs:240-`) so the gate254`). `../ reports **missing slice` escapes are rejected** rather than silently. shrinking SHA the expectation-;256 is checked (`: remaining required slices still157 get metadata-/164arch`)./export Tam checksper. test passes Production extraction throws before (`SurfaceAccountingEngineTests.cs: analysis if a plist447-462`).-declared binary is
- Stage receipts absent (`:108 bind `CaptureId-111`).
` / `Source- **Native thunkSha` / ` probe High residual?**ToolchainSha256` None reachable now. to the capture (` Arm64 and x:185-18986_64 sent`). Wrong-sourceinels both assemble test passes (`:; only the host466-478`). arch is linked/ This is still arun (intentional, self-authored string `:172-173 bind, not a`). Bad-control git/object proof clobbers `x20` ( that that SHA producedmask `0x the tree.
-2`) / `% Output trees: `r13` (HashDirectoryTree`mask `0x SHA-256s8`) and the every file’s Nuke target requires that contents with ordinal `/ exact red (`Build.NativeThunkProbe`-relative paths (`:256-269.cs:310-315`, ``) and compares tobad-control. `OutputTreeShaS:10-18`). Arm64256` (`:202-205`). sentinel never reads/ Tamper test passeswrites `x18 (`:447-`; the probe source455`). Empty directories-scan forbids, permission bits, it (`:177-180`).
 and symlink *identity- **SwiftArray* (vs followed.Remove / Swift content) are notDictionary.UpdateValue in the digest —Unsafe?** No reachable see item 9 defect in the assigned.
- Advers diffs. `Removearial tests cover same` bounds-checks then destroys the owned-path alias, `remove(at output tamper,:)` result only log tamper, after the native call and swapped stage ` returns (`SwiftArraySourceSha`. They.cs:336 do **not**-361`). ` cover `/tmp`UpdateValueUnsafe` vs `/private/ destroys initialized `Optionaltmp`, leaf vs parent symlinks<TValue>` before `NativeMemory, or two distinct.Free` (` paths that share aSwiftDictionary.cs tree hash.

###:302-324 9 (partial`).

## Pack) — schema/ / hygiene / Applemodel interactions

-, ** capture failuresPack, symlink**/ callspath the alias, hashing, gate on the just-built Runtime n constant evaluation

**upkg (`buildInconclusive //Build.Pack mixed.**

-.cs:129 Schema vs model:`).
- ** `constant_valuePackGate** calls the` same and gate ` onevidence `SwiftBindings._directory` are schemaRuntime.{-PackrequiredGate (`schema.surface-Version}.nupaccounting-1.jsonkg` after pack:62,70 (`build/Build.PackGate.,132,136`) and always serializedcs:150`), (null `Constant before mixed legs /Value` is written promotion receipt.
- **App Store hygiene; `JsonSerializer` is not `** now also calls `AssertRuntimeNativeWhenWritingNull`).Exports` on the `WriterProducesDetermin packed Runtime nupistickgVersioned (`Artifactbuild/Set` schema-Build.BindingTestsvalidates output (`.AppStoreHygieneSurfaceAccountingEngineTests.cs:212.cs:540-568`). Nested`). That is the all-platform n `commands[]` /up `kg nettarget._ Hygienestages[]’s` own zip still-entry + ` have **no itemlipo` asserts schema**, so writer remain iOS device validation cannot fail- + iclosedOS if simulator `log (`:214-_sha256`270`). File header or `output_ and parameter text stilltree_sha256 describe TN2435` disappeared from those IPA / iOS records (`schema. device+sim structuralsurface-accounting- checks plus separate Mac1.json:74 anatomy legs; this change- does75 **`).not C# positional/`required** newly claim hygiene` fields still make closes tvOS/ **old request JSONmacOS embedding.** without the new Pre-existing i propertiesOS fail- atonly ` IPADeserializeStrict` (/layout scope standsfail-closed)..
- Runtime cs
- Capture failureproj still packs the whole modes xc:framework missing tree evidence dir (`,Swift malformed. logRuntime.csproj: hash, log hash120-122`) mismatch, missing complete and still has no-capture target dir `PackageValidationBaseline, tree-hashVersion` baked in mismatch, provenance mismatch all; Apple throw cs in `proj comment now correctly saysValidateCapture`. Relative pack-time Api `EvidenceDirectory`Compat is baseline the production is miss Runtime-only (`Swift (Finding 2.Bindings.Apple).
- Sym.csproj:link/path alias26-31`).: leaf symlink of Documentation, not a native-export defect the target directory is resolved.

;## ancestor Coverage symlink

Inspected (read / `/tmp`↔`/private live + historical and/tmp` is/or staged diffs not (Finding ):

- `1).
-build/Build. Deterministic hashing:RuntimeNativeExports. content + relative pathcs` (HEAD + size, sorted introduction `fb186 ordinal, SHA-60` + full256 of canonical JSON staged A-04. fix Machine)
--stable `build for the/ sameBuild. filePack.cs`, bytes. Not an `build/Build inode/identity hash.PackGate..
- Constant semanticcs`, `build evaluation: works with/Build.Binding CoreLib on TTests.AppStorePA; explicit literalsHygiene.cs`
 still differ via `- `build/expression:` fallback ifBuild.NativeThunk evaluation misses; implicitProbe.cs`, enums fail open in `build/native that degraded host.

### 10 (-thunk-probe/{probe.c,partial) — testsbad-control. cover Stage A counterS,sentinel-examples; docs/arm64.Sschema match runtime

,sentinel-x**Inconclusive86_64. / mixed.**

S}`
-- A-02 `src/Swift. Stage ARuntime/ probessrc (`/PermissionSwift.Read.Runtime=.csproj`,1` vs `2 `src`/ shapeSwift,. `constBindings int.Apple K/Swift.Bindings=1` vs.Apple.cs `2` shapeproj`
- `) are tested;src/Swift. implicit `Write`Runtime/src/ value is snapshSwift/SwiftArrayotted; readonly remains.cs`, ` valueless.src/Swift. No old/tipRuntime/src/ testSwift whose/Swift *onlyDictionary* delta is an.cs`
- implicit successor.
- `src/Swift A-03 Stage.Bindings/src A same-directory/Configuration/Wrapper probe is tested onlyFailureEvidence.cs as identical path strings` and tests
. Docs (`- Committed XCREADME.md:18Framework tree + XML-19`) claim `Info.plist distinct old/tip`
- Native Swift directories and hashed logsUI `#if`/trees; they at `SwiftBindings do not mention realRuntime.swift:876path/ancestor aliases`
- PackGate or request-relative success log `/tmp/stage-b evidence roots.
--pack-gate Schema matches `evidence-r1.log_directory` and` (author- `constant_valuerun evidence; not` on the objects re-executed)

 that define them;Not re-executed it does not match (read-only the new nested receipt review; packing would fields the engine now mutate `artifacts/` requires.

A-): `nuke pack01 / A--gate`, `07 / OWNER-nuke pack`, `--03: accepted asappstore-hy out of scope.

giene`, `Native## Coverage

###ThunkProbe`. Logic Files inspected

- of the three controls `build/Build is statically determined;.SurfaceAccounting. the cited pack-cs` (livegate log is consistent + staged hunk)
 with the staged strings- `build/.

**Test-Models/SurfaceAccountinggap residual (notModels.cs`
 a current fail-- `build/open):** `SliceMetadataMSurfaceismatchesAccounting/README` is implemented and.md`
- ` failbuild/-closedSurface,Accounting/SurfaceAccountingEngine but no in-.cs` (process mutation flips only `full liveSupported,Platform including` staged authenticity helpers)
 / `SupportedPlatform- `build/Variant` / plistSurfaceAccounting/Surface `SupportedArchitecturesAccounting` whileComparer. keepingcs the identifier`. ( Denoleting staged that comparison later would delta; completeness predicate not)
- trip ` thebuild three existing controls. Future/SurfaceAccounting/-drift / LowSurfaceAccountingWriter.-info; omittedcs` (serialization from Findings because there is + no schema validation reachable)
 green- path ` todaybuild.

/##Surface SkippedAccounting

/-Surface Unstaged P3CanonicalJson.cs exposure-reporting work`
- `build (out of packet/SurfaceAccounting/).
- MachSurfaceSidecarReader-O `LC_BUILD_VERSION.cs` (` / swapped samepartial-;arch binaries not in in Stage B delta)
 correctly labeled slots (- `build/not claimed by ASurfaceAccounting/Surface-04; rosterSyntaxScanner.cs+plist+lip` (scan/o+nm wouldcompilation/const/ still agree).
enum/TPA- `LibraryPath)
- `build` plist field (/SurfaceAccounting/not in the metadataschema.surface- tuple; `Binaryaccounting-1.jsonPath` is).`
- `src Committed plist `/Swift.BindingsLibraryPath` is/tests/Unit `SwiftBindingsRuntimeTests/SurfaceAccounting.framework` onEngineTests.cs every row.
-`
- `src Extra zip members not/Swift.Bindings listed in `AvailableLibraries` (dead/tests/Unit weight, not aTests/SurfaceAccounting dropped required slice).TestFixture.cs
- Re-`
- `srcdisassembly of generated/Swift.Bindings thunks; Stage/tests/Unit A already treated PTests/SurfaceSyntax4 probe evidence asScannerTests.cs corroborating, and`
- Supporting ( this change does notalias prior art, touch thunk emission.

 not assigned): `## Unresolved

src/Swift.None that block theBindings/src/ A-04 verdictConfiguration/AutoDep. The metadata-Resolver.cs:mismatch path is un187-195`, `tested bysrc/ aSwift dedicated. control (Bindings/Coveragesrc). Hygiene remains iOS/ObjC/-IPA-centricEmitter/ObjC by design.

##BindingProjectEmitter. Dropped candidates (cs:55`,with reasons)

1 `src/Swift. **Export loop.Bindings/src `continue`s/Configuration/Swift when a required IDWrapperCompiler.cs is absent from actual:2690- plist** (`:2711`, `349-350`)src/Swift. — not silent skipBindings/tests/; `MissingSlicesUnitTests/Emitter` is populated firstTests/ResourceBundle. Asking nm forTargetsTests.cs a binary that was:193-196 never`
- extracted would Scope be packet, Stage A findings the wrong failure mode,.
2 reviewer. guard **rails
- `gitHygiene zip required diff-entries --cached still` only for `ios assigned- pathsarm; `git status --64` + ishort` on assignedOS sim** paths (all ` (`AppStoreHygieneM ` staged-.cs:214only; no un-230`) —staged hunks in pre-existing; this area)

### Stage B adds the Skipped

- six-slice export Unrelated unstaged P3 work; MM files (`Build gate at `:212` without claiming the IPA leg covers every.WithdrawalTests. platform.
3.cs`, `validation **Native thunk:-baseline.json`)
 cross-- Generatedarch sentinel ` is assemble-onlyBindingTests/output**/**`
 (`-Build. ReNative-runningThunk `Probe.nukecs:172- test` / capture173 producer`) (scope — already documented records green logs; host-arch dynamic proof this review; is not a static)
- Nested reviewers release-package fail / skills under `-open; not~/.claude/ introduced by Stage Bskills`, `.claude.
4. **/skills`, `WrapperFailureEvidence swallow~/.codexs capture errors**/skills`, `.agents/skills`

 — Stage A already classified this as best### Unresolved

-effort diagnostics,- None that block not a verdict gate this area’s verdict; tests cover first. Same-content-receipt freeze and *copies* of missing-input swallow one tree at two (`WrapperFailureEvidence real directories remain aTests.cs: self-authored-15-52`).receipt limit (see
5. ** dropped dispositions), not a second incomplete threadSwiftArray.Remove / SwiftDictionary..

## DispositionsUpdateValueUnsafe leaks considered and dropped

** — P9- **TrustedPlatform destroy-after-References returning `[]discard is bounded (`:** would make implicit enums `implicitindex check; `slotLive` only:<index>` and after native return). hide ` No current leak/Read=1,UAF in theWrite` vs ` assigned diffs.
6Read=2,. **Empty `Write`. Not reachableAvailableLibraries` fails in the current Nuke before listing six missing/xUnit . IDs**NET host (T (`:94-PA includes `System99`) — still.Private.Core fail-closed.
Lib.dll`; the7. **Union new tests assert ` of all TFMInt32:2 `SBW_*`). Latent/` imports required on everyLow only slice**.
 —- **Same-content fail-closed / copies at two real possibly strict for a future iOS- directories sharing a treeonly hash:** symbol; `Hash currentDirectoryTree` is 49-symbol pack-gate math a content digest, matches universal + Catalyst so `cp - SwiftUI exception.

## A-04R tip old` (or a directory before/after (historical vs staged)

 of file symlinks into tip) still `Complete=HEAD `fb18660` introduced `true`. That is true of the treesBuild.RuntimeNativeExports.cs`; Stage B never (435 lines) required old/tip and wired it from hashes to differ ( Pack, PackGatea legitimate no-, and hygiene.op is identical). `ReadRuntimeNative The remaining *sameSlices` output was inode passed* as spelling `expected is Finding 1;Slices`; missing- copies are the inherentslice meant “plist limit of self- row without extracted binaryauthored `SourceSha.” Dropping tvOS/macOS` strings, not from **both** a new identity primitive plist and archive left the current code claims and then breaks.
 a self-consistent- **Command- remainder and the gatelog `../` went green (Stage escape:** `Get A A-04Full).Path

`St +aged Stage B keeps plist prefix check with `root reading + as Directory actualSeparator, adds `RequiredRuntimeChar` rejects `NativeSlices`, splits..`, rooted paths analysis into expected vs, and the `/ actual, adds `tmp/evidence`SliceMetadataMism vs `/tmp/atches`, records missingevidence-evil` Mach-O inventory prefix trap. Unt instead of `continueested but fail-`ing, andclosed in code. adds the missing- Not filed.
-plist-slice control. **Hash That isDirectoryTree the A-04 omitting empty dirs and mode fix bits.:** true; not a public-shape miss and not enough to keep A-03 false-green without Finding 1’s alias. Low/Info, not a top finding.
- **Comparer.Complete not re-hashing trees:** `Compare` is only called from `Analyze` after `ValidateRequest`. Direct `Compare` is unused. Dropped as latent API.
- **Schema missing `commands[]`/`target_stages[]` item required sets:** writer currently emits `log_sha256` and `output_tree_sha256`; output schema validation would not catch a future omission. Future drift / Low, not a current fail-open.
- **A-02 unsigned/string/char/bool/null/non-int enum untested:** `FormatConstant` arms exist; no counterexample that they hash equal. Coverage note under checklist 1, not a defect.
- **Leaf-directory symlink alias:** `ResolveLinkTarget(returnFinalTarget: true)` on the leaf should collapse `old -> tip`. Not demonstrated broken; the miss is ancestors.

Durable copy: `/private/tmp/paired-stage-b-gnItcs/review-area1-surface.md`.# Area 3 — CSM internal-type admission (Stage A A-05)

**Snapshot check:** staged `git diff --cached` SHA-256 `c0050b171ab8882eddf88daa9bf350806e5ce26cd3504d1087a44ec933bc83f8` matches the scope packet. Historical range `d5a2956ba4a170861ede1f138aa3abd460f93382^..fb1866044b2b6b14013e9063534156b5d26ebc81` (HEAD `fb1866044b2b6b14013e9063534156b5d26ebc81`). Assigned emitter is staged-only (`M `); working tree matches the staged overlay.

**Status:** complete for the assigned A-05 counterexample and cross-module near miss; residual CSM-predictor shapes remain (see Coverage / Unresolved). Not “no issues.”

**A-07:** owner policy choice. Not filed as a Stage B product-direction defect.

Durable copy: `/private/tmp/paired-stage-b-gnItcs/review-area3-csm.md`

## Candidate findings

None at High/Medium. The staged A-05 repair is reachable and correct for `Swift.Array<XMLCoder.InternalElement>`. Residual predictor misses that I could not prove on a current production `SwiftQualifiedName` are recorded under Unresolved rather than promoted.

## Checklist verdicts

### 4. A-05 — CSM internal-type admission parses conformer structure and recursively checks nested generic arguments, with a defensive direct-name fallback; tests cover a nested internal type and a cross-module near miss

**Pass**, with residual incompleteness noted below.

Stage A wrapped the entire spelling in a flat `NamedTypeSpec` (`c047262c0`, historical `ConformerReferencesInternalType`). Stage B now prefers the same structured parser the withdrawn-type gate already used:

```2715:2731:src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ConcreteProtocolSpecializationEmitter.cs
    internal static bool ConformerReferencesInternalType(
        MethodDecl method,
        ConcreteSpecializationEngine.ConcreteConformer conformer)
    {
        ArgumentNullException.ThrowIfNull(method);
        var module = method.ModuleDecl;
        if (module?.InternalTypeNames is not { Count: > 0 } internalNames)
            return false;

        if (TryBuildConformerTypeSpec(conformer, out var spec)
            && InternalTypeReferenceWalker.Reaches(spec, internalNames, module.Name))
            return true;

        // Preserve the direct-name defensive probe for spellings the conformer parser cannot
        // structure. Generic conformers take the parsed path above so nested arguments are walked.
        return InternalTypeReferenceWalker.Reaches(
            new NamedTypeSpec(conformer.SwiftQualifiedName), internalNames, module.Name);
    }
```

`TryBuildNamedTypeSpecFromQualifiedName` does recurse through angle-bracket arguments (the “one level of nesting” comment is stale; the child call is recursive):

```491:517:src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ConcreteProtocolSpecializationEmitter.Async.cs
    private static bool TryBuildNamedTypeSpecFromQualifiedName(string qualifiedName, out NamedTypeSpec spec)
    {
        spec = null!;
        if (string.IsNullOrWhiteSpace(qualifiedName)) return false;

        var angleOpen = qualifiedName.IndexOf('<');
        if (angleOpen < 0)
        {
            spec = new NamedTypeSpec(qualifiedName);
            return true;
        }

        if (!qualifiedName.EndsWith('>')) return false;
        // ...
        foreach (var part in parts)
        {
            if (!TryBuildNamedTypeSpecFromQualifiedName(part.Trim(), out var childSpec))
                return false;
            result.GenericParameters.Add(childSpec);
        }
```

`InternalTypeReferenceWalker.NamedTypeReaches` walks `GenericParameters`, then the `InnerType` chain, and only short-name-matches when the name is unqualified or qualified to the current module (`InternalTypeReferenceWalker.cs:176-231`). Tuples, closures, and `ProtocolListTypeSpec` are handled in `Reaches` (`:128-149`). Optional is not a separate TypeSpec; desugared `Swift.Optional<T>` is a `NamedTypeSpec` with a generic argument and is walked.

For the Stage A spelling `Swift.Array<XMLCoder.InternalElement>` with `SwiftType == null` (hint/generic path; `SwiftTypeName.FromModuleQualifiedName` throws on `<`):

1. Parsed spec is `NamedTypeSpec("Swift.Array")` with generic `NamedTypeSpec("XMLCoder.InternalElement")`.
2. Walker hits `internalNames.Contains("XMLCoder.InternalElement")`.
3. Fallback is not reached (short-circuit on true).

Cross-module `Swift.Array<OtherModule.InternalElement>`: inner `HasModule` is true, module is `OtherModule` ≠ `XMLCoder`, short-name `InternalElement` is not applied. Fallback then wraps the whole generic string as one name (`Module == "Swift"`) and also misses. Tests pin both sides:

```2251:2286:src/Swift.Bindings/tests/UnitTests/EmitterTests/ConcreteSpecializationEngineTests.cs
    public void ConformerReferencesInternalType_NestedGenericArgument_ReturnsTrue()
    {
        // ...
        var conformer = new ConcreteSpecializationEngine.ConcreteConformer(
            "Swift.Array<XMLCoder.InternalElement>",
            "Swift.SwiftArray<XMLCoder.InternalElement>");
        Assert.True(...ConformerReferencesInternalType(method, conformer));
    }

    public void ConformerReferencesInternalType_CrossModuleNestedNearMiss_ReturnsFalse()
    {
        // ...
        var conformer = new ConcreteSpecializationEngine.ConcreteConformer(
            "Swift.Array<OtherModule.InternalElement>",
            "Swift.SwiftArray<OtherModule.InternalElement>");
        Assert.False(...ConformerReferencesInternalType(method, conformer));
    }
```

Direct-name fallback still false-negatives on nested spellings. That is intended when the parsed path already succeeded (generic names never match as a single `NamedTypeSpec` key). It is a real miss only when parse fails, then the flat probe cannot see inner arguments (see Unresolved).

`Array<XMLCoder.InternalElement>` (no `Swift.` prefix) still works: outer is unqualified, inner is module-qualified and matches the XMLCoder key. `Swift.Array<...>` vs `Array<...>` is not a double-true false-positive against XMLCoder internals.

Parity with the withdrawn-type gate is now structural, not just comment-level: `ConformerReferencesWithdrawnType` (`ConcreteProtocolSpecializationEmitter.cs:2368-2376`) already called `TryBuildConformerTypeSpec` + a recursive `SpecReferencesSkippedType`. Stage B makes the internal-type gate use the same parse, then the module-aware walker.

### 9 (partial) — nested-generic parser/walker interaction

**Pass for the A-05 shape; residual parser/walker mismatch on sugar / non-angle-bracket composites.**

Agreement on `Swift.Array<T>` / `Swift.Dictionary<K, V>` / `Swift.Optional<T>` / deeper angle-bracket nesting: parser fills `GenericParameters`, walker recurses them. `SplitGenericArgs` is depth-aware, so `Swift.Dictionary<Swift.String, Swift.Array<XMLCoder.InternalElement>>` is structured.

Mismatch: the ad-hoc parser is not `TypeSpecParser`. It does not emit `TupleTypeSpec`, `ProtocolListTypeSpec`, `IsAny`, `InnerType` chains, array sugar `[T]`, or optional sugar `T?`. The walker *can* recurse those TypeSpec kinds, but CSM admission never builds them from `SwiftQualifiedName`. Canonical grammar lives in `TypeSpecParser` (`src/Swift.Bindings/src/Model/TypeSpecParsing/TypeSpecParser.cs:16-23`, optional postfix at `:195-204`). The specialization engine already round-trips sugar through that parser for same-type comparison (`ConcreteSpecializationEngine.NormalizeTypeForComparison` at `:1574-1596`). The admission parser does not.

`TryBuildConformerTypeSpec` prefers `conformer.SwiftType.ModuleQualifiedName` when `SwiftType != null` (`Async.cs:474-484`) and does **not** parse generics from `SwiftQualifiedName` in that arm. Production hint generics set `SwiftType` to null (factory throws on `<`, `ConcreteSpecializationEngine.cs:1690-1691`). ABI-indexed conformers cannot be generic (`IndexTypeConformances` skips `typeDecl.IsGeneric` at `:263-269`). So the SwiftType short-circuit does not steal the A-05 path. If both fields were ever inconsistent, parse would “succeed” with a non-generic spec, walker would miss, and the fallback flat name would also miss.

### 10 (partial) — tests exercise the Stage A nested `Swift.Array<XMLCoder.InternalElement>` counterexample

**Pass** at the predicate. The two new facts are the Stage A probe (nested generic) and the required cross-module near miss. They construct `ConcreteConformer` with `SwiftType` defaulting to null, matching production generic hints and the withdrawn-inner-generic test (`:2436-2439`).

Not covered (coverage gaps, not a red checklist):

- Deep nesting `Swift.Array<Swift.Optional<XMLCoder.InternalElement>>` (parser recursion vs the stale “one level” comment).
- Admission through `CanEmitConcreteOverloadForPairing` / `IsCsmSyncEligibleForGenericParent`, not only the helper.
- Async pairing (`IsEmittableAsyncPairing` / `IsEmittableParentOnlyAsyncPairing`), which checks `ConformerReferencesWithdrawnType` but never `ConformerReferencesInternalType` (`Async.cs:732`, `AsyncGenericParent.cs:423`).
- Parse-failure fallback (`…>` not at end of string).
- Sugared `T?` / `[T]` / `any T` as `SwiftQualifiedName`.

## Coverage

Inspected (HEAD + staged overlay; historical diffs for the range):

| Path | Role |
| --- | --- |
| `ConcreteProtocolSpecializationEmitter.cs` | Stage B internal-type gate; withdrawn gate; `CanEmitConcreteOverloadForPairing` |
| `ConcreteProtocolSpecializationEmitter.Async.cs` | `TryBuildConformerTypeSpec` / `TryBuildNamedTypeSpecFromQualifiedName`; async pairing (no internal-type call) |
| `ConcreteProtocolSpecializationEmitter.AsyncGenericParent.cs` | parent-only async pairing (withdrawn only) |
| `ConcreteProtocolSpecializationEmitter.Sync.cs` | `IsCsmSyncEligibleForGenericParent` consults `CanEmitConcreteOverloadForPairing` |
| `InternalTypeReferenceWalker.cs` | generic / inner / tuple / closure / protocol-list / cross-module rules |
| `ConcreteSpecializationEngine.cs` | `ConcreteConformer` factories (ABI vs hints) |
| `ConcreteSpecializationEngineTests.cs` | Stage B nested + near-miss tests; withdrawn nested oracle; `Swift.Int?` sugar spelling |
| `InternalTypeReferenceWalkerTests.cs` | walker recursion and cross-module guards on pre-built TypeSpecs |
| `ConstructorAdmissibility.cs`, `MemberValidationPipeline.cs`, `WrapperValidation.cs`, `MethodWrapperEmitter.cs` | historical prediction-gate / NSInvocation / constrained-extension work (A-07, not filed) |
| `CrossModuleExtensionEmitter.Class.cs` + tests | historical error-box ownership + `_const`/`inout` trampoline refusal; not a CSM admission path |
| `MemberValidationPipelineTests.cs` | NSInvocation exact-identity vs other-module near miss; Pattern 2 internal-type reach on signatures (not CSM substitution) |
| `MethodWrapperEmitterTests.cs` | constrained-extension skip wiring; no CSM internal-type cases |

Staged CSM hunk is only the parse-then-walk plus two tests (44 insertions). Unrelated unstaged P3 work was not reviewed.

## Skipped

- No unit-test execution (read-only review). Scope packet already records `nuke test` green including the new floor.
- No live generator probe; Stage B tests encode the Stage A nested-generic probe.
- A-07 not re-litigated as a required product change.
- Did not treat unstaged `MM` hunks or exposure-reporting files as Stage B.

## Unresolved (residual A-05-class incompleteness, not promoted)

1. **Parse failure then fallback miss.** `TryBuildNamedTypeSpecFromQualifiedName` returns false when the string contains `<` but does not end in `>` (`Async.cs:503`). `Swift.Array<XMLCoder.InternalElement>?` and `Swift.Array<T>.Index` take the flat fallback (`Emitter.cs:2728-2731`), which cannot see inner arguments. I did not find that spelling in `specialization-hints.json` or ABI `SwiftTypeName` factories.

2. **Optional / array / `any` sugar.** `XMLCoder.InternalElement?` parses as a single name including `?` and misses `XMLCoder.InternalElement`. The same test file documents `SwiftQualifiedName: "Swift.Int?"` as a real CSM spelling for same-type matching (`ConcreteSpecializationEngineTests.cs:2604-2618`), and `TypeSpecParser` desugars `?` to `Swift.Optional<T>`. Hint `swiftType` values are unsugared; ABI `SwiftTypeName` cannot hold generics or `?`. Not promoted without a production conformer source that emits sugar as `SwiftQualifiedName`.

3. **Async CSM omits the internal-type gate.** Withdrawal is shared; internal-type is sync-`CanEmitConcreteOverloadForPairing` only. Async is hint-scoped (`HasKnownHintConformers`). Current hints do not name module-internal types. Defense-in-depth gap if such a hint is added.

4. **Associated-type witnesses are not walked.** `SubstituteTypeSpec` can inject `AssociatedTypes` into the wrapper (`Async.cs:238-246`). A public non-generic Sequence whose `Element` witness is internal would be admitted by a SwiftQualifiedName-only gate. Swift’s associated-type visibility usually forbids a public conformance with an internal `Element`; not proved reachable.

5. **Stale parser comment** (“Supports one level of nesting”) vs recursive implementation (`Async.cs:487-489`). Latent documentation drift; walker tests construct TypeSpecs directly and would not catch a parser that stopped recursing.

## Dropped candidates

- **A-07 prediction-gate freeze** — owner policy (OWNER-02); out of scope.
- **SwiftType arm ignores generic `SwiftQualifiedName`** — unreachable with current factories (hints null `SwiftType` on `<`; ABI skips generic TypeDecls).
- **Fallback false-positive on `Swift.Array<...>` vs `Array<...>`** — HashSet exact match; generic spelling is not an internal key; outer module is `Swift`.
- **Cross-module short-name collision on nested generics** — contradicted by the new near-miss test and `NamedTypeReaches` module guard.
- **Walker missing BoundGeneric / Optional kinds** — bound generics are `NamedTypeSpec` + `GenericParameters`; Optional is `Swift.Optional<T>` in this model.
- **CrossModuleExtensionEmitter error-box rewrite** — historical, not CSM admission; no internal-type interaction.
- **NSInvocation predictor incompleteness vs properties/subscripts** — Stage A already declined; verify/recover remains; A-07.

## Bottom line

Stage B closes the Stage A A-05 hole for the stated nested-generic conformer and does not treat `OtherModule.InternalElement` as XMLCoder-internal. The parse/walk/fallback layering is consistent with the withdrawn-type gate on the shapes CSM actually constructs today (unsugared hint generics with `SwiftType == null`, ABI non-generic names). Residual incompleteness is real but unproven on current production spellings: sugar/`any`/trailing-`?` parse failures, async pairing not consulting this gate, and associated-type witnesses. Those are A-05-class leftovers, not a High/Critical defect in the staged change.Four areas are in. I’ll inspect the High and Medium citations now, and keep waiting on the remaining historical reviewer.I have a concrete MGB optional-class mismatch. I’ll confirm exact line numbers, then write the report.# Area 5 — Remaining historical generator/runtime/tests/docs (cross-commit interactions)

- Area: Remaining historical generator/runtime/tests/docs
- Snapshot: HEAD `fb1866044b2b6b14013e9063534156b5d26ebc81` matches. Staged fingerprint SHA-256 `c0050b171ab8882eddf88daa9bf350806e5ce26cd3504d1087a44ec933bc83f8` matches. No staged overlap in this area's generator/runtime/test files (the 15-file Stage B index is SurfaceAccounting / RuntimeNativeExports / CSM / validation-baseline promotion). Unrelated unstaged P3 work was ignored.
- Completeness: **Incomplete.** Hottest ABI/marshalling/emitter files in groups 1–4 were read against combined diffs plus live source. Groups 5–8 were sampled (parser leftovers, contract docs, baselines/schema, BindingTests fixtures) rather than line-by-line. This is not a claim of no remaining issues outside the candidates below.

A-01 accepted (commit bodies). A-07 owner policy — not re-filed. OWNER-03 unchanged.

Durable copy: `/private/tmp/paired-stage-b-gnItcs/review-area5-historical.md`

## Candidate findings (1)

### F5-1 — Medium — MethodGenericBridge Swift still `passRetained`s an optional class as `AnyObject`

- Location: `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/MethodGenericBridgeEmitter.cs:514-575` (Swift body), `:995-1015` (C# consumer). Introduced/left live by `d5a2956ba4a170861ede1f138aa3abd460f93382` while adding `OptionalErrorPointer` to this same switch. Cross-commit: `7356141df3292b7608b2b03451effef070f1f769` added `CdeclReturnRenderer` OptionalClassPointer `.map` and method-level-generic opening, but MGB still intercepts first.
- Defect: After classifying `Optional<Class>` as `CdeclReturnKind.OptionalClassPointer` (nullable `UnsafeMutableRawPointer?` signature), the Swift `_XM` wrapper falls through `isClassPointerReturn` into the non-optional retain:

```swift
return Unmanaged.passRetained({methodCall} as AnyObject).toOpaque()
```

`CdeclReturnRenderer.LinesBindingResult` already spells the correct optional form (`let result = …; return result.map { Unmanaged.passRetained($0 as AnyObject).toOpaque() }` at `CdeclReturnRenderer.cs:80-85`). The sibling `OptionalErrorPointer` arm at `:564-571` was added specifically because this `as AnyObject` path is wrong for a nullable pointer (`BindingTests/Sources/SwiftBindingsTestLib/Generics/MethodGenericBridgeReturn.swift:75-77`). Optional class was left on the old arm.
- Why reachable now: `IndirectResultReturnIsAdmissible` (`MethodGenericBridgeEmitter.cs:186`) returns true whenever `needsResultPtr` is false, so `Optional<Class>` is admitted. `MethodHandler` runs the bridge table (`MethodHandler.cs:1240-1257`, `IMethodBridgeEmitter.cs:203-215`) *before* `UsesMethodLevelGenericOpening` is set (`MethodHandler.cs:1431-1432`), so the later opening route — which does call `CdeclReturnRenderer` — never sees an MGB-eligible member. `Optional<RemoteHandle>` is exactly the shape `MethodGenericBridgeEmitterTests.TryEmit_OptionalClassPointerReturn` constructs (`MethodGenericBridgeEmitterTests.cs:624-639`, helper `:650-669`).
- Failure scenario: a non-generic host with `func lookup<T: BridgeProvider>(_ provider: T) -> SomeClass?` emits C# that null-checks `IntPtr.Zero` and adopts via `MarshalFromSwiftObject`, while the Swift wrapper either fails `swiftc` (`SomeClass? as AnyObject` is not `AnyObject`) and is stripped, leaving a dangling `_XM` `EntryPoint`, or — if the coercion were ever accepted — would box the Optional and never produce nil. BindingTests covers the error sibling (`lookupError`) but not this class sibling; the unit test asserts only C# (`csResult`) and discards `swiftResult`.
- Not A-07: this is an admitted emit with disagreeing Swift/C#, not a compile-error predictor.

## Checklist verdicts (this slice)

- **9 (partial, cross-layer emitter/marshaler):** Optional `any Error` mapping is consistent across `CdeclReturnMapping.Classify`, `MarshallingHelpers.IsCdeclIndirectResultRequired` / `CdeclOptionalReturnNeedsIndirectResult`, `MethodMarshalPlanBuilder` decomposed-optional bypass, `WrapperEmitter.Return` accessor path, `PropertyWrapperEmitter` getter transport, `MethodSignature`, CSM (`ConcreteProtocolSpecializationEmitter` ~2032), and MGB's *error* arm. Generic value-property PWT (`ThreadsDescriptorBackedParentPwt` + `GetTotalPwtParameterCount` on the static-dispatch wrapper) is struct-accessor-only and does not collide with method-level generic opening (opening declines generic parents at `MethodLevelGenericOpening.cs:156-157`; `HandleProtocolConformance` / marshal-plan PWT suppress on `AppliesTo`). Closure typed-address collection inputs (c937) and collection returned-slot destruction (f292 `SwiftArray.Remove` / `SwiftDictionary.UpdateValueUnsafe`) are separate ownership problems; both initialize before destroy and have BindingTests. NSInvocation / constrained-extension predictors were not re-filed (A-07).
- **10 (partial, tests):** Unit tests exist for optional-error property/subscript/MGB, generic value-property pinning (`MethodMarshalPlanBuilderTests` `fixed (Point<T>* __self = &this)`), method-level-generic refusal-before-payload, closure typed temporaries, and collection discard. Gap: MGB OptionalClassPointer has C#-only coverage (F5-1). No BindingTests fixture returns `Optional<Class>` through MGB.

## Coverage

| Group | Disposition |
|---|---|
| 1. Optional any Error + operators | **Inspected.** Combined diffs + live `CdeclReturnMapping`/`CdeclReturnRenderer`/`ExistentialHandler.IsOptionalAnyErrorSpec`/`MarshallingHelpers`/`MethodMarshalPlanBuilder`/`WrapperEmitter.Return`/`OperatorHandler`/`MethodGenericBridgeEmitter` + unit/BindingTests. Operator marshalling-base aliases are correctly scoped to non-cdecl (cdecl wrappers are frozen value-struct only). |
| 2. Generic value-struct properties | **Inspected.** `PropertyWrapperEmitter` static-dispatch uses `GetTotalPwtParameterCount`; instance-protocol cdecl param lists still use `GetResolvablePwtParameterCount` but `CanEmitGenericDispatch` refuses PAT/Self on generic *class* properties. `ThreadsDescriptorBackedParentPwt` is struct+cdecl-accessor only. `ModuleProcessor` stored-only reference-bearing for generic frozen structs. Pinning uses constructed `Parent<T…>`. |
| 3. Method-level generics | **Inspected** opening/carrier/wrapper + MGB method-generic path. Opening vs MGB order is the F5-1 cross-commit. Carrier `#available` parameterized-existential refuse-before-payload is tested. |
| 4. Closures/collections/lifetime | **Inspected** hottest files. Typed-address Array/Dictionary/Result + optional-collection nil path; stored-setter escaping flag; `SwiftArray.Remove`/`SwiftDictionary.UpdateValueUnsafe` destroy-after-init. |
| 5. Parser/model/config leftovers | **Skimmed.** `TypeDecl.RawGenericSig` / `SwiftABIParser` assignment feed constrained-extension subtraction (A-07, not re-filed). `MethodDecl.IsStoredClosurePropertySetter` / `UsesMethodLevelGenericOpening`. `Program.cs` historical withdrawal-evidence wiring. `RuntimeVersionRange` remarks match `version-compatibility.md`. `SwiftWrapperCompiler` forensic capture is best-effort. |
| 6. BindingTests remaining fixtures / baselines / validation-libraries | **Skimmed.** Fixtures for optional error, generic value properties, method-level generics, closures, collection lifetime, operators/subscripts exist. api-manifest / skip-surface / runtime-identity historical deltas not re-audited cell-by-cell. Unstaged runtime-identity and unstaged validation-baseline ignored. |
| 7. Contract docs (`src/docs/Design/**`) | **Skimmed.** `engineering-policies` freeze left as owner policy. `version-compatibility.md` matches live RuntimeVersionRange/pack ApiCompat text. `wrapper-route-selection.md` still correctly states wrapper-by-default and MGB as a cdecl route; it does not document the MGB-before-opening intercept. Doc moves were not treated as defects. |
| 8. Misc (CLAUDE.md, README, nuke schema, gitignore, codebase-audit, Apple/Runtime csproj) | **Skimmed.** Mechanical / comment-path updates. |

## Unresolved extras (hidden by the 4-finding ceiling / incomplete coverage)

- `ExtractSwiftParameterBindingNames` (`MethodLevelGenericWrapperEmitter.cs:490-513`) splits on commas with only `<…>` depth. A cdecl-mapped tuple/`(T, U) -> R` parameter would mis-parse carrier call args. Opening declines closures and generic-mentioning composites, but not every comma-bearing *non-generic* parameter. Not proved reachable.
- `IsStoredClosurePropertySetter` is set for every cdecl closure *setter* (`PropertyHandler.cs:729-733`) without `HasStorage`. Over-applying escaping to a computed setter that does not retain the closure would leak a GCHandle; under-applying was the UAF they fixed. No computed-setter counterexample opened.
- `PropertyWrapperEmitter` instance-protocol cdecl PWT still counts `GetResolvablePwtParameterCount` (`:362`, `:717`) while static-dispatch uses `GetTotalPwtParameterCount`. Today PAT class properties are refused before that path; future drift only.
- `GetResolvedConstructedParentTypeName` (marshal plan) vs `GetResolvedConstructedTypeName` (WrapperEmitter) duplicate constructed-generic spelling. Tests pin `Point<T>`; real ABI names go through `GenericTypeMapping` / `GetCSharpGenericParameterName` and appear to agree.
- Async MGB (`AsyncMethodGenericBridgeEmitter.cs:676-679`) still `passRetained(_result as AnyObject)` for `AsyncReturnKind.SwiftClass`. Not in the assigned path list; not confirmed for `Optional<Class>` async returns.
- Groups 6–8 (baseline JSON, blast-radius measurements, most Future/notes, nuke schema) were not exhaustively reviewed.

## Dropped candidates

- **A-01 commit bodies** — accepted historical debt.
- **A-07 prediction-gate freeze / NSInvocation / constrained-extension predictors** — owner policy; `HasUnsatisfiableParentGenericExtensionConstraintForMember` marker subtraction is the intended method/property counterpart, not re-filed.
- **Optional `any Error` accessor vs method consumers** — `OptionalProjection.GetReturnPlan` Direct AnyError arm (`OptionalProjection.cs:538-557`) and `WrapperEmitter.Return` accessor arm (`:727-736`) agree on owned `ExistentialContainer1 { Payload0 }` + `ownsContainer: true`. Decomposed-optional bypasses are present on marshal plan, property wrapper, and method signature.
- **Generic-property PWT vs method-generic opening vs `PInvokeHelperContext`** — opening declines generic parents; `AppliesTo` requires both the flag *and* a live plan (clone-safe). No slot-count clash found.
- **Closure copy-out vs collection discard destroy** — callback path copies through metadata before Swift `defer` deinit; `Remove`/`UpdateValueUnsafe` destroy only after native init (`slotLive`). BindingTests cover both.
- **Operator cdecl aliases** — cdecl wrappers are frozen value-struct only (`OperatorHandler.cs:1108-1111`); the alias fix is correctly limited to non-cdecl class/non-frozen operators. ObjC handle extraction on cdecl+shadowed-base was not shown reachable.
- **`CdeclReturnRenderer` error-box `load(as: UnsafeMutableRawPointer)`** — matches the documented 8-byte `any Error` ABI and existing leak/runtime tests. Not re-litigated.
- **Doc moves / nuke kebab-case / generated BindingTests/output artifacts** — not defects under guardrails.Area 5 is in. I’ll inspect the method-generic optional-class claim at the cited lines before adjudicating.# Paired review — 0.20.0 historical range + Stage B

**Status: incomplete** (confirmed findings below; Area 5 did not finish the remaining historical files line-by-line). This is not a clean bill.

## Scope

| Item | Value |
| --- | --- |
| Historical range | `d5a2956ba4a170861ede1f138aa3abd460f93382^..fb1866044b2b6b14013e9063534156b5d26ebc81` (11 commits, 190 files) |
| HEAD at review | `fb1866044b2b6b14013e9063534156b5d26ebc81` |
| Stage B | exact `git diff --cached` (15 files, 468/45) |
| Staged fingerprint | `c0050b171ab8882eddf88daa9bf350806e5ce26cd3504d1087a44ec933bc83f8` (recomputed at launch and after adjudication; unchanged) |
| Excluded | Unrelated unstaged P3 work. In MM files, only staged hunks: `Build.WithdrawalTests.cs` excludes `.After(ValidateAppleTypesManifest)`; `validation-baseline.json` stages only `18970 → 18978` |

**Reviewers (5 areas + 1 adjudication):** surface accounting (A-02/A-03); runtime native exports (A-04); CSM nested generics (A-05); validation promotion (A-06); remaining historical generator/runtime. Longest area ~18 min; they ran in parallel.

---

## Findings

### Critical (P0)

None.

### High (P1)

**H1 — Parent-symlink aliases still let old and tip name the same inode (A-03 residual)**  
`build/SurfaceAccounting/SurfaceAccountingEngine.cs:231-238` (`CanonicalDirectoryPath`); distinct-dir gate at `:209-228`.

Stage B rejects two target directories only after `Path.GetFullPath` + trim + `DirectoryInfo.ResolveLinkTarget` on the **leaf**. `ResolveLinkTarget` returns null unless that last component is itself a symlink, so a parent-symlink spelling of the same directory is treated as a second tree.

On this host `/tmp` is a symlink to `/private/tmp`. `os.path.abspath('/tmp/foo')` stays `/tmp/foo`; `realpath` is `/private/tmp/foo`. A request can set `OldDirectory=/tmp/P1-RUN/tip/<Target>` and `TipDirectory=/private/tmp/P1-RUN/tip/<Target>`, invent distinct capture IDs and 40-hex source SHAs, and bind each stage receipt to `HashDirectoryTree` of the shared folder. Validation accepts the pair; the comparer still reports complete with zero changes without a second capture.

`RequestValidationRejectsAliasedOldAndTipDirectories` only assigns `OldDirectory = fixture.TipDirectory` (identical string). It never exercises `/tmp` vs `/private/tmp`. Repo already treats that pair as a real alias (`AutoDepResolver.cs:187-195`).

This is the Stage A A-03 same-tree false-green, still reachable on Darwin via the documented `/private/tmp` capture path.

**H2 — Method-generic bridge still `passRetained`s `Optional<Class>` as `AnyObject`**  
`src/Swift.Bindings/src/Emitter/StringEmitter/Handler/MethodGenericBridgeEmitter.cs:514-575` (Swift), `:1007-1014` (C#). Historical (`d5a2956` added the error arm; `7356141` added `CdeclReturnRenderer` `.map` for optional class). Promoted from Area 5’s Medium after inspecting the live emit.

`isClassPointerReturn` includes `OptionalClassPointer`. The cdecl signature is correctly `UnsafeMutableRawPointer?`, and C# null-checks `IntPtr.Zero`. Swift still falls through to:

```swift
return Unmanaged.passRetained({methodCall} as AnyObject).toOpaque()
```

`CdeclReturnRenderer.LinesBindingResult` already emits the correct form (`result.map { Unmanaged.passRetained($0 as AnyObject).toOpaque() }` at `CdeclReturnRenderer.cs:80-85`). The sibling `OptionalErrorPointer` arm at `:564-571` was added because this `as AnyObject` path is wrong for a nullable pointer. Optional class was left on the old arm.

Trigger: non-generic host, one method-own generic with a plain protocol constraint, return `SomeClass?` (or optional ObjC-protocol / optional ObjC-bridgeable container — same `OptionalClassPointer` kind at `CdeclReturnMapping.cs:69-88`). `IndirectResultReturnIsAdmissible` admits `needsResultPtr == false` (`MethodGenericBridgeEmitter.cs:186`). MethodHandler runs the bridge table (`MethodHandler.cs:1240-1257`) before `UsesMethodLevelGenericOpening` (`:1431-1432`), so the later opening route that *does* call `CdeclReturnRenderer` never sees an MGB-eligible member.

Impact: `SomeClass? as AnyObject` is not a class instance; `swiftc` rejects the wrapper. C# is already emitted (`TryEmit` returns handled). The result is a dangling `_XM` entry point (`EntryPointNotFoundException`), or a wrong nil if the coercion were ever accepted. `MethodGenericBridgeEmitterTests.TryEmit_OptionalClassPointerReturn` asserts only C# and discards `swiftResult` (`:624-639`). BindingTests covers `lookupError` (`MethodGenericBridgeReturn.swift:75-77`) but not this class sibling.

---

### Medium (P2)

**M1 — `EvidenceDirectory` is not resolved relative to the request file**  
`build/Build.SurfaceAccounting.cs:59-69` (`ResolvePaths`); consumed at `SurfaceAccountingEngine.cs:154-164`.

Stage B requires `SurfaceCaptureRequest.EvidenceDirectory` and contains command logs under it, but Nuke `ResolvePaths` still only absolutizes `ManifestPath`, `InputLockPath`, `OldDirectory`, and `TipDirectory`. A request that uses the same relative layout as those fields (`"evidence_directory": "old-evidence"` next to `surface-request.json`) is left CWD-relative. `ValidateCapture` then `Directory.Exists` from the process cwd (typically the repo root for `nuke surface-accounting`).

Impact: fail-closed (`DirectoryNotFoundException`), not a new false-green. It is a Stage B wiring hole in the only production entry point. Fixtures pass already-absolute temp paths. README documents request-relative target directories and does not say evidence roots must be absolute.

**M2 — SurfaceAccounting receipt is recorded on a nullable in-memory candidate with no `After(Validate)` edge (A-06 residual)**  
`build/Build.SurfaceAccounting.cs:23-25`, `:53-56`; `build/Build.Validation.cs:58-64`, `:87-89`, `:571-572`; `build/Build.BehaviorTier.cs:59-62`.

Stage B correctly adds `"SurfaceAccounting"` to the required-receipt list and records it only after `summary.Complete` throws on incompleteness. Plain `nuke validate` can no longer write. That closes A-06’s **fail-open**.

It does not close the scheduling hole that made `.After(SurfaceAccounting)` insufficient:

- `Validate` still `.Triggers(PackGate, BehaviorTier)` only; it does not schedule P1.
- `PromoteValidationBaseline` still `.After(SurfaceAccounting)` only — an ordering edge **if** P1 is already in the plan.
- `SurfaceAccounting` is still `.After(ReleaseGatesAttest)` only. There is no `.After(Validate)`.
- `validationPromotion?.Record("SurfaceAccounting")` is null-conditional. The candidate is constructed only inside `Validate.Executes`. If P1 runs first in the same process, Record is a silent no-op; Validate then builds a fresh candidate that never sees the receipt; Promote logs “lacks complete qualification receipts” and leaves the baseline unchanged.

Not High: A-06’s High was a write without P1. Grep finds `Record("SurfaceAccounting")` only at the post-Complete site. Residual is fail-closed / order-dependent liveness of the intended combined invocation.

---

### Low (P3)

**L1 — Schema does not require the new nested receipt fields the engine now enforces**  
`build/SurfaceAccounting/schema.surface-accounting-1.json:74-75` (`commands` / `target_stages` are arrays with no item schema). Engine requires `log_sha256` and `output_tree_sha256` on those records. Writer currently emits them; output schema validation cannot fail-closed if they disappear. Old request JSON without the new C# required properties still fails at `DeserializeStrict` (fail-closed). Future-drift; existing C# required fields are the live guard.

**L2 — Stale “one level of nesting” comment on a recursive parser**  
`ConcreteProtocolSpecializationEmitter.Async.cs:487-489` vs recursive `TryBuildNamedTypeSpecFromQualifiedName` at `:512`. Implementation walks nested angle-brackets; the comment does not. Latent documentation drift, not a current miss on `Swift.Array<XMLCoder.InternalElement>`.

---

## Checklist

| # | Item | Verdict |
| --- | --- | --- |
| 1 | **A-02** const/enum values in public-shape comparison, including implicit enum and invariant formatting; readonly remains valueless | **Pass.** `SurfacePublicShape.ConstantValue` (`SurfaceAccountingModels.cs:164-167`) is hashed. `AddField` writes values only for `const` (`SurfaceSyntaxScanner.cs:418-420`). Enum members always get `ConstantValue` (`:488`). Implicit `Write` after `Read = 1` is `Int32:2` (test `:354-355`). Hex `0x2` collapses to `Int32:2`. `FormatConstant` uses invariant numeric/`R` (`:519-531`). Readonly `RuntimeValue` stays null (`:353`). Stage A probes are now shape-changes (`:359-376`). |
| 2 | **A-03** distinct old/tip dirs; contained hashed logs; stage receipts bind capture/source/toolchain; hashed output trees; adversarial tests | **Fail — H1.** Distinct-dir is leaf-only. Logs are path-contained and SHA-256 verified (`:240-254`); tamper test passes. Stage receipts bind CaptureId/SourceSha/ToolchainSha256 (`:185-189`); wrong-source test passes. `HashDirectoryTree` (`:256-269`) is a content digest; tamper test passes. Tests do **not** cover `/tmp` vs `/private/tmp`. Same-content *copies* at two real directories remain an inherent self-authored-`SourceSha` limit (dropped, not a second High). |
| 3 | **A-04** independent six-slice roster; plist metadata, Mach-O arch, exports independent; missing-symbol / wrong-slice / missing-plist-slice reject | **Pass.** `RequiredRuntimeNativeSlices` (`Build.RuntimeNativeExports.cs:29-37`) is a static six-row contract. Plist is actual-only (`ReadRuntimeNativeSlices`). Analysis joins roster vs plist IDs, roster vs plist metadata, roster vs `lipo -archs`, roster × symbols vs `nm`. Missing-plist-slice control (`:478-494`) drops the required ID from actual maps while keeping the roster. PackGate log: 49 imports, 478 requirements, 10 pairs, 6 slices; all three controls rejected. |
| 4 | **A-05** CSM internal-type admission parses structure and recurses nested generics; defensive direct-name fallback; nested + cross-module tests | **Pass** for the Stage A counterexample. `ConformerReferencesInternalType` (`ConcreteProtocolSpecializationEmitter.cs:2724-2731`) prefers `TryBuildConformerTypeSpec` + `InternalTypeReferenceWalker`, then the flat-name fallback. Tests cover `Swift.Array<XMLCoder.InternalElement>` (true) and `Swift.Array<OtherModule.InternalElement>` (false). Residual sugar/`T?` parse-then-fallback misses are unproven on current production `SwiftQualifiedName` (unresolved, not High). |
| 5 | **A-06** promotion requires SurfaceAccounting; record only after complete; self-test unchanged until final receipt | **Mostly fixed; residual M2.** Production required receipts include `SurfaceAccounting` (`Build.Validation.cs:571-572`). Record is only after Complete (`Build.SurfaceAccounting.cs:53-56`). Self-test keeps bytes unchanged through Validate/PackGate/BehaviorTier and promotes only after SurfaceAccounting (`Build.WithdrawalTests.cs:189-194`). `.After(SurfaceAccounting)` is still not a required run of P1. |
| 6 | **A-01** accepted historical commit-message debt | **Confirmed.** Nine bodyless commits not re-filed. No history rewrite. |
| 7 | **A-07** owner policy; no predictor-policy change in scope | **Confirmed.** Stage B does not add/remove emission-time predictors. OWNER-02 untouched. |
| 8 | **OWNER-03** publication blockers unchanged; publication/push prohibited | **Confirmed.** Stage B does not claim publication readiness or relabel the blocked Q1 receipt. |
| 9 | Schema/model, capture failure, symlink/alias, hashing, constant eval, slice joins, Nuke receipts, nested-generic parser/walker | Mixed: A-04 joins pass; A-05 walker pass for angle-bracket generics; constant eval pass on current TPA host; **symlink alias fails (H1)**; EvidenceDirectory CWD-relative (M1); Nuke receipt scheduling residual (M2); schema nested items (L1). |
| 10 | Tests exercise Stage A counterexamples; docs/schema match runtime | A-02 probes tested. A-03 same-path alias tested; parent-symlink not. A-04 missing-plist-slice tested. A-05 nested + near-miss tested. A-06 missing-surface self-test present (omits production `WithdrawalEvidence` — coverage caveat). Docs claim distinct dirs/hashed trees; they do not mention realpath. Floor `18970 → 18978` matches eight new xUnit cases (6 Facts + 1 Theory × 2 InlineData). |

**Validation evidence** (logs, not re-executed): `nuke test` 19,024 / 2 skipped + 65 withdrawal assertions; `nuke pack-gate` 3:10 with the three native-export controls rejected; `nuke binding-tests --compile-only` 5:13; simulator 4,061 passed / 32 skipped. Green gates do not disprove H1/H2.

---

## Candidate dispositions

| Candidate | Origin | Decision | Reason |
| --- | --- | --- | --- |
| A-03 `/tmp` vs `/private/tmp` parent-symlink | Area 1 High | **Keep High (H1)** | Inspected `CanonicalDirectoryPath`; `ResolveLinkTarget` is leaf-only; host `/tmp` → `/private/tmp`; test is identical-string only. |
| EvidenceDirectory not request-relative | Area 1 Medium | **Keep Medium (M1)** | `ResolvePaths` has no capture evidence rewrite; fail-closed, production hole. |
| A-04 independent roster | Area 2 | **Drop (fixed)** | Static six-slice contract; three controls; pack-gate log agrees. |
| SliceMetadataMismatches untested dedicated control | Area 2 | **Drop** | Implemented and in `IsSuccess`; no current fail-open. Future-drift / Info. |
| Hygiene still iOS-IPA-centric | Area 2 | **Drop** | Pre-existing; Stage B adds the six-slice nupkg gate without claiming IPA covers every platform. |
| Native thunk cross-arch assemble-only | Area 2 | **Drop** | Documented host-arch dynamic proof; not a package fail-open. |
| A-05 nested generic | Area 3 | **Drop (fixed)** for the Stage A spelling | Parse-then-walk + tests. Residuals unproven on production spellings. |
| Sugar/`T?`/async-omits-internal-type | Area 3 unresolved | **Inconclusive** | Not promoted. No production `SwiftQualifiedName` shown. |
| A-07 prediction-gate freeze | Areas 3/5 | **Drop** | OWNER-02; out of scope. |
| A-06 fail-open without P1 | Area 4 | **Drop (fail-open closed)** | Named receipt + Complete-before-Record + self-test. Residual is M2. |
| SurfaceAccounting Record scheduling | Area 4 Medium | **Keep Medium (M2)** | Graph-reachable; not executed live. Fail-closed liveness, not a write without P1. |
| Promote still *triggered* without P1 | Area 4 | **Drop** | Intended: trigger remains, write is receipt-gated. |
| Self-test omits `WithdrawalEvidence` | Area 4 | **Drop as separate finding** | Coverage caveat under checklist 5/10, not a production write path. |
| A-01 bodyless commits | All | **Drop** | Accepted historical debt. |
| OWNER-03 publication | Area 4 | **Drop** | Unchanged. |
| MGB OptionalClassPointer `as AnyObject` | Area 5 Medium | **Keep High (H2), promoted** | Inspected Swift arm vs `CdeclReturnRenderer` `.map` and C# null-check; unit test discards Swift; Error sibling in the same range was fixed. Reachable incorrect emit, not a test wish. |
| Optional `any Error` consumer divergence | Area 5 | **Drop** | Mapping/renderer/marshal-plan/property/MGB error arm agree. |
| Generic-property PWT vs method-generic opening | Area 5 | **Drop** | Opening declines generic parents; no slot-count clash found. |
| Closure vs collection discard | Area 5 | **Drop** | Destroy-after-init; BindingTests exist. |
| Same-content copies at two real directories | Area 1 | **Drop** | Content digest; a legitimate no-op is identical. Distinct-inode spelling is H1; copies are the self-authored `SourceSha` limit. |
| Command-log `../` escape | Area 1 | **Drop** | Prefix check fail-closed; untested but not a green path. |
| HashDirectoryTree omits empty dirs/mode | Area 1 | **Drop** | Not enough for A-03 false-green without H1. |
| TPA-missing implicit enum `implicit:<index>` | Area 1 | **Drop** | Not reachable in current Nuke/xUnit host; tests assert `Int32:2`. |
| Identity codec token/enum drift | Area 4 | **Drop** | Parity-tested; latent Low/Info. |
| ExtractSwiftParameterBindingNames comma split | Area 5 unresolved | **Inconclusive** | Not proved reachable; opening declines closures/generic composites. |
| Async MGB `passRetained` for Optional class | Area 5 unresolved | **Inconclusive** | Not in assigned path; not confirmed. |

---

## Coverage

**Inspected (complete enough to judge):** Stage B 15-file index; surface-accounting engine/scanner/models/schema/comparer/writer/tests; runtime native-export gate + Pack/PackGate/hygiene wiring; CSM internal-type gate + tests; ValidationPromotion / Validate / BehaviorTier / SurfaceAccounting receipt scheduling; hottest remaining ABI files (optional error, generic value properties, method-level generics, MGB, closures, collection discard).

**Incomplete / skimmed (why the review is incomplete):** Area 5 groups 5–8 were sampled, not line-by-line: parser/model/config leftovers (`SwiftABIParser`, `ModuleProcessor`, `ClangAstParser`, `VerificationCache`, `Program.cs` historical), BindingTests remaining fixtures beyond the hottest new files, api-manifest / skip-surface / runtime-identity historical cells, blast-radius measurements, most `src/docs/Future/notes/**`, `.nuke/build.schema.json`. Unresolved extras under Area 5 were not promoted.

**Skipped by design:** unstaged P3 exposure-reporting; MM unstaged hunks; generated `BindingTests/output/**`; re-running nuke gates; A-01 rewrite; A-07 product-direction change; publication/push.

**Missing evidence:** live Nuke peel order for `nuke validate SurfaceAccounting` (M2 is graph-reachable, not process-logged); no executable `/tmp` vs `/private/tmp` request (H1 is code + host filesystem); no BindingTests Optional\<Class\> MGB fixture (H2 is generator source + unit test that discards Swift).

Reviewers: 5 area agents + 1 adjudicating pass. Snapshot unchanged through the review.