# Framework-Linkage Blast-Radius — Findings

## Methodology

Two minimal macOS console apps (net10.0-macos, osx-arm64) compared side-by-side:

| Project | Dependencies | Supplement usage |
|---|---|---|
| `BlastRadius.Baseline` | `Swift.Runtime` only | none |
| `BlastRadius.Consumer` | `Swift.Runtime` + `Swift.Bindings.Apple` | one `typeof(Locale.Language)` reference |

Both publish via `dotnet build -c Release -r osx-arm64`. We inspect:

1. `otool -L` on the Mach-O executable — system dylibs linked.
2. `nm -gU` on the Mach-O executable — exported symbols.
3. `strings | grep swift\|Swift\|SB_` — residual Swift/Apple symbol strings.
4. `find ... -name '*.dylib' -o -name '*.dll'` on the `.app` bundle — shipped artifacts.

Diffs are committed under `measurements/` as regression guards.

**Why not `PublishAot`?** `Swift.Analyzers` (a Roslyn analyzer pulled in transitively) isn't AOT-compatible, and `--self-contained` fails to locate `Microsoft.NETCore.App.Runtime.Mono.osx-arm64` 10.0.3 on nuget.org. The plain-build .app bundle still surfaces the linkage delta we care about, and the macios linker's framework-scanning behavior (the mechanism this smoke targets) is identical across Build and PublishAot.

**Why `osx-arm64` instead of `maccatalyst-arm64`?** Every supplement type in the seeded manifest carries `[UnsupportedOSPlatform("maccatalyst")]`, which makes Catalyst a compile-break. macOS is the closest stand-in with the same macios linker semantics.

**Note on `IncludeSwiftBindingsRuntimeNative=false`**: Both csprojs set this to dodge an `install_name_tool` race that fires when `Swift.Runtime`'s native dylib relocation step runs inside a consumer's `obj/`. Because both sides disable it symmetrically, the comparison remains apples-to-apples; the missing dylib is identical in both bundles.

## Results (Session 3)

### System framework linkage (`otool -L`)

**Zero new framework links.** The `otool-L.diff` shows only the differing binary path. Specifically, `CryptoKit.framework` and `ManagedSettings.framework` — which the pre-fix consumer picked up despite referencing neither — no longer appear in the consumer's link list.

### Exported symbols (`nm -gU`)

**No delta.** Empty diff.

### Swift symbol strings (`strings`)

**One added string**: `SwiftBindings.Apple` (the managed assembly name). No Swift mangled symbols, no `SB_` / `SBW_` wrapper symbols, no Swift runtime entry points beyond what `Swift.Runtime` already pulls in.

### Mach-O executable

Baseline and Consumer Mach-O binaries are **byte-identical** (3,968,168 bytes).

### `.app` bundle delta

| Metric | Baseline | Consumer | Delta |
|---|---|---|---|
| Mach-O executable | 3,968,168 B | 3,968,168 B | **0 B (0.00%)** |
| `.app` bundle total | 127,668,224 B | 127,717,376 B | **+49,152 B (+0.04%)** |
| New files | — | `SwiftBindings.Apple.dll` (~49 KB) | +1 managed assembly |

## Fix mechanism

Pre-fix, the macios linker's `tools/common/Assembly.cs::ComputeLinkerFlags` scanned every referenced assembly's ModuleReferences for strings containing `.framework/` and force-added `-framework X` to the native link line — regardless of whether the P/Invoke was ever called. That scan is why a `Locale.Language`-only consumer picked up `CryptoKit.framework` and `ManagedSettings.framework`: the supplement DllImport strings for the other modules were shaped `/System/Library/Frameworks/CryptoKit.framework/CryptoKit`, and the scanner matched them during link.

The fix, landed in Session 3:

1. **Generator emits bare DllImport library names** — `[DllImport("CryptoKit", …)]` instead of `[DllImport("/System/Library/Frameworks/CryptoKit.framework/CryptoKit", …)]`. Bare names don't contain the `.framework/` substring, so the macios linker's scanner ignores them entirely. (`src/Swift.Bindings/src/AppleTypesManifest/AppleTypesCsEmitter.cs::ResolveLibraryPath`.)
2. **Runtime registers `SwiftFrameworkResolver` via a `[ModuleInitializer]` side-car** — the generator emits a `_AppleSupplementRegistration.cs` that calls `SwiftFrameworkResolver.RegisterForAssembly(...)` at first access. The resolver maps bare names to `/System/Library/Frameworks/{name}.framework/{name}` and `dlopen`s them lazily, only when a supplement P/Invoke is actually invoked.
3. **`SwiftFrameworkResolver.GetSearchPaths` gains `/System/Library/Frameworks/{name}.framework/{name}` as the last fallback**, so the bare-name resolution path finds the framework at runtime. `@rpath`/`@executable_path` candidates still win when present, preserving app-bundle overrides.

Net effect: build-time framework leak eliminated; runtime load behavior preserved.

## Caveats

- **Consumer-reachable supplement types matter, not module reachability.** The `Locale.Language` consumer doesn't touch CryptoKit or ManagedSettings at runtime, so their frameworks are never `dlopen`ed. A consumer that actually calls a CryptoKit supplement type will `dlopen` `CryptoKit.framework` on the first P/Invoke — correct and expected behavior, measured at runtime rather than link time.
- **PublishAot re-measurement remains deferred.** `Swift.Analyzers` isn't AOT-compatible yet. When it is, re-run with `PublishAot=true` to confirm the static-link blast radius stays small under the AOT codegen path. The macios linker change only affects Mach-O link flags; NativeAOT uses the same native linker, so we expect the same result, but it's worth re-measuring when AOT becomes available.
- **Trimming-aware sizing remains deferred.** `PublishTrimmed=true` may shrink the ~49 KB managed assembly further once the supplement is annotated with trimming-friendly attributes.
