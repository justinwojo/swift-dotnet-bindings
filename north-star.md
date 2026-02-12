# Swift Bindings: North Star Vision

## Mission

**Enable any .NET developer to consume Swift libraries with the same ease as consuming a NuGet package.**

Just as Objective Sharpie made Objective-C accessible to Xamarin developers, Swift Bindings will make the modern Swift ecosystem accessible to the .NET platform.

*Realistic target: 90%+ of public API member coverage in libraries following common Swift patterns, with clear diagnostics and escape hatches for edge cases.*

---

## The Problem

Apple is actively moving away from Objective-C toward Swift-only APIs:

- **StoreKit 2** - Swift-only, no ObjC equivalent
- **SwiftUI** - Swift-only UI framework
- **WeatherKit** - Swift-only
- **App Intents** - Swift-only
- And more every WWDC...

The current workaround is unsustainable:
1. Create a Swift "proxy" library that wraps the Swift API
2. Expose that proxy via `@objc` attributes
3. Use Objective Sharpie to generate ObjC bindings
4. Maintain both the proxy and the bindings

This is complex, error-prone, and doubles the maintenance burden. Without native Swift interop, **.NET on Apple platforms becomes progressively less capable** with each iOS/macOS release.

---

## The End State

A developer who wants to use a Swift library in their .NET app:

```bash
# 1. Create a binding project
dotnet new swift-binding -n Nuke.Bindings

# 2. Add the xcframework to the project
# (drag-drop or edit .csproj)

# 3. Build to generate bindings + NuGet package
dotnet build

# 4. Distribute
dotnet nuget push ./bin/Release/Nuke.Bindings.1.0.0.nupkg

# 5. Consume in any .NET app
dotnet add package Nuke.Bindings
```

**No Swift knowledge required. No manual wrapper code. No proxy libraries.**

---

## Target Users

### 1. Third-Party Library Consumer
> "I'm building a .NET MAUI app and want to use the Nuke image library. I just want to add a NuGet package and call the API."

### 2. Apple Framework Consumer
> "I'm implementing in-app purchases and need StoreKit 2. I want to call `Product.Products()` directly from C#."

### 3. Binding Author
> "I maintain a popular Swift library and want to publish official .NET bindings so the MAUI community can use it."

---

## Quality Attributes

| Attribute | Target |
|-----------|--------|
| **Completeness** | 90%+ of public API surface bindable (aspire to high 90s) |
| **Correctness** | Memory-safe, crash-free for supported features |
| **Ergonomics** | C# code feels idiomatic, not like translated Swift |
| **Performance** | Minimal interop overhead |
| **Discoverability** | Full IntelliSense/autocomplete support |

---

## Technical Roadmap

### Phase 1: Foundation Completion ✅

**Goal**: Any frozen-type Swift library can be fully bound.

All items complete: async P/Invoke workaround, property setters, enum case constructors, Foundation type coverage, cross-platform generation, namespace mapping, binding completeness report, UnsupportedType placeholders.

**Success Criteria**: Nuke library async image loading works end-to-end. ✅

### Phase 2: Type System Completeness (mostly complete)

**Goal**: Handle the full spectrum of Swift types.

| Feature | Status |
|---------|--------|
| Existential containers (`any Protocol`) | **Done** |
| Generic method support | **Done** |
| Unbound generic type parameters | **Done** |
| Protocol witness tables (blittable + String) | **Done** |
| Protocol witness tables (full — mutating, throws, async) | Remaining |
| Protocols with Associated Types (PATs) | Partial |
| TypeGraph layer for structural types | Not Started |
| Formalize cross-module resolution | **Partial** — TypeDatabase lookup for async bridge params |

**TypeGraph layer**: Extract a `TypeGraph`/`CompositeTypeFactory` layer that builds complex types from nominal types, keeping TypeDatabase nominal-only. Aligns with emitter redesign proposal.

**Cross-module resolution**: Async bridge inference resolves cross-module types (BoundType/BoundEnum) via TypeDatabase with auto-populated ExtraSwiftImports and null-pointer safety guards. Full formalization (explicit config, diagnostics, non-bridge contexts) deferred until more patterns emerge.

**Success Criteria**: Methods with existential parameters and generic methods bind successfully. ✅

### Phase 3: Developer Experience

**Goal**: Streamlined workflow comparable to legacy Xamarin binding projects.

| Feature | Description |
|---------|-------------|
| MSBuild SDK | `<Project Sdk="Swift.Bindings.Sdk">` |
| ABI extraction automation | No manual `swift-frontend` commands |
| Project templates | `dotnet new swift-binding` |
| NuGet packaging | Automatic xcframework bundling |
| Error diagnostics | Clear messages for unsupported features |
| Configuration versioning | Versioned config schema with hash in output |

**Success Criteria**: Single `dotnet build` from xcframework to NuGet package.

### Phase 4: Advanced Features

**Goal**: Handle complex Swift patterns.

| Feature | Status |
|---------|--------|
| Actors | Detection works; isolation enforcement not started |
| `@MainActor` annotations | Not Started |
| Sendable protocol | Partial |
| Throwing async methods | Generation works; runtime blocked by Mono JIT bug |
| Full enum payloads (discriminated unions) | Mostly complete |

