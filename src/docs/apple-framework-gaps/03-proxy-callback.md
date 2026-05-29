# Session 03 — Existential-proxy & callback bridging

The reference-type counterpart to Session 01: bridge protocol conformances and callbacks across the existential/proxy boundary. RC‑PROXY's two failure modes are the same cluster; PlayAudio is a callback into the same RealityKit surface; RC‑WILLSET gets a doc/guardrail in this session (it's adjacent and not generator-fixable).

This session contains the campaign's one **"L" item** (Failure B — new `EveryEntityProtocol : Entity` Swift class). Treat it as one session and only split on real evidence per `feedback_no_session_cascade.md`.

## Why grouped

All three code fixes are reference-type bridging:
- **Failure A** — cross-module conformance-descriptor emission.
- **Failure B** — existential-proxy carrier for class-rooted protocols.
- **PlayAudio** — closure signature gate for pointer-arg callbacks into the same RealityKit surface.

Frameworks touched: RealityFoundation, RealityKit, RoomPlan.

## Task order (small/surgical → large)

1. RC‑PROXY Failure A — smallest, surgical, also de-risks the RoomPlan question early.
2. RC‑CLOSURE PlayAudio — small closure-gate change; quick win.
3. RC‑PROXY Failure B — the campaign's "L" item; takes the bulk of session time.
4. RC‑WILLSET guardrail — doc + best-effort preflight; tightly scoped.

---

### Task 1 — RC‑PROXY Failure A (split conformance-descriptor from interface emission)

**Bug.** `Scene.AddAnchor(IHasAnchoring)` refused at runtime (`apple-frameworks/RealityFoundation/obj/.../RealityFoundation.cs:82174`). `TypeHandlerHelpers.ShouldEmitConformance:1446-1452` skips cross-module conformances whose protocol has members. `AnchorEntity`'s `_protocolConformanceSymbols` dict never gets the `IHasAnchoring` descriptor symbol, so the existential box can't resolve the witness table at runtime.

**Key insight.** The descriptor is only needed for `swift_getWitnessTable` — it does **not** require C# member stubs. The two concerns can be separated.

**Fix.** Split `ShouldEmitForDictionary` (conformance-descriptor dict entry / `IExistentialBoxable`) from `ShouldEmitForInterface` (C# interface decl). The dictionary entry emits whenever the conformance is real; the interface emission keeps its existing gate.

**Lands in:** `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/TypeHandlerHelpers.cs:1446-1452` plus ~6 call sites (search for callers of `ShouldEmitConformance`; some live in `ClassHandler.cs`). **No Swift wrapper change.**

**Suspected secondary site:** RoomPlan `RoomCaptureView.Delegate` view-callback (`apple-frameworks/RoomPlan/obj/.../RoomPlan.cs:5790`). **Confirm same failure mode first**: regen RoomPlan, dump `_protocolConformanceSymbols`, check whether the missing entry is the same shape. If yes, it's covered by the same fix. If no, drop it from this session and document the actual shape (don't expand scope silently).

