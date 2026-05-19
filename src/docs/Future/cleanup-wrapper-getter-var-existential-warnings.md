# Cleanup: `var existential` → `let existential` in protocol property getters

> Cosmetic generator cleanup. Discovered 2026-05-18 while reviewing
> CI logs for the 0.11.0 release branch. Deferred from 0.11.0; safe to
> pick up after the release ships.

## Summary

`nuke binding-tests` CI logs are spammed with Swift compiler warnings of
the form:

```
warning: variable 'existential' was never mutated; consider changing to 'let' constant
  var existential = containerPtr.load(as: (any <Module>.<Protocol>).self)
```

…repeated once per protocol property getter in the generated wrapper.
There are dozens of these in a single run.

These are **warnings, not errors**. `swiftc` exits 0 and the build
succeeds. The reason they look alarming is that NUKE's process-output
capture labels every line that swiftc writes to stderr — warnings
included — as `[ERR]` in the log. The text starts with `warning:`, the
exit code is 0, and the gate passes.

## Where the emitter is wrong

`src/Swift.Bindings/src/Emitter/StringEmitter/WitnessDispatchEmitter.cs`
emits `var existential = containerPtr.load(...)` in property *getter*
templates that only read from it:

| Line | Emitter | Path |
|---|---|---|
| 1217 | `EmitHeapAllocatedPropertyGetter` | Blittable + collection getters |
| 1248 | `EmitPropertyGetterAccessor` (string branch) | String getters |
| 1989 | (third getter template) | — |
| 2017 | (fourth getter template) | — |

Property *setters* (`:1311`, `:1338`) genuinely mutate
(`existential.{Property} = ...`) and need to stay `var`.

Method-dispatch sites (`:1375`, `:1507`, `:1697`, `:1837`, `:1924`)
deliberately use `var` with the comment
*"use var for methods that may be mutating in the future"*. They don't
currently mutate, so they'd warn too if exercised — but the visible
warnings in CI today are all from the four getter sites above.

## Fix

Change `var existential` → `let existential` at the four getter
templates. Optionally tighten the method-dispatch sites the same way
and drop the forward-compat comment — Swift's compiler will tell us
the day we genuinely need `var` (it's a hard error to call a `mutating`
member on a `let`).

## Why deferred from 0.11.0

It's a cosmetic log-noise issue, not a correctness or runtime bug. The
release branch is stable and the change touches the wrapper emitter,
so it wants a `nuke binding-tests --sim --device` run to prove the
generated wrappers still compile and the getters still work. Cheap to
do, but not worth re-cutting the release for.

## Validation when picked up

- `nuke test` — unit tests
- `nuke binding-tests --sim` — runtime gate
- Re-read a CI log after the change and confirm the
  `'existential' was never mutated` lines are gone.
