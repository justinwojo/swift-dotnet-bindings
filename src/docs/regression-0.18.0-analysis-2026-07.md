# 0.18.0 pre-release regression — root-cause analysis and prevention plan

Date: 2026-07-26. Status: analysis complete (all six investigations reported), corrected by
an independent paired external review (notably: Defect E's mechanism re-attributed from the
tombstone eligibility gate to the supported-closure `PInvokeEmitter` asymmetry, and D/E
reframed as latent gaps exposed — not introduced — by `8b607fa3`); **fixes applied and integrated — see §11**. Source evidence: the 0.18.0 regression run
(`artifacts/regression-validate-0.18.0.json` in swift-dotnet-packages, 16 ERROR cells across
6 libraries) plus five independent root-cause investigations and one gate-coverage
investigation, each with A/B or counterfactual proof. Full per-family reports with complete
evidence chains are archived in the session scratchpad (`analysis-{reality,kingfisher-lottie,
mattersupport,facebook,stripe,gaps}.md`).

---

## 1. Executive summary

The 16 red cells reduce to **five distinct generator defects** plus **one process defect**.
None of the six regression families is input/SDK drift, test-expectation drift, or harness
drift — every one was proven against unchanged inputs (including regenerating with the
`sdk-v0.16.0` generator against the *current, bumped* upstream xcframeworks where pins had
moved).

| Defect | Causal commit(s) | Families | Shipped on NuGet? |
|---|---|---|---|
| **A. Quarantine seed predicate too narrow** — Clang-imported C aggregates (`c:@S@`, `c:@SA@`, `c:@E@` USRs, `isExternal` re-export stubs with no `mangledName`) are quarantined as "malformed"; the proven-closure walk then withdraws every type transitively storing one. Sub-defect **A2**: the closure's completeness proof has a hole — the SwiftUI bridge plane never consults the withdrawal set, so a withdrawn-but-bridged type yields a *compile-broken* binding (CS0234) instead of the promised SWIFTBIND120 fail-closed | `56662339` (2026-07-20); predicate itself from `a781c97e` (2026-06-16, latent until the walk existed) | Kingfisher (4 types), Lottie (4+6), **all seven Stripe kits** (StripePaymentSheet 72/164 types withdrawn, incl. `AddressViewController` → the CS0234 via A2), RealityFoundation (13 types incl. `Transform`) → RealityKit downstream, **StoreKit2 (latent — cells green only because tests don't touch `SKError`/`SKANError`)** | **No** — post-0.17.0 |
| **B. Microsoft.iOS surface index treated as authority for absence** — `withdrawOnNoHit: true` withdraws members whose types live in sibling `SwiftBindings.Apple.*` packages the index never sees | `4dbcf105` (2026-07-22); index introduced benignly by `62e2f11b` | MatterSupport ×4 platforms (`setupPayload` property + two ctors via default-arg cascade) | **No** — post-0.17.0 |
| **C. RawRepresentable planner ignores the discard writer** — for module-internal `enum X: String`, the Swift wrapper is written to a discarded `StringWriter` while the C# plane unconditionally emits `LibraryImport`s naming those symbols | Latent since `3b3f4e97` (2026-04-01); **exposed** by the SWIFTBIND108 integrity gate `3c3de627` (2026-07-12) | Facebook/FBSDKCoreKit (20 internal String-raw enums × 2 symbols = 40 dangling P/Invokes; generator exits 1) | **Yes, silently** — every shipped FB binding since ~0.14 has these members as dead API (`EntryPointNotFoundException` if touched). The *red cell* is new; the defect is old and SWIFTBIND108 is correctly refusing to ship it. |
| **D. Closure tombstone names a module the binding has no managed reference to** — `ClosureParamTombstoneEmitter.Emit` writes the projected parameter type verbatim (`StripeCore.STPAPIClient`) into a binding whose csproj has only a `<SwiftFrameworkDependency>` (native-only — no `PackageReference`) on that module. The missing managed reference is the causal arm; the missing `global::` is a separate latent hardening (qualification cannot fix an unreferenced assembly) | Latent since `1e3af3e5` (2026-05-08, tombstone introduction); **exposed** by `8b607fa3` (2026-07-24), which routed this closure shape to the tombstone. `8b607fa3` does not touch the emitter file | StripeConnect (CS0246 `StripeCore` ×2 platforms) | **No** — post-0.17.0 |
| **E. Supported-closure plan/emit asymmetry in P/Invoke emission** — a *baseline-supported* async-throwing closure (`() async throws -> String`) whose containing member fails any of `asyncBridgeEligible`'s four conjuncts (`PInvokeEmitter.cs:505-509`: cdecl method wrapper + member async + member throws + baseline shape; this sync silgen-path init fails at least two) falls to `Swift.AnyType` at `:520` — inside the **supported**-closure branch, while the method body still emits the half-bridge state machine (`AsyncThrowingClosureState` + start callback). The tombstone (which handles only *unsupported* closures) is never consulted, so its struct-ctor exclusion is not the causal gate. A twin AnyType arm exists for non-throwing baseline async at `:523-539`. Root asymmetry: the exact guard exists (`WrapperValidation.cs:688`, both arms) but only the ordinary method path invokes it (`MethodHandler.cs:1442`) — `ConstructorHandler.Emit` (`:90`) skips it | AnyType fallback latent (pre-dates the window); **exposed** by `8b607fa3` (2026-07-24), whose demangler fix — async detected on the outermost `FunctionType` only — stops mis-marking sync inits with async-closure params as async, un-skipping this member (the String-return baseline arm itself dates to `b6308240`, 2026-04-22) | StripePaymentSheet (`CustomerSheet.IntentConfiguration` init → SYSLIB1051 ×2 platforms) | **No** — post-0.17.0 |
| **P. Process: the release path cannot see surface shrinkage, and the matrix artifact is clobberable** | — | 29-day / 169-commit detection latency; 0.17.0 published with a 1-cell artifact | 0.17.0 shipped unobserved |

Key structural facts:

- **Defects A and B are the same *class***: a fail-closed absence rule ("authority X has no
  record of this type ⇒ withdraw") whose authority is incomplete for the binding's real
  reference closure. Defect C is the same class one plane over (plan/emit bookkeeping
  asymmetry), and it predates the 0.17→0.18 window by three months — evidence the pattern is
  structural, not an artifact of this release cycle. A2 (SwiftUI bridge ignores withdrawals)
  and the module-database staleness (§6.1) are two more members of that same asymmetry class:
  a plane that names types without consulting the plane that deletes them.
- **Defects D and E are both latent gaps *exposed* — not introduced — by `8b607fa3`**
  (paired-review finding, verified in git: that commit touches neither
  `ClosureParamTombstoneEmitter.cs` nor `PInvokeEmitter.cs`). D is the tombstone firing on a
  newly-routed shape and writing a name from an unreferenced module; E is a supported-closure
  reaching a `PInvokeEmitter` fallback that only ever made sense for members the validator
  used to skip. The lesson stands in sharpened form: a commit that widens which shapes
  *reach* an emission path inherits responsibility for fixturing that path's parent-kind ×
  member-kind × module-locality matrix, because the latent gaps it exposes bisect to it.
- **Every automated gate is satisfied by these failures by construction.** A withdrawal
  always compiles (the closure walk removes the whole dependent tree, leaving no dangling
  reference); a dangling entry point compiles on both planes. Only consumer code referencing
  the vanished surface — which exists solely in the downstream packages repo — turns red.
- **Green unit tests were load-bearing false confidence — in two distinct ways.** For B,
  the tests *positively assert the bug*: `4dbcf105`'s `AppleTypeProjectionTests` assert
  withdraw-on-no-hit against a hand-built index containing only unrelated namespaces. For A,
  the failure is *incomplete coverage misread as coverage*: `AbiIngestionContractTests`
  claims to cover "a C-typedef struct re-exported through a Swift module" but has no
  `c:@S@`/`c:@SA@`/`c:@E@` theory row. For E, the miss is a *fixture-pairing gap*: no test
  or fixture pairs a supported baseline async closure with a sync / non-cdecl-wrapper
  containing member, so the `PInvokeEmitter` AnyType fallback was never exercised on an
  emitted member. Three different test smells, one outcome.

## 2. Defect A — ingestion-quarantine seed predicate (the big one)

**Mechanism.** `SwiftABIParser.CollectDeclarations` (`SwiftABIParser.cs:1416–1438`)
quarantines any bindable node with an empty `mangledName` unless `IsObjCRootedIdentity`
(`SwiftABIParser.cs:4327–4338`) exempts it. The exemption knows only `c:objc(` and `c:@T@`
USR prefixes (plus the `ObjC` decl attribute). But the ABI digester emits root-level
`isExternal: true` re-export stubs — with C-aggregate USRs `c:@S@X` / `c:@SA@X` / `c:@E@X`
and no `mangledName` — for foreign types a module retroactively conforms
(`extension CGSize: KingfisherCompatibleValue`) or otherwise re-exports (`simd_quatf` in
RealityFoundation). These are quarantined, and `IngestionQuarantineClosure.Compute`
(`IngestionQuarantineClosure.cs:100–121`, `StructurallyReaches` at `:203–266`) withdraws to a
fixpoint every type whose superclass / conformance / **stored property** / enum payload
reaches one, plus every retained member whose signature reaches one as a leaf.

**Proof.** Two agents independently A/B'd byte-identical inputs: the `sdk-v0.16.0` and
`56662339^` generators emit all affected types (including from the *current* Kingfisher
8.10.0 xcframework, exonerating the pin bump); `56662339` and HEAD emit none. At 0.16.0 the
identical nodes produced only a diagnostic ledger line ("ABI declaration dropped … no mangled
name") and the field types resolved through the Apple supplement.

**Blast radius** (corpus-wide binding-report sweep): Kingfisher 6 withdrawn, Lottie 10,
StripePaymentSheet 72 of 164 (including `AddressViewController` via `PaymentSheet.Appearance`
storing `NSDirectionalEdgeInsets`), StripeUICore 27 (0 of 96 types emitted), 1–2 in each of
five more Stripe kits, RealityFoundation 13 (incl. `Transform`, which then breaks RealityKit's
*generated* code — see §6.1), StoreKit2 `SKError`/`SKANError` (silent — no red cell).

**Fix direction** (agents' convergent recommendation, not yet implemented):
1. Model `isExternal` on the ABI `Node` (currently unparsed) and treat an external re-export
   stub's missing mangled name as expected — skip the *stub* cleanly, pre-`56662339`
   semantics. **Review caveat (Codex, code-verified): "skip" must mean "don't quarantine /
   don't bind the stub", not "don't walk it"** — the parser deliberately walks cross-module
   foreign nodes so extension members declared by the current module get attached
   (`SwiftABIParser.cs:1387`); a blanket skip of every external mangled-name-less node would
   discard legitimate retroactive-extension surface. Related: the system re-export
   fall-through at `SwiftABIParser.cs:1368-1374` is *why* these stubs reach the quarantine
   site at all — the fix should make that path's disposition explicit rather than incidental.
   Fallback: widen the prefix family (`c:@S@`/`c:@SA@`/`c:@E@`/`c:@U@`, bare and `c:@M@…`
   module-qualified), routed through one shared helper with
   `TypeDatabaseExtensions.IsClangImportedValueTypeUsr` (`:670`, which itself misses `@SA@`)
   instead of two drifting lists.
2. Resolvability pre-check before the cascade: a quarantine should mean "no channel can
   resolve this type", not "this one ABI node lacked a field" — `CGSize` resolves through the
   Apple supplement on the very same run.
3. Unit-test rows with the *real* node shape (`c:@SA@simd_quatf`, `c:@S@CGPoint`,
   `c:@E@NSComparisonResult`; no `mangledName`, `isExternal: true`, foreign `moduleName`).
4. First-party fixture: a retroactive conformance on a CoreGraphics struct in
   SwiftBindingsTestLib (corpus uses CG types but never declares a retroactive conformance on
   one, so the stub node never appears in any gate's input).

Do **not** exempt types by name and do not weaken the closure walk — the walk is sound; the
seed is wrong.

**A2 — the completeness proof has a hole (independent fix, not subsumed by the seed fix).**
`IngestionQuarantineClosure.cs:28-33` asserts exactly one unmodelled residual channel (a
retained type's own generic where-clause) and promises SWIFTBIND120 fail-closed otherwise.
That claim is false: the **SwiftUI bridge plane** is a second unmodelled channel —
`SwiftUIBridgeEmitter`/`SwiftUIBridgeCollector` contain zero references to the
withdrawal/denylist set, and the residual scan (`IngestionQuarantineClosure.cs:144,350`)
re-scans only the retained type-decl surface. Stripe is the proof: withdrawing
`AddressViewController` left `StripePaymentSheet.SwiftUIBridge.cs:175,185,188` referencing
its nested types → CS0234, a *compile-broken* binding where the design promises either a
clean smaller binding or a hard SWIFTBIND120. Fix: every plane that can name a type must
either consult the withdrawal set or be covered by the residual scan — review confirmed the
same blindness in `ThemeBridgeEmitter` (walks every raw module class at `:82-89`, emits
Swift references and a C# partial class from the unfiltered name at `:603`), found no
withdrawal consult in the ObjC companion plane, and round 2 added a **fourth plane — the
typed-error registry**: `ModuleEmitter` precomputes error types from the *unfiltered*
module tree (`ModuleEmitter.cs:83`), `ErrorEnumRegistryEmitter` registers every qualifying
type (`ErrorEnumRegistryEmitter.cs:53`) with a skip predicate that checks SPI/internal/
underscore/supplement state but no withdrawal set (`:146`), and those registrations are
later emitted as concrete Swift type references (`ErrorRegistryHelperEmitter.cs:382`) and
concrete C# type names (`:458`) — a collateral-withdrawn error-conforming type reproduces
A2 there too. All four planes are in S1's acceptance scope, not just the SwiftUI bridge. Even with the seed predicate fixed, any future
legitimate quarantine of a bridged type reproduces this exact break.

**Cross-module seeding validation requirement.** Stripe's poisoned seed
(`c:@S@NSDirectionalEdgeInsets`) arrives via a **dependency module's** ABI (StripeUICore) and
propagates into StripePaymentSheet through `dependencyQuarantinedNames` — the multi-module
path `56662339` introduced. The other families seed locally. The S1 fix must be validated on
both paths; a fixture that only exercises local seeding would pass with the cross-module arm
still broken.

## 3. Defect B — absence-authority overshoot (MatterSupport)

**Mechanism.** `4dbcf105` added `withdrawOnNoHit: true` at
`TypeDatabaseExtensions.cs:539-542`: a type that misses `AppleTypeSurfaceIndex` (reflected
from **the single platform reference assembly** — Microsoft.iOS/macOS/tvOS/MacCatalyst per
target, never sibling packages) is stamped `AbsentAppleProjection` and its members withdrawn
as `AbsentFrameworkType`. But MatterSupport's `Matter.MTRSetupPayload` is supplied by the
sibling package `SwiftBindings.Apple.Matter`, which the binding `PackageReference`s and which
Microsoft.iOS does not bind at all. The same run *emits* `Matter.MTRNetworkCommissioningWiFiSecurity`
(also absent from Microsoft.iOS) via a different ingest path — the generator contradicts its
own premise in one run. Cascade: `setupPayload` is default-arg param #2 of both inits, so its
withdrawal also kills both multi-arg ctor overloads (one then dropped as `DuplicateSignature`)
— three public API shapes lost to one bad absence verdict.

**Proof.** Executed counterfactual: HEAD generator with only `withdrawOnNoHit` flipped to
`false` regenerates the real ios cell → consumer app compiles with 0 errors; stock generator
reproduces the exact 10 CS1739/CS1061. Same SDK, same swiftinterface, same abi.json.

**Fix direction:** (1) minimal — gate no-hit withdrawal on namespace coverage
(`CoversNamespace`: zero `Matter.*` entries ⇒ the index has no opinion ⇒ fall through to
synthesis; namespace covered but name absent ⇒ withdrawal stands, preserving the legitimate
FBSDKCoreKit `UIKit.*` withdrawal); (2) durable — reflect resolved sibling
`SwiftBindings.Apple.*` assemblies into the index so cross-package references become genuine
*hits* with shape correction; (3) fix the report attribution (`CauseOwner: "Environment"` +
"add a binding for the framework" is wrong when that binding is referenced two files away);
(4) first-party fixture for cross-package type references (extend the
SwiftBindingsTestLibDependency two-module skeleton). Do **not** blanket-revert `4dbcf105` —
its hit-based shape-correction branches are sound and load-bearing.

**Two adjacent authority defects the same stream must cover (paired-review findings,
code-verified):**
- **The USR arm bypasses `withdrawOnNoHit` entirely.** `TypeDatabaseExtensions.cs:608-611`
  withdraws any `IsClangImportedValueTypeUsr` reference "regardless of caller trust",
  *before* the no-hit branch — so a namespace-coverage gate applied only at `:618` leaves a
  second, ungated absence arm; and the predicate itself (`:670-676`) misses `c:@SA@`.
  S2 must either gate this arm consistently or document why it is sound as-is.
- **A cross-namespace bare-name hit suppresses withdrawal it should not.** The index keeps a
  first-writer-wins bare-name map; a qualified miss followed by a bare-name hit in an
  *unrelated* namespace is declined for correction (`TypeDatabaseExtensions.cs:596-605`) but
  leaves `hit` non-null, so the `:618` no-hit withdrawal is bypassed and a synthesized name
  the exact lookup already proved absent is retained. Namespace-coverage gating does not
  inherently close this; note the *effect* is retain-synthesis (pre-`4dbcf105` behavior), so
  treat as a correctness cleanup within S2, not a new red-cell source.
- Namespace coverage is a **Matter-shaped mitigation, not a complete authority**: a sibling
  package contributing a type to an already-covered namespace (e.g. a `SwiftBindings.Apple.*`
  package extending `Foundation`) would still be falsely withdrawn. The sibling-assembly
  reflection is the structurally complete answer; S2 should implement the minimal gate but
  must not claim completeness for it.

## 4. Defect C — RawRepresentable plan/emit asymmetry (Facebook)

**Mechanism.** `EnumHandler.cs:86-87` redirects module-internal enums' Swift plane to a
discard `StringWriter` (correct: an internal Swift enum can't be spelled from the wrapper
module). `EnumHandler.RawRepresentable.cs` then unconditionally emits `LibraryImport`s for
`SBW_{Type}_InitWithRawValue` / `SBW_{Type}_CaseByIndex` (`:135-137`, `:304-306`, `:398-400`,
case properties `:422-460`) without ever consulting the same predicate. 20 module-internal
`enum X: String` types in FBSDKCoreKit (including a synthesized-`Codable` `CodingKeys`) × 2
symbols = the 40 dangling P/Invokes. `WrapperSymbolIntegrityGate` (added `3c3de627`)
correctly hard-fails.

**Proof.** A/B: the `sdk-v0.16.0` generator emits the *identical* 40 dangling references with
zero wrapper definitions — it just had no gate to notice. Instrumented trace confirmed each
affected enum writes its wrapper into a per-type discard writer (the only
`new SwiftWriter(...)` site in the generator).

**Consequence for shipped releases:** every published Facebook binding since the FB program
(~0.14) carries this dead public API — `FromRawValue` + case properties that throw
`EntryPointNotFoundException`. The 0.18.0 red is SWIFTBIND108 *working*.

**Fix direction:** gate the planner on `WrapperValidation.IsTypeOrEnclosingModuleInternal`
(the contract already written in the comment at `EnumHandler.cs:86`); for this shape prefer
tombstoning the RawRepresentable surface over the `FromRawValue(rawValueLiteral)` fallback
(which would emit *wrong* values — CaseByIndex exists precisely because abi.json lacks real
raw values). Defence in depth: an `IsDiscarding` flag on `SwiftWriter` with the P/Invoke path
refusing `SBW_` entry points while the enclosing type's Swift plane discards — a structural
invariant at the emission site, not a prediction gate (freeze policy inapplicable). Fixture:
PartialSuccessKitchen addition per P3 — public `Codable` struct whose `CodingKeys` reaches
the RawRepresentable planner, plus a plain module-internal `enum X: String` (no public
exposure needed or possible — see P3's validity note). Do **not** relax SWIFTBIND108.

## 5. Defects D and E — closure-plane regressions (Stripe; both exposed by `8b607fa3`)

The dedicated Stripe investigation confirmed Defect A independently (bisect to `56662339`;
de-confounded against the Stripe 25.17.0→26.0.0 pin bump: the 0.16.0 generator emits 74/164
StripePaymentSheet types from the *current* 26.0.0 inputs vs HEAD's 18/164) and surfaced two
additional defects, both *bisecting* to `8b607fa3` "Tombstone non-baseline async closures"
(2026-07-24), the second-to-last commit before the release run. Paired review (Grok M1,
Codex High) corrected the attribution: `8b607fa3` touches neither emitter involved — it is
the **exposure** commit for two latent gaps, one in the tombstone plane (May, `1e3af3e5`)
and one in the P/Invoke plane. The mechanism descriptions below incorporate those
corrections; the original agent report (`analysis-stripe.md`) had D's emission site right
but E's causal gate wrong.

**Defect D — CS0246: `StripeCore` namespace not found (StripeConnect).** Not the quarantine
cascade (StripeCore emits 19/111 with zero withdrawals and its assembly builds fine earlier
in the same log). `8b607fa3` changed which closure shapes are rejected, routing
`EmbeddedComponentManager.init` (closure param `() async -> String?`) to the tombstone path;
`ClosureParamTombstoneEmitter.Emit` (`ClosureParamTombstoneEmitter.cs:210/:223` — the file
lives directly under `Emitter/StringEmitter/`, introduced `1e3af3e5`) writes the projected
type name verbatim — `StripeCore.STPAPIClient` — into
`StripeConnect.Types.EmbeddedComponentManager.cs:6281`. StripeConnect's csproj declares only
a `<SwiftFrameworkDependency>` on StripeCore, which is native/framework-level only
(`Sdk.props:137` explicitly does not inject a `PackageReference`), so *any* `StripeCore.*`
name in its compile unit is an unconditional CS0246 — `global::` qualification would not
save it (review-corrected: qualification fixes ambiguity, not a missing assembly reference).
At 0.16.0 the same member emitted a comment-only skip. Fix direction, in causal order:
(1) the tombstone must refuse to name a type from a module the binding has no managed
reference to — degrade to `object?` or comment-only skip, or register the cross-module
reference; (2) SDK-side hard diagnostic when a declared dependency's types appear in the
compile unit with no managed reference — SWIFTBIND080 (`Sdk.targets:2080`) documents the
contract but the "no sibling binding project found" warning fires only for auto-detected
deps; explicitly-declared ones are the blind spot (P14); (3) `global::`-qualification as a
*house rule* is still right, but it is cross-cutting — bare `projection.PublicType` writes
exist in at least `MethodSignature.cs`, `PropertyHandler.cs`, `ProtocolHandler.cs`,
`OperatorHandler.cs`, `EnumHandler.CaseConstruction.cs` — so it belongs at the render
layer across all those sites (S4/P11, per the `ModuleEmitter.cs:94` precedent), not as a
tombstone-only patch.

**Defect E — SYSLIB1051: `Swift.AnyType` in a `[LibraryImport]` (StripePaymentSheet).**
**Mechanism corrected by paired review (two rounds, code-verified).** The closure on
`CustomerSheet.IntentConfiguration.init` is `() async throws -> String` — a **supported
baseline** async-throwing shape (`ClosureHandler.IsBaselineAsyncThrowingClosure`,
`ClosureHandler.cs:1039`; the String-return baseline arm dates to `b6308240`, 2026-04-22).
Because the closure is *supported*, member validation passes and the tombstone — which
absorbs only **unsupported** closures — is never consulted; its struct-ctor `parentOk`
exclusion (`ClosureParamTombstoneEmitter.cs:66-71`, a deliberate, unit-test-locked design
choice) is *not* the causal gate. The break is in the **supported branch of P/Invoke
emission**: `PInvokeEmitter.cs:503-521` bridges an async-throwing closure only when all
four `asyncBridgeEligible` conjuncts hold (`:505-509` —
`UsesCdeclMethodWrapper && IsAsync && Throws && IsBaselineAsyncThrowingClosure`); otherwise
it emits the parameter as `Swift.AnyType` (`:520`). This init is synchronous *and* on the
CallConvSwift/silgen path (no cdecl wrapper), so at least two conjuncts fail
(`StripePaymentSheet.Types.CustomerSheet.cs:753-755` → SYSLIB1051). Worse, it is a
**body/P-Invoke split, not just a validator/P-Invoke split**: the method body still emits
the half-bridge state machine (`AsyncThrowingClosureState` + `Callback_Start`,
`CustomerSheet.cs:649-706`) against the `AnyType` extern. A **twin arm** exists for
non-throwing baseline async closures at `:523-539` (`IsBaselineAsyncNonThrowingClosure` +
`UsesCdeclMethodWrapper && IsAsync`, else AnyType) — same fix class, must be covered
together. Exposure: at 0.16.0 the member was skipped because the demangler mis-marked a
sync init carrying async-closure params as async; `8b607fa3` legitimately fixed detection
to the outermost `FunctionType` only, un-skipping the member without replacing the guard on
this plane. **Handler-path parity is the causal asymmetry (round-2 finding, code-verified):**
the exact guard already exists — `WrapperValidation.HasUnbridgeableAsyncThrowingClosure`
(`WrapperValidation.cs:688`) checks precisely the `asyncBridgeEligible` conjuncts *for both
arms* (throwing and non-throwing baseline) — but only the ordinary method path invokes it
(`MethodHandler.cs:1442`, which skips the member cleanly). The demangler fix rerouted this
now-sync init into `ConstructorHandler` (factory selects sync ctors, `MethodHandler.cs:47`),
whose `Emit` (`:90`) proceeds without ever calling the guard. `PInvokeEmitter`'s `AnyType`
is therefore the *terminal manifestation*; the P/Invoke signature builder cannot itself
cleanly withdraw an already-selected member. Proven independent of the withdrawals: parent
commit `b2ace759` already carries the full Defect-A cascade with zero `Swift.AnyType`
files. Fix direction: (1) **primary — guard-invocation parity**: every handler path that
can carry a supported async-baseline closure (`ConstructorHandler.Emit`, and audit any
sibling paths) must invoke `HasUnbridgeableAsyncThrowingClosure` exactly as the ordinary
method path does, skipping the member with an honest reason before signature building —
this withdraws body and P/Invoke in one decision because the member is skipped whole
(note the pre-dispatch validator is *not* part of this lockstep and needs no change: it
runs before handler dispatch at `IHandler.cs:468` and rightly accepts supported closures,
`MemberEmissionValidator.cs:698` — the guard is deliberately a handler-layer decision, as
at `MethodHandler.cs:1448`); (2) **defense-in-depth at the terminal site**: when `asyncBridgeEligible` is false
for a supported closure (either arm, `:503-521` and `:523-539`), never emit an `AnyType`
P/Invoke — fail loudly instead, so a future handler path that misses the guard cannot
silently ship a broken extern; (3) backstop in the verify-recover loop: an emitted `[LibraryImport]` must never carry
`Swift.AnyType` in its signature — this shape compile-errors (SYSLIB1051), so per the
prediction-gate freeze policy it belongs in verify-recover, not a new hand-coded predictor
(P11); (4) separately *assess* (not assume) widening the bridge to sync/non-cdecl members —
note `UsesCdeclMethodWrapper` is part of the gate, so "make it work for sync members" is
not just flipping the `IsAsync` conjunct; and the tombstone's definite-assignment rationale
for excluding struct ctors is itself questionable (an always-throw body never exits
normally, so CS0171 cannot fire, and C# 11+ auto-defaults struct fields) — settle with a
compile probe. Gate miss was pure fixture coverage: no fixture pairs a baseline
async(-throwing or not) closure with a sync or non-cdecl-wrapper containing member; any
such variant goes red at `--compile-only`.

**Stripe non-regressions (pre-existing, separate tickets, not 0.18.0 blockers):**
StripeUICore emits 0/96 types / 0/538 members, of which only 27 are quarantine withdrawals —
0.16.0 emitted 1/96 from the same input, so the near-emptiness predates this window (though
shipping a package with zero API is a product problem worth its own ticket). StripeCore's
19/111 skip volume (`ModuleInternal`, `@_spi`) is likewise pre-existing.

## 6. Secondary defects (real, smaller, all confirmed)

1. **Module database ignores emission-time withdrawals.** `ModuleDatabaseEmitter` never
   consults the withdrawal set, so `RealityFoundationDatabase.xml` still advertises
   `Transform` and RealityKit's generator emits code against a type its dependency no longer
   declares (`RealityKit.Types.ARView.cs:179,208` — a *generated-code* compile break).
   Review sharpened the scope in both directions: (a) `ModuleProcessor` *already* withholds
   `IsIngestionQuarantined` seeds — the staleness is specifically the emission-time
   *collateral* withdrawals (whole-type units like `Transform`), so "filter quarantined
   types" alone fixes nothing; (b) the invariant "every `<typedeclaration>` has an emitted
   type" is too literal — the database legitimately carries resolution-only records the
   emitter intentionally does not declare (supplement-owned types,
   `Marshaler/IHandler.cs:291-308`; SwiftUI Views, `:317-321`). P5 must assert parity for
   *withdrawal-eligible* records only.
2. **Withdrawals are silent in real builds.** SWIFTBIND046 is `LogWarning` and the SDK runs
   the generator at `StandardOutputImportance="low"` — 13 public types vanished from
   RealityFoundation with zero lines in the build log. A quarantine seeded by a `c:` USR or
   withdrawing ≥N public types should be loud (error-level or fail-closed).
3. **Misleading skip attribution.** Defect B's withdrawals say `CauseOwner: "Environment"`
   with a workaround ("add a binding…") that is already satisfied; Defect A's say
   `CauseOwner: "InputConfiguration"` for perfectly-formed Apple SDK input. Absence-based
   withdrawals must name the authority consulted and why it's believed complete.
4. **Ledger triage quality.** `FirstReachedWithdrawnName` yields `'?'` behind
   `Optional<>`/closure specs and self-references once a type joins the withdrawn set.

## 7. Why 169 commits of green gates shipped 16 red cells

(Condensed from the gate-coverage investigation; full table in `analysis-gaps.md`.)

- **The blind spot is directional.** Every release-path gate asks "does what we emit
  compile / link / pack?" None asks "did we stop emitting something we used to emit?" The two
  surface-adjacent gates are anti-correlated by design: `--skip-surface` logs a vanished
  type's disappeared skip markers as `GONE … improvement`, and the API-manifest ratchet's own
  header says removals "are reported but never fail the gate".
- **The verify-recover program was a surface-shrink machine run against compile-oriented
  gates.** Its success predicate ("emitted C# compiles, reached by withdrawing what doesn't")
  is exactly what the gates measure — a regression the loop "recovers" by dropping a member
  produces a *greener* signal. A program that trades surface for compilability needed a
  surface meter; none existed.
- **`nuke validate` structurally cannot catch this class**, frozen baseline or not: it
  compiles the generated csproj (or a fallback csproj whose only Compile item is the generated
  `.cs`), so a shrunken binding compiles perfectly. Line-count drift is informational.
  The "validate restoration" owner decision is orthogonal to this incident and must not be
  mistaken for the fix.
- **Detection latency was 29 days / 169 commits**, including one release (0.17.0) published
  with no matrix run — its artifact is a 1-cell post-hoc spot check that *overwrote* the slot,
  because `WriteJsonArtifact` unconditionally clobbers
  `regression-validate-{version}.json` with only the current run's cells and records nothing
  about completeness (`Build.RegressionValidate.cs` ~line 1028; the hand-renamed
  `…-0.16.0.FULL.json` shows the hazard had bitten before). `artifacts/` is gitignored, so
  the release evidence trail is untracked local state.
- **Green tests asserted the bugs** (§1). The closest-miss list is specific:
  `AbiIngestionContractTests` (missing USR-family rows), the `IngestionKitchen` quarantine
  legs (cover only module-owned malformed records — a shape that essentially never occurs in
  real input, vs. the `isExternal` foreign stub that always does), PartialSuccessKitchen
  (covers synthesized Codable but its fixture's `CodingKeys` never reaches the
  RawRepresentable planner).
- **Defects D and E are a different gate-miss shape: pure fixture-coverage misses.** Unlike
  A–C, the existing gates *would* have caught both — `--compile-only` goes red on CS0246 and
  SYSLIB1051 — had `8b607fa3`'s own fixture included a cross-module closure param (D) or a
  baseline async(-throwing) closure on a sync / non-cdecl-wrapper containing member (E). The
  lesson is narrower but cheap to encode: a fixture accompanying a change to which shapes
  *reach* an emission path must enumerate parent kinds (class/struct/enum × member/ctor),
  member asyncness/wrapper mode, and module locality (local/cross-module), not just the
  shape that motivated the commit.

## 8. Prevention plan

Owner policy constraints honored throughout: **no third-party libraries or their tests as
in-repo gates** — third-party stays in the downstream pre-release regression flow; in-repo
coverage is first-party fixtures replicating the *pattern*. BindingTests improvements are
welcome; no direct third-party SDK references.

### 8a. In-repo, first-party (ship with the fixes)

| # | Change | Catches |
|---|---|---|
| P1 | BindingTests fixture: retroactive conformance on a CoreGraphics C struct (platform SDK, already imported by the corpus) | Defect A's seed shape, at `--compile-only` cost |
| P2 | Extend SwiftBindingsTestLibDependency: cross-package sibling type reference (module A's binding uses a type packaged by module B's binding, neither in Microsoft.iOS) | Defect B's shape |
| P3 | PartialSuccessKitchen: public `Codable` struct with planner-reaching `CodingKeys` + a module-internal `enum X: String` that reaches the RawRepresentable planner (baseline reseeded in the same commit, per that gate's discipline). Review correction: the originally-proposed "`public func` returning `internal enum`" is not valid Swift ("function cannot be declared public because its result uses an internal type") — no public exposure is needed anyway; FBSDKCoreKit's 20 affected enums were ingested and planned as plain module-internal declarations | Defect C's shape, with the existing no-SWIFTBIND108 + compiles + positive-controls assertions |
| P4 | Unit-test rows using real node shapes for the quarantine exemption predicate; correct the `4dbcf105` tests that assert withdraw-on-no-hit for uncovered namespaces | The false-confidence tests |
| P5 | Module-database ↔ emitted-assembly parity assertion, scoped to withdrawal-eligible records (resolution-only records — SwiftUI Views, supplement-owned types — are intentionally declaration-less and exempt; and the filter must target emission-time collateral withdrawals, since quarantine *seeds* are already withheld by `ModuleProcessor`) | The RealityKit-style downstream generated-code break, for any future withdrawal mechanism |
| P6 | Discard-writer invariant (`SwiftWriter.IsDiscarding` + refuse `SBW_` entry points while discarding). Scope: only `EnumHandler.cs:87` creates a true discard writer today — the flag must be set at that construction site only; the `StringWriter` merge/deferred buffers in `ProtocolHandler`, `MethodClosureBridge`, `AsyncHarnessEmitter`, `EnumHandler.SimpleEnum` are healthy emission paths and must not be painted as discarding | The whole plan/emit asymmetry class at its choke point |
| P7 | Two-sided API-manifest ratchet over BindingTests output (removals fail, reseed-in-same-commit like retargets — the diff is already computed, only the `if` is missing). Honest-scope note from review: the manifest today records only symbol-bearing methods/ctors (`ApiManifestEmitter` no-ops with zero recorded members; recording happens at method chokepoints), so the two-sided ratchet catches *method/ctor* removal only — extending recording to types/properties is part of P7, or its claim shrinks accordingly. P8 inherits the same blind spot | Surface shrink for the shapes the manifest records, forever |
| P8 | Fix `--skip-surface` `GONE` semantics: cross-reference the api-manifest; `GONE` marker + member absent from manifest = regression, not improvement (the reserved-but-unimplemented "Tombstone" detector) | The "shrink reads as improvement" inversion |
| P9 | Loud withdrawals: error-level (or fail-closed) when a quarantine seeds from a `c:` USR or a withdrawal removes ≥N public types; fix `CauseOwner` attributions | Silent multi-type API loss in any consumer build log |
| P10 | Populate or delete `BindingTests/Sources/SurfaceArea/` (currently an empty scanning root that overstates `--skip-surface` breadth) | Honest gate surface |
| P11 | Verify-recover backstop: an emitted `[LibraryImport]` carrying `Swift.AnyType` (or any unresolved-fallback type) is treated as a compile-red to recover from, never shippable output; plus `global::`-qualification as the house rule for emitter-written projected type names — applied at the **render layer**, never at projection construction: `PublicType` is a semantic identity (`TypeProjectionFactory.cs:401`) consumed in equality comparisons (`UrlStringConvenienceOverloadEmitter.cs:156` vs `Foundation.NSUrl`; `ArrayProjection.cs:91`; `OptionalProjection.cs:113`), so prefixing it at construction breaks those branches; the repo already qualifies post-render at `ModuleEmitter.cs:94`. **S4 implementation finding (empirical, compile-gate proven): naive per-site `global::` prefixing at the render sites is unimplementable** — emitter type-name strings mix globally-rooted names (`Foundation.NSUrl`) with namespace-relative/nested paths (`Signing.PrivateKey`, `VariadicConsumer.Buffer`), which are textually indistinguishable, and `global::` on a relative path is CS0400 (real failures in `--compile-only`). The correct design: generalize the existing post-render pass `ModuleEmitter.QualifyNamespaceReferences` (`ModuleEmitter.cs:94`) — which already carries nested-type-exclusion logic — to qualify any root segment that (a) is a real namespace per the TypeDatabase and (b) is shadowed by a type declared in the current module. S4 attempted, hit the CS0400 class, and cleanly reverted; P11's qualification arm is **unlanded** and needs the TypeDatabase-aware design as its own change. Understood as namespace-collision hardening, not a fix for D's missing-reference CS0246 | Defect E's escape hatch; latent CS0246/CS0118 class across all emitters |
| P12 | Withdrawal-plane completeness: every emission plane that names types (SwiftUI bridge, theme bridge, ObjC companion, typed-error registry `ErrorEnumRegistryEmitter`/`ErrorRegistryHelperEmitter`) consults the withdrawal set or is covered by the residual scan; SWIFTBIND120 tests that go red when a bridged type is withdrawn AND when a withdrawn *concrete `Error`-conforming type* stays registered — the registry admits only concrete error-conforming types (`ErrorEnumRegistryEmitter.cs:65`), so a plain bridged-type fixture cannot exercise that plane | Sub-defect A2 — the false completeness proof — for any future withdrawal mechanism |
| P13 | Fixture matrix rule for new rejection/tombstone mechanisms: parent kinds (class/struct/enum × member/ctor) × module locality (local/cross-module) enumerated in the accompanying fixture | The D/E-style pure coverage miss |
| P14 | SDK diagnostic: a `<SwiftFrameworkDependency>` whose module's types appear in the compile unit without a managed `PackageReference`/`ProjectReference` fails with a clear SWIFTBIND error instead of raw CS0246 | Defect D's SDK-side blind spot (explicitly-declared deps bypass the SWIFTBIND080 sibling warning at `Sdk.targets:2080`) |

### 8b. Pre-release flow / downstream (swift-dotnet-packages)

| # | Change | Catches |
|---|---|---|
| F1 | Self-describing, un-clobberable matrix artifact: `complete`/`cellsPlanned`/`filter` + an `inputs` block pinning library versions, apple-version, Xcode; filtered runs write `.partial-N.json`; skill Step 5 requires `complete: true` for SHIP | The 0.17.0 evidence-free release; confounded baselines |
| F2 | Un-gitignore `artifacts/` | Lose-able release evidence |
| F3 | Per-library API-surface snapshot diff in the packages repo: check in each binding's already-emitted `{Module}.api-surface.md` (or api-manifest) and diff on every matrix run — surface shrink becomes an instant, attributable local red *before* the consumer compile | Defects A and B class, on real third-party inputs, where third-party inputs belong |
| F4 *(found during the S5 fix, worse than F1)* | Zero-cell false green: a `--filter`/`--platforms` combination yielding 0 cells logged "Cells to run: 0" and exited **0** — a release gate would read that as a full pass with nothing validated. Now a hard fail (exit 3) | An evidence-free green strictly worse than a clobbered artifact |
| F5 *(decision made during the S5 fix)* | Canonical-slot ownership: a *complete-but-failing* run **replaces** the canonical verdict artifact (the superseded green survives in its own run-scoped dir). A stale green canonical outliving a red full run is the same false-confidence inversion F1 removes | Stale-green canonical masking a later full-matrix red |
| F4 | Add Facebook + MatterSupport generation coverage to the pre-release flow's swift-bindings-side sweep (`validation-libraries.json` — opt-in insight sweep, not a repo gate); any library we ship a package for but never generate in any upstream flow is a permanent blind spot | The "28 commits for a library no gate ever ran" failure |
| F6 *(found during re-verification of these fixes)* | **NuGet global-packages-cache eviction.** A same-version rebuild of the `SwiftBindings.*` packages is invisible to a downstream restore: NuGet does not re-expand a version folder it has already extracted, so a consumer restore silently reuses the previously-extracted bits. The matrix then validates the *old* generator while the log shows a fresh pack — the same false-attribution shape as a clobbered artifact, one layer lower. Fix: the pre-release flow evicts the extracted `swiftbindings.runtime` / `swiftbindings.sdk` / `swiftbindings.templates` / `swiftbindings.apple` folders (plus the `swiftbindings.apple.*` supplements) for the exact version under test before restoring — a targeted `rm -rf` under the resolved global-packages root, deliberately **not** `dotnet nuget locals all --clear` (which would evict every unrelated package and make each run pay a full cold restore) | A matrix verdict attributed to a build that was never actually consumed |

*Numbering note:* two rows above are both labelled **F4** (the zero-cell false green, and the Facebook/MatterSupport generation coverage) — a pre-existing collision from the original drafting. They are left as-authored so prior references still resolve; **F6** is the next free number, hence the jump.

### 8c. Owner decisions (surfaced, not made here)

- Whether the matrix (or its F3 surface-diff subset, which needs no device) becomes a
  mechanical release-gate precondition in `release.yml` (0.17.0 would have been blocked).
- Whether a generation-only surface ratchet should also run somewhere upstream of the
  pre-release flow (policy tension: it consumes third-party inputs; placement options are the
  packages repo per-merge, or pre-release only).
- The standing "validate restoration package" decision — unchanged by this incident, and
  explicitly not the fix for it.

## 9. Fix streams (planned; parallel worktrees; each TDD — fixture/test red first)

| Stream | Scope | Files (primary) | Independent? |
|---|---|---|---|
| S1 | Defect A: seed predicate (`isExternal` modeling preferred) + resolvability pre-check + **A2 withdrawal-plane completeness (P12)** + P1 + P4(A-side) + P9 attribution for quarantine. **Must validate on both local and cross-module (`dependencyQuarantinedNames`) seeding paths** | `SwiftABIParser.cs`, `Node` model, `IngestionQuarantineClosure.cs`, `SwiftUIBridgeEmitter/Collector`, `ThemeBridgeEmitter`, ObjC companion plane, `ErrorEnumRegistryEmitter`/`ErrorRegistryHelperEmitter` (withdrawal consults), BindingTests sources | Yes |
| S2 | Defect B: namespace-coverage gate (minimal) with the sibling-assembly reflection (durable) assessed in-stream + P2 + P4(B-side) + attribution | `TypeDatabaseExtensions.cs`, `AppleTypeSurfaceIndex`, BindingTests dependency sources | Yes |
| S3 | Defect C: planner gates + tombstone choice + P3 + P6 | `EnumHandler.RawRepresentable.cs`, `SwiftWriter`, PartialSuccessKitchen fixture + baseline | Yes |
| S4 | Cross-cutting invariants + gate semantics: P5 (scoped per §6.1), P7, P8, P10, plus P11's `global::` house rule at the render layer (post-render qualification per `ModuleEmitter.cs:94` precedent; never mutate `PublicType` identities) | `ModuleDatabaseEmitter.cs`, `Build.ApiManifestGate.cs`, skip-surface compare, render-site emitters, BindingTests | Mostly (baseline files may overlap S1–S3 reseeds → merge last) |
| S5 | Flow fixes F1–F2 (+F3 scaffolding) in swift-dotnet-packages | `Build.RegressionValidate.cs`, `.gitignore`, skill doc | Yes (different repo) |
| S6 | Defects D+E (one stream — same exposure commit, adjacent planes). **D:** no-managed-reference disqualification in the tombstone (degrade to `object?` or comment-only skip) + P14 SDK diagnostic. **E (review-corrected mechanism, r2):** primary fix is **guard-invocation parity at the handler layer** — the exact eligibility guard already exists (`WrapperValidation.HasUnbridgeableAsyncThrowingClosure`, `WrapperValidation.cs:688`, covers both arms) but only the ordinary method path calls it (`MethodHandler.cs:1442`); `ConstructorHandler.Emit` (`MethodHandler.cs:90`) must invoke it identically (and audit sibling handler paths), skipping the member whole so body, P/Invoke, and validator stay in lockstep. Then **defense-in-depth at the terminal site** — both arms, throwing (`PInvokeEmitter.cs:503-521`) and non-throwing (`:523-539`): when `asyncBridgeEligible` is false, fail loudly, never emit an `AnyType` P/Invoke (the signature builder cannot cleanly withdraw an already-selected member, so this is a backstop, not the fix). Assess (compile-probe, don't assume) widening the bridge to sync/non-cdecl members — `UsesCdeclMethodWrapper` is a gate conjunct, not just `IsAsync` — and re-test the tombstone's definite-assignment rationale. Plus P11 backstop in verify-recover, P13 fixture matrix (baseline async closure {throwing, non-throwing} × {sync method, struct init, non-cdecl-wrapper member} + cross-module tombstone variant). `global::` house rule moves to S4 (cross-cutting, render-layer) | `MethodHandler.cs` (ConstructorHandler), `WrapperValidation.cs`, `PInvokeEmitter.cs`, `ClosureParamTombstoneEmitter.cs`, `MemberEmissionValidator.cs`, verify-recover loop, `Sdk.props`/`Sdk.targets`, BindingTests sources | Yes vs S1–S3; overlaps S4 on the tombstone (a P11 render site) — S4 merges last and rebases its tombstone qualification on S6's result |

Acceptance for the combined result: `nuke test` ≥ floor, `nuke binding-tests --compile-only`
green, `--partial-success-kitchen` green with reseeded baseline in the same commit as its
generator change, then regenerate + rebuild the 16 red cells (plus StoreKit2's latent case
and a FamilyControls spot-check) in the packages repo.

## 10. Release exposure summary

- **0.17.0 (live on NuGet):** clean of Defects A, B, D, and E (A and B post-date it; D and E
  are latent gaps first *exposed* — first reachable in emitted output — by `8b607fa3`,
  2026-07-24). Carries Defect C silently (dead
  FB enum API) exactly as 0.14.0–0.16.0 did. No new pull-the-release urgency discovered, but
  the FB kits' RawRepresentable surface has never worked in any shipped version.
- **0.18.0 (unreleased):** blocked by all five defects; this analysis is the unblock path.

## 11. Resolution addendum — what landed

The fix streams were built in parallel worktrees, then combined into one integrated tree,
reconciled, baselines reseeded (API-manifest before skip-surface, since the manifest gate
throws first), and all gates re-run. Everything below is *landed in that tree*; nothing was
committed by the integration pass.

### 11a. Defect by defect

- **A — quarantine seed predicate.** Clang-imported C aggregate USRs (`c:@S@`, `c:@SA@`,
  `c:@E@`) and `isExternal` re-export stubs with no `mangledName` are no longer classified as
  malformed input, so the proven-closure walk stops withdrawing every type that transitively
  stores one. Both seeding paths — local and cross-module (`dependencyQuarantinedNames`) — are
  exercised, and the ingestion-contract tests that claimed to cover the C-typedef shape now
  actually carry the USR rows.
- **A2 — withdrawal-plane completeness.** The planes that name types without deleting them —
  SwiftUI bridge, theme bridge, typed-error registry — now consult the withdrawal set, and the
  post-emission withdrawal set closes over **nested descendants** so a withdrawn outer type
  cannot leave a nested type declared in the module database or the ownership manifest. The
  descendant expansion matches by verbatim module-qualified name rather than string prefix, so
  a same-prefix sibling (`M.OuterOther` under `M.Outer`) is unmatchable by construction.
- **B — Microsoft.iOS surface index as absence authority.** An absence verdict
  (`withdrawOnNoHit`) is now confined to namespaces the index actually declares something in:
  a miss in an uncovered namespace is no longer evidence of absence, so a member whose type
  lives in a sibling `SwiftBindings.Apple.*` package survives. The unit tests that *positively
  asserted* the old behaviour against a hand-built index of unrelated namespaces were
  corrected. The durable end-state — reflecting sibling assemblies into the index — was
  assessed in-stream and routed to `not-planned.md` with its blocking infrastructure and a
  reopen trigger, because it needs a generator CLI option, SDK plumbing, and a restore-ordering
  guarantee before it can be more than a partial view.
- **C — RawRepresentable planner vs the discard writer.** The discard writer is now explicit
  (`SwiftWriter.IsDiscarding`, set only at the one construction site that genuinely discards)
  and `WrapperValidation.RequireLiveWrapperPlane` refuses any `SBW_` entry-point claim made
  against a discarded plane (P6) — the plan/emit asymmetry closed at its choke point rather
  than per emitter. A module-internal `enum X: String` now has its RawRepresentable surface
  **tombstoned honestly** (`SkipReason.ModuleInternal`, one greppable `// Unsupported:` marker
  per dropped member) instead of emitting `LibraryImport`s naming symbols the discarded plane
  never defines. PartialSuccessKitchen carries the shape and its baseline was reseeded in the
  same change (P3). Recovering the per-case accessors through the value-witness tag-injection
  path — which needs no wrapper symbol — is routed to `not-planned.md` with a trigger.
- **D — closure tombstone naming an unreferenced module.** The tombstone no longer writes a
  projected parameter type from a module the binding has only a native
  `<SwiftFrameworkDependency>` on; the missing-managed-reference case is disqualified rather
  than emitted, and a cross-module tombstone fixture pins it.
- **E — supported-closure plan/emit asymmetry.** The eligibility guard is now invoked with
  **parity across handler paths** (the constructor path included) from a single shared
  predicate consumed by both the decision site and the pre-emission prediction site, so body,
  P/Invoke, and validator can no longer disagree about whether a member is emittable; that
  predicate was subsequently tightened in review to mirror every wrapper-installing condition
  emission applies before the async branch. As defense in depth, the `PInvokeEmitter` AnyType
  arms (throwing and non-throwing) now fail loudly instead of writing an unmarshallable
  declaration. Two assessments the stream was asked to make were made and routed, not
  actioned: widening the async bridge to sync / non-cdecl carriers is an **implementation**
  boundary rather than an ABI one (compile-probed), and the tombstone's struct-ctor
  definite-assignment rationale is **void** at the language level we emit for — both are in
  `not-planned.md` under pending owner decisions.
- **P5** — module-database ↔ emitted-assembly parity, scoped to withdrawal-eligible records
  (resolution-only records stay exempt), so a future withdrawal mechanism cannot silently
  desynchronise the two. **P7** — the API-manifest gate is two-sided: removals fail, reseed in
  the same commit. **P8** — `--skip-surface` no longer reads a surface *shrink* as an
  improvement; a `GONE` marker is cross-referenced against the manifest. **P10** — the empty
  `BindingTests/Sources/SurfaceArea/` scanning root, which made the gate's advertised breadth
  wider than its real reach, is deleted and the scanner now has exactly one root
  (`BindingTests/output/`). **P14** — the SDK emits **SWIFTBIND081** when a declared
  `<SwiftFrameworkDependency>`'s module appears in the compile unit with no managed
  `PackageReference`/`ProjectReference`, replacing a raw CS0246 with an actionable diagnostic.
- **P11's qualification arm remains unlanded** and is now recorded as such: naive per-site
  `global::` prefixing is unimplementable (CS0400 — emitter strings mix globally-rooted and
  namespace-relative paths), and the implementable design — generalizing the existing
  post-render pass over the TypeDatabase's namespace roots — is routed to `not-planned.md`
  with a cross-module name-shadowing red as its trigger. P11's other arm (an emitted
  `[LibraryImport]` carrying an unresolved fallback type is never shippable output) is covered
  by E's loud-failure change.
- **F1–F6.** F1 (self-describing, un-clobberable matrix artifact), F2 (un-gitignore
  `artifacts/`), F4 (zero-cell runs hard-fail instead of exiting 0) and F5 (canonical-slot
  ownership: a complete-but-failing run replaces the canonical verdict) landed in the packages
  repo with F3's scaffolding; **F6** — the NuGet global-packages-cache eviction gap — was found
  during re-verification of these very fixes and is now encoded in the pre-release flow's
  package-staging step (see §8b).

### 11b. Paired external review

Two rounds against the integrated tree, **zero Highs in either**. Round 1 returned six
findings (five Mediums + one Low); round 2, run as a delta review over the round-1 fixes,
returned two Mediums and one Low. All nine were fixed in-tree, TDD (red probe first) wherever
a red was constructible; the round-2 items were prediction/emission divergence via the
debug-default wrapper, parent-withdrawal not cascading to nested descendants, and operator
transaction early-exits that skipped rollback. Two baseline rows moved as a direct consequence
of the new async-closure fixture and were reseeded manifest-first.

### 11c. Re-verification

Interim downstream run: **20/22 cells green**. The two non-green cells are Stripe, and they
are **not** a generator regression — they are P14 working: SWIFTBIND081 fires truthfully on
**25 missing managed references across 10 packages-repo csprojs** that declare a
`<SwiftFrameworkDependency>` without the matching managed reference. That is precisely Defect
D's causal arm, present in the downstream fixtures (wired as `ProjectReference`s) and
previously invisible because it surfaced only as a raw CS0246 in whichever binding happened to
name the type. The diagnostic is reporting a real, pre-existing wiring gap in the fixtures.

**Final verdict run** (run dir `20260727-000956.281Z-p6928-1e8c27a`, 22 cells): **every
non-device cell green — 13/13 across ios-sim, macos and maccatalyst**, with no new reds. That
recovers all 10 originally-ERROR non-device cells from the canonical BLOCK run plus Stripe
ios-sim, whose path required two consumer-fixture corrections that the new fail-closed guards
surfaced in sequence: the 25 missing managed references (SWIFTBIND081), then a missing
`<IsBindingProject>true</IsBindingProject>` in the Stripe3DS2 csproj (SWIFTBIND004) — the only
ObjC binding project in the downstream repo lacking it. The 9 ios-device cells all built,
NativeAOT-published, produced an `.ipa` and installed cleanly, then aborted at launch with the
documented environmental devicectl signature (CoreDeviceError 10002 / NSPOSIXErrorDomain 22);
the unified-log action trace shows `LaunchActionDeclaration` is never invoked, so the app image
is never entered and those cells carry no product signal. The same device cells passed on an
identical build earlier the same day, and a manual relaunch on the same device succeeded —
device state, not code. Re-running the device leg after a device reboot is an owner decision.
The canonical BLOCK artifact was verified byte-identical (sha256 `931b50ec…04aa46`) after every
re-verification run.
