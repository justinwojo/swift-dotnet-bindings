# Custom-Global-Actor Constructor Binding

> **Status.** Resolved via the async-factory path (Approach 3). Synchronous `new T(...)` projection remains skipped under SWIFTBIND022 — that part of the design stayed intact. The async factory is additive: each constructor on a `@<CustomActor>`-isolated parent is emitted as `static Task<T> CreateAsync(..., CancellationToken)` whose Swift-side wrapper is `Task { try await Type.init(...) }`. The implicit hop at the `await` lands the init on the actor's executor; C# never crosses the actor boundary directly. Validated end-to-end on simulator (Mono JIT) and physical device (NativeAOT) — both round-trip the init, throwing init failure, and cancellation cleanly. The historical analysis below is preserved as the design rationale for future readers.
>
> **Implementation summary.**
> - `SwiftABIParser.cs` — extended the existing actor-isolation block (which already marked actor-isolated instance methods as async) to also tag constructors on `IsCustomActorIsolated` parents as `IsAsync`. The existing async-method pipeline then emits `CreateAsync` for them, just as it does for `func describe() async` etc.
> - `MethodHandler.cs` — wrapped the SWIFTBIND022 wholesale skip in `!methodEnv.MethodDecl.IsAsync` so async ctors flow through to the async-factory pipeline. Sync ctors that the parser couldn't tag still hit the skip (defense in depth).
> - `WrapperValidation.cs` — gate 6b unchanged in behavior; comment updated to record that gate 5 (`isAsync → false` admission) covers async ctors first and 6b is now a fallback.
> - The fix is ~3 surgical edits totalling far less code than the originally-specified ~200 LOC of new emission. The async-constructor pipeline (CreateAsync, `_SBWTaskEntry`, `_sbwRegisterTask`, GCHandle/TaskCompletionSource bridging) was already in place for plain async inits and required no changes — actor-isolated inits route through it identically. End-to-end coverage lives in `BindingTests/RuntimeTestsApp/Async/CustomGlobalActorTests.cs`.

# Original write-up — Open Architecture Question (preserved)

> **Audience.** This doc is written to be self-contained for an external Swift / .NET interop expert. It explains the problem, the constraints that make it hard, what we have already tried and why each attempt failed, and the specific questions we would like a second opinion on.

## Project context (one paragraph)

This repository is a Swift → C# / .NET binding generator. Given a compiled Swift xcframework + its `.swiftinterface` and ABI JSON, it emits C# bindings that .NET 10 (Mono on simulator, NativeAOT on device) consumers can call. The default emission strategy for Swift methods is to generate a `@_cdecl` Swift wrapper file that re-exports the Swift method as a C ABI symbol, then a C# `[DllImport]` that calls it. For methods whose Swift ABI maps cleanly, we emit `[UnmanagedCallConv(CallConvSwift)]` directly against the Swift-native mangled symbol and skip the wrapper. The wrapper-or-direct decision is made per-member by the emitter.

## Working assumption

Anything in `_Concurrency` private API is on the table for a *short-term* fix only if it is observably stable and can be deleted once a public alternative ships. Anything that requires forking the Swift runtime is out of scope.

---

## The problem

Swift 6 ecosystem libraries are increasingly annotating types with custom `@globalActor`-isolated state — Nuke 13 (`@ImagePipelineActor` on `ImagePipeline`, `TaskQueue`, `ImagePrefetcher`) is the canonical real-world example, and the pattern will spread as the Swift 6 strict-concurrency migration accelerates.

A custom global actor in Swift 6 looks like this:

```swift
@globalActor
public actor ImagePipelineActor {
    public static let shared = ImagePipelineActor()
    public init() {}
}

@ImagePipelineActor
public final class ImagePipeline {
    public init(configuration: Configuration = Configuration()) { ... }
    public func loadImage(with url: URL) async throws -> ImageContainer { ... }
    public static let shared: ImagePipeline = ImagePipeline()  // pre-built, isolation-respecting
}
```

The class itself, all its methods, and its static `.shared` accessor are global-actor-isolated. The compiler enforces that any synchronous access to `ImagePipeline`'s instance state (including its `init`) happen from inside the `@ImagePipelineActor` isolation domain.

We need a C# consumer to be able to construct an instance from C# without writing Swift code in their consumer project:

```csharp
// Goal: this should work, where ImagePipeline is bound from a Swift 6 actor-isolated class.
using var pipeline = new ImagePipeline(configuration: customConfig);
var container = await pipeline.LoadImageAsync(url);
```

---

## What works today

After our last batch of fixes, the following works for `@<CustomActor>`-isolated classes:

| Member kind | Status | How |
|---|---|---|
| The C# class itself | Emitted | Standard class emission; no calling-convention path required for the type record itself. |
| Instance methods | Emitted, callable | `@_cdecl` wrappers receive the existing instance pointer; the method body runs in whatever isolation domain the caller is in. Execution-time safety contract: caller must be on the actor's executor. |
| Property getters / setters (instance and static) | Emitted, callable | Same as instance methods. |
| Static `.shared` accessor | Emitted, callable | The Swift `static let shared` is initialized lazily by Swift's runtime via `swift_once`, so the C# call into the static getter just reads the cached pointer. The init for `.shared` runs on the actor at first access, transparently. |
| Methods that take `any P` / generic existentials / Self-requirement protocols | Emitted, round-trippable | Covered by separate marker-protocol metadata + existential-boxing work. |
| Subclasses, conformances, witness tables | Emitted | Standard. |

Practically: a C# consumer can use `ImagePipeline.Shared` (the singleton) and call every method / property on the resulting instance. This is the most common Nuke usage pattern.

## What doesn't work

User-callable initializers from C#. Specifically: synchronous construction of an `@<CustomActor>`-isolated class from outside the actor.

```csharp
var pipeline = new ImagePipeline(configuration: customConfig);  // not currently bindable
```

The binding generator currently emits diagnostic `SWIFTBIND022` and wholesale-skips every constructor on any class whose parent type is annotated with `@<CustomActor>`. The C# binding has no public constructors. Consumers who need a non-default-configured instance of a `@<CustomActor>` class cannot get one without writing Swift glue in their own project.

---

## Constraints that make this hard

### Swift 6 isolation rules

- A `@<CustomActor>`-isolated initializer is *global-actor*-isolated. Calling it from outside that isolation domain is a compile-time error in Swift 6 strict-concurrency mode.
- `MainActor.assumeIsolated { ... }` exists as a stdlib-special-cased synchronous entry point into `@MainActor`. **There is no analogous `<CustomActor>.assumeIsolated`.** The closest available primitive is `Actor.shared.assumeIsolated { isolated in ... }`, but that closure inherits *instance-actor* isolation (`isolated CustomActor`), not `@<CustomActor>` *global-actor* isolation. The compiler treats these as distinct isolation domains.
- `@_cdecl` functions are nonisolated by definition. Swift 6 does not allow attaching a global-actor annotation to a `@_cdecl` function (and even if it allowed it, the resulting symbol would no longer be plain-C-callable from .NET P/Invoke).
- Swift's allocating init for an actor-isolated class takes an implicit isolation parameter as part of its calling convention. The exact register layout depends on actor type (instance vs. global) and is not stable across Swift versions.

### NativeAOT calling-convention constraints

- `[UnmanagedCallConv(CallConvSwift)]` in .NET supports the Swift calling convention for *non-isolated* functions. Whether it correctly accounts for actor-isolation parameters across Mono and NativeAOT is unclear (see "Approach 2" below — Mono passed, NativeAOT crashed).
- Per CLAUDE.md, Mono and NativeAOT have different bugs. A binding that passes on Mono is not validated until it also passes on NativeAOT.

### Generator constraints

- The wrapper file we emit is plain Swift compiled with `swiftc -emit-library`. It can use anything available in the Swift module's public ABI plus standard library. It cannot import private SPI of the consumer's frameworks. It can use `import _Concurrency` and use any underscore-prefixed API at our risk.
- We do not control the Swift package being bound. The actor type's `unownedExecutor` may be a custom `SerialExecutor` implementation we know nothing about.
- The C# side's call into the wrapper is synchronous unless we change the API shape.

### Consumer ergonomics

- We can change the C# API shape (e.g. expose `static Task<T> CreateAsync(...)` instead of `T(...)`), but every API-shape divergence between "Nuke from Swift" and "Nuke from C#" is friction.
- Adding a Swift companion file in the consumer's project is the current escape hatch. It works but defeats the purpose of having a binding generator.

