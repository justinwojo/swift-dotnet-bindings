# UnsafeMutableRawBufferPointer

This document covers the projection of Swift's `UnsafeMutableRawBufferPointer`
parameter type into C#. It is the writable companion to the existing
`UnsafeRawBufferPointer` → `ReadOnlySpan<byte>` projection (see
[`binding-overview.md`](binding-overview.md) for the broader marshalling
philosophy and [`memory-management.md`](memory-management.md) for memory
ownership rules).

## Surface

Swift surface that this projection covers:

```swift
public struct ImageDecoder {
    // Synchronous, nonescaping mutable raw buffer parameter.
    public func fill(into buffer: UnsafeMutableRawBufferPointer) -> Int { ... }
}
```

C# surface produced by the generator:

```csharp
public struct ImageDecoder {
    public nint Fill(System.Span<byte> buffer);
}
```

The C# wrapper pins the span and forwards a `(IntPtr, nint)` pair to a
synchronous `@_cdecl` Swift thunk that reconstructs the
`UnsafeMutableRawBufferPointer` for the inner call. This mirrors the
read-only path bit-for-bit; the only difference is the C# parameter type
(`Span<byte>` vs `ReadOnlySpan<byte>`) and the Swift reconstruction type
(`UnsafeMutableRawBufferPointer(...)` vs `UnsafeRawBufferPointer(...)`).

## v1 scope

In scope:

- `UnsafeMutableRawBufferPointer` as a **synchronous, nonescaping**
  parameter on free functions, methods, and constructors. Projected as
  `System.Span<byte>` in C#.
- Aliasing across multiple parameters of the same call (see *Aliasing*
  below).
- Empty (zero-length) buffers.
- Sliced buffers — `Span<byte>.Slice` already produces a
  ranged view; the projection is uniform over the slice.

Out of scope (see *v2 — deferred*):

- Escaping closure parameters (`@escaping (UnsafeMutableRawBufferPointer) -> Void`).
- Async function parameters (`func f(buffer: UnsafeMutableRawBufferPointer) async`).
- Return-position buffers (`func f() -> UnsafeMutableRawBufferPointer`).
- A first-class mutable wrapper class on the C# side (a managed
  `MutableRawBuffer`-style projection).
- Bare `UnsafeMutableRawPointer` (no `Buffer`) — see *Bare pointer
  projection*.
- `UnsafeBufferPointer<T>` / `UnsafeMutableBufferPointer<T>` (typed
  buffer pointers) — separate feature, not covered here.

Each out-of-scope shape is rejected with `SWIFTBIND104` and the binding
emits no member, so consumers see the warning at generate time rather
than a runtime crash. The existing read-only `UnsafeRawBufferPointer`
projection has the same v1 boundaries; this feature mirrors them
exactly.

## Lifetime

The `Span<byte>` is valid **only for the duration of the synchronous
Swift call**. Concretely:

- The C# wrapper opens a `fixed (byte* p = span)` block immediately
  before the P/Invoke and closes it immediately after the call returns.
- The pointer that Swift receives is the pinned address of the C#-owned
  memory.
- Once the Swift call returns and the `fixed` block ends, the GC is free
  to move the underlying allocation. **Any pointer that Swift retained
  past the synchronous return is dangling.**

This is why escaping closures, `async` parameters, and return-position
buffers are out of scope: each of those would require the pointer to
outlive the `fixed` block, which the projection cannot guarantee. v1
fails closed on those shapes rather than silently producing a use-after-free.

A future v2 with a managed mutable wrapper class can lift these
restrictions by switching from `fixed`-pin to GCHandle-pin (or to a
native-allocated buffer the wrapper owns), at the cost of an additional
allocation and explicit `Dispose`.

## Aliasing — Swift's law of exclusivity

Two questions: *(a)* can the same memory be passed twice to the same
Swift call via two `UnsafeMutableRawBufferPointer` parameters, and *(b)*
does Swift's exclusive-access rule trip on aliasing?

