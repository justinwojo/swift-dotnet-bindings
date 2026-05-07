# Bug: Generic-async wrapper emits `[LibraryImport(EntryPoint = "…_async")]` for a symbol that does not exist

> SDK 0.10.0 generator correctness bug. Discovered 2026-05-05 during the
> WeatherKit + MusicKit cross-package consumer-experience audit (Round 5).
> See
> [`audit-weatherkit-musickit-2026-05-05.md`](../../swift-dotnet-packages/audit-weatherkit-musickit-2026-05-05.md)
> finding **O-1**.

## Summary

When a Swift `async` method is generic over a `Sequence`-style protocol
(e.g. `func insert<S: Sequence>(_:position:) async throws where
S.Element == Album`), the generator emits a `[LibraryImport]` PInvoke
referencing an `_async`-suffixed mangled symbol whose **name follows
the unspecialized generic mangling** — but the wrapper xcframework only
exports `_async` cdecl trampolines for the *concrete-element*
overloads. The generic `_async` cdecl symbol does not exist. First call
into the generic overload throws
`EntryPointNotFoundException` at PInvoke resolution.

## Environment

- SwiftBindings.Sdk 0.10.0 / Runtime 0.10.0 / Apple 26.2.1
- .NET SDK 10.0.107, workload 10.0.107
- Xcode 26.3 / Swift 6.2.x / iPhoneOS26.2.sdk
- macOS 26.x, arm64

## Repro

```bash
sed -n '38266,38325p' apple-frameworks/MusicKit/obj/Debug/net10.0-ios26.2/swift-binding/MusicKit.cs
```

```csharp
// MusicKit.cs:38266
[LibraryImport(
    "@rpath/MusicKitSwiftBindings.framework/MusicKitSwiftBindings",
    EntryPoint = "$s8MusicKit11MusicPlayerC5QueueC6insert_2atyx_AC22EntryInsertionPositionOtKYaF…STRzAE0G0V7ElementRtzlF_async")]
[UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
internal static partial void PInvoke_insert_<HASH>(
    delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void> callback,
    IntPtr taskHolder,
    IntPtr taskIndex,
    IntPtr entrySwiftDirect,
    int position,
    IntPtr T_metadata,
    IntPtr T_STRz_pwt,
    IntPtr T_E_AC_EntryAC_pwt);
```

The mangled name encodes:
- `8MusicKit11MusicPlayerC5QueueC6insert_2at` — `MusicPlayer.Queue.insert(_:at:)`
- `STRzAE0G0V7ElementRtz` — generic constraint `where S : Sequence,
  S.Element == EntryInsertionPosition` (actually `Element == Entry`,
  abbreviated)
- `_async` — async-trampoline suffix appended by the generator

Verify against the wrapper binary:

```bash
nm -gU artifacts/wrappers/MusicKitSwiftBindings.xcframework/ios-arm64/MusicKitSwiftBindings.framework/MusicKitSwiftBindings \
    | grep insert_ | grep _async
```

Result: only the four concrete-element symbols appear:

```
__$s8MusicKit11MusicPlayerC5QueueC6insert_2at7MusicKit5AlbumO_async
__$s8MusicKit11MusicPlayerC5QueueC6insert_2at7MusicKit8PlaylistO_async
__$s8MusicKit11MusicPlayerC5QueueC6insert_2at7MusicKit4SongO_async
__$s8MusicKit11MusicPlayerC5QueueC6insert_2at7MusicKit5TrackO_async
```

The `STRzAE0G0V7ElementRtzlF_async` symbol is absent. The C# overload
matching `InsertAsync<S>(IEnumerable<S>, EntryInsertionPosition,
CancellationToken)` references nothing — it throws on first call.

## Hypothesis

Two cooperating bugs in the wrapper-emit + binding-emit pipeline:

1. **Wrapper-emit side:** the cdecl `@_cdecl` trampoline emitter sees a
   Swift `async` function with a generic-over-Sequence parameter and
   either skips it (`SkipReason: GenericConstraintShape`) or specializes
   it to the *concrete element types* it knows about (`Album`,
   `Playlist`, `Song`, `Track`) without emitting an additional
   unspecialized `_async` trampoline.
