I'll start from the round-2 scope packet, original scope, and round-1 report, then verify the accepted fixes and close only the stated coverage gaps.Staged fingerprint matches the r2 packet. I’ll dump the new fix hunks and close only the round-1 coverage gaps.I’ll assign four reviewers: two for the accepted-fix verification, two to close the remaining historical coverage gaps.I'll inspect groups 6–8 against the assigned range and write the report from the live fixtureI'll inspect the assigned surface/doc/scheduling/schema range and pairing staged.I'll inspect the hunks only, First assigned I'll parser/config then write the round files and load the scope,- historical guard2rails report,. diffs first and the historical, then write the diffs round.-2 Area 5 report.I'll inspect the H2/L2 follow-up and remaining MethodGenericBridge paths, then write the report from opened file:line evidence.I'll pull the roundRound-1 report and historical diffs for the assigned parser/config files next. 1 left groups 6–8 skimmed; I’ll read that report, then pair each fixture with its C# coverage.Snapshot matches. Next I’ll open the MethodGenericBridge, async sibling, renderer, and tests for H2/L2.Snapshot matches. Next I’ll read the staged surface-accounting hunks and the live CanonicalDirectoryPath / scheduling / schema code.Staged H2/L2 hunks match the packet. I’ll now open the remaining ClassPointer and async retain paths.Diffs are small; I'll pull the full historical hunks and then read the live surrounding source.Unstaged MM hunks are excluded. I’ll now open CanonicalDirectoryPath, ResolvePathsI’ll, schema, and the new pull the fixture diffs tests. and live pairings next, then check blast-radius, baselines, and the contract docs.I'll inspect the live wiring around each historical hunk, starting with the parser and model flags.Next I’ll check Classify, async admission, MethodHandler order, and the test helper so H2 isn’t judged from the Swift arm alone.I’ll pair remaining Swift additions with C# tests, then inspect baseline and contract-doc diffs for live mismatches.NativeThunkReach C# imports a sentinel export that may be missing from the Swift fixture — I’ll confirm that pairing next.The floor bump is 18970→18979. I’ll probe Darwin `/tmp` aliasing, cycle termination, empty EvidenceDirectory, and the writer/schema `c_sharp_compile` path.Evidence is consistent: H2 routes both optional kinds through the renderer, the remaining ClassPointer arm is non-optional only, and Optional class async is gated out. Writing the report.Host `/tmp` is a symlink to `private/tmp`. I’ll run the canonicalization algorithm against that pair, plus a cycle case.Wrote the full report to `/private/tmp/paired-stage-b-gnItcs/review-r2-mgb.md`.

Snapshot matches HEAD `fb1866044b2b6b14013e9063534156b5d26ebc81` and staged SHA-256 `4cf00a78e300676c1ce6808a1f8c8958e5fe73d22158904ba781f1a9c39c457a`. Assigned coverage is complete. Zero candidate findings.

**H2 — FIXED.** `OptionalClassPointer` and `OptionalErrorPointer` both call `CdeclReturnRenderer.LinesBindingResult`; the renderer still switches on Kind, so optional-class gets `.map { Unmanaged.passRetained($0…)` and optional-error keeps the error-box path. The leftover `else if (isClassPointerReturn)` arm is non-optional `ClassPointer` only. `DoesNotContain("return Unmanaged.passRetained(")` does not false-fail on the `.map` line.

**L2 — FIXED, comment-only.** The parser already recursed (`TryBuildNamedTypeSpecFromQualifiedName` + depth-tracking `SplitGenericArgs`); the xmldoc now says so. No behavior change.

**Async MGB sibling — not reachable.** `ClassifyReturnKind` bails on `ContainsGenericParameters`, so `Optional<Class>` never hits `passRetained(_result as AnyObject)`. Dropped, not High.I have enough to close H1/M1/M2/L1. Writing the round-2 report now.I have enough pairing evidence to write the groups 6–8 report: no High, coverage complete, baselines reviewed from diffs rather than every JSON cell.Writing the full groups 6–8 report to the assigned path.Round-2 surface review is complete. The full report is at `/private/tmp/paired-stage-b-gnItcs/review-r2-surface.md`.

Snapshot matches: HEAD `fb1866044b2b6b14013e9063534156b5d26ebc81`, staged SHA-256 `4cf00a78e300676c1ce6808a1f8c8958e5fe73d22158904ba781f1a9c39c457a`. MM unstaged hunks and worktree floor 19025 were excluded. Staged floor is `18970 → 18979` (nine new cases, including the ancestor-symlink Fact).

