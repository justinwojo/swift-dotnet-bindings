# Bug: Async top-level ObjC-container return emits pointer-shape call on managed Swift collection

> SDK 0.10.0 latent generator bug. Discovered 2026-05-05 during the Bundle 01
> codex-review pass. Not triggered by any current BindingTests / nuke validate
> fixture, but a real-world Swift API of the form `func foo() async -> [URL]`
> (top-level, non-optional ObjC-bridge container) would emit non-compiling C#.
> Pre-existing on `main`; not a Bundle 01 regression — same shape exists with
> the prior `ArrayFromHandle<T>(handle)` single-arg form, which also expects
> an `IntPtr` rather than a managed `SwiftArray<T>`.

## Summary

`WrapperEmitter.Async.cs::EmitAsyncWrapperForCollection` declares
`var _collection = SwiftMarshal.MarshalFromSwift<{runtimeType}>(resultPtr);`
where `runtimeType` is the projection's `ContainerTypeName` (e.g.
`SwiftArray<Foundation.NSUrl>`). It then drops in
`ITypeProjection.GetReturnContainerConversion("_collection")` for the
result expression.

For ObjC-container-bridge projections (`UsesObjCContainerBridge == true` —
i.e. `[URL]`, `Set<URL>`, `[String: URL]` whose elements bridge through
NSObject), `GetReturnContainerConversion` now emits an IntPtr-typed call:

- `ArrayProjection`: `Foundation.NSArray.ArrayFromHandleFunc<NSUrl>(_collection, h => …, true)`
- `SetProjection`: `ObjCRuntime.Runtime.GetINativeObject<Foundation.NSSet>(_collection, true)!`
- `DictionaryProjection`: `ObjCRuntime.Runtime.GetINativeObject<Foundation.NSDictionary>(_collection, true)!`

Both forms expect an `IntPtr`, but `_collection` is the managed
`SwiftArray<NSUrl>` / `SwiftSet<NSUrl>` / `SwiftDictionary<…>` returned
by `MarshalFromSwift`. The C# compiler rejects the call as a type
mismatch (`CS1503`).

The same code path is correct for the optional unwrap shape
(`Optional<Array<URL>>`): `TryGetOptionalMarshalType` switches the
projection-conversion variable to `_ptr` (a real `IntPtr` carrier) at
`WrapperEmitter.Async.cs:1890`, side-stepping the mismatch.

## Why it doesn't fire today

No fixture in `BindingTests/Sources/SwiftBindingsTestLib/` has the
top-level non-optional shape:

```bash
grep -rn 'async.*-> \[URL\|async.*-> Set<URL\|async.*-> \[String: URL' \
  BindingTests/Sources/SwiftBindingsTestLib/
```

Every async ObjC-container return in the fixture set is `[URL]?` /
`Set<URL>?` / `[String: URL]?` (Optional-wrapped), which routes through
the working pointer path. The compile gate (`nuke binding-tests
--compile-only --strict`) is therefore green even with the latent bug
present.

The same is true on `main`: the Apple framework binding suite
(StoreKit2 etc.) used the optional shape exclusively, which is why
[bug-0.10.0-bridged-nsarray-retain-leak-from-passretained.md] called
out the optional path specifically.

## Hypothesis

`EmitAsyncWrapperForCollection` needs the same routing branch the
optional wrapper grew: when the projection's
`UsesObjCContainerBridge == true`, change the Swift-side ABI to store
the +1-retained NSArray/NSSet/NSDictionary pointer (via `as AnyObject`)
into the carrier, and the C# side to read that as an `IntPtr` and
hand it to `GetReturnContainerConversion("_ptr")` — exactly the
pattern used by the optional case. Or, equivalently, route ObjC-bridge
container returns through a separate emitter rather than reusing
`EmitAsyncWrapperForCollection`.

## Severity

**Correctness — Latent.** No fixture triggers it; would cause
compile-time `CS1503` on a real Swift API of the form
`async -> [URL]` / `async -> Set<URL>` / `async -> [String: URL]`.

## Out of scope for Bundle 01

Bundle 01's theme is SafeHandle / refcount lifetime
(`bug-0.10.0-equals-and-setter-missing-dangerousaddref`,
`bug-0.10.0-deferredsafehandlerelease-refcount-underflow`,
`bug-0.10.0-safehandle-wraps-stack-pointer-in-generic-enum-extractor`,
`bug-0.10.0-bridged-nsarray-retain-leak-from-passretained`). This is an
async-emitter ABI-routing issue. Filed for a future async/marshalling
bundle.
