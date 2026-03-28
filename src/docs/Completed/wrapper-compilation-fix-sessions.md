# Wrapper Compilation Fix Sessions

**Created**: March 28, 2026
**Goal**: Push swift wrapper compilation from 51/56 to 55/56. Fix 4 generator bugs + 2 infrastructure issues across 5 sessions.
**Pre-state**: 51/56 passing after MCB dedup + FindBlockEnd fixes (commit `424e2a16`)

**Sessions**: 5 sequential sessions, executed via session orchestrator.

---

## Session 1: Kingfisher MCB Struct Self-Reconstruction Bug — COMPLETE (a8aadd63)

**Goal**: Fix the `MethodClosureBridge` struct self-reconstruction to use `assumingMemoryBound` instead of `Unmanaged<T>` for struct parent types. This fixes Kingfisher (51 -> 52/56).

### Context

The compilation failures doc attributed Kingfisher's failure to "4252 internal type references." Investigation disproved this entirely:
- Kingfisher has **zero** internal types (`internalTypeNames` is empty, confirmed via `wrapper-context.json`)
- The actual errors are **9 instances** of `'Unmanaged' requires that 'X' be a class type`
- All 9 affected types are **public structs** that conform to protocols with closure-bearing methods

The root cause is in `MethodClosureBridge.cs` line 331. The MCB emitter always uses `Unmanaged<T>.fromOpaque(self_).takeUnretainedValue()` for instance method self-reconstruction, regardless of whether the parent type is a class or struct. `Unmanaged<T>` requires `T: AnyObject` (class protocol), so it fails at compile time for struct parents.

### Affected Types (all `public struct` in Kingfisher)

| Struct | Protocol |
|--------|----------|
| `LocalFileImageDataProvider` | `ImageDataProvider` |
| `Base64ImageDataProvider` | `ImageDataProvider` |
| `RawImageDataProvider` | `ImageDataProvider` |
| `ThumbnailImageDataProvider` | `ImageDataProvider` |
| `AVAssetImageDataProvider` | `ImageDataProvider` |
| `PhotosPickerItemImageDataProvider` | `ImageDataProvider` |
| `PHPickerResultImageDataProvider` | `ImageDataProvider` |
| `DelayRetryStrategy` | `RetryStrategy` |
| `NetworkRetryStrategy` | `RetryStrategy` |

### The Fix

**File**: `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/MethodClosureBridge.cs`, lines 328-332

Current (buggy):
```csharp
if (isInstance)
{
    swiftWriter.WriteLine($"    let selfObj = Unmanaged<{typeName}>.fromOpaque(self_).takeUnretainedValue()");
}
```

Fix — check if parent is class vs struct:
```csharp
if (isInstance)
{
    bool isClassParent = parentDecl is ClassDecl;
    if (isClassParent)
        swiftWriter.WriteLine($"    let selfObj = Unmanaged<{typeName}>.fromOpaque(self_).takeUnretainedValue()");
    else
        swiftWriter.WriteLine($"    let selfObj = self_.assumingMemoryBound(to: {typeName}.self).pointee");
}
```

This matches the pattern used by `SelfReconstructionEmitter.cs` (lines 31-44), `PropertyWrapperEmitter`, `SubscriptWrapperEmitter`, and `ArraySliceNormalizationEmitter` — all of which already handle the class/struct distinction.

**Also check**: The MCB already handles this correctly for non-closure parameter loading (lines 259-261, 304-310) using `IsClassTypeForSwift()`. Only the self-reconstruction path (line 331) was missed.

### Other Potentially-Affected Emitters

Grep for `Unmanaged<` in emitter files that handle self-reconstruction. These other sites are likely safe but should be audited:
- `AsyncStreamEmitter.cs` line 119 — likely safe (async props on structs uncommon)
- `ProtocolExtensionEmitter.cs` lines 1331, 1548 — safe (protocol extension dispatch via existential, always class-bound)
- `ForeignTypeExtensionEmitter.cs` lines 390, 432, 503 — safe by design (foreign types are ObjC classes)

