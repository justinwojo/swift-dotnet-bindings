# Cleanup: `var existential` → `let existential` in protocol property getters

> NOT cosmetic. The naive substitution was attempted 2026-05-19 against
> 0.11.1 prep and triggered a -8 runtime regression. Re-scoped to
> "needs investigation" — see *Failure mode* below.

## Summary

`nuke binding-tests` CI logs are spammed with Swift compiler warnings of
the form:

```
warning: variable 'existential' was never mutated; consider changing to 'let' constant
  var existential = containerPtr.load(as: (any <Module>.<Protocol>).self)
```

…repeated once per protocol property getter in the generated wrapper.
~1,900 of them in a single `nuke binding-tests` run against
`BindingTests`. They appear as `[ERR]` in NUKE's log because the
process-output capture labels everything swiftc writes to stderr —
warnings included — as `[ERR]`. swiftc itself exits 0 and the gate
passes.

## Where the emitter writes `var existential`

`src/Swift.Bindings/src/Emitter/StringEmitter/WitnessDispatchEmitter.cs`:

| Line | Emitter | Path | Mutates? |
|---|---|---|---|
| 1217 | `EmitHeapAllocatedPropertyGetter` | Blittable + collection getters | No |
| 1248 | `EmitPropertyGetterAccessor` (string branch) | String getters | No |
| 1989 | Third getter template | Class/AnyObject return | No |
| 2017 | Fourth getter template | Struct-return getter | No |
| 1311, 1338 | Property *setters* | String + blittable setters | **Yes** — stay `var` |
| 1375, 1507, 1697, 1837, 1924 | Method-dispatch sites | Currently non-mutating | No, but flagged forward-compat |

The four getter sites read once into `let result = existential.{Property}`
and never mutate. By inspection they look like safe `let` substitutions.

## Failure mode (the trap)

Empirically, swapping `var` → `let` at the four getter templates
**regresses BindingTests by -8 passes** (`2113 → 2105`, ClosureTests
crashes with `EntryPointNotFoundException: SBW_ProcessingMode_get_modeName_0`).

Walk-through:

1. Substitution lands in the four templates. Generator regenerates the
   wrapper. The `let existential = containerPtr.load(as: (any P).self)`
   form is **rejected by swiftc for some subset of protocols** —
   suspected: protocols with `Self`-conformance, sendable constraints,
   class-binding quirks, or existentials whose layout requires a `var`
   binding to satisfy load semantics. Root cause not yet identified.
2. The build pipeline's strip-retry loop
   (`Build.BindingTests.cs:589-650`) catches the per-function failures
   and **silently strips the offending `@_cdecl` functions** to let the
   wrapper-as-a-whole compile. Strip count jumps from **91 → 177** (86
   additional accessors stripped).
3. Some stripped functions back real runtime tests — notably
   `SBW_ProcessingMode_get_modeName_0`, exercised by `ClosureTests`.
   At runtime the C# P/Invoke can't find the entry point and crashes
   the test class, blind-skipping the next class too.

The 5 unrelated swiftc errors that pre-exist in the baseline wrapper
(`EveryProtocol` non-conformance to `MutableNamed`/`MutablePrioritized`,
duplicate `label()`, missing `AuthenticationServices` import,
`shipped` enum-case arity) are **not** what's failing here — those
already get handled by the strip-retry loop in the baseline. The new
strips are net-new failures caused by the substitution.

## Why it looks innocent

- `[ERR]`-tagged warnings make the noise look like errors; switching to
  `let` is the literal fix swiftc suggests.
- All four sites read `existential` exactly once and never mutate.
- Code review can't see the failure — it only shows up when the
  wrapper actually compiles and gets exercised at runtime.
- `nuke binding-tests --compile-only` **does not catch it** (the strip
  loop hides the new failures and the wrapper still produces an
  artifact). You only see the regression on a full `--sim` run.

## What's actually needed

Before re-attempting:

1. **Isolate the failing protocol(s).** Re-apply the `var` → `let` swap,
   run `nuke binding-tests --sim`, and grep
   `BindingTests/output/SwiftBindingsTestLibSwiftBindings.xcframework/ios-arm64-simulator/SwiftBindingsTestLibSwiftBindings.framework/SwiftBindingsTestLibSwiftBindings.swiftc-stderr.txt`
   for the *new* errors (delta vs baseline's 5 known errors).
   Look for `let existential` lines that swiftc complains about.
2. **Identify the structural property** (Sendable? class-bound?
   self-conforming? AnyObject-rooted?) that makes `let` reject and
   `var` accept.
3. **Narrow the substitution** — either gate the `let` form on the
   protocol's structural shape in the emitter, or pick a different
   suppression (`_ = existential` after the read, or a `withExtendedLifetime`-style
   borrow) that doesn't trigger the same swiftc rejection.

## Scope reminder

Even a clean fix at the four getter templates only quiets the
**WitnessDispatchEmitter** subset of the 1,900 warnings. The
method-dispatch sites (`:1375, :1507, :1697, :1837, :1924`) emit the
same pattern and contribute. A full sweep would re-validate those too,
with the same per-protocol structural risk.

## Validation when picked up

- `nuke test` — unit tests.
- `nuke binding-tests --sim` (with a clean
  `rm -rf BindingTests/RuntimeTestsApp/{bin,obj}` first — stale
  artifacts mask as a -8 ClosureTests regression of their own; see
  memory `feedback_clean_bin_before_stage2.md`).
- Diff the swiftc-stderr file vs baseline. Strip count must stay at
  baseline (91 for current BindingTests) — any increase means new
  accessors got dropped.
- `nuke binding-tests --device` if structural Swift code paths change.
- Confirm the `'existential' was never mutated` lines actually drop.

## Why deferred again

Originally deferred from 0.11.0 as cosmetic. Re-deferred from 0.11.1
after the empirical regression. Not blocking — pure log-noise issue —
but the real fix requires generator-level discrimination between
existential shapes, which is more work than the doc previously
implied.
