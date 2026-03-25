# ObjC Bridge Projection: Eliminating Hand-Written Foundation Types

## Problem Statement

The runtime library (`Swift.Runtime`) contains hand-written C# types for `Foundation.URL`, `Foundation.URLRequest`, and `Foundation.URLResponse` with custom P/Invoke declarations that call directly into Foundation's Swift ABI symbols. These have two critical problems:

1. **Non-blittable P/Invokes**: Several P/Invokes pass `SwiftString` or `URL` (SafeHandle-backed class) through `CallConvSwift`, which Mono rejects. This blocks all 16 URLRequestTests on the iOS simulator.

2. **iOS 26 ABI break**: Apple's swift-foundation rewrite changed several symbol names. Four of the URL/URLRequest entry points no longer exist on iOS 26:

| Symbol | Status on iOS 26 | Change |
|--------|------------------|--------|
| `URL.init(string:)` `SS_tcfC` | Missing | → `SSh_tcfC` (borrowing param) |
| `URL.init(filePath:isDirectory:)` | Missing | Replaced by new API with different params |
| `URL.isFileURL` `9isFileURLSbvg` | Missing | → `06isFileB0Sbvg` (name change) |
| `URLRequest.init(url:)` | Missing | → multi-param init with cachePolicy + timeoutInterval |

Additionally, 7 hand-written runtime files use `$sSo...CMa` Swift overlay metadata accessor symbols that are fragile across iOS versions. The Foundation ones (`URLResponse`, `OperationQueue`) are already missing on iOS 26 — the libswiftFoundation.dylib symbols were not carried forward when it was merged into Foundation.framework. The remaining 5 files (`UIImage`, `NSImage`, `NSColor`, `CIContext`, `DispatchQueue`) use the same pattern and are vulnerable to the same breakage in future iOS releases.

All `Foundation.Data` symbols are intact on iOS 26. Data is a frozen struct with a stable ABI — it does not share the problems described in this document.

## Current Architecture

### How it works today

```
Consumer code:  pipeline.ImageTask(myNSUrl)
                         ↓
Generated binding:  using var urlSwift = Swift.URL.FromNSUrl(myNSUrl);
                         ↓
Swift.URL.FromNSUrl:  URL.FromString(nsUrl.AbsoluteString)  ← round-trips through string!
                         ↓
Swift.URL.FromString:  PInvoke_InitWithString(swiftString)  ← CallConvSwift + broken entry point
                         ↓
Foundation.framework:  $s10Foundation3URLV6stringACSgSS_tcfC  ← MISSING on iOS 26
```

The generated binding already exposes `Foundation.NSUrl` to consumers (via `NativeRemappedProjection`). `Swift.URL` is purely an internal marshalling intermediary.

### Files involved

**Hand-written runtime types** (all have P/Invokes into Foundation Swift ABI):
- `src/Swift.Runtime/src/Swift/URL.cs` — `FromString`, `FromFilePath`, `AbsoluteString`, `Path`, `IsFileURL`, `ToNSUrl`, `FromNSUrl`
- `src/Swift.Runtime/src/Swift/URLRequest.cs` — `FromURL`, `HTTPMethod`, `TimeoutInterval`, `SetValue`, `AddValue`, `Value`, `ToNSUrlRequest`, `FromNSUrlRequest`
- `src/Swift.Runtime/src/Swift/URLResponse.cs` — `URL`, `MIMEType`, `ExpectedContentLength`, `TextEncodingName`, `SuggestedFilename`
- `src/Swift.Runtime/src/Swift/OperationQueue.cs` — `Init`, `GetMain`, `GetCurrent`
- `src/Swift.Runtime/src/Swift/Data.cs` — `FromBytes`, `Count`, `CopyBytes`, `ToByteArray` (frozen struct, entry points intact — **not in scope**)

**Hand-written runtime types using `$sSo...CMa` metadata accessors** (vulnerable to iOS overlay merges):
- `src/Swift.Runtime/src/Swift/URLResponse.cs` — `$sSo15NSURLResponseCMa` (broken on iOS 26)
- `src/Swift.Runtime/src/Swift/OperationQueue.cs` — `$sSo16NSOperationQueueCMa` (broken on iOS 26)
- `src/Swift.Runtime/src/Swift/UIImage.cs` — `$sSo7UIImageCMa`
- `src/Swift.Runtime/src/Swift/NSImage.cs` — `$sSo7NSImageCMa`
- `src/Swift.Runtime/src/Swift/NSColor.cs` — `$sSo7NSColorCMa`
- `src/Swift.Runtime/src/Swift/CIContext.cs` — `$sSo9CIContextCMa`
- `src/Swift.Runtime/src/Swift/DispatchQueue.cs` — `$sSo17OS_dispatch_queueCMa`

**Generator type database**:
- `src/Swift.Runtime/src/Swift/FoundationDatabase.xml` — registers URL (nativeType=NSUrl), URLRequest, URLResponse, Data (nativeType=NSData) etc.

**Generator projection code**:
- `src/Swift.Bindings/src/Marshaler/Projection/NativeRemappedProjection.cs` — handles Swift type ↔ .NET native type conversion in generated C#
- `src/Swift.Bindings/src/Marshaler/TypeConversionHandler.cs` — type conversion for methods/properties, references `Swift.URL.FromNSUrl()`/`.ToNSUrl()`
- `src/Swift.Bindings/src/Emitter/StringEmitter/CdeclParamMapper.cs` — handles Foundation.Date, Foundation.Data, Swift.String in @_cdecl wrapper params
- `src/Swift.Bindings/src/Emitter/StringEmitter/MethodWrapperEmitter.cs` — emits @_cdecl wrapper bodies
- `src/Swift.Bindings/src/Marshaler/Projection/ArrayProjection.cs` — container marshalling, uses element projection's `SwiftContainerGenericType`
- `src/Swift.Bindings/src/Marshaler/Projection/DictionaryProjection.cs` — dictionary marshalling, same pattern
- `src/Swift.Bindings/src/Marshaler/Projection/SetProjection.cs` — set marshalling, same pattern

## Design Principles

These principles were established through multi-round architectural review and apply to all decisions in this document:

1. **Depend only on stable boundaries.** The ObjC runtime (`objc_getClass`, ObjC object pointers) and Swift's `_ObjectiveCBridgeable` protocol are public, documented APIs that Apple cannot break without breaking all Swift↔ObjC interop. Internal Swift ABI symbols (`$s...`, `$sSo...CMa`) are not stable. Every hand-written P/Invoke into an internal Swift symbol is technical debt with a ticking clock.

2. **Raw pointers at the C ABI boundary.** The @_cdecl signature uses `UnsafeMutableRawPointer` (not `AnyObject`) for ObjC object parameters. This matches the established generator convention in `CdeclParamMapper.cs` (line 69-77) where all class references use `UnsafeMutableRawPointer` + `Unmanaged` reconstruction inside the wrapper body.

3. **Separate "public API remap" from "@_cdecl bridge strategy."** The `nativeType` XML attribute means "what .NET type does the consumer see" (e.g., URL → NSUrl). A new `objcBridgeable` attribute means "cross the @_cdecl boundary via ObjC object pointer." These are independent concerns: Data has `nativeType` but does NOT need `objcBridgeable` (frozen struct, fast path). URL needs both.

