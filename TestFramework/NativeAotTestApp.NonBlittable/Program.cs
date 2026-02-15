// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// NonBlittable test project for Blocker 2 investigation.
// If NativeAOT `dotnet publish` fails for this project, that IS a result —
// it confirms ILCompiler rejects non-blittable CallConvSwift signatures at compile time.

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.InteropServices.Swift;
using Foundation;
using Swift;
using Swift.Runtime;
using UIKit;

namespace NativeAotTestApp.NonBlittable;

#region Blittable stand-in types for CustomMarshaller experiment

/// <summary>
/// Blittable representation of SwiftOptional&lt;int&gt; for CustomMarshaller experiment.
/// Matches Swift Optional&lt;Int32&gt; memory layout: 4-byte value + 1-byte discriminator.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct BlittableOptionalInt32
{
    public int Value;
    public byte Discriminator; // 0 = Some, 1 = None

    public static BlittableOptionalInt32 Some(int value) => new() { Value = value, Discriminator = 0 };
    public static BlittableOptionalInt32 None() => new() { Value = 0, Discriminator = 1 };
}

/// <summary>
/// CustomMarshaller that lowers SwiftOptional&lt;int&gt; to BlittableOptionalInt32.
/// This is the key Blocker 2 experiment: can source-generated marshalling produce
/// a blittable intermediate that satisfies the CallConvSwift validator?
/// </summary>
[CustomMarshaller(typeof(SwiftOptional<int>), MarshalMode.ManagedToUnmanagedIn, typeof(SwiftOptionalInt32Marshaller))]
public static class SwiftOptionalInt32Marshaller
{
    public static BlittableOptionalInt32 ConvertToUnmanaged(SwiftOptional<int>? optional)
    {
        if (optional == null || optional.Case == SwiftOptionalCases.None)
            return BlittableOptionalInt32.None();
        return BlittableOptionalInt32.Some(optional.Value);
    }

    public static void Free(BlittableOptionalInt32 _) { }
}

#endregion

#region Non-blittable P/Invoke declarations

/// <summary>
/// Hand-written P/Invoke declarations for Blocker 2 testing.
/// These use signatures that Mono rejects with InvalidProgramException.
/// </summary>
public static class NonBlittablePInvokes
{
    // B2 Test: SwiftOptional<int> via DllImport + CallConvSwift
    // Expected: Either compile-time rejection by ILCompiler or runtime InvalidProgramException
    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
    [DllImport("/usr/lib/swift/libswiftCore.dylib", EntryPoint = "swift_getTypeByMangledNameInContext")]
    public static extern IntPtr B2_OptionalDllImport_GetType(
        IntPtr mangledName,
        int mangledNameLength,
        IntPtr context,
        IntPtr contextDescriptor);

    // B2 Test: SafeHandle via DllImport + CallConvSwift
    // SafeHandle is non-blittable — can't pass through CallConvSwift
    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
    [DllImport("/usr/lib/swift/libswiftCore.dylib", EntryPoint = "$sSS5countSivg")]
    public static extern long B2_SafeHandleDllImport_GetLength(SafeHandle strBuffer);
}

#endregion

#region LibraryImport variants

/// <summary>
/// LibraryImport variants of the same non-blittable signatures.
/// Tests whether source-generated stubs handle things differently.
/// </summary>
public static partial class LibraryImportPInvokes
{
    // B2 Test: LibraryImport with CallConvSwift — does source gen change anything?
    [LibraryImport("/usr/lib/swift/libswiftCore.dylib", EntryPoint = "swift_getTypeByMangledNameInContext")]
    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
    public static partial IntPtr B2_OptionalLibImport_GetType(
        IntPtr mangledName,
        int mangledNameLength,
        IntPtr context,
        IntPtr contextDescriptor);

