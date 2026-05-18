# Session 10 — Residual consumers + cleanup + close A-1

Final wave. After Sessions 7–9, the big-three KeyPath consumers (Foundation, AppIntents, SwiftUI) are bound. Session 10 picks up the remaining ~120 lines of `KeyPath<…>` surface across smaller consumers, runs the regression-validation sweep against the full SDK, and closes the user-visible deferral.

## Goal

1. Bind the residual KeyPath consumers: **Charts** (49 lines), **SwiftData** (38 lines), **Combine** (14 lines), **UIKit** (12 lines), **Observation** (4 lines).
2. Run the `regression-validation` skill against the full Apple SDK suite. Path B from Session 6 — real MusicKit smoke. Real `swift-dotnet-packages` test cell coverage.
3. Update public-facing documentation: wiki, A-1 entry in the active release doc, internal design docs.
4. Final validation sweep across all 11 sessions' fixtures.

## Why this session

- Long-tail consumers are individually small but cumulatively non-trivial. Skipping them leaves visible tombstones in shipping consumer-facing code.
- The regression-validation sweep (Path B) is the only step that validates against *real* Apple frameworks, not mocks. Catches ABI bugs that mock fixtures cannot.
- Doc/wiki updates are the user-visible closure of the A-1 deferral. Without them, the project is materially complete but unreleased.

## Dependencies

- **Sessions 1-9** all shipped, baselines ratcheted, fixtures passing on sim + device.

## Phase 10.1 — Charts (49 lines, all read-only)

Charts uses `KeyPath<Datum, Value>` exclusively as a *read-only* selector for `VectorizedChartContent` — the mark builder enumerates data and projects values. No `WritableKeyPath` or `ReferenceWritableKeyPath` surface.

Pre-image:

```swift
extension VectorizedChartContent {
    public func data<Element>(_ data: KeyPath<Self.Element, Element>) -> some VectorizedChartContent
    // ~10 similar projection extensions
}
```

