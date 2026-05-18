# Session 4 — Typed singleton trampoline emission for closed-conformer KeyPath construction

The IN-path session of the subsystem. After it ships, the C# caller can originate a `KeyPath<TRoot, TValue>` from C# (without needing to call any Swift API) for any closed-conformer-rooted nested type — the canonical example being `Album.LibraryFilter` from MusicKit.

This is the structural reason the subsystem is the size of the closure subsystem: most KeyPath-consuming APIs need the *C# caller* to originate the path, not just receive it from Swift.

## Goal

For each closed conformer of a PAT-constrained generic type whose generic parameter's associated type is a `KeyPath`-rooted bag (e.g. `MusicLibraryRequestable.LibraryFilter`), the generator emits:

1. **One Swift `@_cdecl` trampoline per stored property of the bag.** Each trampoline contains the `keypath` SIL instruction for `\AlbumLibraryFilter.title` (or whichever) and returns the retained `AnyKeyPath` via `Unmanaged.passRetained(...).toOpaque()`.
2. **One C# `public static readonly` field per property** on the C# side, typed to the most-specific KeyPath subclass (`KeyPath` / `WritableKeyPath` / `ReferenceWritableKeyPath`), initialized lazily via the trampoline.

End-to-end test: a Swift type `MockFilterableItem` exposes a nested `LibraryFilter` struct with three properties (`title: String`, `year: Int?`, `isExplicit: Bool`); the generator emits typed singletons; C# consumes them and reads through `MockFilterableItem.LibraryFilter.title` etc., observing correct value-equality and round-trip through a consumer Swift method.

## Why this session

- Without it, KeyPath-consuming APIs (every consumer in Sessions 6–10) are still unreachable from C# even with the foundation in place.
- It's the structural complement of Session 3's OUT-path: Session 3 gives C# a way to *hold* a KeyPath returned from Swift; Session 4 gives C# a way to *originate* one.
- The work is contained: a new "KeyPath property walker" hooked into the existing conformer-specialisation loop, leveraging the existing WrapperEmitter pattern.

## Dependencies

- **Session 3** (KeyPath foundation) — type records, `KeyPathProjection`, runtime SafeHandle, P/Invoke marshalling.

Independent of Sessions 1, 2, 5 (those are CSM/property-handler work, not KeyPath).

## Approach

The conformer-specialisation loop (`ConcreteSpecializationEngine.FindSpecializableMethods` + `EmitConcreteSpecializationsForGenericParent` in `ConcreteProtocolSpecializationEmitter.cs:2374–2499`) already walks the closed conformers of PAT-constrained types. Hook a new "KeyPath singleton walker" into that loop. For each closed conformer's associated-type bag (the `LibraryFilter`-shaped nested type), walk `TypeDecl.Properties` (`TypeDecl.cs:25`) and emit per-property:

- Swift side: a `@_cdecl` trampoline emitted into the existing per-namespace `{Namespace}.Wrapper.swift` file (`ModuleEmitter.cs:140`).
- C# side: a `public static readonly KeyPath<TRoot, TValue> {PropertyName}` field on a generated container class (likely `{Module}.{ParentTypeName}{ConformerName}LibraryFilterKeyPaths` or similar naming).

The Swift trampoline is the only place a `keypath` SIL instruction can be emitted (constraint: no C-callable component-wise builder exists per `_swift_keyPath_create` — see `00-overview.md`).

## Naming conventions

Container class shape: `{ConformerTypeName}{NestedBagTypeName}KeyPaths`. For MusicKit:
- `AlbumLibraryFilterKeyPaths` (with `Title`, `Artist`, `ReleaseDate`, `IsExplicit`, …)
- `SongLibraryFilterKeyPaths`
- One container per (closed conformer × nested bag) pair.

Field name: `PascalCase`-projected from Swift property name (matches existing property emission convention).

Symbol name (Swift `@_cdecl`): `SBW_KP_{Module}_{Conformer}_{Bag}_{Property}_{hash8}` — mirror `MethodWrapperEmitter.GetMethodSymbolName` (`MethodWrapperEmitter.cs:188–192`) pattern.

The container class lives in the same namespace as the conformer's bindings to avoid cross-assembly visibility issues (constraint #29 — cross-module proxy class qualification). Confirm by tracing an existing closed-conformer-emission site (e.g. one of the BindingTests CSM fixtures).

