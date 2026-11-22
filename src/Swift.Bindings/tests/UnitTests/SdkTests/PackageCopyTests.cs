// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using SwiftBindings.Tasks;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// The rules of a binding package's per-toolchain copies (<c>lib/&lt;tfm&gt;/</c>,
/// <c>buildTransitive/&lt;tfm&gt;/</c>, <c>native/&lt;tfm&gt;/</c>, keyed by the full TFM): the producer
/// label (SWIFTBIND137), the consumer's copy selection and fallback (SWIFTBIND136, SWIFTBIND139) and
/// the merge of per-toolchain contributions (SWIFTBIND138). Each refusal is a seeded red beside the
/// accepted shape it must not catch.
/// </summary>
public sealed class PackageCopyTests : IDisposable
{
    private const string Id = "Acme.Kit";
    private const string Generator = "0.20.0";

    private readonly string tempDir = Directory.CreateTempSubdirectory("swift-package-copy-").FullName;

    public void Dispose() => Directory.Delete(tempDir, recursive: true);

    // ---- SWIFTBIND137: the producer label ---------------------------------------------------------

    [Theory]
    // A versionless .NET 10 project defaults to 26.0 on every toolchain: on Xcode 27.2 the label claims
    // an SDK the copy was not generated from, and an Xcode 26 app would select it.
    [InlineData("net10.0-ios26.0", "27.2")]
    [InlineData("net10.0-macos26.0", "27.2")]
    [InlineData("net11.0-ios26.0", "27.2")]
    // A label newer than the SDK the copy came from.
    [InlineData("net10.0-ios27.2", "26.2")]
    [InlineData("net10.0-ios26.2", "26.0")]
    public void Label_AnAppleFrameworkCopyLabelledForAnotherSdk_IsRefused(string packTfm, string sdk)
    {
        var refusal = PackageCopyRules.CheckLabel(Id, packTfm, "AppleFramework", sdk);
        Assert.NotNull(refusal);
        Assert.Contains(packTfm, refusal);
        Assert.Contains($"SDK", refusal);
    }

    [Fact]
    public void Label_Refusal_NamesTheTfmThatMatchesTheSdk()
    {
        var refusal = PackageCopyRules.CheckLabel(Id, "net10.0-ios26.0", "AppleFramework", "27.2");
        Assert.Contains("net10.0-ios27.2", refusal);
    }

    [Theory]
    [InlineData("net10.0-ios26.0", "AppleFramework", "26.2")]   // the oldest copy, on Xcode 26.3
    [InlineData("net10.0-ios26.2", "AppleFramework", "26.2")]
    [InlineData("net10.0-ios27.2", "AppleFramework", "27.2")]
    [InlineData("net11.0-ios27.0", "AppleFramework", "27.2")]
    [InlineData("net10.0-maccatalyst26.0", "AppleFramework", "26.2")]
    // A library binding's surface is the library's: a versionless library packed on Xcode 27.2 keeps
    // its 26.0 label and serves .NET 10 + Xcode 26 apps (the documented third-party flow).
    [InlineData("net10.0-ios26.0", "SwiftFramework", "27.2")]
    [InlineData("net10.0-ios26.0", "", "27.2")]
    [InlineData("net10.0-ios", "SwiftFramework", "")]
    public void Label_TheSdksOwnLabel_OrALibraryBinding_IsAccepted(string packTfm, string kind, string sdk)
    {
        Assert.Null(PackageCopyRules.CheckLabel(Id, packTfm, kind, sdk));
    }

    [Fact]
    public void Label_AnUnknownSdkVersion_IsRefusedForAnAppleFramework()
    {
        var refusal = PackageCopyRules.CheckLabel(Id, "net10.0-ios26.0", "AppleFramework", "");
        Assert.NotNull(refusal);
        Assert.Contains("could not be determined", refusal);
    }

    // ---- SWIFTBIND138 at pack: the consumer targets reference only their own copy ------------------

    [Fact]
    public void TargetsReferences_OwnCopy_IsAccepted()
    {
        Assert.Empty(PackageCopyRules.CheckTargetsReferences("net10.0-ios26.0", Targets("net10.0-ios26.0")));
    }

    [Theory]
    [InlineData("$(MSBuildThisFileDirectory)../../runtimes/ios-arm64/native/Kit.xcframework", "runtimes/")]
    [InlineData("$(MSBuildThisFileDirectory)../../native/net10.0-ios27.2/Kit.xcframework", "native/net10.0-ios27.2/")]
    [InlineData("$(MSBuildThisFileDirectory)..\\..\\native\\net10.0-ios27.2\\Kit.xcframework", "native/net10.0-ios27.2/")]
    [InlineData("$(MSBuildThisFileDirectory)../../native/net10.0-ios26.0/../net10.0-ios27.2/Kit.xcframework", "leaves the copy's native/net10.0-ios26.0/")]
    public void TargetsReferences_AnotherLayoutOrCopy_IsRefused(string reference, string named)
    {
        var problems = PackageCopyRules.CheckTargetsReferences("net10.0-ios26.0", $"<Project><ItemGroup><NativeReference Include=\"{reference}\" /></ItemGroup></Project>").ToList();
        Assert.Single(problems);
        Assert.Contains(named, problems[0]);
    }

