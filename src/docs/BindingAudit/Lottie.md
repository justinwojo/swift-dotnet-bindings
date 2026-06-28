# Lottie — Binding Audit

- **Package**: SwiftBindings.Lottie v4.6.2   **Mode**: source   **TFM(s)**: net10.0-ios
- **Native**: airbnb/lottie-ios 4.6.0
- **Audited at**: swift-dotnet-packages `1e8c27a`, generated 2026-06-27T15:09Z

## Verdict

Excellent overall health. 96.4% of types and 93.0% of members are emitted; every skip is either a deliberate project exclusion (SynthesizedCodable, ModuleInternal, SwiftUI) or a known generator limitation with low-to-medium consumer impact. The async loading APIs (`LottieAnimation.LoadedFromAsync`, `DotLottieFile.NamedAsync`) surface cleanly as `Task<T>` with `CancellationToken`. One cosmetic quality issue (empty public nested class) and one medium-value member gap (`CompatibleRenderingEngineOption.generateLottieConfiguration`) are the only actionable findings. Test coverage is the main weakness: 89 test cases exist but zero cover async loading, GradientValueProvider, or LottieAnimationSource, and ~12 cases silently skip at runtime due to missing animation files.

## 1. Coverage

| Metric | Count | % of total |
|---|---|---|
| Types emitted | 80 / 83 | 96.4% |
| Types skipped | 3 | — |
| Members emitted | 438 / 471 | 93.0% |
| Members skipped | 28 | — |
| Members synthesized | 301 | (added by generator, not native Swift) |

**Skipped types (3):** `LottieView` (SwiftUIConstraint — generic `View` constraint; correctly excluded), `LottieButton` and `LottieSwitch` (SwiftUIView — bridges generated; `BridgeSummary: 3/3 Generated`). All three are correctly handled.

### Skip-reason breakdown (28 member skips)

| Reason | Count | Classification |
|---|---|---|
| SynthesizedCodable | 13 | **(a) Correctly excluded** — `Encoder`/`Decoder` existential not bindable; deliberate project decision |
| ModuleInternal | 5 | **(a) Correctly excluded** — implicit overriding constructors on `LottieAnimationLayer`, `AnimatedControl`, `CompatibleAnimationKeypath`, `CompatibleAnimation`, `CompatibleDictionaryTextProvider` |
| UnsupportedSignature | 5 | **(a)/(b) mixed** — see below |
| DuplicateSignature | 2 | **(a)/(b) mixed** — see below |
| SwiftUIView | 2 | **(a) Correctly excluded** (bridged) |
| UnsupportedExistential | 2 | **(b) Real gap** — see below |
| SwiftUIConstraint | 1 | **(a) Correctly excluded** |
| UnsatisfiedGenericConstraint | 1 | **(b) Marginal** — see below |

### (a) Correctly excluded — UnsupportedSignature

- `LottieLogger.assert`, `.assertionFailure`, `.warn`: Swift `@autoclosure () -> String` + `StaticString` parameters have no C# equivalent. These are developer logging/debugging utilities; very low consumer value.
- `Keyframe<T>.==`: operator on a generic type requires buffer marshalling (not yet supported). Low direct consumer value — `Keyframe` is a model/JSON type rarely compared in C# code.

### (b) Real gaps

**Gap 1 — `CompatibleRenderingEngineOption.generateLottieConfiguration` (UnsupportedSignature)**
Swift: `public static func generateLottieConfiguration(_ configuration: CompatibleRenderingEngineOption) -> LottieConfiguration`
Both `CompatibleRenderingEngineOption` (emitted as enum, `Lottie.cs:24369`) and `LottieConfiguration` (emitted as struct, `Lottie.cs:9435`) are fully bound. The skip reason is "Return type is unsupported for simple enum static method." This is the ObjC compatibility bridge that converts an option enum value into a full configuration struct. Consumers using the ObjC-compat surface (`CompatibleAnimationView`) have no way to call this conversion from C#.
**Worth fixing**: medium value, medium tractability — a Swift wrapper shim that calls through to the real function would unblock it without generator changes.

**Gap 2 — `DotLottieFile.SynchronouslyBlockingCurrentThread.loadedFrom` / `.named` (UnsupportedExistential)**
Swift: `static func loadedFrom(...) -> Result<DotLottieFile, any Error>` and `static func named(...) -> Result<DotLottieFile, any Error>`.
The `any Swift.Error` existential in the bound generic blocks binding. The async equivalents (`DotLottieFile.NamedAsync`, `.LoadedFromAsync`) ARE emitted and work. Consumers who need synchronous `.lottie` loading must restructure to `async/await`. Medium-low priority given the async alternatives, but note that `SynchronouslyBlockingCurrentThread` ships as a public empty nested class (see §2).

