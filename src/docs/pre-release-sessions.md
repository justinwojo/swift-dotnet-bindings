# Pre-Release Sessions (0.5.0)

**Created**: March 30, 2026
**Goal**: Maximize API coverage across validation libraries before 0.5.0 release.
**Data source**: binding-report.json across 15 sim-validation libraries: 2,850 bound members, 806 skipped (22% skip rate).

**Execution**: Follow `/Users/wojo/Dev/session-orchestrator-prompt.md`. Sessions are sequential — each may depend on the prior.

---

## Session 1: Foundation TypeDatabase Expansion — COMPLETE (83e349b5)

> **Deviation**: JSONEncoder and Decimal entries omitted (worker reported intentional). 6 of 8 types added. Alamofire, XMLCoder, GRDB all improved. Zero regressions.

**Target**: UnsupportedType — 52 skips across 15 libraries (6.4% of all skips)

**Problem**: The generator skips members when it can't resolve a Swift type. Many common Foundation/ObjC types just need XML TypeDatabase entries. These types are listed in `apple-frameworks.json` under `valueTypes` (preventing auto-bridging) but are NOT in any XML database — so they fall back to AnyType.

### Background: How TypeDatabase XML Works

**XML files live at**: `src/Swift.Bindings.Sdk/tools/net10.0/any/Swift/`

Existing databases: `FoundationDatabase.xml`, `CoreGraphicsDatabase.xml`, `UIKitDatabase.xml`, `SecurityDatabase.xml`, `AVFoundationDatabase.xml`, `SwiftUIDatabase.xml`

**Schema (v1.0)**:
```xml
<?xml version="1.0" encoding="utf-8"?>
<swifttypedatabase version="1.0" moduleName="ModuleName" modulePath="/System/Library/Frameworks/...">
    <entities>
        <entity managedNameSpace="CSharpNamespace" managedTypeName="CSharpTypeName">
            <typedeclaration kind="struct|class|enum|protocol" name="SwiftName" module="SwiftModule"
                             mangledName="..." frozen="true|false" requiresMemoryManagement="true|false"
                             objcBridged="true" nativeType="..." rawValueType="..." simpleEnum="true" />
        </entity>
    </entities>
</swifttypedatabase>
```

**Key attributes**:
- `kind` — "struct", "class", "enum", "protocol", "existential"
- `name` — Swift type name (supports nested: "NSNotification.Name")
- `module` — Swift module
- `mangledName` — Swift ABI mangled name (empty string for ObjC types)
- `frozen` — layout known at compile time
- `requiresMemoryManagement` — heap-allocated or contains references
- `objcBridged` — ObjC class wrapper
- `nativeType` — remaps public API to ObjC type (e.g., Foundation.NSUrl)
- `rawValueType` — "Int", "UInt", "String" (for enums)
- `simpleEnum` — no associated values, frozen, integral

**CRITICAL constraint** from `constraints.md`: XML value-type remap entries must use `kind="struct"` (NOT enum). Only genuine ObjC enums with `rawValueType` should be `kind="enum"`.

**Resolution path**: TypeDatabase.TryGetTypeRecord() → module aliases → IsObjCModuleType() synthetic → AnyType fallback. Adding XML entries catches types at step 1.

**Parsed by**: `src/Swift.Bindings/src/TypeDatabase/TypeDatabase.cs` (ReadVersion1_0 method)

### Deliverables

#### 1. Add entries to FoundationDatabase.xml

Add these to `src/Swift.Bindings.Sdk/tools/net10.0/any/Swift/FoundationDatabase.xml`:

```xml
<!-- NSNotification.Name — String-backed struct typedef (8 skips across Alamofire alone) -->
<entity managedNameSpace="Foundation" managedTypeName="NSNotificationName">
    <typedeclaration kind="struct" name="NSNotification.Name" module="Foundation"
                     frozen="true" requiresMemoryManagement="false" />
</entity>

<!-- JSONEncoder — ObjC-bridged NSObject subclass -->
<entity managedNameSpace="Foundation" managedTypeName="NSJsonEncoder">
    <typedeclaration kind="class" name="JSONEncoder" module="Foundation"
                     mangledName="" frozen="false" requiresMemoryManagement="true" objcBridged="true" />
</entity>

<!-- CharacterSet — bridges to NSCharacterSet -->
<entity managedNameSpace="Foundation" managedTypeName="NSCharacterSet">
    <typedeclaration kind="class" name="CharacterSet" module="Foundation"
                     mangledName="" frozen="false" requiresMemoryManagement="true" objcBridged="true" />
</entity>

<!-- Calendar — bridges to NSCalendar -->
<entity managedNameSpace="Foundation" managedTypeName="NSCalendar">
    <typedeclaration kind="class" name="Calendar" module="Foundation"
                     mangledName="" frozen="false" requiresMemoryManagement="true" objcBridged="true" />
</entity>

<!-- Decimal — bridges to NSDecimalNumber -->
<entity managedNameSpace="Foundation" managedTypeName="NSDecimalNumber">
    <typedeclaration kind="struct" name="Decimal" module="Foundation"
                     frozen="false" requiresMemoryManagement="false" />
</entity>
```

#### 2. Add CGBlendMode to CoreGraphicsDatabase.xml

Add to `src/Swift.Bindings.Sdk/tools/net10.0/any/Swift/CoreGraphicsDatabase.xml`:

```xml
<!-- CGBlendMode — integer enum -->
<entity managedNameSpace="CoreGraphics" managedTypeName="CGBlendMode">
    <typedeclaration kind="enum" name="CGBlendMode" module="CoreGraphics"
                     frozen="true" simpleEnum="true" requiresMemoryManagement="false" rawValueType="Int" />
</entity>
```

#### 3. Create CoreMediaDatabase.xml

Create `src/Swift.Bindings.Sdk/tools/net10.0/any/Swift/CoreMediaDatabase.xml`:

```xml
<?xml version="1.0" encoding="utf-8"?>
<swifttypedatabase version="1.0" moduleName="CoreMedia" modulePath="/System/Library/Frameworks/CoreMedia.framework/CoreMedia">
    <entities>
        <!-- CMTime — opaque C struct, map to IntPtr for now -->
        <entity managedNameSpace="System" managedTypeName="IntPtr">
            <typedeclaration kind="struct" name="CMTime" module="CoreMedia"
                             frozen="true" requiresMemoryManagement="false" />
        </entity>
    </entities>
</swifttypedatabase>
```

#### 4. Add SecTrustResultType to SecurityDatabase.xml

Add to `src/Swift.Bindings.Sdk/tools/net10.0/any/Swift/SecurityDatabase.xml`:

```xml
<!-- SecTrustResultType — UInt32 enum -->
<entity managedNameSpace="Security" managedTypeName="SecTrustResultType">
    <typedeclaration kind="enum" name="SecTrustResultType" module="Security"
                     frozen="true" simpleEnum="true" requiresMemoryManagement="false" rawValueType="UInt" />
</entity>
```

#### 5. Verify apple-frameworks.json

Check that `src/Swift.Bindings/src/Data/apple-frameworks.json` has a Security module definition. If not, add:
```json
{
  "module": "Security",
  "autoBridge": true,
  "optionalFallback": true,
  "objcPrefixes": ["Sec"]
}
```

Also verify CoreMedia has an entry. If not, add:
```json
{
  "module": "CoreMedia",
  "optionalFallback": true,
  "valueTypes": ["CMTime", "CMTimeRange", "CMTimeMapping"]
}
```

#### 6. Add unit tests

Add tests to `src/Swift.Bindings/tests/UnitTests/TypeDatabaseTests/` verifying:
- Each new entry resolves via `TryGetTypeRecord()` (not AnyType fallback)
- CoreMediaDatabase.xml loads correctly as a new database
- New entries don't conflict with existing synthetic ObjC bridge records

**Test pattern** (from existing tests):
```csharp
[Fact]
public void TryGetTypeRecord_NSNotificationName_ResolvesFromXml()
{
    var typeDb = new TypeDatabase();
    typeDb.AddModuleDatabase(FoundationDatabasePath);
    var swiftType = SwiftTypeName.Parse("Foundation.NSNotification.Name");
    var result = typeDb.TryGetTypeRecord(swiftType, out var record);
    Assert.True(result);
    Assert.Equal("Foundation", record.ManagedNameSpace);
    Assert.Equal("NSNotificationName", record.ManagedTypeName);
}
```

### Validation

