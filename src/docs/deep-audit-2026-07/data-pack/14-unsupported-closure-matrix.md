# Data Pack — UnsupportedClosure Matrix

| Field | Value |
|-------|--------|
| **Date** | 2026-07-16 |
| **Mode** | Read-only inventory (no production edits) |
| **Why** | **#5** skip in validation corpus (**600** / 8.2% of skipped members) |
| **Disposition** | `KnownLimitation` (`SkipDisposition.cs`) |
| **Consumer copy** | *“Write a Swift wrapper that converts to a supported closure shape.”* (`WorkaroundRecommendations.cs`) |
| **Sources** | `ClosureHandler` / `ClosureEmitter` gates; BindingTests report (9 rows) + `UnsupportedClosureShapes.swift`; `roadmap.md` low-priority remainder; Track A4; BindingAudit samples |

---

## 1. Headline

`UnsupportedClosure` is the **honest skip** when a method/property/return carries a closure shape outside the supported marshalling matrix (or outside a bridge exception). It is **not** a silent emit of broken `Action<>`/`Func<>` — Layer1 fail → skip (B20/B21), with documented exceptions that re-admit GCB/MCB/NCB/PECB/defaulted Optional.

**Two-layer model** (constraints.md + Track A4):

| Layer | Oracle | Role |
|-------|--------|------|
| **Layer1** | `ClosureHandler.IsSupportedClosure` (+ param/return helpers) | **Emit-or-skip** — if false and no bridge exception → `SkipReason.UnsupportedClosure` |
| **Layer2** | `ClosureEmitter.IsCdeclCompatibleType` / `IsClosureCdeclCompatible` / `NeedsClosureCdeclWrapper` | **cdecl-adapt vs legacy** — only among Layer1-supported thunks; `.All()` not `.Any()` |

Layer2 ⊆ Layer1 shapes but uses **different** criteria (e.g. complex enum **param** yes Layer1+Layer2; complex enum **return** blocked Layer1). Fail Layer2 → still emit via legacy `SwiftClosureData` / CallConvSwift thick path when Layer1 passed — **not** UnsupportedClosure.

**Implication:** Residual 600 is **product surface** (matrix capacity), not a free reabstraction crash minefield. Roadmap’s “~188” is a **stale undercount** vs live baseline **600** — treat 600 as authoritative until next validate.

---

## 2. Corpus metrics

| Corpus | Count | Notes |
|--------|------:|-------|
| **Validation baseline** | **600** | `build/baselines/validation-baseline.json` → `skip_metrics` |
| **BindingTests** | **9** | Live `binding-report.json` `SkipReasons` |
| Share of validation skips | **8.2%** | Heatmap #5 |
| Roadmap low-priority note | “~188 skips” | **Stale** relative to baseline 600 — same residual theme, wrong magnitude |

### BindingTests Details histogram (all 9 rows)

| Count | Member | Details pattern | Sub-bucket |
|------:|--------|-----------------|------------|
| 6 | `AsyncClosurePropertySetterHolder.*` + nested `IntentConfigurationNested.*` properties | *“Async closure-typed properties cannot be stored via a sync accessor…”* | **Async property store** (permanent-ish without Swift synthesis) |
| 1 | `UnsupportedClosureAsyncVoidReturn.init` | *“Parameter 'handler' has unsupported closure type…”* | **Async → Void** outside baseline non-throwing matrix |
| 1 | `GenericAbiBox.transform` | Parameter unsupported closure | **Generic-in-closure / ABI fixture** |
| 1 | `MixedEmittability.transform` | Parameter `'_using'` unsupported | **Mixed emittability fixture** |

**Tombstone surface (not pure skips):** several shapes in `UnsupportedClosureShapes.swift` (`OptionalExistentialReturn`, `ArrayOfExistentialReturn`, `SendableOptionalExistential`, `AsyncVoidReturn.onChange`) emit as **`ClosureParamTombstone`** (`object?` + SB0005) when eligible — still “unsupported marshalling” but **reachable** dead surface, not always a SkipReasons row.

