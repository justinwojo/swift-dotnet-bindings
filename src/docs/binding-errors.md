# Binding Errors by Library

Tracks compilation errors found when running real-world Swift libraries through the generator. Used to prioritize bug fixes and measure progress.

Last validated: 2026-02-17 (Validation Pass 5.1 — fixed 2 regressions from Pass 5).

## Baseline Libraries (0 generator errors)

| Library | Lines | Notes |
|---------|-------|-------|
| Alamofire | 41,050 | HTTP networking, closures, protocol composition |
| BlinkID | 53,755 | ObjC-heavy, delegates, callback-driven API |
| BRLMPrinterKit | 43 | Mostly ObjC with thin Swift overlay |
| CryptoSwift | 30,459 | Value types, frozen structs, byte arrays |
| Lottie | 30,299 | Animation framework, protocol-heavy |
| Mappedin | 48,636 | Indoor mapping, existential params, completion handlers |
| MicroblinkPlatform | 3,522 | Document scanning, SwiftUI theme types |
| Mixpanel | 6,760 | Analytics, protocol existentials |
| Nuke | 22,211 | Image loading, AsyncSequence properties, nested types |
| SkeletonView | 12,094 | UI skeleton loading, previously had 9 environmental errors (now resolved) |
| SmartCardIO | 4,514 | Smart card reader abstraction, clean build |

| Stripe | 471 | Top-level Stripe framework, minimal Swift surface |
| StripeApplePay | 2,027 | Apple Pay integration |
| StripeCameraCore | 3,544 | Camera capture, previously had 12 environmental errors (now resolved) |
| StripeCardScan | 2,593 | Card scanning |
| StripeConnect | 11,692 | Stripe Connect integration |
| StripeCore | 32,695 | Core Stripe infrastructure |
| StripeCryptoOnramp | 6,720 | Crypto onramp, ObjC UIViewController |
| StripeFinancialConnections | 2,382 | Financial connections |
| StripeIdentity | 1,759 | Identity verification |
| StripeIssuing | 1,362 | Card issuing |
| StripePayments | 91,961 | Payments core, previously had 9 environmental errors (now resolved) |
| StripePaymentSheet | 46,859 | Payment UI, constructor overloads |
| StripePaymentsUI | 13,167 | Payment UI components, previously had 3 environmental errors (now resolved) |
| StripeUICore | 29,350 | UI components, previously had 2 environmental errors (now resolved) |

## Libraries with Generator Errors

None. All 25 validated libraries compile at 0 generator errors as of Feb 17 (Pass 5.1).

## Libraries with Environmental Errors Only (0 generator errors)

No libraries currently have environmental-only errors. The 5 libraries that previously had environmental errors (SkeletonView, StripePayments, StripeUICore, StripePaymentsUI, StripeCameraCore) are now all at 0 errors as of Feb 17.

## Non-Binding Failures

### Alamofire (wrapper compilation failure — reduced to 1 error in Pass 5.1)

C# binding generation succeeds (41K lines, 0 compile errors), but Swift wrapper compilation fails due to `Alamofire.WebSocketTask` — an internal (non-public) type referenced in an async wrapper function. Same category as SkeletonView (`SkeletonLayer`) and Mixpanel (`ServerProxyResource`). C# bindings are validated via a standalone Test.csproj.

**Pass 5 → 5.1 fixes** (reduced from multiple swiftc errors to 1):
1. **Swift keyword escaping**: `protocol` (a Swift reserved word) used unescaped as a parameter name in `_optbuf` wrapper, producing `let protocolVal = protocol.assumingMemoryBound(...)` which is invalid Swift. Fixed by adding `NameProvider.EscapeSwiftKeyword()` — now emits `` `protocol` `` with backticks. Applied in `OptionalPointerWrapperEmitter.cs` and `ClosureEmitter.SwiftWrapper.cs`.
2. **Raw generic type parameters**: `τ_0_0`, `τ_1_0` etc. (ABI-level names) emitted in extension wrapper blocks. These are never valid Swift identifiers. Fixed by adding `ContainsRawGenericParam` regex (`τ_\d+_\d+`) to `SwiftWrapperPostProcessor` — blocks containing these are now stripped.

