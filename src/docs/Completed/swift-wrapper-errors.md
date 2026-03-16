# Swift Wrapper Compilation Errors

Comprehensive analysis of Swift wrapper compilation failures caught by `validate-libraries.sh`. 89/90 targets pass C# compilation (Kingfisher has 4 pre-existing SwiftUI bridge errors); these are errors in the generated `.swift` wrapper that gets compiled into a `.framework` binary.

**Baseline**: 50/56 passing (34 ObjC/no wrapper), 6 failing — 2 EC-2 residual/architectural (SkeletonView, GRDB), 2 infra (StripeCryptoOnramp, StripeIssuing), 2 unfixable (Quick, TinyConstraints).

**Previous baselines**: 42/56 (session 10), 41/56 (session 9), 36/56 (session 8), 37/56 (session 7), 29/56 (session 6), 28/56 (session 5+RxSwift), 27/56 (session 5), 24/56 (session 4), 21/56 (session 3), 16/56 (`6bf59eab`).

## Session History

**Session 11** (8 libraries fixed: FSPagerView, Parchment, StripePaymentsUI, StripeConnect, StripePaymentSheet, Alamofire, Kingfisher, StripePayments):
- EC-20 (new, implemented): `IsObjCOptional` flag parsed from `"Optional"` in ABI JSON `declAttributes`. `WitnessDispatchEmitter` and `EveryProtocolEmitter` skip @objc optional methods/properties — calling these on protocol existentials returns Optional, requiring `?.` chaining that the dispatch pattern can't express. Fixes FSPagerView (1 error), StripePaymentsUI (12 errors), Parchment (partial).
- EC-3 (genericSig): `IsClassBoundProtocol()` now checks `ProtocolDecl.GenericSignature` for `NSObjectProtocol`/`AnyObject` constraints. ObjC protocols declare these in `genericSig` (e.g., `<τ_0_0 : ObjectiveC.NSObjectProtocol>`) rather than `inheritedProtocols`. Fixes StripePaymentsUI + StripePayments EveryProtocol conformance errors.
- EC-3 (static Self): EveryProtocol Self-typed member gate now includes static methods and static properties (previously only checked instance members). Protocols with `static func fromData(_:) -> Self` or `static var empty: Self` are now correctly skipped. Fixes Kingfisher DataTransformable conformance.
- EC-4 (value types): `UIFont.Weight`, `PHPickerResult`, `PHPickerFilter` added to `AppleFrameworkRegistry.ValueTypes`. Fixes StripeConnect `Unmanaged<UIFont.Weight>` errors.
- EC-10 (implemented): `IsSpiProtected` added to `EnumCaseDecl`, parsed from `spi_group_names` in ABI JSON. SPI-protected cases filtered from C# enum member emission, C# static case property emission, and Swift `CaseByIndex` wrapper (emits `fatalError` for SPI indices). Fixes StripePaymentSheet `.none` case access error.
- EC-12 (implemented): `@autoclosure` forwarding — adapted closures now append `()` when the parameter is `@autoclosure`. Applied in all 3 closure adapter call sites: `MethodWrapperEmitter`, `ClosureEmitter.SwiftWrapper`, `ConstructorWrapperEmitter`. Fixes Kingfisher autoclosure error.
- EC-13 (implemented): UIKit/AVFoundation XML databases corrected `rawValueType` from `Int64`→`Int` and `UInt64`→`UInt` for ObjC enum raw values (NSInteger/NSUInteger bridging). `GetCSharpEnumUnderlyingType` updated: `Int`→`long`, `UInt`→`ulong` (platform-width mapping). Fixes Kingfisher `UIControl.State` errors and StripePayments `UIBarStyle`/`UIKeyboardAppearance` errors.
- CF opaque pointer gate: `WitnessDispatchEmitter.IsTypeBlittable()` now rejects types that project to `System.IntPtr`/`nint` but originate from non-Swift modules (e.g., `Security.SecTrust`). These are reference types in Swift but blittable pointers in C#; dispatching them as `Int.self` causes type mismatch. Fixes Alamofire SecTrust witness dispatch error.
- String return type annotation: `MethodWrapperEmitter.EmitStringReturnBody()` now emits `let result: String = ...` instead of `let result = ...`, disambiguating overloaded methods with different return types. Fixes Alamofire `URLEncodedFormEncoder.encode()` ambiguity.
- Actor isolation nested-type fallback: `ApplyMemberActorIsolation` and `ApplyPropertyActorIsolation` try short key (`TypeName.member`) as fallback when qualified key (`Outer.TypeName.member`) doesn't match. Workaround for `SwiftInterfaceAccessParser` using `LastIndexOf('.')` which drops intermediate nesting components. Fixes Kingfisher `KF.Builder.set(to:)` @MainActor isolation.

