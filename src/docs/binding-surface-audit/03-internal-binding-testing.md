# Binding Surface Audit — `internal-binding-testing`

**Scope**: open-source Swift library bindings under `/Users/wojo/Dev/internal-binding-testing`  
**Generator**: `/Users/wojo/Dev/swift-bindings`  
**Artifacts**: committed `*.cs` + `binding-report.json` (generated ~2026-07-09)  
**Runtime evidence**: `results/validate-0.16.0.json` (full sim+device sweep, 0 failures) and per-library `validate-0.17.0-*.json` samples; smoke tests are construction/metadata-heavy, not full product workflows  
**Method**: static read of binding-report SkipTriage + emitted C# surfaces + Program.cs smoke tests. No generator changes.

---

## TL;DR

1. **Compile/runtime gates are green** for all 15 libraries with full smoke suites (0 fails on sim+device at 0.16.0). That proves marshalling and construction, **not** end-to-end product workflows.
2. **Usable today (core workflow works)**: KeychainAccess, PhoneNumberKit, SnapKit (awkward entry), CryptoSwift (AES encrypt path works despite noise), Starscream, Reachability, DeviceKit, SwiftyBeaver (partial).
3. **Usable with major friction**: Alamofire, Kingfisher — headline types exist, but entry-point ergonomics and key overloads are awkward or dead.
4. **Not a product-usable binding**: RxSwift, Swinject, ObjectMapper, XMLCoder — structural shape of the library (generics + closures + PATs) is beyond current projection.
5. **Universal weakness**: smoke tests never exercise the library's reason-for-being (HTTP round-trip, image load, DI resolve, reactive subscribe). Same BindingAudit theme.

---

## 1. Inventory

Coverage columns use binding-report totals. **Public surface lost** is SkipTriage's honest "would a consumer miss this?" count (excludes ExpectedNonPublic where classified).  
**Tests** = latest full-matrix result from `results/validate-0.16.0.json` unless noted. Pass counts are smoke-test assertions, not xUnit cases.

| Library | Types E/T | Members E/T | Skipped members | Public surface lost | Top skip reasons (SkipTriage) | Sim/Device tests |
|---|---|---|---|---|---|---|
| **Alamofire** | 136/144 | 688/689 | 165 | **159** | UnsupportedSignature 39, UnsatisfiedGenericConstraint 26, UnsupportedClosure 25, GenericProtocolConstraint 20, EveryProtocol 9, GenericTypeCallback 9 | ✅ 43/0 |
| **BonMot** | 31/31 | 144/176 | 27 | 25 | UnsupportedSignature, UnsatisfiedGenericConstraint, DuplicateSignature | ✅ 38/0 |
| **CryptoSwift** | 107/107 | 337/514 | 162 | **82** | ModuleInternal 75, UnsupportedSignature 36, UnsupportedType 24 | ✅ 24/0 |
| **DeviceKit** | 7/7 | 100/111 | 3 | 3 | UnsupportedType 3 | ✅ 26/0 |
| **GDPerformanceView** | 13/13 | 57/68 | 11 | ~10 | ModuleInternal 10, UnsupportedSignature 1 | (no full-matrix entry; has Program.cs) |
| **KeychainAccess** | 9/9 | 106/103 | 10 | 10 | DuplicateSignature 4, ObjCMissingNativeSymbol 2, UnsupportedSignature 2 | ✅ 35/0 |
| **Kidoz** | 26/26 | 104/108 | 8 | ~8 | ModuleInternal, UnsupportedType | sim-only test csproj |
| **Kingfisher** | 129/133 | 570/538 | 77 | **80** | GenericTypeCallback 22, UnsupportedClosure 12, UnsatisfiedGenericConstraint 10 | ✅ 38/0 |
| **ObjectMapper** | 21/23 | 85/81 | 54 | **54** | DuplicateSignature 14, GenericProtocolConstraint 12, **MissingWrapperSymbol 12 (Review)** | ✅ 23/0 |
| **PhoneNumberKit** | 36/36 | 233/249 | 18 | 15 | SynthesizedCodable 6, ModuleInternal 3 | ✅ 30/0 |
| **Reachability** | 3/3 | 15/19 | 4 | 4 | UnsupportedSignature 4 | ✅ 15/0 |
| **RxSwift** | 62/62 | 148/180 | 93 | **89** | UnsupportedClosure 29, UnsupportedSignature 28, AnyTypeFallback 11, EveryProtocol 11 | ✅ 36/0 |
| **SnapKit** | 31/31 | 196/143 | 10 | 10 | AnyTypeFallback 4, DuplicateSignature 3, UnsupportedClosure 3 | ✅ 24/0 |
| **Starscream** | 55/55 | 153/148 | 2 | 1 | UnsupportedSignature, ModuleInternal | ✅ 33/0 |
| **SwiftyBeaver** | 23/23 | 120/155 | 30 | 8 | ModuleInternal (most ExpectedNonPublic), AnyTypeFallback, Pattern2InternalTypeReach | ✅ 33/0 |
| **Swinject** | 19/19 | 55/62 | 72 | **72** | UnsupportedSignature 31, AnyTypeFallback 28, UnsupportedClosure 12 | ✅ 28/0 |
| **XMLCoder** | 69/73 | 99/381 | 321 | **171** | ModuleInternal 141, Pattern2InternalTypeReach 93, UnsupportedSignature 20 | ✅ 13/0 |

