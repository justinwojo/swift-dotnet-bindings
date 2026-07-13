# C# Binding Quality & Structure Audit

- **Scope**: Generated consumer C# surface from the SwiftBindings generator (not coverage %, not runtime ABI proofs).
- **Audited at**: 2026-07-11 — samples from worktree + main package trees under `swift-dotnet-packages` and `internal-binding-testing`.
- **Question**: Are generated bindings well-structured, navigable, and idiomatic for C# consumers who want **1:1 with native APIs** plus **reasonable .NET affordances**?

## Sources sampled (≥8 libraries)

| Library | Path / artifact | Approx size | Mode |
|---|---|---|---|
| **StoreKit2** | `…/StoreKit2/…/swift-binding/StoreKit2.cs` | ~31k lines | Apple Swift |
| **CryptoKit** | `…/CryptoKit/…/swift-binding/CryptoKit.cs` | ~27k lines | Apple Swift |
| **MusicKit** | `…/MusicKit/…/swift-binding/MusicKit.cs` | ~57k lines | Apple Swift (+ package shims) |
| **WeatherKit** | `…/WeatherKit/…/swift-binding/WeatherKit.cs` | large | Apple Swift |
| **RealityFoundation** | `…/RealityFoundation/…/swift-binding/RealityFoundation.cs` | **~135k lines** | Apple Swift |
| **Alamofire** | `internal-binding-testing/Alamofire/Alamofire.cs` | ~60k lines | Third-party Swift |
| **DeviceKit** | `internal-binding-testing/DeviceKit/DeviceKit.cs` | ~6.5k lines | Third-party Swift |
| **SnapKit** | `internal-binding-testing/SnapKit/SnapKit.cs` | large | Third-party Swift |
| **Kingfisher** | `internal-binding-testing/Kingfisher/Kingfisher.cs` | ~62k lines | Third-party Swift |
| **MapLibre** | `libraries/MapLibre/…/ApiDefinition.cs` | ~8k lines | ObjC / bgen |
| **Matter** | `apple-frameworks/Matter/…/ApiDefinition.cs` | **~93k lines** | ObjC / bgen |

Ergonomics guides consulted: `STOREKIT2-GUIDE.md`, `CRYPTOKIT-GUIDE.md`, `MUSICKIT-GUIDE.md`, `WEATHERKIT-GUIDE.md`, `REALITYFOUNDATION-GUIDE.md`, `TIPKIT-GUIDE.md`.

---

## Overall quality score / verdict

### Score: **B+ (good for a 1:1 ABI binding generator; not yet “hand-crafted SDK polish”)**

**Verdict.** The generated C# is **usable, predictable, and largely 1:1 with Swift** once a consumer learns a short set of projection rules (documented well in the package GUIDEs). Lifetime (`IDisposable` / SafeHandle / ARC class handles), async (`Task` + `CancellationToken`), payload enums (`CaseTag` + `TryGet*`), collections (`IReadOnlyList<T>`), and protocol→interface + proxy are **consistent across libraries**. Interop machinery is mostly hidden behind `EditorBrowsable(Never)` or private P/Invokes.

The same emission model that makes this work at scale also produces **chronic navigability tax**: single mega-files (RealityFoundation ~135k lines, Matter ApiDefinition ~93k), name stutter (`OfferTypeTypeType`, `ReasonType`), hash-suffixed factories (`Create_C11D4260`), awkward specialization names (`FrombyteArr_`), broken noun→`Get` on zero-arg fluent methods (`GetequalToSuperview`), AsyncSequence surfaces that stop short of `IAsyncEnumerable`, and sparse XML docs on ordinary members. Package GUIDEs and occasional hand shims (MusicKit) currently carry the ergonomic load the generator does not.

**Ship posture.** Fine for early adopters who follow Apple docs + GUIDEs and treat the binding as a thin projection. Not yet “open the assembly in Object Explorer and feel at home” without the GUIDE.

---

## Good patterns to preserve

### 1. Type modeling is coherent and documented

