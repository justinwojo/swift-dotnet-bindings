# Objective-C binding generator gaps — consolidated fix list (2026-06-28)

> **This doc is now a `session-runner` execution plan** (`/Users/wojo/Dev/session-runner`,
> one-doc-many-sessions mode) layered on top of the original evidence list. The numbered
> **`## ObjC Session N`** sections below are the units of work; everything from **"Why this doc
> exists"** onward is the evidence each session reads. Run with:
>
> ```bash
> python3 run-sessions.py --repo ~/Dev/swift-bindings-objc-improvements \
>   --doc src/docs/objc-binding-generator-gaps.md --sessions 3 --panes
> ```

## Execution plan — Context (every session reads this)

### What this is

Three end-to-end ObjC binding spikes (Facebook, MapLibre, Firebase) against `SwiftBindings.Sdk/0.16.0`
converged on a set of generator-side Objective-C bugs. This plan packages the fixes into **three
coherent, committable sessions**, grouped by the actual generator subsystem they touch (not by the
Theme A/B/C headings further down). The full symptom / root-cause / validate evidence for each gap ID
(A1, B2, …) lives in the themed sections below — **read the gap's section before touching its code.**

### Branch & coexistence with the Binding-Audit Gameplan

This work lands on branch `swift-bindings-objc-improvements`; the parallel Binding-Audit Gameplan
(`/Users/wojo/Dev/swift-bindings/src/docs/BindingAudit/Gameplan.md`) Sessions 9/10/11 land on `main`.
Both diverge from the same commit, so the only coexistence question is file overlap — it is narrow and
concentrated in **Gameplan Session 9's** files:

- **`Emitter/StringEmitter/MemberGateEvaluator.cs`** — edited by ObjC Session 3 (CS0535 / absent-type
  guards) *and* Gameplan Session 9 (module gate). **ObjC Session 3 must run after Gameplan Session 9
  lands.**
- **`TypeDatabase/AppleFrameworkRegistry.cs`** — ObjC Session 1's one-line OpenGLES entry (B6) vs
  Gameplan Session 9's module reclassification. Trivial; different region — keep B6's edit minimal.
- ObjC Sessions 1 (ObjC emitter subtree) and 2 (MSBuild SDK + ObjC-clang path) touch **nothing** in
  Gameplan 9/10/11 and can run in parallel with them. Gameplan Session 10 (Swift symbol-graph parser)
  never collides with the ObjC clang-AST path.

### Reproduce against CURRENT source first (do not trust the 0.16.0 symptom verbatim)

The evidence below was captured against the *packaged 0.16.0 SDK*; this branch has moved on, and some
0.16.0 symptoms may already be fixed or shifted. Re-verify, don't assume:

- **B5** — `static inline` / `NS_INLINE` functions appear to already be excluded by the clang-AST parser
  (they aren't top-level function decls). Reproduce the undefined-symbol link failure on current source
  before "fixing" it.
- **B3** — `SuppressedProxyReferenceException` already looks like a **per-member skip** (roll back the
  member, emit a no-op/throw stub), not the whole-module abort the 0.16.0 Facebook-Share symptom
  describes. Confirm whether/where it still escapes to module level.
- **B4** — the ObjC enum emitter (`StructsAndEnumsEmitter.EmitEnum`) already emits explicit clang-AST raw
  values verbatim. `AuthErrorCode` is a *Swift* `@objc enum`, so the lost `= 17009` is more likely in the
  parser or the Swift enum path than the ObjC emitter. **Investigate before patching.**

Each session: write the failing repro/fixture first (TDD red), then fix at root cause, then green.

### Validating against the preserved spike worktrees

The three spikes live as locked worktrees under the `swift-dotnet-packages` repo (paths, branches, and
sim UDIDs in the **"Preserved spike worktrees"** table below). They pin the broken 0.16.0 SDK; to test a
fix: build the patched generator → pack a local `SwiftBindings.Sdk` into the packages repo's
`local-packages/`, point the worktree at it (`dotnet nuke BumpSdkVersion --version <local>`), and re-run
the per-library validate commands in each gap's "Validate" block. **In-repo BindingTests are the durable
gate** — reproduce each fixed ObjC pattern as a mixed/ObjC fixture in `BindingTests/` so it stays covered
after the spikes are gone.

### Build/test commands

- `dotnet build src/Swift.Bindings/src -c Debug` **after every generator edit** (regen runs the generator
  from `bin/Debug/`, rebuilt only when missing — a stale binary silently emits pre-patch output).
- `nuke test` — unit/integration per-commit gate.
- `nuke binding-tests --compile-only` then `--sim` — ObjC compile + runtime gate.
- `nuke binding-tests --device --device-udid 559479FD-3C60-51E4-8B2C-872D8CBA8B54` when the change touches
  calling conventions / struct marshalling / P-Invoke (Mono vs NativeAOT differ).

### Conventions (CLAUDE.md + project memory)

No shortcuts / root-cause only; never weaken assertions or skip tests. Grep the whole codebase for ALL
instances of a bug pattern before finishing. After generator changes, verify generated output compiles.
Assert behavior, not exact generated strings. Zero-regression: unit + BindingTests pass counts ≥ baseline
before committing. No doc-file references in code comments — inline the rationale.

---

## ObjC Session 1 — Objective-C emitter codegen correctness (MapLibre)

> **Status: DONE.** All six gaps (B1, B2, B4, B5, B6, B7) fixed at root cause with unit coverage;
> `nuke binding-tests --compile-only` Succeeded, unit suite green, and a **clean regenerated** MapLibre
> binding (no `OpenGLESGlobalUsing.cs`, no `MLNInlineShims` shim, no hand-edited `ApiDefinition.cs`)
> reaches the sim run **10 pass / 0 fail** with the annotation round-trip working (no `InvalidCastException`).
> Each gap's `Resolution:` note below records the mechanism and where it is locked. Sessions 2 and 3 are
> untouched.

The highest-ROI batch: concrete codegen bugs in the ObjC ApiDefinition / StructsAndEnums emitter that
recur for **any** ObjC framework. Two are FATAL (won't launch / won't link); the rest are
correctness/compile bugs. All but B4 validate cleanly against the **MapLibre** worktree, which already
produces a working 104-type binding — no structural unlock needed here.

**Gaps:** B1, B2, B5, B6, B7, B4. Read each gap's section below before coding.

**Subsystem (all under `src/Swift.Bindings/src/ObjC/`):**
- **B1** dup selector — `Emitter/ApiDefinitionEmitter.cs` (`ResolveMethodNameWithDedup` / `EmitMethod` +
  `EmitProperty`). The current dedup *renames* the C# method on a name collision; the registrar abort is
  a duplicate **`[Export]` selector** — drop one member (prefer the property), don't just rename.
- **B2** double-I protocol — `Emitter/ApiDefinitionEmitter.cs` (`EmitProtocol`, the `$"I{proto.Name}"`
  pre-prefix). Emit the bare name and let bgen apply the "I" once. Mind `IsDelegateProtocol` (delegate /
  data-source protocols already use the bare name).
- **B5** inline P/Invoke + absent `[Field]` global — `Emitter/StructsAndEnumsEmitter.cs`
  (`EmitFunction` / `EmitConstant`) and/or `Parser/ClangAstParser.cs`. **Reproduce first** (see Context —
  inline funcs may already be excluded).
- **B6** OpenGLES import — `Emitter/ObjCUsingsEmitter.cs` import arrays (+ register `OpenGLES` in
  `TypeDatabase/AppleFrameworkRegistry.cs` / `apple-frameworks.json` so the startup assertion passes).
  *(Only ObjC byte that touches a Gameplan-S9 file — keep the edit minimal.)*
- **B7** property vs synthesized-method collision (`camera`) — `Emitter/ApiDefinitionEmitter.cs`
  disambiguation. Low severity.
- **B4** enum raw values — **investigate first** (`StructsAndEnumsEmitter.EmitEnum` already emits explicit
  values; the loss is likely in the parser or the Swift `@objc enum` path). Fix wherever the explicit
  `= 17009` is dropped. Runtime confirmation needs Firebase (gated on Session 2), so land this on a
  generator-output/unit assertion that the literal survives.

**Tests:** BindingTests fixtures (mixed/ObjC domain) for — a class with a setter selector that is also an
explicit method (B1); a protocol-typed parameter that must accept a conforming class with no
`InvalidCastException` (B2); an `NS_INLINE` math helper that must not produce an undefined symbol (B5); an
`@objc enum` with an explicit large raw value (B4). Unit tests for each emitter change.

**Dependencies:** none. Runs in parallel with Gameplan 9/10/11.

**Validation:** `nuke test`; `nuke binding-tests --compile-only` + `--sim`. Worktree: a **clean
regenerated** MapLibre binding (no `OpenGLESGlobalUsing.cs`, no `MLNInlineShims` native shim, no
hand-edited `ApiDefinition.cs`) builds, launches, and the sim run reaches **10/0**
(`dotnet nuke ValidateSim --library MapLibre --device-udid 79B0459B-6A8C-43AC-B444-FD8809BC65B6`).

---

## ObjC Session 2 — Third-party ObjC xcframework as a first-class binding target (Theme A)

The structural unlock. A third-party ObjC xcframework currently falls through gates designed for Apple
*system* frameworks (→ empty assembly), and cross-framework `#import`s fail because dependency `-F` paths
aren't threaded into the ObjC clang AST-dump. Land both and Facebook (Login) and Firebase (Core) generate
at all. Use MapLibre's working csproj as the reference and reconcile against Firebase's failing one (both
paths in the "Reconcile A1 vs. the MapLibre success" note below).

**Gaps:** A1, A2.

**Subsystem:**
- **A1** ObjC-mode gating — `src/Swift.Bindings.Sdk/Sdk/Sdk.targets`. Generalize the
  `_SwiftBindingTargetKind == 'AppleFramework'` gates (ObjC type detection, synthesize-xcframework,
  `IsBindingProject` / `ObjcBindingApiDefinition` bgen wiring, `--objc` flag) to any ObjC xcframework, and
  **fix the item-injection ordering** so the generated `ApiDefinition.cs` is registered as
  `ObjcBindingApiDefinition` *before* the iOS bgen target runs (the "No API definition file specified"
  failure).
- **A2** clang `-F` threading — `src/Swift.Bindings/src/ObjC/Parser/ClangAstInvoker.cs`
  (`InvokeClangAstDump`) + `BindingsGeneratorCommand.cs` + `ObjCPipeline.Run`. Thread every
  `--framework-dependency`'s resolved slice dir into the AST-dump `-F` flags (and pass `-fobjc-arc`);
  today the resolved deps reach only the Swift wrapper compiler, not the ObjC pipeline.

**Tests:** a third-party xcframework is awkward to host in BindingTests, so the primary gate is the
worktrees plus unit coverage of the SDK target ordering and the `-F` threading in the generator command.

**Dependencies:** none (independent of Session 1; no shared files with Gameplan 9/10/11).

**Validation:**
- Firebase: `dotnet nuke BuildLibrary --library Firebase --all-products` → `SwiftBindings.Firebase.Core.dll`
  binds real `FIRApp` / `FIROptions` (non-empty), no `objc_msgSend` bootstrap needed.
- Facebook: `dotnet nuke BuildLibrary --library Facebook --all-products` → `FBSDKLoginKit` produces `.cs`
  (no "refusing to emit" / "file not found"); `SwiftBindings.Facebook.Login.dll` exposes `LoginManager` /
  `LoginConfiguration` / `LoginButton`.

---

## ObjC Session 3 — Graceful degradation + cross-binding type guards (Facebook/Firebase)

Convert hard failures into shippable, thinned surfaces: skip an unconstructable / unresolvable member
with a warning instead of aborting the module or emitting uncompilable C#. These are **Swift-emitter**
guards (not the ObjC emitter), surfaced by ObjC-heavy SDKs.

**Gaps:** B3, C1, and D2's CS0535 conformance guard.

**Subsystem:**
- **B3** protocol-existential proxy — `src/Swift.Bindings/src/Emitter/StringEmitter/`
  (`SuppressedProxyReferenceException.cs`, `ProtocolProxyEmissionPolicy.cs`). **Reproduce first** — this
  already looks like a per-member skip; confirm whether/where it still escapes to a whole-module abort
  (the 0.16.0 Facebook-Share symptom) and close that path.
- **C1** absent-type skip — generator type resolution. When a member references a framework type absent
  from the available C# bindings (e.g. `StoreKit.Transaction`), skip it with a `SWIFTBIND` warning rather
  than emitting uncompilable C# (the general guard; binding `StoreKit.Transaction` is the narrower
  alternative).
