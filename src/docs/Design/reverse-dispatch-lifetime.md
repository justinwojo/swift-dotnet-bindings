# Reverse-dispatch lifetime & vtable correctness (design & as-built record)

Status: **IMPLEMENTED + reviewed (Phase 1, Session 1 of `architecture-review-2026-06.md`;
shipped 2026-06-13/14).** All four items below shipped — Design B2 lifetime model, EveryProtocol
per-module metadata, the `@objc optional`-before-required slot fix, and the flag-matrix
invariant — plus the existential-lifetime fold-in (owned-mint + borrowed-keepAlive) and
tuple-of-convertible-element parameter marshalling; each cleared its Grok/Codex design review and
post-hoc pair review. **This doc is now the permanent design reference for the runtime's
reverse-dispatch lifetime model** — cited from `ProxyLifetimeTracker.cs`, `EveryProtocol.cs`,
`AsyncHelpers.cs`, and `ExistentialContainer.cs` as the home for "Design B2" and design change 4,
so it is kept (not archived), not a disposable session log. ⚠device: the runtime fixtures run on
`--sim` (Mono JIT) **and** `--device` (NativeAOT). One residual unit stays deferred (latent, no
reachable fixture today): the EC2+ composition collection-element carrier owned-mint — tracked in
`roadmap.md` (*Latent* → "Owned existential collection-element carrier fall-through") and under
"Deferred / split-out units" below.

Scope (four items from the Session 1 gameplan):

- **Defect G** — Inverted proxy lifetime + silent value fabrication. *(owner decision: root-the-impl + loud backstop)*
- **Finding 33** — `EveryProtocol` type-metadata is a process-global first-wins latch.
- **Defect C** — method-index skew when an `@objc optional` method precedes required methods.
- **Defect F** — vtable property-slot membership divergence + the Part IV flag-matrix invariant (Finding 31).

TDD throughout: red fixture first, then fix, then green. (Session 7b note: there is no longer a
`PreservedProtocols` allowlist step — the harness retired its bespoke stripper and now scrubs the
wrapper with the generator's own `SwiftWrapperPostProcessor.Process`, which preserves every valid
conformance and its witness-table getter by construction.) `--skip-regen` reuses the prior wrapper
and will NOT pick up a freshly-added fixture — validate new fixtures with a full regen run.

---

## Defect G — root the impl, delete the fabrication paths

### Current model (ground-truthed)

- proxy → impl: **WEAK** (`_csharpImplRef`, `ProtocolProxyEmitter.StaticInit.cs:43`).
- `SwiftObjectRegistry`: **STRONG**-roots the proxy by handle (`RegisterStrong`, `Receivers.cs:2050`),
  dropped only by `OnEveryProtocolDeinit` → `Unregister` (Swift deinit).
- The construction **+1 (R0)** from the `SBW_Create…` factory (`Unmanaged.passRetained`) is owned by
  `ProxyLifetimeTracker`, keyed **weakly by impl** via a `ConditionalWeakTable`. It is released on
  **impl-GC** (`ProxyCleanup.~ProxyCleanup → ReleaseAll → SwiftReleaseTrampoline.Release(handle)`,
  the Mono-safe Cdecl trampoline).
- `OnEveryProtocolDeinit` (Swift deinit, Cdecl): `Unregister` + `NotifyDeinit` (marks the entry
  `Released`; does **not** itself call native release).
- Receivers resolve the impl with `SwiftObjectRegistry.TryGetProxy<IProtocolProxyImpl<IFace>>(handle)`
  then `proxy.UserImpl` (the weak unwrap). On a null impl they **fabricate** a return value
  (`AllocZeroedSwiftBuffer`, `SwiftOptional.NewNone`, empty collection, `string.Empty`).

### The bug

