# Swift Bindings 0.20.0 session wave — Stage A findings

- Audit range: `d5a2956ba4a170861ede1f138aa3abd460f93382^..fb1866044b2b6b14013e9063534156b5d26ebc81`
- Depth: standard
- Scope: session documents 00–09 and resulting implementation, including cross-commit interactions and HEAD consumers outside the diff
- Stage: findings only; no fixes or Stage B work

## Findings

### A-01 — Low — nine wave commits omit the required explanatory commit body

- Location: `CLAUDE.md:104`; commits `d5a2956ba4a170861ede1f138aa3abd460f93382`, `392908c2f12f956a2201e3f22a1d95083358ae12`, `16ce24c6c906cda6a79a71ad89af5ba9b9b189ef`, `9efdb29479db728d60bb1e5b68ab0b28c5c6ef3c`, `f759a255a71ce7e214bd3da981aa31fd11d6a98b`, `7356141df3292b7608b2b03451effef070f1f769`, `c9373e6d9bde9e178d5930e5ba2608d5f6eba2b8`, `f2923b03a4f301232451118cda274876d64d2799`, and `fb1866044b2b6b14013e9063534156b5d26ebc81`.
- Defect: the repository requires every commit message to contain a subject plus one to three sentences explaining why, but these nine commits contain only a subject.
- Failure scenario: a later maintainer or release auditor cannot recover the motivation, boundary, or intended compatibility tradeoff from the durable commit record after local session receipts rotate or disappear.
- Proving receipt: `git show d5a2956^:CLAUDE.md` confirms the rule predated the entire range; `git log --reverse --format='%H%x09%s%n%b' d5a2956^..fb18660` shows bodies only on `8bcc98b` and `c047262` and blank bodies on the nine commits listed above.

### A-02 — Medium — surface accounting cannot detect public constant or enum-value changes

- Location: `build/SurfaceAccounting/SurfaceSyntaxScanner.cs:389-473` (`AddField` and `AddEnumMember`), introduced by `8bcc98b246d9dda8d0da9a481d7ffe1fe0314527`.
- Defect: public constant fields and enum members are inventoried without their initializer/assigned numeric value in either `SurfacePublicKey` or `SurfacePublicShape`, even though P1's contract requires constants to be compared.
- Failure scenario: a generator regression changes `Permission.Read = 1` to `Permission.Read = 2` (or changes any public `const` used by consumers), while names and types stay fixed; P1 produces identical keys and shape digests and therefore emits no surface change, hiding a binary/behavioral compatibility break behind a green comparison.
- Proving receipt: an isolated executable probe against the built `SurfaceSyntaxScanner` scanned `public const int K = 1` / `enum E { A = 1 }` and tip variants with values `2`; it printed `keyEqual=True; shapeEqual=True` for both `K` and `A`, with identical old/tip shape hashes. Probe sources are `/tmp/swift-bindings-stage-a-const-probe/Program.cs` and `/tmp/swift-bindings-stage-a-const-probe/probe.csproj`; command: `dotnet run --project /tmp/swift-bindings-stage-a-const-probe/probe.csproj`.

### A-03 — High — P1 accepts self-authored, source-unbound capture receipts

- Location: `build/SurfaceAccounting/SurfaceAccountingEngine.cs:116-168` and `:181-203`; `build/SurfaceAccounting/SurfaceAccountingComparer.cs:24-32` and `:118-128`; introduced by `8bcc98b246d9dda8d0da9a481d7ffe1fe0314527`.
- Defect: P1 validates only the syntax of `SourceSha`, `ToolchainSha256`, command exit codes, and target-stage strings. It never hashes or opens a command log, binds either generated directory to the stated source SHA/toolchain/capture, requires `OldDirectory` and `TipDirectory` to differ, or proves that a directory was produced by the corresponding receipt.
- Failure scenario: a request assigns distinct old/tip capture IDs and plausible 40-hex source SHAs but points both sides of every target at the same complete tip output tree. Because the shared input/toolchain hashes and declared stage strings match, every target is comparable and the comparison reports `Complete=true` with zero changes, falsely certifying the old-to-tip public/dispatch delta without ever observing the old source.
- Proving receipt: `ValidateCapture` at `SurfaceAccountingEngine.cs:127-168` accepts arbitrary lowercase hashes and `success` strings; `AnalyzeCapture` at `:181-203` trusts the request directory directly; and `SurfaceAccountingComparer.cs:123-128` computes completeness solely from those booleans/hashes, target coverage, and unresolved-change absence. The shipped negative controls cover malformed metadata and incomplete stages, but no same-directory or source-to-output binding mutation exists.