    // ---- SWIFTBIND136 / SWIFTBIND139: the consumer's copy -------------------------------------------

    [Fact]
    public void Selection_TheDeclaredCopysAssembly_IsAnnounced()
    {
        var (copy, reference) = Resolved("net10.0-ios26.0", "net10.0-ios26.0");
        var finding = Assert.Single(PackageCopyCheck.Evaluate([copy], [reference], "net10.0-ios", "26.2", requireExactCopy: false));
        Assert.Equal(PackageCopyFindingKind.Selected, finding.Kind);
        Assert.Contains("SwiftBindings copy: Acme.Kit 1.0.0 -> net10.0-ios26.0 (built on .NET 10 + Xcode 26.3)", finding.Text);
        Assert.Contains("native/net10.0-ios26.0/", finding.Text);
    }

    [Fact]
    public void Selection_AnAssemblyFromAnotherCopy_IsRefused()
    {
        // NuGet chose the 26.0 consumer targets (and so its natives) but the 27.2 assembly: the package
        // holds different TFM sets in lib/ and buildTransitive/.
        var (copy, reference) = Resolved("net10.0-ios26.0", "net10.0-ios27.2");
        var finding = Assert.Single(PackageCopyCheck.Evaluate([copy], [reference], "net10.0-ios", "27.2", requireExactCopy: false));
        Assert.Equal(PackageCopyFindingKind.Mismatch, finding.Kind);
        Assert.Contains("net10.0-ios27.2", finding.Text);
        Assert.Contains("net10.0-ios26.0 consumer targets", finding.Text);
    }

    [Fact]
    public void Selection_ACopyWhoseConsumerTargetsAreMissing_IsRefused()
    {
        var (copy, reference) = Resolved("net10.0-ios26.0", "net10.0-ios26.0");
        File.Delete(copy.TargetsFile);
        var finding = Assert.Single(PackageCopyCheck.Evaluate([copy], [reference], "net10.0-ios", "26.2", requireExactCopy: false));
        Assert.Equal(PackageCopyFindingKind.Mismatch, finding.Kind);
        Assert.Contains("consumer targets", finding.Text);
        Assert.Contains("are missing", finding.Text);
    }

    [Fact]
    public void Selection_AnAssemblyWithNoDeclaredCopy_InACopyLayoutPackage_IsRefused()
    {
        var root = PackageRoot("net10.0-ios26.0", "net10.0-ios27.2");
        var reference = new ResolvedReference(Path.Combine(root, "lib", "net10.0-ios27.2", "Acme.Kit.dll"), Id, "1.0.0");
        var finding = Assert.Single(PackageCopyCheck.Evaluate([], [reference], "net10.0-ios", "27.2", requireExactCopy: false));
        Assert.Equal(PackageCopyFindingKind.Mismatch, finding.Kind);
        Assert.Contains("none of the package's buildTransitive/ copies", finding.Text);
    }

    [Fact]
    public void Selection_AnUndeclaredPackageWithoutCopies_IsNotABindingPackage()
    {
        var root = Path.Combine(tempDir, "plain");
        Directory.CreateDirectory(Path.Combine(root, "lib", "net10.0"));
        var reference = new ResolvedReference(Path.Combine(root, "lib", "net10.0", "Plain.dll"), "Plain", "1.0.0");
        Assert.Empty(PackageCopyCheck.Evaluate([], [reference], "net10.0-ios", "26.2", requireExactCopy: false));
    }

    [Theory]
    [InlineData("net10.0-ios26.0", "net10.0-ios", "27.2", "platform 26.0 SDK")]
    [InlineData("net10.0-ios27.2", "net11.0-ios", "27.2", ".NET 10.0")]
    public void Selection_AnOlderCopy_IsAFallbackMessage_OrAnErrorWhenAnExactCopyIsRequired(string copyTfm, string app, string tpv, string why)
    {
        var (copy, reference) = Resolved(copyTfm, copyTfm);
        var findings = PackageCopyCheck.Evaluate([copy], [reference], app, tpv, requireExactCopy: false);
        var fallback = Assert.Single(findings, f => f.Kind == PackageCopyFindingKind.Fallback);
        Assert.Contains(why, fallback.Text);
        Assert.Contains("SwiftBindingsRequireExactCopy", fallback.Text);

        var exact = PackageCopyCheck.Evaluate([copy], [reference], app, tpv, requireExactCopy: true);
        Assert.Single(exact, f => f.Kind == PackageCopyFindingKind.FallbackRefused);
        Assert.DoesNotContain(exact, f => f.Kind == PackageCopyFindingKind.Fallback);
    }