4. **New projection class, not mutation of existing ones.** `ObjCBridgeableProjection` is a new `ITypeProjection` sibling to `NativeRemappedProjection` and `ObjCBridgedProjection`, not a conditional branch inside them. Each projection has a single, clear strategy.

5. **Leave working code alone.** `Foundation.Data` is a frozen struct with stable ABI, working P/Invokes, and zero-copy marshalling. Converting it to ObjC bridge for "consistency" would introduce a performance regression with no correctness benefit. It stays on its current path.

## Proposed Solution

### Core idea

When the generator encounters a type marked `objcBridgeable="true"` in a method signature, emit @_cdecl wrappers that accept/return **ObjC object pointers** (`UnsafeMutableRawPointer`) instead of the Swift struct. Let Swift do the bridging inside the wrapper. For containers holding ObjC-bridgeable elements, bridge the **entire container** to its ObjC collection counterpart on the Swift side.

### Scalar parameters

**Swift @_cdecl wrapper (emitter output):**
```swift
// TODAY: wrapper accepts URL struct bytes (requires C# to construct URL via hand-written P/Invokes)
@_cdecl("SBW_Nuke_ImagePipeline_loadImage_ABC123")
func wrapper(_ resultPtr: UnsafeMutableRawPointer, _ url: ???, _ self_: UnsafeMutableRawPointer) {
    // Can't easily accept URL struct — it's non-frozen, variable size
}

// PROPOSED: wrapper accepts ObjC object pointer, reconstructs via ObjC bridge
@_cdecl("SBW_Nuke_ImagePipeline_loadImage_ABC123")
func wrapper(_ resultPtr: UnsafeMutableRawPointer, _ urlParam: UnsafeMutableRawPointer, _ self_: UnsafeMutableRawPointer) {
    let urlParamVal: Foundation.URL = Unmanaged<AnyObject>.fromOpaque(urlParam).takeUnretainedValue() as! Foundation.URL
    let obj = Unmanaged<Nuke.ImagePipeline>.fromOpaque(self_).takeUnretainedValue()
    let result = obj.loadImage(url: urlParamVal)
    // ... marshal result
}
```

**C# P/Invoke (emitter output):**
```csharp
// TODAY: passes Swift.URL SafeHandle (non-blittable, crashes Mono)
PInvoke_loadImage(resultPtr, urlSwift.Payload, self);

// PROPOSED: passes NSUrl as IntPtr (always blittable)
PInvoke_loadImage(resultPtr, nsUrl.Handle, self);
```

**C# public API (already correct — no change needed):**
```csharp
// Consumer-facing API stays the same:
public void LoadImage(Foundation.NSUrl url) { ... }
```

### Scalar return values

```swift
// PROPOSED: wrapper returns ObjC object pointer
@_cdecl("SBW_...")
func wrapper(_ self_: UnsafeMutableRawPointer) -> UnsafeMutableRawPointer? {
    let obj = Unmanaged<SomeType>.fromOpaque(self_).takeUnretainedValue()
    let url: Foundation.URL? = obj.url
    guard let nsUrl = url as NSURL? else { return nil }
    return Unmanaged.passRetained(nsUrl).toOpaque()
}
```

C# side receives `IntPtr`, converts to `Foundation.NSUrl` via .NET iOS runtime.

### Optional handling

`Optional<URL>` maps to a nullable ObjC pointer at the C ABI boundary:

- **Parameter**: `UnsafeMutableRawPointer?` in @_cdecl. C# passes `IntPtr.Zero` for nil, ObjC handle for non-nil. Swift wrapper reconstructs via `urlParam.map { Unmanaged<AnyObject>.fromOpaque($0).takeUnretainedValue() as! Foundation.URL }`.
- **Return**: Nullable pointer. `nil` → `IntPtr.Zero` on C# side → `null` `NSUrl?`.

This aligns with the existing `Optional<reference type>` path in `CdeclParamMapper.cs` (lines 89-124), which already uses `UnsafeMutableRawPointer?` + `Unmanaged<AnyObject>` reconstruction for ObjC-bridged types.

### Why this works

- `Foundation.URL` ↔ `NSUrl` bridging in Swift is essentially free (they share the same storage internally via `_ObjectiveCBridgeable`)
- `UnsafeMutableRawPointer` in @_cdecl = `IntPtr` on the C# side = always blittable
- No Foundation entry points needed — the wrapper lives in our generated SwiftBindings library
- No iOS version sensitivity — the ObjC bridge exists on all iOS versions
- Eliminates the need for `Swift.URL`, `Swift.URLRequest` as runtime types entirely

## Collection Boundary Decision

### The problem with element-level bridging

The existing container pipeline (`ArrayProjection`, `DictionaryProjection`, `SetProjection`) is structurally coupled to the element projection's `SwiftContainerGenericType` and `MarshalFromSwiftType`. For URL today, the chain is:

```
Parameter: IEnumerable<NSUrl>
  → Select(e => Swift.URL.FromNSUrl(e))     [GetParameterElementConversion]
  → SwiftArray<Swift.URL>.FromEnumerable()   [SwiftContainerGenericType]
  → Extract IntPtr for P/Invoke

Return: IntPtr from P/Invoke
  → SwiftMarshal.MarshalFromSwift<SwiftArray<Swift.URL>>(ptr)  [MarshalFromSwiftType]
  → .AsProjected(e => e.ToNSUrl())           [GetReturnElementConversion]
  → IReadOnlyList<NSUrl>
```

`SwiftMarshal.MarshalFromSwift<T>()` requires `T` to implement `ISwiftObject`. Replacing `Swift.URL` with `IntPtr` in the generic argument does not compile — `SwiftArray<IntPtr>` is not valid. A minimal `Swift.URL` stub would still need `TypeMetadata` to work, which still requires resolving Foundation's Swift ABI metadata — the exact dependency we're trying to eliminate.

### Decision: Whole-container ObjC bridge

When a container's element type is `objcBridgeable`, the @_cdecl wrapper bridges the **entire container** to its ObjC collection counterpart. Swift's `_ObjectiveCBridgeable` bridge is recursive, so this handles nested containers automatically.

**Swift side — array return:**
```swift
@_cdecl("SBW_...")
func wrapper(_ self_: UnsafeMutableRawPointer) -> UnsafeMutableRawPointer {
    let obj = Unmanaged<SomeType>.fromOpaque(self_).takeUnretainedValue()
    let result: [Foundation.URL] = obj.getURLs()
    let nsArray = result as NSArray   // Swift's recursive ObjC bridge
    return Unmanaged.passRetained(nsArray).toOpaque()
}
```

**Swift side — array parameter:**
```swift
@_cdecl("SBW_...")
func wrapper(_ urlsParam: UnsafeMutableRawPointer, _ self_: UnsafeMutableRawPointer) {
    let nsArray = Unmanaged<AnyObject>.fromOpaque(urlsParam).takeUnretainedValue() as! NSArray
    let urls = nsArray as! [Foundation.URL]   // ObjC → Swift bridge
    let obj = Unmanaged<SomeType>.fromOpaque(self_).takeUnretainedValue()
    obj.doSomething(urls: urls)
}
```

**C# side** receives/passes `IntPtr` (the ObjC collection handle). Generated C# converts between `IntPtr` and typed .NET collections using .NET iOS runtime helpers. The exact conversion APIs will be determined during implementation — the architectural commitment is that ObjC-bridgeable containers cross the @_cdecl boundary as ObjC collection pointers, and generated C# converts them back to typed .NET collections.

