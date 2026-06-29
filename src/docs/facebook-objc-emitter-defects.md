# Facebook binding — remaining ObjC/Swift emitter defects (2026-06-29)

> **Status: IMPLEMENTED + verified.** Discovered while validating the issue-#40
> graceful-degradation fix (ObjC Session 3 / B3) against the real Facebook iOS SDK. B3 itself is
> **done and proven** — see "What already landed" below. Everything in this doc was a **separate,
> pre-existing defect class** orthogonal to B3; it blocked `nuke BuildLibrary --library Facebook
> --all-products` from compiling clean. The three designed classes (1–3) plus four more exposed once
> those cleared (Class 4 absent-type-inside-block leak, Class 5 Apple value-type-enum misclassified as
> ObjC class, Class 6 cross-product Swift-renamed ObjC superclass, Class 7 class-bound existential
> array accessor carrier — including its `[any P]?` optional and settable-subscript siblings) all
> shipped. `--all-products` now compiles clean; `nuke test`, `nuke binding-tests --compile-only`, and a
> full sim runtime run are green (zero regression); Codex + Grok paired review surfaced no surviving
> defects. Kept fail-closed by design (not fixed this pass): a doubly-nested block typedef inside a
> block signature still resolves to a dropped or NSObject-mapped member rather than a compile break,
> because `MapBlockType` does not thread the block-typedef map and the resolvability gate drops any
> unresolved inner name — a latent fidelity gap, not a defect, with no current consumer.

## How to reproduce

Worktree: `swift-dotnet-packages` Facebook fixture (binary mode, facebook-ios-sdk 18.1.0, minIOS 15.0;
products in dep order: `FBSDKCoreKit_Basics → FBAEMKit → FBSDKCoreKit → FBSDKLoginKit → FBSDKShareKit`).

```bash
# from swift-bindings: pack the local SDK (carries the generator) into the worktree feed
nuke pack --version <ver> --apple-version 26.2.8 --skip-apple
cp $TMPDIR/swift-nuget/SwiftBindings.{Runtime,Sdk,Templates}.<ver>.nupkg <worktree>/local-packages/
rm -rf ~/.nuget/packages/swiftbindings.*/<ver>
# clean Facebook product obj/bin to force a full regen (incremental skips the generator)
find <worktree>/libraries/Facebook -type d \( -name obj -o -name bin \) -exec rm -rf {} +
cd <worktree> && dotnet nuke BuildLibrary --library Facebook --all-products
```