**Test coverage**: 29 new tests harden both fixes — 7 post-processor τ-stripping tests (`PostProcessorRawGenericParamTests`), 15 `IsSwiftKeyword`/`EscapeSwiftKeyword` tests in `NameProviderParameterTests`, and 3 keyword-escaped emission tests in `OptionalPointerWrapperTests` (verifying backtick output + dereference line).

### SkeletonView (wrapper compilation failure)

C# binding generation succeeds (12K lines, 0 compile errors), but Swift wrapper compilation fails because `SkeletonLayer` is an **internal** class referenced in wrapper code. The wrapper generator emits Swift code referencing this type, but `swiftc` compiling against the public interface can't see it. C# bindings are validated via a standalone Test.csproj.

**Fix approach**: `SwiftWrapperPostProcessor` should filter out wrapper functions that reference internal types.

### RealmSwift (generator crash)

The ABI JSON has an empty module name — built without `BUILD_LIBRARY_FOR_DISTRIBUTION=YES`. Generator throws `InvalidOperationException` with a clear error message about requiring library evolution.

### Realm (no Swift module)

Pure Objective-C framework — no Swift module found. Correctly rejected with user-friendly message.

### Stripe3DS2 (no Swift module)

Pure Objective-C framework — no Swift module found.

### ACSSmartCardIO (wrapper compilation failure + NuGet dependency)

C# bindings generate (2,994 lines) but the emitted `.csproj` references `SmartCardIO.Swift.iOS` NuGet package which doesn't exist, causing NU1101 on build. Wrapper compilation also fails because `swiftc` can't find the dependency module.

**Fix**: Use `--framework-dependency /path/to/SmartCardIO.xcframework` (CLI) or `<SwiftFrameworkDependency Include="../SmartCardIO.xcframework" />` (MSBuild SDK). Both are now implemented. The NuGet dependency error would resolve when a SmartCardIO NuGet package is published.

### Mixpanel (wrapper compilation failure)

C# bindings compile clean (0 errors). Swift wrapper compilation fails because `ServerProxyResource` is not a public member of `Mixpanel.Mixpanel`. The swiftinterface references this type in a `#if compiler` block.

### Stripe and StripeCardScan (NuGet dependency errors)

C# bindings compile clean (0 CS errors). The generated `.csproj` references inter-module NuGet packages (`StripeCore.Swift.iOS`, `StripeApplePay.Swift.iOS`, etc.) that don't exist, causing NU1101 on build. C# bindings are validated via standalone Test.csproj (with `DisableRuntimeMarshallingAttribute`).

### Stripe sub-frameworks (wrapper compilation failures)

Most Stripe sub-frameworks fail wrapper compilation because they `import StripeCore` (or other Stripe modules) and `swiftc` can't find these dependencies. C# binding generation succeeds for all.

**Fix**: Use `--framework-dependency` (repeatable) to provide each dependency xcframework, or `<SwiftFrameworkDependency>` items in the MSBuild SDK. Both are now implemented. Example:
```bash
dotnet run --project $PROJ -- --xcframework StripePaymentSheet.xcframework \
  --framework-dependency ../StripeCore.xcframework \
  --framework-dependency ../StripeUICore.xcframework \
  -o /tmp/output/
```

Affected: Stripe, StripeApplePay, StripeCameraCore, StripeCardScan, StripeConnect, StripeCore, StripeCryptoOnramp, StripeFinancialConnections, StripeIdentity, StripeIssuing, StripePayments, StripePaymentSheet, StripePaymentsUI, StripeUICore.

## Validation Pass 5 (2026-02-17) — re-validation

Re-validated all 25 libraries after Session F (Foundation auto-bridge, UnsafePointer bound-generic fix) and Session B (3 marshaler bug fixes).

