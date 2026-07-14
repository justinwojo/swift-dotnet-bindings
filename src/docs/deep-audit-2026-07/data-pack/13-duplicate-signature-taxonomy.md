# Data Pack — DuplicateSignature Taxonomy

| Field | Value |
|-------|--------|
| **Date** | 2026-07-16 |
| **Mode** | Read-only inventory (no production edits) |
| **Why** | **#6** skip in validation corpus (**450** / 6.1% of skipped members) |
| **Disposition** | `KnownLimitation` (`SkipDisposition.cs`) |
| **Consumer copy** | *“Rename one member via a Swift extension to disambiguate.”* (`WorkaroundRecommendations.cs`) |
| **Sources** | Production `SkipReason.DuplicateSignature` assign sites; BindingTests `output/binding-report.json` (7 rows); `roadmap.md` protocol residual; BindingAudit library notes; `validation-baseline.json` count only |

---

## 1. Headline

`DuplicateSignature` is the **dedup residual after projection + naming**. Two Swift members that cannot coexist as distinct C# overloads (same projected name + param types, constructors that cannot be renamed, pure type-erasure, or residual protocol static label-blindness) drop the loser with this reason.

**Not one bug:**

| Sub-bucket | Meaning | Default fixability |
|------------|---------|-------------------|
| **Label-only collapse** | Differ only by Swift external arg labels → same C# signature after erasure | **Instance protocol FIXED**; class methods already label-inclusive + numeric suffix; **static protocol residual by design** |
| **Type-projection collapse** | Distinct Swift types project to one C# type (`any P`/`any Sendable` → same interface param; generics → `AnyType`) | **By-design / hard** — C# has no dual overload; reverse-dispatch must keep raw-key slot collapse honest |
| **Constructor projected collision** | Two `init`s → same projected ctor key; C# ctors cannot be renamed | **Partial fix**: failable inits recover as `TryCreate` + label suffix; non-failable still drop |
| **Property / subscript name collision** | Same C# property/subscript surface already emitted | Mixed — enum case recovery exists; true dups by-design |
| **Bypass reduced-signature collision** | Optional-closure bypass omits params → reduced key collides with existing member | Honest skip / rare |

**Implication:** Do not treat “fix 450” as one epic. Instance label-only protocol work is **done**. Remaining value is naming policy (statics, ctor labels, subscripts) and accepting pure type-erasure drops as structural.

---

## 2. Corpus metrics

| Corpus | Count | Notes |
|--------|------:|-------|
| **Validation baseline** | **450** | `build/baselines/validation-baseline.json` → `skip_metrics` |
| **BindingTests** | **7** | Live `BindingTests/output/binding-report.json` |
| Share of validation skips | **6.1%** | `04-validation-corpus-skip-heatmap.md` |

### BindingTests Details histogram (all 7 rows)

| Count | ContainingType.Name | Details | Sub-bucket |
|------:|---------------------|---------|------------|
| 1 | `ReturnTypeOnlyOverloadHost.selectExpression` | `method:selectExpression(arg0:…VariadicSection)` | **Primary-key / return-type-only** (same inputs, return-only distinction erased) |
| 1 | `WitnessIndexProto.consume` | `Duplicate protocol method signature.` | **Protocol raw-key collapse** (type-erasure residual) |
| 1 | `WitnessIndexConformer.consume` | `method:consume(arg0:Swift.AnyType)` | **Type-projection** (`AnyType` key) |
| 1 | `OverloadCollapseDelegate.record` | `Duplicate protocol method signature.` | **Protocol type-erasure** (fixture for existential overload collapse; receivers keep raw-key honesty) |
| 1 | `TombstoneOverloadCollision.handle` | `method:handle\|throws(callback:Swift.AnyType)` | **Tombstone + AnyType** projected collapse |
| 1 | `CRTRefinedShapeImpl.makeShape` | `method:makeShape()` | **Zero-arg projection collision** (refined-type / CRT fixture) |
| 1 | `CRTPropertyImpl.makeColumn` | `method:makeColumn()` | same family |

