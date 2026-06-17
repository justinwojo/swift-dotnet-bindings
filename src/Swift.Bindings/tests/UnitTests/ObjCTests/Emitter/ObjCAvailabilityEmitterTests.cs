// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Text;
using BindingsGeneration.ObjC;
using Xunit;

namespace BindingsGeneration.Tests.ObjCTests;

/// <summary>
/// Unit tests for <see cref="ObjCAvailabilityEmitter"/> (Finding 22, recovery option a2): turning
/// recovered <see cref="ObjCAvailability"/> records into fully-qualified .NET platform-availability
/// attributes that mirror the Swift <c>@available</c> path's emission shape.
/// </summary>
public class ObjCAvailabilityEmitterTests
{
    private static string Emit(params ObjCAvailability[] availability)
    {
        var sb = new StringBuilder();
        ObjCAvailabilityEmitter.EmitAvailabilityAttributes(sb, availability, "    ");
        return sb.ToString();
    }

    [Fact]
    public void Introduced_EmitsSupportedOSPlatform()
    {
        var output = Emit(new ObjCAvailability { Platform = "ios", IntroducedVersion = "15.0" });
        Assert.Contains("[global::System.Runtime.Versioning.SupportedOSPlatform(\"ios15.0\")]", output);
    }

    [Fact]
    public void Introduced_MajorOnly_NormalizedToMajorMinor()
    {
        // Matches the Swift path: "13" → "13.0" so the two emitters produce identical strings.
        var output = Emit(new ObjCAvailability { Platform = "ios", IntroducedVersion = "13" });
        Assert.Contains("SupportedOSPlatform(\"ios13.0\")", output);
    }

    [Fact]
    public void Deprecated_EmitsObsoletedOSPlatform_WithMessage()
    {
        var output = Emit(new ObjCAvailability
        {
            Platform = "ios",
            IntroducedVersion = "13.0",
            DeprecatedVersion = "15.0",
            Message = "use newThing"
        });

        Assert.Contains("SupportedOSPlatform(\"ios13.0\")", output);
        Assert.Contains("[global::System.Runtime.Versioning.ObsoletedOSPlatform(\"ios15.0\", \"use newThing\")]", output);
    }

    [Fact]
    public void Obsoleted_EmitsObsoletedOSPlatform()
    {
        var output = Emit(new ObjCAvailability { Platform = "macos", ObsoletedVersion = "12.0" });
        Assert.Contains("ObsoletedOSPlatform(\"macos12.0\")", output);
    }

    [Fact]
    public void Unavailable_EmitsUnsupportedOSPlatform_AndDoesNotIntroduce()
    {
        var output = Emit(new ObjCAvailability { Platform = "tvos", IsUnavailable = true });
        Assert.Contains("[global::System.Runtime.Versioning.UnsupportedOSPlatform(\"tvos\")]", output);
        Assert.DoesNotContain("SupportedOSPlatform", output);
    }

    [Fact]
    public void EmptyList_EmitsNothing()
    {
        Assert.Equal("", Emit());
    }

    [Fact]
    public void Message_WithQuotesAndBackslashes_IsEscaped()
    {
        var output = Emit(new ObjCAvailability
        {
            Platform = "ios",
            DeprecatedVersion = "15.0",
            Message = "say \"hi\" \\ bye"
        });
        // The generated C# string literal must be valid: embedded quotes/backslashes escaped.
        Assert.Contains("ObsoletedOSPlatform(\"ios15.0\", \"say \\\"hi\\\" \\\\ bye\")", output);
    }

    [Fact]
    public void DuplicateAttributes_AreDeduped()
    {
        // A decl can carry both API_AVAILABLE and API_DEPRECATED for the same platform/version.
        var output = Emit(
            new ObjCAvailability { Platform = "ios", IntroducedVersion = "13.0" },
            new ObjCAvailability { Platform = "ios", IntroducedVersion = "13.0" });

        var idx = output.IndexOf("SupportedOSPlatform(\"ios13.0\")", StringComparison.Ordinal);
        Assert.True(idx >= 0);
        Assert.Equal(-1, output.IndexOf("SupportedOSPlatform(\"ios13.0\")", idx + 1, StringComparison.Ordinal));
    }

