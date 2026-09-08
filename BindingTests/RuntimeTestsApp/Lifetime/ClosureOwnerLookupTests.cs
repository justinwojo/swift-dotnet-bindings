// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using System.Text;
using RuntimeTestsApp.Infrastructure;
using Swift.Runtime;

namespace RuntimeTestsApp.Lifetime;

/// <summary>Observes the process symbol namespace used by generated owner helpers.</summary>
public class ClosureOwnerLookupTests : TestBase
{
    public ClosureOwnerLookupTests(TestResults results) : base(results) { }

    public unsafe void TestProcessFactoryMatchesRuntimeExport()
    {
        // Module initialization has already attempted destroy registration.
        // Query process visibility before requesting any additional native handle.
        var process = Dlopen(IntPtr.Zero, 0);
        var symbol = Encoding.UTF8.GetBytes("SwiftBindings_NewClosureContext\0");
        fixed (byte* symbolPtr = symbol)
        {
            var current = Dlsym(process, symbolPtr);
            Console.WriteLine($"CLOSURE_LOOKUP: process={current:x}; process-handle={process:x}");
            Console.WriteLine($"CLOSURE_LOOKUP: registration={SwiftClosureContext.Status}; " +
                $"diagnostic={SwiftClosureContext.RegistrationDiagnostic ?? "none"}");
            AssertEqual(SwiftClosureContext.OwnerTokenStatus.Ready, SwiftClosureContext.Status,
                SwiftClosureContext.RegistrationDiagnostic ?? "Closure owner registration must be process-visible");
            AssertEqual(SwiftClosureContext.FactoryAddress, current,
                "Process lookup must resolve the factory selected for destroy registration");
            var runtime = SwiftFrameworkResolver.ResolveSwiftFramework(
                "SwiftBindingsRuntime", typeof(SwiftFrameworkResolver).Assembly, null);
            AssertTrue(runtime != IntPtr.Zero, "The runtime resolver could not return the injected native image.");
            try
            {
                bool hasFactory = NativeLibrary.TryGetExport(runtime, "SwiftBindings_NewClosureContext", out var direct);
                string image = "unknown";
                DlInfo info;
                if (direct != IntPtr.Zero && Dladdr(direct, &info) != 0)
                    image = Marshal.PtrToStringUTF8(info.FileName) ?? "null";
                var after = Dlsym(process, symbolPtr);
                Console.WriteLine($"CLOSURE_LOOKUP: direct={direct:x}; process-after-resolver={after:x}; image={image}");
                AssertTrue(hasFactory && direct != IntPtr.Zero, "Runtime image does not export the owner factory.");
                AssertTrue(current == direct,
                    "Generated owner helpers use process lookup, which did not resolve the injected runtime factory. " +
                    "See CLOSURE_LOOKUP addresses and image above.");
            }
            finally
            {
                // Balance this test's independent acquisition. Registration
                // holds its own deliberate process-lifetime reference.
                NativeLibrary.Free(runtime);
            }
        }
        // No global promotion occurs here. This observes current process lookup,
        // not the contents of an existing generated helper's lazy cache.
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DlInfo
    {
        public IntPtr FileName;
        public IntPtr ImageBase;
        public IntPtr SymbolName;
        public IntPtr SymbolAddress;
    }

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "dlopen", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr Dlopen(IntPtr path, int flags);
    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "dlsym", CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe IntPtr Dlsym(IntPtr handle, byte* symbol);
    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "dladdr", CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe int Dladdr(IntPtr address, DlInfo* info);
}
