# Session 12 — AppIntents 0.12 platform parity

Follow-on to session 8 (AppIntents productionization). The 0.12.0 SDK + regen
sweep against `apple-frameworks/AppIntents/` surfaced two distinct
post-fix regressions after commits `c742e3e0` (v1 framework flip), `d408df92`
(CoreSpotlight wrapperImportable flip), `510d8717` (UnderscoreProtocolSynthesizer),
and `77f19a80` (dependent-member + variadic-pack gates). Both are AppIntents
0.12.0–scoped — they did not appear against the earlier SDK because the
underlying surfaces were not reachable until the gates above landed.

## State as of 2026-05-22

Regen of `/Users/wojo/Dev/swift-dotnet-packages/apple-frameworks/AppIntents/`
against locally-published `SwiftBindings.Sdk` / `SwiftBindings.Runtime`
0.12.0 + live NuGet `SwiftBindings.Apple` 26.2.3:

| Platform | Stage that fails | Errors | Shape |
|---|---|---|---|
| iOS 26.2 | Swift wrapper compile (MSB3073) | 12× "expect a compile-time constant literal" at `AppIntents.Wrapper.swift:3810,3822` | Item A |
| tvOS 26.2 | Cascade from iOS (same wrapper) | — | Item A |
| macOS 26.2 | C# compile (post wrapper success) | 454 errors (CS0535 × ~440, CS0246 × ~14) | Item B |
| macCatalyst 26.2 | C# compile (post wrapper success) | 456 errors (same shape as macOS) | Item B |

iOS / tvOS C# emission is **never attempted** because the wrapper build fails
first. Items A and B are independent — fixing A unblocks iOS C# emission but
does nothing for the macOS/Catalyst schema cascade; fixing B does nothing for
iOS wrapper compile. Both items must land for full four-platform parity.

---

## Item A — `IntentCollectionSize` const-literal init filter expansion

### Evidence

`obj/Debug/net10.0-ios26.2/swift-binding/AppIntents.Wrapper.swift:3810`:

```swift
@_cdecl("SBW_AppIntents_IntentCollectionSize_init_5280119B")
public func _sbw_init_825F7E30(_ resultPtr: UnsafeMutableRawPointer, _ min: Int, _ max: Int) {
    let result = AppIntents.IntentCollectionSize(min: min, max: max)   // ← line 3810
    resultPtr.assumingMemoryBound(to: AppIntents.IntentCollectionSize.self).initialize(to: result)
}
```

`swiftc` fails at the call sites because `IntentCollectionSize.init(min:max:)`
and `IntentCollectionSize.init(exactly:)` are declared with `@_const` /
compile-time-constant parameter requirements. The emitter routed them through
the standard cdecl-wrapper path and passed runtime parameters, which Swift
rejects with `error G51777C7D: expect a compile-time constant literal`.

### Already documented

`08b-entityproperty-init-keypath.md:55` predicted this exact shape:

> Value-generic integer constant-literal init filtering — `IntentCollectionSize.init(min:max:)`
> requires compile-time constants the runtime wrapper cannot supply.

The prediction is now an observation. The 0.12 SDK exposed two specific
init shapes:

- `IntentCollectionSize.init(min: Int, max: Int)`
- `IntentCollectionSize.init(exactly: Int)`

Both must skip cdecl-wrapper emission outright — there is no runtime story
for a constructor whose argument the Swift compiler refuses to read at
call time.

### Approach