### Deliverables

1. Fix `MethodClosureBridge.cs` line 331 — class/struct self-reconstruction
2. Update `src/docs/swift-wrapper-compilation-failures.md` — correct the diagnosis from "internal type references" to "MCB struct self-reconstruction bug"
3. Unit tests in `MethodClosureBridgeTests.cs` — add test with `StructDecl` parent + closure method, assert generated Swift uses `assumingMemoryBound` (not `Unmanaged`)
4. BindingTests Swift source: add a struct with a closure-bearing method in `BindingTests/Sources/SwiftBindingsTestLib/Closures/` — e.g., a `struct DataTransformer` with a `process(completion: @escaping (Result) -> Void)` method
5. BindingTests runtime test — verify the struct's closure method works end-to-end
6. Validate Kingfisher: `./validate-libraries.sh --filter Kingfisher`

### Validation

- `./run-tests.sh` — unit tests pass
- `./validate-libraries.sh --filter Kingfisher` — swift_compile passes
- `cd BindingTests && ./build-and-test.sh` — new struct MCB tests pass

### Key files to read before starting

- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/MethodClosureBridge.cs` (full file, focus on lines 294-340)
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/SelfReconstructionEmitter.cs` (lines 31-44 for the correct pattern)
- `src/Swift.Bindings/tests/UnitTests/EmitterTests/MethodClosureBridgeTests.cs`
- `BindingTests/Sources/SwiftBindingsTestLib/Closures/` (existing closure sources)
- `BindingTests/RuntimeTestsApp/Closures/ClosureEdgeCaseTests.cs`

---

## Session 2: GRDB EveryProtocol Hidden Requirements — COMPLETE (c6d8fff7)

**Goal**: Fix 2 remaining GRDB EveryProtocol conformance errors for `RowAdapter` and `FTS5Tokenizer`. This fixes GRDB (52 -> 53/56).

### Context

After the MCB dedup fix (commit `424e2a16`), GRDB's 26 MCB redeclaration errors are resolved. Only 2 EveryProtocol conformance errors remain. Each has a distinct root cause.

### Bug A: `RowAdapter` — `some` Parameter Parsing Failure

**Error**: `type 'EveryProtocol' does not conform to protocol 'RowAdapter'`

**Protocol definition** (from GRDB swiftinterface):
```swift
public protocol RowAdapter : Swift.Sendable {
    func _layoutedAdapter(from layout: some _RowLayout) throws -> any _LayoutedRowAdapter
}
extension GRDB.RowAdapter {
    public func addingScopes(_ scopes: [String: any RowAdapter]) -> any RowAdapter
}
```

The actual protocol requirement is `_layoutedAdapter(from:)`. The `addingScopes` is an extension method (default implementation, not a requirement).

**Root cause**: `GenericSignatureParser.cs` line 33-34. The `some _RowLayout` opaque parameter creates `τ_1_0` in the generic signature, making the ABI JSON have:
- `genericSig`: 2 type params (`τ_0_0: RowAdapter`, `τ_1_0: _RowLayout`)
- `sugared_genericSig`: 1 type param (`Self: RowAdapter`)

The parser throws `InvalidOperationException("Generic and sugared parameter counts do not match.")` when counts differ (2 vs 1). This means `_layoutedAdapter` fails parsing and never enters `ProtocolDecl.Methods`. The emitter only sees `addingScopes` (extension method) and the conformance fails because the actual requirement is missing.

**Scope**: 21 GRDB protocols have methods with `some` parameters causing this mismatch. Most are filtered out by other EveryProtocol gates. `RowAdapter` slips through because its only parseable method passes all checks.

**Fix approach**: Skip the EveryProtocol conformance when a protocol has requirements that failed ABI parsing. The protocol's ABI JSON children include `_layoutedAdapter` with `protocolReq: true` and `reqNewWitnessTableEntry: true`, but it doesn't appear in `ProtocolDecl.Methods`. Add a validation pass in `EveryProtocolEmitter.EmitProtocolConformance()` comparing expected requirements (from ABI JSON) against parsed requirements (in `ProtocolDecl.Methods`). If any are missing, skip the conformance entirely.

