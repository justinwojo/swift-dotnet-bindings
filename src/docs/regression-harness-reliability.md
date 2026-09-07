# Regression-harness reliability

The pre-release regression gate (`/regression-validation`) runs two downstream consumer repos —
`swift-dotnet-packages` (`nuke RegressionValidate`, an 80-cell library × platform matrix) and
`internal-binding-testing` (`validate.sh`). Both drive real apps on real simulators and a real
phone, so a red cell can mean either "the binding is broken" or "the launcher/host had a bad
moment". Today the harness cannot tell those apart, and the second kind is common enough that a
full-green 80-cell run is the exception rather than the norm.

That costs real time on every release: each red has to be re-run and hand-diagnosed before anyone
can say whether it blocks. This doc records the specific mechanisms behind the recurring
environmental reds, so they can be fixed rather than re-diagnosed.

**The fixes described here land in `swift-dotnet-packages`' `build/`, not in this repo.** The doc
lives here because this is where release decisions get made and where the equivalent policy already
exists.

## Gap 1 — the first-marker timeout is too tight for cold-start cells

`RegressionValidate` gives every cell a fixed window to print its first marker line. The value is
`Timeout` (`build/Build.cs`), a `[Parameter("Sim test timeout in seconds")]` defaulting to **30**,
passed unchanged to both `ValidateSimFor` and `ValidateDeviceFor`
(`build/Build.RegressionValidate.cs`). When the window closes with no marker, `StdoutWatcher`
records `no marker after 30s` and the cell is a `TIMEOUT`.

The problem is that several cells legitimately need close to the whole window. Measured on isolated
re-runs at an unchanged commit, `CryptoKit/ios-sim` reached its first marker at **32.4s** and
`BlinkID/ios-sim` at **29.9s** — one on each side of the deadline. A cell whose honest cold-start
cost straddles the limit will pass or fail depending on what else the host is doing.

Two things make that worse in a full run:

- **Cold start is the worst slot, and the scheduler hands it out.** Cells dispatch under a
  `--regression-jobs` semaphore (default 4). The first wave runs against freshly-booted simulators
  and a cold MSBuild, and it is also the wave with the most concurrent first-time builds. In the
  0.19.4 run, `BlinkID/ios-sim` and `CryptoKit/ios-sim` both started in that first wave and both
  timed out; every sim cell dispatched later passed.
- **The same cells recur.** `CryptoKit/ios-sim` timed out this way on the 0.19.3 run too, and also
  passed (31.2s) when re-run alone. It is a property of those apps' startup cost, not of the change
  under test.

Worth considering:

- Separate the *launch/first-marker* budget from the *test-run* budget. They answer different
  questions and only the first is sensitive to host load.
- Scale the budget for the first dispatch wave, or warm the toolchain before the matrix opens, so a
  cell is not penalised for drawing the cold slot.
- Whatever the shape, pick the number from measured first-marker times across the corpus with
  headroom, rather than leaving it at a value several cells sit on top of.

A `--timeout N` override already exists and can raise the window for a single run today. That is a
usable stopgap, not a fix — it moves the cliff rather than removing it, and it applies to every cell
equally.

## Gap 2 — the device lane treats a launcher abort as a product failure

`devicectl` sometimes aborts before it ever sends the launch request, reporting
`CoreDeviceError 10002` with `NSPOSIXErrorDomain error 22 (EINVAL)`. The app image is never entered,
so the run carries no signal about the binding at all. `RegressionValidate` has no handling for this:
`DevicectlClient.BuildLaunchPsi` builds one launch invocation, the watcher sees a non-zero exit with
no marker, and the cell is recorded as a `FAIL` — indistinguishable in the artifact from a genuine
test failure.

**This repo already has the policy the regression lane is missing.**
`build/Models/LaunchDiagnostics.cs` encodes the discriminator (`LauncherAborted` patterns,
`MaxLauncherAbortAttempts = 3`), and `LaunchUntilAppRuns` in `build/Build.BindingTests.cs` retries on
it; `Build.BindingTests.MixedDirect.cs`, `Build.BindingTests.MixedPack.cs` and
`Build.RuntimeTests.cs` all go through it. `swift-dotnet-packages` has no equivalent, which is why
only its device cells ever show this.

Porting that discriminator is the first step, and it is worth porting the *distinction* rather than
just the retry: a launcher abort should be reported as its own result, not folded into `FAIL`. A
cell that never entered the app image is unknown, not failed, and an artifact that says so keeps a
reader from spending a release cycle chasing a binding bug that was never observed.

### The retry alone may not be enough

Retrying assumes the abort is intermittent. On the 0.19.4 run it was not: `MapLibre/ios-device`
failed three consecutive harness attempts, first with an install-time signature rejection
(`0xe8008001`) and then twice with the EINVAL abort — while the same bundle, launched directly,
started cleanly and passed 12/12 every time, with and without the harness's `--terminate-existing`.

The remaining difference is that the harness launches immediately after `InstallApp` returns, and
MapLibre is the heaviest device app in the corpus (a ~7.9 MB third-party framework plus the NativeAOT
binary). That points at the install→launch handoff: `devicectl install` returning does not appear to
guarantee the app is ready to launch, and the window looks wider for larger bundles.

Worth considering: confirm the app is installed and launchable before launching (rather than
sleeping a fixed amount), and treat "install reported success but the app is not yet launchable" as
its own retryable state. The signature rejection on the first attempt is likely the same handoff seen
from the install side — the artifact it rejected verified clean under `codesign --verify --strict`
immediately afterwards and installed without changes.

## Telling an environmental red from a real one

Until the above lands, this is the triage that has worked. It is ordered cheapest-first, and the
first three answer most cases without rebuilding anything.

1. **Diff the generated surface.** Every run snapshots each binding's `api-surface/`. If the failing
   library's surface is byte-identical to the last release's run, the generator did not change what
   that library emits. In the 0.19.4 run the only libraries whose surface changed — Mappedin, Nuke,
   Stripe — all passed every cell, while all three reds were in libraries whose output was unchanged.
2. **Check the other runtimes for the same library.** The cells share test source. A marshalling or
   ABI defect does not usually confine itself to one runtime, so a red sim cell beside green device,
   macOS and Catalyst cells is evidence about the host, not the binding.
3. **Read the failure mode, not the verdict.** `no marker after Ns` with empty app output, and
   `CoreDeviceError 10002`/EINVAL, both mean the app never ran. Neither carries product signal.
   Past the point where the app prints its first line, failures are ours.
4. **Re-run the cell alone** (`--filter <Library> --platforms <platform>`). This writes a
   `partial-N` artifact and leaves the canonical one untouched, so it is safe to do during a release.
5. **Drive the app by hand** if the harness still disagrees — install and launch with `devicectl`
   directly and read the app's own output. That separates "the harness cannot run this" from "this
   does not work".

Two standing cautions. A cell that consistently fails in the harness while passing by hand is still
a gap worth fixing, because that cell is delivering no coverage in the meantime — "environmental"
is an explanation, not a dismissal. And a red that clears on re-run should be recorded rather than
waved through: the recurrence of the *same* cells across releases is what identified both gaps above.

## Scope

Out of scope here: `BUILD-ONLY` cells (tvOS has no validator yet — a known coverage gap, not
reliability), and the `internal-binding-testing` lane, which has not shown this failure class. If it
starts to, it shares `StdoutWatcher`'s marker contract and the notes above should transfer.