    [Theory]
    // A 26.0 copy is the .NET 10 + Xcode 26 copy: every 26.x build is its own.
    [InlineData("net10.0-ios26.0", "net10.0-ios", "26.2")]
    [InlineData("net10.0-ios26.0", "net10.0-ios", "26.0")]
    [InlineData("net10.0-ios27.2", "net10.0-ios", "27.2")]
    [InlineData("net11.0-ios27.0", "net11.0-ios", "27.2")]
    [InlineData("net10.0-macos26.0", "net10.0-macos26.2", "")]
    public void Fallback_TheBuildsOwnCopy_IsNone(string copyTfm, string app, string tpv)
    {
        Assert.Null(PackageCopyCheck.Fallback(copyTfm, app, tpv));
    }

    // ---- SWIFTBIND138: the merge ----------------------------------------------------------------

    [Fact]
    public void Merge_PerToolchainContributions_MakeOnePackageWithEveryCopy()
    {
        var older = Contribution("x26", ["net10.0-ios26.0", "net10.0-macos26.0"], api: "public static class Kit { public static int A() => 1; }");
        var newer = Contribution("x27", ["net10.0-ios27.2", "net10.0-macos27.2"], api: "public static class Kit { public static int A() => 1; public static int B() => 2; }");
        var net11 = Contribution("n11", ["net11.0-ios27.0"], api: "public static class Kit { public static int A() => 1; public static int B() => 2; }");
        string[] matrix = ["net10.0-ios26.0", "net10.0-ios27.2", "net10.0-macos26.0", "net10.0-macos27.2", "net11.0-ios27.0"];

        var result = SwiftBindingPackageMerge.Merge([older, newer, net11], matrix, Path.Combine(tempDir, "out"));

        Assert.True(result.Problems.IsEmpty, string.Join("\n", result.Problems));
        Assert.Equal(matrix.Order(StringComparer.Ordinal), result.Copies.Order(StringComparer.Ordinal));
        using var zip = ZipFile.OpenRead(result.Package!);
        var names = zip.Entries.Select(e => e.FullName).ToHashSet();
        foreach (var tfm in matrix)
        {
            Assert.Contains($"lib/{tfm}/Acme.Kit.dll", names);
            Assert.Contains($"buildTransitive/{tfm}/Acme.Kit.targets", names);
            Assert.Contains($"buildTransitive/{tfm}/Acme.Kit.props", names);
            Assert.Contains($"native/{tfm}/Kit.xcframework/Info.plist", names);
        }
        Assert.DoesNotContain(names, n => n.StartsWith("runtimes/", StringComparison.Ordinal));
        var nuspec = XDocument.Load(zip.GetEntry("Acme.Kit.nuspec")!.Open());
        var groups = nuspec.Descendants().Where(e => e.Name.LocalName == "group" && e.Parent?.Name.LocalName == "dependencies")
            .Select(g => g.Attribute("targetFramework")!.Value).Order(StringComparer.Ordinal);
        Assert.Equal(matrix.Order(StringComparer.Ordinal), groups);
    }

    [Fact]
    public void Merge_AContributionWhoseLibAndBuildTransitiveDisagree_IsRefused()
    {
        var bad = Contribution("x27", ["net10.0-ios27.2"], omit: "buildTransitive/net10.0-ios27.2/");
        var result = Merge([Contribution("x26", ["net10.0-ios26.0"]), bad], "net10.0-ios26.0", "net10.0-ios27.2");
        Assert.Contains(result.Problems, p => p.Contains("every copy must appear in all three", StringComparison.Ordinal));
    }

    [Fact]
    public void Merge_AMissingCopy_IsRefused()
    {
        var result = Merge([Contribution("x26", ["net10.0-ios26.0"])], "net10.0-ios26.0", "net10.0-ios27.2");
        Assert.Contains(result.Problems, p => p.Contains("No contribution carries the expected copy net10.0-ios27.2", StringComparison.Ordinal));
        Assert.Null(result.Package);
    }

    [Fact]
    public void Merge_ACopyOutsideTheMatrix_IsRefused()
    {
        var result = Merge([Contribution("x26", ["net10.0-ios26.0"]), Contribution("x27", ["net10.0-ios27.2"])], "net10.0-ios26.0");
        Assert.Contains(result.Problems, p => p.Contains("net10.0-ios27.2, which is not in the expected matrix", StringComparison.Ordinal));
    }

