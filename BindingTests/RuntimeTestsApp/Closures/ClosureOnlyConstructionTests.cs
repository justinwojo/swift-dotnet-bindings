// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Closures;

/// <summary>
/// Runtime coverage for a type whose ONLY public initializer takes a closure. If that one
/// initializer refuses, every other member of the type is dead surface: emitted, fully typed,
/// and unreachable because no consumer can build a receiver. The block hands its parameter back
/// mutated rather than returning a fresh value, so the initializer rides the same write-back
/// lowering the builder hooks use — reached through a constructor instead of a method. These
/// tests drive the block's two outcomes and the members the initializer unlocks.
/// </summary>
/// <remarks>
/// The protocol arm is the second, independent half of the reported red and is NOT covered here:
/// <c>ResourceLoading</c>'s single requirement carries a closure parameter, which the
/// reverse-dispatch vtable has no slot shape for, so the interface emits with an SB0010 marker
/// and a C# conformer is never called back. Only the forward direction — handing a Swift-vended
/// loader to a Swift function that takes the existential — works, and that is what
/// <see cref="TestExistentialForwardDirectionReachesTheSameInstanceMethod"/> pins.
/// </remarks>
public class ClosureOnlyConstructionTests : TestBase
{
    public ClosureOnlyConstructionTests(TestResults results) : base(results) { }

    /// <summary>The initializer binds at all — the gate every other test here depends on.</summary>
    public void TestClosureOnlyInitializerConstructsWithManagedBlock()
    {
        using var loader = new ValidatingResourceLoader(0, slot => { slot.Value = 7; return true; });
        AssertNotNull(loader, "the closure-only initializer should produce an instance");
    }

    /// <summary>The block's accept arm returns the value it left in the slot, not the seed.</summary>
    public void TestValidationBlockAcceptArmPublishesItsMutation()
    {
        using var loader = new ValidatingResourceLoader(0, slot =>
        {
            slot.Value += 1;
            return true;
        });

        // 41 seeded, block leaves 42, accepted -> returned as-is.
        AssertEqual(42, loader.ValidateStatus(41), "accepted status should carry the block's mutation");
    }

    /// <summary>The reject arm negates, proving the verdict and the mutation are independent.</summary>
    public void TestValidationBlockRejectArmPublishesBothMutationAndVerdict()
    {
        using var loader = new ValidatingResourceLoader(0, slot =>
        {
            slot.Value = 400;
            return false;
        });

        AssertEqual(-400, loader.ValidateStatus(503), "rejected status should be the mutated value, negated");
    }

    /// <summary>A block that assigns nothing leaves Swift's seeded value intact.</summary>
    public void TestValidationBlockWithoutAssignmentPreservesTheSeededStatus()
    {
        using var loader = new ValidatingResourceLoader(0, _ => true);

        AssertEqual(77, loader.ValidateStatus(77), "an unassigned slot should keep the value Swift seeded");
    }

    /// <summary>The block decides per call, so the slot is not latched across invocations.</summary>
    public void TestValidationBlockDecidesPerCall()
    {
        using var loader = new ValidatingResourceLoader(0, slot => slot.Value < 400);

        AssertEqual(200, loader.ValidateStatus(200), "under the threshold should be accepted");
        AssertEqual(-500, loader.ValidateStatus(500), "over the threshold should be rejected");
        AssertEqual(399, loader.ValidateStatus(399), "the accept arm should still work after a reject");
    }

    /// <summary>
    /// The fully typed instance method — the member that was emitted-but-unreachable — now runs on
    /// a constructed receiver, and its own <c>onStatus</c> callback fires with the pre-validation
    /// status.
    /// </summary>
    public void TestFullyTypedInstanceMethodIsReachableOnAConstructedReceiver()
    {
        var observed = -1;
        using var loader = new ValidatingResourceLoader(10, slot => { slot.Value += 100; return true; });

        // "abcd" is 4 chars + configuration 10 = 14 seeded, block adds 100, accepted.
        AssertEqual(114, loader.Load("abcd", status => observed = status),
            "Load should return the validated status");
        AssertEqual(14, observed, "onStatus should fire with the pre-validation status");
    }

    /// <summary>The same method's reject arm, so <c>Load</c> is pinned on both branches.</summary>
    public void TestFullyTypedInstanceMethodRejectArm()
    {
        using var loader = new ValidatingResourceLoader(0, slot => { slot.Value = 500; return false; });

        AssertEqual(-500, loader.Load("abcd", _ => { }), "a rejected load should return the negated status");
    }

    /// <summary>
    /// The initializer's default arguments each bind as their own overload, so a consumer can
    /// construct without supplying the block at all — the Swift default routes to a Swift-side
    /// validation block the managed side never sees.
    /// </summary>
    public void TestDefaultArgumentOverloadsConstructWithoutABlock()
    {
        using var bare = new ValidatingResourceLoader();
        AssertEqual(200, bare.ValidateStatus(200), "the Swift default block should accept an in-range status");
        AssertEqual(-400, bare.ValidateStatus(404), "the Swift default block should clamp and reject");

        using var configured = new ValidatingResourceLoader(5);
        AssertEqual(9, configured.Load("abcd", _ => { }), "configuration should still be applied");
    }

    /// <summary>The default block is also directly callable, through a method-level <c>ref</c>.</summary>
    public void TestDefaultValidateIsCallableDirectly()
    {
        var status = 404;
        AssertFalse(ValidatingResourceLoader.DefaultValidate(ref status), "404 should be rejected");
        AssertEqual(400, status, "the rejected status should have been clamped in place");

        var ok = 200;
        AssertTrue(ValidatingResourceLoader.DefaultValidate(ref ok), "200 should be accepted");
        AssertEqual(200, ok, "an accepted status should be left alone");
    }

    /// <summary>
    /// The control: a sibling whose initializer block takes its parameter by value. It bound before
    /// this change too, which is what isolates the write-back shape — rather than "a closure in an
    /// initializer" — as what had stranded the type.
    /// </summary>
    public void TestByValueInitializerBlockControlStillBinds()
    {
        using var counting = new CountingResourceLoader(v => v * 3);
        AssertEqual(21, counting.Weight(7), "the by-value control should round-trip");
    }

    /// <summary>
    /// Forward direction through the existential: a Swift function taking <c>any ResourceLoading</c>
    /// reaches the same instance method on a constructed loader. The reverse direction (a C#
    /// conformer called back from Swift) is the recorded gap — see the remarks on this class.
    /// </summary>
    public void TestExistentialForwardDirectionReachesTheSameInstanceMethod()
    {
        using var loader = new ValidatingResourceLoader(0, _ => true);

        AssertEqual(4, TestLibFunctions.RunResourceLoader(loader, "abcd"),
            "the existential route should reach Load on a constructed receiver");
    }
}
