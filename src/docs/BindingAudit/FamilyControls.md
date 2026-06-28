# FamilyControls — Binding Audit

- **Package**: SwiftBindings.Apple.FamilyControls v26.2.8   **Mode**: apple   **TFM(s)**: net10.0-ios26.2
- **Native**: Apple FamilyControls.framework (iOS 15.0+, macOS 13.0+)
- **Audited at**: swift-dotnet-packages `1e8c27a`, generated 2026-06-27

## Verdict

Near-clean small binding. The authorization flow (`AuthorizationCenter.Shared`, `RequestAuthorizationAsync`, `AuthorizationStatus`, `FamilyActivitySelection`) is fully surfaced and ergonomic. All three SwiftUI-view skips are correctly resolved: `FamilyActivityPicker` has a rich generated `FamilyActivityPickerSession` bridge (create / UIViewController / selection JSON round-trip / lifecycle hooks), while `FamilyActivityIconView` / `FamilyActivityTitleView` are decorative and correctly omitted. The single real gap is a missing async overload for `RevokeAuthorization` (only the iOS-15 callback form appears; the iOS 16+ async form is absent from both wrapper and api-manifest). Token-set properties (`ApplicationTokens`, `CategoryTokens`, `WebDomainTokens`) degrade to `SwiftSet<IntPtr>` — opaque by Apple design, not a generator regression. Test depth is strong on construction and metadata; async authorization and revocation are untested (entitlement barrier but error-path coverage is viable).

---

## 1. Coverage

### Totals
| Dimension | Emitted | Total | % |
|---|---|---|---|
| Types | 6 | 9 | 67% |
| Declared members | 21 | 29 | 72% |
| Synthesized members | +16 | — | — |
| Total C# surface | 37 | — | — |

### Skip-reason breakdown

| Reason | Count | Classification |
|---|---|---|
| SwiftUIView | 3 types | (a) Correctly excluded — see bridge note below |
| SynthesizedCodable | 2 methods | (a) Correctly excluded — compensated by `EncodeToJson`/`DecodeFromJson` |
| ModuleInternal | 1 property | (a) Correctly excluded (`@Published` projected `$authorizationStatus`) |
| UnsupportedSignature | 1 property | (b) Real gap — see below |

**SwiftUI bridge detail**: Of the 3 view skips, `FamilyActivityPicker` has `BridgeStatus: Generated` — a full `FamilyActivityPickerSession` class ships in `FamilyControls.SwiftUIBridge.cs` with create, `ViewController`, `UpdateSelection`, `ReadSelection` (JSON round-trip via `FamilyActivitySelection.EncodeToJson`/`DecodeFromJson`), lifecycle callbacks, frame/padding/color/font setters, and `BindTo<T>`. `FamilyActivityIconView` and `FamilyActivityTitleView` have `BridgeStatus: Skipped` — they are icon/title decorators, not interactive, and consumers don't need them directly.

**SynthesizedCodable workaround**: The pruned `encode(to:)` and `init(from:)` are replaced by synthesized `EncodeToJson()` (line 1116) / `DecodeFromJson(byte[])` (line 1149) wrappers backed by a Swift `@_cdecl` JSONEncoder/JSONDecoder shim. This is a complete functional replacement.

### Real gap

**`FamilyControlsError.errorUserInfo`** (`UnsupportedSignature`): The property returns `[String: Any]` — a heterogeneous Swift dictionary whose value type (`Any`) has no C# equivalent the generator can resolve. Worth fixing in the generator (heterogeneous dictionary projection to `Dictionary<string, object?>`) but the impact here is minimal: `GetErrorDescription()` already covers the primary consumer need.

### Missing async overload — `RevokeAuthorizationAsync`

Apple's `AuthorizationCenter.revokeAuthorization() async throws` is available on iOS 16+ (parallel to `requestAuthorization(for:) async throws`). The binding generates `RequestAuthorizationAsync(FamilyControlsMember, CancellationToken)` for the latter but only the callback form `RevokeAuthorization(Action<SwiftResult<SwiftVoid, ExistentialContainer1>>)` for revocation — no `RevokeAuthorizationAsync`. The api-manifest lists `RevokeAuthorization(Swift.AnyType)` only; no async symbol appears. This is a gap: the idiomatic C# revocation path forces consumers onto the callback form. **Verify**: check `FamilyControls.swiftinterface` for `revokeAuthorization() async throws`; if present, this needs an async wrapper alongside `requestAuthorization`.

### Prioritized generator unlocks

| # | Swift API | Reason | Value | Effort |
|---|---|---|---|---|
| 1 | `revokeAuthorization() async throws` | Async wrapper missing | High — mirrors RequestAuthorizationAsync | Low (same pattern already generated for requestAuthorization) |
| 2 | `FamilyControlsError.errorUserInfo: [String: Any]` | Heterogeneous dict | Low | Medium |