**Key files**:
- `src/Swift.Bindings/src/Parser/GenericSignatureParser.cs` lines 16-48 — the mismatch throw site (line 34)
- `src/Swift.Bindings/src/Emitter/StringEmitter/EveryProtocolEmitter.cs` lines 682-882 — `EmitProtocolConformance`
- `src/Swift.Bindings/src/Marshaler/ModuleHandler.cs` lines 552-588 — protocol selection pipeline

### Bug B: `FTS5Tokenizer` — `@convention(c)` vs `@escaping` Mismatch

**Error**: `type 'EveryProtocol' does not conform to protocol 'FTS5Tokenizer'` with note "candidate has non-matching type"

**Protocol definition** (from GRDB swiftinterface):
```swift
public protocol FTS5Tokenizer : AnyObject {
    func tokenize(context: UnsafeMutableRawPointer?, tokenization: FTS5Tokenization,
                  pText: UnsafePointer<CChar>?, nText: CInt,
                  tokenCallback: @convention(c) (...) -> CInt) -> CInt
}
```

**Root cause**: The ABI JSON `TypeFunc` node does NOT encode `@convention(c)` — the attribute is absent. The EveryProtocol closure stub emitter (`EmitClosureMethodStub` at line 1115) renders closures with `@escaping` (line 1195). Swift requires exact convention matching: `@escaping` does not match `@convention(c)`.

**Fix approach**: Skip the EveryProtocol conformance when a protocol method has a `@convention(c)` closure parameter. Since the ABI JSON lacks this info, detection requires cross-referencing with the swiftinterface. Alternatively, skip conformances where the closure stub fails to satisfy the protocol (a compile-time error means the conformance is broken).

A simpler approach: in the `WillSkipConformance` or pre-emission validation, check if any protocol method's closure parameters use conventions other than the default. The swiftinterface text can be searched for `@convention(c)` or `@convention(block)` in the context of the protocol's methods.

### Deliverables

1. **Add missing-requirement detection** to `EveryProtocolEmitter.EmitProtocolConformance()` — compare ABI JSON protocol requirements (`protocolReq: true`) against parsed `ProtocolDecl.Methods`. If any requirement is missing from the parsed methods, skip the conformance.
2. **Add `@convention(c)` detection** — either via swiftinterface cross-reference or by adding a pre-emission validation that detects convention mismatches.
3. Unit tests in `EveryProtocolEmitter` tests — add tests for:
   - Protocol with `some` parameter method → conformance skipped
   - Protocol with `@convention(c)` closure → conformance skipped
   - Protocol where all requirements are present → conformance emitted normally
4. BindingTests: add a protocol with a `some`-parameter method in `BindingTests/Sources/SwiftBindingsTestLib/Protocols/` to test the skip behavior at the integration level.
5. Validate GRDB: `./validate-libraries.sh --filter GRDB`

### Validation

- `./run-tests.sh` — unit tests pass
- `./validate-libraries.sh --filter GRDB` — passes (0 errors)
- `./validate-libraries.sh` — no regressions across all libraries
- `cd BindingTests && ./build-and-test.sh` — new skip-detection tests pass

### Key files to read before starting

- `src/Swift.Bindings/src/Emitter/StringEmitter/EveryProtocolEmitter.cs` (full file, focus on 682-882 for conformance, 1115-1256 for closure stubs)
- `src/Swift.Bindings/src/Parser/GenericSignatureParser.cs` (lines 16-48)
- `src/Swift.Bindings/src/Marshaler/ModuleHandler.cs` (lines 548-626 for selection pipeline)
- `src/Swift.Bindings/src/Configuration/SwiftWrapperPostProcessor.cs` (lines 88-115 for EveryProtocol block stripping)
- GRDB swiftinterface: `.libraries/GRDB/GRDB.xcframework/ios-arm64_x86_64-simulator/GRDB.framework/Modules/GRDB.swiftmodule/arm64-apple-ios-simulator.swiftinterface` (search for `RowAdapter` and `FTS5Tokenizer`)