The build hard-fails at **FBAEMKit (product #2)**, so FBSDKShareKit (#5) never reaches its compile pass
— but every product's `.cs` is generated under `…/<Product>/obj/Debug/net10.0-ios/swift-binding/`.
Mixed (ObjC+Swift) products emit `ApiDefinition.cs` + `StructsAndEnums.cs` + `BgenDelegates.cs` (ObjC
side) **and** `<Product>.cs` (Swift side); Swift-only products emit just `<Product>.cs`.

## What already landed (do not re-open)

- **B3 / issue #40 graceful degradation — proven on FBSDKShareKit.** Its `binding-emission-report.json`
  shows `degradedReverseDispatchReceivers: ["Sharing.shareContent setter", "SharingButton.shareContent
  setter"]` — the `any SharingContent` suppressed-proxy setters degrade to FailFast stubs instead of
  aborting the whole module, so FBSDKShareKit now binds (295 members, 122 skipped) and emits a full
  `.cs`. Before B3, `SuppressedProxyReferenceException` at those receivers killed the module.
- **ARC ownership-qualifier parser fix.** `ObjCTypeRefParser.StripObjCMacros` now strips
  `__strong`/`__weak`/`__unsafe_unretained`/`__autoreleasing` from pointer qualTypes. Without it, block
  typedef params like `NSData * __strong` left the trailing `*` unrecognized, so the pointer was never
  mapped and the literal ObjC text leaked into a C# delegate in `BgenDelegates.cs` (CS1003/CS1001).
  This is what previously blocked FBSDKCoreKit_Basics; it now emits valid C#.

## Defect classes (all pre-existing, orthogonal to B3)

### Class 1 — ObjC ApiDefinition emitter emits each type twice (structural)

Highest-volume class. The ObjC ApiDefinition emitter generates a **second** `partial interface` block
for some types, and that second block lists the type **itself** in its inheritance list.

Evidence — `FBSDKCoreKit/.../swift-binding/ApiDefinition.cs`, type `FBSDKBridgeAPIRequest`:

- `1265:  partial interface FBSDKBridgeAPIRequest : INSCopying`              ← first (correct) decl
- `4086:  partial interface FBSDKBridgeAPIRequest : INSCopying, FBSDKBridgeAPIRequest`  ← second decl
  lists itself → `CS0529` "causes a cycle in the interface hierarchy of itself"
- `4085:  error CS0579: Duplicate 'BaseType' attribute`  (the second block re-emits `[BaseType]`)
- `4101/4119:  CS0111/CS0102` — the second block re-defines members `RequestURL` / `Scheme`

Symptom totals across Core/CoreKit_Basics: **CS0102 ×18, CS0111 ×7, CS0529 ×11, CS0579** on
`FBSDKAppEventsConfiguration`, `FBSDKBridgeAPIRequest`, `FBSDKGraphRequest`, `FBSDKInternalUtility`,
`FBSDKErrorConfiguration`, `FBSDKKeychainStore`, several `*Factory` protocols, etc. Root cause: a
de-dup gap where a type reachable via two emission paths (e.g. plain interface **and** a
protocol/category/static-members path) is emitted as two full declarations, and the second
incorrectly re-lists the host type as a base. Fixing the de-dup (one declaration per type; merge
static/category members into it; never self-reference in the inheritance list) should clear all four
error codes at once.

### Class 2 — Swift-binding emitter naming/variance bugs (FBAEMKit.cs)

Two distinct bugs in the **Swift** binding output; these are what currently hard-fail the build.

- **`Handle` vs `NSObject.Handle` collision (`CS0428` ×3 + `CS0108`).**
  `FBAEMKit.cs:4013  warning CS0108: 'AEMReporter.Handle(NSUrl?)' hides inherited member
  'NSObject.Handle'` then `FBAEMKit.cs:3819/3820/3868  error CS0428: Cannot convert method group
  'Handle' to non-delegate type 'nint'`. A Swift method projected to C# `Handle(...)` shadows the
  `NSObject.Handle` (`NativeHandle`/`nint`) property on an NSObject-derived class; later code that
  reads the `Handle` property instead resolves the method group. Fix: collision-rename a projected
  member that would shadow a base-class property (the name-shaping already handles sibling
  collisions; this is the inherited-NSObject-member axis).

- **Nested `IReadOnlyDictionary` invariance (`CS0266`).**
  `FBAEMKit.cs:2926  Cannot implicitly convert IReadOnlyDictionary<string, Dictionary<string,object>>
  to IReadOnlyDictionary<string, IReadOnlyDictionary<string,object>>`. The inner dictionary value is
  projected as the concrete `Dictionary<…>` while the outer expects the `IReadOnlyDictionary<…>`
  element type. `IReadOnlyDictionary` is invariant in its value, so the element needs an explicit cast
  (cf. the "IReadOnlyDictionary invariance" architectural note — element conversions in containers need
  an explicit cast, unlike covariant `IReadOnlyList<T>`). The existing rule likely doesn't recurse into
  a dictionary-of-dictionaries value.

### Class 3 — cross-framework / system type resolution (`CS0246`)

`ApiDefinition.cs:2223  SKPaymentTransaction could not be found`; also `FBSDKAppLink` and
`ISKProductsRequestDelegate`. These are StoreKit / cross-product types the binding references but
doesn't resolve (no using/assembly reference, or a dependency product not surfaced to the consuming
compile). Likely overlaps the absent-framework-type handling (C1 / SWIFTBIND049) but on the ObjC
ApiDefinition path and across product boundaries; needs its own triage to decide drop-vs-resolve.

## Implementation plan

Settled design for a single implementation session. Each fork's design was stress-tested with the
Codex + Grok second/third-brain consults; where they diverged, the divergence and its resolution are
recorded inline so the session doesn't re-litigate. Every fix ships with tests at the layer that
actually exercises it (per the BindingTests-as-durable-gate policy).

