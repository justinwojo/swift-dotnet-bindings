# Regression matrix wall-clock — speed-up plan

> **Status (parked).** **Round 1 is done** — cell parallelization landed
> 2026-06-01 at ~3.8× speedup (19:59 vs ~75 min baseline), the structural win.
> **Round 2 is lower priority and not scheduled**: every remaining item is
> diminishing returns against a 20-min baseline for a gate that runs only a
> handful of times per release, and several add shared-state hazards (stale
> caches, cross-step crash misattribution) to a *regression* gate where a
> false-green costs more than the minutes saved. Reopen only if a future matrix
> expansion pushes wall-clock back past ~40 min and it starts hurting — and even
> then, measure the tail first (the doc already says so). The Round 1 follow-ups
> (F1–F4) are kept as foot-gun documentation for anyone touching the harness.

`nuke RegressionValidate` (Step 2 of the `/regression-validation` skill, run from
`swift-dotnet-packages`) currently takes ~1h15m on an M-series Mac for the full
0.12.0 matrix (~30 cells across ios-sim, ios-device, macos, maccatalyst,
tvos-sim). On a high-core machine that's mostly idle silicon — the matrix
runs strictly serially.

This doc was the plan of record: **Round 1** was the implementation step (done),
**Round 2** is gated on Round 1's measured results (parked — see Status above).

## Baseline (0.12.0 rerun on 2026-05-31)

- Per-cell wrapper xcframework rebuild: ~10–20s
- Per-cell C# binding compile: ~10–20s
- Per-cell iOS/tvOS app bundle build: ~30–60s
- Per-cell `simctl install` (sim cells only): ~10–30s
- Per-cell launch + test execution: ~10–30s
- Effective per-cell wall-clock: ~2 min
- Total cells: ~30 → ~60 min compute, ~1h15m wall-clock with overhead
- ios-device cell tail: ~12 min (one phone, exclusive)
- Non-device cells: ~20 min sequential, fully parallelizable

Most of the wall-clock budget is spent in a single MSBuild/Swift compile
pipeline at a time, while the rest of the CPU sits idle.

## Round 1 — landed 2026-06-01

Measured Step 2 wall-clock on the 0.12.0 / Apple 26.2.4 verification run:
**19:59 vs ~75 min baseline = ~3.8× speedup** — better than the 1.5–2×
target. The implementation matches the plan below; the surprises landed in
four follow-up fixes captured under "Round 1 follow-ups" further down. Read
that subsection before touching the same machinery again — every one of
those was a non-obvious foot-gun.

The original plan-of-record follows. Target was: cut wall clock to
~35–45 min. Touches Step 2's Nuke target + one helper + one skill file,
plus Step 3's `validate.sh` and `run-all-sim.sh`. Mirrors the
already-reviewed `Build.Validation.cs` parallel harness for Step 2 and
applies the same pre-restore + multi-sim pattern to Step 3.

### Step 2 changes (swift-dotnet-packages)

#### 1.1 Parallelize cells in `Build.RegressionValidate.cs`

Replace the serial `foreach (var cell in packageCells)` loop (around
`Build.RegressionValidate.cs:520`) with a `SemaphoreSlim` + `Task.WhenAll` +
`ConcurrentDictionary<Cell, CellOutcome>` shape mirroring the Phase 3a/3b/3c
pattern in `Build.Validation.cs:203–295`.

Required guards:

- **Pre-restore phase** before the cell loop: one `dotnet restore` over every
  in-scope test csproj, then every per-cell `dotnet build` / `dotnet publish`
  adds `--no-restore`. Without this, parallel restores on the same csproj
  race on shared `obj/project.assets.json` / `obj/*.nuget.cache` writes even
  though the SDK splits `_SwiftBindingIntermediateDir` per TFM. The pre-flight
  csproj-rewrite + `obj/` wipe already runs serially, so the warm restore
  fits naturally as the last pre-flight step.
- **Single global semaphore** sized by `--jobs`. The device lane uses a
  separate UDID-exclusion lock but still draws from the same pool — running
  general=N + device=1 would put N+1 cells under memory pressure
  simultaneously, and the NativeAOT publish is the RAM-heaviest path.
- **Default `--jobs 4`**, overridable. `cores - 2` (the `nuke validate` default)
  is too aggressive here: that target is compile/generate only;
  RegressionValidate adds CoreSimulator install/launch and NativeAOT publish
  on top. Start conservative, bump after measuring memory headroom.
- **`--serial`** for parity with `nuke validate`.
- **Longest-first scheduling** driven by the prior
  `artifacts/regression-validate-$VERSION.json` durations, not by cell count
  or lines-of-code. Mappedin / Stripe / RealityFoundation are the 65–75s
  outliers and should start first.