    [Fact]
    public void Lines_CarryRequestedIndent()
    {
        var output = Emit(new ObjCAvailability { Platform = "ios", IntroducedVersion = "15.0" });
        Assert.StartsWith("    [global::System.Runtime.Versioning.SupportedOSPlatform", output);
    }

    [Fact]
    public void MacCatalystFloor_IsLiftedToHigherIosFloor()
    {
        // Parity with the Swift path's AvailabilityHelpers.LiftMacCatalystFloorToIOS: a decl carrying
        // BOTH an explicit maccatalyst floor (13.0) and a HIGHER ios floor (16.0, >= 13.0) must emit
        // the maccatalyst floor lifted to the ios floor. swiftc maps iOS>=13 floors onto macCatalyst
        // 1:1, so emitting the literal maccatalyst13.0 would let a Catalyst consumer between 13.0 and
        // 16.0 slip past CA1416 and hit a missing symbol at runtime.
        var output = Emit(
            new ObjCAvailability { Platform = "ios", IntroducedVersion = "16.0" },
            new ObjCAvailability { Platform = "maccatalyst", IntroducedVersion = "13.0" });

        Assert.Contains("SupportedOSPlatform(\"ios16.0\")", output);
        Assert.Contains("SupportedOSPlatform(\"maccatalyst16.0\")", output);
        Assert.DoesNotContain("maccatalyst13.0", output);
    }

    [Fact]
    public void MacCatalystFloor_NotLiftedWhenAlreadyAtOrAboveIosFloor()
    {
        // The common Apple shape: maccatalyst floor >= ios floor (e.g. ios(13.0), macCatalyst(13.1)).
        // The lift only RAISES, never lowers — maccatalyst13.1 stays as-is.
        var output = Emit(
            new ObjCAvailability { Platform = "ios", IntroducedVersion = "13.0" },
            new ObjCAvailability { Platform = "maccatalyst", IntroducedVersion = "13.1" });

        Assert.Contains("SupportedOSPlatform(\"ios13.0\")", output);
        Assert.Contains("SupportedOSPlatform(\"maccatalyst13.1\")", output);
    }

    [Fact]
    public void MacCatalystFloor_NotInventedFromIosOnlyAnnotation()
    {
        // Gated on an EXPLICIT maccatalyst entry (mirrors the Swift gate): an ios-only annotation must
        // NOT synthesize a maccatalyst attribute — .NET's ios→maccatalyst child-platform inheritance
        // already narrows Catalyst consumers to the ios floor.
        var output = Emit(new ObjCAvailability { Platform = "ios", IntroducedVersion = "16.0" });

        Assert.Contains("SupportedOSPlatform(\"ios16.0\")", output);
        Assert.DoesNotContain("maccatalyst", output);
    }

    [Fact]
    public void MacCatalystLift_ClearsVacuousDeprecationBelowLiftedFloor()
    {
        // When the lifted introduced floor (16.0) sits above this annotation's own deprecated version
        // (14.0), the deprecation is vacuous (the API never existed there) and must be cleared, else a
        // backwards [ObsoletedOSPlatform("maccatalyst14.0")] below [SupportedOSPlatform("maccatalyst16.0")]
        // would be emitted.
        var output = Emit(
            new ObjCAvailability { Platform = "ios", IntroducedVersion = "16.0" },
            new ObjCAvailability { Platform = "maccatalyst", IntroducedVersion = "13.0", DeprecatedVersion = "14.0" });

        Assert.Contains("SupportedOSPlatform(\"maccatalyst16.0\")", output);
        Assert.DoesNotContain("ObsoletedOSPlatform(\"maccatalyst14.0\")", output);
    }
}