---

## Session 3: @MainActor Annotation Gaps — COMPLETE (747b522c) in Closure Bridge Emitters

**Goal**: Close the remaining gaps where `@_cdecl` wrappers are emitted without `@MainActor` annotation for actor-isolated types. Add BindingTests coverage for untested actor isolation patterns.

### Context

The @MainActor infrastructure was implemented in a prior session (March 17, 2026) across 15 emission sites. Detection works via swiftinterface parsing (`SwiftInterfaceAccessParser.GetMainActorTypes()`), model flags (`TypeDecl.IsMainActorIsolated`, `MethodDecl.IsMainActorIsolated`), and a centralized decision function (`WrapperValidation.NeedsMainActorAnnotation()`).

However, 2-3 emission sites were missed and still emit `@_cdecl` without the annotation. This causes `call to main actor-isolated method in a synchronous nonisolated context` errors for libraries with @MainActor types.

### Gap 1: `MethodClosureBridge.cs` — Missing @MainActor on @_cdecl

**File**: `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/MethodClosureBridge.cs`, line 296

The MCB emitter emits `@_cdecl` wrappers for methods with bound generic closures. It does NOT call `WrapperEmitterHelpers.EmitCdeclAnnotation()` which handles @MainActor. If a method on a @MainActor type has closures triggering the MCB path, the wrapper lacks the annotation.

**Fix**: Use `WrapperEmitterHelpers.EmitCdeclAnnotation(swiftWriter, silgenName, needsMainActor)` instead of directly writing `@_cdecl`. The `needsMainActor` flag should be derived from `WrapperValidation.NeedsMainActorAnnotation()` on the method/parent type.

### Gap 2: `GenericClosureBridgeEmitter.cs` — Missing @MainActor on @_silgen_name

**File**: `src/Swift.Bindings/src/Emitter/StringEmitter/GenericClosureBridgeEmitter.cs`, line 265

Same pattern — emits `@_silgen_name` without @MainActor for generic closure bridge wrappers.

### Gap 3: `EnumCaseWrapperEmitter.cs` — Missing @MainActor on @_cdecl (low priority)

**File**: `src/Swift.Bindings/src/Emitter/StringEmitter/EnumCaseWrapperEmitter.cs`, line 240

Enums are unlikely to be @MainActor, but for completeness the annotation should be handled.

### Existing Infrastructure (DO NOT reimplement — use these)

| Component | File | Purpose |
|-----------|------|---------|
| Detection | `SwiftInterfaceAccessParser.cs:218` | `GetMainActorTypes()` → `HashSet<string>` |
| Model flags | `TypeDecl.cs:77`, `MethodDecl.cs:146-158` | `IsMainActorIsolated`, `IsNonisolated` |
| Decision | `WrapperValidation.cs:275` | `NeedsMainActorAnnotation()` → bool |
| Emission helper | `WrapperEmitterHelpers.cs:20` | `EmitCdeclAnnotation(writer, symbol, needsMainActor)` |

### BindingTests Gaps to Fill

Existing BindingTests (`Async/MainActor.swift` + `Async/MainActorTests.cs`) cover:
- `@MainActor class MainActorViewModel` — constructor, properties, methods, async
- `struct MainActorMethods` — per-method @MainActor
- `@MainActor func mainActorFreeFunction()`

Missing test patterns:
1. **`nonisolated` member on @MainActor type** — verify the wrapper does NOT get @MainActor
2. **@MainActor type with closure method** — exercises the MCB gap (gap 1)
3. **@MainActor subscript** — untested emission path
4. **Custom actor type** — verify it's properly blocked (negative test: methods are skipped)

### Deliverables

