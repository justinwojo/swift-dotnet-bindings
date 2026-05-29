# Session 01 — Value-type & stdlib-generic marshalling correctness

Get value types and stdlib generics marshalled and metadata-rooted correctly, validated on a physical device. All three fixes lean on the same `SwiftArray` runtime template, and all need the `--device` NativeAOT gate (Mono and NativeAOT diverge here).

## Why grouped

- **RC‑SIMD** is value-type parameter marshalling.
- **RC‑AOT** is generated-generic metadata rooting; the fix *is* "do what `SwiftArray` does on device."
- **RC‑MISSING `ClosedRange`** is a new stdlib generic type; the fix *is also* "mirror `SwiftArray`."

Shared template, shared device-deploy cost, shared gate. One session validates all three on device.

## Task order (de-risk → highest impact)

Sequence within the session: ClosedRange first (lowest risk, validates the device-deploy loop) → RC‑AOT (same template, natural follow-on) → RC‑SIMD last (trickiest, highest value, now with a warm device pipeline so anything failing is clearly SIMD-specific).

---

### Task 1 — RC‑MISSING `ClosedRange` (new stdlib generic)

**Bug.** `Swift.ClosedRange<Bound>` has no entry in `SwiftDatabase.xml`; type resolution falls through all 13 strategies to `TryGetAnyTypeFallbackInfo` and the member drops. Blocks WorkoutKit `HeartRate/Cadence/Power/SpeedRangeAlert` constructors (`apple-frameworks/WorkoutKit/obj/.../WorkoutKit.cs:309`+).

**Fix.** Mirror `SwiftArray` end-to-end:
- `src/Swift.Runtime/src/Swift/SwiftDatabase.xml` — `ClosedRange` record (two-field `lowerBound`/`upperBound`, real `$sSN…Ma` metadata-accessor symbol).
- `src/Swift.Runtime/src/Swift/SwiftClosedRange.cs` (new) — runtime class with `GetTypeMetadata` P/Invoke, mirroring `SwiftArray.cs`.
- `BoundGenericsHandler.s_stdlibGenerics` — add entry.
- `BareGenericGuardStrategy.KnownGenericTypes` — add entry.
- `src/Swift.Runtime/src/Swift/ILLink.Descriptors.xml` — preserve entry.

**Verify** the real `$sSN…Ma` accessor symbol against a dylib (e.g. dump from any framework that returns a `ClosedRange`) — don't guess from the mangled name (`feedback_verify_swift_abi_sil.md`).

**Tests.**
- Unit: TypeDB resolves `Swift.ClosedRange<Int>`; `BareGenericGuardStrategy` no longer drops it.
- BindingTests: `ClosedRange<Int>`/`ClosedRange<Double>` round-trip end-to-end (sim + device).
- After WorkoutKit regen: `HeartRateRangeAlert(closedRange: 60...80, …)` constructs (verify in `apple-frameworks/WorkoutKit/tests/`).

---

### Task 2 — RC‑AOT (eager `cctor` + `ILLink` for generated generic `ISwiftObject`)

**Bug.** `MeshBuffer<T>` and other generated generic `ISwiftObject` types work on sim (Mono reflection path) but fail on device/NativeAOT — ILC can't see the instantiation. Two causes: (a) the generated `cctor` doesn't eagerly call `SwiftObjectHelper<T>.GetTypeMetadata()` the way hand-written `SwiftArray` does (`SwiftArray.cs:80-106`); (b) no `TrimmerRootDescriptor` is emitted for generated binding assemblies.

**Fix.** Emitter synthesizes the eager-`cctor` pattern under `SwiftRuntimeInfo.IsNativeAotRuntime` for every generated generic `ISwiftObject`, and emits an `ILLink` descriptor (or `[DynamicDependency]`) so ILC roots the instantiation.

**Lands in:** the binding-class emitter — same site that emits the rest of each generated `ISwiftObject`'s ABI plumbing. Identify by searching for the existing `cctor` emission for generic types.

**Tests.**
- Unit: generated source for a generic `ISwiftObject` includes the eager-`cctor` block + an `ILLink`/`[DynamicDependency]` root for the chosen instantiations.
- BindingTests: a generic `ISwiftObject` round-trips on **device/NativeAOT** (this is the entire point — sim alone is insufficient).

---

### Task 3 — RC‑SIMD (route simd vectors through the indirect path)

**Bug.** RealityKit/RealityFoundation transform/Quaternion/Matrix setters **silently truncate writes**. Root cause is a **register-class mismatch**, NOT a 12-vs-16-byte size mismatch:

- Swift's `simd_floatN` is a Clang `ext_vector_type` passed in a *single* 128-bit NEON register (`v0` on AArch64).
- .NET passes `System.Numerics.Vector3`/`Quaternion` as an **HFA** — spread across separate single-float registers (`s0, s1, s2, …`).
- Only lane 0 aligns.

`simd_quatf` (16 B = 16 B, *no* size difference) loses lanes too. **The predicate must gate on "is a simd type," not on size mismatch**, or Quaternion stays broken (this is the failure mode of the obvious patch).

