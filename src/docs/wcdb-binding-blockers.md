# WCDB binding — two blockers found in 0.9.0 SDK

> Investigation date: 2026-05-03. SDK: SwiftBindings.Sdk 0.9.0 / Apple 26.2.1. Tooling: spm-to-xcframework `5ddb8ecd9aed`. Library: [Tencent/WCDB](https://github.com/Tencent/wcdb) v2.1.16 (`WCDBSwift` product). Reproduction repo: `swift-dotnet-packages/libraries/WCDB/`.

A consumer asked whether WCDB could be bound with our tooling. The xcframework builds cleanly on the first try (23.5 MB, both device + simulator slices) and the binding generator parses the ABI cleanly (162/162 types emitted, 451/861 members emitted), but two distinct issues in the SDK pipeline prevent the package from compiling. Both look in scope for a fix before the next SDK release.

## Status

**Decision (2026-05-03): defer to 0.10.0.** All three fixes (blocker 1, blocker 2, bonus CS0109) are concrete and bounded after the verified scoping below. None block what 0.9.0 already delivers — WCDB isn't currently supported and no existing library regresses. Don't hold up 0.9.0 to bundle these.

| Item | Owner | Verified shape | Test gap |
|---|---|---|---|
| Blocker 1 — synthesize `WCDB_Private` clang submodule | `spm-to-xcframework` | New `_inject_bridge_submodule` injection pass at `spm_to_xcframework.py` ~line 5145, ~60-80 lines, mirrors existing `inject_system_clang_modules` pattern (line 4411). | None at the tool layer; integration is the WCDB build itself. |
| Blocker 2 — covariant-return forwarder | `Swift.Bindings` generator | Edit `ProtocolProxyEmitter.InterfaceImpl.cs:350-359` (or thereabouts — see verified scoping). Emit explicit interface impl when dedup check rejects a base-protocol method whose return type differs. | No existing unit or BindingTests fixture exercises refined-return-type protocol inheritance. New fixture needed. |
| Bonus CS0109 spam (215 warnings) | `Swift.Bindings` generator | Edit `ClassHandler.cs:686, 726, 740, 760`. Gate `HasNewModifier` on actual ancestor `PInvoke_getMetadata` emission (mirror `HasMethodInResolvedAncestors` at `WrapperEmitter.Signature.cs:80`). **Not the same logic family as blocker 2** — different file, different path. The original "same family" hypothesis below is wrong. | Build-warning suppression; existing inheritance fixtures should cover with one new assertion on warning count. |

**Recommended sequence for 0.10.0:**
1. `/codex-review` a written plan for blocker 2 first — the "what if the C# class hierarchy doesn't mirror Swift's" edge case (see verified scoping) is the kind of subtle question where a second opinion helps.
2. Land blocker 1 in `spm-to-xcframework` (mechanically clear, can go straight to implementation).
3. Land bonus CS0109 fix in this repo (also mechanically clear).
4. Land blocker 2 last with the new BindingTests fixture.

The repro artifacts under `swift-dotnet-packages/libraries/WCDB/` are preserved (untracked) — see *Artifacts* section at the bottom. Re-running `nuke BuildLibrary --library WCDB` against a fixed `spm-to-xcframework` and SDK should be the verification path.

## Reproduction

```bash
cd swift-dotnet-packages
./scripts/new-library.sh WCDB --repo https://github.com/Tencent/wcdb.git \
    --version 2.1.16 --mode source --scheme WCDBSwift
dotnet nuke BuildLibrary --library WCDB
```

`BuildXcframework` step succeeds. `dotnet build` step fails on `SWIFTBIND051` (blocker 1). Re-running with `-p:SwiftWrapperRequired=false` exposes blocker 2 (`CS0738`). Both are deterministic.

---

## Blocker 1 — wrapper compile fails: `no such module 'WCDB_Private'` (SWIFTBIND050/051)

### What we see

The wrapper-compile step invokes `swiftc` against the freshly emitted `<Module>.Wrapper.swift`. swiftc tries to consume `WCDBSwift`'s textual interface and bails:

```
WCDBSwift.framework/.../arm64-apple-ios-simulator.private.swiftinterface:7:8:
  error: no such module 'WCDB_Private'
   5 | import Foundation
   6 | import Swift
   7 | import WCDB_Private
     |        `- error: no such module 'WCDB_Private'

obj/.../WCDBSwift.Wrapper.swift:1:8:
  error: failed to build module 'WCDBSwift' for importation due to the errors above;
         the textual interface may be broken by project issues or a compiler bug
```

The same `import WCDB_Private` line is present in **both** the public `arm64-apple-ios.swiftinterface` and the `arm64-apple-ios.private.swiftinterface`. So this isn't a "we accidentally consumed the private interface" case — it would fail against the public one too.

### Root cause: missing clang submodule in the packaged framework

WCDB's [Package.swift](https://github.com/Tencent/wcdb/blob/v2.1.16/Package.swift) declares four targets (`objc-core` → `common` → `bridge` → `WCDBSwift`). The Swift target depends transitively on a large C/ObjC++/C++ surface. Because library-evolution is enabled (`-enable-library-evolution`), the swiftinterface preserves the import chain — including the synthetic `WCDB_Private` clang module name SPM generates for the bridge headers. (WCDB doesn't define `WCDB_Private` explicitly anywhere in `Package.swift`; SPM/Xcode constructs the name from the bridge target's headers when archiving the `WCDBSwift` scheme.)

But the framework's own module map (in `WCDBSwift.framework/Modules/module.modulemap`) declares only `WCDBSwift`:

```modulemap
framework module WCDBSwift {
  header "BindParameterBridge.h"
  header "BindingBridge.h"
  ... 60+ bridge headers ...
  export *
}
```

There is no `framework module WCDBSwift.WCDB_Private { ... }` submodule, and no separate `WCDB_Private.modulemap` shipped alongside. The bridge headers are present in `WCDBSwift.framework/Headers/`, but the `WCDB_Private` symbolic name that the swiftinterface relies on never makes it into the archive.

Once the framework leaves the SPM build context, any downstream `swiftc` invocation that consumes the swiftinterface will fail. Our wrapper compile is the first place this surfaces, but consumers using `WCDBSwift.xcframework` directly from Swift code outside of WCDB's SPM checkout would hit the same wall.

### Where this lives in our pipeline

`spm-to-xcframework` is the layer that owns this. It runs `xcodebuild archive`, then assembles the `.xcframework` from the per-arch archives. Right now it copies `Modules/<Module>.swiftmodule/*` and the framework binary, but it doesn't preserve the synthetic clang submodules SPM constructed during the build. The intermediate clang module maps live somewhere under `.build/` during the archive — `xcodebuild -resultBundle` should have a path to them.

### Suggested fix directions (in roughly increasing complexity)

1. **Strip the synthetic import from the swiftinterface during packaging.** Before sealing the xcframework, post-process every `.swiftinterface` (public + private) to drop `import WCDB_Private` (or any non-public synthetic module name). This is brittle — if any public type signature refers back to a `WCDB_Private` type, swiftc consuming the trimmed interface will then fail on the type reference itself instead. But for WCDB the bridge headers are pure C/ObjC++ and the public Swift surface doesn't seem to leak them, so a strip would likely work.
2. **Synthesize a `WCDB_Private` submodule in the framework's `module.modulemap`.** When `spm-to-xcframework` detects that the Swift target's swiftinterface imports a clang module whose headers are already being copied into the framework, append a sibling `framework module WCDBSwift.WCDB_Private { umbrella header "WCDB_PrivateUmbrella.h" }` (or an explicit-header variant). Then update the `import` line in the swiftinterface to `import WCDBSwift.WCDB_Private`. Less brittle than option 1, but requires the tool to know which headers belonged to which sub-target.
3. **Capture and ship the SPM-generated clang module maps verbatim.** During archive, the SPM build emits intermediate `module.modulemap` files for each target into `.build/<config>/<Target>.build/module.modulemap`. Capture these, ship them alongside the framework as `WCDB_Private.modulemap`, and emit `-fmodule-map-file=...` flags into a sidecar that downstream tools (including our wrapper compile) consume. Cleanest from a fidelity standpoint, but requires propagating those flags through every consumer of the xcframework.

### Verified scoping (2026-05-03)

**Option 1 is dead.** Direct verification: WCDB's *public* `arm64-apple-ios.swiftinterface` contains 54 references to `WCDB_Private` types beyond the import line — return types (`getRawStatement() -> WCDB_Private.CPPHandleStatement`), generic arguments in public class declarations (`Identifier<WCDB_Private.CPPBindParameter>`), property types (`UnsafeMutablePointer<WCDB_Private.CPPObject>`). Stripping the import would just shift the failure to `error: cannot find type 'WCDB_Private' in scope` at every reference. Don't pursue.

**Option 2 is the right shape.** `spm-to-xcframework` is a single Python file (`/Users/wojo/Dev/spm-to-xcframework/src/spm_to_xcframework.py`, ~5900 lines). Architecturally the closest existing pass is `inject_system_clang_modules` (line 4411), which already does "synthesize a sibling framework with a modulemap + headers for a module the swiftinterface imports but the main framework doesn't declare." The new `_inject_bridge_submodule` pass would do the same thing one level deeper — write a submodule block inside the existing `Modules/module.modulemap` rather than ship a sibling framework.

Concrete landing:
- New function `_inject_bridge_submodule(fw_path)` ~60-80 lines: scan `Modules/**/*.swiftinterface` for `^import` lines, subtract modules already declared in the framework's `module.modulemap`, and for any remainder where the named module's headers appear in `Headers/`, append `framework module <Framework>.<Submodule> { headers... export * }` and rewrite the swiftinterface import to the qualified form.
- Call site: new injection pass at approximately line 5145, after `inject_objc_headers` (line 5113-5130) and before `inject_resource_bundles`. Must be guarded for idempotency (the function `inject_objc_headers` already exits early when bridge headers are present, line 4550-4554 — same guard pattern applies).
- The `.framework` bundle and its `module.modulemap` come **verbatim from the xcodebuild archive**; nothing currently regenerates them. So this pass is purely additive — it only modifies the modulemap if a missing-module pattern is detected.
- No model changes needed. `BuildUnit.source_targets` (line 405) carries the backing target names but doesn't link to bridge sub-targets — and we don't need that link, because we're inferring from "header file is present + module name is imported" rather than from source-side metadata.

**Option 3 is partially blocked.** `.build/` is in `TOXIC_NAMES` (line 579) and is deleted from `staged_dir` before archive runs. The intermediate modulemaps would have to come from DerivedData (`dd_path`) instead. Additionally, propagating `-fmodule-map-file` flags to every consumer is out of `spm-to-xcframework` scope — that burden falls on the SDK. Skip.

### Survey results — WCDB is the only library affected

Ran the survey across all 8 libraries currently in `swift-dotnet-packages/libraries/`:

```
[BlinkID]    imports CoreMedia    (false positive — system framework, missing from filter list)
[WCDB]       imports WCDB_Private (NOT in modulemap)  ← only real positive
[BlinkIDUX]  no modulemap         (pure-Swift framework, expected)
[Kingfisher] no modulemap         (pure-Swift framework, expected)
[Lottie]     no modulemap         (pure-Swift framework, expected)
[Nuke]       no modulemap         (pure-Swift framework, expected)
[Mappedin]   modulemap clean
[Stripe]     modulemap clean
```

Confirmed by inspecting the swiftinterfaces of the modulemap-less libraries: they only import system frameworks (`Foundation`, `UIKit`, `Combine`, `SwiftUI`, etc.), never a synthetic clang submodule. So this is a single-library workaround case, not structural. Option 2 is still the right long-term fix because the WCDB pattern (Swift facade over a private Clang dep with library-evolution on) will keep appearing — but the urgency is "do it for 0.10.0," not "block 0.9.0."

Survey command (with system-framework filter expanded — the original snippet missed `CoreMedia` and several modern SDK frameworks):

```bash
SYSTEM_FRAMEWORKS='Foundation|Swift|UIKit|CoreGraphics|CoreFoundation|_*|ObjectiveC|Combine|os|os.log|Dispatch|Darwin|simd|QuartzCore|Metal*|CoreML|AVFoundation|AVKit|CoreImage|CoreMedia|CoreData|SwiftUI|WebKit|MapKit|StoreKit|UserNotifications|AuthenticationServices|LocalAuthentication|Security|CryptoKit|Network|Vision|VisionKit|CoreLocation|CoreMotion|CoreNFC|CoreBluetooth|CoreTelephony|PassKit|Contacts|EventKit|HealthKit|HomeKit|MessageUI|Photos|PhotosUI|SafariServices|SceneKit|SpriteKit|GameKit|GameController|MediaPlayer|MusicKit|WeatherKit|WidgetKit|ActivityKit|ARKit|RealityKit|Accelerate|Compression|Concurrency|JavaScriptCore|WatchConnectivity|CallKit|PushKit|ReplayKit|VideoToolbox|AudioToolbox|CoreText|CoreVideo|CoreServices|FileProvider|UniformTypeIdentifiers|BackgroundTasks|LinkPresentation|NaturalLanguage|Speech|Translation|Intents|IntentsUI|Charts|TipKit|PDFKit|EventKitUI|MetricKit|OSLog|AppIntents|GroupActivities|ManagedSettings|Sensors|FamilyControls|DeviceActivity|ScreenTime|MediaAccessibility|Symbols|CarPlay|CommonCrypto|MobileCoreServices|ImageIO|DeveloperToolsSupport|zlib'

for lib in libraries/*/*.xcframework; do
    [ -d "$lib" ] || continue
    libname=$(basename $(dirname "$lib"))
    iface=$(find "$lib" -name "*.swiftinterface" | head -1)
    [ -z "$iface" ] && { echo "[$libname] no swiftinterface"; continue; }
    mm=$(find "$lib" -name "module.modulemap" | head -1)
    [ -z "$mm" ] && { echo "[$libname] no modulemap"; continue; }
    grep -h '^import ' "$iface" | awk '{print $2}' | while read m; do
        echo "$m" | grep -qE "^($SYSTEM_FRAMEWORKS)$" && continue
        grep -q "module $m\b\|module .*\.$m\b" "$mm" 2>/dev/null || echo "[$libname] imports $m (NOT in modulemap)"
    done
done
```

---

## Blocker 2 — generated C# fails: covariant interface return type mismatch (CS0738)

### What we see

After bypassing blocker 1 with `<SwiftWrapperRequired>false</SwiftWrapperRequired>`, the C# compile fails with one error (and 215 `CS0109` "the new keyword is not required" warnings — separate issue, mentioned at the end):

```
WCDBSwift.cs(36143,60): error CS0738:
  'PropertyConvertibleProxy' does not implement interface member
  'IColumnConvertible._in(string)'.
  'PropertyConvertibleProxy._in(string)' cannot implement
  'IColumnConvertible._in(string)' because it does not have the matching
  return type of 'Column'.
```

### Root cause: protocol-inheritance covariant return wasn't generated

WCDB's query DSL leans hard on protocol inheritance with refined return types. The relevant Swift shape is roughly:

```swift
public protocol ColumnConvertible: ... {
    func `in`(_ table: String) -> Column
}

public protocol PropertyConvertible: ColumnConvertible, ... {
    func `in`(_ table: String) -> Property   // refined return type
}
```

Both protocols expose an `in(_:)` method, but `PropertyConvertible` refines the return type from `Column` to `Property` (`Property` is a subclass of `Column` in WCDB's hierarchy). In Swift this works via protocol witness table dispatch.

The generator emits the C# interfaces correctly:

```csharp
// WCDBSwift.cs:14102
public interface IColumnConvertible : ... {
    WCDBSwift.Column _in(string table);
}

// WCDBSwift.cs:8830, 8839
public interface IPropertyConvertible : IPropertyRedirectable, IColumnConvertible, ... {
    WCDBSwift.Property _in(string table);   // shadows IColumnConvertible._in
}
```

So far so good — `IPropertyConvertible._in` shadows the inherited member. The problem is the auto-generated **proxy** class for the existential:

```csharp
// WCDBSwift.cs:36143
public unsafe partial class PropertyConvertibleProxy
    : IPropertyConvertible, ISwiftObject, IDisposable, ... {
    // ...
    public WCDBSwift.Property _in(string table) { ... }   // line 36429
}
```

The proxy declares it implements `IPropertyConvertible` (which transitively requires implementing `IColumnConvertible._in` returning `Column`) but only emits **one** `_in` method, returning `Property`. C# requires a separate implementation of the inherited `IColumnConvertible._in` that returns `Column` — implicit covariant return doesn't apply across interface inheritance the way it does across class inheritance.

### Suggested fix directions

In the proxy emitter (the bit that produces `<Protocol>Proxy` classes for existentials), when an emitted method shadows an inherited interface member with a refined return type, also emit an explicit interface implementation for the base member that calls through to the refined one and casts back:

```csharp
public WCDBSwift.Property _in(string table) {
    /* existing body */
}

