# Phase 6 — Sibling cascade, cohort accounting, program closeout

Final session of the next-impact program (0.18.0 payload). Full account:
`src/docs/sessions/2026-07-next-impact/07-program-results.md`.

## What shipped

**Corpus harness `_feed` fix** (internal-binding-testing 905e99e, committed last phase, verified
here): siblings referenced by an emitted csproj are built + packed into the run-scoped feed before
the dependent's verdict; one bounded generate retry; ObjC-only deps excluded from the pack closure;
stale `*.swift.ios/0.0.0` NuGet-cache purge between packages. Paired external review: no High, no
false-success path; three Medium classification-precision latents recorded in the closeout §4a
with a realized-risk check showing none fired this run.

**Generator: Apple-supplement reference recording closed over every name-gated supplement-text
emission arm.** Root cause of the InputBarAccessoryView red (blocking MessageKit): the
enum-payload `Foundation.Data` construction/inspection arms emit `Swift.Foundation.Data` without
`AppleSupplementReferences.Record`, so nothing else in the module recording ⇒ csproj lacks the
`SwiftBindings.Apple` PackageReference ⇒ the binding's own C# verify fails CS0234 ⇒ SWIFTBIND114
fail-closed ⇒ SWIFTBIND111. Fixture-first fix (red → green:
`Emit_EnumWithDataPayload_RecordsAppleSupplementReference`), then a whole-codebase sweep closed the
same miss at 13 arms total: enum construction/inspection + offset marshal, closure callback args
(plain + Optional), async Data-return helper, wrapper tuple-element return, TypeConversionHandler
native-remap helpers, ConcreteProtocolSpecializationEmitter inline-struct param/decomposition/return
arms (supplement-homed entries only), and the four MethodClosureBridge `AnyError` arms.
Projection-keyed arms need no Record (DataProjection is factory-only; the factory records).
Do NOT instrument generic fall-throughs (`GetCSharpTypeNameForEnumCase`, `GetPInvokeType`) — they
surface DB records for all types; recording belongs at the name-gated emission arms.

Paired external review of the generator fix (Codex + Grok, r1): **no High from either**. Two
hygiene items fixed (thread-static try/finally Reset in the new fixture; fixture comment reworded
to claim exactly what the assertion pins — the enum path as a whole, any arm satisfying it).
Verified repo-wide that every test reading `AppleSupplementReferences.Current` Resets before
emission, so writer-side residue cannot false-positive an existing test. Recorded residual (both
reviewers, accepted): most of the 13 Record arms have no per-arm collector assertion — a
dropped Record there manifests fail-closed (CS0234 → SWIFTBIND114), and the mechanism is pinned
by the fixture + the MessageKit/InputBarAccessoryView e2e canary; not worth 13 per-arm fixtures.

## Cohort accounting (30 packages vs S09)

Zero ratchet violations. 8 newly green (CocoaMQTT + MessageKit named-input→full-green; Eureka,
Factory, JTAppleCalendar, SwiftMessages, SwiftUICharts, swift-argument-parser compile-red→green,
all with full skip reporting). 6 compile-red residuals in recorded families (operator-return
CS0029 ×2, S04 proxy residuals, CombineCocoa, Moya). 10 SWIFTBIND111 unchanged, all with dossiers.
Sibling classifications: OrderedCollections = silent-tombstone registrar↔handler divergence
(new, recorded); Crypto = ObjC-header C-function-pointer lowering (new, recorded); SwiftDrawDOM =
bare `Scalar` (`Swift.Unicode.Scalar`) qualification gap (new, recorded); EpoxyBars +
IssueReporting + RevenueCat = SWIFTBIND111 RequiresGraphClosure (Program 4 scope).

## Gates at close (all re-run after the generator fix)

- `nuke test`: **15703 / 0** (floor 15702 + new fixture)
- `nuke binding-tests --compile-only`: **green** (fail-closed)
- Default sim run: **3245 / 0 / 38** (second run). First run was 3244/1: single failure in
  `BorrowedCallbackArgLeakProbeTests.TestBorrowedResultCallbackArgReleasesEmbeddedRef` with the
  classified end-state-(a) counter signature (survivor identity present; metadataZeroSkip,
  releaseCatch, and wire-skip counters all zero; wire-destroy entered==completed) — the known
  Mono conservative-GC rooting flake, rerun-legitimate per the flake policy. The diff provably
  cannot affect runtime behavior (Record → csproj emission only; the regenerated csproj was
  byte-identical on the supplement reference pre/post change). Rerun green at full count.
