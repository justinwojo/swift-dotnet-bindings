# BindingTests Hardening & Async Fix Sessions

**Created**: March 27, 2026
**Goal**: Harden BindingTests to be the primary confidence gate, reducing reliance on external validation libraries. Then fix one roadmap bug.

**Sessions**: 5 sequential sessions, executed via session orchestrator.

---

## Session 1: Identifier Sanitization & Collision Hardening ✅ `1ba8a83b`

**Goal**: Reproduce real validation library failures (Valet, BonMot, SVGView, ObjectMapper/Parchment) inside BindingTests. Ensure these patterns are caught by unit tests AND runtime tests.

### Context

The generator already handles many collision/identifier cases, but BindingTests coverage is thin:
- **Case-insensitive enum collisions**: Handled by `NameProvider.ComputeCaseNameMap()` (lines 830-878). BindingTests has `CaseCollisions.swift` with `DrawCommand` and `CSSProperty` — but runtime tests (`CollisionTests.cs`) only cover Move/Move2. Line/Line2, Close, and the entire CSSProperty enum have zero runtime tests.
- **Emoji identifiers**: `NameProvider.SanitizeIdentifierChars()` (lines 152-183) replaces emoji with underscores. Unit tests exist in `NameProviderSanitizationTests.cs` but NO BindingTests Swift source exercises emoji in enum cases or type names.
- **Default parameter overload collisions**: `DefaultParameterOverloadEmitter.cs` generates trimmed overloads. Dedup uses `GetProjectedCSharpMethodKey()` in `IHandler.cs:403-473`. `Parameters/Defaults.swift` has default params but no colliding explicit overload. No BindingTests pattern tests collision between an explicit overload and a default-parameter-trimmed overload.
- **Backtick identifiers**: `Keywords.swift` exists with `KeywordTest` struct and `processKeywords()` function. But the only runtime test (`TestKeywordTestCreation`) is `[Skip]`'d due to the 4-string-param ABI overflow bug. `processKeywords()` has no runtime test at all. Need a simpler backtick test that doesn't hit the ABI limit.

### Deliverables

#### 1. New Swift source: `BindingTests/Sources/SwiftBindingsTestLib/Collisions/EmojiIdentifiers.swift`

