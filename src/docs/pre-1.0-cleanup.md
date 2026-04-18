# Pre-1.0 Cleanup

Running list of design decisions that were made conservatively (preserving
compatibility, deferring breakage) and should be revisited before 1.0.

**Operating principle:** pre-1.0 is the window where breaking changes are
free. Decisions that accepted ugliness "to avoid breaking consumers" need
to be re-evaluated now, not after 1.0 locks us in.

---

## Session plan

Four sessions. Ordering matters: Session 1 reshapes the Apple package,
Session 2 renames on top of that shape, Session 3+4 are generator work
that doesn't touch package identity.

### Session 1 — Apple package exodus (items #1, #6, #7, #8, #13) ✅ `ceaaec3f`

Move the eight legacy canonicals + `CIContext` out of `SwiftBindings.Runtime`
into `SwiftBindings.Apple`. While already in the supplement's csproj, drop
the blanket `SB1001` + `CS1591` suppressions, populate the `runtime_tests`
baseline, and extend the manifest validator to exercise non-POD VWT and
`Optional<T>` round-trip. One regeneration wave at the end covers all of
it.

**Why together:** single theme — `SwiftBindings.Apple` becomes the shape
it should have had from the start. All downstream bindings regenerate
once against the new type ownership.

**Autocompaction risk:** high. Item #1 alone has wide blast radius across
every binding that references `Foundation.Date` etc. If it cascades badly,
push #13 to Session 2's tail.

### Session 2 — Renames & symbol cleanups (items #2, #3, #5 ✅ · #4 deferred)

Rename framework packages to `SwiftBindings.Apple.*`. Delete
`SwiftSafeHandle<T>.RegisterDestroyAction`. Unify `MCB_{hash}` closure
symbol naming to always-indexed. Single regeneration wave.

**Item #4 deferred.** Removing the `status` / `isEligibleForIntroOffer`
`Property`-suffix overrides regressed `OrderContainer.Status` /
`PaymentContainer.Status`: those types have both a nested `Status` enum
*and* a `status` property, which collide when the property pascals to
`Status`. `NameProvider.GetPropertyName`'s existing collision logic only
handles CS0542 (member-name-same-as-containing-type) via the `"Value"`
suffix — it does not see nested types. Extending it requires passing
nested-type names through every `GetPropertyName` call site (~30
locations), which is larger than Session 2's scope. Tracked as
follow-up; see item #4 below.

**Why together:** all four require regenerating downstream bindings.
Batching saves three regen cycles and one `swift-dotnet-packages` PR
storm. Same coordination surface.

### Session 3 — Protocol correctness + dead code (items #9, #11)

Flip the inherited-protocol requirement count in `ProtocolHandler` to the
correct value, chase every downstream assert that breaks. Audit that every
async view resolves through `AsyncViewPattern`, then delete the legacy
`SwiftUIBridgeEmitter` paths.

**Why together:** both are bounded emitter changes. #9 is the correctness
fix (highest-priority bug of the 13 items — the rest are aesthetics or
scope). #11 is a delete-after-audit.

### Session 4 — Emitter architecture completion (items #10, #12)

Implement single-wrapper multi-outer-closure ABI in `NestedClosureBridge`.
Implement non-void result marshalling in `ExistentialBypassEmitter`.

**Why together:** both expand emitter paths that were scope-limited at
introduction. Back-to-back work keeps the ABI mental model warm between
them.

---

## 1. Move legacy canonicals out of `SwiftBindings.Runtime` into `SwiftBindings.Apple`

**Current state.** `SwiftBindings.Runtime` owns a grab bag of Apple-specific
Swift value types that were hand-rolled before the supplement existed:

- `Foundation.Date`, `Foundation.Data`, `Foundation.URL`, `Foundation.Decimal`
- `Foundation.Measurement<T>`, `Foundation.AnyError`
- `ManagedSettings.Token<T>`
- `SwiftUI.Text`