**Fix.** Force the indirect/pointer path for any parameter typed as a simd vector (`module=="simd"` plus any escape hatches for ext_vector_type types declared elsewhere — confirm with the Codex consult below):
- C# side: `stackalloc 16/64-byte` buffer + `MarshalToSwift`.
- Swift side: `UnsafeRawPointer` + `.assumingMemoryBound(...).pointee`.

Memory layout is unambiguous, which sidesteps register classification entirely. Reads already work (struct returns go through `resultPtr`).

**Lands in:**
- `src/Swift.Bindings/src/Emitter/StringEmitter/WrapperValidation.cs` — `IsNonPrimitiveFrozenStructParam` (~`:926-945`).
- `src/Swift.Bindings/src/Marshaler/CdeclParamMapper.cs` — `Map`, `IsSystemFrozenStruct`.

**Tests.**
- Unit: simd-vector parameter goes down the indirect path; non-simd `@frozen` structs still take the by-value path; Quaternion (16 B) and Matrix4x4 (64 B) both gate as simd.
- BindingTests: Swift fixtures round-trip `simd_float3`, `simd_quatf`, `simd_float4x4` as `inout` and as setter params; assert lane-by-lane equality. **Sim and device both required.**
- After RealityFoundation regen: the transform-setter test that today silently truncates (`apple-frameworks/RealityFoundation/tests/Tests.cs:178`) now round-trips exactly.

---

## Frameworks unblocked

- **WorkoutKit (🟠 → close to 🟢):** all four range alerts constructible (HeartRate/Cadence/Power/Speed).
- **RealityFoundation (🔴 → significantly recovered):** transform/Quaternion/Matrix setters correct; typed mesh buffers work on device.
- **RealityKit (🔴 → significantly recovered):** inherits all the above. Session 03 closes the remaining RealityKit gaps.

## Consult points

- **Codex** on the SIMD detection predicate: "Are there simd-shaped types (Clang `ext_vector_type`) declared *outside* `module=="simd"` that the generator currently routes through `IsSystemFrozenStruct`? What's the cleanest cross-type-system way to detect ext_vector_type so the gate is complete, not just covering the obvious cases?" Pair this with your own audit of `IsSystemFrozenStruct` callers — don't outsource the thinking (`feedback_codex_design_partner.md`).
- **Grok** to verify the real `$sSN…Ma` metadata-accessor symbol for `ClosedRange<Bound>` by dumping it from a dylib that returns one; confirms wiring before the runtime class is committed (`feedback_verify_swift_abi_sil.md`).
- **End-of-session paired review.**

## Test gate

Default `nuke binding-tests --sim` **plus** `nuke binding-tests --device`. ClosedRange touches generic metadata that ILC handles differently from JIT; RC‑AOT is by definition a device fix; SIMD failure modes diverge across the JIT/AOT split.

Per `feedback_clean_bin_before_stage2.md`: if re-running `--sim --device` in the same worktree after a partial run, `rm -rf BindingTests/RuntimeTestsApp/{bin,obj}` first — stale artifacts cause unrelated crashes.

## Known limitations after this session

- **Open-generic `ISwiftObject` nested inside an open-generic outer** (e.g. `Outer<T>.Inner<U>`) is deliberately dropped from RC‑AOT tracking. The outer type parameter is not in scope at the static-init context, so neither the eager `cctor` path nor the ILLink descriptor can name `Inner<U>` without instantiating `Outer<T>` first. Closes when the outer is closed-form; consumers that materialize `Outer<X>.Inner<Y>` purely through runtime reflection on a fully-trimmed NativeAOT build are the affected (theoretical) case. Codex r1+r2 flagged this; the fix lives at the consumer's instantiation site, not in our descriptor emitter. See `ModuleEmissionContext.RecordOpenGenericISwiftObjectType` for the guard.

## Risks / re-scope triggers

- **SIMD predicate gets a long escape-hatch list** → if the predicate has to detect ext_vector_type for types declared outside `module=="simd"` (e.g. inside any framework that defines its own SIMD-shaped aggregate), resolve with the Codex consult before adding case-by-case logic. Don't ship the case-by-case version.
- **RC‑AOT for `MeshBuffer<T>` needs more than the SwiftArray pattern** (e.g. associated-type plumbing the array doesn't have) → re-scope explicitly; don't bolt extra mechanisms onto this session.
- **WorkoutKit range-alert regen reveals additional missing-stdlib types** beyond `ClosedRange` (e.g. `Range<T>`) → enumerate via Grok categorical sweep; decide whether `Range<T>` belongs in this session (likely yes, same template) or its own.

## References

- `src/docs/apple-framework-binding-gaps.md` §6b (RC‑SIMD, RC‑AOT, RC‑MISSING ClosedRange detail with the same file:line landings).
- `src/Swift.Runtime/src/Swift/SwiftArray.cs` — the template both RC‑AOT and ClosedRange mirror (eager `cctor` is around `:80-106`).
- `src/Swift.Bindings/src/TypeDatabase/Resolver/Strategies/BoundGenericSimdAliasStrategy.cs` — existing simd alias resolution (`SIMD3<Float>` → `simd_floatN`); SIMD fix shouldn't break this.
- Memory: `feedback_verify_swift_abi_sil.md`, `feedback_swift_frozen_first.md`, `feedback_clean_bin_before_stage2.md`, `feedback_codex_design_partner.md`.
