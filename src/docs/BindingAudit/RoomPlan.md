# RoomPlan — Binding Audit

- **Package**: SwiftBindings.Apple.RoomPlan v26.2.8   **Mode**: apple   **TFM(s)**: net10.0-ios26.2
- **Native**: Apple RoomPlan framework (iOS 16.0+/17.0+)
- **Audited at**: swift-dotnet-packages main `1e8c27a`, binding generated 2026-06-27

## Verdict

The core scan-and-read-room flow is **usable**: `RoomCaptureSession.Run`/`Stop`, `RoomBuilder.CapturedRoomAsync`, and all `CapturedRoom` geometry (`Walls`, `Doors`, `Windows`, `Openings`, `Floors`, `Objects`) are bound with correct `Matrix4x4`/`Vector3` transforms and proper `async Task` surfacing. The primary usability risk is `RoomCaptureView.Delegate` — the EveryProtocol proxy for `IRoomCaptureViewDelegate` was not emitted, so apps using the all-in-one view cannot receive scan-completion callbacks from Swift; the session-delegate flow works fine as a workaround. A secondary structural gap is `CapturedStructure` (multi-story output): only `Rooms` and scalar fields surface — the 7 element-category geometry arrays all fall to `AnyTypeFallback`. Tests cover metadata resolution and enum ordering only; no ABI-crossing geometry or async tests exist, which is unavoidable on simulator (LiDAR requires hardware) but JSON round-trip tests are feasible.

---

## 1. Coverage

### Totals

| Dimension | Count | % |
|---|---|---|
| Types emitted / total | 39 / 39 | 100% |
| Members emitted / total | 142 / 188 | 75.5% |
| Skipped members | 43 | — |
| Synthesized members (generator-added) | 107 | — |

### Skip-reason breakdown

| Reason | Count | Classification |
|---|---|---|
| SynthesizedCodable | 18 | ✅ Correctly excluded |
| AnyTypeFallback | 8 | ⚠️ Real gap (mixed severity — see below) |
| UnsupportedSignature | 6 | ⚠️ Real gap (partial) |
| UnsupportedExistential | 5 | ⚠️ Real gap (partial) |
| DuplicateSignature | 3 | ⚠️ Real gap (information loss) |
| EveryProtocolConformanceSkipped | 2 | 🔴 Functional gap (one critical) |
| GenericProtocolConstraint | 1 | ⚠️ Real gap (tied to existential) |

**Real gaps after removing SynthesizedCodable**: 25 skipped members.

---

### (a) Correctly excluded

**SynthesizedCodable (18)** — `encode(to:)` / `init(from:)` on `CapturedRoom`, `CapturedRoom.Surface`, `CapturedRoom.Object`, `CapturedRoom.Section`, `CapturedRoom.AttributesCodableRepresentation`, `CapturedRoomData`, `CapturedStructure`, `CapturedRoom.Surface.Curve`, `CapturedElementCategory`. All correctly pruned per project policy. The generator compensates by synthesizing `EncodeToJson()`/`DecodeFromJson(byte[])` on Codable types, providing a usable JSON serialization path (`RoomPlan.cs:10515`, `10553`).

---

### (b) Real gaps

#### AnyTypeFallback (8) — mixed severity

**`RoomCaptureView.captureSession`** (`RoomPlan.cs:6142`): `captureSession` typed as `RoomCaptureSession` at the Swift level but resolved to `AnyType` by the generator (the property type is `RoomCaptureSession` in some Swift API shapes but treated as opaque here). A dev using `RoomCaptureView` must construct a `RoomCaptureSession` separately and pass it to `RoomCaptureView(frame:, arSession:)` (iOS 17+) or track the session alongside the view. Minor ergonomic issue — workaround is straightforward.