| Swift shape | C# projection | Evidence |
|---|---|---|
| Non-frozen / resilient struct | `partial class` + `SwiftSafeHandle<T>` + `IDisposable` | StoreKit2 `Product`, `Transaction`; DeviceKit `Device` |
| Swift class | `partial class` + `SwiftClassHandle<T>` + optional dispose / ARC finalizer | MusicKit `AnyMusicProperty`; SnapKit makers |
| Plain int enum | C# `enum : int` + `…Extensions.AllCases` / `GetDescription()` | WeatherKit `MoonPhase`; CryptoKit `CryptoKitASN1Error` |
| Enum with payload / behavior | Class + nested `CaseTag` + `Tag` + `TryGet*` + static case factories | StoreKit2 `VerificationResult<T>`; MusicKit `Track`; DeviceKit `Device` |
| Protocol | `I{Name}` interface + `…Proxy` in `{Module}.SwiftInterop` | Alamofire `IParameterEncoding`; StoreKit2 `IStoreDownloaderExtension` |
| Nested caseless enum namespaces | Nested `static partial class` facades | CryptoKit `AES.GCM`, `Curve25519.Signing`, TipKit `Tips.*` |
| Free functions | `Functions` static holder | RealityFoundation `Functions.Blend(...)` |

Dispose messaging is explicit in type remarks (“must be disposed” for structs vs “finalizer handles ARC” for classes). That is the right consumer education for a 1:1 binding.

### 2. Naming transforms are rule-based (GUIDEs match emission)

Consistently observed:

- PascalCase properties/methods (`entity.Name`, `product.PurchaseAsync`).
- First argument label dropped; remaining labels kept as C# parameter names.
- Swift `async` → `*Async` + `Task`/`Task<T>` + trailing `CancellationToken cancellationToken = default`.
- Property/method name collision → `FooMethod` (e.g. StoreKit2 `Transaction.CurrentEntitlementsMethod(string productID)` vs property `CurrentEntitlements`).
- Reserved / runtime-colliding names → suffix (`FinalizeSwift` for `finalize`).
- Namespace collision avoidance where needed (`StoreKit2` not `StoreKit`).

### 3. Async is real .NET async (not blocking-only)

```csharp
// StoreKit2 — default-param overload + CT
Task<PurchaseResult> PurchaseAsync(IEnumerable<PurchaseOption> options, CancellationToken cancellationToken = default);
Task<PurchaseResult> PurchaseAsync(CancellationToken cancellationToken = default);
Task<IReadOnlyList<Product>> ProductsAsync(IEnumerable<string> identifiers, CancellationToken cancellationToken = default);

// WeatherKit
Task<Weather> WeatherAsync(CLLocation location, CancellationToken cancellationToken = default);
```

Callbacks, cancel registration, and `DeferredSafeHandleRelease` keep native lifetime correct across the await boundary. This is a flagship affordance and should stay.

### 4. Collections and strings project idiomatically at the public edge

- Arrays / forecasts → `IReadOnlyList<T>` (WeatherKit `Forecast<T>`, MusicKit `MusicItemCollection<T>`).
- Optional arrays → `IReadOnlyList<T>?` with nullable annotations.
- String properties → `string` / `string?` via Utf8Slice / `SwiftString` conversion **inside** getters (DeviceKit `Name` → `((SwiftString?)__ret)?.ToString()`).
- Parameters accept `string`; marshalling to `SwiftString` is internal.

### 5. Generics: closed specializations + honest obsolete open forms

CryptoKit emits usable `byte[]` / `Foundation.Data` specializations and marks open-generic ABI stubs:

```csharp
[Obsolete("No @_cdecl wrapper or native thunk available. ...", DiagnosticId = "SB0001", ...)]
public byte[] Signature<D>(D data) where D : ISwiftObject
```

Concrete factories carry XML one-liners (`/// Concrete specialization for byte[].`). Prefer-specialization is the correct CryptoKit story (GUIDE matches code).

### 6. Interop noise is mostly contained

