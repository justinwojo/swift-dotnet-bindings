# Facebook + MapLibre — remaining work to ship

**Status:** triaged 2026-07-02, then code-path-scoped the same day by four parallel investigations
(FB-1, FB-3, ML-1, W-1). Both libraries are **feasibility-proven and runtime-green**; what remains is a
small, enumerated set of generator improvements plus a durable-test gap. Numbers below were **measured** by
regenerating both bindings against `main` @ `262ea8c3` (local SDK `0.16.1-d8local` for Facebook, `d7local`
for MapLibre). Every work item carries the exact fix location and the traps found during scoping, so an
implementation session can execute without re-discovering the code path.

**Scoping changed the plan in three ways** — read these before the detail:
1. **ML-1 (MapLibre `camera` collision) is already fixed** (commit `3e5a0a5e`, 2026-06-28). The prior doc
   carried a stale spike finding. It is *not* a work item — it becomes a regression scenario inside W-1.
2. **FB-3 (`validate(options:)`) graduated from "don't-chase" to a real, well-shaped feature**: it's an
   ObjC `NS_OPTIONS` type-database gap, the direct sibling of the `NS_ENUM` / `NS_TYPED_EXTENSIBLE_ENUM`
   bridges already landed. High-value and low-risk (established pattern).
3. **FB-1b (two `LoginConfiguration` inits) is a genuine loss**, not benign — but fixing it needs a
   public-API naming decision, so it's its own item, not a rider on FB-1.

---

## Snapshot

| Library | Feasibility | Runtime | Generator work remaining | Real ship blocker |
|---|---|---|---|---|
| **MapLibre** | Proven | sim 10/0/0 + device NativeAOT 10/0/0 | **None** (ML-1 already fixed) | **Durable test coverage** (W-1 — no BindingTests gate for its pure-ObjC shape) |
| **Facebook** | Proven | sim 6/0 + device NativeAOT 6/0 | FB-1 (cheap), FB-3 + FB-2 (features), FB-1b (needs API decision) | Product call (surface polish + demand), not generator |

The primary consumer surface works at runtime for both. Facebook's Login and Share flows round-trip;
MapLibre renders a real map from C# with delegate callbacks firing ObjC→C#.

---

## Suggested sequencing (for `/next-session`)

**Batch A — one session, cheap, TDD.** The durable-test foundation plus the one cheap generator fix.
- **W-1** pure-ObjC umbrella fixture (4 shapes + 1 regression scenario) — the MapLibre durability gate.
- **FB-1** enum property/case rename — small, self-contained, its test lands in the Swift enum fixtures.
- These pair naturally: build the fixtures, one shape is red until FB-1 lands, then green.

**Batch B — separate session(s), features, each needs a device run (marshalling/type-DB changes).**
- **FB-3** `NS_OPTIONS` type-database bridge — recommended *first* of the two: lower risk, follows the
  established `NS_ENUM` pattern, generalizes to every mixed binding, and may knock loose 2 Review proxies.
- **FB-2** `[any P]` collection existentials — bigger; a deeper existential-marshalling change.

**Standalone — its own design pass, not batched.**
- **FB-1b** `LoginConfiguration` init overload-collapse — real loss, but the fix renames a public
  `TryCreate` method, which is an API-shape decision to settle before implementing.

**Then — pre-ship verification & prep (not a generator session).** The generator sessions close the
*generator* work; they don't by themselves publish a package. After they land, prove the packages actually
build, link, and run from a clean consumer, and settle the open product/API calls — see
**Post-batch — pre-ship verification & prep** below. MapLibre reaches this step after Batch A alone; it does
not wait on the Facebook feature sessions.

Parallelizing across worktrees is **not** worth it: the surface is small and every change re-serializes on
the one simulator/device runtime gate and a single combined packages-repo re-measure. Do the batches in
sequence; parallelize only investigation and fixture-source authoring (as was done here).