**`CapturedStructure.walls/doors/windows/openings/objects/floors/sections` (7)** (`RoomPlan.cs:6943–6949`): All seven element-category array properties on `CapturedStructure` fall to `AnyTypeFallback`. In `CapturedRoom` (the per-room type from `RoomBuilder`), the identical properties ARE bound correctly as `IReadOnlyList<CapturedRoom.Surface>` and `IReadOnlyList<CapturedRoom.Object>`. The root cause is that `CapturedStructure` exposes these as protocol-erased collections (`[any CapturedRoom.Surface]`/`[any CapturedRoom.Object]`-ish existential arrays) rather than concrete typed arrays. The only available `CapturedStructure` API is `Rooms` (→ `IReadOnlyList<CapturedRoom>`), `Identifier`, and `Version`. For a single-room workflow this is no loss; for multi-room `StructureBuilder` use, callers must iterate `Rooms` and aggregate manually. **Worth a generator fix** if multi-story StructureBuilder use is prioritised: the concrete element types are known, so a typed projection from the existential is feasible.

#### DuplicateSignature (3) — information loss

Three `captureSession()` methods on `RoomCaptureSessionDelegate` are dropped because they resolve to the same C# overload signature that is already emitted. The four surfaced `IRoomCaptureSessionDelegate` methods (`RoomPlan.cs:5264–5271`) cover:
- `CaptureSession(session, CapturedRoom)` — maps both `didUpdate(room:)` and the skipped `didAdd(room:)` / `didChange(room:)`
- `CaptureSession(session, Instruction)`
- `CaptureSession(session, Configuration)`
- `CaptureSession(session, CapturedRoomData, AnyError?)`

The skipped `captureSession(_:didAdd:)` and `captureSession(_:didChange:)` collapse into the emitted `CaptureSession(session, CapturedRoom)` — incremental-update and final-change events are indistinguishable in C#. The proxy IS generated (`RoomCaptureSessionDelegateProxy`, `RoomPlan.cs:12296`) and callbacks arrive correctly; callers just lose the add-vs-change distinction. **Worth a generator fix** with renamed overloads (`CaptureSessionDidAdd`, `CaptureSessionDidChange`) to disambiguate.

#### EveryProtocolConformanceSkipped (2) — one functional, one peripheral

**`RoomCaptureViewDelegate.RoomCaptureViewDelegateProxy`** 🔴: No EveryProtocol conformance proxy was emitted for `IRoomCaptureViewDelegate`. The interface is generated (`RoomPlan.cs:6494–6502`) and the `Delegate` setter on `RoomCaptureView` compiles (uses `ExistentialContainerFactory.GetOrCreate` without a proxy lambda, `RoomPlan.cs:6188`). However, the getter throws `NotSupportedException` (`RoomPlan.cs:6145`) and — more critically — Swift cannot reverse-dispatch `captureView(shouldPresentProcessedResults:)` / `captureView(didPresent:)` back into managed code because there is no proxy. Assigning an `IRoomCaptureViewDelegate` implementation to `RoomCaptureView.Delegate` is silently non-functional for callbacks. **The session-delegate path (`IRoomCaptureSessionDelegate` on `RoomCaptureSession.Delegate`) works fully** and is the recommended workaround. This gap blocks the "drop in a `RoomCaptureView` and implement the view delegate" integration path. **High value generator fix**: emit the `RoomCaptureViewDelegateProxy`.

**`CapturedRoomAttribute.CapturedRoomAttributeProxy`**: The `CapturedRoomAttribute` protocol is primarily expressed through concrete attribute types (`ChairType`, `SofaType`, `TableType`, `StorageType`, etc.), all of which are fully bound. No use case requires implementing this protocol from C#. Peripheral gap.

#### UnsupportedExistential (5) — mostly peripheral

**`CapturedRoom.Object.attributes`** (`bound generic contains existential 'any CapturedRoomAttribute'`): The per-object attribute collection is dropped. A `CapturedRoom.Object` surfaces its `Category` (e.g. `Chair`, `Sofa`), `Dimensions`, `Transform`, `Confidence`, `Identifier` — but NOT the detected fine-grained attribute (e.g. `ChairType.Dining`, `SofaType.LShaped`). For a spatial-data app that only needs bounding-box geometry and object category, this is acceptable; for furniture-attribute analysis it is a hard limit. **Worth a generator fix** via existential `any CapturedRoomAttribute` array projection.

**`CapturedRoom.AttributesCodableRepresentation.attributes` + `init`**: Internal serialization helper — not consumer-facing. Not worth a fix.

