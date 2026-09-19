# SwiftBindings 0.20.0

This release fixes crashes and memory bugs in generated bindings, several of them under Mono, and gets more of your library into C#.

## Highlights

- **Fewer crashes under Mono** — Mono can lose track of its own state across certain kinds of Swift calls, and the app aborts. Most of the affected calls now go through a generated C wrapper that avoids the problem.
- **Fewer memory bugs when passing values to Swift** — strings, class instances and some structs passed to Swift initializers and setters could be released one time too many, which led to crashes or corrupted values. Swift types that can't be copied are now moved or skipped with a reason, rather than stopping the app when used.
- **Generic types return correct values** — static members of a generic type, members of a type nested inside one, and generic struct results could crash or return garbage; they now go through wrappers that pass Swift what it expects. A Swift object conforming to a class-only protocol no longer crashes when handed back to Swift.
- **One problem member no longer takes down the whole library** — when Swift can't compile the wrapper for one member, only that member is dropped and reported. A member named like a namespace or standard type (`System`, `Swift`, `Exception`, `IntPtr`) no longer breaks the generated code around it, and a protocol that gained requirements in a newer OS version no longer breaks the binding for the whole module.
- **More of your API binds** — callbacks that modify an `inout` argument, generic struct properties, constrained generic methods (which now keep their own name), initializers with default arguments, C pointers and function-type typedefs in Objective-C headers, and initializers that differ only by argument label (these become `CreateWith…` factory methods).

## Behaviour changes

- **A Swift `Int` or `UInt` property exposed as `int`/`uint` throws `OverflowException` when the value doesn't fit** — it used to cut the value down to 32 bits without telling you. Each such property now has a `…Native` sibling returning `nint`/`nuint` with the full value.
- **Swift collections that don't start at index 0 now index from 0 in C#** — for example, an `ArraySlice` exposed as `IReadOnlyList` used to need the Swift index; it now behaves like any other .NET list.
- **Swift errors thrown by regular methods are typed** — they surface as `SwiftException<TError>` carrying your library's error value, as async methods already did. It derives from `SwiftException`, so existing `catch` blocks still work.
- **A few generic members now throw instead of returning wrong results** — where the Swift function needs information the call cannot pass, the member keeps its declaration so your code still compiles, carries an `SB0009` marker, and throws when called. Methods from a constrained extension are no longer offered on instantiations Swift doesn't accept.
- **`Any` values arrive as the value itself** — an `Any` property or callback argument used to hand you Swift's raw container, and passing a plain C# value to an `Any` parameter threw `InvalidCastException`. A callback that returns `Any` is no longer bound, since it crashed.

## Other fixes

- `SwiftBindings.Apple` types such as `Locale.Language` now work in NativeAOT apps without adding your own trimming roots.
- A `DispatchQueue` you created could be released while a native call was still using it, and reading a string out of an `Any` leaked memory on every call.
- A tuple returned from Swift could read memory that had already been freed.
- Supplying all of a package's frameworks together no longer fails with `SWIFTBIND119` because an unrelated sibling (a test-support module) imports something the binding never loads.
- A binding project that targets a .NET version older than 10 now fails immediately with a clear `SWIFTBIND010` error, rather than much later during generation.

## Packages

| Package                  | Version |
|--------------------------|---------|
| SwiftBindings.Runtime    | 0.20.0  |
| SwiftBindings.Sdk        | 0.20.0  |
| SwiftBindings.Templates  | 0.20.0  |
| SwiftBindings.Apple      | 26.2.9  |

See the [GitHub wiki](https://github.com/justinwojo/swift-dotnet-bindings/wiki) for installation and usage.
