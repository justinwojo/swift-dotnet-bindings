# Binding API — Remaining Future Work

**Created**: February 2026
**Source**: Consolidated from `binding-review.md` (R6) and `binding-api-improvements.md` (N5, cross-cutting)
**Completed work**: See `Completed/binding-api-sessions-a-d.md`, `Completed/binding-api-review-and-improvements.md`, and `Completed/binding-api-completed-items.md`

---

## Session Plan

| Session | Work Item | Status | Depends On |
|---------|-----------|--------|------------|
| ~~A~~ | ~~ExistentialContainer in Public API~~ | **Done** | — |
| ~~B~~ | ~~Exception Mapping for Swift `throws`~~ | **Done** | — |
| ~~C~~ | ~~CancellationToken on Async Methods~~ | **Done** | — |
| ~~D~~ | ~~Async Callback → Task Wrappers~~ | **Done** | — |
| ~~E~~ | ~~Golden Scenario Validation~~ | **Done** | A |
| F | AnyType in Golden Scenarios | **Partial** — see below | E |

---

## Session E — Golden Scenario Validation (Done)

Established baseline AnyType counts and validated golden scenario libraries compile.

## Session F — AnyType Reduction (Partial)

**Root cause analysis** (correcting original documentation): The ~27 AnyType references originally attributed to `UnsafePointer<T>` were actually caused by missing `Foundation` in `AppleObjCFrameworkModules`. Foundation class types (URLResponse, URLSession, URLSessionTask, URLSessionTaskMetrics, etc.) were falling through to AnyType because the type database didn't recognize them as ObjC classes.

**Fixes applied** (2026-02-17):
1. Added `Foundation` to `AppleObjCFrameworkModules`
2. Added Foundation value types (Data, URL, UUID, URLError, URLRequest, etc.) to exclusion list
3. Fixed `UnsafePointer<T>` not being excluded from `IsBoundGeneric` (emitted broken `.Payload.DangerousGetHandle()` wrapper code)

**Results**: BlinkID 0, Nuke 0, Lottie 1 (unchanged), Alamofire 13 (was 32). See `anytype-audit-before.md` and `anytype-audit-after.md` for details.

**Remaining Alamofire residuals** (13 entries, all expected):
- Foundation value types (URLError.Code, String.Encoding): 5
- Security CF types (SecCertificate, SecKey): 3 — out of scope, CF-style types
- SystemConfiguration (SCNetworkReachabilityFlags): 1
- Closures with Foundation types: 3
- Generic associated types: 1

### Remaining AnyType Work

#### QuartzCore Auto-Bridging — Ready to implement

**Status**: Fully researched, approach validated by Claude, Gemini, Grok, and Codex. Ready for implementation.

**Problem**: Lottie's `animationLayer` (`Optional<QuartzCore.CALayer>`) remains at AnyTypeFallback. QuartzCore is not in `AppleObjCFrameworkModules`, so its types fall through to AnyType.

**Key finding** (correcting original doc): QuartzCore types are NOT re-exported through UIKit. They live in the `CoreAnimation` C# namespace in .NET iOS (`Microsoft.iOS.dll`). The Swift module name `QuartzCore` does not match the C# namespace `CoreAnimation` — this is the only confirmed Apple framework with such a mismatch.

**Verified .NET iOS types** (from `Microsoft.iOS.dll` string analysis):
- **Classes**: CALayer, CAAnimation, CABasicAnimation, CAKeyframeAnimation, CAPropertyAnimation, CASpringAnimation, CAAnimationGroup, CATransition, CAEmitterLayer, CAGradientLayer, CAMetalLayer, CAReplicatorLayer, CAScrollLayer, CAShapeLayer, CATextLayer, CATiledLayer, CATransformLayer, CARenderer, CADisplayLink, CAMetalDisplayLink, CATransaction, CAValueFunction, CAEmitterCell, CAMediaTimingFunction, CAEdrMetadata
- **Structs/Enums**: CATransform3D, CACornerMask, CAEdgeAntialiasingMask, CAContentsFormat, CACornerCurve, CADynamicRange, CAFillMode, CAGradientLayerType, CAToneMapMode, CATextLayerAlignmentMode, CATextLayerTruncationMode, CAScroll
- **Not in .NET iOS** (string-backed constants): CALayerContentsGravity — used in Lottie ABI, must be excluded

**Implementation plan** (in `TypeDatabaseExtensions.cs`):

1. Add `"QuartzCore"` to `AppleObjCFrameworkModules`

