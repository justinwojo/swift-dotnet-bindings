0.16.0 is a correctness and fail-closed release. The generator now either binds an API correctly or drops it with a clear diagnostic, instead of silently emitting code that won't compile or crashes at runtime, and a batch of confirmed crash and leak fixes lands across existentials, protocol proxies, and async. It also lets common `async` protocol requirements dispatch without blocking the calling thread, binds throwing async streams, adds identity equality on wrapper types, and ships a memory-leak and retain-cycle toolkit. It pairs SDK 0.16.0 with Apple supplement 26.2.8.

## Highlights

- **Async protocol methods and streams that work end to end** — A C# implementation of a primitive-shaped `async` Swift protocol requirement — an instance method with up to four non-`inout` blittable-scalar parameters and a blittable-scalar return — no longer blocks the calling thread; the suspended Swift caller is handed a real continuation and resumes cleanly, while other shapes keep the existing blocking dispatch. A throwing `AsyncThrowingStream` now binds to `IAsyncEnumerable<T>` with Swift errors surfaced at the `await foreach`, and disposing the C# enumerator early task-cancels the suspended Swift producer and frees its context handle.
- **Bindings either work or are dropped cleanly** — A broad fail-closed pass means the generator surfaces a diagnostic (and, with the new `--strict-inputs` flag, a non-zero exit) instead of quietly narrowing the API or emitting an unprovable constraint that won't compile. Constructors that can't be marshalled correctly — for example ones taking a `@convention(c)` closure or an `async -> Void` closure — are now dropped at generation time rather than producing code that fails to build or crashes at the call site.
- **Crash and leak fixes across existentials, proxies, and async** — Fixes an invalid-free in `ExistentialUnion.As<T>()` for resilient (non-`@frozen`) struct conformers, a `SIGSEGV` when reading an `Optional` ObjC-class property through a protocol proxy, and several async result-carrier leaks on both the success and fault paths.
- **Reference types behave like .NET objects** — Swift class wrappers now implement `Equals`/`GetHashCode` keyed on the live handle, so they compare correctly and stay findable in `Dictionary`/`HashSet` even after `Dispose`, and a caught Swift error round-trips its identity and description instead of collapsing to a message-only exception.
- **Memory-safety tooling for consumers** — `@MainActor`-isolated APIs now carry a `[SwiftMainActor]` attribute with a `DEBUG`-only off-thread guard, a new `SB1002` analyzer warns about callback captures that may create a Swift/C# retain cycle, and `WeakSwiftReference<T>` plus `SwiftLeakCensus` ship in the runtime for leak diagnosis.

## Generator correctness

- **More APIs either bind or fail closed** — Unprovable secondary and method-level conformance constraints, generic-parameter constraints with no emittable C# scope, and `BitwiseCopyable`-constrained constructors are now dropped instead of emitting wrappers that fail to compile. Degraded inputs (dropped members, downgraded marshalling, unresolved dependencies) raise distinct `SWIFTBIND` diagnostics that `--strict-inputs` escalates to a non-zero exit rather than shipping a quietly narrowed binding, and an unverified or out-of-envelope Xcode toolchain warns with `SWIFTBIND055` (a recognized out-of-range version also fails under `--strict-inputs`).
- **Real-world library regressions cleared** — Generic and non-generic overloads that projected to identical C# parameter types no longer break with `CS0535`, an unreleased content sort no longer renames an already-published overload (`CS1739`), and over-stripped initializer symbols that NativeAOT needs at link time are restored. A second batch fixes `Optional`-tag layout divergence between the field-layout and register paths, witness-dispatch slot shifts that called the wrong protocol method, and a dependency-collision scan that missed `indirect`/`nonisolated` modifier prefixes.
- **`async` and `@convention(c)` read from the demangle tree** — Methods that return `some Protocol` (opaque return) are now correctly emitted `async`, and a type or symbol whose name merely contains `Ya` or `XC` — anything under `XCTest`, for example — is no longer mis-classified as `async` or `@convention(c)` by the old substring scan.
- **Existential properties and overloaded requirements emit valid C#** — Settable properties and subscripts typed as `any Protocol` no longer emit a getter/setter signature mismatch (`ExistentialUnion` is now reserved for get-only return positions), and protocols whose requirements differ only by existential parameter type no longer emit orphaned receiver stubs that break the build.
- **Parser and type-resolution fidelity** — `where` clauses on a parent extension with sugared generic parameter names are no longer silently dropped, a labeled empty tuple no longer corrupts every later `()` in the same run (a `() -> ()` could render as `first: () -> first: ()`), and a false `String : RangeExpression` conformance no longer admits `String` to a `RangeExpression`-constrained generic.
- **Async and sync namesakes compile together** — A protocol that declares both an `async` requirement and a same-named sync method now compiles its blocking reverse-async receiver instead of failing with `CS1061`.