**Verdict: complete for assigned paths. No new High/Critical/Medium findings.**

| Claim | Result |
| --- | --- |
| **H1** | **Closed.** `CanonicalDirectoryPath` walks every ancestor and recursively canonicalizes absolute link targets. Live probe: `/tmp` and `/private/tmp` collapse; a `/tmp/…` child whose leaf is not a symlink still canonicalizes under `/private/tmp`. Recursion on already-resolved `/private/tmp` terminates. Cycles fail-closed (`Exists` false or `IOException`). |
| **M1** | **Closed.** `ResolvePaths` now resolves old/tip `EvidenceDirectory` relative to the request file. Rooted, relative, and missing-dir cases behave; empty string degenerates to the request directory (not a false-green). |
| **M2** | **Closed** for combined-invocation liveness. `.After(Validate)` is ordering, not `DependsOn`; when both targets are named, Validate peels first and constructs the candidate before Record. Standalone `nuke surface-accounting` still cannot Record — intended fail-closed, not a write hole. Promote remains `.After(SurfaceAccounting)`. |
| **L1** | **Closed.** Strict `commandReceipt` / `targetStageReceipt` item defs require the hash/provenance fields. `CSharpCompile` serializes as `c_sharp_compile` under `SnakeCaseLower`; the writer schema-validates `capture.json`, so a wrong property name would fail `WriterProducesDeterministicVersionedArtifactSet`. |I have enough to judge wiring vs. consumers. Next I’ll write the Area 5 group-5 report.Groups 6–8 are closed. Full report: `/private/tmp/paired-stage-b-gnItcs/review-r2-fixtures-docs.md`.

**Snapshot.** HEAD `fb1866044b2b6b14013e9063534156b5d26ebc81`. Staged SHA-256 matches `4cf00a78…c457a`. Staged validation-baseline is only the unit floor `18970→18979`.

**Disposition: complete. 0 candidate findings (no High).**

Fixture/C# pairings hold: un-skips (`GenerateContentStream` SB0001, throwing-void `Validator` setter) have real round-trips; new Swift (generic value properties, MLG carriers, cross-module `reviewedThrow`, optional-error subscripts, ownership setters, native-thunk) has C# coverage or an honest skip-surface row. `bt_native_thunk_sentinel` is linked in `CompileModuleSlice`, not the Swift file. `NumberBox` `[Skip]` tests are Issue-2 activation contracts; `ObjCOperatorValue` is compile-gate only.

Baselines reviewed from diffs/scripted set-compare, not every JSON cell. API-manifest dropped only `AppleShapedForecast` index properties (now >3-slot property refuses; collection `Count` still tested). Skip-surface removed `ScalarWeighedBox.netUnitsValue` in lockstep with the new runtime test. Identity skips add the three operator `[Skip]`s and drop the unskipped Validator test. tvOS/x64 pass floors lag (conservative, not inverted). Blast-radius `LC_ALL=C` matches the resorted measurements.

Contract docs match live `RuntimeContract` floor 16, SWIFTBIND119–121, and wrapper-by-default. Doc moves and nuke kebab-case were not filed. Misc schema/csproj/CLAUDE/README updates are path retargets and taxonomy corrections.Round-2 Area 5 group 5 review is complete. Full report: `/private/tmp/paired-stage-b-gnItcs/review-r2-parser-config.md`.

**Snapshot:** HEAD `fb1866044b2b6b14013e9063534156b5d26ebc81`, staged fingerprint matches. Assigned files have no Stage B hunks; unstaged Program.cs P3 was ignored.

**Completeness:** complete for the eleven assigned files (large files hunk-scoped).

**Finding:** one Low, no High.

- **F5g5-1 Low** — `TypeDecl.RawGenericSig` is wired for methods, but synthesized property/subscript accessors never get `MethodDecl.RawGenericSig`. The new parent-marker subtraction cannot see `where T: BitwiseCopyable` on those accessors; the wrapper then fails swiftc and recovery withdraws them. Fail-closed, not an ABI crash.

Round-1 extras closed: stored-closure setter flag is conservative (Swift always boxes); Program.cs withdrawal wiring is sound; RuntimeVersionRange matches `version-compatibility.md`; forensic capture cannot change compile success/failure. A-01 / A-07 / OWNER-03 not re-filed.All four area reports are in. I’ll inspect the accepted-fix loci and the new Low before assembling the final verdict.# Paired review r2 — accepted-fix follow-up + remaining-historical coverage

