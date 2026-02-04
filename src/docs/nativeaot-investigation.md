# NativeAOT Investigation for Swift Interop (.NET 10)

_Date: 2026-02-04_
_Updated: 2026-02-04 (AI model consultation — Grok, Gemini)_

## Scope and context
This investigates whether moving Swift interop paths from Mono JIT to NativeAOT can avoid the three known runtime blockers documented in:

- `src/docs/known-issues-workarounds.md`
- `src/docs/remaining-work.md` (item 18)
- `src/Swift.Runtime/src/Swift/Runtime/TypeMetadata.cs`

---

## Blocker-by-blocker analysis

### Blocker 1: Mono JIT assertion crash (`!ji->async` at `jit-info.c:918`)

**Verdict: BYPASSED by NativeAOT** (Grok and Gemini agree)

NativeAOT compiles to native code via ILCompiler — there is no JIT, so the `jit-info.c` assertion path does not exist. The `!ji->async` failure is definitively Mono-specific.

Evidence:
- NativeAOT has its own Swift ABI handling in `ILCompiler.Compiler/.../ApplicationBinaryInterface/SwiftAbi.cs` — completely separate from Mono's JIT stack walker
- `CallConvSwift` exists in .NET 10 and NativeAOT is aware of it: <https://source.dot.net/#q=CallConvSwift>
- Gemini cited PR #105630 as implementing Swift ABI in ILCompiler — **verified as hallucinated** (actual PR is unrelated JIT IR validation)

**Impact**: Under NativeAOT, `swift_getExistentialTypeMetadata` and similar Swift runtime functions can be called directly via P/Invoke with `CallConvSwift` without Swift wrapper functions. This would eliminate the need for `SBW_createExistentialArray`-style Swift wrappers for existential type metadata construction.

---

### Blocker 2: Non-blittable types rejected with `CallConvSwift`

**Verdict: PERSISTS under NativeAOT** (Grok confident; Gemini partially disagrees)

The restriction on non-blittable types in `CallConvSwift` P/Invoke signatures is enforced in NativeAOT's ILCompiler, not just Mono's JIT. No .NET 10/11 PRs have been identified that relax this restriction.

**Grok's analysis** (high confidence):
- The validation happens at compile-time in `SwiftAbi.cs` and `CallConvSwiftValidator.cs` within ILCompiler
- `[LibraryImport]` produces faster stubs and is NativeAOT-friendly, but handles `SwiftSelf`/`SwiftIndirectResult`/`SwiftError` identically to `[DllImport]`
- Non-blittable types face the same rejection regardless of import attribute

**Gemini's analysis** (partial disagreement):
- Suggests `[LibraryImport]` source generation could marshal non-blittable types into blittable types _before_ the P/Invoke call, satisfying NativeAOT's checks
- Recommends `CustomMarshaller` for complex types like `SwiftOptional<T>` to define blittable memory layouts
- Claims Microsoft is moving toward `[LibraryImport]` as the intended .NET 10 interop strategy

**Assessment**: Grok's analysis is more architecturally grounded. `[LibraryImport]` changes _where_ stubs are generated (compile-time vs runtime) but does not change ABI legality for the underlying native call. If the final native call signature remains non-blittable under `CallConvSwift`, failures persist. However, Gemini's `CustomMarshaller` suggestion is worth investigating — if source-generated marshalling can produce an intermediate blittable signature that the runtime accepts, it could be a viable path for types like `SwiftOptional<T>`.

**Action item**: Test whether `[LibraryImport]` with a `CustomMarshaller` that lowers `SwiftOptional<T>` to a blittable layout can bypass the `InvalidProgramException` under NativeAOT.

---

### Blocker 3: SafeHandle not preserved across async P/Invoke

**Verdict: UNCERTAIN — testing required** (Grok and Gemini disagree)

**Grok's analysis** (conservative):
- Not purely Mono-specific — the root cause is GC pinning across Task continuations combined with Swift ARC mismatch
- NativeAOT improves async state machines but does not auto-pin handles across continuations
- Referenced `dotnet/runtime#109234` as NativeAOT async pinning bugs — **verified as hallucinated** (actual item is a WASM LibraryImport PR)

**Gemini's analysis** (optimistic):
- Claimed PR #106504 adds `SwiftAsyncContext` async state machine support — **verified as hallucinated** (actual PR is an unrelated diagnostic method fix)
- The architectural argument (NativeAOT generates better async state machines) may still hold, but has no confirmed PR backing it