**`CapturedRoom.ModelProvider.modelFileURL(for:)` + `setModelFileURL(_:for:)` (existential key variants)**: The attribute-keyed overloads are dropped, but concrete alternative overloads are generated — `ModelFileURL(CapturedRoom.Object category)` and `ModelFileURL(CapturedRoom.Object.CategoryType category)` (`RoomPlan.cs:11601, 11638`). The common use case (look up a model file by object category) is covered. Not a priority.

#### UnsupportedSignature (6) — mixed

- `CapturedRoom.Confidence.encode(to:)`, `Surface.Edge.encode(to:)`, `Object.Category.encode(to:)` — Codable enum extension methods. Not critical; `EncodeToJson`/`DecodeFromJson` on the parent struct covers serialization.
- **`Object.Category.supportsCombination(with:)`** — tests whether two furniture categories can be combined in a single scan. Moderate value.
- **`Object.Category.supportedAttributeTypes`** — lists which attribute types apply to a given category (e.g. `Chair → [ChairType, ChairLegType, ChairArmType, ChairBackType]`). Useful for building attribute pickers. Blocked by enum extension with set return type. **Worth a Swift wrapper**.
- **`Object.Category.supportedCombinations`** — which category combinations are valid in one capture. **Worth a Swift wrapper** alongside `supportedAttributeTypes`.

#### GenericProtocolConstraint (1)

**`CapturedRoom.Object.attribute<T: CapturedRoomAttribute>()`** — typed attribute accessor blocked by associated-type constraint on the protocol. Resolving `Object.attributes` (above) is the higher-priority unlock that subsumes this.

---

### Prioritised generator unlocks

| Priority | Gap | Mechanism | Value |
|---|---|---|---|
| 1 | `RoomCaptureViewDelegate` proxy (EveryProtocol conformance) | EveryProtocol emission for this carrier | High — unblocks the primary view-based workflow |
| 2 | `CapturedStructure` 7× AnyTypeFallback geometry arrays | Existential concrete projection on protocol-erased arrays | Medium — needed for multi-story StructureBuilder |
| 3 | `CapturedRoom.Object.attributes` (existential `[any CapturedRoomAttribute]`) | Bound-generic existential projection | Medium — fine-grained attribute extraction |
| 4 | `DuplicateSignature` delegate overload disambiguation | Emit renamed overloads (`DidAdd`, `DidChange`) | Low-Medium — currently collapses add/change events |
| 5 | `Object.Category.supportedAttributeTypes` + `supportedCombinations` | Swift wrapper with concrete return types | Low — schema metadata for attribute pickers |

---

## 2. C# Quality

### Naming and shape — Clean

PascalCase throughout. No leaked Swift mangling. Nested types (`CapturedRoom.Surface`, `CapturedRoom.Object`, `CapturedRoom.Section`, `RoomCaptureSession.Configuration`, etc.) are expressed as C# nested classes. `CapturedRoom.Object.CategoryType` as a plain `enum : int` with 16 furniture cases (`RoomPlan.cs:10490`) is idiomatic. `IRoomCaptureSessionDelegate` / `IRoomCaptureViewDelegate` prefix matches project convention.

### Geometry types — Excellent mapping

Swift `simd_float4x4` → `System.Numerics.Matrix4x4` (`RoomPlan.cs:8909, 10234`); `simd_float3` → `System.Numerics.Vector3` (`RoomPlan.cs:8867, 10192`). Both are correct blittable mappings. `UUID` → `System.Guid` (`RoomPlan.cs:8914, 10239`). These are idiomatic .NET types — a spatial computing consumer has everything they need to feed a scene graph.

### Async — Correct

`RoomBuilder.capturedRoom(from:)` → `Task<CapturedRoom> CapturedRoomAsync(CapturedRoomData, CancellationToken)` (`RoomPlan.cs:5862`). Full `CancellationToken` wiring with `SBW_CancelTask` integration. `StructureBuilder.build(capturedStructure:)` → `Task<CapturedStructure> BuildAsync(...)` (iOS 17+). Proper `async Task` for both. No blocking-only surfaces.

