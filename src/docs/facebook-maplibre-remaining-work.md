# Facebook + MapLibre — remaining work to ship

**Single source of truth.** This doc supersedes and consolidates the four docs that used to track this
batch (the old `facebook-maplibre-remaining-work.md` triage and `src/docs/ship-sessions/{01-fb3-ns-options,
02-fb2-any-p-collections,02-fb2-deferral}.md`) — all now deleted. Everything below is **future-relevant
only**; completed work is captured as one-line "already done" notes, not re-litigated. Decisions were
locked by the owner 2026-07-05.

---

## Status in one line

**Generator work is complete except two small, agreed items** (a naming dedup and a fail-closed defect
hardening — see the Build session below). Both libraries are feasibility-proven and runtime-green on sim +
device. Everything else remaining is ship-mechanics (pack-and-consume verification, App Store hygiene,
release lanes) and two documented known-limitations.

## What already shipped (do not redo)

- **W-1** — pure-ObjC clang-umbrella BindingTests fixture (5 behavior-asserted shapes + the ML-1 collision
  regression scenario). The durable gate for MapLibre's shape.
- **FB-1** — enum computed-property vs case-name collision recovery (`EnumPropertyRenames` channel). General
  fix; the 6 named FBSDKShareKit members it targeted turned out `@usableFromInline internal` (correctly
  suppressed, not recoverable) — the real public photo/video-source API was already emitted.
- **FB-3** — ObjC `NS_OPTIONS` → Swift type-DB bridge (sibling of the `NS_ENUM` / `NS_TYPED_EXTENSIBLE_ENUM`
  bridges). Unblocked all `Share*Content.validate(options:)` methods. Committed.
- **FB-2 (Swift-protocol half)** — `[any P]` collections (`Array`/`Dictionary`/nested) for **Swift**
  protocol elements, forward projection + reverse dispatch. Committed. Only the `@objc`-element case was
  deferred (now a documented limitation — see below).
- **ML-1** — MapLibre `camera` property/method collision. Fixed (`3e5a0a5e`); guarded by W-1's regression
  scenario.

## Locked ship decisions (2026-07-05)

- **Facebook kit scope: ship all 5** — `SwiftBindings.Facebook.{CoreBasics, AEM, Core, Login, Share}`.
  CoreBasics/AEM/Core are the mandatory dependency closure; Login is the concentrated demand; **ShareKit is
  the differentiator** (the closest existing binding, brandmooffin, ships no ShareKit; Xamarin.Facebook is
  frozen ~6 majors back) and is runtime-green + improved by FB-3. The old "demand-gate ShareKit" caution is
  retired — its cost was generator effort, which is now spent.
- **MapLibre: GO.** Generator-complete; ships on its V-1 alone (does not wait on any Facebook work).
- **FB-1b (naming dedup): BUILD IT** — see Build session, item 1.
- **FB-2 `@objc` container existentials: harden the latent defect, skip the forward feature** — see Build
  session item 2 for the hardening; see Known limitations for why the forward feature is dropped.

---

## Remaining ship checklist

None of this is generator work except the Build session. Do V-1 once per library after that library's
generator work lands. **Until a real consumer links the real package, "shippable" is a claim, not a fact.**

1. **Build session** (the two generator items below). Rides the upcoming SDK/runtime regression cycle.
2. **V-1 MapLibre** — pure-ObjC pack lane. Build the real nupkg (`dotnet nuke BuildLibrary --library
   MapLibre --all-products`), then from a **fresh single-`PackageReference` consumer app**, build + run on
   the iOS Simulator and on device (NativeAOT). Assert the map renders and a delegate callback fires ObjC→C#.
   This is the gap the synthetic gates don't cover (no pure-ObjC nupkg-consumption leg exists).
3. **V-1 Facebook** — mixed ObjC+Swift pack lane. `--mixed-pack` on the real binding: pack the 5 kits, then
   from a single-`PackageReference` consumer build (sim) + NativeAOT-publish (device), and assert Login + a
   Share flow round-trip with the ObjC classes registering exactly once ("Class X is implemented in both …"
   is the failure to rule out).
4. **App Store hygiene** — `nuke binding-tests --appstore-hygiene` (library-agnostic, once). Asserts the
   runtime nupkg embeds as a signed framework and a built `.ipa` is TN2435-compliant. Green before any publish.
5. **Cut releases** via the normal `release/**` flow (MapLibre lane; Facebook lane).

**Document the two known limitations** (below) in the wiki Known Limitations page as part of the release.

---

## Known limitations to document (not bugs — deliberate scope)

- **FB App Links `[any P]` collections (`@objc` element case).** `AppLink.targets` /
  `AppLink.init(sourceURL:targets:webURL:)` / `AppLink.appLink(…)` / `AppLinkFactory.createAppLink` /
  `AppLinkNavigation.navigationType` don't bind. Root cause: `AppLinkTargetProtocol` is an **`@objc`
  protocol** (`NS_SWIFT_NAME(AppLinkTargetProtocol)` over `@protocol FBSDKAppLinkTarget`), so
  `[any AppLinkTargetProtocol]` is the heavyweight `@objc`-container-existential case — out of scope
  (see below). `AppLinkNavigation`'s inits also take `[String:Any]` dictionaries, an independent by-design
  drop. Facebook App Links deep-linking is therefore unsupported; **Login and Share are unaffected.**
