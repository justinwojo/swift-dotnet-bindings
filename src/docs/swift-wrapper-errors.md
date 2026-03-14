# Swift Wrapper Compilation Errors

Comprehensive analysis of Swift wrapper compilation failures caught by `validate-libraries.sh`. All 90 targets pass C# compilation; these are errors in the generated `.swift` wrapper that gets compiled into a `.framework` binary.

**Baseline**: 37/56 passing (34 ObjC/no wrapper), 19 failing.

**Previous baselines**: 29/56 (session 6), 28/56 (session 5+RxSwift), 27/56 (session 5), 24/56 (session 4), 21/56 (session 3), 16/56 (`6bf59eab`).

## Session History

**Session 7** (8 libraries fixed: Reachability, KeychainSwift, AnimatedCollectionViewLayout, Valet, SVGView, Mixpanel, NVActivityIndicatorView, SwiftyBeaver; major error reduction in SkeletonView, FSPagerView):
- EC-1: `.swiftinterface` pre-compilation for module/type name collisions. Creates a shadow framework with patched+pre-compiled binary `.swiftmodule` that overrides the textual `.swiftinterface` via `-F` precedence. Resolves 6 libraries directly (Reachability, KeychainSwift, AnimatedCollectionViewLayout, Valet, SVGView, Mixpanel) and unblocks 3 more with residual fixes.
- EC-18: Nested type disambiguation. Collision regex now preserves `Module.NestedType` references when `NestedType` is nested inside the colliding class (SwiftyBeaver.Level stays qualified).
- EC-2: Internal type member gate. `MemberGateEvaluator.EvaluateHardGates/EvaluatePropertyHardGates` now resolves parameter/return types via TypeDatabase; methods referencing module-internal types are skipped. SkeletonView 532→2 errors. SwiftyBeaver+NVActivityIndicatorView internal types eliminated.
- EC-3: EveryProtocol class-bound + CaseIterable gates. Protocols inheriting NSObjectProtocol/AnyObject or CaseIterable are now skipped. Fixes StripeIssuing, NVActivityIndicatorView, FSPagerView, Parchment partial.
- EC-4: `Unmanaged<ValueType>` fix. `IsOptionalWithReferenceInner` now checks `TypeRecordKind` — ObjC-bridged/rooted structs (UIFont.Weight, PHPickerResult) return false. Fixes StripeConnect (dep gate).


**Session 6** (1 library fixed: XMLCoder; error reduction in CryptoSwift, SkeletonView):
- Public member names negative-space detection from `.swiftinterface`: parses all `public`/`open` members from the public interface; any ABI member NOT in the set is marked internal. Handles `static`, `class`, setter-visibility (`internal(set)`, `private(set)`), `@MainActor`-annotated, `nonisolated`, backtick-escaped identifiers, and multiline signatures.
- Fixes internal member leaks: `skeletonLog`, `SkeletonViewAppearance.shared`, `XMLHeader.isEmpty`/`toXML`, `XMLDocumentType` init, `SHA2.Variant.finalLength`, `PKCS5.PBKDF1.Variant.size`
- Implicit constructor guard: constructors checked even when `@implicit` (fixes `@_hasMissingDesignatedInitializers` types)
- `UnsafeRawBufferPointer` `@_cdecl` gate: constructors with buffer pointer params skipped (not C-representable)
- Simple enum `IsModuleInternal` property gate: enum properties with `@usableFromInline` now filtered (was already filtering methods but not properties)

**Session 5** (4 libraries improved: StripeIdentity, RxSwift, AMPopTip, Swinject):
- `spi_group_names` ABI JSON field detection → StripeIdentity passes; StripeConnect 51 → 2 errors
- `IsModuleInternal` gating in `MethodWrapperEmitter`, `WrapperValidation`, `ConstructorWrapperEmitter`, and optional-pointer/closure wrapper paths → internal method/constructor wrappers no longer emitted
- `@usableFromInline internal` and `@inlinable` free functions filtered from module-level emission
- `_`-prefixed method/property suppression: methods without explicit `AccessControl` attribute treated as internal
- `SBW_Utf8Slice` dedup fix: emission context properly threaded (eliminates Kingfisher duplicate declaration)
- Variadic expansion pattern detection: constructors with N unnamed protocol params + trailing Array skipped
- Raw generic param (`τ_0_0`) gate added to optional-pointer wrapper emission → AMPopTip passes
- `inout` parameter support in EveryProtocol conformance and WitnessDispatch emission (with caller buffer writeback) → Swinject passes

