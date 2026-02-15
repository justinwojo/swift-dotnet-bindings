// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// macOS NativeAOT console app for Blocker 1/2/3 validation.
// Targets osx-arm64 — same ARM64 ABI and libswiftCore.dylib as iOS.
// No simulator, no code signing, no UIKit dependencies.

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.InteropServices.Swift;
using System.Text;
using Swift;
using Swift.Runtime;
using Swift.Runtime.InteropServices;

// --- Test infrastructure ---

int passCount = 0;
int failCount = 0;
int crashCount = 0;

string? testFilter = null;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--test-id" && i + 1 < args.Length)
    {
        testFilter = args[i + 1];
        i++;
    }
}

void RunTest(string testId, Action test)
{
    if (testFilter != null && testFilter != testId)
        return;

    try
    {
        test();
        passCount++;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"FAIL: {testId}: {ex.GetType().Name}: {ex.Message}");
        failCount++;
    }
}

Console.WriteLine("=========================================");
Console.WriteLine(" NativeAOT macOS Test Runner");
Console.WriteLine("=========================================");
Console.WriteLine();

// -----------------------------------------------------------------------
// Blocker 1: Mono JIT assertion crash — CallConvSwift P/Invoke
// Under NativeAOT there is no JIT, so these should all pass.
// -----------------------------------------------------------------------

RunTest("b1-string-create", () =>
{
    // Raw CallConvSwift P/Invoke to libswiftCore string constructor.
    // On Mono this triggers jit-info.c:918 assertion.
    unsafe
    {
        byte[] utf8 = Encoding.UTF8.GetBytes("NativeAOT");
        fixed (byte* ptr = utf8)
        {
            var buffer = SwiftString.PInvoke_Create(ptr, utf8.Length, 1);
            Console.WriteLine("PASS: b1-string-create");
        }
    }
});

RunTest("b1-string-length", () =>
{
    // Raw CallConvSwift P/Invoke to libswiftCore count getter.
    unsafe
    {
        byte[] utf8 = Encoding.UTF8.GetBytes("Hello");
        fixed (byte* ptr = utf8)
        {
            var buffer = SwiftString.PInvoke_Create(ptr, utf8.Length, 1);
            long length = SwiftString.PInvoke_GetLength(buffer);
            if (length == 5)
                Console.WriteLine("PASS: b1-string-length");
            else
                throw new Exception($"Expected length 5, got {length}");
        }
    }
});

RunTest("b1-string-wrapper", () =>
{
    // SwiftString via Cdecl wrapper path (baseline — works on Mono too).
    // On macOS without the wrapper dylib, this falls through to direct CallConvSwift.
    using var str = new SwiftString("Hello NativeAOT");
    var result = str.ToString();
    if (result == "Hello NativeAOT")
        Console.WriteLine("PASS: b1-string-wrapper");
    else
        throw new Exception($"Expected 'Hello NativeAOT', got '{result}'");
});

RunTest("b1-string-metadata", () =>
{
    // SwiftString.PInvoke_getMetadata() — CallConvSwift type metadata accessor.
    var metadata = SwiftString.PInvoke_getMetadata();
    if (metadata.IsValid)
        Console.WriteLine($"PASS: b1-string-metadata (size={metadata.Size})");
    else
        throw new Exception("Metadata is not valid");
});

RunTest("b1-string-roundtrip", () =>
{
    // Full SwiftString lifecycle: create, get length, get UTF-8, compare.
    // Tests multiple CallConvSwift P/Invokes in sequence.
    var testStr = "Hello, NativeAOT! \U0001F389";
    using var swiftStr = new SwiftString(testStr);
    var roundtripped = swiftStr.ToString();
    if (roundtripped == testStr)
        Console.WriteLine("PASS: b1-string-roundtrip");
    else
        throw new Exception($"Expected '{testStr}', got '{roundtripped}'");
});

// -----------------------------------------------------------------------
// Blocker 1: VWT indirect function pointers
// -----------------------------------------------------------------------