**Environmental errors eliminated (35 → 0):**
- **SkeletonView**: 9 `UIKit.NSTextAlignment` errors → **0 errors**
- **StripePayments**: 9 environmental errors (ObjC enums + Foundation.URL) → **0 errors**
- **StripeCameraCore**: 12 `AVFoundation.AVCapture*` errors → **0 errors**
- **StripePaymentsUI**: 3 `UIKit.NSTextAlignment` errors → **0 errors**
- **StripeUICore**: 2 `UIKit.NSWritingDirection` errors → **0 errors**

**Regressions (0 → 4 generator errors across 2 libraries):**
- **Nuke**: 0 → **2 generator errors** (CS0102). AsyncSequence auto-bridge emits `Progress` property that collides with nested `Progress` type.
- **Mappedin**: 0 → **2 generator errors** (CS0234). `AnyObject` parameter projected as `Swift.AnyObject` which doesn't exist in runtime.

**Line count increases** across most libraries (Foundation auto-bridge emitting more bindings): Alamofire +6,373, StripePayments +3,670, BlinkID +3,201, CryptoSwift +2,465, Nuke +2,328, Lottie +1,717, StripePaymentSheet +1,599, StripeCore +1,400, StripeCryptoOnramp +747, StripeUICore +731, SmartCardIO +599, SkeletonView +376, Mixpanel +307, StripePaymentsUI +286, StripeCameraCore +256, StripeFinancialConnections +245, StripeCardScan +203, StripeApplePay +147, StripeIssuing +149, StripeIdentity +85, Mappedin +62, StripeConnect +60, Stripe +22.

**Line count decreases**: MicroblinkPlatform -1,000 (Foundation auto-bridge likely consolidated some types), BRLMPrinterKit -10.

**Net: 23 of 25 libraries at 0 generator errors. 2 regressions to fix. All 35 environmental errors resolved.**

## Validation Pass 5.1 (2026-02-17) — regression fixes

Fixed both Pass 5 generator regressions + improved Alamofire wrapper compilation. 29 new unit tests added.

**Generator regression fixes (4 errors → 0, plus 15 unmasked pre-existing errors):**
- **Nuke** (CS0102, 2 errors): AsyncSequence auto-bridge `Progress` property collided with nested `Progress` type in `ImageTask`. Root cause: `ComputeAndApplyNestedTypeRenames` excluded AsyncStream properties from the collision set because `HasUnsupportedPropertyType` treats `_Concurrency.AsyncStream` as unsupported module. Fix: added AsyncStream property names to collision set in `NameProvider.cs` so `Progress` property triggers rename of nested type to `ProgressInfo`.
- **Mappedin** (CS0234, 2 errors → fixed, then 15 unmasked CS1503 → fixed): `AnyObject` parameter projected as `Swift.AnyObject`. Three-part fix:
  1. **TypeDatabaseExtensions.cs**: Added `Swift.Any`/`Swift.AnyObject` → AnyType mapping in `TryGetTypeRecord` and `GetTypeRecordOrThrow`. These protocol types are module-qualified so they don't match `IsExistentialTypeName` and aren't in the type database.
  2. **MethodHandler.cs** (async overload params): Completion handler overload `TryEmitCompletionHandlerOverload` re-resolves types from SwiftTypeSpec. AnyObject params use `ProtocolListTypeSpec` (not `NamedTypeSpec`), so the TypeDB fix didn't apply. Added existential handling via `ExistentialHandler.GetPublicExistentialType` → `object`.
  3. **MethodHandler.cs** (completion result type guard): Fixing CS0234 unmasked 15 pre-existing CS1503 errors — completion handler overloads where the closure handler's callback type (`SwiftOptional<SwiftString>`, `SwiftArray<ExistentialContainer1>`) doesn't match the TCS result type (`string?`, `IEnumerable<AnyType>`). C# doesn't chain implicit conversions, so `SwiftOptional<SwiftString>` → `string?` fails. Added `IsCompletionResultCompatible` guard that compares closure handler type with TCS type; incompatible overloads are now skipped.
