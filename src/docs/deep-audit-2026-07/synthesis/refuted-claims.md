# Refuted / Verified-Clean Claims — This Audit Pass

Do **not** re-file these as open P0s without new reachability.

---

## Architecture / reverse-dispatch

| Claim | Reality | Track |
|-------|---------|-------|
| Layout still consults skip sets / F8 as written | Layout = `IncludedSlots` only; skip sets = fillability | A5c, W10 |
| Legacy blocking async missing `default(CancellationToken)` | **Fixed** + BindingTests | A5b |
| Orphan receivers on overload collapse | **Fixed** via `emittedRawKeys` | A5b |
| Projected key gates vtable membership | **Does not**; GetMethodKey for slots | A5a |
| Slot-shift as dominant open class | Largely **solved** post-VtableLayout | W2 |

---

## CSM / TypeDB

| Claim | Reality | Track |
|-------|---------|-------|
| Class returnsGenericParam carrier wrap open | **Fixed** | A6 |
| MethodGenericBridge AllocHGlobal antipattern open | **Fixed** | A6 |
| Self not substituted in CSM | **Fixed** | A6 |
| Primary T:P&Q composition broken | **Fixed** (method-where residual only) | A6 |
| F15 IsOptionalObjCBridged parity open | **Shared predicate** | M3 |
| Projection visitors missing arms | **Exhaustive** 24 arms | M3 |

---

## P/Invoke / packaging

| Claim | Reality | Track |
|-------|---------|-------|
| Widespread CallConv/arity mismatch in BindingTests | **≥20 pairs MATCH** | A1 |
| will-be-produced NativeReference broken | **Closed** | M2 |
| Primary arch fold loses primary | **try/catch/finally restore** | M2 |
| Arch option only on one path | **Shared** CompileWrapperForArchitectures | M2 |
| inout blittable writeback totally missing | **cdecl path fixed** (KeyPath e2e residual) | A2 |

---

## Closures / async / runtime

| Claim | Reality | Track |
|-------|---------|-------|
| Optional closures not treated escaping | **IsEffectivelyEscaping** | A4 |
| Layer2 uses .Any() | **.All()** | A4 |
| Need full async-emitter merge | **Rejected** (again) | A7 |
| New Mono upstream issues found | **None**; still exactly 4 + comment | A3, W6 |
| Runtime double-free open class | **0 new** emission-live P0 | W6 |

---

## Tests

| Claim | Reality | Track |
|-------|---------|-------|
| Widespread MonoJitCrash still present | **0** | T |
| strip tripwire not zero | **0** enforced | T |
| compile-only always permissive | **Fail-closed** on real integrity legs | T |
