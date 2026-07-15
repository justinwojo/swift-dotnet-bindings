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
| `coverage-report.py` not in CI | **Runs in CI** (`ci.yml:111`, `release.yml:206`, `sys.exit(1)` fails); real defect is the weak fail criterion — see §4.4 | T |

---

## Parser / visibility

| Claim | Reality | Track |
|-------|---------|-------|
| DA-W5-A8-002 `public nonisolated` visibility drop | **Refuted / unreachable** — swiftc canonicalizes to `nonisolated public` in generated `.swiftinterface` (the only form the walker reads); 0 corpus hits for the failing order. See §4.1 | A8 |
| S1-37 `ComputeEntryPoint(MethodDecl)` dead / safe delete | **Refuted** — 3 live callers (`CrossModuleExtensionEmitter.cs:633/694/734`), overloads diverge post-AF13; superseded by D7. See §4.3 | A1 |
| G1-003 produce-throw silently compile-but-dead | **Substantially shipped** — SB0006 `[Obsolete(error:true)]` makes calling one a compile error; only the consume-degraded arm survives. See §4.2 | G1 |
| A1 §2: iOS `output/` drops `Wrapper.swift` | **Retained** (~4.3 MB on an iOS regen; gitignored run artifact). See §6.6 | A1 |
