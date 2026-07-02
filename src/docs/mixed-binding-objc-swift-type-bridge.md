# Mixed ObjC+Swift bindings: the missing type-resolution bridge

**Status:** Finding + reviewed design. Not started.
**Written:** 2026-07-01
**Reviewed:** 2026-07-01 — deep code-verification pass over the resolver, marshaler, ObjC pipeline, and module-database machinery. Diagnosis confirmed; the two original option sketches are superseded by the *Recommended approach* below (in-memory registration into the module's own type database), and the verified-facts section records what the code actually supports.
**Origin:** Investigating why the Facebook SDK (FBSDK) re-spike bound cleanly and round-tripped on both runtimes, yet the *public* API surface was thinner than expected. This documents the single highest-leverage generator gap behind that thinness, with reproduction and implementation starting points, so a future session can pick it up cold.

---

## TL;DR

In a **mixed Objective-C + Swift** binding, the generator emits two halves that **do not share a type table**:

- the **ObjC half** — a bespoke clang-AST pass that emits `ApiDefinition.cs` / `StructsAndEnums.cs` from an in-memory `ObjCModule` that never touches `ITypeDatabase`;
- the **Swift half** — the normal Parser → TypeDatabase → Marshaler → Emitter pipeline.

When a Swift-interface member references an **Objective-C-defined type** (e.g. `FBSDKCoreKit.AccessToken`), the Swift-side type resolver has no way to learn that this name is already bound as `partial interface FBSDKAccessToken` on the ObjC side. It falls back to `Swift.AnyType` / `object`, or drops the member as `UnsupportedSignature` ("unsupported placeholder type") / `UnsupportedType`.

In an SDK like FBSDK whose **foundational model types are all Objective-C** (`AccessToken`, `AuthenticationToken`, `LoggingBehavior`, `AdvertisingTrackingStatus`, `UserAgeRange`, `Location`, `LoginAuthType`, …), this one gap Swiss-cheeses the Swift surface: every Swift member that touches a core model type degrades or drops.

**The fix is a one-directional bridge: feed the ObjC pass's parsed type names into the Swift type resolver so an ObjC-bound type resolves to its ObjC binding instead of `AnyType`.** It is the headline lever for making mixed ObjC+Swift bindings substantially more complete — not just for Facebook. The entire gap is *resolution* — once a type resolves with the `ObjCBridged` flag, the existing marshalling pipeline handles it with zero new marshaler code (verified below).

---

## Why this matters (the FBSDK data)

Re-spike numbers, four modules, iOS (`obj/Debug/net10.0-ios/swift-binding/binding-report.json` in `swift-dotnet-packages/libraries/Facebook/`):

| Module | Total members | Emitted | Skipped | % emitted |
|---|---:|---:|---:|---:|
| FBSDKCoreKit | 1055 | 651 | 449 | 61% |
| FBAEMKit | 201 | 91 | 136 | 45% |
| FBSDKLoginKit | 468 | 292 | 215 | 62% |
| FBSDKShareKit | 295 | 204 | 122 | 69% |

**Most of what's skipped is correctly out of scope.** Aggregating skip reasons across the four modules, they fall into three buckets:

- **~72% internal / internal-reach** (`ModuleInternal` 277, `Pattern2InternalTypeReach` 366, `ParentModuleInternalNoFallback` 21) — members that are `@usableFromInline internal`, or public members whose signature/parent reaches an internal type. Not bindable as public C# API; a Swift consumer can't call these either. **Correctly dropped.**
- **~5% reverse-dispatch proxy conformances** (`EveryProtocolConformanceSkipped` 47) — mixed; some fixable, most reach internal/unsupported types.
- **~23% genuine generator gaps** (`UnsupportedSignature` 87, `AnyTypeFallback` 45, `MissingWrapperSymbol` 34, `UnsupportedExistential` 12, `UnsupportedType` 8, `DuplicateSignature` 8, `StaticProtocolMember` 6, `UnsupportedClosure` 4, misc) — the only bucket we can close by writing generator code.

So raw coverage has a hard ceiling here (~70%) no matter how good the generator gets — you cannot bind `@usableFromInline internal` types as public API. **The only bucket worth chasing is that ~23%, and it lands disproportionately on the high-value public types.**

### The real-gap drops land on the marquee public types

Filtering the fixable-gap reasons to public (non-underscore) declaring types, the drops (~194 members) concentrate on exactly the types a consumer cares about:

| Type | Non-structural drops | Reasons |
|---|---|---|
| `FBSDKLoginKit.LoginConfiguration` | 9 | `UnsupportedSignature` ×6, `DuplicateSignature` ×2, `AnyTypeFallback` |
| `FBSDKCoreKit.Profile` | 7 | `AnyTypeFallback` ×3 (`ageRange`/`hometown`/`location`), `UnsupportedSignature` ×4 |
| `FBSDKCoreKit.ApplicationDelegate` | 5 | `UnsatisfiedGenericConstraint` ×2, `UnsupportedSignature` ×3 |
| `FBSDKCoreKit.Settings` | 5 | `UnsupportedType`, `AnyTypeFallback`, `UnsupportedSignature` ×3 |
| `FBSDKCoreKit.AppLink` / `AppLinkNavigation` | 3 / 4 | `UnsupportedExistential` (`[any AppLinkTargetProtocol]`) |
| `FBSDKLoginKit.FBLoginButton` | 4 | `AnyTypeFallback`, `NonBlittableCallConvSwift`, `UnsupportedSignature` ×2 |
| `FBSDKLoginKit.LoginManagerLoginResult` | 3 | `AnyTypeFallback` ×2 (`token`, `authenticationToken`), `UnsupportedSignature` |
| `FBSDKShareKit.SharePhoto.Source` / `ShareVideo.Source` | 3 / 3 | `DuplicateSignature` (enum property name vs case name) |

The bulk of `AnyTypeFallback` (43 on public types) and a large share of `UnsupportedSignature` "placeholder type" (65) trace to **one** root cause below.

---

## Root cause, with evidence

FBSDK's core model types are **Objective-C**, declared in the framework's ObjC headers, and the Swift interface merely *references* them.

`FBSDKCoreKit.framework/Headers/FBSDKAccessToken.h:73`:

```objc
@interface FBSDKAccessToken : NSObject <NSCopying, NSObject, NSSecureCoding,
                                        FBSDKAccessTokenProviding, FBSDKTokenStringProviding>
```

We bind it correctly on the **ObjC** side — generated `ApiDefinition.cs:2791`:

```csharp
partial interface FBSDKAccessToken : INSCopying, INSSecureCoding,
                                     FBSDKAccessTokenProviding, FBSDKTokenStringProviding
```

But the **Swift** interface references it as `FBSDKCoreKit.AccessToken?` — e.g. `LoginManagerLoginResult.token: FBSDKCoreKit.AccessToken?` and `authenticationToken: FBSDKCoreKit.AuthenticationToken?`. When the Swift pipeline resolves that type it finds nothing, and the generated Swift-side C# degrades it to `object` — the `AnyType` fallback. Generated `FBSDKCoreKit.cs`:

```csharp
public object AccessTokenProvider { get; }   // AccessToken → object
```

**That is the entire mechanism.** The type is bound; the Swift resolver just can't see it. The same happens for:

- `LoggingBehavior` — an ObjC `NS_TYPED_ENUM` (bound as `[Field]` `NSString` constants in `StructsAndEnums.cs`). `Settings.loggingBehaviors: Set<LoggingBehavior>` → `SwiftSet<AnyType>`; `Settings.enableLoggingBehavior(_:)` → `UnsupportedSignature`.
- `AdvertisingTrackingStatus` — an ObjC `NS_ENUM` (bound as `enum FBSDKAdvertisingTrackingStatus : ulong`, `StructsAndEnums.cs:18`). `Settings.advertisingTrackingStatus` → `UnsupportedType` "Type resolution failed".
- `UserAgeRange`, `Location` — ObjC classes. `Profile.ageRange/hometown/location` → `AnyType`; `Profile.init(...)` → `UnsupportedSignature`.
- `LoginAuthType` — an NS_TYPED_ENUM. `LoginConfiguration.authType`, `FBLoginButton.authType` → `AnyType`.

**Not** every "placeholder type" is this bug. `ApplicationDelegate.applicationDidBecomeActive(_ application: UIKit.UIApplication?)` fails because the placeholder is `UIApplication` — an *external* framework type we don't bind. Those are out of scope (see below). But on FBSDK's public surface the in-framework ObjC-type case dominates.

---

## The generator code map

### Where the Swift side falls back to `AnyType`

`src/Swift.Bindings/src/TypeDatabase/TypeDatabaseExtensions.cs`
- `AnyType` (≈310–317) — the static `TypeRecord` singleton that *is* the "unresolved" projection (`Kind = Protocol`, `CSharpTypeName.AnyType`).
- `GetTypeRecordOrAnyType(...)` overloads (≈79–105, 203–214) — the chokepoints: when `TypeResolver.Default.TryResolve` fails, they return `AnyType`. The `SwiftTypeName` overload (203–214) first tries `IsObjCClassSwiftType` → `CreateObjCBridgedTypeRecord` (≈443) before giving up — **the only existing escape hatch, and it fires only for types already recognized as ObjC (Apple-only today).**
- `TryGetAnyTypeFallbackInfo(...)` (≈249–275) — produces the `AnyTypeFallback` diagnostic; synthesizes `"Type is missing from the type database"` when the resolver found nothing (the message an unresolved `FBSDKCoreKit.AccessToken` hits).

`src/Swift.Bindings/src/TypeDatabase/Resolver/TypeResolver.cs`
- `TypeResolver.Default` (≈85–111) — the ordered strategy chain. Last strategy is `ObjCBridgingStrategy` (appended ≈111); fall through all of them → `TryResolve` returns false → `AnyType`.

Member-skip emission sites:
- `Emitter/StringEmitter/MemberGateEvaluator.cs` (≈96, 140, 221, 294, 301, 458) — `SkipReason.AnyTypeFallback` for props/methods/subscripts carrying `AnyType` generic args.
- `Emitter/StringEmitter/MemberEmissionValidator.cs:760` + `Handler/MethodHandler.cs:475-476,1455-1456` + `Handler/OperatorHandler.cs:224` — `UnsupportedSignature` "unsupported placeholder type" (driven by `SignatureHandler.GetWrapperSignature().ContainsPlaceholder`).
- `Emitter/StringEmitter/Handler/PropertyHandler.cs:241` + `MemberEmissionValidator.cs:307` — `UnsupportedType` "Type resolution failed".
- `Reporting/BindingReport.cs` — `enum SkipReason` (`UnsupportedType`, `AnyTypeFallback`, `UnsupportedSignature`).

### Where the ObjC half is emitted (separate pass, separate model)

`src/Swift.Bindings/src/ObjC/Pipeline/ObjCPipeline.cs`
- `ObjCPipeline.Run(...)` (≈22–206) — the whole ObjC pass. Invokes `clang -Xclang -ast-dump=json` (`ClangAstInvoker`), parses JSON into an in-memory `ObjCModule` (`ClangAstParser`, `ObjC/Model/ObjCModule.cs`), applies mixed-framework dedup, then emits `ApiDefinitionEmitter.Emit` → `ApiDefinition.cs`, `StructsAndEnumsEmitter.Emit` → `StructsAndEnums.cs`, `ObjCBindingProjectEmitter.Emit` → companion csproj.
- This is **not** objective-sharpie/bgen shelled out — it's a bespoke in-process reimplementation, and critically the `ObjCModule` object model **never touches `ITypeDatabase` / `TypeRecord`.** The ObjC-bound types exist only inside this pass.

### The one existing cross-pipeline link (wrong direction) + the Apple-only bridge

- `src/Swift.Bindings/src/Emitter/SwiftTypeOwnershipManifest.cs` — `swift-types.json`. Written by the **Swift** pass, consumed by the **ObjC** pass (`ReadOwnedObjCRuntimeNames`) purely so ObjC emission drops classes the Swift side already owns. **Swift → ObjC only, for dedup — no ObjC → Swift equivalent.**
- `src/Swift.Bindings/src/TypeDatabase/Resolver/Strategies/ObjCBridgingStrategy.cs` — the last-resort resolver strategy, but it only claims a type if `TypeDatabaseExtensions.IsObjCModuleType` is true, which is true only for `NSObject`/`NSProxy` roots or modules listed as `autoBridge` in `src/Swift.Bindings/src/Data/apple-frameworks.json` (loaded by `AppleFrameworkRegistry`). That JSON is an **exhaustively first-party Apple** allowlist (UIKit, AppKit, …) with **no extension point for a consumer's own mixed-binding ObjC surface.** Pinned by `TypeDatabaseExtensionsTests.cs:244-249` and `:851-855`.

### The sequencing constraint (read this before designing the fix)

`src/Swift.Bindings/src/BindingsGeneratorCommand.cs` → `Execute(...)` runs everything sequentially:
1. Swift pipeline (`BindingsGenerator.GenerateBindings`, ≈790) runs to completion — **all Swift type resolution and C# emission happen here.**
2. `CollectSwiftEmittedTypeNames` reads back `swift-types.json` (≈1279).
3. `ObjCPipeline.Run(..., isMixed: true, excludeTypeNames: swiftTypeNames)` (≈1288).

**Today the ObjC types do not exist as parsed data until *after* Swift resolution has already finished and emitted.** Any bridge must move the ObjC type discovery to before Swift resolution.

**Verified: the re-sequencing is mechanical, and no "lightweight pre-scan" is needed — do the full parse once, earlier.** Inside `ObjCPipeline.Run` (`ObjC/Pipeline/ObjCPipeline.cs:22–206`), the parse half (framework-path resolve → umbrella-header find → `ClangAstInvoker` → `ClangAstParser.Parse` → raw `ObjCModule`, lines ≈41–84) has **zero dependency on `excludeTypeNames`**; only the filter+emit half (≈87 onward: `FilterForMixedFramework`, delegate detection, the three emitters) needs the Swift pass's `swift-types.json`. No data threads backward from emit into parse, so splitting `Run` into `Parse(...) → ObjCModule` and `FilterAndEmit(module, excludeTypeNames, ...)` is a straight extraction. The clang AST dump is exactly one subprocess per generator run either way (`ClangAstInvoker.InvokeClangAstDump`, `ClangAstInvoker.cs:31–105`; there is no caching to invalidate), so reordering adds no cost — and the parse could even run concurrently with the Swift *parse*, since the bridged records are only needed before Swift *resolution/emission* consumes them.

---

## Design review — verified facts that shape the fix (2026-07-01)

A code-verification pass confirmed the diagnosis and surfaced six facts that materially reshape the approach. Each was verified by direct read, not inferred.

1. **The downstream marshalling path is already fully general — the gap is resolution only.** Once a type resolves with `TypeRecordFlags.ObjCBridged`, `TypeProjectionFactory.ProjectNamedType` routes it to `ObjCBridgedProjection` (`Marshaler/Projection/ObjCBridgedProjection.cs`): P/Invoke type `IntPtr`, parameters pass `.Handle`, returns come back through `ObjCRuntime.Runtime.GetNSObject<T>` / `GetINativeObject<T>` (`MarshallingHelpers.FormatObjCBridgeCall`). **No Swift metadata is consulted anywhere on this path** — `MetadataAccessor = ""` is a live, shipped shape (the curated `UIKitDatabase.xml` entries include enums with no `mangledName` attribute at all, parsed by the same `ReadVersion1_0`). `Optional<T>` goes through `IsOptionalObjCBridged` (`MarshallingHelpers.cs:204–222`), whose *first* branch is a plain type-database lookup for the ObjCBridged flag — DB-registered records take the exact parity-safe branch the constraints doc demands. Containers: `ObjCBridgedProjection` implements `GetParameterElementConversion`/`GetReturnElementConversion` (per-element `.Handle` / `GetNSObject<T>`), which `ArrayProjection`/`SetProjection` consume — the Apple-proven path; note it does **not** set `UsesObjCContainerBridge` (that whole-container NSArray/NSSet bridge belongs to `ObjCBridgeableProjection`, the value-type bridge, e.g. URL↔NSURL and the NSString typed-enum remaps).

2. **The managed peer type exists and is already referenced.** The ObjC companion is a bgen-style binding project (ApiDefinition → `[Register]`-attributed `NSObject` subclasses), which is exactly what `Runtime.GetNSObject<T>` requires, and the Swift-side binding csproj already ProjectReferences + embeds it fail-closed (`BindingProjectEmitter.cs:393–438`, SWIFTBIND039; SDK-direct via `_ReferenceMixedObjCCompanion`, SWIFTBIND041). Both halves derive the same default C# namespace from the same `NamespacePatternResolver` inputs — with one silent-divergence edge: the ObjC pipeline appends a `Binding` suffix when a class name equals the namespace (`ObjCPipeline.cs:160–166`). Bridge records must consume the companion's *resolved* namespace, never recompute it.

3. **`NS_SWIFT_NAME` is parsed but never consumed — and it is the bridge's keying mechanism.** `ClangAstParser.ExtractSwiftName` (`ClangAstParser.cs:1457–1468`) captures `SwiftNameAttr` for classes, enums, methods, and properties into `SwiftName` fields that **no emitter reads** (zero grep hits across `ObjC/Emitter/`). This is precisely the `FBSDKAccessToken` ⇄ Swift-facing `AccessToken` mapping the bridge needs; the bridge becomes its first consumer. Gap: `ObjCProtocolDecl` and `ObjCCategoryDecl` have no `SwiftName` field at all (`ObjC/Model/ObjCDeclarations.cs:107–126, 212–226`) — must be added if/when protocols are bridged.

4. **`NS_TYPED_ENUM` has no C# type to bridge to — and Apple precedent says bridge it as `Foundation.NSString`.** Typed-enum typedefs are resolution-only aliases in the ObjC pass; their constants land in one flat `{Module}Constants` class as `[Field]` NSString properties (`StructsAndEnumsEmitter.cs:591–628`) with no type grouping. `apple-frameworks.json` resolves the same problem for Apple SDKs by remapping typed enums (`AVMediaType`, `CALayerContentsGravity`, `FileAttributeKey`, …) straight to `Foundation.NSString`. Phase 1 should mirror that — it un-drops `Settings.loggingBehaviors` / `LoginConfiguration.authType` as NSString-typed (usable, if weakly typed) API. A typed "smart enum" C# projection is a separate ObjC-*emitter* feature, deliberately not bridge scope.

5. **Records registered in the module's own database persist and propagate cross-module for free.** `ReadVersion1_0` requires only structural attributes (module, name, managedTypeName/NameSpace, frozen, requiresMemoryManagement); `mangledName` defaults to empty; `objcBridged`/`objcRooted`/`objcProtocol`/`simpleEnum`/`rawValueType` all exist in the schema (`TypeRecord.cs:12–93`). Critically, `ModuleDatabaseEmitter.Emit` serializes **all** records in the module database (`GetAllTypeRecords()`, `ModuleDatabaseEmitter.cs:32`), and the emitted `{Module}Database.xml` already ships in the package and threads to dependents via the `SwiftModuleDatabase` item → `_CollectSwiftModuleDatabases` → `--module-database` (`Sdk.targets:1591–1646`, `ConsumerTargetsEmitter.cs:237–244`). So if the ObjC-derived records live in the current module's own `ModuleTypeDatabase`, cross-module resolution (`FBSDKLoginKit` seeing `FBSDKCoreKit.AccessToken`) needs **zero new SDK plumbing**.

6. **`AppleFrameworkRegistry` is a dead end for this and should not be touched.** It loads a single embedded resource (`apple-frameworks.json`, ~90 hardcoded Apple modules) with no extension surface of any kind, and its remap target is *Microsoft's pre-existing .NET Apple-SDK bindings* — a different mechanism than our companion. The Apple-only pins in `TypeDatabaseExtensionsTests.cs:244–249, 851–855` guard the last-resort *fallback strategy*; the recommended approach never touches that strategy, so those pins stay valid as-is.

---

## Recommended approach — in-memory bridge registration, existing persistence

Synthesize `TypeRecord`s from the parsed `ObjCModule` **in-process** and register them into the current module's **own** `ModuleTypeDatabase` before Swift resolution runs. No new resolver strategy, no separate artifact, no new marshaler code.

1. **Split `ObjCPipeline.Run` into `Parse` → `FilterAndEmit`** (verified mechanical, sequencing section above). Run `Parse` before the Swift pass; `FilterAndEmit` stays where it is, after `swift-types.json`, so mixed dedup is untouched.
2. **A small factory walks the `ObjCModule` and synthesizes `TypeRecord`s**, mirroring `CreateObjCBridgedTypeRecord`'s proven shape:
   - **Swift-facing key:** `SwiftName ?? ObjC name` — the first consumer of the parsed `NS_SWIFT_NAME` data (fact 3).
   - **C# projection:** namespace = the companion's *resolved* namespace (shared `NamespacePatternResolver` result, including the `Binding`-suffix edge — fact 2); type name = the same name `ObjCTypeMapper.MapClassName` will emit (acronym-cased), never the raw ObjC name.
   - **Classes** → `Kind = Class`, `Flags = ObjCBridged | RequiresMemoryManagement`, `MetadataAccessor = ""`.
   - **NS_ENUM** → mirror the curated Apple enum-entry shape (`kind="enum"`, `simpleEnum`, `rawValueType`), pointing at the companion's emitted enum. Respect the documented kind trap: only genuine integral-raw-value enums get `kind="enum"`; **NS_OPTIONS imports into Swift as an OptionSet struct, not an enum — verify its import shape before including it in phase 1.**
   - **NS_TYPED_ENUM** → remap to `Foundation.NSString` (fact 4, Apple precedent).
3. **Register with Swift-wins conflict semantics** into the module's own database (the ObjC types *are* part of `FBSDKCoreKit` — same module name, same database; a parallel database would violate `AddModuleDatabase`'s one-database-per-module-name invariant). A Swift-owned `@objc` class re-exported through the umbrella header must resolve to its Swift record, never to a companion type that mixed dedup will exclude from emission. *Implementation checkpoint:* pick the registration seam — drain-in via the `_pendingCrossModuleRecords` queue at `AddModuleDatabase` time vs explicit post-parse registration with `ConflictPolicy.KeepExisting` — after checking which `ConflictPolicy` the Swift parser's own registration uses, so the "Swift wins" ordering is guaranteed rather than incidental.
4. **Resolution and marshalling then need no new code.** The `DatabaseCascade` strategies precede the last-resort `ObjCBridgingStrategy` in `TypeResolver.Default`, so the records resolve as ordinary database hits; `ObjCBridgedProjection` + `IsOptionalObjCBridged` + element conversions do the rest (fact 1).
5. **Cross-module rides the existing channel** (fact 5): `ModuleDatabaseEmitter` persists the registered records into `{Module}Database.xml` automatically; dependent modules load it exactly as today. Verify the dependent module's *compile* can see the dependency's companion assembly on all three consumption paths (the package `lib/` embed covers path a; confirm SDK-direct b and ProjectReference c).