    [Fact]
    public void Merge_ACopyFromTwoContributions_IsRefused()
    {
        var result = Merge([Contribution("a", ["net10.0-ios26.0"]), Contribution("b", ["net10.0-ios26.0"])], "net10.0-ios26.0");
        Assert.Contains(result.Problems, p => p.Contains("is carried by both", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Acme.Other", "1.0.0")]
    [InlineData(Id, "1.0.1")]
    public void Merge_AnotherPackageOrVersion_IsRefused(string id, string version)
    {
        var result = Merge([Contribution("x26", ["net10.0-ios26.0"]), Contribution("x27", ["net10.0-ios27.2"], id: id, version: version)], "net10.0-ios26.0", "net10.0-ios27.2");
        Assert.Contains(result.Problems, p => p.Contains("same package id and version", StringComparison.Ordinal));
    }

    [Fact]
    public void Merge_DifferentNuspecMetadata_IsRefused()
    {
        var result = Merge([Contribution("x26", ["net10.0-ios26.0"]), Contribution("x27", ["net10.0-ios27.2"], description: "another")], "net10.0-ios26.0", "net10.0-ios27.2");
        Assert.Contains(result.Problems, p => p.Contains("differ in their nuspec <description>", StringComparison.Ordinal));
    }

    [Fact]
    public void Merge_CopiesFromDifferentSdks_AreRefused()
    {
        var result = Merge([Contribution("x26", ["net10.0-ios26.0"]), Contribution("x27", ["net10.0-ios27.2"], generator: "0.21.0")], "net10.0-ios26.0", "net10.0-ios27.2");
        Assert.Contains(result.Problems, p => p.Contains("different SwiftBindings SDK versions", StringComparison.Ordinal));
    }

    [Fact]
    public void Merge_ANewerCopyThatLosesPublicApi_IsRefused()
    {
        var older = Contribution("x26", ["net10.0-ios26.0"], api: "public static class Kit { public static int A() => 1; public static int B() => 2; }");
        var newer = Contribution("x27", ["net10.0-ios27.2"], api: "public static class Kit { public static int A() => 1; }");
        var result = Merge([older, newer], "net10.0-ios26.0", "net10.0-ios27.2");
        var problem = Assert.Single(result.Problems);
        Assert.Contains("lacks 1 public API member", problem);
        Assert.Contains("Kit::B", problem);
    }

    [Fact]
    public void Merge_ANewerCopyThatChangesASignature_IsRefused()
    {
        var older = Contribution("x26", ["net10.0-ios26.0"], api: "public static class Kit { public static int A(int x) => x; }");
        var newer = Contribution("x27", ["net10.0-ios27.2"], api: "public static class Kit { public static int A(long x) => 1; }");
        var result = Merge([older, newer], "net10.0-ios26.0", "net10.0-ios27.2");
        Assert.Contains(result.Problems, p => p.Contains("Kit::A`0(Int32)", StringComparison.Ordinal));
    }

    [Fact]
    public void Merge_CopiesOfDifferentPlatforms_AreNotComparedForApi()
    {
        var ios = Contribution("ios", ["net10.0-ios26.0"], api: "public static class Kit { public static int A() => 1; public static int B() => 2; }");
        var mac = Contribution("mac", ["net10.0-macos27.2"], api: "public static class Kit { public static int A() => 1; }");
        Assert.Empty(Merge([ios, mac], "net10.0-ios26.0", "net10.0-macos27.2").Problems);
    }

    [Fact]
    public void Merge_ANewerCopyThatLosesANativeExport_IsRefused_ButTheManifestHashMayDiffer()
    {
        if (!OperatingSystem.IsMacOS())
            return;
        var olderBinary = Dylib("older", "int kit_a(void){return 1;} int kit_b(void){return 2;} int SBTM_Kit_older(void){return 0;}");
        var newerBinary = Dylib("newer", "int kit_a(void){return 1;} int SBTM_Kit_newer(void){return 0;}");
        var fullBinary = Dylib("full", "int kit_a(void){return 1;} int kit_b(void){return 2;} int kit_c(void){return 3;} int SBTM_Kit_full(void){return 0;}");
        const string binary = "Kit.xcframework/ios-arm64/Kit.framework/Kit";

        var refused = Merge([
            Contribution("x26", ["net10.0-ios26.0"], extra: [($"native/net10.0-ios26.0/{binary}", olderBinary)]),
            Contribution("x27", ["net10.0-ios27.2"], extra: [($"native/net10.0-ios27.2/{binary}", newerBinary)]),
        ], "net10.0-ios26.0", "net10.0-ios27.2");
        var problem = Assert.Single(refused.Problems);
        Assert.Contains("lacks 1 export(s)", problem);
        Assert.Contains("kit_b", problem);

        var accepted = Merge([
            Contribution("x26", ["net10.0-ios26.0"], extra: [($"native/net10.0-ios26.0/{binary}", olderBinary)]),
            Contribution("x27", ["net10.0-ios27.2"], extra: [($"native/net10.0-ios27.2/{binary}", fullBinary)]),
        ], "net10.0-ios26.0", "net10.0-ios27.2");
        Assert.Empty(accepted.Problems);
    }

    [Theory]
    [InlineData("runtimes/ios-arm64/native/Kit.xcframework/Info.plist")]
    [InlineData("native/Kit.xcframework")]
    [InlineData("buildTransitive/Acme.Kit.targets")]
    [InlineData("build/net10.0-ios26.0/Acme.Kit.targets")]
    public void Merge_AFileOutsideTheCopyLayout_IsRefused(string file)
    {
        var result = Merge([Contribution("x26", ["net10.0-ios26.0"], extra: [(file, [1])])], "net10.0-ios26.0");
        Assert.Contains(result.Problems, p => p.Contains(file, StringComparison.Ordinal));
    }

    [Fact]
    public void Merge_ConsumerTargetsThatReferenceAnotherCopy_AreRefused()
    {
        var result = Merge([Contribution("x26", ["net10.0-ios26.0"], targetsFor: _ => Targets("net10.0-ios27.2"))], "net10.0-ios26.0");
        Assert.Contains(result.Problems, p => p.Contains("another copy's natives", StringComparison.Ordinal));
    }

    [Fact]
    public void Merge_ADeclarationOfAnotherCopy_IsRefused()
    {
        var result = Merge([Contribution("x26", ["net10.0-ios26.0"], declares: _ => "net10.0-ios27.2")], "net10.0-ios26.0");
        Assert.Contains(result.Problems, p => p.Contains("declares the copy net10.0-ios27.2, not net10.0-ios26.0", StringComparison.Ordinal));
    }

    [Fact]
    public void Merge_ACopyWithNoDeclaration_IsRefused()
    {
        var result = Merge([Contribution("x26", ["net10.0-ios26.0"], omit: "buildTransitive/net10.0-ios26.0/Acme.Kit.props")], "net10.0-ios26.0");
        Assert.Contains(result.Problems, p => p.Contains("carries no copy declaration", StringComparison.Ordinal));
    }

    [Fact]
    public void Merge_ASharedFileThatDiffers_IsRefused_ButTheReadmeIsTheNewestCopys()
    {
        var older = Contribution("x26", ["net10.0-ios26.0"], extra: [("README.md", Encoding.UTF8.GetBytes("old")), ("LICENSE.txt", [1])]);
        var newer = Contribution("x27", ["net10.0-ios27.2"], extra: [("README.md", Encoding.UTF8.GetBytes("new")), ("LICENSE.txt", [1])]);
        var merged = Merge([older, newer], "net10.0-ios26.0", "net10.0-ios27.2");
        Assert.True(merged.Problems.IsEmpty, string.Join("\n", merged.Problems));
        using (var zip = ZipFile.OpenRead(merged.Package!))
        using (var reader = new StreamReader(zip.GetEntry("README.md")!.Open()))
            Assert.Equal("new", reader.ReadToEnd());

        var differs = Merge([
            Contribution("x26", ["net10.0-ios26.0"], extra: [("LICENSE.txt", [1])]),
            Contribution("x27", ["net10.0-ios27.2"], extra: [("LICENSE.txt", [2])]),
        ], "net10.0-ios26.0", "net10.0-ios27.2");
        Assert.Contains(differs.Problems, p => p.Contains("LICENSE.txt differs between contributions", StringComparison.Ordinal));
    }

    [Fact]
    public void Merge_ASignedContribution_IsRefused()
    {
        var result = Merge([Contribution("x26", ["net10.0-ios26.0"], extra: [(".signature.p7s", [1])])], "net10.0-ios26.0");
        Assert.Contains(result.Problems, p => p.Contains("is signed", StringComparison.Ordinal));
    }

    [Fact]
    public void Merge_AContributionWithTwoDependencyGroupsForOneCopy_IsRefused()
    {
        var result = Merge([Contribution("x26", ["net10.0-ios26.0"], duplicateGroup: true)], "net10.0-ios26.0");
        Assert.Contains(result.Problems, p => p.Contains("has 2 dependency group(s) for net10.0-ios26.0", StringComparison.Ordinal));
    }

    [Fact]
    public void Merge_AContributionWithNoDependencies_IsRefused()
    {
        var result = Merge([Contribution("x26", ["net10.0-ios26.0"]), Contribution("x27", ["net10.0-ios27.2"], noDependencies: true)], "net10.0-ios26.0", "net10.0-ios27.2");
        Assert.Contains(result.Problems, p => p.Contains("has no dependency group for net10.0-ios27.2", StringComparison.Ordinal));
    }

    [Fact]
    public void Merge_AMemberThatTurnsFromStaticToInstance_IsRefused()
    {
        var older = Contribution("x26", ["net10.0-ios26.0"], api: "public class Kit { public static int A() => 1; }");
        var newer = Contribution("x27", ["net10.0-ios27.2"], api: "public class Kit { public int A() => 1; }");
        var result = Merge([older, newer], "net10.0-ios26.0", "net10.0-ios27.2");
        Assert.Contains(result.Problems, p => p.Contains("lacks 1 public API member(s)", StringComparison.Ordinal) && p.Contains("static", StringComparison.Ordinal));
    }

    [Fact]
    public void Merge_ANewerBareDylibSliceThatLosesAnExport_IsRefused()
    {
        if (!OperatingSystem.IsMacOS())
            return;
        const string binary = "Kit.xcframework/ios-arm64/libKit.dylib";
        var result = Merge([
            Contribution("x26", ["net10.0-ios26.0"], extra: [($"native/net10.0-ios26.0/{binary}", Dylib("bare-older", "int kit_a(void){return 1;} int kit_b(void){return 2;}"))]),
            Contribution("x27", ["net10.0-ios27.2"], extra: [($"native/net10.0-ios27.2/{binary}", Dylib("bare-newer", "int kit_a(void){return 1;}"))]),
        ], "net10.0-ios26.0", "net10.0-ios27.2");
        Assert.Contains(result.Problems, p => p.Contains("libKit.dylib", StringComparison.Ordinal) && p.Contains("kit_b", StringComparison.Ordinal));
    }

    [Fact]
    public void Merge_IsStagedUntilPublished_AndNeverReplacesAContribution()
    {
        var older = Contribution("x26", ["net10.0-ios26.0"]);
        var output = Path.Combine(tempDir, "staged");
        var result = SwiftBindingPackageMerge.Merge([older, Contribution("x27", ["net10.0-ios27.2"])], ["net10.0-ios26.0", "net10.0-ios27.2"], output);
        Assert.True(result.Problems.IsEmpty, string.Join("\n", result.Problems));
        Assert.NotEqual(result.Destination, result.Package);
        Assert.False(File.Exists(result.Destination), "nothing reaches the destination before the merged package is checked");
        Assert.Equal(result.Destination, SwiftBindingPackageMerge.Publish(result));
        Assert.True(File.Exists(result.Destination));
        Assert.False(File.Exists(result.Package));

        // Merging into the directory of a contribution named like the merged package is refused,
        // and leaves the contribution where it was.
        var host = Path.Combine(tempDir, "host");
        Directory.CreateDirectory(host);
        var own = Path.Combine(host, "Acme.Kit.1.0.0.nupkg");
        File.Copy(older, own);
        var refused = SwiftBindingPackageMerge.Merge([own, Contribution("x27", ["net10.0-ios27.2"])], ["net10.0-ios26.0", "net10.0-ios27.2"], host);
        Assert.Null(refused.Package);
        Assert.Contains(refused.Problems, p => p.Contains("would replace the contribution", StringComparison.Ordinal));
        Assert.True(File.Exists(own));

        // So is one the volume resolves to the same file under another case, or through a symbolic link.
        var cased = SwiftBindingPackageMerge.Merge([own, Contribution("x27", ["net10.0-ios27.2"])], ["net10.0-ios26.0", "net10.0-ios27.2"], host.ToUpperInvariant());
        Assert.Contains(cased.Problems, p => p.Contains("would replace the contribution", StringComparison.Ordinal));
        var alias = Path.Combine(tempDir, "alias");
        Directory.CreateSymbolicLink(alias, host);
        var linked = SwiftBindingPackageMerge.Merge([own, Contribution("x27", ["net10.0-ios27.2"])], ["net10.0-ios26.0", "net10.0-ios27.2"], alias);
        Assert.Contains(linked.Problems, p => p.Contains("would replace the contribution", StringComparison.Ordinal));

        // Each merge stages under a name of its own, so a contribution named like a staged package, or
        // a second merge into the same directory, is never replaced before its checks.
        var staging = Path.Combine(host, "Acme.Kit.1.0.0.merging.nupkg");
        File.Move(own, staging);
        var bytes = File.ReadAllBytes(staging);
        var first = SwiftBindingPackageMerge.Merge([staging, Contribution("x27", ["net10.0-ios27.2"])], ["net10.0-ios26.0", "net10.0-ios27.2"], host);
        var second = SwiftBindingPackageMerge.Merge([staging, Contribution("x27", ["net10.0-ios27.2"])], ["net10.0-ios26.0", "net10.0-ios27.2"], host);
        Assert.True(first.Problems.IsEmpty && second.Problems.IsEmpty, string.Join("\n", first.Problems.Concat(second.Problems)));
        Assert.NotEqual(first.Package, second.Package);
        Assert.Equal(bytes, File.ReadAllBytes(staging));
    }

    [Theory]
    [InlineData("public class Kit { public static int F() => 1; }", "public class Kit { protected static int F() => 1; }", false)]
    [InlineData("public class Kit { protected static int F() => 1; }", "public class Kit { public static int F() => 1; }", true)]
    [InlineData("public class Kit { public int F; }", "public class Kit { protected int F; }", false)]
    public void Merge_ANewerCopyThatNarrowsAccess_IsRefused_AndOneThatWidensIt_IsAccepted(string olderApi, string newerApi, bool accepted)
    {
        var older = Contribution("x26", ["net10.0-ios26.0"], api: olderApi);
        var newer = Contribution("x27", ["net10.0-ios27.2"], api: newerApi);
        var problems = Merge([older, newer], "net10.0-ios26.0", "net10.0-ios27.2").Problems;
        if (accepted)
            Assert.Empty(problems);
        else
            Assert.Contains(problems, p => p.Contains("public ", StringComparison.Ordinal) && p.Contains("Kit::F", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(Id, "../../escape")]
    [InlineData(Id, "1.0.0\\x")]
    public void Merge_AnIdOrVersionThatIsNotAPlainName_IsRefused(string id, string version)
    {
        var result = Merge([Contribution("x26", ["net10.0-ios26.0"], id: id, version: version)], "net10.0-ios26.0");
        Assert.Null(result.Package);
        Assert.Contains(result.Problems, p => p.Contains("is not a plain package name", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("public interface IKit { } public class Kit : IKit { public int A() => 1; }", "public interface IKit { } public class Kit { public int A() => 1; }", "IKit")]
    [InlineData("public class Base { } public class Kit : Base { }", "public class Base { } public class Kit { }", "Base")]
    [InlineData("public interface IKit<T> { } public class Base<T> { } public class Kit<T> : Base<int>, IKit<T> { }",
        "public interface IKit<T> { } public class Base<T> : IKit<T> { } public class Kit<T> : Base<int> { }", "IKit")]
    public void Merge_ANewerTypeThatDropsABaseOrInterface_IsRefused(string olderApi, string newerApi, string key)
    {
        var older = Contribution("x26", ["net10.0-ios26.0"], api: olderApi);
        var newer = Contribution("x27", ["net10.0-ios27.2"], api: newerApi);
        var result = Merge([older, newer], "net10.0-ios26.0", "net10.0-ios27.2");
        Assert.Contains(result.Problems, p => p.Contains("lacks", StringComparison.Ordinal) && p.Contains(" : ", StringComparison.Ordinal) && p.Contains(key, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("public interface IKit { } public class Kit { }", "public interface IKit { } public class Kit : IKit { }")]
    [InlineData("public interface IKit { } public class Base { } public class Kit : Base, IKit { }", "public interface IKit { } public class Base : IKit { } public class Kit : Base { }")]
    [InlineData("public class Base { } public class Kit : Base { }", "public class Base { } public class Mid : Base { } public class Kit : Mid { }")]
    [InlineData("public interface IKit { } public class Base<T> { } public class Kit : Base<int>, IKit { }", "public interface IKit { } public class Base<T> : IKit { } public class Kit : Base<int> { }")]
    [InlineData("public interface IKit<T> { } public class Base<T> { } public class Kit : Base<int>, IKit<int> { }", "public interface IKit<T> { } public class Base<T> : IKit<T> { } public class Kit : Base<int> { }")]
    public void Merge_ANewerTypeThatKeepsEveryConversion_IsAccepted(string olderApi, string newerApi)
    {
        var older = Contribution("x26", ["net10.0-ios26.0"], api: olderApi);
        var newer = Contribution("x27", ["net10.0-ios27.2"], api: newerApi);
        Assert.Empty(Merge([older, newer], "net10.0-ios26.0", "net10.0-ios27.2").Problems);
    }

    [Fact]
    public void Merge_AnEntryPastTheWindowsPathBudget_IsRefused()
    {
        var deep = $"native/net10.0-ios26.0/Kit.xcframework/{new string('a', 200)}/Info.plist";
        var result = Merge([Contribution("x26", ["net10.0-ios26.0"], extra: [(deep, [1])])], "net10.0-ios26.0");
        Assert.Contains(result.Problems, p => p.Contains("Windows restore budget", StringComparison.Ordinal));
    }

    // ---- fixtures -------------------------------------------------------------------------------

    private PackageMergeResult Merge(string[] contributions, params string[] matrix) =>
        SwiftBindingPackageMerge.Merge(contributions, matrix, Path.Combine(tempDir, "out-" + Guid.NewGuid().ToString("N")[..8]));

    private static string Targets(string tfm) =>
        $"<Project><ItemGroup><NativeReference Include=\"$(MSBuildThisFileDirectory)../../native/{tfm}/Kit.xcframework\" /></ItemGroup></Project>";

    /// <summary>A package root extracted as NuGet lays it out, holding <paramref name="tfms"/> copies.</summary>
    private string PackageRoot(params string[] tfms)
    {
        var root = Path.Combine(tempDir, "packages", "acme.kit", "1.0.0");
        foreach (var tfm in tfms)
        {
            Directory.CreateDirectory(Path.Combine(root, "lib", tfm));
            Directory.CreateDirectory(Path.Combine(root, "buildTransitive", tfm));
            File.WriteAllText(Path.Combine(root, "buildTransitive", tfm, "Acme.Kit.targets"), "<Project />");
            Directory.CreateDirectory(Path.Combine(root, "native", tfm));
        }
        return root;
    }

    /// <summary>The declared copy <paramref name="declared"/> and an assembly resolved from <paramref name="resolvedFrom"/>'s lib/.</summary>
    private (PackageCopy Copy, ResolvedReference Reference) Resolved(string declared, string resolvedFrom)
    {
        var root = PackageRoot(declared, resolvedFrom);
        var copy = new PackageCopy(Id, declared, ".NET 10 + Xcode 26.3",
            Path.Combine(root, "lib", declared) + "/", Path.Combine(root, "native", declared) + "/",
            Path.Combine(root, "buildTransitive", declared, "Acme.Kit.targets"));
        return (copy, new ResolvedReference(Path.Combine(root, "lib", resolvedFrom, "Acme.Kit.dll"), Id, "1.0.0"));
    }

    /// <summary>A contribution nupkg in the copy layout carrying <paramref name="tfms"/>.</summary>
    private string Contribution(string name, string[] tfms, string id = Id, string version = "1.0.0", string generator = Generator,
        string api = "public static class Kit { public static int A() => 1; }", string description = "Kit",
        (string Path, byte[] Data)[]? extra = null, string? omit = null, Func<string, string>? targetsFor = null,
        Func<string, string>? declares = null, bool duplicateGroup = false, bool noDependencies = false)
    {
        var assembly = SwiftTransitionManifestTests.Compile("Acme.Kit", api);
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var groups = new StringBuilder();
        foreach (var tfm in tfms)
        {
            files[$"lib/{tfm}/Acme.Kit.dll"] = assembly;
            files[$"buildTransitive/{tfm}/{id}.targets"] = Encoding.UTF8.GetBytes(targetsFor?.Invoke(tfm) ?? Targets(tfm));
            files[$"buildTransitive/{tfm}/{id}.props"] = Encoding.UTF8.GetBytes(
                PackageCopyDeclarationEmitter.Compose(id, declares?.Invoke(tfm) ?? tfm, $"toolchain of {name}", "26.2", generator));
            files[$"native/{tfm}/Kit.xcframework/Info.plist"] = Encoding.UTF8.GetBytes("<plist/>");
            var group = $"<group targetFramework=\"{tfm}\"><dependency id=\"SwiftBindings.Runtime\" version=\"{generator}\" /></group>";
            groups.Append(group);
            if (duplicateGroup)
                groups.Append(group);
        }
        foreach (var (path, data) in extra ?? [])
            files[path] = data;
        if (omit is not null)
            foreach (var key in files.Keys.Where(k => k.StartsWith(omit, StringComparison.Ordinal)).ToList())
                files.Remove(key);
        var readme = files.ContainsKey("README.md") ? "<readme>README.md</readme>" : "";
        files[$"{id}.nuspec"] = Encoding.UTF8.GetBytes(
            $"<?xml version=\"1.0\" encoding=\"utf-8\"?><package xmlns=\"http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd\"><metadata>"
            + $"<id>{id}</id><version>{version}</version><authors>Acme</authors><description>{description}</description>{readme}"
            + (noDependencies ? "" : $"<dependencies>{groups}</dependencies>") + "</metadata></package>");
        files["[Content_Types].xml"] = Encoding.UTF8.GetBytes("<?xml version=\"1.0\" encoding=\"utf-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"dll\" ContentType=\"application/octet\" /></Types>");
        files["_rels/.rels"] = Encoding.UTF8.GetBytes("<Relationships />");

        var path2 = Path.Combine(tempDir, $"{name}-{Guid.NewGuid():N}.nupkg");
        using var zip = ZipFile.Open(path2, ZipArchiveMode.Create);
        foreach (var (entry, data) in files)
        {
            using var stream = zip.CreateEntry(entry).Open();
            stream.Write(data);
        }
        return path2;
    }

    /// <summary>An arm64 dylib exporting the C functions of <paramref name="source"/>.</summary>
    private byte[] Dylib(string name, string source)
    {
        var c = Path.Combine(tempDir, name + ".c");
        var output = Path.Combine(tempDir, name + ".dylib");
        File.WriteAllText(c, source);
        var start = new ProcessStartInfo("xcrun", ["clang", "-arch", "arm64", "-dynamiclib", c, "-o", output])
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
        return File.ReadAllBytes(output);
    }
}