**Status: complete.** Round 1’s groups 5–8 gap is closed. The original requested scope (historical range + current staged Stage B) has now been reviewed. This is not a clean bill: one new Low remains from the leftover parser wiring.

## Scope

| Item | Value |
| --- | --- |
| Historical range | `d5a2956ba4a170861ede1f138aa3abd460f93382^..fb1866044b2b6b14013e9063534156b5d26ebc81` |
| HEAD | `fb1866044b2b6b14013e9063534156b5d26ebc81` |
| Stage B (this follow-up) | exact `git diff --cached`, 18 files, 556 / 52 |
| Staged fingerprint | `4cf00a78e300676c1ce6808a1f8c8958e5fe73d22158904ba781f1a9c39c457a` (matches `scope-r2.md`; unchanged after adjudication) |
| Excluded | Unrelated unstaged P3. MM staged-only: `Build.WithdrawalTests.cs` excludes `.After(ValidateAppleTypesManifest)`; `validation-baseline.json` stages only `18970 → 18979` |

**This follow-up did not re-audit** already-complete r1 areas (A-02/A-04/A-05 surfaces, hottest ABI groups 1–4) except the accepted-fix loci and their callers.

**Reviewers:** 4 areas (surface/scheduling fixes; MGB H2 + L2; parser/model/config group 5; fixtures/docs/baselines groups 6–8) + 1 adjudication. Longest area ~10 min; they ran in parallel.

---

## Round-1 findings vs this overlay

| R1 | Disposition now |
| --- | --- |
| **H1** parent-symlink aliases | **Closed.** `CanonicalDirectoryPath` (`SurfaceAccountingEngine.cs:231-251`) walks every ancestor with `ResolveLinkTarget(returnFinalTarget: true)` and recursively canonicalizes `linkTarget.FullName`. A `/tmp/…` child whose leaf is not a symlink still collapses under `/private/tmp`. Recursion on already-resolved `/private/tmp` terminates. `RequestValidationRejectsAncestorSymlinkAliases` (`SurfaceAccountingEngineTests.cs:446-465`) builds an ancestor symlink. Same-string alias test remains (`:433-444`). |
| **H2** MGB OptionalClass `as AnyObject` | **Closed.** `MethodGenericBridgeEmitter.cs:564-575` routes `OptionalClassPointer` **and** `OptionalErrorPointer` through `CdeclReturnRenderer.LinesBindingResult`. Renderer still switches on Kind (`CdeclReturnRenderer.cs:80-89`): optional class gets `.map { Unmanaged.passRetained($0…) }`; optional error keeps the error-box path. Remaining `else if (isClassPointerReturn)` is non-optional `ClassPointer` only. Unit test now keeps `swiftResult` and asserts the `.map` body while `DoesNotContain("return Unmanaged.passRetained(")` does **not** false-match the `.map` line (`MethodGenericBridgeEmitterTests.cs:624-641`). |
| **M1** EvidenceDirectory CWD-relative | **Closed.** `ResolvePaths` (`Build.SurfaceAccounting.cs:61-73`) resolves old/tip `EvidenceDirectory` through the same request-relative `ResolvePath` as other request paths. README states this (`README.md:25`). |
| **M2** nullable Record / no `After(Validate)` | **Closed** for combined-invocation liveness. `SurfaceAccounting` is `.After(Validate, ReleaseGatesAttest)` (`Build.SurfaceAccounting.cs:23-27`). `.After` is ordering, not `DependsOn` — standalone `nuke surface-accounting` still cannot Record, which is intended fail-closed. When both targets are named, Validate peels first and constructs the candidate before Record. Promote remains `.After(SurfaceAccounting)`. Record is still only after Complete (`:55-58`). |
| **L1** schema nested receipts | **Closed.** `commands` / `target_stages` `$ref` `#/$defs/commandReceipt` and `targetStageReceipt` (`schema.surface-accounting-1.json:74,80,96-100`). Required fields include `log_sha256` and `output_tree_sha256`. Acronym property is `c_sharp_compile`. |
| **L2** stale “one level” comment | **Closed, comment-only.** `ConcreteProtocolSpecializationEmitter.Async.cs:487-489` now says recursively nested; the body already recursed. No behavior change. |

**Not re-litigated:** A-01 accepted historical debt; A-07 OWNER-02; OWNER-03 still publication-blocking; A-04/A-05 remain as reviewed in r1.