### Superseded option sketches (for the record)

- **Option A — synthesize an ObjC module database XML, load it like a dependency.** Superseded because the XML round-trip is dead weight within a single invocation (the parsed `ObjCModule` is already in-process), a *separate* ObjC database artifact would need new SDK-targets plumbing to thread cross-module, and it collides with the one-database-per-module-name invariant — the ObjC types belong in the module's own database, at which point the existing persistence already does the cross-module job (fact 5).
- **Option B — a new per-binding `IResolutionStrategy` + manifest.** Superseded because database hits already precede the last-resort strategy (no new strategy needed for records that are *in* the database), and a per-binding manifest would be a second persistence format duplicating `{Module}Database.xml`. Extending `AppleFrameworkRegistry` instead is explicitly a dead end (fact 6).

### What the fix must get right

| ObjC decl | Swift-facing form | C# projection target | Risk |
|---|---|---|---|
| `@interface` class | `SwiftName ?? name` | companion `[Register]`'d NSObject subclass, via ObjCBridged/`IntPtr` | Low — the proven Apple path; highest value (`AccessToken`, `UserAgeRange`, `Location`) |
| `NS_ENUM` | imported Swift enum | companion `enum X : {underlying}`, by value | Medium — `kind="enum"` vs `"struct"` trap |
| `NS_OPTIONS` | imported OptionSet **struct** | companion `[Flags]` enum | Verify import shape before including |
| `NS_TYPED_ENUM` | String-backed struct | `Foundation.NSString` (Apple precedent) | Low, weakly typed by design in phase 1 |
| `@protocol` | `any P` | companion interface | **Phase 2** — no `SwiftName` field on `ObjCProtocolDecl`; ObjCProtocol-flag semantics unproven for companions |

