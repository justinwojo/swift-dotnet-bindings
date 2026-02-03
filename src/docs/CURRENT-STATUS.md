# Swift Bindings - Current Status

**Last Updated**: February 2026 (Phase 43 Complete)
**Unit Tests**: 1032 passed
**Libraries Tested**: Nuke, BlinkID, Lottie

---

## Compilation Status

| Library | Generator Errors | Runtime Validation |
|---------|------------------|-------------------|
| **Nuke** | 0 ✅ | Full runtime validation |
| **BlinkID** | 0 ✅ | Compiles clean (no runtime tests yet) |
| **Lottie** | 0 ✅ | Runtime validated (8/9 tests pass) |

### Binding Coverage

| Library | Types | Type % | Members | Member % |
|---------|-------|--------|---------|----------|
| BlinkID | 116/119 | 97.5% | 559/655 | 85.3% |
| Nuke | 60/68 | 88.2% | 325/490 | 66.3% |
| Lottie | 79/93 | 84.9% | 365/609 | 59.9% |

Member coverage gaps are primarily due to unsupported signatures and existential edge cases. Target is 90%+ for common API patterns.

---

## What Works

### Types
- ✅ Classes (with ARC via SafeHandle)
- ✅ Structs (frozen and non-frozen)
- ✅ Enums (with associated values, raw representable, runtime enum case construction)
- ✅ Protocols (interface + proxy generation + conformance emission)
- ✅ Generics (bound generics, generic enums, generic classes)
- ✅ Actors (detected via Actor protocol conformance, emitted as classes with actor comment)

### Members
- ✅ Methods (instance, static, async)
- ✅ Properties (getters and setters)
- ✅ Operators (+, -, ==, !=, <, >, etc. with automatic pair synthesis)
- ✅ Constructors
- ✅ Subscripts (as C# indexers)

### Special Types
- ✅ SwiftString, SwiftArray<T>, SwiftSet<T>, SwiftOptional<T>
- ✅ Closures (@convention(c), @escaping with frozen types, throwing closures)
- ✅ Tuples (1-7 elements)
- ✅ Existential containers (protocol composition)
- ✅ Opaque return types (`some Protocol` → existential container via Swift wrapper)
- ✅ CoreGraphics opaque types (CGImage, CGColor, CGContext → IntPtr)

### DX Features
- ✅ Binding completeness report (`binding-report.json`)
- ✅ `[UnsupportedSwiftType]` attribute on degraded members
- ✅ Skip reasons in report (UnsupportedSignature, AnyTypeFallback, AsyncProperty, etc.)
- ✅ Configurable namespace mapping
- ✅ Async property detection via TBD symbol analysis

---

## What Doesn't Work

### Architectural Gaps
- ❌ **Protocol witness tables** - Full witness table handling
- ❌ **Actor isolation enforcement** - Actor methods callable without async/await from C# (Swift runtime handles isolation internally)

### Framework Limitations
- ❌ **SwiftUI** - Types with SwiftUI constraints skipped
- ❌ **Combine** - Reactive framework out of scope

### Edge Cases
- ❌ **8+ element tuples** - Would require ValueTuple nesting
- ❌ **Closures within closures** - Not supported
- ❌ **Generic associated types** - PATs limited
- ❌ **Async+throwing closures at runtime** - Binding generation works but runtime blocked by existential metadata Mono JIT bug

### Known Runtime Issues
- **Lottie**: `LottieConfiguration.Shared` property getter returns non-null object but property access throws `NullReferenceException` (1/9 test failure)
- **Mono JIT**: `swift_getExistentialTypeMetadata` crash when creating `SwiftArray<ExistentialContainer>` (workaround: Swift wrapper functions)
- **SafeHandle in async**: .NET runtime doesn't preserve SafeHandle through async P/Invoke (workaround: singleton pattern + IntPtr conversion)
- See `known-issues-workarounds.md` for full details

---

## Recent Completions (Phase 43)

### Protocol Conformance Emission
- Types now emit C# interfaces for same-module protocol conformances
- `SimpleItem : ISwiftObject, ISwiftDescribable, ISwiftTestIdentifiable`
- Works across classes, structs, and enums

### Opaque Return Types (`some Protocol`)
- `OpaqueTypeArchetype` parsed from ABI JSON → `ProtocolListTypeSpec { IsOpaque = true }`
- Swift wrappers generated to box concrete returns into existential containers
- Property getters and methods both supported

### Async Property Detection
- Async getters detected via TBD symbol `mangledName + "Tu"` suffix
- Properly skipped with `SkipReason.AsyncProperty` (previously emitted as synchronous)

### Actor Type Support
- Actors detected via `Actor` protocol conformance in ABI JSON
- `IsActor` flag on `ClassDecl`, `unownedExecutor` property filtered
- Generated with `// Swift actor type` comment

### Bug Fixes
- Fixed NullReferenceException in MethodHandler for top-level async functions
- Fixed `CacluateFlags` crash for unknown generic types (e.g., `Swift.KeyPath`)
- Fixed test reflection for `DemanglingResults` constructor with `AllSymbols` parameter

### Previous Phase (42) Highlights
- Lottie: 8/9 runtime tests pass on iOS Simulator
- Enum case construction fix (DestructiveInjectEnumTag)
- CoreGraphics type stubs (CGImage, CGColor, etc.)

### Test Coverage
- Unit tests: 1032
- Integration tests: 678 passed
- Runtime tests: 108 passed

---

## Active Documentation

| File | Purpose |
|------|---------|
| `north-star.md` | Project vision and roadmap |
| `codex-task-specs.md` | Current phase task specifications |
| `comprehensive-architecture-review.md` | Strategic direction and priorities |
| `emitter-redesign-proposal.md` | Future architecture improvement plan |
| `known-issues-workarounds.md` | Runtime issues and workarounds |
| `swift-concurrency-interop-plan.md` | Async/concurrency interop design (partially implemented) |

---

## Development History

43 phases of improvements tracked in git history. Key milestones:
- Phase 1-15: Core infrastructure and Nuke validation
- Phase 16-29: Type system and runtime fixes
- Phase 30-33: Generic type improvements
- Phase 34-39: Codex task completion (operators, enums, reporting)
- Phase 40: Protocol conformance infrastructure, namespace mapping
- Phase 41: Generic type fixes, 0 generator errors achieved
- Phase 42: Lottie runtime validation, enum case construction, CoreGraphics stubs
- Phase 43: Protocol conformance emission, opaque returns, async properties, actors
