# Closure-Regression Test-Gap Analysis

> Companion to `REMEDIATION-PLAN.md` §6. Answers the question: *these closure
> regressions reached `main` / a validation sweep before we caught them — why did
> they leak, which test layer was missing, and what now closes each gap?*

This cluster is **three coupled defects** that all surfaced through the same
fixture shape (an optional/throwing Void closure), but leaked for **three
different reasons** at three different layers. Understanding each separately is
the point — "add a test for closures" would not have caught all three.

| # | Defect | Caught by | Layer that *should* have caught it first | Now closed by |
|---|---|---|---|---|
| 1 | CS0103 — `SBW_CreateError_{module}` emitted but Swift helper unregistered → co-gater strips the callback method → dangling field/call-site | full `nuke validate` (Alamofire + YouTubePlayerKit), **after** it landed | emitter unit test on the symbol-registration invariant **+** a BindingTests fixture for the shape | `SwiftErrorMintEmitterTests`, `CSharpWrapperCoGaterTests`, `OptionalThrowingVoidClosures` fixture |
| 2 | Box/unbox — throwing + indirect-return callbacks read context **raw** while the setter **boxed** it → `InvalidCastException` escapes UCO → SIGABRT | `nuke binding-tests --device` (one `[SkipOnSimulator]` test) | emitter unit test on box/raw symmetry across the three callback shapes | `ClosureEmitterDirectTests` box/raw symmetry tests + DRY single-gate |
| 3 | Async-throwing error-mint over-emission — the #1 fix's predicate matched `Throws` ignoring `IsAsync`, emitting a stray helper for a *skipped* property | an **existing** emitter unit test (`Emit_OptionalAsyncThrowingClosureProperty_SkipsEmission`) — i.e. caught *before* landing | (was caught at the right layer) | `Method_AsyncThrowingClosureParam_DoesNotEmitHelper`, `Property_OptionalAsyncThrowingClosure_DoesNotEmitHelper` |

## Defect 1 — CS0103 compile regression (Alamofire, YouTubePlayerKit)

**Why it leaked.** Three layers each had a hole, and the only gate with no hole
runs too late:

1. **No BindingTests fixture exercised the trigger shape.** Existing closure
   coverage had non-optional throwing closures and optional *non-throwing*
   closures, but not the specific combination that breaks: *optional + escaping +
   throwing + Void-return*, forwarded through a **native** path (the
   `_optbuf`/default-parameter/non-optional-setter wrappers) that bypasses
   `ClosureEmitter.GetSwiftClosureAdapterCode` — the only funnel that historically
   *registered* the `SBW_CreateError_{module}` Swift helper. No fixture drove a
   native-forward throwing closure, so the unregistered-symbol path was never
   exercised in-repo.
2. **No emitter unit test pinned the symbol-registration invariant.** The
   contract "if the C# binding emits a `SBW_CreateError_{module}` P/Invoke, the
   Swift `@_cdecl` helper for that symbol is registered" had no unit-level guard.
   The mismatch surfaced only as a *downstream* effect: the co-gater stripped the
   now-unsatisfiable callback method and left its one-line `s_<cb>` field +
   call-site dangling → CS0103 in a different file. The actual fault (an
   unregistered symbol) was never asserted directly.
3. **The gate that *did* catch it — full `nuke validate` — is opt-in (~5 min) and
   not part of the per-commit inner loop.** So it caught the regression only on a
   later sweep, after it had already landed. Correct gate, wrong cadence.

**What now closes it.**
- **BindingTests fixture** `BindingTests/Sources/.../OptionalThrowingVoidClosures.swift`
  + `RuntimeTestsApp/Closures/OptionalThrowingVoidClosureTests.cs`: free-function
  optional param, initializer param, default-parameter shim, and optional *and*
  non-optional settable throwing-Void **properties** — the exact Alamofire
  `requestModifier:` and YouTubePlayerKit `htmlProvider`/`HtmlProvider` shapes.
  Compile-fails before the fix, passes after.
- **`SwiftErrorMintEmitterTests`** pins the invariant at the unit layer: the
  helper is emitted **and registered** for a synchronous throwing-closure
  param/property, and the wiring test
  (`MethodHandlerEmit_ThrowingClosureParam_RegistersCreateError_BeforeContractCheck`)
  proves `MethodHandler.Emit` fires it *before* the wrapper-symbol contract check
  — deleting the handler-layer call fails this even though the policy tests pass.
- **`CSharpWrapperCoGaterTests`** locks the defense-in-depth half: stripping a
  P/Invoke also strips the dangling one-line `s_<cb>` field + `new SwiftClosureData`
  call-site, so a future unregistered symbol degrades to a clean suppression
  instead of a CS0103.

The fixture moves detection from "5-minute opt-in real-world sweep" to "every
`nuke binding-tests --compile-only`" (CI's per-commit gate); the unit tests move
it to "every `nuke test`."

## Defect 2 — box/unbox device crash (`validator: () throws -> Void`)

**Why it leaked.** This is the canonical "fixed one site, missed its siblings"
gap, compounded by a structural sim/device blind spot:

1. **The box/raw gate was threaded into one of three sibling emitters.** The
   contract — on the non-cdecl legacy `SwiftClosureData` path the setter boxes the
   GCHandle in an `_SBClosureCtx`, so the trampoline must read it via
   `GetDelegateFromBoxedContext` — was honored by the *non-throwing* escaping
   callback (`EmitEscapingClosureCallback`) but **hardcoded raw**
   (`GetDelegateFromContext`) in the *throwing* and *indirect-return* callbacks.
   The box-vs-raw ABI invariant had no enforcement that all three callback shapes
   agreed with the write side.