2. Add a namespace resolver method (consolidating the existing `ObjectiveC`→`Foundation` special case):
   ```csharp
   private static string ResolveObjCBridgedNamespace(string swiftModule)
   {
       if (swiftModule == ObjCModuleName || swiftModule == "Foundation")
           return "Foundation";
       if (ModuleToCSharpNamespaceOverrides.TryGetValue(swiftModule, out var ns))
           return ns;
       return swiftModule;
   }

   private static readonly Dictionary<string, string> ModuleToCSharpNamespaceOverrides = new(StringComparer.Ordinal)
   {
       { "QuartzCore", "CoreAnimation" },
   };
   ```

3. Add QuartzCore non-class types to `AppleFrameworkValueTypes`:
   ```
   QuartzCore.CATransform3D, QuartzCore.CACornerMask, QuartzCore.CAEdgeAntialiasingMask,
   QuartzCore.CAContentsFormat, QuartzCore.CACornerCurve, QuartzCore.CADynamicRange,
   QuartzCore.CAFillMode, QuartzCore.CAGradientLayerType, QuartzCore.CAToneMapMode,
   QuartzCore.CATextLayerAlignmentMode, QuartzCore.CATextLayerTruncationMode,
   QuartzCore.CAScroll, QuartzCore.CALayerContentsGravity
   ```
   Note: Types not present in `Microsoft.iOS.dll` (like `CALayerContentsGravity`) must also be excluded — they would generate references to nonexistent C# types.

4. Use `ResolveObjCBridgedNamespace()` in `CreateObjCBridgedTypeRecord` instead of inline ternary

**Test plan**:
- Unit test: `QuartzCore.CALayer` → `CoreAnimation.CALayer` (ObjCBridged class record)
- Unit test: `QuartzCore.CATransform3D` → excluded (value type, not auto-bridged)
- Unit test: `QuartzCore.CALayerContentsGravity` → excluded (not in .NET iOS)
- Integration: Regenerate Lottie bindings → verify 0 AnyType (was 1)
- Integration: Compile Lottie bindings → verify no regressions
- Full audit: Run golden scenario libraries to confirm no new compile errors

**Risk**: Low. The "KNOWN GAP" pattern (misclassifying a struct as class) produces compile-time errors, not silent runtime bugs. Any missing exclusions surface immediately.

#### Context-Aware `Any` Translation — Shelved

**Status**: Investigated thoroughly, **blocked by runtime limitation**. Shelved until runtime metadata enhancement.

**Research summary** (Claude + Gemini + Grok + Codex review):
- Bare `Any` (existential with 0 protocols) → `object` via `ExistentialHandler.GetPublicExistentialType()`
- `object` causes CS0311 when used as generic arg where `ISwiftObject` constraint exists
- Only `SwiftOptional<T>` has no `ISwiftObject` constraint, so `SwiftOptional<object>` would compile
- **P0 blocker** (Codex): `SwiftOptional<object>` is not runtime-safe — `TypeMetadata.GetTypeMetadataOrThrow<object>()` throws because there is no Swift type metadata registration for C# `object`. The failure moves from compile-time to runtime, which is worse.
- **P1 blocker** (Codex): Even if `HasNonSwiftObjectGenericArg` is relaxed, additional gates in `MemberEmissionValidator` and `MethodHandler` also reject when public type resolves to `"object"` — multiple code paths would need changes
- The current AnyType fallback for bare `Any` in generic positions is the correct behavior given runtime constraints

**What would unblock this** (future work):
- Register Swift `Any` metadata in the runtime so `TypeMetadata.GetTypeMetadataOrThrow<object>()` succeeds
- Or introduce a `SwiftAny` wrapper type that implements `ISwiftObject` and wraps an existential container
- Either approach requires runtime library changes (`Swift.Runtime`), not just generator changes

**Other interop precedents** (Gemini/Grok research):
- Swift/Java: uses type erasure, `Any` → `Object`, constraint violations are runtime failures (worse than our compile-time skip)
- Kotlin/Native: `Any` → `Any?` (nullable), dynamic checks at runtime
- PythonNet: `Any` → `object`, no generics (dynamic typing)
- Our static AnyType fallback is strictly better than all of these — compile-time skip vs runtime crash

---

## Quality Scorecard — Remaining Gates

| Metric | Gate | Status | Unblocked By |
|--------|------|--------|--------------|
| ~~Public `ExistentialContainer*` for `any Error`~~ | ~~0~~ | **Done** (mapped to `AnyError`) | ~~Session A~~ |
| Golden scenarios AnyType reduction | 3/4 | **Partial** (BlinkID 0, Nuke 0, Lottie 1 — QuartzCore fix ready, Alamofire 32→13) | Session F + QuartzCore fix |
| Context-aware `Any` in generics | N/A | **Shelved** — blocked by runtime metadata (`TypeMetadata` can't resolve `object`) | Runtime `SwiftAny` type |
| ~~Typed Swift error exceptions~~ | ~~Yes~~ | **Done** | ~~Session B~~ |
| ~~Async cancellation support~~ | ~~Yes~~ | **Done** | ~~Session C~~ |