- **FB `LoginConfiguration(messengerPageId:)` init.** Being recovered by Build-session item 1 — if that
  item is descoped for any reason, document it here instead.

## Why the `@objc` `[any P]` forward feature is dropped (don't re-investigate)

Enabling forward `@objc [any objcP]` collections is **not** "lift a gate." Per the (now-deleted) deferral
investigation, it requires rewriting a fail-closed regression gate, two novel `.map`-laundering `@_cdecl`
wrapper shapes, a **mandatory** physical-device (NativeAOT) validation, and it still leaves the reverse
direction broken (no sound retainable 8-byte `@objc` carrier exists — that needs new runtime
existential-container plumbing). The payoff is a niche feature (App Links deep-linking) plus a general
capability with little real-world demand (`[any objcProtocol]` collections are rare). Risk/payoff is poor
**regardless of schedule** — this is demand-driven work, not hygiene work. A full turnkey "Design B" revival
spec exists in git history if demand ever appears — retrieve it with
`git show 8d06bd0d:src/docs/ship-sessions/02-fb2-deferral.md` rather than carrying it here.

## Explicitly NOT worth doing (settled — don't chase)

- **Internal DI infra** (`DependentAsObject/Value/Type`) and **underscore SPI** (`_WebDialog`, `_BridgeAPI*`,
  `_ShareUtility`, `_ViewImpressionLogger`) — never consumer surface.
- **`[String:Any]` dictionary bridging** (`Share*Content.addParameters(_:options:)`, `AppLinkNavigation`
  extras/appLinkData) — by-design AnyType-in-container.
- **The 3 Review-tier proxies** (`CAPIReporter`, `SharingContent`, `SharingValidatable`) — reference ObjC
  types/protocols with no Swift TypeDatabase record; zero recoverable consumer value (only a C#-side
  *implementer* would notice, and none exists). FB-3 already gave `ShareBridgeOptions` a record; re-check
  after it whether `SharingContent`/`SharingValidatable` flipped, but don't design toward it.

## Facebook surface — the one-line evidence base

Measured across the 4 consumer kits: **916/2019 members emitted; 71% of skips never-public, 21% by-design,
8% (99) actionable — of which ~45 are internal DI/SPI**. The genuinely consumer-facing, cleanly-fixable
remainder was exactly FB-1 / FB-2 / FB-3 / FB-1b, now all resolved or decided. The primary consumer types
(Settings, Profile, ApplicationDelegate, LoginManager, LoginConfiguration, AccessToken, ShareLinkContent,
SharePhotoContent, ShareVideoContent, ShareMediaContent, ShareDialog) are present and runtime-proven. So
Facebook's ship decision is a product call about polish + demand, not generator completeness.

---

## Build session — the two remaining generator items (kick off next)

Self-contained brief for a fresh Claude session. Read `CLAUDE.md` first — its build/test targets,
zero-regression policy, and "no shortcuts / root-cause fixes" rule are binding. Both items are **general
generator improvements** surfaced by Facebook, not FB-specific hacks, and both ride the upcoming SDK/runtime
regression cycle. All file:line pointers below are **approximate — verify with grep before editing**; the
tree moves.

### Scope

Two independent items. They can land in either order or as separate commits.

### Item 1 — FB-1b: failable-init overload-collapse dedup naming

**Problem.** Two `LoginConfiguration` `init?` overloads erase to the same C# `TryCreate` signature and one is
silently dropped (`DuplicateSignature`):
- `init?(permissions:, tracking:, messengerPageId: String?)` collides with the emitted
  `init?(permissions:, tracking:, nonce: String)` — both erase to
  `TryCreate(IEnumerable<string>, LoginTracking, string, out …)`.
- the 4-arg `+appSwitch:` variant collides the same way.

`messengerPageId` and `nonce` are semantically distinct but both erase to C# `string`, so the first-declared
wins the slot and the sibling is unreachable (no factory lets a caller supply only
`permissions + tracking + messengerPageId(+appSwitch)`).

**Fix — the agreed naming rule (general, backward-compatible).** When N failable-init (or ctor) overloads
collide on erased C# signature, **the first-declared keeps the plain `TryCreate` name; each colliding sibling
is suffixed by its distinguishing parameter label** — `TryCreateWithMessengerPageId`, and the appSwitch
variant disambiguated the same way. This recovers the dropped init **without renaming anything already
emitted** (minimal surprise), and generalizes to any library with erased-signature init overloads.

