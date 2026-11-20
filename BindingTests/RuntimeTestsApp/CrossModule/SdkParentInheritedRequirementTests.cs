// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.Versioning;
using Foundation;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.CrossModule;

/// <summary>
/// An empty child of a protocol from an SDK module the run never loads
/// (<c>SdkParentInheritedRequirement.swift</c>): <c>SdkParentAccepting</c>'s one requirement,
/// <c>accept(connection:)</c>, is ExtensionFoundation's <c>AppExtensionConfiguration</c>'s, read
/// from the SDK interface. Swift must dispatch it into a C# conformer, through the child and the
/// parent existential, and hand the conformer the <c>NSXPCConnection</c> it built.
/// </summary>
public class SdkParentInheritedRequirementTests : TestBase
{
    public SdkParentInheritedRequirementTests(TestResults results) : base(results) { }

    // ExtensionFoundation's protocol needs iOS 16, tvOS 16 or macOS 13 (Mac Catalyst follows iOS).
    [SupportedOSPlatformGuard("ios16.0")]
    [SupportedOSPlatformGuard("tvos16.0")]
    [SupportedOSPlatformGuard("macos13.0")]
    [SupportedOSPlatformGuard("maccatalyst16.0")]
    private static bool HasExtensionFoundation =>
        OperatingSystem.IsIOSVersionAtLeast(16) || OperatingSystem.IsTvOSVersionAtLeast(16)
        || OperatingSystem.IsMacOSVersionAtLeast(13) || OperatingSystem.IsMacCatalystVersionAtLeast(16);

    public void TestSdkParentRequirement_DispatchesIntoTheCSharpConformer()
    {
        if (!HasExtensionFoundation)
        {
            TestLogger.Info("AppExtensionConfiguration requires iOS 16 / tvOS 16 / macOS 13; skipping on this OS.");
            return;
        }

        var acceptor = new ConnectionAcceptor { Answer = true };

        AssertTrue(Functions.AcceptFreshConnectionThroughSdkChild(acceptor), "the conformer's answer comes back through the child");
        AssertEqual(1, acceptor.Calls, "the call reached the conformer");
        AssertTrue(acceptor.LastConnection is not null, "the conformer received a connection");
        AssertEqual((ulong)Functions.GetRecordedSdkConnectionAddress(), (ulong)(nint)acceptor.LastConnection!.Handle,
            "the conformer received the connection Swift passed");

        acceptor.Answer = false;
        AssertFalse(Functions.AcceptFreshConnectionThroughSdkParent(acceptor), "the conformer's answer comes back through the parent");
        AssertEqual(2, acceptor.Calls, "the parent existential reached the same conformer");
        AssertEqual((ulong)Functions.GetRecordedSdkConnectionAddress(), (ulong)(nint)acceptor.LastConnection!.Handle,
            "the conformer received the connection Swift passed through the parent");
    }
}

[SupportedOSPlatform("ios16.0")]
[SupportedOSPlatform("tvos16.0")]
[SupportedOSPlatform("macos13.0")]
[SupportedOSPlatform("maccatalyst16.0")]
internal sealed class ConnectionAcceptor : ISdkParentAccepting
{
    public bool Answer { get; set; }
    public int Calls { get; private set; }
    public NSXpcConnection? LastConnection { get; private set; }

    public bool Accept(NSXpcConnection connection)
    {
        Calls++;
        LastConnection = connection;
        return Answer;
    }
}
