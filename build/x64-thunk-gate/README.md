# x86_64 (SysV) thunk-backend gate

Durable, opt-in gate that proves the generator's **x86_64 thunk backend** emits
an ABI-correct `cdecl → swiftcc` bridge, verified by running real round-trips
under Rosetta. It is the committed replacement for the throwaway `/tmp` spike
used while bringing the backend up.

## What it covers

`Fixture.swift` exercises every corner the x86_64 thunks must get right:

| Member | ABI surface |
|---|---|
| `Counter.init(start:)` | constructor + metatype accessor, returns instance |
| `Counter.addAndGet(_:)` | instance method, `self` in `%r13` |
| `Counter.snapshot(scale:)` | `>16B` mixed int/float return-by-value → field-wise return-store bridge |
| `Counter.checkedAdd(_:)` | throwing instance method, error in `%r12` (swifterror) + out-param writeback |
| `Counter.origin()` | static method (metatype accessor, no `self`) |
| `Counter.makeMixed(_:)` | static method + mixed-width struct return |

`Mixed { Int32; Float; Int64; Double }` (24 bytes) is **`@frozen` on purpose**:
only frozen 17–32B structs returned by value reach the register-return-bridge
path. A resilient (non-`@frozen`, library-evolution) struct becomes an
opaque-payload class returned indirectly and would bypass the very code this
gate exists to prove.

## What it does *not* cover

The driver P/Invokes the generated `thunk_*` symbols **directly** with manual
`cdecl` `[DllImport]` declarations — it deliberately does not depend on
`Swift.Runtime`. The full idiomatic generated-bindings path
(`TypeMetadata`/`SwiftObjectHelper`/ARC) on x86_64 is a separate, later-session
runtime concern and is intentionally out of scope here.

## How it works

`nuke X64ThunkGate` (see `build/Build.X64ThunkGate.cs`):

1. Rebuilds the generator in **Debug** (the gate calls the Debug dll directly).
2. Builds `Fixture.swift` for `x86_64-apple-macos` → framework → xcframework.
3. Runs the generator (`--platform macos`) → emits `FixtureLib.x86_64.s` thunks,
   the C# bindings, and the Swift `@_cdecl` wrapper.
4. Asserts the expected thunk count, assembles the thunks (`clang -target x86_64…`),
   and links them + the wrapper into `libFixtureLibSwiftBindings.dylib`.
5. `nm`-asserts every emitted thunk symbol is exported.
6. Derives each method's `thunk_*` EntryPoint from the generated `FixtureLib.cs`
   (no hard-coded FNV hashes) and writes `ThunkSymbols.g.cs` for the driver.
7. Publishes the driver self-contained for `osx-x64`, drops the wrapper dylib +
   `FixtureLib.framework` next to it, and runs it under `arch -x86_64`.
8. The driver asserts every round-trip and exits non-zero on any mismatch.

All build output lands in `artifacts/x64-thunk-gate/` (gitignored).

## Running

```bash
nuke X64ThunkGate
```

Requires a macOS host with the Swift toolchain and Rosetta. Not part of
`nuke test` / `nuke binding-tests` (it needs the macOS SDK + Rosetta and runs
~30–60s).