**Assessment**: These claims need hands-on verification. The architectural argument (NativeAOT generates better async state machines than Mono JIT) is plausible, but whether it specifically solves SafeHandle lifetime across Swift async suspension points is unproven. Our existing workaround (manual ARC retain/release around async calls) remains the safe path regardless.

**Action item**: Build a minimal NativeAOT iOS test that calls an async Swift instance method via `SwiftSelf` + `SafeHandle` to see if the handle survives the suspension point.

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

**Finding:** **No clear bypass for the underlying native call**, but `CustomMarshaller` may offer a workaround path.

Evidence:

- Existing failure in this repo is a runtime limitation: non-blittable types with `CallConvSwift` throw `InvalidProgramException` (documented in `known-issues-workarounds.md`).
- Runtime source search indicates `CallConvSwift` restrictions are enforced in interop/runtime compilation logic, not only in JIT-generated IL stubs:
  - <https://source.dot.net/#q=CallConvSwift>
- `[LibraryImport]` docs do not indicate special support that relaxes Swift CC non-blittable rules:
  - <https://learn.microsoft.com/en-us/dotnet/standard/native-interop/pinvoke-source-generation>
- Grok confirms `SwiftSelf`/`SwiftIndirectResult`/`SwiftError` are handled identically between `[LibraryImport]` and `[DllImport]`
- Microsoft is shifting toward `[LibraryImport]` generally but has no Swift-specific guidance

Interpretation:

- `[LibraryImport]` changes **where stubs are generated** (compile-time), but does not automatically change **ABI legality** for Swift CC signatures.
- If the signature remains non-blittable under `CallConvSwift` (e.g., `SafeHandle`, `SwiftOptional<T>`), failures are expected to persist unless signatures are redesigned (e.g., `IntPtr` + manual marshalling/wrappers).
- **Open question**: Can `[LibraryImport]` with `[MarshalUsing(typeof(CustomMarshaller))]` produce a blittable intermediate signature that satisfies the `CallConvSwift` validator? If so, this could automate what we currently do manually with `IntPtr` + `SBW_Utf8Slice`.

---

## C) Is NativeAOT viable for iOS deployment, and does it help Swift interop?

**Finding:** **Viable for iOS deployment in general; partially helpful for Swift interop.**

Evidence:

- .NET MAUI documents NativeAOT support for iOS/Mac Catalyst and provides publish workflow:
  - <https://learn.microsoft.com/en-us/dotnet/maui/deployment/nativeaot?view=net-maui-9.0>
- NativeAOT support for iOS/Mac Catalyst workloads was tracked in dotnet/runtime and completed for .NET 9 milestone:
  - <https://github.com/dotnet/runtime/issues/80905>

Interpretation:

- Deployment model is viable.
- NativeAOT definitively avoids Mono JIT-specific failures (the `!ji->async` assertion path).
- Non-blittable `CallConvSwift` restrictions likely persist unless signatures are redesigned.
- Async SafeHandle behavior needs hands-on testing.

---

## D) dotnet/runtime issues/PRs discussing NativeAOT + Swift interop

### Verified issues