1. Fix `MethodClosureBridge.cs` — use `EmitCdeclAnnotation` with `needsMainActor`
2. Fix `GenericClosureBridgeEmitter.cs` — same pattern
3. Fix `EnumCaseWrapperEmitter.cs` — same pattern (low priority)
4. Unit tests — verify @MainActor annotation appears in MCB/GenericClosure output for @MainActor parent types, and does NOT appear for non-actor types
5. BindingTests Swift source — add `nonisolated` member, closure method on @MainActor type, subscript. Add in existing `Async/MainActor.swift` or a new companion file.
6. BindingTests runtime tests — test the new patterns in `Async/MainActorTests.cs`

### Validation

- `./run-tests.sh` — unit tests pass
- `cd BindingTests && ./build-and-test.sh` — new @MainActor tests pass
- `./validate-libraries.sh` — no regressions (the annotation is compile-time only, no ABI change)

### Key files to read before starting

- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/MethodClosureBridge.cs` (line 296 — @_cdecl emission)
- `src/Swift.Bindings/src/Emitter/StringEmitter/GenericClosureBridgeEmitter.cs` (line 265)
- `src/Swift.Bindings/src/Emitter/StringEmitter/EnumCaseWrapperEmitter.cs` (line 240)
- `src/Swift.Bindings/src/Emitter/StringEmitter/WrapperEmitterHelpers.cs` (line 20 — `EmitCdeclAnnotation`)
- `src/Swift.Bindings/src/Emitter/StringEmitter/WrapperValidation.cs` (line 275 — `NeedsMainActorAnnotation`)
- `BindingTests/Sources/SwiftBindingsTestLib/Async/MainActor.swift`
- `BindingTests/RuntimeTestsApp/Async/MainActorTests.cs`

---

## Session 4: ExistentialContainer2+ Test Coverage + Skip Audit — COMPLETE (5f9e91f5)

**Goal**: Add multi-protocol optional existential tests (ExistentialContainer2) to close a documented test gap, verify existing EC2 non-optional code paths work at runtime, and audit all `[Skip]` annotations for tests that can be unskipped after recent fixes.

### Part A: ExistentialContainer2+ Optional Tests

### Context

The Optional existential getter fix (commit `f9b37256`) uses the projected container type for buffer allocation (`ExistentialContainer1`, `ExistentialContainer2`, etc.). The code path handles multi-protocol compositions via `(innerProjection as ExistentialProjection)?.PInvokeType`, but NO test exercises `ExistentialContainer2+`.

Additionally, `BindingTests/Sources/SwiftBindingsTestLib/Protocols/Composition.swift` already has multi-protocol composition types (`Nameable & Ageable` → EC2) and functions (`processNameableAgeable`), but these have **zero runtime tests** — the Swift source compiles and the generator emits correct bindings, but no C# test calls them.

### How ExistentialContainer Size Is Determined

`ExistentialHandler.GetCSharpExistentialType()` (line 193) counts non-marker protocols:
```csharp
var count = GetNonMarkerProtocols(protocolList).Count;
return $"Swift.Runtime.ExistentialContainer{count}";
```

`GetNonMarkerProtocols()` (line 63) filters out `Sendable`, `Escapable`, `Copyable`, `SendableMetatype` (no witness tables). Container size = `4 + N` words where N = non-marker protocol count.

| Protocols | Container | Size (arm64) |
|-----------|-----------|------|
| `any P` | `ExistentialContainer1` | 40 bytes |
| `any P & Q` | `ExistentialContainer2` | 48 bytes |
| `any P & Q & R` | `ExistentialContainer3` | 56 bytes |

### Deliverables

1. **Add Optional EC2 Swift source** to `BindingTests/Sources/SwiftBindingsTestLib/Protocols/OptionalExistentialProperties.swift`:
   ```swift
   public protocol Taggable {
       func tag() -> String
   }

   public class TaggableRenderableHolder {
       public var item: (any Renderable & Taggable)?
       public init() { self.item = nil }
       public init(item: any Renderable & Taggable) { self.item = item }
       public func getItemDescription() -> String {
           guard let item = item else { return "none" }
           return "\(item.render())+\(item.tag())"
       }
   }

   public struct TaggableRenderable: Renderable, Taggable {
       public let name: String
       public init(name: String) { self.name = name }
       public func render() -> String { return "Render(\(name))" }
       public func tag() -> String { return "Tag(\(name))" }
   }

   public func makeTaggableRenderableHolder(name: String) -> TaggableRenderableHolder {
       return TaggableRenderableHolder(item: TaggableRenderable(name: name))
   }

   public func makeEmptyTaggableRenderableHolder() -> TaggableRenderableHolder {
       return TaggableRenderableHolder()
   }
   ```

2. **Runtime tests** in `OptionalExistentialPropertyTests.cs`:
   - `TaggableRenderableHolder` constructor (nil and non-nil)
   - `getItemDescription()` round-trip
   - Property getter returning `(any Renderable & Taggable)?` → should use EC2 (48 bytes)
   - Property setter (if supported)
   - Set-then-get round-trip, clear-to-null

3. **Non-optional EC2 runtime tests**: Add runtime tests for existing `Composition.swift` patterns (`processNameableAgeable`, etc.) in `BindingTests/RuntimeTestsApp/Protocols/`. These currently have zero runtime coverage.

### Part B: Skip Audit

### Context

Recent commits fixed several issues. Audit all `[Skip]` annotations to find tests that should now pass.

### Skip Audit Findings (Pre-Researched)

**Strong unskip candidates** (7 tests):

| File | Tests | Current Skip Reason | Why Unskippable |
|------|-------|---------------------|-----------------|
| `ProtocolClosureSkipTests.cs` | Lines 94, 103, 113, 123, 134, 153, 172 | "EveryProtocol: EventDelegate witness table stripped by build-bridge.sh strip/retry" | Commit `f9b37256` fixed EveryProtocol closure stubs — witness tables should now survive |

**NOT candidates for unskipping** (all other `[Skip]` tests):

These are blocked by known open bugs or fundamental limitations NOT addressed by recent fixes:
- String callback marshalling (upstream + generator bug)
- String enum raw values (ABI JSON limitation)
- `@convention(c)` struct return (generator bug)
- Variadic methods (not generated)
- Method-level generics (crashes)
- ~Copyable wrapper stripping
- 4-string-param ABI overflow
- `SwiftOptional<Color>` type initializer failure
- CGPoint/CGRect `MarshalToSwift` missing
- Mono JIT async assertion (upstream)
- NativeAOT SIGBUS on async P/Invoke (upstream)
- ObjC selector types
- Cross-module closure wrapper
- weak/unowned references
- Opaque return types

### Deliverables

4. **Try unskipping** the 7 `ProtocolClosureSkipTests.cs` tests — remove `[Skip]`, rebuild bridge, run runtime tests
5. **If any still fail**, investigate whether the bridge strip/retry is still removing the witness table. Check if `build-bridge.sh` needs updating.
6. **Document findings** — update skip reasons for any tests that still can't pass, with the specific current blocker

### Validation

- `cd BindingTests && ./build-and-test.sh` — EC2 tests pass, unskipped tests pass
- `./run-tests.sh` — unit tests pass (if any unit tests were added)

### Key files to read before starting

- `BindingTests/Sources/SwiftBindingsTestLib/Protocols/OptionalExistentialProperties.swift`
- `BindingTests/Sources/SwiftBindingsTestLib/Protocols/Composition.swift`
- `BindingTests/RuntimeTestsApp/Protocols/OptionalExistentialPropertyTests.cs`
- `BindingTests/RuntimeTestsApp/Protocols/ProtocolClosureSkipTests.cs`
- `src/Swift.Bindings/src/Marshaler/ExistentialHandler.cs` (lines 63, 193, 221)
- `src/Swift.Runtime/src/Swift/Runtime/ExistentialContainer.cs`

---

## Session 5: Quick XCTest Dependency + TinyConstraints Architecture Fix — COMPLETE (3052fe09)

**Goal**: Fix 2 infrastructure-level wrapper compilation failures. Quick needs XCTest framework search paths + module collision resolution. TinyConstraints needs arm64-simulator support. Target: 53 -> 55/56.

### Quick — XCTest Dependency (2 issues)

#### Issue 1: Missing XCTest Framework Search Path

Quick's umbrella header includes `<XCTest/XCTest.h>`. XCTest.framework is NOT in the SDK frameworks directory — it lives at the **platform** level:

```
/Applications/Xcode.app/Contents/Developer/Platforms/iPhoneSimulator.platform/Developer/Library/Frameworks/
```

The generator needs to add `-F {platformPath}/Developer/Library/Frameworks` to the swiftc invocation. The platform path is obtainable via `xcrun --show-sdk-platform-path --sdk {sdkName}`.

**Key file**: `src/Swift.Bindings/src/Configuration/SwiftWrapperCompiler.cs`
- `ResolveSdkPath()` at line 931 resolves SDK path. Need a parallel `ResolvePlatformPath()` using `xcrun --show-sdk-platform-path --sdk {sdkName}`.
- `InvokeSwiftCompiler()` at line 1087 constructs the swiftc command. The `additionalFrameworkSearchPaths` parameter is already plumbed through 18+ call sites — just append the platform frameworks path.

#### Issue 2: XCTest Module/Class Name Collision

Quick's swiftinterface references `XCTest.XCTestCase`, `XCTest.XCTestSuite`. The XCTest **module** contains a **class** also named `XCTest` (`@interface XCTest : NSObject`). Swift resolves `XCTest.XCTestCase` as "nested type of class XCTest" instead of "type from module XCTest", causing:

```
error: 'XCTestCase' is not a member type of class 'XCTest.XCTest'
```

This is the **exact same pattern** as the existing EC-1 collision fix. The generator already has `PrecompileCollidingModule()` at line 1205 of `SwiftWrapperCompiler.cs` which:
1. Copies the `.swiftinterface` files
2. Patches module-prefixed type references that collide with class names
3. Pre-compiles to a binary `.swiftmodule`
4. Uses it as a shadow framework via higher-priority `-F` path

**Verified fix**: Patching Quick's swiftinterface with `sed 's/XCTest\.XCTest/XCTest/g'` and running `swift-frontend -compile-module-from-interface` succeeds with zero errors.

The fix should detect XCTest as a transitive dependency (by scanning the swiftinterface for `import XCTest`) and apply the same collision resolution pattern. This should be generator-level auto-detection, not validation-infra-level, because any user's xcframework that depends on XCTest would hit the same issue.

**Note**: After fixing both issues, the wrapper may hit additional errors from `NSInvocation` unavailability — the post-processor should strip those wrappers. Verify the post-processor handles this.

#### Quick Deliverables

1. Add `ResolvePlatformPath()` to `SwiftWrapperCompiler.cs` — parallel to `ResolveSdkPath()`
2. In `InvokeSwiftCompiler()`, detect XCTest import in the swiftinterface and append platform framework search path
3. Extend `PrecompileCollidingModule()` pattern (or add a parallel method) to handle XCTest module/class collision
4. Unit tests for platform path resolution
5. Validate: `./validate-libraries.sh --filter Quick`

### TinyConstraints — x86_64-Only Simulator Slice

#### Root Cause

TinyConstraints' xcframework has a simulator slice with **x86_64 only** (no arm64):

```
ios-x86_64-simulator/  → SupportedArchitectures: ["x86_64"]
ios-arm64/             → SupportedArchitectures: ["arm64"]
```

The source repository has a pre-Apple-Silicon xcconfig:
```
VALID_ARCHS[sdk=iphonesimulator*] = i386 x86_64
```

When `fetch-libraries.sh` builds the xcframework, the archive only contains x86_64.

The generator then targets `arm64-apple-ios-simulator` (hardcoded default in `SliceVariant.cs` line 19), but the `-Swift.h` bridging header only has `#elif defined(__x86_64__)` guards — no `__arm64__` block. Result: `#error unsupported Swift architecture`.

