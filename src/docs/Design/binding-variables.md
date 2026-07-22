# Binding Variables & Properties

As-built map of how Swift `var`/`let` members become C# surface. This is not a Swift language primer: only facts that shape binding decisions are kept.

## Scope

| Swift surface | Bound today? | Notes |
|---|---|---|
| Instance / static properties on classes, structs, enums | Yes | Via type handlers → `PropertyHandler` |
| Protocol property requirements | Yes | Interface + proxy/witness path (`ProtocolHandler` / `ProtocolProxyEmitter`) |
| Module-level globals (`public var` / `let` at file scope) | **No** | Parsed onto `ModuleDecl.Properties` but not emitted (see below) |
| Subscripts | Separate | `SubscriptHandler` (same accessor-method idea, different public shape) |
| `willSet` / `didSet` as C# events | **No** | Open decision; declined for now (see below) |
| `@objc dynamic` KVO | Partial | Separate path: `KvoExtensionEmitter` for primitive stored props on NSObject-rooted classes |

---

## Parse model

ABI JSON `Var` nodes become `PropertyDecl` (`SwiftABIParser.CreatePropertyDecl`):

- `SwiftTypeSpec` — property type
- `IsStatic`, `HasStorage` (`HasStorage` DeclAttribute), `IsOverride` / `IsFinal`
- `IsModuleInternal` / `IsSpiProtected` / actor-isolation flags
- `IsObjCDynamic` — both `ObjC` and `Dynamic` in declAttributes (KVO eligibility only)
- `Accessors` — only **get** and **set** are modeled (`GetAccessorDecl` / `SetAccessorDecl`). ABI `_modify` is ignored. There is **no** parse of `willSet`/`didSet` into accessors (diagnostic enum values `AccessorKind.WillSet` / `DidSet` exist but are unused for emission).

Each accessor is a synthesized `MethodDecl`:

| Accessor | Method name | Signature sketch |
|---|---|---|
| get | `{name}_Get` | returns property type; `Throws` / `IsAsync` from the accessor node |
| set | `{name}_Set` | void + `value` parameter; `Throws`/`IsAsync` fixed false |

Getter async detection uses mangled TBD symbols (`ManglingProbes.IsAsyncAccessor`), not a direct ABI flag. Mutating accessors set `MethodDecl.IsMutating` from DeclAttributes / `funcSelfKind`.

`HasStorage` does **not** change the public C# property path: stored and computed properties both go through accessors. `HasStorage` is used for frozen-struct field layout, KVO eligibility, KeyPath walks, and similar layout/metadata concerns — not for choosing between “field” vs “property” emission on the public API.

---

## Emission entry points

`PropertyHandler` (`Emitter/StringEmitter/Handler/PropertyHandler.cs`) is the single handler for `PropertyDecl`. Call sites:

- `ClassHandler` — all non-synthesized class properties (skips actor `unownedExecutor`)
- `FrozenStructHandler` / `NonFrozenStructHandler` — all properties (frozen structs also emit private backing fields for **instance** stored properties into the layout/Buffer mirror)
- `EnumHandler` / simple-enum extension paths
- Protocol interface / proxy emission (requirements; reverse-dispatch is a separate witness story)

Pre-gates before `PropertyHandler.Emit`:

1. `MemberEmissionValidator.CanEmitProperty` — module-internal/SPI, constrained-extension routing, SwiftUI/Combine, unsupported type shapes
2. `MemberValidationPipeline.ValidatePropertyEmission` — bound-generic / existential / bare-generic property gates
3. Handler-local special cases (AsyncStream, closure-typed properties, existentials)

Skipped members leave an `// Unsupported:` tombstone and a binding-report row; they do not set `PropertyDecl.WasEmitted`.

### Module-level globals (not emitted)

`SwiftABIParser.ParseModule` collects top-level `Var` nodes into `ModuleDecl.Properties`. `ModuleHandler` emits top-level **free functions** into a module wrapper class (`Functions` / `GlobalFunctions` / …) but has **no** loop that marshals `moduleDecl.Properties` through `PropertyHandler`. Globals are only scanned for Apple-framework import inference (`ScanModuleMembersForFrameworkImports`) and for recovery quarantine signature edges.

