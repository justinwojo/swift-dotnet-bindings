# NativeAOT Skipped Tests — Status & Diagnosis

> **Last updated:** 2026-03-31
> **Device counts:** 1235 pass, 0 fail, 39 skip, 0 crash
> **Simulator counts:** 1265 pass, 0 fail, 9 skip, 0 crash

## Background

24 tests are skipped on NativeAOT device via `[SkipOnDevice]`. All 24 pass on the Mono simulator. The remaining ~15 device skips come from `[Skip]` attributes (known limitations unrelated to NativeAOT).

An audit in March 2026 attempted to fix these by treating them as generator emission bugs (missing `@_cdecl` wrappers, incorrect calling conventions). The generator changes are correct — the emitted wrappers compile and work on the simulator — but the tests still crash on NativeAOT device. The root causes are **NativeAOT runtime marshalling issues**, not generator bugs.

### What the audit DID fix (shipped)

- **Device build was totally broken** — 3 Swift wrapper compilation errors prevented ANY device tests from running. Fixed: `@available` propagation, noncopyable metadata wrapper skipping, async throwing property `Throws` flag parsing.
- **Validation regression** — Fix A/F changes broke Kingfisher + Nuke x3 (55→51 swift wrapper) due to parser bug (`Throws=false` hardcoded on property accessors). Fixed back to 55/56.
- **Optional existential setter @_cdecl emission** — Generator now emits `@_cdecl` wrappers for optional existential property setters (previously suppressed). Wrappers compile correctly. Crash is at runtime on device.
- **Bound generic module initializer registration** — Generator now records closed generic types for NativeAOT module initializer. Registration runs but metadata resolution still fails.

---

## Category A: Optional Existential Setter (5 tests) — Runtime crash

**File:** `BindingTests/RuntimeTestsApp/Protocols/OptionalExistentialPropertyTests.cs`

| Test | Line |
|------|------|
| `TestPrimarySetterAssignRenderable` | 136 |
| `TestPrimarySetterThenGetterRoundTrip` | 154 |
| `TestPrimarySetterClearToNull` | 168 |
| `TestItemSetterFromSwiftExistential` | 290 |
| `TestItemSetterClearToNull` | 307 |

**What happens:** 11 getter tests in the same class pass on device. The first setter test crashes the app (SIGKILL, signal 9). All subsequent tests in the class are lost.

**Generator output (correct):**

C# setter (`SwiftBindingsTestLib.cs:~102775`):
```csharp
set {
    unsafe {
        void* __heap = null;
        try {
            IntPtr __ptr = IntPtr.Zero;
            bool __hasVal = value != null;
            if (value is { } __v) {
                var __container = ExistentialContainerFactory.GetOrCreate<IRenderable>(__v);
                __heap = NativeMemory.Alloc((nuint)Unsafe.SizeOf<ExistentialContainer1>());
                Unsafe.Copy(__heap, ref __container);
                __ptr = (IntPtr)__heap;
            }
            Primary_Set(__ptr, __hasVal);
        } finally {
            if (__heap != null) NativeMemory.Free(__heap);
        }
    }
}
```

P/Invoke (`SwiftBindingsTestLib.cs:~102770`):
```csharp
[DllImport("SwiftBindingsTestLibSwiftBindings", EntryPoint = "SBW_Set_...")]
[UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
private static extern void Primary_Set(IntPtr newValue, [MarshalAs(UnmanagedType.U1)] bool hasValue, IntPtr self_);
```

Swift @_cdecl wrapper (`SwiftBindingsTestLib.swift:~21720`):
```swift
@_cdecl("SBW_Set_SwiftBindingsTestLib_RenderableHolder_primary")
public func _sbw_set_primary_116B309F(_ newValue: UnsafeRawPointer, _ hasValue: Bool, _ self_: UnsafeMutableRawPointer) {
    let obj = Unmanaged<SwiftBindingsTestLib.RenderableHolder>.fromOpaque(self_).takeUnretainedValue()
    let newValueVal: (any SwiftBindingsTestLib.Renderable)? = hasValue ? newValue.assumingMemoryBound(to: (any SwiftBindingsTestLib.Renderable).self).pointee : nil
    obj.primary = newValueVal
}
```

**Diagnosis needed:**
- The P/Invoke signature matches the Swift wrapper (3 params: IntPtr, bool, IntPtr).
- The EC1 container is heap-allocated and passed as IntPtr.
- The crash is SIGKILL (not SIGSEGV), suggesting a memory corruption that triggers the iOS watchdog or a guard page violation.
- Possible causes: (1) EC1 layout mismatch between C# `ExistentialContainer1` and Swift's actual existential container layout on arm64, (2) `assumingMemoryBound(to: (any Renderable).self)` interpreting the bytes differently than what C# wrote, (3) NativeAOT's `NativeMemory.Alloc` returning memory with different alignment than Swift expects.

