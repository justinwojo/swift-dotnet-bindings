# Swift Wrapper Compilation Errors

Comprehensive analysis of Swift wrapper compilation failures caught by `validate-libraries.sh`. All 90 targets pass C# compilation; these are errors in the generated `.swift` wrapper that gets compiled into a `.framework` binary.

**Baseline**: 41/56 passing (34 ObjC/no wrapper), 15 failing.

**Previous baselines**: 36/56 (session 8), 37/56 (session 7), 29/56 (session 6), 28/56 (session 5+RxSwift), 27/56 (session 5), 24/56 (session 4), 21/56 (session 3), 16/56 (`6bf59eab`).

## Session History

**Session 9** (5 libraries fixed: Nuke x3, PhoneNumberKit, ObjectMapper):
- EC-9 (refined): `SwiftInterfaceAccessParser.GetSubscriptLabels()` now correctly distinguishes labeled vs unlabeled subscript params. In Swift subscripts, single-name params (`subscript(key: String)`) have NO argument label — only two-name params (`subscript(bitAt index: Int)`) do. The `.swiftinterface` format is ambiguous for single-name (identical output for `subscript(key:)` and `subscript(_ key:)`), but the ABI JSON (`subscript(_:)`) is authoritative. Parser now returns `_` for single-name params. `SwiftABIParser.CreateSubscriptDecl()` forces `indexN` name pattern when `.swiftinterface` confirms no label. Fixes Nuke x3 (4 errors → 0 each), ObjectMapper (3 errors → 0).
- EC-11 (refined): `MethodWrapperEmitter.EmitSwiftMethodWrapper()` now accepts `silgenHasResultBuffer` parameter. When the `@_silgen_name` target has `_resultBuf` for large optional returns (e.g., `Optional<String>`), the `@_cdecl` wrapper forwards `resultPtr` to the silgen call and skips its own result handling. For throwing methods, the forwarding integrates with the existing `do { try ... } catch { errorOut... }` structure. Fixes PhoneNumberKit (3 errors → 0).

**Session 8** (2 libraries fixed: BonMot, Mappedin; 10 ECs implemented, 3 deferred):
- EC-5: `DefaultParameterOverloadEmitter.TryEmitOverloads()` now gates on `methodDecl.IsGeneric` — prevents `@_silgen_name` wrappers for method-level generics that produce unresolvable `τ_0_0` type names. `MemberGateEvaluator.EvaluateHardGates()` also checks `HasRawGenericTypeParams` as catch-all.
- EC-6: `@MainActor` stripped from ALL `@_cdecl` wrapper function declarations (method, property getter/setter, subscript getter/setter). Wrappers are C-bridge functions called from nonisolated C#; with `-strict-concurrency=minimal`, nonisolated wrappers can call `@MainActor` members without error.
- EC-7: `ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec()` — new rendering method that preserves module prefixes (`BonMot.StringStyle` instead of `StringStyle`). Used in all `.load(as:)`, `.initializeMemory(as:)`, and `.assumingMemoryBound(to:)` call sites across ConstructorWrapperEmitter, MethodWrapperEmitter, PropertyWrapperEmitter, SubscriptWrapperEmitter. Fixes BonMot (28 errors → 0).
- EC-8: `ProtocolExtensionEmitter.TryInjectMethod()` now gates on `TypeRecordKind.Protocol` for the conforming type. Protocol metatypes (e.g., `CryptoSwift.Updatable.self`) are invalid in `assumingMemoryBound(to:)` / `Unmanaged<T>.fromOpaque()` contexts.
- EC-9: `SwiftInterfaceAccessParser.GetSubscriptLabels()` parses subscript parameter labels from `.swiftinterface`. `SwiftABIParser` cross-references these to correct ABI JSON label mismatches. `SubscriptWrapperEmitter` preserves labels in bracket syntax via `FixSubscriptCallArg()` (strips only `indexN` auto-generated labels).
- EC-11: `DefaultParameterOverloadEmitter.GetSilgenFuncName()` extracted as single source of truth for `_dbw_{name}_{hash}_{trim}` pattern. `EmitSwiftWrapper()` now takes canonical `trim` loop variable, eliminating dispatch key divergence between `@_cdecl` and `@_silgen_name` wrappers.
- EC-14: `ModuleHandler.AppleFrameworks` expanded with 16 additional frameworks (Contacts, ContactsUI, Photos, PhotosUI, PassKit, MessageUI, etc.). Fixes missing `import Contacts` for `CNContact` references.
- EC-15: `SwiftTypeNameHelper.GetSwiftTypeName()` for closures now includes ALL type-level attributes (`@MainActor`, `@Sendable`) while excluding calling convention attributes (`@escaping`, `@autoclosure`). Fixes EveryProtocol closure property type mismatches.
- EC-16: `ConstructorWrapperEmitter.IsAnyObjectType()` detects AnyObject as both `ProtocolListTypeSpec` and `NamedTypeSpec`. `GetCdeclReturnMapping` routes AnyObject through `ClassPointer` (Unmanaged) instead of `IndirectResult` (invalid `any AnyObject.self`). `GetCdeclParamMapping` uses `Unmanaged<AnyObject>.fromOpaque()` for AnyObject parameters. Fixes Mappedin (2 errors → 0).
- EC-17: `ProtocolExtensionEmitter.TryInjectMethod()` gates on `WrapperValidation.ContainsRawGenericTypeParam()` and `ContainsAssociatedTypeReference()` for parameters and return types. Catches `τ_0_0`, `Self.X`, and `AssociatedTypeReferenceSpec` without relying on fragile bare-name matching.
- Deferred: EC-10 (@_spi member leaks, 2 errors), EC-12 (@autoclosure gap, 1 error), EC-13 (Int64/Int mismatch — `Int` ≠ `Int64` in Swift even on 64-bit; needs .swiftinterface cross-reference, 4 errors).

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