RunTest("b1-vwt-destroy", () =>
{
    // VWT Destroy on SwiftString — uses indirect CallConvSwift function pointer.
    // On Mono this crashes with jit-info.c:918 via VWT dispatch.
    using var str = new SwiftString("destroy-test");
    // Explicit Dispose triggers VWT Destroy through SwiftSafeHandle.ReleaseHandle
    str.Dispose();
    Console.WriteLine("PASS: b1-vwt-destroy");
});

RunTest("b1-vwt-initcopy", () =>
{
    // VWT InitializeWithCopy via ISwiftObject.MarshalToSwift path.
    using var str = new SwiftString("copy-test");
    var swiftObj = (ISwiftObject)str;
    var metadata = SwiftObjectHelper<SwiftString>.GetTypeMetadata();
    Span<byte> buffer = stackalloc byte[(int)metadata.Size];
    swiftObj.MarshalToSwift(ref buffer);
    Console.WriteLine("PASS: b1-vwt-initcopy");
});

// -----------------------------------------------------------------------
// Blocker 1: SwiftArray — tests generic type metadata + CallConvSwift
// -----------------------------------------------------------------------

RunTest("b1-array-create", () =>
{
    // SwiftArray<int> construction uses CallConvSwift for metadata + init.
    using var arr = new SwiftArray<int>(new[] { 1, 2, 3, 4, 5 });
    if (arr.Count == 5)
        Console.WriteLine("PASS: b1-array-create");
    else
        throw new Exception($"Expected count 5, got {arr.Count}");
});

RunTest("b1-array-element", () =>
{
    // Array element access uses CallConvSwift subscript P/Invoke.
    using var arr = new SwiftArray<int>(new[] { 10, 20, 30 });
    int val = arr[1];
    if (val == 20)
        Console.WriteLine("PASS: b1-array-element");
    else
        throw new Exception($"Expected 20, got {val}");
});

// -----------------------------------------------------------------------
// Blocker 1: SwiftOptional — type metadata
// -----------------------------------------------------------------------

RunTest("b1-optional-create", () =>
{
    // SwiftOptional<int> construction uses CallConvSwift metadata.
    using var opt = SwiftOptional<int>.NewSome(42);
    if (opt.Case == SwiftOptionalCases.Some && opt.Value == 42)
        Console.WriteLine("PASS: b1-optional-create");
    else
        throw new Exception($"Expected Some(42), got {opt.Case}({opt.Value})");
});

RunTest("b1-optional-none", () =>
{
    using var opt = SwiftOptional<int>.NewNone();
    if (opt.Case == SwiftOptionalCases.None)
        Console.WriteLine("PASS: b1-optional-none");
    else
        throw new Exception($"Expected None, got {opt.Case}");
});

// -----------------------------------------------------------------------
// Trimming tests — MakeGenericType / reflection
// -----------------------------------------------------------------------

RunTest("n3-trimming-marshal", () =>
{
    // SwiftMarshal.MarshalFromSwift<int> uses reflection (MakeGenericType).
    // Under NativeAOT with TrimMode=partial, this should survive.
    unsafe
    {
        int testValue = 42;
        var result = SwiftMarshal.MarshalFromSwift<int>(new IntPtr(&testValue));
        if (result == 42)
            Console.WriteLine("PASS: n3-trimming-marshal");
        else
            throw new Exception($"Expected 42, got {result}");
    }
});

RunTest("n3-trimming-metadata-cache", () =>
{
    // TypeMetadata.Cache uses ConcurrentDictionary — verify it works under trimming.
    var meta1 = SwiftObjectHelper<SwiftString>.GetTypeMetadata();
    var meta2 = SwiftObjectHelper<SwiftString>.GetTypeMetadata();
    if (meta1.IsValid && meta1.Equals(meta2))
        Console.WriteLine("PASS: n3-trimming-metadata-cache");
    else
        throw new Exception("Metadata cache returned invalid or inconsistent results");
});

