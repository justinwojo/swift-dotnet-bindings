This release adds an `ActivityKit` binding so you can drive Live Activities from C#, and clears a batch of generator and runtime defects that blocked noncopyable methods, cross-module protocols, and several Apple framework types. It pairs SDK 0.14.0 with Apple supplement 26.2.6.

## Highlights

- **Live Activities from C#** — A new `ActivityKit` binding lets you start, update, and end Lock Screen and Dynamic Island Live Activities from managed code, with a widget extension that pairs by type name without linking the supplement.
- **Fewer crashes on async and composition-heavy code** — Fixed a `SIGSEGV` when async callbacks are rooted in an `@objc` object and a Mono finalizer crash in existential-composition (`EC2+`) returns.
- **More Swift APIs bind cleanly** — Noncopyable `consuming`/`borrowing` methods, cross-module protocols, and members named after language keywords now generate working bindings instead of silently degrading or failing to compile.
- **Wider Apple framework reach** — `RealityKit` `Entity` collections and `CryptoKit` HPKE constructors are now usable from the generated bindings.
- **Safer Apple-framework packaging** — An interrupted build can no longer leave a half-built xcframework that the SDK treats as current and then fails at runtime with a missing-library error.

## Generator improvements

- **Noncopyable methods bind correctly** — Instance methods with `consuming`/`borrowing` `self` now get a real `@_cdecl` wrapper instead of degrading to raw `CallConvSwift`. `consuming` moves the value and marks the C# handle consumed to avoid a double-free; `borrowing` reads through a true borrow, and a use-after-consume guard fails fast where Swift's move checker would.
- **Cross-module protocols no longer collide** — EveryProtocol emission markers are now keyed by module-qualified name, so a local protocol and a same-named protocol from another module can't share a marker slot and mis-gate a cross-module proxy.
- **Keyword-named members compile** — `View` init parameters, modifier labels, closure labels, and protocol members spelled as a Swift or C# keyword are now escaped in the SwiftUI bridge and EveryProtocol emission instead of emitting raw identifiers that fail to build.
- **`RealityKit` `Entity` collections project** — Non-`@objc` Apple classes from concrete-class-fallback modules now project as `Array`/`Set`/`Dictionary` elements rather than returning null and silently dropping the whole member (for example `Entity` child-collection `append`/`replaceAll` over `[Entity]`).
- **`CryptoKit` HPKE constructors emit** — Throwing generic constructors that take a concrete `Foundation.Data` argument alongside a specializable key now emit per-conformer specialization factories, making `Sender`/`Recipient` construction reachable.

## Runtime improvements

- **Async callbacks rooted in ObjC objects no longer over-release** — The async self-retain is now released through the isa-dispatching path that matches its retain, so an `@objc`-rooted callback target isn't driven to a premature `deinit` and `SIGSEGV`.
- **Composition-return finalizers are crash-safe** — The existential-composition owned-return proxy now routes its value-witness-table destroy through the `SBW_VWTDestroy` `@_cdecl` trampoline, moving it off the confirmed Mono finalizer-thread crash path that every sibling finalizer already avoided.

## Packages

| Package | Version |
|---------|---------|
| SwiftBindings.Runtime | 0.14.0 |
| SwiftBindings.Sdk | 0.14.0 |
| SwiftBindings.Templates | 0.14.0 |
| SwiftBindings.Apple | 26.2.6 |

See the [GitHub wiki](https://github.com/justinwojo/swift-dotnet-bindings/wiki) for installation and usage.