### A-04 — High — runtime export qualification derives its expected platform roster from the artifact under test

- Location: `build/Build.RuntimeNativeExports.cs:70-115`, `:186-230`, and `:311-363`, added by `fb1866044b2b6b14013e9063534156b5d26ebc81`.
- Defect: `ReadRuntimeNativeSlices` treats the packed XCFramework's own `AvailableLibraries` array as `expectedSlices`; the analyzer then compares binaries discovered by iterating that same array back to it. There is no independent required platform/slice roster for the runtime package.
- Failure scenario: packaging drops an entire supported slice (for example tvOS or macOS) and also drops its plist row, which is the normal internally consistent form of that defect. All remaining declared binaries can export every symbol, so `AssertRuntimeNativeExports` passes and the release package ships with no runtime for that platform.
- Proving receipt: the sole `slices` value is read from the nupkg at line 79, drives binary discovery at lines 90-109, and is passed as the expected roster at lines 111-115. `AnalyzeRuntimeNativeExports` can only label a slice missing relative to that caller-supplied list. The source runtime XCFramework currently has six independently observable slices (`ios-arm64`, iOS simulator, Catalyst, macOS, `tvos-arm64`, and tvOS simulator), but no six-slice expectation reaches this gate. The two in-process adversarial controls at lines 386-433 remove a symbol and rename an already-derived slice; neither deletes a slice from both the plist-derived expectation and actual inventory. The separate App Store hygiene check names only iOS device and simulator entries (`build/Build.BindingTests.AppStoreHygiene.cs:214-233`) and does not close the all-platform gap.

### A-05 — Medium — CSM internal-type admission does not recurse into generic conformers

- Location: `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ConcreteProtocolSpecializationEmitter.cs:2398-2411` and `:2715-2725`, added by `f2923b03a4f301232451118cda274876d64d2799`.
- Defect: `ConformerReferencesInternalType` wraps the entire spelling (for example `Swift.Array<Module.Internal>`) in a flat `NamedTypeSpec`, so `InternalTypeReferenceWalker` sees no `GenericParameters` to recurse into. The adjacent withdrawn-type gate correctly uses `TryBuildConformerTypeSpec` and therefore does catch the same nested shape.
- Failure scenario: a public generic member is specialized with a discovered conformer such as `Swift.Array<XMLCoder.InternalElement>`. The new gate returns false, CSM emits a public overload and Swift wrapper naming the internal inner type, and wrapper post-processing strips or compilation rejects the inaccessible native side while leaving the managed specialization dangling (the exact defect the P9 gate claims to prevent).
- Proving receipt: an executable probe against the built generator registered `XMLCoder.InternalElement` as internal and printed `direct=True` but `nested=False` for `Swift.Array<XMLCoder.InternalElement>` (`dotnet run --project /tmp/swift-bindings-stage-a-csm-probe/probe.csproj`; source at `/tmp/swift-bindings-stage-a-csm-probe/Program.cs`). The cause is `new NamedTypeSpec(conformer.SwiftQualifiedName)` at line 2725, while `ConformerReferencesWithdrawnType` parses the structured conformer at lines 2368-2375. Tests at `ConcreteSpecializationEngineTests.cs:2214-2248` cover only direct shapes; the withdrawn-type counterexample at `:2387-2417` demonstrates the nested representation omitted here.

### A-06 — High — validation-baseline promotion does not require the planned surface-accounting receipt

- Location: `build/Build.Validation.cs:58-64` and `:571-574`; `build/Build.BehaviorTier.cs:57-62` and `:192-196`; plan contract at `src/docs/sessions/0.20.0/02-withdrawal-gates-and-repairs.md:29-31`; introduced by `c047262c0c48f28f59b4d4d6201b23c2adf38bdf`.
- Defect: the promotion candidate requires only `Validate`, `PackGate`, `BehaviorTier`, and `WithdrawalEvidence`. `SurfaceAccounting` never records a receipt. `PromoteValidationBaseline.After(SurfaceAccounting)` is only an ordering edge and does not schedule or prove that target; the normal `Validate` flow triggers `PackGate` and `BehaviorTier`, and `BehaviorTier` triggers promotion without P1.
- Failure scenario: a full validation run compiles and its two triggered fixture gates pass while generated public API is silently removed or retargeted in a way those aggregate checks miss. Promotion then ratchets the new compile counts into `validation-baseline.json` even though no frozen-corpus old/tip surface comparison was supplied, contradicting the session's explicit rule that missing surface proof leaves the baseline unchanged.
- Proving receipt: the `ValidationPromotion` constructor at `Build.Validation.cs:571-573` names all required receipts and omits `SurfaceAccounting`; repository-wide search finds `Record("Validate")`, `Record("WithdrawalEvidence")`, `Record("PackGate")`, and `Record("BehaviorTier")` only. `BehaviorTier` calls `Record("BehaviorTier")` and then triggers promotion. The staged-promotion self-test in `build/Build.WithdrawalTests.cs:189-192` likewise proves promotion after three receipts and contains no missing-surface negative control.

