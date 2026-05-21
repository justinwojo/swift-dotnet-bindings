0.11.2 is a small patch on top of 0.11.1. It restores native-runtime packing for auto-discovered binding projects (so consumers no longer hit `DllNotFoundException` at runtime), fixes a `SIGSEGV` on property and subscript accessors of constrained generic classes, and gives 22 Foundation Swift-overlay types proper typed bindings instead of falling back to `NSObject`.

## Highlights
- **Auto-discovered binding packages ship native runtime slices again (#40)** — `dotnet pack` on a binding project with no explicit `<SwiftFramework>` was producing managed-only nupkgs; consumers loaded the managed assembly and threw `DllNotFoundException` on the first P/Invoke. `_ConfigureSwiftBindingPack` now gates on stable properties evaluated after `_DiscoverSwiftFrameworks`, `SWIFTBIND038` fails closed if the wrapper is still missing after discovery, and a new pack-gate leg exercises the exact regression shape.
- **Constrained-generic class accessors no longer `SIGSEGV`** — The C# P/Invoke side passes both a metadata pointer and a protocol-witness-table pointer for `class Box<T: SomeProtocol>` shapes, but the Swift `@_cdecl` wrappers were only absorbing the metadata, so the unabsorbed PWT slid into the `self_` slot and `Unmanaged.fromOpaque(self_)` walked garbage. Subscript accessors additionally weren't inheriting their parent type's generic parameters in the parser, so call arity was wrong even before the wrapper. End-to-end coverage now exercises getter and setter round-trips on both property and subscript shapes.
- **Foundation Swift-overlay classes get typed `Foundation.NS*` bindings** — Twenty-two overlay types (`ByteCountFormatter`, `ValueTransformer`, `NetService`, `Pipe`, `PropertyListSerialization`, the formatter family, etc.) now route to their typed `Foundation.NS*` counterparts in the .NET ref assemblies instead of falling back to `NSObject`, giving callers proper static typing on the generated bindings. Three macOS-conditional types (`Process`, `Host`, `DistributedNotificationCenter`) and seven permanent-`NSObject` types (`MessagePort`, `SocketPort`, the `XML*` family) stay on `NSObject` by design.

## GitHub issues closed in this release

Closed with this release:

- [#40](https://github.com/justinwojo/swift-dotnet-bindings/issues/40) — `dotnet pack` drops native runtimes from auto-discovered bindings, causing `DllNotFoundException` at runtime

## Packages

| Package                  | Version |
|--------------------------|---------|
| SwiftBindings.Runtime    | 0.11.2  |
| SwiftBindings.Sdk        | 0.11.2  |
| SwiftBindings.Templates  | 0.11.2  |
| SwiftBindings.Apple      | 26.2.3  |

`SwiftBindings.Apple` tracks the Apple SDK train independently and is unchanged in this release. See the [GitHub wiki](https://github.com/justinwojo/swift-dotnet-bindings/wiki) for installation and usage.
