# Corpus compile-red recheck — 2026-07-22

Status: **results + decision menu; no work funded.** Owner will decide whether/when to pick up any family below.

## What was done

After the 2026-07-next-impact program landed on `main` (through `ee8a7602`, including the
proxy-availability oracle broadened across 14 emission sites), the 6 remaining compile-red
corpus packages were re-run against the rebuilt generator to see how many the committed
fixes had already flipped. Mechanics: the corpus-sweep per-library driver
(`internal-binding-testing/corpus-sweep/scripts/run_library.py NAME --skip-convert`),
cached conversions, NuGet + output caches purged per package so no stale artifacts could
poison verdicts.

**Result: 0 of 6 flipped. Corpus stands at 58/120 green.** Every package kept its verdict
*and* its exact pre-fix error histogram — the proxy-availability work does not touch any of
these failure surfaces.

## Per-package results and corrected mechanism labels

The important output is a triage correction: two packages carried a "proxy residual" label
that matched on CS error codes only. The actual mechanisms are different, and none of them
are covered by work already on `main`.

| Package | Verdict | Errors | Actual mechanism |
|---|---|---|---|
| DGCharts | compile_failed | 10× CS0103, 6× CS1061 | Malformed marshalling-body emission: undefined emitter-internal locals (`_handle`, `resultPtr`) inside marshalling bodies; `.Payload` accessed on Apple-projected `CGContext`, which is not payload-carrying. **Not** a dangling proxy reference. |
| rive-ios (RiveRuntime) | compile_failed | 16× CS0103, 4× CS1061, 4× CS1503 | Same family as DGCharts: undefined locals (`riveBox`, `s_init_rive_*_Callback`), `.Payload` on `RiveViewModel`, plus `Func<Task<Rive>>` → `Swift.AnyType` conversion failures. |
| CombineCocoa | compile_failed | 2× CS0246, 2× CS0535 | Generated `DelegateProxy` class **exists but is incomplete** — missing `IDisposable.Dispose()` — plus a cross-module `Runtime` type reference not resolving. Not dangling; a completeness gap in an emitted class. |
| Moya | compile_failed | 8× CS0246 | Sibling-surface gap: the packed Alamofire binding does not surface `IParameterEncoding`, so Moya's cross-module reference fails. Blocks CombineMoya downstream (still `named_missing_input`). |
| Macaw | compile_failed | 6× CS0029 | Operator-return family: `nint` assigned to `Macaw.Size`. As previously recorded. |
| SwiftDate | compile_failed | 4× CS0029 | Same operator-return family: `nint` → `SwiftDate.TimePeriod`. |

Incidental regression check: Moya's in-run sibling chain (Alamofire + linkage siblings)
still generated, packed, and compiled clean — no regression signal in previously-green
packages. No full sweep was run.

Artifacts: fresh `result.json` + per-product `compile.log`/`generate.log` under
`internal-binding-testing/corpus-sweep/output/{DGCharts,Moya,CombineCocoa,rive-ios,Macaw,SwiftDate}/`;
pre-rerun snapshots at `/tmp/proxyfix-rerun/*.result.old.json`.

## Path from 58 to ~68, restated honestly

The earlier ~68 projection assumed several reds would fall to the proxy-availability fix.
They don't. Reaching it now requires **new mechanism work**, roughly four families:

1. **Marshalling-body emission bugs** — DGCharts + rive-ios (+2 greens). Undefined
   emitter locals and `.Payload` on non-payload Apple projections. Likely the largest
   single family; root cause unknown until investigated.
2. **Operator-return CS0029** — Macaw + SwiftDate (+2 greens). Most contained-looking
   family; a plausible first pick.
3. **Sibling surface gap** — Moya (+1 green, +1 cascade via CombineMoya). Why the
   Alamofire binding drops `IParameterEncoding` from its public surface.
4. **Incomplete generated `DelegateProxy`** — CombineCocoa (+1 green). Interface
   contract not fully emitted on a generated class.

All six flipping ≈ 65/120; the previously identified sibling mechanisms
(OrderedCollections, Crypto, SwiftDrawDOM) are the remainder of the ~68 picture.

## Non-decisions this doc does not make

- None of the four families is funded. Each needs its own root-cause pass; sizes unknown.
- Ordering vs. the 0.18.0 cut is an owner call — this is all additive generator work, so
  it can land before or after the release.
- The 10 SWIFTBIND111 generate-fails and the graph-closure/named-input reds are out of
  scope here; unchanged by design, each with a structured dossier.
