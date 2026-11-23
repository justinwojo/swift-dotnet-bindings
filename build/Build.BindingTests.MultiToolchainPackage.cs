// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Build.BindingTests.MultiToolchainPackage.cs — opt-in gate of one binding package that carries a
// copy per supported .NET + Xcode pairing (src/docs/Design/multi-toolchain-packages.md).
//
// This side packs the throwaway SwiftBindings Runtime, Sdk and Apple feed every pairing restores
// from (one SDK version serves every copy, as in a release), then hands the matrix to
// build/scripts/multi-toolchain-package.py. That driver takes each pairing's environment from the
// toolchain launcher (run-toolchain.py: the profile's DOTNET_ROOT, DEVELOPER_DIR and SDK pin,
// validated as the launcher validates them), so one unprofiled entry point drives all three
// toolchains without nesting profile workspaces. It packs the contributions, merges them, builds and
// runs the consumers, and requires every control to fail with its own code. Receipts land under
// artifacts/multi-toolchain-package/.
//
// Opt-in, heavyweight; never part of the default run or --compile-only.

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tooling;
using Serilog;

partial class Build
{
    [Parameter("Opt-in: pack Apple-framework fixtures on each supported .NET + Xcode pairing, merge them into one package per fixture with SwiftBindingsMergePackages, and build and run consumers of every copy (iOS simulator Debug and Release, NativeAOT ios-arm64 publish, macOS, tvOS simulator, Mac Catalyst, a versionless library through a ProjectReference) with the copy-selection controls required red. Run unprofiled; it drives every profile itself. Never part of the default run or --compile-only.")]
    readonly bool MultiToolchainPackage;

    const string MtpSdkVersion = "0.0.0-mtp";
    const string MtpAppleVersion = "26.2.0-mtp";

    AbsolutePath MtpScratch => RootDirectory / "artifacts" / "multi-toolchain-package";

    /// <summary>
    /// The driver's scratch root: outside the checkout, so its projects inherit neither the
    /// repository's Directory.Build files nor its SDK pin, and keyed on the checkout so two
    /// worktrees never share one.
    /// </summary>
    AbsolutePath MtpWork
    {
        get
        {
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(RootDirectory.ToString())))[..12].ToLowerInvariant();
            return (AbsolutePath)"/private/tmp" / $"swift-bindings-mtp-{Environment.UserName}-{key}";
        }
    }

    void RunMultiToolchainPackageGate()
    {
        Log.Information("=================================================");
        Log.Information(" BindingTests — one package, a copy per toolchain (--multi-toolchain-package)");
        Log.Information("=================================================");
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SWIFT_BINDINGS_TOOLCHAIN_PROFILE")))
            throw new Exception("--multi-toolchain-package drives every toolchain profile itself; run it unprofiled: " +
                "nuke binding-tests --multi-toolchain-package");
        if (RuntimeLane.IsNet11)
            throw new Exception("--multi-toolchain-package packs the SwiftBindings feed on .NET 10; run it without --binding-test-framework net11.0.");

        EnsureGeneratorBuilt();
        if (Directory.Exists(MtpScratch)) MtpScratch.DeleteDirectory();
        var feed = MtpScratch / "packages";
        feed.CreateDirectory();
        var passed = false;
        try
        {
            using (var scope = new VersionScope(MtpSdkVersion, RootDirectory, MtpAppleVersion))
                BuildLocalPackageFeed(feed, scope, "multi-toolchain-package", MtpSdkVersion, MtpAppleVersion, "MtpFixture.ProximityReader");
            var process = ProcessTasks.StartProcess("python3",
                $"-B \"{RootDirectory / "build/scripts/multi-toolchain-package.py"}\" --root \"{RootDirectory}\" " +
                $"--feed \"{feed}\" --sdk-version {MtpSdkVersion} --apple-version {MtpAppleVersion} " +
                $"--work \"{MtpWork}\" --receipts \"{MtpScratch / "receipts"}\"",
                workingDirectory: RootDirectory, timeout: (int)TimeSpan.FromHours(3).TotalMilliseconds);
            process.WaitForExit();
            if (process.ExitCode != 0)
                throw new Exception($"--multi-toolchain-package FAILED; receipts under {MtpScratch / "receipts"} " +
                    $"(summary.txt), scratch kept at {MtpWork}.");
            Log.Information("--multi-toolchain-package PASSED. Receipts: {Dir}", MtpScratch / "receipts");
            passed = true;
        }
        finally
        {
            EvictThrowawayPackages(MtpSdkVersion, MtpAppleVersion, "MtpFixture.ProximityReader");
            if (passed && Directory.Exists(feed)) feed.DeleteDirectory();
        }
    }

    Target MultiToolchainPackageTests => _ => _
        .Description("Test the multi-toolchain package gate's package doctoring and evidence readers without a toolchain.")
        // Ordering only: a standalone sink must join the sinks' total order, or Nuke --strict rejects
        // every plan ("Incomplete target definition order").
        .After(SwiftTransitionGate)
        .Executes(() => ProcessTasks.StartProcess("python3",
            $"-B \"{RootDirectory / "build/scripts/test_multi_toolchain_package.py"}\" -v",
            workingDirectory: RootDirectory).AssertZeroExitCode());
}
