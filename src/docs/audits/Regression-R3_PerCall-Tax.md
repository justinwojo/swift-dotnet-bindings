# Regression Audit R3 — Per-call marshalling tax (Session 11, commit f9229c50)

**Scope:** Regression hunt over commit f9229c50 ("Trim per-call allocation overhead from the `@_cdecl` wrapper hot path") across four target surfaces — the wrapper-finalizer removal (~583 emit sites), the constant-size `@_cdecl` scratch-buffer `stackalloc`, the `EphemeralSwiftString` transient-string fast path, and the deliberately-excluded non-frozen-struct ownership-transfer path.

**Overall risk: 2 / 5 (low).** Confidence: high. The hot-path rewrite is sound on every axis that was decisively probed — finalizer removal does not leak (the payload `SwiftSafeHandle<T>` is the real owner and still runs VWT-destroy via its own critical finalizer), every new `stackalloc` is a compile-time constant 16 bytes and never reaches an async body, and `EphemeralSwiftString`'s native ABI is byte-identical to the heap path it replaces. The single confirmed finding is a **latent, unguarded footgun, not an active bug**: it is not triggered by any code the generator emits today.

**Confirmed: 1.** **Regressions among confirmed: 0** — the one confirmed finding is a *defense-in-depth gap in genuinely-new code* (the `EphemeralSwiftString` ref struct introduced by this commit), but it does not change observable behavior versus the pre-change heap path and is not reachable through emitted output. Counting it as "new code, latent" rather than "behavioral regression."

---

## 1. Confirmed findings

| file:line | severity | regression? | claim | what the probe showed |
|---|---|---|---|---|
| `src/Swift.Runtime/src/Swift/SwiftString.cs:366` | P2 | No (latent, new code) | `EphemeralSwiftString` over-release-on-copy hazard is enforced only by emitter convention, not by the type — a future non-`using`/value-copy call site silently double-frees a heap-backed Swift String | A ref-struct value copy (`var b = a;`) is a raw bit copy that duplicates both the 16-byte buffer (same retained backing-storage pointer) **and** `_created = true`; two `Dispose()` calls then each run the native destroy. C# probe: `DestroyCount = 2` for one `Create`. Swift probe: the matching double `deinitialize` of one `initialize(to:)` SIGSEGVs (EXIT 139) on the heap-backed storage. |

### `EphemeralSwiftString` over-release-on-copy hazard (P2, latent, not a regression) — **FIXED (structural guard)**

`EphemeralSwiftString` (`src/Swift.Runtime/src/Swift/SwiftString.cs:366`) is a move-style owning `ref struct`. Its constructor calls `SBW_SwiftString_Create` and sets `_created = true` (lines 385-387); `Dispose()` runs `SBW_SwiftString_Destroy` when `_created` (lines 404-414). The native `SBW_SwiftString_Create` does `initialize(to: str)` — a `+1` into the inline 16-byte buffer — and `SBW_SwiftString_Destroy` does `deinitialize(count: 1)` to release that `+1` (`src/Swift.Runtime/swift/SwiftBindingsRuntime.swift:243-265`).

The hazard, which the type's own doc-comment admits (`SwiftString.cs:353-364`): a value-copy of the owning struct carries `_created = true` and the retained backing-storage pointer into the copy, so two instances can each `deinitialize` the same String — an over-release / double-free for large-form (heap-backed) strings. There is **no language-level guard**. C# has no move-only types; ref-struct stack-confinement blocks heap escape but does **not** prevent a same-frame value copy. The `_created = false` guard in `Dispose` provides per-*instance* idempotency only — it does not survive a copy, because each copy owns its own `_created` flag. (The existing test `SwiftStringWrapperTests.cs:313` `Ephemeral_DoubleDispose_DoesNotThrow` covers only the *same-instance* double-dispose, which the guard does handle; there is no copy-case coverage.)

**Why this is not an active bug.** The only thing standing between this footgun and a crash is that the generator emits exactly one safe shape — the sole emitter site is `WrapperEmitter.Marshalling.cs:749-752`:

```csharp
using var {csName}Swift = new SwiftString.EphemeralSwiftString({csName});
var {csName}Buf = {csName}Swift.Buffer;
nint {csName}_w0 = Unsafe.As<SwiftString.Buffer, nint>(ref {csName}Buf);
nint {csName}_w1 = Unsafe.Add(ref Unsafe.As<SwiftString.Buffer, nint>(ref {csName}Buf), 1);
```