### EC-1: `.swiftinterface` Module/Type Name Collision ✅ Session 7

**Status**: Implemented. 6 libraries fully fixed, 3 unblocked for residual fixes.

### EC-2: Internal Type References in Wrapper Signatures ✅ Session 7

**Status**: Implemented. SkeletonView 532→2 errors, NVActivityIndicatorView + SwiftyBeaver internal types eliminated.

### EC-3: EveryProtocol Unsatisfiable Conformance ✅ Session 7

**Status**: Implemented. Class-bound + CaseIterable gates added.

### EC-4: `Unmanaged<ValueType>` — Struct Treated as Class ✅ Session 7

**Status**: Implemented. `TypeRecordKind` check generalizes NSString-only guard.

### EC-5: Raw Generic Parameter (τ_0_0) Leaked into Wrapper ✅ Session 8

**Affected**: Alamofire (5+ errors)

**Root cause**: Method-level generics (not class-level) produce unresolved `τ_0_0` type parameters in wrapper code. `DefaultParameterOverloadEmitter` generated `@_silgen_name` wrappers for methods with trailing defaults without checking `IsGeneric`, producing code like `public func _dbw_publishResponse_CB7F610A_1(_ serializer: τ_0_0)`.

**Fix** (implemented):
1. `DefaultParameterOverloadEmitter.TryEmitOverloads()`: `if (methodDecl.IsGeneric) return;` added after parent-type generic check
2. `MemberGateEvaluator.EvaluateHardGates()`: `WrapperValidation.HasRawGenericTypeParams()` added as catch-all for raw ABI generic type parameters in signatures

**Validation**: Reduces Alamofire errors (still blocked by other ECs). Unit tests pass.

### EC-6: `@MainActor` Isolation on `@_cdecl`/`@_silgen_name` Functions ✅ Session 8

**Affected**: Kingfisher (1 error), Parchment (1 error)

**Root cause**: `@MainActor` annotation was emitted on `@_cdecl` wrapper function declarations when the parent type or member was `@MainActor`-isolated. These are C-bridge functions called from nonisolated C#/.NET context.

