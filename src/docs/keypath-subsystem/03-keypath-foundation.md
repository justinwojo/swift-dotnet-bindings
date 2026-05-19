# Session 3 — KeyPath foundation: opaque pass-through end-to-end

**Status: complete — landed in commit `26dcdcb6`.**
Follow-up surfaced (not blocking — captured in `src/docs/roadmap.md`): C#-side `inout` write-back for blittable structs (Swift round-trips fine via SBW; generated C# call site copies struct into a stack buffer without reading back). KeyPath fixture worked around it by returning a mutated copy.

The structural session of the subsystem. After it ships, the C# generator can receive a `KeyPath<Root, Value>` from a Swift API, hold it via a SafeHandle, and pass it back into another Swift API — all five flavors of the KeyPath class hierarchy round-trip through the binding boundary with correct ARC, equality, and Optional composition.

No typed singleton construction yet (that's Session 4). Just the foundation: type records, projection, runtime helper, marshalling, end-to-end opaque pass-through.

## Goal

Ship the end-to-end opaque pass-through path for all five Swift KeyPath classes:

- `Swift.AnyKeyPath` — base, fully type-erased
- `Swift.PartialKeyPath<Root>` — `Value`-erased
- `Swift.KeyPath<Root, Value>` — fully typed, read-only
- `Swift.WritableKeyPath<Root, Value>` — fully typed, value-type mutation
- `Swift.ReferenceWritableKeyPath<Root, Value>` — fully typed, reference-type-property mutation

For each: TypeDatabase entry, `ITypeProjection` implementation with required visitor parity, C# wrapper type, runtime SafeHandle, P/Invoke marshalling.

End-to-end test: a Swift function returns a `KeyPath<Point, Int>`; C# receives it; C# passes the same handle into another Swift function that reads through it via `swift_getAtKeyPath`; the returned value matches the field offset. Repeat for `WritableKeyPath` and `ReferenceWritableKeyPath` with mutation. Repeat for `Optional<KeyPath>` and `[KeyPath]`.

## Why this session

- Foundation for every consumer-library productionization (Sessions 7–10).
- All five flavors land together because they share machinery; partial coverage creates inconsistent emission gates.
- After this session, KeyPath-typed parameters on already-bound APIs (without further work) emit as opaque pass-through — which is sufficient for OUT-path scenarios (a Swift method returns a KeyPath, the C# caller stashes it).
- Independent of typed-singleton-construction (Session 4), which is the IN path.

## Dependencies

None. Independent of Sessions 1 and 2. Can run in parallel with both.

## ABI ground truth (from `00-overview.md`)

Re-stated here for in-session reference; do not duplicate the SIL probe.

- KeyPath is a class. ABI at `@_cdecl` boundary: single pointer.
- `@_cdecl` rejects `KeyPath<R,V>` directly (`"cannot be represented in Objective-C"`). Boundary spelling is `UnsafeRawPointer` / `UnsafeMutableRawPointer`.
- Return is **+1** (retained). Caller transfers ownership to receiver.
- Parameter is `@guaranteed` (borrowed). Caller retains for the duration of the call; receiver does NOT release.
- Runtime construction goes through `swift_getKeyPath(descriptor, nullptr)` only — no exported component-wise builder.
- Equality dispatches to `AnyKeyPath.==` (value-equal on path content). Pointer-identity equality is **forbidden** (cross-module false negatives).
- `WritableKeyPath` and `ReferenceWritableKeyPath` share the single-pointer ABI; SIL distinguishes them statically via `upcast` op.

## Session 3 work breakdown

### Phase 3.1 — Type records in TypeDatabase

Add records for the five KeyPath classes. Likely sites:

- XML DB: a new entry in `src/Swift.Runtime/src/Swift/RuntimeDatabase.xml` (or wherever stdlib types are registered — confirm by grepping for `Swift.Array` or `Swift.Optional` registration sites).
- C# side: `TypeRecord` with `Kind = TypeRecordKind.Class`, `Flags = TypeRecordFlags.RequiresMemoryManagement`, generic param counts matching:
  - `AnyKeyPath` — 0
  - `PartialKeyPath` — 1 (Root)
  - `KeyPath` — 2 (Root, Value)
  - `WritableKeyPath` — 2
  - `ReferenceWritableKeyPath` — 2

Metadata accessor mangled name: confirm via `nm` on `libswiftCore.dylib`. Expected:
- `$ss10AnyKeyPathCMa` (or equivalent — verify)
- `$ss14PartialKeyPathCMa`
- `$ss7KeyPathCMa`
- `$ss15WritableKeyPathCMa`
- `$ss24ReferenceWritableKeyPathCMa`

Don't guess — generate from a Swift `MemoryLayout<KeyPath<Point, Int>>.size` probe and inspect the mangled accessor symbol.

Inheritance: register the subclass chain so projection can pick the most-derived static type at parser time. The parser's `ModuleProcessor.cs:790` is where class kind is assigned; the subclass relationship may need an explicit pass to recognise that `WritableKeyPath` *is-a* `KeyPath`.

### Phase 3.2 — `KeyPathProjection : ITypeProjection`

New file `src/Swift.Bindings/src/Marshaler/Projection/KeyPathProjection.cs`. Mirror `ClassProjection` (`src/Swift.Bindings/src/Marshaler/Projection/ClassProjection.cs:6`). Differences:

- `PInvokeType` = `IntPtr`.
- `GetParameterPlan`: pass the SafeHandle via `DangerousGetHandle()`, but wrap the P/Invoke in `RuntimeHelpers.PrepareConstrainedRegions()` / `DangerousAddRef` / `DangerousRelease` because the Swift side borrows `@guaranteed` (the SafeHandle must not be finalized mid-call). Pattern: see `SwiftClassHandle` usage in `ClassProjection.cs:31`.
- `GetReturnPlan`: receive the +1 retained `IntPtr` and construct a `SwiftKeyPath` SafeHandle that adopts the retained pointer without an extra retain. Pattern: `ClassProjection.cs:41`'s `SwiftMarshal.MarshalFromSwiftObject<T>` variant — likely a new `SwiftMarshal.AdoptKeyPath<TRoot,TValue>` helper.
- `RequiresSwiftWrapper`: false for the foundation session. Session 4 may flip this true for singleton trampolines.
- `GetSwiftWrapperCode`: null for now.

**Constraint #22 (visitor parity)** — implement on:
- `AccessorGetterConversionVisitor` (`src/Swift.Bindings/src/Emitter/StringEmitter/Handler/AccessorConversionVisitors.cs:10`)
- `AccessorSetterConversionVisitor` (same file, line 199)
- `OptionalAccessorGetterVisitor` (same file, line 134)
- `IProjectionVisitor<T>` interface (`src/Swift.Bindings/src/Marshaler/Projection/IProjectionVisitor.cs:10`)

Add `Visit(KeyPathProjection)` to every visitor. The compile-time exhaustiveness check will fail closed if a visitor is missed.

**Manual check site** — `ProtocolProxyEmitter.Receivers` uses a switch with `_ => null` fallback (per constraint #22). Inspect manually and add a KeyPath case if needed.

`TypeProjectionFactory` (`src/Swift.Bindings/src/Marshaler/Projection/TypeProjectionFactory.cs:395`) — add a branch *before* the generic `TypeRecordKind.Class` fallback:

```csharp
if (typeRecord.Kind == TypeRecordKind.Class)
{
    if (typeRecord.IsKeyPathFamily)
        return new KeyPathProjection(typeName, rootTypeSpec, valueTypeSpec, keyPathFlavor);
    if (MarshallingHelpers.IsObjCRooted(typeRecord))
        return new ObjCRootedClassProjection(typeName);
    return new ClassProjection(typeName);
}
```

`IsKeyPathFamily` is a TypeRecord flag set when the registered class is one of the five KeyPath types.

### Phase 3.3 — Runtime SafeHandle

New file `src/Swift.Runtime/src/Swift/Runtime/SwiftKeyPath.cs`. Mirror `SwiftClassHandle<T>` (`src/Swift.Runtime/src/Swift/Runtime/SwiftClassHandle.cs:37`):

```csharp
public abstract class SwiftKeyPathHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    protected SwiftKeyPathHandle(IntPtr retainedPointer) : base(ownsHandle: true)
    {
        SetHandle(retainedPointer);
    }

    protected override bool ReleaseHandle()
    {
        if (SwiftExitGuard.IsProcessExiting) return true;
        // Mirror SwiftClassHandle: explicit-dispose uses Arc.Release; finalizer uses
        // SwiftReleaseTrampoline.Release to avoid Mono JIT crash.
        SwiftReleaseTrampoline.Release(handle);
        return true;
    }

    public bool ValueEquals(SwiftKeyPathHandle other) =>
        SwiftKeyPathRuntime.AnyKeyPathEquals(handle, other.handle);

    public int ValueHash() => SwiftKeyPathRuntime.AnyKeyPathHashValue(handle);
}
```

Three public typed wrappers, each a class deriving from a common typed base:

```csharp
public class AnyKeyPath : SwiftKeyPathHandle, ISwiftObject { /* … */ }
public class PartialKeyPath<TRoot> : AnyKeyPath, ISwiftObject { /* … */ }
public class KeyPath<TRoot, TValue> : PartialKeyPath<TRoot>, ISwiftObject { /* … */ }
public class WritableKeyPath<TRoot, TValue> : KeyPath<TRoot, TValue>, ISwiftObject { /* … */ }
public class ReferenceWritableKeyPath<TRoot, TValue> : WritableKeyPath<TRoot, TValue>, ISwiftObject { /* … */ }
```

Namespace: `Swift` (root namespace, mirrors `Swift.Array<T>`, `Swift.Optional<T>`). The C# `Swift.KeyPath<TRoot, TValue>` directly maps to `Swift.KeyPath<Root, Value>` in Swift source.

`Equals` / `GetHashCode` overrides on `AnyKeyPath` dispatch to `ValueEquals` / `ValueHash`. Pointer equality (`object.ReferenceEquals`) is explicitly *not* equivalent to value equality — document this in the XML doc comment.

`Sendable` annotation — `00-overview.md` notes `KeyPath` is `Sendable` when Root and Value are. C# has no `Sendable` analogue; document the C# wrapper as thread-safe-when-Swift-sees-it-as-Sendable in a `// remarks` block. No runtime enforcement.

### Phase 3.4 — Runtime helper: `swift_getAtKeyPath`, equality, hash

New file `src/Swift.Runtime/src/Swift/Runtime/SwiftKeyPathRuntime.cs`. P/Invoke declarations for:

```csharp
internal static class SwiftKeyPathRuntime
{
    // Read through a KeyPath: $@convention(thin) <τ_0_0, τ_0_1>
    //   (@in_guaranteed τ_0_0, @guaranteed KeyPath<τ_0_0, τ_0_1>) -> @out τ_0_1
    [DllImport(SwiftLib.Core, EntryPoint = "swift_getAtKeyPath")]
    internal static extern void GetAtKeyPath(
        IntPtr outValue, IntPtr inRoot, IntPtr keyPath,
        IntPtr rootMetadata, IntPtr valueMetadata);

    // Value equality on path content
    [DllImport(SwiftLib.Core, EntryPoint = "...")]
    internal static extern bool AnyKeyPathEquals(IntPtr a, IntPtr b);

    [DllImport(SwiftLib.Core, EntryPoint = "...")]
    internal static extern int AnyKeyPathHashValue(IntPtr kp);
}
```

The exact entry points for equality/hash need to be confirmed against `libswiftCore.dylib` symbol table. `AnyKeyPath` implements `Equatable` and `Hashable` in Swift; the Swift-emitted `==` and `hashValue` thunks live as `$ss10AnyKeyPathC...Tw` / `$ss10AnyKeyPathC9hashValueSivg` or similar. Resolve via `nm libswiftCore.dylib | grep -i keypath` early in the session.

The runtime helper may need its own Swift-side trampoline if `AnyKeyPath.==` / `hashValue` aren't directly callable from C — most likely they need a `@_cdecl` shim emitted into a new file `src/Swift.Runtime/native/SwiftKeyPathRuntime.swift` (mirroring the pattern of other runtime helpers). Confirm pattern by inspecting existing runtime Swift helpers.

### Phase 3.5 — Marshalling at parameter and return boundaries

Generator side: when `KeyPathProjection` is the active projection for a parameter or return, emit:

**Return (OUT — Swift returns retained, C# adopts):**
- Swift wrapper: `let kp = obj.someMethod(); return Unmanaged.passRetained(kp).toOpaque()`
- C# P/Invoke: returns `IntPtr`; C# call site: `new KeyPath<TRoot, TValue>(adoptedRetainedPointer)`

**Parameter (IN — C# passes borrowed, Swift uses guaranteed):**
- C# call site: `kp.Payload.DangerousGetHandle()` (mirrors `ClassProjection`)
- Swift wrapper: `let kp = Unmanaged<KeyPath<R,V>>.fromOpaque(raw).takeUnretainedValue()` — `takeUnretained` because the C# SafeHandle owns the retained reference and outlives the call.

Constraint #21 (Swift iterator `Arc.Retain`): N/A for KeyPath parameter case because we're not returning a reference *to* the argument. But if a method returns a KeyPath that was an argument (rare; possibly an identity pass-through), the wrapper must `Arc.Retain` before return. Cover with a fixture variant in Phase 3.7.

### Phase 3.6 — Optional and array composition

`Optional<KeyPath<R,V>>` — composes with the existing `OptionalProjection` infrastructure. The KeyPath class is a reference type, so `Optional<KeyPath<R,V>>` is laid out as either a non-null pointer (KeyPath instance) or null (.none). At the `@_cdecl` boundary it's still a single pointer.

C# side: `KeyPath<TRoot, TValue>?` (nullable reference type, since KeyPath is a class) maps directly. The `OptionalProjection`'s `Visit(KeyPathProjection)` (constraint #22) handles the marshalling.

`[KeyPath<R,V>]` — composes with `SwiftArray<T>`. The Swift Array marshalling already exists; KeyPath ARC-managed element type is no different from any other Swift class element. Verify with a fixture.

### Phase 3.7 — BindingTests fixture

Swift: `BindingTests/Sources/SwiftBindingsTestLib/KeyPath/KeyPathFoundation.swift`:

```swift
// `@frozen` so Point projects to a blittable C# struct, so `inout Point` on
// `writeInt` flows through the supported UnsafeMutableRawPointer write-back
// path. Without it, Point projects as a SafeHandle-backed class and the
// wrapper emitter rejects `inout` of class-projected types (see
// WrapperValidation.HasInoutWithAbiMismatch); the direct-mangled-name
// fallback would target the bare `tFZ` symbol but only the `tFZTj` thunk is
// actually exported for class methods, so the call would EntryPointNotFound.
@frozen public struct Point {
    public var x: Int
    public var y: Int
    public init(x: Int, y: Int) { self.x = x; self.y = y }
}

public class Box {
    public var n: Int = 0
    public var label: String = ""
    public init() {}
}

public class KeyPathFactory {
    // OUT — returns a typed KeyPath
    public class func makePointXPath() -> KeyPath<Point, Int> { \Point.x }
    public class func makePointYPath() -> KeyPath<Point, Int> { \Point.y }

    public class func makeWritablePointXPath() -> WritableKeyPath<Point, Int> { \Point.x }
    public class func makeReferenceWritableBoxNPath() -> ReferenceWritableKeyPath<Box, Int> { \Box.n }

    // OUT — Optional<KeyPath>
    public class func maybePath(_ make: Bool) -> KeyPath<Point, Int>? {
        make ? \Point.x : nil
    }

    // OUT — array
    public class func allPointPaths() -> [KeyPath<Point, Int>] {
        [\Point.x, \Point.y]
    }
}

public class KeyPathConsumer {
    // IN — consumes a KeyPath, reads through it
    public class func readInt(from p: Point, by kp: KeyPath<Point, Int>) -> Int {
        return p[keyPath: kp]
    }

    // IN — WritableKeyPath assigns into a value-type field through the KP subscript.
    // Returns the mutated copy. `inout` would round-trip on the Swift side (the SBW
    // wrapper does load+defer write-back) but the generated C# call site marshals the
    // struct into a stack buffer and never reads back — a generator gap (inout-of-
    // blittable-struct write-back missing on the C# side) outside Session 3 scope.
    public class func writeInt(into p: Point, by kp: WritableKeyPath<Point, Int>, value: Int) -> Point {
        var copy = p
        copy[keyPath: kp] = value
        return copy
    }

    // IN — ReferenceWritableKeyPath, mutates reference-type property
    public class func writeIntRef(into b: Box, by kp: ReferenceWritableKeyPath<Box, Int>, value: Int) {
        b[keyPath: kp] = value
    }

    // Round-trip: pass a KeyPath in, return the same one out (identity preservation)
    public class func roundTrip(_ kp: KeyPath<Point, Int>) -> KeyPath<Point, Int> { kp }

    // Equality: are two paths equal?
    public class func samePath(_ a: AnyKeyPath, _ b: AnyKeyPath) -> Bool { a == b }
}
```

C#: `BindingTests/RuntimeTestsApp/KeyPath/KeyPathFoundationTests.cs`. Cover, with assertions:

- **OUT — typed KeyPath**: call `KeyPathFactory.MakePointXPath()`, receive a `Swift.KeyPath<Point, long>` (or `Swift.Int` if that's how `Int` projects), assert non-null.
- **OUT — WritableKeyPath subclass identity**: `MakeWritablePointXPath()` returns a `Swift.WritableKeyPath<Point, long>` (the more-derived static type), assert `is WritableKeyPath` true and `is KeyPath` true.
- **OUT — Optional**: `MaybePath(false)` returns `null`; `MaybePath(true)` returns non-null.
- **OUT — array**: `AllPointPaths()` returns `IReadOnlyList<KeyPath<Point, long>>` of length 2.
- **IN — read**: pass `MakePointXPath()` to `KeyPathConsumer.ReadInt(from: Point(x:7, y:42), by: kp)`, assert returns 7.
- **IN — write value-type**: pass `MakeWritablePointXPath()` to `WriteInt`, mutate `Point.x` to 99, observe via subsequent `ReadInt`.
- **IN — write reference-type**: pass `MakeReferenceWritableBoxNPath()` to `WriteIntRef`, mutate `Box.n` to 1234, read directly.
- **Round-trip identity (value-equality, not pointer-equality)**: `var a = MakePointXPath(); var b = RoundTrip(a); Assert.True(a.Equals(b));` and `Assert.Equal(a.GetHashCode(), b.GetHashCode())`. Do NOT assert `object.ReferenceEquals(a, b)` — that's the test for *handle* identity, which is allowed to differ.
- **Cross-instance equality**: `var a = MakePointXPath(); var b = MakePointXPath(); Assert.True(a.Equals(b));` — same factory call twice produces value-equal paths.
- **Inequality**: `var a = MakePointXPath(); var b = MakePointYPath(); Assert.False(a.Equals(b));`
- **Disposal**: `using (var kp = MakePointXPath()) { … }` — confirm `ReleaseHandle` called; if a finalizer test is feasible, assert no crash on process exit.
- **NativeAOT-specific**: device run must pass. NativeAOT has historically had `[ModuleInitializer]` + finalizer issues; this is a fresh code path and needs explicit device gate.

## Validation gates

| Gate | Expected | Notes |
|---|---|---|
| `nuke test` | New `KeyPathProjectionTests` + visitor-parity tests pass | Constraint #22 enforced |
| `nuke binding-tests` (sim) | New `KeyPathFoundationTests` passes | Mono JIT |
| `nuke binding-tests --device` | Same set passes | NativeAOT — required because new SafeHandle + new runtime helper |
| `nuke validate` | Baseline holds; no library that didn't bind KeyPath surfaces before now suppresses something | Cross-cutting projection change |

`nuke validate` is recommended for this session because adding a new `ITypeProjection` flavor changes the visitor dispatch graph; a regression in `Visit(KeyPathProjection)` could silently fall through to the `_ => null` fallback in `ProtocolProxyEmitter.Receivers` and break unrelated emission.

## Exit criteria

- All five KeyPath classes registered in TypeDatabase with correct kind, generic param counts, ARC flag, metadata accessors.
- `KeyPathProjection` exists with all three visitor `Visit()` methods plus `GetReturnPlan` / `GetParameterPlan`.
- `Swift.KeyPath`/`WritableKeyPath`/`ReferenceWritableKeyPath`/`PartialKeyPath`/`AnyKeyPath` exist in `Swift.Runtime`.
- `SwiftKeyPathRuntime` runtime helper exposes `swift_getAtKeyPath`, equality, hash via P/Invoke.
- BindingTests fixture covers all eight scenarios above. Sim + device green.
- `Optional<KeyPath>` and `[KeyPath]` round-trip correctly.
- Validate baseline holds.

## Risks specific to Session 3

- **Risk A (projection parity violation)** — Failing to implement one of the three visitor `Visit(KeyPathProjection)` methods produces silent wrong marshalling on Optional/accessor paths. Mitigated by compile-time exhaustiveness check on `IProjectionVisitor<T>`, but the `ProtocolProxyEmitter.Receivers` switch is manual (constraint #22). **Diagnostic:** add a unit test that constructs a `KeyPathProjection` and runs every visitor through the public dispatch path; assert non-null result.
- **Risk B (cross-module proxy class qualification)** — A `KeyPath` returned by module A and consumed by module B can pick the wrong proxy class name if `ProjectionContext.CurrentModuleName` is unthreaded (constraint #29). **Diagnostic:** in the fixture, add a second Swift module `BindingTests/Sources/SwiftBindingsTestLibTwo/` that imports the first and re-exposes a method taking `KeyPath<Point, Int>`. Round-trip a KeyPath from module 1 to module 2.
- **Risk C (ARC ownership at `@guaranteed` boundary)** — If the Swift wrapper that receives an inbound KeyPath calls `passRetained` (or its inverse), there's a double-release. SIL evidence: `@guaranteed`, caller does NOT transfer +1. **Diagnostic:** stress test the round-trip — call `RoundTrip(kp)` in a loop of 100,000 iterations; observe no leak (memory plateau) and no crash. Also: explicit-dispose ordering — `kp.Dispose()` after `RoundTrip(kp)` must succeed exactly once.
- **Risk D (subclass-cast loss)** — Swift returns a `WritableKeyPath<Point, Int>` but the generator's projection picks the upcast `KeyPath<Point, Int>` type. The C# `is WritableKeyPath` check then fails. **Cause:** projection picks the *declared* return type, which Swift's static analysis upcasts wherever the API demands `KeyPath`. **Solution:** preserve the declared static type (Swift `WritableKeyPath` API → C# `WritableKeyPath`); on the C# side, if the actual Swift instance is a more-derived subclass, return the most-derived C# wrapper type via a runtime kind check. May require a `swift_keyPath_kind(IntPtr)` runtime trampoline. Confirm with the fixture's `is WritableKeyPath` assertion before considering this risk closed.
- **Risk E (Optional<KeyPath> null-pointer ambiguity)** — Swift's `Optional<KeyPath<R,V>>` is laid out as either non-null pointer or null. The cdecl marshalling for Optional class types is already established; KeyPath should compose without new code. **Diagnostic:** the `MaybePath` fixture covers both cases; assert `null` vs non-null cleanly.
- **Risk F (Swift runtime equality entry-point name)** — `AnyKeyPath.==` may not exist as a directly-callable C symbol. Will likely need a `@_cdecl` shim in `src/Swift.Runtime/native/SwiftKeyPathRuntime.swift`. **Diagnostic:** `nm libswiftCore.dylib | grep -E 'KeyPath.*(Eq|equal|Hash)'` early in the session; budget a shim file if the direct symbol isn't exposed.
- **Risk G (generic parameter projection for `TRoot` / `TValue` in C#)** — `KeyPath<TRoot, TValue>` is a generic C# class. When `TRoot` is e.g. `Swift.Array<Int>` (a Swift type with its own C# wrapper), the round-trip from Swift's `Root` generic to C#'s `TRoot` must use the existing generic-projection machinery without surprises. **Diagnostic:** fixture variant with `KeyPath<SwiftArray<Int>, Int>` or similar — defer to Session 4 / a later session if the existing generic-substitution path can't handle it.

## References

- `00-overview.md` — ABI ground truth, design decision
- `src/Swift.Bindings/src/Marshaler/Projection/ClassProjection.cs:6` (mirror)
- `src/Swift.Bindings/src/Marshaler/Projection/TypeProjectionFactory.cs:395` (branch site)
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/AccessorConversionVisitors.cs:10,134,199`
- `src/Swift.Bindings/src/Marshaler/Projection/IProjectionVisitor.cs:10`
- `src/Swift.Runtime/src/Swift/Runtime/SwiftClassHandle.cs:37` (mirror)
- `src/Swift.Runtime/src/Swift/Runtime/Arc.cs:25,69`
- `.claude/rules/constraints.md` lines 10, 18, 22, 29
- `src/docs/Design/binding-closures.md` (closure subsystem precedent for runtime helper file pattern)