- **Unit test fix**: `CompletionHandler_ResultWithError_EmitsErrorBranch` changed from `Optional<String>` to `Optional<Int>` (primitive type where closure handler produces `nint?` matching TCS type).

**Wrapper compilation improvements — Alamofire** (multiple swiftc errors → 1):
1. **Swift keyword escaping** — `protocol` used as parameter name without backtick escaping in `_optbuf` wrappers. Added `NameProvider.EscapeSwiftKeyword()` (`NameProvider.cs`) and applied in `OptionalPointerWrapperEmitter.cs` + `ClosureEmitter.SwiftWrapper.cs`. Consolidated duplicate `_swiftKeywords` from `EveryProtocolEmitter.cs` into `NameProvider`.
2. **Raw generic type parameters** — `τ_0_0` ABI names in extension wrappers. Added `ContainsRawGenericParam` regex to `SwiftWrapperPostProcessor.cs` (checks `IsSilgenNameBroken`, `IsExtensionBroken`, `IsStandaloneFuncBroken`).
3. **Remaining**: `Alamofire.WebSocketTask` internal type reference (same category as SkeletonView/Mixpanel).

**Test coverage (29 new tests):**
- `PostProcessorRawGenericParamTests` — 7 tests: τ stripping across all 3 block patterns (silgen_name, extension, standalone func) + mixed/clean preservation
- `NameProviderParameterTests` — 15 tests: `IsSwiftKeyword` (10 keywords), `EscapeSwiftKeyword` (backtick addition + passthrough for 5 non-keywords), empty string edge case
- `OptionalPointerWrapperTests` — 3 tests: keyword param emits backticks in Swift wrapper, non-keyword has none, dereference line uses escaped name on RHS + safe suffixed name on LHS

**Net: 25 of 25 libraries at 0 generator errors. Alamofire wrapper failure reduced to single internal-type reference. Unit tests: 3171 (up from 3142).**

## Fixed Bug Patterns

### Validation Pass 4 (2026-02-12) — 166+ errors fixed

| ID | Pattern | Errors Fixed | Libraries | Fix |
|----|---------|-------------|-----------|-----|
| C1 | Optional tuple with unsupported closure element | 2 | Alamofire | Recursive `ContainsUnsupportedTupleElement` helper in `CanEmitProperty` detects closures inside Optional-wrapped tuples |
| C2 | SwiftUI/closure/AnyType property not skipped | 108 | MicroblinkPlatform | Wired `CanEmitProperty()` gate into all 4 type handlers (ClassHandler, FrozenStructHandler, NonFrozenStructHandler, EnumHandler) |
| C3 | DateTimeOffset in SwiftObjectHelper | 4 | StripeConnect | Extended `IsNonSwiftObjectMappedType()` to detect non-Swift module types mapped to `System.*` namespace |
| C4 | ObjC-bridged type in async copy buffer | 18 | StripeCryptoOnramp | Added ObjC-bridged exclusion to `WrapperEmitter.Async.cs` nonFrozenParams filter |
| C5 | Duplicate parameter name `result` | 2 | StripeFinancialConnections | Extended `DeduplicateParameterNames` coverage to missing emission path |
| C6 | Duplicate method + enum in callback | 10 | StripePayments | Strengthened `HasSignatureCollision` to check projected param types + extended enum callback guard |
| C7 | Duplicate constructor after normalization | 2 | StripePaymentSheet | Constructor dedup via `GetProjectedCSharpMethodKey` with type-aware collision detection |
| C8 | Optional\<NonSimpleEnum\> return type | 18 | StripeUICore | Extended B18 check to unwrap Optional and inspect inner enum in `CanEmitProperty`/`CanEmitMethod` |
| C9 | Protocol proxy interface param mismatch | 2 | StripeCameraCore | Aligned `ProtocolProxyEmitter.GetCSharpTypeName` with `ProtocolHandler.GetCSharpTypeName` for closure param types |
| C9b | Composition proxy idiomatic type mismatch | 4 | CryptoSwift | Added `GetIdiomaticCSharpType` call in `ModuleHandler.ResolveCSharpTypeName` for nested types |
| C10 | Enum case variable shadowing | 3 | StripeFinancialConnections | Renamed local `result` to `__enumResult` in `EnumHandler.CaseConstruction` when parameter collision detected |
| C11 | Optional\<Closure\> vs bare Closure dedup | 1 | StripePayments | Unwrap `Optional<ClosureTypeSpec>` in `GetProjectedCSharpMethodKey` — nullable refs don't affect overload resolution |
| C12 | Optional\<Array\<T\>\> missing generic arg + closure return guard | 7 | StripePaymentSheet | Conditional `fullArrayType` build in `GetParameterConversion` + `void*` closure return guard in `CanEmitProperty` |
| **Total** | | **181** | | |