1. `nuke test` — unit tests pass
2. `nuke validate` — no regressions, verify previously-UnsupportedType members now emit
3. Re-run binding reports on sim-validation libraries to measure skip reduction:
   ```bash
   cd /Users/wojo/Dev/sim-validation && ./regenerate-all.sh 2>&1 | tee /tmp/regen-session1.txt
   ```
   Then aggregate skip reasons with:
   ```bash
   python3 -c "
   import json, os, collections
   skip_reasons = collections.Counter()
   base = '/Users/wojo/Dev/sim-validation'
   for lib in sorted(os.listdir(base)):
       report = os.path.join(base, lib, 'binding-report.json')
       if not os.path.isfile(report): continue
       with open(report) as f: data = json.load(f)
       for item in data.get('SkippedItems', []):
           skip_reasons[item.get('Reason', 'Unknown')] += 1
   for reason, count in skip_reasons.most_common(20):
       print(f'  {count:5d}  {reason}')
   "
   ```
   Verify UnsupportedType count drops.

### Expected impact

~15-20 UnsupportedType skips eliminated. NSNotification.Name alone accounts for 8 (all Alamofire notification properties).

---

## Session 2: Closure Handler Gap Analysis & Targeted Fixes — COMPLETE (81017017)

> Class/ObjC closure returns, enum invocability, invoke thunk expansion. PhoneNumberKit 8→0 errors. Starscream regression caught in code review and fixed. Zero validation regressions.

**Target**: UnsupportedClosure — 102 skips across 15 libraries (12.7% of all skips)

### Background: Why Closures Get Skipped

The generator has a **two-layer gate system** for closures:

**Layer 1 — Method emission gate** (`MemberEmissionValidator.cs`):
- Line 148: `IsSupportedClosure()` check for closure properties
- Line 153: `CanInvokeFromCSharp()` check for closure properties
- Line 795: `NestedClosureBridge.IsEligible()` check for method closure params
- Line 807: Skip if closure param fails all gates

**Layer 2 — @_cdecl wrapper generation** (`ClosureEmitter.SwiftWrapper.cs:520`):
- `IsCdeclCompatibleType()` — checks if a type can be passed through C function pointer ABI
- Allows: Bool, primitives, pointers, classes, ObjC-bridged types, simple enums, Optional<Class>
- Blocks: String, Data, non-frozen structs, complex enums, existentials, Optional<Bool/Struct/SimpleEnum>

**Key gate functions** (all in `ClosureHandler.cs` unless noted):
| Function | Location | What it checks |
|----------|----------|---------------|
| `IsSupportedClosure()` | :203 | Validates all args + return type are supported |
| `IsSupportedClosureParameterType()` | :445 | Per-arg check: no nested closures, generics, existentials, unresolvable types |
| `IsCdeclCompatibleType()` | `ClosureEmitter.SwiftWrapper.cs:520` | Can type pass through C function pointer? |
| `CanInvokeFromCSharp()` | :1250 | Can C# code call the closure? Non-primitive tuples/closures/existentials fail |
| `NestedClosureBridge.IsEligible()` | `NestedClosureBridge.cs:37` | Can a method with closure-in-closure be bridged? |

### Research: Root Cause Classification of 102 Skips

| Root Cause | Count | % | Fixable? |
|-----------|------:|--:|---------|
| Generic type parameter closures (`(τ_0_0) -> τ_1_0`) | 26 | 25% | Partial — GenericClosureBridge handles some |
| Complex struct params not invokable from C# | 26 | 25% | Partial — expand marshalling per type |
| Result/union types with existential args | 27 | 26% | Blocked — can't fit existential in function pointer |
| Complex struct return types | 10 | 10% | Fixable — indirect return marshalling |
| ArraySlice<T> with non-primitive T | 9 | 9% | Blocked — no slice conversion |
| Existential/protocol return types | 4 | 4% | Blocked — abstract type, no ABI |

**Distribution by library**:
- Alamofire: 40 (39%) — DataResponse.map/tryMap, ClosureEventMonitor callbacks, validate, interceptors
- RxSwift: 25 (25%) — Event handlers, schedulers, Result-based patterns
- Kingfisher: 9 (9%)
- CryptoSwift: 9 (9%) — ArraySlice<UInt8> cipher operations
- XMLCoder: 5, Swinject: 5, PhoneNumberKit: 4, Starscream: 3, SwiftyBeaver: 1, ObjectMapper: 1

