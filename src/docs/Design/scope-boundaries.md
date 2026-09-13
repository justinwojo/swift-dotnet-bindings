# Scope boundaries

Settled product boundaries and accepted limitations. These are reference decisions, not unprioritized work. Counts below are historical snapshots; use the named baseline for current measurements.

## Not Worth Addressing

Counts are from `build/baselines/validation-baseline.json` `skip_metrics.skip_reasons` (`git_sha` `9772e256`) — re-read the baseline for current figures rather than trusting this table.

| Skip Reason | Count | Why Not |
|-------------|------:|---------|
| @_spi / internal members (`ModuleInternal`) | ~1095 | Correct behavior — private API should not be bound |
| Synthesized Codable | ~971 | .NET consumers use own serialization (`System.Text.Json`, etc.) |
| AnyTypeFallback (`Any`, `[Any]`, `Optional<Any>`, ObjC delegate protocols, PAT subscripts) | ~418 | PAT classification + by-design Swift `Any` + ObjC protocols + cross-library — fully architecturally-deferred. In-scope single-module gaps measure 0 hits. |
| Unsupported signatures (associated types, bare generics) | ~1418 | Swift patterns with no C# equivalent |
| Generic protocol constraints / PATs | ~394 | Architecturally blocked by associated type erasure |
| SwiftUI/Combine dependencies | ~178 | Framework boundary — consumers use SwiftUI bridge instead (`SwiftUIConstraint` + `SwiftUIView`) |
| Unsupported existential (opaque generics) | ~101 | Fundamental limitation of Swift's type system vs C# generics |
| UnsatisfiedGenericConstraint (remaining) | ~263 | Fundamental type system constraints, not relaxable gates |

## Explicitly Out of Scope

| Item | Reason |
|------|--------|
| Full Swift type graph infrastructure | Over-engineered for current needs |
| Deep generic signature / associated type constraint emission | C# generics can't express Swift's full type system |
| Result builder (`@resultBuilder`) projection | Compile-time Swift feature, no ABI JSON representation |
| Broad `@dynamicMemberLookup` reification (beyond targeted Apple Supplement shims) | Affects <5 types across 66 validation libraries. `AttributedString` is covered narrowly via a hand-rolled partial layered on the Apple Supplement xcframework (Session 7 — `LanguageIdentifier` shipped; `link`/`foregroundColor`/etc. follow the same shape on demand). The general "walk every `@dynamicMemberLookup` host and reify per-key C# properties" pass is still out of scope. |
| Composing SwiftUI view trees from C# | Result builders are a compiler feature |
| Structs projected as C# value types | Only safe for frozen+blittable subset; marginal benefit |

### Apple-framework by-design limits (won't fix)

Framework-specific consequences of the result-builder / source-gen / PAT boundaries above — closing any of these is a different product or contradicts the framework's own design. The Apple-framework gap-fix campaign closed in 0.12.0 and the residual Tier 2 surface shipped in 0.14.0 (RC-AOT typed mesh buffers `68e984ae`, CryptoKit/HPKE construction `7cfa4950`, witness-getter callback path Option A, sibling emission-marker re-keying `ad6c8d27`); these are the consciously-parked limits that outlived it.

| Item | Reason / workaround |
|------|------|
| **AppIntents `perform()` / authoring, ActivityKit Live Activities** (RC-STRUCTURAL) | Need a C#→Swift source-gen + macro-expansion subsystem (a different product). Both on the `swift-dotnet-packages` do-not-ship list. |
| **WeatherKit statistics/summaries + `weather(for:including:)`** | 6-way method-own-generic `async` tuple return exceeds the CSM cartesian cap. Full-bundle `WeatherAsync` is the workaround. |
| **TipKit result-builder DSL** (RC-AEIC) | Entrypoints are shimmable but the authoring experience is not restorable from C# — the same `@resultBuilder` wall as the rows above. |
| **RC-SB0003 reverse witness dispatch** | Case-by-case; many are by-design Swift limits. The forward (C#-implements) path works and is the supported mechanism. |
| **`@autoclosure`** (RC-CLOSURE) | No shipping-framework consumer; revisit only if one needs it. |
| **App-defined PAT conformers** (RC-PAT; e.g. ProximityReader `requestDocument`) | CSM only works for Apple-finite conformer sets; app-defined conformers are source-gen territory. |
| **RealityKit detached-setter `willSet` trap** (RC-WILLSET) | Framework `willSet` precondition; no ABI route bypasses a Swift property observer, so nothing is generator-fixable here — the setter must be called on an attached entity per the framework's own contract. |
| **General `Measurement<T>` value-only projection** | Foundation type behavior, not a binding defect. The targeted `Measurement<T>(double, T)` ctor (WorkoutKit range alerts) is a deliberate narrow surface, not a general round-trippable `Measurement<T>`. |
| **BlinkIDUX raw `events` stream** (RC-PAT; third-party) | `BlinkIDAnalyzer.events` getter stays a produce-throw (compile-poisoned SB0006): its root proxy `EventStreamProxy` is blocked by a PAT (`associatedtype Event`), which is not forward-safe, so it correctly stays fail-closed. Supported path: the concrete `BlinkIDEventStream.Stream` `IAsyncEnumerable`, which works. |
| **Authoring brand-new C# conformers behind forward-only proxies** (RC-SB0003 instance) | After the EveryProtocol proxy rescue, RealityFoundation `IMaterial`/`ISynchronizationService` project through forward-only proxies with no reverse-dispatch impl ctor — writing a from-scratch C# conformer and packing it into the framework stays unsupported. Consuming and round-tripping framework-produced instances works. |