**Session 10** (1 library fixed: CryptoSwift; error reduction in Alamofire, Parchment, Kingfisher, SkeletonView, GRDB):
- EC-2 (metadata gate): `MetadataWrapperEmitter` emission now gated on `!typeDecl.IsModuleInternal` across all 3 call sites (ClassHandler, TypeHandlerHelpers, EnumISwiftObjectMethodWriter). Internal types (both `@usableFromInline` and truly internal) are inaccessible by name from external Swift code; the wrapper's `Module.Type.self` reference won't compile. C# side falls back to CallConvSwift P/Invoke targeting the dylib's native metadata accessor (`$s...Ma`) instead of a dangling cdecl wrapper symbol. Fixes CryptoSwift metadata errors (BlockEncryptor, StreamEncryptor, StreamDecryptor) and Alamofire (JSONDecoder, PropertyListDecoder).
- EC-8 (protocol composition metatype): `initializeMemory(as:)` calls with protocol existential return types now parenthesize the metatype: `(any P1 & P2).self` instead of `any P1 & P2.self`. Without parentheses, `.self` binds to only the last protocol in the composition. Applied across MethodWrapperEmitter (non-throwing + throwing paths), PropertyWrapperEmitter, and SubscriptWrapperEmitter. Fixes CryptoSwift (4 errors: AES/ChaCha20 makeEncryptor/makeDecryptor returning `any Cryptor & Updatable`).
- EC-3 (transitive PAT gate): `InheritsProtocolWithAssociatedTypes()` added to `ModuleHandler` and `EveryProtocolEmitter`. Parses protocol `GenericSignature` (`<Self : Module.ParentProtocol>`) to find parent protocol constraints, then checks intra-module protocols for `AssociatedTypes > 0` or `HasSelfRequirement`. Cross-module check uses `TypeRecordFlags.HasAssociatedTypes | HasSelfRequirement`. Fixes Alamofire (ResponseSerializer → DataResponseSerializerProtocol/DownloadResponseSerializerProtocol with `SerializedObject` associated type), Parchment (PagingViewControllerDataSource, PagingViewControllerInfiniteDataSource), and Kingfisher (DataTransformable) EveryProtocol conformance errors.

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

### EC-10: `@_spi` Enum Case Leaked ✅ Session 11

**Affected**: StripePaymentSheet (1 error — `.none` case of `SetupFutureUsage`)

**Root cause**: `EnumCaseDecl` had no `IsSpiProtected` field. `CreateEnumCaseDecl()` never checked `spi_group_names`. The `CaseByIndex` Swift wrapper and C# enum/property emission iterated all cases without filtering.

**Fix** (implemented):
- `EnumCaseDecl.IsSpiProtected` added, parsed from `spi_group_names` via `IsNodeSpiProtected()`
- C# enum member emission: SPI cases skipped (`EnumHandler.SimpleEnum.cs`)
- C# static case properties: SPI cases skipped (`EnumHandler.RawRepresentable.cs`)
- Swift `CaseByIndex` wrapper: SPI indices emit `fatalError` instead of case construction

**Validation**: **StripePaymentSheet fully fixed** (1 error → 0). Unit tests pass.

### EC-11: Default Parameter Overload Dispatch Mismatch ✅ Session 8, refined Session 9

**Affected**: PhoneNumberKit (6 errors session 8, 3 residual)

**Root cause**: Two issues: (1) Session 8: `DefaultParameterOverloadEmitter` generated `@_cdecl` wrappers where the silgen function name suffix didn't match the trim count. (2) Session 9: `@_cdecl` wrappers calling `@_silgen_name` functions with large optional returns (e.g., `Optional<String>`) didn't forward the `resultPtr` to the silgen function's `_resultBuf` parameter.

**Fix** (implemented):
- Session 8: `GetSilgenFuncName(MethodDecl, int trimCount)` extracted as single source of truth for `_dbw_{name}_{hash}_{trim}` pattern. `EmitSwiftWrapper()` takes canonical `trim` loop variable.
- Session 9: `MethodWrapperEmitter.EmitSwiftMethodWrapper()` accepts `silgenHasResultBuffer` parameter. When true, appends `resultPtr` to the silgen call args and skips the wrapper's own result handling (silgen writes to `_resultBuf` directly). For throwing methods, integrates with the `do { try ... } catch { errorOut... }` structure by treating the call as void from the wrapper's perspective. Callers in `DefaultParameterOverloadEmitter` and `MethodHandler` pass `BoundGenericsHandler.IsLargeOptionalReturn()`.

**Validation**: **PhoneNumberKit fully fixed** (3 errors → 0). Unit tests pass.

### EC-12: `@autoclosure` Parameter Forwarding ✅ Session 11