---

## Findings (this follow-up)

### Critical (P0) / High (P1) / Medium (P2)

None.

### Low (P3)

**L3 — Synthesized accessors never receive `MethodDecl.RawGenericSig`, so parent-marker subtraction cannot see property/subscript constrained-extension markers**  
`SwiftABIParser.cs:2745` (`CreateMethodDecl` sets `RawGenericSig = node.GenericSig`) vs `CreateGetAccessor` (`:3231-3274`) which parses `accessor.GenericSig` into `GenericParameters` and never copies the string. Same omission in `CreateSetAccessor` / subscript accessors. Consumer: `HasExtensionAddedUnerasableParentMarkerConstraint` (`ConstructorAdmissibility.cs:166-192`) reads `method.ParsedGenericSignature` (from `RawGenericSig`). Property path: `PropertyNarrowsParentGenericSignature` (`PropertyWrapperEmitter.cs:1030-1033`) → `MemberNarrowsParentGenericSignature` → that same core with `subtractParentDeclaredMarkers: true` (`ConstructorAdmissibility.cs:95-116`).

`BitwiseCopyable` is dropped from `GenericConformances` and survives only on the lossless signature (`:109-117`). Ordinary methods on `Box<T> where T: BitwiseCopyable` are refused onto the direct route. A property/subscript declared in `extension Box where T: BitwiseCopyable` when the parent does not already require that marker has an empty `ParsedGenericSignature`, so the marker walk is a no-op, the wrapper is admitted, `swiftc` rejects the unconditional extension, and verify/recover withdraws the accessor.

Not High: fail-closed, not an ABI crash. Representable extension constraints still flow through the `GenericParameters` walk (`:142-156`). Not A-07: incomplete wiring of a gate this range already added, not a request for a new predictor.

---

## Checklist (original packet, current overlay)

| # | Item | Verdict |
| --- | --- | --- |
| 1 | A-02 const/enum public-shape | **Pass** (r1; not re-opened). |
| 2 | A-03 distinct dirs, hashed logs/trees, adversarial tests | **Pass** on the overlay. H1 ancestor-symlink closed; same-path, tamper, and wrong-source tests remain. |
| 3 | A-04 independent six-slice roster | **Pass** (r1; Stage B native-export hunk unchanged in intent). |
| 4 | A-05 nested generic CSM | **Pass** for the Stage A spelling (r1). L2 comment-only. |
| 5 | A-06 SurfaceAccounting receipt | **Pass.** Required receipt + Complete-before-Record + self-test + `.After(Validate)` for combined invocations. Standalone P1 cannot promote (fail-closed by design). |
| 6 | A-01 | Accepted; not re-filed. |
| 7 | A-07 | Owner policy; not re-filed. |
| 8 | OWNER-03 | Unchanged; no publication claim. |
| 9 | Schema, alias, hashing, receipts, nested-generic parser | Closed for the r1 residuals (H1/M1/M2/L1/L2). L3 is leftover parser wiring, not a surface-accounting hole. |
| 10 | Tests vs Stage A counterexamples; docs/schema | Ancestor-symlink Fact added. Floor `18970 → 18979` = nine new cases (r1 eight + `RequestValidationRejectsAncestorSymlinkAliases`). Worktree 19025 is unstaged P3 and excluded. Schema item defs match engine fields. |

**Validation evidence (logs, not re-executed):** focused slice 294 passed; `nuke test` 19,025 / 2 skipped + 65 withdrawal assertions (worktree includes P3); `nuke binding-tests --compile-only` 5:09. Earlier PackGate 3:10 and simulator 4,061 / 32 skipped still apply to unchanged native-export / runtime behavior.

---

## Candidate dispositions