**Codex review fixes (P0, P1):**
- **P0**: `ProtocolProxyEmitter.GetCSharpTypeName` gained `forAbiMarshalling` parameter — `MarshalFromSwift<T>` calls now use ABI types (SwiftString) instead of idiomatic types (string) that would corrupt runtime memory
- **P1**: `DefaultParameterOverloadEmitter.GetProjectedOverloadKey` now unwraps `Optional<Closure>` to match main pass C11 fix

### Validation Pass 3 (2026-02-12) — 228 errors fixed

| ID | Pattern | Errors Fixed | Libraries | Fix |
|----|---------|-------------|-----------|-----|
| B5 | Optional tuple with existential element | 4 | Alamofire | `HasNonSwiftObjectGenericArg` extended to check tuple elements inside Optional for unresolvable existentials |
| B6 | Dictionary existential generic arg mismatch | 20 | Mixpanel | `TryGetFirstExistentialTypeArgument` guard in MethodHandler + MemberEmissionValidator for non-Array bound generics (Array<any P> allowed — has dedicated marshalling) |
| B7 | Closure thunk return void* vs struct | 4 | Mixpanel | `IsSupportedClosureReturnType` rejects bound generic returns with `RequiresMemoryManagement` inner types |
| B8 | `void` as generic type arg (Result<Void,Error>) | 30 | StripePaymentSheet | `Swift.Void` → `SwiftVoid` mapping extended to `ClosureHandler.TranslateBoundGenericToCSharp` |
| B9 | Existential→interface in proxy receiver | 2 | StripeCore | Protocol methods with existential params added to `_skippedMethodKeys` |
| B10 | Protocol proxy receiver type asymmetry | 4 | StripeCore (2), StripeCameraCore (2) | `GetReturnConversion` applied after unmarshalling in receiver to convert ABI→idiomatic type |
| B11 | DateTimeOffset in SwiftObjectHelper | 4 | StripeConnect | `HasNonSwiftObjectGenericArg` checks TypeRecord `NativeTypeName` for .NET-mapped types |
| B12 | ObjC-bridged type treated as Swift class | 18 | StripeCryptoOnramp | Existing `IsObjCBridgedType` guards now catch module-qualified ObjC types; belt-and-suspenders guard in `EmitTypeConversions` for Optional<ObjC> |
| B13 | Async closure arity mismatch | 2 | StripeCryptoOnramp | `IsSupportedClosure` rejects async+throwing closures with parameters |
| B14 | Duplicate P/Invoke parameter name | 2 | StripeFinancialConnections | `DeduplicateParameterNames` with HashSet-based collision avoidance |
| B15 | Duplicate async method after normalization | 6 | StripePayments | Secondary dedup in `HandleBaseDecl` based on projected C# public method signature |
| B16 | Non-blittable enum in UnmanagedCallersOnly | 4 | StripePayments | `IsSupportedClosureParameterType` rejects enum types |
| B17 | INSObject composition for ObjC root type | 2 | StripePayments | `GetCompositionInterfaceName` filters out ObjC root protocols |
| B18 | Enum .Buffer return type doesn't exist | 18 | StripeUICore | `CanEmitMethod`/`CanEmitProperty` skip non-simple enum returns requiring memory management |
| B19 | SwiftUI namespace in main binding | 108 | MicroblinkPlatform | `ReferencesUnsupportedModule` member-level check in `CanEmitMethod`/`CanEmitProperty` |
| **Total** | | **228** | | |

