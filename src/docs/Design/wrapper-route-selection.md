# Wrapper route selection

How the emitter decides, per member, whether a binding calls Swift directly or through a generated
shim — and what the wrapper symbol namespace guarantees once it has decided.

## Three routes, and which one is the fallback

For each member the emitter tries, in order:

1. a **native ARM64 thunk**, when the signature is simple enough to hand-assemble;
2. a **Swift `@_cdecl` wrapper** — a generated free function in the module's wrapper source that
   takes `self` as a plain pointer, calls the real member, and returns through the C ABI;
3. a **direct `CallConvSwift` P/Invoke** against the member's own mangled Swift symbol.

The third is what remains when the first two cannot be built. `WrapperDecision` has two values —
`WrapperRequired` and `CannotWrap` — and the per-kind `DetermineMethodWrapperDecision` /
`…Constructor…` / `…Property…` predicates in `WrapperValidation` answer the second only when an
eligibility guard rejects. **Wrapper-by-default is the policy, not an optimisation**, and widening
the direct lane is never the fix for a direct-lane defect: the two routes differ in more than
performance.

`WrapperValidation.GetCallingConvention` is the single place the convention is chosen: `Cdecl` iff
the member ended up on a `@_cdecl` wrapper or a native thunk, `Swift` for everything else —
**including `@_silgen_name` wrappers**, which only rename a symbol and leave the Swift convention in
place. That matters because the library a member is called *in* and the convention it is called
*with* are decided independently: `PInvokeEmitter.NeedsWrapperLib` can point an async or
opaque-return member at the generated wrapper dylib while the convention stays `CallConvSwift`,
producing a direct Swift-convention call *into the wrapper library* under an `SBSW_` entry point.
Moving a member into the wrapper library is therefore not the same as rerouting it.

## Why the direct route carries a cost