### Deliverables

#### 1. Investigation: Extract exact closure signatures from ABI JSON

For each of the 102 skipped closures, extract the Swift closure type from the ABI JSON and classify it. Produce a triage table in this doc with columns: Library, Type.Member, Closure Signature, Root Cause, Fixable?.

Use the binding-report.json files at `/Users/wojo/Dev/sim-validation/*/binding-report.json` as the source list of skipped items.

For ABI JSON lookup, the files are at:
```
/Users/wojo/Dev/swift-bindings/.libraries/{LibName}/{LibName}.xcframework/ios-arm64/{LibName}.framework/Modules/{LibName}.swiftmodule/arm64-apple-ios.abi.json
```

#### 2. Implement fixable patterns (prioritized)

**Priority 1 — Struct return types in closures (~10 items)**:
These closures return a custom struct that needs indirect return marshalling. The mechanism exists (buffer-based return) but the closure handler rejects it. Investigate whether `IsSupportedClosureReturnType()` can be relaxed to allow frozen struct returns via indirect buffer.

Key files:
- `ClosureHandler.cs` — `IsSupportedClosureReturnType()`
- `ClosureEmitter.SwiftWrapper.cs` — return emission
- Test: Add BindingTests Swift source with closure returning custom struct

**Priority 2 — GenericClosureBridge expansion (~8-12 items)**:
`GenericClosureBridgeEmitter.cs` handles method-level generic monomorphization for closures. Investigate whether its scope can be expanded to cover more of the 26 generic-param closures.

Key files:
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/GenericClosureBridgeEmitter.cs`
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/NestedClosureBridge.cs`

**Priority 3 — Struct parameter invocation (~5-8 items)**:
`CanInvokeFromCSharp()` rejects closures whose parameters are complex structs. Some may be fixable by adding marshalling for specific known struct types.

#### 3. Document unfixable patterns

For the ~42 architecturally blocked items (existential params, Result<T, any Error>, ArraySlice), document why they're blocked and what would be needed to fix them (delegate-based wrapper rearchitect, etc.). Add findings to the "Hard / Deferred" section of `roadmap.md`.

#### 4. Add BindingTests coverage

For each pattern that gets fixed, add:
- Swift source to `BindingTests/Sources/SwiftBindingsTestLib/Closures/`
- Runtime test to `BindingTests/RuntimeTestsApp/Closures/`

Existing closure test files to reference:
- `ClosureEdgeCaseTests.cs`, `StructClosureBridgeTests.cs`
- Swift: `Closures.swift`, `StructClosureBridge.swift`, `ClosureEdgeCases.swift`

### Validation

1. `nuke test` — unit tests pass
2. `nuke validate` — no regressions
3. Re-run binding reports to measure UnsupportedClosure reduction
4. `nuke binding-tests` if new BindingTests were added

### Expected impact

Conservative: 10-15 closures unblocked (struct returns + some generic bridge expansion).
Optimistic: 25-30 closures unblocked if struct param invocation also works.

---

## Session 3: Data Projection Verification & Downstream Test Updates — COMPLETE

> Data round-trip verified end-to-end (byte[] → Swift.Data → byte[]). 2 BindingTests added (15/15 pass). Starscream Binary+Ping unskipped (33/33 pass, up from 31+2 skip). No other stale Data skips found. DataProjection active across 9 sim-validation libraries (216 byte[] params, 172 returns, backed by 435 Foundation.Data ABI references across 19 validation libraries).

**Target**: Verify DataProjection works end-to-end and unskip stale downstream tests.

### Background: DataProjection Already Exists

Research revealed that `DataProjection` is **already fully implemented**:

- **Projection**: `src/Swift.Bindings/src/Marshaler/Projection/DataProjection.cs` — `PublicType: byte[]`, `PInvokeType: Swift.Data`
- **Runtime**: `src/Swift.Runtime/src/Swift/Data.cs` — frozen struct with `FromByteArray()`, `ToByteArray()`, `FromNSData()`, `ToNSData()`
- **TypeDatabase**: `FoundationDatabase.xml` has `Foundation.Data` entry with `nativeType="Foundation.NSData"`
- **Factory**: `TypeProjectionFactory.cs` routes `"Foundation.Data"` → `new DataProjection()`
- **Visitors**: All 3 accessor visitors (getter, setter, optional getter) implement `Visit(DataProjection p)`
- **Tests**: `BindingTests/Sources/SwiftBindingsTestLib/WrapperCoverage/ConstructorParams.swift` has `TimestampedBlob` with `Data` param