Touch points: enumerate `VectorizedChartContent` conformers, emit per-conformer typed singletons for projected properties. Same machinery as Session 8 (closed-conformer enumeration → typed singletons → C# extension methods).

BindingTests fixture: smoke-test against one or two Charts mark types (`LineMark`, `PointMark`) with `KeyPath<…, …>` projection.

## Phase 10.2 — SwiftData (38 lines, mostly `PartialKeyPath<T>`)

SwiftData uses `PartialKeyPath<T>` heavily for schema/index metadata (type-erased property reference). Less use of typed `KeyPath<T, V>`.

Pre-image:

```swift
public struct FetchDescriptor<T> where T : PersistentModel {
    public init(predicate: Predicate<T>? = nil, sortBy: [SortDescriptor<T>] = [])
}

public struct SortDescriptor<T> {
    public init(_ keyPath: PartialKeyPath<T>, order: SortOrder = .forward)
    public init<V>(_ keyPath: KeyPath<T, V>, order: SortOrder = .forward) where V : Comparable
}
```

`SortDescriptor` is the key API. Closed-conformer enumeration: `PersistentModel` conformers. Same as AppIntents `AppEntity` — closed in the SDK, open if user-defined.

BindingTests fixture: define a mock `PersistentModel`, construct a `SortDescriptor` with both `KeyPath` and `PartialKeyPath` variants.

## Phase 10.3 — Combine (14 lines)

```swift
extension Publisher {
    public func map<T>(_ keyPath: KeyPath<Output, T>) -> Publishers.MapKeyPath<Self, T>
    public func assign<Root>(to keyPath: ReferenceWritableKeyPath<Root, Output>, on object: Root) -> AnyCancellable
}
```

`Publisher.map(_:)` and `Publisher.assign(to:on:)` are the two key surfaces. Both consume the closed-conformer typed singletons from Sessions 4/9.

BindingTests fixture: create a `Just<Int>` publisher, `map(MockObservableKeyPaths.Counter)` to project a property, and `assign(to:on:)` to bind output to a `ReferenceWritableKeyPath`-rooted property.

## Phase 10.4 — UIKit (12 lines)

```swift
public class UIPasteboard {
    public func value(forPasteboardType: String) -> Any?
    // not KeyPath surface
}

@_enclosingInstance
extension UIView {
    public subscript<Value>(_ keyPath: ReferenceWritableKeyPath<UIView, Value>) -> Value
}
```

UIKit's KeyPath surface is small. The `@_enclosingInstance` subscript shape is structurally interesting — the receiver type is the *enclosing* instance, an attribute that may not yet be projected correctly. Verify at session time.

BindingTests fixture: minimal UIKit smoke (sim-only — UIKit doesn't run on macOS).

## Phase 10.5 — Observation (4 lines)

```swift
extension ObservationRegistrar {
    public func access<Subject, Member>(_ subject: Subject, keyPath: KeyPath<Subject, Member>)
    public func willSet<Subject, Member>(_ subject: Subject, keyPath: KeyPath<Subject, Member>)
}
```

Observation is the Swift 5.9+ macro-based observability framework. `ObservationRegistrar` is the low-level integration point; most user code uses the `@Observable` macro which expands to `ObservationRegistrar` calls. Binding-level only emits the `access(_:keyPath:)` / `willSet(_:keyPath:)` methods; the macro is C#-unsurfaceable.

BindingTests fixture: minimal `ObservationRegistrar` smoke.

## Phase 10.6 — `regression-validation` skill flow (Path B from Session 6)

Per the `regression-validation` skill stored in `apple-framework-portfolio.md`:

- Run real MusicKit smoke against a populated music library (physical device required for Music auth).
- Confirm `filter(matching: AlbumLibraryFilterKeyPaths.Title, contains: "love")` returns expected results.
- Confirm `await request.response()` returns a typed `MusicLibraryResponse<Album>`.
- Sweep against the `swift-dotnet-packages` test cells for each framework added in Sessions 7-10.

Any failure here points to ABI/integration bugs not caught by mock fixtures.

## Phase 10.7 — Doc/wiki update

- **Wiki — Known Limitations page** (`/Users/wojo/Dev/swift-dotnet-bindings.wiki/Known-Limitations.md`): remove `MusicLibraryRequest`, remove `Binding<T>` projection, remove `EntityProperty`, remove `AttributedString attribute projection`, remove `EnvironmentValues environment` from the limitations list. Add explicit entries for the *remaining* limitations: "Open-conformer KeyPath (user-defined `AppEntity` / `ObservableObject` in C# consumer code) — not supported in v1; track as future work."
- **Wiki — supported APIs page** — add per-framework cells noting "KeyPath APIs supported as of v0.X.Y": Foundation, AppIntents, SwiftUI, Charts, SwiftData, Combine, UIKit, Observation, MusicKit.
- **`src/docs/sdk-X.Y.Z-remaining.md`** (or active release doc): close A-1; mark all KeyPath subsystem session items as done.
- **`src/docs/keypath-subsystem/00-overview.md`**: status section → "Shipped in v0.X.Y."
- **Roadmap (`src/docs/roadmap.md`)**: remove KeyPath items from in-progress; add Open-Conformer-KeyPath follow-up as a tracked future-work item.

Coordinate the wiki edit with the wiki repo's normal flow (per `MEMORY.md` the wiki lives at `/Users/wojo/Dev/swift-dotnet-bindings.wiki`).

## Phase 10.8 — Final validation sweep

Run the full gate suite one last time:

- `nuke test` — baseline holds.
- `nuke binding-tests --sim --device` — every fixture (1–10) passes on both runners.
- `nuke validate` — full library sweep; baseline ratchets to its final post-KeyPath state. The Apple framework `swift_compile` and `cs_compile` counts should reflect every newly-bound type.
- Regression-validation skill — green across the full Apple SDK suite.

If any fixture or library shows a regression: hot-fix in the appropriate Session-N follow-up before sign-off. Do not close this session with a known-failing gate (per `feedback_no_expected_failures.md` and `feedback_no_autonomous_defer.md`).

## Validation gates

| Gate | Expected |
|---|---|
| `nuke test` | Baseline |
| `nuke binding-tests --sim` | All Sessions 1-10 fixtures pass |
| `nuke binding-tests --device` | Same |
| `nuke validate` | Final baseline; KeyPath subsystem fully reflected; no regressions |
| `regression-validation` skill flow | All Apple-framework cells pass |
| Wiki diff | Known Limitations updated; supported-APIs pages updated |

## Exit criteria

- Charts, SwiftData, Combine, UIKit, Observation KeyPath surface emits and passes test.
- `regression-validation` skill flow green.
- Wiki updated; public-facing limitations list reflects v2 work items.
- A-1 closed in active release doc.
- `00-overview.md` status → Shipped.
- Roadmap updated.
- No open follow-up items in Sessions 1-9 that block release.

## Risks specific to Session 10

- **Risk A (regression-validation discovers a real ABI bug)** — Path A mock fixtures didn't catch it; Path B real-SDK does. **Mitigation:** treat any regression-validation failure as a hot-fix gate for the relevant earlier session. Do not press through to release with a real-SDK failure.
- **Risk B (`@_enclosingInstance` subscript shape unhandled)** — UIKit's enclosing-instance subscript is structurally unusual. May not project correctly via current machinery. **Mitigation:** if this shape causes consistent failure, scope-out UIKit's `@_enclosingInstance` subscripts to a v2 follow-up rather than blocking session close.
- **Risk C (Observation macro expansion divergence)** — `@Observable` macro evolves between Swift versions. Generator emission of macro-expanded code may shift. **Mitigation:** test against current Swift version; track upstream changes via regression-validation cadence.
- **Risk D (Wiki edit ordering)** — wiki updates can't ship before NuGet publish (consumers reading the wiki would expect features not yet downloadable). **Mitigation:** coordinate wiki PR open *after* the NuGet package is uploaded. Per `feedback_no_commit_packages.md`, also don't commit the `swift-dotnet-packages` references until the SDK version is published.
- **Risk E (Long-tail validation count)** — running ~25 fixtures in `binding-tests` on both sim + device may exceed reasonable session-time budget. **Mitigation:** parallelise where possible; track total wall-time; if too slow, accept partial sim-only coverage with explicit follow-up for the device pass.
- **Risk F (Open-conformer follow-up scope creep)** — the wiki documentation of "Open-conformer KeyPath is v2 work" is the closure for v1. Future user requests will pressure widening scope. **Mitigation:** track explicitly as a *separate* numbered project (not a follow-up bullet); refuse to widen this session's scope to include it.

## References

- `00-overview.md` — final status update lands here
- Sessions 1-9 — all must be shipped
- Active release doc — A-1 entry
- `MEMORY.md` — wiki repo path, `feedback_no_commit_packages.md`, `feedback_no_expected_failures.md`, `feedback_no_autonomous_defer.md`
- `apple-framework-portfolio.md` (regression-validation skill)
- Wiki repos: `/Users/wojo/Dev/swift-dotnet-bindings.wiki` (public docs)