**Session 4** (4 libraries improved: Lottie, BlinkID, BlinkIDUX, SwipeCellKit):
- `@autoclosure` parameter detection from `.swiftinterface`
- Custom actor isolation gating (`@ProcessingActor`)
- ObjC-bridged struct `as AnyObject` in closure params
- Module/type name collision post-processor

**Session 3** (6 libraries improved: SnapKit, KeychainAccess, Starscream, Nuke x3):
- Post-processor extension self fix, dictionary enum fix, malformed parameter names

---

## Error Class Taxonomy

Every remaining Swift wrapper compilation failure falls into one of 18 verified error classes. Each class below includes root cause, affected libraries, generator code path, and verified fix approach.

### EC-1: `.swiftinterface` Module/Type Name Collision

**Affected**: 9 libraries (Reachability, KeychainSwift, NVActivityIndicatorView, AnimatedCollectionViewLayout, Valet, FSPagerView, SwiftyBeaver, SVGView, Mixpanel)

**Root cause**: When a module's public type has the same name as the module (e.g., module `Reachability` with `public class Reachability`), the Swift compiler misresolves `Module.Type` references in the framework's own `.swiftinterface` during `import`. Error: `'X' is not a member type of class/struct 'Module.Module'`.

**Verified fix** (POC'd on all 9):
1. Copy `.swiftinterface` to temp dir
2. Patch collision: apply existing collision regex `\b{ModuleName}\.(\w+(?:\.\w+)*)` → `$1` (from `SwiftWrapperPostProcessor.cs:161-188`), preserving import lines
3. Pre-compile to binary `.swiftmodule` via `xcrun swift-frontend -compile-module-from-interface` (reuse existing flow in `XCFrameworkResolver.GenerateAbiJson()` at lines 808-856)
4. Add `-I /tmp/precompiled/` to `InvokeSwiftCompiler` — binary module takes precedence over textual interface

**Pre-compilation test results** (all 9 pre-compile successfully):

| Library | Wrapper Compiles? | Residual Errors | Residual Error Class |
|---------|-------------------|-----------------|---------------------|
| Reachability | Yes (0 errors) | — | — |
| KeychainSwift | Yes (0 errors) | — | — |
| AnimatedCollectionViewLayout | Yes (0 errors) | — | — |
| Valet | Yes (0 errors) | — | — |
| SVGView | Yes (0 errors) | — | — |
| Mixpanel | Yes (0 errors) | — | — |
| NVActivityIndicatorView | No (144 errors) | Internal types (34 animation classes) | EC-2, EC-3 |
| FSPagerView | No (8 errors) | NSObjectProtocol (1) + optional closures (7) | EC-3, EC-15 |
| SwiftyBeaver | No (40 errors) | Internal FilterValidator (36) + nested type over-strip (4) | EC-2, EC-18 |

**Key findings**:
- No binary `.swiftmodule` files exist in any of the 9 frameworks — pre-compilation is the only path
- `-module-alias` doesn't work (can't alias a module to itself)
- Direct pre-compilation without patching fails (same collision error)
- 6/9 libraries fully fixed by pre-compilation alone

**Implementation**: Shared per-slice resolver near `XCFrameworkResolver`. Takes `moduleNameForCollision` + slice info. Returns `-I` path for `InvokeSwiftCompiler`. Both `CompileSlice` and `CompileAll` call it. SDK targets get it through the same code path (shared below CLI/SDK entrypoints).

### EC-2: Internal Type References in Wrapper Signatures

**Affected**: SkeletonView (~532 errors), NVActivityIndicatorView (144 post-collision), SwiftyBeaver (36 post-collision), CryptoSwift (6), Alamofire (4), StripeCryptoOnramp (2), StripePayments (~2)