**Related non-UnsupportedClosure:** `UnsupportedClosureAsyncThrowingParam.runRequest` → **`UnsupportedSignature`** (*“Async-throwing closure parameter cannot be bridged (non-baseline…)”*) — outer-method wrapper gate, not pure Layer1.

---

## 3. Layer1 — supported matrix bounds

Source of truth: `src/Swift.Bindings/src/Marshaler/ClosureHandler.cs` (`IsSupportedClosure`, `IsSupportedClosureParameterType`, `IsSupportedClosureReturnType`, baseline async helpers).

### 3.1 Sync / escaping / `@convention(c)`

| Axis | Supported | Rejected at Layer1 |
|------|-----------|-------------------|
| Escaping / convention(c) | Yes (implicit for public ABI) | Nested `ClosureTypeSpec` **as param element** (unless NCB re-admits whole method) |
| Args | Concrete named types in TypeDB; pointers; simple/complex **enums as params**; existentials if `IsSupportedExistential`; supported tuples; bound generics if `IsSupportedGenericType` | Bare generic params (`τ_0_0` / method `T`); nested closures; unresolved / `AnyType` leaves; many nested ObjC-protocol existentials in containers |
| Returns | Primitives (direct); many bound generics via **indirect** return; supported existentials; pointers | Complex **enum returns**; ObjC-bridged **class returns**; `Optional<any P>` / `[any P]` (existential as generic param of return); memory-managed inners inside Optional except String carve-out; closure-unsafe tuple elements |
| Optional closures | Always escaping (`IsEffectivelyEscaping`) | Optional\<ObjC-**value**\> args e.g. `(URL?) -> Void` — **roadmap medium**, narrow `IsOptionalReferenceArg` |

### 3.2 Async baseline matrix

| Shape | Gate | Bounds |
|-------|------|--------|
| **Async + throwing** | `IsBaselineAsyncThrowingClosure` | Arity **0–4** (`MaxAsyncThrowingClosureArity`); return = blittable primitive **or** `Swift.String` (full arity) **or** `Foundation.Data` (**zero-arg only**); args ∈ Primitive / SwiftString / SwiftClass only |
| **Async non-throwing** | `IsBaselineAsyncNonThrowingClosure` | Same arity; return = **non-void** blittable primitive only — **no** Void, **no** String/Data non-throwing |
| Outside baseline | `IsSupportedClosure` → **false** | e.g. `async -> Void`, arg-bearing Data, non-primitive returns, Optionals/generics in async args |

Emitter: `ClosureEmitter.Async` + runtime `AsyncThrowingClosureState` / `AsyncClosureHelper` per-arity overloads.

### 3.3 Bridge exceptions (Layer1 fail but method still emits)

From `MemberEmissionValidator` B20 (must stay ordered / complete):

| Exception | Emitter | Shape |
|-----------|---------|-------|
| GCB | `GenericClosureBridgeEmitter` | Method-generic noescape identity-style when eligible + non-closure params compatible |
| MCB | `MethodClosureBridge` | Bound-generic / complex enum / `any Error` in args |
| NCB | `NestedClosureBridge` | Nested closure param shapes in eligibility matrix |
| PECB | `ProtocolExtensionEmitter.IsClosureBridgeable` | Protocol-extension methods |
| Defaulted Optional\<Closure\> | `ExistentialBypassEmitter` | Omit defaulted optional closure; Swift fills nil |

If exception eligibility fails after Layer1 false → record `UnsupportedClosure`.

### 3.4 Property-specific gates (`PropertyHandler`)

| Case | Outcome |
|------|---------|
| `!IsSupportedClosure` | Skip `UnsupportedClosure` — “Closure type is not supported.” |
| **Async** closure-typed property | Skip `UnsupportedClosure` — cannot store via sync accessor (Stripe `confirmHandler` shape) |
| Supported but `!CanInvokeFromCSharp` / bad return marshal | **Setter-only** property (not skip) when setter exists |
| Setter-only needed but getter-only | Skip `UnsupportedClosure` |