- **Per-cell output buffered** and replayed in stable order after
  `Task.WhenAll` — same shape as `Build.Validation.cs`'s `GenOutput` /
  `SwiftOutput` capture + manifest-order replay. The skill's "tee the long
  log, read it later" contract (`SKILL.md:26`) breaks if 4 cells interleave
  raw output.
- **`PrintMatrix` and `WriteJsonArtifact` move from `List<CellOutcome>` to
  `ConcurrentDictionary`** so post-phase aggregation is well-defined.

Pre-flight stays untouched: `BumpSdkVersionInternal` / `BumpAppleVersionInternal`
/ `PackCrossFrameworkDependencies` already run before the cell loop and are
the only mutators. Per-cell `dotnet` processes write only into their own
`tests/obj/{tfm}` / `bin/{tfm}` / `publish/` trees (different TFMs use disjoint
directories even for the same library), so the parallel-cell shape is safe
at the filesystem layer.

#### 1.2 Extend `SimulatorFleet` for N-way fanout

`SimulatorFleet` is a single-sim lifecycle manager today (boot one, hand back
its UDID). For Round 1 it needs an `EnsureFleet(int n)` that returns a list of
N booted UDIDs. Each ios-sim cell receives its own UDID via the existing
`DeviceUdid` parameter — no cell may use "booted" string addressing. Fleet is
disposed on Nuke target exit.

### Skill change (orchestrator)

#### 1.3 Surgical NuGet cache clear in `SKILL.md`

Replace the wholesale `dotnet nuget locals all --clear` in
`~/.claude/skills/regression-validation/SKILL.md:56` with a targeted delete
of only the same-version SwiftBindings extractions. Two corrections vs. the
naive sketch:

- Honor `NUGET_PACKAGES` env first, then `~/.nuget/packages` — reuse the
  same resolution `Build.RegressionValidate.cs:464–470`
  (`GlobalNuGetPackagesDir()`) already does.
- Glob must catch every sibling apple-framework dir, not just the core:
  `<global>/swiftbindings.apple.*/*/$APPLE_VERSION` (covers
  `swiftbindings.apple.matter/$APPLE_VERSION`,
  `swiftbindings.apple.realityfoundation/$APPLE_VERSION`, etc., which
  `PackCrossFrameworkDependencies` produces).

Preserves correctness — only same-version SwiftBindings packages can collide
in the global-packages tree — while keeping MSBuild's incremental-build
engine warm for hundreds of unrelated downstream csprojs. HTTP cache and
`temp` stay (the flow uses explicit local-packages with exact-version pins
after stamping; no floating ranges).

### Step 3 changes (internal-binding-testing)

#### 1.4 Pre-restore + `--no-restore` on per-cell builds

In `validate.sh`, after the wrapper Mach-O check and before the sim/device
runners, run one `dotnet restore` over every test csproj in scope (sim AND
device variants for the filtered library set). Then `run-all-sim.sh` and
`run-all-device.sh` pass `--no-restore` to their per-cell `dotnet build` /
`dotnet publish` calls.

Self-contained and orthogonal to concurrency. Same rationale as Step 2's
pre-restore: NuGet restore writes shared project-level files
(`obj/project.assets.json`, `obj/*.nuget.cache`) that race when invoked
back-to-back, and the warm restore makes those writes a one-time cost
instead of per-cell.

#### 1.5 Parallelize `run-all-sim.sh`

Today: serial bash loop over 15 libs against one hardcoded booted simulator
(`run-all-sim.sh:29`). Each cell does build → install → launch → poll.

Round 1 shape:

- `validate.sh` accepts `--jobs N` (default 4) and propagates to
  `run-all-sim.sh`.
- At the top of `run-all-sim.sh`, boot N simulators via `simctl` (reuse
  existing iPhone sims by preference, create fresh if needed) and collect
  N UDIDs. Replace the hardcoded `SIM_UDID` and every `install booted` /
  `launch booted` with the worker's assigned UDID.
- Drive the per-library worker function via a Python worker pool (or bash
  FIFO of UDIDs) so each worker pulls the next library off a queue,
  acquires a UDID, runs build+install+launch+parse, releases the UDID.
  Python is the cleanest fit since `validate.sh` already shells out to
  Python for its aggregator (`validate.sh:272`).
- Each worker writes its result line to a per-worker temp file; merge into
  the existing `$RESULTS_FILE` after all workers complete. Preserves the
  exit-code + result-file discipline the skill's aggregator relies on.