// -----------------------------------------------------------------------
// Blocker 2: CustomMarshaller + CallConvSwift experiments
// Key question: Can [LibraryImport] + [MarshalUsing(CustomMarshaller)] produce
// a blittable intermediate that ILCompiler accepts for CallConvSwift?
// Uses libNativeAotSwiftLib.dylib (Swift functions with @_silgen_name).
// -----------------------------------------------------------------------

RunTest("b2-libimport-baseline", () =>
{
    // Baseline: [LibraryImport] + CallConvSwift with blittable types.
    // If this fails, LibraryImport + CallConvSwift doesn't work at all.
    int result = Blocker2PInvokes.AddInt32(17, 25);
    if (result == 42)
        Console.WriteLine("PASS: b2-libimport-baseline");
    else
        throw new Exception($"Expected 42, got {result}");
});

RunTest("b2-marshaller-optional-some", () =>
{
    // KEY EXPERIMENT: Pass SwiftOptional<int>(42) through CustomMarshaller + CallConvSwift.
    // The marshaller lowers SwiftOptional<int> → BlittableOptionalInt32 (blittable struct).
    // If ILCompiler accepted this at publish time and the call succeeds, Blocker 2 has a path.
    using var opt = SwiftOptional<int>.NewSome(42);
    int result = Blocker2PInvokes.AcceptOptionalInt32(opt);
    if (result == 42)
        Console.WriteLine("PASS: b2-marshaller-optional-some");
    else
        throw new Exception($"Expected 42, got {result}");
});

RunTest("b2-marshaller-optional-none", () =>
{
    // Pass None — Swift function returns -1 for nil input.
    using var opt = SwiftOptional<int>.NewNone();
    int result = Blocker2PInvokes.AcceptOptionalInt32(opt);
    if (result == -1)
        Console.WriteLine("PASS: b2-marshaller-optional-none");
    else
        throw new Exception($"Expected -1, got {result}");
});

RunTest("b2-marshaller-optional-null", () =>
{
    // Pass C# null (should marshal as None).
    int result = Blocker2PInvokes.AcceptOptionalInt32(null!);
    if (result == -1)
        Console.WriteLine("PASS: b2-marshaller-optional-null");
    else
        throw new Exception($"Expected -1, got {result}");
});

RunTest("b2-marshaller-roundtrip-some", () =>
{
    // Roundtrip: pass Some(21) → Swift doubles it → returns Some(42).
    // Tests CustomMarshaller in BOTH directions (param + return).
    using var opt = SwiftOptional<int>.NewSome(21);
    using var result = Blocker2PInvokes.DoubleOptionalInt32(opt);
    if (result.Case == SwiftOptionalCases.Some && result.Value == 42)
        Console.WriteLine("PASS: b2-marshaller-roundtrip-some");
    else
        throw new Exception($"Expected Some(42), got {result.Case}({(result.Case == SwiftOptionalCases.Some ? result.Value : 0)})");
});

RunTest("b2-marshaller-roundtrip-none", () =>
{
    // Roundtrip: pass None → Swift returns nil.
    using var opt = SwiftOptional<int>.NewNone();
    using var result = Blocker2PInvokes.DoubleOptionalInt32(opt);
    if (result.Case == SwiftOptionalCases.None)
        Console.WriteLine("PASS: b2-marshaller-roundtrip-none");
    else
        throw new Exception($"Expected None, got {result.Case}");
});

RunTest("b2-raw-blittable-optional", () =>
{
    // Direct blittable struct without CustomMarshaller — verifies the ABI layout is correct.
    // This bypasses the marshaller entirely: just pass BlittableOptionalInt32 directly.
    var blittable = new BlittableOptionalInt32 { Value = 99, Discriminator = 0 };
    int result = Blocker2PInvokes.AcceptOptionalInt32Raw(blittable);
    if (result == 99)
        Console.WriteLine("PASS: b2-raw-blittable-optional");
    else
        throw new Exception($"Expected 99, got {result}");
});

// -----------------------------------------------------------------------
// SafeHandle + LibraryImport + CallConvSwift experiments
// Question: Does LibraryImport's built-in SafeHandle marshalling work with
// CallConvSwift? (It extracts IntPtr via DangerousGetHandle at compile time.)
// -----------------------------------------------------------------------