---

## Batch A

### W-1 — Generalized pure-ObjC umbrella BindingTests fixture  ·  **DONE 2026-07-03 (fixture + 5 behavior-asserted shapes shipped; sim-green)**

**Why.** BindingTests (the durable end-to-end gate) has an `ObjCInterop` suite and the synthetic mixed
ObjC+Swift fixture (`--mixed-pack` / `--mixed-direct`), but **zero authored `module.modulemap` fixtures** —
i.e. no durable test for a **pure-ObjC clang-umbrella library**, which is exactly what MapLibre is. The
generator behaviors MapLibre depends on (duplicate-selector flattening, static-inline exclusion, double-`I`
protocol avoidance, protocol-typed collection round-trip, property-vs-method disambiguation, `ApiDefinition.cs`
emission) have no regression gate. ML-1 already regressed-then-was-fixed silently once; this fixture stops
the next one.

**A synthetic ObjC framework** (`module.modulemap` + umbrella header + `.m` files), not MapLibre, asserted
by behavior. The four expressible-in-pure-ObjC shapes:
- a selector exposed as **both a property and a method** → duplicate-selector flattening (no launch abort);
- a `static inline` C function **and** a real exported C function → the exported one binds via
  `DllImport("__Internal")`, the inline one is correctly excluded (no dead P/Invoke → no link failure);
- an ObjC **protocol used as a collection element** (`NSArray<id<Foo>>`) → `IFoo` round-trips with no
  double-`I` collapse / `InvalidCastException`;
- a **delegate protocol** with an optional callback → ObjC→C# override dispatch fires.
- **+ regression scenario (the ML-1 shape):** a multi-keyword method whose *first* selector segment
  collides with an unrelated property's bare name (e.g. a `camera` property alongside a
  `camera:fittingX:edgePadding:` method) → the property keeps `Camera`, the method disambiguates. This is a
  *different* collision than the same-selector-as-both-property-and-method bullet above, and it guards
  commit `3e5a0a5e`. Test-only.

The fifth shape from the earlier draft — a nested enum whose property collides with a case — **cannot be
expressed in pure ObjC** (`NS_ENUM` compiles to plain integer constants, no computed properties). That is
intrinsically the FB-1 bug: a *Swift* enum with associated values. Its test lives with FB-1, in the Swift
enum fixtures — not here.

**Wiring checklist** (scoped against the current harness):
- **Source dir:** a new sibling under `BindingTests/Sources/` (e.g. `Sources/ObjCUmbrella/`) with `.h`/`.m`
  + `Modules/module.modulemap` + `Headers/{Module}.h`. Keep it out of the Swift target's source list or
  the framework misclassifies as Mixed. Closest copyable pattern is the mixed fixture's ObjC companion —
  the modulemap+umbrella shape at `build/Build.PackGate.MixedFixture.cs:~696` and the selector-dedup
  "Shape B" at `~:557`.
- **Build:** mirror the Swift fixture's pipeline in `build/Build.BindingTests.cs` (`BuildXcframework` →
  `RegenerateBindings`). Invoke the generator with **`--objc`** (`CliOptions.cs:176-179`) to force the ObjC
  pipeline deterministically rather than relying on Swift-resolution fallback. The resolver validates the
  modulemap at `XCFrameworkResolver.ResolveObjCFramework` (`XCFrameworkResolver.cs:433-486`).
- **Generated output:** the ObjC pipeline emits **fixed filenames** (`ApiDefinition.cs`,
  `StructsAndEnums.cs`, `BgenDelegates.cs`) into `-o`, so give the fixture its **own** output dir (e.g.
  `BindingTests/output-objc/`) to avoid collision with the Swift path's `SwiftBindingsTestLib.cs`.
- **Link:** in `BindingTests/RuntimeTestsApp/RuntimeTestsApp.csproj`, add `<Compile Include>` for the
  generated `.cs` and a `<NativeReference Include=... Kind="Framework">` for the fixture xcframework,
  mirroring the existing `SwiftBindingsTestLib` block.