| Issue | Status | Notes |
|-------|--------|-------|
| [#93631](https://github.com/dotnet/runtime/issues/93631) | Closed (Delivered) | Swift interop .NET 9 — basic `CallConvSwift` shipped |
| [#108662](https://github.com/dotnet/runtime/issues/108662) | Open | Swift interop .NET 10 — NativeAOT validation strictness noted |
| [#64215](https://github.com/dotnet/runtime/issues/64215) | Closed (Merged) | Introduce `CallConvSwift` — foundation work |
| [#96059](https://github.com/dotnet/runtime/issues/96059) | Closed | Swift into .NET (opposite direction — `UnmanagedCallersOnly`) |
| [#100543](https://github.com/dotnet/runtime/issues/100543) | Closed | `SwiftSelf<T>` and `SwiftIndirectResult` — basic support; async gaps noted |
| [#80905](https://github.com/dotnet/runtime/issues/80905) | Closed (Delivered) | NativeAOT iOS/Mac Catalyst — full support in .NET 9+ |

### Hallucinated claims from AI models (verified as incorrect)

These PR/issue numbers were cited by Grok and Gemini but are **confirmed unrelated** to Swift interop. This is a known limitation of LLM research — specific numbers are often fabricated.

| Reference | Source | Claimed | Actual |
|-----------|--------|---------|--------|
| PR [#105630](https://github.com/dotnet/runtime/pull/105630) | Gemini | NativeAOT Swift ABI handling in ILCompiler | "More IR validations in HIR" — JIT IR validation, not Swift-related |
| PR [#106504](https://github.com/dotnet/runtime/pull/106504) | Gemini | `SwiftAsyncContext` async state machine support | "Update MethodTable::IsDynamicStatics" — diagnostic method fix, not Swift-related |
| Issue [#109234](https://github.com/dotnet/runtime/issues/109234) | Grok | NativeAOT async pinning bugs | Actually a PR: "Add forwarding support for WasmLinkage on LibraryImport" — WASM interop, not Swift-related |

**Conclusion**: The _architectural reasoning_ from both models is sound (NativeAOT bypasses JIT code paths, `[LibraryImport]` generates compile-time stubs), but specific PR citations should never be trusted without manual verification. The blocker-by-blocker verdicts above rely on architectural analysis, not these fabricated references.

**Source-level references worth follow-up (from `CallConvSwift` source search):**

- `source.dot.net` search reveals additional linked issue IDs in Swift CC interop generator/runtime paths:
  - <https://source.dot.net/#q=CallConvSwift>

Note: these need targeted triage to separate:
1. NativeAOT-specific interop issues
2. general Swift calling-convention restrictions
3. JSImport/LibraryImport generator-specific gaps

---

## Summary verdict

**Verdict: PARTIALLY VIABLE — one definitive win, two unknowns**

| Blocker | Mono JIT | NativeAOT | Confidence |
|---------|----------|-----------|------------|
| #1 `!ji->async` assertion | Crashes | **Bypassed** (no JIT) | High (both models agree) |
| #2 Non-blittable `CallConvSwift` | `InvalidProgramException` | **Likely persists** (ILCompiler enforces same restriction) | Medium (models disagree on `CustomMarshaller` path) |
| #3 SafeHandle across async | GC collects handle | **Uncertain** (may improve, needs testing) | Low (models disagree) |

**Bottom line**: NativeAOT eliminates the most severe blocker (existential metadata crash) and is viable for iOS deployment. The other two blockers require the same workarounds we already have (`IntPtr` + manual marshalling, `SBW_Utf8Slice`, manual ARC retain/release). NativeAOT is worth pursuing as the deployment target, but is not a silver bullet for all interop limitations.

---

## Recommended next steps

1. **Verify unconfirmed PR/issue numbers** — Check dotnet/runtime for PRs #105630, #106504, and issue #109234. If they exist and are relevant, update this document.
2. **Build a minimal NativeAOT iOS test app** that reproduces the three known failures using current signatures.
3. **Run matrix tests** for each bug with both `[DllImport]` and `[LibraryImport]` forms:
   - `swift_getExistentialTypeMetadata` (expect: passes under NativeAOT)
   - non-blittable params/returns with `CallConvSwift` (expect: still fails)
   - async instance call with `SafeHandle`-backed `self` (expect: unknown)
4. **Test `[LibraryImport]` + `CustomMarshaller`** for `SwiftOptional<T>` — can source-generated blittable lowering bypass the `CallConvSwift` restriction?
5. **Prototype `IntPtr`-first interop layer** for problematic Swift CC signatures to test whether NativeAOT + manual marshalling is stable.
6. **File/triage upstream runtime issues** with minimal repros that clearly label runtime mode (`Mono JIT`, `Mono AOT`, `NativeAOT`).

---

## Research methodology

This investigation combines documentation review, source code analysis, and consultation with external AI models:

- **Initial research** (2026-02-04): Web searches of Microsoft documentation, dotnet/runtime source, and issue tracker
- **Grok consultation** (2026-02-04, `grok-4-1-fast-reasoning`): Focused on architectural analysis of NativeAOT vs Mono code paths. Conservative assessment — high confidence on Blocker #1, cautious on #2 and #3.
- **Gemini consultation** (2026-02-04, `gemini-3-flash-preview`): More optimistic assessment, citing specific PRs for Swift ABI and async context support. All three cited PR/issue numbers were verified as hallucinated (unrelated to Swift interop).
- **PR/issue verification** (2026-02-04): Manually checked all three AI-cited references (#105630, #106504, #109234) via GitHub — none are related to Swift interop. Architectural reasoning from both models is sound; specific citations are not.

Areas of agreement between models:
- Blocker #1 is definitively Mono-specific and bypassed by NativeAOT
- NativeAOT is viable for iOS deployment (.NET 9+)
- `[LibraryImport]` handles Swift-specific types (`SwiftSelf`, etc.) identically to `[DllImport]`

Areas of disagreement:
- Whether `[LibraryImport]` + `CustomMarshaller` can work around Blocker #2
- Whether NativeAOT's async state machine improvements address Blocker #3
