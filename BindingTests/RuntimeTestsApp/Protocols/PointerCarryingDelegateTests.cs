// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Protocols;

/// <summary>
/// Reverse dispatch of a frozen struct that carries a pointer Swift will dereference.
///
/// <para>
/// The consumer-owned carrier lane answers a collected implementation with the return type's
/// identity value instead of killing the process. That is only sound when the synthesized value is
/// genuinely inhabitable, and blittability does not establish that: <c>PointerHolder</c> is frozen
/// and reference-free — so it travels inline as bytes — yet its stored <c>UnsafeRawPointer</c>
/// excludes null, and the Swift host loads through that field the moment it receives the struct.
/// <c>ExtentHolder</c> is the same transport class with numeric fields only, whose all-zero form
/// really is a value of the type.
/// </para>
///
/// <para>
/// What runs here is the LIVE half: a held implementation hands back a real holder and Swift reads
/// through it, which is the behaviour a consumer depends on and the thing a wrong marshalling
/// story breaks first. The degraded half is asserted at the generator layer instead
/// (<c>ProtocolProxyEmitterTests.EmitProxyClass_FrozenStructWithPointerField_*</c> and its nested
/// and numeric-only siblings): the policy for the pointer-bearing struct is a fail-fast terminal,
/// so driving it would take the whole test process down, and a passing suite cannot contain it.
/// </para>
/// </summary>
public class PointerCarryingDelegateTests : TestBase
{
    public PointerCarryingDelegateTests(TestResults results) : base(results) { }

    private const byte ProbeValue = 0xA7;

    /// <summary>
    /// The round trip the pointer-bearing struct exists for: C# builds a <c>PointerHolder</c> over
    /// an address Swift itself minted, the Swift host reads the getter and dereferences the pointer
    /// inside the returned struct, and the byte comes back. A struct that lost its field — zeroed,
    /// truncated, or read out of the wrong slot — faults or reports the wrong byte here.
    /// </summary>
    public void TestLiveGetterPreservesThePointerSwiftDereferences()
    {
        var probe = Functions.AllocateProbeByte(ProbeValue);
        try
        {
            var host = new PointerCarryingHost();
            var del = new PointerCarryingDelegateImpl(probe);
            host.Delegate = del;

            AssertTrue(host.HasDelegate, "the weak delegate slot still resolves after assignment");

            AssertEqual((int)ProbeValue, (int)host.ReadHeldByte(),
                "Swift loaded through the pointer carried inside the struct the C# getter returned; " +
                "a zeroed or corrupted holder cannot produce this byte.");
            AssertEqual((long)probe, (long)host.ReadHeldAddress(),
                "the exact address survived the round trip, not merely some readable one");

            GC.KeepAlive(del);
        }
        finally
        {
            Functions.FreeProbeByte(probe);
        }
    }

    /// <summary>
    /// The same getter fired repeatedly, with a forced collection in between while the
    /// implementation stays held. The pointer must still be the one the consumer set: a receiver
    /// that started answering from the degraded arm — or that lost the rooting — would surface as a
    /// zero address rather than as a wrong byte.
    /// </summary>
    public void TestRepeatedGetterKeepsThePointerAcrossCollection()
    {
        var probe = Functions.AllocateProbeByte(ProbeValue);
        try
        {
            var host = new PointerCarryingHost();
            var del = new PointerCarryingDelegateImpl(probe);
            host.Delegate = del;

            AssertEqual((int)ProbeValue, (int)host.ReadHeldByte(), "first read");

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            AssertTrue(host.HasDelegate, "the held implementation survived the collection");
            AssertEqual((int)ProbeValue, (int)host.ReadHeldByte(),
                "the second read still goes through the consumer's pointer");
            AssertEqual((long)probe, (long)host.ReadHeldAddress(), "and through the same address");
            AssertEqual(3, del.HolderReads, "every read reached the C# implementation");

            GC.KeepAlive(del);
        }
        finally
        {
            Functions.FreeProbeByte(probe);
        }
    }

    /// <summary>
    /// The numeric-only control on the same protocol. It shares the transport class with the
    /// pointer-bearing struct, so a change that broke frozen-struct returns outright would show up
    /// here too — which is what separates "the pointer field is refused" from "frozen structs
    /// stopped working".
    /// </summary>
    public void TestNumericOnlyFrozenStructStillRoundTrips()
    {
        var probe = Functions.AllocateProbeByte(ProbeValue);
        try
        {
            var host = new PointerCarryingHost();
            var del = new PointerCarryingDelegateImpl(probe);
            host.Delegate = del;

            AssertEqual("42/1.5", host.ReadExtentDescription(),
                "both fields of the numeric-only frozen struct arrived intact");

            GC.KeepAlive(del);
        }
        finally
        {
            Functions.FreeProbeByte(probe);
        }
    }

    /// <summary>
    /// Hands back a holder over an address the caller owns, and counts how often Swift asked.
    /// </summary>
    private sealed class PointerCarryingDelegateImpl : IPointerCarryingDelegate
    {
        private readonly IntPtr _probe;

        public PointerCarryingDelegateImpl(IntPtr probe) => _probe = probe;

        public int HolderReads { get; private set; }

        public PointerHolder Holder
        {
            get
            {
                HolderReads++;
                return new PointerHolder(_probe);
            }
        }

        public ExtentHolder Extent => new ExtentHolder(42, 1.5);
    }
}