- **Assertions:** a new file in `BindingTests/RuntimeTestsApp/ObjCInterop/` (domain-matched).

Keep the MapLibre spike app in the packages repo as the pre-release "a real map renders" integration check
— the one thing a synthetic fixture can't prove.

### FB-1 — `DuplicateSignature`: enum computed-property collides with a case name  ·  **DONE 2026-07-03 (general feature shipped + tested; the 6 named FB members are internal — see Outcome)**

**What.** A Swift enum with associated values exposes a computed property whose name matches one of its
cases; the generator projects both to the same C# name and **drops the property entirely**.

**Evidence (6 members, all `KnownLimitation` disposition — currently silently accepted).**
`SharePhoto.Source.{image,url,asset}` and `ShareVideo.Source.{data,url,asset}` — Details e.g. *"Enum
property 'Image' collides with case constructor name."* Recovering them lets a consumer read a photo/video's
source.

**Fix location + the trap.**
- Skip is raised at `EnumHandler.cs:373-380` (property loop: `emittedCaseConstructorNames.Contains(propertyName)`
  → `RecordMemberSkipped(... DuplicateSignature ...)` → `continue`).
- Case-constructor names are built at `EnumHandler.cs:316-317` / `EnumHandler.CaseConstruction.cs:17-20`;
  property names via `NameProvider.GetPropertyName` (`NameProvider.cs:980-997`), applied at
  `PropertyHandler.cs:413-415`.
- **Fix:** disambiguate the *property* side with the existing `Value`-suffix idiom
  (`Image`→`ImageValue`, numeric fallback `Value2`…, mirroring
  `NameProvider.ComputePropertyRenamesForNestedTypeCollisions` at `NameProvider.cs:1073-1101`).
- **⚠ Load-bearing trap:** do **not** add these to the shared `propertyRenames` dict. That dict is read by
  *both* the property-naming path **and** the case-constructor-naming path, and the Swift property and case
  share the literal same identifier — so `propertyRenames["Image"]="ImageValue"` would rename the *case*
  too and silently recreate the collision one level down. Instead add a **property-only rename channel**:
  a new optional field on `TypeHandlerContext` (`Marshaler/TypeHandlerContext.cs:27-33`, a record — additive),
  populated by a pre-pass in `EnumHandler` (the `emittedCaseConstructorNames` set is fully built before the
  property loop), applied only at `PropertyHandler.cs:414-415`. Keep every case-name call site reading only
  the original `propertyRenames`. Also update the in-sync consumers: the `propertyNames` HashSet
  (`EnumHandler.cs:416-419`) and `ToStringHelper` (`EnumHandler.cs:478`).
- Confirmed **cosmetic only** — the projected C# name never feeds the P/Invoke `EntryPoint` / `@_cdecl`
  wrapper symbols, so no ABI or reverse-dispatch impact.

**Tests.** Add a Swift enum with associated values + a computed property named like a case to
`BindingTests/Sources/SwiftBindingsTestLib/Enums/`; assert both the case constructor and the (renamed)
property surface and round-trip. This is the shape that can't live in W-1's pure-ObjC fixture.

**Outcome (2026-07-03).** The general feature shipped exactly as scoped: a property-only rename channel
(`TypeHandlerContext.EnumPropertyRenames`) populated by an `EnumHandler` pre-pass over the colliding
INSTANCE properties, applied only at the `PropertyHandler` name site. Coverage: an emitter unit test
(`EnumHandlerOutputTests.Emit_EnumWithAssociatedValueCaseAndCollidingInstanceProperty_RecoversPropertyWithValueSuffix`,
plus the preserved static-skip sibling) and an end-to-end BindingTests fixture
(`Enums/CasePropertyCollisionEnum.swift` + `Marshalling/EnumCasePropertyCollisionTests.cs`) that proves a
genuinely-PUBLIC colliding property (`ShareSource.image`/`link`/`blob`) recovers as `ImageValue`/`LinkValue`/
`BlobValue`, round-trips, and dispatches to Swift (fallback + match paths). Scoped to **instance** properties:
a colliding STATIC property keeps its pre-existing drop-as-`DuplicateSignature` behavior (no runtime coverage
for static recovery).