#### Fix (Two Parts)

**Part A: Fix `fetch-libraries.sh`** — add `VALID_ARCHS='$(ARCHS_STANDARD)'` override for libraries with pre-Apple-Silicon xcconfigs. This can be a global default (since `VALID_ARCHS` is deprecated) or per-library in `validation-libraries.json`:

In `scripts/fetch-libraries.sh` lines 162-195 (the `xcodebuild archive` commands), add:
```bash
VALID_ARCHS='$(ARCHS_STANDARD)'
```

Or add a `buildSettings` field to `validation-libraries.json`:
```json
{ "name": "TinyConstraints", "buildSettings": { "VALID_ARCHS": "$(ARCHS_STANDARD)" } }
```

**Part B: Generator defense-in-depth** — use the resolved architecture instead of hardcoded arm64. The `XCFrameworkResolver` already stores `SelectedArchitecture` on the resolution (line 225). Propagate it to `SliceVariant.Architecture` instead of defaulting to `"arm64"`.

Key files:
- `src/Swift.Bindings/src/Configuration/SliceVariant.cs` line 19 — `Architecture = "arm64"` hardcoded
- `src/Swift.Bindings/src/Configuration/XCFrameworkResolver.cs` line 225 — `SelectedArchitecture` already resolved
- `src/Swift.Bindings/src/Configuration/SwiftWrapperCompiler.cs` — `CompileAll()` should propagate resolved architecture