### Libraries without `binding-report.json`

| Folder | Status |
|---|---|
| **GRDB** | Separate NuGet-shaped project (`SwiftBindings.GRDB.csproj`); xcframework present; not in the sim-matrix. README shows only `new Configuration()` — incomplete sample. |
| **MediaPipe** | Partial: `MediaPipeTasksGenAI.cs` + wrapper xcframework + Program.cs; no binding-report. |
| **Mixpanel** | Partial: `Mixpanel.cs` + wrapper + Program.cs; no binding-report. |

### Project config (typical)

All generated `*.Swift.iOS.csproj` observed:

- `TargetFramework`: `net10.0-ios26.0`
- `SupportedOSPlatformVersion`: `15.0`
- `AllowUnsafeBlocks`, `DisableRuntimeMarshalling`, `GenerateDocumentationFile`
- Wrapper ships as `*SwiftBindings.xcframework`

---

## 2. Core feature usability (deep dive)

Verdict tags: ✅ usable · ⚠️ usable with friction · 🛑 not product-usable

### 2.1 Alamofire — networking + generics + protocols — ⚠️

**What works**

- Value types & helpers: `HTTPMethod.Get/Post/…`, `HTTPHeader`, `HTTPHeaders`, `URLEncoding`, `JSONEncoding`, `AFError` (large enum surface).
- Session entry: `Session.Default` (`Alamofire.cs:40084`).
- Request builders: multiple `Session.Request(IURLConvertible, HTTPMethod, …)` overloads (`:41476+`), plus `Upload` / `StreamRequest`.
- Response bridges: `DataRequest.ResponseData` / `ResponseString` / `ResponseJSON` with C# `Action<…>` completions (`:35070–35178`).
- Cancellation / lifecycle: `Request.Cancel/Suspend/Resume`, progress closures, interceptors (`IRequestAdapter` / `IRequestRetrier`).

**What blocks the idiomatic Swift workflow**

| Issue | Severity | Evidence |
|---|---|---|
| No `string` → `IURLConvertible` sugar | **High** | `Request` requires `IURLConvertible` (`:41476`); C# `string` does not implement it. Consumer must wrap `NSUrl` or hand-roll a conformer — not `AF.request("https://…")`. |
| Decodable / async-serializing path dead | **High** | `DataRequest.responseDecodable`, `serializingDecodable`, `publishDecodable` → UnsupportedClosure / GenericProtocolConstraint (`binding-report` UnsupportedCommentDrops). |
| Combine publishers pruned | Medium | Combine refs classified under SwiftUIConstraint; intentional. |
| `ResponseJSON` projects `Swift.AnyType` | Medium | `:35178` — JSON body not typed as `object`/`NSObject`. |
| PAT serializer protocols | Medium | `IDataResponseSerializerProtocol<T>` exists but `EveryProtocolConformanceSkipped` for proxies; custom C# serializers hard. |
| Closure-tombstone adapters | Medium | `Adapter`/`Retrier`/`Interceptor` inits wrapped as ClosureParamTombstone (SB0005) — reachable but unusable with real closures. |

**Smoke tests** (`Program.cs`): metadata + constructors + property getters + `Session.Default` accessors. **No HTTP round-trip.**

**Developer answer**: can assemble headers/encoding and *probably* fire a request if they invent `IURLConvertible` + use `ResponseString`/`ResponseData`. Cannot do Alamofire's flagship Codable/async API from C#.

