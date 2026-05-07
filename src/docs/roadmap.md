# Roadmap

This doc covers longer-term themes, blocked items, and lower-priority ideas. Live baseline counts live in `.validation-baseline.json`; per-library status lives with each package.

> **Every skipped test is guilty until proven innocent.** There are exactly 4 confirmed upstream .NET runtime behaviours — see `Blocked` section below + memory `feedback_mono_jit_blame.md`. If a crash doesn't match one of these, it's our bug.

---

## Medium Priority

| Item | Notes |
|------|-------|
| **Generated-local name registry** | Codex round-2 follow-up: many emitted locals are still hardcoded names (`tag`, `optionalMetadata`, `resultPtr`, `resultBuffer`, `swiftResult`, `_swiftResult`, `existentialResult`, `returnMetadata`, `swiftIndirectResult`). A Swift method whose parameter is projected to any of those names would shadow the generated local. No real-world repro — punted from the `result` collision fix. Real fix: a `LocalNameRegistry` consulted by `MethodMarshalPlanBuilder`, `FailableFactory`, and every projection emitter so all generated locals are guaranteed unique against projected parameter names. Apply only when a real collision surfaces. |
| **Multi-PAT existential boxing** | A type conforming to 2+ PAT protocols cannot box through the `object` fallback because the `typeof(object)` dictionary key is ambiguous. Guarded to fail explicitly (`InvalidCastException`) rather than silently select the wrong witness table. Extremely rare in practice. |
| **Mixed-indirect generic tuple returns** | Bare-generic shape (`(T, U)`, `(T, U, V)`) is covered by `IsMultiElementGenericTupleIndirectReturn`. Mixed and bound-generic shapes — `(T, Int)` → `(@out T, Int)`, `(Array<T>, T)` → `(Array<T>, @out T)`, `(UnsafePointer<T>, T)` → `(UnsafePointer<T>, @out T)`, `(Optional<T>, T)` → `(@out Optional<T>, @out T)` — fall through to the legacy `SwiftIndirectResult` path with the wrong shape. Real fix: per-element address-only/direct ABI classifier driving a partial-indirect P/Invoke signature. No active repro from validation libraries; un-block when one surfaces. |
| **Protocol-side dedup ignores argument labels** | `ProtocolSignatureHelper.GetMethodSignatureKey` keys witnesses by name + positional types only — protocol witness matching is positional in Swift. But two protocol requirements with the same positional shape and different labels (e.g. `request(_:didCreateTask:)` vs `request(_:didReceiveTask:)`) would still collapse there. No active repro from validation libraries — pin if one surfaces. |
| **Stripper subscript witness key collapses overloaded subscripts** | `SwiftSourceStripper.MakeVtableWitnessKey` normalizes every `func_subscript_<index>_get/_set` to the single key `WitnessKey("subscript", "subscript")`. If a preserved EveryProtocol extension has two or more overloaded subscripts and cross-extension dedup drops only one, the missing overload is invisible to the cross-extension preservation pass. No active repro from validation libraries or BindingTests. Real fix: parse the subscript parameter shape on both the vtable-field side and the `public subscript(...)` declaration side and pair by signature. |
| **Constrained-generic PWT plumbing for non-accessor P/Invokes** | `EnumHandler.RawRepresentable.cs:146,254` and `OperatorHandler.cs:453,481` still pass bare `GetMetadataArgumentList()`. Not triggered by any current validation library — leave alone until a repro surfaces. |
| **Wrapper-helper path dynamic PWT resolution** | Swift wrapper side still fail-closed for Self-requirement / associated-type protocols. Not triggered by any current validation library. |

---

## Low Priority

| Item | Notes |
|------|-------|
| **Performance benchmarks** | Baseline P/Invoke overhead measurement. [`Future/interop-performance-validation-plan.md`](Future/interop-performance-validation-plan.md) |
| **API snapshot tooling** | Detect API surface drift between versions. [`Future/api-snapshot-tooling.md`](Future/api-snapshot-tooling.md) |
| **tvOS device runner** | Requires provisioning profile + physical Apple TV. Generator, SDK, runtime, and build infra already support tvOS; only the `nuke runtime-tests-tvos-device` Nuke target and deployment mechanism are missing. |
| **UnsupportedClosure remaining shapes** | ~188 skips. Already reduced via setter-only closure properties and the async-closure bridge (throwing 0–3 args with primitive returns plus zero-arg `Foundation.Data` return; non-throwing 0–3 args with primitive returns only). Remaining are generic params, nested closures, and async-closure shapes outside the supported arg/return matrix (e.g., arg-bearing `Data` returns, non-throwing `Data` returns). |
| **Result<T,E> parameter direction** | Blocked. Needs native payload synthesis for C#-created instances. |
| **Multi-protocol generic compositions** | Blocked. Needs full existential composition in `@_cdecl` wrapper. |
| **Value-type generic conformers** | Blocked. Requires non-AnyObject transport through `@_cdecl` boundary. |
| **SwiftUI beyond current level** | Wait for consumer feedback before investing further. |
| **Property wrappers / KeyPaths** | Low frequency in public API surfaces. |
| **Static protocol constructors** | Init witness dispatch needs allocation infrastructure. |
| **Weak/unowned references** | 4 test skips. Requires ownership tracking infrastructure. |

---

