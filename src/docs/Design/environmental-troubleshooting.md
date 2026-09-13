# Environmental troubleshooting

Recorded discriminators that prevent repeated diagnosis of known environmental failures. A different signature remains an open investigation; do not use these records to dismiss runtime crashes generally.

<a id="devicectl-launch-abort-after-a-successful-install"></a>

## `devicectl` launch abort after a successful install

`CoreDeviceError 10002` wrapping `NSPOSIXErrorDomain 22` (EINVAL) has appeared on
`devicectl device process launch` after a successful install. That signature alone does not
establish that the app never started or exclude a binding, loader, signing, or entitlement defect.
Neither empty app output nor a missing `LaunchActionDeclaration` log entry proves non-execution.
Inspect the complete launch transcript and available termination diagnostics; app output, loader
errors, crash evidence, and launcher start confirmations take precedence over generic abort wording.

This repo's [launch classifier](../../../build/Models/LaunchDiagnostics.cs) examines captured
output and result state; it does not query unified logs for `LaunchActionDeclaration`. The downstream
`swift-dotnet-packages` classifier is narrower: only explicit boot/state or transport failures,
without app-start or product-failure evidence, qualify for infrastructure retries. Preserve every
attempt and any observed failure: a successful retry does not erase an earlier failure or supply
coverage for an unobserved run. **Trigger:** a recurring launch failure, contradictory start evidence,
or exhausted retries requires investigation rather than an environmental dismissal.

<a id="historical-downstream-regression-observations"></a>

## Historical downstream regression observations (0.19.3–0.19.4)

Retained from the release investigation; downstream implementation status checked on **2026-09-12**.
The original note did not attach raw run artifacts, so these are recorded observations rather than
independently reverified timings:

- At unchanged commits, isolated `CryptoKit/ios-sim` reruns reached a first marker at **31.2s**
  (0.19.3) and **32.4s** (0.19.4); `BlinkID/ios-sim` reached it at **29.9s** (0.19.4).
  These straddled the then-shared 30s budget. First-wave failures followed by successful isolated
  runs suggested startup/load sensitivity, but did not establish that the binding was uninvolved.
- In 0.19.4, `MapLibre/ios-device` had an install-time signature rejection (`0xe8008001`), then
  two EINVAL launch failures. The same bundle reportedly passed **12/12** when launched directly,
  with and without `--terminate-existing`; it also verified under `codesign --verify --strict`
  and installed unchanged. An install→launch readiness race was a hypothesis, including the
  proposed connection to the signature rejection; those observations did not confirm it.

The main proposed fixes already exist in the owning sibling repository's `build/` tree:
`swift-dotnet-packages/build/Build.RunBudgets.cs` supplies a separate **120s default launch budget**;
`build/Helpers/StdoutWatcher.cs` separates launch and run deadlines;
`build/Models/LaunchDiagnostics.cs` and `build/Build.Validate.cs` classify and retry eligible launcher
failures up to three attempts. `build/Build.RegressionValidate.cs` records exhausted launcher aborts
as **ERROR**, distinct from a test **FAIL**, and now runs tvOS validation (or records unavailable
coverage). These paths are historical context, not an open implementation plan.

For future triage, unchanged API snapshots establish only unchanged recorded surface, not unchanged
wrapper implementation, packaging, or runtime behavior. Green cells on other runtimes narrow the
failure's scope but cannot exclude a runtime-specific ABI or marshalling defect. Retain original
artifacts before isolated or direct-launch comparisons, and keep unexplained failures unresolved.

<a id="mt7155-mt7156-bundleresource-dedup-warnings-on-macos"></a>

## MT7155 / MT7156 `BundleResource` dedup warnings on macOS

Two `LogicalName` collisions on `Swift/*Database.xml` bundle resources during macOS builds. Pre-existing, warning-only, no runtime effect. Don't chase them mid-gate. **Trigger:** they promote to errors, or a resource genuinely fails to land in a bundle.

<a id="liveactivity-request-path-tests-fail-a-foreground-active-precondition"></a>

## LiveActivity `request()`-path tests fail a foreground-active precondition

Recorded case: six ActivityKit tests calling `Activity.request()` failed under a CLI-launched simulator and were attributed to the framework's foreground-active precondition. Verify app state and the actual precondition diagnostic before applying that explanation to a new failure. Passing non-`request()` calls does not by itself exclude a binding defect. **Revisit when:** the cells fail with the app demonstrably foreground-active, on a device leg, or with a different diagnostic.

<a id="storekit-products-for-returning-0-and-storekit-config-files"></a>

## StoreKit `products(for:)` returning 0, and `.storekit` config files

Two recorded observations: empty `products(for:)` results were attributed to App Store Connect / sandbox configuration, with marshalling controls passing on simulator and device at `sdk-v0.15.0`; loading a `.storekit` configuration under `mlaunch` produced a SIGSEGV in StoreKit with no binding frames. These describe those investigations, not a general impossibility of configuration-file support or proof that every new failure is upstream. **Revisit when:** configured product identifiers still return nothing, configuration loading is required, or current crash evidence differs; inspect the complete call path before attribution.

## Verify the effect of control commands

**When a control action gates something expensive, verify the *effect*, never the *return code*.** Three commands on this machine report success while doing nothing: (i) `find -newermt` — this `bfs`-backed `find` rejects both `-newermt '-300 seconds'` and a non-integer `-mmin`, and the empty stdout reads as "zero hits", so never gate a build/agent handoff on it (use an integer `-mmin -N`, or an epoch diff: `newest=$(find … -exec stat -f '%m' {} \; | sort -rn | head -1); echo $(( $(date +%s) - newest ))`); (ii) `pkill -f 'nuke <target>'` matches nothing because `nuke` is a shim — the real process is `dotnet run --project build/_build.csproj -- <target>`, so match `_build.csproj -- <target>` and confirm the kill by the log going quiet, not by exit code; (iii) grepping a `nuke test` log for a test-class name proves nothing, because `--verbosity minimal` names only failing/skipped tests — enumerate from the TRX instead.