**Container type mapping:**

| Swift Container | ObjC Bridge | @_cdecl type |
|----------------|-------------|--------------|
| `[URL]` | `NSArray` | `UnsafeMutableRawPointer` |
| `[String: URL]` | `NSDictionary` | `UnsafeMutableRawPointer` |
| `Set<URL>` | `NSSet` | `UnsafeMutableRawPointer` |
| `[[URL]]` | `NSArray` (recursive) | `UnsafeMutableRawPointer` |
| `Optional<[URL]>` | Nullable `NSArray` pointer | `UnsafeMutableRawPointer?` |

### Implementation in the projection layer

The container projections (`ArrayProjection`, `DictionaryProjection`, `SetProjection`) need a fork based on whether the element projection is `ObjCBridgeableProjection`:

1. `ObjCBridgeableProjection` exposes a flag (e.g., `UsesObjCContainerBridge = true`)
2. Container projections check this flag on their element projection(s)
3. If true: emit ObjC collection bridge code (whole-container bridge on Swift side, `IntPtr` on C# side)
4. If false: existing `SwiftArray<T>` / `SwiftDictionary<K,V>` pipeline, unchanged

The non-bridgeable container path is untouched. Only containers with ObjC-bridgeable elements take the new path. This is a clean fork — the discriminator is an explicit flag on the element projection, and the two paths are independent.

## Scope of Types Affected

| Swift Type | ObjC Bridge | Action | XML Changes |
|-----------|-------------|--------|-------------|
| `Foundation.URL` | `NSUrl` | New `ObjCBridgeableProjection` | Add `objcBridgeable="true"` |
| `Foundation.URLRequest` | `NSURLRequest` | New `ObjCBridgeableProjection` | Add `nativeType="Foundation.NSUrlRequest"` + `objcBridgeable="true"` |
| `Foundation.URLResponse` | `NSURLResponse` | Fix metadata accessor (`objc_getClass`) | Already `objcBridged="true"` |
| `Foundation.OperationQueue` | `NSOperationQueue` | Fix metadata accessor (`objc_getClass`) | Already `objcBridged="true"` |
| `Foundation.Data` | `NSData` | **No change** — frozen struct, stable ABI, fast path | Unchanged |
| `Foundation.Date` | `NSDate` | **No change** — already handled as Double | Unchanged |

### Why Data stays on its current path

`Foundation.Data` is a frozen struct (`frozen="true"` in the XML). Apple's ABI stability guarantee means its layout (`_flags: Int64, _object: IntPtr`) is locked forever. The current marshalling is zero-copy at the @_cdecl boundary (pass two words inline). The ObjC bridge (`NSData` round-trip) would require byte copying, which is strictly worse for performance. Data has no non-blittable P/Invoke issues and no iOS 26 breakage. Converting it would trade a working fast path for a slower one to achieve "consistency" — that is not a good trade.

If a future iOS release breaks Data's entry points (unlikely given frozen ABI), the ObjC bridge approach documented here can be applied to Data at that time. Until then, it stays.

## What Gets Deleted (End State)

Once the `ObjCBridgeableProjection` handles all scalar, optional, container, and accessor cases:

- `src/Swift.Runtime/src/Swift/URL.cs` — **delete entirely**
- `src/Swift.Runtime/src/Swift/URLRequest.cs` — **delete entirely**
- `src/Swift.Runtime/src/Swift/URLResponse.cs` — simplify after `objc_getClass` migration
- `BindingTests/RuntimeTestsApp/Marshalling/URLRequestTests.cs` — **rewrite** to test the generated binding path

### Deletion gate

`URL.cs` and `URLRequest.cs` may only be deleted when ALL of the following are satisfied:

1. All cases in the verification matrix (below) pass in BindingTests on simulator
2. Container cases specifically tested: `[URL]` param, `[URL]` return, `[String: URL]` return, `Set<URL>` param/return, `[[URL]]` return
3. Optional cases tested: `URL?` param, `URL?` return
4. Property accessor cases tested: `var url: URL { get set }`
5. `validate-libraries.sh --tier all` shows no regressions
6. No remaining code references `Swift.URL` or `Swift.URLRequest` outside the files being deleted

## What Gets Added/Changed

### New: `ObjCBridgeableProjection` class

A new `ITypeProjection` implementation (sibling to `NativeRemappedProjection` and `ObjCBridgedProjection`):

- `PublicType` → .NET native type (e.g., `Foundation.NSUrl`)
- `PInvokeType` → `IntPtr` (always)
- `SwiftContainerGenericType` → N/A (containers use whole-container bridge, not element-level generic)
- `UsesObjCContainerBridge` → `true` (signals container projections to use ObjC collection path)
- `GetParameterPlan()` → extract `IntPtr` from `.Handle`, no wrapper construction
- `GetReturnPlan()` → wrap `IntPtr` as ObjC class via .NET iOS runtime
- Visitor implementations for `AccessorGetterConversionVisitor`, `AccessorSetterConversionVisitor`, `OptionalAccessorGetterVisitor` (projection parity requirement)

### Generator changes

1. **`TypeProjectionFactory.cs`**: When a type record has `objcBridgeable="true"`, create `ObjCBridgeableProjection` instead of `NativeRemappedProjection`.

2. **`CdeclParamMapper.cs`**: Add case for `objcBridgeable` types. Emit `UnsafeMutableRawPointer` param + `Unmanaged<AnyObject>.fromOpaque(...).takeUnretainedValue() as! SwiftType` reconstruction. Follows existing AnyObject pattern at line 69-77.

3. **`MethodWrapperEmitter.cs` / `ConstructorWrapperEmitter.cs`**: Update return value handling — when return type is `objcBridgeable`, bridge to ObjC class and return as `UnsafeMutableRawPointer`.

4. **Container projections** (`ArrayProjection.cs`, `DictionaryProjection.cs`, `SetProjection.cs`): Add fork — when element projection has `UsesObjCContainerBridge = true`, emit whole-container ObjC bridge code instead of `SwiftArray<T>` pipeline.

### Type database changes

5. **`FoundationDatabase.xml`**: Add `objcBridgeable="true"` to URL. Add `nativeType="Foundation.NSUrlRequest"` and `objcBridgeable="true"` to URLRequest.

### Runtime changes (independent workstream)

6. **All 7 `$sSo...CMa` files**: Replace Swift overlay metadata accessors with `objc_getClass()` calls. This is a repo-wide policy: hand-written ObjC class wrappers resolve metadata through the ObjC runtime, not Swift overlay metadata accessors.

### Test changes

7. **URLRequestTests.cs**: Rewrite to test through generated bindings (Swift test library functions that take/return URL/URLRequest). Remove tests for hand-written wrapper convenience APIs.

8. **New BindingTests**: Swift test functions covering all cases in the verification matrix.

## Implementation Sessions

This work is structured as 4 sequential sessions, each producing a shippable commit. Sessions are designed for the session orchestrator (`/Users/wojo/Dev/session-orchestrator-prompt.md`) — each session contains enough context for a fresh agent to execute without a separate planning phase.

**Dependency graph:**
```
Session 1 (objc_getClass) ──────────────────────────┐
                                                     ├─→ both merge independently
Session 2 (scalar projection) ──→ Session 3 (containers) ──→ Session 4 (URLRequest + delete)
```

Sessions 1 and 2 are independent and can run in parallel. Sessions 3 and 4 are sequential dependencies on 2.

---

### Session 1: `objc_getClass` metadata accessor migration — COMPLETE (4ebb8042)

**Objective:** Replace all 7 hand-written `$sSo...CMa` Swift overlay metadata accessor P/Invokes with `objc_getClass()` calls. This eliminates fragile dependencies on Swift overlay symbols that Apple can drop when merging framework dylibs (as happened with libswiftFoundation on iOS 26).

**Prerequisites:** None. This session is independent from all others.

**Complexity:** Low. Mechanical, same pattern repeated 7 times.

#### What to change

Each of the 7 files below has a P/Invoke that calls a `$sSo...CMa` symbol to get the ObjC class's Swift type metadata. Replace each with a call to the ObjC runtime's `objc_getClass()`, which returns the same class pointer through a stable API.

**Files and their current metadata accessor symbols:**

| File | Current symbol | ObjC class name |
|------|---------------|-----------------|
| `src/Swift.Runtime/src/Swift/URLResponse.cs` | `$sSo15NSURLResponseCMa` | `NSURLResponse` |
| `src/Swift.Runtime/src/Swift/OperationQueue.cs` | `$sSo16NSOperationQueueCMa` | `NSOperationQueue` |
| `src/Swift.Runtime/src/Swift/UIImage.cs` | `$sSo7UIImageCMa` | `UIImage` |
| `src/Swift.Runtime/src/Swift/NSImage.cs` | `$sSo7NSImageCMa` | `NSImage` |
| `src/Swift.Runtime/src/Swift/NSColor.cs` | `$sSo7NSColorCMa` | `NSColor` |
| `src/Swift.Runtime/src/Swift/CIContext.cs` | `$sSo9CIContextCMa` | `CIContext` |
| `src/Swift.Runtime/src/Swift/DispatchQueue.cs` | `$sSo17OS_dispatch_queueCMa` | `OS_dispatch_queue` |

#### Pre-research findings

All 7 files follow the **exact same pattern** (verified by code analysis):

1. **P/Invoke signature**: `private static extern TypeMetadata PInvoke_GetMetadata()` with `[UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]`
2. **Cached in static field**: `private static TypeMetadata? _cachedMetadata;` — called via null-coalescing `_cachedMetadata ??= PInvoke_GetMetadata()`
3. **Used for**: `metadata.Size` and `metadata.ValueWitnessTable` access — both files use the metadata for memory allocation and layout
4. **No inter-P/Invoke dependencies**: Other P/Invokes in each file (property getters, constructors, etc.) do NOT depend on the metadata result
5. **No existing `objc_getClass` P/Invoke** exists anywhere in the codebase — one must be created

**Critical type mismatch**: The current P/Invokes return `TypeMetadata` (a C# struct wrapping a pointer to Swift metadata). `objc_getClass()` returns `IntPtr`. For ObjC classes, the Swift metadata pointer IS the ObjC Class pointer (they share the same address), but you need to construct a `TypeMetadata` from the `IntPtr`. Check how `TypeMetadata` is constructed from a raw pointer — it likely has a constructor or static factory that takes `IntPtr`.

#### Implementation pattern

For each file:

1. **Read the file** and find the `$sSo...CMa` P/Invoke declaration. It will look like:
   ```csharp
   [DllImport(KnownLibraries.SwiftFoundation, EntryPoint = "$sSo15NSURLResponseCMa")]
   [UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]
   private static extern TypeMetadata PInvoke_GetMetadata();
   ```
   Note: the library name varies per file (`KnownLibraries.SwiftFoundation`, `KnownLibraries.UIKit`, `KnownLibraries.AppKit`, `KnownLibraries.CoreImage`, `KnownLibraries.SwiftDispatch`).

2. **Create a shared `objc_getClass` helper**. Since all 7 files need this, add it to a shared location (e.g., a new `ObjCInterop.cs` helper or an existing shared file in the runtime). No such P/Invoke exists in the codebase yet:
   ```csharp
   [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_getClass")]
   private static extern IntPtr ObjCGetClass([MarshalAs(UnmanagedType.LPUTF8Str)] string className);
   ```

3. **Convert `IntPtr` to `TypeMetadata`**. The call sites expect `TypeMetadata`, not `IntPtr`. Read `TypeMetadata`'s definition to find how to construct one from a raw pointer. Then create a helper method that calls `objc_getClass` and returns `TypeMetadata`.

4. **Update each file's caching pattern**. The null-coalescing pattern (`_cachedMetadata ??= PInvoke_GetMetadata()`) stays — just point it at the new helper instead of the old P/Invoke.

#### Important considerations

- The `$sSo...CMa` metadata accessors use `CallConvSwift`. The `objc_getClass` replacement uses standard C calling convention — no `CallConvSwift` needed.
- All 7 files use the metadata for `.Size` and `.ValueWitnessTable` access. Verify that a `TypeMetadata` constructed from `objc_getClass()` still provides valid VWT access for ObjC classes. For pure ObjC classes, the VWT should be the standard ObjC object VWT (retain/release semantics). If any file's VWT usage is incompatible with ObjC class metadata, flag it.
- `DispatchQueue.cs` has `$sSo17OS_dispatch_queueCMa` but also uses pure Swift symbols (`$s8Dispatch0A5QueueC4mainACvgZ`, `$s8Dispatch0A5QueueC6globalACyFZ`). This session only replaces the metadata accessor, not the other P/Invokes.

#### Tests

- **Unit tests**: Add tests verifying the `objc_getClass` P/Invoke declarations are correctly formed. Test that metadata resolution works for each type.
- **No new BindingTests needed** — this is a runtime-internal change. Existing tests that use these types (if any are unskipped) should continue passing.

#### Validation gates

1. `./run-tests.sh 2>&1 | tee /tmp/run-tests-results.txt` — all unit tests pass
2. `cd BindingTests && ./run-runtime-tests.sh --skip-regen --timeout 90 2>&1 | tee /tmp/runtime-tests-results.txt` — no regressions on simulator

#### Scope boundaries

- Do NOT modify URL.cs, URLRequest.cs, or Data.cs — those are handled in later sessions
- Do NOT change any generator code — this session is runtime-only
- Do NOT change any P/Invokes other than the `$sSo...CMa` metadata accessors in each file
- Do NOT delete any files

---

### Session 2: `ObjCBridgeableProjection` — scalar URL params, returns, optionals, and accessors — COMPLETE (91d52a2e)

**Objective:** Create the new `ObjCBridgeableProjection` type projection class and wire it through the generator pipeline. After this session, any Swift library method that takes or returns `Foundation.URL` (including `Optional<URL>` and property accessors) generates working bindings that cross the @_cdecl boundary as ObjC object pointers.

**Prerequisites:** None (independent from Session 1). But Session 1 should be merged before final validation if both are being worked.

**Complexity:** High. New projection type touching 5+ generator files, new XML attribute, new BindingTests.

#### Pre-research findings

**Key insight: `ObjCBridgeableProjection` is nearly identical to `ObjCBridgedProjection` in behavior.** Both use `IntPtr` for P/Invoke, extract `.Handle` for parameters, and use `MarshallingHelpers.FormatObjCBridgeCall()` for returns. The difference is the dispatch path (keyed off `objcBridgeable` flag vs `objcBridged` flag) and the Swift-side wrapper behavior (ObjC bridge cast vs direct ObjC pointer).

**Exact file locations for all changes:**

| What | File | Where |
|------|------|-------|
| TypeRecordFlags enum | `src/Swift.Bindings/src/TypeDatabase/TypeRecord.cs` | Add `ObjCBridgeable = 1 << N` after existing flags |
| XML attribute parsing | `src/Swift.Bindings/src/TypeDatabase/TypeDatabase.cs` ~line 166 | Parse `objcBridgeable` attribute alongside `objcBridged`, `frozen`, etc. |
| Flag composition | Same file ~line 213-225 | Add to bitwise OR chain |
| IProjectionVisitor interface | `src/Swift.Bindings/src/Marshaler/Projection/IProjectionVisitor.cs` | Add `T Visit(ObjCBridgeableProjection p);` — **compile-time exhaustive** |
| TypeProjectionFactory dispatch | `src/Swift.Bindings/src/Marshaler/Projection/TypeProjectionFactory.cs` ~line 308-341 | Add check BEFORE the existing `nativeType` check at line 327, so `objcBridgeable` takes precedence over `NativeRemappedProjection` for URL |
| Accessor visitors (3 classes) | `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/AccessorConversionVisitors.cs` | Add `Visit(ObjCBridgeableProjection)` to all 3 visitor classes |
| FormatObjCBridgeCall helper | `src/Swift.Bindings/src/Marshaler/MarshallingHelpers.cs` line 471-477 | Already exists — reuse for returns. Returns `Runtime.GetNSObject<T>(ptr)!` or `Runtime.GetINativeObject<T>(ptr, false)!` |
| CdeclParamMapper insertion | `src/Swift.Bindings/src/Emitter/StringEmitter/CdeclParamMapper.cs` | Insert after Optional<reference type> handling (line 124), before generic containers (line 196) |
| CdeclReturnMapping insertion | `src/Swift.Bindings/src/Emitter/StringEmitter/CdeclReturnMapping.cs` | Insert after Optional<reference> (line 55), before generic containers (line 58) |

**CdeclParamMapper dispatch chain** (16 categories in order — the new check goes between #4 and #5):
1. Line 54: Primitives (Int, Bool, etc.)
2. Line 72: AnyObject → `UnsafeMutableRawPointer` + `Unmanaged<AnyObject>`
3. Line 81: Protocol existentials
4. Line 92: Optional<reference type> → nullable pointer + `Unmanaged<AnyObject>` bridge
5. **→ INSERT objcBridgeable check HERE** (after line 124)
6. Line 129: Optional<blittable primitive>
7. Line 160: Optional<opaque type>
8. Line 196: Generic containers
9. Line 220: Foundation.Date
10. Line 233: Foundation.Data
11. Line 242: Swift.String
12. Line 266: Class/ObjCBridged/ObjCRooted
13–17: Remaining types

**Visitor implementations** — all 3 visitors should be simple for `ObjCBridgeableProjection`:
- `AccessorGetterConversionVisitor.Visit()` → `(null, false)` (return plan handles conversion)
- `AccessorSetterConversionVisitor.Visit()` → `(null, false)` (parameter plan handles `.Handle` extraction)
- `OptionalAccessorGetterVisitor.Visit()` → null check + `FormatObjCBridgeCall` (same pattern as `ObjCBridgedProjection`)

#### What to build

**1. XML attribute: `objcBridgeable="true"`**

Add support for a new `objcBridgeable` attribute on `<typedeclaration>` elements in the type database XML. This is distinct from `nativeType` (which controls public API remap) and `objcBridged` (which marks ObjC class wrappers). `objcBridgeable` means: "this Swift value type freely bridges to an ObjC class via `_ObjectiveCBridgeable`, so cross the @_cdecl boundary as an ObjC object pointer."

- Parse the attribute in the type database XML parser (find where `nativeType`, `objcBridged`, `frozen`, etc. are parsed — look in TypeDatabase/TypeRecord loading code)
- Store it on the `TypeRecord` (add a `bool ObjCBridgeable` property or similar)
- Add `objcBridgeable="true"` to the URL entry in `src/Swift.Runtime/src/Swift/FoundationDatabase.xml`:
  ```xml
  <typedeclaration kind="struct" name="URL" module="Foundation" mangledName="$s10Foundation3URLV"
    frozen="false" requiresMemoryManagement="true" nativeType="Foundation.NSUrl" objcBridgeable="true" />
  ```

**2. New projection class: `ObjCBridgeableProjection`**

Create `src/Swift.Bindings/src/Marshaler/Projection/ObjCBridgeableProjection.cs` as a new `ITypeProjection` implementation.

Reference existing projections as templates:
- `ObjCBridgedProjection.cs` — similar in that it uses `IntPtr` for P/Invoke, but it's for ObjC classes, not bridgeable value types
- `NativeRemappedProjection.cs` — the current URL projection being replaced; study its visitor implementations and element conversion methods

Key properties and methods:
- `PublicType` → the .NET native type name (e.g., `"Foundation.NSUrl"`) — same as `NativeRemappedProjection`
- `PInvokeType` → `"IntPtr"` (always — ObjC handle is blittable)
- `SwiftContainerGenericType` → see Session 3 (for now, can return `PInvokeType` or throw; containers aren't handled yet)
- `MarshalFromSwiftType` → `PublicType` (no intermediate wrapper type needed)
- `ElementRequiresDisposal` → `false` (ObjC handles don't need C#-side disposal)
- `UsesObjCContainerBridge` → `true` (for Session 3 to key off of; can be added now)

`GetParameterPlan()`:
- No wrapper construction needed. Extract the ObjC handle directly from the .NET type.
- Setup: extract handle — e.g., `var urlHandle = url.GetHandle();` or `var urlHandle = url.Handle;`
- PInvokeExpression: the `IntPtr` handle variable
- No `using` statement needed (no disposal)
- Study `ObjCBridgedProjection.GetParameterPlan()` for the pattern

`GetReturnPlan()`:
- Receive `IntPtr` from P/Invoke, wrap as the .NET native type
- Use `ObjCRuntime.Runtime.GetNSObject<T>()` or `MarshallingHelpers.FormatObjCBridgeCall()` — study how `ObjCBridgedProjection.GetReturnPlan()` does this
- Handle both direct return and indirect result strategies
- For nullable returns (`Optional<URL>`): `IntPtr.Zero` → `null`

Visitor implementations (required by projection parity constraint):
- `AccessorGetterConversionVisitor.Visit()` — convert from return type to public type (similar to return plan)
- `AccessorSetterConversionVisitor.Visit()` — convert from public type to parameter type (similar to parameter plan)
- `OptionalAccessorGetterVisitor.Visit()` — handle `Optional<URL>` property getters

Study how `NativeRemappedProjection` implements these visitors in `AccessorConversionVisitors.cs` — the new projection needs the same visitor methods but with direct IntPtr ↔ ObjC class conversion instead of `Swift.URL.FromNSUrl()`/`.ToNSUrl()`.

**3. TypeProjectionFactory dispatch**

In `src/Swift.Bindings/src/Marshaler/Projection/TypeProjectionFactory.cs`, the `CreateProjectionForTypeRecord()` method dispatches based on TypeRecord properties. The current order is:
1. Line 308: `IsObjCBridged(typeRecord)` → `ObjCBridgedProjection`
2. Line 327: `typeRecord.NativeTypeName != null` → `NativeRemappedProjection`
3. (other checks follow)

Add the `objcBridgeable` check **BEFORE** the `nativeType` check (between lines 308 and 327), so it takes precedence:
```csharp
// After IsObjCBridged check, before NativeTypeName check:
if (typeRecord.Flags.HasFlag(TypeRecordFlags.ObjCBridgeable) && typeRecord.NativeTypeName != null)
    return new ObjCBridgeableProjection(typeRecord.NativeTypeName.FullyQualifiedName);
```

This means:
- URL (`nativeType` + `objcBridgeable`) → `ObjCBridgeableProjection` (new)
- Data (`nativeType` only, no `objcBridgeable`) → `NativeRemappedProjection` (unchanged)
- URLResponse (`objcBridged`) → `ObjCBridgedProjection` (unchanged)

**4. CdeclParamMapper changes**

In `src/Swift.Bindings/src/Emitter/StringEmitter/CdeclParamMapper.cs`, add handling for `objcBridgeable` types in the `Map()` method. When a parameter's type record has `ObjCBridgeable == true`:

Swift @_cdecl parameter: `UnsafeMutableRawPointer`
Reconstruction: `Unmanaged<AnyObject>.fromOpaque(param).takeUnretainedValue() as! Foundation.URL`

This follows the existing AnyObject pattern at lines 69-77. For `Optional<URL>` parameters, follow the existing `Optional<reference type>` pattern at lines 89-124 which already uses `UnsafeMutableRawPointer?` + `Unmanaged<AnyObject>` + cast.

The key question is WHERE to add this check in the `Map()` method's dispatch chain. Read the method carefully — it has ~16 type categories checked in order. The `objcBridgeable` check should go early (before the non-frozen struct fallback) and should check the type record, not just the type spec.

**5. MethodWrapperEmitter return path**

In `src/Swift.Bindings/src/Emitter/StringEmitter/MethodWrapperEmitter.cs`, update the return value handling. When the return type is `objcBridgeable`:

```swift
// Bridge to ObjC and return as retained pointer
let nsObj = result as NSURL   // or as! NSURL for non-optional
return Unmanaged.passRetained(nsObj).toOpaque()
```

For `Optional` returns:
```swift
guard let nsObj = result as NSURL? else { return nil }
return Unmanaged.passRetained(nsObj).toOpaque()
```

Study how class returns are currently handled (they use `Unmanaged.passRetained(result).toOpaque()`) — the ObjC bridgeable return is the same pattern but with an `as` cast before the retain.

**6. TypeConversionHandler updates**

In `src/Swift.Bindings/src/Marshaler/TypeConversionHandler.cs`, the methods `GetNativeParameterConversion()` (line ~258) and `GetNativeReturnConversion()` (line ~282) currently emit `Swift.URL.FromNSUrl(param)` and `result.ToNSUrl()`. These need to be updated or bypassed for `objcBridgeable` types, since the new projection handles conversion directly without wrapper types.

Check whether these methods are still called for types that go through `ObjCBridgeableProjection`. If the new projection's `GetParameterPlan()` and `GetReturnPlan()` handle everything, these `TypeConversionHandler` methods may not be reached for URL anymore. Verify by tracing the code path.

#### BindingTests

Add Swift test functions to `BindingTests/Sources/SwiftBindingsTestLib/` and C# runtime tests to `BindingTests/RuntimeTestsApp/`:

**Swift source** (add to an appropriate existing file or create a new one for URL/Foundation tests):
```swift
public class URLTestHelper {
    public var storedURL: URL

    public init(url: URL) {
        self.storedURL = url
    }

    // Scalar param + return
    public func getURL() -> URL {
        return storedURL
    }

    public func setURL(url: URL) {
        storedURL = url
    }

    // Optional param + return
    public func getOptionalURL() -> URL? {
        return storedURL
    }

    public func acceptOptionalURL(url: URL?) -> Bool {
        if let url = url {
            storedURL = url
            return true
        }
        return false
    }

    // Property accessor
    public var url: URL {
        get { storedURL }
        set { storedURL = newValue }
    }

    public var optionalURL: URL? {
        get { storedURL }
    }
}
```

**C# tests**: Verify round-trip correctness — create an NSUrl, pass it through generated bindings, get it back, verify the URL string is preserved. Test nil/null for optionals. Test property getter/setter.

#### Validation gates

1. `./run-tests.sh 2>&1 | tee /tmp/run-tests-results.txt` — all unit tests pass
2. `cd BindingTests && ./build-and-test.sh 2>&1 | tee /tmp/build-and-test-results.txt` — generated bindings compile, runtime tests pass
3. `./validate-libraries.sh 2>&1 | tee /tmp/validate-results.txt` — no regressions on third-party libraries (Nuke, Alamofire, Kingfisher all use URL)

#### Scope boundaries

- Do NOT modify URL.cs or URLRequest.cs — they stay for now (deletion is Session 4)
- Do NOT handle containers (`[URL]`, `[String: URL]`, etc.) — that is Session 3
- Do NOT add `objcBridgeable` to URLRequest yet — that is Session 4
- Do NOT modify `ArrayProjection.cs`, `DictionaryProjection.cs`, or `SetProjection.cs`
- The `NativeRemappedProjection` class should remain unchanged — Data still uses it

---

### Session 3: Whole-container ObjC bridge for `objcBridgeable` elements

**Objective:** When a container (`Array`, `Dictionary`, `Set`) holds `objcBridgeable` elements, bridge the entire container to its ObjC collection counterpart on the Swift side, bypassing the `SwiftArray<T>` / `SwiftDictionary<K,V>` pipeline. After this session, methods taking or returning `[URL]`, `[String: URL]`, `Set<URL>`, and `[[URL]]` generate working bindings.

**Prerequisites:** Session 2 must be complete (the `ObjCBridgeableProjection` class and `UsesObjCContainerBridge` flag must exist).

**Complexity:** High. Forks the container projection pipeline — touches `ArrayProjection`, `DictionaryProjection`, `SetProjection`, and the @_cdecl wrapper emission for containers.

#### Why a container fork is needed

The existing container pipeline is structurally coupled to the element projection's `SwiftContainerGenericType` and `MarshalFromSwiftType`. For URL, this means `SwiftArray<Swift.URL>` and `SwiftMarshal.MarshalFromSwift<SwiftArray<Swift.URL>>()`. Replacing `Swift.URL` with `IntPtr` doesn't compile — `SwiftArray<IntPtr>` is not valid (`IntPtr` doesn't implement `ISwiftObject`). A minimal `Swift.URL` stub would still need Foundation's Swift ABI metadata for deserialization — the exact dependency we're eliminating.

The solution: when the element projection has `UsesObjCContainerBridge == true`, skip the `SwiftArray<T>` pipeline entirely. Instead, bridge the whole container to NSArray/NSDictionary/NSSet on the Swift side, and pass the ObjC collection as an `IntPtr` to C#.

#### What to change

**1. Container projection fork**

For each of `ArrayProjection.cs`, `DictionaryProjection.cs`, and `SetProjection.cs`:

Read the existing `GetParameterPlan()` and `GetReturnPlan()` methods. They call `_elementProjection.SwiftContainerGenericType`, `_elementProjection.GetParameterElementConversion()`, etc. Add a check at the top of each method: if the element projection has `UsesObjCContainerBridge == true`, take an alternate code path.

**Array — ObjC bridge path:**

Parameter direction (C# → Swift):
- C# constructs an `NSArray` from the `IEnumerable<NSUrl>` elements and passes `IntPtr`
- @_cdecl receives `UnsafeMutableRawPointer`, reconstructs:
  ```swift
  let nsArray = Unmanaged<AnyObject>.fromOpaque(param).takeUnretainedValue() as! NSArray
  let urls = nsArray as! [Foundation.URL]
  ```

Return direction (Swift → C#):
- @_cdecl bridges the result:
  ```swift
  let nsArray = result as NSArray
  return Unmanaged.passRetained(nsArray).toOpaque()
  ```
- C# receives `IntPtr`, wraps as `NSArray`, extracts typed elements

**Dictionary — ObjC bridge path:**

Same pattern but with `NSDictionary`:
- Swift: `result as NSDictionary` / `nsDictionary as! [Key: Value]`
- Only applies when the VALUE projection (and/or KEY projection) has `UsesObjCContainerBridge`
- If only the value is bridgeable (e.g., `[String: URL]`), the entire dictionary still bridges to NSDictionary because Swift's ObjC bridge handles String↔NSString automatically

**Set — ObjC bridge path:**

Same pattern with `NSSet`:
- Swift: `result as NSSet` / `nsSet as! Set<URL>`

**2. CdeclParamMapper container handling**

The @_cdecl wrapper needs to know that a container parameter is arriving as an ObjC collection pointer (not as a Swift container struct). Check how containers are currently handled in `CdeclParamMapper.Map()` — they likely fall into the non-frozen struct or generic container path. For the ObjC bridge case, the parameter should be `UnsafeMutableRawPointer` with ObjC collection reconstruction (same as scalar ObjC bridge, but with an additional `as! [Type]` cast).

**3. MethodWrapperEmitter container returns**

Similarly, when a method returns a container with ObjC-bridgeable elements, the wrapper needs to bridge the entire container to ObjC before returning. Check how container returns are currently emitted and add the ObjC bridge path.

**4. C# side collection conversion**

The generated C# needs to convert between `IntPtr` (ObjC collection handle) and typed .NET collections. The exact helper APIs need to be determined empirically during implementation:

- Study how `ObjCBridgedProjection` handles collections currently
- Check what .NET iOS runtime helpers exist for `NSArray` → `T[]` / `IReadOnlyList<T>` conversion
- Check `INativeObject` constraints and whether `NSUrl` satisfies them for typed collection extraction
- For dictionary generics with string keys, verify the .NET binding layer handles the NSString↔string conversion

**IMPORTANT**: The C# collection conversion specifics are the least certain part of this design. If the standard .NET helpers don't work cleanly for a particular collection shape, the worker should document what was tried and what the limitation is, and message the lead for guidance rather than inventing a workaround.

#### BindingTests

Add Swift test functions and C# tests for container cases:

**Swift source:**
```swift
// Add to URLTestHelper or create a new class
public class URLContainerTestHelper {
    public func getURLArray() -> [URL] {
        return [URL(string: "https://example.com")!, URL(string: "https://test.com")!]
    }

    public func acceptURLArray(urls: [URL]) -> Int {
        return urls.count
    }

    public func getURLDictionary() -> [String: URL] {
        return ["home": URL(string: "https://example.com")!, "api": URL(string: "https://api.example.com")!]
    }

    public func getURLSet() -> Set<URL> {
        return Set([URL(string: "https://example.com")!, URL(string: "https://test.com")!])
    }

    public func acceptURLSet(urls: Set<URL>) -> Int {
        return urls.count
    }

    public func getNestedURLArray() -> [[URL]] {
        return [[URL(string: "https://a.com")!], [URL(string: "https://b.com")!, URL(string: "https://c.com")!]]
    }
}
```

**C# tests**: Verify round-trip for each container shape. Check element count, verify URL strings are preserved, test empty containers.

#### Validation gates

1. `./run-tests.sh 2>&1 | tee /tmp/run-tests-results.txt`
2. `cd BindingTests && ./build-and-test.sh 2>&1 | tee /tmp/build-and-test-results.txt`
3. `./validate-libraries.sh 2>&1 | tee /tmp/validate-results.txt`

#### Scope boundaries

- Do NOT modify `NativeRemappedProjection` or the existing non-bridgeable container paths — Data and other types must continue working
- Do NOT handle URLRequest — that is Session 4
- Do NOT delete URL.cs or URLRequest.cs
- If a container shape proves problematic on the C# side (e.g., nested containers, dictionary with string keys), implement what works and document the limitation — don't block the session on edge cases that can be iterated on later

---

### Session 4: URLRequest + cleanup + deletion

**Objective:** Apply the proven `ObjCBridgeableProjection` to URLRequest, verify it works through all paths, then delete the hand-written `URL.cs` and `URLRequest.cs` runtime files and rewrite tests. This is the payoff session — ~1,100 lines of fragile hand-written code removed.

**Prerequisites:** Sessions 2 and 3 must be complete (scalar + container ObjC bridge paths proven for URL).

**Complexity:** Medium. Mostly applying a proven pattern + cleanup. URLRequest's mutable↔immutable ObjC bridge is the one thing to watch.

#### What to change

**1. Add URLRequest to the ObjC bridgeable path**

In `src/Swift.Runtime/src/Swift/FoundationDatabase.xml`, update the URLRequest entry:
```xml
<!-- BEFORE -->
<typedeclaration kind="struct" name="URLRequest" module="Foundation"
  mangledName="$s10Foundation10URLRequestV" frozen="false" requiresMemoryManagement="true" />

<!-- AFTER -->
<typedeclaration kind="struct" name="URLRequest" module="Foundation"
  mangledName="$s10Foundation10URLRequestV" frozen="false" requiresMemoryManagement="true"
  nativeType="Foundation.NSUrlRequest" objcBridgeable="true" />
```

This should cause `TypeProjectionFactory` to create `ObjCBridgeableProjection` for URLRequest, routing it through the same path as URL.

**2. Verify URLRequest bridging works**

URLRequest↔NSURLRequest bridging in Swift is non-trivial:
- `URLRequest` is mutable (value type with mutating methods)
- `NSURLRequest` is immutable; `NSMutableURLRequest` is the mutable ObjC counterpart
- Swift's `as` bridge from `URLRequest` → `NSURLRequest` creates an immutable copy
- Swift's `as` bridge from `NSURLRequest` → `URLRequest` works for both mutable and immutable

For our use case (passing URLRequest as a parameter to library methods), the C# side has `Foundation.NSUrlRequest`. When passed to a Swift method that takes `URLRequest`, the @_cdecl wrapper does `nsUrlRequest as! URLRequest` which creates a Swift URLRequest copy. This should preserve all properties (URL, headers, timeout, etc.).

**Test carefully**: Create an NSUrlRequest with specific headers/timeout, pass it through the binding, have Swift read those properties and return them, verify they match on the C# side.

**3. Delete URL.cs and URLRequest.cs**

Before deleting, verify the deletion gate:

1. All verification matrix cases pass in BindingTests on simulator
2. Container cases tested: `[URL]` param, `[URL]` return, `[String: URL]` return, `Set<URL>` param/return, `[[URL]]` return
3. Optional cases tested: `URL?` param, `URL?` return
4. Property accessor cases tested: `var url: URL { get set }`
5. `validate-libraries.sh --tier all` shows no regressions
6. No remaining code references `Swift.URL` or `Swift.URLRequest` outside the files being deleted

To check criterion 6, grep the entire codebase:
```bash
grep -r "Swift\.URL[^R]" --include="*.cs" --include="*.xml" src/ BindingTests/
grep -r "Swift\.URLRequest" --include="*.cs" --include="*.xml" src/ BindingTests/
```

Any remaining references must be updated or removed before deletion. Common places:
- `TypeConversionHandler.cs` — `IsFoundationURL()`, `GetNativeParameterConversion()`, `GetNativeReturnConversion()`, `GetSwiftWrapperTypeForNative()` — these methods reference `Swift.URL` and `Swift.URLRequest` directly. They may be dead code if the new `ObjCBridgeableProjection` bypasses them entirely. Verify and remove.
- `NativeRemappedProjection.cs` — if the factory no longer creates it for URL, the URL-specific code paths may be dead. Verify Data still works through it.
- Test files — any test that directly constructs `Swift.URL` or `Swift.URLRequest`.

**4. Rewrite URLRequestTests.cs**

The current `BindingTests/RuntimeTestsApp/Marshalling/URLRequestTests.cs` is:
- Skipped at class level (non-blittable P/Invoke issue)
- Tests hand-written wrapper APIs (`URL.FromString()`, `URLRequest.FromURL()`, etc.)

Rewrite it to test the generated binding path instead. The tests from Sessions 2 and 3 already cover URL. Add URLRequest-specific tests:

**Swift source:**
```swift
public class URLRequestTestHelper {
    public func createRequest(url: URL) -> URLRequest {
        return URLRequest(url: url)
    }

    public func getRequestURL(request: URLRequest) -> URL? {
        return request.url
    }

    public func getTimeout(request: URLRequest) -> Double {
        return request.timeoutInterval
    }

    // Container
    public func getRequestArray() -> [URLRequest] {
        return [URLRequest(url: URL(string: "https://a.com")!), URLRequest(url: URL(string: "https://b.com")!)]
    }
}
```

**C# tests**: Verify URLRequest round-trip, URL property preservation, timeout preservation, container handling.

**5. Remove the `[Skip]` attribute**

The URLRequestTests class currently has a `[Skip("URL/URLRequest P/Invokes use non-blittable types...")]` attribute. The rewritten tests should NOT have this skip — they should run and pass.

#### Validation gates

This session has the strictest validation — it's the final gate:

1. `./run-tests.sh 2>&1 | tee /tmp/run-tests-results.txt`
2. `cd BindingTests && ./build-and-test.sh 2>&1 | tee /tmp/build-and-test-results.txt`
3. `./validate-libraries.sh --tier all 2>&1 | tee /tmp/validate-results.txt` (note: `--tier all`, not default)
4. Full deletion gate verified (all 6 criteria above)
5. `grep -r "Swift\.URL[^R]" --include="*.cs" src/` returns no results
6. `grep -r "Swift\.URLRequest" --include="*.cs" src/` returns no results

#### Scope boundaries

- Do NOT delete Data.cs — it stays on its current path
- Do NOT delete URLResponse.cs or OperationQueue.cs — Session 1 fixed their metadata accessors, but their property P/Invokes are a separate concern for a future effort
- Do NOT modify the `ObjCBridgeableProjection` class unless URLRequest reveals a bug — at this point the projection should be proven
- If URLRequest bridging reveals edge cases (e.g., mutable headers not preserved through NSURLRequest round-trip), document them and discuss with the lead before working around them

## Verification Matrix

All cases must pass in BindingTests (runtime, on iOS simulator) before the deletion gate is satisfied.

| Context | @_cdecl Swift side | C# P/Invoke type | C# public API |
|---------|-------------------|-------------------|---------------|
| `url: URL` | `UnsafeMutableRawPointer` → `as! URL` | `IntPtr` | `NSUrl` param |
| `-> URL` | `url as NSURL` → retained pointer | `IntPtr` | wrap as `NSUrl` |
| `url: URL?` | `UnsafeMutableRawPointer?` → map + cast | `IntPtr` (0 = nil) | `NSUrl?` param |
| `-> URL?` | nullable retained pointer | `IntPtr` | null or `NSUrl` |
| `urls: [URL]` | `UnsafeMutableRawPointer` → `as! [URL]` | `IntPtr` (ObjC collection) | `IEnumerable<NSUrl>` |
| `-> [URL]` | `result as NSArray` → retained pointer | `IntPtr` | typed .NET collection |
| `-> [String: URL]` | `result as NSDictionary` → retained pointer | `IntPtr` | typed .NET dictionary |
| `urls: Set<URL>` | `UnsafeMutableRawPointer` → `as! Set<URL>` | `IntPtr` (ObjC collection) | `IEnumerable<NSUrl>` |
| `-> Set<URL>` | `result as NSSet` → retained pointer | `IntPtr` | typed .NET collection |
| `-> [[URL]]` | `result as NSArray` (recursive bridge) | `IntPtr` | nested .NET collections |
| Property getter `var url: URL` | Same as return | Same | `AccessorGetterConversionVisitor` |
| Property setter `var url: URL` | Same as param | Same | `AccessorSetterConversionVisitor` |
| Optional property `var url: URL?` | Same as optional return | Same | `OptionalAccessorGetterVisitor` |

## Risks

- **URLRequest is non-trivially bridged**: Unlike URL↔NSUrl (nearly identical storage), URLRequest↔NSURLRequest bridging in Swift creates a copy (mutable URLRequest → immutable NSURLRequest). This is a data copy, not a crash risk, but test carefully for property preservation (headers, timeout, etc.).
- **Container ObjC bridge performance**: Bridging `[URL]` to `NSArray` copies the array. For the small URL arrays typical in real APIs, this is negligible. If a library passes very large arrays of URLs, measure.
- **C# collection conversion specifics**: The exact .NET iOS runtime helpers for `IntPtr` → typed collection conversion need to be verified during implementation. `INativeObject` constraints and untyped collection fallbacks in the .NET binding layer may require careful handling for some collection shapes (e.g., dictionary generics with string keys vs. pure NS-object generic cases). The architecture is sound; the specific helper APIs will be determined empirically.
- **Validation library impact**: Many libraries use URL (Nuke, Alamofire, Kingfisher, etc.). Run full `validate-libraries.sh --tier all` after each phase.

## Review History

This design was developed through multi-round architectural review:

1. **Initial design** — proposed ObjC bridge in @_cdecl wrappers for URL/URLRequest
2. **Review round 1** (Claude) — endorsed core direction, recommended converting Data for consistency, broadened scope
3. **Review round 2** (Codex) — corrected ABI signature (`UnsafeMutableRawPointer`, not `AnyObject`), recommended separate `objcBridgeable` attribute distinct from `nativeType`, identified 7 `$sSo...CMa` files (not 2), conservative on Data
4. **Review round 3** (Claude + Codex) — resolved container boundary question (whole-container ObjC bridge, not element-level IntPtr), established deletion gate, finalized verification matrix
5. **Final review** (Codex) — approved direction, tightened C# collection conversion language to avoid promising specific helper APIs before implementation verification
