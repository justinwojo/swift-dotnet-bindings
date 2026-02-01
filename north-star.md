# Swift Bindings: North Star Vision

## Mission

**Enable any .NET developer to consume any Swift library with the same ease as consuming a NuGet package.**

Just as Objective Sharpie made Objective-C accessible to Xamarin developers, Swift Bindings will make the modern Swift ecosystem accessible to the .NET platform.

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
> "I'm implementing in-app purchases and need StoreKit 2. I want to call `Product.products()` directly from C#."

### 3. Binding Author
> "I maintain a popular Swift library and want to publish official .NET bindings so the MAUI community can use it."

---

## Quality Attributes

| Attribute | Target |
|-----------|--------|
| **Completeness** | 90%+ of public API surface bindable |
| **Correctness** | Memory-safe, crash-free for supported features |
| **Ergonomics** | C# code feels idiomatic, not like translated Swift |
| **Performance** | Minimal interop overhead |
| **Discoverability** | Full IntelliSense/autocomplete support |

---

## Technical Roadmap

### Phase 1: Foundation Completion
**Goal**: Any frozen-type Swift library can be fully bound.

| Feature | Status | Priority |
|---------|--------|----------|
| Async P/Invoke SafeHandle fix | **Done** | P0 |
| Property setters | **Done** | P0 |
| Enum case constructors | **Done** | P1 |
| Foundation type coverage (URL, Data, etc.) | **Done** | P1 |
| Cross-platform generation (iOS bindings on macOS) | **Done** | - |

**Success Criteria**: Nuke library async image loading works end-to-end. ✅ ACHIEVED

### Phase 2: Type System Completeness
**Goal**: Handle the full spectrum of Swift types.

| Feature | Status | Priority |
|---------|--------|----------|
| Existential containers (`any Protocol`) | **Done** | P0 |
| Generic method support | **Done** | P0 |
| Protocol witness tables | Not Started | P1 |
| Unbound generic types | Not Started | P1 |
| Protocols with Associated Types (PATs) | Partial | P2 |

**Success Criteria**: Methods with existential parameters and generic methods bind successfully.

### Phase 3: Developer Experience
**Goal**: Streamlined workflow comparable to legacy Xamarin binding projects.

| Feature | Description |
|---------|-------------|
| MSBuild SDK | `<Project Sdk="Swift.Bindings.Sdk">` |
| ABI extraction automation | No manual `swift-frontend` commands |
| Project templates | `dotnet new swift-binding` |
| NuGet packaging | Automatic xcframework bundling |
| Error diagnostics | Clear messages for unsupported features |

**Success Criteria**: Single `dotnet build` from xcframework to NuGet package.

### Phase 4: Advanced Features
**Goal**: Handle complex Swift patterns.

| Feature | Status |
|---------|--------|
| Actors | Not Started |
| `@MainActor` annotations | Not Started |
| Sendable protocol | Partial |
| Throwing async methods | Partial |
| Full enum payloads (discriminated unions) | Partial |

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
| SwiftUI interop | Requires deep UI framework integration |
| C# → Swift bindings | Reverse direction is a separate project |
| Windows/Linux support | Apple platforms only |
| Swift Package Manager integration | Focus on compiled frameworks |
| Objective-C bridging | Existing tools handle this |

---

## Current State (January 2026)

### What Works
- Classes, structs (frozen and non-frozen)
- Instance and static methods, property getters and setters
- Async methods (with frozen and non-frozen parameter types)
- Closures (`@convention(c)`, `@escaping` with frozen types, bound generics, existentials)
- Tuples (1-7 elements with frozen types and existentials)
- Operators (arithmetic, comparison, bitwise, unary)
- Basic enums (with payload construction for associated values)
- **RawRepresentable enums** (both frozen and non-frozen) - `FromRawValue()` and static case properties
- SwiftString, SwiftArray<T>, SwiftSet<T>, SwiftOptional<T>, SwiftResult<S,F>
- Existential types (`any Protocol`) - parameters, returns, properties, closures, tuples
- Generic constructors and generic methods (with where clause constraints)
- StoreKit 2 bindings (published as experimental NuGet)
- Comprehensive test coverage (unit, integration, and runtime tests)

### What Doesn't Work Yet
- Actors - unsupported
- Full protocol witness table handling
- Protocols with Associated Types (PATs) - partial support

### Key Gaps (from Nuke Testing)
The Nuke image library binding attempt revealed:
- ~19 methods with unsupported signatures (mostly `AnyType`/existential types in dictionaries)
- See `/src/docs/nuke-binding-roadmap.md` for detailed tracking

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

| Metric | Current | v1.0 Target | v2.0 Target |
|--------|---------|-------------|-------------|
| Nuke API coverage | ~60% | 90% | 98% |
| Compilation errors | 0 | 0 | 0 |
| Runtime crashes | Some | 0 (supported) | 0 |
| Manual steps to bind | 5+ | 1 | 1 |

---

## References

### In This Repository
- `/docs/binding-overview.md` - Binding philosophy
- `/src/docs/emitter-redesign-proposal.md` - Architecture improvements
- `/src/docs/nuke-binding-roadmap.md` - Real-world testing and gap tracking
- `/src/docs/nuke-binding-roadmap.md` - Incremental progress tracking

### External
- [Swift ABI Stability Manifesto](https://github.com/apple/swift/blob/main/docs/ABIStabilityManifesto.md)
- [Swift Calling Convention](https://github.com/apple/swift/blob/main/docs/ABI/CallingConvention.rst)
