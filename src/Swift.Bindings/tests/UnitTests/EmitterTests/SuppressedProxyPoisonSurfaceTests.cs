// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.IO;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Locks the compile-time-visible surface emitted in front of a suppressed-proxy read/return
/// (getter, indexer getter, method return, async producer). The policy: a member whose Swift
/// protocol proxy could not be generated must fail at COMPILE TIME when read — never a silent
/// runtime trap — via <c>[Obsolete(msg, error: true, DiagnosticId = "SB0006")]</c>. These tests
/// assert the marker shape and its constants directly, so a future refactor cannot quietly drop
/// <c>error: true</c> (which would demote the build error back to a suppressible warning) or
/// change the diagnostic id the wiki/troubleshooting docs point consumers to.
/// </summary>
public class SuppressedProxyPoisonSurfaceTests
{
    private static string EmitPoison()
    {
        var output = new StringWriter();
        var csWriter = new CSharpWriter(output);
        WrapperEmitter.EmitSuppressedProxyReadPoison(csWriter);
        return output.ToString();
    }

    [Fact]
    public void EmitSuppressedProxyReadPoison_EmitsObsoleteErrorMarker()
    {
        var poison = EmitPoison();

        Assert.Contains("[Obsolete(", poison);
        // error: true is the load-bearing positional arg — it turns a consumer read into a
        // BUILD error, not a suppressible warning. Assert it is present as its own argument
        // (", true," — not ", true)]" which the ConsumerSafetyAttribute path deliberately avoids).
        Assert.Contains(", true, DiagnosticId =", poison);
    }

    [Fact]
    public void EmitSuppressedProxyReadPoison_CarriesSb0006DiagnosticId()
    {
        var poison = EmitPoison();

        Assert.Contains("DiagnosticId = \"SB0006\"", poison);
        Assert.Equal("SB0006", WrapperEmitter.ProxySuppressedDiagnosticId);
    }

    [Fact]
    public void EmitSuppressedProxyReadPoison_LinksTroubleshootingUrl()
    {
        var poison = EmitPoison();

        Assert.Contains("UrlFormat = \"https://github.com/justinwojo/swift-dotnet-bindings/wiki/Troubleshooting\"", poison);
    }

    [Fact]
    public void EmitSuppressedProxyReadPoison_MessageMentionsSetterStaysUsable()
    {
        var poison = EmitPoison();

        // The consumer-facing text steers a caller to the assign-only surface: the read is gone,
        // but a setter (where the member has one) still works. This is the whole point of the
        // produce-throw / consume-degrade split, so the guidance must survive.
        Assert.Contains("setter", WrapperEmitter.ProxySuppressedObsoleteMessage);
        Assert.Contains(WrapperEmitter.ProxySuppressedObsoleteMessage.Substring(0, 20), poison);
    }

    [Fact]
    public void EmitSuppressedProxyReadPoison_EscapesMessageAsStringLiteral()
    {
        var poison = EmitPoison();

        // The emitted attribute must be a single well-formed C# string literal — no raw newlines
        // that would split the [Obsolete(...)] across lines and fail to compile.
        Assert.DoesNotContain("\n\"", poison.TrimEnd());
        Assert.EndsWith(")]", poison.TrimEnd());
    }
}
