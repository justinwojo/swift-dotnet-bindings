# Upstream Issues — Filing Guide

Submission-ready bug reports against [dotnet/runtime](https://github.com/dotnet/runtime/issues). Each file below is self-contained and can be filed verbatim once the pre-flight checks at the bottom of this README pass.

Project: [swift-dotnet-bindings](https://github.com/justinwojo/swift-dotnet-bindings)
Repro: [swift-interop-repro](https://github.com/justinwojo/swift-interop-repro)
Last verified: **2026-04-30** on .NET 10.0.103, Microsoft.iOS.Sdk 26.2.10197, Xcode 26.2 / Swift 6.2.3, macOS 26.2 (Apple M1 Pro), iPhone 13 (iOS 26).

## Files

| Issue | File | Type | Runtime | Repro class |
|-------|------|------|---------|-------------|
| 1 | [`upstream-issue-01-mono-jit-async-assert.md`](./upstream-issue-01-mono-jit-async-assert.md) | Bug | Mono | `Issue1_MonoSignalHandlerAssert` |
| 2 | [`upstream-issue-02-non-blittable-callconvswift.md`](./upstream-issue-02-non-blittable-callconvswift.md) | Feature Request | Mono + NativeAOT | `Issue2_NonBlittableRejection` |
| 3 | [`upstream-issue-03-mono-set-insert-done-blocking.md`](./upstream-issue-03-mono-set-insert-done-blocking.md) | Bug | Mono | `Issue3_MonoSetInsertDoneBlocking` |
| 4 | [`upstream-issue-04-mono-catalyst-x64-instability.md`](./upstream-issue-04-mono-catalyst-x64-instability.md) | Bug | Mono (maccatalyst-x64) | `Issue4_MonoCatalystX64Instability` *(pending publish)* |
| 5 | [`upstream-issue-05-mono-unwinder-oce-pac.md`](./upstream-issue-05-mono-unwinder-oce-pac.md) | Bug | Mono (ios-sim arm64) | BindingTests `PatParentAsyncMethodsTests.*CancelRespondAsync*` *(standalone repro pending)* |

**Filing priority:** Issue 2 first (highest real-world impact — drives ~67% of the wrapper rate across 51 third-party Swift library bindings), then Issues 1 and 3.

## Not filed as standalone bug reports

- **`SwiftSelf<SafeHandle>` lifetime across async P/Invoke (Mono-only)** is a supportability question rather than a confirmed runtime bug. Post as a comment on the Swift interop tracking issue (successor to [#108662](https://github.com/dotnet/runtime/issues/108662)), not a standalone filing. Full comment text below under [Tracking-issue comment: `SwiftSelf<T>` async lifetime](#tracking-issue-comment-swiftselft-async-lifetime).
- **Issue 8 (NativeAOT 2-type-param `CallConvSwift` SIGSEGV)** — closed 2026-04-30. Wrong P/Invoke shape on our side, not a runtime bug. Multi-result tuple returns use `(x0=res1, x1=res2, x2=pay1, x3=pay2, x4=Tmeta, x5=Umeta)`, not `(x8=SwiftIndirectResult, x0=pay1, x1=Tmeta, x2=Umeta, …)`. Generator now emits the correct shape for fully bare-generic tuples (`pair<T, U> -> (T, U)`); mixed/bound-generic shapes are tracked in `not-planned.md` (Mixed-indirect generic tuple returns). Disassembly evidence is in commit 1d0c5569.

## Pre-flight checks before filing

For each issue file:

1. **Re-search dotnet/runtime issues** for any new filings since 2026-04-30 — at minimum the symptom string (e.g. "`!ji->async`", "non-blittable types with Swift calling convention", "Cannot transition thread from STARTING with DONE_BLOCKING") and the relevant source file paths.
2. **Re-verify the repro on the latest .NET SDK** in use at filing time. Update the "Verified on" line and the `.NET SDK X.Y.Z` / `Microsoft.iOS.Sdk X.Y.Z` version numbers in the file header.
3. **Re-check the Xcode / iOS SDK version** in the repro environment (the iOS Simulator runtime version drives some of the symptoms).
4. **Replace `justinwojo/swift-interop-repro`** with the actual published repo URL if different.
5. **Issue 2 only:** confirm the source-pointer line numbers (`marshal.c:3700-3735`, `SwiftPhysicalLowering.cs:~215`) still match the current `dotnet/runtime` `main` branch — they tend to drift between releases.
6. **Issue 3 only:** confirm `nm -g libswiftCore.dylib | grep Sh6insert` still resolves to `_$sSh6insertySb8inserted_x17memberAfterInserttxnF` (the Swift stdlib symbol can be re-mangled across major Swift versions, though this is rare).

## File-once order suggestion

1. **Issue 2** as `enhancement` / feature request — it is the framing question (does the runtime want to support non-blittable `CallConvSwift`? if not, what is the long-term direction?), and the answer informs how aggressively we should pursue per-method `@_cdecl` wrappers in swift-bindings.
2. **Issue 1** as a Mono bug — independent of Issue 2, smaller in scope, and the fix is purely on Mono's side.
3. **Issue 3** as a Mono bug — also Mono-only and well-scoped to one `CallConvSwift` ABI shape; cross-link to Issue 1 since both involve Mono's `CallConvSwift` trampoline.

After filing, link the dotnet/runtime issue numbers back into:
- `src/docs/roadmap.md` (Blocked — Confirmed Upstream Only section)
- `/Users/wojo/.claude/projects/-Users-wojo-Dev-swift-bindings/memory/feedback_mono_jit_blame.md` (the authoritative confirmed-issues list)
- `/Users/wojo/Dev/swift-interop-repro/README.md` (Reproduced Issues table)

---

## Tracking-issue comment: `SwiftSelf<T>` async lifetime

> Post as a comment on the Swift interop tracking issue (successor to [#108662](https://github.com/dotnet/runtime/issues/108662)). NativeAOT does not reproduce — confirmed by BindingTests `--device` runs (March 2026, .NET 10.0.103). Mono-only.

**Subject:** Question: Is `SwiftSelf<T>` with a managed lifetime-bearing `T` (e.g. `SafeHandle`) supported across Swift `async` suspension on Mono with `CallConvSwift`?

We're building a binding generator that calls async Swift instance methods from C# via P/Invoke with `CallConvSwift`. The `self` parameter is passed via `SwiftSelf<T>`, where `T` is a `SafeHandle`-derived pointer holder.

We're observing that when an async Swift method suspends, the `SafeHandle` reference is not preserved across the Task continuation boundary on Mono — the GC can collect the handle (triggering `swift_release()`) while the Swift async operation is still in flight, even though the awaiting `Task` is still alive on the managed side.

Our reading of the marshalling code is that this might be by-design rather than a bug:

- The standard CoreCLR P/Invoke `SafeHandle` lifetime guarantee is *call-scoped*: `ILSafeHandleMarshaler::ArgumentOverride` (`coreclr/vm/ilmarshalers.cpp:~2899`) emits an `AddRef` before dispatch and `Release` in the cleanup block of the generated stub (`coreclr/vm/dllimport.cpp:~2280`). It guarantees liveness for the duration of the native call, not across an arbitrary Swift async suspension that continues after the P/Invoke returns.
- Mono's `CallConvSwift` path *bypasses* the normal `SafeHandle → IntPtr + AddRef/Release` marshalling for `SwiftSelf` (special-cased at `marshal.c:~3725` before the blittable check), so even on runtimes that *do* run `SafeHandle` marshalling, `SwiftSelf` doesn't go through it today.

Our current workaround is to generate Swift wrapper functions that use `Unmanaged` to recover the instance from a raw pointer, and on the C# side, explicitly `DangerousAddRef` → read pointer → `Arc.UnknownObjectRetain` (Swift-side retain) → `DangerousRelease`, with a holder that releases the retained pointer when the async operation completes:

```swift
@_silgen_name("MyClass_asyncMethod_wrapper")
public func myClass_asyncMethod_wrapper(_ selfPtr: UnsafeMutableRawPointer) async {
    let instance = Unmanaged<MyClass>.fromOpaque(selfPtr).takeUnretainedValue()
    await instance.asyncMethod()
}
```

```csharp
public async Task AsyncMethod()
{
    bool addedRef = false;
    try
    {
        _handle.DangerousAddRef(ref addedRef);              // pin SafeHandle
        var ptr = _handle.DangerousGetHandle();             // safe to read after AddRef
        Arc.UnknownObjectRetain(ptr);                       // Swift-side ARC retain
        try { await MyClass_asyncMethod_wrapper(ptr); }
        finally { Arc.Release(ptr); }
    }
    finally
    {
        if (addedRef) _handle.DangerousRelease();
    }
}
```

The `DangerousAddRef` / `DangerousRelease` bracket avoids a finalizer race between reading the handle and retaining it on the Swift side; the Swift-side `Arc.UnknownObjectRetain` keeps the underlying object alive even if the C# `SafeHandle` is collected after the P/Invoke returns. The repo's generator emits this pattern (see `WrapperEmitter.Async.cs`).

**Questions:**
1. Is async P/Invoke with `CallConvSwift` + `SwiftSelf<T>` (where `T` carries a managed lifetime) a supported scenario on Mono today, or is it explicitly out of scope for current `SwiftSelf` semantics?
2. If supported, what is the recommended pattern for ensuring the underlying handle stays alive across Swift async suspension points? Is there a runtime mechanism we're missing, or is the `DangerousAddRef` + Swift-side `Arc.UnknownObjectRetain` pattern above the expected approach?
3. If unsupported today, is extending `SwiftSelf<T>` to a Task-scoped lifetime contract on the roadmap? It's required for calling any async instance method on a Swift class from .NET without per-method Swift wrapper generation.

This affects every async Swift instance method we bind — libraries like StoreKit 2 and Nuke rely heavily on async instance methods. Currently every such method requires an `@_silgen_name` Swift wrapper, which adds significant build complexity.

**Verified on 2026-04-30** with .NET SDK 10.0.103, Microsoft.iOS.Sdk 26.2.10197, Mono iOS Simulator runtime 26.3, Xcode 26.2 (build 17C52). The Mono-only scope is confirmed by recent BindingTests `--device` runs (NativeAOT path): the same async + `SwiftSelf<SafeHandle>` patterns succeed on NativeAOT iOS device. The standalone repro app does not include a dedicated async-suspension test (the `MonoC_ClosureCallback_WithSwiftSelf` block exercises the synchronous-callback shape but not the await-induced suspension that triggers the lifetime gap), so this filing relies on BindingTests evidence rather than a `swift-interop-repro` reduction.