### Phase 5: Ecosystem Integration

**Goal**: Production-ready for the .NET ecosystem.

| Feature | Description |
|---------|-------------|
| Official Apple framework bindings | StoreKit, HealthKit, etc. as NuGet packages |
| CI/CD templates | GitHub Actions, Azure Pipelines |
| Documentation generator | API docs from Swift doc comments |

---

## Non-Goals

To maintain focus, these are explicitly **out of scope**:

| Non-Goal | Rationale |
|----------|-----------|
| SwiftUI interop (deep) | `@State`/`@Binding`/`@Environment` semantics remain out of scope; auto-generated UIHostingController bridge covers View instantiation |
| C# → Swift bindings | Reverse direction is a separate project |
| Windows/Linux support | Apple platforms only |
| Swift Package Manager integration | Focus on compiled frameworks |
| Objective-C bridging | Existing tools handle this |

---

## Realistic Scope & Limitations

### The Coverage Target

The 90%+ target reflects a universal ceiling across all cross-language interop projects:

| Project | Language Pair | Typical Coverage |
|---------|---------------|------------------|
| CppSharp | C++ → C# | ~80% of POD types |
| SWIG | Multi-language | ~85% with manual work |
| JNI | Java ↔ Native | ~90% with boilerplate |
| Kotlin/Native | Kotlin ↔ C | ~85-90% |
| **Swift Bindings** | Swift → C# | **90%+ target** |

Full 100% coverage is not achievable for any cross-language interop tool. We aspire to the high 90s for member coverage on libraries following common Swift patterns, with the understanding that the remaining few percent requires manual wrapper code.

### What Falls in the Hard-to-Bind Tail

| Feature | Why It's Hard | Workaround |
|---------|---------------|------------|
| **Actors** | Swift's actor isolation model doesn't map to .NET threading | Manual Swift wrapper |
| **Protocols with Associated Types (PATs)** | Exponential type complexity | Concrete type wrappers |
| **SwiftUI deep state** | `@State`/`@Binding`/`@Environment` don't map to C# | Auto-generated UIHostingController bridge for View instantiation |
| **Combine publishers** | Reactive paradigm mismatch with .NET | Use async/await patterns |
| **`@MainActor` constraints** | Thread affinity doesn't map cleanly | Dispatch manually |
| **8+ element tuples** | C# ValueTuple nesting complexity | Restructure API |
| **Closures within closures** | ABI complexity | Flatten callback structure |

### The Escape Hatch: Swift Wrappers

For APIs that cannot be directly bound (due to .NET runtime limitations or Swift complexity), the generator supports a **Swift wrapper fallback**:

```
Normal API:      C# → P/Invoke → Swift dylib
Edge case API:   C# → P/Invoke → Swift wrapper → Swift dylib
```

**Why this works:** The Swift compiler is the only entity guaranteed to understand the Swift ABI perfectly. When .NET's JIT or marshalling fails, delegating to Swift-side code is architecturally correct.

The binding report (`binding-report.json`) documents which APIs use wrappers and why.

### Implications for Users

1. **Most libraries will "just work"** — 0 generator errors across tested libraries
2. **Some APIs may be skipped** — The binding report explains what and why
3. **Edge cases have workarounds** — Swift wrapper functions handle runtime limitations
4. **SwiftUI Views are bridgeable** — Auto-generated UIHostingController bridge embeds Views in .NET apps; deep state management (`@State`/`@Binding`) remains out of scope

---

## Architecture Overview

```
Swift Framework (.xcframework)
         │
         ├── .swiftinterface (ABI contract)
         ├── .dylib (native code)
         └── .tbd (symbol table)
         │
         ▼
┌─────────────────────────────────────┐
│       Swift.Bindings Tool           │
├─────────────────────────────────────┤
│  SwiftABIParser → TypeDatabase →    │
│  Marshaler → Emitter                │
└─────────────────────────────────────┘
         │
         ▼
┌─────────────────────────────────────┐
│       Generated Output              │
├─────────────────────────────────────┤
│  Swift.{Module}.cs   (C# bindings)  │
│  Swift.{Module}.swift (async wrap)  │
│  NuGet Package                      │
└─────────────────────────────────────┘
         │
         ▼
    .NET Application
```

---

## Success Metrics

| Metric | v1.0 Target | v2.0 Target |
|--------|-------------|-------------|
| Member API coverage | 90%+ | High 90s |
| Compilation errors | 0 | 0 |
| Runtime crashes (supported features) | 0 | 0 |
| Manual steps to bind | 1 (`dotnet build`) | 1 |

---

## References

### In This Repository
- `/docs/design/binding-overview.md` - Binding philosophy
- `/src/docs/CURRENT-STATUS.md` - Current compilation status and coverage
- `/src/docs/roadmap.md` - Active work queue
- `/src/docs/Future/emitter-redesign-proposal.md` - Architecture improvements

### External
- [Swift ABI Stability Manifesto](https://github.com/apple/swift/blob/main/docs/ABIStabilityManifesto.md)
- [Swift Calling Convention](https://github.com/apple/swift/blob/main/docs/ABI/CallingConvention.rst)
