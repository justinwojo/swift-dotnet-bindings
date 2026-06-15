This release clears the App Store Connect rejection that was blocking binding-based apps from shipping, and hardens the generator and runtime against a batch of crashes, leaks, and real-world binding failures. It pairs SDK 0.15.0 with Apple supplement 26.2.7.

## Highlights

- **App Store submissions go through** — The native runtime now ships as a signed `SwiftBindingsRuntime.xcframework` that the .NET Apple workload embeds and re-signs as a normal framework, instead of a loose `dylib` that App Store review forbids — so submissions no longer fail with the "SwiftSupport folder is missing" / `ITMS-90426` rejection ([#42](https://github.com/justinwojo/swift-dotnet-bindings/issues/42)).
- **More real-world libraries bind** — Closure-payload enums (`Alamofire`'s `URLEncoding.ArrayEncoding`), `RealityKit`/`RealityFoundation` `simd` types, and mixed ObjC+Swift frameworks that reference unbindable ObjC types (`Quick`) now generate compiling, crash-free bindings instead of failing to build or faulting on first use.
- **Safer async boundaries** — Cancellation is replayed so it can't be lost, every native callback is wrapped in an exception guard, and an `AsyncStream` use-after-free on the context handle is closed.
- **Fewer crashes and leaks in C#-implemented protocols** — Reverse-dispatch proxy/impl lifetime and vtable slot membership are corrected, and a Mono full-AOT iOS device can no longer mistake itself for NativeAOT and hit an uncatchable dispatch assertion.

## Generator improvements

- **Closure-payload enums marshal correctly** — A Swift enum with a closure/function-payload case (such as `Alamofire`'s `URLEncoding.ArrayEncoding`) used to record zero associated values, get lowered to an int-backed enum, and crash on ARC release when the wrapper read a multi-word payload out of a 4-byte buffer. These now emit as associated-value classes with a case tag.
- **Unbindable ObjC type references are dropped** — Mixed ObjC+Swift bindings whose ObjC surface references types we can't bind — classes rooted in Developer-tools types like `XCTestCase`, their transitive subclasses, and block-typedef delegates — used to emit uncompilable C#. The emitter now drops those declarations to a fixpoint so the rest of the binding compiles (`Quick`).
- **Swift `inout` propagates as C# `ref`** — `inout` protocol parameters now flow as `ref` across the interface declaration, the proxy stubs, and the reverse-dispatch receivers instead of being dropped.
- **Tuple parameters carry class and existential elements** — Tuples whose elements are class instances or protocol existentials now marshal across the cdecl buffer ABI, completing the tuple-parameter path that previously handled only scalar elements.
- **`Optional` and closure-dispatch correctness** — A value-type `Optional` appearing as a tuple or container element used to marshal a real Swift `nil` to C# as `Some(default)`; it now arrives as `null`. A closure method that force-unwrapped a nil owner vtable (when only the peer protocol was implemented in C#) and a force-unwrap in the closure fan-out path no longer `SIGSEGV`.
- **Unsupported shapes fail loudly instead of silently** — Generic-enum case constructors and frozen-struct layouts we can't faithfully mirror now decline with loud unsupported markers (falling back to `@_cdecl`) rather than binding non-exported symbols or claiming a blittable mirror of an unknown layout, and PAT existentials that degrade to `object` raise a `SWIFTBIND023` diagnostic and carry `[UnsupportedSwiftType]` on every emitted member.

## Runtime improvements

- **Async cancellation and exceptions are crash-safe** — Cancellation replays through a process-monotonic registry so a cancel fired in any window is never lost, and every `[UnmanagedCallersOnly]` body routes through a single guard envelope so a managed exception can't unwind across the native boundary. The `AsyncStream` context handle is now freed solely by the always-last completion callback, closing a use-after-free where a freed `GCHandle` cookie could be recycled by a concurrent allocation.
- **Mono full-AOT devices take the right dispatch path** — The runtime heuristic couldn't tell a Mono full-AOT iOS device from NativeAOT (same `ios-arm64` RID, no `Mono.Runtime` type), so it could take the NativeAOT direct-dispatch path on Mono and hit an uncatchable `jit-info.c` assertion. The SDK now injects the build-time interop contract so the correct path is always chosen, with a simulator-safe heuristic fallback.
- **Reverse-dispatch lifetime and vtable slots are correct** — The C# implementation is now rooted from its Swift proxy and releases its construction `+1` on deinit, so abandoned conformers no longer leak or fault. Same-module vtable slot membership is aligned with the cross-module path — an optional-before-required member previously skewed every method slot into a guaranteed nil-unwrap crash — and `any P & Q` composition existentials are rooted across the marshalling sites so a GC can't release a proxy's sole retain mid-call.
- **ABI mirrors are checked against live Swift** — The runtime's hand-mirrored value-witness offsets, existential-container sizes, metadata-kind discriminators, tuple element offsets, and frozen-struct sizes are now asserted against ground truth exported from live `MemoryLayout` and type metadata on both the simulator and device, so an Apple ABI drift surfaces as a failing test instead of silent memory corruption.

## Packaging and developer experience

- **Mixed (ObjC+Swift) bindings pack again** — `dotnet pack --no-build` forwarded `NoBuild=true` to the SDK's out-of-band ObjC-companion build, which then refused to build and produced no package, so every mixed binding failed to pack. This regression is fixed by pinning `NoBuild=false` on the SDK's internal companion and sibling-dependency builds.
- **Apple-supplement facades warn before they throw** — The `ActivityKit` and SwiftUI facades in `SwiftBindings.Apple` now carry `[SupportedOSPlatform]`/`[UnsupportedOSPlatform]` attributes (including the explicit Mac Catalyst exclusion), so consumers get a `CA1416` warning when calling an API on an unsupported platform instead of only finding out at runtime.
- **Trimmer descriptors and XML docs reach consumers** — The generator-emitted trimmer descriptor now reaches every consumer topology — embedded for the IL trimmer across all three SDK-path shapes and rooted via `IlcArg` for ILC under `PublishAot` — so trimming and NativeAOT keep the symbols the bindings need, and packable bindings now emit a documentation file for consumer IntelliSense.
- **Parallel builds no longer flake** — Wrapper-xcframework compilation is serialized under an obj-dir lock, fixing a spurious `SWIFTBIND051` when the same leaf csproj was scheduled in two build contexts sharing one obj dir and the follower validated a peer's still-partial xcframework.

## Reported issues fixed

- **[#42](https://github.com/justinwojo/swift-dotnet-bindings/issues/42) — App Store Connect rejection: "The SwiftSupport folder is missing"** — Apps built on the bindings were rejected at submission (`ITMS-90426`/`90429`/`90171`) because the native runtime shipped as a loose `libSwiftBindingsRuntime.dylib` in the app's `Frameworks/`, which App Store review forbids (TN2435). The runtime now ships as a signed `SwiftBindingsRuntime.xcframework` of per-platform `.framework` slices that the .NET Apple workload embeds and re-signs through a single `NativeReference`, so a submitted IPA carries a properly signed framework and passes review. A new `nuke binding-tests --appstore-hygiene` gate builds a signed IPA from the packed runtime and asserts it is TN2435-compliant.

## Packages

| Package | Version |
|---------|---------|
| SwiftBindings.Runtime | 0.15.0 |
| SwiftBindings.Sdk | 0.15.0 |
| SwiftBindings.Templates | 0.15.0 |
| SwiftBindings.Apple | 26.2.7 |

See the [GitHub wiki](https://github.com/justinwojo/swift-dotnet-bindings/wiki) for installation and usage.
