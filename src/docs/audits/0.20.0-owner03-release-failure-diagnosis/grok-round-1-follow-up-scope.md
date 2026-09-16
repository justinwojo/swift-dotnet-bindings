# OWNER-03 diagnosis ledger — accepted-fix follow-up

Resume Grok session `01a09e9f-5763-7370-9cf3-6c23312fc638`. Review only the changes
made in response to its four accepted findings and the directly adjacent precision
edits in `src/docs/audits/0.20.0-owner03-release-failure-diagnosis.md`.

## Accepted findings and dispositions

1. **High D07/B09 `.storekit` path — fixed.** The ledger now states that a local
   `.storekit` file cannot be activated through `simctl`, `mlaunch`, `dotnet`, or the
   current Nuke launcher. The shipping command-line gate requires a provisioned
   Sandbox backend, same-backend native control, fail-closed readiness, and bounded
   cancellation. `.storekit` is limited to a separate genuinely Xcode-driven control.
2. **Medium D12/B04 systematic drift — fixed.** The ledger now lists every shared lane
   and its scalar/identity pass-skip values, excludes scalar-only x64 lanes, requires
   reconciliation of every shared lane, and keeps Mono-AOT `4061/32/0` as the named
   observed mismatch rather than the whole repair.
3. **Medium D02 existing pre-fix assertion — fixed.** The ledger explicitly names
   `MethodLevelGenericOpeningTests.cs`, requires inversion of its no-pre-catch and
   one-write assertions, calls for a sibling occurrence-count audit, and identifies
   the runtime test type as `BasicThrowingTests`.
4. **Medium B05 Nuke cell dependency — fixed.** B05 now requires whole-cell green only
   for Mappedin and identity-filtered confirmation for Nuke native-width behavior. It
   explicitly leaves whole Nuke cells to B12 after B03.

Adjacent Low precision edits: the verdict distinguishes 18 red library cells from the
harness-only signature; D11 names the same defective pattern in `run-all-device.sh`;
D04 accurately separates `ShowMapOptions` seed coverage from `FocusOptions`
mutation/writeback. The D03 render-effect analogue remains required because the user
explicitly requires a first-party regression home for every signature; its four-arm
Lottie differential remains the layer discriminator.

## Review limits

Confirm the accepted High defect and affected category are repaired without new
contradictions or weakened gates. Do not restart a full audit of unchanged rows. Apply
the same repository guidance and read-only guardrails from the original scope. Do not
edit, mutate, launch nested reviewers, implement product fixes, or broaden scope.
Return any remaining actionable High/Critical regression in these fixes; otherwise
state that the accepted-fix follow-up is clean, while noting any directly caused
Medium/Low issue if necessary.