#### TinyConstraints Deliverables

6. Fix `fetch-libraries.sh` — add `VALID_ARCHS` override
7. Fix `SliceVariant.cs` — use resolved architecture from `XCFrameworkResolver`, not hardcoded arm64
8. Re-fetch TinyConstraints: `scripts/fetch-libraries.sh --filter TinyConstraints`
9. Validate: `./validate-libraries.sh --filter TinyConstraints`

### Validation

- `./run-tests.sh` — unit tests pass
- `./validate-libraries.sh --filter Quick` — passes
- `./validate-libraries.sh --filter TinyConstraints` — passes
- `./validate-libraries.sh` — no regressions across all libraries

### Key files to read before starting

- `src/Swift.Bindings/src/Configuration/SwiftWrapperCompiler.cs` (lines 931-941 ResolveSdkPath, 1087-1173 InvokeSwiftCompiler, 1205-1301 PrecompileCollidingModule)
- `src/Swift.Bindings/src/Configuration/SliceVariant.cs` (line 19 — hardcoded arm64)
- `src/Swift.Bindings/src/Configuration/XCFrameworkResolver.cs` (line 188-190 arch selection, line 225 SelectedArchitecture)
- `scripts/fetch-libraries.sh` (lines 162-195 — xcodebuild archive commands)
- `validation-libraries.json` (Quick and TinyConstraints entries)
- Quick swiftinterface: `.libraries/Quick/Quick.xcframework/ios-arm64_x86_64-simulator/Quick.framework/Modules/Quick.swiftmodule/arm64-apple-ios-simulator.swiftinterface`

---

## Notes for Orchestrator

- Sessions 1-3 are independent — no cross-dependencies.
- Session 4 is independent but should ideally run after Session 1 (the MCB struct fix might affect which tests pass/fail).
- Session 5 is fully independent (infrastructure fixes).
- Each session should run the validation gates in its Validation section.
- For sessions that modify emitter code (1, 2, 3), run `./validate-libraries.sh` to verify no regressions across all 56 targets.
- Session 5 modifies `fetch-libraries.sh` — the worker must re-fetch the affected library before validating.
- The target post-session state is **55/56** (StripePaymentSheet remains blocked by inter-module `StripePayments` dependency).