**Root cause**: Session 6 fixed internal MEMBER detection (methods/properties that are internal). Missing: internal TYPE detection — methods/properties whose parameter or return types are themselves internal types. The generator emits wrappers that reference internal types like `ViewAssociatedKeys`, `SkeletonMultilineLayerBuilder`, `StreamEncryptor`, `FilterValidator`, etc. Swift wrapper fails: `module 'X' has no member named 'InternalType'`.

**Why current gates miss it**: `IHandler.HandleBaseDecl()` (line 202-225) intentionally skips the `IsModuleInternal` check for `@usableFromInline` types (they can appear in public signatures of `@inlinable` functions). But the wrapper emission for those types' members doesn't check if the parameter/return types are also internal.

**Generator code path**:
- `MemberGateEvaluator.EvaluateHardGates()` (lines 234-268): checks for bare generics, unsupported modules — **missing**: internal type resolution check for parameter/return types
- `MemberGateEvaluator.EvaluatePropertyHardGates()` (lines 274-296): same gap
- `MemberEmissionValidator.CanEmitMethod()` (lines 391-600): no internal type parameter check
- `MemberEmissionValidator.CanEmitProperty()` (lines 60-190): no internal type check

**Fix**: Add member-level gate — for each parameter/return type spec in a method or property, resolve to `TypeDecl` and check `IsModuleInternal`. Skip the wrapper if any referenced type is internal.

### EC-3: EveryProtocol Unsatisfiable Conformance

**Affected**: StripeIssuing (1), StripePayments (1), StripePaymentsUI (1), FSPagerView (1 post-collision), Mappedin (3), Alamofire (2), Kingfisher (1), ObjectMapper (1), Parchment (2), NVActivityIndicatorView (1 post-collision)

**Root cause**: `EveryProtocol` conformance is emitted for protocols it can't actually satisfy.

**Existing gates** (EveryProtocolEmitter:365-405 + ModuleHandler:516-540):
- ✅ Self requirement (`HasSelfRequirement`)
- ✅ Self-typed members (generic type params in signatures)
- ✅ No implementable instance members (static-only protocols)
- ✅ Associated types (`AssociatedTypes.Count > 0`)
- ✅ Codable inheritance (`InheritsCodable()`)
- ✅ Internal types, unsupported modules
- ❌ **Missing: Class-bound protocols** (NSObjectProtocol / `AnyObject` inheritance)
- ❌ **Missing: Synthesized-only conformance** (CaseIterable)

**Failing protocol → root cause**:

| Protocol | Library | Root Cause | Gate Status |
|----------|---------|-----------|-------------|
| NSObjectProtocol | StripeIssuing, StripePayments, StripePaymentsUI, FSPagerView, Parchment | Class-bound (requires inheriting NSObject) | ❌ Missing |
| CaseIterable | NVActivityIndicatorView | Requires compiler-synthesized `allCases` | ❌ Missing |
| Decodable/Encodable | Mappedin | Codable inheritance | ✅ Implemented but not catching transitive |
| DownloadResponseSerializerProtocol | Alamofire | Associated types | ✅ Implemented |
| DataTransformable | Kingfisher | Associated types | ✅ Implemented |
| ImmutableMappable | ObjectMapper | Associated types + custom init | ✅ Implemented |
| PagingViewController*DataSource | Parchment | NSObjectProtocol inheritance | ❌ Missing (class-bound gate) |
| STPFormEncodable, STPAPIResponseDecodable | StripePayments | NSObjectProtocol inheritance | ❌ Missing (class-bound gate) |