- **Name mapping discipline.** Swift-facing side keys on `SwiftName ?? name`; C# side uses `ObjCTypeMapper`'s output and the companion's resolved namespace. Using the raw ObjC name on either side produces records that reference types that don't exist.
- **Ownership round-trip.** Swift-parsed records must beat ObjC-derived records (step 3), and `swift-types.json` dedup must still drop Swift-owned classes from ObjC emission. Fixture: an `@objc` Swift class visible through the umbrella header, referenced from another Swift member — must resolve to the Swift binding, not the (never-emitted) companion type.
- **Optional / collection positions.** The failing FBSDK sites are mostly `T?`, `Set<T>`, `[T]`. Scalar and `Optional` are metadata-free by construction (fact 1); container positions ride the per-element conversion path — cover all four positions in the fixture rather than assuming.
- **`IsOptionalObjCBridged` parity.** DB-registered records take its database branch, which is the parity-safe one; pin with a unit test rather than relying on the prefix heuristic ever firing for consumer modules.
- **Cross-module.** `LoginManagerLoginResult.token` (`FBSDKCoreKit.AccessToken` referenced from `FBSDKLoginKit`) must resolve via the dependency's shipped `{Module}Database.xml` *and* compile against the dependency's embedded companion assembly.
- **Acceptance metric.** Re-run the FBSDK re-spike and diff `binding-report.json`: public-type `AnyTypeFallback` (43) and placeholder-driven `UnsupportedSignature` (≈65) should collapse; track per-module emitted-% against the table above.