**Finding — the 6 named FBSDKShareKit members are internal, not recoverable.** Regenerating FBSDKShareKit
confirms the `DuplicateSignature` "collides with case constructor name" skips are **gone** for all six
(`SharePhoto.Source.{image,url,asset}`, `ShareVideo.Source.{data,url,asset}`) — but they are **not emitted**:
they now report `ModuleInternal` ("Internal property suppressed from bindings."). This is correct, not a
regression. `SharePhoto.Source` / `ShareVideo.Source` are `@usableFromInline internal` enums — present in
`FBSDKShareKit.abi.json` but absent from **both** the public and the private `.swiftinterface`; the parser
sets `IsModuleInternal=true` for their computed accessors. The original triage misread the
`DuplicateSignature` label as "a public property we could recover"; in reality the case-collision skip
(`EnumHandler.cs`, ~`:440`) fired *before* the emittability check (`MemberEmissionValidator.CanEmitProperty`
→ `ModuleInternal`) and masked the true reason. The rename pre-pass now lets every colliding instance
property flow to its true skip reason instead of masking it as `DuplicateSignature`. The real public
photo/video-source API is the class-level properties — `SharePhoto.Image`/`ImageUrl`/`PhotoAsset`,
`ShareVideo.Data`/`VideoAsset`/`VideoURL` — which are already emitted. So FB-1 surfaces **no new
FBSDKShareKit API**; its value is the general generator improvement for genuinely-public colliding enum
properties (and more accurate skip reporting for the internal/unemittable ones).

**Review refinement (2026-07-03).** Paired Codex + Grok review (no High findings; both independently
confirmed the `ModuleInternal` suppression is correct) converged on one non-functional imprecision: the
pre-pass's `reservedNames` seed pulled from *every* property, including internal/`@_spi` ones that
`CanEmitProperty` always drops. That let an internal sibling's projected name push a genuinely-recoverable
public property's suffix higher (`Image` → `ImageValue2`) with no real collision to avoid. Fixed by
skipping `IsModuleInternal`/`IsSpiProtected` properties when seeding `reservedNames` — those never emit, so
they don't "claim" a C# name (aligning the set with its documented purpose). Provably collision-safe
(over-reservation was already the conservative direction; this only removes a spurious suffix bump), leaves
the rename scan's diagnostic improvement intact, and is output-identical across all fixtures + FBSDKShareKit.

---

## Batch B

### FB-3 — Bridge ObjC `NS_OPTIONS` bitmasks into the Swift type database  ·  **priority: medium-high (recommended first feature)**

**What (reframed by scoping).** Every `Share*Content` type drops `validate(options: ShareBridgeOptions)
throws` as `UnsupportedSignature` "unsupported placeholder type." The signature is *clean*
(no `[String:Any]`) — the block is that `ShareBridgeOptions` is an ObjC **`NS_OPTIONS`** bitmask
(`NS_SWIFT_NAME(ShareBridgeOptions)`) that never gets a Swift type record in a mixed binding, so it degrades
to `Swift.AnyType` and the whole method is dropped (`MethodHandler.cs:1448-1459`, via
`MethodSignature.ContainsPlaceholder` at `MethodSignature.cs:138-140`).