**Fix**: Add two new gates to the conformance check:
1. **Class-bound gate**: `protocolDecl.IsClassBound || InheritedProtocols contains NSObjectProtocol/AnyObject`
2. **Synthesized-only gate**: `protocolDecl.Name is "CaseIterable"` (EveryProtocol can't synthesize `allCases`)
3. **Verify Codable gate**: Ensure `InheritsCodable()` catches Mappedin's transitive case

**Implementation**: Centralized `CanSynthesizeConformance` check in `EveryProtocolEmitter` or `ModuleHandler`, replacing scattered gate checks.

### EC-4: `Unmanaged<ValueType>` — Struct Treated as Class

**Affected**: StripeConnect (8 errors — all errors), Kingfisher (3 of 18 errors)

**Root cause**: `WrapperValidation.IsOptionalWithReferenceInner()` (line 143-182) incorrectly classifies some value types as reference types. When a property returns `Optional<UIFont.Weight>`, the generator emits `Unmanaged.passRetained($0).toOpaque()` — but `Unmanaged` requires `T: AnyObject` and `UIFont.Weight` is a struct.

**Decision chain**:
1. `PropertyWrapperEmitter.GetCdeclReturnMapping()` line 468 → calls `IsOptionalWithReferenceInner()`
2. Returns true → emits `CdeclReturnKind.OptionalClassPointer`
3. Line 588 → emits `return (obj.property).map { Unmanaged.passRetained($0).toOpaque() }`

**Why it's wrong**: `IsOptionalWithReferenceInner` has a fallback heuristic (line 178) using `HasObjCClassPrefix` which can match struct types that follow ObjC naming conventions. Also, the TypeRecord `Kind` may be `ObjCBridged`/`ObjCRooted` even for structs.

**Existing partial guard**: Lines 155-163 guard against NSString typedef structs, but the pattern isn't generalized.

**Fix**: In `IsOptionalWithReferenceInner`, after resolving the TypeRecord, verify `Kind == TypeRecordKind.Class` (not just ObjCBridged/ObjCRooted). For ObjC-bridged structs, return false so they get `IndirectResult` marshalling instead. Apply in:
1. `WrapperValidation.IsOptionalWithReferenceInner()` (primary fix)
2. `PropertyWrapperEmitter.GetCdeclReturnMapping()` line 495 (secondary guard)
3. `ConstructorWrapperEmitter.GetCdeclParamMapping()` line 622-629 (setter path)

### EC-5: Raw Generic Parameter (τ_0_0) Leaked into Wrapper

**Affected**: Alamofire (5+ errors)

**Root cause**: Method-level generics (not class-level) produce unresolved `τ_0_0` type parameters in wrapper code. Session 5 added a τ_0_0 gate for optional-pointer emission, but method dispatch wrappers for generic methods still leak through.

**Generated code example** (Alamofire):
```swift
public func _dbw_publishResponse_CB7F610A_1(_ serializer: τ_0_0) -> DownloadResponsePublisher<τ_0_1> {
```

**Fix**: Strengthen the existing τ_0_0 gate — check for raw generic parameters in all method wrapper emission paths (not just optional-pointer).

### EC-6: `@MainActor` Isolation on `@_cdecl`/`@_silgen_name` Functions

**Affected**: Kingfisher (1 error), Parchment (1 error)

**Root cause**: Properties/methods marked `@MainActor` in the library propagate that annotation to `@_cdecl` wrapper functions. But `@_cdecl` functions are called from non-isolated C#/.NET context. Error: `call to main actor-isolated instance method in a synchronous nonisolated context`.

**Note**: The wrapper already uses `-strict-concurrency=minimal` flag, but this doesn't suppress all actor isolation errors on wrapper function declarations.

**Fix**: Strip actor isolation attributes (`@MainActor`) from `@_cdecl`/`@_silgen_name` wrapper function declarations. The wrapper function accesses the property through the object, so isolation is the caller's responsibility, not the wrapper's.

### EC-7: Type Ambiguity in `.load(as:)` Expressions

**Affected**: BonMot (28 errors)

**Root cause**: Wrapper code emits `value0.load(as: StringStyle.self)` where `StringStyle` is ambiguous — the type exists in the `BonMot` module (imported), and the unqualified name can't be uniquely resolved. Error: `conflicting arguments to generic parameter 'T' ('StringStyle' vs. 'StringStyle')`.

**Generator code path**: `ConstructorWrapperEmitter.GetCdeclParamMapping()` line 647 renders `let {label}Val = {label}.load(as: {swiftType}.self)`. The `swiftType` comes from `ExistentialBypassEmitter.RenderSwiftTypeSpec()` (line 1112) which returns unqualified names.

**Fix**: Use module-qualified names (`BonMot.StringStyle`) when rendering types in `.load(as:)` and `.assumingMemoryBound(to:)` expressions in wrapper code.

### EC-8: Protocol Composition `.self` Metatype

**Affected**: CryptoSwift (4 errors)

**Root cause**: Protocol extension wrappers reference `CryptoSwift.Updatable.self` — the metatype of a protocol — in a context where protocol metatypes aren't valid.

**Fix**: Gate or suppress protocol metatype references. When the type spec resolves to a protocol, skip the wrapper emission.

### EC-9: Subscript Label Mismatch

**Affected**: CryptoSwift (2 errors — `bitAt:`), ObjectMapper (5+ errors — `nested:`, `delimiter:`, `ignoreNil:`)

**Root cause**: Subscript parameter labels extracted from ABI JSON don't match the source declaration. The ABI may not encode all label variations for subscripts.

**Fix**: Cross-reference subscript parameter labels from `.swiftinterface` file. Extend `SwiftInterfaceAccessParser` to extract subscript declarations with parameter labels.

### EC-10: `@_spi` Member Leaked

**Affected**: StripePaymentSheet (2 errors)

**Root cause**: Wrapper references `none` which is `@_spi`-protected. Session 5 added `spi_group_names` detection for method-level SPI, but specific member references (enum cases, static properties) on SPI-gated types may leak.

**Fix**: Extend SPI detection to cover enum case and static member references accessed through SPI-gated types.

### EC-11: Default Parameter Overload Dispatch Mismatch

**Affected**: PhoneNumberKit (6 errors)

**Root cause**: `DefaultParameterOverloadEmitter` generates `@_cdecl` wrappers that call `@_silgen_name` dispatch methods, but the dispatch references point to the wrong overload. The 1-parameter `@_cdecl` calls the 3-parameter dispatch, causing `missing argument for parameter #2 in call`.

**Generated code**:
```swift
// 1-param @_cdecl wrapper calls 3-param dispatch — WRONG
let result = obj._dbw_getFormattedExampleNumber_C2D26DA8_3(countryCodeVal)
// But _dbw_...3 takes (countryCode, type, format) — 3 params
```

**Fix**: Fix overload key matching in `DefaultParameterOverloadEmitter` so dispatch wrappers call the correct overload with the correct parameter count.

### EC-12: `@autoclosure` Parameter Gap

**Affected**: Kingfisher (1 error)

**Root cause**: Session 4 added `@autoclosure` detection from `.swiftinterface`, but at least one case still leaks through. Error: `add () to forward '@autoclosure' parameter`.

**Fix**: Audit `@autoclosure` detection in wrapper code — the parameter needs `()` appended when forwarded. May be a gap in `GetAutoclosureParameters()` or a missing check in `MethodWrapperEmitter`.

### EC-13: Integer Type Width Mismatch

**Affected**: Kingfisher (2 errors — `UInt64` → `UInt`), StripePayments (2 errors — `Int64` → `Int`)

**Root cause**: Wrapper generates parameter with `UInt64`/`Int64` type, but the Swift method expects `UInt`/`Int`. This happens for ObjC enum raw values and Apple framework types where the ABI reports a fixed-width integer but Swift uses platform-width.

**Fix**: Map fixed-width integer types to platform-width types in wrapper parameter emission when the target method expects `UInt`/`Int`.

### EC-14: Missing Framework Import

**Affected**: StripePayments (1 error — `CNContact`)

**Root cause**: Wrapper uses `Unmanaged<CNContact>.fromOpaque(contact)` but the `import Contacts` statement is missing. The generator adds framework imports for the primary module but doesn't auto-detect transitive framework dependencies needed for parameter types.

**Fix**: Auto-detect framework imports by scanning wrapper code for types from known Apple frameworks (e.g., `CNContact` → `Contacts`, `CLLocation` → `CoreLocation`) and emitting the appropriate `import` statement.

### EC-15: Optional Closure Unwrapping with `@MainActor`

**Affected**: StripePaymentsUI (12 errors), FSPagerView (7 errors post-collision)

**Root cause**: EveryProtocol conformance emits protocol method stubs that call optional closure properties. When the closure type is `(@MainActor (STPPaymentCardTextField) -> ())?`, the wrapper doesn't use optional chaining. Error: `value of optional type '(@MainActor ...) -> ())?' must be unwrapped`.