// Auto-emit when refined-return shadowing is detected:
WCDBSwift.Column IColumnConvertible._in(string table) => this._in(table);
```

The cast is implicit because `Property : Column` in the generated hierarchy (the binding's class hierarchy mirrors Swift's). For cases where the refinement isn't a true subtype relationship in the C# hierarchy, a generator-side check should fall back to skipping the emission with a `binding-report.json` entry.

### Verified scoping (2026-05-03)

**Proxy emitter location.**
- Class declaration (`public unsafe partial class <Protocol>Proxy : I<Protocol>, ...`): `src/Swift.Bindings/src/Emitter/StringEmitter/ProtocolProxyEmitter.cs:96`.
- Method body emission entry point: `EmitInterfaceImplementation` at `src/Swift.Bindings/src/Emitter/StringEmitter/ProtocolProxyEmitter.InterfaceImpl.cs:8`.
  - Property stubs: lines 17-32 via `EmitPropertyImplementation`.
  - Method stubs: lines 69-96 via `EmitMethodImplementation`.

**Inherited-interface walk.** `EmitInheritedInterfaceImplementations` at `ProtocolProxyEmitter.InterfaceImpl.cs:232` performs a BFS over `protocolDecl.InheritedProtocols` (seed lines 247-279, recursion lines 288-332). It re-applies the same filter guards as `ProtocolHandler.GetInheritedInterfaceList` (AnyObject skip, Sendable/Copyable/Escapable skip, cross-module skip, PAT/Self-requirement skip, underscore-suppressed skip). For each ancestor it calls `EmitInheritedPropertyStub` (line 343) and `EmitInheritedMethodStub` (line 358).

**Why the forwarder is missing today.** `EmitInheritedMethodStub` (`InterfaceImpl.cs:387`) emits `public {returnTypeName} {methodName}(...)` using `GetCSharpTypeName(returnType!)` resolved against the *inherited* base protocol's own method decl — so it would correctly produce `Column` for `IColumnConvertible._in`. The breakage happens earlier: a `emittedCSharpKeys` dedup set (lines 91-93) silences the second emission because the C# method key (name + parameter list) collides with the already-emitted `Property`-returning version. C# treats the two as ambiguous, but the proxy class only ever sees the `Property` one — so `IColumnConvertible._in() -> Column` is unimplemented.

**Where the fix lands.** `EmitInheritedInterfaceImplementations` at `InterfaceImpl.cs:350-359`. After the dedup check (`emittedCSharpKeys.Add(projectedKey)` returns false), if the base method's return type differs from the already-emitted method's return type, emit an explicit interface implementation forwarder:

```csharp
{BaseInterfaceFullName} {InheritedBaseTypeName} {MethodName}(...) =>
    ({BaseReturnType})this.{MethodName}(...);