### Class 1 — duplicate class/protocol emission → disambiguate the protocol, keep the class

**Design (settled).** When an ObjC name exists as BOTH a class and a (possibly `[Model]`) protocol,
they are two distinct runtime entities that happen to share a spelling — do **not** merge and do
**not** drop either. The **class keeps the bare name** `Foo` (it carries the real superclass, which
is load-bearing for bgen); the **protocol's managed name is renamed to `FooProtocol`** and emitted
with `[Protocol(Name = "Foo")]` so its native selector mapping is preserved. This is the canonical
dotnet/macios convention for exactly this clash (e.g. `NSAccessibilityElement` the class +
`NSAccessibilityElementProtocol` with `[Protocol(Name="NSAccessibilityElement")]`; `NSTextAttachmentCell`
likewise). Consumers get the class `Foo` AND the protocol interface `IFooProtocol`; the class's
conformance is rewritten to the renamed reference (so it lists `FooProtocol`, never a bare self-name).

> **Reviewer divergence (resolved).** Codex = rename the protocol to `FooProtocol` (lossless, cites
> macios source precedent). Grok = suppress the duplicate protocol decl entirely, class wins.
> **Chosen: Codex.** Suppression is lossy — it discards the protocol's members and breaks any other
> type that conforms to / takes a parameter typed as that protocol — and Grok did not surface the
> `FooProtocol` macios precedent that makes "one decl per *C# name*" achievable without losing an
> entity. Renaming satisfies the same "one decl per name" principle Grok argued for, losslessly.

**Fix site.** `src/Swift.Bindings/src/ObjC/Emitter/ApiDefinitionEmitter.cs`. The three loops in
`Emit()` have no cross-loop name tracking. Introduce a single **managed-protocol-name resolver** keyed
on the set of names that are *both* a class and a protocol in the module (compute once from
`module.Classes` ∩ `module.Protocols` at the top of `Emit()`):
- `EmitProtocol` (name/attrs at ~159-169): when the protocol's name is in the clash set, emit the
  managed interface as `{Name}Protocol` and add `[Protocol(Name = "{Name}")]` (carry `[Model]` if the
  protocol already requires it; the model class then becomes `FooProtocol`, not `Foo`).
- The resolver must be consulted at **every** site that references a local protocol by managed name —
  not just the decl: `ProtocolInterfaceReference` (~770, the bare-local-name → `IFoo` path),
  forward-declaration emission, inherited-protocol lists, member protocol-typed references, and
  delegate detection. The current bare-native-name keying cannot remain.
- `EmitClass` conformance list (~280-287): rewrite a conformance whose name is in the clash set to the
  renamed `{Name}Protocol` reference (→ `IFooProtocol`); this also removes the CS0529 self-cycle since
  the class name `Foo` no longer appears in its own inheritance list.

This single resolver clears CS0579 / CS0102 / CS0111 / CS0529 together (they are all the one
double-emission root). Categories are already distinctly named (`Foo_Bar`) — leave them untouched.

**Tests.** Unit/emitter layer — `ApiDefinitionEmitterTests.cs`, building the fixture via
`ObjCModuleBuilder` with a name present as both a class and a protocol (and a second case where the
protocol is `[Model]`). Assert: exactly one `partial interface Foo` with the class's real `[BaseType]`;
a `[Protocol(Name="Foo")] partial interface FooProtocol`; the class conformance lists `FooProtocol`
(not bare `Foo`); no self-reference in any inheritance list; a referencing member resolves to
`IFooProtocol`. (No BindingTests leg — there is no lightweight ObjC-xcframework fixture harness; the
emitter test is the durable gate for this path.)

### Class 2, Bug A — Swift method shadows inherited `NSObject.Handle` → seed the NSObject property set

**Design (settled — both reviewers agree).** `Handle` is an inherited NSObject **property**, so it is
covered by neither the sibling-property axis nor the `_inheritedMethodCollisions` (method) axis. Seed
the `propertyNames` set — the input to the existing sibling-property rename axis — with the curated set
of C#-surfaced NSObject **instance property** names, but **only when `classDecl.IsObjCRooted`**. The
existing axis then renames any projected method that would shadow one (`Handle` → `HandleMethod` /
`WithHandle`), uniformly, with no new rename logic.