**Fix**: Use optional chaining (`closure?()`) or force-unwrap with guard when calling optional closure properties in EveryProtocol stubs and WitnessDispatch emission.

### EC-16: `AnyObject` Existential Projection

**Affected**: Mappedin (2 errors)

**Root cause**: Property has type `AnyObject`, and the generator emits `resultPtr.initializeMemory(as: any AnyObject.self, ...)` — but `any AnyObject.self` is not a valid metatype expression in Swift. Error: `instance method 'self()' is not a member type of 'AnyObject'`.

**Fix**: Handle `AnyObject` properties as `UnsafeRawPointer` instead of trying to use existential type in memory operations. Use `Unmanaged<AnyObject>` marshalling (since AnyObject IS a class reference).

### EC-17: GRDB Protocol Extension Associated Types (Architectural)

**Affected**: GRDB (666 errors — `Element` 72, `Base` 80, `U` 110, `Value` 20, `Record` 10, etc.)

**Root cause**: Protocol extension wrappers use bare associated type names (`Element`, `Base`) outside their protocol context. The `ProtocolExtensionEmitter` (`ResolveSelfElement()` at lines 1877-1963) resolves `Self.Element` but doesn't carry forward the protocol's generic constraints into wrapper signatures.

**Near-term containment**: Prune protocol extension wrappers that reference unresolved associated types. Detect bare `Element`, `Base`, `Value`, `Record` etc. in wrapper signatures and skip those wrappers at emission time or post-processor level.

