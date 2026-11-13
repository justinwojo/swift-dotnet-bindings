// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RuntimeTestsApp.Infrastructure;
using Swift;
using Swift.Runtime;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Marshalling;

/// <summary>
/// Members whose Swift-convention import the generator routed through a native Swift bridge: a
/// C-convention twin calls an assembly stub that loads self, the error slot and the indirect result
/// into their Swift registers and calls the member. The runtime never makes the Swift-convention
/// transition for these, so each test is the bridge's own end-to-end check: values arrive in the
/// right registers, ownership is neither lost nor doubled, an error leaves the receiver intact, and
/// the registers the bridge borrows come back to the caller unchanged.
/// </summary>
/// <remarks>
/// The bridges are generated from swiftc lowering evidence, which the iOS and Mac Catalyst
/// regenerations pass, and those regenerations ask for these members' bridges by symbol. The other
/// platforms regenerate without evidence and bind the same members through their <c>@_cdecl</c> wrapper,
/// which takes a C callback like any other parameter. Every behavioral test here runs everywhere;
/// the routing test asserts whichever route the platform's regeneration takes.
/// <see cref="SretSelfProbeTests"/> keeps the raw Swift-convention probes of the same register
/// shapes as negative controls: they make the runtime's own transition on purpose, and the
/// generated-bridge tests below are their counterparts.
/// </remarks>
public class SwiftBridgeTests : TestBase
{
    public SwiftBridgeTests(TestResults results) : base(results) { }

    /// <summary>
    /// On iOS and Mac Catalyst each probe member reaches Swift through exactly one native bridge twin and keeps no
    /// Swift-convention import. Elsewhere no bridge is generated, and each member must take its
    /// <c>@_cdecl</c> wrapper rather than a direct import that carries self in the Swift self register.
    /// </summary>
    [DynamicDependency(DynamicallyAccessedMemberTypes.NonPublicMethods, typeof(SwiftBridgeProbe))]
    public void TestProbeMembersAreRoutedThroughNativeBridges()
    {
        if (!ImportRoutes.BridgesRouted)
        {
            foreach (var member in new[] { "accumulate", "combine", "derive" })
                ImportRoutes.AssertCdeclWrapper(typeof(SwiftBridgeProbe), member);
            return;
        }

        var imports = typeof(SwiftBridgeProbe).GetMethods(BindingFlags.NonPublic | BindingFlags.Static);
        foreach (var member in new[] { "accumulate", "combine", "derive" })
        {
            var forMember = imports.Where(m => m.Name.StartsWith("PInvoke_" + member + "_", StringComparison.Ordinal)).ToArray();
            TestLogger.Info($"SwiftBridgeProbe.{member}: {string.Join(", ", forMember.Select(m => m.Name))}");
            AssertEqual(1, forMember.Count(m => m.Name.Contains("__SBSR_", StringComparison.Ordinal)),
                $"{member} must reach Swift through exactly one native bridge twin");
            AssertFalse(forMember.Any(m => m.GetCustomAttribute<UnmanagedCallConvAttribute>()?.CallConvs?.Contains(typeof(CallConvSwift)) == true),
                $"{member} must keep no Swift-convention import");
        }
    }

    // ---- self + indirect result + ordinary arguments ---------------------------------------------

    /// <summary>
    /// The bridged counterpart of the raw combine probe: self travels in the self register, the
    /// 40-byte result through the indirect-result register, and the two ordinary arguments in the
    /// first argument registers. Every field reads back a distinct input, so a pointer landing in
    /// the wrong register cannot produce the right struct.
    /// </summary>
    public void TestCombineThroughBridge_SelfIndirectResultAndArguments()
    {
        using var probe = new SwiftBridgeProbe(total: 1000);
        var result = probe.Combine(10, 20, v => v * 3);

        AssertEqual(1030L, result.A, "a = total + map(x)");
        AssertEqual(1060L, result.B, "b = total + map(y)");
        AssertEqual(90L, result.C, "c = map(x + y)");
        AssertEqual(10L, result.D, "d = x");
        AssertEqual(20L, result.E, "e = y");
        AssertEqual(1000L, probe.Total, "combine must not disturb the receiver");
    }

    /// <summary>
    /// The heap-buffer variant of the raw probe has no bridged analogue to share buffers with, so the
    /// bridged counterpart repeats the call against many receivers instead: each result must name its
    /// own receiver, which a self register left over from a previous call would not.
    /// </summary>
    public void TestCombineThroughBridge_EachReceiverIsItsOwnSelf()
    {
        var probes = Enumerable.Range(1, 16).Select(i => new SwiftBridgeProbe(total: i * 100L)).ToArray();
        try
        {
            for (int i = 0; i < probes.Length; i++)
            {
                var result = probes[i].Combine(i, -i, v => v);
                AssertEqual((i + 1) * 100L + i, result.A, $"receiver {i}: a");
                AssertEqual((i + 1) * 100L - i, result.B, $"receiver {i}: b");
                AssertEqual(0L, result.C, $"receiver {i}: c");
            }
        }
        finally
        {
            foreach (var probe in probes)
                probe.Dispose();
        }
    }