```

**Edge case that needs `TypeDatabase` lookup.** The implicit cast `(Column)this._in(table)` only compiles if `Property` is a C# subtype of `Column` in the binding's emitted hierarchy. WCDB's binding does mirror this (`Property : Column`), but the generator can't assume it in general — Swift protocol-witness dispatch works regardless of class subtyping in the witness types. Required: a `TypeDatabase.IsAssignableTo(refinedType, baseType)` check before emitting the forwarder. Fallback when the relationship doesn't hold:
1. Emit the forwarder with an explicit `(BaseReturnType)(object)this.{Method}(...)` and let it throw `InvalidCastException` at runtime if exercised, OR
2. Skip emission with a `binding-report.json` entry (`SkipReason: CovariantReturnNotRepresentable`), and let CS0738 surface so the consumer knows to provide a manual extension.

Pick option 2 — option 1 hides a real bug in marshalling at runtime. The proxy is a generated convenience; consumers can write the forwarder manually if they hit it.

### Bug 2 (CS0109) is NOT the same logic family — see corrected analysis below

The original "Bonus" section below hypothesized that the 215 CS0109 warnings shared the proxy emitter's "walk the hierarchy" logic and could be fixed in the same pass. **That hypothesis is wrong.** Verified: CS0109 lives in `ClassHandler.cs` (Swift class emitter), not `ProtocolProxyEmitter.cs` (existential proxy emitter). Different file, different code path. See the corrected "Bonus" section for the real fix landing.

### Test gap

No existing unit or end-to-end test covers refined-return-type protocol inheritance. Both layers need new fixtures:

- **Unit tests**: `src/Swift.Bindings/tests/UnitTests/EmitterTests/ProtocolProxyEmitterTests.cs` (5,496 lines today). Existing tests at lines 826 (`EmitProxyClass_EmptyProtocol_WithInheritedRequirements_StillGeneratesProxy`) and 840 (`EmitProxyClass_EmptyProtocol_InheritingOnlyAnyObject_GeneratesProxy`) cover empty inheritance, but nothing covers a two-level protocol where a child refines a method return type. Add a fixture that asserts the explicit-interface forwarder is emitted and a separate one for the `IsAssignableTo` fallback path.
- **End-to-end BindingTests**: `BindingTests/Sources/SwiftBindingsTestLib/Protocols/BasicProtocols.swift` has a 3-level protocol chain (`BaseRule → InputValidation → StrictInputValidation`) but each level uses different method names, so no refined-return case exists. Add a fixture pair modeled on WCDB's `ColumnConvertible`/`PropertyConvertible` shape, plus a runtime test under `BindingTests/RuntimeTestsApp/Protocols/` that exercises the existential through both the refined and base interface references.

### Wider impact

This pattern (protocol inheritance with refined return types) shows up anywhere a library uses a builder/chain pattern with hierarchical types. WCDB is the most aggressive example we've seen, but it's worth scanning for it across other libraries' `binding-report.json` files. The proxy-class generator is in scope for proxies of any protocol existential — Stripe and Lottie both have protocol-heavy public surfaces and may have latent CS0738s suppressed only because nobody's tried `<SwiftWrapperRequired>false</SwiftWrapperRequired>` on them.

---

## Bonus — 215 CS0109 warnings ("the new keyword is not required")

Not a blocker, but the WCDB build emits 215 of these on members like:

```
StatementSelect.Payload    StatementSelect.GetSwiftHandle()    StatementSelect.Dispose()
StatementDropTrigger.Payload    StatementUpdate.Payload    StatementVacuum.Payload    ...
```

### Verified scoping (2026-05-03)

**Original hypothesis was wrong.** This is *not* the same logic family as blocker 2's CS0738 fix. Different file, different code path:

- Blocker 2 (CS0738) lives in `ProtocolProxyEmitter.InterfaceImpl.cs` (existential proxy emitter).
- CS0109 lives in `ClassHandler.cs` (Swift class emitter).

**Where `new` modifiers are emitted in `ClassHandler.cs`:**
- `_handle` field + `Payload`/`Dispose` accessors: lines 304-306 and `WriteClassHandleField`/`WriteClassHandleAccessors` at lines 433-468. Condition: `classDecl.DirectSuperclassName != null`. This is correct — when the C# base class declares `_handle`/`Payload`/`Dispose`, the derived class needs `new`.
- `PInvoke_getMetadata` P/Invoke stubs: lines 686, 726, 740, 760. Condition: `HasNewModifier = _isDerived` where `_isDerived = ClassHandler.IsEffectivelyDerived(classDecl)` (line 561) returns true whenever the superclass resolved in-module *or* cross-module.

**Root cause.** `_isDerived` being true is necessary but not sufficient — `PInvoke_getMetadata` lives inside the nested `NativeMethods` class. When the *parent's* `NativeMethods.PInvoke_getMetadata` wasn't actually emitted (parent class skipped, or parent uses a different metadata accessor shape), the `new` modifier on the derived stub has nothing to shadow → CS0109. WCDB's `Statement*` hierarchy hits this because `StatementBase` either skipped or resolved to a shape that doesn't expose `PInvoke_getMetadata` in its `NativeMethods`, but every `Statement*` derived class still emits `new static partial TypeMetadata PInvoke_getMetadata(...)`.

**Fix.** Gate `HasNewModifier` on actual ancestor emission. The pattern already exists in this repo at `WrapperEmitter.Signature.cs:80` (`HasMethodInResolvedAncestors`) — call shape is "walk the resolved class chain in `TypeDatabase`, ask whether any ancestor's `NativeMethods` actually emitted `PInvoke_getMetadata`." If yes, keep `new`. If no, drop it.

Estimated edit: a new helper `HasMetadataPInvokeInResolvedAncestors(classDecl)` on `ClassHandler` (~15 lines mirroring the wrapper-side helper), plus four call-site updates at lines 686/726/740/760.

### Test gap

The existing class-inheritance fixtures should already exercise this path — they just don't *assert* on emitted warnings. Add a single assertion to a representative existing test that the generated C# compiles with zero CS0109 warnings (or a count below a baseline). Lower-cost than adding a new fixture.

---

## Skip-rate snapshot (informational)

For context on what binding generation produced before either blocker hit:

```
Module: WCDBSwift
TotalTypes:    162    EmittedTypes:    162   SkippedTypes:    0
TotalMembers:  861    EmittedMembers:  976   SkippedMembers:  410

