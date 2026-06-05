# Session 7 — Name/key consistency (C2): prep & on-ramp

> **Status: PREP ONLY — no code changes in this doc.** Research/planning to de-risk the execution of Session 7. Owns the §5-tracker row "7 | Name/key consistency | ⬜ not started | 0 / 2".
> Source of truth for the bugs: `Track-C2_Invariant-Drift-Dedup.md` (per-bug mechanism + probes) and `REMEDIATION-PLAN.md` §2/§3/§6 (scope, owner-map, folded-in work). This doc re-pins the audit's line numbers to the **current** tree (the audit was 2026-06-02; Sessions 2–6 have landed since and shifted many) and **settles every scope/design decision** (§8) so S7 opens straight into execution. No owner sign-off pending — the driver made the calls during prep.

---

## 1. Scope (what Session 7 owns — and explicitly does NOT)

Per `REMEDIATION-PLAN.md` §2 (Session 7 row) + §3 (identifier-guard owner map) + the §6 fold-in note, S7 owns exactly:

1. **P1-21** — the C2 confirmed key/name-divergence defects (Track-C2 §1): `IHandler.cs`, `IEnvironment.cs`, `WrapperEmitter.Signature.cs`, and the `constraints.md:18` WasEmitted refresh.
2. **P1-22 verify + orphan rollout** — own the identifier-guard sites in files **no other session edits**, then *verify* (read-only) that all P1-22 sites are covered across S2/S4/S5/S6. Never re-open a file another session already closed (§3).
3. **`constraints.md` drift refresh** — three stale invariant counts (WasEmitted, overload-key, GetPublicMethodName).
4. **The folded-in §6 "synthetic-LOCAL bare-literal" category** — `REMEDIATION-PLAN.md` §6 routes this to S7 as "most naturally folded into Session 7 (Name/key consistency), which already owns identifier hygiene." **§5 below shows this category has largely closed itself** as a side-effect of S6's user-param escape work; the surviving live residual is **two extension emitters** (`ProtocolExtensionEmitter` + `ForeignTypeExtensionEmitter`), fixed in one pass with the proven S6 escape pattern.

**Gate:** `nuke validate` (changes to dedup keys → broad output blast). Run after the emitter sessions (2–6, all ✅ done) have settled so it rebases on their output — which is now the case.

### OUT of scope (do NOT pull in — anti-cascade, `feedback_no_session_cascade` / `feedback_no_autonomous_defer`)

Track-C2 lists **19 deferred candidates (§3)** + **1 inconclusive (§2)** in the *same key-divergence family*. These are **NOT** confirmed P1-21 — they are part of the ~280 deferred pool and belong to a possible future Phase 2 (`REMEDIATION-PLAN.md` §0). Pulling any of them into S7 is the #1 way the ten-session ceiling breaks. They are logged here only so S7 does not "rediscover" them and feel obligated:

- §2 `ProtocolExtensionEmitter.cs` manual dedup key bypass — **inconclusive** (the downstream B15 dedup in `IHandler.cs:442` backstops it end-to-end; only an advisory-filter `constraints.md:16` tidy, not a CS0111 producer). Out.
- §3.1 interface-vs-proxy `propertyNames` mismatch (`ProtocolHandler.cs:371`); §3.2 DIM nint-overload key (`ProtocolHandler.cs:1449`); §3.3 extension projected-key `isSelfReturning`/`parentTypeName` omission; §3.5 `GetProjectedOverloadKey` tombstone collapse; §3.6 `ProtocolSignatureHelper` `parentTypeName`/`CancellationToken` omission; §3.8–§3.19 (try/catch parity, EveryProtocol legacy key, subscript key duplication, cross-pool comparisons, `NormalizeParamTypeForOverloadIdentity` narrowness, fast-path escaped-label mismatch, …). **All deferred. All out.**

If S7's grep-sweep surfaces a *new* same-shape instance of a P1-21 defect in a file S7 already edits → absorb it (§1.2). Anything else → one line in `REMEDIATION-PLAN.md` §6, do not fix.

---

## 2. P1-21 — confirmed defects, re-pinned to the current tree

Root cause (single, multiple manifestations): the **authoritative emitted C# method name applies a sibling-property-collision rename (`Foo`→`FooMethod`/`WithFoo`) and a numeric collision suffix (`Foo2`) that the dedup keys and the same-module override verifier do NOT see.** Emitted name and dedup key disagree → real C# overload collisions slip past dedup (`CS0111`) and overrides bind to the wrong slot.