### Phasing

- **Phase 1 — classes + NS_ENUM + NS_TYPED_ENUM→NSString.** Covers every named FBSDK degradation in this doc (`AccessToken`, `AuthenticationToken`, `UserAgeRange`, `Location`, `AdvertisingTrackingStatus`, `LoggingBehavior`, `LoginAuthType`).
- **Phase 2 — ObjC protocols** (add `SwiftName` to `ObjCProtocolDecl`/`ObjCCategoryDecl`, prove ObjCProtocol-flag semantics against companion interfaces), **NS_OPTIONS** if its import shape needs distinct handling, and — as a separate ObjC-emitter feature, not bridge scope — a typed smart-enum C# projection for NS_TYPED_ENUM groups.

---

## The other public-type gaps (ranked, for completeness)

The bridge is the headline. The rest, in leverage order:

1. **Collections of existentials** — `[any AppLinkTargetProtocol]` on `AppLink`/`AppLinkNavigation` (`UnsupportedExistential`, 8 on public types). This is the **direct next increment** of the scalar `@objc`-existential work landed in `dd62b3bb` ("Route @objc class-bound existentials through reverse-dispatch receiver elements"): we handle `any P`, not yet `[any P]` / `Array<any P>` / `Optional<any P>`.
2. **Overload / naming dedup** (`DuplicateSignature`, 8) — `LoginConfiguration` init overloads that erase to the same C# signature (differ only by Swift argument labels); `SharePhoto.Source`/`ShareVideo.Source` where an enum's associated-value property name collides with the case name in C#. Mechanical naming-policy fixes.
3. **Reverse-dispatch proxies for protocols with static requirements** (`EveryProtocolConformanceSkipped` 46 + `StaticProtocolMember` 6) — cluster on DI/plumbing protocols (`DependentAs*`, `*Providing`, `*Creating`). Lower consumer value, partly structural.
4. **Niche** — generic methods with constraints we can't express (`UnsatisfiedGenericConstraint`, 2: `ApplicationDelegate.initializeSDK`), container-param-through-CallConvSwift ctor (`NonBlittableCallConvSwift`, 1: `FBLoginButton.init`).

