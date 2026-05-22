# EveryProtocol wrapper-emission bugs — fixed

> Three latent swiftc failures in the generated `EveryProtocol` Wrapper.swift
> that were masked on `main` by the build script's strip-retry loop. They
> surfaced as hard wrapper-compile failures whenever new EveryProtocol-conformed
> protocols were added (e.g. when widening BindingTests for inherited-delegate
> dispatch — 3-level chains, non-empty children, cross-module children).

## The three bugs and resolution

| # | Symptom | Root cause | Resolution |
|---|---|---|---|
| 1 | `EveryProtocol does not conform to MutableNamed` | Property dedup in `EveryProtocolEmitter` keyed only on bare property name; first-seen protocol owned the body, later protocol got empty extension. If late protocol needed setter, swiftc rejected ("missing set witness"). | Property-emission-ownership map: protocol with fattest accessor set wins (`get set` > `get`); siblings emit empty extensions and conform via Swift's cross-extension witness resolution. |
| 2 | `EveryProtocol does not conform to MutablePrioritized` | Same shape as #1. | Same fix as #1. |
| 3 | `invalid redeclaration of 'label()'` | Swift forbids `var label` and `func label()` on the same class. Two protocols contributing both shapes to `EveryProtocol`'s namespace collide structurally. | Drop the method-side protocol (preserving the more common property shape). Structural Swift limitation — no emission trickery can preserve both on the same nominal type. |

## Where the fix lives

- `src/Swift.Bindings/src/Emitter/StringEmitter/EveryProtocolEmitter.cs`
  - `ComputePropertyEmissionOwners` — builds the ownership map keyed by `"{name}|{typeKey}"`.
  - `EmitProtocolExtension` — property loop checks ownership before global dedup.
  - Owner emits `var foo { get { ... } set { ... } }` against its own vtable; siblings emit empty extensions.
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ModuleHandler.cs`
  - Cross-module parent collection hoisted ABOVE the property-type-count
    conflict gate. The gate now scans the union of local + cross-module
    parents and drops conflicting LOCAL protocols first; if a residual
    parent-vs-parent conflict remains, the affected parents are also
    dropped. Without this, a `var id: String` on a local protocol and a
    `var id: Int` on an unrelated cross-module parent would each get
    distinct ownership entries (the map keys on `name|typeKey`), both
    emit bodies, and swiftc would reject with "invalid redeclaration of
    'id'" on EveryProtocol.
  - When a residual parent IS dropped, any local that transitively
    inherits it is cascade-dropped from `suitableProtocols` via
    `TransitivelyInheritsCrossModuleParent`. Without the cascade, the
    local's `extension EveryProtocol: L` would emit but the parent body
    its witness dispatches into would not, leaving the strip-salvage
    path to clean up the wrapper while the C# proxy/P/Invoke surface
    stays out of sync with the stripped Swift.
  - `ComputeNonThrowingOverrides` also widened to the union.
  - Member-kind collision gate (bug 3) also scans the union: a property
    name contributed by EITHER a local protocol OR a cross-module
    parent preempts a zero-arg method of the same name on either side.
    The colliding method-side protocol is dropped from whichever list
    (`suitableProtocols` or `crossModuleParents`) it lives in. Without
    this, a dep parent's `var label: T` + a local's `func label() -> T`
    (or the inverse) would generate the same `invalid redeclaration of
    'label()'` failure that the in-module-only gate fixed.
  - Old "drop set-required protocols" workaround removed.

## Why bug 3 keeps the drop

Swift's redeclaration check fires at the nominal-type namespace level, not per-extension. `var label` and `func label()` cannot coexist on `EveryProtocol`, regardless of which extension declares them. The C# side already disambiguates (property vs. method), but the Swift wrapper cannot. Preserving both would require a second EveryProtocol-like box type per "colliding name partition" — a structural overhaul touching `ExistentialContainer`, `ProtocolProxyEmitter.StaticInit`, the proxy registry, and existential-container construction. Not justified for this case.

## Runtime semantics: documented limitation

When a C# class implements only the smaller protocol of a sibling group (e.g.
only `INameable`, not `IMutableNamed`), the smaller protocol's extension on
`EveryProtocol` is empty — Swift's cross-extension witness resolution points
its witness at the OWNER protocol's body, which calls the owner's vtable.
Concretely: a thin `INameable` proxy populates `_nameable_vtable` only; when
ANY Swift dispatch reaches the `var name` declaration — whether through the
fat protocol's witness table OR through the smaller protocol's witness table
(which resolves to the same body) — the body calls `_mutableNamed_vtable`,
which is uninitialised → force-unwrap crash. The empty extension makes the
two dispatch paths converge on the owner's implementation, so any unmodelled
proxy combination crashes the same way.

No existing BindingTests exercise this path; the proxies populate per-class
vtables in their static cctors, and no test creates a thin proxy and then
uses the box (under any conformance) to read the shared property.

If/when a real consumer hits this, the fix is a localised getter fan-out in
`EmitPropertyImplementation`: the owner's body checks each sibling's vtable
(`!= nil`) and routes to whichever one is initialised. That requires
`ComputePropertyEmissionOwners` to expose siblings — today it only returns
the owner; the call sites would need either a richer return type (e.g.
`IReadOnlyDictionary<string, (ProtocolDecl Owner, IReadOnlyList<ProtocolDecl> Siblings)>`)
or a second map. Not added speculatively to honour the project's
bug-first-test rule.

## Related references

- `src/docs/Future/cleanup-wrapper-getter-var-existential-warnings.md` — treats the unfixed pre-state as "unrelated swiftc errors that pre-exist in the baseline wrapper".
- `src/docs/inherited-delegate-dispatch-remaining-work.md` — inherited-delegate categorical gates that this fix unblocks.
