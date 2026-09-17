// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Covers the availability DELTA — the floors an inner declaration needs that its enclosing
/// scope does not already guarantee.
///
/// <para>Two emitters read it, and the tests below pin them to the same answer on purpose. A
/// reverse-dispatch witness takes the delta as a declaration-level <c>@available</c>: without it,
/// a requirement introduced later than its protocol names types introduced alongside it at the
/// protocol's older floor, which swiftc rejects outright — and because the conformance extension
/// carries no <c>@_cdecl</c> symbol, that error tiles to module scope and costs the whole binding
/// rather than the one member. A sibling fan-out branch takes the same delta as a runtime
/// <c>#available</c> guard. Both must also go QUIET in the same cases: a redundant guard makes
/// swiftc warn the check is always true and dead-codes the else branch it exists to reach, and a
/// redundant annotation restates a floor the scope already has.</para>
/// </summary>
public class AvailabilityDeltaEmissionTests
{
    private static AvailabilityAnnotation Ann(string platform, string? introduced) =>
        new(platform, introduced, null, null, false, false, null, null);

    private static string Emit(
        IReadOnlyList<AvailabilityAnnotation>? member,
        IReadOnlyList<AvailabilityAnnotation>? enclosing)
    {
        var stringWriter = new StringWriter();
        var writer = new SwiftWriter(stringWriter);
        WrapperEmitterHelpers.EmitSwiftAvailabilityDelta(writer, member, enclosing);
        writer.Flush();
        return stringWriter.ToString();
    }

    // --- The witness case: a requirement newer than the protocol that owns it ---

    [Fact]
    public void EmitSwiftAvailabilityDelta_MemberNewerThanExtension_EmitsMemberFloor()
    {
        var member = new List<AvailabilityAnnotation> { Ann("iOS", "16.0"), Ann("iOS", "17.0") };
        var enclosing = new List<AvailabilityAnnotation> { Ann("iOS", "16.0") };

        Assert.Equal("@available(iOS 17.0, *)" + Environment.NewLine, Emit(member, enclosing));
    }

    [Fact]
    public void EmitSwiftAvailabilityDelta_ExtensionAlreadyCoversMember_EmitsNothing()
    {
        var member = new List<AvailabilityAnnotation> { Ann("iOS", "16.0") };
        var enclosing = new List<AvailabilityAnnotation> { Ann("iOS", "16.0") };

        Assert.Equal(string.Empty, Emit(member, enclosing));
    }

    [Fact]
    public void EmitSwiftAvailabilityDelta_UngatedMember_EmitsNothing()
    {
        // The overwhelming majority of witnesses. Emitting the extension's own floor back onto
        // every one of them would restate it thousands of times for no behavioural gain.
        var enclosing = new List<AvailabilityAnnotation> { Ann("iOS", "16.0") };

        Assert.Equal(string.Empty, Emit(null, enclosing));
    }

    [Fact]
    public void EmitSwiftAvailabilityDelta_PlatformAbsentFromExtension_IsEmitted()
    {
        // The protocol names iOS only; the requirement also names visionOS. The extension
        // guarantees nothing on visionOS, so the member has to carry that floor itself.
        var member = new List<AvailabilityAnnotation> { Ann("iOS", "16.0"), Ann("visionOS", "1.0") };
        var enclosing = new List<AvailabilityAnnotation> { Ann("iOS", "16.0") };

        Assert.Equal("@available(visionOS 1.0, *)" + Environment.NewLine, Emit(member, enclosing));
    }

    [Fact]
    public void EmitSwiftAvailabilityDelta_MixedPlatforms_EmitsOnlyTheUncoveredOnes()
    {
        var member = new List<AvailabilityAnnotation>
        {
            Ann("iOS", "17.0"),     // newer than the extension — needed
            Ann("macOS", "13.0"),   // same as the extension — redundant
        };
        var enclosing = new List<AvailabilityAnnotation> { Ann("iOS", "16.0"), Ann("macOS", "13.0") };

        Assert.Equal("@available(iOS 17.0, *)" + Environment.NewLine, Emit(member, enclosing));
    }

    [Fact]
    public void EmitSwiftAvailabilityDelta_MemberOlderThanExtension_EmitsNothing()
    {
        // Swift rejects a member declared MORE available than the extension enclosing it, and
        // the floor is already guaranteed anyway. Never emit a lower floor.
        var member = new List<AvailabilityAnnotation> { Ann("iOS", "14.0") };
        var enclosing = new List<AvailabilityAnnotation> { Ann("iOS", "16.0") };

        Assert.Equal(string.Empty, Emit(member, enclosing));
    }

    [Fact]
    public void EmitSwiftAvailabilityDelta_VersionsCompareNumerically_NotLexicographically()
    {
        // "9.0" is older than "10.0" despite sorting after it as text.
        var member = new List<AvailabilityAnnotation> { Ann("iOS", "10.0") };
        var enclosing = new List<AvailabilityAnnotation> { Ann("iOS", "9.0") };

        Assert.Equal("@available(iOS 10.0, *)" + Environment.NewLine, Emit(member, enclosing));
    }

    // --- The two readers agree ---

    [Fact]
    public void BranchGuardAndDeclarationDelta_AgreeOnWhichFloorsAreNeeded()
    {
        var inner = new List<AvailabilityAnnotation> { Ann("iOS", "17.0"), Ann("macOS", "13.0") };
        var enclosing = new List<AvailabilityAnnotation> { Ann("iOS", "16.0"), Ann("macOS", "13.0") };

        var guard = WrapperEmitterHelpers.BuildBranchAvailabilityGuard(inner, enclosing);
        var declaration = Emit(inner, enclosing);

        Assert.Equal("#available(iOS 17.0, *)", guard);
        Assert.Equal("@available(iOS 17.0, *)" + Environment.NewLine, declaration);
    }

    [Fact]
    public void BranchGuardAndDeclarationDelta_BothGoQuiet_WhenEnclosingScopeAlreadyCovers()
    {
        var inner = new List<AvailabilityAnnotation> { Ann("iOS", "16.0") };
        var enclosing = new List<AvailabilityAnnotation> { Ann("iOS", "16.0") };

        Assert.Equal(string.Empty, WrapperEmitterHelpers.BuildBranchAvailabilityGuard(inner, enclosing));
        Assert.Equal(string.Empty, Emit(inner, enclosing));
    }

    [Fact]
    public void EmitSwiftAvailabilityDelta_NoEnclosingAvailability_EmitsEveryMemberFloor()
    {
        var member = new List<AvailabilityAnnotation> { Ann("iOS", "17.0") };

        Assert.Equal("@available(iOS 17.0, *)" + Environment.NewLine, Emit(member, null));
    }
}