---

## Approaches tried

### Approach 1 — `@_cdecl` thunk that wraps the init in `<Actor>.shared.assumeIsolated`

**Plan.** Emit a `@_cdecl` Swift wrapper:

```swift
@_cdecl("MyActorClass_init_thunk")
public func MyActorClass_init_thunk(_ label: UnsafePointer<CChar>, _ count: Int) -> OpaquePointer {
    let instance = MyGlobalActor.shared.assumeIsolated { _ in
        MyActorClass(label: String(cString: label), count: count)
    }
    return Unmanaged.passRetained(instance).toOpaque()
}
```

**Result.** Compile-time error:

```
error: call to global actor 'MyGlobalActor'-isolated initializer
'init(label:count:)' in a synchronous actor-isolated context
```

**Why.** `Actor.shared.assumeIsolated`'s closure parameter is `(isolated MyGlobalActor) -> T`. That gives the closure body *instance-actor* isolation against the `MyGlobalActor` instance. It is not the same as `@MyGlobalActor`-isolated context. The Swift 6 compiler distinguishes:
- `isolated MyGlobalActor` — instance-actor isolation tied to a specific actor instance
- `@MyGlobalActor` — global-actor isolation, valid against any caller in that global actor's domain

A `@MyGlobalActor`-isolated init can only be called from another `@MyGlobalActor`-isolated context. There is no public stdlib primitive that enters `@<CustomActor>` global-actor isolation synchronously. `MainActor.assumeIsolated` is the only one that exists, and it is implemented as a stdlib special case on `MainActor` itself rather than as a generic primitive.

This is the architectural roadblock that motivated this whole consultation.

### Approach 2 — Direct `CallConvSwift` to the Swift-native init, document the on-executor contract

**Plan.** Skip the Swift wrapper entirely. Emit:

```csharp
[DllImport("MyLib", EntryPoint = "$s7MyLib12MyActorClassC5label5countACSS_SitcfC")]
[UnmanagedCallConv(CallConvSwift)]
private static extern IntPtr __MyActorClass_init(SwiftString label, nint count, SwiftMetatype self);

[Obsolete("SB0001: caller must be on the actor's executor at call time.")]
public MyActorClass(string label, long count) {
    _handle = __MyActorClass_init(label, count, GetMetatype());
}
```

The idea: if the C# caller is already on the actor's executor (via, e.g., `MainActor` delegation, or because the actor's executor is synthesized from the calling thread), the call is sound. The `[Obsolete]` SB0001 documents the runtime contract.

**Result.** Mono (simulator) passed all our BindingTests. NativeAOT (physical device) crashed inside Swift's allocating init.

**Diagnosis.** The crash signature pointed at the metatype / `self` register layout. NativeAOT's `CallConvSwift` lowering does not appear to account for the actor isolation parameter convention used by `@<Actor>`-isolated allocating inits — the Swift runtime expects a register layout that the .NET-emitted call site does not provide. The `[UnmanagedCallConv(CallConvSwift)]` attribute is documented to support Swift functions but the actor-isolation interaction with NativeAOT is, at best, untested upstream.

**Why we reverted.** Shipping a binding that passes on simulator and crashes on device is worse than not shipping the constructor at all. We restored the wholesale SWIFTBIND022 skip.

### Approach 3 — Async-factory pattern (documented follow-up; not yet implemented)

**Plan.** Change the C# API shape from `new T(...)` to `static Task<T> CreateAsync(...)`, projected from a Swift wrapper that uses `Task { @<Actor> in init(...) }`:

```swift
@_cdecl("MyActorClass_createAsync_thunk")
public func MyActorClass_createAsync_thunk(
    _ label: UnsafePointer<CChar>,
    _ count: Int,
    _ continuationHandle: UnsafeMutableRawPointer
) {
    Task {
        let instance = await { @MyGlobalActor in
            MyActorClass(label: String(cString: label), count: count)
        }()
        // signal the C# TaskCompletionSource via continuationHandle
    }
}
```

```csharp
public static Task<MyActorClass> CreateAsync(string label, long count) {
    var tcs = new TaskCompletionSource<MyActorClass>();
    var handle = GCHandle.Alloc(tcs);
    __createAsync_thunk(label, count, GCHandle.ToIntPtr(handle));
    return tcs.Task;
}
```