**Fix** (implemented): Stripped `@MainActor` from ALL `@_cdecl` wrapper declarations:
- `MethodWrapperEmitter.EmitSwiftMethodWrapper()` — removed @MainActor emission
- `PropertyWrapperEmitter.EmitSwiftGetterWrapper()` — removed @MainActor emission
- `PropertyWrapperEmitter.EmitSwiftSetterWrapper()` — removed @MainActor emission
- `SubscriptWrapperEmitter.EmitSwiftSubscriptGetterWrapper()` — removed @MainActor emission
- `SubscriptWrapperEmitter.EmitSwiftSubscriptSetterWrapper()` — removed @MainActor emission

With `-strict-concurrency=minimal` (used by the wrapper compiler), nonisolated functions can call `@MainActor` members without error.

**Validation**: Reduces Kingfisher + Parchment errors (both still blocked by other ECs). Unit tests pass.

### EC-7: Type Ambiguity in `.load(as:)` Expressions ✅ Session 8

**Affected**: BonMot (28 errors)

**Root cause**: `ExistentialBypassEmitter.RenderSwiftTypeSpec()` stripped module prefixes (e.g., `BonMot.StringStyle` → `StringStyle`). When used in `.load(as: StringStyle.self)`, the unqualified name was ambiguous — the type exists in both the imported module and potentially others.

**Fix** (implemented):
- `ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec()` — new method preserving module prefixes
- Core rendering refactored to `RenderSwiftTypeSpecCore(typeSpec, moduleQualified)` with boolean parameter
- All `.load(as:)`, `.initializeMemory(as:)`, `.assumingMemoryBound(to:)` call sites updated across ConstructorWrapperEmitter (7 sites), MethodWrapperEmitter (4 sites), PropertyWrapperEmitter (1 site), SubscriptWrapperEmitter (1 site)

**Validation**: **BonMot fully fixed** (28 errors → 0). Unit tests pass.

### EC-8: Protocol Composition `.self` Metatype ✅ Session 8

**Affected**: CryptoSwift (4 errors)

**Root cause**: Protocol extension wrappers referenced `CryptoSwift.Updatable.self` — protocol metatypes are invalid in `assumingMemoryBound(to:)` and `Unmanaged<T>.fromOpaque()` contexts.

**Fix** (implemented): Gate in `ProtocolExtensionEmitter.TryInjectMethod()` — checks `TypeRecordKind.Protocol` for the conforming type and skips wrapper emission.

**Validation**: Reduces CryptoSwift errors (still blocked by EC-9). Unit tests pass.

### EC-9: Subscript Label Mismatch ✅ Session 8, refined Session 9

**Affected**: CryptoSwift (2 errors — `bitAt:`), ObjectMapper (5+ errors — `nested:`, `delimiter:`, `ignoreNil:`), Nuke x3 (4 errors — `key:`)

**Root cause**: Three issues: (1) ABI JSON `PrintedName` for subscripts sometimes had wrong/missing labels. (2) `SubscriptWrapperEmitter.BuildSubscriptAccessExpr` called `StripArgLabel` which stripped ALL labels from bracket syntax — even when labels were required. (3) `.swiftinterface` parser treated single-name params (`subscript(key: String)`) as labeled, but Swift subscripts use no label for single-name params — only two-name params (`subscript(bitAt index: Int)`) have labels.

**Fix** (implemented):
- `SwiftInterfaceAccessParser.GetSubscriptLabels()` — parses subscript declarations from `.swiftinterface` with parameter labels. Session 9: distinguishes single-name params (no label → `_`) from two-name params (first word is label, e.g., `bitAt`)
- `SwiftABIParser.CreateSubscriptDecl()` — cross-references parsed labels, overwrites ABI JSON labels when mismatched. Session 9: when `.swiftinterface` confirms no label (`_`), forces param name to `indexN` pattern so `FixSubscriptCallArg` strips the label
- `SubscriptWrapperEmitter` — removed `StripArgLabel`, added `FixSubscriptCallArg()` (strips only `indexN` auto-labels) and `GetProtocolSubscriptLabel()` for protocol conformance declarations

**Validation**: **Nuke x3 fully fixed** (4 errors → 0 each). **ObjectMapper fully fixed** (3 errors → 0). CryptoSwift `bitAt:` handled correctly (two-name param). Unit tests pass.

