// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Swift;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;
// The test library exports a public `CallConvCdecl` on purpose, to prove the generator qualifies
// the BCL names it emits. That makes a bare `CallConvCdecl` ambiguous here, where the calling
// convention is meant.
using CallConvCdecl = System.Runtime.CompilerServices.CallConvCdecl;

namespace RuntimeTestsApp.Marshalling;

/// <summary>
/// Isolates the direct <c>CallConvSwift</c> calling-convention shape that combines a
/// <see cref="SwiftIndirectResult"/> (sret) AND a <see cref="SwiftSelf"/> in a single call — the
/// exact x86_64 register layout of the Swift stdlib's <c>Dictionary.updateValue(_:forKey:)</c>
/// (indirect result in %rax, integer arguments in %rdi/%rsi, self pointer in %r13; verified by
/// disassembly). The probes hand-marshal raw swiftcc symbols with no stdlib generics, no type
/// metadata, and no value-witness tables in play, so a divergence cannot be a bug in our
/// generic-collection marshalling — it must lie in the runtime's CallConvSwift trampoline or in this
/// test's own marshalling.
///
/// Each direct probe is paired with a plain-C-ABI (<c>@_cdecl</c>) control performing the identical
/// computation. Heap (NativeMemory) and stack (stackalloc) variants run side-by-side so a divergence
/// between them isolates a stack-aliasing artifact from a real ABI break — the real
/// <c>SwiftDictionary</c> uses heap-allocated buffers, so the heap variant is the
/// dictionary-representative shape.
/// </summary>
public class SretSelfProbeTests : TestBase
{
    public SretSelfProbeTests(TestResults results) : base(results) { }

    private const string TestLib = "SwiftBindingsTestLib";

