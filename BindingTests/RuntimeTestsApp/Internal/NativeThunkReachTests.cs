// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using RuntimeTestsApp.Infrastructure;
using Swift.Runtime;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Internal;

/// <summary>
/// End-to-end proof that a public member retained on an opaque
/// <c>@usableFromInline internal</c> class calls its generated native thunk. The C exports in the
/// fixture only create and observe receivers; every operation under test is the generated concrete
/// <see cref="NativeThunkReceiver.Add(int)"/> member.
/// </summary>
public class NativeThunkReachTests : TestBase
{
    private const string FixtureLibrary = "SwiftBindingsTestLib";
    private const string WrapperLibrary = "SwiftBindings";

    public NativeThunkReachTests(TestResults results) : base(results) { }

    [DllImport(FixtureLibrary, EntryPoint = "bt_native_thunk_create", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr CreateReceiver(int child);

    [DllImport(FixtureLibrary, EntryPoint = "bt_native_thunk_calls", CallingConvention = CallingConvention.Cdecl)]
    private static extern int CallCount();

    [DllImport(FixtureLibrary, EntryPoint = "bt_native_thunk_deinits", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DeinitCount();

    [DllImport(FixtureLibrary, EntryPoint = "bt_native_thunk_sentinel", CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe ulong CallThroughSentinel(
        IntPtr function,
        IntPtr self,
        int delta,
        int* result);

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "dlsym", CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe IntPtr Dlsym(IntPtr handle, byte* symbol);

    [DynamicDependency(DynamicallyAccessedMemberTypes.NonPublicMethods, typeof(NativeThunkReceiver))]
    public unsafe void TestRetainedNativeThunkUnderGcPressure()
    {
        var thunkSymbol = GetGeneratedAddThunkSymbol();

        // Load the exact wrapper named by the generated LibraryImport. dlsym on that image proves
        // the entry point is exported, rather than merely trusting source text or waiting for an
        // EntryPointNotFoundException.
        var wrapperHandle = SwiftFrameworkResolver.ResolveSwiftFramework(
            WrapperLibrary,
            typeof(NativeThunkReceiver).Assembly,
            searchPath: null);
        var callsBefore = 0;
        var deinitsBefore = 0;
        NativeThunkReceiver? baseReceiver = null;
        NativeThunkReceiver? childReceiver = null;

        try
        {
            AssertTrue(wrapperHandle != IntPtr.Zero, "native-thunk wrapper must load");
            var thunkPointer = ResolveExport(wrapperHandle, thunkSymbol);
            AssertTrue(thunkPointer != IntPtr.Zero,
                $"generated thunk {thunkSymbol} must be exported and dlsym-resolvable");

            callsBefore = CallCount();
            deinitsBefore = DeinitCount();

            // The factory returns +1. NativeThunkReceiver's generated NewFromPayload has Adopt
            // semantics, so each managed wrapper owns exactly that reference and Dispose balances
            // it exactly once.
            var basePointer = CreateReceiver(child: 0);
            AssertTrue(basePointer != IntPtr.Zero, "base factory must return a retained receiver");
            baseReceiver = (NativeThunkReceiver)SwiftObjectHelper<NativeThunkReceiver>
                .NewFromPayload(basePointer);

            var childPointer = CreateReceiver(child: 1);
            AssertTrue(childPointer != IntPtr.Zero, "child factory must return a retained receiver");
            childReceiver = (NativeThunkReceiver)SwiftObjectHelper<NativeThunkReceiver>
                .NewFromPayload(childPointer);

            var baseSentinelResult = 0;
            var baseMask = CallThroughSentinel(thunkPointer, basePointer, delta: 5, &baseSentinelResult);
            AssertEqual(0UL, baseMask,
                "base thunk must preserve every sentinel-covered callee-saved register and stack/frame state");
            AssertEqual(22, baseSentinelResult, "base sentinel call must return 17 + 5");

            var childSentinelResult = 0;
            var childMask = CallThroughSentinel(thunkPointer, childPointer, delta: 5, &childSentinelResult);
            AssertEqual(0UL, childMask,
                "override thunk must preserve every sentinel-covered callee-saved register and stack/frame state");
            AssertEqual(122, childSentinelResult,
                "child sentinel call must dispatch through the override");

            AssertEqual(22, baseReceiver.Add(delta: 5), "base warm-up must return 17 + 5");
            AssertEqual(122, childReceiver.Add(delta: 5),
                "child warm-up must dispatch through the override");

            const int groupCount = 10;
            const int callsPerGroup = 100;
            for (var group = 0; group < groupCount; group++)
            {
                for (var call = 0; call < callsPerGroup; call++)
                {
                    var useChild = (((group * callsPerGroup) + call) & 1) != 0;
                    var result = useChild
                        ? childReceiver.Add(delta: 5)
                        : baseReceiver.Add(delta: 5);
                    AssertEqual(useChild ? 122 : 22, result,
                        $"alternating native-thunk call {group * callsPerGroup + call}");
                }

                // Keep both opaque receivers live while forcing unrelated managed movement and
                // collection between call groups. This makes each subsequent call re-enter through
                // the generated DangerousAddRef/SwiftSelf path after GC pressure.
                AllocateManagedPressure(group);
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                GC.KeepAlive(baseReceiver);
                GC.KeepAlive(childReceiver);
            }

            AssertEqual(callsBefore + 1004, CallCount(),
                "two sentinel calls, warm-up, and 1000 alternating calls must reach Swift exactly once each");
            AssertEqual(deinitsBefore, DeinitCount(),
                "retained receivers must not deinit during managed allocation/GC pressure");
        }
        finally
        {
            childReceiver?.Dispose();
            baseReceiver?.Dispose();
            NativeLibrary.Free(wrapperHandle);
        }

        AssertEqual(deinitsBefore + 2, DeinitCount(),
            "the two adopted factory references must deinit exactly once after Dispose");
        AssertEqual(callsBefore + 1004, CallCount(),
            "Dispose must not invoke the tested operation");
        TestLogger.Info($"Native thunk {thunkSymbol}: 1004 exact calls, clean register sentinels, 2 exact deinits.");
    }

    private static string GetGeneratedAddThunkSymbol()
    {
        var candidates = typeof(NativeThunkReceiver)
            .GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Select(method => (Method: method, Import: method.GetCustomAttribute<LibraryImportAttribute>()))
            .Where(candidate => candidate.Method.Name.StartsWith("PInvoke_add_", StringComparison.Ordinal)
                && candidate.Import?.EntryPoint?.StartsWith("thunk_SwiftBindingsTestLib_", StringComparison.Ordinal) == true)
            .ToArray();

        if (candidates.Length != 1)
            throw new InvalidOperationException(
                $"Expected one generated native-thunk import for NativeThunkReceiver.Add; found {candidates.Length}.");

        var import = candidates[0].Import
            ?? throw new InvalidOperationException("Generated native-thunk import attribute is missing.");
        if (!string.Equals(import.LibraryName, WrapperLibrary, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"NativeThunkReceiver.Add imports {import.LibraryName}, expected {WrapperLibrary}.");

        return import.EntryPoint!;
    }

    private static unsafe IntPtr ResolveExport(IntPtr handle, string symbol)
    {
        var byteCount = Encoding.UTF8.GetByteCount(symbol);
        Span<byte> utf8 = stackalloc byte[byteCount + 1];
        Encoding.UTF8.GetBytes(symbol, utf8);
        utf8[byteCount] = 0;
        fixed (byte* symbolPointer = utf8)
            return Dlsym(handle, symbolPointer);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AllocateManagedPressure(int group)
    {
        var pressure = new byte[32][];
        for (var i = 0; i < pressure.Length; i++)
        {
            pressure[i] = new byte[1024 + ((group + i) & 255)];
            pressure[i][0] = (byte)(group + i);
        }
        GC.KeepAlive(pressure);
    }
}