BindingTests is **fixture-shaped**: label-only *instance* protocol overloads are **not** in this bucket anymore (`DuplicateSignatureDisambiguation` proves both survive). The 7 rows are intentional residual collapses.

### Real-library skew (BindingAudit samples)

| Library | ~Dup rows | Dominant flavor |
|---------|----------:|-----------------|
| AppIntents | 175 | `ctor(string)` label collisions (mild — survivor still usable) |
| RealityFoundation | 41 | Init overloads (CGImage/URL/Data) + peripheral |
| Kingfisher | 7 | Method overload drops |
| MusicKit | 4 | `MusicItemID.init`, factory/enum collision |
| LiveCommunicationKit | 2 | **Was** label-only instance delegate — **fixed**; residual would be statics/type-erasure only |
| RoomPlan | 3 | Delegate `didAdd`/`didChange` family (label-derived rename product ask) |
| Stripe | 3 | PaymentSheet / FlowController init/present collisions |

---

## 3. Production record sites (complete inventory)

Every production assign of `SkipReason.DuplicateSignature` (excluding tests).

### 3.1 Primary method / constructor path — `IHandler` / `ModuleHandler`

| File | Trigger | Details pattern | Fixability |
|------|---------|-----------------|------------|
| `Marshaler/IHandler.cs` | Primary `GetMethodSignatureKey` already in `emittedMethodSignatures` (non-accessor) | signature key string | **Label-only methods** already include labels in primary key on class path → usually only true dups / return-type-only |
| `IHandler.cs` | Constructor projected key collision, **non-failable** | `Projected C# constructor signature collides: {key}` | **Fixable (naming)** — label-derived factory naming for non-failable is open product; failable already recovers as `TryCreate*` |
| `Emitter/…/ModuleHandler.cs` | Free-function primary signature dup | signature key | Same as method primary |

**Not DuplicateSignature (adjacent):** empty-tuple ctor collision → `UnsupportedSignature`; failable-init projected collision → **emit** under label-suffixed `TryCreate` (no skip).

### 3.2 Protocol path — `ProtocolHandler.cs`

| Branch | Key axis | Drop Details | Status |
|--------|----------|--------------|--------|
| **Static methods** | Label-**blind** `GetMethodSignatureKey` | `Duplicate protocol method signature.` / projected collide | **By-design residual** — no reverse-dispatch; selector rename alone breaks concrete static name-parity; roadmap medium “residual = statics” |
| **Instance methods** | `ProtocolMethodDisambiguator.EffectiveRawKey` (label-**inclusive** for label-only siblings) | same strings | **Label-only FIXED**; remaining = pure type-erasure / raw identity |
| **Instance projected** | `EffectiveProjectedKey` (label-derived ObjC-selector names) | `Projected C# method signature collides…` | Type-erasure residual |
| **Emitted signature** | `BuildEmittedSignature` (async CT, property rename) | `Emitted C# method signature collides…` | Rare post-projection collision |
| Protocol property / subscript | name/signature HashSet | `Duplicate protocol property/subscript signature.` | Structural |

Pinned: `ProtocolHandlerOutputTests` label-only pair; `WitnessDispatchEmitterTests` slot split; BindingTests `DuplicateSignatureDisambiguation` (+ class non-protocol label-only both survive).

### 3.3 Other type handlers

| File | Trigger | Details |
|------|---------|---------|
| `MethodHandler.cs` | Optional-closure **ctor** bypass reduced key collides | `Optional closure bypass reduced constructor signature collides: …` |
| `IMethodBridgeEmitter.cs` | Optional-closure **method** bypass reduced key collides | `Optional closure bypass reduced C# signature collides: …` |
| `SubscriptHandler.cs` | Duplicate subscript signature | `Duplicate subscript signature.` |
| `ClassHandler.cs` | Property name already emitted with different staticness | `Property '…' already emitted with different staticness.` |
| `FrozenStructHandler.cs` / `NonFrozenStructHandler.cs` | Property name already emitted | `Property '…' already emitted.` |
| `EnumHandler.cs` | Enum property collides with case constructor name | `Enum property '…' collides with case constructor name.` — **partial recovery** exists (Value suffix / FB-1); residual still records Dup |