## Blocked (Confirmed Upstream Only)

These are the **only** confirmed upstream issues. There are exactly 4 (reproduced in standalone repro at `/Users/wojo/Dev/swift-interop-repro/`). If a crash doesn't match one of these, it's our bug. See `feedback_mono_jit_blame.md` for the full investigation checklist.

| Filing | Issue | Blocked By |
|--------|-------|-----------|
| 1 | **Mono: JIT assertion `!ji->async` on CallConvSwift P/Invoke** | Fatal `jit-info.c:918` during stack unwinding through a `wrapper_managed_to_native_*` frame after a native crash in a `CallConvSwift` callee. Workaround: `@_silgen_name` Swift wrappers / avoid native crashes through `CallConvSwift` |
| 2 | **Non-blittable type rejection with CallConvSwift** | .NET runtime design limitation. Workaround: `@_cdecl` wrappers (already covers 78.5% of P/Invokes) |
| 3 | **Mono: `Cannot transition thread from STARTING with DONE_BLOCKING` on `(Bool, @out via x0)` tuple-return CallConvSwift** | Specific to `Set<T>.insert` ABI shape. `Set.contains` (no `@out`) passes. Workaround: `@_cdecl` Swift wrapper |
| comment | **Mono: SafeHandle async lifetime** (tracking-issue comment, no standalone filing) | GC may collect SafeHandle during async suspension. Workaround: manual ARC retain/release or singleton pattern |

| Other | Status |
|-------|--------|
| **Non-Int32 enum raw values** | Blocked on Swift compiler: `.swiftinterface` strips integer raw values. No workaround. 1 skipped test. |

---

## Not Worth Addressing

| Skip Reason | Count | Why Not |
|-------------|------:|---------|
| @_spi / internal members | ~750 | Correct behavior — private API should not be bound |
| Synthesized Codable | ~730 | .NET consumers use own serialization (`System.Text.Json`, etc.) |
| AnyTypeFallback (`Any`, `[Any]`, `Optional<Any>`, ObjC delegate protocols, PAT subscripts) | ~614 | PAT classification + by-design Swift `Any` + ObjC protocols + cross-library — fully architecturally-deferred. In-scope single-module gaps measure 0 hits. |
| Unsupported signatures (associated types, bare generics) | ~611 | Swift patterns with no C# equivalent |
| Generic protocol constraints / PATs | ~453 | Architecturally blocked by associated type erasure |
| SwiftUI/Combine dependencies | ~181 | Framework boundary — consumers use SwiftUI bridge instead (`SwiftUIConstraint` + `SwiftUIView`) |
| Unsupported existential (opaque generics) | ~90 | Fundamental limitation of Swift's type system vs C# generics |
| UnsatisfiedGenericConstraint (remaining) | ~76 | Fundamental type system constraints, not relaxable gates |

---

## Long-term: retire `nuke validate`

`nuke validate` exists because we needed a quick "are these libraries still
working?" sanity sweep while the generator was changing rapidly. The long-term
goal has always been to make BindingTests the durable, sole gate. The 0.10.0
release cycle is the first concrete investment toward that — it lands the
`BindingTests/Sources/SurfaceArea/` corpus + Layer B `--skip-surface` ratchet
(scaffolding) and seeds skip-class snippets in-bundle as fixes ship.

**Retirement criterion**: validate is officially decorative when a full `nuke
validate` run surfaces no bug that BindingTests + SurfaceArea didn't already
catch *across multiple consecutive scheduled sweeps*. We're not close to that
yet — the audits behind the 0.10.0 plan ran against real third-party libraries
and found patterns BindingTests had no coverage for.

**Migration path**:

- **Each skip-class fix** lands a minimized Swift pattern in
  `BindingTests/Sources/SurfaceArea/`. Each shape-class fix lands Layer A
  coverage instead (Swift repro + C# assertion + generator unit test).
- **Future audit findings** route by class: skip-class drops a new
  `SurfaceArea/` snippet as the first step of triage; shape-class adds Layer
  A coverage to the appropriate domain test class. In both cases the
  regression test lands as part of triage, not after the fix.
- **Validate's role narrows progressively**:
  - Today: targeted per-bundle gate where the bug was found only in real
    libraries (Bundles 8 and 9), discovery sweep pre-release.
  - Next minor cycles: as SurfaceArea matures, drop targeted-validate gates
    bundle-by-bundle when the domain is provably covered by SurfaceArea.
  - Eventually: validate runs as scheduled discovery only, no merge gates.
- **Retirement happens** when validate stops surfacing surprises that
  BindingTests didn't catch across, say, three consecutive scheduled sweeps.
  Until then, validate stays in scope as a discovery sweep, not a blanket
  per-bundle blocker.

---

## Explicitly Out of Scope

| Item | Reason |
|------|--------|
| Full Swift type graph infrastructure | Over-engineered for current needs |
| Deep generic signature / associated type constraint emission | C# generics can't express Swift's full type system |
| Result builder (`@resultBuilder`) projection | Compile-time Swift feature, no ABI JSON representation |
| `@dynamicMemberLookup` / KeyPath projection | Affects <5 types across 53 validation libraries |
| Composing SwiftUI view trees from C# | Result builders are a compiler feature |
| Structs projected as C# value types | Only safe for frozen+blittable subset; marginal benefit |
