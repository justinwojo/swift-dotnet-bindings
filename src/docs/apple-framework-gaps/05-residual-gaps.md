# Session 05 — Residual gaps after the 0.12.0 validation pass

This doc records the gaps that remain **after** SDK 0.12.0 / Apple 26.2.4 shipped, discovered while validating the Session 01–04 fixes end-to-end against the real consumer packages in `swift-dotnet-packages` (not the generator's own BindingTests harness). It is the deliverable of the pre-ship double-check: *which claimed fixes actually came through, and which didn't.*

**Validation method.** For each fix, the relevant per-package test was un-skipped / added and run on **simulator (Mono JIT)** and, where Session 01–03 gated it, on a **physical device (NativeAOT)** — iPhone 13, 2026-05-29. A test that merely exercises a type's metadata is not enough; these tests drive the actual fixed API end-to-end and assert a correct result. Tests for gaps that did *not* land are left as explicit `Skip(...)` with a reason pointing here, so a future SDK rebuild re-checks them rather than silently assuming they work.

## TL;DR — what landed vs what didn't

| Fix (session) | Sim | Device | Verdict |
|---|---|---|---|
| RC‑SIMD Transform setters + `Transform(Matrix4x4)` (01) | ✅ | ✅ | **Landed** |
| RealityKit cross-module reads (`CameraTransform`, `Environment.SceneUnderstanding`) (01/03) | ✅ | ✅ | **Landed** |
| CryptoKit incremental `HMAC<H>` via CSM factories (02) | ✅ | ⚠️ skip | **Landed on sim; device blocked** (§3) |
| ProximityReader `GetErrorDescription` @_cdecl parity (04) | ✅ | — | **Landed** |
| MusicKit `Create` array-shims + `MusicItemProxy` (04) | ✅ | — | **Landed** |
| WorkoutKit `SwiftClosedRange<Bound>` ctors emitted (01) | ✅* | — | **Partial** — ctors emit but un-callable (§4) |
| **RC‑PROXY Failure A — `Scene.AddAnchor(IHasAnchoring)` (03)** | ❌ | ❌ | **Did NOT land** (§1) |
| **RC‑SWIFTUI — `FamilyActivityPicker` bridge (04)** | ❌ | — | **Did NOT land** (§2) |
| RC‑PROXY Failure B — gestures (03, the "L" item) | n/a | n/a | **Deferred as planned** (§5) |

\* WorkoutKit metadata + enums + simple ctors all pass; the four range-alert ctors are emitted but cannot be called (§4).

---

## §1 — RC‑PROXY Failure A did NOT land: `Scene.AddAnchor(IHasAnchoring)` still throws

**Status: confirmed broken, sim + device. Highest-impact finding.**

Session 03 Task 1 set out to fix `Scene.AddAnchor` by splitting the conformance-descriptor dictionary emission (`ShouldEmitConformanceDictionary`) from the C# interface emission (`ShouldEmitConformanceInterface`), so `AnchorEntity`'s `_protocolConformanceSymbols` dict would get the `IHasAnchoring` descriptor symbol even though the cross-module protocol has members. **The predicate-split code shipped in 0.12.0** — verified: `Swift.Bindings.dll` in `SwiftBindings.Sdk/0.12.0` contains `ShouldEmitConformanceDictionary`, and the fix commit `dda69dff` is an ancestor of the 0.12.0 build commit `e8560eb`.

**But the fix does not cover this case.** In the freshly-generated bindings (SDK 0.12.0, regenerated 2026-05-29):

```csharp
// apple-frameworks/RealityFoundation/obj/.../RealityFoundation.cs  (AnchorEntity, ~110612)
public partial class AnchorEntity : Entity, ISwiftObject, IHasAnchoring, IEquatable<AnchorEntity>
{
    private static Dictionary<Type, string> _protocolConformanceSymbols;
    static AnchorEntity()
    {
        _protocolConformanceSymbols = new Dictionary<Type, string>
        {
            // EMPTY — no IHasAnchoring descriptor symbol
        };
    }
    // No BoxAsExistential1 override; inherits Entity's, which hardcodes Create<Entity, TProtocol>(this)
}
```

Two compounding problems:

1. **The dict is empty.** `ShouldEmitConformanceDictionary` (`TypeHandlerHelpers.cs:1484`) returns `false` for `AnchorEntity : HasAnchoring` because the gate at `:1495` requires `typeDatabase.TryGetTypeRecord(conformance.Protocol, ...)` to succeed, and the cross-module/umbrella protocol `HasAnchoring` has **no loaded `TypeRecord`** during RealityFoundation generation (its mangled name encodes the RealityKit umbrella; the protocol record isn't in the loaded module database). The comment at `:1493-1494` describes exactly this path: *"Cross-module protocols require a loaded module database (--module-database) to have a TypeRecord; without one they are silently skipped."* So the conformance is silently skipped — the predicate split changed nothing for the one case it was written to fix.

2. **`BoxAsExistential1` uses the wrong concrete type.** `AnchorEntity` does not override `IExistentialBoxable.BoxAsExistential1<TProtocol>()`; it inherits `Entity`'s, which is hardcoded to `ExistentialContainerFactory.Create<Entity, TProtocol>(this)` (`RealityFoundation.cs:103236`). Even if the descriptor existed, the witness-table lookup would be for `Entity : HasAnchoring` (which doesn't exist — only `AnchorEntity` conforms) rather than `AnchorEntity : HasAnchoring`.

**Runtime evidence (both lanes):**
- Live end-to-end (RealityKit, `arView.Scene.AddAnchor(anchor)`): `TargetInvocationException` ("Exception has been thrown by the target of an invocation") — sim **and** device.
- Headless box (RealityFoundation, `ExistentialContainerFactory.GetOrCreate<IHasAnchoring>(anchor)` — the exact call the generated `AddAnchor` makes, `RealityFoundation.cs:85060`): same throw, both lanes.

**Impact.** Core AR anchor placement is still blocked from C#: `Scene.AddAnchor`, `Scene.RemoveAnchor`, `AnchorCollection.Append/Remove/ReplaceAll`, and `AnchorCollection`'s indexer setter all box `IHasAnchoring` via `GetOrCreate<IHasAnchoring>` and therefore all throw. `Scene.Anchors` *read* traversal and `AnchorEntity` *construction* work (validated, real passes) — you just can't put the anchor into the scene graph.

**Same family — not retested this pass:** RoomPlan `RoomCaptureView.Delegate` (the view-callback path, `RoomPlan.cs:5790`) is the RC‑PROXY secondary site flagged in 03-proxy-callback.md Task 1. Session 03 said to confirm it shares Failure A's shape before claiming it; given Failure A itself didn't land, RoomPlan's view delegate is presumed still dead (the **session** delegate, `RoomCaptureSessionDelegateProxy` `RoomPlan.cs:11539`, is the supported path and is unaffected).

**Fix direction (for the next SDK).** The predicate split is necessary but not sufficient. Either (a) load the declaring/umbrella module's `TypeRecord` for cross-module protocols so `ShouldEmitConformanceDictionary` doesn't silently skip, **and** (b) make the generated `BoxAsExistential1<TProtocol>()` dispatch on the *runtime* concrete type (or override it per-subclass so `AnchorEntity` emits `Create<AnchorEntity, TProtocol>`), populating `AnchorEntity._protocolConformanceSymbols` with the real `IHasAnchoring` descriptor symbol. A BindingTests fixture that boxes a **subclass-only** cross-module conformance (subclass conforms to a protocol the base does not) would have caught this — the existing fixtures box on the declaring class itself.

---

## §2 — RC‑SWIFTUI did NOT land in packaging: `FamilyActivityPicker` throws `DllNotFoundException`

**Status: confirmed broken, sim. Generator side landed; consumer-package packaging side missing.**

Session 04 Task 3 extended the SwiftUI bridge to ferry `Binding<Codable Struct>` as JSON, making `FamilyActivityPicker(selection:)` presentable from C#. The generator emits **both** halves of the bridge into the package's `obj/.../swift-binding/`:
- C#: `FamilyControls.SwiftUIBridge.cs` — `[LibraryImport("FamilyControlsBridge", EntryPoint = "SBW_FamilyControls_FamilyActivityPicker_*")]`
- Swift: `FamilyControls.SwiftUIBridge.swift` — the matching `@_cdecl` trampolines.

**But the native `FamilyControlsBridge` library is never built or bundled.** Verified with `nm` across every binary in the built `.app`: the wrapper framework `FamilyControlsSwiftBindings.framework` exports the normal `SBW_FamilyControls_*` symbols (AuthorizationCenter, FamilyActivitySelection, …) but **none** of the `SBW_FamilyControls_FamilyActivityPicker_*` bridge symbols, and there is no `FamilyControlsBridge` dylib/framework anywhere in the bundle. The generated bridge Swift (`FamilyControls.SwiftUIBridge.swift`) is not fed to the wrapper's `swiftc` invocation, and no separate `FamilyControlsBridge` build target exists.

**Runtime evidence:** `FamilyActivityPickerSession.Create(...)` → `DllNotFoundException("FamilyControlsBridge")` (sim).

**Why it slipped through.** The fix was validated only in swift-bindings' own BindingTests harness via the `CodableProfileEditorView` fixture, which compiles *all* generated sources (including the bridge swift) into one test module — so `@rpath`/library naming lines up there. 04-targeted-shims.md Task 3 says so explicitly: *"What is genuinely missing is an in-process UI-driven trigger… out of scope for this session. … the next step is a fixture in `swift-dotnet-packages` that mounts the picker via UIHostingController."* That consumer-package path is what this validation exercised, and it surfaces the packaging gap.

**Impact.** Unchanged from before Session 04: you can construct/read/persist/apply a `FamilyActivitySelection` (those tests pass), but you cannot present the `FamilyActivityPicker` that produces one. The display-only `FamilyActivityTitleViewSession`/`IconViewSession` share the same `FamilyControlsBridge` import and are presumed equally unreachable.

**Fix direction.** Teach the SDK build/pack pipeline to compile `*.SwiftUIBridge.swift` into a native library named exactly `FamilyControlsBridge` (matching the C# `[LibraryImport]` name), bundle it as a `NativeReference`, and add a per-package end-to-end fixture (UIHostingController + programmatic selection round-trip) so the packaging path is gated, not just the emitter.

---

## §3 — CSM `HMAC<H>` works on sim but not on device: `@rpath` system-framework load

**Status: confirmed; sim ✅, device ⚠️. New this pass.**

Session 02 restored the `HMAC<H>` CSM factories (`HMACSHA256CsmExtensions.FromSHA256`, `.AuthenticationCode`, `.Update`, `.Finalize`, and the SHA384 sibling). End-to-end on **sim (Mono)** these are correct: incremental `Update`/`Finalize` produces a MAC bit-for-bit identical to the one-shot `AuthenticationCode` (`ByteCount` 32/48). Validated, real passes.

On **device (NativeAOT)** the generated `HMAC{SHA256,SHA384}CsmExtensions` static class fails to initialize on the first call:

```
TypeInitializationException -> SwiftRuntimeException:
    Unable to load library: @rpath/CryptoKit.framework/CryptoKit
```

The CSM factory's static initializer resolves the hash conformer's protocol-conformance descriptor by `dlopen`-ing the framework via `@rpath/CryptoKit.framework/CryptoKit` (the same `ProtocolConformanceDescriptor.LoadFromSymbol("@rpath/<Framework>.framework/<Framework>", symbol)` mechanism seen in `RealityFoundation.cs`). On a physical device that path does not resolve: **CryptoKit is an Apple *system* framework** at `/System/Library/Frameworks`, not bundled in the app's `@rpath`. On the simulator the runtime resolves `@rpath` to the framework, so it works there. The other 40 CryptoKit tests (AES.GCM, SHA, key types, …) pass on device — only the CSM conformance-descriptor load path hits this.

Session 02 device-validated the CSM ctor ABI via the `KeyedBag<…>` BindingTests fixture, which lives in a **non-system** test module where `@rpath` is valid — so the real CryptoKit (system-framework) regen had never been device-validated until now.

**Impact.** Incremental `HMAC<H>` is usable on sim but not on a shipped NativeAOT device build. The per-package test now gates on `RuntimeFeature.IsDynamicCodeSupported`: it runs and asserts on the JIT/sim lane, and skips with this reason on the AOT/device lane.

**Fix direction.** The generated CSM conformance-descriptor load must address *system* frameworks by their install name / absolute system path (or via an already-loaded handle) rather than `@rpath`, for Apple-framework bindings. This likely generalizes to any CSM specialization over an Apple-system-framework type on device.

---

## §4 — WorkoutKit range alerts: `ClosedRange` ctors emit but bounds aren't constructible

**Status: partial; the un-callable surface is `Measurement<T>`, by design.**

Session 01 added the `Swift.SwiftClosedRange<Bound>` stdlib generic, so the `HeartRateRangeAlert` / `CadenceRangeAlert` / `PowerRangeAlert` / `SpeedRangeAlert` constructors are no longer dropped — they emit as callable signatures (no `[UnsupportedSwiftType]` stub; verified in the generated `WorkoutKit.cs`). That half landed.

But every range-alert ctor takes `SwiftClosedRange<Measurement<NSUnit…>>`, and the Foundation projection `Measurement<T>` is **value-only**: it exposes `.Value` but has no public constructor (only `internal Measurement(IntPtr)` — `Swift.Bindings.Apple/Sources/Foundation/Measurement.cs:136`). So the bounds cannot be minted from C#, and the alerts remain non-constructible end-to-end despite the `ClosedRange` fix.

`Measurement<T>` being value-only is catalogued as **by design** in the action list (§4 "Not gaps", `apple-framework-binding-gaps.md:208,246`) — it's Foundation type behavior, not a binding defect. The practical consequence, though, is that WorkoutKit's range alerts are un-constructible. If we want them usable, the fix is a targeted shim (per-framework `@_cdecl` trampoline that builds a `Measurement<Unit>(value:unit:)` from a double + unit, mirroring the MusicKit array-shim pattern), not a generator change.

The four range-alert ctors are left as `Skip(...)` in the WorkoutKit test with this reason; metadata, enums, and the non-Measurement ctors (`WorkoutStep`, `IntervalStep`, `IntervalBlock`) all pass.

---

## §5 — Known gaps confirmed still open (never claimed fixed / deferred as planned)

These are not regressions; they're documented here so the per-package skips have a single referent.

- **RC‑SIMD multi-param ctor — `Transform(scale:rotation:translation:)`.** The indirect/pointer marshalling fix reached the SIMD *setters* and the `Transform(Matrix4x4)` ctor (both validated, sim + device), but **not** the three-SIMD-param `Transform(scale,rotation,translation)` ctor, which still binds to the real Swift symbol via `CallConvSwift` with the params passed **by value** (`init_C8B878FF` in the generated source — no `@_cdecl` wrapper). Runtime: `InvalidProgramException` on the JIT lane. Left as a `Skip` probe in the RealityFoundation test. Fix: route this ctor's SIMD params through the same indirect path the setters use.

- **RC‑PROXY Failure B — entity gesture recognizers.** The campaign's one "L" item, explicitly scoped as deferrable in 03-proxy-callback.md. `EntityTranslationGestureRecognizer` & friends deliver their target entity through an `EveryEntityProtocol` existential that the generator can't synthesize a working proxy for on real (non-nil) input; the recognizer *type* binds and its metadata resolves, but installing one and receiving a callback does not round-trip. Needs a generated `EveryEntityProtocol : Entity` analogous to the existing `EveryObjCProtocol : NSObject`. Left as a `Skip` in the RealityKit test.

- **RC‑AOT — typed mesh buffers on NativeAOT.** `MeshBuffer<T>` / `MeshBuffers.Semantic<T>` / `UnsafeForceEffectBuffer<T>` generic-specialization metadata resolves on Mono/sim but not on NativeAOT/device (the constraint-relaxation `T : Vector3` instantiation isn't rooted). Documented since 2026-05-02; the RealityFoundation test capability-gates on `IsDynamicCodeSupported` (fail-if-regressed on Mono, skip on AOT). This is RC‑AOT's harder case that Session 01's re-scope trigger anticipated ("needs more than the SwiftArray pattern"). Confirmed still present on device this pass (29/0/11, the 8 buffer entries among the skips).

- **CryptoKit RC‑GENERIC remainders.** Still SB0001 / generic-only stubs with no concrete overload:
  - **HPKE `Sender`/`Recipient` initializers** (all 10) and `Sender.ExportSecret` — blocked by the NestedType structural rejection at `ConcreteProtocolSpecializationEmitter.cs:1858-1860` (3+-part `ModuleQualifiedName` conformers like `Curve25519.KeyAgreement.PublicKey`). A separate generator feature, deferred with reason in 02-csm-cryptokit.md. (HPKE `Seal`/`Open` likewise broken; `ExportSecret(byte[]/Data)` and KEM `Decapsulate` *do* have working concrete overloads.)
  - **Ed25519 signing** — `Curve25519.Signing.PrivateKey.Signature<D>` (`CryptoKit.cs:269`) is generic-only → cannot *produce* Ed25519 signatures (verification via `PublicKey.IsValidSignature(byte[]/Data,…)` works).
  - **Context-string sign/verify** — P256/etc. `Signature<D,C>` / `IsValidSignature<S,D,C>` have no concrete overload.
  - **Resolved (not a gap):** `MusicItemProxy` — the MusicKit 0.11.x ancestor-ordering crash is gone; MusicKit builds and runs (40/0/0 on sim, incl. the `Create` shims).

---

## Per-package test dispositions applied this pass

| Package / test | Was | Now |
|---|---|---|
| RealityKit — `Scene.AddAnchor/RemoveAnchor(IHasAnchoring)` (Failure A) | Fail | **Skip** → §1 (traversal + ctor stay real passes) |
| RealityFoundation — `AnchorEntity boxes as IHasAnchoring` (Failure A) | Fail | **Skip** → §1 |
| FamilyControls — `FamilyActivityPicker bridge … round-trip` | Fail | **Skip** → §2 (selection ctor/property tests stay real passes) |
| CryptoKit — `HMAC<SHA256/384> incremental == one-shot` | Fail (device) | **capability-gated**: pass on sim, **Skip** on device → §3 |
| RealityFoundation — `Transform(scale,rotation,translation)` | (new) | **Skip** probe → §5 |
| WorkoutKit — `{HeartRate,Cadence,Power,Speed}RangeAlert` ctors | (new) | **Skip** → §4 |
| RealityKit — `EntityGestureRecognizer callback` (Failure B) | Skip | **Skip** (unchanged) → §5 |

All seven packages are green on their gated lanes (sim for all; sim+device for CryptoKit, RealityFoundation, RealityKit). No hard failures remain.