**Why it's now attractive.** The Clang parser *already* fully extracts it —
`ClangAstParser.cs:~590-654` produces `ObjCEnumDecl { IsOptions=true, Cases, UnderlyingType }`, the same
data shape as `NS_ENUM`. It's `ObjCBridgeRecordFactory` that *explicitly* excludes it today:
`if (enumDecl.IsOptions) continue;` (`ObjCBridgeRecordFactory.cs:107-108`, with the design comment at
`:48-49` / `:90-91`). This is the **direct sibling** of the `NS_ENUM` → `SimpleEnum` bridge (same file) and
the `NS_TYPED_EXTENSIBLE_ENUM` bridge landed today in `be5b70f8` — an established, low-risk pattern.

**Fix shape.**
1. New `IsOptions == true` branch in `ObjCBridgeRecordFactory` synthesizing a type record (bitmask /
   `[Flags]`-style) with the same raw-value round-trip the `SimpleEnum` path already uses.
2. Companion C# emission: `[Flags] public enum ShareBridgeOptions : nuint { Default = 0, PhotoAsset = 1<<0, … }`.
3. Marshal parameter/return through the raw-value round-trip (reuse the `SimpleEnum` mechanism).

**Payoff.** Unblocks all 8 `validate(options:)` methods + `_ShareUtility.validateShareContent`, and
generalizes to any mixed binding using `NS_OPTIONS` (very common in ObjC). **Possible knock-on:** the 2
Review-tier EveryProtocol proxies `SharingContent` / `SharingValidatable` were skipped precisely because
`ShareBridgeOptions` had *no Swift type-database record* — giving it one may flip that predicate and recover
their proxies. Verify after FB-3 lands; don't promise it up front (the proxy path may have other gates).

**Tests.** BindingTests: an `NS_OPTIONS` typedef in a mixed fixture consumed by a Swift method
`validate(options:)`; assert the `[Flags]` enum round-trips and the method is reachable. Device run required
(marshalling change).

### FB-2 — `UnsupportedExistential`: collections of `any P` (`[any AppLinkTargetProtocol]`)  ·  **priority: medium**

**What.** `any P` in *direct* parameter position is supported; `Array<any P>` (a bound generic whose
element is an existential) is dropped. *(Not code-path-scoped this round — investigate the
`ExistentialHandler` gate before implementing.)*

**Evidence (consumer-facing ~6 of 12).** `AppLink.targets`/`.init`/`.appLink`,
`AppLinkFactory.createAppLink`, `AppLinkNavigation.navigationType`, `ShareMediaContent` — all *"Bound generic
contains existential type argument 'any …Protocol'."* The rest are internal `_BridgeAPI*` / `AEMReporter`.

**Fix approach.** Extend existential support to bound-generic element position (Array/Dictionary of `any P`)
in reverse dispatch and forward projection — the `ExistentialHandler`
`HasUnsupportedObjCProtocolExistentialPosition` gate drops it today. One general fix, reusable across
libraries. Medium effort (touches projection + container marshalling). The FB feature itself (App Links deep
linking) is niche; the *fix* is what has value. Device run required.

**Tests.** BindingTests: a method taking/returning `[any P]` for an ObjC and a Swift protocol; assert a
heterogeneous collection round-trips.

---

## Standalone

### FB-1b — `LoginConfiguration` init overload-collapse  ·  **priority: medium, needs an API decision first**

**What (verdict: real loss).** Two `init?` overloads project to a C# `TryCreate` signature already claimed
by a sibling, and are dropped:
- `init?(permissions:, tracking:, messengerPageId: String?)` collides with the emitted
  `init?(permissions:, tracking:, nonce: String)` — both erase to `TryCreate(IEnumerable<string>,
  LoginTracking, string, out)`.
- the 4-arg `+appSwitch:` variant collides the same way.

`messengerPageId` and `nonce` are semantically distinct but both erase to C# `string`, so the first-declared
wins the slot. Confirmed no surviving `TryCreate` / factory lets a caller supply *only* `permissions +
tracking + messengerPageId(+ appSwitch)` — genuinely unreachable (evidence: generated `TryCreate` list in
`FBSDKLoginKit.cs` around :7856/:7903/:7950; skip raised at `IHandler.cs:491`).