**Yes** to (a), and the answer to (b) is that **the law of exclusivity
does not apply**. Swift's exclusive access checking
([SE-0176](https://github.com/swiftlang/swift-evolution/blob/main/proposals/0176-enforce-exclusive-access-to-memory.md))
is enforced on `inout` parameters and direct mutation of variables —
not on pointer values held inside an `UnsafeMutableRawBufferPointer`.
The buffer pointer is a *value type* (a `(start, count)` pair); two
buffer pointers may legally aim at overlapping ranges of the same
backing memory, and Swift will not diagnose that.

Correctness when aliased is the Swift function's responsibility. The
projection guarantees only that:

- Each `Span<byte>` parameter is independently pinned (the `fixed`
  blocks nest; see [Pinning](#pinning) below).
- The pointer Swift sees for two aliased C# spans really does point at
  the same backing memory — i.e. the projection does not silently copy.

If a Swift API specifies "the two buffer parameters must not overlap",
that contract is enforced at the API level, not at the projection
level.

## Write-back semantics

The C# caller observes Swift's mutations after the call returns,
because the Swift thunk operates directly on the pinned C# memory.
There is no copy in either direction:

```csharp
Span<byte> buffer = stackalloc byte[16];
buffer.Fill(0);
decoder.Fill(buffer);          // Swift writes bytes into `buffer`.
Console.WriteLine(buffer[0]);  // Observes Swift's first byte.
```

This matches the natural Swift call-site semantics ("the bytes Swift
writes through the pointer are visible to the caller") and incurs no
managed allocation on the hot path.

## Sourcing the buffer

`Span<byte>` is the universal target type, so any C# memory shape that
implicitly converts to `Span<byte>` is supported:

- `byte[]` — managed array, pinned by the GC during `fixed`.
- `stackalloc byte[N]` — stack allocation; no GC interaction.
- A pinned native buffer (`new Span<byte>(intPtr.ToPointer(), len)`) —
  pin is a no-op since native memory does not move.
- A `Memory<byte>.Span` slice — the underlying `MemoryManager<byte>`
  controls pinning; the projection is agnostic.

The `fixed` statement uses `Span<byte>.GetPinnableReference()`, which
returns a managed reference even for empty spans (a sentinel reference
to a zero-length region). The Swift `@_cdecl` thunk reconstructs an
`UnsafeMutableRawBufferPointer(start: nil, count: 0)` for empty spans —
matching the read-only path.

## Pinning

The C# wrapper emits a `fixed` block per buffer parameter, immediately
inside the wrapper body's `unsafe` context:

```csharp
public unsafe nint Fill(Span<byte> buffer)
{
    fixed (byte* bufferPinnedPtr = buffer)
    {
        return PInvoke_fill_<hash>((IntPtr)bufferPinnedPtr,
                                   (nint)buffer.Length,
                                   _payload.DangerousGetHandle());
    }
}
```

Multiple buffer parameters yield nested `fixed` blocks; the
`AssertRawBufferFixedDepthZero` check at the tail of `WrapperEmitter`
catches mismatched start/end pairs at generate time.

The `fixed` block guarantees the GC will not move the underlying
allocation until the block exits. For `byte[]`, this means the array
itself is pinned for the duration of the synchronous Swift call. For
`stackalloc` and native pointers, pinning is structural — the address
was already stable.

We chose `fixed` over `MemoryMarshal.GetReference` + manual
`Unsafe.AsPointer` because:

1. The read-only path uses `fixed` already; staying consistent keeps
   the emitter simpler (one helper, not two).
2. `fixed` is statically scoped — start and end are paired tokens, so
   leaks are caught by C# scope rules. Manual pinning would require
   explicit `try/finally` and a depth counter.
3. The CLR optimizes `fixed (byte* p = span)` into a no-op pin for
   stack and native memory.

## P/Invoke and `@_cdecl` shape

C# P/Invoke signature (split):

```csharp
[UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
[DllImport(...)]
private static extern nint PInvoke_fill_<hash>(
    IntPtr bufferPtr,
    nint   bufferLen,
    IntPtr self);
```

Swift `@_cdecl` thunk (matched):

```swift
@_cdecl("SBW_TestLib_ImageDecoder_fill_<hash>")
public func SBW_TestLib_ImageDecoder_fill_<hash>(
    _ bufferPtr: UnsafeMutableRawPointer?,
    _ bufferLen: Int,
    _ self_: UnsafeMutableRawPointer
) -> Int {
    let bufferVal = UnsafeMutableRawBufferPointer(start: bufferPtr, count: bufferLen)
    let selfVal = self_.assumingMemoryBound(to: ImageDecoder.self).pointee
    return selfVal.fill(into: bufferVal)
}
```

The pointer half is `UnsafeMutableRawPointer?` (optional) — not
`UnsafePointer<UInt8>?` — because `UnsafeMutableRawBufferPointer.init(start:count:)`
takes an optional mutable raw pointer, and an empty C# span pins to a
non-null sentinel that we want Swift to interpret as
`UnsafeMutableRawBufferPointer(start: nil, count: 0)` when count is zero.
(The read-only path uses the same convention with the immutable
counterparts.)

## Bare pointer projection — explicit non-decision

Swift also has `UnsafeMutableRawPointer` (no `Buffer`). The natural
question is whether v1 should also project that to `Span<byte>`. **No.**
A bare `UnsafeMutableRawPointer` carries no length, so a `Span<byte>`
projection has no bounds. Two unattractive options:

- Make the C# parameter `(Span<byte> buffer)` and silently truncate the
  span to length 0 — pointless and bug-prone.
- Force the C# caller to pass `(IntPtr, nint)` separately — that's the
  ABI of the buffer pointer already, defeating the point of the
  projection.

Existing behavior for bare `UnsafeMutableRawPointer` is unchanged: the
type appears in `IsSwiftPointerType` (in `MethodClosureBridge.cs`),
which excludes it from closure bridging, and the rest of the generator
falls through to whatever fallback applies (typically
`Unsupported`-skip). v2 may revisit this as part of the "mutable
wrapper class" workstream, where the wrapper carries explicit length
metadata.

## SWIFTBIND104

When a method's signature contains an `UnsafeMutableRawBufferPointer`
in an out-of-scope position, the generator skips the member and emits
`SWIFTBIND104`:

> **SWIFTBIND104**: Skipping `<member>` because
> `UnsafeRawBufferPointer` / `UnsafeMutableRawBufferPointer` appears in
> an unsupported position (return type, async method parameter, or
> escaping closure parameter). v1 supports synchronous, nonescaping
> parameters only. See
> `src/docs/Design/unsafe-mutable-raw-buffer-pointer.md`.

The validator emits SWIFTBIND104 in two places:

1. Return-position — any method whose return type is one of the buffer
   pointer variants.
2. Async parameter — any `async` method that takes a buffer pointer
   parameter (the `fixed` block scopes only the synchronous P/Invoke
   start; an awaiting continuation would leave Swift with a dangling
   pointer).

Escaping-closure parameter rejection happens upstream in
`MethodClosureBridge.IsSwiftPointerType`, which excludes both buffer
pointer variants from closure bridging — those methods are skipped
before reaching the SWIFTBIND104 gate.

`UnsafeRawBufferPointer` (read-only) shares the same constraints, but
its existing emission path silently falls through to "Unsupported" for
out-of-scope shapes. v1 does not retroactively add a warning for the
read-only path — that is a separate cleanup. The new warning is scoped
to the writable variant introduced here.

## Implementation map

For maintainers — files touched and the role each plays:

| File | Role |
|------|------|
| `Marshaler/MarshallingHelpers.cs` | Adds `IsUnsafeMutableRawBufferPointer` and a unified `IsAnyUnsafeRawBufferPointer` predicate. |
| `Marshaler/MarshalledType.cs` | `RawBufferPtr` / `RawBufferLen` records remain — the C ABI is identical for both variants. Doc comment updated to mention both. |
| `Emitter/StringEmitter/CdeclParamMapper.cs` | Adds the mutable case: produces `UnsafeMutableRawPointer?` + `Int` cdecl params and an `UnsafeMutableRawBufferPointer(start:count:)` reconstruction. |
| `Emitter/StringEmitter/Handler/MethodSignature.cs` | Public C# signature emits `Span<byte>` for the mutable variant; `ReadOnlySpan<byte>` unchanged for read-only. |
| `Emitter/StringEmitter/Handler/PInvokeEmitter.cs` | Splits the mutable buffer parameter into `(RawBufferPtr, RawBufferLen)`, same as read-only. |
| `Emitter/StringEmitter/Handler/WrapperEmitter.cs` | `unsafe`-body gating, `fixed` block emission, and assertion-pairing all extended to cover both variants. |
| `Emitter/ThunkEmitter/NativeThunkEmitter.cs` | Forces the `@_cdecl` wrapper path for both variants — the Swift native ABI passes a 16-byte struct in two registers, which doesn't match the split. |
| `Emitter/StringEmitter/MemberEmissionValidator.cs` | Removes the blanket rejection of mutable buffer pointers; replaces it with a SWIFTBIND104 skip for return-position shapes. |
| `Emitter/StringEmitter/ConstructorWrapperEmitter.cs` | `HasUnsupportedBufferPointerParameter` no longer lists the mutable variant (constructors now flow through). |

Existing patterns we reuse rather than re-invent:

- The read-only path's `RawBufferPtr` / `RawBufferLen` `MarshalledType`
  records — the C ABI is byte-identical.
- The `_rawBufferFixedDepth` counter and `AssertRawBufferFixedDepthZero`
  invariant — extended to cover both predicates.
- `MethodClosureBridge.IsSwiftPointerType` — already excludes both
  buffer pointer variants from closure bridging, so escaping/async
  closure cases hit the `MemberValidationPipeline` rejection without
  any new code.

## v2 — deferred

These are explicitly out of scope for v1. Each lives in
`src/docs/roadmap.md`:

- **Escaping closure parameter**: requires `Span<byte>` lifetime to
  outlive the synchronous frame. Likely solution: native-allocated
  buffer copied through a managed `MutableRawBuffer` class with
  explicit `Dispose`, or a sentinel that disallows write-back past the
  closure invocation.
- **Async parameter**: same lifetime problem. The async harness already
  copies value-typed parameters into the heap-allocated continuation
  buffer; a buffer pointer would need either a copy (defeats
  zero-allocation) or an explicit pin until the continuation completes
  (`SafeHandle`-style ownership).
- **Return-position buffer**: ambiguous ownership (Swift-allocated or
  caller-allocated?). Likely solution: project to a managed
  `MutableRawBuffer` class that wraps the Swift-side allocation and
  releases on `Dispose`.
- **Mutable wrapper class**: a first-class C#-side
  `Swift.UnsafeMutableRawBuffer` (or similar) carrying both the pointer
  and a `Free` callback. Subsumes the three points above.
- **Typed buffer pointers**: `UnsafeBufferPointer<T>` and
  `UnsafeMutableBufferPointer<T>` — needs element-type-aware
  marshalling, generic instantiation, and stride alignment. Different
  feature.

When v2 lands, this doc should grow a "v2" section rather than be
rewritten — v1 semantics will continue to apply to direct
`Span<byte>` callers.
