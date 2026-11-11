// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable
using System;
using System.IO;
using Xunit;

namespace BindingsGeneration.Tests;

public sealed class AppBuildReceiptTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "app-build-receipt-" + Guid.NewGuid().ToString("N"));
    string App => Path.Combine(root, "RuntimeTestsApp.app");

    public AppBuildReceiptTests()
    {
        Directory.CreateDirectory(Path.Combine(App, "Frameworks", "Runtime.framework"));
        File.WriteAllText(Path.Combine(App, "RuntimeTestsApp"), "executable");
        File.WriteAllText(Path.Combine(App, "Frameworks", "Runtime.framework", "Runtime"), "native");
    }

    public void Dispose() => Directory.Delete(root, recursive: true);

    AppBuildReceipt Receipt(string lane = "net10.0/Debug", string toolchain = "tc", string source = "src") =>
        new(lane, toolchain, source, AppBuildReceipt.BundleDigest(App), DateTimeOffset.UtcNow);

    [Fact]
    public void ReceiptRoundTripsBesideTheBundle()
    {
        var receipt = Receipt();
        receipt.Save(App);
        Assert.True(File.Exists(App + AppBuildReceipt.FileSuffix));
        Assert.Equal(receipt, AppBuildReceipt.Load(App));
        Assert.Equal(receipt, AppBuildReceipt.Load(App + "/"));
    }

    [Fact]
    public void MissingOrUnreadableReceiptRefusesReuse()
    {
        Assert.Null(AppBuildReceipt.Load(App));
        Assert.NotNull(AppBuildReceipt.Refusal(null, "net10.0/Debug", "tc"));
        File.WriteAllText(App + AppBuildReceipt.FileSuffix, "net11.0/coreclr/Release/abc");
        Assert.Null(AppBuildReceipt.Load(App));
        File.WriteAllText(App + AppBuildReceipt.FileSuffix, "{}");
        Assert.Null(AppBuildReceipt.Load(App));
    }

    [Theory]
    [InlineData("net11.0/coreclr/Release", "tc", true)]
    [InlineData("net10.0/Debug+CRYPTOKIT_SMOKE", "tc", true)]
    [InlineData("net10.0/Debug", "other-toolchain", true)]
    [InlineData("net10.0/Debug", "tc", false)]
    public void AnotherLaneOrToolchainRefusesReuse(string lane, string toolchain, bool refused)
        => Assert.Equal(refused, AppBuildReceipt.Refusal(Receipt(), lane, toolchain) != null);

    [Fact]
    public void UnchangedBuildIsCurrent()
        => Assert.Null(Receipt().StaleReason("src", () => AppBuildReceipt.BundleDigest(App)));

    [Fact]
    public void ChangedSourceIsStaleWithoutHashingTheBundle()
        => Assert.Contains("source", Receipt().StaleReason("edited", () => throw new InvalidOperationException("hashed"))!);

    public static TheoryData<string> BundleChanges => new()
    {
        "content", "added file", "removed file", "renamed file", "added directory", "symlink target",
    };

    [Theory]
    [MemberData(nameof(BundleChanges))]
    public void AnyBundleChangeIsStale(string change)
    {
        var link = Path.Combine(App, "Frameworks", "Current");
        File.CreateSymbolicLink(link, "Runtime.framework");
        var receipt = Receipt();
        var executable = Path.Combine(App, "RuntimeTestsApp");
        switch (change)
        {
            case "content": File.WriteAllText(executable, "rebuilt"); break;
            case "added file": File.WriteAllText(Path.Combine(App, "Extra.dll"), ""); break;
            case "removed file": File.Delete(executable); break;
            case "renamed file": File.Move(executable, executable + "2"); break;
            case "added directory": Directory.CreateDirectory(Path.Combine(App, "PlugIns")); break;
            case "symlink target": File.Delete(link); File.CreateSymbolicLink(link, "Other.framework"); break;
        }
        Assert.Contains("bundle", receipt.StaleReason("src", () => AppBuildReceipt.BundleDigest(App))!);
    }

    [Fact]
    public void DigestIgnoresTimestampsAndNeverFollowsLinks()
    {
        File.CreateSymbolicLink(Path.Combine(App, "Loop"), App);
        var before = AppBuildReceipt.BundleDigest(App);
        File.SetLastWriteTimeUtc(Path.Combine(App, "RuntimeTestsApp"), DateTime.UtcNow.AddDays(-1));
        Assert.Equal(before, AppBuildReceipt.BundleDigest(App));
    }

    [Fact]
    public void MissingBundleCannotBeDigested()
        => Assert.Throws<DirectoryNotFoundException>(() => AppBuildReceipt.BundleDigest(Path.Combine(root, "None.app")));

    [Fact]
    public void AppIdentitySeparatesEveryLaneSharingABundlePath()
    {
        var net10 = RuntimeTestLane.Create(null, null, null, false, false, false);
        Assert.Equal("net10.0/Debug", net10.AppIdentity(device: false, monoAot: false));
        Assert.Equal("net10.0/Release", net10.AppIdentity(device: true, monoAot: false));
        Assert.Equal("net10.0/Debug", net10.AppIdentity(device: true, monoAot: true));
        var release = RuntimeTestLane.Create("net11.0", null, "Release", false, false, false);
        var debug = RuntimeTestLane.Create("net11.0", null, "Debug", false, false, false);
        Assert.Equal("net11.0/coreclr/Release", release.AppIdentity(false, false));
        Assert.NotEqual(release.AppIdentity(false, false), debug.AppIdentity(false, false));
        Assert.Equal("net11.0/nativeaot/Release", release.AppIdentity(true, false));
        Assert.Equal("net11.0/coreclr/Release",
            RuntimeTestLane.Create("net11.0", null, "Release", true, true, false).AppIdentity(true, false));
    }
}
