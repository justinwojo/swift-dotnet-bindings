STATUS: COMPLETE
Commit: 9e7f6dec (main)

Usability-probe deliverables (U-001–U-009). Shipped, all additive:
- `string`→`Foundation.URL` convenience overloads (new method post-processor).
- Placeholder-default recovery: a ctor/method dropped only for a trailing defaulted `AnyType` now emits a truncated overload — recovers Stripe `ApplePayConfiguration`'s public init.
- CoreGraphics `Swift.CG*`↔`CoreGraphics.CG*` implicit conversions now runtime-test-covered (operators already existed).
- Enum-case native-int `Int`/`UInt` convenience forwarder.
- Generated `{Module}.api-surface.md` from the emitted member set; records recovered overloads; self-deletes on zero members.

Gates: `nuke test` 14768/0; `binding-tests --compile-only` green; sim 3238 pass (+46 vs 3192), 0 crash, 6 fails = known-env LiveActivity foreground precondition (memory-documented, unrelated to this work).

Surface changes for owner review: NONE — every shipped change is additive (overloads/tests/doc). Deferred surface-CHANGE candidates (owner decisions, in `not-planned.md`): U-001 `Swift.CG*`→`CoreGraphics.CG*` unification (source-breaking + ABI re-verify), U-003 property-rename knob (nested rename verified already-correct, no defect), U-008 null-literal overload ambiguity. Also deferred there: U-002 external-type extensions, U-005/U-006 existential-result projection, U-009 property/subscript + README-wiring residuals.