## Crashes and memory safety

- **Existential-union returns** — `ExistentialUnion.As<T>()` no longer invalid-frees on resilient (non-`@frozen`) struct conformers and now reads payloads stored out-of-line in a heap box (large structs) by projecting through the box instead of reading its header.
- **Protocol-proxy receivers** — Reading an `Optional` ObjC-class property through an existential-protocol proxy no longer `SIGSEGV`s on first use; the `+1` pointer is now adopted instead of dereferenced raw.
- **Closure marshalling** — Passing an escaping `@convention(c)` callback no longer leaks its thread-static slot on every call, and constructors that take a `@convention(c)` or `async -> Void` closure are dropped at generation time (with a recorded reason) instead of crashing or failing to compile at the call site.
- **Async result carriers** — Async completions that fault their awaiting `Task`, and async methods that return collections or generic types, no longer leak the Swift result's `+1` reference; the fault path releases the carrier per its shape and the marshal path is guarded by `try/finally`.

## Runtime and developer experience

- **Lower-overhead `@_cdecl` hot path** — Fixed-size indirect-result scratch buffers and transient string arguments are now stack-allocated instead of heap-allocated per call, and a registered Swift type's metadata resolves through a typed dispatcher rather than a reflection scan — less GC pressure at high call rates, with no change to calling convention, ownership, or marshalling order.
- **Safer loading** — A binding generated against one `SwiftBindings.Runtime` no longer hard-aborts the app at load when run against a same-minor runtime; the load-time contract now accepts any binding whose epoch falls inside the supported version window and only rejects genuinely incompatible cross-minor pairs.
- **OS-availability guards are catchable** — Touching a binding for an API gated above the running OS version now throws a catchable `PlatformNotSupportedException` instead of an uncatchable native abort during eager metadata or registration work.
- **xcframework metadata survives large values** — A `<integer>` wider than 32 bits in a third-party `Info.plist` no longer throws and causes the whole plist — including its minimum-OS metadata — to be silently discarded.
- **Build wiring fails loudly** — Broken MSBuild hook wiring in the SDK now produces an explicit `SWIFTBIND062`–`065` error instead of silently no-op-ing, and a missing `SwiftBindings.Runtime` embed is caught at build time via a positive embed stamp rather than surfacing only as an App Store rejection.

## Objective-C and Apple frameworks

- **Objective-C availability reaches .NET** — Availability annotations on Objective-C-imported APIs (`API_AVAILABLE`, `API_DEPRECATED`, `API_UNAVAILABLE`, `NS_AVAILABLE_*`, and `__attribute__((availability))`) are now recovered from the header source and emitted as `[SupportedOSPlatform]`/`[UnsupportedOSPlatform]`/`[ObsoletedOSPlatform]` attributes, so the `CA1416` platform-availability analyzer covers ObjC APIs the way it already covered Swift ones.
- **`...Ref` alias scoping** — The `Foo`/`FooRef` typedef-alias toggle is now restricted to the `CoreFoundation`/`CoreGraphics` family, so a non-Apple Swift type with a `...Ref` suffix can no longer resolve to the wrong sibling.
- **Supplement regenerated** — `SwiftBindings.Apple` 26.2.8 is rebuilt on the 0.16.0 generator, so the Apple-framework bindings inherit this release's existential, conformance, and parser-fidelity fixes.

## Packages

| Package | Version |
|---------|---------|
| SwiftBindings.Runtime | 0.16.0 |
| SwiftBindings.Sdk | 0.16.0 |
| SwiftBindings.Templates | 0.16.0 |
| SwiftBindings.Apple | 26.2.8 |

See the [GitHub wiki](https://github.com/justinwojo/swift-dotnet-bindings/wiki) for installation and usage.