**Why standalone.** Different subsystem from FB-1 (constructor/`TryCreate` dedup, not `EnumHandler`), and
disambiguation must rename the **externally visible** `TryCreate` (e.g. `TryCreateWithMessengerPageId` vs
`TryCreateWithNonce`) — there's no quiet return/receiver slot to suffix. Which label wins the plain
`TryCreate`, how many overloads may differ this way, and the ordering-dependence are an API-shape design
call to settle before implementing.

---

## Post-batch — pre-ship verification & prep

The last mile between "generator done" and "published nupkg." **None of this is generator work** — it's
proving the package actually builds, links, and runs from a *clean* consumer, plus settling the two open
product/API calls. Do V-1 once per library after that library's generator work lands. **Until a real
consumer links the real package, "shippable" is a claim, not a proven fact** — so treat V-1 as the gate that
converts "the generator is done" into "the binding is verified shippable."

### V-1 — Pack-and-consume verification (per library)

Emitting correct bindings ≠ a NuGet package a clean consumer links and runs. The synthetic pack gates prove
the *mechanism*; V-1 proves *these* packages.
- **MapLibre (pure-ObjC pack lane).** Build the real nupkg (`dotnet nuke BuildLibrary --library MapLibre
  --all-products`), then from a *fresh* single-`PackageReference` consumer app, build + run on the iOS
  Simulator and on device (NativeAOT). Assert the map renders and a delegate callback fires — the spike
  app's scenarios, but consuming the *packed* artifact, not a project reference. This is the gap the
  synthetic gates don't cover: BindingTests has no pure-ObjC nupkg-consumption leg, and W-1 proves the
  generated binding, not the packed one.
- **Facebook (mixed ObjC+Swift pack lane).** The `--mixed-pack` shape on the *real* binding: pack the 5 FB
  kits, then from a single-`PackageReference` consumer, build (sim) and NativeAOT-publish (device), and
  assert Login + a Share flow round-trip with the ObjC classes registering exactly once (the
  duplicate-ObjC-registration hazard — "Class X is implemented in both …").
- **App Store hygiene (library-agnostic, once).** Run `nuke binding-tests --appstore-hygiene` — it asserts
  the runtime nupkg embeds as a signed framework and a built `.ipa` is TN2435-compliant. Must be green
  before any publish.

### V-2 — Settle FB-1b (API decision)

Either pick the disambiguated `TryCreate` naming rule (e.g. `TryCreateWithMessengerPageId` vs
`TryCreateWithNonce`) and implement it, or accept the two dropped inits as a documented consumer-facing
limitation (wiki Known Limitations). Owner call — do not autopilot the naming.

### V-3 — Product go/no-go

Owner decisions, not engineering: which FB kits ship (Login is the concentrated demand; confirm ShareKit
demand before shipping Share), MapLibre demand, and the version/lane per library — see **Ship decisions**
below. Then cut the release via the normal `release/**` flow.

---

## Facebook — measured skip accounting

The generator fail-closes on any member it can't faithfully bind, so a raw skip count over-reads as "thin".
The `SkipTriage` roll-up in each `binding-report.json` buckets every skip by actionability. Aggregated
across the four consumer kits:

| Kit | Types | Members | Skips | Never-public | By-design | Actionable | Review |
|---|---|---|---|---|---|---|---|
| Core (FBSDKCoreKit) | 158/159 | 429/1055 | 673 | 516 | 113 | 43 | 1 |
| AEM (FBAEMKit) | 25/25 | 70/201 | 157 | 98 | 55 | 4 | 0 |
| Login (FBSDKLoginKit) | 83/83 | 256/468 | 250 | 181 | 52 | 17 | 0 |
| Share (FBSDKShareKit) | 61/61 | 161/295 | 165 | 92 | 36 | 35 | 2 |
| **Total** | **327/328** | **916/2019** | **1245** | **887 (71%)** | **256 (21%)** | **99 (8%)** | **3** |

