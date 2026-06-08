0.13.0 is a stability and correctness release. An audit of the generator and runtime surfaced a backlog of confirmed defects, and a focused remediation campaign — plus follow-up cleanup — fixed the bulk of them, most being crash-class or memory-safety bugs that could bite real apps.

Every fix shipped with tests at the layer that exercises it — generator unit tests, runtime marshalling tests, and end-to-end `BindingTests` on iOS Simulator (Mono JIT), macOS (CoreCLR), and physical device (NativeAOT).

## Highlights

* **Exceptions from your C# code no longer crash the process** — When a C# delegate handed to Swift threw (a closure, an `async` callback, a protocol method you implement in C#, or a SwiftUI event handler), the exception previously unwound straight into native Swift and hard-aborted the app with `SIGABRT` and no diagnostic. Those callback boundaries are now guarded and fail gracefully instead of taking the process down.

* **Calling-convention and ABI correctness** — A family of low-level register and struct-layout bugs that could silently return garbage or crash is fixed: by-value struct returns (indirect-return / `x8`), multi-field struct packing and alignment, throwing initializers (error-register placement), `consuming` parameters (double-free), and generic protocol-witness ordering.

* **Real-world libraries that used to break now bind** — Generated C# names now line up with the generator's internal dedup/override keys, clearing the duplicate-member and missing-member compile errors (`CS0111` / `CS1061`) seen on libraries like Kingfisher and GRDB, and correcting sibling and `async`-vs-sync method dispatch.

* **Memory leaks closed across closures, returns, and collections** — Closures, value returns, and collection elements no longer leak, and existential collections (`[any P]`, `[K: any P]`) now keep a correct retain/release balance — extended to nested existential collections (`[[any P]]`, `[K: [any P]]`, `[[K: any P]]`).

* **Mixed ObjC+Swift frameworks pack and run** — A binding that bundles an ObjC companion alongside Swift now links and runs on iOS Simulator (Mono JIT) and physical device (NativeAOT) without the duplicate ObjC-class registration that previously surfaced as "Class X is implemented in both …", and every mixed-framework consumption mode now fails closed rather than silently producing a broken package.

## Crashes and memory safety

* **Callback boundaries are exception-safe** — `[UnmanagedCallersOnly]` closure callbacks, `async` completion callbacks, and C#-implemented protocol methods now catch managed exceptions at the native boundary and surface a managed fault or route through the error channel, instead of unwinding into Swift and aborting.

* **ABI-core codegen** — By-value struct returns, multi-field struct packing/alignment, throwing-initializer error registers, `consuming` parameter ownership (double-free), and generic protocol-witness ordering were all corrected; a box/unbox path that crashed only on device is fixed.

* **Protocols and existentials (`any P`)** — Fixed double-releases, a finalizer-thread crash, and a nil-unwrap path; a protocol-proxy receiver that took a concrete Swift-class parameter no longer `SIGSEGV`s on first use. `Optional<ObjC-rooted>` class returns now adopt the owned `+1` and release on dispose.

* **Generics and specialization** — Fixed a fixed-size buffer overflow, a double-free, and a use-after-free that hit when a class conforms to a generic protocol.

* **Memory leaks** — Nested-closure context boxes, `async` callback handles, and frozen-with-reference closures no longer leak; value-return and existential-collection copy-out paths now destroy the Swift-owned source after copying instead of leaking a retain, and reverse-dispatch receivers that bury an existential leaf inside an `Array` / `Dictionary` / `Set` adopt and release the moved-out `+1` correctly.

## Correctness and library support

* **Name / dedup-key alignment** — Emitted C# names are computed with the same dedup/override key builder the rest of the generator uses, fixing duplicate- and missing-member breaks and correcting sibling and `async`-vs-sync dispatch. The protocol-extension overload key now routes through the canonical builder too, fixing a `CS0111` duplicate-member break on `Optional<class>` protocol-extension defaults (Kingfisher shape).

* **Reserved-identifier collisions** — A Swift parameter named like an internal synthetic (`self_`, `newValue`, `resultPtr`, …) no longer silently breaks the generated wrapper.

* **Ownership-modifier parsing** — `consuming` / `borrowing` funcs written without a leading `public`/`open` keyword (bare protocol requirements, protocol-extension defaults, `@inlinable internal`) are no longer mis-classified as module-internal and degraded to a raw `CallConvSwift` P/Invoke with an `SB0001` `[Obsolete]`; they now emit a proper `@_cdecl` wrapper.

* **Members that used to vanish are kept** — Collections of an ObjC class from any auto-bridged Apple module (e.g. `[AVFoundation.AVAsset]`) now project instead of being silently dropped, and a non-frozen value type with optional sub-word fields no longer loses its own constructors and static factories to an over-eager by-value layout guard.