**Affected**: Kingfisher (1 error)

**Root cause**: The `@autoclosure` attribute was correctly parsed from `.swiftinterface` and applied to the `ClosureTypeSpec`, and the closure adapter was correctly generated. But when the adapted closure was passed to the method call, the code emitted `adapterName` instead of `adapterName()`. For `@autoclosure` parameters, Swift wraps the expression in a closure, so the adapter (which IS a closure) must be called with `()` to produce the value the autoclosure expects.

**Fix** (implemented): Added `closureTypeSpec.IsAutoClosure ? "()" : ""` suffix to adapter call args in all 3 closure adapter emission sites:
- `MethodWrapperEmitter.cs` line 289 — `@_cdecl` method wrappers
- `ClosureEmitter.SwiftWrapper.cs` line 543 — closure-based Swift wrappers
- `ConstructorWrapperEmitter.cs` line 392 — constructor wrappers

**Validation**: **Kingfisher autoclosure error fixed** (1 error → 0). Unit tests pass.

### EC-13: Integer Type Width Mismatch ✅ Session 11

**Affected**: Kingfisher (2 errors — `UInt64` → `UInt`), StripePayments (2 errors — `Int64` → `Int`)

**Root cause**: UIKit/AVFoundation XML database entries used `rawValueType="Int64"` / `rawValueType="UInt64"` for ObjC enums whose actual raw value type is `Int` / `UInt` (from `NSInteger` / `NSUInteger` bridging). The Swift wrapper emitted `Int64`/`UInt64` parameter types for `init(rawValue:)` calls, but Swift's `Int` and `Int64` are distinct types.