---

## 2. C# Quality

**Naming / shape**: Clean. `AuthorizationCenter`, `AuthorizationStatus`, `FamilyActivitySelection`, `FamilyControlsError`, `FamilyControlsMember` — all PascalCase, no mangled names. Namespacing is flat `FamilyControls`.

**`AuthorizationStatus` as class** (lines 63–475): Swift resilient enum → C# class with static singleton cases (`NotDetermined`, `Denied`, `Approved`), nested `CaseTag` enum (uint), `Tag` property, `Description`, `RawValue`, `FromRawValue(long)`, `IEquatable<T>`, and `ISwiftHashable` conformance with proper `GetHashCode`. This is the standard resilient-enum pattern; consumers must do `status.Tag == AuthorizationStatus.CaseTag.NotDetermined` instead of a C# `switch(status)` on a plain enum. Workable but unusual for C# callers.

**`RequestAuthorizationAsync`** (line 1583): iOS 16+, `Task`-returning, `CancellationToken` support, typed exception dispatch via `_SbwModuleErrorRegistry_FamilyControls` → `SwiftException<FamilyControlsError>`. Idiomatic. Good.

**`RequestAuthorization` callback form** (line 1452): iOS 15 path, takes `Action<SwiftResult<SwiftVoid, ExistentialContainer1>>`. The `ExistentialContainer1` type in the action signature leaks interop internals; a consumer checking success vs. failure must know about `SwiftResult`. This is the legacy overload and most callers should use `RequestAuthorizationAsync`; a doc comment directing them there would help.

**Token properties type** (lines 599, 677, 755):
- `ApplicationTokens`: `SwiftSet<IntPtr>` — `ApplicationToken` is an opaque FamilyControls/ManagedSettings struct with no public accessor surface; `IntPtr` is the correct fallback.
- `CategoryTokens`: `SwiftSet<IntPtr>` — same (`ActivityCategoryToken`).
- `WebDomainTokens`: `SwiftSet<IntPtr>` — same (`WebDomainToken`).
- Contrast: `Applications`, `Categories`, `WebDomains` (read-only) are `IReadOnlySet<Swift.ManagedSettings.{Application,ActivityCategory,WebDomain}>` — properly typed.

The token sets are read-write (get + set both generated), which is correct Swift API behaviour. Elements can be held as `IntPtr` handles and passed back to Swift APIs, but C# can't inspect them. This is by Apple design — the tokens are privacy-preserving opaque identifiers.

**`FamilyActivitySelection.GetHashCode()` returns 0** (line 937): `FamilyActivitySelection` conforms to `Equatable` but not `Hashable`; the generator correctly produces `return 0`. This degenerates all instances into one hash bucket. Using `FamilyActivitySelection` as a `Dictionary` key or in a `HashSet` is O(n) but functionally correct. A `<remarks>` comment would inform consumers.

**Lifetime / `IDisposable`**:
- `AuthorizationStatus`: `IDisposable` with `_isCachedSingleton` guard — cached singletons don't dispose the backing buffer. Correct.
- `FamilyActivitySelection`: `IDisposable`. No `_isCachedSingleton` needed (not a singleton-value type). Correct.
- `AuthorizationCenter`: ARC-managed class, `IDisposable` with finalizer fallback, proper identity `Equals`/`GetHashCode` via cached handle hash. Correct.
- `FamilyActivityPickerSession`: `IDisposable` with finalizer, `Dispose(bool)` pattern, GCHandle array cleanup delegated to native `FreeGCHandles` trampoline. Correct.

**`AuthorizationCenter.Shared` property** (line 1235): Returns a **new** C# wrapper per call (no caching at the managed layer). `Equals`/`GetHashCode` compare by Swift pointer identity so two calls to `.Shared` are `Equals`-equal, but callers should be aware they get distinct C# objects. The typed `AuthorizationStatus` property on `AuthorizationCenter` (line 1277) returns a fresh `AuthorizationStatus` value on each access — correct (it's a KVO-observed property in Swift, so reading it live is appropriate).

**Nothing outright broken or unusable.** The auth flow is callable end-to-end from C#.

---

## 3. Test Coverage

**Test count**: 16 named cases across `tests/Tests.cs` (numbered 1–12 + 15–16; no tests 13/14 in the file).