**Status.** This is the path documented in the roadmap as the unblocking direction, but unimplemented. Open questions on this path are listed below.

### Approach 4 — Synchronous bridge with `dispatch_semaphore_wait` (rejected, listed for completeness)

**Plan.** Wrap `Task { @<Actor> in init(...) }` then block the calling thread on a semaphore until the task completes.

**Why we rejected this.** Deadlocks if the calling thread is on the actor's executor — the Task can't be scheduled because the executor is held by the blocked caller. There is no general-purpose way to detect "is this thread the actor's executor thread?" from a `nonisolated` context. We could mitigate with priority-aware tricks but the failure mode is silent deadlock, which is worse than a clean compile-time skip.

### Approach 5 — Consumer Swift glue (current escape hatch)

**Plan.** Document that consumers needing custom-configured `@<Actor>`-isolated instances must add a Swift file to their project that exposes a `nonisolated` factory:

```swift
// Consumer-side companion file
import Nuke

public enum NukeBridge {
    public static func makePipeline(configuration: ImagePipeline.Configuration) async -> ImagePipeline {
        await Task { @ImagePipelineActor in
            ImagePipeline(configuration: configuration)
        }.value
    }
}
```

**Status.** This is what consumers do today. It is friction we would like to remove.

---

## Questions for review

These are the specific things we would like a second opinion on. They are roughly ordered from "most likely to unblock us soon" to "most ambitious".

### 1. Is there a public or underscore Swift 6 stdlib primitive we missed?

We canvassed:
- `MainActor.assumeIsolated` (special-cased, only for `@MainActor`)
- `Actor.shared.assumeIsolated` (instance-actor isolation, different domain)
- `Task { @<Actor> in ... }` (works but is async)
- `withTaskExecutorPreference` (preference, not entry — does not change the static isolation domain)
- `withSerialExecutor` (we could not find a public form that synchronously enters a global-actor domain)
- `_unsafeForwardToExecutor`, `_unsafeForwardOnSerialExecutor` (mentioned in passing in `_Concurrency` source; we did not validate behavior or stability)
- `@isolated(any)` parameters / sending closures (Swift 6.0+) — these affect the *parameter*'s isolation, not the call site's

Question: **Is there an underscore or recently-shipped public API that allows synchronous entry into an arbitrary `@<GlobalActor>`-isolated context, given the global actor's `unownedExecutor`?** If yes, what is its stability story across Swift 6.x?

### 2. Is the NativeAOT `CallConvSwift` failure in Approach 2 our P/Invoke signature being wrong, or is it CallConvSwift itself not supporting actor-isolation parameters?

We assumed the actor's allocating init takes an implicit isolation parameter beyond the explicit init parameters and `self` metatype. Our P/Invoke signature did not declare any extra parameter for it. Possibilities:
- The Swift mangled symbol for an actor-isolated allocating init has a different ABI than a non-isolated one (extra hidden parameter at a known register, e.g. `x20` on AArch64). If so, encoding that in the C# P/Invoke signature might fix NativeAOT.
- `CallConvSwift` in .NET 10 does not propagate the isolation register at all, and there is no signature we can write that NativeAOT will lower correctly.

Question: **What does the AArch64 Swift ABI actually pass to a `@<GlobalActor>`-isolated allocating init beyond the explicit parameters and `self` metatype?** Is there a documented register convention we can target? Can `[UnmanagedCallConv(CallConvSwift)]` express it?

If a SIL dump + register-allocation walk would clarify this, we are happy to produce one — guidance on what to look for would help.

### 3. For the async-factory pattern (Approach 3), what is the cleanest cross-runtime continuation-handoff?

Specifically:
- Should the `@_cdecl` thunk take a `UnsafeMutableRawPointer` continuation handle and signal a C#-side `TaskCompletionSource<T>` via a callback function pointer? Or should we use a different shape (e.g., return a `UnsafeRawPointer` that wraps the `Task` itself, and let C# poll/await it)?
- How should we plumb errors from a `throws` Swift init back to `TaskCompletionSource<T>.SetException` cleanly across the Swift / C# boundary, without leaking the Swift error existential?
- For NativeAOT, are there pinning/lifetime concerns with `GCHandle`-based continuation handles when the Task runs on a Swift-managed thread that is not a .NET runtime thread?