2. **Binding-emit side:** the C# side independently emits the
   `[LibraryImport]` based on the *generic* Swift signature, computing
   the mangled name as if the trampoline had been emitted. The two
   sides disagree on what was emitted.

The fix is structural: either the wrapper emits the unspecialized
generic `_async` trampoline (preferred — keeps the API surface
generic), or the binding emits only the concrete-element overloads
that match real wrapper exports (consumer would then call
`InsertAsync(IEnumerable<Album>)` etc. directly).

## Impact

The generic `MusicPlayer.Queue.InsertAsync<S>(IEnumerable<S>,
EntryInsertionPosition, CancellationToken)` overload — the natural
"enqueue heterogeneous items" entry point — is unreachable from C#.
Any consumer that compiles against it gets a clean compile and a
PInvoke crash on first call.

Compounds with **O-3**
([`bug-0.10.0-ienumerable-iswiftstruct-raw-intptr-serialization.md`](bug-0.10.0-ienumerable-iswiftstruct-raw-intptr-serialization.md)) — the
four concrete-element overloads that *do* have `_async` symbols are
themselves broken by raw-IntPtr serialization + `using var` lifetime.
So even falling back to the typed overloads doesn't recover.

## Severity

**High.** Surface-load-bearing API (queue enqueue is one of MusicKit's
two flagship surfaces). Compounded by O-3 → total Queue.InsertAsync
family unusability.

## Fix gate

`MusicKit.cs:38266-38325` should declare an `EntryPoint` that resolves
in the wrapper binary's symbol table — either by emitting the generic
`_async` trampoline on the wrapper side, or by removing the generic
overload from the C# binding.

A general-purpose post-emit pass that runs `nm -gU` on the wrapper
binary and verifies every `[LibraryImport(EntryPoint = ...)]` resolves
would catch this defect class at SDK-build time. Worth doing — see
**O-2** for a sibling defect that needs the same gate.

## Status — RESOLVED in 0.10.0 (CSM-async pairing-filter parity)

The CSM-async pipeline now applies the same bilateral
associated-type filter that the sync path uses. `IsEmittableAsyncPairing`
(in `ConcreteProtocolSpecializationEmitter.Async.cs`) takes the
`MethodDecl` + `TypeDecl` and consults
`DoesPairingSatisfyAssociatedTypeConstraints` against the type
database before reserving a wrapper symbol. Same-type
floors (`where S.Element == Album`) and class-subtype bounds
(`where S.Element : Animal`) reject mismatched conformers (UInt8,
SongItem, AlbumItem, …) before any `_async` cdecl trampoline is
reserved. Only conformers whose recorded `Element` exactly matches —
or, for class targets, sits anywhere up the
`TypeRecord.SuperclassTypeName` chain — survive into emission.

The `[LibraryImport]` set the C# side emits is now exactly the set
of `_async` wrapper symbols the Swift side compiles into the dylib.
First call into a generic-async overload no longer
`EntryPointNotFoundException`s; it dispatches into the matching
closed-instantiation cdecl trampoline.

**Regression coverage:** the BindingTests fixture
`Async/AsyncGenericSequence.swift` declares
`AnimalAsyncRoster.insertAsync<S>(...)
async throws where S.Element : Animal` — the same class-inheritance
bound shape as the sync `AnimalRoster.insert` fixture, but routed
through the CSM-async path. Generated output now contains exactly two
`InsertAsync` overloads (Animal + Dog); pre-fix the cartesian product
emitted overloads for UInt8, SongItem, AlbumItem, and ArtistItem too,
each pointing at a non-existent
`SBW_CSM_…_Swift_Array_<T>_insertAsync_*_async` symbol. Runtime tests
in `RuntimeTestsApp/Async/AsyncGenericSequenceTests.cs` exercise the
Animal overload, the Dog-subclass overload (proving the class-subtype
walk fired), and the async-throws plumbing on the surviving path.

The post-emit wrapper-export cross-reference gate originally proposed
under "Fix gate" remains a worthwhile safety net for *future*
shapes that escape the bilateral filter; that is tracked separately
on the roadmap and is no longer load-bearing for this specific bug.