| Test | API touched | Depth |
|---|---|---|
| 1 | `AuthorizationCenter.Shared` | Weak — non-null only |
| 2 | `AuthorizationStatus` singletons (3 cases) | Weak — non-null only |
| 3 | `AuthorizationStatus.CaseTag` uint values | Strong — value assertions |
| 4 | `AuthorizationStatus.NotDetermined.Tag` round-trip | Strong — Tag property ABI |
| 5 | `FamilyControlsError` integer values | Strong — value assertions |
| 6 | `FamilyControlsError.GetErrorDescription()` | Medium — call + null-tolerance |
| 7 | `FamilyControlsMember` integer values | Strong — value assertions |
| 8 | `FamilyControlsMember.GetDescription()` | Medium — non-empty assertion |
| 9 | `FamilyActivitySelection()` no-arg ctor | Medium — non-null |
| 10 | `FamilyActivitySelection(bool)` ctor + `IncludeEntireCategory` | Strong — round-trip bool |
| 11 | `FamilyActivitySelection` equality (`==`/`!=`) | Strong — two default instances |
| 12 ×3 | Metadata for `AuthorizationStatus`, `FamilyActivitySelection`, `AuthorizationCenter` | Weak — non-zero handle |
| 15 | `AuthorizationCenter.AuthorizationStatus` property | Medium — non-null, logs tag |
| 16 | `FamilyActivityPickerSession.Create` + `ViewController` + `ReadSelection` JSON round-trip | **Strong** — multi-step bridge |

**Untested surface (significant)**:

| Untested | Why it matters |
|---|---|
| `RequestAuthorizationAsync` (iOS 16+) | Core async auth flow; only error-path is viable without entitlement |
| `RequestAuthorization` (callback, iOS 15) | Callback ABI |
| `RevokeAuthorization` (callback, iOS 15) | Revoke flow |
| `RevokeAuthorizationAsync` (if present) | See §1 gap |
| `ApplicationTokens` / `CategoryTokens` / `WebDomainTokens` read + write | `SwiftSet<IntPtr>` set round-trip |
| `Applications` / `Categories` / `WebDomains` (IReadOnlySet) | Cross-framework type resolution |
| `AuthorizationStatus.Description` + `RawValue` | String + int round-trip |
| `AuthorizationStatus.FromRawValue(long)` | Failable init path |
| `AuthorizationStatus.Equals`/`==`/`!=` | Equality ABI |
| `FamilyActivityPickerSession.UpdateSelection` | Selection mutation |
| `FamilyActivityPickerSession.SetFrame`, lifecycle callbacks | Bridge modifiers |

**Recommended additions**:

1. `RequestAuthorizationAsync` — trigger with `FamilyControlsMember.Individual`, verify `SwiftException<FamilyControlsError>` is thrown (entitlement absent → `.restricted` or `.unavailable`). This is the single most important untested path.
2. `AuthorizationStatus.FromRawValue(0)` → `NotDetermined`; `FromRawValue(99)` → `null`. Exercises the failable-init path.
3. `AuthorizationStatus.Equals` / `==` between the same singleton case — `NotDetermined == NotDetermined`.
4. `FamilyActivitySelection` token-set round-trip: read `ApplicationTokens` on a default selection, verify `Count == 0`.
5. `FamilyActivityPickerSession.UpdateSelection` — create a session with `includeEntireCategory: false`, update to `includeEntireCategory: true`, `ReadSelection` and assert.

---

## Action Items

| # | Dimension | Finding | Recommendation | Effort | Value |
|---|---|---|---|---|---|
| 1 | Coverage | `RevokeAuthorizationAsync` absent from binding — only callback form generated | Verify `revokeAuthorization() async throws` exists in `FamilyControls.swiftinterface`; if so, add async wrapper (same shape as `RequestAuthorizationAsync`) | Low | High |
| 2 | C# Quality | `RequestAuthorization` callback form leaks `ExistentialContainer1` in action signature | Add `<remarks>` directing iOS 15 callers to `RequestAuthorizationAsync` on iOS 16+ | Trivial | Low |
| 3 | C# Quality | `FamilyActivitySelection.GetHashCode()` returns 0 with no explanation | Add `<remarks>` noting `FamilyActivitySelection` is `Equatable` but not `Hashable`; O(n) hash behaviour | Trivial | Low |
| 4 | Tests | `RequestAuthorizationAsync` completely untested | Add test: invoke without entitlement, assert `SwiftException<FamilyControlsError>` thrown | Low | High |
| 5 | Tests | `AuthorizationStatus.FromRawValue` untested | Add test: `FromRawValue(0)` → `NotDetermined`, `FromRawValue(99)` → null | Low | Medium |
| 6 | Tests | Token-set and typed-set properties untested | Add test: default `FamilyActivitySelection` has empty `ApplicationTokens` and `Applications` | Low | Medium |
| 7 | Coverage | `FamilyControlsError.errorUserInfo: [String: Any]` — UnsupportedSignature | Generator unlock: heterogeneous dict projection; low priority for this API | Medium | Low |