**Next steps:** Add diagnostic logging to the Swift wrapper — print the raw bytes at `newValue` pointer before the `assumingMemoryBound` call. Compare with what C# wrote. Run with `--class-filter OptionalExistentialPropertyTests` on device.

---

## Category B: State Corruption — Existential Boxing (9 tests)

**Files:**
- `BindingTests/RuntimeTestsApp/Protocols/ExistentialBoxingTests.cs` (2 tests)
- `BindingTests/RuntimeTestsApp/Protocols/ValueProviderPatternTests.cs` (7 tests)

| Test | File | Line |
|------|------|------|
| `TestRunModeConsumerWithSimpleMode` | ExistentialBoxingTests | 249 |
| `TestRunModeConsumerWithStrictMode` | ExistentialBoxingTests | 259 |
| `TestSetProviderWithFloatProvider` | ValueProviderPatternTests | 113 |
| `TestSetProviderWithColorProvider` | ValueProviderPatternTests | 125 |
| `TestSetProviderWithGradientProvider` | ValueProviderPatternTests | 136 |
| `TestMultipleProviders` | ValueProviderPatternTests | 149 |
| `TestHasUpdateForKeypath` | ValueProviderPatternTests | 171 |
| `TestGetProviderKindFreeFunction` | ValueProviderPatternTests | 191 |
| `TestCheckProviderUpdateFreeFunction` | ValueProviderPatternTests | 202 |

**What happens:** All 9 tests pass when run in isolation (`--class-filter`). In the full test suite, they SIGKILL. The crash is in `BoxAsExistential1`, which is called by many tests earlier in the suite.

**Diagnosis needed:** This is a state corruption bug — some earlier test class leaves corrupted runtime state (metadata cache, existential container pool, or GCHandle table) that causes `BoxAsExistential1` to crash in subsequent test classes. Binary search: run pairs of classes together to find which earlier class poisons the state.

**Next steps:**
1. Run `--class-filter ExistentialBoxingTests` on device — confirm passes in isolation.
2. Run `--class-filter ValueProviderPatternTests` on device — confirm passes in isolation.
3. If both pass, binary search: add one class at a time to find the poisoning class.

---

## Category C: Existential Array Constructor (2 tests)

**File:** `BindingTests/RuntimeTestsApp/Collections/ConstructorCollectionTests.cs`

| Test | Line |
|------|------|
| `TestProcessingPipelineWithExistentialArray` | 141 |
| `TestProcessingPipelineEmptyExistentialArray` | 150 |

**What happens:** `ProcessingPipeline(modes: IProcessingMode[])` constructor takes a `SwiftArray<ExistentialContainer1>` parameter. The type cast from `IProcessingMode[]` to `SwiftArray<EC1>` crashes.

**Hypothesis:** `SwiftArray<ExistentialContainer1>` metadata resolution fails on NativeAOT. The `TypeMetadata.cs:404` path has explicit `IExistentialContainer` handling that routes through `swift_getExistentialTypeMetadata` wrappers — check whether this path works on device.

**Next steps:** Run `--class-filter ConstructorCollectionTests` on device. If crashes, add logging to `TypeMetadata.GetTypeMetadataOrThrow` for the `SwiftArray<EC1>` path.

---

## Category D: Non-Trivial Struct Existential Boxing (2 tests)

**File:** `BindingTests/RuntimeTestsApp/Marshalling/ConstructorParamTests.cs`

| Test | Line |
|------|------|
| `TestProtocolExistentialParamConstruction` | 29 |
| `TestProtocolExistentialParamGetText` | 40 |

**What happens:** `DescriptionPrinter(source: any Describable)` constructor takes a non-frozen struct (`SimpleItem`) boxed as an existential. The `swift_allocBox` call or `MarshalPayload` operation crashes.

**Hypothesis:** Non-frozen struct existential boxing requires VWT (value witness table) operations that work differently under NativeAOT. The `swift_allocBox` P/Invoke uses default Cdecl convention which is correct, but the metadata pointer passed to it may be wrong.

**Next steps:** Run `--class-filter ConstructorParamTests` on device. Check if the non-existential constructor param tests in the same class pass (they should — those are simpler types).

---

## Category E: MCB Callback Bridge (3 tests)

**File:** `BindingTests/RuntimeTestsApp/Closures/ClosureEdgeCaseTests.cs`

| Test | Line |
|------|------|
| `TestMCBOverload_DataProcessorProcess` | 249 |
| `TestMCBOverload_ImageProcessorProcess` | 267 |
| `TestMCBOverload_DataProcessorProcessWithError` | 281 |