RunTest("b2-safehandle-libimport", () =>
{
    // LibraryImport has built-in SafeHandle support — extracts IntPtr in generated stub.
    // Test: allocate memory, write an int, pass SafeHandle to Swift, read it back.
    unsafe
    {
        var mem = (int*)NativeMemory.AllocZeroed(sizeof(int));
        *mem = 42;
        using var handle = new NativeMemoryHandle(new IntPtr(mem));
        int result = Blocker2PInvokes.ReadInt32FromPtr(handle);
        if (result == 42)
            Console.WriteLine("PASS: b2-safehandle-libimport");
        else
            throw new Exception($"Expected 42, got {result}");
    }
});

RunTest("b2-safehandle-write", () =>
{
    // SafeHandle for mutable pointer — Swift writes through it.
    unsafe
    {
        var mem = (int*)NativeMemory.AllocZeroed(sizeof(int));
        using var handle = new NativeMemoryHandle(new IntPtr(mem));
        Blocker2PInvokes.WriteInt32ToPtr(handle, 99);
        if (*mem == 99)
            Console.WriteLine("PASS: b2-safehandle-write");
        else
            throw new Exception($"Expected 99, got {*mem}");
    }
});

// -----------------------------------------------------------------------
// SwiftString + CallConvSwift experiments
// Question: Can we pass SwiftString (16-byte struct, non-blittable class)
// through CallConvSwift via a blittable 16-byte struct + CustomMarshaller?
// -----------------------------------------------------------------------

RunTest("b2-string-raw-blittable", () =>
{
    // ABI verification: pass a BlittableSwiftString directly (no marshaller).
    // Construct the raw words from a known SwiftString, pass to Swift, verify length.
    unsafe
    {
        using var str = new SwiftString("Hello");
        bool success = false;
        str.Payload.DangerousAddRef(ref success);
        try
        {
            var ptr = (nint*)str.Payload.DangerousGetHandle();
            var blittable = new BlittableSwiftString { Word0 = ptr[0], Word1 = ptr[1] };
            int len = Blocker2PInvokes.StringLengthRaw(blittable);
            if (len == 5)
                Console.WriteLine("PASS: b2-string-raw-blittable");
            else
                throw new Exception($"Expected 5, got {len}");
        }
        finally
        {
            if (success) str.Payload.DangerousRelease();
        }
    }
});

RunTest("b2-string-marshaller", () =>
{
    // KEY EXPERIMENT: Pass SwiftString through CustomMarshaller + CallConvSwift.
    // The marshaller copies 16 raw bytes into BlittableSwiftString.
    using var str = new SwiftString("NativeAOT");
    int len = Blocker2PInvokes.StringLength(str);
    if (len == 9)
        Console.WriteLine("PASS: b2-string-marshaller");
    else
        throw new Exception($"Expected 9, got {len}");
});

RunTest("b2-string-marshaller-emoji", () =>
{
    // Multi-byte UTF-8: emoji. Swift's String.count returns grapheme clusters.
    using var str = new SwiftString("Hi \U0001F389!");
    int len = Blocker2PInvokes.StringLength(str);
    if (len == 5)  // H, i, space, party popper, !
        Console.WriteLine("PASS: b2-string-marshaller-emoji");
    else
        throw new Exception($"Expected 5, got {len}");
});

RunTest("b2-string-return-marshaller", () =>
{
    // Return direction: Swift returns a String, CustomMarshaller converts to SwiftString.
    using var input = new SwiftString("ab");
    using var result = Blocker2PInvokes.StringRepeat(input, 3);
    var str = result.ToString();
    if (str == "ababab")
        Console.WriteLine("PASS: b2-string-return-marshaller");
    else
        throw new Exception($"Expected 'ababab', got '{str}'");
});

