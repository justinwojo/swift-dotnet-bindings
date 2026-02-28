# SwiftUI Bridge Roadmap

**Date**: February 2026
**Status**: Sessions 1-2 complete (closure/optional expansion + generic views), Sessions 3-6 planned
**Prior work**: `Completed/swiftui-bridge-v2-phases1-2.md` (param expansion + async inference), `Completed/swiftui-bridge-v2-phase3.md` (bridge hints)

---

## What We Have Today

The generator automatically detects SwiftUI Views and generates a bridge layer that wraps each View in a `UIHostingController`, exposing it as a `UIViewController` that .NET can embed in UIKit or MAUI layouts. This is **bridge generation, not SwiftUI binding** — SwiftUI's declarative rendering pipeline stays entirely in Swift.

### Completed Infrastructure

| Component | Status | Description |
|-----------|--------|-------------|
| **View detection** | Done | `SwiftUIViewDetector` identifies `SwiftUI.View` conformance |
| **Bridge collector** | Done | Thread-safe accumulation of Views during module emission |
| **Parameter bridging** | Done | Primitives, String, closures (≤4 args, String/class/primitive args), enums, classes, Optional\<String\>, Optional\<Closure\>, Optional\<Enum\>, Optional\<Class\> |
| **Async factories** | Done | ABI-driven inference of async init chains (depth 3, cross-module) |
| **Bridge hints** | Done | JSON sidecar for skip/forceTemplate/preferredInit/asyncPattern |
| **Runtime types** | Done | `SwiftColor`, `SwiftFont` projections |
| **Type database** | Done | `SwiftUIDatabase.xml` with Color + Font entries |

### Current Coverage

| Library | Views | Bridged | Runtime Tests |
|---------|-------|---------|---------------|
| BlinkIDUX | 4 | 2 (50%) | 16/16 |
| BridgeParamTest | 14 | 14 (100%) | 43/43 |
| Lottie | 6 | 5 (83%) | 15/15 |

### What Makes a View Unbridgeable Today

| Blocker | Example | Frequency |
|---------|---------|-----------|
| Generic View with non-View constraint | `ListView<Item: Identifiable>` | Moderate |
| Existential param | `init(delegate: any SomeProtocol)` | Moderate |
| Non-frozen struct param | `init(config: Configuration)` | Moderate |
| Closure with non-primitive returns | `init(validator: (String) -> MyClass)` | Rare |
| Tuple param | `init(range: (min: Int, max: Int))` | Rare |
| >4 closure params | Multiple typed closures | Rare |

**Resolved in Session 2**: Generic View types with View-constrained placeholders (e.g., `AnimatedImage<Placeholder: View>`) and `@ViewBuilder` closure params are now bridged automatically via `EmptyView` substitution.

---

## Why Not "Write SwiftUI in C#"?

SwiftUI's value proposition is its programming model: declarative body, result builders, property wrappers, opaque return types. These are **Swift compiler features**, not library APIs. They don't survive a language boundary.

UIKit works from .NET because it's imperative and object-oriented — create objects, set properties, call methods. SwiftUI's equivalent would require type-erasing everything to `AnyView` (killing performance), manually wrapping every modifier (massive maintenance surface), and reimplementing the state system (which depends on Swift runtime internals).

The correct strategy is: make it **trivial to consume SwiftUI components from Swift libraries** in .NET apps. That's where real developers hit friction.

---

## Sessions

Ordered by priority. Each session is a self-contained unit of work. You can stop after any session and have shipped value.

| Session | Focus | Priority | Status | Key Unlock |
|---------|-------|----------|--------|------------|
| **1A** | Closure & Optional expansion | Highest | **Done** | Result callbacks, selection handlers, data-passing closures |
| **1B** | Closure non-primitive returns | Medium | Planned | `(String) -> MyClass`, `(Int) -> String` return values |
| **2** | Generic view support | High | **Done** | `AnimatedImage<T>`, `LottieView<T>`, @ViewBuilder params |
| **3** | Struct params & type database | High | Planned | Configuration-object patterns, reduced AnyType pollution |
| **4** | Two-way state binding | Medium | Planned | Dynamic updates after creation (search, toggles, sliders) |
| **5** | Lifecycle, modifiers & navigation | Medium-low | Planned | `onAppear`/`onDisappear`, frame/padding/background, presentation |
| **6** | Observable binding & corpus tracking | Low | Planned | C# → Swift reactivity, coverage measurement infrastructure |

---

### Session 1A: Closure & Optional Expansion ✅

**Status**: Complete — all sub-tasks implemented, tested, and validated.