**What happens:** Methods with closure parameters that use `SwiftResult<FetchResult, FetchError>` crash. The MCB (MethodClosureBridge) emits a `@_cdecl` wrapper that receives callback function pointers and invokes them with the result.

**Hypothesis:** `SwiftResult<T, E>` has a `Lazy<nuint>` static field for type metadata. On NativeAOT, if the generic instantiation isn't statically referenced, metadata resolution fails. Also possible: `GCHandle` lifecycle issue in the callback — the handle may be freed before the callback fires.

**Next steps:** Run `--class-filter ClosureEdgeCaseTests` on device. The non-MCB closure tests in the same class should pass — isolate whether it's the SwiftResult metadata or the callback mechanism.

---

## Category F: Bound Generic Metadata (1 test)

**File:** `BindingTests/RuntimeTestsApp/Generics/BoundGenericEdgeCaseTests.cs`

| Test | Line |
|------|------|
| `TestMakeRefPair` | 41 |

**What happens:** `makeRefPair()` returns `Pair<CoordinateRef, LabelRef>`. The C# side calls `TypeMetadata.GetTypeMetadataOrThrow<Pair<CoordinateRef, LabelRef>>()` which fails with `SwiftRuntimeException: Unable to get type metadata for type Pair`2`.

**Generator output:** The module initializer registers `CoordinateRef` and `LabelRef` separately, but `Pair<CoordinateRef, LabelRef>` as a closed generic is NOT registered (the `SwiftOptional<T>` exclusion in `RecordBoundGenericSwiftObjectType` may have been too broad, or the `Pair` type's metadata wrapper needs a different approach for generic instantiation).

**Root cause:** NativeAOT trims the explicit interface implementation `ISwiftObject.GetTypeMetadata()` on closed generic types. The `RunClassConstructor` fallback also fails because NativeAOT doesn't support runtime class constructor invocation for trimmed types.

**Diagnosis needed:** Check whether `Pair<CoordinateRef, LabelRef>` appears in the module initializer output. If not, the `RecordBoundGenericSwiftObjectType` exclusion is too aggressive. If it does appear but still fails, the metadata wrapper for `Pair` itself may not handle generic type arguments correctly.

---

## Closure + Existential Array (2 tests)

**File:** `BindingTests/RuntimeTestsApp/Closures/ClosureTests.cs`

| Test | Line |
|------|------|
| `TestClosureWithExistentialArrayInit` | 381 |
| `TestClosureWithExistentialArrayEmptyModes` | 391 |

**What happens:** Constructor takes both a closure parameter AND an existential array (`IProcessingMode[]`). Combines Category C (existential array) with closure parameter marshalling.

**Next steps:** If Category C is fixed, these may also resolve. Test after C is addressed.

---

## Diagnosis Playbook

For any category, the debugging approach is:

1. **Run in isolation first:** `nuke runtime-tests-device --skip-regen --class-filter ClassName`
2. **Add Swift wrapper logging:** Print raw pointer bytes before `assumingMemoryBound` or `load(as:)` calls. Rebuild with `nuke runtime-tests-device` (no `--skip-regen`).
3. **Compare with simulator:** Add the same logging on simulator. Compare pointer values, sizes, and byte patterns.
4. **Check P/Invoke match:** Verify C# `DllImport` parameter count/types match Swift `@_cdecl` wrapper exactly. Use `grep` on both generated files.
5. **Check metadata:** For metadata failures, add `Console.WriteLine` in `TypeMetadata.GetTypeMetadataOrThrow<T>()` to see which path (wrapper Cdecl vs fallback CallConvSwift) is attempted and what fails.

### Key files

- Generated C#: `BindingTests/output/SwiftBindingsTestLib.cs`
- Generated Swift wrappers: `BindingTests/output/SwiftBindingsTestLib.swift`
- Module initializer: search `SwiftBindingsTestLib.cs` for `__SwiftFrameworkResolver`
- Runtime metadata: `src/Swift.Runtime/src/Swift/Runtime/TypeMetadata.cs`
- Existential container: `src/Swift.Runtime/src/Swift/Runtime/ExistentialContainer.cs`
- SwiftArray: `src/Swift.Runtime/src/Swift/Runtime/SwiftArray.cs`

### Common NativeAOT pitfalls

- **Trimming:** NativeAOT aggressively trims unreferenced code. Explicit interface implementations on generic types get trimmed unless statically called.
- **Metadata resolution:** `Type.GetType()` and reflection-based metadata lookup may not work. Module initializer pre-registration is the workaround.
- **Memory layout:** NativeAOT may use different struct packing than Mono for interop scenarios. `Unsafe.SizeOf<T>()` should match but verify.
- **GCHandle lifetime:** Callbacks that fire asynchronously may outlive the GCHandle that pins the managed delegate. NativeAOT is stricter about this than Mono.
