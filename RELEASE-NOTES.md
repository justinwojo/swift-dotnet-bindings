0.12.1 is a small patch over **[SwiftBindings SDK 0.12.0 + Apple 26.2.4](https://github.com/justinwojo/swift-dotnet-bindings/releases/tag/sdk-v0.12.0)** — the large release this builds on. **Start with the 0.12.0 notes for everything new in this train**: real Intel-Mac / x64 RID support, MusicKit / CryptoKit / RealityKit / WorkoutKit / ScreenTime-FamilyControls reach, Foundation `KVO` and `AttributedString`, the KeyPath subsystem foundation, the owned-return ARC cleanup pass, and the `(any Error)?` source-breaking change.

This patch carries a single fix on top of that release.

## Fixes

- **Windows `dotnet restore` no longer silently drops Apple-supplement files (#40)** — The `SwiftBindings.Apple` package's companion xcframework used a 28-character native module name (`SwiftBindingsAppleSupplement`) that appears three times in every packed path (`.../<Module>.xcframework/<slice>/<Module>.framework/Modules/<Module>.swiftmodule/...`). On the universal (`arm64 + x86_64`) slices that pushed the longest `.abi.json` path past Windows' legacy 260-character `MAX_PATH` ceiling, so NuGet silently failed to extract those files during restore — and the binding build then failed for every Windows consumer of the supplement. The native module is renamed to `SBApple`, bringing the worst-case packed path comfortably back under the limit (160 of 166 budgeted characters). Two build-time guards now fail the pack if any packed path would regress past the Windows ceiling again.

The rename is internal to the package: the supplement's public surface — `AttributedString`, `LanguageIdentifier`, `Measurement<T>(value, unit)`, and the `AnyError` reference type — is unchanged, and no source, API, or wire-ABI changes are required. macOS, iOS, Mac Catalyst, and tvOS consumers were unaffected by the bug and are unaffected by the fix.

## Packages

| Package                  | Version |
|--------------------------|---------|
| SwiftBindings.Runtime    | 0.12.1  |
| SwiftBindings.Sdk        | 0.12.1  |
| SwiftBindings.Templates  | 0.12.1  |
| SwiftBindings.Apple      | 26.2.5  |

See the [GitHub wiki](https://github.com/justinwojo/swift-dotnet-bindings/wiki) for installation and usage.