The Starscream generated bindings already use DataProjection correctly:
```csharp
public static unsafe WebSocketEvent Binary(byte[] data)
{
    var __data = Swift.Data.FromByteArray(data);  // DataProjection in action
    PInvoke_Binary(__data, buffer);
    ...
}
```

The skips in `sim-validation/Starscream/Program.cs` are **stale comments** from before the projection was implemented.

### Deliverables

#### 1. Verify Data round-trip in BindingTests

Check that the existing `TimestampedBlob` tests run on simulator:
```bash
nuke runtime-tests-simulator --class-filter ConstructorParamTests --skip-regen 2>&1 | tee /tmp/data-verify.txt
```

If no runtime test exists for Data, add one:
- Swift: function taking `Data` and returning `Data` (round-trip)
- C#: `byte[]` → call → verify returned `byte[]` matches

#### 2. Unskip Starscream Data tests

In `/Users/wojo/Dev/sim-validation/Starscream/Program.cs`, convert the 2 hard skips to try/catch tests:

```csharp
// WebSocketEvent.Binary — Data projection exists, unskipping
try
{
    byte[] testData = new byte[] { 0x01, 0x02, 0x03 };
    using var evt = Starscream.WebSocketEvent.Binary(testData);
    Console.WriteLine($"[PASS] WebSocketEvent.Binary tag={evt.Tag}");
    passed++;
}
catch (Exception ex)
{
    Console.WriteLine($"[FAIL] WebSocketEvent.Binary: {ex.GetType().Name}: {ex.Message}");
    failed++;
}
```

Do the same for WebSocketEvent.Ping (optional Data payload).

#### 3. Search for other stale Data skips

Grep all sim-validation and swift-dotnet-packages test files for "Data" skip comments that may be stale now that DataProjection exists. Unskip any that are no longer valid.

#### 4. Quantify Data type usage across validation libraries

Run this across all ABI JSON files to count how many public APIs use Foundation.Data:
```bash
grep -r '"Foundation.Data"' /Users/wojo/Dev/swift-bindings/.libraries/*/abi.json 2>/dev/null | wc -l
```
Or check binding reports for Data-related members. Document findings — this validates the projection's value.

### Validation

1. `nuke runtime-tests-simulator --skip-regen` — Data tests pass
2. Rebuild and run Starscream sim test:
   ```bash
   cd /Users/wojo/Dev/sim-validation && ./run-all-sim.sh --filter Starscream --timeout 30
   ```
3. Verify 32+ pass (up from 31 with ViabilityChanged, now +2 for Binary/Ping)

### Expected impact

2-3 Starscream tests unskipped. Documentation of Data projection completeness for release notes.

---

## Session 4: Skip Metrics Tooling & Release Baseline — COMPLETE (d8492657)

> skip-metrics.py created, validation baseline extended with skip_metrics, Build.Validation.cs integrated. Release 0.5.0 baseline: 89/90 CS, 54/56 Swift, 9,953 emitted, 1,920 skipped (16.2%), 9,704 unit tests.

**Target**: Build tooling to measure binding coverage, establish 0.5.0 baseline.

### Background: Current Metrics Gap

The `.validation-baseline.json` only tracks compile gate metrics:
```json
{
  "git_sha": "29b22ff4",
  "compile_gate": {
    "libraries": {
      "Alamofire": {
        "compile": "ok",
        "errors": 0,
        "lines": 45584,
        "dep_compile": "none",
        "swift_compile": "ok"
      }
    }
  }
}
```

**What's NOT tracked**: skip metrics (total skipped members, skip reason distribution, emitted/skipped by kind). These are available in `binding-report.json` files produced by the generator but never aggregated.

### binding-report.json Schema