Add enum with emoji case names (reproduces Valet's 24 errors):
```swift
// Emoji in enum case names — generator must sanitize to valid C# identifiers
public enum ValidationStatus: Int32 {
    case success = 0
    case error🚫 = 1       // emoji in case name
    case warning⚠️ = 2     // emoji in case name
    case pending⏳ = 3      // emoji in case name
}

public func describeValidationStatus(_ status: ValidationStatus) -> String {
    // return description based on case
}
```

Also add a struct/class with emoji in a method or property name if feasible (check that swiftc allows it with `BUILD_LIBRARY_FOR_DISTRIBUTION=YES`). If emoji in identifiers doesn't survive ABI JSON, document that finding and skip.

#### 2. New Swift source: `BindingTests/Sources/SwiftBindingsTestLib/Collisions/DefaultParamCollisions.swift`

Add a class with overloads that collide after default parameter trimming:
```swift
// Pattern from ObjectMapper/Parchment: explicit overload collides with
// default-param-trimmed overload
public class SearchService {
    // This has a default param — generator may emit a 1-param overload
    public func find(query: String, limit: Int32 = 10) -> String {
        return "find(\(query), limit=\(limit))"
    }

    // This explicit 1-param overload would collide with the trimmed version above
    public func find(query: String) -> String {
        return "find(\(query))"
    }
}
```

The generator should detect the collision and avoid emitting the trimmed overload (or use a different dedup strategy). Test should verify BOTH methods are callable at runtime without CS0111.

#### 3. Expand `CaseCollisions.swift` runtime coverage

Add runtime tests in `CollisionTests.cs` for:
- `DrawCommand.Line` and `DrawCommand.Line2` (currently untested)
- `DrawCommand.Close` value check
- `CSSProperty` enum — if string enum raw values work, test all cases. If blocked by the raw value bug, add a `[Skip]` with reason.
- `DescribeCSSProperty()` round-trip

#### 4. Add simpler keyword/backtick test

The existing `KeywordTest` struct has 4 string params and hits the GPR overflow bug. Add a simpler fixture in `Keywords.swift`:
```swift
// Simpler backtick test that avoids 4-string-param ABI limit
public func getKeywordValue(`for` key: String) -> String {
    return "value-for-\(key)"
}

public func processKeywordParam(`class` name: String, count: Int32) -> String {
    return "\(name):\(count)"
}
```

Add runtime tests for these in `EdgeCaseTests.cs`.

#### 5. New unit tests

- `NameProviderSanitizationTests.cs`: Add test for emoji that produces collision (two different emoji both becoming `__` → need dedup)
- `DefaultParameterOverloadEmitterTests.cs`: Add test for collision detection when explicit overload matches trimmed signature
- `BaseHandlerDedupTests.cs`: Add test that verifies dedup key for a method with defaults matches the explicit overload key

#### 6. Runtime tests for emoji identifiers

In `CollisionTests.cs`, add tests for `ValidationStatus` enum and `describeValidationStatus()`. Verify that sanitized C# names are accessible and round-trip correctly.

### Validation

- `./run-tests.sh` — unit tests pass
- `cd BindingTests && ./build-and-test.sh` — new Swift sources compile, bindings generate, runtime tests pass

### Key files to read before starting

- `src/Swift.Bindings/src/Marshaler/NameProvider.cs` (lines 120-183 sanitization, 830-878 case collision)
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/DefaultParameterOverloadEmitter.cs`
- `src/Swift.Bindings/src/Marshaler/IHandler.cs` (lines 403-473 dedup key)
- `BindingTests/Sources/SwiftBindingsTestLib/Collisions/CaseCollisions.swift`
- `BindingTests/Sources/SwiftBindingsTestLib/EdgeCases/Keywords.swift`
- `BindingTests/RuntimeTestsApp/Collisions/CollisionTests.cs`
- `BindingTests/RuntimeTestsApp/EdgeCases/EdgeCaseTests.cs`
- `src/Swift.Bindings/tests/UnitTests/MarshalerTests/NameProviderSanitizationTests.cs`
- `src/Swift.Bindings/tests/UnitTests/EmitterTests/DefaultParameterOverloadEmitterTests.cs`

---

## Session 2: Closure Edge Case Coverage ✅ `bf3b3ba0`

**Goal**: Add BindingTests coverage for closure patterns that are the #1 failure category across validation libraries. Both supported patterns (testing runtime correctness) and unsupported patterns (testing graceful skip behavior).

### Context

Closures cause ~50+ errors across Nuke, GRDB, Kingfisher, Mappedin, Stripe libraries. The generator has detailed support detection in `ClosureHandler.cs`:
- `IsSupportedClosure()` (lines 203-259) — main gate
- `IsSupportedClosureParameterType()` (lines 453-525) — param type checks
- `IsSupportedClosureReturnType()` (lines 345-427) — return type checks

Current BindingTests closures (`Closures/Escaping.swift`) cover: basic callbacks, multi-arg, optional closure params, optional primitive/enum closure params, struct params, existential array params. `ClosureReturns.swift` covers functions-returning-closures (makeAdder, makeMultiplier). `AsyncCallbackClosures.swift` has `Result<T,Error>` completion handlers. But missing: throwing closures (explicitly removed from Escaping.swift — "Known generator limitation"), closure ACCEPTING non-frozen struct/class/enum as RETURN type, simple completion handler → Task conversion (the `(T) -> Void` pattern that `CompletionHandlerDetector` converts), nested closures.

### Deliverables

#### 1. New Swift source: `BindingTests/Sources/SwiftBindingsTestLib/Closures/ThrowingClosures.swift`

```swift
// Throwing closure patterns — GRDB, Stripe hit these
public enum ClosureError: Error {
    case invalid
    case timeout
}

// Simple throwing closure (should be supported)
public func callThrowingClosure(_ callback: @escaping () throws -> Int32) -> Int32 {
    do {
        return try callback()
    } catch {
        return -1
    }
}

// Throwing closure with parameters
public func callThrowingWithParam(_ callback: @escaping (Int32) throws -> String) -> String {
    do {
        return try callback(42)
    } catch {
        return "error"
    }
}
```

Check `ClosureEmitter.Throwing.cs` for current support. If throwing closures emit correctly, add runtime tests. If they're blocked (the previous comment in `Escaping.swift` says "SwiftString→void* return mismatch in thunks"), add as compiled-out with explanation.

#### 2. New Swift source: `BindingTests/Sources/SwiftBindingsTestLib/Closures/CompletionHandlers.swift`

```swift
// Completion handler → Task conversion patterns (Stripe, Alamofire)
public class AsyncService {
    // Standard completion handler (void result)
    public func fetchData(completion: @escaping () -> Void) {
        completion()
    }

    // Completion handler with result
    public func fetchValue(completion: @escaping (Int32) -> Void) {
        completion(42)
    }

    // Completion handler with error
    public func fetchWithError(completion: @escaping (Int32, Bool) -> Void) {
        completion(100, true)
    }
}
```

The `CompletionHandlerDetector.cs` (lines 1-256) converts qualifying completion handlers to `Task`-returning overloads. Test that both the callback version AND the Task version work at runtime.

#### 3. New Swift source: `BindingTests/Sources/SwiftBindingsTestLib/Closures/ClosureReturnTypes.swift`

Test closure return type edge cases:
```swift
// Closure returning non-frozen struct (indirect return path)
public func callWithNonFrozenReturn(_ callback: @escaping () -> NonFrozenPoint) -> NonFrozenPoint {
    return callback()
}

// Closure returning class (reference return)
public func callWithClassReturn(_ callback: @escaping () -> SimpleClass) -> SimpleClass {
    return callback()
}

// Closure returning enum
public func callWithEnumReturn(_ callback: @escaping () -> Color) -> Color {
    return callback()
}
```

These test the `ClosureEmitter.IndirectReturn.cs` paths vs direct return paths.

#### 4. Runtime tests: `BindingTests/RuntimeTestsApp/Closures/ClosureEdgeCaseTests.cs`

Add runtime tests for all new Swift sources. For each pattern:
- If supported: assert correct round-trip values
- If unsupported: verify the method is NOT emitted (no compile error = success)

#### 5. Unit tests for skip detection

In `src/Swift.Bindings/tests/UnitTests/MarshalerTests/ClosureHandlerTests.cs`, add test cases for:
- Nested closure detection (`IsSupportedClosureParameterType` returns false for closures-in-closures)
- Bare generic type in closure params (e.g., `T` without bound)
- Async+throwing closure with parameters (B13 skip)

### Validation

- `./run-tests.sh` — unit tests pass
- `cd BindingTests && ./build-and-test.sh` — new sources compile, bindings generate, runtime tests pass

### Key files to read before starting

- `src/Swift.Bindings/src/Marshaler/ClosureHandler.cs` (full file — support detection logic)
- `src/Swift.Bindings/src/Emitter/StringEmitter/ClosureEmitter.Throwing.cs`
- `src/Swift.Bindings/src/Emitter/StringEmitter/ClosureEmitter.Async.cs`
- `src/Swift.Bindings/src/Emitter/StringEmitter/ClosureEmitter.IndirectReturn.cs`
- `src/Swift.Bindings/src/Emitter/StringEmitter/CompletionHandlerDetector.cs`
- `BindingTests/Sources/SwiftBindingsTestLib/Closures/Escaping.swift`
- `BindingTests/Sources/SwiftBindingsTestLib/Closures/ClosureReturns.swift`
- `BindingTests/RuntimeTestsApp/Closures/ClosureTests.cs`
- Existing closure unit tests: `ClosureHandlerTests.cs`, `ClosureEmitterDirectTests.cs`, `CompletionHandlerDetectorTests.cs`

---

## Session 3: Protocol, Existential & Generic Edge Cases ✅ `8751cb25`

**Goal**: Cover optional existential property accessors, protocol proxy closure skipping, and generic type edge cases — patterns responsible for ~70+ errors across validation libraries.

### Context

- **Optional existential property accessors**: `ExistentialHandler.cs` (lines 269-417) handles `Optional<any Protocol>` detection and marshalling. The `OptionalMarshalStrategy.cs` classifies strategies. Existing BindingTests have non-optional existential params (`ExistentialBoxing.swift`: `any ProcessingMode`) and non-optional existential returns (`ExistentialReturns.swift`: `any ERTestProtocol`), but ZERO optional existential patterns anywhere — grep for `any.*?` in Protocols/ returns no matches.
- **Protocol proxy closure skipping**: `ProtocolProxyEmitter.cs` (lines 25-54) skips protocol methods containing unsupported closures. This affects Starscream, RxSwift, StripeUICore. No BindingTests protocol has a mix of closure and non-closure methods to validate this skip behavior.
- **Generic bound type propagation**: `BoundGenericsHandler.cs` handles resolution. Type args can silently degrade to `AnyType` for unresolved/existential args. Existing `Generics/Types.swift` has basic generics but no multi-type-argument bound generic with concrete types exercised at runtime.

### Deliverables

#### 1. New Swift source: `BindingTests/Sources/SwiftBindingsTestLib/Protocols/OptionalExistentialProperties.swift`

```swift
// Optional existential property patterns (11+ validation libraries hit this)
public protocol Describable {
    func describe() -> String
}

public class DescribableHolder {
    public var primary: (any Describable)?

    public init() {
        self.primary = nil
    }

    public init(primary: any Describable) {
        self.primary = primary
    }

    public func getPrimaryDescription() -> String {
        return primary?.describe() ?? "none"
    }
}

// Concrete conformer for testing
public struct SimpleDescribable: Describable {
    public let name: String
    public init(name: String) { self.name = name }
    public func describe() -> String { return "SimpleDescribable(\(name))" }
}

public func makeDescribableHolder(name: String) -> DescribableHolder {
    return DescribableHolder(primary: SimpleDescribable(name: name))
}

public func makeEmptyDescribableHolder() -> DescribableHolder {
    return DescribableHolder()
}
```

#### 2. New Swift source: `BindingTests/Sources/SwiftBindingsTestLib/Protocols/ProtocolClosureSkipping.swift`

```swift
// Protocol with closure method — generator should skip the closure method
// but still emit non-closure methods (interface coherence)
public protocol EventDelegate {
    func didReceiveEvent(name: String) -> Bool       // should be emitted
    func onComplete(handler: @escaping () -> Void)   // should be SKIPPED (closure in protocol)
    var delegateName: String { get }                  // should be emitted
}

public class EventRouter {
    public var delegate: (any EventDelegate)?

    public init() {
        self.delegate = nil
    }

    public func routeEvent(name: String) -> Bool {
        return delegate?.didReceiveEvent(name: name) ?? false
    }
}
```

#### 3. Expand generics tests: `BindingTests/Sources/SwiftBindingsTestLib/Generics/BoundGenericEdgeCases.swift`

```swift
// Bound generic with multiple type arguments — both resolved
public struct Pair<A, B> {
    public let first: A
    public let second: B
    public init(first: A, second: B) {
        self.first = first
        self.second = second
    }
}

// Functions that use Pair with concrete types
public func makeIntStringPair(_ a: Int32, _ b: String) -> Pair<Int32, String> {
    return Pair(first: a, second: b)
}

// Bound generic in return type
public func makePairDescription<A, B>(_ pair: Pair<A, B>) -> String {
    return "Pair(\(pair.first), \(pair.second))"
}
```

Note: The `makePairDescription` is a method-level generic and may not be supported. Include it to verify the skip behavior. The `makeIntStringPair` with concrete types should work.

#### 4. Runtime tests

- `BindingTests/RuntimeTestsApp/Protocols/OptionalExistentialPropertyTests.cs` — test optional existential getter (null and non-null), setter if supported, `getPrimaryDescription()` round-trip
- Add protocol closure skip assertions to existing `BindingTests/RuntimeTestsApp/Protocols/` — verify `EventDelegate` interface exists in C# but doesn't have `OnComplete` method (compile-time check via presence of other methods)
- `BindingTests/RuntimeTestsApp/Generics/BoundGenericEdgeCaseTests.cs` — test `MakeIntStringPair` round-trip

#### 5. Unit tests

- `ExistentialOptionalGuardTests.cs`: Add test cases for optional existential in property position (vs method param/return)
- `ProtocolProxyEmitterTests.cs`: Add test that closure-containing method is skipped but non-closure methods are kept
- `BoundGenericsHandlerTests.cs`: Add test for multi-type-arg bound generic resolution

### Validation

- `./run-tests.sh` — unit tests pass
- `cd BindingTests && ./build-and-test.sh` — full pipeline

### Key files to read before starting

- `src/Swift.Bindings/src/Marshaler/ExistentialHandler.cs` (lines 269-417)
- `src/Swift.Bindings/src/Emitter/StringEmitter/OptionalMarshalStrategy.cs`
- `src/Swift.Bindings/src/Emitter/StringEmitter/ProtocolProxyEmitter.cs` (lines 25-54 skip tracking)
- `src/Swift.Bindings/src/Marshaler/BoundGenericsHandler.cs`
- `BindingTests/Sources/SwiftBindingsTestLib/Protocols/` (all files)
- `BindingTests/Sources/SwiftBindingsTestLib/Generics/` (all files)
- `BindingTests/RuntimeTestsApp/Protocols/` (all files)
- `BindingTests/RuntimeTestsApp/Generics/` (all files)
- Existing unit tests: `ExistentialOptionalGuardTests.cs`, `ProtocolProxyEmitterTests.cs`, `BoundGenericsHandlerTests.cs`

---

## Session 4: Apple Framework Types & Optional Value-Type Coverage ✅ `f9a5e850`

**Goal**: Expand BindingTests coverage of Apple framework type mappings and value-type optional handling — patterns that affect PhoneNumberKit, AMPopTip, SwipeCellKit, and others.

### Context

- **Apple framework type database**: 21 XML databases in `src/Swift.Bindings.Sdk/tools/net10.0/any/Swift/`. Types are classified as value types (frozen structs, enums) or reference types (ObjC-bridged classes). The `AppleFrameworkRegistry.cs` handles module classification, type remapping, and ObjC prefix detection.
- **Value-type optional distinction**: `WrapperValidation.cs` (lines 390-440) has `IsOptionalWithReferenceInner()` with a two-path detection (TypeRecord check + unresolved Apple fallback). Wrong classification silently generates bad marshalling.
- **Existing CoreGraphics Swift source**: `Types/CoreGraphicsTypes.swift` has `LayoutConfig` struct (CGPoint/CGSize/CGRect/CGFloat fields) and free functions (createPoint, createRect, etc.) — but has ZERO runtime tests (grep for these symbols in RuntimeTestsApp/ returns nothing). Optional CG types only appear in `Patterns/HierarchyInspection.swift` (CGPoint?, CGRect?) which also has no runtime test for the optional paths.
- **Existing optional tests**: `Optionals/OptionalTypes.swift` covers optional Int32, optional String, optional class, but NOT optional frozen structs, optional enums with nil paths, or optional non-frozen structs.

### Deliverables

#### 1. Add runtime tests for existing `CoreGraphicsTypes.swift` + expand with optional CG types

`Types/CoreGraphicsTypes.swift` already has `LayoutConfig`, `createPoint()`, `createSize()`, `createRect()`, `describeRect()`, `rectArea()` — but ZERO runtime tests exist. First priority: write runtime tests for these existing functions.

Then expand the Swift source with optional CG type patterns:
```swift
// Optional value types from Apple frameworks
public func processOptionalPoint(_ point: CGPoint?) -> String {
    guard let p = point else { return "nil" }
    return "(\(p.x), \(p.y))"
}

public func processOptionalRect(_ rect: CGRect?) -> String {
    guard let r = rect else { return "nil" }
    return "\(r.origin.x),\(r.origin.y) \(r.size.width)x\(r.size.height)"
}

// CGFloat parameter (tests Double mapping)
public func scaleCGFloat(_ value: CGFloat, by factor: CGFloat) -> CGFloat {
    return value * factor
}
```

#### 2. New Swift source: `BindingTests/Sources/SwiftBindingsTestLib/Types/AppleFrameworkTypes.swift`

Test patterns from libraries that use Apple framework types as parameters/returns:
```swift
import Foundation

// Foundation.Data round-trip
public func processDataLength(_ data: Data) -> Int32 {
    return Int32(data.count)
}

// Optional Foundation types
public func processOptionalData(_ data: Data?) -> Int32 {
    return Int32(data?.count ?? -1)
}

// Date operations (already in Foundation/Date.swift, but verify optional path)
public func processOptionalDate(_ date: Date?) -> String {
    guard let d = date else { return "nil" }
    return "\(d.timeIntervalSince1970)"
}
```

**Important**: Check what `Foundation/Date.swift`, `Foundation/URL.swift.disabled`, and `Foundation/Data.swift.disabled` already cover. The `.disabled` files were disabled for a reason — read them to understand why. Don't duplicate existing coverage. Focus on OPTIONAL paths and types not yet covered.

#### 3. New Swift source: `BindingTests/Sources/SwiftBindingsTestLib/Types/OptionalValueTypes.swift`

Cover the value-type vs reference-type optional distinction:
```swift
// Frozen struct optional (value type → Nullable<T> or SwiftOptional<T>)
public func acceptOptionalFrozenPoint(_ point: FrozenPoint?) -> String {
    guard let p = point else { return "nil" }
    return "(\(p.x), \(p.y))"
}

// Non-frozen struct optional (reference type → SwiftOptional<T> with decomposed buffers)
public func acceptOptionalNonFrozenPoint(_ point: NonFrozenPoint?) -> String {
    guard let p = point else { return "nil" }
    return "(\(p.x), \(p.y))"
}

// Enum optional (value type)
public func acceptOptionalColor(_ color: Color?) -> String {
    guard let c = color else { return "nil" }
    switch c {
    case .red: return "red"
    case .green: return "green"
    case .blue: return "blue"
    @unknown default: return "unknown"
    }
}

// Bool optional
public func acceptOptionalBool(_ flag: Bool?) -> String {
    guard let f = flag else { return "nil" }
    return f ? "true" : "false"
}
```

#### 4. Runtime tests

- `BindingTests/RuntimeTestsApp/Marshalling/OptionalValueTypeTests.cs` — test each optional function with both `Some` and `None` values
- Expand `BindingTests/RuntimeTestsApp/Marshalling/` with CoreGraphics/Apple framework type tests
- Test both the value path (non-null) and null path for each optional variant

#### 5. Unit tests

- `OptionalMarshalStrategyTests.cs`: Add test cases verifying correct strategy classification for:
  - Frozen struct → expected strategy
  - Non-frozen struct → expected strategy
  - Simple enum → expected strategy
  - ObjC-bridged class → NullablePointer
  - CGPoint (frozen value) → correct strategy
- `AppleFrameworkRegistryTests.cs`: Add tests for module classification edge cases if gaps found

### Validation

- `./run-tests.sh` — unit tests pass
- `cd BindingTests && ./build-and-test.sh` — full pipeline

### Key files to read before starting

- `src/Swift.Bindings/src/Emitter/StringEmitter/WrapperValidation.cs` (lines 390-440)
- `src/Swift.Bindings/src/Emitter/StringEmitter/OptionalMarshalStrategy.cs`
- `src/Swift.Bindings/src/TypeDatabase/AppleFrameworkRegistry.cs`
- `src/Swift.Bindings.Sdk/tools/net10.0/any/Swift/CoreGraphicsDatabase.xml`
- `src/Swift.Bindings.Sdk/tools/net10.0/any/Swift/UIKitDatabase.xml`
- `src/Swift.Bindings.Sdk/tools/net10.0/any/Swift/FoundationDatabase.xml`
- `BindingTests/Sources/SwiftBindingsTestLib/Types/CoreGraphicsTypes.swift`
- `BindingTests/Sources/SwiftBindingsTestLib/Foundation/` (all files, including .disabled)
- `BindingTests/Sources/SwiftBindingsTestLib/Optionals/OptionalTypes.swift`
- `BindingTests/RuntimeTestsApp/Marshalling/` (all files)
- Existing unit tests: `OptionalMarshalStrategyTests.cs`, `AppleFrameworkRegistryTests.cs`

---

## Session 5: Fix Async Frozen Struct Parameter Bug ✅ `1f7ae284`

**Goal**: Fix the `stackalloc` not safe after `await` bug for frozen struct parameters in async methods. This is a roadmap item with a clear fix path.

### Context

When an async method has a frozen blittable struct parameter, the generator uses `stackalloc` for marshalling (in `WrapperEmitter.Marshalling.cs` lines 595-601). This is unsafe because the stack buffer is used across an `await` boundary — by the time the async callback fires, the stack frame is gone.

Non-frozen structs already use heap allocation via `NativeMemory.Alloc` in `WrapperEmitter.Async.cs` (lines 71-166). The fix is to route frozen blittable struct params through the same heap allocation path when the method is async.

### Root Cause

In `WrapperEmitter.Async.cs` line 57, the filter that determines which params need async copy-buffer treatment:
```csharp
return !MarshallingHelpers.IsTypeFrozen(typeRecord) || typeRecord.Kind == TypeRecordKind.Enum;
```

This specifically EXCLUDES frozen structs (unless they're enums). So frozen struct params fall through to the `stackalloc` path in `EmitCdeclFrozenStructMarshalling()`.

### Deliverables

#### 1. Fix the async parameter filter

In `WrapperEmitter.Async.cs`, modify the filter to include frozen blittable structs when the method is async. The filter needs to identify frozen structs that use `stackalloc` (i.e., blittable frozen structs without `RequiresMemoryManagement`):

```csharp
// Include frozen blittable structs — they use stackalloc which is unsafe across await
bool isBlittableFrozenStruct = MarshallingHelpers.IsTypeFrozen(typeRecord)
    && !MarshallingHelpers.RequiresMemoryManagement(typeRecord)
    && typeRecord.Kind != TypeRecordKind.Enum; // enums already handled
```

#### 2. Modify `EmitCdeclFrozenStructMarshalling` for async context

In `WrapperEmitter.Marshalling.cs`, add an async-aware path that uses heap allocation instead of `stackalloc` when the enclosing method is async:

```csharp
if (isAsync && !MarshallingHelpers.RequiresMemoryManagement(typeRecord))
{
    // Async: heap allocate (stackalloc unsafe across await)
    // Use NativeMemory.Alloc + explicit free in callback cleanup
}
else
{
    // Sync: stackalloc is safe
    // (existing code)
}
```

**Critical**: The heap-allocated buffer must be freed in the async callback's cleanup/finally block. Study how non-frozen structs handle this in `WrapperEmitter.Async.cs` lines 103-166 and replicate the pattern.

#### 3. Add Swift test source: `BindingTests/Sources/SwiftBindingsTestLib/Async/AsyncFrozenStructParams.swift`

```swift
// Async methods with frozen struct parameters (triggers the stackalloc bug)
public func asyncProcessFrozenPoint(_ point: FrozenPoint) async -> String {
    return "(\(point.x), \(point.y))"
}

public func asyncScaleFrozenPoint(_ point: FrozenPoint, by factor: Double) async -> FrozenPoint {
    return FrozenPoint(x: point.x * factor, y: point.y * factor)
}

// Multiple frozen struct params in async
public func asyncCombineFrozenPoints(_ a: FrozenPoint, _ b: FrozenPoint) async -> FrozenPoint {
    return FrozenPoint(x: a.x + b.x, y: a.y + b.y)
}

// Async throwing with frozen struct param
public func asyncValidateFrozenPoint(_ point: FrozenPoint) async throws -> Bool {
    return point.x >= 0 && point.y >= 0
}
```

#### 4. Runtime tests: `BindingTests/RuntimeTestsApp/Async/AsyncFrozenStructParamTests.cs`

Test each async method with frozen struct params. These tests would CRASH before the fix (stack buffer invalid across await). After the fix they should pass.

#### 5. Unit tests

- `AsyncSwiftWrapperTests.cs`: Add test verifying that frozen blittable struct params in async methods use `NativeMemory.Alloc` instead of `stackalloc` in the generated output. Assert the generated code contains `NativeMemory.Alloc` (not `stackalloc`) when the method is async.

#### 6. Verify no regression for sync paths

The `stackalloc` path must remain for sync methods (it's faster). Run existing tests to verify sync frozen struct params still work correctly.

### Validation

- `./run-tests.sh` — unit tests pass (including new async tests)
- `./validate-libraries.sh` — no regressions (frozen struct sync paths still work)
- `cd BindingTests && ./build-and-test.sh` — new async frozen struct runtime tests pass

### Key files to read before starting

- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/WrapperEmitter.Async.cs` (full file)
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/WrapperEmitter.Marshalling.cs` (lines 553-603)
- `src/Swift.Bindings/src/Marshaler/MarshallingHelpers.cs` (IsTypeFrozen, RequiresMemoryManagement)
- `src/Swift.Bindings/tests/UnitTests/EmitterTests/AsyncSwiftWrapperTests.cs`
- `BindingTests/Sources/SwiftBindingsTestLib/Async/Methods.swift` (existing async patterns)
- `BindingTests/Sources/SwiftBindingsTestLib/Async/AsyncComplexTypes.swift`
- `BindingTests/RuntimeTestsApp/Async/` (existing async runtime tests)

---

## Session 7: Fix EveryProtocol Closure Stub & Optional Existential Getter

**Goal**: Fix two generator bugs discovered during Session 6 review gap analysis. Both cause runtime test skips that should be passing.

### Bug A: EveryProtocol conformance stripped for protocols with closure methods

**Symptom**: `EventDelegateProxy(IEventDelegate)` fails because `Get_EveryProtocol_EventDelegate_WitnessTable` doesn't exist in the binary. All C#-to-Swift proxy wrapping for `EventDelegate` is broken.

**Root cause**: The EveryProtocol conformance emitter generates a full implementation of every protocol method, including closure methods that can't be marshalled. When the Swift wrapper tries to compile `extension EveryProtocol: EventDelegate`, the closure method `onComplete(handler:)` fails compilation, and the entire conformance gets stripped from the binary.

**Fix**: In the EveryProtocol conformance emitter, stub out closure-skipped methods with `fatalError("Not implemented")` instead of omitting them. This lets the conformance compile (the vtable slot is populated) while ensuring any accidental call hits a clear crash rather than undefined behavior. The C# proxy already throws `NotSupportedException` before reaching Swift, so the `fatalError` is a safety net only.

**Key files**:
- `src/Swift.Bindings/src/Emitter/StringEmitter/ProtocolProxyEmitter.cs` — skip tracking (lines 25-54)
- `src/Swift.Bindings/src/Emitter/StringEmitter/ProtocolProxyEmitter.StaticInit.cs` — vtable init (lines 115, 184)
- `src/Swift.Bindings/src/Emitter/StringEmitter/EveryProtocolEmitter.cs` — conformance emission
- `BindingTests/RuntimeTestsApp/Protocols/ProtocolClosureSkipTests.cs` — 7 skipped tests to unskip

**Validation**: Unskip the 7 `ProxySkipReason` tests in `ProtocolClosureSkipTests.cs`. They should pass after the fix (C# `TestEventDelegate` → proxy → Swift `EventRouter`).

### Bug B: Optional existential property getter returns non-blittable struct as IntPtr

**Symptom**: Reading `RenderableHolder.Primary` crashes on Mono. The P/Invoke returns `IntPtr` but the Swift property getter returns `Optional<ExistentialContainer1>` (40 bytes on arm64). Non-blittable return via `CallConvSwift`.

**Root cause**: The property getter P/Invoke is emitted with `CallConvSwift` directly targeting the mangled Swift getter symbol, but `Optional<ExistentialContainer1>` is too large for register return. The getter needs a `@_cdecl` wrapper (like the setter already has) that handles the indirect return convention.

**Fix**: In the property handler or wrapper emitter, detect optional existential property getters and route them through a `@_cdecl` wrapper with explicit out-parameter marshalling, matching the pattern already used for the setter (`_optbuf` suffix).

**Key files**:
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/PropertyHandler.cs` — property emission
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/WrapperEmitter.Marshalling.cs` — marshalling paths
- `src/Swift.Bindings/src/Marshaler/ExistentialHandler.cs` (lines 269-417) — optional existential detection
- `BindingTests/RuntimeTestsApp/Protocols/OptionalExistentialPropertyTests.cs` — 5 skipped getter tests to unskip

**Validation**: Unskip the 5 `GetterSkipReason` tests in `OptionalExistentialPropertyTests.cs`. They should pass after the fix (read `holder.Primary`, verify non-null `IRenderable`, call `Render()`).

### Validation gates

- `./run-tests.sh` — unit tests pass
- `./validate-libraries.sh` — no regressions (property/protocol emission changes are high-risk)
- `cd BindingTests && ./build-and-test.sh` — unskipped tests pass

---

## Notes for Orchestrator

- Sessions 1-4 are independent in principle, but running them sequentially avoids merge conflicts in shared files (coverage-matrix.json, test infrastructure).
- Session 5 depends on sessions 1-4 only insofar as the test infrastructure is stable.
- Each session should run the validation gates specified in its Validation section — these are deliberately scoped per CLAUDE.md's mid-session guidance.
- Swift sources must compile with `BUILD_LIBRARY_FOR_DISTRIBUTION=YES` (the xcframework build flag). If a Swift pattern doesn't survive this, document the finding and skip it.
- When adding new Swift source files, they must also be added to the Package.swift if it has explicit file lists (check first).
- Runtime test classes auto-discover via reflection — just extend `TestBase` and put in the right namespace.