Curated set (static source, parallel to `_inheritedMethodCollisions` in `NameProvider.cs` ~884):
`Handle, SuperHandle, IsDirectBinding, Class, Description, DebugDescription, Zone, Self, Superclass,
RetainCount, IsProxy`. **Exclude `Hash`** — .NET surfaces it as the method `GetNativeHash`, not a
property, so seeding it would spuriously rename. Do **not** mass-add NSObject *methods* to the method
axis (C# permits hiding; over-seeding renames legitimate Swift APIs); extend that set only on a
demonstrated compile/semantic hazard.

**Fix sites.**
- `src/Swift.Bindings/src/Marshaler/NameProvider.cs` — add the static curated `_objCRootedInheritedPropertyNames`
  set near `_inheritedMethodCollisions`.
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ClassHandler.cs` (~383-389) — where
  `propertyNames` is built from own properties + nested types, union in the curated set when
  `isObjCRooted` (the fact is already computed at ~159), before it flows to `HandleBaseDecl` (~399).

**Tests.**
- Unit — `ClassObjCRootedTests.cs`: an ObjC-rooted class with a Swift method whose projected name is
  `Handle` renames; a **non**-ObjC-rooted class with the same method keeps `Handle` (gating proof).
- **Drift test** (Codex's addition): a reflection-based test over the repo's supported Microsoft.iOS
  NSObject surface asserting every public NSObject **instance property** capable of colliding is in the
  curated set, with documented exclusions (`Hash`). Reflection lives in the *test* only — never in
  generation (reproducible generation must not depend on an installed workload) — and makes SDK drift
  visible loudly.

### Class 2, Bug B — nested `IReadOnlyDictionary` invariance → route through the cast owner

**Design (settled).** `IReadOnlyDictionary<K,V>` is invariant in `V`, so a `Dictionary<string,object>`
inner value cannot implicitly convert to the `IReadOnlyDictionary<string,object>` element type the
outer projection expects. The correct cast already exists in `DictionaryProjection.BuildAsProjected`
→ `CastValueSelectorBody` (it applies the invariant-slot cast when the value is itself an
Array/Dictionary/Set projection). The defect is that the **dictionary getter accessor inlines its own
`AsProjected`** and bypasses that cast.

**Fix site.** `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/AccessorConversionVisitors.cs` —
`DictGetterConversion` (~57-83) inlines `AsProjected` at ~67/75/80 without the cast. Route it through
`dict.BuildAsProjected(keyConv, valConv)` so it reuses `CastValueSelectorBody`, matching how
`GetReceiverDictSetterConversion` (`ProtocolProxyEmitter.Receivers.cs` ~1890) already delegates.

**Tests.**
- Unit — `DictionaryProjection`/accessor test: a `[String: [String: Any]]`-shaped property asserts the
  emitted getter contains the value-slot cast (semantic check, not exact-string).
- **BindingTests** (this one CAN go end-to-end): add a Swift property of type `[String: [String: Any]]`
  to `SwiftBindingsTestLib`, round-trip it in the matching C# domain test. Dictionary-of-dictionaries
  marshalling is exactly the kind of ABI/projection behavior BindingTests exists to catch.

### Class 3 — missing Apple-framework `using` → derive usings from authoritative provenance

**Design (settled — supersedes both r1 recommendations).** The member-resolvability guard correctly
classifies Apple SDK types (e.g. StoreKit's `SKPaymentTransaction`) as resolvable and emits members
referencing them, but the curated `ObjCUsingsEmitter.ApiDefinitionUsings` list omits `using StoreKit;`
→ CS0246. The **root cause** is that the `using` set is a hand-maintained kitchen-sink list, decoupled
from what the binding actually references; adding `StoreKit` fixes Facebook but repeats the defect for
the next unlisted framework.

The prep phase found a fact neither reviewer had: **the owning-framework provenance is already in hand
at the collection site.** `ClangAstParser.cs:155-159` adds each Apple SDK type name while holding
`nodeResolvedFile` — the resolved header path — which for SDK types is `…/<Framework>.framework/Headers/…`.
The `.framework` segment is the **authoritative** owning framework (ground truth, not prefix inference),
and the registry already carries the framework→.NET-namespace remap machinery
(`AppleFrameworkRegistry.MapModuleToNetNamespace` / `NamespaceRemap`, e.g. `QuartzCore`→`CoreAnimation`).

> **Reviewer divergence (resolved by new evidence).** Codex = curated list + add StoreKit; adopt
> dynamic *only when authoritative type-to-framework provenance exists* (argued prefix inference is
> unsound — and it is: the registry's name set carries no module, and prefix `CM` is ambiguous between
> CoreMedia/CoreMotion). Grok = derive dynamically via registry **prefixes** (general, but that's the
> unsound inference Codex flagged). **Chosen: provenance-based dynamic derivation** — which satisfies
> *both* (it's Codex's "provenance exists" gate met via the header path, and Grok's generality), and
> is the root-cause fix no-shortcuts demands. Codex's own decision criterion ("switch when authoritative
> provenance exists") is now met.

**Fix sites.**
- `src/Swift.Bindings/src/ObjC/Model/ObjCModule.cs` (~29) — change `AppleSdkTypeNames` from
  `HashSet<string>` to a name→owning-namespace map (`IReadOnlyDictionary<string,string>` of type name
  → resolved .NET namespace), or a richer record set.
- `src/Swift.Bindings/src/ObjC/Parser/ClangAstParser.cs` (~155-159) — parse the `<Framework>.framework`
  dir out of `nodeResolvedFile`, map it through `AppleFrameworkRegistry.MapModuleToNetNamespace`, store
  alongside the name.
- `src/Swift.Bindings/src/ObjC/Emitter/ObjCUsingsEmitter.cs` — emit the **union of namespaces of the
  Apple SDK types actually referenced by emitted members** + `AlwaysAvailable`, deterministically
  sorted (churn-free). Keep the startup registry assertion as the safety net.
- Update the two `AppleSdkTypeNames` consumers (`ObjCPipeline.cs:470`, `ApiDefinitionEmitter.cs:32`).

**Decision gate / fallback.** A residual risk: a small number of Apple types live outside a
`.framework` (e.g. `/usr/include/…` runtime headers) and won't yield a dir, and a few dir→namespace
pairs may need a remap entry. During implementation, after wiring provenance, regen Facebook and check
that every referenced Apple framework resolves to a known namespace. If a meaningful fraction don't and
can't be cheaply remapped, **fall back for this session** to Codex's curated approach: add `StoreKit`
(and any other Apple frameworks in Facebook's CS0246 set) to `ApiDefinitionUsings`, and leave the
provenance derivation as the next tracked item (now de-risked and specified). Cross-product *third-party*
sibling types (`FBSDKAppLink`) stay **dropped** — no generator-side `using` resolves them; that part is
already correct and out of scope.

**Tests.** Unit — `ObjCUsingsEmitterTests.cs`: a module whose emitted members reference an Apple SDK
type from a framework not in today's list (StoreKit/`SKPaymentTransaction`) emits exactly
`using StoreKit;`; a `QuartzCore`-provenance type emits `using CoreAnimation;` (remap proof); an
unreferenced framework is not emitted (minimal-set proof). If the fallback path is taken, assert the
curated list now contains the needed namespace and the startup assertion still passes.

### In-session sequence

1. **Class 1 first** — dominant compile-error source and a single de-dup root; unblocks Core/CoreKit_Basics.
2. **Class 2 Bug A**, then **Bug B** — small, self-contained Swift-emitter fixes closest to current work.
3. **Class 3 last** — the parser-model change (provenance) is the most invasive; do it once the
   compile surface above is green so its regen signal is clean. Honor the decision gate.
4. After each class: `dotnet build src/Swift.Bindings/src -c Debug` (stale binary masks regen), then the
   layer's unit tests. After all three: `nuke binding-tests --compile-only` then `--skip-regen` (Bug B's
   end-to-end leg), and a full Facebook regen via the repro recipe above to confirm `--all-products`
   compiles clean. `nuke validate` only if the Class 1 resolver proves broader than the clash set.