**Marginal — `GradientValueProvider.storage` (UnsatisfiedGenericConstraint)**
`ValueProviderStorage<Array<Double>>` doesn't satisfy `AnyInterpolatable`. `GradientValueProvider` itself is fully emitted (`Lottie.cs:18873`) with `colors`, `locations`, and the `IAnyValueProvider` conformance — the `storage` property is an implementation detail. Correctly excluded; zero consumer impact.

**DuplicateSignature**
- `LottiePlaybackMode.paused` (static property): Collides with the synthesized `Paused(at:)` factory method name. The property is `@available(*, deprecated, renamed: "LottiePlaybackMode.paused(at:)")` in the swiftinterface — deprecated API, low impact.
- `AnimationKeypath.init`: A secondary constructor shadows the primary `init(keypath:)` binding. Both `AnimationKeypath(string keypath)` and `AnimationKeypath(IEnumerable<string> keys)` are emitted (`Lottie.cs:17406`, `Lottie.cs:17431`); the colliding form is the redundant one. No consumer impact.

### Prioritized generator unlocks

| Priority | Gap | What's needed |
|---|---|---|
| Medium | `CompatibleRenderingEngineOption.generateLottieConfiguration` | Swift wrapper shim (trivial — single-line pass-through) |
| Low | `DotLottieFile.SynchronouslyBlockingCurrentThread` sync loaders | Generator support for `Result<T, any Error>` return in static methods |
| Low | `Keyframe<T>.==` | Buffer marshalling for operators on generic types |

## 2. C# Quality

**Naming / shape**: Clean throughout. PascalCase on all public members, no mangled Swift identifiers visible to consumers. Enums (`LottieBackgroundBehavior`, `DecodingStrategy`, `RenderingEngine`, `CompatibleRenderingEngineOption`, `CompatibleBackgroundBehavior`) map directly with integer raw values. Nested `CaseTag` enums on Swift enum-structs are internal enough to not impede navigation. `ILottieURLSession`, `IAnimationCacheProvider`, `IAnyValueProvider`, etc. are generated as proper C# interfaces.

**Async**: All `async` Swift methods surface as `Task<T>` with `CancellationToken` (`Lottie.cs:2435`, `2608`, `2762` for `LottieAnimation.LoadedFromAsync`; `Lottie.cs:14924`, `15104`, `15261` for `DotLottieFile.NamedAsync`). No async API is blocked to sync-only. Cancellation wiring is present on all overloads.

**Nullability**: Correctly annotated throughout. `LottieAnimation.Named()` → `LottieAnimation?` (`Lottie.cs:2219`); `CurrentPlaybackMode` → `LottiePlaybackMode?` (`Lottie.cs:3199`); optional protocol parameters accept null. No contradictory annotations spotted.

**Lifetime / IDisposable**: Present on all struct-wrapping types (`LottieConfiguration`, `AnimationKeypath`, `LottieLoopMode`, `LottiePlaybackMode`, `LottieColor`, `LottieVector1D/2D/3D`, `DotLottieConfiguration`, `DotLottieConfigurationComponents`, `ValueProviderStorage<T>`). Class-wrapping types use `SwiftClassHandle`. `IDisposable` doc comments ("Use a 'using' block or call Dispose()") present on structs — consumers can manage lifetime idiomatically. No obvious leaks in the generated wrappers.

**`[Obsolete]` coverage**: Deprecated Swift APIs (`LottiePlaybackMode.Progress`, `.Frame`, `.Pause`, `.PlaySection`, `.PlayFromFrame`, `.PlayFromProgress`, `.Stop`) correctly carry `[Obsolete]` attributes (`Lottie.cs:6602`, `6620`, etc.). Consumers get the deprecation guidance at compile time.

**Issue — empty public nested class** (`Lottie.cs:14795`):
`DotLottieFile.SynchronouslyBlockingCurrentThread` is emitted as a public nested `partial class` with no members — all two members it would have contained were skipped (UnsupportedExistential). A consumer browsing IntelliSense or docs discovers a callable-looking class that does nothing. Should either be suppressed from emission or carry an XML doc comment explaining the gap.

**No outright broken types**: Every emitted type has at least one usable constructor or factory method. `LottieAnimationSource` is a Swift enum-struct with static factories `LottieAnimationSource.LottieAnimation(...)` and `LottieAnimationSource.DotLottieFile(...)` (`Lottie.cs:942`, `959`); not headline-obvious but functional.

