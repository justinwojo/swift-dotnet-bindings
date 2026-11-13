# Binding Closures

Closures in Swift come in two general forms: escaping and non-escaping. A closure is considered escaping if it continues to exist outside of the context that created it. For example, a closure parameter to a function that gets stored in a member variable in a class *must* be marked `@escaping` or the compiler will flag it as an error.

In either case, Swift closures, unlike some other languages, are full-fledged closures that can have both free and bound variables within the body of the closure.

The distinction between the two is that escaping closures that capture free variables use a dynamically allocated object to contain any captured variables. This object is reference counted. Non-escaping closures use stack allocated memory to contain any captured variables.

Beyond the broad classification of escaping/non-escaping, closures can also be async and they can throw.

Internally, closures are represented as 2 machine words:

- Pointer to a function entry point for the closure (see below for details)
- Pointer to the data context object for captured free variables or 0/null if there is no context object. When the closure is called, this will be in the context/self register.

## Language Parity Mismatches

None in particular. From a language standpoint, they both align in capability although the implementation are different.

Swift has a delightful bit of syntax sugar that allows you to supply a closure at the call site in a way that it looks like inline code.
If you have a function like this in Swift:

```swift
public func sorter<T>(arr: [T], by: (T, T) -> Int) { /* implementation not important */ }

```

And `sorter` can be called by any of the following means:

```swift
private func sort0 (a: Int, b: Int) -> Int {
    return a - b
}

let arr = [7, 1, 27]
sorter(arr: a, by: sort0) // 1: sort0 is your closure
sorter(arr: a, by: { a, b in return a - b}) // 2: inline closure
sorter(arr: a) { a, b in  // 3: trailing closure
    return a - b
}
```

The last case is a trailing closure which is exactly equivalent to the second inline closure

## ABI Differences

Closures follow the same calling conventions as functions.

With support for Swift calling conventions in the runtime, we should be insulated from issues in the ABI differences.

We would need to be aware reference counting of the context object. Closures are more or like like this:

```swift
public struct EscapingClosure {
    public var entryPoint: OpaquePointer
    public var context: AnyObject?
}

public struct Closure {
    public var entryPoint: OpaquePointer
    public var context: OpaquePointer?
}
```

When invoking the closure, the self register needs to be set to contents of the context pointer.
So essentially:

```asm
mov entryPoint[closure], rax
mov context[closure], r13
jsr [rax]
```

or something similar.

One interesting thing is that the Swift compiler writes closure with arguments for free variables as well as a forwarder.

For example, if I have a function that returns a closure like this:

```swift
public func getSummer (a: Int) -> (Int) -> Int {
    return { (b: Int) in
        a + b
    }
}
```

Then the compiler will write this:

```Swift
private func getSummerImplementation(a: Int, b: Int) -> Int {
    return a + b
}
```

And a forwarder that looks like this:

```asm
move 10[r13], rsi // b goes into argument 2
jmp _getSummerImplementation
```

## Runtime Differences

Ideally, we would like to be able to pass C# delegates to Swift functions or store them into Swift types and have them call back into the right place.  The problem with this is that it would be essentially a reverse p/invoke so an arbitrary C# delegate is incompatible as is, but with support from the runtime, this should be less of an issue, but there are still some things that we would need to care about.

If there is a case when we can't directly adapt a Swift closure into C#, there are some options that are available to us. In BTfS, which has no benefits from the runtime, this is done by converting Swift adapters into a more general form:

Given a closure of the form `(arguments) -> return`, this can be converted into the form: `(UnsafeMutablePointer<return>, UnsafeMutablePointer<(arguments)>)->()` For example:

```swift
public func callsIntoCSharp (a: @escaping (Int, SomeStruct, Bool) -> SomeOtherStruct) {
    let a_adapter = { (ret: UnsafeMutablePointer<SomeOtherStruct>, args: UnsafeMutablePointer<(Int, SomeStruct, Bool)>) in
        let (i, ss, b) = args.pointee
        ret.initialize(to: a(i, ss, b))
    }
    callToCSharpMethod (a_adapter)
}
```

In this case, the original closure gets captured by `a_adapter`. Before calling the adapter, C# obviously has to create the argument tuple, space for the return value (allocated but not initialized) and can call it because effectively the delegate signature has become `delegate void csAdapter (IntPtr ret, IntPtr args)`. A similar process is used to get C# closures into Swift.

