# Apple toolchain qualification profiles

The profiles pin a complete SDK, workload and Xcode pairing. They are qualification
configurations; adding a manifest does not declare that the configuration has passed
the release gates.

| Profile | Role | .NET SDK | Workload set | Xcode |
| --- | --- | --- | --- | --- |
| `net10-xcode27` | .NET 10 release anchor | 10.0.404 | 10.0.404 | 27.2 |
| `net11-xcode27` | .NET 11 release anchor | 11.0.100 | 11.0.100.1 | 27.2 |
| `net10-xcode26` | Supported compatibility lane (.NET 10 on Xcode 26.3) | 10.0.107 | 10.0.107 | 26.3 |

## Roles and cadence

The two Xcode 27.2 profiles are the release anchors, the newest toolchain for each .NET
version. `net10-xcode26` is a fully supported compatibility lane: many consumers stay on
.NET 10 and Xcode 26 for months, and some never move. All three profiles run the full
release matrix for their framework: the simulator, host, device and package gates.

The anchors differ from the compatibility lane in one respect: no security-patched .NET 10
runtime pairs with Xcode 26.x. GHSA-p3jp-9v83-76w9 needs runtime 10.0.14 or later; the first
workload set carrying it, 10.0.114, needs Xcode 27.1, and 10.0.115 needs Xcode 27.2. The
newest Xcode 26 Apple workload in any 10.0.1xx set ships runtime 10.0.11, which is still
affected.

`net10-xcode26`'s NativeAOT restore therefore meets the affected
`Microsoft.DotNet.ILCompiler` package, which the repository's `TreatWarningsAsErrors` turns
into an NU1903 error. The profile manifest suppresses exactly that advisory URL
(`nugetAuditSuppress`, with its reason). The advisory concerns Microsoft's toolchain, not
SwiftBindings code, and a consumer outside the repository still sees the warning. The
launcher exports the URL and `Directory.Build.props` turns it into one `NuGetAuditSuppress`
item. Outside this profile nothing is suppressed, and every other NU1901-NU1904 still fails.
`nuke test` (ToolchainProfileTests) evaluates the NativeAOT device app under each profile and
fails if a profile's suppressions differ from its manifest, if an unprofiled restore
suppresses anything, or if audit or its error escalation is weakened. Mono AOT needs no
suppression, because its runtime packs come from the workload rather than NuGet.

Xcode 26 support ends once the App Store requires apps built with the iOS 27 SDK
(date: _to be set by the owner_). Until then the lane stays in the release matrix.

| When | What runs |
| --- | --- |
| Every change | .NET 10 on Xcode 26.3 with the global 10.0.107 install: `net10-xcode26`, or the unprofiled repository workflow with Xcode 26.3 selected. Run the gates the repository's gate table asks for. Add a `net11-xcode27` check when a change touches marshalling, calling conventions or runtime dispatch. |
| Before a release | The full matrix on all three profiles: `net10-xcode26`, `net10-xcode27` and `net11-xcode27`. |