---

### 2.2 CryptoSwift — crypto primitives — ✅ / ⚠️

**What works**

- Core types: `AES`, `SHA1`/`SHA2`/`SHA3`, `MD5`, `HMAC`, `CBC`/`ECB`, `Blowfish`, `ChaCha20`, `Rabbit`, `Digest`, RSA encrypt/decrypt/sign.
- AES construction: `new AES(keyBytes, IBlockMode, Padding)` (`:7193`), `new AES(string key, string iv, Padding)` (`:7242`).
- **One-shot encrypt/decrypt present**: `AES.Encrypt(IEnumerable<byte>)` / `Decrypt` (`:7300`, `:7340`) — not only MakeEncryptor.
- Streaming path: `MakeEncryptor()` / `MakeDecryptor()` → `ICryptorAndUpdatable` (`:7383`).
- RSA: `Encrypt`/`Decrypt` with ArraySlice normalization wrappers.

**What hurts**

| Issue | Severity | Evidence |
|---|---|---|
| Noise density from ModuleInternal | Low–Med | 75 ModuleInternal skips — internal hash state props (`AES.T0`, `SHA2.accumulated`, …). Correct pruning, but 118 `// Unsupported:` comments in the .cs. |
| Open generic / ArraySlice encrypt overloads dropped | Medium | Comments at `:7232–7234` for placeholder-type overloads; usable `IEnumerable<byte>` path remains. |
| Existential `IBlockMode` ctor fragile | **High** (runtime) | Program.cs skips `AES(byte[], ECB, Padding)`: "ExistentialContainer boxing crashes process (known limitation)" — NativeAOT SIGKILL. |
| Protocol `ICipher.Encrypt(Swift.AnyType)` | Medium | Dual AnyType + `IEnumerable<byte>` overloads on cipher interface (`:4215–4224`). |

**Smoke tests**: construct digests/AES/HMAC; **no encrypt→decrypt round-trip asserted.**

**Developer answer**: yes for AES-CBC string-key encrypt/decrypt and digests/HMAC; avoid open existential block-mode construction on device until fixed.

---

### 2.3 SnapKit — DSL / constraints — ⚠️

**What works**

- Entry: `UIView.GetSnp()` extension (`:9540`) → `ConstraintViewDSL`.
- DSL: `MakeConstraints` / `RemakeConstraints` / `UpdateConstraints` / `RemoveConstraints` with `Action<ConstraintMaker>` (`:7748+`).
- Maker chain: `ConstraintMaker.Left/Top/Bottom/Right/…` → `ConstraintMakerExtendable` → `EqualTo` / `LessThanOrEqualTo` / `GreaterThanOrEqualTo` / `EqualToSuperview` (`:395+`, `:2411+`).
- Priorities: `ConstraintPriority.Required/High/Medium/Low`, updates on installed constraints.

**What hurts**

| Issue | Severity | Evidence |
|---|---|---|
| Closure-form `equalToSuperview { }` dead | Medium | UnsupportedClosure ×3 on ConstraintMakerRelatable (`binding-report`). Parameterless `EqualToSuperview()` still works. |
| Naming stutter | **High** (polish) | `GetequalToSuperview()` (`:545`) — PascalCase + lowercase `equal` collision from Swift `equalToSuperview` noun rule. |
| `target` AnyType | Low | ConstraintViewDSL/GuideDSL/SupportDSL `target` → AnyType. |
| Verbose vs Swift DSL | Medium | `view.GetSnp().MakeConstraints(make => { make.Left.EqualTo(…); })` not `view.snp.makeConstraints { $0.left.equalToSuperview() }`. |

**Smoke tests**: priorities + LayoutConstraint only — **no real Auto Layout install.**

**Developer answer**: core pin-to-superview/edges workflow is expressible; expect awkward names and no trailing-closure sugar.

---

### 2.4 KeychainAccess — simple practical API — ✅

**What works (product path)**

- Ctors: `Keychain()`, `Keychain(service)`, `Keychain(service, accessGroup)`, server/protocol variants (`:3068–3159`).
- CRUD: `Get` / `GetString` / `GetData` / `Set(string|byte[])` / `Remove` / `RemoveAll` (`:3471–3716`).
- Builder chain: `WithAccessibility`, `WithSynchronizable`, `WithLabel`, `WithComment`, `WithAuthenticationUI`, `WithAuthenticationPrompt`.
- Enums: `ItemClass`, `ProtocolType`, `AuthenticationType`, `Accessibility`, `Status` with descriptions.
- OS gates: `[SupportedOSPlatform]` on auth APIs (`:3387+`).