### Nullability — Correct

Delegate properties (`RoomCaptureSession.Delegate`, `RoomCaptureView.Delegate`) → `IRoomCaptureSessionDelegate?` / `IRoomCaptureViewDelegate?`. `CapturedRoom.Surface.ParentIdentifier` → `Guid?`. Optional `ARSession` parameter on `RoomCaptureView.init` → `ARSession?`. No missing or contradictory annotations found.

### Lifetime — Correct

All Swift struct wrappers implement `IDisposable` with `_payload.Dispose()` + `GC.SuppressFinalize`. Swift class wrappers (`RoomCaptureSession`, `RoomBuilder`, `RoomCaptureView`, `StructureBuilder`) implement `IDisposable` with ARC handle release; finalizers handle GC-collected cleanup. `SwiftDisposeScope.TryRegister` used throughout. `_isCachedSingleton` guard prevents double-dispose on cached enum singleton instances (`RoomPlan.cs:51`).

### Delegate interface naming — Minor awkwardness

All four `IRoomCaptureSessionDelegate` methods are named `CaptureSession()` (e.g. `CaptureSession(session, CapturedRoom)`, `CaptureSession(session, Instruction)`) — correctly reflecting the Swift `captureSession(_:...)` naming, but unusual for a C# interface. A consumer implementing the interface has four overloads of the same method name, which IDE tooling handles adequately but reads oddly. This matches the project's 1:1 binding philosophy; no fix warranted.

### `[SwiftMainActor]` annotations — Present and correct

`RoomCaptureView.init`, `Delegate`, `IsModelEnabled` all carry `[SwiftMainActor]` and an `AssertMainThread()` guard (`RoomPlan.cs:6384`). Consumers are warned at the right granularity.

### `RoomCaptureView.Delegate` getter — Broken

`RoomPlan.cs:6143–6145`: `Delegate_Get()` unconditionally throws `NotSupportedException("Protocol proxy not available: EveryProtocol conformance was not emitted.")`. The property has a `get` and `set`, but `get` is always broken. This is expected given the missing proxy, but it means code doing `if (view.Delegate != null)` will throw rather than returning `null`. The comment on the property at `RoomPlan.cs:6176` gives no hint that `get` is broken — the doc should note the limitation. Not a generator priority but a documentation gap.

---

## 3. Test Coverage

### Summary

**20 test cases** in `Tests.Run()` (`tests/Tests.cs`); no test cases in `Program.UIKit.cs` (app harness only).

| Test category | Count | Depth |
|---|---|---|
| `MetadataTest<T>` (symbol resolution) | 9 | Weak — checks handle non-null, no ABI |
| Enum value assertions (`CaptureError`, `Instruction`, `BuildError` × 2, `Error`, `Confidence`, CaseTags) | 6 | Weak — pure C# constant checks |
| CaseTag round-trips (`ChairType`, `SofaType`, `TableType`, `StorageType`) | 4 | Weak-medium — crosses metadata VWT but no Swift call |
| `GetErrorDescription` cdecl round-trips | 3 | Medium — actual P/Invoke cross-boundary |

### Depth assessment

Only the 3 `GetErrorDescription` tests (`RoomCaptureSession.CaptureError`, `RoomBuilder.BuildError`, `CapturedRoom.Error`) make real cross-boundary Swift calls. The metadata and enum tests exercise type registration but do not prove ABI.

### Untested surface

| Surface | Why it matters |
|---|---|
| `CapturedRoom.Walls / Doors / Windows / Objects / Floors / Sections` | Core geometry output — the primary consumer value |
| `CapturedRoom.Surface.Transform` (`Matrix4x4`) | If column/row order is wrong, all spatial positions are garbage |
| `CapturedRoom.Surface.Dimensions` (`Vector3`) | Bounding-box readout |
| `CapturedRoom.Surface.Category` (payload enum `CategoryType`) | Tag extraction from Swift discriminated union |
| `RoomBuilder.CapturedRoomAsync` | Only async API — tests CancellationToken wiring, error propagation |
| `CapturedRoom.Export(NSUrl)` | USD export — main persistence path |
| `RoomCaptureSession.Run` / `Stop` | Session lifecycle (hardware-only, but smoke test with invalid state is feasible) |
| `CapturedRoom.EncodeToJson` / `DecodeFromJson` | JSON round-trip is simulator-friendly and verifies Codable substitute |