Each generator run produces a report with this structure:
```json
{
  "ModuleName": "Alamofire",
  "GeneratedAt": "ISO8601",
  "TotalTypes": 140,
  "EmittedTypes": 132,
  "SkippedTypes": 2,
  "TotalMembers": 729,
  "EmittedMembers": 518,
  "SkippedMembers": 163,
  "SynthesizedMembers": 315,
  "EmittedMembersByKind": { "Method": 212, "Property": 292, "Operator": 13, "Subscript": 1 },
  "SkippedMembersByKind": { "Method": 91, "Property": 55, "Type": 17 },
  "SkippedItems": [
    {
      "Kind": "Method",
      "Name": "encode",
      "ContainingType": "Alamofire.URLEncoding",
      "Reason": "UnsupportedExistential",
      "Details": "...",
      "RecommendedWorkaround": "..."
    }
  ],
  "WrappedItems": [ { "Kind": "Method", "Name": "...", "WrapperKind": "MethodClosureBridge" } ]
}
```

**Known Reason values**: UnsupportedClosure, UnsupportedSignature, AnyTypeFallback, ModuleInternal, UnsatisfiedGenericConstraint, UnsupportedType, EveryProtocolConformanceSkipped, UnsupportedExistential, GenericProtocolConstraint, SynthesizedCodable, SwiftUIConstraint, GenericTypeCallback, StaticProtocolMember, SwiftUIView, DuplicateSignature

### Where binding-report.json files are produced

- `nuke validate` outputs to `/tmp/binding-validation-{git_branch}/{LibraryName}/binding-report.json`
- `sim-validation/regenerate-all.sh` outputs to `/Users/wojo/Dev/sim-validation/{LibName}/binding-report.json`
- Build.Validation.cs (Phase 3a, lines 183-203) runs the generator per library

### Deliverables

#### 1. Create `build/scripts/skip-metrics.py`

Script that aggregates binding-report.json files and produces a structured report.

**Usage**:
```bash
# After nuke validate:
python3 build/scripts/skip-metrics.py --input /tmp/binding-validation-main/ --output skip-metrics.json

# For sim-validation:
python3 build/scripts/skip-metrics.py --input /Users/wojo/Dev/sim-validation/ --output skip-metrics.json

# Compare against baseline:
python3 build/scripts/skip-metrics.py --input /tmp/binding-validation-main/ --baseline .validation-skip-baseline.json
```

**Output format**:
```json
{
  "timestamp": "ISO8601",
  "git_sha": "abc123",
  "summary": {
    "total_libraries": 46,
    "total_types": 5000,
    "emitted_types": 4500,
    "total_members": 20000,
    "emitted_members": 16000,
    "skipped_members": 4000,
    "skip_rate_pct": 20.0,
    "wrapped_items": 500
  },
  "skip_reasons": {
    "UnsupportedClosure": 500,
    "AnyTypeFallback": 400,
    "UnsupportedSignature": 350
  },
  "per_library": {
    "Alamofire": {
      "emitted_members": 518,
      "skipped_members": 163,
      "skip_rate_pct": 23.9,
      "top_skip_reasons": { "UnsupportedClosure": 40, "UnsupportedSignature": 39 }
    }
  }
}
```

**Reference**: Reuse patterns from `build/scripts/coverage-report.py` (argument parsing, JSON loading, aggregation logic).

#### 2. Extend .validation-baseline.json

Add a `skip_metrics` section to the baseline so future validations can detect skip regressions:

```json
{
  "git_sha": "...",
  "compile_gate": { ... },
  "skip_metrics": {
    "total_emitted_members": 16000,
    "total_skipped_members": 4000,
    "skip_reasons": { ... }
  }
}
```