### A-07 — Medium — the wave adds compiler-error prediction gates across a hard freeze boundary

- Location: policy at `CLAUDE.md:103` and `src/docs/Design/engineering-policies.md:13-23`; additions in `c047262c0c48f28f59b4d4d6201b23c2adf38bdf` (`MemberValidationPipeline.cs:108-117`, `ConcreteProtocolSpecializationEmitter.cs:2398-2411`) and expansion in `f2923b03a4f301232451118cda274876d64d2799` (`ConstructorAdmissibility.cs:80-160`, consumed by `MethodWrapperEmitter.cs:904-924`).
- Defect: the repository's hard policy says an emission-time gate is justified only for a failure that would compile clean and fail at runtime; compile-detectable failures must be attributed and withdrawn by verify/recover. This wave instead pre-skips Foundation `NSInvocation` specifically because swiftc rejects it, pre-rejects internal CSM conformers specifically because wrapper compilation/post-processing rejects them, and broadens constrained-extension prediction for wrappers swiftc rejects. No escape predicate or explicit policy exception is recorded; P9's own plan says “do not introduce a new compile-error predictor.”
- Failure scenario: each new special case creates a second, incomplete model of compiler legality. It can silently remove surface before the general verifier observes it, requires indefinite parity maintenance (A-05 is already a concrete nested-type miss in one of these predictors), and makes future compiler/toolchain improvements unable to recover the member without another hand-edited allow path.
- Proving receipt: the policy predates the range (`git show d5a2956^:CLAUDE.md` contains the same criterion). The new comments themselves name compile-time outcomes: `NSInvocation` “cannot appear in a Swift wrapper”; the CSM gate says the post-processor strips the wrapper; the constrained-extension code and `09-review-followups.md:21-29` say swiftc rejects the shapes and then records a sound refusal. Session-wide instructions repeat the freeze at `src/docs/sessions/0.20.0/README.md:58`.

## OWNER rows

| ID | Related finding / evidence | Owner decision required |
| --- | --- | --- |
| OWNER-01 | A-01 | Decide whether the nine bodyless commits are accepted as immutable historical debt or whether an explicitly authorized history rewrite is warranted. Stage A makes no history mutation. |
| OWNER-02 | A-07 | Choose between removing the new compile-error predictors in favor of verify/recover or recording an explicit, scoped exception to the prediction-gate freeze. The current state contradicts the standing policy, so an autonomous Stage B implementation must not choose the product direction. |
| OWNER-03 | `src/docs/sessions/0.20.0/execution/P8/final-q1/qualification-receipt.md` | The integrated Q1 receipt is explicitly **BLOCKED FOR PUBLICATION**: 24 nonzero available corpus candidates, 36 absent inputs, 14 red `swift-dotnet-packages` cells, four red internal-binding-testing cells, and nine unaccepted Mono full-AOT x20 clobbers. Repair the exact residuals or explicitly accept each limitation before publication; this audit does neither. |

## Coverage manifest

### Audit disposition

- Actionable: **YES**.
- Findings: **3 High, 3 Medium, 1 Low**.
- Lead/depth: one GPT-5.6 Sol/High Stage A audit lead, standard depth, with bounded read-only/mechanical support sweeps. Claude and paired review were not used, per the user's override. No Stage B fixes, baseline reseeds, commits, pushes, or publication actions were performed.
- Runtime: Darwin/macOS, established before reading macOS-specific evidence. The pre-existing dirty P3/exposure-report worktree was treated as owner state and not modified; the only repository write made by this audit is this ignored findings file.

### Commits walked

