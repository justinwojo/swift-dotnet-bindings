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

## Owner items (unchanged from closeout §6)

Cut `release/sdk-0.18.0` (RuntimeContract floor stays 16); device legs; Programs 3–6 scope
ratification + cross-repo funding call using the dossier evidence.