Setter-only recovery is a **prior reduction** of UnsupportedClosure (roadmap: “already reduced via setter-only closure properties”).

---

## 4. Layer2 — cdecl wrapper gate

Source: `ClosureEmitter.SwiftWrapper.cs` — `IsCdeclCompatibleType`, `IsClosureCdeclCompatible`, `NeedsClosureCdeclWrapper`.

```text
NeedsClosureCdeclWrapper:
  thunkClosures = supported && RequiresThunk && !async
  require Count > 0 AND thunkClosures.All(IsClosureCdeclCompatible)   // .All() not .Any()
```

**Layer2 accepts (examples):** primitives, Bool, Void, pointers, classes, ObjC-bridged classes, simple enums, frozen/non-frozen structs (heap pointer ABI), complex enums **as params**, Optional reference / selected Optional value layouts.

**Layer2 rejects (examples):** `inout` args; well-known protocol wrapping needing MCB; existential **returns** (params OK via heap); throwing + Optional\<value\> returns; String / non-cdecl-complex as direct cdecl without indirect path; nested shapes that fail type walk.

Private **narrower** copies exist on `ProtocolExtensionEmitter` / `ForeignTypeExtensionEmitter` (Track A4 dual-oracle hazard) — PE excludes SimpleEnum/ObjCBridged by policy; do not “fix cdecl once” without all oracles.

---

## 5. Production record sites

| Site | When | Details flavor |
|------|------|----------------|
| `MemberEmissionValidator` B20 | Param closure `!IsSupportedClosure` after bridge exceptions | `Parameter '…' has unsupported closure type that cannot be marshalled.` |
| `MemberEmissionValidator` B21 | Return closure `!CanInvokeReturnedClosure` | Return invoker matrix fail |
| `MemberEmissionValidator.CanEmitProperty` | Property closure unsupported | `Closure type is not supported.` |
| `PropertyHandler` | Async store / unsupported / getter-only setter-only fail | Async-store string or generic unsupported |
| `MethodHandler` / `IMethodBridgeEmitter` | Defaulted optional closure bypass ineligible | “Optional closure… incompatible with bypass…” |
| Tombstone path | Unsupported but class method eligible for `ClosureParamTombstoneEmitter` | Emit `object?` + SB0005 **instead of** pure drop (IHandler tombstone arm) |

Disposition classifier: **KnownLimitation**.

---

## 6. Remaining shapes vs fixed

### Already fixed / reduced (do not re-open as greenfield)

| Area | Status |
|------|--------|
| Baseline async **throwing** 0–4 args, primitive / String / zero-arg Data returns | **Shipped** |
| Baseline async **non-throwing** 0–4 args, non-void primitive returns | **Shipped** |
| Setter-only recovery for non-invocable sync closure properties | **Shipped** |
| NestedClosureBridge eligibility set | **Partial** — expands NCB, residual nested still skip |
| GenericClosureBridge / MethodClosureBridge | **Partial** — eligible islands only |
| Optional-escaping GCHandle / `IsEffectivelyEscaping` | **Held** (ownership trap fixed) |
| Layer1/Layer2 `.All()` invariant | **Held** (Track A4) |

### Still residual (product / capacity)