### Validation Pass 2 (2026-02-12) — 35 errors fixed

| Bug Pattern | Errors Fixed | Libraries | Fix |
|-------------|-------------|-----------|-----|
| B3 gap: `Swift.Void` as NamedTypeSpec | 15 | StripePaymentSheet | `NamedTypeSpec("Swift.Void")` → `SwiftVoid` mapping in BoundGenericsHandler |
| A4: Bare generic types | 6 | Alamofire (4), Mixpanel (2) | Two-layer bare generic detection: module-local TypeDecl lookup + stdlib fallback set |
| Generic constraint mismatch | 8 | StripeCore (4), SkeletonView (4) | Context-aware `HasNonSwiftObjectGenericArg` guard: blocks tuples (except Optional) and ObjC-bridged types |
| A6: AnyType type erasure dedup | 2 | Alamofire | Three-layer protocol method dedup: Swift signature → projected C# → emitted resolution |
| Duplicate `_` parameters | 4 | Lottie | `GetCSharpParameterName` derives name from type for `_` params + `DeduplicateParameterNames` in protocol emission |

## Remaining Environmental (out of scope)

**All 35 environmental errors from Feb 12 are now resolved.** The 5 libraries that previously had environmental errors (SkeletonView, StripePayments, StripeUICore, StripePaymentsUI, StripeCameraCore) now compile clean. Generator changes eliminated the references to missing .NET iOS SDK types (`UIKit.NSTextAlignment`, `UIKit.NSWritingDirection`, `AVFoundation.AVCapture*`, ObjC enum types).

---

## How to Validate Libraries

### Prerequisites

Build the generator first — all commands below assume you're in the repo root (`/Users/wojo/Dev/swift-bindings`):

```bash
./build.sh
```

The generator is invoked via `dotnet run --project src/Swift.Bindings/src/Swift.Bindings.csproj`. It must be run **sequentially** (not in parallel) because `dotnet run` takes a build lock on the project.

### Library locations

| Location | Libraries |
|----------|-----------|
| `BindingTesting/Nuke/Nuke.xcframework` | Nuke |
| `BindingTesting/Lottie/Lottie.xcframework` | Lottie |
| `BindingTesting/BlinkId/BlinkID.xcframework` | BlinkID |
| `BindingTesting/CryptoSwift/CryptoSwift.xcframework` | CryptoSwift |
| `/Users/wojo/Dev/Libraries/Mappedin.xcframework` | Mappedin |
| `/Users/wojo/Dev/Libraries/SmartCardIO.xcframework` | SmartCardIO |
| `/Users/wojo/Dev/Libraries/BRLMPrinterKit.xcframework` | BRLMPrinterKit |
| `/Users/wojo/Dev/Libraries/ACSSmartCardIO.xcframework` | ACSSmartCardIO |
| `/Users/wojo/Dev/Libraries/MicroblinkPlatform.xcframework` | MicroblinkPlatform |
| `/Users/wojo/Dev/Libraries/Alamofire/Alamofire.xcframework` | Alamofire |
| `/Users/wojo/Dev/Libraries/mixpanel-swift-5.2.0/Mixpanel.xcframework` | Mixpanel |
| `/Users/wojo/Dev/Libraries/SkeletonView/SkeletonView.xcframework` | SkeletonView |
| `/Users/wojo/Dev/Libraries/Stripe.xcframework/{Name}.xcframework` | Stripe family (14 frameworks) |