It binds with `using` and reads the inert bytes via `.Buffer` (a `readonly` by-value property returning a 16-byte copy, `SwiftString.cs:398`) — it never copies the owning struct. A grep across `src/` confirms this is the only site. So today's generated output is safe.

**The risk surface is future change.** A later emitter edit that copies the handle, drops `using`, or returns it from a helper would compile cleanly and double-free at runtime — and nothing in the test suite would catch the regression, because no test exercises the copy case. This is a defense-in-depth gap in code introduced by f9229c50, distinct from the four refuted async-lifetime / leak / overflow hypotheses below.

**Recommended hardening (not a code fix to apply here):** either make the type structurally copy-resistant, or add a guard test that asserts the copy-double-free shape so a regressing emitter edit fails CI. See §7 for the fixture.

**Fix (structural emitter guard + deterministic large-form sentinel + e2e heap-backed round-trip — not a live copy-double-free test).** The first remediation option (make the type structurally copy-resistant) is not achievable: C# has no move-only types, and a `ref struct`'s stack-confinement does not prevent a same-frame value copy — so the hazard is fundamentally enforced by *what the generator emits*, not by the type. A *live* copy-double-free test is also rejected on principle: deliberately copying the owning handle and double-disposing is non-deterministic undefined behavior (over-release of heap storage), which would be a flaky test, not a gate (consistent with the project's "finalizer-leak hazards cannot be made deterministically red" finding). The durable, deterministic gate is therefore three layers that lock the *only safe shape* and the *real risk surface*:

1. **Structural emitter guard** — `StringByValueFastPathEmitterTests.EphemeralSwiftString_ConstructedAtExactlyOneUsingBoundEmitterSite` scans the entire generator source tree (`src/Swift.Bindings/src/**/*.cs`) and asserts there is **exactly one** `new SwiftString.EphemeralSwiftString(` construction site **and** that it is `using var`-bound. A future emitter edit that adds a second construction site, drops the `using`, or binds the owning handle to a copyable local turns this red — converting the "convention" into an enforced invariant. The companion `CdeclMethodWrapper_StringParam_UsesEphemeralStackBuffer` additionally asserts the emitted body reads the inert bytes via `var {n}Buf = {n}Swift.Buffer;` and never copies the owning struct (`DoesNotContain("= nameSwift;")`).
2. **Deterministic large-form lifecycle sentinel** — `SwiftStringWrapperTests.Ephemeral_LargeForm_HeapBacked_BuildsAndReleasesOnce` constructs a 64-char (heap-backed / large-form) `EphemeralSwiftString` — the *only* representation whose double-free is dangerous (small-form ≤15-byte inline strings double-destroy as a safe no-op) — reads its `.Buffer` words, disposes once, and asserts the buffer was non-empty (`!(w0 == 0 && w1 == 0)`), pinning that the heap-backed build/borrow/release path is exercised deterministically. A NOTE in the file records why no live copy-double-free test exists (it would be vacuous/UB; the structural emitter guard is the real gate).
3. **End-to-end heap-backed round-trip** — `BindingTests` `ReturnPathTests.TestGreeterHeapBackedStringParam` calls `Greeter.greet(greeting:)` (already on the `@_cdecl` String-by-value fast path) with a 32-byte (`> 15` UTF-8 byte ⇒ heap-backed) argument and asserts the full string round-trips. Every other `Greeter` test uses ≤15-byte inline strings, so this is the sentinel that the emitted fast path stays correct against the dangerous large-form input; a regressing emitter edit that copied the handle or dropped the `using` would over-release this heap storage and crash the round-trip.

Together these make the latent footgun's *only* safe emission structurally enforced and its dangerous representation deterministically exercised, without depending on a non-deterministic UB repro. The `EphemeralSwiftString` type itself is unchanged (it is correct as used today); what was missing was the gate, which is now in place.

---

## 2. Inconclusive / needs deeper probe

None. Every hypothesis in this track reached a decisive verdict via static read plus a compile/SIL/runtime probe.

---

## 3. Deferred (candidate, unverified)

These are real candidates that fell past the per-track verification cap. Neither is believed to be an active defect; both are recorded for completeness.

- **`EphemeralSwiftString` ctor catches only `DllNotFound`/`EntryPointNotFound` — a different exception after partial in-place init would leak the `+1`** (`src/Swift.Runtime/src/Swift/SwiftString.cs:383`, P2, not a regression). The ctor sets `_created = true` only *after* `SwiftString_Create` returns; the try/catch swallows only the two missing-runtime exceptions. If `SwiftString_Create` ever threw some other exception *after* initializing the String into the buffer, the partially-constructed value would propagate with `_created == false`, so the `using` Dispose is moot and the in-place `+1` would not be released — a transient leak. In practice the native `initialize(to: str)` cannot meaningfully throw a managed exception mid-init, and the pre-existing heap `SwiftString(string)` ctor (`SwiftString.cs:192`) has the identical catch shape and exposure — so this is neither introduced by f9229c50 nor reachable in practice. Robustness note only.