- **CS0535 conformance guard (D2)** — `Emitter/StringEmitter/ProtocolConformanceValidator.cs` /
  `MemberGateEvaluator.cs`. When a protocol member is skipped, also drop the conformance declaration (or
  emit a throwing stub) so the result compiles. **Shared file with Gameplan Session 9.**

**Tests:** BindingTests / unit fixtures for — a module with one unconstructable proxy member that must
degrade (rest of module still emits); a member referencing an absent framework type that must be skipped,
not emitted; a partially-skipped protocol conformance that must still compile (no CS0535).

**Dependencies:** **Run after Gameplan Session 9 lands** (shares `MemberGateEvaluator.cs`). Full worktree
validation of B3/C1 also needs Session 2 (Facebook must generate first).

**Validation:** `nuke test`; `dotnet nuke BuildLibrary --library Facebook --all-products` → `FBSDKShareKit`
produces `.cs` and `SwiftBindings.Facebook.Share.dll` exposes `ShareLinkContent` / `ShareDialog` /
`SharePhotoContent`; `FBSDKCoreKit.cs` compiles (no `CS0234`). Then triage the documented skipped-member
volume (D2).

---

## Why this doc exists

We ran three end-to-end binding spikes — **Facebook (Meta) iOS SDK**, **MapLibre Native iOS**, and
a **Firebase Auth bake-off vs. AdamE.Firebase.iOS** — against the published `SwiftBindings.Sdk/0.16.0`,
each driven all the way through simulator validation. They were chosen as high-value, ObjC-heavy
targets to stress the generator's **Objective-C** path (which has had far less investment than the
Swift path).

**All three converge on the same conclusion: the repo-side plumbing (ingest, layout, dependency
wiring, packaging) is solid; every real blocker is inside the binding generator's Objective-C
handling.** This doc is the de-duplicated, self-sufficient fix list. Each gap below has the exact
symptom, root cause, the library/file where it manifests, a fix direction, and a concrete path to
**validate the fix directly against the preserved spike worktree**.

Fixing these is high-leverage: most are **general ObjC-binding correctness bugs that recur for any
ObjC framework**, not per-library quirks. Land them and Facebook, MapLibre, and Firebase's ObjC core
all unblock together, along with any future ObjC framework.

---

## Preserved spike worktrees (validate fixes here)

All three live as **locked git worktrees** under the `swift-dotnet-packages` repo, each on its own
branch with the full spike committed (scaffolding + a `STREAM-*.md` report). They are locked so
`git worktree prune` won't remove them. Nothing was merged to `main` or pushed.

| Spike | Worktree path | Branch | Commit | Report | Sim UDID used |
|---|---|---|---|---|---|
| **Facebook** | `/Users/wojo/Dev/swift-dotnet-packages/.claude/worktrees/agent-a1883ee7e9575661c` | `worktree-agent-a1883ee7e9575661c` | `e232c00` | `STREAM-FACEBOOK.md` | `B3518681-51EE-4178-99DE-31AF1027BA44` (iPhone 17 Pro Max) |
| **MapLibre** | `/Users/wojo/Dev/swift-dotnet-packages/.claude/worktrees/agent-a9ba038284d333e5e` | `worktree-agent-a9ba038284d333e5e` | `914c103` | `STREAM-MAPLIBRE.md` (+ `scratchpad/maplibre-render*.png`) | `79B0459B-6A8C-43AC-B444-FD8809BC65B6` (iPhone 17) |
| **Firebase** | `/Users/wojo/Dev/swift-dotnet-packages/.claude/worktrees/agent-a34a938885b48dc6c` | `worktree-agent-a34a938885b48dc6c` | `3bbf1a7` | `STREAM-FIREBASE.md` | `027A30CA-93A3-4F53-A8B5-591C35D01631` (iPhone 16e) |