One obvious problem here is that this only works with escaping closures. If rewritten with a non-escaping closure, the act of capturing the original closure in the new one is flagged by the Swift compiler as an error. Fortunately, Swift has a workaround to this via the function [`withoutActuallyEscaping`](https://developer.apple.com/documentation/swift/withoutactuallyescaping(_:do:)/), for which the previous code can be rewritten as this:

```swift
public func callsIntoCSharp (a: (Int, SomeStruct, Bool) -> SomeOtherStruct) {
    withoutActuallyEscaping (a) { a_escaping in
        let a_adapter = { (ret: UnsafeMutablePointer<SomeOtherStruct>, args: UnsafeMutablePointer<(Int, SomeStruct, Bool)>) in
            let (i, ss, b) = args.pointee
            ret.initialize(to: a_escaping(i, ss, b))
        }
    }
    callToCSharpMethod (a_adapter)
}
```

Please note that with runtime support, this shouldn't be necessary, but this may be important for future reference.

In running the other direction, we would need a way to convert a C# closure into something that is callable from Swift. The approach in BTfS is heavy handed because of the lack of runtime support. Given a C# closure, we create a handle to it, then call into a Swift routine which generates a swift closure that calls back into C# with a pointer to argument and return as before, but now with the handle and a `@convention (c)` function pointer to goes back into to a C# routine that unpacks the arguments, uses the handle to get the original C# closure and calls it. We should be able to do better than this.

## Idiomatic Differences

The main idiomatic difference has to do with the escaping/non-escaping varieties of closure. Obviously, C# doesn't make this distinction. As such, if a C# method gets passed a delegate from Swift that is non-escaping, it is incumbent upon the user to never store it. We can make this somewhat better by putting an attribute on such delegates that flags it as a non-escaping and create a Roslyn analyzer that looks for usage that would violate that.

## Current Implementation: @_cdecl Wrapper Architecture

The generator uses `@_cdecl` Swift wrapper functions as the primary ABI boundary. For methods with closure parameters, the wrapper:

1. **Receives closure arguments as C-compatible function pointers** — `@convention(c)` instead of Swift thick closures
2. **Converts to Swift closures inside the wrapper** — the wrapper constructs a Swift closure that calls back through the C function pointer
3. **Handles escaping closure semantics** — the wrapper manages the lifetime of the closure bridge

This eliminates the need for the "heavy-handed" approach described in the Runtime Differences section above. The `@convention(c)` function pointers are directly compatible with .NET's `UnmanagedCallersOnly` delegates.

### Closure Cdecl compatibility

Not all closure signatures can be represented as `@convention(c)`. The generator checks each closure parameter for Cdecl compatibility (`IsCdeclCompatibleType` in `ClosureEmitter.SwiftWrapper.cs`). That gate is the full set; the main admitted categories are:

- Primitive types, `Swift.Bool`, pointers, and `OpaquePointer`
- Classes and ObjC-bridged classes (pointer ABI)
- Simple enums (underlying integer ABI)
- Frozen structs (including `Swift.String`), non-frozen structs, and complex enums (heap-pointer ABI where needed)
- Protocol existentials (bridged as heap pointers), except well-known-protocol wrapping cases that stay on the non-Cdecl path
- Selected `Optional<…>` shapes: reference types (nil-pointer ABI); numeric primitives / `Bool` / simple enums / frozen structs (heap or nil-for-none ABI)

A method gets a **standalone closure wrapper** only when *all* of its thunk closures are Cdecl-compatible (`NeedsClosureCdeclWrapper`). Methods with any closure that can't be Cdecl-adapted fall back to **direct `CallConvSwift` P/Invoke** with the original Swift thick closure ABI.

On that direct lane Swift calls an `[UnmanagedCallersOnly]` callback whose parameters and result are the Swift values themselves, so each one needs a C# type an unmanaged signature can carry. A tuple has none. It would be declared as `System.ValueTuple<…>`, which is an auto-layout struct, and the runtimes reject it in any unmanaged signature. CoreCLR fails the callback when Swift first calls it. Mono fails the static initializer of the type that declares the callback, which takes every other member of that type down with it. The `@_cdecl` adapter cannot take the shape either, because a C function has no tuple result. A closure handed to Swift with a tuple argument or a tuple result is therefore refused under `UnsupportedClosure`, together with the member that would carry it; for a settable property that is the whole property, getter included. A Swift closure returned to C# is unaffected, since its tuple result comes back through out-pointers (below).

### Delegate projection

Closure types are projected to C# as BCL `Action<>` / `Func<>` delegates:

- Plain closures → `Action<…>` or `Func<…, T>`
- Throwing (non-async) → `Func<…, SwiftResult<T, SwiftError>>` (or `SwiftResult<SwiftVoid, SwiftError>` for void success)
- Async → `Func<…, Task>` / `Func<…, Task<T>>` (async+throwing surfaces errors via the continuation, not a nested `SwiftResult`)

**One computation, two consumers.** The delegate type appears twice in the emitted code — in the public method signature (which is the type the consumer's lambda is stored under) and in the trampoline's `SwiftClosureMarshaller.GetDelegateFrom(Boxed)Context<T>` recovery. That recovery is an unchecked cast of the `GCHandle` target, so a disagreement between the two compiles on both sides and only appears as an `InvalidCastException` on the first callback, which `FailFastUnhandledClosureException` turns into a process abort. `ClosureHandler.GetCSharpDelegateType` is therefore the single computation: every trampoline emitter reads it, and `TypeProjectionFactory.ProjectClosure` resolves it once and hands the string to `ClosureProjection`, whose `PublicType` returns it verbatim. `ClosureProjection.BuildDelegateType` — a parallel composition over the sub-projections' `PublicType`s — remains only as the fallback for a projection hand-built without a `ClosureTypeSpec` (unit tests); it must not become a second production translator, and a per-case rewrite that re-syncs the two strings for one shape is the same defect in a smaller form. `nuke binding-tests --compile-only` enforces the property directly: the closure delegate-type parity gate scans the generated bindings and fails on any cast whose type the file's public surface does not declare.

The generated marshalling code handles the conversion between those .NET delegates and Swift closure representations at the P/Invoke boundary. Custom per-signature `delegate` type declarations are a non-goal (historical alternative only) — which is also why an `inout` parameter inside a closure signature is refused rather than emitted: `Action<>`/`Func<>` cannot express `ref`, both closure ABIs marshal by value, and an emitted by-value approximation would compile while silently discarding every mutation the consumer makes.

## Swift closures C# calls: the invocation contract

A Swift closure reaches C# as a value it can call whenever a member hands one back: a method or free function returning a closure, a property getter (plain or `Optional`), or a closure argument Swift passes to a C# implementation of a protocol requirement. In every case C# receives a `Func<>`/`Action<>` and the contract below decides how that delegate calls into Swift. The conformance lane builds the same thunk per closure parameter but does not yet take the by-address shapes (an existential argument, a result through out-pointers). A requirement with one is not dispatched to C#: its witness in the generated conformance is the `fatalError` stub every undispatchable requirement gets. Stubs spell `@escaping` only on parameters, the one position Swift accepts it, so a requirement that returns a closure still yields a stub that compiles.

**The one call path.** The member's `@_cdecl` wrapper writes the closure into a typed buffer with `initializeMemory(as: F.self)`, which stores it at Swift's most general abstraction. C# keeps the two words in a `SwiftEscapingClosure` (which owns the context reference) and calls a per-member **invoke thunk**, a second `@_cdecl` function named `<wrapper symbol>_InvCR`. The thunk rebuilds the closure in a typed buffer, reloads it with `assumingMemoryBound(to: F.self).pointee`, and calls it from Swift. The store and the reload name the same type, so the compiler reabstracts on both sides and the closure is always entered at the abstraction it was stored at.

C# never calls the closure's function pointer itself. A `delegate* unmanaged[Swift]` call through it is wrong two ways at once: the stored closure expects its general form (every argument and result by address), which no natural-abstraction signature matches once any argument or result is passed indirectly, and Mono faults on the call for every shape, scalar closures included. A returned closure therefore has no "simple enough" direct path.

**What crosses the thunk.**

| Position | Shape | Carrier |
|---|---|---|
| Argument | Integer and floating primitives, `Bool`, simple enums | By value (`Bool` as a byte, a simple enum as its raw integer) |
| Argument | Class references, ObjC-bridged classes | Object pointer, borrowed for the call |
| Argument | Structs and payload enums, frozen or not | By address: C# marshals into a buffer, Swift loads `T` |
| Argument | Existentials (opaque and class-bound), `any Error` | By address of an `ExistentialContainer1`: a Swift-vended value is copied in; a C#-authored conformer is boxed through the protocol's proxy. C# destroys and frees the container after the call |
| Result | Void, primitives, `Bool`, simple enums, class references | Returned directly |
| Result | Existentials (opaque and class-bound) | Out-pointer the thunk initializes; C# adopts the +1 value as the protocol proxy, which releases it on dispose or finalization |
| Result | `any Error` | Out-pointer, adopted as `AnyError` |
| Result | Tuples of scalars | One out-pointer per element |
| Throwing | Any of the above | An error out-pointer; the delegate returns `SwiftResult<T, SwiftError>` |

**What is refused.** A shape the thunk cannot carry has no correct call, so the member is left unbound and reported under `UnsupportedClosure` with the detail `Return type is a closure C# cannot invoke: <reason>`. The refusal is a prediction gate, justified under the freeze policy because every wrong call it prevents compiles:

- **Async closures.** An async closure can only be awaited from a Swift async context. The C# side of a call is a `Task`; the Swift side is a thunk that starts a Swift task, awaits the closure, and completes the `Task` through a C callback. The generator has that bridge for async methods, and the protocol-conformance lane has it for exactly one closure shape (`() async -> Int32` passed to a C# implementation). A returned async closure would need it for every carrier above, plus error delivery and cancellation, so it is refused rather than called synchronously.
- `inout` arguments (a `Func<>` cannot express `ref`).
- Closure arguments, including `@convention(c)` function pointers: the thunk has no callback marshalling for an argument of the returned closure.
- Results the thunk has no out-pointer for: `String`, structs, payload enums, `Optional`, closures, collections.
- `Optional` arguments, and arguments that mention a generic parameter.
- **Subscript getters.** A subscript accessor has no thunk lane; its getter returns the closure at its natural abstraction through the Swift calling convention.
- **Property getters that cannot take the `@_cdecl` wrapper.** Only that wrapper carries the invoke thunk, so the getter is dropped. The setter is a callback path and is unaffected: a settable property is emitted set-only, a get-only one is skipped. A setter that cannot use the wrapper does not take the getter with it.
- **Requirements of a Swift-vended protocol value.** Witness dispatch through a protocol proxy has no closure-result lane, so a requirement that returns a closure is declared on the interface but throws `NotSupportedException` through the proxy, and is reported as not dispatchable.
- Closures in enum payloads, tuples and collections. The enum case keeps its tag but gets no factory or payload accessor; the member that would carry the tuple or collection is skipped.
- A member whose result protocol's proxy was suppressed. The C# member becomes a throw stub, and the wrapper leaves its invoke thunk out with it.

The emitter enforces the contract on its own as well: if a member that returns a closure reaches C# emission without an invoke thunk, the generator fails loudly for that member instead of falling back to a direct call.

**C function pointers.** A returned `@convention(c)` function is stored by the wrapper as an ordinary Swift closure, which Swift converts implicitly, and is then called through the thunk like any other. A `@convention(c)` parameter of a bound member is passed as a pointer to an `[UnmanagedCallersOnly(CallConvCdecl)]` method, and Swift calls it through the C convention. A C function pointer nested inside a closure's parameters or result is refused, never passed through a guessed convention.

## Callback lifetime

Two directions, two owners.

**A C# delegate handed to Swift** (a closure parameter or a closure property setter) is pinned with a `GCHandle` for as long as Swift can call it.

- *Non-escaping* parameters are released when the call returns. Swift cannot keep a non-escaping closure, so no callback can arrive afterwards.
- *Escaping* parameters and stored properties transfer the handle to Swift inside a boxed context. Swift owns that box. When the last Swift reference to the closure goes away, its deinit frees the `GCHandle`, and only then can the delegate and everything it captures be collected. If the call that would have transferred the handle fails before Swift takes it, the binding frees the handle itself.
- A `@convention(c)` parameter is different: C function pointers carry no context, so the delegate is parked in a per-thread slot for the duration of the call and the pointer names a static `[UnmanagedCallersOnly]` method that reads the slot. The pointer is valid only until the call returns. A Swift API that keeps it and calls it later is not supported, since by then the slot no longer holds this delegate.
- A callback that throws a managed exception cannot unwind through Swift frames. The trampoline catches the exception and ends the process with `FailFast`. Callbacks must handle their own exceptions.
- Callbacks run on whatever thread Swift calls them from. A callback that touches UI must marshal back to the main thread itself.

**A Swift closure handed to C#** (the returned closures above) is retained by its `SwiftEscapingClosure`. The delegate captures that wrapper, so the Swift context stays alive for as long as the delegate is reachable, and is released when the wrapper is finalized. Storing the delegate keeps the Swift closure, and everything the closure captured, alive. Values passed to the closure are borrowed for the duration of the call. An existential or `Error` result is owned by the C# object that wraps it.

## Accessibility

As built, Swift closure types are presented to C# as BCL `Action<>` / `Func<>` (with the `SwiftResult` / `Task` wrappers above). Custom `delegate` type declarations matching each Swift signature were considered early on but were not adopted — they would avoid the Action/Func return-vs-void split, at the cost of a larger generated surface, and the generator settled on the BCL forms.
