# Bug: Indirect-result returns invoked without the indirect buffer (DataLoader.Validate)

> SDK 0.10.0 generator correctness bug. Discovered 2026-05-05 during a
> consumer-experience audit of
> [SwiftBindings.Nuke](https://github.com/justinwojo/swift-dotnet-packages)
> 13.0.5 generated bindings.

## Summary

The generator emits a `DataLoader.Validate(NSUrlResponse)` C# wrapper that
allocates an `SwiftIndirectResult` buffer, then calls a PInvoke entry point
whose signature does **not** take that buffer (returns `IntPtr` directly),
discards the returned pointer, and finally tries to read the result from the
unpopulated buffer.

The buffer is freed in `finally`, so we don't leak — but every call returns
whatever happened to be in freshly-allocated native memory, which the
generated C# then unmarshals as a `SwiftOptional<ExistentialContainer1>`.
Almost always reads as garbage `Some(…)` and constructs an `AnyError` over
random bits.

## Environment

- SwiftBindings.Sdk 0.10.0 / Runtime 0.10.0 / Apple 26.2.2
- .NET SDK 10.0.107, workload 10.0.107
- Xcode 26.3 / Swift 6.2.x
- macOS 26.x, arm64
- Source library: Nuke 13.0.5

## Repro

The Nuke 13.0.5 build itself is currently blocked by
[bug-0.10.0-nested-protocol-i-prefix.md](./bug-0.10.0-nested-protocol-i-prefix.md),
but `obj/Debug/net10.0-ios/swift-binding/Nuke.cs` is generated *before* the
C# compile fails, so the corrupt `Validate` wrapper is observable in the
generated source as soon as `BuildLibrary` is run:

```bash
cd swift-dotnet-packages
dotnet nuke BuildLibrary --library Nuke   # (will fail at csc — that's fine)
sed -n '10486,10523p' libraries/Nuke/obj/Debug/net10.0-ios/swift-binding/Nuke.cs
```

## Generated code (reduced)

```csharp
// Nuke.cs:10492
public static Swift.Foundation.AnyError? Validate(Foundation.NSUrlResponse response)
{
    unsafe
    {
        IntPtr responseHandle = response?.Handle ?? IntPtr.Zero;
        void* _cdeclBuf = null;
        try
        {
            var returnMetadata = TypeMetadata
                .GetTypeMetadataOrThrow<Swift.Foundation.AnyError?>();
            _cdeclBuf = NativeMemory.Alloc((nuint)returnMetadata.Size);
            var swiftIndirectResult = new SwiftIndirectResult(_cdeclBuf);   // [1] buffer prepared

            PInvoke_validate_0E1EED8E(responseHandle);                       // [2] called WITHOUT buffer

            var swiftResult = SwiftMarshal
                .MarshalFromSwiftObject<SwiftOptional<Swift.Runtime.ExistentialContainer1>>(
                    new IntPtr(swiftIndirectResult.Value));                  // [3] reads garbage
            return swiftResult.Case == SwiftOptionalCases.None
                ? null
                : new Swift.Foundation.AnyError(swiftResult.Some);
        }
        finally { NativeMemory.Free(_cdeclBuf); }
    }
}

// Nuke.cs:10520
[UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
[LibraryImport("Nuke",
    EntryPoint = "$s4Nuke10DataLoaderC8validate8responses5Error_pSgSo13NSURLResponseC_tYbFZ")]
private static partial IntPtr PInvoke_validate_0E1EED8E(IntPtr response);   // [4] wrong signature
```

Two bugs compound:

- **[4]** The PInvoke is declared as `IntPtr return, single argument`. The
  Swift function has signature
  `static func validate(response: URLResponse) -> (any Error)?`. An optional
  existential return is *indirect* in Swift's calling convention — the C#
  PInvoke must declare `(SwiftIndirectResult, IntPtr) -> void`, not
  `(IntPtr) -> IntPtr`.

- **[2]/[3]** The wrapper allocates a buffer for the indirect result but
  never passes it as the first argument. Then it reads from it as if Swift
  had populated it.

The `IntPtr` returned at [2] is also discarded, so even if Swift were
returning something direct, that value would be lost.

## Native ground truth

```text
swiftinterface (line 555):
  @Sendable public static func validate(response: Foundation.URLResponse)
      -> (any Swift.Error)?

mangled symbol decoded:
  $s4Nuke10DataLoaderC8validate8responses5Error_pSgSo13NSURLResponseC_tYbFZ
  → static Nuke.DataLoader.validate(
        response: __C.NSURLResponse
    ) -> Swift.Optional<any Swift.Error>
```

`Optional<any Error>` in Swift's calling convention:

- `any Error` is a 5-word existential container.
- `Optional<…>` of a multi-word existential is an aggregate larger than the
  direct-return register budget on every Apple ABI Swift currently supports.
- → returned via SIL `@out` / Clang `sret`, i.e. as the first argument.

So the correct PInvoke signature is roughly:

```csharp
[LibraryImport("Nuke", EntryPoint = "…")]
private static partial void PInvoke_validate_0E1EED8E(
    SwiftIndirectResult result,
    IntPtr response);
```

…and the call site must pass `swiftIndirectResult` as the first argument.

## Hypothesis

The generator's PInvoke-signature builder seems to have lost track of the
"return is indirect" flag for this method. The wrapper *body* still emits as
if the return were indirect (allocates the buffer, reads from it after the
call), so the body and the signature drifted out of sync.

Two plausible regression sites in `swift-dotnet-bindings`:

1. The ABI classifier that decides direct-vs-indirect for the return type
   may be returning `direct` for `Optional<Existential>`. (Other indirect
   returns in the same generated file — e.g. async result tuples — emit
   correctly, so it isn't *all* indirect returns.)
2. Or the PInvoke-signature emitter and the wrapper-body emitter are
   reading from two different sources of truth and the body's source still
   says "indirect" while the signature's source says "direct".

The mangled symbol `$s4Nuke10DataLoaderC8validate8responses5Error_pSgSo13NSURLResponseC_tYbFZ`
(`Yb` = `@Sendable`, no `Y` for `async`) is sync, so this isn't the
async-return path — it's the plain "return a multi-word aggregate"
indirect-return path.

## Impact

- **Correctness, not crash.** Reads `SwiftOptional<ExistentialContainer1>`
  from uninitialized native memory. `_cdeclBuf` was just `NativeMemory.Alloc`'d
  so it could be anything. Any caller of `DataLoader.Validate(...)` gets a
  garbage `AnyError` back (almost never `None`), which then attempts to
  construct an `AnyError` over invalid existential bytes — likely SIGSEGV
  on the first member access.
- **Scope.** Anywhere the generator emits an indirect-return wrapper for
  a *sync* method whose signature classifier mis-labels the return as
  direct. The Nuke binding gives one concrete example; the same bug is
  almost certainly latent in the other 14 libraries wherever a sync function
  returns an `Optional<Existential>`, an `Optional<ProtocolType>`, a large
  struct, etc. Worth a cross-library scan after the fix.

## Workaround

None on the consumer side — the generator regenerates on every build, so
hand-patches don't survive. Until 0.10.1 ships, callers should treat
`DataLoader.Validate` as unsafe to call from C#.

## Severity

**Correctness — High.** Memory-corruption-class bug. Method runs without
crashing the dispatcher (the `IntPtr` return from a wrong-signature PInvoke
is just discarded), but the returned C# object is constructed over
uninitialized memory.

Pair with the Nuke 13 nested-protocol fix
([bug-0.10.0-nested-protocol-i-prefix.md](./bug-0.10.0-nested-protocol-i-prefix.md))
in the next SDK ship — both touch the same library, both prevent shipping
Nuke 13 with confidence.