Because proxy → impl is weak, the impl can be GC'd as soon as the consumer drops its own reference —
which is the *normal* case for a stored delegate (`harness.receiver = myImpl; myImpl = null`). After
impl-GC the tracker releases R0, but if Swift still holds a stored existential the EveryProtocol is
still alive (Swift's store-retain), so deinit does **not** fire, the registry strong root persists,
and the proxy is still found at dispatch — but `proxy.UserImpl` now resolves **null** → every
receiver **fabricates** a value. Silent data corruption on a live, correctly-registered proxy.

### Keystone ABI fact (verified against generated wrappers)

The marshal boundary on the **C# side does not retain**: `GetExistentialContainer()` returns the
`_swiftContainer` struct by value and `MarshalToSwift` copies the existential bytes (Payload0 = the
EveryProtocol pointer) with no ARC call. Swift takes its own retain **only when it stores** the
`any P` (net **+1** held by the stored property) and **net 0 when it merely borrows** for a call.
Evidence in `BindingTests/output/SwiftBindingsTestLib.Wrapper.swift`:

- store (`…_set_receiver_…`, ~50653): `newValue…pointee` copies the existential (retains the class
  payload), `obj.receiver = newValueVal` stores it → net +1 held by Swift.
- pass-not-store (`…_pingOnce_…`, ~50668): `receiver.load(...)` then borrowed for the call, local
  released at end → net 0.

This asymmetry is decisive: R0 **cannot** be released at "any handoff" (the pass-not-store case
would dealloc the EveryProtocol immediately), and there is no C#-side signal for "Swift has stored
it."

### Why the obvious fixes don't work (cycle/impossibility analysis)

The construction +1 (R0) is an ARC retain on EveryProtocol. The teardown sequence is
`R0 released → Swift's last store-retain released → EveryProtocol deinit → OnEveryProtocolDeinit →
Unregister`. Therefore **anything that gates R0 release on EveryProtocol's own liveness deadlocks**:

- **"strong impl + keep impl-keyed R0 release"** (naïve root-the-impl): registry(strong)→proxy→impl
  roots the impl until deinit; impl-GC needs deinit; deinit needs R0; R0 needs impl-GC. **Cycle → leak.**
  (This is exactly the leak the current weak `_csharpImpl` comment is avoiding.)
- **"strong impl + Swift-rooted impl GCHandle freed on deinit + R0 still on impl-GC"**: impl-GC needs
  the GCHandle freed needs deinit needs R0 needs impl-GC. **Cycle → leak.**
- **"release R0 in OnEveryProtocolDeinit"**: deinit only fires at retain 0, which needs R0 already
  released. **Deinit never fires. Leak.**
- **"release R0 eagerly in the ctor"**: pass-not-store takes net 0 → EveryProtocol deallocs before the
  consumer's first use → **premature death / UAF.**

The forced conclusion: **R0 must be released on a signal that is independent of EveryProtocol
liveness, and that fires only after the consumer is done driving the proxy from C#.** The only such
signal is **proxy collection**, and for the proxy to be collectable before deinit it must **not** be
strong-rooted until deinit — which means **reverse dispatch must locate the impl without a live
proxy.**

### Recommended design — "Design B2"

Three coordinated changes; together they make the canonical pattern fabrication-free, cycle-free,
window-safe, and leak-free.

1. **Root the impl by Swift-liveness, keyed by handle.** In the C#-impl ctor, allocate a *strong*
   `GCHandle` on the implementation and store it in a `handle → GCHandle` map owned by
   `ProxyLifetimeTracker` (replacing the impl-keyed `ConditionalWeakTable`). This keeps the impl
   alive exactly as long as Swift holds the EveryProtocol. The GCHandle is **freed in
   `OnEveryProtocolDeinitCore`** (Swift's last retain dropped). → fabrication impossible while Swift
   references the proxy; impl becomes collectable the instant Swift is done.

2. **Reverse dispatch resolves the impl from that strong root, not from the proxy.** Add
   `ProxyLifetimeTracker.ResolveImpl<T>(IntPtr handle)` → `s_implRoots[handle].Target as T`. Replace
   every receiver preamble (≈16 sites in `ProtocolProxyEmitter.Receivers.cs`, all the
   `TryGetProxy<IProtocolProxyImpl<IFace>>(handle …) → proxy.UserImpl` shape) with
   `ResolveImpl<IFace>(handle)`. **Delete** the fabrication branches; on a null impl emit the **loud
   backstop**: `Environment.FailFast` naming the protocol/member and handle (now unreachable in the
   canonical pattern, kept as defense-in-depth). The sibling-fallback sites (the
   `… && proxy is not null` variants and the cross-module parent path) keep their *try-own-then-
   siblings* ordering, swapping only the lookup primitive (`ResolveImpl<IFace>` succeeds iff the impl
   implements `IFace`, identical truth value to `TryGetProxy<IProtocolProxyImpl<IFace>>` succeeding).

3. **R0 is owned by the proxy and released on the proxy's finalizer/Dispose**, via
   `SwiftReleaseTrampoline.Release` (the Mono-finalizer-safe Cdecl path — do **not** use
   `Arc.Release`). The C#-impl proxy therefore must **not** `GC.SuppressFinalize` (today it does at
   `Receivers.cs:2077`); `Dispose` releases R0 and suppresses. The registry root for the C#-impl
   proxy becomes **weak** (`Register`, not `RegisterStrong`) so the proxy can be collected once the
   consumer drops it, independent of Swift. The existing `HandleEntry.Released` Interlocked guard +
   `SwiftExitGuard` process-exit short-circuit are retained.

4. **Pin the existential's backing proxy across the native call (`GC.KeepAlive`).** *Added after the
   design review — the one gap the impossibility analysis missed.* Once the registry root goes weak
   (change 3), an **auto-wrapped** proxy (one the consumer never holds — `ExistentialContainerFactory.
   GetOrCreate` mints it for a plain C# impl passed to a Swift setter/param) is rooted by nothing
   strong while Swift runs: the marshalling copies an `ExistentialContainer1` **struct** (raw pointers,
   no managed ref to the proxy) to the heap and calls native with that pointer. A GC between container
   creation and Swift's first store-retain would finalize the proxy → release R0 → premature deinit /
   UAF *inside* the running `_set_receiver_`/`pingOnce` call. Today `RegisterStrong` incidentally
   prevents this; weakening the registry exposes it. Fix: `GetOrCreate` gains an `out object? keepAlive`
   returning the proxy it built or reused; every existential-argument marshalling site captures it and
   emits `GC.KeepAlive(__keepAlive)` as the **last statement of the existing `finally`** (which runs
   after the native call returns — by which point Swift has completed its store-retain *or* finished
   borrowing). In the already-a-proxy branch `keepAlive` is the live method argument (stack-rooted
   anyway, KeepAlive harmless); in the boxable branch it is `null` (owned container, destroyed in the
   same finally). **This is coupled to change 3 and must land in the same change** — it is a no-op
   while the registry is strong, and load-bearing the instant it goes weak.

**Lifetime walk-through.** *Store case*: consumer sets `harness.receiver = proxy`, drops it. Proxy
GCs → R0 released; Swift's store-retain keeps EveryProtocol alive; impl stays rooted by its GCHandle;
later `harness.receiver.ping()` resolves the impl via `s_implRoots[handle]` (proxy long gone). When
Swift sets `receiver = nil` → retain 0 → deinit → free impl GCHandle + Unregister → impl collectable.
*Pass-not-store case*: consumer holds proxy across `pingOnce(proxy)` (proxy is a live arg → R0 alive →
EveryProtocol alive; any reverse callback resolves impl via the GCHandle). Consumer later drops proxy
→ R0 released → no Swift retain → deinit → teardown. No premature death (the proxy owning R0 is alive
throughout the consumer's use). No cycle (R0 release gated on consumer only; deinit gated on
R0-release which is independent).

### Open risks for the design review

- **R1 — resolved by review (Grok + Codex).** Codex correctly notes `ResolveImpl<IFace>(handle)`
  (`impl as IFace`) is a *strictly wider* predicate than `TryGetProxy<IProtocolProxyImpl<IFace>>` —
  the registered proxy for handle H satisfies `IProtocolProxyImpl<IQ>` only for ancestors of its own
  protocol P (via the covariant `IProtocolProxyImpl<out T>`), whereas `impl as IQ` matches any sibling
  interface the same object implements. Grok correctly notes the widening is **unreachable from
  generated code**: each receiver preamble calls `ResolveImpl<ITS-OWN-IFace>`, and a handle dispatched
  through P's vtable is an EveryProtocol minted as `{P}Proxy`, so only `ResolveImpl<IP>` (+ P's
  ancestors via covariance, which is exactly the cross-module parent path) is ever invoked for it.
  Verdict: faithful for every reachable site. Make it a **tested invariant** — a fixture where one C#
  class implements two *unrelated* reverse-dispatch protocols, asserting each handle resolves only its
  own protocol's view with no cross-talk.
- **R2:** is registering the C#-impl proxy *weakly* (or not at all) safe — does anything besides
  reverse dispatch read it from `SwiftObjectRegistry` for a C#-impl proxy? (Swift→C# wrapping uses the
  second, existential-adopting ctor + `ExistentialContainerFactory`; that path is unchanged.)
  *Review found no other reader for C#-impl proxies.*
- **R3 — resolved by review.** The C#-impl proxy now has an active finalizer that releases R0 via the
  trampoline. Both reviewers confirm no forced double-release **provided** the existing pattern is
  carried over exactly — `HandleEntry.Released` Interlocked claim + the `_disposed` flag +
  `GC.SuppressFinalize(this)` in `Dispose` + `SwiftExitGuard`. The only delta is that the C#-impl
  proxy gains an active finalizer (load-bearing for R0); keep all four guards.