- Device legs deliberately not run this program — device leg recommended before release
  (S05 tuple-conversion marshalling + S02 cross-module P/Invoke re-target), owner-attended.

## Post-closeout addendum: independent Record-coverage sweep

An independent full-map sweep of Foundation.Data Record coverage (run against the pre-fix tree)
confirmed the shipped 13-arm fix and surfaced ONE residual gap it had missed: **method parameter
tuples**. `MethodSignature`'s param-tuple fallback and `PInvokeEmitter`'s tuple arms surface
`Swift.Foundation.Data` verbatim via `TupleHandler.TranslateElementTypeTo{CSharp,PInvoke}`
(explicitly NOT factory-projected, unlike return tuples — which project per-element and therefore
record). Fixed fixture-first (`TupleDataElementParamEmitterTests`, red→green, + an
over-recording guard) with a namespace-gated `TupleHandler.RecordAppleSupplementReferences`
helper called from the three name-emitting arms (MethodSignature param tuple; PInvokeEmitter
supported + cdecl-buffer tuple). Sweep's other candidates verified non-gaps at HEAD: enum
construction/offset/closure arms already closed by the fix; generic enum-marshal fallback is
unreachable for bare Data (name-gated arm intercepts) and Data-bound-generics record via factory
recursion; closure delegate types never surface `Swift.Foundation.Data` at all (a name-gated
`ClosureHandler.TranslateTypeSpecToCSharp` arm projects Data to `byte[]`, and native-remapped
types surface their platform `Foundation.NS*` name); `NativeRemappedProjection`'s single
construction site serves nativeType-without-objcBridgeable records, and the only shipped
supplement-homed record with that shape is Foundation.Data — intercepted earlier by the
recorded DataProjection arm.

