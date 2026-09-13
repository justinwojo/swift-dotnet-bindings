# Roadmap

Work we currently intend to pursue. Detailed implementation plans own their execution order and acceptance criteria; this page links them rather than duplicating them. Reference notes do not become work merely because they exist.

## Current work

- **SDK/Runtime/Templates 0.20.0 + Apple 26.2.9:** the [active remaining-work plan](sessions/0.20.0/README.md) owns the release sequence, conditional probes and final qualification. The accepted P0 fixes are implemented and validated: optional-error subscripts now use an owning nullable error-box pointer transport, and non-cdecl class operators share the ordinary marshalling-alias preamble. Class-parent operator execution remains activation-gated on the existing CallConvCdecl/IntPtr transport prerequisite. `sessions/` is local-only and is not present in a fresh clone; consult the current owner-maintained plan when executing this release.
- **Release decisions:** the active plan owns compatibility decisions and the RuntimeContract floor check. Do not infer permission to change them from a deferred note.

## Direction

Prioritize new real consumer inputs and evidence-backed repairs. Broad internal rescans and structural refactors have diminishing returns; [the strategic posture and engineering policies](Design/engineering-policies.md) explain the constraints.

- **Closure capability:** expand remaining generic, nested and async shapes incrementally when a consumer or validation input requires them. [Known shapes and evidence](Future/notes/closures-async.md).
- **Multi-module dependency closure:** improve converter import closure and generator cross-module facts around concrete consumer ecosystems. [Ingestion notes](Future/notes/ingestion.md) and [deferred recovery proposals](Future/notes/recovery.md).
- **Validation evolution:** grow durable BindingTests coverage from real-library discoveries, and narrow the external sweep only when its retirement criterion is demonstrated. [Criterion and migration direction](Future/validation-evolution.md).

## Reference

- [Engineering policies](Design/engineering-policies.md): prediction-gate freeze, surface-loss protection and the generator/converter contracts.
- [Scope boundaries](Design/scope-boundaries.md): by-design limits and explicitly excluded work.
- [Decisions](Design/decisions.md): settled choices and investigated alternatives.
- [Confirmed upstream blockers](Future/upstream-blockers.md): known upstream cases and workarounds. Every other runtime crash is ours until proven otherwise; a matching symptom alone is not upstream attribution.
- [Subsystem reference notes](Future/notes/README.md): deferred evidence to search when relevant, not a source of automatic next tasks.

Live baseline counts belong in `build/baselines/validation-baseline.json`; per-library status lives with each package. When work is selected, link its plan here. Remove completed work after preserving any durable rationale in Design and behavior in tests.