The .NET 10/Xcode 27.2 pair follows Microsoft's
[27.2.10000 release](https://github.com/dotnet/macios/releases/tag/dotnet-10.0.1xx-xcode27.2.10000)
(Apple workload manifests 27.2.10000). The .NET 11/Xcode 27.2 pair is the .NET 11 GA
SDK with Microsoft's
[27.2.11039 release](https://github.com/dotnet/macios/releases/tag/dotnet-11.0.1xx-xcode27.2.11039)
(Apple workload manifests 27.2.11039). Both use Xcode 27.2 (27B52) from separate
SDK roots, `~/.local/share/swift-bindings/toolchains/net10-xcode27.2/dotnet` and
`~/.local/share/swift-bindings/toolchains/net11-xcode27.2/dotnet`; the .NET 11 root
also carries a .NET 10 runtime (10.0.15) for the net10 build tools.

Earlier receipts for these profile names used other pins: `net10-xcode27` used SDK
10.0.401/workload 10.0.401.1 with Xcode 27.0 (and before that 10.0.402/10.0.402.1
with Xcode 27.1); `net11-xcode27` used the RC2 SDK 11.0.100-rc.2.26504.105/workload
11.0.100-rc.2.26509.2 with Xcode 27.0. Their recorded identities remain authoritative
for the runs they describe; they do not qualify the current 27.2 pairs, which have
not yet been qualified. Changing a profile invalidates its disposable workspace.

Install the matching SDK and the `ios`, `tvos`, `macos` and `maccatalyst` workloads
before using a profile. The launcher validates the installed versions. It does not
install tools or change the machine's `xcode-select` setting.

Run the usual Nuke targets through the profile launcher:

```sh
bash build.sh --toolchain-profile net10-xcode26 -- test
bash build.sh --toolchain-profile net10-xcode27 -- test
bash build.sh --toolchain-profile net10-xcode27 -- binding-tests --compile-only
bash build.sh --toolchain-profile net10-xcode27 -- binding-tests --sim --device-udid <simulator-udid>
```

For other installation paths, use launcher options before the `--` separator:

```sh
bash build.sh --toolchain-profile net10-xcode27 \
  --dotnet-root /path/to/dotnet \
  --developer-dir /Applications/Xcode.app/Contents/Developer \
  -- test
```

Without `--toolchain-profile`, `build.sh` retains the normal repository workflow
and `global.json` selection.

The [.NET 10 maintenance qualification](net10-maintenance-qualification-2026-10-22.md)
records the tested 26.3 and 27.0 pairings. The 27.0 profile passed the full Mono AOT
device suite, but its full NativeAOT suite was blocked by upstream IL2037 for explicit
interface properties on NSObject subclasses. That record does not cover the current
27.2 pin; do not treat the profile manifest as unrestricted NativeAOT support until
the 27.2 pair passes the NativeAOT gates.

## Isolation and reuse

Each profile owns `artifacts/toolchains/<profile>/`. Its `workspace/` is a disposable
Git checkout containing the current source, including uncommitted changes and new
non-ignored files. Build products from the source checkout are excluded. The profile
has its own NuGet HTTP cache and CLI home. Installed NuGet packages live inside
its owned external temporary directory, so packaged generator source discovery
cannot find the owning checkout and inherit a different workload pin. The temporary directory
lives under `/private/tmp/swift-bindings-tc-*`, outside the repository, so scratch
consumer projects cannot inherit repository build properties. A standalone `global.json`
in that directory pins the same SDK and workload set, even when newer SDKs share the
selected .NET installation.
Expect the first run to restore packages and build both native parser architectures.

`toolchain.json` records SDK/workload identities, Xcode and Swift versions, native SDK
paths/versions, the installed NuGet package directory, and reference-assembly hashes. `source.json` records the source
inventory and fingerprints. `--prepare-only` produces the checkout and receipts
without running Nuke. `--state-root /path` relocates profile storage; a location
inside the source checkout must be Git-ignored.

An unchanged source/toolchain identity reuses the checkout. A changed identity
replaces only the launcher's owned checkout; package caches remain. The parser's
`tools/SwiftInterfaceParser/.build/` cache also survives an unrelated source change
when the toolchain, parser package/source inventory and `Build.SwiftInterfaceParser.cs`
are unchanged. The previous source receipt must match the owned checkout and its
source fingerprint. The launcher temporarily moves only that cache within profile
state while refreshing the checkout; interrupted holders are never reused. Staged
parser binaries and managed outputs are discarded, so Nuke still builds the parser,
stages it and checks its universal2 architectures. Reuse flags
such as `--skip-regen`, `--skip-build` and `--quick` reject a changed identity rather
than reusing incompatible artifacts. Save any evidence needed from `workspace/`
before changing source and running the profile again.

Qualification runs compare canonical baselines but cannot seed, promote or
automatically update them, with one exception: `net11-xcode27` seeds its own .NET 11
runtime-identity floors (below). Each profile permits one invocation at a time. Different
profiles can build concurrently, but runtime tests must use distinct simulator
UDIDs, or run sequentially on a shared simulator/device: app bundle identities are
shared across profiles.

The disposable checkout and profile caches can be removed after their evidence has
been retained and all invocations have finished. This does not remove either SDK
installation, Xcode, or the source checkout. The external temporary directory is
recorded in `toolchain.json`; retain needed evidence there before removing it too.

## Managed .NET 11 preparation probes

The generator still targets net10.0. Its own runtime configuration permits major
roll-forward, preserving preference for an installed .NET 10 runtime while allowing
an 11-only host. Consumer app roll-forward is unchanged.

With an isolated .NET 11 SDK (no Apple workloads required), run:

```sh
bash build.sh --toolchain-profile net10-xcode26 -- verify-managed-compatibility \
  --generator-candidate-dotnet /path/to/isolated-net11/dotnet
```

The gate executes the same built generator on 10-only, 11-only and temporarily
composed side-by-side runtime roots, checks host traces, parses real dependency
input, and proves that removing major roll-forward fails on the 11-only host.
The probe opts into prerelease runtime roll-forward only in generator child
processes, so it also accepts a preview .NET 11 runtime. Its recorded results used
the RC2 runtime; repeat it with the GA root before claiming GA qualification.

It also builds a real net11.0 desktop executable consuming the existing net10.0
Runtime assembly and its production configuration target. Safe and Direct policy
cases both identify CoreCLR, including when dynamic-code support is explicitly
disabled, and execute Swift string round-trips through the committed native runtime.
It also verifies that conflicting Mono/AOT selection and suppressed native
compilation produce the expected configuration diagnostic under the .NET 11 SDK.
Receipts are written to the profile workspace's `artifacts/generator-hosts/` and
`artifacts/runtime-host/`; retain them before changing source/profile inputs.

These probes do not qualify mobile CoreCLR, its interpreter, Apple workload
packaging, or NativeAOT configuration transport. Both Xcode 27 profiles use the
Xcode 27.2 installation, with independent .NET 10 and .NET 11 SDK/workload roots.

## .NET 11 Apple bring-up

The `net11-xcode27` profile selects net11 apps. Its GA pin does not set
`allowPrerelease`; a profile opts into prerelease SDK selection only while its pin is
a prerelease, and the launcher tests enforce that for the committed manifests.
Its Nuke build orchestrator also runs on net11 because the selected SDK's in-process
MSBuild/NuGet assemblies require that runtime. Product libraries and the shipped
generator retain their net10 targets. Keep a .NET 10 runtime in the isolated SDK root
for the net10 global Nuke tool and generator; no global runtime roll-forward is used.

The primary BindingTests apps support `--binding-test-framework net11.0` (implied by
this profile) and `--binding-test-configuration Debug|Release`. Device CoreCLR uses
`--device --core-clr`; `--device` alone retains NativeAOT publish. Mono AOT remains a
net10 lane. CoreCLR and NativeAOT app outputs are separated, and cached binding/app
reuse checks the framework, engine, configuration and toolchain identity.

```sh
bash build.sh --toolchain-profile net11-xcode27 -- binding-tests --macos \
  --class-filter RuntimeEngineIdentityTests --allow-unqualified-runtime
bash build.sh --toolchain-profile net11-xcode27 -- binding-tests --sim \
  --device-udid <simulator-udid> --allow-unqualified-runtime
```

The .NET 11 Apple workloads build only the 27.2 platform version on iOS, macOS, tvOS
and Mac Catalyst; `net11.0-ios27.0` and the other earlier versions fail restore with
NETSDK1140. A versionless `net11.0-<platform>` target therefore generates
`net11.0-<platform>27.2`. The .NET 10 defaults stay at 26.0, which the 27.2 .NET 10
workload still builds.

Both Xcode 27.2 workloads also fail any iOS or Mac Catalyst app whose Info.plist lacks
`UIApplicationSceneManifest` (MT7199): on iOS and macOS 27 such an app crashes at
launch without the UIKit scene lifecycle. Every iOS, tvOS and Mac Catalyst app that the
repository checks in or the gates generate declares a manifest. Apps that start
`UIApplication.Main` register the scene delegate their manifest names. Executables that
never start it declare a manifest with no scene configuration. None disables
`ValidateSceneManifest`. AppKit macOS apps are outside MT7199.

Each .NET 11 lane is graded against its own entry in
`build/baselines/runtime-identity-net11-baseline.json`, keyed by framework, platform, RID,
engine and configuration. A lane without an entry needs `--allow-unqualified-runtime` and
reports UNQUALIFIED even when its functional assertions pass. The .NET 10 identity floors
are never reused or written by this profile.

Seed a Release lane's entry from a fresh, full, green run:

```sh
bash build.sh --toolchain-profile net11-xcode27 -- binding-tests --macos \
  --binding-test-configuration Release --seed-runtime-identity-baseline
```

`--skip-regen` is allowed; `--skip-build` and `--class-filter` are not, and a failing run
writes nothing. `--seed-runtime-identity-baseline` is the only baseline write the launcher
accepts, only on this profile and only for `binding-tests`. Nuke writes the entry in the
disposable workspace. Once Nuke exits successfully, the launcher copies that single file
back to the checkout. It refuses if any other baseline changed in the workspace or if the
checkout's copy changed during the run. Review and commit the result like any other baseline.
A write-back changes the source identity, so the next profile run refreshes the workspace
and must regenerate.
Debug lanes, which run the CoreCLR interpreter, are not seedable until the interpreter
bridge work lands; Nuke rejects a seed request without `--binding-test-configuration Release`.

`--compile-only` runs under the net11 profile. It starts no runtime, so it needs no identity
entry. It generates for and compile-checks `net11.0-ios` (platform 27.2): the BindingTests
library, the ObjC umbrella, and the resilience and ingestion kitchens. Generated C# does not
depend on the target framework, so its sub-gates compare against the same checked-in
baselines as net10: artifact parity, API manifest, skip surface and the kitchen reports. The
overload-name and thunk weak-link gates are policy checks. Every comparison is read-only.

ABI-grid, Apple smoke, ActivityKit push-token and Catalyst x64 JIT gates keep net10 policy
and reject net11 selection:

- The ABI grid grades against the Mono JIT simulator and NativeAOT device runtimes its
  manifest declares. A net11 simulator runs CoreCLR, which has no cell there.
- The Apple smokes regenerate snapshots for .NET 10, and several need credentials.
- The other two exercise Mono-specific behavior.

Partial-success kitchen, mixed-package, SDK-direct, Apple package consumer and App Store
hygiene gates accept the net11 profile. The kitchen compiles its generated C# for
`net11.0-ios` and compares against the same frozen report.

Two focused Nuke gates provide separate evidence:

- `core-clr-canary --core-clr-canary-dotnet <11-only-dotnet> --core-clr-canary-feed <feed>
  --core-clr-canary-runtime-version <version> --core-clr-canary-developer-dir <developer-dir>`
  runs eight desktop processes: Debug/trimmed Release, Safe/Direct, dynamic code
  enabled/disabled. It verifies the loaded CoreCLR image and cold Swift generic,
  conformance, tuple, collection and callback behavior against a hashed Runtime
  package. This does not establish mobile interpreter or Apple bundle qualification.
- `verify-net11-package-consumer --net11-consumer-dotnet <apple-net11-dotnet>
  --net11-consumer-package-cache <retained-net10-package-cache>` restores and compiles
  fresh consumers against the pinned historical fixture packages on all four Apple
  platforms. Set `DEVELOPER_DIR` to the matching Xcode. Actual assets, compiled DLLs,
  native-reference receipts and package hashes are preserved; successful scratch
  caches are removed. This gate does not link or execute an Apple application.