- P/Invokes are **private/internal** `LibraryImport` helpers co-located with the type (not a public `NativeMethods` API on consumer types).
- Payload / metadata fields: `[EditorBrowsable(EditorBrowsableState.Never)]`.
- `ModuleInitializer` lives on an **internal** `__SwiftFrameworkResolver_{Module}` class (CryptoKit ~26088) with CA2255/CA1416 pragmas — not on the public API.
- Proxies in `{Module}.SwiftInterop` are `EditorBrowsable(Never)`.
- Unsupported members leave **comments** (`// Unsupported: method '…' — reason`) rather than fake stubs that crash silently (except where SB0001/SB0003/SB0004 obsolete stubs are intentional).

### 7. ObjC path is recognizable bgen quality

MapLibre / Matter `ApiDefinition.cs`:

- Classic `[Protocol]`, `[BaseType]`, `[Export]`, `[Abstract]`, `[NullAllowed]`.
- Header doc comments flow into `/// <summary>` (often long, but present).
- Namespace = module (`MapLibre`, `Matter`).
- Complements Swift-mode: different shape (NSObject inheritance, selectors) but consistent with Xamarin/.NET iOS expectations.

### 8. Package-level GUIDEs are excellent product surface

Naming tables, memory notes, known limitations, and copy-paste workflows compensate for generator roughness. MusicKit’s hand shims for existential-metatype inits are the right **package-level** escape hatch when the generator cannot express a Swift shape.

---

## Defect catalog

Severity: **P0** ship-blocker / misleading public API · **P1** frequent consumer pain · **P2** polish · **P3** niche.

### D1 — Single mega-file emission (P1, structural)

| Library | Lines (approx) |
|---|---|
| RealityFoundation.cs | **~135,000** |
| Matter `ApiDefinition.cs` | **~93,000** |
| Kingfisher.cs | ~62,000 |
| Alamofire.cs | ~60,000 |
| MusicKit.cs | ~57,000 |
| StoreKit2.cs | ~31,000 |
| CryptoKit.cs | ~27,000 |

**Impact:** IDE navigation, diff review, Source Link, and analyzer cost scale poorly. `partial class` is already used per type, so **file-per-type or file-per-top-level-type** is a pure packaging win with no API break.

**Also:** mid-file **indent collapse** (MusicKit after ~line 12k: types still in `namespace MusicKit` but at column 0). Cosmetic only, but it makes human review and brace-matching harder.

### D2 — Name stutter / collision suffixes (P1)

| Example | Library | Root cause |
|---|---|---|
| `Transaction.OfferTypeTypeType` | StoreKit2 | Nested type `OfferType` + property `OfferType` + nested `Type` suffix cascade |
| `Transaction.OwnershipTypeType`, `OfferTypeType` | StoreKit2 | Same pattern |
| `Message.ReasonType` | StoreKit2 | Nested type `Reason` vs property `Reason` → `ReasonType` |
| `Sha3256` vs `SHA3_256Digest` | CryptoKit | Digit-boundary PascalCase inconsistency |
| `FrombyteArr_` | CryptoKit | Specialization name mangling for `byte[]` |
| `Create_C11D4260` | MusicKit | Hash-suffixed factory for an init that needs a shim story |
| `FromTipKit_AnyTip` / `FromCryptoKit_SymmetricKey` | TipKit / CryptoKit | Verbose specialization factory names |
| `CurrentEntitlementsMethod` | StoreKit2 | Property/method collision rename (rule-correct, ugly) |
| `GetequalToSuperview` | SnapKit | Zero-arg method got noun→`Get` prefix with **broken casing** (`Get` + `equal…` not `Equal`) |

**Preserve** collision renames (they prevent CS0111). **Polish** Type-suffix policy, digit casing (`SHA3_256` not `Sha3256`), and specialization naming (`FromByteArray` not `FrombyteArr_`).

### D3 — AsyncSequence is not `IAsyncEnumerable` (P1)

StoreKit2 `PurchaseIntent.PurchaseIntents`, `Transaction.Transactions`, `Message.MessagesType` expose:

- `MakeAsyncIterator()`
- Nested `AsyncIterator.NextAsync(...)` → often `Task<SwiftOptional<IntPtr>>` or similarly raw forms

