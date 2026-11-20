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
/// parent existential, and hand the conformer the <c>NSXPCConnection</c> it built. A same-module
/// protocol (<c>FoundationObjectVending</c>) covers the other directions of a Foundation-class slot:
/// a requirement that returns one and a get/set property of one.
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

        acceptor.Answer = true;
        AssertTrue(Functions.AcceptFreshConnectionGenerically(acceptor), "the conformer's answer comes back through a generic parameter");
        AssertEqual(3, acceptor.Calls, "the generic parameter reached the same conformer");
        AssertEqual((ulong)Functions.GetRecordedSdkConnectionAddress(), (ulong)(nint)acceptor.LastConnection!.Handle,
            "the conformer received the connection Swift passed through the generic parameter");
    }

    // A requirement returning a Foundation class: the object the conformer returns must reach Swift
    // with a reference of its own, so Swift still holds it, exactly once, after C# lets it go.
    public void TestFoundationObjectReturn_CrossesWithItsOwnReference()
    {
        var vendor = new ObjectVendor();
        var address = Functions.KeepVendedObject(vendor);
        AssertTrue(vendor.LastVended is not null, "the call reached the conformer");
        AssertEqual((ulong)address, (ulong)(nint)vendor.LastVended!.Handle, "Swift received the object the conformer returned");

        vendor.LastVended.Dispose();
        vendor.LastVended = null;
        AssertEqual((long)Functions.GetSwiftOwnedObjectRetainCount(), (long)Functions.GetVendedObjectRetainCount(),
            "Swift holds exactly its own reference once C# lets go, as an object only Swift referenced does");
        Functions.ReleaseVendedObject();
    }

    // A get/set property of a Foundation class: the object Swift stores reaches the conformer as the
    // live object, and reading it back hands Swift that object without leaking a reference.
    public void TestFoundationObjectProperty_RoundTripsTheLiveObject()
    {
        var vendor = new ObjectVendor();
        var stored = Functions.StoreFreshObject(vendor);
        AssertEqual((ulong)stored, (ulong)(nint)vendor.Current.Handle, "the conformer received the object Swift stored");

        var before = Functions.GetStoredObjectRetainCount();
        for (var i = 0; i < 3; i++)
            AssertEqual((ulong)stored, (ulong)Functions.ReadCurrentObject(vendor), "Swift reads back the object it stored");
        AssertEqual((long)before, (long)Functions.GetStoredObjectRetainCount(), "reading the property leaks no reference");
        Functions.ReleaseStoredObject();
    }

    // An Optional Foundation class argument: one pointer word on Swift's side, so the conformer
    // receives the object Swift passed, and null for nil.
    public void TestOptionalFoundationObjectArgument_ArrivesAsTheObjectOrNull()
    {
        var vendor = new ObjectVendor();
        var passed = Functions.InspectThroughVendor(vendor, passingNil: false);
        AssertEqual(1, vendor.InspectCalls, "the call reached the conformer");
        AssertTrue(vendor.LastInspected is not null, "a live object arrives as an object");
        AssertEqual((ulong)passed, (ulong)(nint)vendor.LastInspected!.Handle, "the conformer received the object Swift passed");

        AssertEqual(0UL, (ulong)Functions.InspectThroughVendor(vendor, passingNil: true), "Swift passed nil");
        AssertEqual(2, vendor.InspectCalls, "the nil call reached the conformer");
        AssertTrue(vendor.LastInspected is null, "nil arrives as null");
    }
}

internal sealed class ObjectVendor : IFoundationObjectVending
{
    public NSObject? LastVended { get; set; }
    public NSObject Current { get; set; } = new NSObject();

    public NSObject? LastInspected { get; private set; }
    public int InspectCalls { get; private set; }

    public NSObject GetVend()
    {
        LastVended = new NSObject();
        return LastVended;
    }

    public void Inspect(NSObject? @object)
    {
        InspectCalls++;
        LastInspected = @object;
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
