# Swift Bindings - Current Status

**Last Updated**: February 2026 (Phase 42 Complete)
**Unit Tests**: 1032 passed
**Libraries Tested**: Nuke, BlinkID, Lottie

---

## Compilation Status

| Library | Generator Errors | Runtime Validation |
|---------|------------------|-------------------|
| **Nuke** | 0 ✅ | Full runtime validation |
| **BlinkID** | 0 ✅ | Compiles clean |
| **Lottie** | 0 ✅ | Runtime validated (8/9 tests pass) |

---

## What Works

### Types
- ✅ Classes (with ARC via SafeHandle)
- ✅ Structs (frozen and non-frozen)
- ✅ Enums (with associated values, raw representable, runtime enum case construction)
- ✅ Protocols (interface + proxy generation)
- ✅ Generics (bound generics, generic enums, generic classes)

### Members
- ✅ Methods (instance, static, async)
- ✅ Properties (getters and setters)
- ✅ Operators (+, -, ==, !=, <, >, etc.)
- ✅ Constructors
- ✅ Subscripts (as C# indexers)

### Special Types
- ✅ SwiftString, SwiftArray<T>, SwiftSet<T>, SwiftOptional<T>
- ✅ Closures (@convention(c), @escaping with frozen types)
- ✅ Tuples (1-7 elements)
- ✅ Existential containers (protocol composition)
- ✅ CoreGraphics opaque types (CGImage, CGColor, CGContext → IntPtr)

### DX Features
- ✅ Binding completeness report (`binding-report.json`)
- ✅ `[UnsupportedSwiftType]` attribute on degraded members
- ✅ Skip reasons in report (UnsupportedSignature, AnyTypeFallback, etc.)
- ✅ Configurable namespace mapping

---

## What Doesn't Work

### Architectural Gaps
- ❌ **Async properties** - Properties with async getters
- ❌ **Actors** - Swift actor types
- ❌ **Protocol witness tables** - Full witness table handling

### Framework Limitations
- ❌ **SwiftUI** - Types with SwiftUI constraints skipped
- ❌ **Combine** - Reactive framework out of scope

### Edge Cases
- ❌ **8+ element tuples** - Would require ValueTuple nesting
- ❌ **Closures within closures** - Not supported
- ❌ **Generic associated types** - PATs limited

---

## Recent Completions (Phase 42)

### Lottie Runtime Validation
- 8/9 runtime tests pass on iOS Simulator
- LottieColor creation, animation loading, vector types, enum cases all work
- 1 pre-existing failure: `LottieConfiguration.Shared` property getter NullRef

### Enum Case Construction Fix
- Simple enum cases now use `DestructiveInjectEnumTag` (not P/Invoke)
- Enum case symbols (`...mF`) are not exported; `...mFWC` are data, not functions
- Non-frozen enum parameters use scoped `EnumSafeHandle` → `IntPtr` for `CallConvSwift`

### CoreGraphics Type Stubs
- CGImage, CGColor, CGContext, CGColorSpace → IntPtr (opaque handles)
- Ref suffix alias resolution (CGImage ↔ CGImageRef)
- Lottie skipped members reduced: 63 → 59

### Test Coverage
- Unit tests: 1032 (up from 1029)
- Integration tests: 678 passed
- Runtime tests: 108 passed

---

## Key Files

| File | Purpose |
|------|---------|
| `north-star.md` | Project vision and roadmap |
| `known-issues-workarounds.md` | Runtime issues (Mono JIT bugs) |
| `emitter-redesign-proposal.md` | Architecture improvement plan |

---

## Development History

42 phases of improvements tracked in git history. Key milestones:
- Phase 1-15: Core infrastructure and Nuke validation
- Phase 16-29: Type system and runtime fixes
- Phase 30-33: Generic type improvements
- Phase 34-39: Codex task completion (operators, enums, reporting)
- Phase 40: Protocol conformance infrastructure, namespace mapping
- Phase 41: Generic type fixes, 0 generator errors achieved
- Phase 42: Lottie runtime validation, enum case construction, CoreGraphics stubs