There is **no** `IAsyncEnumerable<T>` / `GetAsyncEnumerator` on the public sequence types sampled.

**Impact:** Consumers cannot `await foreach`. Guides must teach manual iterator loops. This is the highest-value missing .NET affordance that still stays 1:1 with Swift’s async sequence model.

### D4 — Open generics / AnyType leakage (P1 in generic-heavy libs)

| Symptom | Libraries |
|---|---|
| `[Obsolete(SB0001)]` open-generic methods still public | CryptoKit (many), RealityFoundation |
| `Swift.AnyType` on protocol statics / members | Alamofire `IEmptyResponse.GetEmptyValue()`, `IResponseSerializer` edges |
| Empty interfaces `SB0004` (all members skipped) | Alamofire `IDataResponseSerializerProtocol<T>` |
| `[UnsupportedSwiftType]` + `OriginalSwiftType` attributes on public APIs | RealityFoundation `Functions.Blend`, Alamofire encodings |
| `// Unsupported: … AnyType …` comments in type body | Alamofire, SnapKit, RealityFoundation |

**Positive:** failures are visible (obsolete + diagnostic id + wiki URL). **Negative:** public surface still lists unusable members in IntelliSense.

### D5 — Hash / opaque factory names (P1 for constructibility)

MusicKit generated:

```csharp
public static unsafe MusicCatalogSearchSuggestionsRequest Create_C11D4260(string term)
```

Package shims add proper `Create(term, types)` factories — proof that the generator name is not consumer-grade. Without shims, discoverability is near zero.

### D6 — Overload quality mixed (P2, sometimes P1)

**Good**

- Default-arg overloads (StoreKit2 `PurchaseAsync()` vs with options; TipKit `Tips.Configure()`).
- SnapKit default-parameter expansion for `file`/`line` plus `uint`→`nuint` wrappers.
- CryptoKit closed `byte[]` / `Data` matrices for seal/open/HMAC.

**Awkward / senseless**

- SnapKit `GetequalToSuperview` / `GetPriorityRequired` zero-arg forms (wrong `Get` + casing).
- Parallel overloads that only differ by optional bookkeeping params consumers never pass (file/line) — acceptable 1:1, noisy without `[EditorBrowsable]` or defaults-only public API.
- Open-generic + specialized pairs both visible (CryptoKit) — obsolete helps but still clutters completion lists.

### D7 — Documentation barren on generated members (P2)

| What gets XML docs | What usually does not |
|---|---|
| `Dispose` one-liners | Ordinary properties (`Code`, `Message`, product fields) |
| Enum case / `TryGet*` summaries | Most methods (`PurchaseAsync`, `WeatherAsync`) |
| ObjC header prose (MapLibre/Matter) | Swift modules (Apple docs not imported into XML) |
| Package shims (MusicKit Create) | Hash factories (`Create_C11D4260`) |

DeviceKit is relatively better on enum case docs. StoreKit2 has good enum-payload docs, sparse elsewhere. **GUIDEs carry the narrative**; IntelliSense does not.

### D8 — Public lifetime / dispose discipline is demanding (P2, by design)

Nearly every resilient struct is `IDisposable`. Correct, but:

- Nested graphs (MusicKit search response → collections → items) encourage dispose footguns.
- Async iterators and sequences require careful `using`.
- GUIDEs already stress this; a future `SwiftDisposeScope` / analyzer package would help more than changing defaults.

### D9 — ObjC vs Swift dual personality (P2, expected)

Consumers of Matter/MapLibre see NSObject/`[Export]` world; consumers of StoreKit2 see SafeHandle/Task world. Same product brand, different idioms. Document at package README level (Matter already does). Do **not** force Swift-mode shapes onto bgen APIs.

### D10 — Indentation / file hygiene (P3)

- Blank-heavy method bodies, repeated try/finally SafeHandle ceremony inlined (not factored in source form).
- MusicKit column-0 types after large nested emission (still namespaced — verified via `T:MusicKit.Album` in shipped XML).
- `// Unsupported:` comments inside public type bodies — useful for auditors, slightly noisy for readers.