**Gaps**

| Issue | Severity | Evidence |
|---|---|---|
| Closure `get` overload skipped | Low | UnsupportedClosure on handler form. |
| `allKeys` UnsatisfiedGenericConstraint | Medium | enumeration of keys missing. |
| Subscript / some Dup signatures | Low | 4 DuplicateSignature. |
| Shared password APIs awkward | Low | `GetSharedPassword` variants with `AnyError`. |

**Smoke tests**: **include Set/Get string CRUD** (`Program.cs:305+`) — one of the few libraries with a real product path in tests.

**Developer answer**: yes — this is the showcase "simple library binds cleanly."

---

### 2.5 PhoneNumberKit — ✅

**What works**

- `PhoneNumberUtility()` / parse: `Parse(numberString, region, ignoreType)` (`:6810`), `Format(phoneNumber, formatType, prefix)` (`:7208`).
- Models: `PhoneNumber`, `PhoneNumberFormat`, `PhoneNumberType`, `PhoneNumberError`.
- UI: `PhoneNumberTextField`, `CountryCodePickerViewController`, options structs.
- `PartialFormatter.FormatPartial`.

**Gaps**

| Issue | Severity | Evidence |
|---|---|---|
| SynthesizedCodable on metadata types | Low | expected. |
| Some UI delegate/AnyType | Low | `PhoneNumberTextField.delegate` AnyType fallback comment. |
| SwiftUI textContentType | — | intentional skip. |

**Developer answer**: parse/format core path is solid for C# consumers; UI controls usable with UIKit.

---

### 2.6 Kingfisher — image loading — ⚠️

*(Also audited in BindingAudit/Kingfisher.md for the packages set; this copy matches that shape.)*

**What works**

- Types: `KingfisherManager`, `ImageCache`, `ImageDownloader`, processors (`Blur`, `RoundCorner`, …), `KF.Builder` chain (`Downloader`, `DownloadPriority`, …).
- Some set paths on builder: `KF.Builder.Set(UIImageView)` (`:18076`).
- Cache store/remove APIs on `DiskStorage`/`MemoryStorage` backends.
- SwiftUI views → bridge file (`Kingfisher.SwiftUIBridge.*`); not re-litigated.

**What blocks the idiomatic path**

| Issue | Severity | Evidence |
|---|---|---|
| `KingfisherWrapper.setImage` mass-dead | **Critical** for `.kf` ergonomics | ~20 GenericTypeCallback / closure-in-generic skips (`:21032+`). Swift's `imageView.kf.setImage(with:)` is gone. |
| Builder is the alternate | Medium | `KF.Builder` partially works; success/failure/progress **delegates** UnsatisfiedGenericConstraint (`:17918–17920`). |
| `RetrieveImageResult.data` missing | Medium | closure getter unsupported (`:23687`). |
| MissingWrapperSymbol Review | Medium | `Delegate.call` / `callAsFunction` stripped wrappers. |
| ImageCache storage props | Medium | memory/disk storage getters UnsatisfiedGenericConstraint. |

**Smoke tests**: metadata + construction of manager/cache/processors — **no network image load.**

**Developer answer**: possible via `KingfisherManager` / `KF.Builder` with reduced callback fidelity; **not** the Swift one-liner UX. Prefer Nuke (packages) if both available.

---

### 2.7 RxSwift — reactive — 🛑

**What exists**

- Skeleton types: `Observable<T>`, `PublishSubject`/`BehaviorSubject`/`ReplaySubject`/`AsyncSubject`, schedulers, disposables, `PrimitiveSequence`, `Infallible`, `Event`/`MaybeEvent`.

**What is missing for any real use**

| Issue | Severity | Evidence |
|---|---|---|
| `Subscribe` on Observable/Subjects | **Critical** | GenericProtocolConstraint — PAT observer (`:10600`, subjects). |
| Event case constructors stripped | **Critical** | `Event.Next`/`Error`, `MaybeEvent.Success` — MissingWrapperSymbol-style generic enum payloads (`:10989+`). |
| Operator surface | **Critical** | UnsupportedClosure dominates operators (`combineLatest`, `create`, `catch`, …). |
| AnyType leaks | High | `PrimitiveSequence` property, `Event.event`, etc. |
| `IObservableType.Subscribe(Swift.AnyType)` | High | protocol surface unusable (`:7876`). |