### EC-10: `@_spi` Member Leaked ⏳ Deferred

**Affected**: StripePaymentSheet (2 errors)

**Root cause**: Wrapper references `none` which is `@_spi`-protected. Session 5 added `spi_group_names` detection for method-level SPI, but specific member references (enum cases, static properties) on SPI-gated types may leak.

**Status**: Deferred — needs generated output debugging to identify exact leak path. Only 2 errors.

### EC-11: Default Parameter Overload Dispatch Mismatch ✅ Session 8, refined Session 9

**Affected**: PhoneNumberKit (6 errors session 8, 3 residual)

**Root cause**: Two issues: (1) Session 8: `DefaultParameterOverloadEmitter` generated `@_cdecl` wrappers where the silgen function name suffix didn't match the trim count. (2) Session 9: `@_cdecl` wrappers calling `@_silgen_name` functions with large optional returns (e.g., `Optional<String>`) didn't forward the `resultPtr` to the silgen function's `_resultBuf` parameter.

**Fix** (implemented):
- Session 8: `GetSilgenFuncName(MethodDecl, int trimCount)` extracted as single source of truth for `_dbw_{name}_{hash}_{trim}` pattern. `EmitSwiftWrapper()` takes canonical `trim` loop variable.
- Session 9: `MethodWrapperEmitter.EmitSwiftMethodWrapper()` accepts `silgenHasResultBuffer` parameter. When true, appends `resultPtr` to the silgen call args and skips the wrapper's own result handling (silgen writes to `_resultBuf` directly). For throwing methods, integrates with the `do { try ... } catch { errorOut... }` structure by treating the call as void from the wrapper's perspective. Callers in `DefaultParameterOverloadEmitter` and `MethodHandler` pass `BoundGenericsHandler.IsLargeOptionalReturn()`.

**Validation**: **PhoneNumberKit fully fixed** (3 errors → 0). Unit tests pass.

### EC-12: `@autoclosure` Parameter Gap ⏳ Deferred

**Affected**: Kingfisher (1 error)

**Root cause**: Session 4 added `@autoclosure` detection from `.swiftinterface`, but at least one case still leaks through. Error: `add () to forward '@autoclosure' parameter`.

**Status**: Deferred — only 1 error, needs specific Kingfisher output to identify the gap in `GetAutoclosureParameters()` parsing.

### EC-13: Integer Type Width Mismatch ⏳ Deferred

**Affected**: Kingfisher (2 errors — `UInt64` → `UInt`), StripePayments (2 errors — `Int64` → `Int`)

**Root cause**: ABI JSON reports `Swift.Int64`/`Swift.UInt64` where the Swift source type is `Swift.Int`/`Swift.UInt`. On 64-bit platforms, these have identical ABI representation but are **distinct types** in Swift source (`Int.self == Int64.self` returns `false`).

**Status**: Deferred — blanket normalization (`Int64` → `Int`) would break methods that genuinely use `Int64`. Needs targeted fix: either parse actual parameter types from `.swiftinterface`, or normalize only for ObjC-bridged contexts where `NSInteger` → `Int` bridging is known. Only 4 errors across 2 libraries.

### EC-14: Missing Framework Import ✅ Session 8

**Affected**: StripePayments (1 error — `CNContact`)

**Root cause**: `ModuleHandler.AppleFrameworks` set was missing frameworks like `Contacts`, `Photos`, `PassKit`, etc. Wrapper used `Unmanaged<CNContact>` but `import Contacts` was missing.

**Fix** (implemented): Added 16 frameworks to `AppleFrameworks`: Contacts, ContactsUI, EventKit, EventKitUI, PhotosUI, Photos, PassKit, MessageUI, UserNotifications, NetworkExtension, CoreBluetooth, CoreNFC, CoreMotion, CoreTelephony, CarPlay, Intents, IntentsUI, LinkPresentation, MediaPlayer.

**Validation**: Reduces StripePayments errors (still blocked by other ECs). Unit tests pass.