- **No additional f9229c50 defect found beyond the one confirmed item** (`src/Swift.Bindings/src/Emitter/StringEmitter/Handler/EnumHandler.cs:254`, P2, not a regression). An independent re-audit of all four target surfaces from angles distinct from the prior finders — finalizer removal, stackalloc result buffers, the enum cached-singleton path, the marshalling early-`return true` reorder, and `EphemeralSwiftString` native semantics — surfaced nothing new. The marshalling reorder (early `return true` at `WrapperEmitter.Marshalling.cs:747-754` before `MarshalPlanRenderer.RenderStatements`) is per-argument and drops no setup: `ShouldDecomposeStringForCdecl` fires only for an exact top-level `Swift.String` parameter whose projection setup was solely the now-replaced heap allocation; `String?`/`[String]`/`Data` take other branches that still reach `RenderStatements`. Recorded as a candidate because it was reasoned-sound rather than independently probed to ground.

---

## 4. Checked & refuted

Brief — each was probed (SIL / assembly / compile / live-runtime) and the suspected defect does **not** exist:

- **Async `@_cdecl` String-by-value param disposed before the continuation reads it (UAF)** — `WrapperEmitter.Marshalling.cs:749`, P1, refuted (×3 independent finders). The mechanical premise is correct (an async String-by-value param marshals only via the `EphemeralSwiftString` arm, with no `_asyncDeferredList` hoisting; `SwiftString` is frozen so it is excluded from both `nonFrozenParams` and `IsAsyncDeferredDisposeContainerParam`, which covers only Array/Set/Dictionary). But the inferred consequence is wrong: the emitted Swift wrapper reconstructs the String via `unsafeBitCast((_sW0_, _sW1_), to: String.self)` on the **foreground thread** and captures it into the `Task` closure, taking an independent `+1` *before* the foreground returns. SIL shows `strong_retain` / `retain_value` (and assembly `swift_bridgeObjectRetain`) emitted before `Task.init`. When the C# `using` Dispose releases the original `+1`, the Task's captured retain keeps the storage alive across suspension. String was correctly never wired into the deferred-dispose list — its by-value ownership differs from the container path (whose `+0` pointer aliases a buffer the `using var` would free). Lifetime is identical old-vs-new.

- **Finalizer removal leaks / double-releases** — `SwiftHandle.cs:197`, P2, refuted. The removed wrapper `~T()` only re-did the release that `_payload` (`SwiftSafeHandle<T> : SafeHandleZeroOrMinusOneIsInvalid`, a `CriticalFinalizerObject`) already performs via its own `ReleaseHandle` → `HandleFinalizerRelease` (Cdecl `SBW_VWTDestroy`). Abandon-without-Dispose still releases; removal is strictly a promptness win (one fewer finalizable object). Cached enum singletons are rooted forever by `static readonly Lazy<T>` (`EnumHandler.cs:648`) so the payload never finalizes; the removed `!_isCachedSingleton` guard was moot, and `Dispose` still early-returns on `_isCachedSingleton` (`EnumHandler.cs:265`). The borrowed-marshal double-release guard is `SuppressPayloadFinalizer()` on the SafeHandle, not the removed wrapper finalizer (`SwiftMarshal.cs:991-1007`). Verified by live `SwiftStringWrapperTests` (17/17 against the real dylib) and end-to-end compile probes.

- **Unbounded / async stackalloc** — `MethodMarshalPlanBuilder.cs:619,636`, P2, refuted. `StackAllocByteCount` is the literal `"nint.Size * 2"` (=16) at exactly two sites (closure return, String return); the sole emission point writes `byte* _cdeclBuf = stackalloc byte[nint.Size * 2];` once per body, non-escaping (copied out via `ReadUtf8Slice` / delegate read before return), `CleanupCode = null`. All variable-size returns still `NativeMemory.Alloc`. The buffer is never emitted on an async path: `MethodRequiresIndirectResult` returns `false` for `IsAsync` (`MarshallingHelpers.cs:182`) → `BuildIndirectResultSetup` returns null; independently, async params explicitly `continue` past the stackalloc (`WrapperEmitter.Marshalling.cs:822-827`). 133/133 targeted unit tests pass.

