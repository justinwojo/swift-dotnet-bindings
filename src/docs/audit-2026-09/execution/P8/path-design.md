# P8 SDK absolute-path design

September 7, 2026. This design covers only W9-01 path output. The W9-H1 resource graph is a separate P8 slice and is intentionally outside this change.

## Contract and authorities

`_SwiftBindingIntermediateDir` remains the SDK's authority for where generated binding artifacts live. It is derived from `IntermediateOutputPath` after `Microsoft.NET.Sdk` has resolved that property, and it may therefore be either relative to the binding project or already absolute (including `UseArtifactsOutput`/`ArtifactsPath` layouts).

Every path exported from a binding project to another MSBuild project must be absolute. `System.IO.Path.GetFullPath(candidate, MSBuildProjectDirectory)` is the existing SDK normalization authority: it preserves an absolute candidate and resolves a relative candidate against the declaring binding project. The consumer must not prepend its own directory, and path validity must not be inferred from a filename substring.

## Path-site inventory

The demonstrated category has three faulty producers in `src/Swift.Bindings.Sdk/Sdk/Sdk.targets`:

1. `GetSwiftFrameworkSearchPaths` exports the generated wrapper xcframework for a dependent binding's wrapper compile.
2. `GetNativeManifest` exports the generated wrapper xcframework to a `ProjectReference` consumer.
3. `GetNativeManifest` exports the generated bridge xcframework to a `ProjectReference` consumer.

All three currently concatenate `$(MSBuildProjectDirectory)/` with `_SwiftBindingIntermediateDir`. That works for a relative intermediate path and corrupts an absolute one. Declared dependency entries and the native-manifest source entry already use absolute-path transforms. Review found the search-path source entry still exported raw Identity; the final pass normalizes it with existing FullPath item metadata so relative source includes survive the project boundary. SDK-local compile, pack, resource, and `NativeReference` items consume `_SwiftBindingIntermediateDir` in the declaring project and do not cross a project boundary, so they remain unchanged.

The three output expressions duplicate the same normalization decision. A shared eager property would be unsafe because `_SwiftBindingIntermediateDir` can be set or finalized after the SDK import in tests and specialized projects. The bounded refactor is to use the same two-argument `GetFullPath` authority at each emitted item, evaluated in the target when the module name and intermediate directory are current. This removes the invalid prefix rule without changing target ordering or the resource graph.

## Validation matrix

| Case | Command owned by lead | Required assertion |
| --- | --- | --- |
| Absolute intermediate, wrapper manifest | Focused `SdkTargetsBehaviorTests` | Returned item equals the wrapper directory's normalized full path and `Directory.Exists` is true; the static source is absent. |
| Absolute intermediate, bridge manifest | Focused `SdkTargetsBehaviorTests` | Returned item equals the bridge directory's normalized full path and `Directory.Exists` is true. |
| Relative intermediate control | Focused `SdkTargetsBehaviorTests` | Wrapper and bridge outputs resolve against the binding project, equal the created directories, and exist. |
| `UseArtifactsOutput`/absolute `ArtifactsPath` control | Focused `SdkTargetsBehaviorTests` | SDK-derived intermediate output produces existing wrapper and bridge manifest paths without a project-directory prefix. |
| Wrapper framework-search output | Focused `SdkTargetsBehaviorTests` | Absolute and relative intermediate forms both return the exact existing wrapper path. |
| `ProjectReference` consumer | Focused `SdkTargetsBehaviorTests`, or the repository's SDK consumer gate if workload behavior cannot be represented without it | A consumer querying its referenced binding receives the exact existing wrapper/bridge paths. |

The lead owns Nuke, builds, regeneration, and shared consumer gates. Focused test execution and any workload-dependent ProjectReference result must be recorded by the lead. This slice cannot establish clean/incremental resource readiness, package consumption, simulator loading, or Bundle.module behavior.