2. **The crashing shape is `[SkipOnSimulator]` — by construction the everyday
   inner loop is blind to it.** Throwing-closure callbacks carry a `SwiftError*`,
   which trips the Mono JIT `!ji->async` assertion (confirmed upstream Issue 1),
   so the test runs **only** on device/NativeAOT. The sim gate — the everyday
   runtime loop — *cannot* catch a box/unbox throwing-closure crash.
3. **It was latent behind Defect 1.** The box/unbox skew can only execute once the
   code compiles; until the CS0103 fix landed, the shape never ran, so the crash
   could not surface.

**What now closes it.**
- **`ClosureEmitterDirectTests` box/raw symmetry tests** assert, at the emitter
  layer, that the throwing and indirect-return callbacks honor the gate
  symmetrically with the non-throwing one: legacy-escaping ⇒
  `GetDelegateFromBoxedContext`; cdecl / default ⇒ raw `GetDelegateFromContext<`.
  These run in `nuke test` — they would have caught the missing gate **without a
  device**, neutralizing the `[SkipOnSimulator]` blind spot for this class.
- **The DRY consolidation** (`WrapperEmitter.Marshalling.EmitClosureCallbacks`)
  computes `useBoxedContext` **once** and passes it to all three `Emit*Callback`
  calls. The three shapes can no longer drift apart — the drift *was* the defect.
- **`TestHolder_SetValidator_DelegateThrows_GracefulFault`** (device-only) exercises
  it end-to-end; device pass count 2588 → 2589.

## Defect 3 — async-throwing error-mint over-emission (caught pre-landing)

**This one is the positive control.** The Defect-1 fix consolidated error-mint
emission to the handler layer with a predicate (`IsThrowingClosureType` /
`MethodHasThrowingClosureParam`) that matched `ClosureTypeSpec.Throws` **without**
mirroring the live emission's async-first branch ordering — so it emitted the
helper for an `Optional<async-throwing closure>` property that is *skipped from
emission entirely* (async-throwing closures propagate errors via the continuation,
never via `SBW_CreateError`). The result was a stray `@_cdecl` helper in
otherwise-empty Swift output.

**Why it did *not* leak.** An **existing** emitter-level unit test —
`PropertyHandlerTests.Emit_OptionalAsyncThrowingClosureProperty_SkipsEmission`,
which asserts *empty* output for the skipped Stripe `ConfirmHandler` shape —
turned red the moment the over-emitting predicate landed. A pre-existing
"skips-emission" assertion caught a *new over-emission* introduced by a fix to an
unrelated defect. This is exactly the layer-appropriate guard Defects 1 and 2
lacked, working as intended.

**What now closes it permanently.** `Method_AsyncThrowingClosureParam_DoesNotEmitHelper`
and `Property_OptionalAsyncThrowingClosure_DoesNotEmitHelper` lock the exclusion
directly against the error-mint policy, so the predicate is pinned independent of
the broader property-emission test.

## The systemic thread (and the durable lesson)

All three are **ABI/symbol invariants enforced at one emission site but not its
siblings, where the only catching gate was an expensive end-to-end run (`validate`)
or a device-only runtime test.** The fix pattern is the same across all three:

1. **Make the invariant a single source of truth** where the structure allows —
   the handler-layer `SwiftErrorMintEmitter` chokepoint (Defect 1), the single
   `useBoxedContext` gate (Defect 2). Drift you cannot express cannot regress.
2. **Add an emitter-level unit assertion of the invariant** — fast,
   layer-appropriate, runs in `nuke test`. This is what moves detection off the
   slow/occasional gates. Defect 3 already had one; Defects 1 and 2 did not, which
   is *why* they leaked and Defect 3 did not.
3. **Add a BindingTests fixture for the runtime half** the unit test cannot see —
   the symbol-stripping CS0103 and the box/unbox SIGABRT are real ABI behaviors
   that only a compile/run gate exercises.

The concrete takeaway, consistent with CLAUDE.md ("BindingTests are the real
end-to-end gate", "New work ships with tests"): **a closure-callback ABI contract
(box/raw, symbol registration, calling convention) needs coverage at *both* layers
— an emitter unit test that catches drift on every `nuke test`, and a BindingTests
fixture that catches the runtime ABI mismatch the unit test cannot.** The CS0103
and box/unbox defects leaked precisely because, when introduced, they had
*neither* — only the opt-in `validate` sweep and a device-only test, both of which
run too late or too rarely to gate the change that introduced them.

## Residual gaps (honest)

- **Throwing-closure runtime behavior remains device-only** (`[SkipOnSimulator]`
  Mono JIT Issue 1). The emitter unit tests are the mitigation, but any *new*
  runtime behavior of a throwing closure (not just box/raw) still needs a device
  run to validate — the sim cannot help. Keep running `--device` for any change to
  throwing-closure marshalling.
- **`ClosureProjection`'s escaping branch is unguarded dead code** (REMEDIATION-PLAN
  §6): its `CallbackDeclarations` emits a cdecl callback wired into a
  `SwiftClosureData` with no Swift-side adapter (a cdecl-vs-Swift-self skew). It is
  unreachable today (zero emission consumers), so no fixture exercises it; if that
  path is ever wired into live emission, it needs both layers of coverage before
  it ships.