Add a filter (probably in `ConstructorHandler` or `MethodHandler` depending
on Apple's `@_const` exposure in ABI JSON) that detects compile-time-constant
parameter requirements and skips emission with a `SkipReason.UnsupportedSignature`
+ `UnsupportedCommentEmitter` tombstone. The exact detection signal needs an
ABI-JSON probe pass — `@_const` may or may not surface in the digester output;
if not, fall back to a per-type/per-init allowlist keyed on
`SwiftTypeName.ModuleQualifiedName + InitDiscriminator`.

Both inits are non-essential — `IntentCollectionSize` is a sizing hint
struct; consumers can use the default `IntentCollectionSize()` initializer
or skip the type entirely. The filter just needs to be precise enough to
exclude the const-literal inits without dropping the type.

### BindingTests coverage

Add a fixture in `BindingTests/Sources/SwiftBindingsTestLib/Constructors/`:

```swift
public struct ConstLiteralInit {
    public init(@_const min: Int, @_const max: Int) { ... }
    public init(plainRuntime: Int) { ... }
}
```

Assert via reflection that the `(min, max)` init is absent and the
`plainRuntime` init is present. Mirrors the round-3 fixture strategy from
session 77f19a80 — reflection-only assertion, no runtime construction
through wrapper-less paths.

### Risks

- **`@_const` not in ABI JSON** — likely. The `swift-api-digester` output for
  AppIntents would need a direct check before committing to detection
  strategy. If `@_const` is stripped, the fallback is per-init allowlist
  (small set, low maintenance, but brittle as Apple adds more such inits).
- **Filter over-fires on plain `Int` inits** — only a risk if detection
  is signal-based (e.g. "any init taking only `Int` and returning a struct
  with `@_const` on the type" — too broad). Per-init allowlist or per-param
  `@_const` detection both avoid this.

---

## Item B — Three umbrella AssistantSchemas types cascade on macOS/Catalyst

### Evidence

`obj/Debug/net10.0-macos26.2/swift-binding/AppIntents.cs:796`:

```text
error CS0535: 'EntitySchema' does not implement interface member 'IBooksEntity.Audiobook'
error CS0535: 'EntitySchema' does not implement interface member 'IBooksEntity.Book'
error CS0535: 'EntitySchema' does not implement interface member 'IBooksEntity.Settings'
error CS0535: 'EntitySchema' does not implement interface member 'IBooksEnum.Font'
...
```

And from `AppIntents.cs:160` / `:292` / `:424` (truncated, three sibling sites):

```text
error CS0246: The type or namespace name 'IEnum' could not be found
error CS0246: The type or namespace name 'IModel' could not be found
error CS0246: The type or namespace name 'IReaderEnum' could not be found
error CS0246: The type or namespace name 'IBrowserEnum' could not be found
error CS0246: The type or namespace name 'IPhotosEnum' could not be found
...
```

The shape is three sibling umbrella types from AppIntents' AssistantSchemas
namespace — `EnumSchema`, `IntentSchema`, `EntitySchema` — each declared
to implement a flat list of domain-organized protocols
(`IBooksEnum`, `IBooksIntent`, `IBooksEntity`, `IPhotosEnum`,
`IPhotosIntent`, `IPhotosEntity`, `IReaderEnum`, `IReaderIntent`,
`IReaderEntity`, `IBrowserEnum`, `ICameraEnum`, `IWhiteboardEnum`,
`IJournalIntent`, `IMailIntent`, `IPresentationIntent`,
`IVisualIntelligenceIntent`, `IWordProcessorIntent`, `ISpreadsheetIntent`,
etc.).

The base umbrella interface (`IEnum`, `IIntent`, `IEntity`, `IModel`) and
the per-domain interfaces (`IBooksEnum`, `IPhotosIntent`, …) are missing
from `AppIntents.cs` on macOS/Catalyst — but the umbrella types' implementation
list still references them. Each missing interface drives one `CS0246` plus
one or more `CS0535` per nested member the umbrella was supposed to project.

### Hypothesis (needs verification)

The Apple swiftinterface gates AssistantSchemas behind `@available(iOS 18.0, macOS …, *)`.
Either:

1. The availability declarations on macOS/Catalyst exclude these protocols on
   the macOS-26.2 SDK we're regen-ing against (unlikely — both SDKs are 26.2),
   **or**
2. The parser parses the protocols but `MemberEmissionValidator` /
   `MemberValidationPipeline` drops them per-platform, and the umbrella types
   `EnumSchema` / `IntentSchema` / `EntitySchema` are NOT dropped in lockstep —
   so the umbrella's implementation list still references them.

(2) is more likely given that the umbrella types are not gated themselves but
their interface list references types that are. The emitter probably needs
to filter the interface list to only include interfaces that are actually
being emitted for the current platform — a categorical check parallel to the
availability-propagation work that landed in session 8.

### Verification pass needed before committing to a fix

1. `grep IBooksEnum /path/to/AppIntents.swiftinterface` for both iOS and macOS
   SDKs — confirm the protocol is present on iOS, present-but-availability-gated
   on macOS, or absent on macOS.
2. `grep EnumSchema /path/to/AppIntents.swiftinterface` for the umbrella types
   on macOS — confirm whether they're declared with a stripped/filtered
   conformance list or a complete one.
3. Inspect `AppIntents.cs:796` (`EntitySchema` declaration) and its corresponding
   block — verify the interface list is verbatim from swiftinterface or
   constructed by the emitter from `TypeConformance` data.
4. If swiftinterface-driven, fix is at the parser layer
   (`SwiftInterfaceParser` / `TypeDecl.Conformances` population). If
   emitter-driven, fix is in whichever handler builds `EnumSchema` /
   `IntentSchema` / `EntitySchema` — likely `FrozenStructHandler` or
   `ClassHandler` depending on Swift kind.

### Approach (provisional, depends on verification)

Filter the implementation list at emission time to exclude conformances
whose target protocol is dropped from the current platform's emission.
Reuse `MemberEmissionValidator` or extend it with a `IsProtocolEmitted`
query. Symmetric to availability propagation — if `IBooksEnum` is gated
behind iOS-only availability and we're emitting for macOS, drop it from
`EntitySchema`'s `:` list.

The 454 / 456 error counts will then collapse to whatever the umbrella
types actually project on macOS after the drop. May produce empty bodies
on three umbrella types — that's the correct outcome.

### BindingTests coverage

Add a Swift fixture with the umbrella-of-domain-protocols shape and gate
some of the domains with `@available(iOS …, *)`:

```swift
public protocol IPlatformShared { }
@available(iOS 99.0, *)
public protocol IIosOnly { var token: Int { get } }

public struct PlatformUmbrella: IPlatformShared, IIosOnly {
    public let token: Int = 0
}
```

Assert via reflection on a non-iOS target framework that `PlatformUmbrella`
exists and `IPlatformShared` is on its implementation list, but `IIosOnly`
is absent.

### Risks

- **Wrong hypothesis** — if (1) is the true cause, the fix moves to
  parser-layer availability handling, not emitter-layer filtering. The
  verification pass above must run first; do not commit to an approach
  until the swiftinterface / ABI JSON has been inspected.
- **Conditional implementation lists in Swift** — Swift allows
  `extension EnumSchema: IBooksEnum where ...`. If AssistantSchemas uses
  this pattern, the fix needs to handle both the direct-conformance and
  the constrained-extension paths.
- **Collateral skipping** — dropping the conformance from the list silently
  may break consumers who downcast to `IBooksEnum` (if any consumer
  bindings exist). Mitigation: tombstone the dropped conformance with
  `UnsupportedCommentEmitter` so the absence is visible.

---

## Sequencing

Item A first. It's smaller (12 errors, one type, one filter), it's already
predicted in 08b, and it unblocks iOS / tvOS C# emission — which lets us
verify Item B's reach (the macOS-only CS0535 cascade may or may not have
counterparts that would surface on iOS post-Item-A).

Item B second. Verification pass before fix; commit only after the
swiftinterface inspection confirms the layer.

Both items ship with BindingTests fixtures. `nuke binding-tests --compile-only`
is the natural gate during iteration; `--macos` and `--catalyst` are the
end-of-session gates for Item B.

## References

- `08b-entityproperty-init-keypath.md:55` — predicted shape of Item A.
- `08-appintents-productionization.md` — session 8 availability propagation
  categorical audit (Item B's likely cousin).
- `/tmp/appintents-regen-verify.txt` — verbatim regen log (preserved for
  reference; do not rely on `/tmp` persistence across sessions).
- `/Users/wojo/Dev/swift-dotnet-packages/apple-frameworks/AppIntents/obj/Debug/net10.0-{macos,maccatalyst,ios,tvos}26.2/swift-binding/`
  — current generated output, reproducible via the regen pipeline.

---

## Outcome (2026-05-22)

Both items landed.

**Item A** lands as the `_const` / `IsConstLiteral` parameter-filter changes
across `ArgumentDecl`, the `InterfaceFacts` pipeline (regex producer +
`SwiftInterfaceAccessParser`), and the two cdecl-wrapper emitters
(`ConstructorWrapperEmitter`, `MethodWrapperEmitter`). 12 wrapper-compile
errors at iOS/tvOS → 0. (The earlier session summary's claim that Item A
shipped as `77f19a80` was a mis-attribution — that commit covers the
dependent-member + variadic-pack regressions only; the const-literal
filter is part of this session's diff.)

**Item B** root cause was **not** the availability-propagation hypothesis. The
real shape was twofold:

1. **Protocol-extension defaults flattened into protocol child lists.** The
   ABI digester surfaces `@_alwaysEmitIntoClient` extension defaults
   (e.g. `extension BooksEnum { var contentType: some Enum { ... } }`) as
   children of the parent protocol with `isFromExtension=true,
   protocolReq=false`. The emitter treated them as abstract requirements,
   producing the ~440 CS0535 cascade across umbrella structs. Fixed by:
   - Adding `PropertyDecl.IsFromExtension` mirroring
     `MethodDecl.IsExtensionMethod`.
   - Filtering at `SwiftABIParser.CreateProtocolDecl` so any child with
     `IsFromExtension && !IsProtocolRequirement` is dropped from the
     protocol's abstract contract. BindingTests fixture:
     `BindingTests/Sources/SwiftBindingsTestLib/Protocols/MarkerProtocolUmbrella.swift`.

2. **Nested-protocol interface references emitted unqualified.** The
   `AssistantSchemas` parent of `BooksEnum/CameraEnum/...` emits as a C#
   namespace facade (`namespace AssistantSchemas { interface IBooksEnum }`)
   while the sibling singular umbrella `AssistantSchema` emits as a class.
   Conformance lists from `class AssistantSchema.EnumSchema : IBooksEnum, ...`
   referenced the bare leaf name, which doesn't resolve from class scope.
   Fixed by promoting the existing `GetConformanceProtocolNames` nested-path
   qualification into a shared `QualifyNestedProtocolInterface` helper in
   `TypeHandlerHelpers.cs` and applying it at all five conformance-emission
   sites.

3. **Composition existentials over suppressed protocols.** With the cascade
   gone, two CS0246 errors surfaced from
   `I_AppShortcutsContentMarkerAnd_LimitedAvailabilityAppShortcutsContentMarker`
   referencing underscore-prefixed marker protocols that have no emitted C#
   interface. Fixed by gating multi-protocol composition emission in
   `ExistentialHandler.GetPublicExistentialType`, collapsing to `object`
   when any participant is missing. The gate uses a new
   `EffectiveProtocolsHaveTypeRecords` helper (mirrors `GetEffectiveProtocols`)
   rather than `AllProtocolsHaveTypeRecords` (which uses `GetNonMarkerProtocols`):
   the existential-emission path filters ObjC participants from the produced
   composition name, so the gate must filter them too to avoid over-broadly
   collapsing a mixed `ObjCProtocol & SwiftProtocol` shape to `object`. The
   broader-scope `AllProtocolsHaveTypeRecords` predicate is preserved
   unchanged for its other 15 callsites (witness dispatch, enum
   construction, P/Invoke marshalling), which have a different semantic
   contract.

### Post-review fixes (codex-review pass)

Two real findings from `/codex-review` were applied before reviews closed:

- **Cross-module nested protocol mis-qualification.** The first cut of
  `QualifyNestedProtocolInterface` naively prepended the parent path to the
  entire interface name. When `NameProvider.GetInterfaceName` had already
  returned a module-qualified name (e.g. `OtherModule.IFoo`), this produced
  `Parent.OtherModule.IFoo` instead of `OtherModule.Parent.IFoo`. Fixed by
  splitting on the last `.` and inserting the parent prefix BETWEEN the
  namespace prefix and the leaf interface. Direct unit tests cover
  same-module, cross-module top-level, cross-module nested, and deep
  same-module nested shapes.
- **Composition gate over-broad on ObjC participants** (see above —
  `EffectiveProtocolsHaveTypeRecords` carve-out).

A third finding — that the parser-layer flattening filter currently only
covers properties and methods, not subscripts — is real but not exercised
by the AppIntents 0.12.0 surface (no subscript regressions). Logged here
as a forward-looking risk; will land alongside the next protocol-extension
flattening case that reproduces it.

After all three fixes, AppIntents macOS C# compile errors collapsed from
454 → 1.

### Residual — closed

The previously-tracked residual error:

```text
AppIntents.cs(9365): error CS0029: Cannot implicitly convert type 'nint' to 'Swift.PartialKeyPath<TEntity>'
```

on `EntityQuerySort<TEntity>.By_Get()` is **fixed**. Root cause:
`KeyPathProjection.ContainerTypeName` inherited the default
`PInvokeType` (`"IntPtr"`), while every other container projection
(`ArrayProjection`, `DictionaryProjection`, `OptionalProjection`,
`SetProjection`, `ResultProjection`) overrides it to the public wrapper
type. The `WrapperEmitter.Return` BoundGenericClassReturn branch emits
`SwiftMarshal.MarshalFromSwift<{ContainerTypeName}>(result)` — so KeyPath
properties on a generic host received `MarshalFromSwift<IntPtr>` instead
of `MarshalFromSwift<Swift.PartialKeyPath<TEntity>>`.

Fix: one-line override in
`src/Swift.Bindings/src/Marshaler/Projection/KeyPathProjection.cs` —
`public string ContainerTypeName => _publicType;`. BindingTests fixture
in `KeyPath/KeyPathGenericReturn.swift` exercises both a struct host
(`KeyPathGenericSort<TElement>`) and a class host
(`KeyPathGenericContainer<TElement>`) with `PartialKeyPath<TElement>`-
typed instance accessors; the compile-only gate now passes. Runtime
smoke at `RuntimeTestsApp/KeyPath/KeyPathGenericReturnTests.cs`.

### Residual carry-out — generic-host constructor wrapper gap

The same fixture surfaced a **separate** runtime bug on the
*construction* path: `KeyPathGenericSort<T>(by:)` and
`KeyPathGenericContainer<T>(by:)` are emitted as direct-`CallConvSwift`
P/Invokes with the `[Obsolete(SB0001)]` "no @_cdecl wrapper available"
warning, and exercising them produces a SIGSEGV on a subsequent
`@_cdecl`-wrapped factory call (cache/heap corruption from the SB0001
init's disposal phase). The session-12 close-out trimmed the runtime
tests to the one passing shape; the broken shapes are tracked as
**Session 13** at
`src/docs/keypath-subsystem/13-sb0001-generic-host-wrapper-gap.md`,
which also covers the 9 AppIntents 0.12 production sites with the same
SB0001 emission pattern (`EntityURLRepresentation<TEntity>`,
`IntentURLRepresentation<TIntent>`, `IntentParameterSummary`, etc.).
