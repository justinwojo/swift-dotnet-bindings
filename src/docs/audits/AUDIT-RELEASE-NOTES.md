# Release Notes — Audit Remediation

This release lands the results of a large, systematic audit of the Swift → .NET
binding generator and runtime. A read-only audit across the codebase surfaced a
backlog of confirmed defects; a focused ten-session remediation campaign — plus
follow-up cleanup — fixed roughly **104 confirmed bugs**, the bulk of them
crash-class or memory-safety issues that could bite real apps.

Every fix shipped with tests at the layer that actually exercises it (generator
unit tests, runtime marshalling tests, and end-to-end BindingTests on iOS
Simulator/Mono, macOS/CoreCLR, and physical device/NativeAOT), and every change
went through paired AI code review before landing.

---

## Highlights

### Crashes eliminated

- **Exceptions from your C# code no longer crash the process.** When a C#
  delegate you hand to Swift threw — a closure, an `async` callback, a protocol
  method you implement in C#, or a SwiftUI event handler — the exception
  previously unwound straight into native Swift and hard-aborted the app
  (`SIGABRT`) with no diagnostic. Those callback boundaries are now guarded and
  fail gracefully (surfacing a managed fault or routing through the error
  channel) instead of taking the process down.
- **Calling-convention / ABI correctness.** Fixed a family of low-level register
  and struct-layout bugs that could silently return garbage or crash: by-value
  struct returns (indirect-return / `x8`), multi-field struct packing and
  alignment, throwing initializers (error-register placement), `consuming`
  parameters (double-free), and generic protocol-witness ordering.
- **Protocols & existentials (`any P`).** Fixed double-releases, a
  finalizer-thread crash, and a nil-unwrap path. Concrete Swift-class callback
  parameters in C#-implemented protocols no longer crash on first use.
- **Generics & specialization.** Fixed a fixed-size buffer overflow, a
  double-free, and a use-after-free that hit when a class conforms to a generic
  protocol.
- **SwiftUI bridge.** Enums constructed from an unknown raw value now fail
  gracefully instead of trapping; ObjC-bridgeable struct parameters (e.g. `URL`)
  are no longer misread as raw struct bytes.

### Memory leaks fixed

- Closures (nested-closure context boxes, `async` callback handles, and
  frozen-with-reference closures), value returns, and collection elements no
  longer leak.
- Existential collections (`[any P]`, `[K: any P]`) now keep a correct
  retain/release balance — extended in the follow-up work to **nested**
  existential collections (`[[any P]]`, `[K: [any P]]`, `[[K: any P]]`).

### Correctness & naming

- **Generated C# names now line up with the generator's internal dedup/override
  keys**, fixing duplicate-member and missing-member compile errors
  (`CS0111` / `CS1061`) seen on real libraries (e.g. Kingfisher, GRDB), and
  correcting sibling and async-vs-sync method dispatch.
- **Reserved-identifier collisions resolved.** A Swift parameter named like an
  internal synthetic (`self_`, `newValue`, `resultPtr`, …) no longer silently
  breaks the generated wrapper.
- **Parser / type-classification fidelity hardened** against real-world Apple
  framework inputs — typed throws, `@Sendable`, `where ...: AnyObject`,
  NaturalLanguage / Foundation type mapping, and several demangler edge cases.

### Packaging & platform support

- **x64 Simulator / Apple-Silicon Rosetta:** the SwiftUI-bridge and wrapper
  dylibs now build for every requested architecture, fixing `DllNotFound` on
  `iossimulator-x64` and `tvossimulator-x64`.
- The C#/Swift co-gater now recognizes the full set of generated P/Invoke
  shapes, so it no longer over-strips a valid binding.
- xcframework fat-folding is now **atomic and crash-safe** — an interrupted
  build can no longer leave a torn or denied slice behind.

### Test & release-gate trust

- **Purged false "upstream Mono/.NET runtime bug" test skips.** Several tests
  were skipped under the banner of a known runtime issue when the real cause was
  our own generator/runtime bug. A new meta-test hard-requires any such skip to
  actually sit on a Swift calling-convention path, so a mislabeled crash now
  shows up as a failing test instead of a quietly-skipped one.
- Fixed a macOS / Mac Catalyst test-gating bug, and added a build-time error for
  `async void` test methods (which can silently "pass" without running their
  assertions).

### Follow-up cleanup (post-campaign)

- **Generic closure bridge** (the `DatabaseReader.read { … }`-style API) now
  round-trips: fixed a self-register calling-convention mismatch that crashed
  every call, and the class-typed-return buffer handling that crashed on first
  use of the result.
- **Intra-protocol async/sync overloads** — a single protocol declaring both
  `func m()` and `func m() async` — now get distinct dispatch slots instead of
  collapsing into one.
- **Throwing closures that take a by-value struct argument** now compile and
  marshal that argument correctly.
- **Frozen structs with packed sub-word optionals** are now detected and handled
  safely instead of being silently mis-laid-out.

---

## By the numbers

- ~**104** confirmed defects fixed across 10 focused sessions, plus follow-up
  cleanup of the items logged during the campaign.
- End-to-end BindingTests pass counts grew from ~2,553 → **2,723+** on iOS
  Simulator and ~2,568 → **2,735+** on physical device, and unit tests from
  12,223 → **12,509**, as new coverage landed alongside the fixes.
- Coverage runs green on all three runtimes: Mono JIT (Simulator), CoreCLR
  (macOS), and NativeAOT (device).
