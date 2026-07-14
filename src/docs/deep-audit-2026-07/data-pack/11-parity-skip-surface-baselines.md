# Data Pack — Parity / Skip-Surface / API-Manifest Baselines

**Date**: 2026-07-16  
**Sources**: `build/baselines/{parity,skip-surface,api-manifest,runtime-identity}-baseline.json`

---

## Parity baseline (ArtifactParityGate)

| Bucket | Count / content | Meaning |
|--------|-----------------|--------|
| symbol_forward_known_missing | **8** symbols (SwiftBindings module) | C# P/Invoke expects symbol not in wrapper — **allowlisted** |
| symbol_reverse_known_orphans | **6** | Wrapper exports helpers C# doesn't declare (CancelTask, error helpers, EveryProtocol release, …) |
| struct_arity_known_mismatches | **0** | Clean |
| vtable_field_known_mismatches | **0** | Clean — layout parity healthy |
| vtable_cs_only_known | **20** protocol names | C# vtable fields without Swift twin — known set |

**Forward missing symbols (concrete worker targets if not intentional):**

- `SBSW_MCB_*` method-closure-bridge run/filter (2)  
- `SBW_CombinedMixedSelfGeneric_*` get/set/free combinedName (3)  
- `SBW_PhantomOwnerMixedGeneric_*` get/set/free phantomName (3)  

**Reverse orphans (expected helpers):**  
`SBW_CancelTask_*`, `SBW_GetErrorDescription_*`, `SBW_ReleaseError_*`, `SBW_ReleaseEveryObjCProtocol`, `SBW_ReleaseEveryProtocol`, `SBW_UnregisterTask_*`

**Worker note:** vtable field mismatches at **0** corroborates Wave 2 “layout SSOT sound.” Forward missing MCB/mixed-generic props are residual fixture debt.

---

## Skip-surface baseline (opt-in `--skip-surface`)

| Metric | Value |
|--------|------:|
| Entries | 73 |
| Dominant marker | **80×** “No @_cdecl wrapper or native thunk…” (SB0001-class surface) |
| Unsupported closure fallback | 10 |
| Existential fallback | 6 |
| Misc method-level | rest |

Sources almost all `BindingTests/output/SwiftBindingsTestLib.cs`.  
**CI:** not on default PR path (needs `--skip-surface`).

---

## API manifest baseline

| Metric | Value |
|--------|------:|
| Entries | **2558** | Surface ratchet for BindingTests API shape |

---

## Runtime identity baseline

| Platforms keyed | `simulator` (at least) |
| Worker: device identity may be separate / sparse |

---

## Cross-link to CI pack (03)

Enforced in compile-only / CI: parity + api-manifest + strip.  
Skip-surface: opt-in.  
Validation skip_metrics: advisory only.