**Skip these** — they don't have Swift modules or are known-broken inputs:
- `Realm.xcframework` — pure ObjC, no Swift module
- `RealmSwift.xcframework` — empty module name (not built with library evolution)
- `Stripe3DS2.xcframework` — pure ObjC, no Swift module

### Validate a single library

```bash
PROJ="src/Swift.Bindings/src/Swift.Bindings.csproj"

# 1. Generate bindings
mkdir -p /tmp/binding-validation/Nuke
dotnet run --project $PROJ -- \
  --xcframework BindingTesting/Nuke/Nuke.xcframework \
  -o /tmp/binding-validation/Nuke

# 2. Compile the generated bindings
#    If a .csproj was emitted (wrapper compilation succeeded):
dotnet build /tmp/binding-validation/Nuke/Nuke.Swift.iOS.csproj \
  -p:EnableDefaultCompileItems=false

#    If NO .csproj was emitted (wrapper compilation failed — e.g. Mixpanel, SkeletonView):
#    Create a minimal test project, then build it:
cat > /tmp/binding-validation/Mixpanel/Test.csproj << 'EOF'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Library</OutputType>
    <TargetFramework>net10.0-ios</TargetFramework>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <Nullable>enable</Nullable>
    <NoWarn>CS0169</NoWarn>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Swift.Runtime" Version="0.1.0-preview.1" />
  </ItemGroup>
</Project>
EOF
dotnet build /tmp/binding-validation/Mixpanel/Test.csproj

# 3. Check results
dotnet build ... 2>&1 | grep "Error(s)"          # Quick pass/fail
dotnet build ... 2>&1 | grep "error CS"           # List individual errors
dotnet build ... 2>&1 | grep "error CS" | \
  sed 's/.*error //' | sort | uniq -c | sort -rn  # Categorize by error type
wc -l /tmp/binding-validation/Nuke/Swift.Nuke.cs  # Line count
```

**Why `-p:EnableDefaultCompileItems=false`?** The generated `.csproj` explicitly lists `<Compile>` items, but the .NET SDK also auto-includes `*.cs` files, causing NETSDK1022 (duplicate compile items). This flag disables auto-inclusion. The MSBuild SDK mode (`Sdk.targets`) avoids this issue because it adds `<Compile>` items dynamically rather than through the project file.

### Validate all libraries (full batch)

Run each library sequentially through the generator, then compile. This is the script pattern used for each validation pass:

```bash
PROJ="src/Swift.Bindings/src/Swift.Bindings.csproj"
rm -rf /tmp/binding-validation

# --- BindingTesting libraries ---
for lib in Nuke Lottie BlinkID CryptoSwift; do
  echo "=== Generating $lib ==="
  mkdir -p "/tmp/binding-validation/$lib"
  case "$lib" in
    BlinkID) xcf="BindingTesting/BlinkId/BlinkID.xcframework" ;;
    *) xcf="BindingTesting/$lib/$lib.xcframework" ;;
  esac
  dotnet run --project "$PROJ" -- --xcframework "$xcf" -o "/tmp/binding-validation/$lib" 2>&1 | tail -3
done

# --- /Dev/Libraries standalone ---
LIBDIR="/Users/wojo/Dev/Libraries"
for lib in Mappedin SmartCardIO BRLMPrinterKit ACSSmartCardIO MicroblinkPlatform; do
  echo "=== Generating $lib ==="
  mkdir -p "/tmp/binding-validation/$lib"
  dotnet run --project "$PROJ" -- --xcframework "$LIBDIR/$lib.xcframework" -o "/tmp/binding-validation/$lib" 2>&1 | tail -3
done

# --- /Dev/Libraries subdirectory ---
echo "=== Generating Alamofire ===" && mkdir -p /tmp/binding-validation/Alamofire
dotnet run --project "$PROJ" -- --xcframework "$LIBDIR/Alamofire/Alamofire.xcframework" -o /tmp/binding-validation/Alamofire 2>&1 | tail -3

echo "=== Generating Mixpanel ===" && mkdir -p /tmp/binding-validation/Mixpanel
dotnet run --project "$PROJ" -- --xcframework "$LIBDIR/mixpanel-swift-5.2.0/Mixpanel.xcframework" -o /tmp/binding-validation/Mixpanel 2>&1 | tail -3

echo "=== Generating SkeletonView ===" && mkdir -p /tmp/binding-validation/SkeletonView
dotnet run --project "$PROJ" -- --xcframework "$LIBDIR/SkeletonView/SkeletonView.xcframework" -o /tmp/binding-validation/SkeletonView 2>&1 | tail -3

# --- Stripe family ---
STRIPEDIR="$LIBDIR/Stripe.xcframework"
for lib in Stripe StripeApplePay StripeCameraCore StripeCardScan StripeConnect StripeCore \
           StripeCryptoOnramp StripeFinancialConnections StripeIdentity StripeIssuing \
           StripePayments StripePaymentSheet StripePaymentsUI StripeUICore; do
  echo "=== Generating $lib ==="
  mkdir -p "/tmp/binding-validation/$lib"
  dotnet run --project "$PROJ" -- --xcframework "$STRIPEDIR/$lib.xcframework" -o "/tmp/binding-validation/$lib" 2>&1 | tail -3
done
```