    // SretSelfProbe.combine(_:_:) — mutating; raw swiftcc. self (inout) -> %r13, x -> %rdi, y -> %rsi,
    // 40-byte indirect return -> sret pointer in %rax. SwiftIndirectResult + SwiftSelf combined.
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvSwift) })]
    [DllImport(TestLib, EntryPoint = "$s20SwiftBindingsTestLib13SretSelfProbeV7combineyACSi_SitF")]
    private static extern unsafe void Combine(SwiftIndirectResult result, long x, long y, SwiftSelf self);

    // Plain-C-ABI control for the same computation: no register travels as sret/self at the boundary.
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    [DllImport(TestLib, EntryPoint = "sbw_sretselfprobe_combine_cdecl")]
    private static extern unsafe void CombineCdecl(void* self, long x, long y, void* outResult);

    // LargeScalarStructFactory.make() — final-class instance method; raw swiftcc. self (class pointer)
    // -> %r13, 40-byte indirect return -> sret pointer in %rax. The no-argument sret+self shape.
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvSwift) })]
    [DllImport(TestLib, EntryPoint = "$s20SwiftBindingsTestLib24LargeScalarStructFactoryC4makeAA0efG0VyF")]
    private static extern unsafe void FactoryMake(SwiftIndirectResult result, SwiftSelf self);

    /// <summary>
    /// Direct CallConvSwift sret + self + args, with HEAP-allocated buffers. This is the
    /// dictionary-representative shape (SwiftDictionary allocates its discarded-result via
    /// NativeMemory and its self handle is heap-resident). A faithful Mono-x86_64 trampoline writes
    /// the 40-byte result through %rax and mutates self through %r13 here.
    /// </summary>
    public unsafe void TestCombineDirectHeapBuffers()
    {
        long* selfBuf = (long*)NativeMemory.Alloc(5, sizeof(long));
        long* sretBuf = (long*)NativeMemory.Alloc(5, sizeof(long));
        try
        {
            selfBuf[0] = 1; selfBuf[1] = 2; selfBuf[2] = 3; selfBuf[3] = 4; selfBuf[4] = 5;
            for (int i = 0; i < 5; i++) sretBuf[i] = -1;

            Combine(new SwiftIndirectResult(sretBuf), 10, 20, new SwiftSelf(selfBuf));

            AssertEqual(11L, sretBuf[0], "heap sret a = 1 + 10");
            AssertEqual(22L, sretBuf[1], "heap sret b = 2 + 20");
            AssertEqual(33L, sretBuf[2], "heap sret c = 3 + (10 + 20)");
            AssertEqual(14L, sretBuf[3], "heap sret d = 4 + 10");
            AssertEqual(25L, sretBuf[4], "heap sret e = 5 + 20");
            AssertEqual(11L, selfBuf[0], "heap self a mutated");
            AssertEqual(22L, selfBuf[1], "heap self b mutated");
            AssertEqual(25L, selfBuf[4], "heap self e mutated");
            TestLogger.Info($"Combine heap = sret({sretBuf[0]},{sretBuf[1]},{sretBuf[2]},{sretBuf[3]},{sretBuf[4]}) self({selfBuf[0]},{selfBuf[1]},{selfBuf[2]},{selfBuf[3]},{selfBuf[4]})");
        }
        finally
        {
            NativeMemory.Free(selfBuf);
            NativeMemory.Free(sretBuf);
        }
    }

    /// <summary>
    /// Direct CallConvSwift sret + self + args, with STACK-allocated (stackalloc) buffers. Paired
    /// with the heap variant: if heap passes and stack corrupts the self-buffer readback on the same
    /// target, the divergence is stack-frame aliasing in the trampoline's spill layout, not the ABI
    /// itself — and irrelevant to the real SwiftDictionary path which uses heap buffers.
    /// </summary>
    public unsafe void TestCombineDirectStackBuffers()
    {
        long* selfBuf = stackalloc long[5];
        selfBuf[0] = 1; selfBuf[1] = 2; selfBuf[2] = 3; selfBuf[3] = 4; selfBuf[4] = 5;
        long* sretBuf = stackalloc long[5];
        for (int i = 0; i < 5; i++) sretBuf[i] = -1;

        Combine(new SwiftIndirectResult(sretBuf), 10, 20, new SwiftSelf(selfBuf));

        AssertEqual(11L, sretBuf[0], "stack sret a = 1 + 10");
        AssertEqual(22L, sretBuf[1], "stack sret b = 2 + 20");
        AssertEqual(33L, sretBuf[2], "stack sret c = 3 + (10 + 20)");
        AssertEqual(14L, sretBuf[3], "stack sret d = 4 + 10");
        AssertEqual(25L, sretBuf[4], "stack sret e = 5 + 20");
        AssertEqual(11L, selfBuf[0], "stack self a mutated");
        AssertEqual(25L, selfBuf[4], "stack self e mutated");
        TestLogger.Info($"Combine stack = sret({sretBuf[0]},{sretBuf[1]},{sretBuf[2]},{sretBuf[3]},{sretBuf[4]}) self({selfBuf[0]},{selfBuf[1]},{selfBuf[2]},{selfBuf[3]},{selfBuf[4]})");
    }

    /// <summary>
    /// Plain-C-ABI control: identical computation, no sret/self registers at the managed boundary.
    /// Passes on every target; pairs with the direct probes to localize any divergence.
    /// </summary>
    public unsafe void TestCombineViaCdeclControl()
    {
        long* selfBuf = (long*)NativeMemory.Alloc(5, sizeof(long));
        long* outBuf = (long*)NativeMemory.Alloc(5, sizeof(long));
        try
        {
            selfBuf[0] = 1; selfBuf[1] = 2; selfBuf[2] = 3; selfBuf[3] = 4; selfBuf[4] = 5;
            for (int i = 0; i < 5; i++) outBuf[i] = -1;

            CombineCdecl(selfBuf, 10, 20, outBuf);

            AssertEqual(11L, outBuf[0], "cdecl out a = 1 + 10");
            AssertEqual(22L, outBuf[1], "cdecl out b = 2 + 20");
            AssertEqual(33L, outBuf[2], "cdecl out c = 3 + (10 + 20)");
            AssertEqual(14L, outBuf[3], "cdecl out d = 4 + 10");
            AssertEqual(25L, outBuf[4], "cdecl out e = 5 + 20");
            AssertEqual(11L, selfBuf[0], "cdecl self a mutated");
            TestLogger.Info($"CombineCdecl = ({outBuf[0]},{outBuf[1]},{outBuf[2]},{outBuf[3]},{outBuf[4]})");
        }
        finally
        {
            NativeMemory.Free(selfBuf);
            NativeMemory.Free(outBuf);
        }
    }

    /// <summary>
    /// Direct CallConvSwift sret + self with NO explicit arguments (final-class instance method).
    /// A second independent sret+self shape — class-instance self rather than a struct buffer — that
    /// corroborates the combine probe.
    /// </summary>
    public unsafe void TestFactoryMakeDirectCallConvSwiftSretPlusSelf()
    {
        var factory = new LargeScalarStructFactory(seed: 200L);
        IntPtr selfHandle = factory.Payload.DangerousGetHandle();

        long* sretBuf = (long*)NativeMemory.Alloc(5, sizeof(long));
        try
        {
            for (int i = 0; i < 5; i++) sretBuf[i] = -1;

            FactoryMake(new SwiftIndirectResult(sretBuf), new SwiftSelf((void*)selfHandle));

            AssertEqual(200L, sretBuf[0], "make a = seed");
            AssertEqual(201L, sretBuf[1], "make b = seed + 1");
            AssertEqual(202L, sretBuf[2], "make c = seed + 2");
            AssertEqual(203L, sretBuf[3], "make d = seed + 3");
            AssertEqual(204L, sretBuf[4], "make e = seed + 4");
            TestLogger.Info($"FactoryMake = ({sretBuf[0]},{sretBuf[1]},{sretBuf[2]},{sretBuf[3]},{sretBuf[4]})");
            GC.KeepAlive(factory);
        }
        finally
        {
            NativeMemory.Free(sretBuf);
        }
    }
}