- **`EphemeralSwiftString` native-ABI / over-release on the emitted path** — `SwiftString.cs:366` + `SwiftBindingsRuntime.swift:243-265`, refuted. SIL probe: `Create` = `String._fromUTF8Repairing` (`+1`) stored into the buffer, no balancing release (`+1` in the buffer); `Destroy` = in-place value destroy (`destroyArray`/`deinitialize`) with **no** `deallocate` of the container, so stack-backing the 16-byte buffer is safe. The `@_cdecl` wrapper passes the reconstructed String `@guaranteed` (borrowed, `+0`) with a balanced `strong_retain`/`release_value` pair — ARC-neutral — so the C# side's `+1` is released exactly once by `Dispose`. Byte-identical to the old heap `PayloadBuffer` path. A 200k-iteration create→borrow→destroy probe under MallocScribble/GuardEdges stayed clean.

---

## 5. Coverage gaps

- ~~**No copy-case test for `EphemeralSwiftString`.** The existing `Ephemeral_DoubleDispose_DoesNotThrow` (`SwiftStringWrapperTests.cs:313`) asserts same-instance idempotency only. The over-release-on-copy shape (`var b = a; b.Dispose(); a.Dispose();`) — the exact regression a future emitter edit would introduce — is untested, so CI would not catch it.~~ **Closed via the structural guard, not a live copy test** — a live copy-double-free is non-deterministic UB (and only dangerous for large-form strings), so the gate is instead the emitter guard (single `using`-bound site) plus a deterministic large-form lifecycle sentinel (`Ephemeral_LargeForm_HeapBacked_BuildsAndReleasesOnce`) and an e2e heap-backed round-trip. A future emitter edit that copies the handle now fails the structural guard, which is the actual regression vector.
- ~~**No structural guard pinning the single safe emitter shape.** Nothing asserts that `WrapperEmitter.Marshalling.cs:749-752` remains the *only* `EphemeralSwiftString` construction site or that it always uses `using` + `.Buffer` without copying the owning struct. A grep-style emitter guard test would convert "convention" into an enforced invariant.~~ **CLOSED by the fix** — `EphemeralSwiftString_ConstructedAtExactlyOneUsingBoundEmitterSite` scans the generator source and asserts exactly one `using var`-bound construction site, and `CdeclMethodWrapper_StringParam_UsesEphemeralStackBuffer` asserts the body reads `.Buffer` and never copies the owning struct.
- **Finalizer-leak hazards cannot be made deterministically red** (per project memory): the abandon-without-Dispose leak probes complete clean even under contamination, so the finalizer-removal safety rests on static + SIL + live-runtime evidence, not a forced repro. This is an accepted limit, not an actionable gap.

---

## 6. Recommended BindingTests fixtures

To lock down the one confirmed defect. (Swift shapes described; no code fixes proposed.)

1. **Copy-double-free guard (the durable gate for the confirmed finding).** A *runtime unit test* in `SwiftStringWrapperTests` (not a generated BindingTests case — the hazard is in the runtime ref struct, not in emitted output). Construct `using var a = new SwiftString.EphemeralSwiftString(new string('x', 64));` to force the large-form, heap-backed representation, then `var b = a;` and explicitly `b.Dispose(); a.Dispose();`. Run under a LifetimeTracker / sanitizer build and assert a double-deinitialize is observable (over-release / heap corruption on the 64-char heap-backed string). This demonstrates the latent footgun is real and unguarded, and turns any future regressing emitter edit into a red test. Pair it with the existing same-instance double-dispose test so both the protected (same-instance) and unprotected (copy) shapes are pinned.

2. **End-to-end heap-backed String round-trip (regression sentinel for the emitted fast path).** A Swift `@_cdecl`-eligible method taking a `String` by value and returning a derived value, e.g. `func lengthOf(_ s: String) -> Int { s.count }`, called from C# in a tight loop with a `> 15`-char (heap-backed) argument. Assert the returned count matches across the loop and live-count returns to baseline. This affirmatively re-confirms the emitted `EphemeralSwiftString` fast path (which uses `using` + `.Buffer`, never a copy) stays correct and would fail if a future emitter change introduced a copy or dropped the `using`.

3. **Heap-backed String round-trip through a `String`-returning `@_cdecl` method**, e.g. `func echo(_ s: String) -> String { s }`, with a `> 15`-char argument, asserting value round-trips. This exercises the stackalloc copy-out (`stackalloc byte[nint.Size * 2]` → `ReadUtf8Slice`) alongside the `EphemeralSwiftString` param marshal in one path, pinning both ends of the fast path against the same heap-backed input.