Then compile everything and summarize:

```bash
# For libraries whose wrapper compilation failed (no .csproj emitted),
# create a minimal test project so we can still compile-check the C# bindings.
for lib in $(ls /tmp/binding-validation/); do
  if [ ! -f "/tmp/binding-validation/$lib/"*.csproj ] 2>/dev/null; then
    csfile=$(ls /tmp/binding-validation/$lib/Swift.*.cs 2>/dev/null | head -1)
    [ -z "$csfile" ] && continue
    cat > "/tmp/binding-validation/$lib/Test.csproj" << 'EOF'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Library</OutputType>
    <TargetFramework>net10.0-ios</TargetFramework>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <Nullable>enable</Nullable>
    <NoWarn>CS0169</NoWarn>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Swift.Runtime" Version="0.1.0-preview.1" />
  </ItemGroup>
</Project>
EOF
  fi
done

# Compile and summarize
for lib in $(ls /tmp/binding-validation/ | sort); do
  csproj=$(ls /tmp/binding-validation/$lib/*.csproj 2>/dev/null | head -1)
  [ -z "$csproj" ] && continue
  result=$(dotnet build "$csproj" -p:EnableDefaultCompileItems=false 2>&1)
  errors=$(echo "$result" | grep "Error(s)" | head -1 | awk '{print $1}')
  lines=$(wc -l < "/tmp/binding-validation/$lib/Swift.$lib.cs" 2>/dev/null || echo "?")
  if [ "$errors" = "0" ]; then
    echo "✓ $lib — 0 errors ($lines lines)"
  else
    echo "✗ $lib — $errors errors ($lines lines)"
    echo "$result" | grep "error CS" | sed 's/.*error /  /' | sort -u
  fi
done
```

### Tips

- **Run generation sequentially.** `dotnet run --project` takes a build lock; parallel invocations will fail or queue. Compilation (`dotnet build` on the output) can be parallelized.
- **Always run from repo root.** The working directory matters for `dotnet run --project` — relative paths to the generator project resolve from cwd.
- **Wrapper compilation failures are expected** for libraries with inter-module dependencies (all Stripe sub-frameworks, ACSSmartCardIO, Mixpanel, SkeletonView). The C# bindings are still generated and should be compile-checked.
- **Distinguish generator errors from environmental errors.** CS0234 for `UIKit.NSTextAlignment`, `UIKit.NSWritingDirection`, and `AVFoundation.AVCapture*` types are .NET iOS SDK gaps, not generator bugs. These are documented in the "Remaining Environmental" section.
- **Clean up** when done: `rm -rf /tmp/binding-validation`