**Developer answer**: do not ship as a reactive library for C#. Use `System.Reactive` / async streams. Binding is a structural stress test only (and a useful generator canary).

---

### 2.8 Swinject / ObjectMapper — DI / mapping — 🛑

#### Swinject

- `Container`, `Assembler`, `ObjectScope`, storage classes **construct**.
- **Every** `Container.Register` (factory closures) and essentially all `Resolve` overloads skipped (`:1004–1263`).
- Result: empty DI container with no register/resolve product API.

#### ObjectMapper

- `Map`, `Mapper`, transforms partially emitted.
- **Core `Mapper.map` / `mapArray` / `mapDictionary`**: 12× **MissingWrapperSymbol** (SkipTriage Review) — symbols not registered by wrapper-emit (`:4593+`).
- Remaining map overloads: DuplicateSignature + GenericProtocolConstraint.
- PAT transforms (`DictionaryTransform`, `EnumTransform`) IndeterminatePwtShape.

**Developer answer**: neither is usable for their purpose. Prefer System.Text.Json / DI containers on the .NET side. ObjectMapper's MissingWrapperSymbol cluster is a **generator defect to fix** even if product use stays low.

---

## 3. C# quality defects

### 3.1 Critical / High

| Defect | Severity | Where |
|---|---|---|
| **MissingWrapperSymbol on core APIs** | Critical | ObjectMapper `Mapper.map*`; Kingfisher `Delegate.call*` |
| **GenericTypeCallback kills constrained-extension surface** | Critical | Kingfisher `KingfisherWrapper.setImage`; Alamofire constrained extensions |
| **Existential protocol args crash (NativeAOT)** | High | CryptoSwift `AES(IEnumerable<byte>, IBlockMode, …)` Program.cs skip |
| **No stdlib protocol sugar for common types** | High | Alamofire `string` ↛ `IURLConvertible`; consumers stuck |
| **Naming stutter `GetequalToSuperview`** | High | SnapKit `:545` |
| **Subscribe/Register/map product paths absent** | Critical (product) | RxSwift / Swinject / ObjectMapper |

### 3.2 Medium