| Commit | Scope checked | Stage A result |
| --- | --- | --- |
| `d5a2956ba4a170861ede1f138aa3abd460f93382` | Optional `any Error` cdecl return mapping/rendering; property/subscript/method/generic/specialization consumers; operator marshalling aliases and tests | A-01 only; no consumer divergence found. |
| `8bcc98b246d9dda8d0da9a481d7ffe1fe0314527` | Surface-accounting models, request validation, capture analysis, syntax/native joins, comparer/writer/schema, frozen roster, unit negatives | A-02 and A-03. |
| `c047262c0c48f28f59b4d4d6201b23c2adf38bdf` | Canonical recovery identity, exact withdrawal evidence/policies, provenance/receipt archive, staged baseline promotion and Nuke ordering, strict compile-only joins, compiler predictors | A-06 and part of A-07. |
| `392908c2f12f956a2201e3f22a1d95083358ae12` | Engineering-doc reorganization, moved policy/decision references, Runtime/Apple version-compatibility wording, non-doc link adjustments | A-01; no broken implementation contract identified in the moved references reviewed. |
| `16ce24c6c906cda6a79a71ad89af5ba9b9b189ef` | Generated Nuke command schema refresh against newly added targets/options | A-01; mechanical schema delta only. |
| `9efdb29479db728d60bb1e5b68ab0b28c5c6ef3c` | Native-thunk managed path, two-architecture probe/sentinels, runtime fixtures and local P4 evidence | A-01; probe controls and local receipts corroborate the claimed result. |
| `f759a255a71ce7e214bd3da981aa31fd11d6a98b` | Generic value-struct property eligibility, constructed receiver pinning, descriptor/static PWT slot threading, optional error interaction, fixtures/tests/baselines | A-01; no additional mismatch found. |
| `7356141df3292b7608b2b03451effef070f1f769` | Method-own generic plan/admission, metadata opening, refusal channel/order, managed result liveness, bounded constraints, fixtures/tests | A-01; no additional mismatch found. |
| `c9373e6d9bde9e178d5930e5ba2608d5f6eba2b8` | Closure callback typed-address carriers, direct/optional stored property setters, escaping ownership propagation, async/result/collection fixtures | A-01; no additional mismatch found. |
| `f2923b03a4f301232451118cda274876d64d2799` | Collection returned-slot destruction, cross-module error ownership, constrained extensions, CSM internal conformers, review follow-ups | A-01, A-05, and part of A-07. |
| `fb1866044b2b6b14013e9063534156b5d26ebc81` | Runtime native-export inventory and negative controls, wrapper failure evidence, release documentation/qualification follow-ups | A-01 and A-04. |

The full combined range is 11 commits, 190 changed files, 17,961 insertions, and 2,059 deletions. `git diff --check d5a2956^..fb18660` was clean.

### Shared surfaces and consumers enumerated

- Canonical identity codec: `DeclId`, `RecoveryUnitId`, generator withdrawal evidence, build-side policy parser, strict tests.
- Exact withdrawal semantics: generator report writer, validation target, pure-ObjC path, BindingTests fresh-zero gate, staged promotion, self-tests.
- Surface-accounting contract: Nuke entry point, strict JSON/models, frozen roster, engine, sidecars, syntax scanner, manifest/native/origin joins, comparer, writer, unit suite.
- Optional `any Error` return ABI: centralized classifier/renderer and method, property, subscript, optional-pointer, method-generic and CSM consumers.
- Generic value-property metadata/PWT seam: wrapper admission, `PInvokeHelperContext`, P/Invoke signature, marshal-plan locals, property Swift protocol/extension, constructed managed receiver.
- Method-level generic opening: method admission, wrapper routing, P/Invoke phase ordering, metadata/PWT suppression, refusal-name collision handling, result-liveness cleanup, existential/associated-type/superclass carriers.
- Closure shapes/ownership: ordinary adapter, typed collection/Result address inputs, optional collection nil path, direct and optional stored setters, transferred GCHandle cleanup, return invoker paths.
- Review follow-ups: CSM structural preflight, constrained extension marker subtraction, cross-module retained error ownership, `SwiftArray.Remove`, and `SwiftDictionary.UpdateValueUnsafe`.
- Runtime native exports: all packed Runtime managed assemblies, package plist/binaries/architectures, `Pack`, `PackGate`, App Store hygiene, and negative controls.
- Multi-commit combined diffs were read for the hottest shared files: `PropertyWrapperEmitter.cs`, `MethodMarshalPlanBuilder.cs`, `WrapperEmitter.Return.cs`, `ConcreteProtocolSpecializationEmitter.cs`, `MemberValidationPipeline.cs`, `WrapperValidation.cs`, `GenericDispatchEmitter.cs`, `Build.SurfaceAccounting.cs`, `Build.PackGate.cs`, and the validation/runtime/API/skip baselines.