**Long-term fix**: Architectural — carry full generic constraint context from protocol definition into protocol extension wrapper signatures. Requires type graph changes.

### EC-18: SwiftyBeaver Nested Type Disambiguation

**Affected**: SwiftyBeaver (4 errors post-collision)

**Root cause**: The collision regex strips `SwiftyBeaver.Level` → `Level`, but `Level` is a nested enum inside class `SwiftyBeaver` — not a module-level type. The regex can't distinguish module.Type from Class.NestedType.

**Fix**: When applying the collision post-processor, maintain a set of types nested inside the colliding class (from ABI JSON). Skip stripping for references to those nested types. E.g., `SwiftyBeaver.Level` should stay as `SwiftyBeaver.Level` because `Level` is nested in class `SwiftyBeaver`.

### EC-19: Not Fixable

| Library | Reason |
|---------|--------|
| Quick | XCTest dependency — `XCTest/XCTest.h` not found. Test framework, not a generator bug |
| TinyConstraints | x86_64-only xcframework — no arm64 simulator slice. Stale build artifact |

---

## Per-Library Fix Map

Each library mapped to the exact error classes that need fixing, with expected result.

| Library | Raw Errors | Error Classes | All fixes applied → Result |
|---------|-----------|--------------|---------------------------|
| Reachability | 34 | EC-1 | ✅ Pass (verified) |
| KeychainSwift | 16 | EC-1 | ✅ Pass (verified) |
| AnimatedCollectionViewLayout | 38 | EC-1 | ✅ Pass (verified) |
| Valet | 462 | EC-1 | ✅ Pass (verified) |
| SVGView | 351 | EC-1 | ✅ Pass (verified) |
| Mixpanel | 342 | EC-1 | ✅ Pass (verified) |
| NVActivityIndicatorView | 22 (144 post-EC1) | EC-1, EC-2, EC-3 | ✅ Pass |
| FSPagerView | 78 (8 post-EC1) | EC-1, EC-3, EC-15 | ✅ Pass |
| SwiftyBeaver | 166 (40 post-EC1) | EC-1, EC-2, EC-18 | ✅ Pass |
| SkeletonView | 532 | EC-2 | ✅ Pass |
| StripeCryptoOnramp | 2 | EC-2 | ✅ Pass |
| StripeConnect | 8 | EC-4 | ✅ Pass |
| StripeIssuing | 2 | EC-3 | ✅ Pass |
| StripePaymentSheet | 2 | EC-10 | ✅ Pass |
| Alamofire | 34 | EC-2, EC-3, EC-5 | ✅ Pass |
| CryptoSwift | 34 | EC-2, EC-8, EC-9 | ✅ Pass |
| Mappedin | 10 | EC-3, EC-16 | ✅ Pass |
| StripePayments | 12 | EC-2, EC-3, EC-13, EC-14 | ✅ Pass |
| StripePaymentsUI | 26 | EC-3, EC-15 | ✅ Pass |
| Kingfisher | 18 | EC-3, EC-4, EC-6, EC-12, EC-13 | ✅ Pass |
| PhoneNumberKit | 6 | EC-11 | ✅ Pass |
| ObjectMapper | 14 | EC-3, EC-9 | ✅ Pass |
| BonMot | 28 | EC-7 | ✅ Pass |
| Parchment | 8 | EC-3, EC-6 | ✅ Pass |
| GRDB | 666 | EC-17 | ⚠️ Near-term containment likely; full fix architectural |
| Quick | 6 | EC-19 | N/A (skip) |
| TinyConstraints | — | EC-19 | N/A (skip) |

