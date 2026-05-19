0.11.1 is a focused patch on top of 0.11.0. It unblocks binding generation on Intel Mac hosts and decouples the `SwiftBindings.Apple` supplement from `SwiftBindings.Runtime` minor bumps so a single shipped supplement can ride forward across all 0.x SDK releases.

## Highlights
- **Intel Mac hosts unblocked (#39)** — `SwiftInterfaceParser` is now built for both `arm64` and `x86_64` and `lipo`-merged into a `universal2` binary, so Intel Mac developers can run the generator. `Pack` and `PackGate` independently assert the staged parser is `universal2` so a stale single-arch artifact can't slip through CI. (Full Intel-target support — `x86_64` simulator and `osx-x64` deployment — remains tracked separately in `src/docs/Future/intel-mac-x64-support.md`.)
- **`SwiftBindings.Apple` decoupled from Runtime minor bumps** — The Apple supplement nupkg is always brokered by `SwiftBindings.Sdk` (whose `Sdk.props` injects a bounded Runtime range into consumers), so the supplement's own outbound Runtime dependency was redundant duplication that forced a no-op repack of the supplement on every Runtime minor bump. The supplement's Runtime dep is now floor-only `[X.Y.Z,)` (`RuntimeVersionRange.BuildMinimumOnly`, stamped via `VersionScope.StampSupplementRuntimeRange`), so a single shipped supplement rides forward across all 0.x SDK bumps.

## GitHub issues closed in this release

Closed with this release:

- [#39](https://github.com/justinwojo/swift-dotnet-bindings/issues/39) — "Bad CPU type" error on Intel

## Packages

| Package                  | Version |
|--------------------------|---------|
| SwiftBindings.Runtime    | 0.11.1  |
| SwiftBindings.Sdk        | 0.11.1  |
| SwiftBindings.Templates  | 0.11.1  |
| SwiftBindings.Apple      | 26.2.3  |

`SwiftBindings.Apple` tracks the Apple SDK train independently and is unchanged in this release — that's now an expected outcome rather than a no-op repack. See the [GitHub wiki](https://github.com/justinwojo/swift-dotnet-bindings/wiki) for installation and usage.
