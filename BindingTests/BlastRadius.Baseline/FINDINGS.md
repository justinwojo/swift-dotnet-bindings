# Session 5 / M9 — Blast-Radius Smoke Test Findings

## Methodology

Two minimal macOS console apps (net10.0-macos, osx-arm64) compared side-by-side:

| Project | Dependencies | Supplement usage |
|---|---|---|
| `BlastRadius.Baseline` | `Swift.Runtime` only | none |
| `BlastRadius.Consumer` | `Swift.Runtime` + `Swift.Bindings.Apple` | one `typeof(Locale.Language)` reference |

Both publish via `dotnet build -c Release -r osx-arm64`. We inspect:

1. `otool -L` on the Mach-O executable — system dylibs linked.
2. `nm -gU` on the Mach-O executable — exported symbols.
3. `strings | grep swift\|Swift\|SB_` — residual Swift/Apple symbol strings in the binary.
4. `find ... -name '*.dylib' -o -name '*.dll'` on the `.app` bundle — shipped artifacts.

Diffs are committed under `measurements/` so future refactors can see regressions at a glance.

**Why not `PublishAot`?** `Swift.Analyzers` (a Roslyn analyzer pulled in transitively) isn't AOT-compatible, and `--self-contained` fails to locate `Microsoft.NETCore.App.Runtime.Mono.osx-arm64` 10.0.3 on nuget.org. The plain-build .app bundle still surfaces the linkage delta we care about.

**Why `osx-arm64` instead of `maccatalyst-arm64` per the brief?** Every supplement type in the seeded manifest carries `[UnsupportedOSPlatform("maccatalyst")]`, which makes Catalyst a compile-break. macOS is the closest stand-in with the same linker semantics.

**Note on `IncludeSwiftBindingsRuntimeNative=false`**: Both csprojs set this to dodge an `install_name_tool` race that fires when `Swift.Runtime`'s native dylib relocation step runs inside a consumer's `obj/`. Because both sides disable it symmetrically, the comparison remains apples-to-apples; the missing dylib is identical in both bundles.

## Results

### Mach-O executable

Baseline and Consumer Mach-O binaries are **byte-identical in size** (3,968,168 bytes). The managed host executable doesn't change when a supplement type is referenced.

### System framework linkage (`otool -L`)

Consumer links **two additional system frameworks** on top of the 150+ frameworks the baseline already pulls in:

- `CryptoKit.framework`
- `ManagedSettings.framework`

Both are OS-resident (`/System/Library/Frameworks/...`) and add zero bytes to the shipped bundle. Their presence is an artifact of Xamarin.Shared.Sdk's framework-resolver scanning `SwiftBindings.Apple.dll` and matching against the workload's framework database; they would not appear if the supplement had been trimmed to only the frameworks actually referenced by the consumer.

### Exported symbols (`nm -gU`)

**No delta.** The supplement does not export additional Mach-O symbols.

### Swift symbol strings (`strings`)

**One added string**: `SwiftBindings.Apple` (the managed assembly name). No Swift mangled symbols, no `SB_` / `SBW_` wrapper symbols, no Swift runtime entry points beyond what Swift.Runtime already pulls in.

### `.app` bundle delta

| Metric | Baseline | Consumer | Delta |
|---|---|---|---|
| Mach-O executable | 3,968,168 B | 3,968,168 B | **0 B (0.00%)** |
| `.app` bundle total | 127,668,224 B | 127,713,280 B | **+45,056 B (+0.04%)** |
| New files | — | `SwiftBindings.Apple.dll` (~42 KB) | +1 managed assembly |

## Interpretation

The Apple supplement's blast radius at the **binary level** is negligible:

- ~42 KB managed assembly (SwiftBindings.Apple.dll)
- Two additional `FrameworkReference`s that expand to **zero-byte** system dylib entries

This validates the M4 design — VWT-backed opaque storage means the supplement is essentially a thin metadata-and-shim layer; it does not statically bake Swift type payloads into the consumer binary. Each referenced type pays for one managed metadata stub and whatever Swift runtime libraries were already loaded.

## Next steps (deferred to later sessions)

- **Framework-trimming (M10 candidate)**: Teach `_InjectAppleSupplementPrototype` (or the shipped `SwiftBindings.Apple` package) to emit only the `FrameworkReference`s whose modules the consumer actually touches. Today's `+2 frameworks` will grow as the manifest expands to more modules, and most consumers will not need all of them.
- **AOT-publish measurement**: When `Swift.Analyzers` gains AOT support (or is made conditional on non-AOT builds), re-run this measurement with `PublishAot=true` to confirm the static-link blast radius stays small.
- **Trimming-aware sizing**: Re-run with `PublishTrimmed=true` once the supplement is annotated with trimming-friendly attributes, to see if the 42 KB assembly can be trimmed further when only one type is referenced.