**Tests.**
- Unit: after the predicate split, a cross-module conformance whose protocol has members emits the dict entry but not the interface decl.
- BindingTests: a cross-module existential round-trip (Swift type in module A conforms to protocol in module B; C# constructs the existential and the witness table resolves).
- After RealityFoundation regen: `Scene.AddAnchor(IHasAnchoring)` no longer refuses; AR anchor placement works.

---

### Task 2 — RC‑CLOSURE (PlayAudio render handler)

**Bug.** `PlayAudio` / `PrepareAudio` render handlers throw at the C# closure gate (`apple-frameworks/RealityFoundation/obj/.../RealityFoundation.cs:105873`). Signature: `(UnsafeMutablePointer<AudioBufferList>) -> OSStatus`.

**Cause (two possibilities — verify which).** Either `Darwin.OSStatus` / `AudioBufferList` aren't module-qualified/registered in the TypeDB, **or** `IsSupportedGenericType` doesn't short-circuit pointer instantiations (every `Unsafe*Pointer<T>` is `IntPtr` on the wire regardless of `T`).

**Fix.** Prefer the broader, more durable fix: short-circuit `ClosureHandler.IsSupportedGenericType` so any `Unsafe*Pointer<T>` passes the closure gate (since `T` doesn't affect the wire representation). If that surfaces other regressions, fall back to registering the specific Darwin types.

**Lands in:** `src/Swift.Bindings/src/Marshaler/ClosureHandler.cs` — `IsSupportedClosure:203`, `IsSupportedGenericType:726`.

**Tests.**
- Unit: a closure parameter typed `(UnsafeMutablePointer<T>) -> R` for any `T` passes the closure gate.
- BindingTests: a pointer-arg closure fires and the pointer is non-null + readable from the C# delegate (sim + device).

---

### Task 3 — RC‑PROXY Failure B (`EveryEntityProtocol : Entity` — the "L" item)

**Bug.** Gesture `.Entity` getters throw (`apple-frameworks/RealityKit/obj/.../RealityKit.cs:33`, `:647`, `:900`); `InstallGestures` throws (`:7389`). The constraining protocols are **class-rooted** (`HasAnchoring : Entity`); `EveryProtocol` can hold only one superclass; `EveryProtocolEmitter` skips the conformance via `HasClassSuperclassRequirement` (~`:1623`).

**Fix.** New generated Swift class `EveryEntityProtocol : Entity`, mirroring the proven `EveryObjCProtocol : NSObject` path. Wire it up as a parallel existential-proxy carrier for class-rooted protocols whose root class is `Entity`.

**Audit `EveryObjCProtocol` end-to-end first** — its generated Swift class, its P/Invokes, its lifecycle (retain/release, init pattern). The new `EveryEntityProtocol` must be structurally faithful; an incomplete copy will silently break dispatch.

**Open ABI questions to resolve before implementation:**
- Does `Entity` (a Swift class) have an `isa`/object-header layout that differs from `NSObject` in any way that affects the proxy? Codex consult below.
- Retain/release: `Entity` uses Swift's standard retain/release; `EveryObjCProtocol`'s `NSObject` uses ObjC. The new path needs Swift retain/release in the right places.

**Lands in:** `src/Swift.Bindings/src/Emitter/StringEmitter/EveryProtocolEmitter.cs` (`EmitProtocolConformance`, `HasClassSuperclassRequirement` ~`:1623`) + a new generated Swift class template + matching P/Invokes.

**Tests.**
- Unit: a class-rooted protocol whose root is `Entity` no longer trips `HasClassSuperclassRequirement`; an `EveryEntityProtocol` conformance is emitted.
- BindingTests: an `Entity`-rooted existential round-trip via `EveryEntityProtocol`; gesture `.Entity` getter returns a usable `Entity` reference; `InstallGestures` doesn't throw (sim + device).

---

### Task 4 — RC‑WILLSET guardrail (doc/preflight only)

**Background.** RealityKit `Observable.Transform` setter traps inside the framework's own `willSet` when the entity is detached (`apple-frameworks/RealityFoundation/tests/Tests.cs:292`). **No ABI route bypasses a property observer** — this is not generator-fixable.

**Action.** Add a best-effort C# preflight guard if there's a reliable public "attached to running scene" predicate (e.g. checking `scene` is non-nil). Throw a clear `InvalidOperationException` ("Cannot set Transform on a detached entity; attach to a scene first") rather than letting the Swift `willSet` trap. Plus a doc note in the RealityFoundation guide.

**This does not make detached mutation work.** Keep this task scope-bounded — do not let it inflate.

---

## Frameworks unblocked

- **RealityFoundation (🔴 → close to 🟢):** Scene.AddAnchor, PlayAudio/PrepareAudio handlers, WILLSET preflight error message.
- **RealityKit (🔴 → close to 🟢):** gesture `.Entity` getters, `InstallGestures`.
- **RoomPlan (🟢 → fully clean, conditionally):** delegate view-callback path — only if Task 1's RoomPlan check confirms the same failure mode.

## Consult points

- **Codex** on the Failure-B `EveryEntityProtocol` design — this is the session's hardest call. Ask: "Given the structure of `EveryObjCProtocol : NSObject` in the generator (point to `EveryProtocolEmitter.cs` and the generated Swift class), what's the minimal-faithful shape of an `EveryEntityProtocol : Entity` for class-rooted Swift protocols rooted at a `wrapperImportable` Swift class? What ABI differences (object header, isa, retain/release, init) need accounting for vs the ObjC-rooted path?" Pair with your own end-to-end read of the existing path — don't accept Codex's design uncritically.
- **Grok** for the Failure-A categorical sweep — enumerate **every** call site of `ShouldEmitConformance` and every cross-module-conformance shape across the shipping frameworks, so the predicate split fixes the category, not just `Scene.AddAnchor` (`feedback_codex_loop_categorical_audit.md`).
- **End-of-session paired review** — especially against the new Swift class in Failure B; mis-shapes here corrupt dispatch silently.

## Test gate

Sim **plus device** — `CLAUDE.md` requires `--device` when calling conventions, struct marshalling, or P/Invoke signatures change; Failure B introduces new P/Invokes and witness-table dispatch differs between Mono and NativeAOT.

Per `feedback_device_gate_flake_vs_regression.md`: if the device gate shows a low pass-count delta, re-run fresh (0 crashes) before treating it as a regression. `CrashCount>0` skips auto-ratchet and visible numbers are partial-recovery, not truthful.

## Risks / re-scope triggers

- **Failure B `EveryEntityProtocol` discovers ABI differences from `EveryObjCProtocol`** beyond the obvious (e.g. requires changes to the existential-box machinery, not just a parallel class) → re-scope explicitly. Decide whether to land Failure A + PlayAudio + WILLSET guardrail as this session, deferring B with a written reason — don't quietly expand.
- **RoomPlan delegate is NOT Failure A's shape** → drop it from this session; document the actual shape; treat as future targeted work (it's currently 🟢 with a partial, so not urgent).
- **PlayAudio short-circuit breaks other closures** that depend on the current `IsSupportedGenericType` gating → fall back to TypeDB registration of `OSStatus`/`AudioBufferList` specifically.

## References

- `src/docs/apple-framework-binding-gaps.md` §6b (RC‑PROXY A+B, RC‑CLOSURE PlayAudio, RC‑WILLSET detail).
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/TypeHandlerHelpers.cs:1446-1452` and callers.
- `src/Swift.Bindings/src/Emitter/StringEmitter/EveryProtocolEmitter.cs` — `EmitProtocolConformance`, `HasClassSuperclassRequirement` ~`:1623`.
- `src/Swift.Bindings/src/Marshaler/ClosureHandler.cs` — `IsSupportedClosure:203`, `IsSupportedGenericType:726`.
- Memory: `feedback_no_session_cascade.md`, `feedback_codex_loop_categorical_audit.md`, `feedback_objc_class_init_crash.md` (proxy precedent), `feedback_device_gate_flake_vs_regression.md`, `feedback_lifetimetracker_counts_objects.md` (for any retain/release tests).