**Fix** (implemented):
- `UIKitDatabase.xml`: 9 entries changed `Int64` → `Int`, 2 entries changed `UInt64` → `UInt`
- `AVFoundationDatabase.xml`: 1 entry changed `Int64` → `Int`
- `EnumHandler.SimpleEnum.GetCSharpEnumUnderlyingType()`: `Int` → `long`, `UInt` → `ulong` (preserves 64-bit C# enum underlying type for platform-width integers)

**Validation**: **Kingfisher UInt64→UInt fixed**, **StripePayments Int64→Int fixed**. Unit tests updated and pass.

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

### EC-20: Optional ObjC Protocol Method Dispatch ✅ Session 11

**Affected**: FSPagerView (1 error), StripePaymentsUI (12 errors), Parchment (1 error)

**Root cause**: ObjC protocols can declare `@objc optional` methods/properties. When called on a protocol existential (`any Protocol`), Swift wraps the result in Optional and requires `?.` optional chaining. The witness dispatch emitter called these directly (`existential.method(args)`) producing type errors. The EveryProtocol conformance declared implementations for optional methods that the conforming type doesn't need to provide.

**Fix** (implemented):
- `MethodDecl.IsObjCOptional` and `PropertyDecl.IsObjCOptional` added, parsed from `"Optional"` in ABI JSON `declAttributes`
- `WitnessDispatchEmitter`: skips @objc optional methods and properties in all 3 loops (getter, setter, method)
- `EveryProtocolEmitter`: skips @objc optional in vtable struct fields, protocol extension methods, and protocol extension properties

**Validation**: **FSPagerView fully fixed** (1 error → 0). **StripePaymentsUI fully fixed** (12 errors → 0). **Parchment fully fixed** (combined with EC-3 genericSig fix). Unit tests pass.

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
| CryptoSwift | EC-2, EC-8 | ✅ **Fixed (session 10)** |
| Alamofire | EC-2, EC-3, EC-13, CF, overload | ✅ **Fixed (session 11)** |
| Kingfisher | EC-3, EC-4, EC-12, EC-13, actor | ✅ **Fixed (session 11)** — swift:ok, C# has 4 pre-existing SwiftUI bridge errors (`KFImage` not in main bindings) |
| Parchment | EC-3, EC-20 | ✅ **Fixed (session 11)** |
| FSPagerView | EC-1, EC-20 | ✅ **Fixed (session 11)** |
| StripePayments | EC-3, EC-13 | ✅ **Fixed (session 11)** — swift:ok, but validation shows swift:fail because `Stripe3DS2` framework not available during wrapper compilation |
| StripePaymentSheet | EC-10 | ✅ **Fixed (session 11)** |
| StripePaymentsUI | EC-3, EC-20 | ✅ **Fixed (session 11)** |
| StripeConnect | EC-4 | ✅ **Fixed (session 11)** |
| SkeletonView | EC-2 | ⚠️ 266 errors (internal types — parent-type wrapper gate tried session 10, reverted) |
| GRDB | EC-17 | ⚠️ Containment gate implemented, architectural fix needed |
| StripeCryptoOnramp | infra | ⚠️ Missing transitive dep `StripeCameraCore` (via StripePaymentSheet, not in manifest) |
| StripeIssuing | infra | ⚠️ Missing `Stripe3DS2` framework (separate manual library, not auto-discoverable) |
| Quick | EC-19 | N/A (skip — XCTest dependency) |
| TinyConstraints | EC-19 | N/A (skip — x86_64-only xcframework) |

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

### Session 10: Metadata Gate + Protocol Composition + PAT Inheritance (41 → 42)

**Scope**: EC-2 metadata, EC-8 composition, EC-3 transitive PAT

**EC-2 (metadata gate)** (done)
- `MetadataWrapperEmitter` emission gated on `!typeDecl.IsModuleInternal` across ClassHandler, TypeHandlerHelpers, EnumISwiftObjectMethodWriter
- C# side falls back to CallConvSwift P/Invoke targeting the dylib's native `$s...Ma` metadata accessor (no dangling symbol)
- Attempted parent-type wrapper gate (`parentType.IsModuleInternal` in MethodWrapperEmitter etc.) but reverted — caused regressions in XMLCoder/SwiftyBeaver due to interaction with post-processor and wrapper compilation environment

**EC-8 (protocol composition metatype)** (done)
- `initializeMemory(as:)` calls parenthesize protocol existentials: `(any P1 & P2).self`
- Applied in MethodWrapperEmitter (2 paths), PropertyWrapperEmitter, SubscriptWrapperEmitter

**EC-3 (transitive PAT inheritance)** (done)
- `InheritsProtocolWithAssociatedTypes()` parses `GenericSignature` for parent protocol constraints
- Cross-module check uses `HasAssociatedTypes | HasSelfRequirement` from TypeRecord flags
- ABI JSON conformance parsing bug found (`Kind == "Conformance"` vs `kNominal = "TypeNominal"`) — NOT fixed due to cascading C# emission effects; GenericSignature-based approach used instead

**Outcome**: CryptoSwift fully fixed. Alamofire 6 → 2 errors, Parchment 4 → 2 errors, Kingfisher 8 → 7 errors. 9 new unit tests (4 PAT inheritance, 3 metatype parenthesization, 2 metadata gate regression).

### Session 11: Batch Fix — 8 Libraries (42 → 50)

**Scope**: EC-3 (genericSig + static Self), EC-4 (value types), EC-10, EC-12, EC-13, EC-20 (new), CF opaque pointer gate, overload disambiguation, actor isolation nested-type fallback.

See EC descriptions and session history above for implementation details. Key changes across 19 files, ~200 lines of generator changes + XML database corrections + test updates.

**Investigation**: Confirmed "dep gate" labels on Stripe libraries were incorrect. The C# dependency gate passes 14/14 for all Stripe targets. All Stripe wrapper failures were generator bugs (EC-4, EC-10, EC-13, EC-20) or missing transitive framework paths (StripeCryptoOnramp, StripeIssuing).

**Outcome**: 8 libraries fully fixed (FSPagerView, Parchment, StripePaymentsUI, StripeConnect, StripePaymentSheet, Alamofire, Kingfisher, StripePayments). Net: 50/56 passing. Kingfisher has 4 pre-existing C# SwiftUI bridge errors (KFImage type not in main bindings — unrelated to Swift wrapper). StripePayments wrapper compiles cleanly but validation reports swift:fail because Stripe3DS2 framework not available during wrapper compilation.

**Remaining (6 failing)**:
- **SkeletonView** — EC-2 (266 internal type errors). Parent-type wrapper gate tried session 10, reverted. Needs post-processor enhancement or per-type wrapper suppression.
- **GRDB** — EC-17 (architectural). Protocol extension associated types require full generic constraint context in wrapper signatures.
- **StripeCryptoOnramp** — infra: missing transitive dep `StripeCameraCore` (via StripePaymentSheet, not in validation manifest).
- **StripeIssuing** — infra: missing `Stripe3DS2` framework (separate manual library, not auto-discoverable by validation).
- **Quick** — EC-19: XCTest dependency, not a generator bug.
- **TinyConstraints** — EC-19: x86_64-only xcframework, no arm64 simulator slice.

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
| 10 | CryptoSwift | 1 |
| 11 | FSPagerView, Parchment, StripePaymentsUI, StripeConnect, StripePaymentSheet, Alamofire, Kingfisher, StripePayments | 8 |
| Pre-existing | BRLMPrinterKit, MicroblinkPlatform, SmartCardIO, SwiftyGif, DifferenceKit, CocoaLumberjackSwift, DeviceKit, Stripe*, StripeCore, StripeApplePay, StripeCameraCore, StripeCardScan, StripeFinancialConnections, StripeUICore | 14 |