There is also a one-page strategic summary (verdicts, rankings) at
`/Users/wojo/Dev/swift-dotnet-packages/BINDING-CANDIDATES.md` (uncommitted, on `main`'s working tree).

### How to validate a generator fix against a worktree

The worktrees pin `SwiftBindings.Sdk/0.16.0` (the broken version). To test a fix:

1. Build the patched generator → pack a local `SwiftBindings.Sdk` nupkg into the packages repo's
   `local-packages/` source (the repo's `NuGet.config` already has a `local` source pointing there).
2. Point the worktree at it. The Nuke harness has a sweep target:
   `dotnet nuke BumpSdkVersion --version <local-version>` (run inside the worktree) — it rewrites every
   `Sdk="SwiftBindings.Sdk/…"` attribute and wipes sibling `obj/` so bindings actually regenerate.
3. Re-run the per-library build/validate commands in each gap's "Validate" section below.

The generated C# the gaps refer to lands under `libraries/<Lib>/obj/.../swift-binding/*.cs`
(`ApiDefinition.cs`, `StructsAndEnums.cs`, the per-module `.cs`). That dir is gitignored — it exists
on disk in the worktree until cleaned, and is regenerated on each build. To see the generator's *real*
stderr (MSBuild's `MSB3073` wrapper hides it), invoke the generator command directly, as the spikes did.

> **Sim UDIDs are machine-local.** Substitute a booted simulator from `dotnet nuke ListSims` /
> `BootSim`, or pass `--device-udid <id>` to `ValidateSim`.

---

## Theme A — Third-party Objective-C xcframework is not a first-class binding target

This is the **structural** gap. The SDK's ObjC binding pipeline is gated on *Apple system frameworks*
only; a third-party ObjC xcframework either falls through to an empty assembly or requires fragile
hand-wiring. Two of the three spikes hit this, with **divergent outcomes that must be reconciled** —
that divergence is itself the most useful clue.

### A1. ObjC pipeline gated on `AppleFramework` mode → third-party ObjC xcframework yields an empty assembly

- **Discovered by:** Firebase (binding `FirebaseCore`, pure ObjC).
- **Severity:** High (structural). Blocks the FirebaseCore bootstrap layer that *every* Firebase
  product needs; blocks any third-party ObjC-only framework.
- **Symptom:** A binding project over a third-party ObjC xcframework compiles to a **0-type assembly**.
  The generator still emits `obj/.../swift-binding/ApiDefinition.cs`, but the MSBuild side never feeds
  it to `bgen`, so nothing is bound. Adding `<IsBindingProject>true</IsBindingProject>` by hand makes
  the iOS `bgen` target run, but it fires **before** the SDK injects the API-definition item →
  `error: No API definition file specified` (target ordering).
- **Root cause:** All ObjC handling — type detection, the synthesize-xcframework target, the
  `IsBindingProject` / `ObjcBindingApiDefinition` `bgen` wiring, and the generator's `--objc` flag — is
  gated on `_SwiftBindingTargetKind == 'AppleFramework'`. In the packaged 0.16.0 SDK this is in
  `Sdk.targets` around **lines 340–371, 810, and 1657** (verify against current source; line numbers
  are as-packaged in 0.16.0). A third-party ObjC xcframework is not `AppleFramework`, so it falls
  through every gate.
- **Fix direction:** Introduce a first-class "third-party ObjC xcframework" binding mode (or generalize
  the `AppleFramework` gates to any ObjC xcframework). It must (a) run ObjC type detection on the
  ingested xcframework, (b) inject the generated `ApiDefinition.cs` as the `ObjcBindingApiDefinition`
  item **before** the iOS `bgen` target runs (fix the ordering), and (c) thread `--framework-dependency`
  sibling xcframeworks into both the generator and `bgen` (see A2).
- **Validate:**
  - Worktree: Firebase. File: `libraries/Firebase/FirebaseCore/SwiftBindings.Firebase.Core.csproj`.
  - Command: `dotnet nuke BuildLibrary --library Firebase --all-products`.
  - Done when: `SwiftBindings.Firebase.Core.dll` contains the real `FIRApp` / `FIROptions` types
    (non-empty), and the test app's `FIRApp.configure` no longer needs the raw-`objc_msgSend`
    bootstrap (`libraries/Firebase/tests/Program.cs`, `ObjcBootstrap`).

### A2. ObjC clang AST-dump ignores `--framework-dependency` `-F` paths (cross-framework `#import` fails)

- **Discovered by:** Facebook (binding `FBSDKLoginKit`, whose umbrella imports `FBSDKCoreKit`).
- **Severity:** High. Blocks **Facebook Login** entirely (and any mixed/ObjC framework whose umbrella
  header does a cross-framework `#import <Dep/Dep.h>`).
- **Symptom (exact):**
  ```
  ObjC pipeline for mixed framework failed (exit 1); refusing to emit a Swift-only binding...
  Clang AST dump failed: FBSDKLoginKit.h:14:9: fatal error: 'FBSDKCoreKit/FBSDKCoreKit.h' file not found
  ```
  Generation exits 1 with **no `.cs` produced**.
- **Root cause:** The generator's ObjC clang AST-dump pass does not add the dependency xcframeworks
  (passed via `--framework-dependency`) to its clang `-F` search path, so the umbrella's
  `#import <FBSDKCoreKit/FBSDKCoreKit.h>` cannot resolve. The generator then **refuses** to fall back
  to a Swift-only binding (by design — it won't silently drop the ObjC surface), producing nothing.
- **Repro proof:** a standalone `clang -fobjc-arc -fmodules -F<dep-xcframework-slice> ...` over the same
  umbrella header compiles cleanly — i.e. the only missing ingredient is threading the dependency `-F`
  (and `-fobjc-arc`) into the AST-dump invocation.
- **Fix direction:** Thread every `--framework-dependency`'s resolved slice directory into the ObjC
  AST-dump's `-F` flags (and pass `-fobjc-arc`). Pairs with A1 — both are "make cross-framework ObjC
  resolution work."
- **Validate:**
  - Worktree: Facebook. Files: `libraries/Facebook/FBSDKLoginKit/SwiftBindings.Facebook.Login.csproj`
    (+ the auto-injected `SwiftFrameworkDependency` block pointing at `FBSDKCoreKit`).
  - Command: `dotnet nuke BuildLibrary --library Facebook --all-products`.
  - Done when: `FBSDKLoginKit` generation produces `.cs` (no "refusing to emit" / "file not found"),
    and `SwiftBindings.Facebook.Login.dll` exposes `LoginManager` / `LoginConfiguration` / `LoginButton`.

### Reconcile A1 vs. the MapLibre success (key clue)

**MapLibre is a working reference for the third-party ObjC path**: with
`<SwiftFrameworkType>ObjC</SwiftFrameworkType>` + `<IsBindingProject>true</IsBindingProject>` and the
generated `ApiDefinition.cs` fed via an `<ObjcBindingCoreSource>`, it produced **104 `MLN*` types** and
a `bgen`-compiled assembly. Firebase, with similar knobs, hit the A1 ordering failure and an empty
assembly. **Diff the two csprojs** before designing the first-class mode — MapLibre proves the path can
work; the delta explains what the supported mode must do:

- Works (104 types): `…/agent-a9ba038284d333e5e/libraries/MapLibre/SwiftBindings.MapLibre.csproj`
- Breaks (empty / ordering): `…/agent-a34a938885b48dc6c/libraries/Firebase/FirebaseCore/SwiftBindings.Firebase.Core.csproj`

(MapLibre is single-framework; Firebase needs cross-framework deps — so the supported mode must cover
both, which is where A2's `-F` threading also comes in.)

---

## Theme B — Objective-C emitter correctness (recurs for ANY ObjC framework — highest ROI)

These are concrete codegen bugs in the ObjC ApiDefinition emitter. Two are FATAL (won't launch / won't
link), one is SEVERE (whole API family unusable at runtime), one is a silent correctness regression.
None are library-specific.

### B1. Duplicate selector from property-setter vs. explicit method → registrar aborts the app at launch

- **Discovered by:** MapLibre (`MLNImageSource`: `setURL:`, `setCoordinates:`).
- **Severity:** FATAL — the app `SIGABRT`s at startup before any user code runs.
- **Symptom (exact):**
  ```
  Could not register the selector 'setURL:' … already registered on 'SetURL'
  ```
- **Root cause:** In ObjC a selector can legally be both a `@property` setter *and* an explicit method
  (`setURL:` = the `URL` property setter **and** a declared `- (void)setURL:`). The emitter produces
  **two** managed members — `void SetURL(NSUrl)` and `URL { …set; }` — both exporting the same selector.
  The .NET ObjC registrar fatally rejects duplicate selector exports and kills the whole app.
- **Fix direction:** De-duplicate at emit time: when a property setter's selector matches an explicit
  method's `[Export]`, emit only one of them (prefer the property).
- **Resolution:** DONE. Before each emit loop (class / protocol / category) the emitter pre-computes the
  ObjC accessor selectors its *emittable* properties export (`BuildPropertyAccessorSelectors`: getter
  `GetterSelector ?? Name`, plus `SetterSelector ?? "set{Name}:"` for read-write), bucketed by
  instance-vs-class kind. A method whose `[Export]` selector matches one of the same kind
  (`CollidesWithPropertyAccessor`) is dropped in favour of the property and recorded as a
  `DuplicateSelector` skip; the same drop is replayed in `SeedInheritedProtocolSignatures` so it doesn't
  seed descendants. Locked by `ApiDefinitionEmitterTests` + the MapLibre sim run (no registrar abort).
- **Validate:**
  - Worktree: MapLibre. Currently worked around by **hand-editing** generated
    `obj/.../swift-binding/ApiDefinition.cs` to drop the redundant `SetURL`/`SetCoordinates` methods.
  - Done when: a **clean regenerated** binding (no hand-edit) launches without the registrar abort.
    `dotnet nuke BuildTestApp --library MapLibre && dotnet nuke ValidateSim --library MapLibre --device-udid 79B0459B-6A8C-43AC-B444-FD8809BC65B6` reaches `TEST SUCCESS`.

### B2. ObjC protocol "I"-prefix applied twice → protocol-typed APIs unusable (`InvalidCastException`)

- **Discovered by:** MapLibre (`MLNAnnotation`; same shape hits `MLNOverlay`, `MLNFeature`, `MLNStylable`).
- **Severity:** SEVERE — an entire API family (annotations / overlays / feature queries / style access)
  is unusable even though the app runs. This is MapLibre's single sim-test failure.
- **Symptom (exact):**
  ```
  InvalidCastException: Unable to cast 'MapLibre.MLNPointAnnotation' to 'MapLibre.IMLNAnnotation'
  ```
  (Also dozens of benign `CS0108 "hides inherited member"` warnings as a tell.)
- **Root cause:** The emitter writes the ObjC `MLNAnnotation` protocol into `ApiDefinition.cs` already
  named `IMLNAnnotation` — i.e. it **pre-applies** `bgen`'s "I"-prefix protocol convention itself.
  `bgen` then applies the convention **again**, producing:
  - `IIMLNAnnotation` — the real protocol-adoption interface (double-I), which `MLNShape` /
    `MLNPointAnnotation` actually implement, and
  - `IMLNAnnotation` — an **orphan concrete `NSObject` class** (bgen's `Model`, named off the def
    interface).

  `MLNMapView.AddAnnotation(IMLNAnnotation)` binds to the **class**, so passing any real annotation
  (which implements the double-I interface) throws.
- **Fix direction:** Emit ObjC protocols into the def under their **bare** name (`MLNAnnotation`), and
  let `bgen` produce `IMLNAnnotation` (interface) + `MLNAnnotation` (model) exactly once. Protocol-typed
  parameters then bind to the interface that shapes implement.
- **Resolution:** DONE, via **positional** protocol spelling rather than a blanket bare-vs-I rule. The
  protocol *declaration* and own-protocol *conformance/inheritance* lists are emitted **bare**
  (`partial interface MLNAnnotation`, `: MLNFeature`); bgen applies its `I` prefix exactly once. A
  protocol used as a *member type* (param / return / property) is emitted as the **interface** `IFoo`
  for own AND SDK protocols (`MapType` step 3 for `id<…>`, plus a new arm for a direct `MLNAnnotation *`
  pointer) — a bare member ref makes bgen bind to the `Foo` Model class, so a conforming subclass fails
  `GetNSObject<Foo>`. Because the api-definition contract compile (a plain `csc` pass before bgen) has no
  bgen-generated `IFoo` in scope, the emitter writes an empty `interface IFoo {}` forward declaration per
  own protocol (the dotnet/macios hand-binding idiom; bgen ignores the attribute-less placeholder). The
  former whole-file `IFoo`→`Foo` regex post-process is gone; the decision is centralized in `MapType` +
  `ProtocolInterfaceReference` + the forward-decl emission, keyed on a `localProtocolNames` set. Locked by
  `ObjCTypeMapperTests` / `ApiDefinitionEmitterTests` / `ObjCPipelineIntegrationTests` and the MapLibre
  annotation round-trip (sim 10/0, no `InvalidCastException`).
- **Validate:**
  - Worktree: MapLibre. The test app's annotation test currently fails on this exact cast (and proves
    the capability exists via a raw `PerformSelector("addAnnotation:")`, which *does* render the pin).
  - Done when: the strongly-typed `MLNMapView.AddAnnotation(new MLNPointAnnotation())` path no longer
    throws and the sim run goes to **10 pass / 0 fail** (currently 9/1).

### B3. Protocol-existential proxy not emitted aborts the whole module

- **Discovered by:** Facebook (`SharingContent` → `SharingContentProxy`, in `FBSDKShareKit`).
- **Severity:** High — aborts **all** of Facebook Share generation (no `.cs` at all).
- **Symptom (exact):**
  ```
  Binding generation failed: Protocol proxy 'SharingContentProxy' is unavailable:
  its EveryProtocol conformance was not emitted, so a member that constructs it cannot be produced.
  ```
- **Root cause:** A protocol existential can't be proxied (its `EveryProtocol` conformance wasn't
  emitted), and the generator aborts the **entire module** rather than skipping the single
  unconstructable member.
- **Fix direction:** Skip the unconstructable member with a `SWIFTBIND` warning (graceful degradation),
  instead of failing the whole module. (Longer term: emit the missing existential proxy.)
- **Validate:**
  - Worktree: Facebook. File: `libraries/Facebook/FBSDKShareKit/SwiftBindings.Facebook.Share.csproj`.
  - Command: `dotnet nuke BuildLibrary --library Facebook --all-products`.
  - Done when: `FBSDKShareKit` produces `.cs` and `SwiftBindings.Facebook.Share.dll` exposes
    `ShareLinkContent` / `ShareDialog` / `SharePhotoContent`.

### B4. `@objc` / Swift enum raw values not preserved (silent correctness regression)

- **Discovered by:** Firebase (`AuthErrorCode`).
- **Severity:** High (correctness) — silent. Breaks `NSError.code` matching for every consumer.
- **Symptom:** Swift declares `case wrongPassword = 17009`; the generator emits `WrongPassword = 7`
  (sequential renumbering). Confirmed at **runtime** in the sim test (`AuthErrorCode.WrongPassword == 7`
  observed, expected `17009`). The incumbent (Objective Sharpie) preserves the raw values correctly.
- **Investigation outcome (current source):** Does **not reproduce**. The hypothesized root cause —
  the emitter assigning sequential ordinals — is wrong: `StructsAndEnumsEmitter.EmitEnum` already emits
  each case's explicit value verbatim when present and only auto-numbers when absent. The two upstream
  paths that the `= 7` symptom would have implicated are both correct on current source:
  1. **Value extraction.** A Swift `@objc enum: Int { case wrongPassword = 17009 }` surfaces in the
     generated `-Swift.h` as `SWIFT_ENUM(NSInteger, AuthErrorCode, closed) { … = 17009, … }`, whose
     clang-AST enumerator tree is `EnumConstantDecl → ImplicitCastExpr → ConstantExpr(17009) →
     IntegerLiteral(17009)`. `ClangAstParser.TryExtractEnumValue` reads `ConstantExpr.value` directly
     and recurses through `ImplicitCastExpr`/`ExplicitCastExpr`/`ParenExpr`/`CStyleCastExpr` wrappers to
     the `IntegerLiteral` leaf, with decimal/hex/octal/negative parsing. It returns `17009`.
  2. **Two-decl dedup.** The `SWIFT_ENUM` macro expands to a zero-enumerator forward declaration
     *followed by* the value-bearing definition. The forward decl appears first; the richest-wins enum
     dedup (`DeduplicateByRichestMergingAvailability`, keyed on `Cases.Count`) keeps the definition.
- **Resolution:** No code change. The 0.16.0 symptom was an earlier value-extraction/dedup gap already
  closed on this branch. Locked against regression by `ClangAstParserTests`
  (`Parse_SwiftObjcEnum_PreservesExplicitRawValues`, faithful to the real `AuthErrorCode` AST dump, and
  `Parse_SwiftObjcEnum_ForwardDeclFirst_KeepsValueBearingDefinition`, the forward-decl-first dedup case)
  plus the existing emitter coverage (`StructsAndEnumsEmitterTests` `Medium = 5,`). Runtime confirmation
  against Firebase (`AuthErrorCode.WrongPassword == 17009`) remains a Session-2 item, but the
  generator-output behavior is correct and pinned now.

### B5. P/Invokes emitted for `static inline` C functions → native link fails

- **Discovered by:** MapLibre (20 `NS_INLINE` helpers, e.g. `MLNCoordinateSpanMake`,
  `MLNDegreesFromRadians`; plus the never-exported `MapboxVersionNumber` global).
- **Severity:** FATAL — native link fails; app never builds.
- **Symptom (exact):**
  ```
  Undefined symbol: _MLNCoordinateSpanMake, _MLNDegreesFromRadians, … (+ _MapboxVersionNumber)
  ```
  The static registrar's `-u` force-reference makes the link unable to complete.
- **Root cause:** The emitter generates `[DllImport("__Internal")]` P/Invokes for **all** free C
  functions in the headers, including `static inline` (`NS_INLINE`) helpers that have **no symbol in any
  binary**, and `[Field]`-binds globals (`MapboxVersionNumber`) the framework never exports.
- **Fix direction:** Do **not** P/Invoke `static inline` functions (reimplement their trivial math in C#,
  or skip them). Do not `[Field]`-bind globals absent from the framework's export list.
- **Resolution:** DONE, in two layers. (1) Parse-time: `ClangAstParser` skips a free function whose
  `storageClass == "static"` or that is `inline` without `extern` — these emit no standalone symbol.
  (2) Binary-backed backstop: a new pipeline pass `FilterToNativeSymbolBackedFreeSymbols` drops any free
  function, and any `extern` global, whose Mach-O symbol `_<name>` is defined in none of the probed
  binaries (`NativeSymbolProbe` now retains every defined symbol, not just `_OBJC_CLASS_$_` class names).
  Non-`extern` constants are emitted as compile-time literals and need no symbol, so they are kept. The
  guard follows the same **fail-open** discipline as the class filter — it acts only on a positively
  `Gathered` probe with at least one defined symbol — and logs dropped names as `SWIFTBIND055`. Locked by
  `NativeSymbolGuardTests` + `ClangAstParserTests`.
- **Validate:**
  - Worktree: MapLibre. Currently worked around with a hand-authored static shim lib
    (`libraries/MapLibre/tests/native/MLNInlineShims.m` → `libMLNInlineShims.a`, referenced as a
    `<NativeReference Kind="Static" ForceLoad="true">`).
  - Done when: removing that `NativeReference` + the shim files still links and reaches `TEST SUCCESS`.

### B6. Missing system-framework import detection (OpenGLES)

- **Discovered by:** MapLibre (`EAGLContext` on `MLNStyleLayerDrawingContext.context`).
- **Severity:** Medium — `ApiDefinition.cs` won't compile until the import is added.
- **Symptom (exact):** `CS0246: The type or namespace name 'EAGLContext' could not be found`.
- **Root cause:** The emitter auto-imports detected system frameworks (MapKit/Metal/UIKit/…) but misses
  **OpenGLES**, where the deprecated `EAGLContext` lives.
- **Fix direction:** Add OpenGLES to the import-detection set — or, more robustly, import any system
  framework whose types appear in the generated surface.
- **Resolution:** DONE. `OpenGLES` is added to `ObjCUsingsEmitter`'s ApiDefinition `using` set and
  registered in `apple-frameworks.json` as `platformUnavailable: ["macOS"]` so the startup
  known-module assertion passes and the `using` is omitted on macOS (where `EAGLContext` doesn't exist).
  The emitter's "is this a framework we touch?" oracle was refactored to a shared
  `ReferencedAppleFrameworkModules` set so a referenced-but-untyped framework still counts. Locked by
  `ObjCUsingsEmitterTests` + `AppleFrameworkRegistryTests`.
- **Validate:**
  - Worktree: MapLibre. Currently worked around by `global using OpenGLES;` injected via
    `libraries/MapLibre/OpenGLESGlobalUsing.cs` (`<ObjcBindingCoreSource>`).
  - Done when: removing `OpenGLESGlobalUsing.cs` still compiles.

### B7. Property vs. synthesized-method-overload name collision (`camera`)

- **Discovered by:** MapLibre (`MLNMapView.camera` property shadowed by `Camera(...)` overloads
  synthesized from `cameraThatFits…` selectors).
- **Severity:** Low — makes the `camera` property unreadable (`CS8917`), but a workaround exists
  (read `zoomLevel` / `direction` instead).
- **Root cause:** Synthesized method names (from `…That…` selector families) collide with a same-named
  property; the emitter doesn't disambiguate.
- **Fix direction:** Disambiguate synthesized method names from property names at emit time.
- **Resolution:** DONE, sharing B1's pre-pass. Because methods are emitted before properties, the emitter
  now seeds the names of its *emittable* properties (and `Weak…` delegate aliases) into
  `emittedPropertyNames` **before** the method loop, so a synthesized method whose short name equals a
  later property's name (`Camera(...)` vs property `camera`) is routed through
  `ResolveMethodNameWithDedup`'s full-selector rename instead of producing a `CS0102`/`CS8917` clash.
  Locked by `ApiDefinitionEmitterTests`.
- **Validate:** Worktree MapLibre — done when `MLNMapView.Camera` (property) is readable without
  `CS8917`.

---

## Theme C — Cross-binding type resolution

### C1. `StoreKit.Transaction` (StoreKit 2) missing from the C# StoreKit binding

- **Discovered by:** Facebook (`FBSDKCoreKit` → `IAPTransaction.Transaction_Get()`).
- **Severity:** High — blocks **FBSDKCoreKit** compile (Core is the dependency of both Login and Share).
- **Symptom (exact):**
  ```
  FBSDKCoreKit.cs(22005): error CS0234: 'Transaction' does not exist in the namespace 'StoreKit'
  ```
  Generation succeeds; the C# compile fails.
- **Root cause:** A generated member returns `StoreKit.Transaction` (StoreKit 2), a type **absent from
  the Apple StoreKit C# binding**.
- **Fix direction:** Either bind `StoreKit.Transaction` in the Apple StoreKit binding, **or** have the
  generator skip members that reference framework types not present in the available bindings (with a
  `SWIFTBIND` warning) rather than emit uncompilable C#. The latter is the general guard.
- **Validate:**
  - Worktree: Facebook. Command: `dotnet nuke BuildLibrary --library Facebook --all-products`.
  - Done when: `FBSDKCoreKit.cs` compiles (no `CS0234`) and `SwiftBindings.Facebook.Core.dll` is produced.

---

## Related (not the binding generator, but surfaced by these spikes)

### D1. `spm-to-xcframework` source mode trips on overlapping `exclude:` paths (Firebase)

- **Tool:** `spm-to-xcframework`, pinned `3ee0109f6599`.
- **Symptom:** `stage_source` deletes every `exclude:` path declared in `Package.swift`. For Firebase,
  one target's `exclude:` is **another target's source directory**, producing
  `invalid custom path 'FirebaseAppDistribution/Tests/Unit/Swift'`. (Mixed Swift/ObjC same-directory
  targets trip this.) The Firebase spike pivoted to the prebuilt `Firebase.zip` (zip mode) as a result.
- **Fix direction:** `stage_source` should only delete `exclude:` paths scoped to the target being
  staged, not globally — or skip deletion of any path that is another target's source root.

### D2. SWIFTBIND061 member-skip volume (post-unblock triage, not a blocker)

Even once the FATAL/SEVERE gaps close, these SDKs lean hard on Swift protocol existentials, `@_spi`,
ObjC↔Swift bridging, closures-with-bridge-option dictionaries, and StoreKit 2 — exactly what the
generator currently skips. The resulting surface will be **thinned** and need per-member triage:

- Facebook `FBSDKCoreKit`: **450 / 1055** members skipped. `FBAEMKit`: **102 / 201**.
- Firebase `FirebaseAuth`: **22 / 250** skipped (17 are correct internal-`init` exclusions; the 5
  meaningful ones are the `UIApplicationDelegate` / `UIScene` **APNs-forwarding** helpers that
  phone-auth / push flows need — `UnsupportedSignature` / `UnsatisfiedGenericConstraint`).

Notably **B-A1 (CS0535)** is the dangerous shape here: when a protocol member is skipped, the generator
must also drop the conformance declaration (or emit a throwing stub), or it produces guaranteed-
uncompilable C#. Fixing that one converts "hard failure" into "graceful, shippable degradation."

---

## Suggested fix order

1. **Theme B correctness first** — B1 (dup selector), B2 (double-I protocol), B4 (enum raw values).
   General, reusable across every ObjC framework; two are FATAL/SEVERE; B4 is a silent correctness bug.
2. **B3 + the CS0535 conformance guard** (graceful skip instead of module/compile abort) — converts hard
   failures into shippable, thinned surfaces.
3. **Theme A** (A1 + A2) — the first-class third-party ObjC xcframework path with cross-framework `-F`
   threading. Structural unlock for Facebook (Login) and Firebase (Core). Use MapLibre's working csproj
   as the reference; reconcile against Firebase's failing one.
4. **C1** (StoreKit.Transaction) and **B5–B7** (inline P/Invoke, OpenGLES import, name collisions).
5. **D1** (spm-to-xcframework) only if we want Firebase on source mode; zip mode already works.

## Definition of done (per spike, validated in its worktree)

- **MapLibre** → CONDITIONAL GO becomes GO: a **clean regenerated** binding (no `OpenGLESGlobalUsing.cs`,
  no `MLNInlineShims` native shim, no hand-edited `ApiDefinition.cs`) builds, launches, and the sim run
  goes **10 / 0** (annotation test passes). Then add the missing **NativeAOT / device** pass the spike
  didn't run.
- **Facebook** → `FBSDKLoginKit` + `FBSDKShareKit` + `FBSDKCoreKit` + `FBAEMKit` all produce `.cs` and
  compile; the `tests/` app (a ready-to-run Login+Share spec) builds and reaches `TEST SUCCESS` on the
  sim. Then triage the 450/1055 + 102/201 skipped members.
- **Firebase** → `SwiftBindings.Firebase.Core` binds `FIRApp`/`FIROptions` (no `objc_msgSend`
  bootstrap), `AuthErrorCode.WrongPassword == 17009` at runtime. Then it's a *collaboration* decision
  with Adam, not a ship decision (he owns the dependency-closure/coexistence story).