**Paired r2 review outcome — a real High, and not the one either round-1 saw.** Codex r2
flagged that the Data-tuple method the Record fix makes publishable ships an ABI-mismatched
P/Invoke; Grok r2 said no High (gate 5b admits the shape because P/Invoke type == C# type).
Both were adjudicated from the code: Codex was right. A non-empty tuple param forces the
@_cdecl wrapper (IsParamTypeCdeclRequired), wrapper eligibility has no tuple guard, and
CdeclParamMapper has no tuple category — so the Swift wrapper takes the tuple through its
fallback arm as `UnsafeRawPointer`, while for a NOT-buffer-marshallable tuple the C#
supported-tuple arm declares the ValueTuple BY VALUE and no buffer is created. Frozen-blittable
non-primitive elements (Data, custom frozen structs) escaped gate 5b because
HasUnmarshalledTupleElements cannot see them (P/Invoke type == C# type) — they were absent
from the gate's own "deliberately unsupported" taxonomy. Pre-existing and broader than the
Record fix (a custom-frozen-struct tuple has the same mismatch with no supplement involvement);
the Record fix only unblocked the accidental whole-binding-red in sole-surface modules.
Fix, fixture-first (`ValidateMethodEmission_TupleParamWithFrozenBlittableStructElement_ReturnsSkip`
red→green + an all-primitive keeps-emitting guard): gate 5b now fails closed on EVERY
non-buffer-marshallable tuple parameter, per the prediction-gate policy (ABI mismatch neither
compiler can see). Protocol requirements are unaffected (they bypass MemberValidationPipeline —
the `(Date, Date)`/`(Int32, Int32)` TupleParamCallback shapes keep their sim-passing coverage),
and `sumPair (Int32, Int32)` stays emitted (buffer-marshallable). Buffer support for
frozen-blittable elements (write the value at its metadata offset, borrowed +0 with keep-alive,
like the String slot) is the lift that would re-admit the shape; until then the tuple arms'
Records are the arm-level invariant (record whenever they emit) plus future-proofing.

**r3 verifying round — the method-path High is closed; one sibling-path High found and fixed.**
Both r3 reviewers confirmed the gate closes the method/ctor path. Codex r3 raised two new Highs
on sibling paths; adjudicated empirically by driving the REAL handlers with a scratch probe
(deleted after use): (1) settable tuple *properties* — REFUTED, unreachable: every tuple-typed
property already fail-closes at MemberEmissionValidator ("Type resolution failed";
`TryGetTypeRecord(TypeSpec)` returns false for all TupleTypeSpecs), proven against
`PropertyHandler.Emit`; (2) tuple-index / settable-tuple-return *subscripts* — CONFIRMED, worse
than described: the probe emitted a `CallConvSwift` P/Invoke taking `ValueTuple<Swift.Foundation.Data,
long>` BY VALUE (StructLayout.Auto — indeterminate layout at the ABI boundary) against the raw
`Tj` thunk, PLUS a projection divergence — public indexer `this[(byte[], long)]` calling an
accessor typed `(Swift.Foundation.Data, long)` → CS1503 → the same whole-binding
SWIFTBIND114→111 red family as the original InputBarAccessoryView defect. SubscriptHandler
emits its accessors itself (never through the method loop), so gate 5b never sees them. Fixed
TDD (2 red → green + get-only-tuple-return and all-primitive-index over-skip guards):
`ValidateSubscriptEmissionCore` now fails closed on any non-buffer-marshallable tuple index
parameter and on a settable subscript whose tuple return isn't buffer-marshallable (the setter
takes it as newValue); get-only tuple returns and buffer-marshallable tuples keep today's
emission. Also fixed Codex's Low: a TupleHandlerTests comment still describing the old
two-predicate gate condition.

**r4 verifying round — the r3 subscript fix had the wrong admit key; corrected and re-verified.**
Grok r4: no High (confirmed the r3 fix sits on the path SubscriptHandler actually uses); its one
Medium (protocol-side subscript mirror) is the same pre-existing bypass family dismissed in r3 —
Grok's own trace shows the concrete conformance path hard-declines subscripts at
`CanEmitSubscript`. Codex r4: one new High, CONFIRMED by a 5-shape probe against the real
SubscriptHandler — the r3 gate keyed on `IsCdeclBufferMarshallableTuple`, but that predicate
answers "can the METHOD path's cdecl buffer carry it"; the subscript path has no buffer and no
per-element conversion layer, so every buffer-marshallable-but-projected element breaks too:
String tuple index → indexer `(string, long)` calls accessor `(Swift.SwiftString, long)`
(CS1503); settable String tuple return → same via setter newValue; class element → accessor
`(Payload, long)` vs P/Invoke `ValueTuple<IntPtr, long>` (CS1503); and BOTH r3 get-only
carve-outs were broken as well — the getter's per-element conversions assume wrapper-decomposed
IntPtr elements but the raw thunk returns raw values (`MarshalFromSwiftObject<SwiftString>(
result.Item1)` with a struct argument; `(*(Swift.Foundation.Data*)(void*)result.Item1)` casting
a struct to void* — CS0030). Fixed TDD (4 red → 14/14 green): new
`TupleHandler.IsAllPrimitiveTuple` predicate; the subscript gate now admits ONLY all-primitive
tuples (raw == public == P/Invoke representation) and gates tuple RETURNS unconditionally
(get-only included; the r3 get-only-Emit fixture flipped to Skip with the probe evidence in its
comment). All-primitive index/return over-skip guards pin the status-quo emission. Method/ctor
gate unchanged (its buffer transport genuinely carries String/class/existential slots).
Probe deleted after use; the shape dumps live under the session scratchpad (`r4-shape*-*.txt`).

**r5 verifying round — no High; one convergent arity defect fixed, reviews closed.** Codex r5
(Medium) and Grok r5 (Low) independently converged on the same residual: `IsAllPrimitiveTuple`
dropped the seven-element arity ceiling the buffer predicate carries, so an 8-element all-primitive
tuple passed the new gate, `SubscriptHandler` reserved the indexer dedup key (SubscriptHandler.cs:147)
BEFORE the accessor resolved the over-arity tuple to AnyType and late-skipped (:186) — suppressing a
later valid subscript sharing the same index parameters (a regression vs the r3 gate, whose buffer
key capped arity). Fixed TDD (red `OverArityAllPrimitiveTupleReturn_ReturnsSkip` → 15/15 green):
`IsAllPrimitiveTuple` now enforces `Elements.Count <= MaxSupportedTupleElements`, failing closed at
the validator before any key reservation. Grok r5's other Lows dismissed with reasons (no direct
predicate unit tests — covered via the pipeline fixtures; baseline trailing newline — style;
Record bypass tests — intentional). Neither reviewer raised a High, so per the re-review gate
(rounds are triggered only by fixed Highs) the paired review is complete at r5.

## Owner items (unchanged from closeout §6)

Cut `release/sdk-0.18.0` (RuntimeContract floor stays 16); device legs; Programs 3–6 scope
ratification + cross-repo funding call using the dossier evidence.
