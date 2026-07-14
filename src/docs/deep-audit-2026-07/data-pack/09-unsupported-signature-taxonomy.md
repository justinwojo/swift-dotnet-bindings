# Data Pack — UnsupportedSignature Taxonomy

| Field | Value |
|-------|--------|
| **Date** | 2026-07-16 |
| **Mode** | Read-only inventory (no production edits) |
| **Why** | `#1` skip in validation corpus (**1420** / 19.3% of skipped members) |
| **Disposition** | `KnownLimitation` (`SkipDisposition.cs`) |
| **Consumer copy** | *“parameter or return type not yet supported”* / *“Write a Swift wrapper with a simplified signature.”* (`WorkaroundRecommendations.cs`) |
| **Sources** | Production `SkipReason.UnsupportedSignature` assign sites; BindingTests `output/binding-report.json` (37 rows); BindingAudit library notes; `validation-baseline.json` count only (no Details histogram) |

---

## 1. Headline

`UnsupportedSignature` is a **catch-all emission/skip bucket**, not a single root cause. It collapses:

- **Language-level impossibilities** (C# has no generic ctors; no parameter packs; no PAT projection)
- **Honest marshalling gaps** (tuple element conversion, async without `@_cdecl`, Result write-in)
- **Placeholder / AnyType residual** when a more specific reason was not chosen
- **Narrow path guards** (simple-enum extension type whitelist, subscript indexer body)

**Implication for G1 / roadmap:** Do not treat “fix UnsupportedSignature” as one epic. Split into the sub-buckets below; many rows are permanent or correctly excluded after CSM recovery.

---

## 2. Corpus metrics

| Corpus | Count | Notes |
|--------|------:|-------|
| **Validation baseline** | **1420** | `build/baselines/validation-baseline.json` → `skip_metrics`; no per-Details breakdown in-tree |
| **BindingTests** | **37** | Live `BindingTests/output/binding-report.json` (see §4) |
| Share of validation skips | **19.3%** | Largest single reason (`04-validation-corpus-skip-heatmap.md`) |

### BindingTests Details histogram (all 37 rows)

| Count | Details pattern (prefix / exact) | Sub-bucket |
|------:|----------------------------------|------------|
| 9 | `Method signature contains unsupported placeholder type.` | **Placeholder residual** |
| 5 | `Property type contains unresolvable associated type reference.` | **PAT / associated type** |
| 4 | `Method signature contains unresolvable associated type reference.` | **PAT / associated type** |
| 4 | `C# does not support generic constructors with method-own type parameters.` | **Generic ctor (method-own)** |
| 3 | `Async method without @_cdecl wrapper — direct CallConvSwift on Swift async ABI is unsafe.` | **Async ABI-unsafe** |
| 3 | `Constructor has a @convention(c) closure parameter alongside a non-optional closure…` | **Ctor @convention(c) mix** |
| 1 | `Variadic generic parameter pack 'each R' has no C# equivalent.` | **Parameter pack (type)** |
| 1 | `Constructor signature contains unsupported placeholder type.` | **Placeholder residual** |
| 1 | `Operator on generic type requires buffer marshalling.` | **Operator / generic buffer** |
| 1 | `Constructor has a variadic parameter (T...) that cannot be wrapped…` | **Value variadic ctor** |
| 1 | `Tuple parameter 'maxSize' has elements whose P/Invoke type differs…` | **Tuple param marshalling** |
| 1 | `Async-throwing closure parameter cannot be bridged…` | **Async-throwing closure** |
| 1 | `Accessor 'value_Set' has a Result-typed parameter…` | **Result write-in** |
| 1 | `Subscript index parameter requires conversion not supported in indexer body.` | **Subscript index** |
| 1 | `Subscript accessor would trigger Swift wrapper with incompatible call syntax.` | **Subscript wrapper** |

BindingTests is **fixture-shaped** (intentional hard cases). Real libraries skew toward **generic ctors + placeholder + PAT** (CryptoKit 99/99 generic ctors; AppIntents 217 mixed; MusicKit 29 mixed).

### BindingTests member samples (by pattern)

| Pattern | Examples (`ContainingType.Name`) |
|---------|----------------------------------|
| Placeholder method | module free funcs `typeName` / `createInstance` / `getType`; `SBSW_GenericAbi_*` harness helpers |
| PAT method | `StateMachine.snapshot`, `AsyncBagItem.makeResponse`, `Container.element`, `TaggedAssociator.process` |
| PAT property | `LabelledContainer.label`, `AttributeKind.value`, … |
| Generic ctor | `ProcessedItem.init` + 3 other method-own-generic inits |
| Async no cdecl | 3 async methods (unsafe direct CallConvSwift) |
| @convention(c) ctor | 3 constructors with mixed closure shapes |
| Pack type | type with `each R` |
| Tuple param | member with `maxSize` tuple |
| Result setter | property whose setter carries `Swift.Result` |
| Subscript | complex index + wrapper-incompatible accessor |

---

## 3. Production record sites (complete inventory)

Every production assign of `SkipReason.UnsupportedSignature` (excluding tests). Grouped by layer.

### 3.1 `MemberValidationPipeline` — pre-Marshal emission gate

File: `src/Swift.Bindings/src/Emitter/StringEmitter/MemberValidationPipeline.cs`

| Gate (doc comment numbering) | Trigger | Details string | Fixability |
|------------------------------|---------|----------------|------------|
| **4b** (after Gate 1) | Method has `each …` / `repeat each …` generic param | `Method declares a variadic generic parameter pack…` | **Permanent** — C# has no packs |
| Constrained-extension method (between Gate 4–5) | Same-type constrained extension method **out of** `IsEmittableConstrainedExtensionMethod` scope | `Constrained-extension method '…' … out of scope for ConstrainedExtensionEmitter (initial scope: zero-argument sync non-throwing…)` | **Fixable (capacity)** — expand CE emitter scope |
| **Gate 5** bare generic | `HasBareGenericUsage` on any CSSignature slot (non-accessor) | `Type '…' contains generic declaration used without type arguments.` | **Mostly permanent** / sometimes TypeDB completeness |
| **Gate 5b** tuple params | `HasUnmarshalledTupleElements` && !`IsCdeclBufferMarshallableTuple` | `Tuple parameter '…' has elements whose P/Invoke type differs…` | **Fixable** — per-element tuple marshalling |
| **Gate 6** generic ctor | Constructor `IsGeneric` with method-own params not on parent | `C# does not support generic constructors with method-own type parameters.` | **Permanent** (C# language); CSM often recovers concrete specializations |
| Property bare generic | Property `HasBareGenericUsage` | same bare-generic Details form | same as method bare generic |

Pipeline **does not** own placeholder / PAT / async-unsafe — those enter via `ShouldSkipMethodEmission` (validator) or later handlers. Gate 2 **delegates** to `MemberEmissionValidator.ShouldSkipMethodEmission`, which can return `UnsupportedSignature` for C6 async non-simple enum tuples, placeholder signatures, SWIFTBIND104 buffer shapes, etc.

### 3.2 `MemberGateEvaluator` — protocol interface + concrete hard gates

File: `src/Swift.Bindings/src/Emitter/StringEmitter/MemberGateEvaluator.cs`

| API | Trigger | Details |
|-----|---------|---------|
| `EvaluateProperty` / `EvaluatePropertyHardGates` | `ContainsAssociatedTypeReference` | `Property type contains unresolvable associated type reference.` |
| same | `HasBareGenericUsage` | `Property type uses generic type without type arguments.` |
| `EvaluateMethod` | PAT in any CSSignature arg | `Method signature contains unresolvable associated type reference.` |
| same | bare generic in method signature | `Method signature uses generic type without type arguments.` |
| `EvaluateSubscript` | PAT on return or index | `Subscript type contains unresolvable associated type reference.` |
| same | bare generic return/index | `Subscript type uses generic type without type arguments.` |
| `EvaluateHardGates` (concrete) | PAT | same method PAT string |
| same | bare generic per-arg | `Type '…' contains generic declaration used without type arguments.` |
| same | `WrapperValidation.HasRawGenericTypeParams` | `Method signature contains raw generic type parameters (τ_0_0)…` |

**Note:** Protocol path uses this evaluator because protocol emission **bypasses** `MemberValidationPipeline` for many members (CS0535 lockstep with concrete skips).

### 3.3 `MemberEmissionValidator` — property/method deep checks

File: `src/Swift.Bindings/src/Emitter/StringEmitter/MemberEmissionValidator.cs`

| Check | Details | Fixability |
|-------|---------|------------|
| Unsupported tuple element (closure / AnyType inside tuple) | `Type contains tuple with unsupported element (closure or AnyType).` | Mixed — AnyType vs closure-in-tuple |
| Bare generic property type | `Type '…' contains generic declaration used without type arguments.` | Mostly permanent |
| Projected bare generic type name | `Property type resolved to bare generic type (…)` | TypeDB / projection |
| Accessor wrapper signature `ContainsPlaceholder` | `Accessor '…' has unsupported signature.` | Often placeholder residual |
| Async method tuple return with **non-simple enum** element (C6) | `Async tuple return contains non-simple enum '…' which is non-blittable in callback.` | Fixable with better async callback marshalling |
| Method wrapper signature `ContainsPlaceholder` | `Method signature contains unsupported placeholder type.` | **Dominant residual** — see §5 |
| Projected return bare generic | `Method return type resolved to bare generic type (…)` | TypeDB |
| `UnsafeRawBufferPointer` **return** | `SWIFTBIND104: '…' is not supported as a return type. v1 supports synchronous, nonescaping parameters only.` | Fixable (v2 buffer story) |
| Same buffer type as **async param** | `SWIFTBIND104: '…' is not supported as a parameter on async methods…` | Fixable |
| **inout** raw buffer param | `SWIFTBIND104: 'inout …' is not supported…` | Fixable (split/writeback ABI agreement) |

**Diagnostic collision note:** Details embed `SWIFTBIND104` for raw-buffer signature skips, while the diagnostic encyclopedia also documents **SWIFTBIND104** as static-archive `nm` failure. Same code string, different subsystems — do not merge buckets when mining logs.

### 3.4 Handler-level record sites

| File | Details / trigger | Fixability |
|------|-------------------|------------|
| **`MethodHandler.cs`** | Ctor: `@convention(c)` closure + non-optional closure → direct CallConvSwift cannot run allocating-init ABI | Architectural hard; rare |
| | Ctor: value variadic `T...` — Swift rejects `Array` at call site for `@_cdecl` | Methods can CallConvSwift; **ctors** fail closed here |
| | Ctor/method: `ContainsPlaceholder` on wrapper signature | Residual |
| | `HasUnbridgeableAsyncThrowingClosure` | Fixable (expand async-throws closure matrix) |
| | `IsSkippedWrapperDirectPInvoke` (async without `@_cdecl`) | Fixable when wrapper eligibility expands; honesty skip is correct today |
| **`PropertyHandler.cs`** | Setter parameter is `Swift.Result` (write-in unsupported) | Architectural until Result outbound exists |
| | Accessor `ContainsPlaceholder` | Residual |
| **`OperatorHandler.cs`** | Placeholder in operator signature | Residual |
| | Generic type parameter operand — C# operators cannot be generic | **Permanent** |
| | Generic-type operator needs buffer marshalling preamble not in operator scope | Fixable (emit buffer preamble) |
| **`SubscriptHandler.cs`** | Index param needs conversion unsupported in indexer body | Fixable (indexer conversion path) |
| | Accessor would force Swift wrapper with incompatible call syntax (`__self[index]`) | Fixable (wrapper subscript syntax) |
| **`IHandler.cs`** | Empty-tuple-only ctor collides with parameterless sibling after `()` stripping | **Permanent / correct** collision guard |
| **`EnumHandler.SimpleEnum.cs`** | Extension method/property/static return or param outside simple-type whitelist | Capacity (whitelist) or permanent for complex types |
| **Type handlers** (`ClassHandler`, `FrozenStructHandler`, `NonFrozenStructHandler`, `EnumHandler`) | **Type-level** `TryGetVariadicGenericParameter` → entire type skipped | **Permanent** (packs) |

### 3.5 `ConstrainedExtensionEmitter` — comment-only (honesty gap)

File: `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ConstrainedExtensionEmitter.cs`

Emits `UnsupportedCommentEmitter.EmitMemberSkipped(..., UnsupportedSignature, …)` for:

- open-generic return substitution unsupported  
- constrained **static** property not yet supported  
- bound-generic return not yet supported  
- unsupported CE return shape (property/method)

**Does not call `ReportCollector.RecordMemberSkipped`.** These can appear as `// Unsupported:` comments and SWIFTBIND025 drops without a matching `SkippedItems` row — undercount vs true loss surface. Related open-generic CE methods that are out-of-scope **are** reported via pipeline Gate (with Details) when suppressed at open-generic level.

---

## 4. Sub-bucket taxonomy (actionable)

Canonical buckets for backlog / triage. Codes are stable **theme IDs**, not enum values.

| ID | Sub-bucket | Discriminating Details tokens | Primary sites | Permanent? | Product note |
|----|------------|-------------------------------|---------------|------------|--------------|
| **US-PAT** | Unresolvable associated type / PAT leak | `unresolvable associated type` | GateEvaluator, protocol/concrete | **Mostly permanent** without concrete specialization or erased API | High in PAT-heavy Apple APIs (AppIntents `perform`, BlinkID analyzer) |
| **US-BARE** | Bare generic type without args | `without type arguments` / `bare generic type` | Pipeline Gate 5, GateEvaluator, Validator | Mostly permanent / TypeDB | Often overlaps open-generic misuse |
| **US-TAU** | Raw ABI τ params in concrete context | `τ_0_0` / `raw generic type parameters` | GateEvaluator hard gates | Permanent or parser fix if false leak | Method-level generics mis-flagged |
| **US-PACK** | Variadic generic parameter packs | `variadic generic parameter pack` / `each …` | Pipeline 4b; type handlers | **Permanent** (C#) | Type-level kills whole type |
| **US-GCTOR** | Generic constructor method-own params | `generic constructors with method-own` | Pipeline Gate 6 | **Permanent** in C#; **CSM recovers** many concrete forms | CryptoKit: 99/99 “correctly excluded” |
| **US-PLACE** | Placeholder / AnyType in projected wrapper signature | `unsupported placeholder type` / `ContainsPlaceholder` | Validator, MethodHandler, PropertyHandler, OperatorHandler | **Mixed residual** — often should be a more specific SkipReason | Dominant BindingTests method cluster (9+1); AppIntents IntentFile gap |
| **US-TUPLE** | Tuple marshalling incomplete | `Tuple parameter` / `unsupported element (closure or AnyType)` / async non-simple enum in tuple | Pipeline 5b, Validator C1/C6 | **Fixable** | Real consumer gap when API uses convertible-element tuples |
| **US-ASYNC** | Async ABI without safe wrapper | `Async method without @_cdecl` / `wrapper not emitted; direct call would be ABI-unsafe` | MethodHandler | **Fixable** (wrapper eligibility) | MusicKit queue insert; BindingTests ×3 |
| **US-ATC** | Async-throwing closure unbridgeable | `Async-throwing closure parameter cannot be bridged` | MethodHandler | **Fixable** (matrix expand) | Stripe IntentConfiguration-style APIs |
| **US-VARARG** | Value-level `T...` on constructors | `variadic parameter (T...)` | MethodHandler | **Semi-permanent** for ctors (wrapper call-site) | Methods use CallConvSwift; ctors skip |
| **US-CCONV** | Ctor `@convention(c)` + Swift closure mix | `@convention(c) closure parameter alongside` | MethodHandler | Architectural hard | Rare but correct fail-closed |
| **US-RESULT** | `Result` write-in (setter/param) | `Result-typed parameter` / write-in | PropertyHandler | Until Result outbound | Getter-only Result remains supported |
| **US-OP** | Operator limits | `C# operators cannot have generic` / `buffer marshalling` / operator placeholder | OperatorHandler | Generic operands permanent; buffer fixable | MusicKit `==` on generic types |
| **US-SUB** | Subscript indexer / wrapper syntax | `indexer body` / `incompatible call syntax` | SubscriptHandler | **Fixable** | RealityFoundation IK subscript etc. |
| **US-BUF** | Unsafe raw buffer pointer v1 limits | `SWIFTBIND104` + buffer pointer | Validator | **Fixable** (v2) | Explicit versioned scope |
| **US-EMPTY** | Empty-tuple ctor collision | `only empty tuple () parameters` | IHandler | **Correct permanent** | Dedup honesty |
| **US-SEENUM** | Simple-enum extension type whitelist | `unsupported for simple enum extension` | EnumHandler.SimpleEnum | Capacity / projection | WorkoutKit-style HealthKit return types |
| **US-CE** | Constrained-extension out of scope / emit bail | `out of scope for ConstrainedExtensionEmitter` / CE comment Details | Pipeline + ConstrainedExtensionEmitter | **Fixable** (expand CE) | Open-generic surface deliberately suppressed; closed CE may recover subset |
| **US-THROWPROP** | Throwing property getter (related) | Often surfaces as SEENUM / wrapper reject SWIFTBIND107 | PropertyWrapperEmitter eligibility (not always this SkipReason) | Fixable | WorkoutKit audit linked SWIFTBIND107 + simple-enum path |

---

## 5. What “placeholder type” actually means

`MethodSignature.Signature.ContainsPlaceholder` (`MethodSignature.cs`):

```text
Parameters.Any(p => p.Type.ContainsAnyTypePlaceholder())
|| ReturnType.Contains(AnyType.CSharpTypeName)
```

`MarshalledType.ContainsAnyTypePlaceholder()` is true when a **Simple** marshalled type’s C# name contains the AnyType fully-qualified name.

So **US-PLACE** is not “parser saw OpaqueType” literally — it is **“projection failed into AnyType on the wrapper signature.”** Adjacent reasons that are **not** folded into UnsupportedSignature when they fire first:

| Earlier / sibling reason | When used instead |
|--------------------------|-------------------|
| `AnyTypeFallback` | Explicit AnyType projection checks (e.g. protocol property AnyType generic arg) |
| `UnsupportedClosure` | Unsupported closure shapes (B20) |
| `UnsupportedExistential` | Unsupported existentials / nested @objc positions |
| `UnsatisfiedGenericConstraint` | Bound generic ISwiftObject / constraint failures |
| `NetUnavailableType` / SwiftUI / Combine | Module classification via `ValidationRuleSet` |
| `GenericProtocolConstraint` | PAT protocol constraints on methods |

**Taxonomy honesty issue:** US-PLACE absorbs “couldn’t project” after other gates miss. Some BindingAudit “(b) small gaps” (e.g. IntentFile, RealityKit `ARView` Metal/AV types) are TypeDB / framework bridging problems mis-bucketed as signature skips.

---

## 6. Validation-corpus shape (from BindingAudit, not baseline Details)

Baseline stores only the **count** 1420. Library audits give density clues:

| Library | US count | Dominant sub-bucket(s) | Audit class |
|---------|---------:|------------------------|-------------|
| CryptoKit | 99 | **US-GCTOR** exclusively (`init<Bytes: ContiguousBytes>` etc.; CSM fills usable specializations) | (a) correctly excluded |
| AppIntents | 217 | US-PAT + US-GCTOR + US-PACK + US-PLACE | mostly (a); small (b) IntentFile |
| MusicKit | 29 | US-OP, US-GCTOR, US-VARARG, enum encode, US-ASYNC, US-PLACE | (b) real gaps called out |
| RealityFoundation | 66 | US-VARARG, US-PLACE (Metal/AV), US-SUB | mixed; not ECS-core |
| WeatherKit / TipKit / Kingfisher / Mappedin / … | 1–24 | PAT, placeholder, packs, collisions | mixed |

**Rule of thumb for the 1420:**

1. **Large permanent share** — US-GCTOR + US-PACK + US-PAT on open protocols.  
2. **Large residual share** — US-PLACE from missing TypeDB / Apple framework types / opaque results.  
3. **Smaller fixable share** — US-TUPLE, US-ASYNC, US-SUB, US-ATC, US-BUF, US-OP buffer, CE scope.

Without a validate-run Details histogram, do not claim precise % splits; re-run `nuke validate` + aggregate `Details` if needed for prioritization math.

---

## 7. Fixable vs permanent (summary)

### Permanent / correctly excluded (do not “fix the generator”)

| Sub-bucket | Why permanent |
|------------|---------------|
| **US-GCTOR** | C# language forbids generic constructors; open form unbindable. Prefer CSM / concrete overloads. |
| **US-PACK** | No C# parameter packs. |
| **US-OP** (generic operands) | C# operators cannot be generic methods. |
| **US-EMPTY** | Avoids CS0111-style ctor collision after `()` elision. |
| **US-PAT** (true PAT in open protocol surface) | Needs concrete Self / specialization or Swift-side erasure. |
| **US-CCONV** | Allocating-init + denied convention(c) path has no safe ABI. |
| **US-VARARG** on **constructors** | `@_cdecl` cannot pass `[T]` for `T...` call site (methods may still CallConvSwift). |

### Fixable (generator / TypeDB capacity)

| Sub-bucket | Direction |
|------------|-----------|
| **US-TUPLE** | Per-element conversion + lifetime for non-buffer-marshallable tuple params |
| **US-ASYNC** | Emit `@_cdecl` async bridges for more shapes; keep fail-closed until then |
| **US-ATC** | Expand async-throws closure baseline matrix |
| **US-SUB** | Indexer body conversions; subscript-aware Swift wrapper syntax |
| **US-BUF** | v2 raw-buffer return / async / inout writeback |
| **US-OP** (buffer marshalling) | Operator preamble for buffer-renamed P/Invoke params |
| **US-CE** | Expand constrained-extension emit scope beyond zero-arg sync non-throwing; **also** wire ReportCollector for comment-only bails |
| **US-RESULT** | Result outbound / setter path (large design) |
| **US-PLACE** when TypeDB-missing Apple types | Register remaps / stubs (Metal, AV, CMTime, …) — may reclassify to `AbsentFrameworkType` / `NetUnavailableType` |
| **US-SEENUM** | Broaden simple-enum extension param/return projection |
| **US-THROWPROP** | Property wrapper try/catch (SWIFTBIND107) |

### Honesty / reporting improvements (not surface unlocks)

1. **Split `UnsupportedSignature` enum or stable Details prefixes** so dashboards don’t treat 1420 as one KPI.  
2. **ConstrainedExtensionEmitter → ReportCollector** so CE bails enter SkippedItems.  
3. **Prefer earlier SkipReason** when placeholder is really NetUnavailable / AbsentFramework / PAT.  
4. **SWIFTBIND104 string collision** between archive-nm Hard and buffer Soft skips — rename one.  
5. Validate baseline should optionally store **Details prefix histogram** for UnsupportedSignature (this pack’s missing data).

---

## 8. Gate map (who maps to UnsupportedSignature)

```text
HandleBaseDecl / emission entry
  └─ MemberValidationPipeline.ValidateMethodEmission / ValidatePropertyEmission
       ├─ 4b packs ──────────────────────────────► US-PACK
       ├─ Gate 2 ← MemberEmissionValidator.ShouldSkipMethodEmission
       │     ├─ C6 async tuple non-simple enum ──► US-TUPLE (async)
       │     ├─ ContainsPlaceholder ─────────────► US-PLACE
       │     └─ SWIFTBIND104 buffers ────────────► US-BUF
       ├─ constrained-extension out-of-scope ────► US-CE
       ├─ Gate 5 bare generic ───────────────────► US-BARE
       ├─ Gate 5b tuple params ──────────────────► US-TUPLE
       └─ Gate 6 generic ctor ───────────────────► US-GCTOR

Protocol emission (MemberGateEvaluator)
  └─ PAT / bare / τ_0_0 ─────────────────────────► US-PAT / US-BARE / US-TAU

PropertyHandler / MethodHandler / Operator / Subscript / IHandler
  └─ late fail-closed shapes ────────────────────► US-* as above

Type handlers (class/struct/enum)
  └─ type-level packs ───────────────────────────► US-PACK (type skip)
```

Related reasons **often confused** with UnsupportedSignature but **separate enums**:

- `UnsupportedClosure`, `UnsupportedExistential`, `AnyTypeFallback`, `UnsatisfiedGenericConstraint`, `GenericProtocolConstraint`, `DuplicateSignature`, `NonBlittableCallConvSwift`, `ConstrainedExtensionWrapper`, `MissingWrapperSymbol`.

---

## 9. Worker implications

1. **Do not open a single “UnsupportedSignature epic.”** Prioritize **US-TUPLE / US-ASYNC / US-SUB / US-PLACE(TypeDB)** for consumer surface; leave **US-GCTOR / US-PACK / US-PAT** as permanent or CSM-adjacent.  
2. **BindingTests covers breadth of sub-buckets well** (37 rows span 14 Details patterns) — good regression matrix; weak on volume of US-GCTOR (only 4). CryptoKit-style mass GCTOR is a validation-only phenomenon.  
3. **G1 partial-success:** This reason is already fail-closed-at-emission for most paths — good degradation. Residual risk is **misclassification** (placeholder vs NetUnavailable) and **comment-only CE skips**.  
4. **Next measurement step** (optional, not done here): script Details-prefix counts over a fresh `nuke validate` tree of per-library `binding-report.json` files to weight the 1420.

---

## 10. File index (absolute)

| Path | Role |
|------|------|
| `/Users/wojo/Dev/swift-bindings/src/Swift.Bindings/src/Reporting/BindingReport.cs` | `SkipReason.UnsupportedSignature` enum |
| `/Users/wojo/Dev/swift-bindings/src/Swift.Bindings/src/Reporting/SkipDisposition.cs` | → KnownLimitation |
| `/Users/wojo/Dev/swift-bindings/src/Swift.Bindings/src/Reporting/WorkaroundRecommendations.cs` | Consumer text |
| `/Users/wojo/Dev/swift-bindings/src/Swift.Bindings/src/Emitter/StringEmitter/MemberValidationPipeline.cs` | Gates 4b, 5, 5b, 6, CE out-of-scope |
| `/Users/wojo/Dev/swift-bindings/src/Swift.Bindings/src/Emitter/StringEmitter/MemberGateEvaluator.cs` | Protocol/concrete PAT, bare, τ |
| `/Users/wojo/Dev/swift-bindings/src/Swift.Bindings/src/Emitter/StringEmitter/MemberEmissionValidator.cs` | Placeholder, tuple, SWIFTBIND104, C6 |
| `/Users/wojo/Dev/swift-bindings/src/Swift.Bindings/src/Emitter/StringEmitter/Handler/MethodHandler.cs` | Ctor/async/closure late gates |
| `/Users/wojo/Dev/swift-bindings/src/Swift.Bindings/src/Emitter/StringEmitter/Handler/PropertyHandler.cs` | Result write-in, accessor placeholder |
| `/Users/wojo/Dev/swift-bindings/src/Swift.Bindings/src/Emitter/StringEmitter/Handler/OperatorHandler.cs` | Operator skips |
| `/Users/wojo/Dev/swift-bindings/src/Swift.Bindings/src/Emitter/StringEmitter/Handler/SubscriptHandler.cs` | Indexer skips |
| `/Users/wojo/Dev/swift-bindings/src/Swift.Bindings/src/Marshaler/IHandler.cs` | Empty-tuple ctor collision |
| `/Users/wojo/Dev/swift-bindings/src/Swift.Bindings/src/Emitter/StringEmitter/Handler/EnumHandler.SimpleEnum.cs` | Simple enum whitelist |
| `/Users/wojo/Dev/swift-bindings/src/Swift.Bindings/src/Emitter/StringEmitter/Handler/{Class,FrozenStruct,NonFrozenStruct,Enum}Handler.cs` | Type-level packs |
| `/Users/wojo/Dev/swift-bindings/src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ConstrainedExtensionEmitter.cs` | Comment-only US skips |
| `/Users/wojo/Dev/swift-bindings/src/Swift.Bindings/src/Emitter/StringEmitter/Handler/MethodSignature.cs` | `ContainsPlaceholder` definition |
| `/Users/wojo/Dev/swift-bindings/BindingTests/output/binding-report.json` | 37 live rows |
| `/Users/wojo/Dev/swift-bindings/build/baselines/validation-baseline.json` | 1420 aggregate |
| `/Users/wojo/Dev/swift-bindings/src/docs/BindingAudit/*.md` | Per-library (a)/(b) classifications |

---

*End of data pack 09. No production code changed.*