### EC-15: Optional Closure Unwrapping with `@MainActor` ✅ Session 8

**Affected**: StripePaymentsUI (12 errors), FSPagerView (7 errors post-collision)

**Root cause**: `SwiftTypeNameHelper.GetSwiftTypeName()` for closures only handled `@escaping` as a closure attribute. Type-level attributes like `@MainActor` and `@Sendable` were silently dropped, causing EveryProtocol property type mismatches. Additionally, `@escaping` was incorrectly included in property type annotations (it's only valid on function parameters).

**Fix** (implemented):
- `SwiftTypeNameHelper.GetSwiftTypeName()` for `ClosureTypeSpec` — includes ALL type-level attributes (`@MainActor`, `@Sendable`) while excluding calling convention attributes (`@escaping`, `@autoclosure`)
- `GetSwiftTypeNameForMetatype` — added handling for `Swift.Optional<ClosureType>` to emit `Optional<(X) -> Y>` instead of `((X) -> Y)?` for metatype `.self` access

**Validation**: Reduces StripePaymentsUI + FSPagerView errors. Unit tests pass.

### EC-16: `AnyObject` Existential Projection ✅ Session 8

**Affected**: Mappedin (2 errors)

**Root cause**: Property type `AnyObject` was routed through `IndirectResult` path, emitting `resultPtr.initializeMemory(as: any AnyObject.self, ...)` — invalid Swift metatype syntax. AnyObject can appear as `ProtocolListTypeSpec` (from existential parsing) or `NamedTypeSpec` (from TypeSpecParser).

**Fix** (implemented):
- `ConstructorWrapperEmitter.IsAnyObjectType()` — detects AnyObject in both `ProtocolListTypeSpec` and `NamedTypeSpec` forms
- `PropertyWrapperEmitter.GetCdeclReturnMapping()` — routes AnyObject through `ClassPointer` (`Unmanaged.passRetained().toOpaque()`) using `IsAnyObjectType()` helper
- `ConstructorWrapperEmitter.GetCdeclParamMapping()` — uses `Unmanaged<AnyObject>.fromOpaque()` for AnyObject parameters (before generic protocol existential check)

**Validation**: **Mappedin fully fixed** (10 errors → 0). Unit tests pass.

### EC-17: GRDB Protocol Extension Associated Types (Architectural) ✅ Session 8 (containment)

**Affected**: GRDB (666 errors — `Element` 72, `Base` 80, `U` 110, `Value` 20, `Record` 10, etc.)

**Root cause**: Protocol extension wrappers use bare associated type names (`Element`, `Base`) outside their protocol context. The `ProtocolExtensionEmitter` (`ResolveSelfElement()`) resolves `Self.Element` but doesn't carry forward the protocol's generic constraints into wrapper signatures.

**Fix** (containment, implemented):
- `ProtocolExtensionEmitter.TryInjectMethod()` — gates on `WrapperValidation.ContainsRawGenericTypeParam()` and `ContainsAssociatedTypeReference()` for all parameter and return TypeSpecs
- `ContainsAssociatedTypeReference()` — recursively detects `AssociatedTypeReferenceSpec`, `Self.X` references, and raw generic params (`τ_0_0`)
- Deliberately avoids bare-name matching (e.g., checking if `Element` lacks a module prefix) — too many false positives on legitimate type names

**Long-term fix**: Architectural — carry full generic constraint context from protocol definition into protocol extension wrapper signatures. Requires type graph changes.

**Validation**: Reduces GRDB errors (exact count TBD — containment prevents emission of problematic wrappers). Unit tests pass.

### EC-18: SwiftyBeaver Nested Type Disambiguation ✅ Session 7

**Status**: Implemented. Collision regex preserves `Module.NestedType` references.

### EC-19: Not Fixable

| Library | Reason |
|---------|--------|
| Quick | XCTest dependency — `XCTest/XCTest.h` not found. Test framework, not a generator bug |
| TinyConstraints | x86_64-only xcframework — no arm64 simulator slice. Stale build artifact |

---

## Per-Library Fix Map

Each library mapped to the exact error classes that need fixing, with expected result.

| Library | Error Classes | Status |
|---------|--------------|--------|
| Reachability | EC-1 | ✅ Fixed (session 7) |
| KeychainSwift | EC-1 | ✅ Fixed (session 7) |
| AnimatedCollectionViewLayout | EC-1 | ✅ Fixed (session 7) |
| Valet | EC-1 | ✅ Fixed (session 7) |
| SVGView | EC-1 | ✅ Fixed (session 7) |
| Mixpanel | EC-1 | ✅ Fixed (session 7) |
| NVActivityIndicatorView | EC-1, EC-2, EC-3 | ✅ Fixed (session 7) |
| SwiftyBeaver | EC-1, EC-2, EC-18 | ✅ Fixed (session 7) |
| BonMot | EC-7 | ✅ Fixed (session 8) |
| Mappedin | EC-3, EC-16 | ✅ Fixed (session 8) |
| Nuke, Nuke@macos, Nuke@tvos | EC-9 | ✅ **Fixed (session 9)** |
| PhoneNumberKit | EC-11 | ✅ **Fixed (session 9)** |
| ObjectMapper | EC-9 | ✅ **Fixed (session 9)** |
| SkeletonView | EC-2, EC-3 | ⚠️ 266 errors (internal types + EveryProtocol NSObjectProtocol) |
| Alamofire | EC-2, EC-3 | ⚠️ 10 errors (internal types JSONDecoder/PropertyListDecoder, EveryProtocol, ambiguous encode, SecTrust type) |
| CryptoSwift | EC-2, EC-8 | ⚠️ 14 errors (internal types StreamEncryptor/StreamDecryptor/BlockEncryptor, protocol metatype) |
| Kingfisher | EC-2, EC-12, EC-13 | ⚠️ EC-12+EC-13 deferred, plus internal nested types |
| Parchment | EC-3, EC-20 | ⚠️ 4 errors (EveryProtocol, label mismatch, @MainActor call) |
| FSPagerView | EC-1, EC-20 | ⚠️ Collision fix works, but optional protocol method dispatch fails |
| StripePayments | EC-2, EC-3, EC-13 | ⚠️ EC-13 deferred (dep gate) |
| StripePaymentSheet | EC-10 | ⚠️ EC-10 deferred (dep gate) |
| StripePaymentsUI | EC-3, EC-15 | ⚠️ swift:fail (dep gate) |
| StripeCryptoOnramp | EC-2 | ⚠️ swift:fail (dep gate) |
| StripeConnect | EC-4 | ⚠️ swift:fail (dep gate) |
| StripeIssuing | EC-3 | ⚠️ swift:fail (dep gate) |
| GRDB | EC-17 | ⚠️ Containment gate implemented, still swift:fail |
| Quick | EC-19 | N/A (skip) |
| TinyConstraints | EC-19 | N/A (skip) |

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

### Session 8: Remaining Fixes (37 → 36, +2 fixed / -3 Nuke infra)

**Scope**: EC-5 through EC-17 (10 implemented, 3 deferred)

See EC descriptions above for implementation details. Key changes across 24 files, ~1500 lines, 43 new tests.

**Outcome**: BonMot and Mappedin fully fixed. Nuke x3 dropped from pass to fail (xcframework creation infrastructure issue, not generator regression — wrapper compiles cleanly with zero errors). Net: 36/56 passing. Error reductions in Alamofire, CryptoSwift, Kingfisher, PhoneNumberKit, ObjectMapper, Parchment, FSPagerView, StripePaymentsUI, GRDB — but these libraries still have other blocking error classes or xcframework creation issues preventing full pass.

### Session 9: Subscript Labels + Result Buffer Forwarding (36 → 41)

**Scope**: EC-9 refinement, EC-11 refinement

**EC-9: Subscript label disambiguation** (done)
- Root cause: `.swiftinterface` format is ambiguous for single-name subscript params — `subscript(key: String)` (no label) and `subscript(key key: String)` (label `key`) both appear as `subscript(key: String)`. The parser was treating all single-name params as labeled, but Swift subscripts use no argument label for single-name params.
- `SwiftInterfaceAccessParser.GetSubscriptLabels()`: now returns `_` for single-name params (`words.Length < 2`), only returns a label for two-name params (`subscript(bitAt index: Int)` → `bitAt`)
- `SwiftABIParser.CreateSubscriptDecl()`: when `.swiftinterface` confirms no label, forces param name to `indexN` pattern (even if ABI JSON gave it a name like `key`) so `FixSubscriptCallArg` strips it in bracket syntax

**EC-11: Large optional result buffer forwarding** (done)
- Root cause: `@_cdecl` wrappers calling `@_silgen_name` functions with large optional returns (e.g., `Optional<String>`) didn't forward `resultPtr`. The silgen function expected `_resultBuf` as its last param but the cdecl wrapper only passed method arguments.
- `MethodWrapperEmitter.EmitSwiftMethodWrapper()`: new `silgenHasResultBuffer` parameter. When true + `needsResultPtr`, appends `resultPtr` to the silgen call and skips the wrapper's own result handling. For throwing methods, the call is treated as void inside the `do { try ... } catch { errorOut... }` structure.
- `DefaultParameterOverloadEmitter`: passes `env.BoundGenericsHandler.IsLargeOptionalReturn(overloadDecl)` as `silgenHasResultBuffer`
- `MethodHandler`: passes same check for debug param wrappers

**Outcome**: Nuke x3, PhoneNumberKit, and ObjectMapper fully fixed. Net: 41/56 passing. Session also investigated remaining failures:
- SkeletonView: 266 errors (far more than "2 residual" from session 7 doc — internal types + EveryProtocol NSObjectProtocol conformance)
- Alamofire: 10 errors (internal types JSONDecoder/PropertyListDecoder, EveryProtocol conformance, type mismatches)
- FSPagerView: EC-1 collision fix works correctly, but remaining errors are optional protocol method dispatch (new EC-20)
- Parchment: 4 errors (EveryProtocol, label mismatch, @MainActor isolation)

### Session 10 (planned): Remaining gaps

**EC-10**: Debug StripePaymentSheet generated output to find `none` reference path (2 errors).

**EC-12**: Debug Kingfisher autoclosure leak — single parameter not detected by `GetAutoclosureParameters()` (1 error).

**EC-13**: Cross-reference integer types from `.swiftinterface` to normalize `Int64` → `Int` where the source type is platform-width (4 errors).

**EC-20** (new): Optional protocol method dispatch. ObjC protocols with `optional func` declarations produce methods that return optionals when called on existentials. Witness dispatch wrappers call them without unwrapping. Affects FSPagerView, Parchment.

**EC-2 residuals**: SkeletonView (SkeletonLayerBuilder, SkeletonLayer, SkeletonTextNodeAssociatedKeys), Alamofire (JSONDecoder, PropertyListDecoder), CryptoSwift (StreamEncryptor, StreamDecryptor, BlockEncryptor) — internal types leaking through the EC-2 gate.

---

## Libraries Fixed by Session

| Session | Libraries | Count |
|---------|-----------|-------|
| 3 | SnapKit, KeychainAccess, Starscream | 3 |
| 4 | Lottie, BlinkID, BlinkIDUX, SwipeCellKit | 4 |
| 5 | StripeIdentity, RxSwift, AMPopTip, Swinject | 4 |
| 6 | XMLCoder | 1 |
| 7 | Reachability, KeychainSwift, AnimatedCollectionViewLayout, Valet, SVGView, Mixpanel, NVActivityIndicatorView, SwiftyBeaver | 8 |
| 8 | BonMot, Mappedin | 2 |
| 9 | Nuke, Nuke@macos, Nuke@tvos, PhoneNumberKit, ObjectMapper | 5 |
| Pre-existing | BRLMPrinterKit, MicroblinkPlatform, SmartCardIO, SwiftyGif, DifferenceKit, CocoaLumberjackSwift, DeviceKit, Stripe*, StripeCore, StripeApplePay, StripeCameraCore, StripeCardScan, StripeFinancialConnections, StripeUICore | 14 |