No consumer mismatch was found beyond A-02/A-05/A-06/A-07. In particular, the canonical codec is source-linked into Nuke rather than reimplemented; method-generic consumers recompute the plan rather than trusting a cloned flag; optional error getters bypass decomposed-optional storage consistently; and the collection discard fixes destroy only initialized native results.

### New gates/refusals adversarially checked

| Gate/refusal | Counterexample/result |
| --- | --- |
| Surface public-shape comparison | Executable constant/enum mutation remained equal — **failed open** (A-02). |
| Surface capture authenticity | Same old/tip tree with invented source/command receipts satisfies all coded completeness predicates — **failed open** (A-03). |
| Exact withdrawal reader/policy | Malformed schema, plane drift, duplicate/foreign/noncanonical IDs, nonzero fresh enrollment, substitution, and pure-ObjC modes have explicit fail-closed paths and tests. |
| Staged promotion | Eligibility, named receipts, starting-hash/concurrent-write checks, zero enrollment, and unrelated-field preservation pass their narrow controls; omission of the required surface receipt **fails open** (A-06). |
| Native-thunk feasibility | Generated import/export, arm64/x86_64 sentinels, and dynamic bad control are exact and corroborated by the P4 evidence root. |
| Generic value-property refusal | Missing descriptor, over-three-slot, unsupported structural container, and legacy T-bearing controls were inspected; no evasion found. |
| Method-generic refusal | Marker, same-type, unknown, four-root, generic-return, dynamic-Self, inout, composite, async/ctor/accessor/variadic and protected-memory refusal-before-payload controls were inspected; no evasion found. |
| Closure setter/adapter admission | Struct/generic-class stored-setter refusals, nil/replacement ownership, collection/Result address lifetime and direct/optional controls were inspected; no additional evasion found. |
| CSM internal conformer | Executable nested-generic probe printed `direct=True`, `nested=False` — **failed open** (A-05). |
| Constrained-extension/NSInvocation predictors | Narrow controls exist, but the gates violate the prediction-freeze contract — A-07. The unproven NSInvocation property/subscript parity concern was not filed because the later verify/recover path remains capable of withdrawal. |
| Runtime native exports | Missing-symbol and renamed-slice controls fail; deletion from both plist and archive is self-rostered and passes — **failed open** (A-04). |
| Wrapper failure evidence | Inspected as best-effort diagnostics, not a verdict gate; capture failures cannot change compiler success/failure. |

### Handoffs, closure claims, reviews, and receipts

- All explicit P4 deferrals have receivers (P5/P6/P7, later subsystem work, or P8). P0 F3 is represented by the P6 implementation. P9's P8 follow-ups are wired by `fb18660`. No orphan handoff was found.
- P6 and P7 local briefs still say implementation-ready even though their implementation commits landed; this is stale local session status, not a false CLOSED claim. P8 correctly says its plan alone performed no execution.
- Commit trailers: none. Nine commits violate the mandatory explanatory-body rule (A-01); no prohibited “Gates passing” footer or false gate claim was found in commit messages.
- The local P4 evidence root was inspected: classification artifacts, host probe, architecture evidence, device/simulator logs, and Grok review exist and corroborate the 93-classified/zero-unresolved P4 statement.
- The P8 Q1 receipt and real logs were inspected. `compile`, `test`, full `validate` (132/132 with PackGate/BehaviorTier), pack, platform lanes, App Store hygiene, blast radius, and mixed gates show the stated green outcomes. The downstream validation log is genuinely red (14/80 cells), and the Mono classifier records 40 safe, two spilled, nine clobber, and one separately-mapped `no_transition`; the receipt honestly remains **BLOCKED FOR PUBLICATION**.
- P8 Grok r1/r2 artifacts exist and disclose/fix their review findings. No Claude review was sought for this audit because the user explicitly replaced the skill's reviewer rule. P0/P5/P9's exact per-batch logs/reviewer sessions are local/ignored and are not independently durable in Git; their integrated behavior is substantially corroborated by Q1, while their disclosed red/degraded lanes were not relabeled green.
- `.agent/`, `artifacts/`, and `src/docs/sessions/*` are ignored. Therefore these receipts are locally inspectable but not durable clone-level evidence. This was recorded as a coverage limitation, not a new defect, because the packet explicitly declares itself local-only and the release verdict is not overstated.