| Candidate | Origin | Decision | Reason |
| --- | --- | --- | --- |
| H1 `/tmp` vs `/private/tmp` | r1 High | **Closed** | Ancestor walk + recursive canonicalize; test + host algorithm probe. |
| H2 MGB OptionalClass retain | r1 High | **Closed** | Shared renderer arm; test asserts `.map` and rejects old retain. |
| M1 EvidenceDirectory | r1 Medium | **Closed** | `ResolvePaths` rewrites both capture evidence roots. |
| M2 Record scheduling | r1 Medium | **Closed** | `.After(Validate)` for named combined invocation; standalone still fail-closed. |
| L1 nested schema | r1 Low | **Closed** | `commandReceipt` / `targetStageReceipt` with required hashes; `c_sharp_compile`. |
| L2 one-level comment | r1 Low | **Closed** | Comment-only; parser already recursive. |
| Async MGB Optional class `passRetained` | r1 unresolved / r2 H2-category | **Drop** | `ClassifyReturnKind` (`AsyncMethodGenericBridgeEmitter.cs:408-411`) bails on `ContainsGenericParameters`; `Swift.Optional<Class>` never hits the SwiftClass retain arm (`:676-679`). Future-admission hazard only. |
| `DoesNotContain("return Unmanaged.passRetained(")` substring trap | r2 H2 | **Drop** | New line is `return result.map { Unmanaged.passRetained($0…`; needle is the old direct retain. |
| Firmlinks / bind mounts | r2 H1 residual | **Drop** | Not POSIX symlinks; Stage A counterexample was `/tmp`↔`/private/tmp`, which is a symlink and is collapsed. |
| Empty `evidence_directory` degenerates to request dir | r2 M1 | **Drop** | Schema `minLength: 1` is not applied to request deserialize; missing log still fail-closes. Not a false-green. |
| `.After` vs `.DependsOn` | r2 M2 | **Drop** | After is the correct edge; DependsOn would force Validate on every solo P1. |
| Accessor `RawGenericSig` omission | r2 group 5 | **Keep Low (L3)** | Inspected `CreateGetAccessor` vs `CreateMethodDecl` and the marker-subtraction consumer. Fail-closed. |
| `IsStoredClosurePropertySetter` without `HasStorage` | r1 unresolved | **Drop** | Swift setter wrappers always `isEscaping: true` + `_SBClosureCtx`; over-apply is conservative, not a leak. |
| Program.cs withdrawal wiring | r1 group 5 | **Drop** | Non-convergence returns before `FromController`; production `Converged()` sets `Cause = None`. |
| SwiftWrapperCompiler Capture | r1 / Stage A | **Drop** | Cannot change compiler success/failure. |
| RuntimeVersionRange vs version-compatibility.md | r1 group 5 | **Drop** | Restore ranges, Apple floor-only, Runtime ApiCompat agree. |
| Comment-only path retargets | group 5 | **Drop** | Target docs exist. |
| ModuleProcessor generic `!HasStorage` skip | group 5 | **Drop** | Aligns ABI field layout for generic frozen structs. |
| A-01 / A-07 / OWNER-03 | all | **Drop** | Out of scope / owner policy. |
| API-manifest `AppleShapedForecast` index drop | group 6 | **Drop** | Matches >3-slot property refuse; collection `Count` still tested. |
| Skip-surface `ScalarWeighedBox.netUnitsValue` removal | group 6 | **Drop** | Lockstep with new runtime test. |
| tvOS/x64 pass floors lag | group 6 | **Drop** | Conservative, not inverted. |
| `NumberBox` `[Skip]` / `ObjCOperatorValue` compile-only | group 6 | **Drop** | Issue-2 activation / compile-gate, not a hidden generator bug. |
| Doc moves / nuke kebab-case / generated output | group 7–8 | **Drop** | Guardrails. |
| ExtractSwiftParameterBindingNames comma split | r1 unresolved, group 3 | **Not reopened** | Already-complete area; still unproven. |

---

## Coverage

**Now complete relative to the original packet:**

- Stage B 18-file index (r1 15 + H2/L2 files).
- Surface accounting authenticity + scheduling + schema (H1/M1/M2/L1).
- MGB optional-class Swift/C# pair (H2) and L2 comment.
- Group 5 parser/model/config leftovers (eleven assigned files; large parsers hunk-scoped, stated in the area report).
- Groups 6–8: BindingTests fixture/C# pairings in the range, blast-radius script + measurements, validation-libraries.json, baseline **diffs** (not every unchanged JSON cell), contract docs, CLAUDE/README/nuke schema/csproj.

**Intentionally not re-done:** r1 Areas 1–4 ABI walks except fix interactions.

**Unavoidable missing evidence (does not reopen incompleteness):**

- Live Nuke peel of `nuke validate SurfaceAccounting` was not executed (graph-derived; a live run would write artifacts).
- No dedicated `/tmp` vs `/private/tmp` request JSON in the suite; the algorithm was probed and the ancestor-symlink Fact covers the same mechanism.
- Baseline JSON reviewed from diffs/set-compare, not every unchanged cell.

**Agents:** 4 area reviewers + 1 adjudicating pass. Snapshot frozen through the review.