Update `build/Build.Validation.cs` (Phase 4, lines 459-493) to:
- Run skip-metrics.py after binding generation
- Include skip metrics in baseline updates
- Warn (but don't fail) if skip count increases

#### 3. Run full validation and establish baseline

```bash
nuke validate 2>&1 | tee /tmp/validate-050.txt
python3 build/scripts/skip-metrics.py --input /tmp/binding-validation-main/ --output /tmp/skip-metrics-050.json
```

Also run downstream:
```bash
cd /Users/wojo/Dev/sim-validation && ./run-all-sim.sh --timeout 30 2>&1 | tee /tmp/sim-val-050.txt
cd /Users/wojo/Dev/swift-dotnet-packages/tests/Nuke.SimTests && ./build-testapp.sh && ./validate-sim.sh 20
# (repeat for Lottie, BlinkID, BlinkIDUX, Stripe)
```

#### 4. Document release baseline

Create `src/docs/release-050-baseline.md` with:
- Compile gate: X/90 CS pass, Y/Z Swift pass
- Skip metrics: total bound, total skipped, skip rate, top reasons
- Unit tests: pass count from `nuke test`
- BindingTests runtime: pass count from `nuke runtime-tests-simulator`
- Downstream: pass counts from sim-validation (15 libs) and swift-dotnet-packages (5 libs)
- Comparison vs 0.4.0 (if available)

### Validation

The script itself should be tested by running it against sim-validation binding reports and verifying the output matches manual counts from this session's earlier research:
- Alamofire: 518 emitted, 163 skipped
- Total across 15 libs: 2,850 emitted, 806 skipped

### Expected impact

Tooling for ongoing skip tracking. Clear release baseline document. No code generation changes.

---

## Session 5: Followup — Sweep Skipped Deliverables — COMPLETE (e110b549)

> Fixed 3 bugs (keyword escaping, metatype detection, metatype→AnyType guard). Added JSONEncoder, JSONDecoder, Locale, ComparisonResult to FoundationDatabase. XMLCoder fail→ok. Decimal deferred (cascading EveryProtocol issues). 9190 unit tests, zero regressions.

**Target**: Pick up anything skipped or deferred across Sessions 1-4.

### Known items
- **Session 1**: JSONEncoder and Decimal TypeDatabase entries were omitted. Investigate correct attributes and add them. Also grep for other common Foundation/system types that are still falling back to AnyType.
- **Sessions 2-4**: TBD — add any skipped deliverables here as they surface.

### Deliverables
1. Add any missing TypeDatabase entries from Session 1 (JSONEncoder, Decimal, plus any other high-impact types found via skip analysis)
2. Address any deferred items from Sessions 2-4
3. Run full validation to confirm zero regressions

### Validation
Same gates as previous sessions — `nuke test`, `nuke validate`, binding-tests if generator changes.

---

## Appendix: Key File Reference

### TypeDatabase
| File | Purpose |
|------|---------|
| `src/Swift.Bindings.Sdk/tools/net10.0/any/Swift/FoundationDatabase.xml` | Foundation type mappings |
| `src/Swift.Bindings.Sdk/tools/net10.0/any/Swift/CoreGraphicsDatabase.xml` | CoreGraphics type mappings |
| `src/Swift.Bindings.Sdk/tools/net10.0/any/Swift/SecurityDatabase.xml` | Security type mappings |
| `src/Swift.Bindings/src/TypeDatabase/TypeDatabase.cs` | XML parser (ReadVersion1_0) |
| `src/Swift.Bindings/src/TypeDatabase/TypeDatabaseExtensions.cs` | Type resolution logic |
| `src/Swift.Bindings/src/Data/apple-frameworks.json` | Module/type registry |
| `src/Swift.Bindings/tests/UnitTests/TypeDatabaseTests/` | Unit tests |

### Closure Handler
| File | Purpose |
|------|---------|
| `src/Swift.Bindings/src/Marshaler/ClosureHandler.cs` | IsSupportedClosure, CanInvokeFromCSharp |
| `src/Swift.Bindings/src/Emitter/StringEmitter/ClosureEmitter.SwiftWrapper.cs` | IsCdeclCompatibleType |
| `src/Swift.Bindings/src/Emitter/StringEmitter/MemberEmissionValidator.cs` | Skip decision points (lines 148, 153, 795, 807) |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/NestedClosureBridge.cs` | IsEligible check |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/GenericClosureBridgeEmitter.cs` | Generic monomorphization |

### Data Projection (already complete)
| File | Purpose |
|------|---------|
| `src/Swift.Bindings/src/Marshaler/Projection/DataProjection.cs` | Projection implementation |
| `src/Swift.Bindings/src/Marshaler/Projection/TypeProjectionFactory.cs` | Factory (line 242) |
| `src/Swift.Runtime/src/Swift/Data.cs` | Runtime Swift.Data struct |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/AccessorConversionVisitors.cs` | Visitor implementations |

### Validation & Metrics
| File | Purpose |
|------|---------|
| `build/Build.Validation.cs` | Validate target, baseline logic |
| `.validation-baseline.json` | Current compile-gate baseline |
| `build/validation-libraries.json` | 90 validation targets manifest |
| `build/scripts/coverage-report.py` | Existing coverage tooling (reference) |
