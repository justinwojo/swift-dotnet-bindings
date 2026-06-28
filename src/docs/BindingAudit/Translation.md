# Translation — Binding Audit

- **Package**: SwiftBindings.Apple.Translation v26.2.8   **Mode**: apple   **TFM(s)**: net10.0-ios26.2
- **Native**: Apple Translation.framework (iOS 18.0 / macOS 15.0 / macCatalyst 26.0)
- **Audited at**: swift-dotnet-packages main 1e8c27a / sdk main 8dcc3032, generated 2026-06-27

## Verdict

Near-perfect coverage: 9/9 types (100%), 43/43 members (100%), one correctly-excluded operator skip. The async translate flow — `TranslateAsync`, `TranslationsAsync`, `BatchResponse` as `IAsyncEnumerable<Response>`, `PrepareTranslationAsync` — is all correctly wired as C# async/Task. The single biggest weakness is tests: all async paths (the binding's main value proposition) have zero runtime coverage. No generator unlocks are needed.

## 1. Coverage

| | Types | Members | Synthesized |
|---|---|---|---|
| **Emitted** | 9 | 43 | 29 |
| **Skipped** | 0 | 1 | — |
| **Total** | 9 | 43+1 | — |
| **%** | 100% | 100% | — |

### Skipped items

| Kind | Name | Type | Reason | Details |
|---|---|---|---|---|
| Operator | `~=` | `TranslationError` | UnsupportedType | "Operator '~=' has no C# equivalent." |

**Classification: (a) correctly excluded.** Swift's `~=` is a pattern-matching operator used in `switch` statements to enable custom case matching. C# uses `==` / `switch`/`when` clauses for the same purpose — a `~=` operator has no natural C# mapping and consumers would never expect it. Zero practical impact.

### Surface inventory

| Type | Kind | Notes |
|---|---|---|
| `LanguageAvailability` | class (ARC) | Availability query; public ctor; `GetSupportedLanguagesAsync`, `StatusMethodAsync` (×2 overloads); nested `Status` enum |
| `LanguageAvailability.Status` | enum | Installed=0, Supported=1, Unsupported=2 |
| `TranslationError` | struct (ISwiftStruct, IDisposable) | 8 static singleton properties; `ErrorDescription`/`FailureReason` string?; typed error registry |
| `TranslationSession` | class (ARC) | Core session: translate single/batch/async-batch, prepare, cancel; iOS 26+ direct ctor |
| `TranslationSession.Request` | struct | `SourceText` (string), `ClientIdentifier` (string?), ctor |
| `TranslationSession.Response` | struct | `SourceLanguage`, `TargetLanguage`, `SourceText`, `TargetText`, `ClientIdentifier`, ctor |
| `TranslationSession.BatchResponse` | class (IAsyncEnumerable<Response>) | Swift `AsyncSequence` → C# `IAsyncEnumerable<Response>`; `MakeAsyncIterator()`, `GetAsyncEnumerator()` |
| `TranslationSession.BatchResponse.AsyncIterator` | struct | `NextAsync(CancellationToken)` → `Task<Response?>` |
| `TranslationSession.Configuration` | struct (IEquatable<Configuration>) | `Source`/`Target` (Locale.Language?), `Version` (int), `Invalidate()`, ctor |

### SwiftUI presentation gap (not a defect)

Translation's primary on-device UI path — `.translationPresentation()` and `.translationTask()` SwiftUI view modifiers — is SwiftUI-only and intentionally outside this binding's scope. There is no `BridgedViews[]` entry because these modifiers don't vend bindable types; they present system UI inline. Non-SwiftUI consumers should note: **on iOS 18–25, `TranslationSession` cannot be constructed directly** — a session is only obtainable via the SwiftUI modifier callback. The `TranslationSession(source:target:)` direct initializer is iOS 26+ only (correctly decorated at Translation.cs:3957). This is a framework design constraint, not a binding gap.

### Prioritized generator unlocks

None. The one skip is legitimately untranslatable.

---

## 2. C# Quality

### Async surface — correct across the board

All Swift `async throws` methods are properly surfaced as `Task`-returning C# methods with `CancellationToken` support and correct cleanup paths (SwiftAsyncCallHolder, cancellation registration, GCHandle lifecycle).