RunTest("b2-string-stress", () =>
{
    // Stress test: 10k iterations of return marshalling with heap-backed strings.
    // Validates +1 ownership transfer in SwiftStringMarshaller.ConvertToManaged —
    // each iteration allocates a Swift String, marshals through BlittableSwiftString,
    // creates a managed SwiftString, reads it, and disposes. No leak or double-free
    // should occur across 10k cycles.
    using var input = new SwiftString("stress-test-value!");  // 18 chars — heap-backed
    const string expected = "stress-test-value!stress-test-value!stress-test-value!";
    for (int i = 0; i < 10_000; i++)
    {
        using var result = Blocker2PInvokes.StringRepeat(input, 3);
        var str = result.ToString();
        if (str != expected)
            throw new Exception($"Iteration {i}: expected '{expected}', got '{str}'");
    }
    Console.WriteLine("PASS: b2-string-stress (10k iterations, heap-backed)");
});

RunTest("b2-optstring-some", () =>
{
    // Optional<String> with a value. Extra-inhabitant type — no discriminator byte.
    using var str = new SwiftString("test");
    int len = Blocker2PInvokes.OptionalStringLength(str);
    if (len == 4)
        Console.WriteLine("PASS: b2-optstring-some");
    else
        throw new Exception($"Expected 4, got {len}");
});

RunTest("b2-optstring-none", () =>
{
    // Optional<String> None — extra inhabitants encode nil as a pointer sentinel.
    int len = Blocker2PInvokes.OptionalStringLength(null);
    if (len == -1)
        Console.WriteLine("PASS: b2-optstring-none");
    else
        throw new Exception($"Expected -1, got {len}");
});

// -----------------------------------------------------------------------
// Summary
// -----------------------------------------------------------------------

Console.WriteLine();
Console.WriteLine("-----------------------------------------");
Console.WriteLine($"  Passed:  {passCount}");
Console.WriteLine($"  Failed:  {failCount}");
Console.WriteLine($"  Crashed: {crashCount}");
Console.WriteLine("-----------------------------------------");

if (failCount > 0 || crashCount > 0)
{
    Console.WriteLine("OVERALL: FAIL");
    return 1;
}
Console.WriteLine("OVERALL: PASS");
return 0;

// =======================================================================
// Blocker 2: CustomMarshaller types and P/Invoke declarations
// =======================================================================

/// <summary>
/// Blittable representation of Swift Optional&lt;Int32&gt;.
/// Layout: 4-byte value + 1-byte discriminator = 5 bytes.
/// On ARM64, CallConvSwift decomposes this into two registers (int + byte),
/// matching Swift's own lowering of Optional&lt;Int32&gt;.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct BlittableOptionalInt32
{
    public int Value;
    public byte Discriminator; // 0 = Some, 1 = None
}

/// <summary>
/// CustomMarshaller that lowers SwiftOptional&lt;int&gt; to BlittableOptionalInt32.
/// Used with [LibraryImport] + [MarshalUsing] to test whether source-generated
/// marshalling can produce a blittable intermediate for CallConvSwift.
/// </summary>
[CustomMarshaller(typeof(SwiftOptional<int>), MarshalMode.Default, typeof(SwiftOptionalInt32Marshaller))]
public static class SwiftOptionalInt32Marshaller
{
    public static BlittableOptionalInt32 ConvertToUnmanaged(SwiftOptional<int> managed)
    {
        if (managed == null || managed.Case == SwiftOptionalCases.None)
            return new BlittableOptionalInt32 { Value = 0, Discriminator = 1 };
        return new BlittableOptionalInt32 { Value = managed.Value, Discriminator = 0 };
    }

    public static SwiftOptional<int> ConvertToManaged(BlittableOptionalInt32 unmanaged)
    {
        return unmanaged.Discriminator == 0
            ? SwiftOptional<int>.NewSome(unmanaged.Value)
            : SwiftOptional<int>.NewNone();
    }

    public static void Free(BlittableOptionalInt32 _) { }
}

