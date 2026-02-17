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

These were investigated but reverted. They remain open for future sessions:

- **QuartzCore auto-bridging** — QuartzCore types (CALayer, etc.) don't have a standalone C# namespace in .NET iOS (re-exported through UIKit). Lottie's `animationLayer` (`Optional<QuartzCore.CALayer>`) remains at AnyTypeFallback because of this. Potential fix: map QuartzCore types to their UIKit re-export namespaces.
- **Context-aware `Any` translation** — Bare `Any` → `object` causes CS0311 in generics with `ISwiftObject` constraint (e.g., `Keyframe<object>`, `SwiftArray<object>`). Only `SwiftOptional<T>` has no constraint. Fix requires the marshaler to check the target generic's constraints before choosing `object` vs `AnyType`.

---

## Quality Scorecard — Remaining Gates

| Metric | Gate | Status | Unblocked By |
|--------|------|--------|--------------|
| ~~Public `ExistentialContainer*` for `any Error`~~ | ~~0~~ | **Done** (mapped to `AnyError`) | ~~Session A~~ |
| Golden scenarios AnyType reduction | 3/4 | **Partial** (BlinkID 0, Nuke 0, Lottie 1 — QuartzCore can't auto-bridge, Alamofire 32→13) | Session E + F |
| ~~Typed Swift error exceptions~~ | ~~Yes~~ | **Done** | ~~Session B~~ |
| ~~Async cancellation support~~ | ~~Yes~~ | **Done** | ~~Session C~~ |