**As-built gap:** public module-level variables are not part of the generated C# API. Closing that would need a deliberate design (static class home, naming, wrapper strategy) — not implied by current code.

---

## Sync properties: what ships in C#

For a supported non-async property, emission is two layers:

### 1. Private accessor methods

Each get/set accessor is emitted via `MethodHandler` with `MethodDecl.IsAccessor = true` so bodies stay in **raw ABI types** (no idiomatic projection on the method itself). Entry points are chosen per accessor, in order:

1. **Native ARM64 thunk** (`NativeThunkEmitter`) when eligible
2. Else **`@_cdecl` property wrapper** (`PropertyWrapperEmitter`) when `WrapperValidation.DeterminePropertyWrapperDecision` says wrapper-required — symbols like `SBW_Get_{Module}_{Type}_{property}` / `SBW_Set_…`
3. Else **ObjC override wrapper** (`ObjCOverridePropertyWrapperEmitter`) for certain ObjC-override cases
4. Else **direct** P/Invoke to the accessor’s silgen mangled name (`CallConvSwift` / cdecl as the method path decides)

Promoted wrapper/thunk symbols live on the emission env (`MethodEnvironment.PromoteSymbol`); `MethodDecl.MangledName` stays the immutable silgen fact.

`PropertyWrapperEmitter` rejects (among others): async accessors, **throwing getters** (SWIFTBIND107 — no try/catch in the property `@_cdecl` path), module-internal/SPI, metatype properties, `self`, some closure setters, unsupported generic containers. Rejection does not always skip the property: emission may fall through to direct CallConvSwift (runtime-risk for non-blittable shapes; still emitted so protocol conformance is not broken by over-suppression).

### 2. Public C# property

```csharp
public [static] [virtual|override|sealed override] TProjected Name
{
    get => /* convert from Name_Get() */;
    set => /* convert into Name_Set(...) */;
}
```

- **Name:** `NameProvider.GetPropertyName` + type-level rename channels (`PropertyRenames`, `EnumPropertyRenames`). CS0542 renames (property vs nested type) get explicit interface forwarders when needed.
- **Projected type:** `TypeProjectionFactory` / existential / closure handlers — idiomatic surface (`string`, `IReadOnlyList<T>`, `T?`, protocol interfaces, …). Accessors remain raw; conversion lives in the get/set bodies (`EmitGetter` / `EmitSetter` + `AccessorConversionVisitors`).
- **nint narrowing:** public properties may narrow `nint`/`nuint` → `int`/`uint`; accessor methods keep native width with casts at the property edge.
- **Static:** `static` modifier when `PropertyDecl.IsStatic`.
- **Class dispatch:** instance properties on non-final classes emit `virtual` unless final; overrides use `override` / `sealed override` only when a resolved ancestor actually emitted the property (`WrapperEmitter.HasPropertyInResolvedAncestors`).
- **Setter OS floor:** tighter setter availability from ABI becomes accessor-level `[SupportedOSPlatform]` via `AvailabilityAttributeEmitter.EmitSetterAccessorAvailability`.

Special type families inside the same handler (not separate product APIs):

| Family | Public shape |
|---|---|
| Existential / optional existential | Interface / `object` / get-only `ExistentialUnion` where allowed; settable existentials stay non-union so assignment can marshal |
| Closure-typed property | C# delegate type; may be setter-only if the getter cannot be invoked from C#; **async** closure-typed properties are skipped |
| Bound generics / collections | Projected containers with conversion + disposal in get/set |
| `AsyncStream` / `AsyncThrowingStream` | `IAsyncEnumerable<T>` via `AsyncStreamEmitter` (separate early path) |

---

## Async property getters

C# properties cannot be `async`. When any accessor’s `Method.IsAsync` is true, `PropertyHandler.EmitAsyncPropertyAsMethods` runs instead of the sync property path:

- Only **async getters** are transformed; non-getter async accessors are skipped with a warning
- The accessor is reshaped into a public method: name `get{Property}` → C# `Get{Property}…Async(CancellationToken = default)`, `IsAccessor = false`, `AsyncPropertyName` set for the Swift wrapper call expression
- Emission reuses the normal async **method** pipeline (`MethodHandler` → `WrapperEmitter.Async`)
- Generic parents that would need `[UnmanagedCallersOnly]` inside a generic type are skipped (`SkipReason.GenericTypeCallback`)

Runtime coverage: `BindingTests/RuntimeTestsApp/Async/AsyncPropertyTests.cs` (`GetAsyncLabelAsync`, etc.).

`SkipReason.AsyncProperty` remains in reporting metadata for historical/attribution rows; the live happy path **emits methods**, it does not skip async getters wholesale.

---

## Throwing accessors

- Getters may be throwing (`MethodDecl.Throws` from the ABI accessor node).
- `@_cdecl` property wrappers do **not** emit try/catch for throwing getters (`PropertyWrapperEmitter` reject `SWIFTBIND107`); those accessors do not take the cdecl property-wrapper strategy.
- Setters are parsed as non-throwing (`CreateSetAccessor` forces `Throws = false`).

There is no separate C# “throwing property” surface; failures that reach a throwing getter go through whatever method/async error path the chosen strategy uses (same as methods when on the method pipeline).

---

## Protocol and reverse-dispatch note

Protocol property requirements participate in interface emission and, for reverse dispatch, in `VtableLayout` / witness fill paths. That is **not** a second property binder: fillability still keys off the same `PropertyDecl` + accessors, with protocol-specific dispatch eligibility in `WitnessDispatchEmitter` / `ProtocolProxyEmitter`. See reverse-dispatch design docs for lifetime and vtable rules.

---

## OPEN (declined for now): `willSet` / `didSet` on the C# side

**Decision status:** open / not planned.

**As-built behavior:**

- Swift property observers are **not** parsed or emitted.
- Calling a generated C# setter invokes the Swift setter (via wrapper or direct P/Invoke). Any Swift-side `willSet`/`didSet` (and superclass chaining) run **inside Swift** as part of that setter. C# does not need to re-implement observer chaining for Swift observers to fire.
- No C# events, partial methods, or virtual hooks are generated for observers.

**Why not generate C# observer surface:** no near-term consumer need; observers are not first-class ABI exports the way get/set are; and mirroring superclass chain order in C# would be fragile without a real override model for bound subclasses. Options considered historically (always emit C# observers, emit only when Swift has them, configurable opt-in) remain **not implemented**.

**Related but different:** `@objc dynamic` KVO on NSObject-rooted classes can get `Observe{Property}` extension methods from `KvoExtensionEmitter` (Foundation KVO, primitive stored properties only). That is KVO, not Swift `willSet`/`didSet`.

Reopen only if a concrete consumer needs C#-side pre/post-set hooks that cannot be expressed by subclassing the Swift type or using KVO where applicable.

---

## Related code

| Concern | Primary locations |
|---|---|
| Parse | `Parser/SwiftABIParser.cs` (`CreatePropertyDecl`, `HandleAccessors`, `CreateGetAccessor` / `CreateSetAccessor`) |
| Model | `Model/TypeDecl/PropertyDecl.cs`, `Model/TypeDecl/AccessorDecl.cs` |
| Emit | `Emitter/StringEmitter/Handler/PropertyHandler.cs` |
| Wrappers | `Emitter/StringEmitter/PropertyWrapperEmitter.cs`, `ObjCOverridePropertyWrapperEmitter.cs`, `NativeThunkEmitter` |
| Gates | `MemberEmissionValidator.CanEmitProperty`, `MemberValidationPipeline.ValidatePropertyEmission` |
| Type handlers | `ClassHandler`, `FrozenStructHandler`, `NonFrozenStructHandler`, `EnumHandler` |
| Module (no globals emit) | `ModuleHandler` (free functions only) |
| Async stream props | `AsyncStreamHandler`, `AsyncStreamEmitter` |
| KVO | `KvoExtensionEmitter` |