| Swift API | C# name | Location | Return |
|---|---|---|---|
| `translate(_:)` | `TranslateAsync(string, CancellationToken)` | Translation.cs:3470 | `Task<Response>` |
| `translate(batch:)` | `Translate(IEnumerable<Request>)` | Translation.cs:3533 | `BatchResponse` |
| `translations(from:)` | `TranslationsAsync(IEnumerable<Request>, CancellationToken)` | Translation.cs:3694 | `Task<IReadOnlyList<Response>>` |
| `prepareTranslation()` | `PrepareTranslationAsync(CancellationToken)` | Translation.cs:3861 | `Task` |
| `status(for:)` (×2 overloads) | `StatusMethodAsync(…)` | Translation.cs:476, 648 | `Task<LanguageAvailability.Status>` |
| `supportedLanguages` | `GetSupportedLanguagesAsync(CancellationToken)` | Translation.cs:160 | `Task<IReadOnlyList<Locale.Language>>` |

`BatchResponse` correctly implements `IAsyncEnumerable<Response>` (Translation.cs:2394) so consumers can `await foreach (var r in session.Translate(batch)) { … }` idiomatically.

### Naming: `StatusMethodAsync` is slightly awkward

`status(for:)` → `StatusMethodAsync` (Translation.cs:476, 648). The `Method` infix is generated to disambiguate from the nested `Status` enum and is not wrong, but reads a little rough. `GetStatusAsync` would be more idiomatic. Within the 1:1 binding philosophy this is acceptable, not a gap. Worth noting for a future polish pass.

### Nullability — correct throughout

- `TranslationSession.SourceLanguage` / `TargetLanguage`: `Locale.Language?` ✓ (Translation.cs:1262, 1313)
- `TranslationSession.Request.ClientIdentifier`: `string?` ✓ (Translation.cs:1836)
- `TranslationSession.Response.ClientIdentifier`: `string?` ✓ (Translation.cs:2222)
- `TranslationError.ErrorDescription` / `FailureReason`: `string?` ✓ (Translation.cs:1036, 1079)
- `LanguageAvailability.StatusMethodAsync` target param: `Locale.Language?` ✓ (Translation.cs:476, 648)

### Lifetime — correct

- `LanguageAvailability` (class/ARC): `IDisposable` + finalizer-safe ARC via `SwiftClassHandle`. (Translation.cs:232)
- `TranslationError` (struct): `ISwiftStruct + IDisposable`. (Translation.cs:1097)
- `TranslationSession` (class/ARC): `IDisposable`. (Translation.cs:1543)
- `Request`, `Response`, `Configuration`, `BatchResponse`, `AsyncIterator`: all `IDisposable`. Correct for struct types holding native memory.

### Error typing — correct

`_SbwModuleErrorRegistry_Translation` (Translation.cs:3989) dispatches on `errorTypeId=1` to reconstruct `SwiftException<TranslationError>`. Consumers can `catch (SwiftException<TranslationError> te)` and inspect `te.Error.ErrorDescription`. The fallback (id=0 or unknown) wraps as `SwiftException`. Correct.

### `TranslationSession` — iOS 26+ ctor is the only direct creation path

`TranslationSession(Locale.Language source, Locale.Language? target)` at Translation.cs:3957 is correctly decorated `[SupportedOSPlatform("ios26.0")]`. On iOS 18–25, `TranslationSession` is a class with no usable public constructor from the binding — sessions are only vended by the SwiftUI `.translationTask` modifier. This is the correct behavior; the binding can't expose what the framework doesn't give programmatically.

### No broken or unusable types

Every emitted type has at least one public constructor or static factory and all significant members are reachable.

---

## 3. Test Coverage

**File**: `tests/Tests.cs`  
**Test count**: 12 Pass/Fail cases (0 Skips)  
**Platform**: iOS Simulator (Mono JIT) via `Program.UIKit.cs`

