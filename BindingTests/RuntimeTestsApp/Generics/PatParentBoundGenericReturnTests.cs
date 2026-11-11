// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// <c>MacAuthenticator&lt;Algorithm: MacAlgorithm&gt;</c> is generic over a protocol with an
/// associated type, so its members exist only as per-conformer specializations. Two of them
/// return <c>MacTag&lt;Algorithm&gt;</c>, a type closed over the parent's own parameter: the
/// instance <c>finalize()</c>, and the static <c>tag(for:key:)</c>, whose own generic parameter
/// sits at the same index one depth down. Both were dropped. These call each one through both
/// conformers and check the tag against the arithmetic the Swift side performs.
/// </summary>
public class PatParentBoundGenericReturnTests : TestBase
{
    public PatParentBoundGenericReturnTests(TestResults results) : base(results) { }

    // The authenticator starts at seed * 1000 + key and folds each absorbed value in as
    // state * 31 + value. MacSum's seed is 11, MacXor's is 5.
    private static int Expected(int seed, int key, params int[] absorbed)
    {
        int state = seed * 1000 + key;
        foreach (var value in absorbed)
            state = unchecked(state * 31 + value);
        return state;
    }

    public void TestFinalize_ReturnsTagClosedOverTheParentParameter()
    {
        using var authenticator = Functions.MakeMacSumAuthenticator(7);
        authenticator.Absorb(3);
        using var tag = authenticator.Finalize();
        AssertEqual(Expected(11, 7, 3), tag.Value, "MacAuthenticator<MacSum>.finalize() tag value");
    }

    public void TestFinalize_SecondConformerSpecializesIndependently()
    {
        using var authenticator = Functions.MakeMacXorAuthenticator(40);
        authenticator.Absorb(1);
        authenticator.Absorb(2);
        using var tag = authenticator.Finalize();
        AssertEqual(Expected(5, 40, 1, 2), tag.Value, "MacAuthenticator<MacXor>.finalize() tag value");
    }

    public void TestStaticTag_OwnParameterStaysDistinctFromTheParents()
    {
        using var message = new MacText("ab");
        using var sumTag = MacAuthenticatorSwiftBindingsTestLib_MacSumCsmExtensions.Tag(message, 2);
        AssertEqual(Expected(11, 2, 'a', 'b'), sumTag.Value, "MacAuthenticator<MacSum>.tag(for:key:)");
        using var xorTag = MacAuthenticatorSwiftBindingsTestLib_MacXorCsmExtensions.Tag(message, 2);
        AssertEqual(Expected(5, 2, 'a', 'b'), xorTag.Value, "MacAuthenticator<MacXor>.tag(for:key:)");
    }
}