### Legitimate gaps

LiDAR scanning requires a physical LiDAR-equipped device; `RoomCaptureSession.Run` and the full capture workflow cannot be exercised on Simulator. All session-level and geometry tests are appropriately omitted from the sim suite.

### Recommended tests to add

1. **`CapturedRoom.EncodeToJson` / `DecodeFromJson` round-trip** — construct a `CapturedRoom` via JSON fixture bytes (a real exported room scan), decode, assert non-empty `Walls`, non-empty `Objects`, and that a sampled `Transform.M44` is `1.0f`. Simulator-safe. Verifies the primary geometry ABI.
2. **`CapturedRoom.Surface.Transform` layout** — from a decoded `CapturedRoom`, read `Walls[0].Transform` and assert `M44 == 1.0f` (homogeneous matrix convention). Catches column/row order bugs.
3. **`CapturedRoom.Surface.Category` tag extraction** — from decoded walls, call `Category.Tag` and assert it equals `CategoryType.CaseTag.Wall`. Verifies payload enum discriminant.
4. **`CapturedRoom.Object.Category` enum round-trip** — from decoded objects, assert `Category` is in the known `CategoryType` range. Verifies the int-to-enum cast at `RoomPlan.cs:10096`.
5. **`RoomBuilder.CapturedRoomAsync` cancellation** — cancel immediately with an already-cancelled token, assert `TaskCanceledException`. No hardware needed; verifies the cancel-before-submit guard at `RoomPlan.cs:5886`.
6. **`CapturedRoom.Export` error path** — call `Export(url)` with a non-USDZ file extension, assert throws `SwiftError` with `UrlInvalidFileExtension`. Validates the error-path P/Invoke.

All six are simulator-safe and require only a JSON fixture of a real captured room (small, can be committed to `tests/`).

---

## Action Items

| # | Dimension | Finding | Recommendation | Effort | Value |
|---|---|---|---|---|---|
| 1 | Coverage | `RoomCaptureViewDelegate` proxy not emitted — setting `RoomCaptureView.Delegate` is silently non-functional for callbacks (`RoomPlan.cs:6145`) | Emit EveryProtocol conformance proxy for `IRoomCaptureViewDelegate` | Medium | High |
| 2 | Coverage | `CapturedStructure` 7 geometry properties dropped (`AnyTypeFallback`) — only `Rooms` array is accessible for multi-room output (`RoomPlan.cs:6943–6949`) | Concrete projection from protocol-erased element arrays; or Swift wrappers returning typed arrays | Medium | Medium |
| 3 | Coverage | `CapturedRoom.Object.attributes` dropped (`UnsupportedExistential`) — no fine-grained attribute readout for captured furniture | Existential `[any CapturedRoomAttribute]` array projection | High | Medium |
| 4 | Coverage | 3 `captureSession()` delegate overloads collapsed (`DuplicateSignature`) — `didAdd` and `didChange` indistinguishable in C# | Emit disambiguated overloads: `CaptureSessionDidAdd(session, CapturedRoom)`, `CaptureSessionDidChange(session, CapturedRoom)` | Low | Medium |
| 5 | Coverage | `Object.Category.supportedAttributeTypes` and `supportedCombinations` dropped (`UnsupportedSignature`) | Add Swift wrapper methods with concrete set return types | Low | Low |
| 6 | C# Quality | `RoomCaptureView.Delegate` getter throws `NotSupportedException` with no documentation hint that it is broken | Add `/// <remarks>` to the property noting the getter is unavailable until the proxy is emitted | Trivial | Low |
| 7 | Tests | Zero ABI-crossing geometry or async tests | Add 6 simulator-safe tests: JSON round-trip, Transform layout, Surface.Category tag, Object.Category enum, CancelledTask fast path, Export error path (requires a small JSON fixture committed to `tests/`) | Low | High |