**Target**: 54/56 = 96.4% (25 libraries fixed + Quick/TinyConstraints skipped). GRDB may reach 100% with containment pruning.

---

## Implementation Sessions

### Session 7: Infrastructure + High-Impact Fixes (29 → 37)

**Scope**: EC-1, EC-2, EC-3, EC-4, EC-18

**EC-1: `.swiftinterface` pre-compilation** (done)
- `SwiftWrapperCompiler.PrecompileCollidingModule()`: creates shadow framework with patched `.swiftinterface` files (both regular and `.private`) pre-compiled to binary `.swiftmodule` via `swift-frontend -compile-module-from-interface`
- Shadow framework added as higher-priority `-F` path before the real framework search path
- Both `PatchSwiftInterface` and `SwiftWrapperPostProcessor.Process` share nested-type-aware collision regex (EC-18)
- Per-slice (target triple + SDK differ); built in `.wrapper-build/` temp area, cleaned up in `finally`
- Wired through `Compile`, `CompileSlice`, `CompileAll` with new `swiftInterfacePath` parameter

**EC-2: Internal type member gate** (done)
- `MemberGateEvaluator.ReferencesInternalModuleType()`: recursively checks if a TypeSpec references a type from the current module that's absent from the TypeDatabase (likely internal)
- Skips existential types (`IsAny` NamedTypeSpec, `ProtocolListTypeSpec`) to avoid false positives — protocol references may validly lack TypeRecords
- Added to both `EvaluateHardGates()` and `EvaluatePropertyHardGates()` with `SkipReason.ModuleInternal`

**EC-3: EveryProtocol conformance gates** (done)
- `IsClassBoundProtocol()`: transitive check via recursive `InheritedProtocols` walk (modeled after `InheritsCodable`). Checks `IsClassBound` flag + `NSObjectProtocol`/`AnyObject` names.
- `InheritsCaseIterable()`: same transitive pattern for CaseIterable.
- Both used in `EveryProtocolEmitter.EmitProtocolConformance()` and `ModuleHandler.EmitEveryProtocolConformances()` filter (passes `protocols` for intra-module lookup).

**EC-4: `Unmanaged<ValueType>` fix** (done)
- `WrapperValidation.IsOptionalWithReferenceInner()`: checks `TypeRecordKind` first — `Struct` and `Enum` always return false, regardless of ObjCBridged/ObjCRooted flags. Generalizes the previous NSString-only guard.

**EC-18: Nested type disambiguation** (done)
- `Program.cs` collects nested types from `collidingType.Types` into `nestedTypesInCollidingClass`
- `SwiftWrapperPostProcessor.Process()` and `PatchSwiftInterface()` both use match evaluator that preserves `Module.NestedType` when the first component is in the nested set