/// <summary>
/// P/Invoke declarations for Blocker 2 experiments.
/// Uses [LibraryImport] (source-generated stubs) instead of [DllImport].
/// The CustomMarshaller converts non-blittable SwiftOptional&lt;int&gt; to
/// blittable BlittableOptionalInt32 before the native call.
/// </summary>
public static partial class Blocker2PInvokes
{
    // Baseline: [LibraryImport] + CallConvSwift with only blittable types.
    [LibraryImport("libNativeAotSwiftLib", EntryPoint = "nativeaot_add_int32")]
    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
    public static partial int AddInt32(int a, int b);

    // KEY EXPERIMENT: Non-blittable SwiftOptional<int> lowered via CustomMarshaller.
    // Source generator should produce a stub that passes BlittableOptionalInt32 (blittable)
    // to the native call, even though the public signature uses SwiftOptional<int>.
    [LibraryImport("libNativeAotSwiftLib", EntryPoint = "nativeaot_accept_optional_int32")]
    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
    public static partial int AcceptOptionalInt32(
        [MarshalUsing(typeof(SwiftOptionalInt32Marshaller))] SwiftOptional<int> value);

    // Roundtrip: CustomMarshaller on both param AND return type.
    [LibraryImport("libNativeAotSwiftLib", EntryPoint = "nativeaot_double_optional_int32")]
    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
    [return: MarshalUsing(typeof(SwiftOptionalInt32Marshaller))]
    public static partial SwiftOptional<int> DoubleOptionalInt32(
        [MarshalUsing(typeof(SwiftOptionalInt32Marshaller))] SwiftOptional<int> value);

    // Raw blittable struct — no CustomMarshaller, passes BlittableOptionalInt32 directly.
    // Verifies the ABI layout is correct independent of the marshalling layer.
    [LibraryImport("libNativeAotSwiftLib", EntryPoint = "nativeaot_accept_optional_int32")]
    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
    public static partial int AcceptOptionalInt32Raw(BlittableOptionalInt32 value);

    // --- SafeHandle experiments ---

    // LibraryImport has built-in SafeHandle marshalling — source generator extracts
    // IntPtr via DangerousGetHandle() and adds DangerousAddRef/DangerousRelease.
    // Question: does the generated blittable stub (with IntPtr) work under CallConvSwift?
    [LibraryImport("libNativeAotSwiftLib", EntryPoint = "nativeaot_read_int32_from_ptr")]
    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
    public static partial int ReadInt32FromPtr(NativeMemoryHandle ptr);

    [LibraryImport("libNativeAotSwiftLib", EntryPoint = "nativeaot_write_int32_to_ptr")]
    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
    public static partial void WriteInt32ToPtr(NativeMemoryHandle ptr, int value);

    // --- SwiftString experiments ---

    // Raw blittable: pass BlittableSwiftString directly (ABI verification).
    [LibraryImport("libNativeAotSwiftLib", EntryPoint = "nativeaot_string_length")]
    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
    public static partial int StringLengthRaw(BlittableSwiftString value);

    // CustomMarshaller: SwiftString → BlittableSwiftString → CallConvSwift.
    [LibraryImport("libNativeAotSwiftLib", EntryPoint = "nativeaot_string_length")]
    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
    public static partial int StringLength(
        [MarshalUsing(typeof(SwiftStringMarshaller))] SwiftString value);

    // Return marshalling: Swift returns a String → BlittableSwiftString → SwiftString.
    [LibraryImport("libNativeAotSwiftLib", EntryPoint = "nativeaot_string_repeat")]
    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
    [return: MarshalUsing(typeof(SwiftStringMarshaller))]
    public static partial SwiftString StringRepeat(
        [MarshalUsing(typeof(SwiftStringMarshaller))] SwiftString value, int count);

    // Optional<String> with CustomMarshaller — extra-inhabitant type (16 bytes, no discriminator).
    [LibraryImport("libNativeAotSwiftLib", EntryPoint = "nativeaot_optional_string_length")]
    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
    public static partial int OptionalStringLength(
        [MarshalUsing(typeof(OptionalSwiftStringMarshaller))] SwiftString? value);
}

// =======================================================================
// SafeHandle for native memory
// =======================================================================