The 99 actionable skips by reason: 48 `UnsupportedSignature`, 25 `AnyTypeFallback`, 12
`UnsupportedExistential`, 8 `DuplicateSignature`, 3 `UnsupportedType`, 1 each of
`UnsatisfiedGenericConstraint` / `NonBlittableCallConvSwift` / `GenericProtocolConstraint`. ~45 of the 99
are still not consumer surface (internal DI infra, underscore SPI, by-design `[String:Any]` helpers). The
genuinely consumer-facing, cleanly-fixable remainder is the FB-1 / FB-2 / FB-3 / FB-1b sets above.

---

## Explicitly NOT worth doing

- **Internal DI infrastructure** (`DependentAsObject` / `DependentAsValue` / `DependentAsType`) and
  **underscore SPI** (`_WebDialog`, `_BridgeAPI*`, `_ShareUtility`, `_ViewImpressionLogger`) — ~30 of the 99
  "actionable" skips. Never consumer surface.
- **`[String:Any]` dictionary bridging** (`Share*Content.addParameters(_:options:)`) — by-design
  AnyType-in-container. Distinct from FB-3's `validate(options:)`, which is recoverable.
- **The 3rd Review proxy `CAPIReporter`** — its requirement references `GraphRequestFactoryProtocol`, an ObjC
  *protocol* with no Swift type-database record (a different missing-type class than FB-3's `NS_OPTIONS`).
  Recovery would need cross-pipeline import of ObjC protocol metadata into the Swift TypeDatabase — general
  but low payoff (only a C#-side *implementer* would notice, and none exists). Leave it.
  *(The other 2 Review proxies, `SharingContent`/`SharingValidatable`, are gated on FB-3 — re-evaluate after.)*

---

## Ship decisions (product calls, not generator work)

- **Facebook.** Feasibility is settled and the primary surface is runtime-proven. The open question is
  whether "primary surface + the gaps above" clears the quality bar, plus a ShareKit **demand check** (Login
  is the concentrated demand; don't assume Share). FB-1 (and optionally FB-3) raise polish; none of the
  above blocks the primary flows.
- **MapLibre.** GO on feasibility; ML-1 is fixed, so the only real gate is **W-1** (durable tests) before a
  NuGet ship.
- **Firebase** (out of scope here) — collaborate-vs-compete; both tooling gaps are closed, so it's a pure
  product/vision call, no generator work pending.

---

## Reproducing the measurements

1. Pack the SDK from `main`: `nuke pack --version <ver> --apple-version 26.2.8 --skip-apple`, copy the
   `SwiftBindings.{Runtime,Sdk,Templates}.<ver>.nupkg` into the packages repo's `local-packages/`, and wipe
   `~/.nuget/packages/swiftbindings.*/<ver>` so the fresh feed is used.
2. Clean `obj`/`bin`, then `dotnet nuke BuildLibrary --library Facebook --all-products` (mixed → emits
   `binding-report.json` per kit) or `--library MapLibre` (pure ObjC → emits `ApiDefinition.cs`; no report).
3. Each Facebook `binding-report.json` carries `EmittedTypes`/`EmittedMembers`/`SkippedItems[]` (with
   `Reason` / `ContainingType` / `Name` / `Details`) and the `SkipTriage` roll-up (`ByDisposition` +
   `ReviewItems`). Group `SkippedItems` by `Reason`, filter to the `KnownLimitation` tier for the actionable
   set. MapLibre is pure ObjC, so verify it structurally: build the binding, confirm `MLNMapView.Camera` is
   a working property in the generated `MLNMapView.g.cs`, and confirm every `DllImport("__Internal")`
   entrypoint is an *exported* symbol (`nm -gU` on the framework binary, both device and simulator slices).