* **Parser / type-classification fidelity** — Hardened against real-world Apple framework inputs: typed throws, `@Sendable`, `where ...: AnyObject`, NaturalLanguage / Foundation type mapping (`NLLanguage`, `NLTagScheme`), raw-value-enum protocol demotion under module-qualified marker constraints, and several demangler edge cases.

* **SwiftUI bridge** — Enums constructed from an unknown raw value fail gracefully instead of trapping, and ObjC-bridgeable struct parameters (e.g. `URL`) are no longer misread as raw struct bytes.

* **Follow-up cleanup** — The generic closure bridge (the `DatabaseReader.read { … }` shape) now round-trips after a self-register calling-convention mismatch and a class-typed-return buffer crash were fixed; intra-protocol `async`/sync overloads (`func m()` and `func m() async` on one protocol) get distinct dispatch slots; throwing closures taking a by-value struct argument now compile and marshal correctly; and frozen structs with packed sub-word optionals are detected and handled instead of mis-laid-out.

## Packaging and platform

* **x64 Simulator / Apple Silicon Rosetta** — The SwiftUI-bridge and wrapper dylibs build for every requested architecture, fixing `DllNotFound` on `iossimulator-x64` and `tvossimulator-x64`. The C#/Swift co-gater now recognizes the full set of generated P/Invoke shapes (including the `DllImport`/`static extern` form) so it no longer over-strips a valid binding and leaves dangling callers.

* **Mixed ObjC+Swift frameworks** — Packaging hardened so the ObjC companion is surfaced correctly across all consumption modes (single packed `PackageReference`, SDK-direct, and local `ProjectReference`), each mode now fails closed, and the class registers exactly once on iOS Simulator and device.

* **Apple-framework dependency packaging** — Cross-module Apple-framework dependencies pack correctly, and system-framework link declarations are surfaced so force-loaded archives resolve at link time.

* **Crash-safe builds** — xcframework fat-folding is now atomic, so an interrupted build can no longer leave a torn or denied slice behind; the SwiftUI-bridge xcframework is excluded on native macOS, clearing an MT158 error at the consuming-app step.

## Test and release-gate trust

* **False "upstream runtime bug" skips purged** — Several tests were skipped under the banner of a known Mono/.NET issue when the real cause was our own generator or runtime bug. Those tests are restored, the underlying wrong-ABI method is suppressed at its source, and a new meta-test hard-requires any such skip to actually sit on a Swift calling-convention path, so a mislabeled crash now shows up as a failing test instead of a quietly-skipped one.

* **Test-harness gaps closed** — Fixed a macOS / Mac Catalyst test-gating bug, and added a build-time error for `async void` test methods, which can otherwise "pass" without ever running their assertions. A runtime-detected skip attribute replaces a CLI-flag skip that was wrongly suppressing macOS CoreCLR runs.

## Reported issues fixed

* **[#41](https://github.com/justinwojo/swift-dotnet-bindings/issues/41) — `MediaPipeTasksGenAI` failed to generate** — A Swift framework that wraps a C/C++ static-archive engine (here, `MediaPipeTasksGenAIC`) aborted binding generation with an opaque `exited with code 1` and no report. The force-loaded archive pulls in Apple system frameworks and `libc++` that carry no autolink hints, so the wrapper link failed. New `--link-framework` / `--link-library` CLI options (and `<SwiftLinkFramework>` / `<SwiftLinkLibrary>` SDK items) let you declare them, and on a link failure the generator now scans the linker errors and the archive's undefined symbols and prints the exact flags and csproj lines to add — replacing the "Undefined symbols" wall. `MediaPipeTasksGenAI` now binds, links, and runs end-to-end with its embedded inference engine reachable from C#.

* **[#40](https://github.com/justinwojo/swift-dotnet-bindings/issues/40) — Kidoz SDK crashed on delegate callbacks** — When Swift called back into a C#-implemented delegate with a Swift-class argument (e.g. an interstitial-ad callback), the binding reinterpreted the raw Swift pointer as a .NET object and crashed (`SIGSEGV`) on first use, and the mixed ObjC+Swift framework needed two separate bindings whose ObjC classes double-registered. Both are fixed — the protocol-proxy receiver marshals Swift-class arguments correctly, and the framework binds as a single package carrying both the ObjC and Swift layers. The reporter validated a one-off package built on these fixes against the live Kidoz SDK before this release.

## Packages

| Package | Version |
|---------|---------|
| SwiftBindings.Runtime | 0.13.0 |
| SwiftBindings.Sdk | 0.13.0 |
| SwiftBindings.Templates | 0.13.0 |

`SwiftBindings.Apple` is unchanged and stays at `26.2.5` — it declares its Runtime dependency as a floor-only range, so the published supplement rides forward to Runtime 0.13.0 without a republish. See the [GitHub wiki](https://github.com/justinwojo/swift-dotnet-bindings/wiki) for installation and usage.