| # | Test | What it exercises | Depth |
|---|---|---|---|
| 1 | `LanguageAvailability.Status values` | Enum integer constants | Weak — pure C# |
| 2 | `LanguageAvailability constructor` | `new LanguageAvailability()` non-null | Weak — no native call |
| 3 | `TranslationError static singletons` | 6 `static` property P/Invokes linking | Weak — no value check |
| 4 | `TranslationError property reads` | `ErrorDescription`, `FailureReason` native get | Moderate — calls cdecl getter, checks no crash |
| 5 | `Request constructor round-trip` | `SourceText` marshal (string↔SwiftString) | **Strong** — round-trips a real string |
| 6 | `Request with clientIdentifier` | `ClientIdentifier` optional string | **Strong** — round-trips nullable string |
| 7–12 | Metadata for 6 types | `SwiftObjectHelper<T>.GetTypeMetadata()` | Weak — metadata handle only |

### Untested surface (significant)

| Member | What's missing |
|---|---|
| `TranslateAsync(string)` | The primary API: zero tests. Requires a real session on device/simulator with language models installed. |
| `TranslationsAsync(IEnumerable<Request>)` | Batch async path: zero tests. |
| `Translate(IEnumerable<Request>)` → `BatchResponse` | `IAsyncEnumerable<Response>` path: zero tests. `await foreach` ABI unverified. |
| `PrepareTranslationAsync()` | Language model pre-download: zero tests. |
| `GetIsReadyAsync()` | Readiness check: zero tests. |
| `GetSupportedLanguagesAsync()` | Returns `IReadOnlyList<Locale.Language>`: zero tests. |
| `StatusMethodAsync(Locale.Language, …)` | Language status query: zero tests. |
| `StatusMethodAsync(string, …)` | String-overload path: zero tests. |
| `Response` property round-trip | `SourceText`, `TargetText` after a real translation: zero tests. |
| `Configuration` construction / `Invalidate()` | Only metadata probe exists: zero tests of value. |
| `Cancel()` [iOS 26+] | Zero tests. |

### Root cause and recommendation

All async paths require a real Translation session with Apple's on-device language models installed. These can only be exercised at runtime (Simulator or device) — they cannot be unit-tested in isolation. The current tests run without a session and so stop at struct/metadata probes.

**High-value tests to add** (all belong in `Tests.cs` with proper Skip gates for unavailable models):

1. **`LanguageAvailability.GetSupportedLanguagesAsync()`** — call it, assert the returned list is non-null and non-empty (if models are installed). This validates the `SwiftArray<Locale.Language>` → `IReadOnlyList<>` marshal path end-to-end. Runnable on any simulator build.

2. **`LanguageAvailability.StatusMethodAsync(string, null)`** — pass an auto-detectable string, assert `Status` is one of the three enum cases. Validates the optional-language parameter path.

3. **`TranslationSession.TranslateAsync` round-trip** — create a session (iOS 26+), call `TranslateAsync("Hello")`, assert `Response.TargetText` is non-empty and `Response.SourceText == "Hello"`. This is the core ABI proof. Skip on iOS < 26.

4. **`BatchResponse` `await foreach` path** — `Translate(new[]{new Request("Hello"), new Request("World")})`, iterate via `await foreach`, assert 2 responses with non-empty `TargetText`. Validates the `IAsyncEnumerable<Response>` / `AsyncIterator.NextAsync` path.

5. **`TranslationsAsync` vs `Translate` comparison** — both should produce the same response count and content for the same input. Validates the two batch API variants don't diverge.

---

## Action Items

| # | Dimension | Finding | Recommendation | Effort | Value |
|---|---|---|---|---|---|
| 1 | Test Coverage | `TranslateAsync`, `TranslationsAsync`, `BatchResponse`/`await foreach`, `PrepareTranslationAsync`, `GetIsReadyAsync` — all zero runtime coverage | Add async-session tests (Items 3–5 above) with model-availability Skip guard; these are the core ABI paths | Medium | High |
| 2 | Test Coverage | `GetSupportedLanguagesAsync` and `StatusMethodAsync` — zero tests but runnable without a full session on simulator | Add tests (Items 1–2 above); `GetSupportedLanguagesAsync` should work on any iOS 18+ sim with System Language configured | Low | Medium |
| 3 | C# Quality | `StatusMethodAsync` naming — "Method" infix is slightly odd vs conventional `GetStatusAsync` | Polish in a future naming-pass sweep (not blocking) | Trivial | Low |