**Outcome**: 8 libraries fully fixed (Reachability, KeychainSwift, AnimatedCollectionViewLayout, Valet, SVGView, Mixpanel, NVActivityIndicatorView, SwiftyBeaver). Major error reductions: SkeletonView 532→2, FSPagerView collision+class-bound fixed (EC-15 remains). Libraries needing EC-5–EC-17 (SkeletonView, StripeConnect, StripeIssuing, etc.) have partial improvements but other error classes block full pass.

### Session 8: Remaining Fixes (target: 37 → 54)

**Scope**: EC-5 through EC-17

**EC-5: τ_0_0 gate strengthening** (Alamofire)
- Audit all wrapper emission paths for raw generic parameter references
- Add check in `MethodWrapperEmitter.ShouldEmitWrapper()` or `MemberGateEvaluator`

**EC-6: @MainActor stripping** (Kingfisher, Parchment)
- In `PropertyWrapperEmitter` and `MethodWrapperEmitter`: don't emit `@MainActor` on `@_cdecl`/`@_silgen_name` functions

**EC-7: Type qualification** (BonMot)
- Module-qualify type names in `.load(as:)` and `.assumingMemoryBound(to:)` expressions
- Modify `RenderSwiftTypeSpec()` or the `.load(as:)` rendering call site

**EC-8: Protocol metatype gate** (CryptoSwift)
- Detect protocol type in metatype position → skip wrapper

**EC-9: Subscript label cross-reference** (CryptoSwift, ObjectMapper)
- Extend `SwiftInterfaceAccessParser` to parse subscript declarations with labels
- Cross-reference during wrapper emission

**EC-10: @_spi gate extension** (StripePaymentSheet)
- Extend SPI detection to cover enum case / static member references

**EC-11: Default parameter overload fix** (PhoneNumberKit)
- Fix dispatch reference key in `DefaultParameterOverloadEmitter`

**EC-12: @autoclosure gap** (Kingfisher)
- Audit autoclosure detection; add `()` when forwarding autoclosure params

**EC-13: Integer type width** (Kingfisher, StripePayments)
- Map `UInt64` → `UInt`, `Int64` → `Int` for platform-width-expected parameters

**EC-14: Missing framework import** (StripePayments)
- Auto-detect `CNContact` → `import Contacts` (and similar Apple framework types)

**EC-15: Optional closure unwrapping** (StripePaymentsUI, FSPagerView)
- Use optional chaining in EveryProtocol stubs / WitnessDispatch for optional closures

**EC-16: AnyObject existential** (Mappedin)
- Handle `AnyObject` properties with `Unmanaged<AnyObject>` (it IS a class reference)

**EC-17: GRDB containment** (GRDB)
- Post-processor or emission gate: detect unresolved associated types in protocol extension wrapper signatures → strip those wrappers
- This is containment, not a full fix; reduces 666 errors to ~0 by skipping unsupported patterns

**Expected outcome**: ~17 more libraries fixed. Total: 54/56 (96.4%).

---

## Libraries Fixed by Session

| Session | Libraries | Count |
|---------|-----------|-------|
| 3 | SnapKit, KeychainAccess, Starscream, Nuke, Nuke@macos, Nuke@tvos | 6 |
| 4 | Lottie, BlinkID, BlinkIDUX, SwipeCellKit | 4 |
| 5 | StripeIdentity, RxSwift, AMPopTip, Swinject | 4 |
| 6 | XMLCoder | 1 |
| 7 | Reachability, KeychainSwift, AnimatedCollectionViewLayout, Valet, SVGView, Mixpanel, NVActivityIndicatorView, SwiftyBeaver | 8 |
| 8 (planned) | Alamofire, CryptoSwift, Kingfisher, Mappedin, PhoneNumberKit, ObjectMapper, BonMot, Parchment, StripePayments, StripePaymentSheet, StripePaymentsUI, FSPagerView, GRDB | ~13 |
| Pre-existing | BRLMPrinterKit, MicroblinkPlatform, SmartCardIO, SwiftyGif, DifferenceKit, CocoaLumberjackSwift, DeviceKit, Stripe*, StripeCore, StripeApplePay, StripeCameraCore, StripeCardScan, StripeFinancialConnections, StripeUICore | 13 |
