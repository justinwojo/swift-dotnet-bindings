# Known Issues and Workarounds

This document tracks major runtime issues, Mono/Swift interop bugs, and their workarounds. These are issues that cannot be fixed in the binding generator itself and require special handling.

---

## Table of Contents

1. [Mono JIT Bug: swift_getExistentialTypeMetadata Crash](#mono-jit-bug-swift_getexistentialtypemetadata-crash)
2. [Non-Blittable Types with Swift Calling Convention](#non-blittable-types-with-swift-calling-convention)
3. [SafeHandle in Async P/Invoke](#safehandle-in-async-pinvoke)

---

## Mono JIT Bug: swift_getExistentialTypeMetadata Crash

**Severity**: High - Blocks certain API patterns
**Status**: Workaround implemented (Swift wrapper functions)
**Affects**: Any code creating `SwiftArray<ExistentialContainer{N}>` at runtime

### Symptoms

When creating a `SwiftArray<ExistentialContainer1>` (or any existential container array), the application crashes with:

```
* Assertion at mono/metadata/jit-info.c:918, condition `!ji->async' not met

Managed Stacktrace:
  at Swift.Runtime.TypeMetadata:swift_getExistentialTypeMetadata
  at Swift.Runtime.TypeMetadata:GetExistentialTypeMetadata
  at Swift.Runtime.SwiftObjectHelper`1:GetTypeMetadata
  at Swift.SwiftArray`1:.cctor
```

### Root Cause

The Swift runtime function `swift_getExistentialTypeMetadata` has a calling pattern that confuses Mono's JIT compiler. Specifically:

1. The function uses Swift's calling convention (`CallConvSwift`)
2. Mono's JIT incorrectly marks the call frame as "async"
3. When unwinding the stack, Mono hits an assertion that the frame should NOT be marked async
4. The assertion `!ji->async` fails, crashing the process

This is a Mono runtime bug, not a binding generator issue or Swift interop design flaw.

### Technical Details

**Swift function signature** (from Swift runtime):
```c
SWIFT_RUNTIME_EXPORT
const ExistentialTypeMetadata *
swift_getExistentialTypeMetadata(
    ExistentialTypeRepresentation classConstraint,
    const Metadata *superclassConstraint,
    size_t numProtocols,
    const ProtocolDescriptorRef *protocols);
```

**C# P/Invoke declaration**:
```csharp
[UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
[DllImport(KnownLibraries.SwiftCore, EntryPoint = "swift_getExistentialTypeMetadata")]
private static extern TypeMetadata swift_getExistentialTypeMetadata(
    byte classConstraint,
    TypeMetadata superclassConstraint,
    nuint numProtocols,
    IntPtr protocols);
```

### Research Findings

Consulted external AI models (Grok, Gemini) on this issue:

**Q: Is this a known Mono bug?**
A: This is a known category of bug in Mono's Swift Calling Convention implementation. The assertion triggers when Mono's JIT tries to unwind the stack and encounters a `CallConvSwift` frame. Not publicly documented but similar issues exist in the dotnet/runtime issue tracker.

**Q: Can it be fixed at the P/Invoke level?**
A: Several approaches were tried, none successful:

| Approach | Result |
|----------|--------|
| `[SuppressGCTransition]` | Did not resolve |
| `CallingConvention.Cdecl` | Did not resolve - function likely needs Swift CC |
| `nint` return type | Did not resolve |
| `[LibraryImport]` | Not tried - would require .NET 7+ source generation |

**Q: Is the Swift wrapper approach the best solution?**
A: Yes, both AIs agreed. Swift wrappers are the most robust solution because:
- Avoids Mono JIT entirely for metadata fetch
- Works on iOS FullAOT (no JIT at all)
- Future-proof (NativeAOT in .NET 10+ ignores JIT bugs)
- Swift compiler understands existential container layout perfectly
- If Apple changes internal layout, wrapper recompiles correctly

### Mitigations Applied

**Phase 28.1 - Defensive measures**:
1. **Lazy initialization** in `SwiftArray<T>` - Element metadata lookup deferred from static constructor to first use, preventing crashes during type loading
2. **Graceful error** - `TypeMetadata.TryGetTypeMetadataUncached` throws a descriptive `SwiftRuntimeException` instead of crashing silently
3. **P/Invoke fix** - Updated `swift_getExistentialTypeMetadata` to use Swift calling convention

**Phase 29 - Working workaround**:
Swift wrapper functions that handle existential array creation on the Swift side:

```swift
// SwiftBindings.swift
@_silgen_name("ImageRequest_initWithURLString_simple")
public func imageRequest_initWithURLString_simple(_ urlString: UnsafePointer<CChar>) -> UnsafeMutableRawPointer {
    let urlStr = String(cString: urlString)
    let request = ImageRequest(url: URL(string: urlStr))  // Empty processors array handled by Swift

    let ptr = UnsafeMutablePointer<ImageRequest>.allocate(capacity: 1)
    ptr.initialize(to: request)
    return UnsafeMutableRawPointer(ptr)
}
```

C# factory class:
```csharp
public static class ImageRequestFactory
{
    public static ImageRequest FromUrlString(string urlString)
    {
        // Calls Swift wrapper, copies result to C# buffer
        // See Swift.Nuke.Wrappers.cs for full implementation
    }
}
```

### Impact on APIs

**Blocked patterns**:
- Any constructor/method that takes `IEnumerable<ExistentialContainer{N}>` as a parameter
- Creating arrays of protocol-typed objects from C#

**Workaround patterns**:
- Swift wrapper functions that create the arrays on the Swift side
- Pass scalar values or pre-built Swift objects instead of arrays

**Affected Nuke APIs**:
- `ImageRequest` constructors with `processors` parameter - Use `ImageRequestFactory` instead

### Long-Term Fix

File a bug report to [dotnet/runtime](https://github.com/dotnet/runtime) with:
1. Minimal reproduction case
2. Stack trace showing the JIT assertion
3. Details about Swift calling convention and existential metadata

The Mono JIT should not mark `swift_getExistentialTypeMetadata` calls as async.

### Related Files

| File | Purpose |
|------|---------|
| `src/Swift.Runtime/src/Swift/Runtime/TypeMetadata.cs` | P/Invoke declarations, workaround attempts |
| `BindingTesting/Nuke/output-ios/SwiftBindings.swift` | Swift wrapper functions |
| `BindingTesting/Nuke/output-ios/Swift.Nuke.Wrappers.cs` | C# factory class |

---

## Non-Blittable Types with Swift Calling Convention

**Severity**: Medium - Affects specific API patterns
**Status**: Documented limitation
**Affects**: P/Invoke calls with complex types and `CallConvSwift`

### Symptoms

```
System.InvalidProgramException: Cannot use non-blittable types with Swift calling convention
```

### Root Cause

.NET's implementation of the Swift calling convention (`CallConvSwift`) requires all parameters and return types to be blittable (directly mappable to native memory without marshalling). Types like `SwiftOptional<T>` or `SafeHandle` derivatives are not blittable.

### Affected Scenarios

1. **URL.AbsoluteString property** - Returns `SwiftOptional<SwiftString>` which requires marshalling
2. **Methods returning Optional types** - Need special handling
3. **SafeHandle parameters in async contexts** - See separate section

### Workarounds

**For Optional returns**: Use wrapper methods that handle the marshalling:
```csharp
// Instead of calling the property directly
var result = url.AbsoluteString;  // May fail

// Use a string-based approach
var urlString = ImageRequestFactory.FromUrlString("...");  // Works
```

**For general non-blittable types**:
- Use `IntPtr` in P/Invoke signatures
- Marshal manually in wrapper code
- Use Swift wrappers when marshalling is too complex

### Related Files

| File | Purpose |
|------|---------|
| `src/Swift.Runtime/src/Swift/URL.cs` | URL type with workarounds |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/MethodHandler.cs` | Non-blittable handling |

---

## SafeHandle in Async P/Invoke

**Severity**: Medium - Requires workaround for async instance methods
**Status**: Workaround implemented (singleton pattern, IntPtr conversion)
**Affects**: Async instance methods on Swift classes

### Symptoms

Async instance methods crash or behave incorrectly when the `self` parameter is passed as a `SafeHandle`.

### Root Cause

The .NET runtime does not support passing `SafeHandle` (or derivatives like `SwiftSafeHandle<T>`) through P/Invoke with Swift calling convention in async contexts. The Task continuation mechanism doesn't properly preserve the handle reference.

### Workarounds Applied

**Singleton Pattern Detection** (Phase 8):
For classes with a `shared` static property (singleton pattern), the generator automatically:
1. Detects the singleton pattern via `TypeDecl.HasSingletonPattern`
2. Uses `ClassName.shared.method()` in Swift wrappers instead of passing `self`

**IntPtr Conversion** (for non-singletons):
```swift
// Swift wrapper uses unsafeBitCast to convert IntPtr back to class instance
let instance = unsafeBitCast(_self, to: ClassName.self)
await instance.someAsyncMethod()
```

### Impact

- Singleton classes (like `ImagePipeline`) work correctly with async methods
- Non-singleton classes may have edge cases with certain class hierarchies
- Proper fix would require .NET runtime changes to support `SwiftSelf` register with async Task closure capture

### Related Files

| File | Purpose |
|------|---------|
| `src/Swift.Bindings/src/Model/TypeDecl/TypeDecl.cs` | `HasSingletonPattern` detection |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/MethodHandler.cs` | Async wrapper generation |

---

## Adding New Issues

When documenting a new major issue, include:

1. **Severity** - High/Medium/Low
2. **Status** - Investigating/Documented/Workaround implemented/Fixed
3. **Affects** - What patterns/APIs are impacted
4. **Symptoms** - Error messages, stack traces
5. **Root Cause** - Technical explanation
6. **Research Findings** - What was tried, why it didn't work
7. **Workarounds** - How to avoid the issue
8. **Long-Term Fix** - What would properly resolve it
9. **Related Files** - Where the workarounds live