**Where.** The `DuplicateSignature` skip for constructors is raised on the concrete-emission path
(`Emitter/StringEmitter/Handler/ClassHandler.cs` / `MethodHandler.cs`); the failable-init `TryCreate` name is
produced in `Emitter/StringEmitter/Handler/WrapperEmitter.FailableFactory.cs` and
`ConstructorWrapperEmitter.cs`. Grep `DuplicateSignature` + `TryCreate` to pin the exact collision site and
naming site before editing. Prefer disambiguating at the name site over widening the collision predicate.

**Trap.** Keep the disambiguation confined to the externally-visible factory name — there is no quiet
return/receiver slot to suffix here (unlike an internal wrapper), so the change *is* public API by design.
Verify the recovered `TryCreateWithMessengerPageId` and the unchanged `TryCreate` both round-trip.

**Tests.** Add a Swift type with two failable inits whose parameter labels differ but erase to the same C#
signature (mirror the `LoginConfiguration` shape) to `BindingTests/Sources/SwiftBindingsTestLib/` +
assertions in the matching domain file: assert both factories are reachable and construct distinct instances.
Add an emitter unit test asserting the naming rule (first-wins keeps `TryCreate`, sibling gets the label
suffix) — assert behavior, not exact strings.

### Item 2 — Harden the latent `@objc [any P]` reverse-path over-read (fail-closed)

**Problem (a real latent defect, independent of the dropped forward feature).** The reverse/interface path
is existential-blind. A protocol whose requirement is typed `[any objcP]` (custom, non-Apple `@objc`
protocol) currently (a) is declared in the C# protocol interface **and** (b) receives an `Included`
reverse-dispatch vtable slot — **without** consulting the `@objc`-existential gate
(`ExistentialHandler.HasUnsupportedObjCProtocolExistentialPosition`, enforced only on the concrete-witness
path). Its reverse receiver is then emitted with a 40-byte `ExistentialContainer1` carrier against an 8-byte
`@objc` element stride — **a buffer over-read.** No current fixture exercises this shape, so it's latent, but
it's a genuine hazard behind the compile. A regression/hardening cycle is the right time to close it.

**Fix — drop `@objc`-container-existential protocol requirements fail-closed, in lockstep.** Add the
`@objc`-container-existential detection to **both** the member gate (`Emitter/StringEmitter/
MemberGateEvaluator.cs`, `Evaluate*`) **and** the vtable classifier
(`Emitter/StringEmitter/VtableLayout.cs`, `ClassifyMethod`/`ClassifyProperty`/`ClassifySubscript`) so the
requirement is dropped from the interface declaration **and** its vtable slot **together**. Lockstep is
mandatory: dropping from one but not the other desyncs vtable size → SIGSEGV (see
`.claude/rules/constraints.md` "vtable size desync"). Keep `ProtocolConformanceValidator`'s `:`
conformance-keeping re-check consistent so it doesn't retain a `: IFoo` conformance whose member just went
away (would otherwise CS0535). Grep **every** consumer of both gates before landing so no path is left
half-lifted.

**Scope guard.** This closes the reverse hole by making the shape a clean fail-closed drop — it does **not**
enable the forward `@objc [any objcP]` feature (explicitly out of scope; see Known limitations). Keep the
existing `ObjCExistentialOutOfScopeGate` fixture's Dictionary/tuple/closure/async cases dropped as they are.

**Tests.** Add a fixture protocol with an `[any objcP]` requirement (a custom `@objc` protocol element) that
**would** have hit the over-read; assert it is now cleanly dropped (compile-gate: the member is absent from
the emitted interface and no vtable slot references it, the binding compiles, no runtime over-read). This is
a fail-closed assertion — behavior is "the hazardous member does not emit," not "it round-trips."

### Validation (hard gates — green ≥ baseline before commit)

1. `nuke test` — unit tests; ≥ the `swift_bindings_unit_pass` floor in
   `build/baselines/validation-baseline.json`.
2. `nuke binding-tests` — default iOS Simulator (Mono JIT); regenerates + compiles + runs. ≥
   `runtime_tests.simulator.pass` baseline, 0 fail.
3. `nuke binding-tests --device --device-udid 559479FD-3C60-51E4-8B2C-872D8CBA8B54` — physical iPhone
   (NativeAOT). **Required for item 2** (existential/container behavior is where Mono and NativeAOT diverge);
   recommended for item 1. First `--device` run must NOT use `--skip-regen`.
4. Optional canary: `nuke validate` — item 2 touches shared vtable/gate classification with real blast
   radius, so a real-world sweep is worth it if you have time. If you run it, `git checkout` the ~8
   `-behaviortier` version-stamp files it dirties but **keep** the updated baseline json; treat only a
   `cs_compile`/`swift_compile` drop below baseline as a regression.

### Guardrails

- Root-cause fix, not symptom suppression. No weakened assertions, no `[Skip]` to force green.
- Item 2 is fail-closed hardening — if you find yourself widening emission (enabling a new shape) rather than
  cleanly dropping the hazardous one, stop: that's the out-of-scope forward feature.
- Confirm regenerated output compiles (the `nuke binding-tests` regen does this) — don't assume.