### Explicitly out of scope

- **External framework types** — `UIKit.UIApplication?` on `ApplicationDelegate` lifecycle forwarders, and any other UIKit/AppKit/SwiftUI reference. Binding these would mean binding the external framework; not worth it for app-lifecycle methods a consumer rarely calls.
- **The ~72% internal / internal-reach surface** — `@usableFromInline internal` types and members. Not public API; correctly dropped. Do **not** chase the underscore-prefixed plumbing (`_BridgeAPI`, `_WebDialog`, `_FeatureManager`, …).

---

## Reproduction

The FBSDK binding artifacts live in the **`swift-dotnet-packages`** repo (separate from swift-bindings), under `libraries/Facebook/<Module>/`. The `obj/.../swift-binding/` outputs are build artifacts — regenerate with a normal build of each module's `*.Swift.iOS.csproj` if they've been wiped.

Skip-reason breakdown (per module, aggregated, and filtered to public types) is a pure read of the four `binding-report.json` files — `SkippedItems[]` each carry `{Kind, Name, ContainingType, Reason, Details}`. The emission-time coarser taxonomy is in the sibling `binding-emission-report.json` (`skipReasons` dict; `parent_module_internal` is the dominant emission bucket).

Evidence files (paths relative to `libraries/Facebook/FBSDKCoreKit/`):
- ObjC binding: `obj/Debug/net10.0-ios/swift-binding/ApiDefinition.cs` (`partial interface FBSDKAccessToken`), `StructsAndEnums.cs` (`enum FBSDKAdvertisingTrackingStatus`, `LoggingBehavior` `[Field]` constants).
- Swift binding: `obj/Debug/net10.0-ios/swift-binding/FBSDKCoreKit.cs` (`public object AccessTokenProvider` — the degradation).
- ObjC headers: `FBSDKCoreKit.xcframework/ios-arm64_arm64e/FBSDKCoreKit.framework/Headers/FBSDKAccessToken.h` etc.
- Swift interface: same framework, `Modules/FBSDKCoreKit.swiftmodule/arm64-apple-ios.swiftinterface`.

---

## Building the in-repo gate

Per project policy, a generator change like this ships with BindingTests coverage — the real ABI gate, not just unit tests. Add a minimal **mixed ObjC+Swift fixture** to `BindingTests/Sources/SwiftBindingsTestLib/` that reproduces the shape: an Objective-C class (with `NS_SWIFT_NAME` rename, so the keying path is exercised), an `NS_ENUM`, and an `NS_TYPED_ENUM`, each referenced from a Swift-interface member in scalar, `Optional`, `Set`, and `Array` positions — then assert the Swift-side member binds to the ObjC type (round-trips a value) instead of degrading to `object`. Include the ownership fixture: an `@objc` Swift class visible through the umbrella header, referenced from another Swift member, resolving to its Swift binding (not the companion). This is the same mixed-binding infrastructure exercised by `nuke binding-tests --mixed-pack` / `--mixed-direct`. Unit coverage for the record factory (SwiftName keying, namespace sharing, kind fidelity) and `IsOptionalObjCBridged` parity belongs in `TypeDatabaseExtensionsTests` / `MarshalPlanRegressionTests`; the existing Apple-only pins there stay untouched (fact 6).
