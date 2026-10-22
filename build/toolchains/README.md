# Apple toolchain qualification profiles

The profiles pin a complete SDK, workload and Xcode pairing. They are qualification
configurations; adding a manifest does not declare that the configuration has passed
the release gates.

| Profile | .NET SDK | Workload set | Xcode |
| --- | --- | --- | --- |
| `net10-xcode26` | 10.0.107 | 10.0.107 | 26.3 |
| `net10-xcode27` | 10.0.402 | 10.0.402.1 | 27.1 |
| `net11-xcode27` | 11.0.100-rc.2.26504.105 | 11.0.100-rc.2.26509.2 | 27.0 |

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

## Isolation and reuse

Each profile owns `artifacts/toolchains/<profile>/`. Its `workspace/` is a disposable
Git checkout containing the current source, including uncommitted changes and new
non-ignored files. Build products from the source checkout are excluded. The profile
has its own NuGet package/cache directories and CLI home. Its temporary directory
lives under `/private/tmp/swift-bindings-tc-*`, outside the repository, so scratch
consumer projects cannot inherit repository build properties. A standalone `global.json`
in that directory pins the same SDK and workload set, even when newer SDKs share the
selected .NET installation.
Expect the first run to restore packages and build both native parser architectures.

`toolchain.json` records SDK/workload identities, Xcode and Swift versions, native SDK
paths/versions and reference-assembly hashes. `source.json` records the source
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
automatically update them. Each profile permits one invocation at a time. Different
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
The RC probe opts into prerelease runtime roll-forward only in generator child
processes; repeat with GA before claiming GA qualification.

It also builds a real net11.0 desktop executable consuming the existing net10.0
Runtime assembly and its production configuration target. Safe and Direct policy
cases both identify CoreCLR, including when dynamic-code support is explicitly
disabled, and execute Swift string round-trips through the committed native runtime.
It also verifies that conflicting Mono/AOT selection and suppressed native
compilation produce the expected configuration diagnostic under the .NET 11 SDK.
Receipts are written to the profile workspace's `artifacts/generator-hosts/` and
`artifacts/runtime-host/`; retain them before changing source/profile inputs.

These probes do not qualify mobile CoreCLR, its interpreter, Apple workload
packaging, or NativeAOT configuration transport. The current .NET 11 RC2 Apple
workload requires Xcode 27.0; installed Xcode 27.1 remains paired with .NET 10.

## .NET 11 Apple bring-up

The `net11-xcode27` profile selects net11 apps and permits the pinned prerelease SDK.
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

New lanes require their own reviewed `runtime-identity-net11-baseline.json` entries.
Until those exist, full bring-up runs require `--allow-unqualified-runtime` and report
UNQUALIFIED even when functional assertions pass. The legacy identity floors are
never reused or seeded by this profile. Compile-only, partial-success kitchen, ABI-grid and Apple smoke gates still retain
net10 policy and reject net11 selection. Mixed-package, SDK-direct, Apple package
consumer and App Store hygiene gates accept the net11 profile.

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
