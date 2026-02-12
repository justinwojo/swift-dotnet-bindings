# Binding Errors by Library

Tracks compilation errors found when running real-world Swift libraries through the generator. Used to prioritize bug fixes and measure progress.

Last validated: 2026-02-12 (Validation Pass 4 fixes applied — all C-series bugs fixed).

## Baseline Libraries (0 generator errors)

| Library | Lines | Notes |
|---------|-------|-------|
| Nuke | 19,883 | Image loading, async/await, ObjC bridging, heavy protocol use |
| CryptoSwift | 27,994 | Value types, frozen structs, byte arrays |
| BlinkID | 50,554 | ObjC-heavy, delegates, callback-driven API |
| Mappedin | 48,574 | Indoor mapping, largest library tested, clean on first try |
| SmartCardIO | 3,915 | Smart card reader abstraction, clean build |
| BRLMPrinterKit | 53 | Mostly ObjC with thin Swift overlay |
| Lottie | 28,582 | Animation framework, protocol-heavy |
| Stripe | 449 | Top-level Stripe framework, minimal Swift surface |
| StripeApplePay | 1,880 | Apple Pay integration |
| StripeCardScan | 2,390 | Card scanning |
| StripeIdentity | 1,674 | Identity verification |
| StripeIssuing | 1,213 | Card issuing |
| Mixpanel | 6,453 | Analytics, protocol existentials |
| StripeCore | 31,295 | Core Stripe infrastructure |
| Alamofire | 34,677 | HTTP networking, closures, protocol composition |
| MicroblinkPlatform | 4,522 | Document scanning, SwiftUI theme types |
| StripeConnect | 11,632 | Stripe Connect integration |
| StripeCryptoOnramp | 5,973 | Crypto onramp, ObjC UIViewController |
| StripeFinancialConnections | 2,137 | Financial connections |
| StripePaymentSheet | 45,260 | Payment UI, constructor overloads |

## Libraries with Environmental Errors Only (0 generator errors)

### StripePayments (9 environmental errors, 88,291 lines)

| Count | CS Code | Category |
|-------|---------|----------|
| 5 | CS0023/CS0315 | ObjC enum types in UIKit (environmental) |
| 4 | CS1729 | Foundation.URL constructor mismatch (environmental) |

All 10 original generator errors (C6: duplicate methods + enum in callback) are fixed. Remaining errors are .NET iOS SDK gaps.

### StripeUICore (2 environmental errors, 28,619 lines)

| Count | CS Code | Category |
|-------|---------|----------|
| 2 | CS0234 | `UIKit.NSWritingDirection` not found (environmental) |

All 18 original generator errors (C8: enum `.Buffer` return) are fixed. Down from 22 total.

### SkeletonView (9 environmental errors, 11,718 lines)

| Count | CS Code | Category |
|-------|---------|----------|
| 9 | CS0234 | `UIKit.NSTextAlignment` not found (environmental) |

Down from 18 in previous validation — half the NSTextAlignment references no longer generated. All remaining errors are environmental.

### StripePaymentsUI (3 environmental errors, 12,881 lines)

| Count | CS Code | Category |
|-------|---------|----------|
| 3 | CS0234 | `UIKit.NSTextAlignment` not found (environmental) |

Down from 6 in previous validation. Same `NSTextAlignment` environmental issue.

### StripeCameraCore (12 environmental errors, 3,288 lines)

| Count | CS Code | Category |
|-------|---------|----------|
| 4 | CS0234 | `AVFoundation.AVCaptureDeviceDeviceType` not found (environmental) |
| 4 | CS0234 | `AVFoundation.AVCaptureDeviceAutoFocusRangeRestriction` not found (environmental) |
| 4 | CS0234 | `AVFoundation.AVCaptureSessionPreset` not found (environmental) |

All 2 original generator errors (C9: protocol proxy type mismatch) are fixed. Down from 14 total.

## Non-Binding Failures

### SkeletonView (wrapper compilation failure)

C# binding generation succeeds (11.7K lines), but Swift wrapper compilation fails because `SkeletonLayer` is an **internal** class referenced in wrapper code. The wrapper generator emits Swift code referencing this type, but `swiftc` compiling against the public interface can't see it.

**Fix approach**: `SwiftWrapperPostProcessor` should filter out wrapper functions that reference internal types.

### RealmSwift (generator crash)

The ABI JSON has an empty module name — built without `BUILD_LIBRARY_FOR_DISTRIBUTION=YES`. Generator throws `InvalidOperationException` with a clear error message about requiring library evolution.

### Realm (no Swift module)

Pure Objective-C framework — no Swift module found. Correctly rejected with user-friendly message.

### Stripe3DS2 (no Swift module)

Pure Objective-C framework — no Swift module found.

### ACSSmartCardIO (wrapper compilation failure)

C# bindings compile clean. Depends on `SmartCardIO` framework — wrapper compilation fails because `swiftc` can't find the dependency module.

**Fix approach**: Add `SwiftFrameworkDependency` item type for `-F` search paths (v2 feature).

### Mixpanel (wrapper compilation failure)

C# bindings compile clean (0 errors). Swift wrapper compilation fails because `ServerProxyResource` is not a public member of `Mixpanel.Mixpanel`. The swiftinterface references this type in a `#if compiler` block.

### Stripe sub-frameworks (wrapper compilation failures)

Most Stripe sub-frameworks fail wrapper compilation because they `import StripeCore` (or other Stripe modules) and `swiftc` can't find these dependencies. C# binding generation succeeds for all. Same root cause as ACSSmartCardIO — needs `-F` search path support.

Affected: Stripe, StripeApplePay, StripeCameraCore, StripeCardScan, StripeConnect, StripeCore, StripeCryptoOnramp, StripeFinancialConnections, StripeIdentity, StripeIssuing, StripePayments, StripePaymentSheet, StripePaymentsUI, StripeUICore.

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

- `UIKit.NSTextAlignment` missing from .NET iOS SDK (12 errors across SkeletonView + StripePaymentsUI)
- `UIKit.NSWritingDirection` missing from .NET iOS SDK (2 StripeUICore errors)
- `AVFoundation.AVCaptureDeviceDeviceType` / `AVCaptureDeviceAutoFocusRangeRestriction` / `AVCaptureSessionPreset` missing from .NET iOS SDK (12 StripeCameraCore errors)
- ObjC enum types / Foundation.URL constructor in .NET iOS SDK (9 StripePayments errors)

**Total: 35 environmental errors across 5 libraries** — all are .NET iOS SDK type gaps, not generator bugs.

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
