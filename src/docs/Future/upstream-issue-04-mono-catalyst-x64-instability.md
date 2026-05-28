# [Mono] Mac Catalyst x86_64 (Rosetta on Apple Silicon) — runtime instability with Swift `CallConvSwift` + sync managed throws

> Standalone bug report for filing against [dotnet/runtime](https://github.com/dotnet/runtime/issues). Project: [swift-dotnet-bindings](https://github.com/justinwojo/swift-dotnet-bindings). Repro: [swift-interop-repro](https://github.com/justinwojo/swift-interop-repro) — *new minimal repro pending*.

## Title

`[Mono] Mac Catalyst x86_64 workload runtime crashes during Swift interop tests that pass on osx-x64 (same code, same Mono workload, same Rosetta)`

## Labels

`area-Interop-Swift`, `os-maccatalyst`, `bug`, `runtime-mono`, `arch-x64`

## Description

**Environment:**
- .NET 10.0 (10.0.100), Mono workload (26.2.10233)
- Apple Silicon (M1 Pro) running x86_64 binaries under Rosetta
- Mac Catalyst x64 (`net10.0-maccatalyst`, RID `maccatalyst-x64`) only
- osx-x64 (`net10.0-macos`, RID `osx-x64`) on the same machine, same Mono workload, same Rosetta layer: passes cleanly
- Microsoft.iOS.Sdk 26.2.10233, Xcode 26.2 / Swift 6.2.3
- macOS 26.2

**Summary:**

A binding generator that emits C# P/Invokes against Swift libraries via `CallConvSwift` (plus clang-`swiftcall` C wrappers for a handful of stdlib generic-collection mutating operations) runs the BindingTests test suite (1960+ tests covering ARC, structs, classes, enums, protocols, existentials, async, error throws, etc.) cleanly on:

- iOS Simulator arm64 (Mono)
- iOS device arm64 (NativeAOT)
- Mac Catalyst arm64 (Mono)
- macOS arm64 (Mono)
- **osx-x64 (Mono x64 workload under Rosetta on Apple Silicon) — 1913 pass / 0 fail / 0 crash / 47 skip**

On **`maccatalyst-x64` (Mono x64 workload under Rosetta on Apple Silicon)**, the same exact build crashes with **at least four distinct, deterministic per-ordering crash classes** at unrelated call sites:

1. **First crash** — sync managed `throw` after an out-error-pointer P/Invoke (`SwiftMarshal.ThrowSwiftError` → `throw new SwiftException(...)` inside `try { } finally { releaseError(...) }`). Fault is inside `mono_handle_exception_internal` (NULL deref during exception unwinder traversal). Crashes deterministically at test #387 (`BasicThrowingTests.TestDivideByZeroThrows`). Inserting trivial pure-managed warmup `throw`s earlier in the suite pushes this crash past the warmup point.

2. **Second crash** — JIT-compiled wrapper for the auto-generated 2-arg trim overload of an async-throwing instance method (`DefaultedAsyncRoster.AppendOrThrowAsync(source, shouldThrow)`). Fault is in JITted code, fault address near null (observed values: `0x2`, `0x4`, `0x44`) via a `cmp [rax], al` instruction with rax holding a near-null pointer. Hits both call sites of the 2-arg trim variant — `TrimDropsBoth_FillsSwiftDefaults` and `TrimDropsBoth_ThrowsFaultsTask`. The 3-arg primary `AppendOrThrowAsync(source, shouldThrow, options)` on the same fixture passes. The no-throws 2-arg trim `AppendAsync(source)` also passes. The crash is specific to the **async + throws + trim-overload** combination on this fixture.

3. **Third crash** — **JIT compile-time SIGABRT with heap corruption**. Triggered by the first invocation of `Pipeline.GetStepCount()` (an instance method on `Pipeline` taking an existential proxy). Stack: `libsystem_malloc nanov2_guard_corruption_detected` → `monoeg_malloc` → `mono_metadata_parse_type_internal` → `mono_metadata_parse_mh_full` → `mono_method_to_ir` → `mono_jit_compile_method`. The JIT corrupts the heap while parsing the method's IL header on first compile. Unlike crashes 1 and 2 (JIT-output bugs), this is a JIT compiler-internal bug — Mono's metadata parser writes past the end of an allocation during method-header parsing.

4. **Fourth crash** — runtime SIGSEGV inside `mono_class_from_name_checked_aux` invoked from a generated SwiftBindings cdecl wrapper (`SBW_SwiftBindingsTestLib_Pipeline_getModeName` → `_sbw_method_E1DF0542`). Triggered by `Pipeline.GetModeName()` on the next existential-proxy test (`TestPipelineGetModeName`) after the third-crash test is skipped. The Swift wrapper calls into Mono's metadata API for class lookup, and Mono crashes during that lookup with a null deref. Possibly the same root cause as the third crash (corrupted Mono internal data structures) surfacing through a different code path.

All four crashes are **independent** — each has a different fault site, a different fault address, and a different managed/native stack — but each is timing/state-dependent (deterministic per suite ordering). Iteratively skipping the trigger test for each class reveals the next class one position deeper into the suite, demonstrating that the maccatalyst-x64 Mono runtime has fundamental instability rather than a single localized bug.

The crashes are **deterministic per ordering**: running the exact same suite a second time crashes at the same test. They are **specific to the maccatalyst-x64 Mono workload runtime** — identical generated C# bindings, identical native dylibs, identical Mono x64 workload version (26.2.10233), and identical Rosetta translation layer pass cleanly on osx-x64.

**Iron-clad evidence chain:**

| Run target | RID | Mono workload | Rosetta? | Result |
|---|---|---|---|---|
| osx-arm64 | osx-arm64 | net10.0-macos | No | 1906 pass / 0 crash |
| osx-x64 | osx-x64 | net10.0-macos | Yes | **1913 pass / 0 crash** |
| maccatalyst-arm64 | maccatalyst-arm64 | net10.0-maccatalyst | No | 1909 pass / 0 crash |
| maccatalyst-x64 (no warmup) | maccatalyst-x64 | net10.0-maccatalyst | Yes | **386 pass, SIGSEGV in `mono_handle_exception_internal` at `BasicThrowingTests.TestDivideByZeroThrows` (crash class 1)** |
| maccatalyst-x64 (managed-throw warmup) | maccatalyst-x64 | net10.0-maccatalyst | Yes | **914 pass, SIGSEGV in JITted `DefaultedAsyncRoster.AppendOrThrowAsync` trim variant (crash class 2)** |
| maccatalyst-x64 (warmup + 1 skip for crash class 2) | maccatalyst-x64 | net10.0-maccatalyst | Yes | **915 pass, SIGSEGV in next 2-arg trim variant (`ThrowsFaultsTask`) — same crash class 2** |
| maccatalyst-x64 (warmup + 2 skips for class 2) | maccatalyst-x64 | net10.0-maccatalyst | Yes | **1060 pass, SIGABRT heap corruption in `mono_metadata_parse_type_internal` during JIT compile of `Pipeline.GetStepCount` (crash class 3)** |
| maccatalyst-x64 (warmup + 2 + 1 skip for class 3) | maccatalyst-x64 | net10.0-maccatalyst | Yes | **1060 pass, SIGSEGV in `mono_class_from_name_checked_aux` from generated SBW wrapper for `Pipeline.GetModeName` (crash class 4)** |
| **maccatalyst-x64 (Mono interpreter — `MtouchInterpreter=all`)** | maccatalyst-x64 | net10.0-maccatalyst | Yes | **1917 pass / 0 fail / 47 skip / 0 crash, `Done=True`** |

The osx-x64 row demonstrates that the Mono x64 workload binaries themselves are **not** broken — they handle the entire test suite (including sync `throw`s, async-throw faults, and the AppendOrThrowAsync trim variant) cleanly under Rosetta. The maccatalyst-x64 row uses the **same Mono x64 workload version** and the **same Rosetta layer**; only the Microsoft.iOS.Sdk Catalyst-specific runtime path differs.

The final row is the key resolution: switching the maccatalyst-x64 app to the Mono **interpreter** (via `<MtouchInterpreter>all</MtouchInterpreter>` + `<UseInterpreter>true</UseInterpreter>` on the iOS workload SDK that Catalyst inherits) bypasses **all four** crash classes end-to-end. The interpreter never enters the JIT subsystem, and all four crashes are inside JIT codegen, JIT-output, the JIT compile-time metadata parser, or class-lookup state populated by JIT-driven loads. This isolates the bug subset cleanly to the **JIT path** of the maccatalyst-x64 Mono runtime build.

**Workaround in use (resolution):**

For our local gate, the maccatalyst-x64 cell **defaults to Mono interpreter mode** (`MtouchInterpreter=all` + `UseInterpreter=true`), set automatically by the nuke `binding-tests` target when `--catalyst-x64` is selected. The `--catalyst-x64-jit` flag opts back into the JIT path for future upstream-fix verification (expect crashes until upstream lands fixes). The `.validation-baseline.json` `maccatalyst_x64` entry now ratchets to `pass=1917, fail=0, skip=47, crash=0`.

Defensive workarounds left in tree (helpful for future `--catalyst-x64-jit` probes and a no-op under interpreter):

- `SwiftMarshal.ThrowSwiftError` (`src/Swift.Runtime/.../InteropServices/SwiftMarshal.cs`) releases the Swift error pointer **before** the managed `throw new SwiftException(...)` rather than via a `finally` wrapping the throw. Avoids the "throw inside try/finally with a P/Invoke in the cleanup block" shape, which is fragile under any Mono x64 unwinder.
- `BasicSyncThrowProbeTests` (4 pure-managed-throw probes inserted alphabetically before `BasicThrowingTests`) — original warmup for class 1; kept as scaffolding for `--catalyst-x64-jit` verification runs and as a generic Mono x64 warmup probe.
- `SkipOnCatalystX64` attribute infrastructure (`BindingTests/RuntimeTestsApp/Infrastructure/TestResults.cs`) plumbed through `TestDiscoveryGenerator` + `TestBase`, runtime-detected via `OperatingSystem.IsMacCatalyst() && RuntimeInformation.ProcessArchitecture == Architecture.X64`. Zero call sites today (all 3 prior-annotated tests pass cleanly under interpreter), but retained as a generic RID-specific escape hatch.

Other RIDs are unaffected: `--catalyst` (arm64) still uses the JIT and remains green at 1909 / 0 / 0; iOS Simulator, iOS device (NativeAOT), tvOS, osx-arm64, osx-x64 all continue to execute the full suite as before.

**Discriminator-probe details:**

The `BasicSyncThrowProbeTests` class (4 tests of pure-managed `throw`/catch with various try/finally shapes, no Swift P/Invoke) was inserted alphabetically between `BasicProtocolDispatchTests` and `BasicThrowingTests`. On maccatalyst-x64:

- All 4 probe tests pass cleanly.
- The subsequent `BasicThrowingTests.TestDivideByZeroThrows` then also passes (where without the probes it crashes).
- The first crash is shifted to test ~#920 (different class, different fault, different stack).

Without `BasicSyncThrowProbeTests`, the same `BasicThrowingTests.TestDivideByZeroThrows` test passes in `--class-filter BasicThrowingTests` isolation — only the suite ordering matters.

**Expected behavior:**

`net10.0-maccatalyst` running under Rosetta on Apple Silicon should behave identically to `net10.0-macos` running under Rosetta on Apple Silicon for the same managed code and the same native interop dylibs.

## Next steps for filing

1. **Reduce to a minimal repro** — extract the smallest sequence that triggers the first crash on `maccatalyst-x64` while passing on `osx-x64`. Candidates:
   - A bare `[DllImport]` cdecl P/Invoke that returns an int + `out IntPtr errorPtr`, followed by a managed `throw new Exception("...")` based on the error pointer, in a `try { } finally { /* NativeMemory.Free */ }` shape.
   - The same call placed after a long alphabetical prefix of test classes that exercise `[SuppressGCTransition]` ARC P/Invokes (`swift_retain`, `swift_isDeallocating`, `swift_unownedRetain`).
2. Publish the minimal repro to `swift-interop-repro` as a new `Issue4_MonoCatalystX64Instability` class.
3. Confirm the repro reproduces on a clean .NET SDK / iOS workload install.
4. File against `dotnet/runtime` with the `os-maccatalyst` + `arch-x64` + `area-Interop-Swift` labels.

## Suspected upstream component

The differential between osx-x64 (passes) and maccatalyst-x64 (crashes) under identical Mono workload + Rosetta isolates the problem to the **Mac Catalyst-specific Mono x64 runtime build** (or its interaction with the iOS-derived Foundation / UIKit runtime that maccatalyst uses but plain osx does not). Possible suspect areas:

- Mono LMF (Last Managed Frame) emission for `[SuppressGCTransition]` P/Invokes on the maccatalyst-x64 codegen path (cf. [dotnet/runtime#122958](https://github.com/dotnet/runtime/issues/122958), which reports a similar shape on a different platform).
- Mono x64 SysV / SwiftCC trampoline state on the maccatalyst codegen path.
- Mono signal-handler / unwinder interaction with the iOS-derived signal stack on maccatalyst-x64 under Rosetta.
- Mono metadata-parser heap accounting on the maccatalyst-x64 runtime build (crash class 3's `nanov2_guard_corruption_detected` strongly suggests an out-of-bounds write inside `mono_metadata_parse_type_internal`).
- Mono runtime-handle / class-name lookup table state on the maccatalyst-x64 runtime build (crash class 4's `mono_class_from_name_checked_aux` SIGSEGV invoked from a `_sbw_*` Swift wrapper). May share a root cause with class 3 — both touch Mono internal data structures.

The progression of distinct crash classes as each is patched out (crash class 1 → 2 → 3 → 4, each surfacing at a deeper test position) supports a model where the maccatalyst-x64 Mono runtime build has multiple latent bugs rather than a single localized fault. A bisect between `net10.0-macos` and `net10.0-maccatalyst` Mono builds may identify the missing patch(es) on the Catalyst codegen / metadata path.

## Impact

`maccatalyst-x64` is a secondary x64 deployment target — Apple's recommended path is `maccatalyst-arm64`, with x64 needed only for legacy Intel Mac deployment. As of 2026-05-27, our gate has a clean workaround in place: the maccatalyst-x64 cell runs under the **Mono interpreter** (`MtouchInterpreter=all` + `UseInterpreter=true`) by default, which bypasses all four crash classes by skipping the JIT subsystem entirely. The suite passes 1917 / 0 / 47 / 0 (`Done=True`), matching osx-x64's footprint.

However, **the JIT bugs are real and should be fixed upstream**. Without an upstream fix, any consumer of `dotnet/runtime` Mono x64 on Mac Catalyst who wants the JIT (for startup-time / steady-state perf reasons rather than interpreter mode) will hit the same four crash classes. The interpreter workaround is acceptable for a test gate but not free in production deployments. The filing remains useful so the JIT path becomes viable on this RID.