## Session 4 work breakdown

### Phase 4.1 — Detect KeyPath-rooted-bag conformers

Inside the conformer-specialisation loop, after closed-conformer substitution, inspect the PAT's associated-type-typed members. If a conformer's substituted associated type:
- Is a stored-property-bearing struct or class (a "bag"), AND
- Is referenced as a `Root` of a `KeyPath<...>` parameter in any method of the parent generic type,

then schedule it for singleton emission.

Concretely, walk `TypeDecl.Types` (`TypeDecl.cs:35`) on the conformer or its associated types — `CrossModuleExtensionEmitter.cs:263` already does nested-type recursion; mirror that.

A KeyPath-rooted-bag is detected by `IsKeyPathFamily` flag on the TypeRecord (from Session 3) matched against the parameter types of the parent's methods. Build a `HashSet<(Conformer, BagTypeDecl)>` per parent type, deduplicated across methods.

### Phase 4.2 — Swift wrapper emission

For each `(Conformer, BagTypeDecl)` pair, for each property on the bag:

```swift
// Generated into {Namespace}.Wrapper.swift
@_cdecl("SBW_KP_MusicKit_Album_LibraryFilter_title_<hash>")
public func SBW_KP_MusicKit_Album_LibraryFilter_title_<hash>() -> UnsafeMutableRawPointer {
    let kp: KeyPath<MusicKit.Album.LibraryFilter, String> = \MusicKit.Album.LibraryFilter.title
    return Unmanaged.passRetained(kp).toOpaque()
}
```

The static-type spelling on the `let kp:` line picks the most-specific Writable/ReferenceWritable flavor (per SIL evidence in `00-overview.md`, `\Type.prop` for a `var` becomes `WritableKeyPath` by default). The C# side uses the same flavor.

Wrapper file routing: route through `WrapperEmitter.EmitMethod` (`WrapperEmitter.cs:527`) or a sibling entry point with a new `EmitKeyPathSingleton(TypeDecl conformer, TypeDecl bag, PropertyDecl prop)` method. Reuse `EmitCdeclWrapper` / `EmitCdeclAnnotation` (`ConcreteProtocolSpecializationEmitter.cs:831,841`).

Each trampoline is a "pseudo-method" from the engine's perspective — no original Swift method exists; the trampoline is synthesised. Confirm the symbol-collision-detection path (`SBW_` symbol dedup) handles synthesised symbols without false positives.

### Phase 4.3 — C# static field emission

For each `(Conformer, BagTypeDecl)` pair, emit a container class:

```csharp
namespace MusicKit
{
    public static class AlbumLibraryFilterKeyPaths
    {
        private static KeyPath<Album.LibraryFilter, Swift.String>? _title;
        public static KeyPath<Album.LibraryFilter, Swift.String> Title =>
            _title ??= AdoptKeyPath<Album.LibraryFilter, Swift.String>(SBW_KP_MusicKit_Album_LibraryFilter_title_xxxxxxx());

        // …one per property…

        [DllImport(/* wrapper lib */, EntryPoint = "SBW_KP_MusicKit_Album_LibraryFilter_title_xxxxxxx", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr SBW_KP_MusicKit_Album_LibraryFilter_title_xxxxxxx();

        // …P/Invokes…

        private static KeyPath<TRoot, TValue> AdoptKeyPath<TRoot, TValue>(IntPtr retainedHandle) =>
            new KeyPath<TRoot, TValue>(retainedHandle);
    }
}
```

**Why lazy init via `??=`** — eager `static readonly` initialisation runs in the type-init cctor which can deadlock or run during process-wide static init in NativeAOT. Lazy init pushes the `swift_getKeyPath` call to first use. Pattern: confirm with one of the existing closed-conformer static fields if any exist, or mirror the `ClosureEmitter`'s deferred-init pattern.

`AdoptKeyPath` lives in `Swift.Runtime` (or local — re-emitted into each container — choose based on whether `Swift.Runtime` already exposes a public adopt helper). Avoid code duplication: one `Swift.Runtime` helper, call sites are thin.

Writable variant: when the bag property is `var`, emit the field as `public static readonly WritableKeyPath<TRoot, TValue> Title` and adopt via `new WritableKeyPath<...>(handle)`. The Swift compiler picks the type on the `let kp:` declaration; the C# field type must match.