## 3. Test Coverage

**Count**: 89 distinct test names (`results.Pass/Fail/Skip`), ~90 `Pass` call-sites.

**Depth**: Good on the core paths. Tests round-trip real values:
- `AnimProp_Duration/Framerate/Frames/DurationConsistency`: checks `> 0` and math consistency on loaded animation properties
- `L4_LoopMode_Repeat/RepeatBackwards`: constructs enum cases and extracts the float payload — proves ABI for the tagged-union pattern
- `LottieColor_Properties`: constructs `LottieColor` with RGBA values and reads them back — real round-trip
- `Cache_Roundtrip`: stores and retrieves an animation from `DefaultAnimationCache` — proves the cache protocol binding
- `Provider_FloatHasUpdate/FloatSet`: exercises `FloatValueProvider.HasUpdate` + `Value` round-trip
- `L9a–L9h`: construction + size sanity on multiple types, plus `AnimationKeypath` string/key round-trips and a `CompatibleDictionaryTextProvider` construction smoke

**Skips at runtime (~12)**: Tests whose body calls `results.Skip("...", "No test file")` — they depend on a bundled Lottie JSON/dotlottie file that isn't present in the test bundle at runtime. These include `View_SetAnimation`, `View_CurrentProgress`, `View_CurrentFrame`, `Layer_PlayStop`, `L1_SetValueProvider_Float/Size/Point`, `L1L2_SetValueProvider_Color` (conditional), `L8_LogHierarchyKeypaths`, `Animation_FromData/FromDataStrategy`, `Cache_Roundtrip` (conditional). These pass mechanically but do not prove the animation-dependent code paths at all.

**Significant untested surface**:

| Missing coverage | Type/member | Why it matters |
|---|---|---|
| Async loading | `LottieAnimation.LoadedFromAsync`, `DotLottieFile.NamedAsync` | Core async feature — zero tests despite full emission |
| Color overrides | `GradientValueProvider` (`Lottie.cs:18873`) | Key value-provider type; `colors`/`locations` setters untested |
| Animation source | `LottieAnimationSource` factory methods (`Lottie.cs:942`, `959`) | Swift enum-struct pattern; untested |
| DotLottie loading | `DotLottieFile` async loads (not just metadata size) | `L5_DotLottieFile_Metadata` is a size-only smoke |
| Keypath value get | `LottieAnimationLayer.GetValue(for:atFrame:)` | Read-back from keypath pipeline untested |

**Recommended tests to add**:
1. `Async_LottieAnimation_LoadedFrom` — call `LottieAnimation.LoadedFromAsync(NSUrl…)` with a test-server URL, `await` it, assert non-null. Proves the async machinery end-to-end.
2. `Provider_Gradient_ColorsRoundtrip` — construct `GradientValueProvider` with a known color array + location array, read back `.Colors` and `.Locations`, assert values. Add to BindingTests `SwiftBindingsTestLib` for a permanent ABI gate.
3. `LottieAnimationSource_Factories` — call both `LottieAnimationSource.LottieAnimation(…)` and `LottieAnimationSource.DotLottieFile(…)`, check `Tag` distinguishes them.
4. Bundle a minimal Lottie JSON fixture into the test app so the 12 currently-skipped test bodies actually execute rather than short-circuit.

## Action Items

| # | Dimension | Finding | Recommendation | Effort | Value |
|---|---|---|---|---|---|
| 1 | Coverage | `CompatibleRenderingEngineOption.generateLottieConfiguration` skipped (UnsupportedSignature) | Add a Swift wrapper shim that calls through and re-generates the binding | XS | Medium |
| 2 | C# Quality | `DotLottieFile.SynchronouslyBlockingCurrentThread` is an empty public class | Suppress emission when all members are skipped, or add XML doc explaining the gap | S | Low |
| 3 | Test Coverage | Zero async loading tests | Add `Async_LottieAnimation_LoadedFrom` + `Async_DotLottieFile_Named` tests to `Program.cs` | S | High |
| 4 | Test Coverage | `GradientValueProvider` completely untested | Add `Provider_Gradient_ColorsRoundtrip` to BindingTests (prove ABI for color-array provider) | S | High |
| 5 | Test Coverage | 12 tests skip due to missing animation file | Bundle a minimal JSON fixture in the test app so animation-dependent tests actually run | M | Medium |
| 6 | Test Coverage | `LottieAnimationSource` factories untested | Add `LottieAnimationSource_Factories` smoke test | XS | Low |
