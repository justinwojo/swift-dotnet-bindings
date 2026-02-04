# NativeAOT Investigation for Swift Interop (.NET 10)

_Date: 2026-02-04_

## Scope and context
This investigates whether moving Swift interop paths from Mono JIT to NativeAOT can avoid the three known runtime blockers documented in:

- `src/docs/known-issues-workarounds.md`
- `src/docs/remaining-work.md` (item 18)
- `src/Swift.Runtime/src/Swift/Runtime/TypeMetadata.cs`

---

## A) Does NativeAOT on .NET 10 support `CallConvSwift`?

**Finding:** **Yes, partially**. NativeAOT has explicit Swift ABI handling, but this does **not** imply all Swift interop signatures are supported.

Evidence:

- `CallConvSwift` exists in .NET 10 (`System.Runtime.CompilerServices.CallConvSwift`):
  - <https://learn.microsoft.com/en-us/dotnet/api/system.runtime.compilerservices.callconvswift?view=net-10.0>
- Runtime source search shows `CallConvSwift` references in NativeAOT compiler code paths under `src/coreclr/nativeaot` (for example `ILCompiler.Compiler/.../ApplicationBinaryInterface/SwiftAbi.cs`):
  - <https://source.dot.net/#q=CallConvSwift>

Interpretation:

- NativeAOT is aware of Swift calling convention and has ABI-specific handling.
- However, source references also show signature-validation and marshalling restrictions tied to Swift CC paths. So support exists, but is constrained.

---

## B) Does `[LibraryImport]` handle non-blittable types with `CallConvSwift` differently than `[DllImport]`?

**Finding:** **No clear bypass**. `[LibraryImport]` is unlikely to sidestep the core `CallConvSwift` non-blittable limitation.

Evidence:

- Existing failure in this repo is a runtime limitation: non-blittable types with `CallConvSwift` throw `InvalidProgramException` (documented in `known-issues-workarounds.md`).
- Runtime source search indicates `CallConvSwift` restrictions are enforced in interop/runtime compilation logic, not only in JIT-generated IL stubs:
  - <https://source.dot.net/#q=CallConvSwift>
- `[LibraryImport]` docs do not indicate special support that relaxes Swift CC non-blittable rules:
  - <https://learn.microsoft.com/en-us/dotnet/standard/native-interop/pinvoke-source-generation>

Interpretation (inference):

- `[LibraryImport]` changes **where stubs are generated** (compile-time), but does not automatically change **ABI legality** for Swift CC signatures.
- If the signature remains non-blittable under `CallConvSwift` (e.g., `SafeHandle`, `SwiftOptional<T>`), failures are expected to persist unless signatures are redesigned (e.g., `IntPtr` + manual marshalling/wrappers).

---

## C) Is NativeAOT viable for iOS deployment, and does it help Swift interop?

**Finding:** **Viable for iOS deployment in general; only partially helpful for Swift interop.**

Evidence:

- .NET MAUI documents NativeAOT support for iOS/Mac Catalyst and provides publish workflow:
  - <https://learn.microsoft.com/en-us/dotnet/maui/deployment/nativeaot?view=net-maui-9.0>
- NativeAOT support for iOS/Mac Catalyst workloads was tracked in dotnet/runtime and completed for .NET 9 milestone:
  - <https://github.com/dotnet/runtime/issues/80905>

Interpretation:

- Deployment model is viable.
- NativeAOT may avoid Mono JIT-specific failures (notably the `!ji->async` assertion path), but it does **not** guarantee support for currently invalid Swift CC non-blittable signatures.

---

## D) dotnet/runtime issues/PRs discussing NativeAOT + Swift interop

**Directly verified issue:**

- iOS/Mac Catalyst NativeAOT workload support (closed): <https://github.com/dotnet/runtime/issues/80905>

**Source-level references worth follow-up (from `CallConvSwift` source search):**

- `source.dot.net` search reveals additional linked issue IDs in Swift CC interop generator/runtime paths:
  - <https://source.dot.net/#q=CallConvSwift>

Note: these need targeted triage to separate:
1. NativeAOT-specific interop issues
2. general Swift calling-convention restrictions
3. JSImport/LibraryImport generator-specific gaps

---

## Summary verdict

**Verdict: PARTIALLY VIABLE**

- **Viable:** NativeAOT for iOS deployment.
- **Potentially improved:** Mono-JIT-only crash class (e.g., `jit-info.c:918 !ji->async`) may be avoided.
- **Likely unchanged:** non-blittable `CallConvSwift` restrictions (`SafeHandle`, `SwiftOptional<T>`-style signatures) unless interop signatures are redesigned.

---

## Recommended next steps

1. Build a minimal NativeAOT iOS test app that reproduces the three known failures using current signatures.
2. Run matrix tests for each bug with both `[DllImport]` and `[LibraryImport]` forms:
   - `swift_getExistentialTypeMetadata`
   - non-blittable params/returns with `CallConvSwift`
   - async instance call with `SafeHandle`-backed `self`
3. In parallel, prototype an `IntPtr`-first interop layer for problematic Swift CC signatures to test whether NativeAOT + manual marshalling is stable.
4. File/triage upstream runtime issues with minimal repros that clearly label runtime mode (`Mono JIT`, `Mono AOT`, `NativeAOT`).