### Skipped or bounded work

- No Nuke/corpus/device/package gate was rerun: Stage A is findings-only, the worktree contains unrelated active P3 changes, and rerunning generators would overwrite shared evidence. Existing real logs plus static/adversarial checks were used. Two isolated executable probes were run from `/tmp` for A-02 and A-05.
- Standard depth did not exhaustively re-disassemble every Mono function or reopen every one of the hundreds of corpus/downstream per-cell logs. Aggregate files, named failures, representative logs, hashes, and the final blocked verdict were checked; the unaccepted residuals remain OWNER-03.
- P3 exposure-reporting work and its dirty/untracked files are outside the audited commit range and were not reviewed or modified.
- The prior pre-0.20.0 audit under `.agent/audit/prior-pre-0.20.0/` was treated as historical context only; this audit did not duplicate or amend it.
- No technical concern was suppressed for lack of a fix. The NSInvocation accessor parity question was explicitly not promoted to a finding because a final unsafe emitted route or false-green release verdict was not proved.

## Stage B disposition — 2026-09-13

Stage B was executed serially by one GPT-5.6 Sol/High lead. The user explicitly
replaced the repository's normal paired-review pair with Grok-only review; Claude
was not invoked. The implementation and evidence archive are in the commit that
contains `src/docs/audits/0.20.0-session-audit.md`.

| Finding | Final disposition |
| --- | --- |
| A-01 | Accepted as immutable historical debt. No history rewrite was performed. |
| A-02 | Fixed. Public constants and enum members now carry canonical semantic constant values in the compared shape. |
| A-03 | Fixed. Captures are bound to distinct canonical evidence roots, hashed logs, source/toolchain identity, stage receipts, and hashed output trees; alias and tamper controls fail closed. |
| A-04 | Fixed. Runtime export qualification uses an independent six-slice roster and rejects missing symbols, wrong slices, and a slice removed from both the plist and archive. |
| A-05 | Fixed. CSM internal-type admission parses the conformer spelling structurally and walks recursively nested generic arguments. |
| A-06 | Fixed. Baseline promotion requires a complete SurfaceAccounting receipt, and combined Validate/SurfaceAccounting invocations order validation before receipt recording. |
| A-07 | Deferred to OWNER-02. No policy direction was chosen autonomously. |

### Grok-only final review disposition

Round 1 found two High, two Medium, and two Low issues in the Stage B candidate.
All were accepted and fixed: ancestor-symlink aliases, optional-class retained
return rendering, request-relative evidence paths, target ordering, nested receipt
schema strictness, and a stale recursion comment. Round 2 closed every Round 1
finding and completed the historical coverage. It found one new Low: synthesized
property and subscript accessors did not preserve `MethodDecl.RawGenericSig`.
That final Low was fixed for getter/setter property and subscript accessors and
covered by one parser regression test. Per the paired-review stopping rule and the
user's High/Critical-only re-review instruction, no third external review was run.

### Final validation

- Focused Surface Accounting / CSM / optional-return review slice: 294 passed.
- Focused synthesized-accessor regression: 1 passed.
- `./build.sh Test`: succeeded; 65 withdrawal assertions, 19,026 Swift.Bindings unit tests passed / 2 skipped, 79 analyzer tests passed, and 922 runtime-library tests passed / 1 skipped.
- `./build.sh BindingTests --compile-only`: succeeded in 5:07; generated C# and Swift wrapper compile gates passed.
- Earlier unchanged-scope gates remain applicable: PackGate succeeded (49 imports, 478 requirements, 10 slice/architecture pairs, six runtime slices, and all three adversarial controls rejected); simulator BindingTests passed 4,061 / 32 skipped.

### Remaining owner decisions

| ID | Status |
| --- | --- |
| OWNER-01 | Resolved by accepting A-01 as historical debt. |
| OWNER-02 | Open: choose whether to remove the A-07 compile-error predictors or record a scoped exception to the prediction-gate freeze. |
| OWNER-03 | Open and publication-blocking: the P8 Q1 residual corpus, downstream, internal-binding, and Mono full-AOT limitations remain unaccepted. |

No package was published and no branch or commit was pushed. The unrelated P3
exposure-reporting worktree was preserved and excluded from the Stage B commit.
