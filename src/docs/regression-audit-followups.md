# Regression-audit follow-ups (R1–R6 residual backlog)

This file consolidates the **open** items from the June-2026 regression audits (`R1`–`R6`,
formerly `src/docs/audits/Regression-R*.md`, now deleted). It exists so the parked backlog
survives the deletion of the per-track audit docs.

**Status of the audits themselves:** every *confirmed* finding across all six tracks was
root-cause-fixed and shipped with tests (R1/R2 in commit `a781c97e` and the parser fixes;
R3–R6 in their respective commits). What remains below is the **deferred / inconclusive /
latent** surface — candidates that fell past each track's verification cap or were probe-
confirmed as *latent* (mechanism real, no live trigger on today's toolchain). None is a
known-active defect unless flagged otherwise.

> Severity is the auditor's nominal rating. Most are P2 latents with "no current emission
> site," which `roadmap.md` explicitly calls low-yield ("we are input-poor, not bug-poor").
> Treat this as a resume-able lead list, not a work queue — re-grep `file:line` before acting
> (line numbers drift), and reproduce with a maximum-case red fixture first.

---

## Already tracked elsewhere (do not duplicate)

Three residual gaps from *confirmed* findings are architectural and were tracked against the
Phase-2 session plans — all of which have since shipped. S07a/S07b/S15 removed the
generate-then-strip C# leg, the harness Swift stripper, and the regex swiftinterface parser;
**S08** (layout & lowering truth) shipped the `SwiftValueLayout` oracle. Every session plan was
archived out of the repo once shipped — the `src/docs/sessions/` folder is retired and the
originals live at `/Users/wojo/Dev/SB-Backup-Docs/architecture-review-2026-06/sessions/`. The
remaining promote-worthy follow-ups now live in `src/docs/release-execution-plan.md`.

- **R6-1 residual** — the `SwiftWrapperPostProcessor` re-derives stripping decisions textually
  that the module-aware `InternalTypeReferenceWalker` already makes semantically. A top-level
  current-module internal type named after a bare-emitted stdlib primitive (`String`, `Int`,
  …) can still be false-stripped. Not reachable in current code (no such type exists).
  Durable fix = have the post-processor's `ReferencesInternalType` consult the module-aware
  semantic walker instead of its textual regex. (NOT a path to retiring the post-processor — it
  stays for its non-internal-receiver strips; see `release-execution-plan.md` P1 #1.)
- **R6-4 residual** — unification of the three Optional-layout oracles (`ClassifyFieldType`,
  `LowerOptional`, `FrozenStructHandler.TryComputeOptionalInlineSize`) behind one
  `SwiftValueLayout`. **Largely shipped by S08b:** `SwiftValueLayout` was created (absorbing
  `OptionalAbiClassifier`), `FrozenStructHandler`'s private sizing tables were deleted, and
  `ClassifyFieldType` + the `EnumHandler` read sites were rerouted through it. The remaining
  `LowerOptional`/`TypeLowering` cross-check was **accepted as a documented blind-spot** (a
  derived-size cross-check is tautological — both operands read the same null-sourced
  `InlineSize`), guarded by `LowerReturnType_NullInlineSize_NoCrossCheck_…`. No open work.
- **R6 CoGater + ContextTracker deferred leads** (see R6 below) live in code that **S07a**
  (delete `CSharpWrapperCoGater`) and **S15** (delete the regex swiftinterface parser) plan to
  remove outright — fix opportunistically there rather than patching the doomed code.

One R6-2 residual is **test infrastructure**, not tracked elsewhere: a `CompileAll`-level
integration test that builds a real 2-slice xcframework (one shared member + one
`#if targetEnvironment(simulator)` thunked member) and asserts the device wrapper links with
the sibling thunk retained. `nuke binding-tests --device` does **not** exercise
`FilterThunkAssembly` today (it hand-compiles from unfiltered `.arm64.s`), so this needs new
harness wiring. The R6-2 matching defect itself is fixed and unit-gated.

---

## R1 — TypeDatabase truth refactor

**Deferred candidates**
- `ITypeDatabase.cs:122` — **P2 regression.** `ApplyEmissionResult` default body is `{ }`. Any
  `ITypeDatabase` other than concrete `TypeDatabase` (test doubles, future DBs) silently
  swallows every nested-type rename + emission stamp, no diagnostic. *Probe:* a mock overriding
  `TryGetTypeRecord` (returns a real record) but not `ApplyEmissionResult`; run
  `NameProvider.PrecomputeNestedTypeRenames` on a collision; assert the rename vanished.
- `TypeDatabase.cs:745` — **P2 latent.** `ApplyEmissionResult` write side (exact `_modules` /
  `_outOfModuleTypes` keys) doesn't consult the `_moduleAliases` / umbrella fallbacks the *read*
  side honors. Unreachable today (producer key == storage key), but a foot-gun if any future
  umbrella/alias-qualified decl reaches a stamp site. *Cheap hardening:* route the write through
  the same cascade as the read, or log when an exact-key write misses a cascade-resolvable key.
- `SwiftABIParser.cs:887` — **P2, not a regression.** `GateAbiFormatVersion` reads
  `json_format_version` only on the root `ABIRoot` node. A future digester stamping it elsewhere
  → `null` → spurious SWIFTBIND033 + fail-closed under `--strict-inputs`. *Probe:* `ParseModule`
  with a null-on-root / 8-on-child shape; assert a degradation is recorded.
- `MethodDecl.cs:70` — **P2 maintainability.** `IsSynthesizedAccessor` migration dropped the
  `required` modifier. Value-correct at all sites today, but a future synthesized accessor that
  forgets the flag defaults to `false` → emits *public* (CS0111 / inadvertently-public helper),
  no compile error. *Resume:* restore `required`, or guard-test that every
  `CreatePropertyGetter/Setter/SubscriptAccessor` product has `IsSynthesizedAccessor == true`.

**Open coverage gaps:** no test feeds an ObjC-rooted node (`c:objc(cs)…` + `ObjC` attr, no
`mangledName`) through `ParseModule` (the exact regression shape); no `--strict-inputs` e2e gate
over a real ObjC-class-exporting library; `RegistryContractTests` has no alias/umbrella
`ApplyEmissionResult` case; no test asserts `ABIRoot` is the canonical `json_format_version`
carrier; no compile-time "synthesized accessor ⇒ private" guard.

---

## R2 — Type-grammar + availability-key consolidation

**Inconclusive (latent — mechanism + stale comment confirmed, no live trigger on Xcode 26.3):**
- `SwiftABIParser.cs:3494` — EOF-strict `Parse` throws on ownership/opaque-modified printedNames
  the old lenient parser tolerated, dropping the whole decl. Reachability refuted on today's ABI
  (modifiers live in side fields or canonicalize to `τ_0_0`; 56/56 leading-modifier nodes are
  `OpaqueTypeArchetype`, caught earlier). Latent across toolchain shifts.
- `SwiftABIParser.cs:3591` — `CreateProtocolCompositionTypeSpec`'s printedName-`&`-split fallback
  calls EOF-strict `Parse` with no `try`/`catch`. All probed composition nodes hit this path; a
  trailing-token part would drop the decl, but the digester only emits clean ` & `-separated
  EOF-valid specs today. Latent hardening gap.

**Deferred — Family A (more un-`try`/`catch`'d EOF-strict `Parse` sites, same root cause):**
`SwiftABIParser.cs:3534/3570/3591/3615` (sibling `CreateTypeSpec` sites — `as NamedTypeSpec` +
null-check guards a null result but not a *throw*); `ForeignTypeExtensionEmitter.cs:1181` (param)
and `:289` (property — `StripSwiftAttributes` strips only `@`/`inout`, leaves `some`/`borrowing`/
`__owned`); `ProtocolExtensionEmitter.cs:646` (`ParseParameter` doesn't strip ownership/`some`);
`ConcreteSpecializationEngine.cs:1375` (`NormalizeTypeForComparison` raw-fallback-on-throw →
CSM declines a specialization it used to emit); `TypeSpecParser.cs:426` (root: `Parse("some X")`
throws but `Parse("any X")` succeeds — `some`-vs-`any` asymmetry); `ProtocolExtensionEmitter.cs:631`
(`FindDefaultValueStart` misses `Int=5` without spaces → `=` reaches tokenizer → drop). All P2.

**Deferred — Family B (`SwiftTypeListText` shared-splitter behavior changes):**
`SwiftTypeListText.cs:85` — arrow guard (`'>' && prev=='-'`) suppresses depth-decrement for *any*
`-`-preceded `>` (unpinned); `:85` again — no `if (depth>0)` floor, so a comparison `>` in an un-stripped default
drives depth negative and merges params; `:106` — `IndexOfTopLevelArrow` returns `-1` on a
truncated slice where `IndexOf("->")` found the arrow → three migrated arrow scans mis-slice. P2.

**Deferred — Family C (`MemberSignatureNormalizer` C#↔Swift mirror gaps):**
`:133` (C# strips trailing `...`, Swift mirror collapses a `...` input to `""` — max-destructive
*if* the "type never carries `...`" invariant breaks); `:168` / `AvailabilityWalker.swift:918`
(trailing-dot + space-before-ellipsis divergence: `Foo.`→`Foo.` vs `""`); `:196`
(`CanonicalizeCollectionSugar` bails on closure-element collections — `[() -> Int]` never
converges; shared by both mirrors). P2.

**Open coverage gaps:** no subscript-`some-P`-param coverage; no `sending`-in-closure-type
coverage; EOF-strict throw paths unpinned (no "un-consumed leading modifier must not drop the
decl" guard test); C#↔Swift mirror parity corpus has no trailing-dot / space-ellipsis /
closure-element-collection edge inputs.

---

## R3 — Per-call marshalling tax

Effectively complete (the one confirmed finding is fixed + gated). Residual notes only:
- `SwiftString.cs:383` — **P2 robustness note, not a regression.** `EphemeralSwiftString` ctor
  catches only `DllNotFound`/`EntryPointNotFound`; a different exception *after* partial in-place
  init would leak the `+1`. Not reachable in practice (`initialize(to:)` can't throw a managed
  exception mid-init); the pre-existing heap ctor has the identical shape.
- `EnumHandler.cs:254` — informational. An independent re-audit of all four f9229c50 surfaces
  found no additional defect; the marshalling early-`return true` reorder was reasoned-sound, not
  probed to ground.
- Accepted limit: finalizer-leak hazards can't be made deterministically red (per project
  memory) — abandon-without-Dispose probes complete clean even under contamination.

---

## R4 — SDK auto-dep verb + wiring tripwire

D1/D2 are **closed** (they *are* the C1 fix — `BuildingProject == 'true'` companion). Open:
- **D3** `Sdk.targets:1961` — **P2.** The tripwire anchors on `CoreCompile`, so it now also runs
  at `dotnet pack` time, but SWIFTBIND062/065 text says "before **compilation**" — mis-describes a
  pack-time failure on an Apple-framework binding. *Decision needed:* pack in scope, or correct the
  wording. *Probe:* Apple-framework fixture, `dotnet pack` (passes), disconnect the generate-hook
  anchor, `dotnet pack` again → observe SWIFTBIND062 fail the pack with a "before compilation" msg.
- **D4** `Sdk.targets:1930` — **P2.** Resolver folds a 5th+ pipe into the xcframework field
  (`Split('|', 4)`), but the SDK reconstructs the SWIFTBIND080 `Include=` via *uncapped*
  `Split('|')[4]`, capturing only the first segment of a pipe-bearing path. Reachable only via a
  malformed `_SwiftBindingDependencies` (producer percent-encodes literal `|` as `%7C`). *Probe:*
  drive the SWIFTBIND080 Text on `WARN|Mod|Pkg|1.0.0|/root/a|b.xcframework`; assert `Include=` shows
  the full path, not `/root/a`.
- **D5** `BindingsGeneratorCommand.cs:194` — **P2.** The `--resolve-auto-deps` failure path
  `LogError`s to stdout (`AddConsole()` has no `LogToStandardErrorThreshold`); the SDK Exec captures
  stdout into `_SwiftAutoDepResult` via `ConsoleToMSBuild=true`. Happy path is clean today, but any
  future informational stdout line pollutes the item list. *Fix:* route diagnostics to stderr.
- **D6** `BindingsGeneratorCommand.cs:185` — **P2.** "stdout = frozen-grammar lines only" is an
  unpinned implicit contract. *Probe:* run the real verb with resolvable + unresolvable specs at
  default verbosity; assert every stdout line `StartsWith("PROJREF|")` or `"WARN|"`.

**Open coverage gaps:** no real-`CoreCompile`-graph test (partially closed — the new
standalone-`CoreCompile` non-misfire is pinned; a full graph-disconnect leg remains); no
"stdout = frozen grammar only" assertion (D5/D6); SWIFTBIND080 round-trip unpinned for
over-long / pipe-bearing WARN records (D4).

---

## R5 — Phase-1 emission + runtime-seam fixes

**Inconclusive (P1-candidate, heavily mitigated):**
- `RuntimeContract.cs:45` — `AssertCompatible` throws on **any** version inequality as the first,
  unconditional statement of every binding's `[ModuleInitializer]` (outside the try/catch).
  Confirmed: a forward-incompatible runtime fires an *uncatchable* app-wide load abort (SIGABRT).
  **Mitigation:** the generator stamps a bounded `[X.Y.Z, X.(Y+1).0)` NuGet range, so a cross-minor
  diamond is blocked at *restore* (NU1107), not unified-then-crashed. **Residual risk:** the
  backstop rests on an *unenforced convention* tying `RuntimeContract.Version` to the package minor;
  a contract bump shipped inside a *patch* would slip it. *Deeper probe:* end-to-end restore test
  asserting NU1107 blocks the diamond, plus a guard tying the contract version to the package minor.

**Deferred candidates:**
1. `SwiftRuntimeInfo.cs:135` — **P2.** Switch-less fallback misclassifies device Mono full-AOT as
   NativeAOT. **Superseded** by the §refutation: production PackageReference *and* published-binding
   ProjectReference consumers both get the correct switch value; only the all-ProjectReference local
   dev-sentinel chain (never device-distributed) hits it. Treat as closed unless that config ships.
2. `SwiftMarshal.cs:1003` — **P2 latent.** `MarshalBorrowedFromSwift` double-release protection is
   now a DIM whose default is a no-op; a future `ISwiftObject` root with a finalizable payload but no
   `SuppressPayloadFinalizer` override would double-release a borrowed (+0) handle, silently. All
   current emitters carry the override. *Needs:* a generator-side guard test that every
   `_handle`/`_payload`-owning `ISwiftObject` emits the override.
3. `EnumHandler.Marshalling.cs:259` — **P2, not a regression.** `GetSwiftAbiMetadataType` (tuple
   element metadata, `nint`/8B) vs `GetSwiftRawValueType` (`Int32`/4B) width disagreement for
   tag-only / unrecognized raw values. `EnumAbiWidthConsistencyTests` excludes these. *Needs:* a
   `(Tag, Int32)` tuple round-trip probe to show whether 8B element metadata over-pads.
4. `RuntimeContract.cs:45` — same surface as the inconclusive item from the policy angle: strict
   `!=` contradicts the documented "additive bump is backward-compatible" policy. Fix candidate:
   error only on `generatedAgainstVersion > Version`.
6. `SwiftMarshal.cs:39` — **P2 latent.** `NewFromPayloadDispatcher.TryCreate` returns `null` for
   both a cache miss *and* a registered factory that legitimately returns null → a null-returning
   factory falls through to reflection (asymmetric with `ConformanceDispatcher.TryGet`'s
   nullable-struct sentinel). Production factories return non-null. *Needs:* a factory-returns-null
   probe.
7. `SwiftMarshal.cs:671` — **P2 latent.** `RegisterConformanceFactory` has no Mono guard while
   sibling `RegisterWitnessTable` early-returns on non-NativeAOT. Safe today *because every emitted
   call site is a closed concrete literal* (memory: cache-first concrete registration is Mono-safe),
   but the asymmetry would reintroduce the jit-info.c:918 crash if any site became open/shared-
   generic. *Needs:* a build-time guard that every `RegisterConformanceFactory<…>` site is
   concrete-literal.

(Items 5 and the device-Mono refutation were inspected and found clean — see "verified clean" below.)

**Open coverage gaps:** no per-emitter field-name gate for the finalizer DIM seam
(`BorrowedMarshalFinalizerTests` use fake types — proves wiring, not per-emitter field-name
correctness); `EnumAbiWidthConsistencyTests` tuple-element gap (#3); no restore-time test for the
RuntimeContract cross-minor diamond; no concrete-literal guard for `RegisterConformanceFactory`
call sites (#7).

---

## R6 — Explicitly-unaudited surface

**Deferred leads (P2, probe-idea recorded, not demonstrated):**
- `XCFrameworkResolver.cs:1005` — **highest-value lead.** `<integer>` parsed via `int.Parse`
  (32-bit); a single 64-bit/odd plist integer throws, the try/catch discards the **entire**
  Info.plist → version + minOS lost → placeholder `0.0.0`. *Fix:* `long.Parse` / store `long`.
- `SimulatorOnlyMemberDetector.cs:471` — `FindBlockEnd` brace-counts raw `{`/`}` ignoring Swift
  string literals/comments; a `}` in a default-value string truncates the
  `#if targetEnvironment(simulator)` guard early → device compile failure.
- `AbiContractChecker.cs:516` — `IsNonBlittable` defaults every unknown type to blittable behind a
  7-name allowlist; a genuinely new non-blittable type (`T[]`, `char`, `decimal`, class wrapper,
  `SwiftArray<T>` projection) escapes CC-001/CC-002. The "~100% recall" claim is unsupported.
- `SwiftInterfaceContextTracker.cs:653` `HasUnmatchedOpenParen` string-literal-blindness —
  RESOLVED by S15 Phase B's SwiftSyntax migration: the host parses the real syntax tree, so a
  signature continuation no longer depends on string-blind paren counting and a `)` inside a
  default-value string can't desync the availability key.
- `SwiftInterfaceContextTracker.cs:174` same-line-`{` push gate — RELOCATED to the host's
  `RegexShape.opensOnSameLine`: type/extension scope push is still gated on a same-line `{` (kept
  for output-neutrality), so a brace-on-next-line type is never pushed → members key at module scope
  and lose their `@available` floor. Re-probe in the host walkers (`AvailabilityWalker`/`MemberCollectionWalker`).
- `CSharpWrapperCoGater.cs:549` — `BuildLineToTypeMap` counts every literal `{`/`}` with no
  string/comment lexer; a net-imbalanced brace in an emitted string/comment mis-attributes every
  later line's type scope, defeating *all* co-gater "same-type" safety at once.
- `CSharpWrapperCoGater.cs:1049` — `ExtractMemberName` returns the RHS callee for an
  expression-bodied property (`=> PInvoke_X()` → `PInvoke_X`) → wrong name into the stripped-name
  set and the consumer-facing stripped-API manifest.
- `CSharpWrapperCoGater.cs:1393` — `IsPropertyRemoved` uses unbounded ` {name}` `Contains`
  (prefix-matches a longer property → over-strip a live `ToString()`) and a `!Contains("(")` guard
  that mis-handles an expression-bodied property whose RHS has `(` → CS0103.
- `CSharpWrapperCoGater.cs:964` — `ContainsCallTo`/`ContainsIdentifier`/`FindExemptedPInvokes` are
  string/comment-blind; a symbol name in a comment/string can over-strip a live member or
  spuriously exempt a dead P/Invoke.
- `ThrowsWalker.swift:200` — `ThrowsWalker.buildPrintedName` (empty-`firstName` `continue`) and
  `SignatureFactsWalker.analyze` (unconditional append) build the same cross-side printedName key by
  divergent rules; an empty `firstName` desyncs the typed-throws key → error type drops to
  untyped-throws ABI. Two copies of one key-builder.

> The four `CSharpWrapperCoGater.cs` leads live in code **S07a** plans to delete; prefer fixing
> them as part of that deletion over patching doomed code. (The two `SwiftInterfaceContextTracker.cs`
> leads above were retired by S15 Phase B's deletion of the regex `.swiftinterface` parser — see
> their resolved/relocated notes.)

**Open coverage gaps:** `SwiftWrapperPostProcessor.ReferencesInternalType` cross-module case has no
test (only a same-module collision is covered); `MatchesThunkBlock` name-only fallback has no
same-type sibling-token test; `tools/SwiftInterfaceParser` (5.4k LOC Swift) and
`SwiftWrapperPostProcessor` internals remain unaudited (the two ContextTracker leads + the
ThrowsWalker key-divergence lead sit in this gap).

---

## Recommended BindingTests fixtures (condensed)

Durable end-to-end gates the audits recommended for the items above (Swift shapes only):

- **R1:** ObjC-rooted Swift class with no Swift mangled name (`c:objc(cs)…`, `declAttributes
  ['ObjC','Dynamic']`) asserted to be `SkippedWithReason` with no degradation; a module that
  references UIKit/Foundation ObjC types asserted `DroppedWithError == 0`; a Swift-defined `@objc`
  class *with* a `$s…` mangled name as a negative control; a stored-property/subscript accessor
  round-trip asserting `GetAccessModifier == "private"`.
- **R2:** a subscript with a `some P` index parameter (via SDK-direct Apple-framework mode, which
  emits the `"some …"` form); a `sending`-bearing closure-typed property/return
  (`var producer: () -> sending Box`); a generator unit test driving each unwrapped `CreateTypeSpec`
  site with an un-consumed leading modifier, asserting the decl is degraded, not dropped.
- **R4:** a two-module cross-dependency binding (`Sheet` → `Core`) for the auto-dep happy path +
  tripwire context; an unresolved variant for the `WARN|`/SWIFTBIND080 path (extend toward the
  pipe-bearing-path edge for D4); a plain-identifier control.
- **R5:** the witness-dispatch index fixture is already shipped (`WitnessDispatchIndexLockstep`);
  pin SwiftRuntimeInfo cctor poisoning as a `Swift.Runtime` unit test (set the `IsNativeAot=true`
  switch + simulator RID, read `IsMonoRuntime` twice, assert same exception type + reachable
  conflict text).
- **R6:** cross-module internal short-name collision (`internal struct Data` + `public func
  makeData() -> Foundation.Data`); a sim-only property + sibling device method token collision run
  on `--device`; a metadata-accessor P/Invoke with a non-blittable param; spare-inhabitant Optional
  params (`Optional<Bool>`/`<pointer>`/`<frozen-enum>` + `Optional<Int>` control); a module-named
  `indirect`/`nonisolated` dependency type.

---

## Already investigated — no action (refuted / verified clean)

Logged so future audits don't re-discover them (per `roadmap.md`'s "input-poor" practice):

- **R1:** `ApplyEmissionResult` nested-rename / class-methods loss for alias/umbrella records
  (×3, unreachable — producer key == storage key); the broad ConflictPolicy/freeze-point/supplement
  sweep (verified clean, zero SWIFTBIND045 on real libs).
- **R2:** `ForeignTypeExtensionEmitter.cs:289` property drop (both old + new parsers drop it at
  `ClassifyReturnType` — zero output impact); `AvailabilityWalker.swift:917` `...`-key divergence
  (variadic ellipsis is a separate token, never in `type.trimmedDescription`).
- **R3:** async String-by-value UAF (foreground `+1` capture into the Task before return — refuted
  ×3); finalizer-removal leak/double-release; unbounded/async stackalloc; `EphemeralSwiftString`
  native-ABI over-release (200k-iteration sanitizer probe clean).
- **R4:** the `BuildingProject == 'false'` *disjunction* framing (the correct guard is the
  *conjunction* `== 'true'`); the "`_DetectSwiftBindingTargetKind` anchor drift silently gates 065
  off" claim (a dangling `DependsOnTargets` is MSB4057 — fail-closed, not silent).
- **R5:** device Mono full-AOT misclassification for ProjectReference consumers (buildTransitive
  *does* propagate across the ProjectReference edge); non-generic enum nested in a generic parent
  (digester stamps the parent generic-sig → fail-close is correct); cache-first + de-reflected
  finalizer seam (live probe: every emitter emits the correct `SuppressPayloadFinalizer` field).
- **R6:** the internal-member detection modifier-order / `package` claim (the headline string is
  invalid Swift; the match is unanchored; `package` members are stripped from the public
  swiftinterface entirely). Internal-member detection now lives in the host's `MemberCollectionWalker`.