    // ---- self + error + scalar result ------------------------------------------------------------

    /// <summary>
    /// success, error, success on one receiver: the error must surface as a managed exception, leave
    /// the receiver's state as it was, and leave the receiver usable — the error register the bridge
    /// clears before the call and reads after it must not leak into the next call.
    /// </summary>
    public void TestAccumulateThroughBridge_SuccessErrorSuccessKeepsTheReceiver()
    {
        using var probe = new SwiftBridgeProbe(total: 5);

        AssertEqual(12L, probe.Accumulate(7, v => v), "first success adds the mapped delta");
        AssertThrows<SwiftException>(() => probe.Accumulate(4, v => -v), "a negative mapped delta throws");
        AssertEqual(12L, probe.Total, "the throwing call must not change the receiver");
        AssertEqual(15L, probe.Accumulate(3, v => v), "the receiver keeps working after the error");
        AssertEqual(15L, probe.Total, "the receiver holds the second success");
    }

    /// <summary>Alternating errors and successes many times over, so a stale error slot or self shows up.</summary>
    public void TestAccumulateThroughBridge_AlternatingErrorsNeverLeak()
    {
        using var probe = new SwiftBridgeProbe(total: 0);
        long expected = 0;
        for (int i = 1; i <= 200; i++)
        {
            if (i % 3 == 0)
            {
                AssertThrows<SwiftException>(() => probe.Accumulate(i, v => -v), $"call {i} throws");
            }
            else
            {
                expected += i;
                AssertEqual(expected, probe.Accumulate(i, v => v), $"call {i} succeeds");
            }
        }
        AssertEqual(expected, probe.Total, "running total after the alternation");
    }

    // ---- ownership -------------------------------------------------------------------------------

    /// <summary>
    /// An owned class reference crosses the bridge in, a new one comes back out: once both managed
    /// wrappers are disposed every tracked Swift object must be gone, and none may go early. A bridge
    /// that dropped or duplicated a retain shows up as a leak or a use-after-free here.
    /// </summary>
    public void TestDeriveThroughBridge_OwnershipBalances()
    {
        LifetimeTracker.RunWithLeakCheck(() =>
        {
            using var probe = new SwiftBridgeProbe(total: 0);
            using var source = TestLibFunctions.CreateTrackedObject(7, "seed");
            for (int i = 0; i < 64; i++)
            {
                using var derived = probe.Derive(source, v => v * 3);
                AssertEqual(21, derived.ObjectId, $"iteration {i}: derived id");
                AssertEqual("seed+", derived.Label.ToString(), $"iteration {i}: derived label");
                AssertEqual(7, source.ObjectId, $"iteration {i}: the argument stays alive and unchanged");
            }
            LifetimeTracker.AssertLiveCount(1, "only the source may be live once every derived object is disposed");
        }, "SwiftBridgeProbe.Derive");
    }

    // ---- borrowed registers ----------------------------------------------------------------------

    /// <summary>
    /// The bridge borrows callee-saved registers (the self and error registers on arm64, their
    /// counterparts on x86_64) and must hand them back. Twelve values derived from runtime input stay
    /// live across every call, including the throwing ones, so the compiler keeps as many as it can
    /// in callee-saved registers; any one changing means a bridge returned without restoring it.
    /// </summary>
    public void TestBridgeCallsPreserveTheCallersLiveValues()
    {
        long seed = Environment.TickCount64 | 1;
        long s0 = seed * 3, s1 = seed ^ 0x5A5A, s2 = seed + 17, s3 = seed * 7 - 1, s4 = ~seed, s5 = seed << 3;
        long s6 = seed - 99, s7 = seed * 11, s8 = seed ^ 0x1234_5678, s9 = seed + 0x0F0F, s10 = seed * 13, s11 = -seed;
        long expected = Sum(s0, s1, s2, s3, s4, s5, s6, s7, s8, s9, s10, s11);

        using var probe = new SwiftBridgeProbe(total: 0);
        for (int i = 0; i < 32; i++)
        {
            probe.Accumulate(1, v => v);
            try
            {
                probe.Accumulate(1, v => -v);
            }
            catch (SwiftException)
            {
            }
            var result = probe.Combine(s0 & 0xFF, s1 & 0xFF, v => v);
            AssertEqual(s0 & 0xFF, result.D, $"call {i}: d");
            AssertEqual(expected, Sum(s0, s1, s2, s3, s4, s5, s6, s7, s8, s9, s10, s11), $"call {i}: live values");
        }
        AssertEqual(32L, probe.Total, "one success per iteration");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long Sum(long a, long b, long c, long d, long e, long f, long g, long h, long i, long j, long k, long l) =>
        unchecked(a + b * 3 + c * 5 + d * 7 + e * 11 + f * 13 + g * 17 + h * 19 + i * 23 + j * 29 + k * 31 + l * 37);
}