### 3.4 Related, different reason

| Reason | Relation |
|--------|----------|
| `ObjCDuplicateSignature` | ObjC companion path; projected 1:1 from `ObjCSkipReason.DuplicateSignature` — **not** counted in the Swift 450 |

---

## 4. Label-only vs type-projection collapse

```text
Swift overloads
  ├─ Differ only by external arg labels
  │    ├─ Protocol INSTANCE ──► ProtocolMethodDisambiguator → BOTH emit (FIXED)
  │    ├─ Protocol STATIC ────► label-blind key → second DuplicateSignature (BY DESIGN)
  │    └─ Class / struct ─────► label-inclusive primary key + numeric suffix → BOTH emit
  ├─ Same labels, distinct Swift types → same C# projection
  │    └─ Type-erasure ───────► second DuplicateSignature (BY DESIGN; C# cannot dual-overload)
  └─ Constructors (non-failable) same projected inputs
       └─ Cannot rename ──────► second DuplicateSignature (PRODUCT: naming/factory policy)
```

**Critical dual-key rule (not this skip, but adjacent):** vtable layout uses **raw** `GetMethodKey` / `VtableLayout`; interface surface uses projected keys. Overload-collapse fix (`emittedRawKeys`) prevents orphan receivers when two raw slots map to one C# method — the dropped requirement is still a `DuplicateSignature` on the interface, but reverse-dispatch stays layout-safe.

---

## 5. Protocol residual statics (roadmap medium)

From `roadmap.md` and inlined `ProtocolHandler.cs` static branch:

1. **Statics** that differ only by label still collapse (second = `DuplicateSignature`).
2. Statics have **no** reverse-dispatch / vtable path; any static *method* requirement disables the whole protocol’s EveryProtocol conformance.
3. A interface-only selector rename would **break** the conformance validator’s static name-parity vs concrete numeric-suffix names (`Configure` / `Configure2`).
4. Faithful fix = **unified static naming policy** (session-08 territory), not disambiguator extension alone.
5. **Trigger to reopen:** real library needs both label-only static requirements **and** working static dispatch.

Second residual: **pure type-erasure** collapses remain intentional.

---

## 6. Fixable vs by-design matrix

| Sub-bucket | Validate role (qualitative) | Fixable? | Notes |
|------------|----------------------------|----------|-------|
| Protocol instance label-only | Was high-value (LCK VoIP) | **DONE** | Keep disambiguator wired everywhere |
| Protocol static label-only | Small | **By-design** until naming policy session | Roadmap residual (1) |
| Type-erasure (existentials, generics → one C#) | Medium | **By-design** | Honest C# limit; document survivor |
| Non-failable ctor projected collision | High in Apple kits | **Fixable (product)** | Label-derived factories / alternate names |
| Failable ctor projected collision | — | **DONE** | `TryCreate` + label suffix |
| Class label-only methods | — | **DONE** | Primary key + numeric suffix |
| Subscript / property name collision | Medium (KeychainAccess) | **Fixable (naming)** | Prefer suffix over drop where legal |
| Enum property vs case ctor | Low | **Mostly fixed** | Residual Value-suffix path |
| Bypass reduced-signature | Rare | Honest | Don’t “fix” by double-emitting |

**G1 / worker takeaway:** Prefer disambiguating suffixes over silent drop for **product-visible** ctor/method sets; do **not** reopen protocol statics without a naming-policy design. Count 450 is capacity/naming, not a crash class.

---

## 7. Key absolute paths

- `src/Swift.Bindings/src/Marshaler/IHandler.cs` — primary + projected ctor dedup  
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ProtocolHandler.cs` — protocol static residual + instance disambiguator  
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/{MethodHandler,ModuleHandler,ClassHandler,EnumHandler,SubscriptHandler,FrozenStructHandler,NonFrozenStructHandler}.cs`  
- `src/docs/roadmap.md` — “Protocol-side dedup… residual = statics”  
- `BindingTests/RuntimeTestsApp/Protocols/DuplicateSignatureDisambiguationTests.cs`  
- `build/baselines/validation-baseline.json` — **450**