- **R4 — corrected by review (Codex): B2-introduced, not pre-existing.** Today `RegisterStrong` keeps
  the weak `s_autoWrapCache` value live while Swift holds the existential, so re-wrapping the same
  impl returns the *same* proxy/EveryProtocol — stable Swift-side identity. Under B2 the proxy can die
  while Swift still holds a *stored* `EveryProtocol`, so a later `GetOrCreate` mints a **second** Swift
  carrier → `===` instability for class-bound stored existentials. The obvious mitigation (a strong
  per-impl cache freed on deinit) **recreates the very cycle B2 exists to break**, so it is
  unavailable. Accept as a documented, narrow behavior change; cover with a fixture asserting **value
  round-trip** (not identity). Note this in wiki Known Limitations on ship.

### Footprint

`ProxyLifetimeTracker.cs` (handle-keyed strong impl root + `ResolveImpl` + R0 release entry point),
`SwiftObjectRegistry` (weak register for C#-impl proxies), `ProtocolProxyEmitter.Receivers.cs`
(receiver preambles + ctor: GCHandle alloc, no-SuppressFinalize, weak register),
`ProtocolProxyEmitter.SwiftObject.cs` (finalizer/Dispose release R0),
`ProtocolProxyEmitter.CrossModuleParent.cs` (fallback primitive swap),
`ProtocolProxyEmitter.StaticInit.cs` (field/finalizer doc),
`ExistentialContainer.cs` (`GetOrCreate` `out object? keepAlive` overload — change 4),
`WrapperEmitter.Marshalling.cs` + `PropertyHandler.cs` (existential-arg marshalling: capture
`keepAlive`, emit `GC.KeepAlive` in the finally — change 4; grep all `GetOrCreate<…>(…, out …)`
emission sites first).

### Change 4 — full +0 argument-direction audit (post-review, ground-truthed)

The "grep all emission sites first" was done empirically (own reads + Explore fan-out + Grok consult).
The bare borrowed leaf `ExistentialProjection.GetParameterElementConversion` (`ExistentialContainerFactory.GetOrCreate<…>` with no `keepAlive` capture) and the closure-invoke arg helper `ClosureEmitter.GetSwiftInvokeArgExpression` are the only +0 sites that can hand Swift an `ExistentialContainer1` aliasing a proxy's sole R0. They split into **three mechanisms**, plus two false alarms:

1. **Collection / container element holes — fix = owned-carrier consistency (NOT keepAlive).** The
   collection containers (`SwiftArray`/`SwiftSet`/`SwiftDictionary`/`SwiftOptional`-element/tuple) have
   **owned element semantics** — their value-witness table runs *destroy* on each element at teardown
   (see `ExistentialProjection.cs:202-208`: the bare leaf "aliased the proxy's only +1, which the
   `__owned` consume plus the carrier's value-witness destroy over-released"). So the bare borrow is a
   **pre-existing over-release** bug *and* a B2 UAF. `ArrayProjection` (`:67-70`) and
   `DictionaryProjection` (`ParamValueConversion`/`ParamValueCarrierType`, `:76/:87`) **already** route
   existential elements through `GetArrayElementCarrierConversion` (an independent +1 mint —
   `CreateOwnedExistential1` / `CreateOwnedClassCarrier`) paired with the carrier container type
   `ArrayElementCarrierType` (an `ITypeProjection` member, default `=> MarshalFromSwiftType`; only
   `ExistentialProjection` overrides it to the 16-byte `ClassExistentialContainer1` for the class-bound
   case). The fix makes the divergent sites mirror that exact pairing (carrier conversion **and** carrier
   container generic type, so class-bound stride agrees). Confirmed GENUINE holes:
   `SetProjection.cs:59,242` (B1/B3); `OptionalProjection.cs:110` element-of-container (D1) **plus the
   standalone `Optional<any P>` param at `:245` and its `OptionalTypeParam` at `:135`** (both feed
   `SwiftOptional<…>.NewSome`, both consumed by the C#-side `using SwiftOptional` disposal → VWT destroy);
   `AccessorConversionVisitors.cs:265,288,317` Array/Dict-value/Set
   setters (H3/H5/H7); reverse-dispatch **`Set<any P>` getter returns** (I1). Correction after
   ground-truthing the dispatch order: `GetReceiverGetterConversion` (`:1275`) consults the
   `GetReceiverExistentialGetterConversion` fast-path (`:1711`) **first**, and that fast-path already has
   owned-carrier arms for standalone / `Optional<any P>` / `[any P]` / `Dict<K,any P>` — only `Set<any P>`
   was missing (no Set arm), so it alone fell through to the bare-leaf `GetReceiverSetGetterConversion`.
   **The `[any P]` getter (I2) is NOT a hole** — the `:1739` Array arm intercepts it; `GetReceiver{Set,Array}GetterConversion`
   are reached only for non-existential / nested elements (which delegate to their own now-fixed
   conversions). Fix for I1 = add a `Set<existential>` arm to the fast-path **and** to its lockstep
   carrier-sizing mirror `GetReceiverGetterCarrierTypeCore` (`:1668`), mirroring the Array arm — keeping
   all existential owned-carrier logic centralized rather than patching the bare leaf.
   Implemented via a shared `ExistentialElementCarrier` helper (`ParamConversion` = owned carrier conv for
   existential, bare borrow otherwise; `CarrierType` = stride-correct carrier type or the site's
   non-existential fallback) so the convention can never drift across the collection projections.
2. **Scalar closure RETURN holes — fix = `GetOwnedParameterElementConversion` (owned +1 mint).** A C#
   delegate / async closure that returns `any P` hands Swift a +1-owned existential (same direction as
   the reverse-dispatch getter return that task #8 already fixed). Implemented at `ClosureProjection.cs`
   `CallbackDeclarations` (F2, the escaping-closure C#→Swift thunk return) and `ClosureEmitter.Async.cs`
   (G1, the async `ContinueWith` ABI-shaped return), both routing existential returns through
   `existRet.GetOwnedParameterElementConversion(...)` and falling back to the borrowed leaf for
   non-existential returns. The synchronous closure return (`ClosureEmitter.BuildCallbackReturnStatement`)
   already minted an owned +1 via `CreateOwnedExistential1` (task #8).
3. **Closure ARGUMENT holes — fix = hoist + `GC.KeepAlive`.** A C# lambda that wraps a Swift function
   pointer passes `any P` args **+0 borrowed** into `_fp(...)`; there is no container temp to own a +1,
   so owned-mint would leak — keepAlive is the right fence. Implemented via a new
   `ExistentialProjection.GetKeepAliveParameterElementConversion(elementVar, keepAliveVar)` (the keepAlive
   sibling of `GetParameterElementConversion`): for the EC1 auto-wrap path it emits the change-4
   `GetOrCreate<…>(…, out _, out var {kaVar})` overload, returning `null` for bare-`Any`/EC2+/no-proxy
   (which don't alias a proxy's sole R0). `ClosureProjection.cs:100` (F1, `GetReturnPlan`) hoists the
   `_fp(...)` call into a local and emits `GC.KeepAlive` after it. `ClosureEmitter.GetSwiftInvokeArgExpression`
   gained an optional `keepAliveVars` sink (scalar + tuple-element `qp != null` arms emit the `out var`
   form and record the token); its two reachable consumers fence after the call — the fallback lambda
   (`ClosureEmitter.cs`, hoist into `_invRet` for the return-value shapes / append after the void call)
   and the throwing path (`ClosureEmitter.Throwing.cs`, a `GC.KeepAlive` line right after the already-hoisted
   `_fp(...)`). **The invoke-thunk consumer (`ClosureEmitter.InvokeThunk.cs:398`) is UNREACHABLE with
   existential args** — `IsInvokeThunkCompatibleArg` admits only CdeclPrimitive/simpleEnum/complexEnum/
   by-value-struct, so a closure carrying an `any P` arg fails `CanUseInvokeThunk` and falls to the
   fallback lambda; the sink is left unset there. The fence shape is
   `var c = GetOrCreate(…, out _, out var __ka); var r = _fp(c,…); GC.KeepAlive(__ka); return r;`.

**False alarms (verified SAFE, no change):**
- Scalar existential **method / enum-case / operator** params are forced through a `@_cdecl` wrapper
  (`any P` is not natively lowerable), and that wrapper path already keepAlives them
  (`WrapperEmitter.Marshalling` `existentialHeaps`/`KeepAliveVar` finally; `EnumHandler.CaseConstruction`
  mirror at `:352-460,613-624`). The non-cdecl bare `EnumHandler.CaseConstruction.cs:939` else is
  residual — protocol existentials are explicitly *not* tuple-ABI-compatible (`EnumCaseWrapperEmitter`
  `IsTupleElementAbiCompatible` → false) so they always take the wrapper.
- `MethodSignature.cs:209-211` direct scalar EC1 arg (Grok flagged as an operator/bypass hazard) is
  **unreachable** for ordinary methods (forced to `@_cdecl`) and for `ExistentialBypassEmitter`
  (`IsParamCompatibleForBypass` admits only exact-match / SafeHandle / string params, never an
  `any P`→`ExistentialContainer1` mismatch). Residual.
- Dict **keys** can never be existential (`any P` is not `Hashable`); the owned-carrier collection fix
  touches values/elements only.
- `TupleProjection.cs:72` (E2) — **reclassified out of mechanism 1 after empirical reachability check
  (Explore fan-out + Grok consult `019ec005`, converged).** `GetParameterPlan`'s per-element conversion
  at `:72` is the *direct-function-argument* path only (`TupleProjection` does not override
  `GetParameterElementConversion`, which defaults to null, so it is NOT the tuple-as-container-element
  path). A method whose parameter is a tuple **containing an existential** is forced through `@_cdecl`
  but is **pruned before emission** — the cdecl tuple fast-path is gated `Elements.All(IsCdeclPrimitive)`
  (`WrapperEmitter.Marshalling.cs:1207`, `PInvokeEmitter.cs:557`) and the per-element path is not yet
  implemented (`PInvokeEmitter.cs:554`). **Correction (2026-06-13):** the `MemberValidationPipeline` gate
  that was *supposed* to prune it (`:420`) keyed on `HasClosureUnsafeTupleElements`, which catches only the
  **IntPtr subset** (the closure-callback delegate-shape contract) and **misses** existential / simple-enum /
  class / non-frozen-struct / frozen-mem-mgmt-struct elements whose P/Invoke type differs from their C# type
  without being `IntPtr`. So the gate was **fail-open**: a tuple-of-existential param fell through to the raw
  `ValueTuple` path and emitted a **CS1503** (raw public-type tuple vs a P/Invoke expecting differing element
  types). Fixed this turn by switching the gate predicate to `TupleHandler.HasUnmarshalledTupleElements` — a
  strict superset flagging *any* per-element P/Invoke≠C# mismatch (IntPtr-prefix-normalized) — so such members
  are now cleanly skipped fail-closed with `UnsupportedSignature`. With that gate live, `:72` is genuinely
  never reached with an `ExistentialProjection` element. Unlike `SwiftSet`/`SwiftOptional`/`SwiftArray` (which
  wrap a disposable C# container whose teardown VWT-destroys the elements → owned-carrier), a tuple
  direct-arg has **no container teardown** and the original Swift tuple param is **+0 borrowed** — an
  owned `+1` mint would leak, so even if per-element tuple marshalling is implemented later the correct
  fix is keepAlive (mechanism 3), not owned-carrier. The fence that guards top-level scalar existential
  params (`existentialHeaps`/`KeepAliveVar`, `CSSignature.Skip(1).Where(IsExistential)`) is non-recursive
  and does **not** reach a tuple-nested existential — a latent gap to close only when/if that path ships.

---

## Finding 33 — per-module EveryProtocol metadata (delete the global latch)

`EveryProtocol.cs` holds a single process-global `_typeMetadataHandle` (volatile, first-wins under
`_metadataLock`). The opaque-path proxy ctor stamps it once (`Receivers.cs:2003`,
`EveryProtocol.SetTypeMetadata(NativeMethods.{GetMetadataMethodName}())`) and reads it back to fill
the existential's metadata word (`Receivers.cs:1994`,
`_swiftContainer.ObjectMetadata = EveryProtocol.GetTypeMetadata()`) and via the proxy's own
`GetTypeMetadata()` (`SwiftObject.cs:202`). Each generated module ships its own Swift `EveryProtocol`
type (its own dylib, its own `GetMetadata_EveryProtocol`), but the C# `EveryProtocol` type is shared
in `Swift.Runtime`. In a multi-binding app, module A wins the latch and module B's opaque proxies are
stamped with **A's** metadata.

**Fix.** Stamp metadata per-module instead of through the global. Each generated proxy class is
inherently module-scoped, so emit a per-proxy static cache populated from the *module's own*
`NativeMethods.{GetMetadataMethodName}()`:

```csharp
private static readonly Swift.Runtime.TypeMetadata s_everyProtocolMetadata =
    Swift.Runtime.TypeMetadata.FromHandle(NativeMethods.{GetMetadataMethodName}());
```

Use `s_everyProtocolMetadata` in the opaque container init (`Receivers.cs:1994`) and the proxy's
`GetTypeMetadata()` (`SwiftObject.cs`). Delete `EveryProtocol.SetTypeMetadata` / the global
`_typeMetadataHandle` / `GetTypeMetadata()` (or leave them `[Obsolete]` and unused if any external
caller exists — grep first). The class-bound paths (`_useObjCBase`/`_useEntityBase`) already skip
metadata entirely and stay unchanged. This dovetails with the Defect G ctor rewrite.

Coverage: a two-module BindingTests fixture (module A + module B, each with an opaque-layout
reverse-dispatch protocol) asserting B's proxy carries B's metadata, not A's. Plus a unit test that
the emitted ctor references the per-module `NativeMethods` accessor and not `EveryProtocol.SetTypeMetadata`.

---

## Defect C — method-index skew (optional-before-required)

Two independent per-method numbering schemes both compute an index by walking `protocolDecl.Methods`;
in each, the **producer skips `@objc optional` before incrementing** but some **consumers do not**, so
an optional method that precedes required methods desynchronizes producer/consumer indices.

**Scheme #1 — witness-accessor symbol** (`SBW_{P}_method_{name}_{idx}`, C#→Swift):
- Producer `WitnessDispatchEmitter.cs:288` — skips optional **before** increment ✓.
- Consumer `ProtocolProxyEmitter.SwiftObject.cs:599` (P/Invoke decl, feeds `GetAccessorSymbol`) — **does not skip** ✗.
- Consumer `ProtocolProxyEmitter.InterfaceImpl.cs:105` (P/Invoke call) — **does not skip** ✗.
- Symptom: required method's P/Invoke names a symbol the dylib doesn't export → `EntryPointNotFoundException`.

**Scheme #2 — vtable field index** (Swift↔C# vtable struct slot position):
- Swift producer `EveryProtocolEmitter.cs:562` — skips optional **before** increment ✓.
- C# consumers do **not** skip (mutually consistent with each other, misaligned with the Swift
  producer): `Vtables.cs:72,151`; `StaticInit.cs:222,282,576,624`; `Receivers.cs:71`.
  (`StaticInit.cs:617` carries a comment asserting "optional still consumes the index — matching
  Vtables.cs / Receivers.cs"; that contradicts the Swift producer and must be resolved, not trusted.)
- Symptom: C# reserves a vtable slot for the optional method that the Swift struct doesn't → positional
  field misalignment (the Finding-8 corruption shape) for vtable-dispatched required methods.

**Fix.** Add `if (method.IsObjCOptional) continue;` immediately after the existing
`IsConstructor || Static` skip, **before** the increment, at the divergent consumer walks so they
match their producers. Scheme #1: the 2 walks (`SwiftObject.cs:599`, `InterfaceImpl.cs:105`). Scheme
#2: align all 7 C# consumer walks to the Swift producer **atomically** (they must stay mutually
consistent). Whether scheme #2 actually triggers depends on whether `@objc optional` methods reach the
vtable path — the maximum-case fixture below decides this empirically (TDD red); patch exactly the
walks the red fixture proves wrong, but treat the audited 9 as the candidate set.

**Fixture (maximum-case, red-first).** A new `@objc protocol …: NSObjectProtocol` with **an optional
method first**, then required methods exercised **both** directions: one required method dispatched
**forward** (C#→Swift on a Swift-vended existential, scheme #1) and one dispatched **reverse**
(Swift→C# via the vtable on a C#-impl, scheme #2). (No allowlist step — Session 7b retired the
harness stripper; valid conformances are preserved automatically.) The existing
`OptionalCallbackDelegate` has its required method *first*, so it does not trigger C — this
must be a distinct fixture with optional-first ordering.

---

## Defect F — vtable property-slot membership divergence + Finding 31 invariant

Three predicates decide "does this protocol property get a vtable slot?" and they diverge on the
`IsProtocolRequirement` axis:

- Vtable **struct** emitter `EveryProtocolEmitter.cs:503`: `IsStatic || IsObjCOptional` (no
  `!IsProtocolRequirement`).
- `ProtocolVtableMembers.IncludesProperty:22` (the documented "single source of truth", used by the
  cross-module parent scaffolding): `IsStatic || IsObjCOptional` (no `!IsProtocolRequirement`).
- Plan/fan-out builders `EveryProtocolEmitter.cs:680, 703, 774`:
  `IsStatic || IsObjCOptional || !IsProtocolRequirement`.

A non-requirement property (e.g. a protocol-extension default-impl property) is therefore *included*
in the struct layout + `IncludesProperty` but *excluded* by the populators → a struct slot the
populator never fills, and since Swift copies the vtable positionally, populated fields land in the
wrong Swift slots (Finding-8 corruption). Correct direction: a non-requirement property has no C#
override to dispatch to (Swift owns the default impl), so it should have **no** slot — **exclude** it
everywhere.

**Fix.** Make `ProtocolVtableMembers.IncludesProperty` authoritative for the `!IsProtocolRequirement`
exclusion and route the struct emitter (`:503`) through it (or add `|| !property.IsProtocolRequirement`
to both `:503` and `IncludesProperty:22`). Note the plan builders apply only a *subset* of
`IncludesProperty`'s conditions (they do not exclude closure/Self/mixed-generic properties), so full
predicate unification is not a literal merge — the invariant test pins the intended relationship
rather than asserting byte-identity.

**Finding 31 invariant test.** A unit test that constructs synthetic `PropertyDecl`s across the flag
matrix `IsStatic × IsObjCOptional × IsProtocolRequirement × IsFromExtension` and asserts
`ProtocolVtableMembers.IncludesProperty` agrees with the plan-builder predicate on slot membership
for every combination — locking the three sites together so they cannot re-diverge.

**Fixture (red-first) — reachability finding (2026-06-13).** The originally-planned end-to-end
fixture (a required property + a *protocol-extension* non-requirement property) is **unreachable**:
the parser strips protocol-extension non-requirement members at the population site
(`SwiftABIParser.cs:1085` keeps `!(IsFromExtension && !IsProtocolRequirement)`), so an
`IsFromExtension && !IsProtocolRequirement` property never reaches the emitter — a BindingTests
fixture of that shape would pass *pre-fix* (the divergent property is gone before any emitter
predicate sees it), i.e. green-for-the-wrong-reason. The only row that survives the parser to trigger
the emitter-layer divergence is `!IsFromExtension && !IsProtocolRequirement` (a body property the ABI
digester marks `protocolReq=false`), which normal Swift source does not produce on demand. The
durable gate is therefore the **emitter-layer invariant test** (`Finding 31`), which constructs the
divergent `PropertyDecl` directly (bypassing the parser) and pins `IncludesProperty`, the struct
emitter, and the plan populators together — verified red (reverting the `IncludesProperty` guard fails
the `static=False objcOpt=False req=False ext=False` row) → green. The emitter fix is therefore
defense-in-depth: it keeps the three predicates consistent so *any* future path that lands a
non-requirement property at the emitter (parser-filter change, a `protocolReq=false` body property,
or a synthetic decl) cannot shift the vtable layout.

**Method-side check (done).** The same `!IsProtocolRequirement` asymmetry does NOT exist on the method
path: `EnumerateProtocolMethodsForDispatch` (the `ComputeMethodEmissionPlans` source), the vtable
struct method walk, and `ProtocolVtableMembers.IncludesMethod` all skip `ctor/static/@objc-optional`
and *none* filter `!IsProtocolRequirement` — so all three agree and there is no positional skew. The
property fix restores the consistency the property populators already had; the method side needs no
change.

---

## Sequencing & gates

Independent, lower-risk items first to de-risk the session, then Defect G:

1. **Defect C** (skip-before-increment) + max-case fixture.
2. **Defect F** (predicate unification) + Finding 31 invariant + fixture.
3. **Finding 33** (per-module metadata) + two-module fixture — folds into the Defect G ctor rewrite.
4. **Defect G** (Design B2) + abandon-impl-then-callback probe fixture.

After generator edits: rebuild the Debug generator dll (`dotnet build src/Swift.Bindings/src -c
Debug`) before any gate — `nuke binding-tests`/`validate` run the prebuilt `bin/Debug` generator and
only rebuild when the dll is missing, not when stale.

Gate matrix: `nuke test` → `nuke binding-tests --compile-only` → `nuke binding-tests` (sim, Mono JIT)
→ `nuke binding-tests --device` (NativeAOT — mandatory, these changes touch calling conventions,
vtable/struct marshalling, and P/Invoke entry points). Zero-regression: BindingTests + unit pass
counts ≥ baseline. `nuke validate` is optional here (run it as a pre-merge cross-cutting sweep given
the generator surface touched).

## Review questions for Grok/Codex (design review)

1. Is the cycle/impossibility argument for Defect G sound — is there a correct root-the-impl design
   that keeps the registry-strong proxy (avoiding the ~16-site receiver sweep) that I've missed?
2. Is `ResolveImpl<IFace>(handle)` a faithful drop-in for the sibling-fallback `TryGetProxy<
   IProtocolProxyImpl<IFace>>(handle) → UserImpl`, same-module and cross-module (risk R1)?
3. Any finalizer double-release / ordering hazard in moving R0 release to the proxy finalizer (R3)?
4. Is the scheme-#2 method-index fix actually triggerable, and is aligning C# consumers to the Swift
   producer (vs. the reverse) the correct direction?
5. Defect F direction: exclude non-requirement properties everywhere — agree?

## Review outcome (2026-06-12)

Paired design review run (🔍design gate). Both reviewers independently confirmed the Defect G
cycle/impossibility argument is sound and that no correct *registry-strong* design avoiding the
receiver-lookup change exists without a separate lifetime-token concept.

- **Codex** (Session `019ebf4a-6b2a-7a31-8d0d-78409a2a343a`): one **High** — under B2 the auto-wrapped
  existential's backing proxy is unrooted across the native call (GC race → premature R0 release /
  UAF). Folded in as **design change 4** (`GetOrCreate` `out object? keepAlive` + `GC.KeepAlive` in the
  marshalling finally). Medium (ResolveImpl wider predicate) and Low (R4 identity instability is
  B2-introduced) folded into R1/R4 above.
- **Grok** (sessionId `019ebf4a-c019-7300-9b3f-d1387adb9b07`): confirmed Q1–Q5; flagged that the
  existing `ProxyLifetimeTests.TestStrongSwiftRetainSurvivesImplGc` currently *tolerates* fabrication
  and should be flipped to assert the now-correct live value; confirmed the optional-first max-case
  fixture is the load-bearing red for Defect C/scheme-#2.

Design gate cleared — the one High is an addition *inside* B2, not a redirection. Proceeding to
implementation.

## EC2+ composition owned-mint + fail-closed tuple gate (2026-06-13)

Change 4 fixed the EC1 (single-protocol) owned-mint / keepAlive sites. This fold-in closes the EC2+
(composition `any P & Q…`) half of the same category and lands the tuple-parameter fail-closed gate.

**Owned EC2 return (mint, always — no donate arm).** The only C# type implementing a composition
interface is the Swift-vended `{Composition}Proxy`; there is **no `BoxAsExistential2`**, so an owned
composition existential flowing C#→Swift can only ever come from a proxy whose `GetExistentialContainer()`
hands back its stored bytes with **no fresh retain**. An owned EC2 return therefore *always* mints an
independent +1 (never borrows), via the new arity-generic runtime helper
`ExistentialContainerFactory.CreateOwnedCompositionExistential<TProtocol, TContainer>` — byte-level
arity-generic over EC2–EC8 through `IExistentialContainer.Count` + `TypeMetadata.GetExistentialTypeMetadata(count)`
+ `CopyWireBufferRetains`. Wired at the two owned-return emission sites that Change 4's EC1 mint did not
cover for compositions: the closure-return (`ClosureEmitter.BuildCallbackReturnStatement`, scalar +
tuple-element arms) and the reverse-dispatch getter return (`ExistentialProjection.GetOwnedParameterElementConversion`,
EC2+ branch between the EC1 mint and the borrowed fallback).

**Borrowed EC2 argument (keepAlive).** A composition existential passed +0 borrowed aliases the proxy's
sole R0; `WrapperEmitter.EmitExistentialHeapDeclarations` now pins the parameter itself (`GC.KeepAlive(csName)`
in the cleanup `finally`) for the borrowed branch — the EC2 analogue of the EC1 `keepAliveVar` fence.

**Fail-closed tuple-of-convertible-element parameter gate.** See the E2 correction above: the prior gate
was fail-open and emitted CS1503 for a tuple param containing an existential (or any element whose P/Invoke
type differs from its C# type). Gated by `HasUnmarshalledTupleElements` → skipped with `UnsupportedSignature`
for elements still out of scope. **This gate was the safe interim; the buffer-marshal path that supersedes
it for the supported element kinds is now implemented — see "Tuple-of-convertible-element parameter
marshalling (Option A)" below.** `describeNameableAgeablePair` is now MARSHALLED (no longer a "correctly
rejected" fixture); the validator lets a tuple through whenever `IsCdeclBufferMarshallableTuple` covers
every element (the gate is `HasUnmarshalledTupleElements && !IsCdeclBufferMarshallableTuple`). The
`HasUnmarshalledTupleElements` predicate itself is unchanged and still pinned by
`TupleHandlerTests.HasUnmarshalledTupleElements_*`.

**Coverage.** EC2 lifetime sites are gated by `CompositionArgLifetimeProbeTests` (sim Mono-JIT + device
NativeAOT): closure-return mint, reverse-dispatch-getter mint (deterministic double-free probes around a
surviving owner — assert live==1 through the call, ==0 after Dispose), and borrowed-arg keepAlive
(no-crash / no-leak / round-trip under induced GC pressure). The new reverse-dispatch protocol
`NameableAgeableProvider` is preserved automatically by the generator's `SwiftWrapperPostProcessor.Process`
(Session 7b retired the harness `PreservedProtocols` allowlist).

**Deferred / split-out units.**
- **Full tuple-of-convertible-element parameter marshalling** — ✅ **DONE** (Option A; see the dedicated
  subsection below). The originally-imagined "4-layer" path (`IsConvertibleType` gate + `GetCallArgumentString`
  threading `plan.PInvokeExpression` + validator + unsafe flag) turned out to be the WRONG ABI model — a
  tuple parameter always forces a `@_cdecl` `UnsafeRawPointer` buffer and `ValueTuple` is `StructLayout.Auto`,
  so the convertible-call-arg path it described is never reached. The correct implementation extends the
  dedicated `@_cdecl` stackalloc buffer block instead. Shipped incrementally: v1 pure Swift class elements,
  v2 `Swift.String`, v3 composition (EC2+) existentials. Single-protocol (EC1) / bare-Any (EC0) existentials,
  simple enums, and non-frozen / frozen-mem-mgmt struct elements stay fail-closed.
- **EC2+ composition collection-element carrier owned-mint** (`[any P & Q]` / `Set<any P & Q>` /
  `Dict<K, any P & Q>` params + reverse-dispatch returns). `ExistentialProjection.GetArrayElementCarrierConversion`
  still routes EC2+ composition elements to the borrowed `GetParameterElementConversion` fallback. Reachability
  not yet confirmed with a live fixture (no composition-collection-param fixture exists today), so this is
  latent, not a live regression; route through `CreateOwnedCompositionExistential` + add a leak probe when a
  reachable site is confirmed.

### Re-review fold-in — full EC2+ borrowed-keepAlive category sweep (2026-06-13)

The post-hoc paired review (Codex + Grok) surfaced three Highs that, on empirical audit, were the same
bug shape at more sites than the initial EC2 fold-in touched. Rather than whack moles, the whole category
was enumerated (Explore fan-out) and fixed in one pass:

1. **Runtime owned-mint helper missing its own keepAlive.**
   `ExistentialContainerFactory.CreateOwnedCompositionExistential<TProtocol, TContainer>` did the
   synchronous `CopyWireBufferRetains` mint without rooting `value` across it — the EC1 helpers
   (`CreateOwnedClassCarrier`, `CreateOwnedExistential1`) all `GC.KeepAlive` their source after the mint.
   Added `GC.KeepAlive(value)` after the copy, before `return owned`.

2. **Borrowed EC2+ closure / property / enum / struct-param argument sites.** Beyond the
   `WrapperEmitter` top-level-param fence, the same +0-borrowed-proxy-aliases-R0 hazard lived at every
   *other* place an `any P & Q…` value is cast to `ISwiftExistentialConvertible<{containerN}>` and its
   `GetExistentialContainer()` bytes are handed to a native call without rooting the proxy across it:
   - `PropertyHandler.EmitSetterBody` — the Optional-existential `@_cdecl` setter (`!useFactory` EC2+
     branch): pins `value` in the `finally` (`GC.KeepAlive(value)`), the EC2 analogue of the EC1 `__keepAlive`.
   - `ClosureEmitter.GetSwiftInvokeArgExpression` — the scalar and tuple-element EC2+ arms (the EC1 arms
     already captured a keepAlive var): add `_arg{i}` / the tuple-element accessor to the `keepAliveVars`
     sink, which the consumer hoists `_fp(...)` around and `GC.KeepAlive`s after. Because the throwing
     closure path routes through this same shared helper, it is covered transitively.
   - `ClosureEmitter.StructParams.cs` — both `EmitClosureReturnMarshallingWithStructParams` and
     `…WithNonFrozenParams`: collect EC2+ args into a `keepAliveArgs` list, hoist the `_fp(...)` call into
     `_invRet` when there's a return, and emit `GC.KeepAlive` after. Empty list ⇒ byte-identical to before.
   - `EnumHandler.CaseConstruction.cs` — the enum `@_cdecl` case factory: the EC1 owning path is unchanged;
     the EC2+/well-known borrowed path now records the param `name` as its `keepAliveVar` (records-present,
     not-owning), consumed by the existing `finally` keepAlive emission.

   **Deterministic proof of emission** (the GC-timing UAF window is intra-wrapper and cannot be forced red
   from a test caller — see `TestBorrowedCompositionArgRootedAcrossCall`'s doc): emitter-layer unit tests
   `PropertyHandlerTests.Emit_OptionalCompositionExistentialProperty_SetterPinsValueAcrossNativeCall` and
   `ClosureEmitterDirectTests.EmitClosureReturnMarshalling_CompositionExistentialArg_PinsProxyAcrossNativeCall`
   assert the EC2 direct-cast container path **and** the `GC.KeepAlive` are present for a two-protocol
   composition. End-to-end no-crash/no-leak/round-trip is the existing `CompositionArgLifetimeProbeTests`.

**Residual (documented, not fixed — non-xcframework legacy mode only).** `MethodSignature.GetCallArgumentString`'s
`MarshalledType.Existential` catch-all (the direct-`CallConvSwift` scalar existential arg, no `@_cdecl`
wrapper) is reachable **only** in non-xcframework mode (`AsyncLibraryName` unset). In the primary/shipping
xcframework mode an `any P & Q` param always takes the `@_cdecl` path (`CdeclExistential` → `WrapperEmitter`
pin), so this site never fires there. Non-xcframework direct-`CallConvSwift` is the legacy, Issue-2-constrained
(non-blittable-`CallConvSwift` rejection) mode; it is left as a known residual rather than fixed, because the
keepAlive fence belongs on the `@_cdecl` path that mode bypasses. Tracked here so it isn't mistaken for covered.

**Investigated and cleared — `ClosureProjection` is NOT a missed site.** A re-review pass questioned whether
the projection-based closure path is a parallel emission mechanism that also needs the EC2+ keepAlive:
`ExistentialProjection.GetKeepAliveParameterElementConversion` returns `null` for EC2+ composition, and its
sibling `GetParameterElementConversion` emits the borrowing `GetExistentialContainer()` form with no pin — so
in isolation it *looks* like a 7th uncovered UAF. It is not: that method's sole caller is `ClosureProjection`'s
lambda-builder (`GetReturnPlan`/`GetParameterPlan`), which is **dead code for live closure emission**.
`WrapperEmitter` diverts every closure return at `WrapperEmitter.Return.cs` (the `IsClosure(returnArg)` guard
fires before the projection is built) and every closure parameter at the `WrapperEmitter.Marshalling.cs`
parameter loops (closure typespecs are neither `IsConvertibleType` nor `OptionalExistential`, and
`IsOptionalClosure` is explicitly `continue`d), routing all closure marshalling — EC2+ composition args
included — to the string-emitter `ClosureEmitter` instead. `ClosureProjection` is still constructed for
auxiliary reads (`PublicType` in tombstone dedup / the member validator / async-return-type projection), but
those never invoke the lambda-builder, so no borrow is ever emitted through it. The misleading "no R0 aliasing"
rationale in both files' comments was corrected to state the accurate reason (no `GetOrCreate` overload) and to
point at the live `ClosureEmitter.GetSwiftInvokeArgExpression` keepAlive.

## Tuple-of-convertible-element parameter marshalling (Option A) — 2026-06-14

Implements the deferred "Full tuple-of-convertible-element parameter marshalling" unit. The literal "4-layer"
framing in the deferral note is the WRONG ABI model and was deliberately NOT followed.

**Why not the 4-layer / `IsConvertibleType` path.** A non-empty tuple PARAMETER always forces a `@_cdecl`
wrapper: the Swift side receives the tuple as an `UnsafeRawPointer` and reconstructs it with
`pointer.assumingMemoryBound(to: (T1, T2, …).self).pointee`. The C# side therefore never passes a `ValueTuple`
through P/Invoke (it is `StructLayout.Auto`, which is not blittable), so flipping `MarshallingHelpers.IsConvertibleType`
and threading `plan.PInvokeExpression` through `GetCallArgumentString` — the "convertible call-arg" path the
deferral imagined — is never reached for a tuple param. The correct seam is the **dedicated `@_cdecl` tuple
buffer block** already in `WrapperEmitter.Marshalling.cs`: allocate `stackalloc byte[tupleMeta.Size]`, write
each element at `tupleMeta.AsTupleMetadata()->GetElementOffset(i)`, and pass `(IntPtr)buf`. Extending that
block per element kind is Option A.

**Supported element kinds (incremental).**
- **v1 — pure Swift class** (`TypeRecordKind.Class`, non-ObjC): an 8-byte pointer slot written as the borrowed
  (+0) object handle (`.Payload.DangerousGetHandle()`). Metadata `GetTypeMetadataOrThrow<Wrapper>()`.
- **v2 — `Swift.String`**: a 16-byte (two-word) value slot. The element is projected as a `Swift.SwiftString`
  that owns its storage, so its borrowed 16-byte value is bit-copied (`Unsafe.Read<SwiftString.Buffer>` through
  the payload handle) into the slot — no fresh mint, no UTF-8 round-trip, no `Dispose`. Metadata
  `GetTypeMetadataOrThrow<Swift.SwiftString>()`.
- **v3 — composition existential (EC2+, ≥2 non-marker protocols, e.g. `any Nameable & Ageable`)**: a fixed-stride
  opaque-existential container slot (EC2 = 48 bytes / 6 words). The element is projected to its
  `ExistentialContainerN` via `((ISwiftExistentialConvertible<ExistentialContainerN>)elem).GetExistentialContainer()`
  — for a composition this is ALWAYS a +0 BORROWED container (`owns == false`; only the EC1 boxing branch can
  own a +1) — and `CopyTo`'d into the slot. Metadata `GetExistentialTypeMetadata(count)` (the opaque-existential
  stride is determined by the non-marker protocol count alone).

**Shared lifetime model — borrow + source keep-alive, no per-element teardown.** Class, String, and composition
slots all hold a +0 borrow that aliases the *source tuple's* ARC root. The stackalloc buffer is pure transport;
nothing in it is owned. The single safety obligation is to root the owning `ValueTuple` past the native call,
emitted once as `GC.KeepAlive(<param>)` in the wrapper's cleanup `finally` (`WrapperEmitter.EmitTupleParamKeepAlive`,
fired when any element returns true from `TupleHandler.TupleElementNeedsBorrowKeepAlive`). The Swift wrapper's
typed `.pointee` load retains each element for the call's duration, so the +0 alias is valid throughout. Primitive
scalars are written by value and need no keep-alive.

**Deliberately fail-closed (require a different mechanism).** Single-protocol (EC1) and bare-Any (EC0) existentials
can box a value-type conformer at +1, so they need per-element owned-payload teardown — out of scope for the
borrowed buffer. Simple enums (underlying-int vs enum type), non-frozen structs and frozen-mem-mgmt structs
(stored inline at value size; a handle write would corrupt the slot) also stay fail-closed. **Mixed ObjC+Swift
compositions** (e.g. `any NSCopying & SwiftP`) are fail-closed too: the public element type filters ObjC protocols
out (`GetEffectiveProtocols`) and collapses to a single interface backed by `ExistentialContainer1`, while the ABI
slot/cast keep the unfiltered EC2+ count — the same filtered-count mismatch the EC2+ guards in `BoundGenericsHandler`
and `ClosureHandler` reject, now mirrored in `IsCompositionExistentialElement` (`filteredCount == Protocols.Count`).
**Over-arity tuples** (>7 elements) stay fail-closed in `IsCdeclBufferMarshallableTuple` (parity with
`IsSupportedTuple`'s `MaxSupportedTupleElements`), since `TypeMetadata.GetTupleTypeMetadataFromElements` throws above 7.
These remain gated by `HasUnmarshalledTupleElements && !IsCdeclBufferMarshallableTuple` → `UnsupportedSignature`.

**Surface.** `TupleHandler.IsCdeclBufferMarshallableElement` is the per-element predicate (primitive / String /
composition existential / borrowed class); `IsCdeclBufferMarshallableTuple` is its all-elements lift;
`IsCompositionExistentialElement` + `GetCompositionExistentialElementConversion` +
`GetCompositionExistentialElementProtocolCount` drive the EC2+ arm. The `WrapperEmitter.Marshalling.cs` metadata-args
and buffer-write loops branch per kind. **Tests.** Unit: `TupleHandlerTests` (per-element + per-tuple predicate
coverage, the combined-gate `…IsAlsoUnmarshalledButNowBufferable` cases, the `…MixedObjCSwiftComposition_ReturnsFalse`
parity guard, and the `…OverArityWithBufferableElements_ReturnsFalse` arity guard). Runtime (BindingTests sim + device):
`TupleMarshallingTests` value-oracle + GC-pressure tests over `sumBoxedPair`/`combineBoxAndScalar` (v1),
`joinStringPair`/`describeLabeledBox` (v2), `describeNameableAgeablePair` (v3). Swift fixtures in
`Tuples/TupleOfClassParam.swift` and `Protocols/CompositionArgLifetime.swift`.