Extended closure and optional parameter bridging to handle String args, class args, Optional\<String\>, and Optional\<Closure\>.

**Completed scope**:

| Sub-task | Status | Description |
|----------|--------|-------------|
| Closures with String args | **Done** | UTF-8 encode via `Array(arg.utf8)` + `withUnsafeBufferPointer` (Swift), `Encoding.UTF8.GetString` (C# trampoline) |
| Closures with class args | **Done** | `Unmanaged.passRetained` (Swift), buffer-wrap + `SwiftMarshal.MarshalFromSwift` (C# trampoline) |
| Optional\<String\> | **Done** | `UnsafePointer<UInt8>?` + `Int` ABI; nil = no pointer, empty = non-null + len 0 |
| Optional\<Closure\> | **Done** | Maps identically to inner closure (closures already nullable in bridge ABI) |
| Non-void closure returns | **Done** | `withUnsafeBufferPointer` return propagation for String-arg closures with typed returns |

**Test coverage**: 20+ unit tests, 8 runtime tests on iOS Simulator (18 total bridge tests passing), 32/32 library validation.

**Key files modified**: `SwiftUIBridgeEmitter.InitAnalyzer.cs` (gate lifts, context threading), `SwiftUIBridgeEmitter.cs` (Swift closure encoding, C# trampoline decoding, fn ptr types), `SwiftUIBridgeEmitterTests.cs` (20+ new tests).

---

### Session 1B: Closure Non-Primitive Returns

**Priority**: Medium — deferred from Session 1A due to complex memory ownership.

Closures that return String or class types across the FFI boundary require careful ownership semantics (who allocates, who frees). Primitive returns already work.

**Scope**:

| Sub-task | Description |
|----------|-------------|
| String return from closure | Swift trampoline decodes returned UTF-8 buffer from C# callback |
| Class return from closure | `Unmanaged.passRetained` from C# → Swift unwrap with ownership transfer |
| Ownership protocol | Clear contract for allocation/deallocation across the FFI boundary |

---

### Session 2: Generic View Support ✅

**Status**: Complete — generic views with View-constrained placeholders are now automatically bridged.

Generic views like `AnimatedImage<Placeholder: View>` and `LottieView<Placeholder>` were the single biggest gap. The `viewType.IsGeneric` gate has been lifted and replaced with intelligent analysis.

**Completed scope**:

| Sub-task | Status | Description |
|----------|--------|-------------|
| Generic view analysis | **Done** | `AnalyzeGenericView()` resolves each generic param to a concrete type. Prefers constructors with `== EmptyView` constraints (ConcreteType), falls back to View protocol constraint → default `EmptyView`. |
| Constructor selection | **Done** | `SelectBestGenericConstructor()` filters failable ctors and method-level generics, ranks by ConcreteType constraints, supports `preferredInit` hint with validation. |
| `@ViewBuilder` synthesis | **Done** | Closure params returning generic placeholders synthesized as `{ EmptyView() }`. Direct generic type params synthesized as `EmptyView()`. Non-generic params bridged normally. |
| Multi-generic-param views | **Done** | Views with `<A: View, B: View>` emit `ViewName<EmptyView, EmptyView>`. Each closure resolves its specific return type (not first-in-dict). |
| Hint control | **Done** | `"placeholder"` field in `bridge-hints.json` (values: `"empty"`, `"uiview"`, `"anyviewfromvc"`). Non-empty strategies return Unsupported with "not yet implemented" (forward compat). |
| Backward compatibility | **Done** | `AnalyzeInitParameters` 2-param overload preserved. All existing tests unchanged. |

**Placeholder strategies** (selected via hint: `"placeholder": "empty"` or `"placeholder": "uiview"`):

| Strategy | Status | Description |
|----------|--------|-------------|
| `EmptyView` | **Implemented** | Default — no placeholder content |
| `UIViewWrapper` | Deferred | Wrap a UIKit `UIView` provided from C# |
| `AnyViewFromVC` | Deferred | Wrap a `UIViewController` as `UIViewControllerRepresentable` |

**Test coverage**: 22+ unit tests, 2 TestFramework integration views (`GenericPlaceholderView<Placeholder>`, `PlaceholderOnlyView<Content>`), bridge compiles with 17 bridged views, 53/53 library validation.

**Key files modified**: `SwiftUIBridgeEmitter.cs` (analysis + emission), `SwiftUIBridgeEmitter.InitAnalyzer.cs` (synthesized args), `BridgeHints.cs` (placeholder hint), `SwiftUIBridgeEmitterTests.cs` (22+ tests), `SimpleViews.swift` (2 generic test views).

---

### Session 3: Struct Parameters & Type Database Expansion

**Priority**: High — configuration-object patterns are extremely common in SwiftUI libraries.

**Scope**:

| Sub-task | Description |
|----------|-------------|
| Frozen struct params | Safe starting point — known layout, no VWT needed |
| Non-frozen struct params | VWT `initializeWithCopy` + `destroy` for struct lifecycle. Session holds copied memory, freed in `Free`. Gate behind flag initially. |
| `Optional<Struct>` | Extend OptionalWrapped for struct inner types |
| SwiftUIDatabase.xml expansion | Add commonly-referenced SwiftUI types to prevent `AnyType` pollution |
| Runtime projections | `SwiftEdgeInsets`, `SwiftAnimation` C# projection types |

**Struct risk**: VWT lookup at runtime adds fragility. Struct layout changes break silently. Start with frozen structs (safe), gate non-frozen separately.

**Type database entries** (don't enable composition — prevent `AnyType` when these appear in third-party library APIs):

| Type | Kind | Use Case |
|------|------|----------|
| `SwiftUI.Image` | struct | Return type in image libraries (Kingfisher, SDWebImage, Nuke) |
| `SwiftUI.Text` | struct | Return type in text/label libraries |
| `SwiftUI.AnyView` | struct | Type-erased view used in generic APIs |
| `SwiftUI.Binding` | struct | Two-way binding wrapper |
| `SwiftUI.EdgeInsets` | struct | Layout configuration |
| `SwiftUI.Animation` | struct | Animation configuration |

---

### Session 4: Two-Way State Binding

**Priority**: Medium — enables dynamic views (search fields, toggles, sliders). Not required for basic consumption, but significantly improves usefulness of bridged views.

Currently bridge parameters are set-once at creation. After `Create`, C# can't update the view.

**Scope**:

| Sub-task | Description |
|----------|-------------|
| Property update methods | For each bridged init param, emit `Update{Param}()` on the session. Swift side uses `@Published` / `objectWillChange` to trigger re-render. |
| ObservableObject wrapper | Session holds an `ObservableObject` that the view reads from (architectural change from current direct view creation) |
| Constrained generics | Views with `<T: Identifiable>` or `<T: Hashable>` — bridge with concrete type when constraint is satisfiable, template fallback otherwise |

**Key challenge**: The session class currently creates the view directly. For mutable state, it needs to hold an `ObservableObject` that the view reads from, so updates flow through SwiftUI's reactivity system.

```csharp
// Generated
public sealed class SearchViewSession : IDisposable {
    public static SearchViewSession Create(string query, Action<string>? onQueryChanged = null) { ... }
    public void UpdateQuery(string newQuery) { ... }  // calls SBW_*_UpdateQuery @_cdecl
}
```

---

### Session 5: Lifecycle, Modifiers & Navigation

**Priority**: Medium-low — polish and convenience. Views already work without these, but these make the bridge feel more complete.

**Scope**:

| Sub-task | Description |
|----------|-------------|
| Lifecycle callbacks | `onAppear`, `onDisappear`, `task` (async on-appear) as `@convention(c)` callbacks following existing VoidClosure pattern |
| Pre-creation modifiers | Fluent builder (`SwiftUIModifiers().Frame(300, 200).Padding(16)`) serialized to Swift bridge, applied before `UIHostingController` wrapping |
| Runtime modifier updates | Update modifiers after creation via the `ObservableObject` wrapper from Session 4 |
| Presentation helpers | `PresentAsSheet()`, `PushOnNavigationStack()`, `Dismiss()` — thin wrappers around UIKit APIs on the `UIHostingController` |

**Modifier scope control**: Curated set only — frame, padding, background, foregroundColor, cornerRadius, opacity, font. Don't try to expose all ~200 SwiftUI modifiers. Users who need exotic modifiers should write them in Swift.

```csharp
var session = MyViewSession.Create(
    title: "Hello",
    onAppear: () => LoadData(),
    modifiers: new SwiftUIModifiers()
        .Frame(width: 300, height: 200)
        .Padding(16)
        .Background(SwiftColor.Blue)
);
session.PresentAsSheet(from: parentViewController);
```

---

### Session 6: Observable Binding (C# → Swift) & Corpus Tracking

**Priority**: Low — advanced reactivity + measurement infrastructure. Nice to have, not essential.

**Scope**:

| Sub-task | Description |
|----------|-------------|
| `INotifyPropertyChanged` binding | C# `ObservableObject` → Swift `@Published` bridge via callbacks. Property change fires callback, Swift wrapper updates, SwiftUI re-renders. |
| Corpus + 3-tier coverage metrics | Systematic tracking across real SwiftUI libraries. Establishes baseline and prevents regressions. |

**Observable binding** is the most architecturally complex piece. Requires callback registration per property, main actor threading for SwiftUI updates, and lifecycle management (unregister on dispose).

```csharp
public class SearchViewModel : INotifyPropertyChanged {
    public string Query { get; set; }
}

var vm = new SearchViewModel { Query = "initial" };
var session = SearchViewSession.Create(viewModel: vm);
vm.Query = "updated";  // view updates automatically
```

**Corpus tracking** (measurement infrastructure):

| Library | Views | Key Challenges |
|---------|-------|----------------|
| BlinkIDUX | 4 | Async chain, existential, generic |
| Lottie | 6 | Optional, closures, @ViewBuilder |
| AlertToast | 2 | Enum params, optional closures |
| ConfettiSwiftUI | 1 | Simple params |
| SwiftUICharts | 5-10 | Data arrays, config structs |
| Kingfisher | 2-3 | Generic image type, async loading |
| SDWebImageSwiftUI | 3 | Async image, closures |

Three-tier metrics per library: **Generated** (bridge code emitted) → **Typechecked** (`swiftc -typecheck` passes) → **Runtime-validated** (iOS Simulator test pass). Automated via `generate-bridge-coverage.sh` with pinned versions + SHA-256 hashes in `bridge-corpus/manifest.json`.

Add `BridgeSummary` to `binding-report.json`:
```json
{
  "BridgeSummary": {
    "TotalViews": 7,
    "Generated": 4,
    "Typechecked": 4,
    "RuntimeValidated": 2,
    "Template": 2,
    "HintSkipped": 1,
    "GeneratedPercent": 57.1,
    "RuntimeValidatedPercent": 28.6
  }
}
```

---

## Session Dependencies

```
Session 1A: Closures & Optionals    ✅ COMPLETE
    │
Session 1B: Non-primitive returns   (standalone — extends Session 1A patterns)
    │
Session 2: Generic Views            ✅ COMPLETE
    │
Session 3: Structs & Type Database  (standalone — benefits from Session 1A optional patterns)
    │
Session 4: Two-Way State            (standalone — architectural change to session class)
    │
Session 5: Lifecycle & Modifiers    (runtime modifiers depend on Session 4's ObservableObject)
    │
Session 6: Observable Binding       (depends on Session 4's ObservableObject wrapper)
           + Corpus Tracking        (standalone — measures everything above)
```

**Stop points**: After Sessions 1-3, the bridge covers the vast majority of real-world SwiftUI views. Sessions 4-6 add power and polish but aren't required for basic consumption.

---

## Out of Scope (Deliberate)

These are things the SwiftUI bridge will **not** attempt:

| Feature | Reason |
|---------|--------|
| Composing SwiftUI view trees from C# | Result builders are a compiler feature; no C# equivalent |
| Implementing `View` protocol in C# | `body` requires `some View` opaque return; not expressible cross-language |
| `@State` / `@Binding` / `@Environment` in C# | Compiler-synthesized storage; SwiftUI runtime manages lifecycle |
| Combine ↔ INotifyPropertyChanged bridge | Different reactive models; complexity not justified for bridge scenario |
| SwiftUI Previews from C# | Xcode-specific tooling; no .NET equivalent |
| Hot reload integration | Different reload mechanisms (SwiftUI vs .NET); not composable |
| Full modifier coverage | ~200 modifiers; curated subset only; rest should be written in Swift |

The product contract remains: **present SwiftUI View as UIViewController, with configuration from C#, callbacks to .NET, and lifecycle ownership.** The SwiftUI rendering pipeline stays in Swift.

---

## Success Metrics

| Metric | Before S1A | After S1A | After S2 | After S3 | After S4-6 |
|--------|------------|-----------|----------|----------|------------|
| Parameter types supported | 7 kinds | 11 kinds | 11 + generic | 15+ kinds | 15+ kinds |
| Closure arg types | Primitives only | + String, class | + String, class | + String, class | + String, class |
| Optional param types | Enum, class | + String, closure | + String, closure | + struct | + struct |
| Generic views bridged | 0% | 0% | View-constrained | 60%+ | 60%+ |
| Bridge rate (estimated) | ~70% | ~80% | ~85% | ~90% | ~95% |
| Post-creation state updates | No | No | No | No | Yes |
| Lifecycle/modifier support | No | No | No | No | Yes |
| Bridged views (TestFramework) | — | 15 | 17 | 20+ | 20+ |
| Unit tests (bridge) | — | 174 | 196 | 220+ | 250+ |