| Defect | Severity | Notes |
|---|---|---|
| **AnyType leaks** | Medium | Alamofire `ResponseJSON` → `DataResponse<Swift.AnyType,…>`; Swinject storage `instance`; RxSwift Event; SnapKit `target` |
| **DuplicateSignature silent drops** | Medium | KeychainAccess (4), SnapKit (3), ObjectMapper (14), Kingfisher (7). **Classified (session 03): NOT protocol label-blindness** (that instance-method bug is fixed — `ProtocolMethodDisambiguator`). These are **class-side** rows of a different family → **session 08** naming policy: KeychainAccess = subscript ×3 + a projected ctor collision (constructors can't be renamed); ObjectMapper = `Map.value` primary-key collapse (12× method-generic shapes whose keys are identical *including labels* — genuine generic-erasure, not a label difference) + a subscript. Class label-only *method* overloads already survive via the class path's label-inclusive primary key + numeric suffix (`Configure`/`Configure2`, locked by `DuplicateSignatureDisambiguationTests.TestNonProtocolLabelOnlyOverloadsBothSurvive`); the residue here is subscripts/ctors/method-generics, which are pure-naming/expressibility, not silent-wrong-dispatch. |
| **`// Unsupported:` density** | Medium | Alamofire ~150, CryptoSwift ~118, Kingfisher ~71, RxSwift ~72 — fine for generators, scary for package consumers if shipped raw |
| **IDisposable everywhere on value-ish types** | Medium | Correct for non-blittable frozen structs (`HTTPMethod`, `PhoneNumber`) but verbose (`using var method = HTTPMethod.Get`) |
| **Swift.SwiftString / Swift.AnyType in public signatures** | Medium | Response handlers and cipher protocols |
| **Default-parameter overload explosion** | Low–Med | Session.Request has many near-identical overloads — intentional generator strategy, noisy IntelliSense |
| **`GetEqualToSuperview` vs `EqualToSuperview`** | Medium | dual names for optional-arg collapse |

### 3.3 Low / polish

- ModuleInternal pruning comments for CryptoSwift internals (correct, noisy).
- `AlamofireAlamofire` / module-prefixed stutter rare here vs some Apple frameworks.
- TFM pin `net10.0-ios26.0` is correct for current generator; consumers on older workloads need matching workload install.
- Tests claim "success" without product-path coverage — process defect more than generator defect.

---

## 4. Cross-cutting generator patterns

Aligned with BindingAudit themes; this set stresses **third-party SPM libraries** harder on closures/generics.

| Pattern | Impact in this set | Exemplars |
|---|---|---|
| **UnsupportedClosure** | High | Alamofire response/map; RxSwift operators; Swinject factories; SnapKit closure-superview |
| **GenericTypeCallback / closure-in-generic** | High | KingfisherWrapper.setImage; Alamofire AuthenticationInterceptor |
| **GenericProtocolConstraint / PAT** | High | RxSwift Subscribe; ObjectMapper Map.value; Alamofire Decodable serializers |
| **UnsatisfiedGenericConstraint** | High | Alamofire progress publishers; Kingfisher cache storage |
| **EveryProtocolConformanceSkipped** | Medium–High | Serializer/delegate proxies; RxSwift ObservableType; XMLCoder dynamic nodes |
| **ModuleInternal / Pattern2InternalTypeReach** | High volume, often correct | CryptoSwift, XMLCoder, SwiftyBeaver — inflates skip counts |
| **MissingWrapperSymbol** | **Defect** | ObjectMapper map*, Kingfisher Delegate |
| **DuplicateSignature** | Medium | Overload collapse after projection |
| **AnyTypeFallback** | Medium | Unresolved Optionals, existentials, Sendable dict values |
| **IndeterminatePwtShape** | Low count, high bite | ObjectMapper EnumTransform/DictionaryTransform |
| **NonBlittableCallConvSwift** | Low | Alamofire JSONResponseSerializer init |
| **SynthesizedCodable** | Expected | PhoneNumber metadata, Empty types |
| **SwiftUIView / SwiftUIConstraint** | Expected — not litigated | Kingfisher KFImage; Combine publishers |
| **ClosureParamTombstone (SB0005)** | Medium | Alamofire Adapter/Retrier — dead but not stripped |
| **ArraySliceNormalization** | Positive | CryptoSwift RSA |
| **ConstrainedExtensionEmitter scope** | Medium | only zero-arg sync methods; Kingfisher/Alamofire extensions mostly out of scope |

### Comparison to BindingAudit (packages)

- Same Kingfisher story: builder partial, `.kf.setImage` dead.
- Third-party set adds **worse** Rx/DI/JSON-mapper failures (packages set leaned Apple frameworks + Lottie/Nuke).
- KeychainAccess here is the clean counterexample BindingAudit wanted for "simple API → high usability."
- XMLCoder illustrates ModuleInternal accounting: 321 skipped members but only 171 PublicSurfaceLost — still a weak consumer package.

---

## 5. Ranked recommendations

### Generator (ordered by consumer impact)

1. **Critical — Fix MissingWrapperSymbol emission** for method-generic wrappers (ObjectMapper `map*`, Kingfisher `Delegate.call*`). Fail-closed at generate time if a claimed wrapper entry point is not registered.  
2. **Critical — Expand GenericTypeCallback / UnmanagedCallersOnly-in-generic** path so constrained-extension methods with escaping closures can emit (Kingfisher setImage, Alamofire validate/adapt families).  
3. **High — Protocol sugar / projection for stdlib conformances** used as parameters: emit `Request(string url, …)` overloads (or auto-box `string`/`NSUrl` into `IURLConvertible`) so Alamofire is callable without ceremony.  
4. **High — ExistentialContainer boxing stability on NativeAOT** for protocol-typed ctor params (CryptoSwift `IBlockMode`). Add BindingTests repro; treat Program.cs skip as a bug not a feature.  
5. **High — PAT / associated-type projection strategy** (even if "facade only"): document and optionally emit non-generic bridges for Subscribe/Resolve-like APIs, or officially mark RxSwift/Swinject as unsupported library classes.  
6. **Medium — Naming**: fix zero-arg rename that produces `GetequalToSuperview`; enforce `GetEqualToSuperview` or keep `EqualToSuperview()` only.  
7. **Medium — DuplicateSignature**: prefer disambiguating suffixes over silent drop for keychain/map overload sets.  
8. **Medium — AnyType in public JSON/response APIs**: project `Any` JSON to `NSObject`/`object` where ObjC-bridged.  
9. **Low — Comment policy for packages**: strip or `#if DEBUG` the wall of `// Unsupported:` when packing NuGets; keep full report in binding-report.json.

### Package / sample polish

1. **Deepen smoke tests** to one real workflow per green library:  
   - KeychainAccess: already has Set/Get — keep as golden.  
   - CryptoSwift: encrypt/decrypt round-trip.  
   - PhoneNumberKit: parse + format assert.  
   - SnapKit: addSubview + MakeConstraints + layoutIfNeeded.  
   - Alamofire: GET httpbin with ResponseString (with URL helper).  
   - Kingfisher: KF.Builder set into UIImageView from a local file provider (no network flakiness).  
2. **Do not market** RxSwift / Swinject / ObjectMapper / XMLCoder as supported bindings; label as generator stress fixtures.  
3. **Ship thin C# facades** (optional package layer) for Alamofire URL strings and SnapKit `view.Snp()` property alias if generator sugar is delayed.  
4. **Finish or drop** GRDB / MediaPipe / Mixpanel partials so the matrix is honest.  
5. **Track PublicSurfaceLost** per library in CI (already in SkipTriage) — fail on regression, not on ModuleInternal noise.

---

## 6. Per-library verdict card (quick reference)

| Library | Verdict | One-line reason |
|---|---|---|
| KeychainAccess | ✅ | Full CRUD + builders; best in set |
| PhoneNumberKit | ✅ | Parse/format/UI present |
| CryptoSwift | ✅/⚠️ | Encrypt works; existential ctor crash on device |
| SnapKit | ⚠️ | DSL works via GetSnp; naming/closure gaps |
| Starscream | ✅ (light) | Near-complete small surface |
| Reachability / DeviceKit | ✅ | Small APIs bind cleanly |
| Alamofire | ⚠️ | Session/request/response exist; URL sugar + Codable dead |
| Kingfisher | ⚠️ | Manager/builder only; `.kf.setImage` dead |
| BonMot / SwiftyBeaver | ⚠️ | Partial; internals pruned |
| XMLCoder | 🛑 | Massive internal surface; low public emit |
| ObjectMapper | 🛑 | map* MissingWrapperSymbol |
| Swinject | 🛑 | No register/resolve |
| RxSwift | 🛑 | No subscribe/operators |
| GDPerformanceView / Kidoz | ⚠️/partial | Small / mixed ObjC |
| GRDB / MediaPipe / Mixpanel | 🔶 | Incomplete matrix membership |

---

## 7. Appendix — source anchors

| Claim | Location |
|---|---|
| Alamofire Session.Default | `Alamofire/Alamofire.cs:40084` |
| Alamofire Request(IURLConvertible,…) | `Alamofire/Alamofire.cs:41476` |
| Alamofire ResponseString/JSON | `Alamofire/Alamofire.cs:35124`, `:35178` |
| Alamofire SkipTriage | `Alamofire/binding-report.json:2399` |
| AES Encrypt | `CryptoSwift/CryptoSwift.cs:7300` |
| AES IBlockMode skip note | `CryptoSwift/Program.cs:177–181` |
| SnapKit GetSnp | `SnapKit/SnapKit.cs:9540` |
| SnapKit GetequalToSuperview | `SnapKit/SnapKit.cs:545` |
| Keychain Set/Get | `KeychainAccess/KeychainAccess.cs:3471`, `:3601` |
| PhoneNumberUtility.Parse | `PhoneNumberKit/PhoneNumberKit.cs:6810` |
| Kingfisher setImage dead comments | `Kingfisher/Kingfisher.cs:21032+` |
| Swinject Register dead | `Swinject/Swinject.cs:1004+` |
| ObjectMapper map MissingWrapperSymbol | `ObjectMapper/ObjectMapper.cs:4593+`, binding-report SkipTriage Review |
| Full matrix green | `results/validate-0.16.0.json` |
| TFM / min OS | `Alamofire/Alamofire.Swift.iOS.csproj:11–18` |

---

*End of audit. Companion index: `src/docs/BindingAudit/_SUMMARY.md` (packages set). This document is the internal-binding-testing secondary corpus only.*
