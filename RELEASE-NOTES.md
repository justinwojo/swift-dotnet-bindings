# SwiftBindings 0.18.0

0.18.0 focuses on making generated bindings more trustworthy: standalone generation now compile-checks its own output — the Swift wrapper and the emitted C# — withdraws members it can't bind soundly (with a recorded reason), and fails with a machine-readable report rather than emitting output it knows is broken. A number of real-world libraries that previously failed to generate, compile, or run now bind, and generated names get a readability pass that includes source-breaking renames.

## Highlights

- **Generation compile-checks its own output** — standalone (`--xcframework`) generation now compiles both sides of the binding, attributes compiler errors back to the Swift declarations that caused them, and withdraws those members rather than shipping them; SDK-driven builds run the same soundness gates, with the project build serving as the compile check. The intent: a library with unsupported corners should produce a smaller but usable binding instead of one that fails to compile.
- **Failures are machine-readable** — a failed generation now writes `binding-failure-report.json` with the terminal reason, blocking diagnostics, and the declarations they attribute to, and the binding report records a disposition for every declaration the generator parsed.
- **Better cross-module support** — types from sibling bindings resolve in more positions (existentials, class returns, protocol requirements), dependency modules are discovered transitively — including ones reachable only through a re-export — and generated projects derive their `PackageReference`s from the namespaces they actually use. Multi-framework libraries benefit the most.
- **More real-world libraries bind** — module names that collide with the BCL, ObjC frameworks needing the `-fmodules` retry, registered Apple integer enums, escaping closures in constrained extensions, protocols with `T!` requirements, and more. Degraded members that previously threw `EntryPointNotFoundException` at runtime now throw `SwiftBindingUnavailableException` with the reason attached.
- **More readable generated names (source-breaking)** — factory and member names are cleaned up in several areas, and pure-ObjC `[Native]` enum members are now PascalCased. Regenerating on 0.18.0 renames identifiers; existing consumers will need to update call sites.

## Generator improvements

- **Naming changes (breaking)** — closed-specialization factories get readable names (`FromByteArray`, not `FrombyteArr_`); a type's sole bypass factory is plain `Create`, keeping the hash suffix only on collision; digit-bearing acronyms keep their casing (`SHA3_256`, `MP3`); fluent builders lose the spurious `Get` prefix (`EqualToSuperview()`); protocol requirement families fold argument labels consistently; ObjC `[Native]` enum members are PascalCased (`Foo.center` → `Foo.Center`).
- **Marshalling fixes** — corrects `inout` parameter marshalling across the wrapper paths, fixes struct-field and existential-setter defects, and Swift operators returning classes and enums now compile, as do `@objc` existential getters.
- **Newly supported shapes** — per-conformer generic container properties, `NSCoding`-style `@objc` delegate reverse dispatch, honest projection of `Never`-defaulted associated types, and Apple's registered integer enums (`PKPaymentButtonType`, `HKWorkoutActivityType`, …) including inside `Optional` and arrays.
- **Apple SDK awareness** — members referencing Apple types are checked against the installed `Microsoft.iOS` reference assembly and withdrawn with a recorded reason when the type isn't there, and a `SwiftBindings.Apple` `PackageReference` is emitted wherever supplement types are used.
- **Narrower, more honest boundaries** — tuple support is now stated explicitly and enforced, where some shapes were previously emitted in forms that could not work, and async closures outside the supported baseline emit `SB0005` tombstone stubs instead of silently degrading to synchronous dispatch.
- **New convenience overloads** — `string` overloads for `Foundation.URL` parameters, `int`/`uint` overloads for enum cases with native-int payloads, and truncated overloads that recover members whose only unbindable part was a trailing defaulted parameter.

## Runtime and async

- **Cancellation fixes** — cross-module extension and generic-parent async members now honor `CancellationToken` and complete their `Task` as `Canceled` on cancellation, matching the behavior plain async methods already had; already-cancelled tokens short-circuit without crossing into Swift. Code that detected cancellation by catching `SwiftException` should catch `TaskCanceledException` instead.
- **Memory fixes** — escaping-closure contexts are released when Swift drops them, and a per-call argument buffer leak in callback marshalling is fixed.
- **`SwiftUI.Text` is public** — the runtime's SwiftUI text projection moved from `Swift.SwiftUI` to the `SwiftUI` namespace and is now publicly constructible (breaking for consumers who referenced the old namespace).

## Diagnostics and failure reporting

- **Integrity checks** — new checks fail generation when a binding would otherwise ship a P/Invoke without its wrapper symbol, a reference to a suppressed proxy class, or an unresolved sibling-module dependency — each with a specific `SWIFTBIND` code naming the cause.
- **Declaration accounting** — parser reconciliation must balance (every declaration parsed, withdrawn, or failed, with a recorded disposition), members of wholly-suppressed types now appear in the report, and the artifact manifest carries a row for every withdrawn declaration with its evidence.
- **Build hygiene** — a failed generation no longer stamps the build up-to-date (the next build retries instead of reusing stale output), wrapper `swiftc` errors are visible at normal verbosity, and a `SwiftFrameworkDependency` missing its managed reference is flagged (`SWIFTBIND081`, with `NativeOnly="true"` to opt out for native-only deps).

## Developer experience

- **One file per type** — generated bindings now split each top-level type into its own `{Module}.Types.{TypeName}.cs` alongside the `{Module}.cs` prelude, plus a per-module `{Module}.api-surface.md` member table; the API surface itself is unchanged. Regenerated projects glob both patterns automatically — only hand-maintained csproj/CI globs need updating.
- **Parallel-build fixes** — the SDK now coalesces its out-of-band builds with the authoritative project graph, fixing races that could break solutions with several bindings under `-m` or IDE concurrency.
- **Cleaner IntelliSense** — unavailable-member stubs and raw async-iterator plumbing are hidden from code completion while remaining callable.
- **New CLI flags** — `--emit-input-graph` (dump the resolved input dependency graph), `--verification-package-feed` (offline feed for the C# verification build), and `--no-verify-csharp` (skip the C# verification leg).

## Packages

| Package | Version |
|---------|---------|
| `SwiftBindings.Runtime` | 0.18.0 |
| `SwiftBindings.Sdk` | 0.18.0 |
| `SwiftBindings.Templates` | 0.18.0 |

`SwiftBindings.Apple` is unchanged at `26.2.8` — it declares a floor-only Runtime range, so the published supplement rides forward without a republish.

See the [GitHub wiki](https://github.com/justinwojo/swift-dotnet-bindings/wiki) for installation and usage.
