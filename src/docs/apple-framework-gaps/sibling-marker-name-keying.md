# Sibling emission-marker name keying — latent cross-module collision audit

> Status: **latent hazard, not a reproducing bug.** Documented during the §5 witness-getter
> review. No fix applied yet — a fix must ship with a reproducing BindingTests fixture
> (TDD-for-regression-fixes). This doc is the categorical audit so a future focused pass
> can harden the whole family in one go instead of patching site by site.

## Background

`ModuleEmissionContext` carries a family of "emitted" markers that `EveryProtocolEmitter`
sets while emitting the Swift wrapper and `ProtocolProxyEmitter` reads while emitting the C#
proxy. They gate whether the proxy emits a given P/Invoke / base class / vtable call.

One member of this family — the **witness-table getter** marker
(`MarkWitnessTableGetterEmitted` / `WasWitnessTableGetterEmitted`) — was changed to key on
`SwiftTypeName.ModuleQualifiedName` (the codebase's canonical unique type identity) instead of
the simple `.Name`. That change is complete and reviewed:

- Mark: `EveryProtocolEmitter.cs` (inside the local-only `sourceModule` guard).
- Read: `ProtocolProxyEmitter.cs`.
- Store: `ModuleEmissionContext` (`protocolKey` parameter, qualified-name docs).

The witness-getter marker was the one that produced a real, observed crash
(`EntryPointNotFoundException` for read-only / cross-module CALLBACK; see
`bug-0.10.0-proxy-vtable-setters-not-exported.md`), and its same-simple-name reachability is
live in the nested-type space the §5c work exercises — so it was an in-scope fix.

## The sibling markers (still simple-name keyed)

| Family | Mark | Read | Mark guarded local-only? | Wrong-decision failure class |
|---|---|---|---|---|
| **SetVtable** | `MarkSetVtableEmitted(.Name)` | `WasSetVtableEmitted(.Name)` (1 site) | No | dangling `Set{Name}_vtable` P/Invoke (gated symbol is simple-named / unprefixed) |
| **ObjCBase** | `MarkObjCBase(.Name)` | `UsesObjCBase(.Name)` (1 site) | No | wrong carrier class (gated symbols are hardcoded `SBW_*EveryObjCProtocol*`, already module-unique → not dangling) |
| **EntityBase** | `MarkEntityBase(.Name)` (pre-scan local-only + a second un-guarded site) | `UsesEntityBase(.Name)` (1 site) | Mixed | wrong carrier class + possible over-emit of the `EveryEntityProtocol` Swift class |
| **Conformance** | `RecordConformanceDecision(.Name, …)` | `WasConformanceEmitted(.Name)` at **3** sites: `ProtocolProxyEmitter.StaticInit` (cross-decl `ancestorDecl.Name`), `WitnessDispatchEmitter`, `ProtocolHandler` | No | proxy emit/suppress mis-gate → terminates in the same dangling simple-named Swift symbols |

## Why it is reachable in principle

A single generator run emits **one** bound module (`Program.cs` constructs one
`ModuleEmissionContext`), but within that run the same context processes both the local
module's protocols **and** the cross-module **parent** protocol decls pulled in from
dependencies (`ModuleHandler` cross-module-parent loop). The sibling Mark sites are **not**
behind the local-only `sourceModule` guard that protects the witness-getter Mark, so a
cross-module parent `Dep.Foo` and a local `Foo` collide on the simple key `"Foo"` in the
shared HashSet / dictionary.

## Why it is NOT a reproducing bug today

- It needs a **naming coincidence across the module boundary**: a local protocol and a
  dependency protocol with the **same simple name**, where exactly one drives the marker and
  their emission decisions **differ**. No validation library or BindingTests fixture in the
  current set is known to exercise that — established by reading the marker call sites, not by
  an exhaustive cross-module protocol simple-name sweep across every declared library and its
  transitive dependencies.
- The cross-module-parent **vtable wiring** does not depend on these markers: it runs through
  `EmitCrossModuleParentVtableInit` with a **module-prefixed** entry point
  (`GetCrossModuleSetVtableEntryPoint`), so inherited dispatch is correct regardless of a
  simple-name marker collision.
- `ObjCBase`/`EntityBase` gate **hardcoded, protocol-independent** helper symbols
  (`SBW_*EveryObjCProtocol*` / `SBW_*EveryEntityProtocol*`), so a collision mis-selects the
  carrier class rather than pointing at a non-existent per-protocol symbol.

## Safe-hardening plan (for a future focused pass)

1. **Write the RED fixture first.** Add a dependency-module protocol whose simple name
   collides with a local protocol (BindingTests already has a dependency module with
   cross-module parent delegates), arranged so the two have **differing** setter / conformance
   emission. Confirm it reproduces a dangling P/Invoke or wrong carrier before changing code.
2. **SetVtable, ObjCBase, EntityBase** are the low-risk re-keys: each is read at a single site
   with the **same decl** that was marked, so swapping both Mark and read to
   `SwiftTypeName.ModuleQualifiedName` mirrors the proven witness-getter change. The
   `EntityBase` pre-scan Mark must be re-keyed in lockstep with its second Mark site.
3. **Conformance is the delicate one.** `WasConformanceEmitted` is read at **three** sites,
   including a **cross-decl** ancestor lookup (`ancestorDecl.Name`). Re-keying requires
   verifying that every reader resolves to the **same** qualified name the recorder used for
   that ancestor, and that the dictionary's last-write-wins behaviour is preserved for the
   intended key. Do this only with the RED fixture in place — a naive swap here can break
   cross-module-parent proxy emission / suppression and reintroduce the MusicKit-class crash
   the witness-getter work fixed.
4. Re-run unit + `binding-tests --compile-only` + `binding-tests --skip-regen`, plus
   `--device` (NativeAOT) since this touches vtable / conformance P/Invoke gating.

## References

- `05-residual-gaps.md` §5 — the witness-getter fix this audit is adjacent to.
- `bug-0.10.0-proxy-vtable-setters-not-exported.md` — the original SetVtable / witness-getter
  "assume every protocol has a setter" crash.