### D11 — Frozen Swift structs never project as C# `struct` in samples (P3 / design note)

Across Apple + third-party samples grepped, **no** `public struct` consumer types appeared — even small value-like types use class + SafeHandle. Matches current marshalling model (`ISwiftStruct` marker on classes). Acceptable for correctness; costs allocations and dispose noise for tiny values (CryptoKit digests, keys).

---

## Theme-by-theme checklist

### 1. Type modeling — **Strong**

SafeHandle / class-handle split, protocol interfaces, payload enums, nested static facades all work. Nested type trees under `Product`, `Transaction`, `Tips` mirror Swift. Preserve.

### 2. Naming — **Good rules, rough edges**

PascalCase + Async suffix + label drop are solid. Stutter, hash factories, `FrombyteArr_`, `GetequalToSuperview`, and `Sha3256` are the polish backlog.

### 3. Async — **Strong Task story; weak sequence story**

Task + CT everywhere that matters. No `IAsyncEnumerable` for AsyncSequence. Manual `NextAsync` is not idiomatic C#.

### 4. Generics / specialization — **Pragmatic**

Closed specializations + SB0001 obsolete open forms is the right CryptoKit strategy. Alamofire/RealityFoundation still show AnyType holes where associated types / existentials win.

### 5. Interop noise — **Mostly good**

Private P/Invoke, Never-browsable payloads, internal ModuleInitializer. Residual: SB0001 methods, Unsupported attributes, proxy namespace (acceptable).

### 6. Nullability & strings — **Good at the boundary**

`string`/`string?` and `T?` optionals are the public face; `SwiftString` stays in private getters. `#nullable enable` on generated files.

### 7. Overloads — **Useful defaults; some noise**

Default-param overloads are a highlight. Fluent zero-arg `Get*` bugs and specialization name noise undercut the win.

### 8. ObjC path — **Consistent bgen-style**

MapLibre/Matter are standard binding projects with real header docs. Different from Swift-mode but appropriate.

### 9. Documentation — **Weak in IntelliSense; strong in GUIDEs**

Generated XML is thin. Package GUIDEs are the real product docs — keep investing there until XML import from symbol graph / Apple docc exists.

### 10. Structural maintainability — **Weak at file scale**

Mega-files dominate. Split emission is the highest leverage generator packaging change with zero API churn.

---

## Recommendations

### Generator polish (API-preserving or additive)

| Priority | Change | Why |
|---|---|---|
| **P1** | Emit **one file per top-level type** (or per namespace segment) | RealityFoundation/Matter navigability; CI diffs; IDE load |
| **P1** | Project AsyncSequence → **`IAsyncEnumerable<T>`** (keep raw iterator as advanced/Never) | StoreKit2 entitlements/updates become idiomatic |
| **P1** | Fix specialization / factory naming: `FromByteArray`, drop hash suffixes when signature is unique; prefer `Create(term)` over `Create_{hash}` | MusicKit/CryptoKit discoverability |
| **P1** | Fix zero-arg noun→`Get` casing (`GetEqualToSuperview` or better **no Get** for method-like nouns with `()` ) | SnapKit fluent API |
| **P1** | Tighten Type-suffix collision algorithm to avoid `TypeTypeType` | StoreKit2 readability |
| **P2** | Hide SB0001 open generics with `EditorBrowsable(Never)` (keep Obsolete) | Cleaner IntelliSense; still callable for experiments |
| **P2** | Normalize digit identifiers (`SHA3_256` not `Sha3256`) | CryptoKit fidelity to Apple names |
| **P2** | Optional XML from Swift doc comments / symbol graph when present | Close the IntelliSense gap |
| **P2** | Restore consistent indentation in emitter | Human auditability |
| **P3** | Consider Never-browsable for `file`/`line` overload variants when default overloads exist | SnapKit noise |

### Package-level docs / shims (do not wait on generator)