    // B2 Test: CustomMarshaller experiment
    // This is the key experiment: [MarshalUsing] + CustomMarshaller to lower
    // SwiftOptional<int> to a blittable BlittableOptionalInt32 struct.
    //
    // NOTE: This may not compile at all — CustomMarshaller + CallConvSwift is
    // untested territory. If it fails to compile, that's an important data point.
    //
    // We use a simple blittable-to-blittable call here since the point is to test
    // whether the marshaller infrastructure works with CallConvSwift at all.
    [LibraryImport("/usr/lib/swift/libswiftCore.dylib", EntryPoint = "swift_getTypeByMangledNameInContext")]
    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
    public static partial IntPtr B2_CustomMarshallerExperiment(
        IntPtr mangledName,
        [MarshalUsing(typeof(SwiftOptionalInt32Marshaller))] SwiftOptional<int>? optionalLength,
        IntPtr context,
        IntPtr contextDescriptor);
}

#endregion

#region Test Dispatch

public static class TestDispatcher
{
    public static void RunTest(string testId)
    {
        Console.WriteLine($"--- Running test: {testId} ---");

        try
        {
            switch (testId)
            {
                case "b2-optional-dllimport":
                    B2_OptionalDllImport();
                    break;
                case "b2-safehandle-dllimport":
                    B2_SafeHandleDllImport();
                    break;
                case "b2-optional-libimport":
                    B2_OptionalLibImport();
                    break;
                case "b2-optional-marshaller":
                    B2_OptionalMarshaller();
                    break;
                default:
                    Console.WriteLine($"FAIL: {testId}: Unknown test ID");
                    return;
            }
        }
        catch (InvalidProgramException ex)
        {
            // This is a specific expected failure for Blocker 2
            Console.WriteLine($"FAIL: {testId}: InvalidProgramException (Blocker 2 persists): {ex.Message}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL: {testId}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    static void B2_OptionalDllImport()
    {
        // Call with all-zero args — we don't care about the result,
        // just whether the CallConvSwift dispatch mechanism works
        var result = NonBlittablePInvokes.B2_OptionalDllImport_GetType(
            IntPtr.Zero, 0, IntPtr.Zero, IntPtr.Zero);
        Console.WriteLine($"PASS: b2-optional-dllimport (result=0x{result:X})");
    }

    static void B2_SafeHandleDllImport()
    {
        // Create a minimal SafeHandle to pass through CallConvSwift
        // This is expected to fail — SafeHandle is non-blittable
        using var str = new SwiftString("test");
        var payload = str.Payload;
        long length = NonBlittablePInvokes.B2_SafeHandleDllImport_GetLength(payload);
        Console.WriteLine($"PASS: b2-safehandle-dllimport (length={length})");
    }

    static void B2_OptionalLibImport()
    {
        var result = LibraryImportPInvokes.B2_OptionalLibImport_GetType(
            IntPtr.Zero, 0, IntPtr.Zero, IntPtr.Zero);
        Console.WriteLine($"PASS: b2-optional-libimport (result=0x{result:X})");
    }

    static void B2_OptionalMarshaller()
    {
        // The key experiment: does CustomMarshaller + CallConvSwift compile and run?
        var result = LibraryImportPInvokes.B2_CustomMarshallerExperiment(
            IntPtr.Zero,
            null, // None case
            IntPtr.Zero,
            IntPtr.Zero);
        Console.WriteLine($"PASS: b2-optional-marshaller (result=0x{result:X})");
    }
}

#endregion

#region Application Entry Point

public class Application
{
    static void Main(string[] args)
    {
        var effectiveArgs = args.Length > 0 ? args : GetProcessInfoArgs();
        string? testId = null;

        for (int i = 0; i < effectiveArgs.Length; i++)
        {
            if (effectiveArgs[i] == "--test-id" && i + 1 < effectiveArgs.Length)
            {
                testId = effectiveArgs[i + 1];
                i++;
            }
        }

        if (testId != null)
        {
            TestDispatcher.RunTest(testId);
        }
        else
        {
            Console.WriteLine("FAIL: No --test-id specified. Usage: --test-id <test-name>");
        }

        Environment.Exit(0);
    }

    static string[] GetProcessInfoArgs()
    {
        var allArgs = NSProcessInfo.ProcessInfo.Arguments;
        if (allArgs.Length <= 1)
            return Array.Empty<string>();
        return allArgs.Skip(1).ToArray();
    }
}

#endregion