| Shape | Why skip | Fixability | Consumer signal |
|-------|----------|------------|-----------------|
| Generic closure params outside GCB (`(T?) -> Void` in `on<T>`) | Layer1 bare generic | **High value** if GCB generalized | Mappedin, RealityKit subscribe, Alamofire generics |
| Nested closures outside NCB | Nested `ClosureTypeSpec` | Medium | SnapKit / factory patterns |
| Async outside baseline (Void return; arg-bearing Data; non-primitive; Optional args) | Baseline matrix | Medium — per-shape sessions | `UnsupportedClosureShapes` + real kits |
| Async **property** store (PaymentSheet confirmHandler) | No Swift synthesis from (fp, ctx) | **Hard / architectural** | Stripe **high** |
| Existential returns in closures (`Optional<any P>`, `[any P]`) | Layer1 return recursion | Medium | Fixtures pinned; AppIntents-heavy |
| ObjC class as **closure return** | No `new Type(SwiftHandle)` | Medium | PassKit-style completions OK as **params** |
| Complex enum as **closure return** | Indirect/return conversion gap | Medium | |
| `(URL?) -> Void` Optional ObjC **value** | Narrow slot ABI | Medium (roadmap medium) | Needs dedicated thunk |
| Transform `(T) -> T` open-generic | Generic param | Medium | Kingfisher modifiers |
| `withUnsafeBytes` rethrows → `R` | Generic + pointer body | Low-med | CryptoKit ×13 |

Roadmap low: *“Remaining are generic params, nested closures, and async-closure shapes outside the supported arg/return matrix (e.g., arg-bearing Data returns, non-throwing Data returns).”* — **correct theme**, count should cite **600** not ~188.

### BindingTests fixture catalog (`UnsupportedClosureShapes.swift`)

Pinned ratchet targets (skip-surface / tombstone):

1. Optional existential return  
2. Array-of-existential return  
3. Sendable + optional existential  
4. Async void return  
5. (+ async-throwing param — often `UnsupportedSignature` on outer method)

---

## 7. Real-library heat (qualitative)

| Library | ~UC | Dominant residual |
|---------|----:|-------------------|
| AppIntents | 364 | Authoring-time callbacks / IntentFile — many architectural |
| RxSwift / Alamofire | high | Operator / response closures (generics + nested) |
| Stripe PaymentSheet | critical | **Async property** confirm handlers |
| CryptoKit | 13 | `withUnsafeBytes` generic body |
| Kingfisher | 12 | `(T)->T` transforms |
| RealityFoundation | 8 | `Scene.subscribe` generic-over-protocol |
| Mappedin | 8 | Generic `on/off` — highest-value third-party gap in audit |

---

## 8. Fixable vs by-design (summary)

| Class | Verdict |
|-------|---------|
| Matrix expansion (async Data+args, existential returns, more NCB/GCB cells) | **Fixable capacity** — per-shape sessions + BindingTests ratchet |
| Generic event subscribe / factory closures | **Fixable high value** — product priority |
| Async **property** storage | **Hard** — not a small gate flip; needs Swift-side store synthesis or API redesign |
| Optional ObjC value in closure arg | **Fixable** with dedicated thunk; wrong widen = crash |
| Layer1 reject of nested/generic as default | **By-design honesty** until bridges cover |
| Layer2 non-cdecl fallback for supported shapes | **By design** — not UnsupportedClosure |

**Worker takeaway:** Treat UnsupportedClosure as a **matrix spreadsheet**, not one bug. Expand cells with red fixtures first; never widen Layer2 without Layer1; never treat async property skip as a false positive.

---

## 9. Key absolute paths

- `src/Swift.Bindings/src/Marshaler/ClosureHandler.cs` — Layer1 SSOT + baseline async  
- `src/Swift.Bindings/src/Emitter/StringEmitter/ClosureEmitter.SwiftWrapper.cs` — Layer2 cdecl  
- `src/Swift.Bindings/src/Emitter/StringEmitter/MemberEmissionValidator.cs` — B20/B21 + bridge exceptions  
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/PropertyHandler.cs` — async property + setter-only  
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/{NestedClosureBridge,MethodClosureBridge}.cs` + `GenericClosureBridgeEmitter`  
- `BindingTests/Sources/SwiftBindingsTestLib/Closures/UnsupportedClosureShapes.swift`  
- `src/docs/roadmap.md` — UnsupportedClosure remaining shapes  
- `src/docs/deep-audit-2026-07/tracks/Track-A4_Closures-Reabstraction.md`  
- `build/baselines/validation-baseline.json` — **600**