| Priority | Change | Why |
|---|---|---|
| **P0/P1** | Keep **GUIDE.md** naming tables + known limitations current per package | Already the consumer on-ramp |
| **P1** | Continue **hand shims** for unexpressible inits (MusicKit metatype arrays; TipKit companion tips) | Generator may never own result-builders / existential metatype arrays cleanly |
| **P1** | README blurb: “1:1 Swift projection — read GUIDE before Object Explorer” | Sets expectations vs hand-crafted SDKs |
| **P2** | Sample apps for StoreKit2 purchase loop + WeatherKit fetch + CryptoKit seal (already partially in guides) | Beats XML for teaching CaseTag/TryGet |
| **P2** | Matter/MapLibre: document ObjC-mode differences vs Swift-mode packages | Avoid dual-stack confusion |
| **P3** | Optional Roslyn analyzer package: “IDisposable Swift struct not disposed” | Lifetime footguns |

### Explicitly **do not** chase (respect project doctrine)

- Reimagining APIs away from Apple docs (no LINQ-only StoreKit, no hiding CaseTag behind C# pattern-match sugar **unless** additive).
- Making resilient structs C# `struct` without a full marshalling redesign.
- Forcing SwiftUI View types into the direct binding (bridge path is intentional).
- Unifying ObjC bgen surface with Swift SafeHandle surface.

---

## Scorecard (consumer experience)

| Dimension | Grade | Notes |
|---|---|---|
| 1:1 fidelity to native names/shapes | **A-** | Predictable transforms; stutter/hash names deduct |
| .NET async affordances | **B+** | Task/CT excellent; AsyncSequence incomplete |
| Lifetime correctness surface | **A-** | Clear IDisposable; heavy but honest |
| IntelliSense / navigability | **C+** | Mega-files + thin XML + noise members |
| Generics usability | **B** | Specializations save CryptoKit; open generics obsolete |
| Protocol / interface usability | **B** | Interfaces + proxies good; empty SB0004 interfaces and Tip-like protocols need companions |
| ObjC binding quality | **B+** | Familiar bgen; huge files |
| Docs (generated + package) | **B** | GUIDEs carry the product; generated XML does not |
| **Overall** | **B+** | Ship-ready with guides; polish backlog is naming, files, sequences |

---

## Concrete exemplars (quick reference)

**Idiomatic success (StoreKit2 purchase):**

```csharp
var products = await Product.ProductsAsync(new[] { "com.app.premium" });
var result = await products[0].PurchaseAsync();
if (result.Tag == Product.PurchaseResult.CaseTag.Success &&
    result.TryGetSuccess(out var verification) &&
    verification.TryGetVerified(out Transaction tx))
{
    await tx.FinishAsync();
}
```

**Generator rough edge (MusicKit constructibility):**

```csharp
// Generated (opaque):
MusicCatalogSearchSuggestionsRequest.Create_C11D4260("daft punk");
// Package shim (idiomatic) — prefer this path in GUIDEs:
MusicCatalogSearchSuggestionsRequest.Create("daft punk", MusicCatalogSearchTypes.Song);
```

**Naming rough edge (StoreKit2):**

```csharp
Transaction.OfferTypeTypeType? offerType = tx.OfferType; // stutter
Transaction.Transactions ents = Transaction.CurrentEntitlementsMethod(productId); // Method suffix
```

**Interop honesty (CryptoKit):**

```csharp
// Prefer:
AES.GCM.Seal(plaintextBytes, key);
// Avoid (obsolete SB0001):
// key.Signature<MyDataProtocol>(data);
```

---

## Bottom line

The generator already delivers a **credible 1:1 Swift→C# surface** with the right lifetime and async fundamentals. Quality feels “compiler output of a careful ABI projector,” not “hand-written SDK.” That is appropriate for the project’s stated goals.

**Highest leverage next steps:** split mega-files, `IAsyncEnumerable` for AsyncSequence, and naming polish (stutter / specialization / zero-arg `Get`). **Keep** GUIDEs and package shims as first-class products — they already convert a B- IntelliSense experience into a B+ consumer experience for Apple frameworks.
)
