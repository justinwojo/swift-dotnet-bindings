# Bug: Proxy `InitializeVtable()` references `Set*_vtable` cdecl symbols not exported by the wrapper

> SDK 0.10.0 generator correctness bug. Discovered 2026-05-05 during the
> WeatherKit + MusicKit cross-package consumer-experience audit (Round 5).
> See
> [`audit-weatherkit-musickit-2026-05-05.md`](../../swift-dotnet-packages/audit-weatherkit-musickit-2026-05-05.md)
> finding **O-2**.

## Summary

For a Swift protocol with C#-side proxy implementations (so consumers
can implement Swift protocols in C#), the generator emits a static
`InitializeVtable()` that calls `NativeMethods.SetXxx_vtable(...)` on
each protocol method. The wrapper xcframework only exports a fraction
of these `Set*_vtable` symbols. Loading any of the affected proxy
types throws `EntryPointNotFoundException` during the static
constructor — **before** the consumer's protocol implementation is
even reached.

This bricks every C#-side custom implementation of an affected
MusicKit protocol.

## Environment

- SwiftBindings.Sdk 0.10.0 / Runtime 0.10.0 / Apple 26.2.1
- .NET SDK 10.0.107, workload 10.0.107
- Xcode 26.3 / Swift 6.2.x / iPhoneOS26.2.sdk
- macOS 26.x, arm64

## Repro

Pick any of the affected proxy types in MusicKit:

```bash
sed -n '46803,46850p' apple-frameworks/MusicKit/obj/Debug/net10.0-ios26.2/swift-binding/MusicKit.cs
```

```csharp
// MusicKit.cs:46803  (MusicLibrarySectionRequestableProxy)
internal static void InitializeVtable()
{
    NativeMethods.SetMusicLibrarySectionRequestable_vtable(
        &MusicLibrarySectionRequestableProxy_VtableImpl);
}

[LibraryImport(
    "@rpath/MusicKitSwiftBindings.framework/MusicKitSwiftBindings",
    EntryPoint = "SetMusicLibrarySectionRequestable_vtable")]
internal static partial void SetMusicLibrarySectionRequestable_vtable(
    delegate* unmanaged[Cdecl]<…> impl);
```

Verify against the wrapper binary:

```bash
nm -gU artifacts/wrappers/MusicKitSwiftBindings.xcframework/ios-arm64/MusicKitSwiftBindings.framework/MusicKitSwiftBindings \
    | grep -i set | grep _vtable
```

Result: only **2** `Set*_vtable` symbols appear in the export table:

```
_SetMusicVideoFilter_vtable
_SetMusicDeveloperTokenProvider_vtable
```

The remaining ~10 proxies (`MusicLibrarySectionRequestableProxy`,
`MusicLibrarySearchableProxy`, `MusicCatalogChartRequestableProxy`,
`MusicLibrarySectionedRequestableProxy`,
`LibraryPlaylistEntrySortPropertiesProxy`, plus the broader
`MusicItem`-family protocol proxies at `MusicKit.cs:47700`,
`:53552`, …) reference `Set*_vtable` symbols that the wrapper never
exports.

C# `static` constructor semantics ensure `InitializeVtable()` runs the
first time the proxy type is touched. PInvoke resolution fails before
the body executes. The exception masquerades as a `TypeInitializationException`
with `EntryPointNotFoundException` as inner.

## Hypothesis

Asymmetric emission between the protocol-vtable wrapper emitter and
the C# proxy emitter. The C# side assumes "every protocol that has a
proxy gets a `Set<Protocol>_vtable` cdecl trampoline." The Swift
wrapper side has additional preconditions (probably "all protocol
methods are bridgeable" or "protocol has no associated types") that
filter out roughly 80% of MusicKit's protocols. The C# side does not
read the wrapper-side filter result, so it emits the proxy +
`InitializeVtable` regardless.

Two structurally clean fixes:

- **Wrapper side:** emit a stub `Set<Protocol>_vtable` trampoline for
  every proxy the C# side will emit, even if the trampoline body is
  `__builtin_trap()` — turns a static-init crash into a
  call-site-when-implementing crash with a clearer message.
- **Binding side:** check the wrapper's symbol table during emit and
  decline to emit the proxy at all when its trampoline isn't exported.
  Fail loud at build time. (Same generator post-emit verification pass
  as O-1.)

The latter is cleaner because there's no point shipping a proxy that
can't be invoked.

## Impact

**Bricks every C#-side custom implementation of any of the ~10
affected MusicKit protocols.** Concretely: a consumer cannot implement
`IMusicLibrarySearchable` in C# and pass it to a `MusicLibraryRequest`,
because the type loader fails before reaching the consumer's class.

The two surviving proxies (`MusicVideoFilter`,
`MusicDeveloperTokenProvider`) work — confirming the proxy
architecture is sound; the issue is wrapper-export coverage.

## Severity

**High.** Bricks the entire customizable-protocol surface of MusicKit
from C#. Consumers must use Swift shims to implement these
protocols, defeating the binding's purpose for any non-trivial use.

## Fix gate

After fix: loading every `*Proxy` type from `MusicKit.cs` (e.g. via a
test that iterates all types ending in `Proxy` and calls
`RuntimeHelpers.RunClassConstructor(t.TypeHandle)`) should succeed
without `EntryPointNotFoundException`. Today only 2 of ~12 succeed.

A wrapper-symbol-table verification pass (covering both this defect
and **O-1**) at SDK-build time would catch this class of defect
before ship.
