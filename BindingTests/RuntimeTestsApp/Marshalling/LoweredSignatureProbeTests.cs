// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RuntimeTestsApp.Infrastructure;
// The test library exports a public `CallConvCdecl` on purpose (see SretSelfProbeTests).
using CallConvCdecl = System.Runtime.CompilerServices.CallConvCdecl;

namespace RuntimeTestsApp.Marshalling;

/// <summary>
/// Observes whether the engine applies the Swift calling convention's struct lowering, without
/// risking the process. <c>loweredSignatureProbeEncode(_:)</c> takes a three-word <c>@frozen</c>
/// struct, which the Swift convention passes in three registers and the C convention passes in
/// memory (x86_64) or by reference (arm64), and returns an integer encoding of its fields. A raw
/// <c>CallConvSwift</c> call that skips the lowering still returns, with the encoding of whatever
/// the registers held, so the defect shows as an assertion rather than a crash.
///
/// This is the negative control of the x86_64 CoreCLR interpreter, which applies no Swift lowering:
/// the sret + self probes of <see cref="SretSelfProbeTests"/> write through an unset self register
/// there and take the process down, so that class is excluded on that lane and this one runs in its
/// place, alone, after the suite, graded against the x86_64 interpreter's expectations in the
/// harness's negative-control registry. Every engine that lowers the convention (JIT, ReadyToRun,
/// NativeAOT, Mono, and the arm64 interpreter, whose defects are in the self register, not in struct
/// lowering) passes all three tests, and those lanes run it as an ordinary class. A test added here
/// needs an expected outcome in that registry.
/// </summary>
public class LoweredSignatureProbeTests : TestBase
{
    public LoweredSignatureProbeTests(TestResults results) : base(results) { }

    private const string TestLib = "SwiftBindingsTestLib";

    /// <summary>The C# mirror of <c>LoweredSignatureProbe</c>: three pointer-sized integers.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Probe
    {
        public nint A;
        public nint B;
        public nint C;
    }

    // loweredSignatureProbeEncode(_:) — raw swiftcc; the struct is lowered into three registers.
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvSwift) })]
    [DllImport(TestLib, EntryPoint = "$s20SwiftBindingsTestLib27loweredSignatureProbeEncodeySiAA07LoweredfG0VF")]
    private static extern nint Encode(Probe probe);

    // loweredSignatureProbeEncodeScalars(_:_:_:) — raw swiftcc with three integer arguments.
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvSwift) })]
    [DllImport(TestLib, EntryPoint = "$s20SwiftBindingsTestLib34loweredSignatureProbeEncodeScalarsyS2i_S2itF")]
    private static extern nint EncodeScalars(nint a, nint b, nint c);

    // Plain-C-ABI control: the same struct by address.
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    [DllImport(TestLib, EntryPoint = "sbw_loweredsignatureprobe_encode_cdecl")]
    private static extern unsafe nint EncodeCdecl(Probe* probe);

    private const long Expected = 7_042_301L;

    private static Probe Sample => new() { A = 7, B = 42, C = 301 };

    /// <summary>
    /// The struct passed by value must arrive in the registers the Swift convention assigns it.
    /// Fails on the x86_64 CoreCLR interpreter, which passes it as C does.
    /// </summary>
    public void TestLoweredStructReachesCallee()
    {
        var received = Encode(Sample);
        TestLogger.Info($"loweredSignatureProbeEncode: expected {Expected}, callee returned {received}");
        AssertEqual(Expected, (long)received, "lowered struct");
    }

    /// <summary>
    /// A Swift-convention call with integer arguments only, which places as C does: it shows the
    /// engine makes the call and the arguments arrive, so only the lowered struct is lost.
    /// </summary>
    public void TestSwiftConventionIntegerArgumentsArrive()
        => AssertEqual(Expected, (long)EncodeScalars(7, 42, 301), "encode scalars(7, 42, 301)");

    /// <summary>Plain-C-ABI control of the same callee: passes on every engine.</summary>
    public unsafe void TestLoweredStructViaCdeclControl()
    {
        var probe = Sample;
        AssertEqual(Expected, (long)EncodeCdecl(&probe), "cdecl encode");
    }
}