/// <summary>
/// Simple SafeHandle wrapping NativeMemory.Alloc'd memory.
/// Used to test LibraryImport's built-in SafeHandle marshalling with CallConvSwift.
/// </summary>
public class NativeMemoryHandle : SafeHandle
{
    public NativeMemoryHandle(IntPtr ptr) : base(IntPtr.Zero, true) { SetHandle(ptr); }
    public override bool IsInvalid => handle == IntPtr.Zero;
    protected override unsafe bool ReleaseHandle()
    {
        NativeMemory.Free((void*)handle);
        return true;
    }
}

// =======================================================================
// SwiftString blittable representation and marshaller
// =======================================================================

/// <summary>
/// Blittable representation of Swift String (16 bytes = two pointer-sized words).
/// On ARM64, CallConvSwift passes this in two registers (x0 + x1).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct BlittableSwiftString
{
    public nint Word0;
    public nint Word1;
}

/// <summary>
/// CustomMarshaller: SwiftString → BlittableSwiftString (16-byte raw copy).
/// Input: copies raw payload words from SwiftString's SafeHandle buffer.
/// Output: allocates temp buffer, writes words, creates SwiftString via VWT InitializeWithCopy.
/// </summary>
[CustomMarshaller(typeof(SwiftString), MarshalMode.Default, typeof(SwiftStringMarshaller))]
public static class SwiftStringMarshaller
{
    public static unsafe BlittableSwiftString ConvertToUnmanaged(SwiftString managed)
    {
        if (managed == null)
            return default;
        bool success = false;
        managed.Payload.DangerousAddRef(ref success);
        try
        {
            var ptr = (nint*)managed.Payload.DangerousGetHandle();
            return new BlittableSwiftString { Word0 = ptr[0], Word1 = ptr[1] };
        }
        finally
        {
            if (success) managed.Payload.DangerousRelease();
        }
    }

    public static unsafe SwiftString ConvertToManaged(BlittableSwiftString unmanaged)
    {
        // Swift returned a String with +1 ownership (two raw words in registers).
        // Write them into a temp buffer, then use MarshalFromSwift which calls
        // SwiftString(IntPtr) — a raw byte copy (ownership transfer), NOT
        // InitializeWithCopy. The +1 refcount from the Swift return value is
        // transferred to the new SwiftString. We must NOT call VWT.Destroy here —
        // that would decrement the refcount on heap-backed strings, leaving the
        // new SwiftString with a dangling pointer. NativeMemory.Free only frees
        // the 16-byte temp buffer without touching the Swift refcount.
        var temp = (nint*)NativeMemory.Alloc((nuint)(2 * sizeof(nint)));
        try
        {
            temp[0] = unmanaged.Word0;
            temp[1] = unmanaged.Word1;
            return SwiftMarshal.MarshalFromSwift<SwiftString>(new IntPtr(temp));
        }
        finally
        {
            NativeMemory.Free(temp);
        }
    }

    public static void Free(BlittableSwiftString _) { }
}

// =======================================================================
// Optional<String> blittable representation and marshaller
// =======================================================================

/// <summary>
/// CustomMarshaller: nullable SwiftString → BlittableSwiftString for Optional&lt;String&gt;.
/// Extra-inhabitant type: Optional&lt;String&gt; is 16 bytes (same as String).
/// None is encoded as the first extra inhabitant (all-zero pointer word).
/// </summary>
[CustomMarshaller(typeof(SwiftString), MarshalMode.ManagedToUnmanagedIn, typeof(OptionalSwiftStringMarshaller))]
public static class OptionalSwiftStringMarshaller
{
    public static unsafe BlittableSwiftString ConvertToUnmanaged(SwiftString? managed)
    {
        if (managed == null)
            return default; // All zeros = None for extra-inhabitant types
        bool success = false;
        managed.Payload.DangerousAddRef(ref success);
        try
        {
            var ptr = (nint*)managed.Payload.DangerousGetHandle();
            return new BlittableSwiftString { Word0 = ptr[0], Word1 = ptr[1] };
        }
        finally
        {
            if (success) managed.Payload.DangerousRelease();
        }
    }

    public static void Free(BlittableSwiftString _) { }
}