SkippedMembersByKind: { Method: 344, Type: 21, Property: 3, Operator: 42 }

Top SkipReasons (binding-report.json):
  266  DuplicateSignature
   45  StaticProtocolMember
   32  GenericProtocolConstraint
   21  EveryProtocolConformanceSkipped
   15  AnyTypeFallback
   14  UnsupportedSignature
    6  SuppressedProxyMethodBody
    3  UnsupportedExistential
    3  UnsatisfiedGenericConstraint
    2  SynthesizedCodable
    2  UnsupportedClosure
    1  ModuleInternal
```

`DuplicateSignature` dominating at 266/410 (~65% of all skips) is interesting on its own — that count is unusual for a single-product library and may suggest the generator is emitting duplicates rather than detecting overload-resolution ambiguity in the source. Worth a closer look if/when blockers 1 and 2 are out of the way and we can get a clean compile to compare against.

---

## Artifacts left in place

The reproduction is preserved under `swift-dotnet-packages/libraries/WCDB/` (untracked):
- `library.json`, `SwiftBindings.WCDBSwift.csproj`, `README.md` — scaffold
- `WCDBSwift.xcframework/` — the built framework (23.5 MB)
- `obj/Debug/net10.0-ios/swift-binding/` — generated C#, wrapper Swift, `binding-report.json`, `WCDBSwift.Wrapper.swift`