All sites below were **re-verified PRESENT** on the current branch (`audit-workflows`, post-S6). Line numbers are current.

| # | Site (current) | Status | The defect |
|---|---|---|---|
| A | `Marshaler/IHandler.cs` — `GetProjectedCSharpMethodKey` def **:521**, `GetPublicMethodName` call **:528–529** | PRESENT | Passes `parentTypeName` but **no** sibling-property set → name component omits the `Foo`→`FooMethod` rename. This is the authoritative dedup key (`EmittedProjectedSignatures`). |
| B | `Marshaler/IEnvironment.cs` — `CSharpMethodName` **:145–156**, `SiblingPropertyNames` passed **:152**, suffix `CollisionIndex+1` **:156** | PRESENT (this is the *correct* producer — the contrast) | The single source of truth for the emitted name. The only key participant that passes `SiblingPropertyNames` *and* appends the collision suffix. The keys must mirror THIS. |
| C | `Emitter/StringEmitter/Handler/DefaultParameterOverloadEmitter.cs` — `GetProjectedOverloadKey` **:670**, call **:677–678** | PRESENT | The mandated mirror of (A) (`constraints.md:16` "must match exactly"). Carries the **same** sibling-property omission. Must be fixed in lockstep with (A). |
| D | `Emitter/StringEmitter/Handler/WrapperEmitter.Signature.cs` — `HasMethodInResolvedAncestors` **:462**, `AncestorCSharpNameMatches` **:570**, `ComputeMethodCSharpName` **:587–624** (calls `GetPublicMethodName` at **:617**) | PRESENT | Same-module override verifier recomputes the NameProvider base name with **no collision suffix** and **never reads `ancestorMethod.EmittedCSharpName`** → a derived override of the *second* overload (`Process2`) emits `override Process(...)` and **silently binds to the wrong base slot** (worse than a missing override — no compile error because C# erases nullable-ref annotations). |

### Reference patterns (how each fix should look — these already exist in-tree)

- For (A)/(C): `ProtocolSignatureHelper.GetProjectedCSharpMethodKey` (`Emitter/StringEmitter/ProtocolSignatureHelper.cs` **:114**, passes `propertyNames` **:122**) is the **correct shape** — it accepts and threads `propertyNames`. Two same-named key functions already disagree on this dimension; close the gap.
- For (D): the cross-module populator `ClassHandler.cs` **:582–583** already does the right thing: `var csharpName = method.EmittedCSharpName ?? WrapperEmitter.ComputeMethodCSharpName(method, classDecl, typeDatabase);` — *prefer `EmittedCSharpName` when non-null, fall back to recompute*. The same-module verifier (D) is the path that was written before this lesson. Suggested fix per Track-C2: `AncestorCSharpNameMatches` should prefer `ancestorMethod.EmittedCSharpName` when non-null before falling back to `ComputeMethodCSharpName`.

### ⚠️ The load-bearing design question for (A)/(C) — resolve BEFORE coding

The two static key builders `IHandler.GetProjectedCSharpMethodKey(MethodDecl, ITypeDatabase, ILogger?)` and `DefaultParameterOverloadEmitter.GetProjectedOverloadKey(MethodDecl, ITypeDatabase)` currently receive **only `MethodDecl` + `ITypeDatabase`** — no sibling-property context. The fix must thread the sibling-property set into **both** in lockstep (`constraints.md:16`). The open questions, which decide whether this is a one-liner or a signature ripple:

1. **Where does the sibling-property set come from at each call site?** `MethodEnvironment.SiblingPropertyNames` carries it for the producer (B). For the static builders, is the set derivable from `methodDecl.ParentDecl` (a `TypeDecl` whose `.Properties` are reachable) at key-build time, or does it need to be threaded as a new parameter through the ~30+ callers (`NativeIntOverloadEmitter.BuildOverloadKey`, `ThrowingClosureSimplificationEmitter.BuildOverloadKey`, the completion-handler async wrapper, `EmittedProjectedSignatures` population)? **Deriving from `ParentDecl.Properties` in-builder is the lower-blast-radius option if the property set is fully populated by the time these keys are built** — verify the pipeline ordering (keys are built in the main pass *and* in post-processors; the property decls must already be on the parent). This is the first thing S7 should establish.
2. **Regression risk (the `nuke validate` gate exists for exactly this):** threading the sibling-property set + `EmittedCSharpName` into the keys changes the *key namespace*. For methods with **no** colliding sibling property the renamed name == the bare name, so the key is unchanged — but confirm there is no library where the *current* (buggy) key accidentally produces correct dedup that the fix would perturb. The §6 deferred-candidate notes (cross-pool key comparison) hint the divergence is load-bearing in places (`ProtocolConformanceValidator.cs:447–459` already computes both ways as a workaround). Re-baseline `nuke validate` after the emitter sessions (done) so it rebases cleanly.

*(An independent Grok plan-check on this exact question is recorded in §7.)*

---

## 3. `constraints.md` drift refresh (current counts to write)

Track-C2 §1 and a fresh recount establish the live numbers. `.claude/rules/constraints.md` line numbers are unchanged (16, 18, 27); the **counts** are stale. S7 must update all three to the live values **and re-count at execution time** (don't trust these numbers blind — that staleness is the whole defect being fixed; Sessions after this prep may shift them again).

| constraints.md line | Current doc text (stale) | Live (verified this prep, 2026-06-04) |
|---|---|---|
| **:18** WasEmitted | "Set at **13** emission points across **6** files (MethodHandler x7, PropertyHandler x2, NestedClosureBridge x1, ProtocolExtensionClosureBridge x1, MethodClosureBridge x1, GenericClosureBridgeEmitter x1)." | **23 `WasEmitted = true;` assignments across 12 files.** Per-file: `MethodHandler.cs` ×9, `PropertyHandler.cs` ×3, `ConcreteProtocolSpecializationEmitter.Async.cs` ×2, `ConcreteProtocolSpecializationEmitter.cs` ×1, `ConcreteProtocolSpecializationEmitter.AsyncGenericParent.cs` ×1, `AsyncMethodGenericBridgeEmitter.cs` ×1, `GenericClosureBridgeEmitter.cs` ×1, `KeyPathBagValueSpecializationEmitter.cs` ×1, `MethodClosureBridge.cs` ×1, `MethodGenericBridgeEmitter.cs` ×1, `NestedClosureBridge.cs` ×1, `ProtocolExtensionClosureBridge.cs` ×1. (Total *mentions* incl. readers: 45 across 22 files — the `REMEDIATION-PLAN.md` "~37/~20" was itself undercounted.) **6 files absent from the doc.** Exclusions that are correctly NOT emission points: `IMethodBridgeEmitter.cs` record-default `bool WasEmitted = true`, `ClosureParamTombstoneEmitter.cs` doc comment. |
| **:16** overload/dedup key | "~**26** call sites across **15** files." | Audit found **35 across 16** (Track-C2 §3.7) and flags that the doc count omits the hand-rolled `ProtocolExtensionEmitter.cs:314` inline key. **Re-count live before writing.** |
| **:27** GetPublicMethodName | "~**22** call sites across **15** files." | Audit found **27 across 14** (Track-C2 §3.11). The trap text only mentions `parameterCount` consistency — it should also flag the `propertyNames`/`parentTypeName` divergences that actually bite (the P1-21 root cause). **Re-count live before writing.** |

**Durable guard (Track-C2 §6.4):** convert the stale-doc hazard into a failing test — a generator-side unit/architecture test asserting the count of `WasEmitted = true;` assignment statements under `Emitter/StringEmitter/Handler` (and peers) equals the number documented in `constraints.md:18`, and that every emitter contributing a class instance method/property to the override chain sets the flag on the live `ClassDecl.Methods`/`.Properties` decl. This makes the next forgotten flag a red test, not a silent CS0115.

---

## 4. P1-22 verify matrix (rollout is essentially complete — one correction)

The identifier-guard primitives (built in S1) are `NameProvider.MakeNonCollidingSyntheticName`, `SyntheticNameScope`, `EscapeReservedSwiftWrapperLabel` (reserved set `NameProvider.cs:1526–1540`), `PInvokeEmitHelper.DeduplicateCSharpParamNames`, `SyntheticLocalNames.Resolve`. Per §3 owner-map, each P1-22 *site* is owned by the session that already edits its file. Verified applied at current lines:

| Site / family | File:line (current) | Owning session | Verified |
|---|---|---|---|
| closure-bridge synthetic locals + `self_` P/Invoke | `MethodClosureBridge.cs:330,1149`; `NestedClosureBridge.cs:319,1008` | S2 | ✅ applied |
| protocol-extension closure-bridge `self_` | `ProtocolExtensionClosureBridge.cs:251–261` | S4 | ✅ applied |
| EveryProtocol synthetic locals | `EveryProtocolEmitter.cs` (fixed synthetic-only signatures — no user params; guard not required) | S4 | ✅ N/A by design |
| SwiftUI factory/async scopes | `SwiftUIBridgeEmitter.cs:2752`; `SwiftUIBridgeEmitter.AsyncPattern.cs:670,941,1177` | S5 | ✅ applied |
| CSM reserved-label + body scope | `ConcreteProtocolSpecializationEmitter.cs:763,1174`; `.AsyncGenericParent.cs:891–896` | S6 | ✅ applied |
| generic-bridge reserved-label + body scope | `MethodGenericBridgeEmitter.cs:461,827`; `AsyncMethodGenericBridgeEmitter.cs:544`; `GenericClosureBridgeEmitter.cs:197–199` | S2/S6 | ✅ applied |
| C# body locals via `SyntheticLocals` | `IEnvironment.cs:208`; `PInvokeEmitter.cs:283,290,307`; `MethodMarshalPlanBuilder.cs` (~20 sites) | — | ✅ routed |
| user-param escapes (inverse direction, S6) | `OptionalPointerWrapperEmitter.cs:149,186`; `CrossModuleExtensionEmitter.{Class,Struct}.cs`; `MarkerProtocolOverloadEmitter.cs:253,273`; `KeyPathBagValueSpecializationEmitter.cs:519,602`; + ~20 more | S6 | ✅ applied |

### ⚠️ Correction: `ModuleEmissionContext.cs:82` is a FALSE POSITIVE

`REMEDIATION-PLAN.md` §2 + §3 name `ModuleEmissionContext.cs:82` as S7's "sole-owner orphan" P1-22 site. **It is not a synthetic-name emission site.** Current `ModuleEmissionContext.cs:81–83` is a `Regex` field (`_collisionPattern`) inside `SetCollisionContext()` used by `QualifyForWrapperSource()` to strip Swift module-name prefixes from emitted type references — it rewrites *type-reference strings*, emits no synthetic C#/Swift *local*, and needs no `SyntheticNameScope` guard. **S7 should NOT try to "guard" it.** The audit's orphan claim does not hold against the current tree (the line drifted, or was misclassified at audit time). → S7's P1-22 "orphan rollout" sub-task collapses to: confirm no genuine sole-owner synthetic-name orphan exists (the sweep in §5 found none beyond the ProtocolExtensionEmitter gap, which is a *user-param escape* gap, not a synthetic-local one), and record that in the §5 tracker note.

---

## 5. The folded-in §6 "synthetic-LOCAL" category — reframed: it largely closed itself

`REMEDIATION-PLAN.md` §6 (the S2 synthetic-identifier bullet) routes to S7 a category described as: *hardcoded synthetic LOCALS emitted as bare literals without the P1-22 guard, across emitters S2 never touched* — naming `PInvokeEmitter` (`_self`/`__self`/`self_`), `ProtocolExtensionEmitter` (`cdecl` buffer), `ConcreteProtocolSpecialization*`, `OptionalPointerWrapper`, `CrossModuleExtension*`, `AsyncStream`, `Marker`, `KeyPathBag` — "fix in one pass."

**A fresh sweep of every named suspect shows this category has *mostly closed itself* as a side-effect of S6's comprehensive user-param-escape rollout.** The mechanism: the synthetic locals at issue (`self_`, `__self`, `resultPtr`, `errorOut`, `_by`, `cdecl`, …) are **all members of `ReservedSwiftWrapperParamNames`**. S6 wired `EscapeReservedSwiftWrapperLabel` into the *user-param* side of nearly every wrapper emitter, which pushes any colliding user binding **off** those reserved names *before* it reaches the param list. So the bare-literal injected synthetic can no longer collide with a user identifier — a stronger guarantee than guarding the synthetic itself. Per-suspect verdicts:

| Emitter | Bare-literal synthetics | Verdict |
|---|---|---|
| `PInvokeEmitter.cs` (`_self`/`_selfClass`/`tupleResult{i}Ptr` at :950–992,:302) | P/Invoke **decl** param names (separate `partial` method signature, positional ABI, deduped via `DeduplicateCSharpParamNames`); body locals route through `SyntheticLocals` | **Not a user-scope collision.** Protected. |
| `OptionalPointerWrapperEmitter.cs` (:119–232 inject `resultPtr`/`_resultBuf`/`_self`/`errorOut`) | user params escaped at **:149,:186** | Protected. |
| `CrossModuleExtensionEmitter.Class/Struct.cs` (`self_`/`__self`/`__resultPtr`) | user params escaped via `SwiftBindingName` (`EscapeReservedSwiftWrapperLabel`-backed) | Protected. |
| `MarkerProtocolOverloadEmitter.cs` (`_self`/`__self`) | user params escaped at **:253,:273** | Protected. |
| `KeyPathBagValueSpecializationEmitter.cs` (`_by`/`self_`/`__self`/`anyKp`) | user params escaped at **:519,:602**; `anyKp` is body-only | Protected. |
| `ConcreteProtocolSpecializationEmitter.cs` (`self_`/`resultPtr`/`errorOut`/`__self`) | user params escaped at **:763** | Protected. |
| `AsyncStreamEmitter.cs` / `AsyncSequenceEmitter.cs` (`__self`/`__sbAsync*`/`iter`) | fixed synthetic-only signatures — **no user params in scope** | Cannot collide. |

### The genuine live residual — TWO extension emitters (user-param escape gap)

> **Updated after the Grok plan-check (§7): there are TWO emitters with this gap, not one.** The independent sweep caught a sibling — `ForeignTypeExtensionEmitter.cs` — that I missed. This is exactly the CLAUDE.md "grep the whole codebase for ALL instances before finishing" discipline; S7 must fix **both** in the one pass (same fix-shape), not just the one the audit named.

**(1) `ProtocolExtensionEmitter.cs`** — the sole *protocol*-extension wrapper emitter S6's escape rollout **missed** — it calls `EscapeReservedSwiftWrapperLabel` **zero** times. Confirmed by direct read:

- It injects `_ self_: UnsafeMutableRawPointer` at **:1445** (and **:1695** for the closure path), and a `let cdecl = ...` body local at **:1817** — bare literals.
- It builds the **user** param list via `ComputeUniqueParamNames(parameters)` at **:1448** (and **:1693**), feeding straight into `RenderSwiftParam` at **:1453**/**:1709**. `ComputeUniqueParamNames` (**:2648**) only dedups user-vs-user (an Ordinal `seen` dict); it never escapes against the reserved set.
- Worse, `SanitizeSwiftParamName` (**:2684**) maps a user argument label `self` → **`self_`** at **:2689** — *exactly* the injected receiver param name.

→ **A protocol-extension method whose parameter is named/sanitized to a reserved synthetic** (`self_` via a `self` label, or a literal `resultPtr` / `errorOut` / `cdecl` / `_self` / `_by`) emits a **duplicate Swift binding** in the `@_cdecl`/`@_silgen_name` wrapper. swiftc rejects the function → the build **silently strips** the wrapper → the entry point goes missing → `EntryPointNotFoundException` / runtime crash. Exactly the "#2, SILENT → runtime crash, highest severity" shape S6 fixed everywhere else.

**(2) `ForeignTypeExtensionEmitter.cs`** — the *same* anti-pattern, verified this prep (Grok-surfaced, then read directly): `EscapeReservedSwiftWrapperLabel` count **0**; injects bare `_ self_: UnsafeMutableRawPointer` at **:529** (getter wrapper), **:573** (setter wrapper), **:596** (method wrapper); builds user params via its own `SanitizeSwiftParamName` (**:600**, **:671**, def **:1294** — the same limited keyword map, no reserved-name escape). Emits the public-func wrappers for foreign/cross-module type extensions. Same silent-drop failure mode for a user param colliding with `self_`/`resultPtr`/`errorOut`/etc.

**Fix shape (mirrors S6's proven pattern, do NOT invent new — apply to BOTH emitters):** route the user param names — in `ProtocolExtensionEmitter` the ones returned by `ComputeUniqueParamNames` (both call sites, :1448 and :1693) before they reach `RenderSwiftParam`; in `ForeignTypeExtensionEmitter` the `SanitizeSwiftParamName` outputs at :600/:671 — through `NameProvider.EscapeReservedSwiftWrapperLabel(name, siblings)` against `ReservedSwiftWrapperParamNames`. The user binding is renamed (e.g. `self_`→`__self_`); the *external* Swift call label is computed separately so forwarding to the underlying API is unchanged (Swift `@_cdecl` and C# P/Invoke match by position, not name). Mind `constraints.md`'s "Generic protocol extension ABI" trap: `self_` must stay first and `PInvokeHelperContext` metadata stays suppressed for protocol-extension methods — the escape touches only the trailing user params, not the receiver slot.

**Durable gate (TDD, `feedback_tdd_for_regression_fixes`):** BindingTests fixtures — (a) a protocol with an extension method taking a param named `self_` (or labeled `self`) and a second taking `resultPtr`/`errorOut`; (b) a foreign/cross-module type extension with the same colliding param — each currently strips the wrapper (RED: missing entry point at runtime); GREEN after the escape. Add Swift to `BindingTests/Sources/SwiftBindingsTestLib/` + C# round-trip assertion in the matching domain file. These are the only fixtures in the folded category that exercise a *real* defect; the rest of the category is defense-in-depth that S6 already neutralized.

> **Caveat for S7:** the §5 verdicts come from a read-only Explore sweep + targeted re-reads, not a runtime probe. The "protected by S6's escape" reasoning is sound but should be *confirmed at execution time* — when S7 writes the ProtocolExtensionEmitter fix, regenerate and grep the generated `BindingTests` tree for any surviving bare reserved-name binding in a user-param position across the other emitters, to close the category empirically rather than by argument. (Independent Grok read recorded in §7.)

---

## 6. Recommended BindingTests fixtures (from Track-C2 §6 + the §5 residual)

Durable gates. Defects are compile-time (`CS0111`) or silent mis-dispatch / missing-entry-point, so the gate is `nuke binding-tests --compile-only` (compile must succeed) **plus** a runtime dispatch assertion for the override case. Add Swift to `BindingTests/Sources/SwiftBindingsTestLib/`, C# to the matching `RuntimeTestsApp/` domain file.

1. **Property-collision dedup (A/B).** A class with stored `var conflict: Int` colliding with `func conflict(_ x: Int) -> Int` and `func conflictMethod(_ x: Int) -> Int` (both project to `ConflictMethod(nint)`). Assert compiles (no `CS0111`) and each call surfaces distinct Swift behavior. Include a control sibling without the property to prove the property is the trigger.
2. **Completion-handler async vs native async collision (B).** A class with `var data: Int`, `func data() async -> Int`, `func data(completion: @escaping (Int) -> Void)` — the property forces the `DataMethod` rename; both async overloads project to `DataMethodAsync(CancellationToken)`. Assert compile + both async paths return correctly.
3. **Same-module override with collision-suffixed base (D).** Base with `func process(_ x: Widget) -> Int` + `func process(_ x: Widget?) -> Int` (nullable-ref erasure → `Process`/`Process2`); derived overrides only the optional variant. **Runtime** assertion: hold `Derived` as `Base`, call the member backing the *second* overload, assert the `Derived` override actually runs (the override attached to `Process2`, not `Process`). Decisive because the bug compiles cleanly. Existing same-module override tests (`ClassInheritanceEmissionTests.cs:814–870`) only use Int-vs-String (distinct keys, no suffix) — this fills the gap.
4. **WasEmitted-checklist guard (§3 durable guard).** Generator-side unit/architecture test pinning the `WasEmitted = true;` assignment count to `constraints.md:18`.
5. **Extension reserved-name param (the §5 residual — BOTH emitters).** (a) A protocol extension method with a param named `self_`/labeled `self` (+ one named `resultPtr`); (b) a foreign/cross-module type extension with the same collision. RED = missing entry point at runtime, GREEN after the `EscapeReservedSwiftWrapperLabel` rollout to `ProtocolExtensionEmitter` *and* `ForeignTypeExtensionEmitter`.

---

## 7. Independent plan-check (Grok)

*(Recorded per `/coding-rules`. Grok ran read-only against the current tree on the two scope questions in §2's design-risk box and §5's residual. Independent — no hypothesis fed. Full transcript: `/private/tmp/grok-swift-bindings-20260604-233710-r1.md`.)*

**Grok sessionId: `019e9612-9659-7752-b48a-5782e0d4fdcf`** (resume with `grok -r "019e9612-9659-7752-b48a-5782e0d4fdcf"` for follow-ups).

**Grok round 1 verdict — both questions HIGH, concurring with this prep + one new catch:**

- **Q1 (key/name divergence) — HIGH, real, "not fully masked."** Confirms the dedup key (`IHandler.cs:442`, inside `HandleBaseDecl`) is computed *before* `env`/sibling/`CollisionIndex` exist (env at :481, stamp `EmittedCSharpName = env.CSharpMethodName` at :493–494), so keys + the same-module override verifier operate on a *pre-sibling-rename, pre-suffix* base name while emission + the cross-module stamp use the full name. Backstops (emission always uses `CSharpMethodName`; rename is narrow; ctors hardcode `"ctor"`; no-collision libs unaffected) limit blast radius but do **not** eliminate it — latent `CS0111` on (projected-overload-collision + property-rename) or (async-wrapper + rename) shapes, and silent wrong-slot override on (inheritance + suffix/rename) shapes, both reachable.
- **Q1(b) — resolves §8 open-question #1 (the load-bearing design risk).** Grok traced the call sites: the sibling-property set is **already in scope** at `IHandler.GetProjectedCSharpMethodKey`'s call (`HandleBaseDecl(..., IReadOnlySet<string>? siblingPropertyNames = null)` param at `IHandler.cs:191`), collected by the type handlers just before the methods loop (`ClassHandler` ~:383/398, `FrozenStructHandler` ~:412, `NonFrozenStructHandler` ~:324, `EnumHandler` ~:457/560). For `GetProjectedOverloadKey`, `env`/`context.MethodEnv` (carrying `.SiblingPropertyNames` + `.CollisionIndex`) is in scope at most callers (`DefaultParameterOverloadEmitter:228`, `ConcreteProtocolSpecializationEmitter:502`, `IMethodBridgeEmitter:259`). `ModuleHandler:210` free-funcs correctly pass null (no module-level props). → **The fix is an OPTIONAL param (`IReadOnlySet<string>? siblingPropertyNames = null`), source-compatible, mirroring `ProtocolSignatureHelper:114` — NOT a forced ~30-caller ripple.** A few detached/reduced/pre-injection sites may derive the set from `ParentDecl.Properties` or pass null; none are *genuinely unavailable*.
- **Q1(c) — regression risk LOW–MEDIUM and corrective.** Non-rename cases: `GetPublicMethodName` with null-vs-empty set ⇒ identical keys ⇒ no output change. Rename cases: keys shift to the post-rename base, so dedup *prevents* a latent `CS0111` rather than creating one. Same-module verifier needs the analogous `EmittedCSharpName`-preference update to avoid introducing new override mismatches. Needs fresh BindingTests coverage for rename + overload + inheritance shapes; `nuke validate` + unit pass-count gates catch any shift.
- **Q2 (extension synthetic-name escape) — HIGH, confirmed,** and — the new catch — **`ProtocolExtensionEmitter` is NOT the only residual.** `ForeignTypeExtensionEmitter.cs` has the identical anti-pattern (bare `self_` injected, own `SanitizeSwiftParamName`, **zero** `EscapeReservedSwiftWrapperLabel`). Grok's exhaustive grep found these **two** extension emitters are the *complete* residual set of "user param list built from limited sanitize only, fixed `self_` prepended, no reserved-name escape" — every other emitter that injects bare synthetics already escapes user labels first (or uses fixed synthetic-only signatures / `MakeNonCollidingSyntheticName` for C# locals). Reachability: only synthetic `ProtocolExtensionMethodDecl`/foreign-extension decls from swiftinterface (e.g. GRDB-style inlinable protocol extensions), gated to non-`where`/non-static/non-async/cdecl-compatible shapes on concrete class + non-frozen-struct conformers — real but not "all methods."

**My synthesis (where I agree / diverge):**
- **Agree and adopt:** the optional-param threading answer (Q1b) is better than the two options I'd weighed before the consult (in-builder derivation vs. a forced ~30-caller ripple) — it's source-compatible *and* low-blast-radius because the set is already collected and in scope. Banked as **decision §8.1**: thread `IReadOnlySet<string>? siblingPropertyNames = null` into both static builders, pass the already-in-scope set, mirror `ProtocolSignatureHelper`. One execution-time ordering check remains (property decls populated on the parent before the key is built — Grok's :383-before-:442 trace says yes for the main pass).
- **Agree and adopt:** `ForeignTypeExtensionEmitter` joins `ProtocolExtensionEmitter` as the §5 residual (verified directly, not on Grok's say-so). Both fixed in one pass; second fixture added (§6 #5).
- **Diverge / hold the line on scope:** Grok notes the `ProtocolExtensionEmitter:301-323` *pre-injection* key also uses the no-sibling `GetProjected` — that is Track-C2 **§2 (inconclusive, B15-backstopped)** and Track-C2 **§3.3 (deferred)**, which are explicitly **OUT of S7 scope** (§1). Threading siblings into the *main* `IHandler`/`DefaultParameterOverloadEmitter` builders is in scope; chasing the extension pre-injection key or the other 19 deferred §3 key-divergence candidates is Phase-2 work — do **not** absorb them just because Grok's trace brushed past them. The anti-cascade rule (§1) governs here.
- **No re-review round needed:** this was a read-only *plan* check, not a review of a fix. The one new High (ForeignTypeExtensionEmitter) is now folded into the prep scope; there is no code change yet to re-verify. The re-review loop belongs in S7 execution (Codex/Grok on the actual diff).

---

## 8. Decisions (driver-made — settled, do not relitigate in S7)

These were prep-session calls. They are made; S7 executes against them. Each carries its rationale so a future executor can see *why* rather than reopen the question.

1. **Key-builder fix = optional param, derive in-scope.** Thread `IReadOnlySet<string>? siblingPropertyNames = null` into `IHandler.GetProjectedCSharpMethodKey` and `DefaultParameterOverloadEmitter.GetProjectedOverloadKey`, mirroring `ProtocolSignatureHelper:114`; pass the set already in scope (`HandleBaseDecl` param `IHandler.cs:191`; `env.SiblingPropertyNames` at the overload callers). Source-compatible, low blast radius — not a ~30-caller ripple. Free-func / detached sites pass null. *Rationale:* §7 Grok trace + this prep's reads. *Execution-time check (not a decision — a verification):* confirm property decls are populated on the parent before the key is built on the post-processor paths (the main pass is confirmed, `:383`-before-`:442`).

2. **Override verifier = prefer `EmittedCSharpName`.** In `AncestorCSharpNameMatches`/`ComputeMethodCSharpName` (`WrapperEmitter.Signature.cs:570/587–624`), prefer `ancestorMethod.EmittedCSharpName` when non-null before falling back to recompute — copy the proven `ClassHandler.cs:582–583` pattern. *Rationale:* the cross-module path already does exactly this and was written to avoid this same bug.

3. **Don't touch `REMEDIATION-PLAN.md` during prep.** The other in-flight session has it checked out (`M` in the working tree); editing it risks clobbering their work. This prep doc is the authority on the `ModuleEmissionContext.cs:82` false-positive correction. S7 (which will be in identifier-hygiene code anyway) updates the plan's §3 owner table + §6 fold-in note as part of its own close-out, once the plan is no longer contended. *Rationale:* avoid a cross-session merge collision; keep the correction recorded where it's actionable.

4. **Folded §6 scope is the two extension escapes + a verification grep — NOT a guard-everything sweep.** S7's confirmed runtime work in this category is the `EscapeReservedSwiftWrapperLabel` rollout to `ProtocolExtensionEmitter` *and* `ForeignTypeExtensionEmitter` only. Do **not** add guards to the already-protected emitters (`OptionalPointerWrapper`, `CrossModuleExtension*`, `Marker*`, `KeyPathBag*`, `ConcreteProtocolSpecialization*`, `PInvokeEmitter`, `AsyncStream*`) — that is churn with no reachable defect (§5 proves S6's user-param escape already neutralizes them). The empirical close-out is one regen + grep of the generated `BindingTests` tree for any surviving bare reserved-name binding in a user-param position; if it returns clean, the category is closed by evidence, not argument. *Rationale:* §5 + §7; the anti-cascade / no-churn discipline (§1).

5. **The 19 deferred Track-C2 §3 candidates + the §2 inconclusive stay OUT.** Including the extension *pre-injection* key Grok's trace brushed (`ProtocolExtensionEmitter:301–323`, = Track-C2 §2/§3.3, B15-backstopped). These are Phase-2; absorbing them is the ten-session-ceiling break the plan exists to prevent (§1). *Rationale:* `REMEDIATION-PLAN.md` §0/§1; `feedback_no_session_cascade`.

### Execution checklist (verifications S7 runs — these are facts to confirm, not choices to make)

- Re-pin every line number in §2/§3/§5 against the tree at S7 start (this prep is dated 2026-06-04; further commits may shift them).
- Re-count the `constraints.md:16` overload-key and `:27` GetPublicMethodName call-site totals live before rewriting them (the WasEmitted 23/12 is fresh; the other two are the audit's 2026-06-02 numbers).
- Confirm the post-processor key-build ordering (decision #1).
- Run the regen + grep close-out (decision #4).
- Gate per `REMEDIATION-PLAN.md` §2: `nuke validate` (after the now-settled emitter sessions), plus `nuke test` + `nuke binding-tests` ≥ baseline. The override-dispatch fixture (§6 #3) is a runtime assertion → `--device` is warranted (marshalling/dispatch behavior; Mono vs NativeAOT differ).