These are pinned to `Runtime` by per-type owner overrides in
`TypeOwnerRegistry`. `SwiftBindings.Apple` only contains *newly*-generated
Swift-only types (Locale.Language, ManagedSettings.Application, CryptoKit
signatures, etc.). Every other Apple-module type routes to `Apple` via
module default.

**Why it's wrong.** `SwiftBindings.Runtime` is supposed to be
SDK-agnostic — core interop infrastructure (SafeHandle, ARC, SwiftString,
SwiftArray, TypeMetadata/VWT) versioned on generator cadence. Apple types
in `Runtime` violate that invariant:

- Couples Runtime version to Apple SDK behavior. A fix to `Foundation.Date`
  semantics ships on the `0.8.x` generator train, not the `26.x` Apple
  train, so consumers get it on the wrong cadence.
- Split ownership is confusing. "Why is `Locale.Language` in Apple but
  `Date` in Runtime?" has no principled answer — only historical.
- Forces the supplement design doc to carve out exceptions (architecture
  doc decision #2, Q1) instead of stating one rule.

**Why we deferred it.** The `apple-swift-types-architecture.md` analysis
concluded `[TypeForwardedTo]` migration was blocked:

- Forwarding requires `Runtime` → `Apple` reference (cycle; violates
  runtime SDK-agnosticism).
- Forwarding can't rename types, so `Swift.Runtime.Date` →
  `Swift.Foundation.Date` would break every consumer.

Both objections assume we need source-compat. Pre-1.0, we don't.

**Proposal.** Move all eight legacy canonicals from `Swift.Runtime/src/Swift/`
to `Swift.Bindings.Apple/` (or generate them from manifest entries, same
as the rest of the supplement). Namespace them correctly:
`Swift.Foundation.Date`, `Swift.SwiftUI.Text`, `Swift.ManagedSettings.Token<T>`,
etc. Delete the per-type owner overrides from `TypeOwnerRegistry` — they
fall through to the module-default `Apple` route like everything else.
`Runtime` is left with only SDK-agnostic primitives.

**Breakage.** Every consumer that references `Swift.Runtime.Date` and
friends will fail to compile. That's acceptable — we're pre-1.0, the
audience is small, and the fix is a namespace rename. Release notes for
the breaking version call it out.

**Open questions to resolve when we do this.**
- Are these types hand-rolled or manifest-driven in their new home?
  Probably manifest-driven for consistency, but some (Measurement<T>,
  AnyError) have custom behavior the current VWT-opaque template doesn't
  cover.
- Does `Runtime` end up with anything Apple-specific at all after the
  move? If not, we can drop the SDK-train-agnostic caveats from the
  Runtime docs.
- What version does this land in? Probably the same release that does
  other breaking pre-1.0 cleanup, not a dedicated break.

---

## 2. Rename Apple framework packages to `SwiftBindings.Apple.*`

**Current state.** Framework binding packages ship as flat names:
`SwiftBindings.StoreKit2`, `SwiftBindings.WeatherKit`,
`SwiftBindings.CryptoKit`, etc. The supplement ships as
`SwiftBindings.Apple`. There's no visual grouping that distinguishes
Apple-framework bindings from third-party Swift-library bindings — a
future `SwiftBindings.Nuke` would sit in the same namespace as
`SwiftBindings.StoreKit2`.

**Why it's wrong.** The flat scheme conflates two different categories:
Apple-framework bindings (versioned per Apple SDK train, share the
`SwiftBindings.Apple` supplement as a dependency) and third-party Swift
bindings (versioned per upstream library, don't touch the supplement).
Nesting Apple framework packages under `SwiftBindings.Apple.StoreKit2`,
`SwiftBindings.Apple.WeatherKit`, etc. makes the family relationship
explicit and leaves `SwiftBindings.*` at the top level for third-party
bindings.

**Why we deferred it.** `0.8.0-ship-plan.md:667` calls it out
explicitly: "only at a deliberate major-version cleanup." Renaming a
NuGet package ID breaks every consumer's `<PackageReference>`. Pre-1.0
that's fine.

**Proposal.** Rename all Apple-framework packages to the nested scheme
in one coordinated commit. `SwiftBindings.Apple` stays as-is (it's
already correctly named). Update `build/validation-libraries.json`,
`release.yml`, and the downstream `swift-dotnet-packages` repo in the
same release.

**Breakage.** Every consumer `<PackageReference Include="SwiftBindings.StoreKit2">`
breaks. Release notes + a table of old→new names is enough.

---

## 3. Delete `SwiftSafeHandle<T>.RegisterDestroyAction`

**Current state.** `src/Swift.Runtime/src/Swift/Runtime/SwiftHandle.cs:65-77`
defines `RegisterDestroyAction(Action<IntPtr>?)` as a public no-op. The
XML doc says outright: "The registered action is ignored — VWT Destroy
is always used directly." It exists only because previously-generated
bindings emitted `@_cdecl destroy` wrappers that called this during
static init.

**Why it's wrong.** Dead public API. Nothing in the current generator
emits calls to it; no in-repo source references it. Every consumer
reading `SwiftHandle.cs` has to figure out that the method does nothing,
and the "backward compat" comment implies it matters.

**Why we deferred it.** Compat with binding assemblies generated by
pre-VWT-destroy versions of the generator. Pre-1.0, nobody is running
those.

**Proposal.** Delete the method. Regenerate any lingering bindings
against the current generator — they won't emit the call site.

**Breakage.** Any consumer holding a hand-edited or ancient pre-VWT
binding assembly that calls `RegisterDestroyAction` in its module
initializer fails to compile. Audit `swift-dotnet-packages` and any
other downstream repos for call sites first — if none, this is a
zero-risk delete.

---

## 4. Remove hardcoded `"Property"` suffix overrides in `NameProvider`

**Current state.** `src/Swift.Bindings/src/Marshaler/NameProvider.cs:106-113`
hardcodes two Swift→C# rename overrides:

```csharp
{ "isEligibleForIntroOffer", "IsEligibleForIntroOfferProperty" },
{ "status", "StatusProperty" }
```

Every other collision in the generator uses the general collision-detection
logic that appends `"Value"`. These two entries predate that logic and
use `"Property"` — inconsistent with the rest of the codebase.

**Why it's wrong.** Two special cases for one downstream consumer
(StoreKit2 bindings) is the wrong shape. The naming rule is supposed to
be "on collision, append `Value`"; these break that rule with no
principled reason beyond "this is what StoreKit2 shipped with."

**Why we deferred it.** Published `SwiftBindings.StoreKit2` consumers
reference `IsEligibleForIntroOfferProperty` and `StatusProperty` by
name. Removing the overrides renames them to `…Value` forms and breaks
every StoreKit2 consumer's source.

**Proposal.** Delete both entries. Let the general collision logic
produce `IsEligibleForIntroOfferValue` and `StatusValue`. Regenerate
`SwiftBindings.StoreKit2` in the same release.

**Breakage.** StoreKit2 consumers referencing the two renamed properties
fail to compile until they rename. Count is small (two property names in
one framework); release notes handle it.

**Session 2 attempt (deferred).** Removing the overrides alone is not
enough. `OrderContainer` and `PaymentContainer` in the BindingTests Swift
library declare both a nested `Status` enum *and* a `status` property.
Without the `"status" → "StatusProperty"` override, the property pascals
to `Status`, which collides with the nested enum type —
`OrderContainer.Status.FromRawValue(...)` then resolves to the instance
property instead of the nested type and fails to compile. The existing
CS0542 collision logic (`pascalName == containingTypeName → +"Value"`)
does not see nested types.

**Real fix.** Extend `NameProvider.GetPropertyName` to accept the set of
nested type names on the containing type and append `"Value"` when the
pascaled property name matches any of them. Plumb nested-type names
through the ~30 `GetPropertyName` call sites (most already have the
`TypeDecl` in scope). Then re-delete the overrides. Track as a follow-up
within the Session 2 scope — the other three items in this session
shipped.

---

## 5. Unify `MCB_{hash}` closure symbol naming — always index

**Current state.**
`src/Swift.Bindings/src/Emitter/StringEmitter/Handler/MethodClosureBridge.cs:171-173`
(and the parallel path in `NestedClosureBridge.cs:222`) special-cases
single-closure methods: when a method has exactly one closure parameter,
the emitted bridge symbol is bare `MCB_{hash}` with no index. Only
multi-closure methods get `MCB_{hash}_0`, `MCB_{hash}_1`, etc.

**Why it's wrong.** The naming rule is context-dependent. A method with
one closure today can grow a second closure tomorrow, and the first
closure's symbol silently changes shape (`MCB_{hash}` →
`MCB_{hash}_0`). The emitter carries branching logic to maintain the
special case. The uniform `_0`-indexed form is simpler, predictable,
and matches the multi-closure path.

**Why we deferred it.** The comment says it outright: "single closure
preserves backward compat." Previously-generated Swift `@_silgen_name`
wrapper symbols and the C# `EntryPoint` strings in published binding
assemblies use the bare form. Switching to `_0`-indexed changes the
symbol at runtime and breaks every existing single-closure binding.

**Proposal.** Always emit `MCB_{hash}_0` (and `NCB_{hash}_0`, wherever
the nested-closure path does the same thing). Regenerate all published
bindings in the same release so Swift wrapper and C# P/Invoke stay in
sync.

**Breakage.** Any consumer still running a pre-rename binding assembly
hits `EntryPointNotFoundException` at the first single-closure call.
Mitigated by regenerating `swift-dotnet-packages` and any other
downstream binding packages in lockstep with the generator change. No
consumer **source** changes — this is an ABI-only break at the
generated-symbol level.

---

## 6. Migrate `Swift.CIContext` out of `Runtime` into a `CoreImageDatabase.xml` remap

**Current state.** `src/Swift.Runtime/src/Swift/CIContext.cs` is a
hand-rolled ObjC wrapper class, the last of its kind. Every other
analogous type (`URLResponse`, `UIImage`, `NSImage`, `NSColor`,
`OperationQueue`) was already migrated to XML-database remaps that
route consumers to the Microsoft.iOS ObjC projection (e.g.
`CoreImage.CIContext`). `CIContext` still registers an ObjC factory
in `SwiftFrameworkResolver.cs:65` and has a dedicated owner override
in `TypeOwnerRegistry.cs:432`.

**Why it's wrong.** This is the exact same category as item #1 —
Apple-specific type sitting in `Runtime`, violating the SDK-agnostic
invariant — but with a second problem: the hand-rolled wrapper
dispatches through Swift thunks that don't exist for ObjC-imported
members. `Swift.CIContext`'s public surface promises methods that
throw `EntryPointNotFoundException` at call time.

**Why we deferred it.** Roadmap notes say "pending a dedicated remap
session." No compat reason — just nobody scheduled the work.

**Proposal.** Delete `CIContext.cs`, remove the factory registration
in `SwiftFrameworkResolver`, remove the `TypeOwnerRegistry` override,
add a `CIContext` entry to `CoreImageDatabase.xml` that remaps to
`CoreImage.CIContext`. Same pattern used for the five predecessors.

**Breakage.** Consumers using `Swift.CIContext` rename to
`CoreImage.CIContext` (or get it via the remapped path automatically,
depending on how they reference it). Fold into the same release as
item #1.

---

## 7. Un-suppress `SB1001` and `CS1591` on `SwiftBindings.Apple`

**Current state.** `src/Swift.Bindings.Apple/Swift.Bindings.Apple.csproj:14-15`
blanket-suppresses two warnings across the whole package:

```xml
<NoWarn>$(NoWarn);SB1001;CS1591</NoWarn>
```

The comment explains: "SB1001: Runtime analyzer fires on the interop
primitives this package builds on. CS1591: Public API XML docs land as
types get generated; suppress for skeleton."

**Why it's wrong.** The skeleton is done — 13 types are emitting. The
suppressions now silence real signal:

- **SB1001** is the runtime analyzer that catches `ISwiftObject`
  implementation errors. Generated VWT-backed supplement types are
  the exact surface this analyzer exists for. Suppressing it on the
  supplement package removes the only automated backstop for the
  generated code in this package.
- **CS1591** means every public supplement type ships with zero XML
  docs — no IntelliSense tooltip for `Foundation.Locale.Language`.

**Why we deferred it.** "For skeleton" — get the package to build
before the generator emitted anything substantive.

**Proposal.** Drop both entries from `NoWarn`. Fix any legitimate
SB1001 violations in the emitted types (or, if the analyzer genuinely
doesn't apply, add a targeted `#pragma warning disable` with a reason,
not a blanket suppression). For CS1591, either emit XML docs from the
generator (ideal — propagate Swift doc comments) or add `<inheritdoc/>`
forwarding.

**Breakage.** None for consumers; fixing the violations is internal
work.

---

## 8. Populate `runtime_tests` in `.validation-baseline.json`

**Current state.** `.validation-baseline.json:697` sets
`"runtime_tests": null`. Only compile-gate pass counts are tracked.
`0.8.0-ship-plan.md` states a zero-regression policy covering
"BindingTests runtime pass count" — but that leg of the policy has
nothing to assert against.

**Why it's wrong.** A generator change can silently regress ABI
marshalling (wrong calling convention, wrong parameter order, etc.)
while compile_gate stays green. BindingTests exist precisely to catch
that class of bug. With `runtime_tests: null`, CI can't enforce the
policy — a dropped pass count passes.

**Why we deferred it.** Not stated. Probably the baseline was seeded
from compile-gate data and the runtime leg was left as TODO.

**Proposal.** Run `nuke runtime-tests-simulator` once on a clean
branch, record total pass count (and optionally the per-class
breakdown), populate the field. Add a baseline check to CI that fails
if the number drops.

**Breakage.** None — internal tooling change.

---

## 9. Enable inherited-protocol requirement counting in `ProtocolHandler`

**Current state.** `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ProtocolHandler.cs:1211-1216`
hardcodes `inheritedRequirementCount = 0` with a TODO: "Enable once
all consumers handle inherited protocol requirements correctly."
`InheritedProtocols` is populated but its requirements are not
counted into `EmittedMemberCount`.

**Why it's wrong.** `EmittedMemberCount` feeds vtable sizing and
downstream conformance checks. For protocols that inherit members
from parent protocols, the count is wrong by construction. Correctness
hole, not a style issue.

**Why we deferred it.** "All consumers" means the generator + runtime
paths that read `EmittedMemberCount`. Enabling the correct count shifts
values downstream; the author didn't want to chase the fallout in the
same change.

**Proposal.** Flip the count to the correct value, chase every
downstream assert/test that breaks, and update them to the correct
expected number. This is the right-design change and it has to land
before any protocol with inherited requirements ships.

**Breakage.** Regenerated bindings for protocols with inherited
requirements may produce slightly different conformance metadata.
Expected and intended.

---

## 10. Ungate `NestedClosureBridge` from single-outer-closure methods

**Current state.**
`src/Swift.Bindings/src/Emitter/StringEmitter/Handler/NestedClosureBridge.cs:57-60`
rejects methods with more than one outer closure parameter:

```csharp
// Multiple outer closures require a single Swift wrapper with ALL funcPtr/context pairs,
// but current architecture emits one wrapper per outer closure with mismatched P/Invoke ABI.
// Re-gate until single-wrapper multi-outer architecture is implemented.
if (closureArgs.Count > 1) return false;
```

**Why it's wrong.** The correct ABI — one Swift wrapper with all
funcPtr/context pairs — is explicitly documented in the comment. The
gate exists because the emitter currently produces one wrapper per
outer closure with a mismatched P/Invoke signature. Entire real-world
methods are being silently dropped pending the rewrite.

**Why we deferred it.** Scope of the emitter rewrite.

**Proposal.** Implement the single-wrapper multi-outer emission path.
Remove the gate. Add BindingTests for 2- and 3-outer-closure methods.

**Breakage.** Additive — methods that were previously skipped now
emit. No existing consumer loses surface.

---

## 11. Delete `SwiftUIBridgeEmitter` legacy async paths

**Current state.** `src/Swift.Bindings/src/Emitter/.../SwiftUIBridgeEmitter.AsyncPattern.cs`
carries three "legacy hard-coded" emission methods
(`EmitLegacySessionClass`, `EmitLegacyAsyncCreate`,
`EmitLegacyCreateAsyncFactory`) alongside the data-driven
`AsyncViewPattern` system. The file header says "v1: Only
BlinkIDUXView is supported"; the legacy fork persists behind a
`ConstructionChain == null` branch.

**Why it's wrong.** Two code paths for the same concept. Every new
async view pattern must be exercised against both paths or risk
divergence. Classic parallel-abstraction tech debt.

**Why we deferred it.** The data-driven system was introduced
incrementally; the legacy path was left in until every known async
view flowed through the new system.

**Proposal.** Confirm every async view in the validation set resolves
through `AsyncViewPattern`. Delete the three legacy methods and the
`ConstructionChain == null` fork. The file header comment stops being
a lie.

**Breakage.** Internal only. Regenerate consumers; generated output
should be byte-identical if the audit is correct.

---

## 12. Lift `ExistentialBypassEmitter` void-return-only restriction

**Current state.**
`src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ExistentialBypassEmitter.cs:327-333`
rejects any method with a non-void return: "Only void return for now —
non-void returns need result marshalling." Methods that would otherwise
qualify for the bypass (cheaper path than a full `@_cdecl` wrapper)
fall through to the heavier path.

**Why it's wrong.** Arbitrary scope limit. The bypass path is a real
optimization for existential-bearing method calls, and "protocol-typed
return value" is a common Swift pattern. Falling back silently means
wrappers are fatter than they need to be and the bypass path covers
less surface than it should.

**Why we deferred it.** Result marshalling is non-trivial for
existential returns (you get back an ExistentialContainer the caller
has to unwrap). Author scoped down to void to ship the bypass at all.

**Proposal.** Implement non-void result marshalling in the bypass
path — likely mirrors whatever the full wrapper does for existential
returns.

**Breakage.** Internal optimization. Generated P/Invoke signatures
shift for affected methods, so downstream bindings regenerate.

---

## 13. Exercise non-POD VWT and `Optional<T>` round-trip in `ValidateAppleTypesManifest`

**Current state.** Per `apple-swift-types-architecture.md:94-95`, the
CI validator currently checks: metadata accessor resolves, manifest
size/alignment/stride match the live VWT, and **POD** types pass a
VWT copy/destroy smoke. `Container/Optional round-trip and non-POD
VWT exercise are explicit future work.`

**Why it's wrong.** The supplement's default storage strategy is
VWT-opaque — non-POD types are the common case, not the edge case.
A type that passes the POD smoke but corrupts memory during non-POD
destroy gets the validator's green light and ships. The validator's
gate is looser than the set of types going through it.

**Why we deferred it.** Shipping the validator at all was Phase 2
scope; author scoped down to POD smoke + symbol/size checks and left
non-POD + Optional as explicit follow-ups.

**Proposal.** Extend the validator to:
- Instantiate each type through its metadata accessor.
- Wrap it in `Optional<T>` and round-trip through the VWT.
- For non-POD types, exercise copy + destroy and verify no leaks
  (or crashes) via a runtime-level smoke.

**Breakage.** None for consumers. Validator may fail on currently-
passing supplement types once it actually exercises them — those
failures are real bugs the loose gate was hiding.

---

<!-- Add further cleanup items below as separate numbered sections. -->