### Phase 4.4 — Routing in the engine

Today the conformer-specialisation loop only iterates over methods. Extend `FindSpecializableMethods` (or a sibling `FindSpecializableKeyPathBags`) to also emit per-bag tasks. Or, more cleanly, add a separate pass `EmitKeyPathSingletonsForGenericParent` that runs alongside `EmitConcreteSpecializationsForGenericParent`.

Pipeline integration: `MemberValidationPipeline` already orchestrates method emission. Add a new pipeline phase or sub-phase for "KeyPath singleton emission" so the work is visible in the pipeline trace and `WasEmitted` flags settle correctly (constraint #18).

### Phase 4.5 — BindingTests fixture

Swift: `BindingTests/Sources/SwiftBindingsTestLib/KeyPath/KeyPathSingletons.swift`:

```swift
public protocol Filterable {
    associatedtype LibraryFilter
    static var defaultFilter: LibraryFilter { get }
}

public struct MockBook: Filterable {
    public struct LibraryFilter {
        public var title: String = ""
        public var year: Int? = nil
        public var isFiction: Bool = false
    }
    public static let defaultFilter = LibraryFilter()
}

public struct MockMovie: Filterable {
    public struct LibraryFilter {
        public var title: String = ""
        public var runtimeMinutes: Int = 0
    }
    public static let defaultFilter = LibraryFilter()
}

public struct Bag<Item: Filterable> {
    public init() {}

    // Method that takes a KeyPath rooted in Item.LibraryFilter — exercises the IN path
    public func count<Value>(matching keyPath: KeyPath<Item.LibraryFilter, Value>, equalTo value: Value) -> Int where Value: Equatable {
        // implementation doesn't matter — we just need this to bind
        return 0
    }
}
```

C#: `BindingTests/RuntimeTestsApp/KeyPath/KeyPathSingletonTests.cs`. Cover:

- **Generated container exists** — `MockBookLibraryFilterKeyPaths.Title` and `MockBookLibraryFilterKeyPaths.Year` and `MockBookLibraryFilterKeyPaths.IsFiction` are reachable.
- **Singleton value-equality across uses** — `MockBookLibraryFilterKeyPaths.Title.Equals(MockBookLibraryFilterKeyPaths.Title)` returns true; calling twice returns the same path (within-TU interning).
- **Two-conformer separation** — `MockBookLibraryFilterKeyPaths.Title` and `MockMovieLibraryFilterKeyPaths.Title` are *different* (typed `KeyPath<MockBook.LibraryFilter, ...>` vs `KeyPath<MockMovie.LibraryFilter, ...>`); `.Equals(…)` returns false (different roots).
- **Writable flavor** — `Year` property is `var`, becomes `WritableKeyPath<...>`. Confirm static C# type tag.
- **Composition with consumer method** — call `bag.Count(matching: MockBookLibraryFilterKeyPaths.Title, equalTo: "Foo")` — proves the IN-path marshalling round-trips correctly.
- **Optional Value variance** — `Year` is `Int?`. The `KeyPath<MockBook.LibraryFilter, Int?>` type spelling must compose with the Optional projection (Session 3 covered this on the OUT side; verify here on the IN side).
- **Cross-CU equality** — the same path materialised in module A (the fixture) and read by a consumer in module B (a second test fixture, if feasible) must value-equal. If not feasible to set up two modules in BindingTests, defer this to a Session-3.5-style follow-up.
- **NativeAOT** — device run must pass. Static-field laziness behaves differently under NativeAOT type-init; verify no static-init deadlock.

## Validation gates

| Gate | Expected |
|---|---|
| `nuke test` | New emitter unit tests for KeyPath singleton container generation pass |
| `nuke binding-tests` (sim) | `KeyPathSingletonTests` passes |
| `nuke binding-tests --device` | Same — NativeAOT static-init paths exercised |
| `nuke validate` | Baseline holds; emit count may rise (new container classes for any already-bound conformer of a KeyPath-using PAT — likely zero unless a validation lib already exposes this shape) |

## Exit criteria

- Generator detects KeyPath-rooted-bag conformers.
- Swift `@_cdecl` trampolines emit per-property into the namespace's wrapper file.
- C# container classes emit with lazy-init `static readonly` typed fields.
- BindingTests fixture covers all eight scenarios above.
- Sim + device green.
- Validate baseline holds.

## Risks specific to Session 4

- **Risk A (static-init deadlock on NativeAOT)** — eager `static readonly` init runs in the type-init cctor; if a generated container's first field touches Swift runtime before `SwiftCore` is initialised, NativeAOT can deadlock or null-pointer. **Mitigation:** lazy init via `??=` or `LazyInitializer.EnsureInitialized`. **Diagnostic:** the device test must run from a cold start.
- **Risk B (symbol-collision on synthesised cdecl names)** — `SBW_KP_*` collides with method `SBW_*` symbols if the hash overlap is non-trivial. **Mitigation:** include a distinct prefix (`SBW_KP_` vs `SBW_`) and include the bag type name in the hash input. **Diagnostic:** unit test on `EmitterUtility.DeterministicHash8` plus a build-time symbol dedup check.
- **Risk C (Singleton not value-equal across uses on different threads)** — `swift_getKeyPath` has a per-descriptor once-token. If two threads race the first invocation, both may compute the KeyPath; only one wins the token. Both *should* return value-equal results (same pattern bytes). **Diagnostic:** concurrent-access test in the fixture — call `MockBookLibraryFilterKeyPaths.Title` from N tasks in parallel; assert all results value-equal.
- **Risk D (open-generic-rooted KeyPath leaks past the closed-conformer filter)** — A method `Bag<T>.count(matching: KeyPath<T.LibraryFilter, V>, …)` is open-generic-rooted. The closed-conformer emission path resolves it per conformer (`MockBook`, `MockMovie`), but if the engine accidentally tries to emit a singleton against the open `T.LibraryFilter`, the generator will fail. **Diagnostic:** explicit reject in `FindSpecializableKeyPathBags` for open-associated-type bags; assert via a unit test on a synthetic `TypeDecl` that exposes a `T.LibraryFilter` reference.
- **Risk E (Writable-typed `var` property in the bag triggers a `WritableKeyPath` field, but consumer wants `KeyPath`)** — Most consumers (MusicKit `filter`) want plain `KeyPath` even though `\Album.LibraryFilter.title` literal compiles to `WritableKeyPath`. **Decision:** emit two field forms — `Title` (typed as the most-specific Writable form, the literal's natural type) and possibly `TitleReadOnly` (typed as plain `KeyPath` for callers that prefer it). Or rely on C# implicit upcast: `WritableKeyPath<R,V> : KeyPath<R,V>` so the same field satisfies both consumer needs. **Pick the upcast option** (per `00-overview.md` design decision); single field, most-specific type, C# inheritance carries the rest.
- **Risk F (Generic-substitution of `Item.LibraryFilter` to `MockBook.LibraryFilter` in the C# field type)** — The field is emitted on `MockBookLibraryFilterKeyPaths`, so the `TRoot` slot is fully substituted to the closed conformer's nested type at generation time. No open generic remains. Confirm by inspecting the generated `.cs` for absence of any open generic param spelling.
- **Risk G (`PartialKeyPath` typed-singleton shape)** — SwiftData uses `PartialKeyPath`, so Session 4 may need a Partial variant: `MockBookLibraryFilterPartialKeyPaths.Year` typed as `PartialKeyPath<MockBook.LibraryFilter>`. **Decision:** emit both typed `KeyPath` and `PartialKeyPath` flavors for each property when the consumer surface demands it. Driven by Session 3's `PartialKeyPath` foundation; here it's an extra emission flag. Add a fixture variant.

## References

- `00-overview.md` — design decision for typed singletons, ABI for the trampoline shape
- `src/Swift.Bindings/src/Marshaler/ConcreteSpecializationEngine.cs:534`
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ConcreteProtocolSpecializationEmitter.cs:2374–2499` (conformer specialisation emission)
- `src/Swift.Bindings/src/Emitter/StringEmitter/MethodWrapperEmitter.cs:188–192` (symbol naming)
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/WrapperEmitter.cs:527` (entry)
- `src/Swift.Bindings/src/Emitter/StringEmitter/ModuleEmitter.cs:140` (per-namespace Swift wrapper file)
- `src/Swift.Bindings/src/TypeDatabase/TypeDecl.cs:25,35` (nested types + properties enumeration)
- `.claude/rules/constraints.md` lines 13, 18, 29
- Session 3 — depends on its foundation