Intra-Step-3 parallelism is safe: crash detection is per-cell file grep
(`run-all-sim.sh:108`), not the host-global `~/Library/Logs/DiagnosticReports`
scan that `Build.Validate.cs::ValidateSimFor` uses. Bundle ids are unique per
library within the repo (`com.test.${LIB_LOWER}simtest`), so two parallel
cells can't collide on the simulator either.

`run-all-device.sh` stays serial — one phone, exclusive install/launch.

### Round 1 follow-ups (not in the original plan)

Four bugs surfaced during Round 1 verification that the original sketch
didn't anticipate. Every one of them is the kind of thing that will re-bite
a future implementer of Round 2 (or anyone porting the same pre-restore
pattern to another harness), so they're captured here, not buried in
commit history.

#### F1. Device cells can't share a pre-restored assets file (NETSDK1083)

The naive sketch was: one pre-restore per csproj with
`-p:RuntimeIdentifiers=iossimulator-arm64;ios-arm64` so both the sim cell
and the device cell can use `--no-restore`. **This doesn't work on the
.NET 10 SDK.** The semicolon-separated multi-RID value bleeds into the
singular `$(RuntimeIdentifier)` slot and trips `NETSDK1083` (`%3B` escape
gets past MSB1006 but doesn't fix the singular/plural confusion downstream).

Working pattern in `Build.RegressionValidate.cs`:

- Pre-restore is sim-RID only (the csproj's default).
- Sim / macOS / maccatalyst / tvos cells use `--no-restore`.
- The device cell drops `--no-restore` and does its own implicit restore
  at `dotnet publish -r ios-arm64` time.
- A TCS-based wait gates the device cell behind its same-library sim cell
  so the device's `project.assets.json` overwrite can't race a sim cell
  that's still mid-build under `--no-restore`.

NativeAOT publish is multi-minute anyway — the extra ~5s restore is noise.
Anyone tempted to retry the multi-RID restore approach should confirm the
SDK behavior on the current `dotnet` major before assuming the docs work.

#### F2. ProjectReference is the same race shape as NativeReference to obj/

The original Fix 2 narrowed in on
`NativeReference Include="../../<lib>/obj/.../*.xcframework"` (BlinkIDUX →
BlinkID). That's not the only race shape. Cross-library
`ProjectReference Include="../../<lib>/...csproj"` is the *canonical*
MSBuild trigger and races identically — the consumer cell's transitive
build of the sibling library writes to the sibling's `obj/` tree while
the sibling's own cell is running.

Three same-shape sites exist in the matrix today:

- BlinkIDUX → BlinkID (NativeReference + ProjectReference, doubly covered)
- MatterSupport → Matter (ProjectReference only)
- RealityKit → RealityFoundation (ProjectReference only)

`InferInterLibraryObjDeps` now matches both shapes via one regex
alternation. Static-file references like
`Include="../../BlinkID/BlinkID.xcframework"` (no `obj/`, no `.csproj`)
point at pre-shipped artifacts and don't race — leave those out of the
dep graph or you'll over-serialize.

Audit empirically before assuming a single shape covers everything: a
`grep -E 'Include="\.\./\.\./[A-Za-z0-9]+/'` across `apple-frameworks/*/tests`
and `libraries/*/tests` is the cheap check.

#### F3. SimTest + DeviceTest share `obj/project.assets.json` (Step 3)

In `internal-binding-testing/`, each library directory has both
`<lib>SimTest.csproj` and `<lib>DeviceTest.csproj`, neither sets
`BaseIntermediateOutputPath`, so they share `<lib>/obj/project.assets.json`.
The original validate.sh sketch ran one combined pre-restore loop
(sim csprojs then device csprojs) — Device's restore overwrites Sim's
RID-specific target, and every `--no-restore` sim build then fails
`NETSDK1047`.

Fix: split the pre-restore into two phases, each running immediately
before its runner. `Sim pre-restore → sim runner → Device pre-restore →
device runner`. Each phase reads the assets file it just wrote.

If you ever consolidate the SimTest/DeviceTest pair into a single
multi-TFM csproj, this whole class of problem disappears — but the
current per-RID single-TFM split is what's in the repo.

#### F4. Bash trap `$udid` scoping (FIFO worker pool)

`run-all-sim.sh` uses a FIFO of UDIDs and a per-worker
`trap '...' EXIT` to belt-and-suspenders re-enqueue the UDID on signal
death. The original write used single quotes:

```bash
trap 'printf "%s\n" "$udid" >&3' EXIT
```

`$udid` is `local` to `run_one_lib`. Single quotes defer expansion to
trap-fire time — by then the function has returned and `$udid` is out of
scope, so every worker writes an **empty line** into the FIFO. The first
JOBS workers run fine on the seed UDIDs; every worker after reads `""`,
`xcrun simctl install ""` fails, and the cell is marked
`INSTALL_FAILED`. The pattern looks deterministic and bizarre: exactly
the first `--jobs` libraries pass, every subsequent one fails identically.

Fix: capture at trap-set time with double-quote interpolation
(`trap "printf '%s\n' '$udid' >&3" EXIT`), or drop `local` so `$udid`
survives function return. Same gotcha will recur in any bash FIFO worker
pool that uses `local` + trap. Stamp a UDID into every "starting on"
log line — a missing UDID column is the fingerprint of this bug.



- **Codesign keychain contention** under parallel `dotnet publish -p:PublishAot=true`.
  Mitigation: `--jobs 4` start cap + one `security unlock-keychain` in
  pre-flight. If "User interaction not allowed" appears, lower further or
  funnel publish through a dedicated single-slot.
- **RAM ceiling.** `--jobs 4` is conservative; bump only after observing
  actual memory headroom. NativeAOT publish + concurrent app-bundle builds
  can push a 32 GB box into swap before CPU saturates.
- **Triage UX regression.** The per-cell buffered-output replay mitigates
  interleaving in the main log. If it still degrades, add per-cell tee files
  (`/tmp/regression-cell-$LIBRARY-$PLATFORM.log`) alongside the main log.
- **Simulator IPC under N booted sims.** CoreSimulator supports concurrent
  install/launch against distinct UDIDs, but it's not free. Default
  `--jobs 4` keeps the fleet small; revisit if simctl operations start
  timing out.

## Round 2 — gated on Round 1 measurements

Round 1 shipped at ~3.8× speedup (19:59 wall-clock vs ~75 min baseline).
Before picking any item below, do one timed run with per-phase
instrumentation (build vs install vs launch vs AOT-link, per cell) so
the tail is identified empirically, not guessed. The current tail is
probably the NativeAOT publishes that fan out across the device cells —
but the "pre-build device apps in parallel" item below is a hypothesis
until measurement confirms it. Don't pre-commit to any single Round 2
item.

### Wrapper xcframework content-hash cache

The Swift wrapper compile (`obj/{config}/{tfm}/swift-binding/*.xcframework`)
rebuilds per cell today because csproj timestamps invalidate MSBuild's
incremental checks even when content is identical. After Round 1, the
~10–20s per-cell wrapper rebuild is a bigger fraction of the remaining wall
time and worth attacking.

Approach: pin the wrapper cache by content hash over `(ABI JSON, SDK
assembly, Swift source set)` instead of csproj timestamp. Lets sibling
cells of the same library and same-content re-runs hit the cache.

### Pre-build device apps in parallel (`run-all-device.sh`)

Today: serial build → install → launch → parse per library, with the phone
serializing install/launch only. The `dotnet publish -c Release -p:PublishAot=true`
step is RAM-heavy but otherwise independent across libraries.

Round 2 shape: pre-publish all device apps in a bounded parallel pass
(default 2–3 concurrent publishes, driven by `--jobs` propagated from
`validate.sh`), then serially install + launch + parse on the phone.
Decouples the compute-bound tail from the device-bound tail.

### Cross-step overlap of Step 2 and Step 3

Step 2 (swift-dotnet-packages) and Step 3 (internal-binding-testing) are
independent except both want the physical phone. The skill could run them
partly in parallel:

- Step 3's sim cells run alongside Step 2 from the start.
- A lockfile (`/tmp/swift-bindings-device.lock`, atomic mkdir, with
  pid-check staleness handling) serializes the device sub-phase between
  them.

Prerequisites before this is safe:

- Per-run temp diagnostic dirs (or per-UDID crash-log filtering) so the
  host-global `~/Library/Logs/DiagnosticReports` scan in `ValidateSimFor`
  (Step 2 path) can't attribute one repo's crash to the other's cell.
  Shared libraries (e.g. Kingfisher) appear in both repos under the same
  `SimTest` app-name prefix.
- Coordinated sim fleet across the two repos, or disjoint fleets — Round 1
  introduces independent fleets per step, which would fight under overlap.
- Stale-lock mitigation (timeout + owner pid + `kill -0`) so a crashed
  Step 2 doesn't permanently block future Step 3 device runs.

## Out of scope

- **Reducing test coverage.** The matrix is the durable gate, not something
  to thin out.
- **Skipping cells based on what changed.** Heuristic, error-prone, masks
  cross-cell regressions (e.g. the macOS-bridge gap that triggered this
  whole analysis).
- **Compiling Swift faster.** Handed to swiftc; no levers here.
- **Moving version stamping to a single `Directory.Build.props`.** Real
  payoff but touches release engineering and the speedup is modest after
  Round 1 already collapses the redundant-restore problem.
