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
`x20` for `SwiftSelf` and `x21` for `SwiftError`. Under Mono's full-AOT managed-to-native wrapper
those registers are also candidates for the GC-safe-region transition cookie, and the wrapper's own
argument setup can destroy the cookie before the branch. The `@_cdecl` route removes the collision
by removing the reserved register from the signature — `self` arrives as an ordinary pointer
argument. The register-level mechanism, its evidence and its reopen trigger are recorded in
`not-planned.md` under *Mono clobbers the GC-safe-region cookie in `x20`*; what belongs here is the
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

Adding a guard here is subject to the prediction-gate freeze policy in `roadmap.md`: a new gate is
justified only when the failure it prevents would otherwise *compile*.

## Wrapper symbols

| Member kind | Symbol |
|---|---|
| Method | `SBW_{module}_{type}_{method}_{hash8}` |
| Constructor | `SBW_{module}_{type}_init_{hash8}` |
| Subscript accessor | `SBW_{SubGet\|SubSet}_{module}_{type}_{hash8}` |
| Property accessor | `SBW_{Get\|Set}_{module}_{type}_{property}` — **no hash** |
| `@_silgen_name` (Swift-convention) shims | `SBSW_…` |

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