A direct `CallConvSwift` P/Invoke for an instance member reserves registers the C ABI does not:
`x20` for `SwiftSelf` and `x21` for `SwiftError`. Under Mono's managed-to-native wrapper (JIT and
full AOT) those registers are also candidates for values the wrapper keeps live across the call —
the GC-safe-region transition cookie, the LMF address — and the wrapper's own argument setup can
destroy them before the branch. A lost cookie aborts the transition; a lost LMF address makes the
post-call LMF pop write through self, silently corrupting the receiver. The `@_cdecl` route removes the collision
by removing the reserved register from the signature — `self` arrives as an ordinary pointer
argument. The register-level mechanism, its evidence and its reopen trigger are recorded in
[the runtime/ABI reference note](../Future/notes/runtime-abi.md#mono-clobbers-the-gc-safe-region-cookie-in-x20-so-any-swiftself-carrying-direct-callconvswift-p-invoke-can-abo); what belongs here is the
consequence for route selection: a member that carries an untyped `SwiftSelf` on the direct route is
exposed to a defect the wrapper route does not have, so the eligibility guards are the surface worth
narrowing, and the untyped-`SwiftSelf` population is a meaningful thing to measure.

`PInvokeEmitter.HandleSwiftSelf` is where that self parameter is spelled. The wrapper, native-thunk
and free-function routes take a plain `IntPtr`; direct-route frozen-struct getters take a typed
`SwiftSelf<T>` (a frozen self travels in ordinary argument registers and is outside the defect);
everything else on the direct route — classes, non-frozen structs, enums, and frozen-struct setters,
which need pointer semantics for the mutation — takes the untyped form.

## The eligibility guards

Each member kind has one single-traversal evaluator returning the **first** guard that rejected the
member, plus a boolean shim over it so the predicate and the diagnostic cannot drift:
`MethodWrapperEmitter`, `ConstructorWrapperEmitter`, `PropertyWrapperEmitter` and
`SubscriptWrapperEmitter` each expose `EvaluateWrapperEligibility`, and
`WrapperValidation.GetMemberRejectionReason` holds the guards shared across all of them (xcframework
mode, internal or SPI parents, async, actor isolation, inherited generic context). Every rejection
carries a stable reason string; those strings are what the generated skip markers and the
`binding-report.json` rows are keyed on, and they are the ledger a reroute effort works from.

Adding a guard here is subject to the [prediction-gate freeze policy](engineering-policies.md#prediction-gate-freeze-policy-hard-policy-boundary): a new gate is
justified only when the failure it prevents would otherwise *compile*.

### C callbacks

A `@convention(c)` parameter is one word with no context, so the wrapper has nothing to adapt: the
`@_cdecl` function declares the parameter under its own `@convention(c)` type and hands the pointer
to the member unchanged (an `Optional` one stays an `Optional` pointer). Methods take this through the
closure wrapper (`ClosureEmitter.NeedsClosureCdeclWrapper`), alongside any Swift closures the same
signature adapts; constructors take it through the constructor wrapper. The managed side is the one
the direct route already used: a per-parameter `[ThreadStatic]` delegate slot, an
`[UnmanagedCallersOnly]` C-convention thunk whose address is passed, and a save/restore of the slot
around the call so a re-entering callback finds its own delegate. That slot binds the callback for
the duration of the call and on the calling thread only. A C function pointer the Swift side stores
and calls after the member returns reaches whatever the slot holds then, and one it calls on another
thread finds that thread's slot empty, which ends the process through the thunk's fail-fast.

The pass-through needs every C function pointer in the signature to be a parameter whose spec
records the convention, because ABI JSON prints a C function pointer like a Swift closure and the
record comes from the demangled signature. A C function pointer anywhere else — inside a Swift
closure's signature, in a result — keeps the member off the wrapper. A constructor's `#file`-style
debug parameters stay out of the wrapper's signature and call, so Swift supplies their defaults there.
A member of a generic type stays off the wrapper too: no wrapper arm spells the parent's type
arguments for a C callback, and the managed thunk is an `[UnmanagedCallersOnly]` method, which C#
does not allow in the generic class the member is bound in. A constructor with a C callback and no
wrapper to take it (a direct-mode generation, a C function pointer with no recorded convention, a
generic parent) is skipped, because the direct route cannot deliver an allocating initializer's
metatype or decode a failable result.

The SDK's bridge qualification corpus keeps three such members on their Swift-convention imports on
purpose: the iOS BindingTests regeneration names them with `--swift-bridge-route` (qualification
generations only), which keeps each off the closure wrapper so its native bridge carries it, and the
generation fails when one is not bridged. It is a control for that corpus, not a route a consumer's
generation can select.

## Wrapper symbols

| Member kind | Symbol |
|---|---|
| Method | `SBW_{module}_{type}_{method}_{hash8}` |
| Constructor | `SBW_{module}_{type}_init_{hash8}` |
| Subscript accessor | `SBW_{SubGet\|SubSet}_{module}_{type}_{hash8}` |
| Property accessor | `SBW_{Get\|Set}_{module}_{type}_{property}` — **no hash** |
| Reverse-conformance vtable setter | `SBW_Set{sourceModule_}{protocol}_vtable` |
| Reverse-conformance witness table / existential size | `SBW_Get_EveryProtocol_{protocol}_{WitnessTable\|ExistentialSize}` |
| `AsyncStream` property bridge | `SBW_{type}_{property}_AsyncStream` |
| `@_silgen_name` (Swift-convention) shims | `SBSW_…` |

Every `@_cdecl` symbol a binding P/Invokes carries one of these two prefixes, because the
post-emission integrity gate below recognises a wrapper symbol by its prefix alone: an entry point
spelled without it is invisible to the gate, so a withdrawn definition would ship as an
`EntryPointNotFoundException` instead of failing generation.

`hash8` is an FNV-1a 32-bit digest of the member's *original mangled* name, so it distinguishes
overloads and per-specialization emissions. The property scheme has no such digest, and it flattens
a nested type's dots to underscores, so it is **not injective**: a nested `Outer.Inner` and a
top-level `Outer_Inner` with the same property name project onto one symbol.

`ModuleEmissionContext` holds the registry, and registration is **first-registration-wins**. The
loser of a collision is refused the claim and returns without emitting its wrapper body. What makes
that worth a test rather than a comment is the failure shape: the symbol *is* registered — by the
winner — so the loser's P/Invoke is still emitted, still links, and calls the winner's wrapper.
Nothing about it fails to compile.

Two gates sit on this contract, and neither covers that case:

- **In-band (`WrapperSymbolContractGate`).** Asks whether a wrapper-targeting P/Invoke names a
  symbol wrapper-emit never *registered*. On a hit the member is skipped in-emission — predicted and
  skipped before the body is written for constructors, or emitted and rolled back to a writer
  checkpoint for methods and bridges, which register their symbol mid-emission. Either way the
  member gets an `// Unsupported: …` marker and a report row rather than an orphan call site.
- **Post-emission (`WrapperSymbolIntegrityGate`, `SWIFTBIND108`).** Reconciles every `EntryPoint` in
  the generated C# against every `@_cdecl`/`@_silgen_name` definition in the generated Swift and
  fails the generation when a reference has no definition.

Both answer "does this symbol exist". Neither answers "does it belong to this member" — that is what
the unit-layer reconciliation over the generated corpus is for, pinning a fixture member to the
symbol the emitter projects for it and asserting the definition and the call site both exist.