Question: **What is the recommended shape for an async-factory thunk in Swift / .NET 10 interop, and are there prior-art examples (Swift / C++ interop, Kotlin / Swift interop, Apple's own internal bridging) we should crib from?**

### 4. Is there a creative ABI manipulation we have not considered?

For example:
- A `nonisolated` Swift function that uses `withCheckedContinuation` and `Task` to bridge synchronously, with deadlock-safe semantics for the on-executor-already case?
- Something using `swift_task_run_inline` or related runtime entry points (we saw these mentioned in `_Concurrency` private headers but did not investigate deeply)?
- A trick using Swift's `@_unsafeInheritExecutor` on an `async` function to "pretend" the wrapper is in the actor's domain, then bridging to C# synchronously via some other mechanism?

Question: **Are any of these ABI-level tricks viable, even as short-term fixes pending a real public API?** What are the failure modes we need to test for?

### 5. Are there Swift evolution proposals or vision docs we should track?

The honest current position is: "Swift 6 does not expose synchronous entry into an arbitrary global actor; the async-factory pattern is the only durable answer."

Question: **Is that going to change in 6.x?** Are there in-flight `swift-evolution` proposals or vision docs (e.g. in `apple/swift-evolution`) that address this gap directly? If so, what is the realistic timeline, and is it worth waiting vs. shipping the async-factory now?

---

## Concrete success criterion

A user with a Swift 6 `@<CustomActor>`-isolated `init` should be able to construct an instance from C# without:

- Writing a Swift companion file in their consumer project
- Risking a deadlock if construction is invoked from any thread, including the actor's own executor thread
- Crashing on either Mono (simulator) or NativeAOT (device) due to calling-convention mismatches

The C# API may be `static Task<T> CreateAsync(...)` rather than `new T(...)` if the only sound path is async — that is acceptable. What is not acceptable is silent crashes, deadlocks, or "you must add a Swift file to your project".

---

## Pointers into the codebase if you want to dig in

- Wrapper-emission gate that decides skip vs. emit per constructor: `src/Swift.Bindings/src/Emitter/StringEmitter/WrapperValidation.cs`
- Where the constructor-skip diagnostic is currently emitted: `src/Swift.Bindings/src/Emitter/StringEmitter/ConstructorWrapperEmitter.cs` plus `MethodHandler.cs`, `DefaultParameterOverloadEmitter.cs`
- Parser threading of `@<CustomActor>` annotation through to the type model: `src/Swift.Bindings/src/Parser/SwiftABIParser.cs` and `SwiftInterfaceAccessParser.cs`; the resulting flag lives in `src/Swift.Bindings/src/Model/TypeDecl/TypeDecl.cs` as `CustomActorIsolatorName` / `IsCustomActorIsolated`
- Marker-protocol and concurrency type records (`Sendable`, `Copyable`, `Escapable`, `SendableMetatype`, `Actor`, `GlobalActor`, `UnownedSerialExecutor`): `src/Swift.Runtime/src/Swift/SwiftDatabase.xml` and `src/Swift.Runtime/src/Swift/_ConcurrencyDatabase.xml`
- Test fixture exercising the current behavior: `BindingTests/Sources/SwiftBindingsTestLib/Async/CustomGlobalActor.swift` (Swift fixture) and `BindingTests/RuntimeTestsApp/Async/CustomGlobalActorTests.cs` (C# tests asserting wholesale ctor skip + non-init member reachability)
- Authoritative list of confirmed upstream .NET runtime bugs (so you can rule those out as a cause): `~/.claude/projects/-Users-wojo-Dev-swift-bindings/memory/feedback_mono_jit_blame.md`

The repo's `CLAUDE.md` has the conventions for tests / validation gates; the binding generator's CLI is `dotnet run --project src/Swift.Bindings/src -- --xcframework <path> -o <out>`.

---

## Summary in one sentence

We can bind every member of a Swift 6 `@<CustomActor>`-isolated class except its constructor; the constructor is blocked by the absence of a synchronous global-actor entry point in Swift 6 (only `MainActor` has one), and our attempt to skip the wrapper and call the Swift-native init via `CallConvSwift` directly compiled fine on Mono but crashed on NativeAOT inside the allocating init — we want to know whether we are missing a primitive, a calling-convention detail, or whether async-factory is genuinely the only durable answer.
