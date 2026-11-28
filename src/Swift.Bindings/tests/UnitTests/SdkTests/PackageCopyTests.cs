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
    // What a scan of the text cannot see: each NativeReference is resolved from buildTransitive/<tfm>/.
    [InlineData("$(MSBuildThisFileDirectory)../../../native/net10.0-ios26.0/Kit.xcframework", "outside the package")]
    [InlineData("$(KitNatives)Kit.xcframework", "not a path from the targets' own directory")]
    [InlineData("$(MSBuildThisFileDirectory)$(KitNatives)Kit.xcframework", "goes through a property or an item")]
    [InlineData("/opt/acme/native/net10.0-ios26.0/Kit.xcframework", "not a path from the targets' own directory")]
    [InlineData("$(MSBuildThisFileDirectory)Kit.xcframework", "resolves to buildTransitive/net10.0-ios26.0/Kit.xcframework")]
    // MSBuild decodes %XX escapes in an item's path, so the rules read the path decoded too.
    [InlineData("$(MSBuildThisFileDirectory)../../native/net10.0-ios26.0/%2E%2E/net10.0-ios27.2/Kit.xcframework", "leaves the copy's native/net10.0-ios26.0/")]
    [InlineData("$(MSBuildThisFileDirectory)../..%2Fnative%2Fnet10.0-ios27.2/Kit.xcframework", "resolves to native/net10.0-ios27.2/Kit.xcframework")]
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
    public void Selection_ANativelessCopyWithNoConsumerTargets_IsAnnounced()
    {
        var root = PackageRoot("net10.0-ios26.0");
        Directory.Delete(Path.Combine(root, "native"), recursive: true);
        File.Delete(Path.Combine(root, "buildTransitive", "net10.0-ios26.0", "Acme.Kit.targets"));
        var copy = new PackageCopy(Id, "net10.0-ios26.0", ".NET 10 + Xcode 26.3", Path.Combine(root, "lib", "net10.0-ios26.0") + "/", "", "", "resources");
        var reference = new ResolvedReference(Path.Combine(root, "lib", "net10.0-ios26.0", "Acme.Kit.dll"), Id, "1.0.0");
        var finding = Assert.Single(PackageCopyCheck.Evaluate([copy], [reference], "net10.0-ios", "26.2", requireExactCopy: false));
        Assert.Equal(PackageCopyFindingKind.Selected, finding.Kind);
        Assert.Contains("native=(resources) targets=(none)", finding.Text);

        // A Swift copy that lost its targets still is refused.
        var swift = new PackageCopy(Id, "net10.0-ios26.0", ".NET 10 + Xcode 26.3", Path.Combine(root, "lib", "net10.0-ios26.0") + "/",
            Path.Combine(root, "native", "net10.0-ios26.0") + "/", Path.Combine(root, "buildTransitive", "net10.0-ios26.0", "Acme.Kit.targets"));
        Assert.Equal(PackageCopyFindingKind.Mismatch, Assert.Single(PackageCopyCheck.Evaluate([swift], [reference], "net10.0-ios", "26.2", requireExactCopy: false)).Kind);
    }

    [Fact]
    public void Selection_AnAssemblyWithNoDeclaredCopy_InANativelessCopyLayoutPackage_IsRefused()
    {
        // A pure Objective-C package has no native/: its copy declarations mark it as laid out per copy.
        var root = PackageRoot("net10.0-ios26.0", "net10.0-ios27.2");
        Directory.Delete(Path.Combine(root, "native"), recursive: true);
        foreach (var tfm in new[] { "net10.0-ios26.0", "net10.0-ios27.2" })
            File.WriteAllText(Path.Combine(root, "buildTransitive", tfm, "Acme.Kit.props"),
                PackageCopyDeclarationEmitter.Compose(Id, tfm, ".NET 10 + Xcode 26.3", "26.2", Generator, "none"));
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
        Assert.Contains(result.Problems, p => p.Contains("every copy must appear in both", StringComparison.Ordinal));
    }

    // ---- SWIFTBIND138: pure Objective-C copies ----------------------------------------------------

    [Fact]
    public void Merge_PureObjCCopiesOfAnAppleFramework_WhichShipNoNatives_Merge()
    {
        // A pure Objective-C binding of an Apple framework binds what the OS provides: each copy is its
        // assembly, its declaration and its consumer targets, with no native/<tfm>/.
        var older = Contribution("x26", ["net10.0-ios26.0"], payload: "none");
        var newer = Contribution("x27", ["net10.0-ios27.2"], payload: "none", api: "public static class Kit { public static int A() => 1; public static int B() => 2; }");
        var net11 = Contribution("n11", ["net11.0-ios27.2"], payload: "none", api: "public static class Kit { public static int A() => 1; public static int B() => 2; }");
        string[] matrix = ["net10.0-ios26.0", "net10.0-ios27.2", "net11.0-ios27.2"];

        var result = SwiftBindingPackageMerge.Merge([older, newer, net11], matrix, Path.Combine(tempDir, "out-objc"));

        Assert.True(result.Problems.IsEmpty, string.Join("\n", result.Problems));
        using var zip = ZipFile.OpenRead(result.Package!);
        var names = zip.Entries.Select(e => e.FullName).ToHashSet();
        foreach (var tfm in matrix)
        {
            Assert.Contains($"lib/{tfm}/Acme.Kit.dll", names);
            Assert.Contains($"buildTransitive/{tfm}/Acme.Kit.props", names);
            using var reader = new StreamReader(zip.GetEntry($"buildTransitive/{tfm}/Acme.Kit.props")!.Open());
            Assert.Contains("<NativePayload>none</NativePayload>", reader.ReadToEnd());
        }
        Assert.DoesNotContain(names, n => n.StartsWith("native/", StringComparison.Ordinal));
        Assert.Contains(result.Notes, n => n.Contains("natives: net10.0-ios26.0 and net10.0-ios27.2 declare none", StringComparison.Ordinal));
    }

    [Fact]
    public void Merge_PureObjCCopiesOfALibrary_WhoseNativesAreTheResourcePackage_Merge()
    {
        var result = Merge([Contribution("x26", ["net10.0-ios26.0"], payload: "resources"),
            Contribution("x27", ["net10.0-ios27.2"], payload: "resources")], "net10.0-ios26.0", "net10.0-ios27.2");
        Assert.True(result.Problems.IsEmpty, string.Join("\n", result.Problems));
        using var zip = ZipFile.OpenRead(result.Package!);
        Assert.Contains(zip.Entries, e => e.FullName == "lib/net10.0-ios27.2/Acme.Kit.resources.zip");
        Assert.DoesNotContain(zip.Entries, e => e.FullName.EndsWith(".targets", StringComparison.Ordinal));
    }

    [Theory]
    // The declaration says no natives, but the copy ships native/<tfm>/.
    [InlineData("native", "none", "carries native/net10.0-ios27.2/")]
    // The declaration says native/<tfm>/, but there is none: the copy's natives were lost.
    [InlineData("none", "native", "carries no native/net10.0-ios27.2/")]
    // The declaration says lib/<tfm>/'s resource package, but there is none.
    [InlineData("none", "resources", "carries no <Assembly>.resources.zip")]
    // The copy ships a resource package its declaration does not name.
    [InlineData("resources", "none", "lib/net10.0-ios27.2/ carries Acme.Kit.resources.zip")]
    [InlineData("none", "framework", "not one a copy can declare")]
    public void Merge_ACopyWhoseDeclarationAndNativesDisagree_IsRefused(string ships, string declares, string expected)
    {
        var result = Merge([Contribution("x26", ["net10.0-ios26.0"], payload: declares == "framework" ? "none" : declares),
            Contribution("x27", ["net10.0-ios27.2"], payload: ships, declaredPayload: declares)], "net10.0-ios26.0", "net10.0-ios27.2");
        Assert.Null(result.Package);
        Assert.Contains(result.Problems, p => p.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void Merge_CopiesThatShipTheirNativesDifferently_AreRefused()
    {
        var result = Merge([Contribution("x26", ["net10.0-ios26.0"]), Contribution("x27", ["net10.0-ios27.2"], payload: "none")],
            "net10.0-ios26.0", "net10.0-ios27.2");
        Assert.Null(result.Package);
        var problem = Assert.Single(result.Problems);
        Assert.Contains("different native payloads (native: net10.0-ios26.0; none: net10.0-ios27.2)", problem);
    }

    [Fact]
    public void Merge_ANativelessCopyWhoseTargetsReferenceNatives_IsRefused()
    {
        var result = Merge([Contribution("x26", ["net10.0-ios26.0"], payload: "none", targetsFor: Targets)], "net10.0-ios26.0");
        Assert.Contains(result.Problems, p => p.Contains("declares its native payload 'none', so it has no native/net10.0-ios26.0/", StringComparison.Ordinal));
    }

    [Fact]
    public void Merge_ACopyThatShipsTargetsItDeclaresItHasNot_IsRefused()
    {
        var result = Merge([Contribution("x26", ["net10.0-ios26.0"], payload: "none", declaresTargets: false)], "net10.0-ios26.0");
        Assert.Contains(result.Problems, p => p.Contains("declares that the copy ships no consumer targets, but it carries", StringComparison.Ordinal));
        // A Swift copy reaches its natives only through its targets: declaring none does not excuse them.
        var swift = Merge([Contribution("s26", ["net10.0-ios26.0"], omit: "buildTransitive/net10.0-ios26.0/Acme.Kit.targets", declaresTargets: false)], "net10.0-ios26.0");
        Assert.Contains(swift.Problems, p => p.Contains("carries no consumer targets", StringComparison.Ordinal));
    }

    [Fact]
    public void Merge_ANewerResourcePackageThatLosesAnExport_IsRefused()
    {
        if (!OperatingSystem.IsMacOS())
            return;
        var older = Dylib("rolder", "int kit_a(void){return 1;} int kit_b(void){return 2;}");
        var newer = Dylib("rnewer", "int kit_a(void){return 1;}");
        const string binary = "Kit.xcframework/ios-arm64/Kit.framework/Kit";
        (string, byte[])[] Resources(string tfm, byte[] image) => [($"lib/{tfm}/Acme.Kit.resources.zip", Zip((binary, image)))];

        var refused = Merge([
            Contribution("x26", ["net10.0-ios26.0"], payload: "resources", extra: Resources("net10.0-ios26.0", older)),
            Contribution("x27", ["net10.0-ios27.2"], payload: "resources", extra: Resources("net10.0-ios27.2", newer)),
        ], "net10.0-ios26.0", "net10.0-ios27.2");
        var problem = Assert.Single(refused.Problems);
        Assert.Contains("lib/net10.0-ios27.2/Acme.Kit.resources/" + binary, problem);
        Assert.Contains("kit_b", problem);

        var accepted = Merge([
            Contribution("y26", ["net10.0-ios26.0"], payload: "resources", extra: Resources("net10.0-ios26.0", newer)),
            Contribution("y27", ["net10.0-ios27.2"], payload: "resources", extra: Resources("net10.0-ios27.2", older)),
        ], "net10.0-ios26.0", "net10.0-ios27.2");
        Assert.True(accepted.Problems.IsEmpty, string.Join("\n", accepted.Problems));
    }

    [Fact]
    public void Merge_ResourcePackagesThatStoreAVersionedFrameworkWithSymbolicLinks_ReadEachImageOnce()
    {
        if (!OperatingSystem.IsMacOS())
            return;
        var older = Dylib("vlolder", "int kit_a(void){return 1;} int kit_b(void){return 2;}");
        var newer = Dylib("vlnewer", "int kit_a(void){return 1;} int kit_b(void){return 2;} int kit_c(void){return 3;}");
        var lost = Dylib("vllost", "int kit_a(void){return 1;}");
        const string a = "net10.0-maccatalyst26.0", b = "net10.0-maccatalyst27.2";
        (string, byte[])[] Resources(string tfm, byte[] zip) => [($"lib/{tfm}/Acme.Kit.resources.zip", zip)];

        // The root binary and Versions/Current are links: the image they name is compared, once.
        var merged = Merge([
            Contribution("x26", [a], payload: "resources", extra: Resources(a, VersionedFrameworkZip(older, links: true))),
            Contribution("x27", [b], payload: "resources", extra: Resources(b, VersionedFrameworkZip(newer, links: true))),
        ], a, b);
        Assert.True(merged.Problems.IsEmpty, string.Join("\n", merged.Problems));
        var compared = Assert.Single(merged.Notes, n => n.Contains("  exports ", StringComparison.Ordinal));
        Assert.Contains($"exports Acme.Kit.resources/{CatalystFramework}Versions/A/Kit (arm64)", compared);

        // A newer copy that loses an export is refused once, at the image itself.
        var refused = Merge([
            Contribution("y26", [a], payload: "resources", extra: Resources(a, VersionedFrameworkZip(older, links: true))),
            Contribution("y27", [b], payload: "resources", extra: Resources(b, VersionedFrameworkZip(lost, links: true))),
        ], a, b);
        var problem = Assert.Single(refused.Problems);
        Assert.Contains($"lib/{b}/Acme.Kit.resources/{CatalystFramework}Versions/A/Kit (arm64) lacks 1 export(s)", problem);
        Assert.Contains("kit_b", problem);
    }

    [Theory]
    // Entry paths spelled with "." components, which extraction drops.
    [InlineData("./", "Versions/Current/Kit", false)]
    // A link through a folder the archive stores as an entry of its own, holding nothing.
    [InlineData("", "Empty/../Versions/A/Kit", true)]
    public void Merge_AResourcePackageWhosePathsResolveOnExtraction_Merges(string spelling, string binaryLink, bool emptyFolder)
    {
        var image = Encoding.ASCII.GetBytes("!<arch>\n/               0           0     0     0       8         `\n\0\0\0\0\0\0\0\0");
        const string a = "net10.0-maccatalyst26.0", b = "net10.0-maccatalyst27.2";
        var framework = spelling + CatalystFramework;
        byte[] Zip() => ZipWithLinks([
            (framework + "Versions/A/Kit", image, null),
            (framework + "Versions/Current", null, "A"),
            (framework + "Kit", null, binaryLink),
            .. emptyFolder ? [(framework + "Empty/", (byte[]?)null, (string?)null)] : Array.Empty<(string, byte[]?, string?)>(),
        ]);
        var merged = Merge([
            Contribution("x26", [a], payload: "resources", extra: [($"lib/{a}/Acme.Kit.resources.zip", Zip())]),
            Contribution("x27", [b], payload: "resources", extra: [($"lib/{b}/Acme.Kit.resources.zip", Zip())]),
        ], a, b);
        Assert.True(merged.Problems.IsEmpty, string.Join("\n", merged.Problems));
        Assert.NotNull(merged.Package);
    }

    [Fact]
    public void Merge_ANewerCopyWhoseFrameworkLinksNameAnotherVersion_ComparesTheVersionTheyName()
    {
        if (!OperatingSystem.IsMacOS())
            return;
        var image = Dylib("vvkept", "int kit_a(void){return 1;} int kit_b(void){return 2;}");
        var lost = Dylib("vvlost", "int kit_a(void){return 1;}");
        const string a = "net10.0-maccatalyst26.0", b = "net10.0-maccatalyst27.2";
        byte[] Linked(string current, params (string Version, byte[] Image)[] versions) => ZipWithLinks([
            .. versions.Select(v => (CatalystFramework + $"Versions/{v.Version}/Kit", (byte[]?)v.Image, (string?)null)),
            (CatalystFramework + "Versions/Current", null, current),
            (CatalystFramework + "Kit", null, "Versions/Current/Kit"),
        ]);
        // The newer copy keeps the older copy's Versions/A, but its links name Versions/B.
        var refused = Merge([
            Contribution("x26", [a], payload: "resources", extra: [($"lib/{a}/Acme.Kit.resources.zip", Linked("A", ("A", image)))]),
            Contribution("x27", [b], payload: "resources", extra: [($"lib/{b}/Acme.Kit.resources.zip", Linked("B", ("A", image), ("B", lost)))]),
        ], a, b);
        var problem = Assert.Single(refused.Problems);
        Assert.Contains($"lib/{b}/Acme.Kit.resources/{CatalystFramework}Kit (arm64) lacks 1 export(s)", problem);
        Assert.Contains("kit_b", problem);
    }

    [Theory]
    // A copy zipped without links stores the root binary and Versions/Current/ as files of their own;
    // the other copy's links name the same images.
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Merge_AResourcePackageWithLinksAndOneWithout_CompareTheSameImages(bool olderLinks, bool newerLinks)
    {
        if (!OperatingSystem.IsMacOS())
            return;
        var image = Dylib($"vm{olderLinks}", "int kit_a(void){return 1;}");
        const string a = "net10.0-maccatalyst26.0", b = "net10.0-maccatalyst27.2";
        var merged = Merge([
            Contribution("x26", [a], payload: "resources", extra: [($"lib/{a}/Acme.Kit.resources.zip", VersionedFrameworkZip(image, olderLinks))]),
            Contribution("x27", [b], payload: "resources", extra: [($"lib/{b}/Acme.Kit.resources.zip", VersionedFrameworkZip(image, newerLinks))]),
        ], a, b);
        Assert.True(merged.Problems.IsEmpty, string.Join("\n", merged.Problems));
        Assert.NotNull(merged.Package);
    }

    [Theory]
    // A link to a file the archive does not carry.
    [InlineData("Versions/Current/Kit", "Versions/Current", "B")]
    // A link out of the archive.
    [InlineData("../../../../Kit", null, null)]
    // Links that name each other.
    [InlineData("Versions/Current/Kit", "Versions/Current", "Current")]
    // A path through a folder the archive does not carry, which no file system resolves.
    [InlineData("Missing/../Versions/A/Kit", null, null)]
    // A path through a file, as if it were a folder.
    [InlineData("Versions/A/Kit/../Kit", null, null)]
    public void Merge_AFrameworkBinaryLinkThatResolvesToNoImage_IsRefused(string binaryLink, string? otherLink, string? otherTarget)
    {
        var image = Encoding.ASCII.GetBytes("!<arch>\n/               0           0     0     0       8         `\n\0\0\0\0\0\0\0\0");
        const string a = "net10.0-maccatalyst26.0", b = "net10.0-maccatalyst27.2";
        var entries = new List<(string, byte[]?, string?)>
        {
            (CatalystFramework + "Versions/A/Kit", image, null),
            (CatalystFramework + "Kit", null, binaryLink),
        };
        if (otherLink is not null)
            entries.Add((CatalystFramework + otherLink, null, otherTarget));
        var refused = Merge([
            Contribution("x26", [a], payload: "resources", extra: [($"lib/{a}/Acme.Kit.resources.zip", ZipWithLinks([.. entries]))]),
            Contribution("x27", [b], payload: "resources",
                extra: [($"lib/{b}/Acme.Kit.resources.zip", ZipWithLinks((CatalystFramework + "Versions/A/Kit", image, null)))]),
        ], a, b);
        Assert.Null(refused.Package);
        var problem = Assert.Single(refused.Problems);
        Assert.Contains($"lib/{a}/Acme.Kit.resources/{CatalystFramework}Kit is a symbolic link to '{binaryLink}'", problem);
    }

    [Fact]
    public void Merge_ResourcePackagesThatCarryAStaticFramework_Merge()
    {
        // A static framework's binary is an archive: present in every copy, with no image exports to compare.
        var archive = Encoding.ASCII.GetBytes("!<arch>\n/               0           0     0     0       8         `\n\0\0\0\0\0\0\0\0");
        (string, byte[])[] Resources(string tfm) =>
            [($"lib/{tfm}/Acme.Kit.resources.zip", Zip(("Kit.xcframework/ios-arm64/Kit.framework/Kit", archive)))];
        var result = Merge([
            Contribution("x26", ["net10.0-ios26.0"], payload: "resources", extra: Resources("net10.0-ios26.0")),
            Contribution("x27", ["net10.0-ios27.2"], payload: "resources", extra: Resources("net10.0-ios27.2")),
        ], "net10.0-ios26.0", "net10.0-ios27.2");
        Assert.True(result.Problems.IsEmpty, string.Join("\n", result.Problems));

        // A newer copy whose framework turned dynamic where the older one's is static is no superset.
        if (!OperatingSystem.IsMacOS())
            return;
        var changed = Merge([
            Contribution("x26", ["net10.0-ios26.0"], payload: "resources", extra: Resources("net10.0-ios26.0")),
            Contribution("x27", ["net10.0-ios27.2"], payload: "resources",
                extra: [("lib/net10.0-ios27.2/Acme.Kit.resources.zip", Zip(("Kit.xcframework/ios-arm64/Kit.framework/Kit", Dylib("rstatic", "int kit_a(void){return 1;}"))))]),
        ], "net10.0-ios26.0", "net10.0-ios27.2");
        Assert.Contains(changed.Problems, p => p.Contains("Kit.framework/Kit is a dynamic image, but the older copy's is a static archive", StringComparison.Ordinal));
    }

    [Fact]
    public void Merge_AFatImageWithAnOffsetPastItsEnd_IsRefusedNotThrown()
    {
        // fat_header (64-bit form), one fat_arch_64 whose offset is the largest a signed 64-bit read holds.
        var image = new byte[8 + 32];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(image, 0xCAFEBABF);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(4), 1);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(16), long.MaxValue);
        (string, byte[])[] Resources(string tfm) =>
            [($"lib/{tfm}/Acme.Kit.resources.zip", Zip(("Kit.xcframework/ios-arm64/Kit.framework/Kit", image)))];
        var result = Merge([
            Contribution("x26", ["net10.0-ios26.0"], payload: "resources", extra: Resources("net10.0-ios26.0")),
            Contribution("x27", ["net10.0-ios27.2"], payload: "resources", extra: Resources("net10.0-ios27.2")),
        ], "net10.0-ios26.0", "net10.0-ios27.2");
        Assert.Null(result.Package);
        Assert.Contains(result.Problems, p => p.Contains("Kit.framework/Kit", StringComparison.Ordinal));
    }

    [Fact]
    public void Merge_AResourcePackageOfAnAssemblyWhoseNameHoldsResources_Merges()
    {
        var core = SwiftTransitionManifestTests.Compile("Acme.resources.Core", "public static class Core { }");
        (string, byte[])[] Extra(string tfm) =>
            [($"lib/{tfm}/Acme.resources.Core.dll", core),
             ($"lib/{tfm}/Acme.resources.Core.resources.zip", Zip(("Core.xcframework/Info.plist", Encoding.UTF8.GetBytes("<plist/>"))))];
        var result = Merge([
            Contribution("x26", ["net10.0-ios26.0"], payload: "resources", extra: Extra("net10.0-ios26.0")),
            Contribution("x27", ["net10.0-ios27.2"], payload: "resources", extra: Extra("net10.0-ios27.2")),
        ], "net10.0-ios26.0", "net10.0-ios27.2");
        Assert.True(result.Problems.IsEmpty, string.Join("\n", result.Problems));
    }

    [Fact]
    public void Merge_AResourcePackageNamedForNoAssemblyOfItsCopy_IsRefused()
    {
        var result = Merge([
            Contribution("x26", ["net10.0-ios26.0"], payload: "resources"),
            Contribution("x27", ["net10.0-ios27.2"], payload: "resources",
                extra: [("lib/net10.0-ios27.2/Acme.Kit.resources.zip", Zip(("Kit.xcframework/Info.plist", Encoding.UTF8.GetBytes("<plist/>")))),
                        ("lib/net10.0-ios27.2/Other.resources.zip", Zip(("Kit.xcframework/Info.plist", Encoding.UTF8.GetBytes("<plist/>"))))]),
        ], "net10.0-ios26.0", "net10.0-ios27.2");
        Assert.Null(result.Package);
        Assert.Contains(result.Problems, p => p.Contains("Other.resources.zip is no binding assembly's resource package", StringComparison.Ordinal));
    }

    [Fact]
    public void Compose_ANativelessCopy_DeclaresNoNativeDirectory_AndNoTargetsWhenItShipsNone()
    {
        var none = PackageCopyDeclarationEmitter.Compose(Id, "net10.0-ios26.0", ".NET 10 + Xcode 26.3", "26.2", Generator, "none");
        Assert.Contains("<NativePayload>none</NativePayload>", none);
        Assert.Contains("<NativeDirectory></NativeDirectory>", none);
        Assert.Contains("<TargetsFile>$(MSBuildThisFileDirectory)Acme.Kit.targets</TargetsFile>", none);
        var resources = PackageCopyDeclarationEmitter.Compose(Id, "net10.0-ios26.0", ".NET 10 + Xcode 26.3", "26.2", Generator, "resources", hasConsumerTargets: false);
        Assert.Contains("<NativePayload>resources</NativePayload>", resources);
        Assert.Contains("<TargetsFile></TargetsFile>", resources);
        Assert.Contains("<NativeDirectory>$(MSBuildThisFileDirectory)../../native/net10.0-ios26.0/</NativeDirectory>",
            PackageCopyDeclarationEmitter.Compose(Id, "net10.0-ios26.0", ".NET 10 + Xcode 26.3", "26.2", Generator));
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
    public void Merge_ACSharpNameANewerSdkMovesToAnotherSwiftDeclaration_IsRefused_NamingBoth()
    {
        // Xcode 26: `Outer.outer` is `Outer.OuterInfo`. Xcode 27 adds the sibling `Outer.outerInfo`,
        // which takes `OuterInfo`, and `outer` moves to `OuterInfo2`. Every older signature and export
        // is still there, so only the record of which Swift declaration a name stands for tells them apart.
        const string olderApi = "public sealed class Outer { public sealed class OuterInfo { public int Ping => 1; } }";
        const string newerApi = "public sealed class Outer { public sealed class OuterInfo { public int Ping => 1; } public sealed class OuterInfo2 { public int Ping => 1; } }";
        var older = Contribution("x26", ["net10.0-ios26.0"], api: olderApi, extra: [Declarations("net10.0-ios26.0",
            types: [("Outer", "type Kit.Outer"), ("Outer.OuterInfo", "type Kit.Outer.outer")],
            members: [("Outer.OuterInfo.Ping", "var Kit.Outer.outer.ping")])]);
        var newer = Contribution("x27", ["net10.0-ios27.2"], api: newerApi, extra: [Declarations("net10.0-ios27.2",
            types: [("Outer", "type Kit.Outer"), ("Outer.OuterInfo", "type Kit.Outer.outerInfo"), ("Outer.OuterInfo2", "type Kit.Outer.outer")],
            members: [("Outer.OuterInfo.Ping", "var Kit.Outer.outerInfo.ping"), ("Outer.OuterInfo2.Ping", "var Kit.Outer.outer.ping")])]);

        var result = Merge([older, newer], "net10.0-ios26.0", "net10.0-ios27.2");

        var problem = Assert.Single(result.Problems);
        Assert.Contains("2 C# name(s) stand for a different Swift declaration in the copy net10.0-ios27.2 than in the older copy net10.0-ios26.0", problem);
        Assert.Contains("the C# type Kit.Outer.OuterInfo is Swift `type Kit.Outer.outer` in net10.0-ios26.0 but Swift `type Kit.Outer.outerInfo` in net10.0-ios27.2", problem);
        Assert.Contains("the C# member Kit.Outer.OuterInfo.Ping is Swift `var Kit.Outer.outer.ping`", problem);
        Assert.Null(result.Package);
    }

    [Fact]
    public void Merge_ASignatureANewerDeclarationTakesWithOtherNullableAnnotations_IsRefused()
    {
        // Xcode 26: `Load(string, string?)` forwards `load(url: URL, name: String?)`. Xcode 27 adds
        // `load(url: String, name: String)`, which declares `Load(string, string)`: one CLR signature,
        // since a reference type's `?` is only an annotation, so its keys differ while a library compiled
        // against the older copy binds the newer declaration.
        const string api = "public static class Kit { public static void Load(string url, string name) { } }";
        var older = Contribution("x26", ["net10.0-ios26.0"], api: api, extra: [Declarations("net10.0-ios26.0",
            types: [("Kit", "type Kit.Kit")],
            members: [("Kit.Load(string,string?)", "static func Kit.Kit.load(url:name:) (url-string forwarder to (Foundation.NSUrl,string?))")])]);
        var newer = Contribution("x27", ["net10.0-ios27.2"], api: api, extra: [Declarations("net10.0-ios27.2",
            types: [("Kit", "type Kit.Kit")],
            members: [("Kit.Load(string,string)", "static func Kit.Kit.load(url:name:)")])]);

        var result = Merge([older, newer], "net10.0-ios26.0", "net10.0-ios27.2");

        var problem = Assert.Single(result.Problems);
        Assert.Contains("the C# member Kit.Kit.Load(string,string?) is Swift `static func Kit.Kit.load(url:name:) (url-string forwarder to (Foundation.NSUrl,string?))` "
            + "in net10.0-ios26.0 but Swift `static func Kit.Kit.load(url:name:)` in net10.0-ios27.2", problem);
        Assert.Contains("Kit.Load(string,string)", problem);
    }

    [Fact]
    public void Merge_ATakeoverBesideAnAddedValueTypeOverloadOfTheSameShape_IsRefused()
    {
        // The newer copy declares the older forwarder's CLR signature `Load(string, string, int?)` itself
        // and adds `Load(string, string, int)`: two keys of one shape, neither the older declaration.
        var older = Contribution("x26", ["net10.0-ios26.0"], api: "public static class Kit { public static void Load(string url, string name, int? count) { } }",
            extra: [Declarations("net10.0-ios26.0", types: [("Kit", "type Kit.Kit")],
                members: [("Kit.Load(string,string?,int?)", "static func Kit.Kit.load(url:name:count:) (url-string forwarder to (Foundation.NSUrl,string?,int?))")])]);
        var newer = Contribution("x27", ["net10.0-ios27.2"],
            api: "public static class Kit { public static void Load(string url, string name, int? count) { } public static void Load(string url, string name, int count) { } }",
            extra: [Declarations("net10.0-ios27.2", types: [("Kit", "type Kit.Kit")],
                members: [("Kit.Load(string,string,int?)", "static func Kit.Kit.load(url:name:count:)"),
                          ("Kit.Load(string,string,int)", "static func Kit.Kit.load(url:name:count:) (native-width forwarder to (string,string,nint))")])]);

        var problem = Assert.Single(Merge([older, newer], "net10.0-ios26.0", "net10.0-ios27.2").Problems);

        Assert.Contains("the C# member Kit.Kit.Load(string,string?,int?) is Swift `static func Kit.Kit.load(url:name:count:) (url-string forwarder to (Foundation.NSUrl,string?,int?))` in net10.0-ios26.0", problem);
        Assert.Contains("as Kit.Kit.Load(string,string,int) or Kit.Kit.Load(string,string,int?)", problem);
    }

    [Fact]
    public void Merge_ASignatureANewerDeclarationTakesWithOtherTupleElementNames_IsRefused()
    {
        const string api = "public static class Kit { public static void Load(string url, (int, int) options) { } }";
        var older = Contribution("x26", ["net10.0-ios26.0"], api: api, extra: [Declarations("net10.0-ios26.0",
            types: [("Kit", "type Kit.Kit")],
            members: [("Kit.Load(string,(int, int))", "static func Kit.Kit.load(url:options:) (url-string forwarder to (Foundation.NSUrl,(int, int)))")])]);
        var newer = Contribution("x27", ["net10.0-ios27.2"], api: api, extra: [Declarations("net10.0-ios27.2",
            types: [("Kit", "type Kit.Kit")],
            members: [("Kit.Load(string,(int x, int y))", "static func Kit.Kit.load(url:options:)")])]);

        var problem = Assert.Single(Merge([older, newer], "net10.0-ios26.0", "net10.0-ios27.2").Problems);

        Assert.Contains("as Kit.Kit.Load(string,(int x, int y))", problem);
    }

    [Fact]
    public void Merge_ATakeoverBesideAnAddedOverloadThatKeepsTheOlderDeclaration_IsRefused()
    {
        // `Load(string?, int?)` is `load(a:b:)`. The newer copy's `Load(string, int?)`, the same CLR
        // signature, is another declaration, and an added `Load(string, int)` keeps `load(a:b:)`: a
        // candidate of the same shape that keeps the declaration does not clear the one that moved.
        var older = Contribution("x26", ["net10.0-ios26.0"], api: "public static class Kit { public static void Load(string a, int? b) { } }",
            extra: [Declarations("net10.0-ios26.0", types: [("Kit", "type Kit.Kit")],
                members: [("Kit.Load(string?,int?)", "static func Kit.Kit.load(a:b:)")])]);
        var newer = Contribution("x27", ["net10.0-ios27.2"],
            api: "public static class Kit { public static void Load(string a, int? b) { } public static void Load(string a, int b) { } }",
            extra: [Declarations("net10.0-ios27.2", types: [("Kit", "type Kit.Kit")],
                members: [("Kit.Load(string,int?)", "static func Kit.Kit.load(c:d:)"), ("Kit.Load(string,int)", "static func Kit.Kit.load(a:b:)")])]);

        var problem = Assert.Single(Merge([older, newer], "net10.0-ios26.0", "net10.0-ios27.2").Problems);

        Assert.Contains("the C# member Kit.Kit.Load(string?,int?) is Swift `static func Kit.Kit.load(a:b:)` in net10.0-ios26.0 but Swift `static func Kit.Kit.load(c:d:)` in net10.0-ios27.2, as Kit.Kit.Load(string,int?)", problem);
    }

    [Fact]
    public void Merge_ARespellBesideAnAddedValueTypeOverloadOfTheSameShape_Merges()
    {
        // `Load(string?, int?)` is `load(a:b:)`; the newer SDK respells it `Load(string, int?)` (the same
        // CLR signature, the same declaration) and adds `load(c:d:)` as `Load(string, int)`, another CLR
        // signature. Both newer keys have the older key's shape, but only the first is its signature,
        // which the copies' assemblies tell apart, and it keeps the declaration.
        var older = Contribution("x26", ["net10.0-ios26.0"], api: "namespace Kit { public static class Kit { public static void Load(string? a, int? b) { } } }",
            extra: [Declarations("net10.0-ios26.0", types: [("Kit", "type Kit.Kit")],
                members: [("Kit.Load(string?,int?)", "static func Kit.Kit.load(a:b:)")])]);
        var newer = Contribution("x27", ["net10.0-ios27.2"],
            api: "namespace Kit { public static class Kit { public static void Load(string a, int? b) { } public static void Load(string a, int b) { } } }",
            extra: [Declarations("net10.0-ios27.2", types: [("Kit", "type Kit.Kit")],
                members: [("Kit.Load(string,int?)", "static func Kit.Kit.load(a:b:)"), ("Kit.Load(string,int)", "static func Kit.Kit.load(c:d:)")])]);

        var merged = Merge([older, newer], "net10.0-ios26.0", "net10.0-ios27.2");

        Assert.True(merged.Problems.IsEmpty, string.Join("\n", merged.Problems));
    }

    [Fact]
    public void Merge_ATakeoverTheAssembliesResolve_IsRefused_NamingOnlyTheOlderSignature()
    {
        // The takeover of `Merge_ATakeoverBesideAnAddedOverloadThatKeepsTheOlderDeclaration_IsRefused`
        // with assemblies that declare the record's namespace: the older signature `Load(string, int?)`
        // resolves to the newer key `Load(string,int?)`, now another declaration, and the added
        // `Load(string, int)` that keeps `load(a:b:)` is another signature, so it neither clears nor
        // joins the refusal.
        var older = Contribution("x26", ["net10.0-ios26.0"], api: "namespace Kit { public static class Kit { public static void Load(string? a, int? b) { } } }",
            extra: [Declarations("net10.0-ios26.0", types: [("Kit", "type Kit.Kit")],
                members: [("Kit.Load(string?,int?)", "static func Kit.Kit.load(a:b:)")])]);
        var newer = Contribution("x27", ["net10.0-ios27.2"],
            api: "namespace Kit { public static class Kit { public static void Load(string a, int? b) { } public static void Load(string a, int b) { } } }",
            extra: [Declarations("net10.0-ios27.2", types: [("Kit", "type Kit.Kit")],
                members: [("Kit.Load(string,int?)", "static func Kit.Kit.load(c:d:)"), ("Kit.Load(string,int)", "static func Kit.Kit.load(a:b:)")])]);

        var problem = Assert.Single(Merge([older, newer], "net10.0-ios26.0", "net10.0-ios27.2").Problems);

        Assert.Contains("the C# member Kit.Kit.Load(string?,int?) is Swift `static func Kit.Kit.load(a:b:)` in net10.0-ios26.0 but Swift `static func Kit.Kit.load(c:d:)` in net10.0-ios27.2, as Kit.Kit.Load(string,int?)", problem);
        Assert.DoesNotContain("Kit.Kit.Load(string,int) ", problem);
    }

    [Fact]
    public void Merge_ATakeoverSpelledThroughAKeywordAlias_IsRefused()
    {
        // The takeover of `Merge_ATakeoverTheAssembliesResolve_IsRefused_NamingOnlyTheOlderSignature`, with
        // the newer key spelling `string` as `global::System.String`: one type, so the key is a candidate
        // for the older member, and its declaration moved.
        var older = Contribution("x26", ["net10.0-ios26.0"], api: "namespace Kit { public static class Kit { public static void Load(string? a, int? b) { } } }",
            extra: [Declarations("net10.0-ios26.0", types: [("Kit", "type Kit.Kit")],
                members: [("Kit.Load(string?,int?)", "static func Kit.Kit.load(a:b:)")])]);
        var newer = Contribution("x27", ["net10.0-ios27.2"],
            api: "namespace Kit { public static class Kit { public static void Load(string a, int? b) { } public static void Load(string a, int b) { } } }",
            extra: [Declarations("net10.0-ios27.2", types: [("Kit", "type Kit.Kit")],
                members: [("Kit.Load(global::System.String,int?)", "static func Kit.Kit.load(c:d:)"), ("Kit.Load(string,int)", "static func Kit.Kit.load(a:b:)")])]);

        var problem = Assert.Single(Merge([older, newer], "net10.0-ios26.0", "net10.0-ios27.2").Problems);

        Assert.Contains("the C# member Kit.Kit.Load(string?,int?) is Swift `static func Kit.Kit.load(a:b:)` in net10.0-ios26.0 but Swift `static func Kit.Kit.load(c:d:)` in net10.0-ios27.2, as Kit.Kit.Load(global::System.String,int?)", problem);
    }

    [Fact]
    public void Merge_ATakeoverBesideATypeOnlyTheNewerCopyDeclares_ReadsEachKeyInItsOwnCopy()
    {
        // The older key spells `int` as a bare `Int32`, which is `System.Int32` there: the newer copy's
        // added `Kit.Int32` is not in the older copy's scope, so the two keys are one shape and the
        // takeover is found.
        var older = Contribution("x26", ["net10.0-ios26.0"], api: "namespace Kit { public static class Kit { public static void Load(int a) { } } }",
            extra: [Declarations("net10.0-ios26.0", types: [("Kit", "type Kit.Kit")],
                members: [("Kit.Load(Int32)", "static func Kit.Kit.load(a:)")])]);
        var newer = Contribution("x27", ["net10.0-ios27.2"],
            api: "namespace Kit { public static class Kit { public sealed class Int32 { } public static void Load(int c) { } } }",
            extra: [Declarations("net10.0-ios27.2", types: [("Kit", "type Kit.Kit"), ("Kit.Int32", "type Kit.Kit.Int32")],
                members: [("Kit.Load(int)", "static func Kit.Kit.load(c:)")])]);

        var problem = Assert.Single(Merge([older, newer], "net10.0-ios26.0", "net10.0-ios27.2").Problems);

        Assert.Contains("the C# member Kit.Kit.Load(Int32) is Swift `static func Kit.Kit.load(a:)` in net10.0-ios26.0 but Swift `static func Kit.Kit.load(c:)` in net10.0-ios27.2, as Kit.Kit.Load(int)", problem);
    }

    [Fact]
    public void Merge_ATakeoverBesideATypeOnlyTheNewerCopyDeclares_ResolvesEachKeyInItsOwnCopysAssembly()
    {
        // The older key's bare `Int32` is `System.Int32`. The newer copy keeps that declaration as
        // `Load(int)` and adds its own `Kit.Int32` with an overload whose key, `Load(Int32?)`, spells it
        // bare too. Read in the newer copy it is that overload, another signature; read in the older
        // copy (or the older key read in the newer one) it would be taken for the older member.
        var older = Contribution("x26", ["net10.0-ios26.0"], api: "namespace Kit { public static class Kit { public static void Load(int a) { } } }",
            extra: [Declarations("net10.0-ios26.0", types: [("Kit", "type Kit.Kit")],
                members: [("Kit.Load(Int32)", "static func Kit.Kit.load(a:)")])]);
        var newer = Contribution("x27", ["net10.0-ios27.2"],
            api: "namespace Kit { public static class Kit { public sealed class Int32 { } public static void Load(int a) { } public static void Load(Int32 b) { } } }",
            extra: [Declarations("net10.0-ios27.2", types: [("Kit", "type Kit.Kit"), ("Kit.Int32", "type Kit.Kit.Int32")],
                members: [("Kit.Load(int)", "static func Kit.Kit.load(a:)"), ("Kit.Load(Int32?)", "static func Kit.Kit.load(b:)")])]);

        var merged = Merge([older, newer], "net10.0-ios26.0", "net10.0-ios27.2");

        Assert.True(merged.Problems.IsEmpty, string.Join("\n", merged.Problems));
    }

    [Fact]
    public void Merge_ATakeoverOfAMemberTakingAnInheritedNestedType_BesideAnAddedTypeOfItsName_IsRefused()
    {
        // `Kit : Base` sees `Base.Int32` as a bare `Int32` in both copies. The newer copy adds a
        // namespace-level `Int32`, which C# never reaches from `Kit`, and gives `Load(Int32)` another
        // declaration: the same CLR signature, so the name moved.
        const string olderApi = "namespace Kit { public class Base { public sealed class Int32 { } } public class Kit : Base { public void Load(Int32? a) { } } }";
        const string newerApi = "namespace Kit { public sealed class Int32 { } public class Base { public sealed class Int32 { } } public class Kit : Base { public void Load(Int32 b) { } } }";
        (string, string)[] types = [("Base", "type Kit.Base"), ("Base.Int32", "type Kit.Base.Int32"), ("Kit", "type Kit.Kit")];
        var older = Contribution("x26", ["net10.0-ios26.0"], api: olderApi, extra: [Declarations("net10.0-ios26.0", types: types,
            members: [("Kit.Load(Int32?)", "func Kit.Kit.load(a:)")])]);
        var newer = Contribution("x27", ["net10.0-ios27.2"], api: newerApi, extra: [Declarations("net10.0-ios27.2", types: [.. types, ("Int32", "type Kit.Int32")],
            members: [("Kit.Load(Int32)", "func Kit.Kit.load(b:)")])]);

        var problem = Assert.Single(Merge([older, newer], "net10.0-ios26.0", "net10.0-ios27.2").Problems);

        Assert.Contains("the C# member Kit.Kit.Load(Int32?) is Swift `func Kit.Kit.load(a:)` in net10.0-ios26.0 but Swift `func Kit.Kit.load(b:)` in net10.0-ios27.2, as Kit.Kit.Load(Int32)", problem);
    }

    [Fact]
    public void Merge_AKeywordTypeSpelledBareBesideAGenericBindingTypeOfItsName_IsReadAsTheKeyword()
    {
        // The binding's `Kit.Int32<T>` is not what a bare `Int32` (no type arguments) names: the older
        // key `Load(Int32)` is `Load(System.Int32)`, which the newer copy spells `Load(int)` and gives
        // another declaration.
        const string api = "namespace Kit { public static class Kit { public sealed class Int32<T> { } public static void Load(int a) { } } }";
        var older = Contribution("x26", ["net10.0-ios26.0"], api: api, extra: [Declarations("net10.0-ios26.0", types: [("Kit", "type Kit.Kit"), ("Kit.Int32", "type Kit.Kit.Int32")],
            members: [("Kit.Load(Int32)", "static func Kit.Kit.load(a:)")])]);
        var newer = Contribution("x27", ["net10.0-ios27.2"], api: api, extra: [Declarations("net10.0-ios27.2", types: [("Kit", "type Kit.Kit"), ("Kit.Int32", "type Kit.Kit.Int32")],
            members: [("Kit.Load(int)", "static func Kit.Kit.load(c:)")])]);

        var problem = Assert.Single(Merge([older, newer], "net10.0-ios26.0", "net10.0-ios27.2").Problems);

        Assert.Contains("the C# member Kit.Kit.Load(Int32) is Swift `static func Kit.Kit.load(a:)` in net10.0-ios26.0 but Swift `static func Kit.Kit.load(c:)` in net10.0-ios27.2, as Kit.Kit.Load(int)", problem);
    }

    [Fact]
    public void Merge_ATakeoverOfAMemberBesideAGenericNamesakeOfTheKeysSwiftArity_IsRefused()
    {
        // A key's `1 counts the Swift declaration's generic parameters, which the C# member need not
        // declare: `Box.Load(string)`1` may be `Load(string)` as well as `Load<U>(string)`, so the newer
        // copy's key is compared, and it gives `Load(string)` another declaration.
        var older = Contribution("x26", ["net10.0-ios26.0"], api: "namespace Kit { public class Box { public void Load(string? a) { } } }",
            extra: [Declarations("net10.0-ios26.0", types: [("Box", "type Kit.Box")], members: [("Box.Load(string?)`1", "func Kit.Box.load(a:)")])]);
        var newer = Contribution("x27", ["net10.0-ios27.2"], api: "namespace Kit { public class Box { public void Load(string b) { } public void Load<U>(string c) { } } }",
            extra: [Declarations("net10.0-ios27.2", types: [("Box", "type Kit.Box")], members: [("Box.Load(string)`1", "func Kit.Box.load(b:)")])]);

        var problem = Assert.Single(Merge([older, newer], "net10.0-ios26.0", "net10.0-ios27.2").Problems);

        Assert.Contains("the C# member Kit.Box.Load(string?)`1 is Swift `func Kit.Box.load(a:)` in net10.0-ios26.0 but Swift `func Kit.Box.load(b:)` in net10.0-ios27.2, as Kit.Box.Load(string)`1", problem);
    }

    [Fact]
    public void Merge_ATakeoverOfAMemberTakingATypeNamedWithACombiningMark_IsRefused()
    {
        // `कि` is one C# identifier: a letter and a combining vowel sign.
        const string api = "namespace Kit { public sealed class कि { } public static class Kit { public static void Load(कि a) { } } }";
        var older = Contribution("x26", ["net10.0-ios26.0"], api: api, extra: [Declarations("net10.0-ios26.0", types: [("Kit", "type Kit.Kit"), ("कि", "type Kit.कि")],
            members: [("Kit.Load(कि?)", "static func Kit.Kit.load(a:)")])]);
        var newer = Contribution("x27", ["net10.0-ios27.2"], api: api, extra: [Declarations("net10.0-ios27.2", types: [("Kit", "type Kit.Kit"), ("कि", "type Kit.कि")],
            members: [("Kit.Load(कि)", "static func Kit.Kit.load(b:)")])]);

        var problem = Assert.Single(Merge([older, newer], "net10.0-ios26.0", "net10.0-ios27.2").Problems);

        Assert.Contains("the C# member Kit.Kit.Load(कि?) is Swift `static func Kit.Kit.load(a:)` in net10.0-ios26.0 but Swift `static func Kit.Kit.load(b:)` in net10.0-ios27.2, as Kit.Kit.Load(कि)", problem);
    }

    [Fact]
    public void Merge_AKeyThatDoesNotRead_IsComparedWithEveryNewerKeyOfItsMemberName()
    {
        const string api = "namespace Kit { public static class Kit { public static void Load(int a) { } } }";
        var older = Contribution("x26", ["net10.0-ios26.0"], api: api, extra: [Declarations("net10.0-ios26.0", types: [("Kit", "type Kit.Kit")],
            members: [("Kit.Load(%)", "static func Kit.Kit.load(a:)")])]);
        var newer = Contribution("x27", ["net10.0-ios27.2"], api: api, extra: [Declarations("net10.0-ios27.2", types: [("Kit", "type Kit.Kit")],
            members: [("Kit.Load(int)", "static func Kit.Kit.load(b:)")])]);

        var problem = Assert.Single(Merge([older, newer], "net10.0-ios26.0", "net10.0-ios27.2").Problems);

        Assert.Contains("the C# member Kit.Kit.Load(%) is Swift `static func Kit.Kit.load(a:)` in net10.0-ios26.0 but Swift `static func Kit.Kit.load(b:)` in net10.0-ios27.2, as Kit.Kit.Load(int)", problem);
    }

    [Fact]
    public void Merge_ATakeoverOfAMemberTakingANestedTypeSpelledThroughADerivedType_IsRefused()
    {
        // `Deps.Child.Value` is `Deps.Base.Value`, which `Child` inherits, and not the unchanged
        // overload's `Extra.Deps.Child.Value`, whose name it ends: the newer copy gives the first
        // `Load` another declaration.
        const string api = "namespace Deps { public class Base { public sealed class Value { } } public class Child : Base { } } "
                           + "namespace Extra.Deps { public class Child { public sealed class Value { } } } "
                           + "namespace Kit { public class Box { public void Load(global::Deps.Base.Value a) { } public void Load(Extra.Deps.Child.Value c) { } } }";
        var older = Contribution("x26", ["net10.0-ios26.0"], api: api, extra: [Declarations("net10.0-ios26.0", types: [("Box", "type Kit.Box")],
            members: [("Box.Load(Deps.Base.Value?)", "func Kit.Box.load(a:)"), ("Box.Load(Extra.Deps.Child.Value)", "func Kit.Box.load(c:)")])]);
        var newer = Contribution("x27", ["net10.0-ios27.2"], api: api, extra: [Declarations("net10.0-ios27.2", types: [("Box", "type Kit.Box")],
            members: [("Box.Load(Deps.Child.Value)", "func Kit.Box.load(b:)"), ("Box.Load(Extra.Deps.Child.Value)", "func Kit.Box.load(c:)")])]);

        var problem = Assert.Single(Merge([older, newer], "net10.0-ios26.0", "net10.0-ios27.2").Problems);

        Assert.Contains("the C# member Kit.Box.Load(Deps.Base.Value?) is Swift `func Kit.Box.load(a:)` in net10.0-ios26.0 but Swift `func Kit.Box.load(b:)` in net10.0-ios27.2, as Kit.Box.Load(Deps.Child.Value)", problem);
    }

    [Fact]
    public void Merge_ARelativeSystemNameThatIsTheBindingsOwnNestedType_IsNotTheBaseClassLibraryType()
    {
        // The newer copy respells `Load(string?)` as `Load(string)` and adds `Load(System.String)`,
        // which takes its own nested `Kit.System.String`: another signature, so it does not move the name.
        var older = Contribution("x26", ["net10.0-ios26.0"], api: "namespace Kit { public static class Kit { public static void Load(string? a) { } } }",
            extra: [Declarations("net10.0-ios26.0", types: [("Kit", "type Kit.Kit")],
                members: [("Kit.Load(string?)", "static func Kit.Kit.load(a:)")])]);
        var newer = Contribution("x27", ["net10.0-ios27.2"],
            api: "namespace Kit { public static class Kit { public static class System { public sealed class String { } } public static void Load(string a) { } public static void Load(System.String b) { } } }",
            extra: [Declarations("net10.0-ios27.2", types: [("Kit", "type Kit.Kit"), ("Kit.System", "type Kit.Kit.System"), ("Kit.System.String", "type Kit.Kit.System.String")],
                members: [("Kit.Load(string)", "static func Kit.Kit.load(a:)"), ("Kit.Load(System.String)", "static func Kit.Kit.load(b:)")])]);

        var merged = Merge([older, newer], "net10.0-ios26.0", "net10.0-ios27.2");

        Assert.True(merged.Problems.IsEmpty, string.Join("\n", merged.Problems));
    }

    [Fact]
    public void Merge_AGlobalNameAndARelativeNameOfANestedType_AreTwoTypes()
    {
        // The newer copy respells `Load(Other.Value?)` (the namespace-level `Other.Value`) as
        // `Load(global::Other.Value)` and adds `Load(Other.Value)`, which takes its own nested
        // `Kit.Other.Value`: another signature, so it does not move the name.
        const string shared = "namespace Other { public sealed class Value { } } ";
        var older = Contribution("x26", ["net10.0-ios26.0"], api: shared + "namespace Kit { public static class Kit { public static void Load(Other.Value? a) { } } }",
            extra: [Declarations("net10.0-ios26.0", types: [("Kit", "type Kit.Kit")],
                members: [("Kit.Load(Other.Value?)", "static func Kit.Kit.load(a:)")])]);
        var newer = Contribution("x27", ["net10.0-ios27.2"],
            api: shared + "namespace Kit { public static class Kit { public static class Other { public sealed class Value { } } "
                 + "public static void Load(global::Other.Value a) { } public static void Load(Other.Value b) { } } }",
            extra: [Declarations("net10.0-ios27.2", types: [("Kit", "type Kit.Kit"), ("Kit.Other", "type Kit.Kit.Other"), ("Kit.Other.Value", "type Kit.Kit.Other.Value")],
                members: [("Kit.Load(global::Other.Value)", "static func Kit.Kit.load(a:)"), ("Kit.Load(Other.Value)", "static func Kit.Kit.load(b:)")])]);

        var merged = Merge([older, newer], "net10.0-ios26.0", "net10.0-ios27.2");

        Assert.True(merged.Problems.IsEmpty, string.Join("\n", merged.Problems));
    }

    [Fact]
    public void TheShippedMergeTask_CarriesTheQualifiersImportTable()
    {
        // The merge reads a key's own nested type narrowly only with this table; without it every
        // candidate of a shape is compared, so a task assembly that lost it would refuse merges of a binding's
        // own nested type beside a base class library type of its name, never let a takeover through.
        using var pe = new System.Reflection.PortableExecutable.PEReader(File.OpenRead(Path.Combine(AppContext.BaseDirectory, "SwiftBindings.Tasks.dll")));
        var md = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
        Assert.Contains("SwiftBindings.Tasks.imported-type-names.json", md.ManifestResources.Select(h => md.GetString(md.GetManifestResource(h).Name)));
    }

    [Theory]
    [InlineData("Kit.Load(global::System.String,System.Int32)", "Kit.Load(string,int)")]
    [InlineData("Kit.Load(string?,(int a, int b))", "Kit.Load(String,ValueTuple<int,int>)")]
    [InlineData("Kit.Load(Int32)", "Kit.Load(Kit.Int32?)")]
    [InlineData("Kit.Load(Other.Value)", "Kit.Load(global::Other.Value)")]
    [InlineData("Kit.Load(T)`1", "Kit.Load(TValue)`2")]
    [InlineData("Box<T>.Load(T)", "Box<TValue>.Load(TValue)")]
    [InlineData("Kit.this[string?]", "Kit.this[String]")]
    [InlineData("Kit.Load(%)", "Kit.Load(int)")]
    public void MemberName_EverySpellingThatCanNameOneSignature_SharesIt(string key, string other) =>
        // The member name only buckets keys: the copies' assemblies tell their signatures apart.
        Assert.Equal(SwiftBindingPackageMerge.MemberName(key), SwiftBindingPackageMerge.MemberName(other));

    [Fact]
    public void Merge_ATakeoverOfAGenericMethodWhoseGenericParameterIsRenamed_IsRefused()
    {
        // `Load<T>(T)` and `Load<TValue>(TValue)` are one CLR signature, `Load<!!0>(!!0)`.
        var older = Contribution("x26", ["net10.0-ios26.0"], api: "namespace Kit { public class Box { public void Load<T>(T a) { } } }",
            extra: [Declarations("net10.0-ios26.0", types: [("Box", "type Kit.Box")], members: [("Box.Load(T)`1", "func Kit.Box.load(a:)")])]);
        var newer = Contribution("x27", ["net10.0-ios27.2"], api: "namespace Kit { public class Box { public void Load<TValue>(TValue b) { } } }",
            extra: [Declarations("net10.0-ios27.2", types: [("Box", "type Kit.Box")], members: [("Box.Load(TValue)`1", "func Kit.Box.load(b:)")])]);

        var problem = Assert.Single(Merge([older, newer], "net10.0-ios26.0", "net10.0-ios27.2").Problems);

        Assert.Contains("the C# member Kit.Box.Load(T)`1 is Swift `func Kit.Box.load(a:)` in net10.0-ios26.0 but Swift `func Kit.Box.load(b:)` in net10.0-ios27.2, as Kit.Box.Load(TValue)`1", problem);
    }

    [Fact]
    public void Merge_ARespellOfAnIndexerAndANestedNullableTheAssembliesResolve_Merges()
    {
        // `this[int?]` respelled with an annotation of its element list, and a method whose `Nullable<T>`
        // sits inside a generic argument: each resolves to the signature it had, beside an added
        // overload of the same shape that is another signature.
        const string olderApi = "namespace Kit { public sealed class Box { public string this[int? i, string? k] => \"\"; "
                                + "public void Fill(System.Collections.Generic.IReadOnlyList<int?>? values) { } } }";
        const string newerApi = "namespace Kit { public sealed class Box { public string this[int? i, string? k] => \"\"; public string this[int i, string k] => \"\"; "
                                + "public void Fill(System.Collections.Generic.IReadOnlyList<int?>? values) { } public void Fill(System.Collections.Generic.IReadOnlyList<int> values) { } } }";
        var older = Contribution("x26", ["net10.0-ios26.0"], api: olderApi, extra: [Declarations("net10.0-ios26.0", types: [("Box", "type Kit.Box")],
            members: [("Box.this[int?,string?]", "subscript Kit.Box.subscript(_:k:)"), ("Box.Fill(IReadOnlyList<int?>?)", "func Kit.Box.fill(_:)")])]);
        var newer = Contribution("x27", ["net10.0-ios27.2"], api: newerApi, extra: [Declarations("net10.0-ios27.2", types: [("Box", "type Kit.Box")],
            members: [("Box.this[int?,string]", "subscript Kit.Box.subscript(_:k:)"), ("Box.this[int,string]", "subscript Kit.Box.subscript(_:j:)"),
                      ("Box.Fill(IReadOnlyList<int?>)", "func Kit.Box.fill(_:)"), ("Box.Fill(IReadOnlyList<int>)", "func Kit.Box.fill(other:)")])]);

        var merged = Merge([older, newer], "net10.0-ios26.0", "net10.0-ios27.2");

        Assert.True(merged.Problems.IsEmpty, string.Join("\n", merged.Problems));
    }

    [Fact]
    public void Merge_AnIndexerANewerSubscriptTakesWithOtherNullableAnnotations_IsRefused()
    {
        const string api = "public interface IIndexed { string this[string key] { get; } }";
        var older = Contribution("x26", ["net10.0-ios26.0"], api: api, extra: [Declarations("net10.0-ios26.0",
            types: [("IIndexed", "protocol Kit.Indexed")], members: [("IIndexed.this[string?]", "subscript Kit.Indexed.subscript(a:)")])]);
        var newer = Contribution("x27", ["net10.0-ios27.2"], api: api, extra: [Declarations("net10.0-ios27.2",
            types: [("IIndexed", "protocol Kit.Indexed")], members: [("IIndexed.this[string]", "subscript Kit.Indexed.subscript(b:)")])]);

        var problem = Assert.Single(Merge([older, newer], "net10.0-ios26.0", "net10.0-ios27.2").Problems);

        Assert.Contains("as Kit.IIndexed.this[string]", problem);
    }

    [Fact]
    public void Merge_AKeyWhoseBracketsDoNotClose_IsComparedAsSpelled()
    {
        const string api = "public interface IIndexed { string this[string key] { get; } }";
        var older = Contribution("x26", ["net10.0-ios26.0"], api: api, extra: [Declarations("net10.0-ios26.0",
            types: [("IIndexed", "protocol Kit.Indexed")], members: [("IIndexed.this[string?]", "subscript Kit.Indexed.subscript(a:)")])]);
        var newer = Contribution("x27", ["net10.0-ios27.2"], api: api, extra: [Declarations("net10.0-ios27.2",
            types: [("IIndexed", "protocol Kit.Indexed")], members: [("IIndexed.this[string?]", "subscript Kit.Indexed.subscript(a:)"), ("IIndexed.this][", "subscript Kit.Indexed.subscript(b:)")])]);
        var merged = Merge([older, newer], "net10.0-ios26.0", "net10.0-ios27.2");
        Assert.True(merged.Problems.IsEmpty, string.Join("\n", merged.Problems));
    }

    [Theory]
    [InlineData("Kit.Load(string,string?)", "Kit.Load(string,string)")]
    [InlineData("Kit.Load(string,(int, System.Collections.Generic.IReadOnlyList<string?> b))", "Kit.Load(string,(int a, System.Collections.Generic.IReadOnlyList<string> b))")]
    [InlineData("Kit.Load(ref string?,int)`1", "Kit.Load(ref string,int)`1")]
    [InlineData("IIndexed.this[string?]", "IIndexed.this[string]")]
    public void Merge_AnnotationsAndTupleNamesAloneDoNotMoveAName(string olderKey, string newerKey)
    {
        // The same declaration spelled with other annotations or element names is no move.
        const string api = "public static class Kit { public static void Load(string url, string name) { } }";
        var older = Contribution("x26", ["net10.0-ios26.0"], api: api, extra: [Declarations("net10.0-ios26.0",
            types: [("Kit", "type Kit.Kit")], members: [(olderKey, "static func Kit.Kit.load(url:name:)")])]);
        var newer = Contribution("x27", ["net10.0-ios27.2"], api: api, extra: [Declarations("net10.0-ios27.2",
            types: [("Kit", "type Kit.Kit")], members: [(newerKey, "static func Kit.Kit.load(url:name:)")])]);
        var merged = Merge([older, newer], "net10.0-ios26.0", "net10.0-ios27.2");
        Assert.True(merged.Problems.IsEmpty, string.Join("\n", merged.Problems));
    }

    [Fact]
    public void Merge_CopiesWhoseNamesKeepTheirSwiftDeclarations_Merge_AndANewerCopyWithoutTheRecord_IsRefused()
    {
        var older = Contribution("x26", ["net10.0-ios26.0"], extra: [Declarations("net10.0-ios26.0",
            types: [("Kit", "type Kit.Kit")], members: [("Kit.A()", "static func Kit.Kit.a()")])]);
        // The newer copy adds a member and a type; every name the older copy has keeps its declaration.
        var newer = Contribution("x27", ["net10.0-ios27.2"], api: "public static class Kit { public static int A() => 1; public static int B() => 2; }",
            extra: [Declarations("net10.0-ios27.2", types: [("Kit", "type Kit.Kit"), ("Extra", "type Kit.Extra")],
                members: [("Kit.A()", "static func Kit.Kit.a()"), ("Kit.B()", "static func Kit.Kit.b()")])]);
        var merged = Merge([older, newer], "net10.0-ios26.0", "net10.0-ios27.2");
        Assert.True(merged.Problems.IsEmpty, string.Join("\n", merged.Problems));
        Assert.Contains(merged.Notes, n => n.Contains("Swift declarations Kit.swift-declarations.json: net10.0-ios26.0 -> net10.0-ios27.2, 2 names, 0 moved", StringComparison.Ordinal));

        var unrecorded = Contribution("y27", ["net10.0-ios27.2"]);
        var refused = Merge([older, unrecorded], "net10.0-ios26.0", "net10.0-ios27.2");
        Assert.Contains("buildTransitive/net10.0-ios27.2/ lacks Kit.swift-declarations.json", Assert.Single(refused.Problems));
        // Nor may the older copy be the one without it.
        Assert.Contains("buildTransitive/net10.0-ios26.0/ lacks Kit.swift-declarations.json",
            Assert.Single(Merge([Contribution("y26", ["net10.0-ios26.0"]), newer], "net10.0-ios26.0", "net10.0-ios27.2").Problems));

        // A record without its member map would compare as nothing moved.
        var incomplete = Contribution("z27", ["net10.0-ios27.2"], extra: [($"buildTransitive/net10.0-ios27.2/{SwiftDeclarationRecordFormat.FileName("Kit")}",
            Encoding.UTF8.GetBytes($"{{\"schema_version\": {SwiftDeclarationRecordFormat.SchemaVersion}, \"module\": \"Kit\", \"types\": {{}}}}"))]);
        Assert.Contains("is not a complete Swift declaration record", Assert.Single(Merge([older, incomplete], "net10.0-ios26.0", "net10.0-ios27.2").Problems));
    }

    [Fact]
    public void Merge_ARecordNoCopyCarries_IsRefused_AndOneFromAnotherGeneration_IsRefused()
    {
        // Each assembly names its module's record and the record's hash. A pack that loses the record
        // (`--no-build` after obj/ was cleaned, a hand-assembled package) loses it from every copy, which
        // the pairwise comparison cannot see: the module's names would merge unchecked.
        var record = Declarations("net10.0-ios26.0", types: [("Kit", "type Kit.Kit")], members: [("Kit.A()", "static func Kit.Kit.a()")]).Data;
        var api = Witnessed(record) + "public static class Kit { public static int A() => 1; }";
        (string, byte[]) Record(string tfm, byte[] data) => ($"buildTransitive/{tfm}/{SwiftDeclarationRecordFormat.FileName("Kit")}", data);

        var carried = Merge([Contribution("x26", ["net10.0-ios26.0"], api: api, extra: [Record("net10.0-ios26.0", record)]),
            Contribution("x27", ["net10.0-ios27.2"], api: api, extra: [Record("net10.0-ios27.2", record)])], "net10.0-ios26.0", "net10.0-ios27.2");
        Assert.True(carried.Problems.IsEmpty, string.Join("\n", carried.Problems));

        var lost = Merge([Contribution("y26", ["net10.0-ios26.0"], api: api), Contribution("y27", ["net10.0-ios27.2"], api: api)],
            "net10.0-ios26.0", "net10.0-ios27.2");
        Assert.Equal(2, lost.Problems.Length);
        Assert.All(lost.Problems, p => Assert.Contains("was generated with the Swift declaration record Kit.swift-declarations.json of the module Kit", p));
        Assert.Contains(lost.Problems, p => p.Contains("the copy net10.0-ios26.0 does not carry it", StringComparison.Ordinal));
        Assert.Null(lost.Package);

        // A record of another generation passes the pairwise comparison against itself, but describes
        // names the assembly may not declare.
        var other = Declarations("net10.0-ios26.0", types: [("Kit", "type Kit.Kit")], members: [("Kit.B()", "static func Kit.Kit.b()")]).Data;
        var stale = Merge([Contribution("z26", ["net10.0-ios26.0"], api: api, extra: [Record("net10.0-ios26.0", other)]),
            Contribution("z27", ["net10.0-ios27.2"], api: api, extra: [Record("net10.0-ios27.2", record)])], "net10.0-ios26.0", "net10.0-ios27.2");
        Assert.Contains("buildTransitive/net10.0-ios26.0/Kit.swift-declarations.json is not the Swift declaration record lib/net10.0-ios26.0/Acme.Kit.dll was generated with",
            Assert.Single(stale.Problems));
    }

    [Fact]
    public void Merge_AModuleWithAWrapperButNoRecordWitness_IsRefused()
    {
        // A generator that records no declarations names no record, so neither copy carries one and
        // nothing names it missing; the module's wrapper identity says a record is owed.
        string Api(string tfm) =>
            $"[assembly: System.Reflection.AssemblyMetadata(\"{SwiftWrapperIdentityFormat.AttributeKey}\", \"{new SwiftWrapperIdentity("Kit", "AcmeKitSwiftBindings", tfm, "26.2", Generator, SwiftWrapperIdentityFormat.SourceDigest("x")).Value}\")]\n"
            + "public static class Kit { public static int A() => 1; }";

        var result = Merge([Contribution("x26", ["net10.0-ios26.0"], api: Api("net10.0-ios26.0")),
            Contribution("x27", ["net10.0-ios27.2"], api: Api("net10.0-ios27.2"))], "net10.0-ios26.0", "net10.0-ios27.2");

        Assert.Contains(result.Problems, p => p.Contains("lib/net10.0-ios26.0/Acme.Kit.dll declares the wrapper of the Swift module Kit but names no Swift declaration record for it", StringComparison.Ordinal));
        Assert.Contains(result.Problems, p => p.Contains("lib/net10.0-ios27.2/Acme.Kit.dll declares the wrapper of the Swift module Kit but names no Swift declaration record for it", StringComparison.Ordinal));
        Assert.Null(result.Package);
    }

    private static string Witnessed(byte[] record) =>
        $"[assembly: System.Reflection.AssemblyMetadata(\"{SwiftDeclarationRecordFormat.AttributeKey}\", \"{SwiftDeclarationRecordFormat.Witness("Kit", SwiftDeclarationRecordFormat.FileName("Kit"), SwiftDeclarationRecordFormat.Hash(record))}\")]\n";

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
    // A newer build that adds an architecture names its slice for it.
    [InlineData("macos-arm64", "arm64", "macos-arm64_x86_64", "arm64,x86_64", null)]
    [InlineData("ios-arm64-simulator", "arm64", "ios-arm64_x86_64-simulator", "arm64,x86_64", null)]
    [InlineData("ios-arm64-maccatalyst", "arm64", "ios-arm64_x86_64-maccatalyst", "arm64,x86_64", null)]
    // One that drops an architecture still drops what the older copy's x86_64 consumers link.
    [InlineData("macos-arm64_x86_64", "arm64,x86_64", "macos-arm64", "arm64", "has no x86_64 slice")]
    [InlineData("ios-arm64_x86_64-simulator", "arm64,x86_64", "ios-arm64-simulator", "arm64", "has no x86_64 slice")]
    // A slice of another platform or variant is no counterpart.
    [InlineData("ios-arm64-simulator", "arm64", "ios-arm64", "arm64", "/Kit.xcframework's ios simulator slice (ios-arm64-simulator in the older copy)")]
    [InlineData("ios-arm64-maccatalyst", "arm64", "macos-arm64", "arm64", "/Kit.xcframework's ios maccatalyst slice (ios-arm64-maccatalyst in the older copy)")]
    public void Merge_ANewerXCFrameworkSliceOfOtherArchitectures_IsComparedAsTheSameSlice(string olderSlice, string olderArches, string newerSlice, string newerArches, string? refused)
    {
        if (!OperatingSystem.IsMacOS())
            return;
        const string source = "int kit_a(void){return 1;} int kit_b(void){return 2;}";
        var result = Merge([
            Contribution("x26", ["net10.0-ios26.0"], extra: [($"native/net10.0-ios26.0/Kit.xcframework/{olderSlice}/Kit.framework/Kit", Dylib("older", source, olderArches.Split(',')))]),
            Contribution("x27", ["net10.0-ios27.2"], extra: [($"native/net10.0-ios27.2/Kit.xcframework/{newerSlice}/Kit.framework/Kit", Dylib("newer", source, newerArches.Split(',')))]),
        ], "net10.0-ios26.0", "net10.0-ios27.2");
        if (refused is null)
        {
            Assert.True(result.Problems.IsEmpty, string.Join("\n", result.Problems));
            Assert.Contains(result.Notes, n => n.Contains($"exports Kit.xcframework/{newerSlice}/Kit.framework/Kit (arm64)", StringComparison.Ordinal) && n.Contains("(0 missing)", StringComparison.Ordinal));
        }
        else
            Assert.Contains(result.Problems, p => p.Contains(refused, StringComparison.Ordinal));
    }

    [Fact]
    public void Merge_ANewerXCFrameworkSliceOfOtherArchitectures_ThatLosesAnExport_IsRefused()
    {
        if (!OperatingSystem.IsMacOS())
            return;
        var result = Merge([
            Contribution("x26", ["net10.0-ios26.0"], extra: [("native/net10.0-ios26.0/Kit.xcframework/macos-arm64/Kit.framework/Kit", Dylib("older", "int kit_a(void){return 1;} int kit_b(void){return 2;}"))]),
            Contribution("x27", ["net10.0-ios27.2"], extra: [("native/net10.0-ios27.2/Kit.xcframework/macos-arm64_x86_64/Kit.framework/Kit", Dylib("newer", "int kit_a(void){return 1;}", "arm64", "x86_64"))]),
        ], "net10.0-ios26.0", "net10.0-ios27.2");
        var problem = Assert.Single(result.Problems);
        Assert.Contains("Kit.xcframework/macos-arm64_x86_64/Kit.framework/Kit (arm64) lacks 1 export(s)", problem);
        Assert.Contains("kit_b", problem);
    }

    [Fact]
    public void Merge_XCFrameworkSlices_AreMatchedByTheirInfoPlist()
    {
        // The Info.plist names each slice's platform and variant whatever its directory is called.
        if (!OperatingSystem.IsMacOS())
            return;
        static byte[] Plist(string id, string platform, string? variant) => Encoding.UTF8.GetBytes(
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?><plist version=\"1.0\"><dict><key>AvailableLibraries</key><array><dict>"
            + $"<key>LibraryIdentifier</key><string>{id}</string><key>LibraryPath</key><string>Kit.framework</string>"
            + $"<key>SupportedPlatform</key><string>{platform}</string>"
            + (variant is null ? "" : $"<key>SupportedPlatformVariant</key><string>{variant}</string>")
            + "</dict></array><key>CFBundlePackageType</key><string>XFWK</string></dict></plist>");
        const string source = "int kit_a(void){return 1;}";
        (string, byte[])[] Copy(string tfm, string id, string platform, string? variant, byte[] image) =>
            [($"native/{tfm}/Kit.xcframework/Info.plist", Plist(id, platform, variant)), ($"native/{tfm}/Kit.xcframework/{id}/Kit.framework/Kit", image)];
        var merged = Merge([
            Contribution("x26", ["net10.0-ios26.0"], extra: Copy("net10.0-ios26.0", "kit-sim", "ios", "simulator", Dylib("older", source))),
            Contribution("x27", ["net10.0-ios27.2"], extra: Copy("net10.0-ios27.2", "kit-sim-universal", "ios", "simulator", Dylib("newer", source, "arm64", "x86_64"))),
        ], "net10.0-ios26.0", "net10.0-ios27.2");
        Assert.True(merged.Problems.IsEmpty, string.Join("\n", merged.Problems));
        var refused = Merge([
            Contribution("x26", ["net10.0-ios26.0"], extra: Copy("net10.0-ios26.0", "kit-sim", "ios", "simulator", Dylib("older", source))),
            Contribution("x27", ["net10.0-ios27.2"], extra: Copy("net10.0-ios27.2", "kit-sim-universal", "ios", null, Dylib("newer", source, "arm64", "x86_64"))),
        ], "net10.0-ios26.0", "net10.0-ios27.2");
        Assert.Contains(refused.Problems, p => p.Contains("/Kit.xcframework's ios simulator slice (kit-sim in the older copy)", StringComparison.Ordinal));

        // A slice directory of the same name that the newer plist declares for another platform is not
        // the older slice's counterpart: the newer copy dropped its simulator slice.
        var relabelled = Merge([
            Contribution("x26", ["net10.0-ios26.0"], extra: Copy("net10.0-ios26.0", "kit", "ios", "simulator", Dylib("older", source))),
            Contribution("x27", ["net10.0-ios27.2"], extra: Copy("net10.0-ios27.2", "kit", "ios", null, Dylib("newer", source))),
        ], "net10.0-ios26.0", "net10.0-ios27.2");
        Assert.Contains(relabelled.Problems, p => p.Contains("/Kit.xcframework's ios simulator slice (kit in the older copy)", StringComparison.Ordinal));

        // A binary plist says the same as an XML one.
        (string, byte[])[] Binary((string Path, byte[] Data)[] copy) => copy.Select(e => e.Path.EndsWith("Info.plist", StringComparison.Ordinal) ? (e.Path, ToBinaryPlist(e.Data)) : e).ToArray();
        var binary = Merge([
            Contribution("x26", ["net10.0-ios26.0"], extra: Binary(Copy("net10.0-ios26.0", "kit-sim", "ios", "simulator", Dylib("older", source)))),
            Contribution("x27", ["net10.0-ios27.2"], extra: Binary(Copy("net10.0-ios27.2", "kit-sim-universal", "ios", "simulator", Dylib("newer", source, "arm64", "x86_64")))),
        ], "net10.0-ios26.0", "net10.0-ios27.2");
        Assert.True(binary.Problems.IsEmpty, string.Join("\n", binary.Problems));
    }

    private byte[] ToBinaryPlist(byte[] xml)
    {
        var path = Path.Combine(tempDir, $"plist-{Guid.NewGuid():N}.plist");
        File.WriteAllBytes(path, xml);
        var start = new ProcessStartInfo("plutil", ["-convert", "binary1", path]) { RedirectStandardError = true };
        using (var process = Process.Start(start)!)
        {
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.Equal(0, process.ExitCode);
        }
        var bytes = File.ReadAllBytes(path);
        Assert.True(bytes.AsSpan().StartsWith("bplist"u8));
        return bytes;
    }

    [Theory]
    [InlineData("arm64")]
    [InlineData("arm64,x86_64")]
    public void Merge_ANewerStaticArchiveThatLosesALinkableSymbol_IsRefused(string arches)
    {
        if (!OperatingSystem.IsMacOS())
            return;
        var architectures = arches.Split(',');
        const string binary = "Kit.xcframework/ios-arm64/Kit.framework/Kit";
        var older = StaticArchive("older", ["int kit_a(void){return 1;}", "int kit_b(void){return 2;} static int kit_private(void){return 3;}"], architectures);
        var lost = StaticArchive("lost", ["int kit_a(void){return 1;}", "int kit_c(void){return 3;}"], architectures);
        var grown = StaticArchive("grown", ["int kit_a(void){return 1;} int kit_b(void){return 2;}", "int kit_c(void){return 3;}"], architectures);

        var refused = Merge([
            Contribution("x26", ["net10.0-ios26.0"], extra: [($"native/net10.0-ios26.0/{binary}", older)]),
            Contribution("x27", ["net10.0-ios27.2"], extra: [($"native/net10.0-ios27.2/{binary}", lost)]),
        ], "net10.0-ios26.0", "net10.0-ios27.2");
        Assert.Equal(architectures.Length, refused.Problems.Length);
        Assert.All(refused.Problems, p => Assert.Contains("lacks 1 linkable symbol(s) of the older copy net10.0-ios26.0 (kit_b)", p));
        Assert.All(architectures, a => Assert.Contains(refused.Problems, p => p.Contains($"Kit.framework/Kit ({a})", StringComparison.Ordinal)));

        // Members may be split or joined differently: the archive's symbols are what is compared.
        var accepted = Merge([
            Contribution("x26", ["net10.0-ios26.0"], extra: [($"native/net10.0-ios26.0/{binary}", older)]),
            Contribution("x27", ["net10.0-ios27.2"], extra: [($"native/net10.0-ios27.2/{binary}", grown)]),
        ], "net10.0-ios26.0", "net10.0-ios27.2");
        Assert.True(accepted.Problems.IsEmpty, string.Join("\n", accepted.Problems));
        Assert.Contains(accepted.Notes, n => n.Contains($"archive symbols {binary} (arm64): net10.0-ios26.0 2 -> net10.0-ios27.2 3 (0 missing)", StringComparison.Ordinal));
    }

    [Fact]
    public void Merge_ABareStaticArchiveSliceThatLosesALinkableSymbol_IsRefused()
    {
        // A library XCFramework's slice is the archive itself (LibraryPath libKit.a).
        if (!OperatingSystem.IsMacOS())
            return;
        const string binary = "Kit.xcframework/ios-arm64/libKit.a";
        var result = Merge([
            Contribution("x26", ["net10.0-ios26.0"], extra: [($"native/net10.0-ios26.0/{binary}", StaticArchive("bare-older", ["int kit_a(void){return 1;}", "int kit_b(void){return 2;}"]))]),
            Contribution("x27", ["net10.0-ios27.2"], extra: [($"native/net10.0-ios27.2/{binary}", StaticArchive("bare-newer", ["int kit_a(void){return 1;}"]))]),
        ], "net10.0-ios26.0", "net10.0-ios27.2");
        var problem = Assert.Single(result.Problems);
        Assert.Contains($"{binary} (arm64) lacks 1 linkable symbol(s) of the older copy net10.0-ios26.0 (kit_b)", problem);
    }

    [Fact]
    public void Merge_AStaticArchiveWithBitcodeMembers_ComparesItsMachOMembers_AndFindsTheRestInItsTableOfContents()
    {
        // A link-time-optimized member is LLVM bitcode, which has no Mach-O symbol table; the archive's
        // table of contents still names what it defines, as the linker reads it.
        if (!OperatingSystem.IsMacOS())
            return;
        const string binary = "Kit.xcframework/ios-arm64/Kit.framework/Kit";
        const string lto = "//flto\nint kit_lto(void){return 9;}";
        var older = StaticArchive("lto-older", ["int kit_a(void){return 1;}", "int kit_b(void){return 2;}", lto]);
        // The same bitcode member, and an ordinary member that lost kit_b.
        var lost = StaticArchive("lto-lost", ["int kit_a(void){return 1;}", lto]);
        // kit_b moved into a bitcode member: the linker still finds it.
        var moved = StaticArchive("lto-moved", ["int kit_a(void){return 1;}", "//flto\nint kit_lto(void){return 9;} int kit_b(void){return 2;}"]);

        var refused = Merge([
            Contribution("x26", ["net10.0-ios26.0"], extra: [($"native/net10.0-ios26.0/{binary}", older)]),
            Contribution("x27", ["net10.0-ios27.2"], extra: [($"native/net10.0-ios27.2/{binary}", lost)]),
        ], "net10.0-ios26.0", "net10.0-ios27.2");
        var problem = Assert.Single(refused.Problems);
        Assert.Contains("lacks 1 linkable symbol(s) of the older copy net10.0-ios26.0 (kit_b)", problem);

        var accepted = Merge([
            Contribution("x26", ["net10.0-ios26.0"], extra: [($"native/net10.0-ios26.0/{binary}", older)]),
            Contribution("x27", ["net10.0-ios27.2"], extra: [($"native/net10.0-ios27.2/{binary}", moved)]),
        ], "net10.0-ios26.0", "net10.0-ios27.2");
        Assert.True(accepted.Problems.IsEmpty, string.Join("\n", accepted.Problems));
        Assert.Contains(accepted.Notes, n => n.Contains("the older copy's 1 member(s) that do not read as Mach-O objects are not required", StringComparison.Ordinal));
    }

    [Fact]
    public void Merge_AStaticArchiveOfUnreadableMembersAndNoTableOfContents_IsNotComparedButMerges()
    {
        const string binary = "Kit.xcframework/ios-arm64/Kit.framework/Kit";
        var bitcode = Ar(("kit.o", [0x42, 0x43, 0xC0, 0xDE, 0x35, 0x14, 0x00, 0x00, 0x05]));
        var result = Merge([
            Contribution("x26", ["net10.0-ios26.0"], extra: [($"native/net10.0-ios26.0/{binary}", bitcode)]),
            Contribution("x27", ["net10.0-ios27.2"], extra: [($"native/net10.0-ios27.2/{binary}", bitcode)]),
        ], "net10.0-ios26.0", "net10.0-ios27.2");
        Assert.True(result.Problems.IsEmpty, string.Join("\n", result.Problems));
        Assert.Contains(result.Notes, n => n.Contains("1 member(s) that do not read as Mach-O objects and no table of contents, linkable symbols not compared", StringComparison.Ordinal));
    }

    [Fact]
    public void Merge_AStaticArchiveThatDoesNotRead_IsRefused_NotThrown()
    {
        const string binary = "Kit.xcframework/ios-arm64/Kit.framework/Kit";
        var malformed = Ar(("#1/-1", [1, 2, 3, 4]));
        var result = Merge([
            Contribution("x26", ["net10.0-ios26.0"], extra: [($"native/net10.0-ios26.0/{binary}", malformed)]),
            Contribution("x27", ["net10.0-ios27.2"], extra: [($"native/net10.0-ios27.2/{binary}", malformed)]),
        ], "net10.0-ios26.0", "net10.0-ios27.2");
        Assert.Contains(result.Problems, p => p.Contains($"{binary} does not read as a static archive", StringComparison.Ordinal));
    }

    /// <summary>An XCFramework <c>Info.plist</c> listing one slice.</summary>
    private static byte[] SlicePlist(string id, string library, string platform, string? variant = null, params string[] architectures) => Encoding.UTF8.GetBytes(
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?><plist version=\"1.0\"><dict><key>AvailableLibraries</key><array><dict>"
        + $"<key>LibraryIdentifier</key><string>{id}</string><key>LibraryPath</key><string>{library}</string>"
        + "<key>SupportedArchitectures</key><array>" + string.Concat(architectures.Select(a => $"<string>{a}</string>")) + "</array>"
        + $"<key>SupportedPlatform</key><string>{platform}</string>"
        + (variant is null ? "" : $"<key>SupportedPlatformVariant</key><string>{variant}</string>")
        + "</dict></array><key>CFBundlePackageType</key><string>XFWK</string></dict></plist>");

    [Fact]
    public void Merge_AnXCFrameworkSlice_IsComparedByTheLibraryItsInfoPlistLinks()
    {
        if (!OperatingSystem.IsMacOS())
            return;
        const string root = "Kit.xcframework";
        var kit = Dylib("kit", "int kit_a(void){return 1;} int kit_b(void){return 2;}");
        // The newer slice still holds Kit.framework, but its plist links Other.framework: a build links
        // what Other exports, and the older copy's Kit exports are gone.
        var moved = Merge([
            Contribution("x26", ["net10.0-ios26.0"], extra: [($"native/net10.0-ios26.0/{root}/Info.plist", SlicePlist("ios-arm64", "Kit.framework", "ios", null, "arm64")),
                ($"native/net10.0-ios26.0/{root}/ios-arm64/Kit.framework/Kit", kit)]),
            Contribution("x27", ["net10.0-ios27.2"], extra: [($"native/net10.0-ios27.2/{root}/Info.plist", SlicePlist("ios-arm64", "Other.framework", "ios", null, "arm64")),
                ($"native/net10.0-ios27.2/{root}/ios-arm64/Kit.framework/Kit", kit),
                ($"native/net10.0-ios27.2/{root}/ios-arm64/Other.framework/Other", Dylib("other", "int other_a(void){return 1;}"))]),
        ], "net10.0-ios26.0", "net10.0-ios27.2");
        Assert.Contains(moved.Problems, p => p.Contains($"{root}/ios-arm64/Kit.framework/Kit: the newer copy's Info.plist links Other.framework from its slice", StringComparison.Ordinal));

        // A library beside the one the older plist links is not linked, so a newer copy may drop it.
        var stray = Merge([
            Contribution("x26", ["net10.0-ios26.0"], extra: [($"native/net10.0-ios26.0/{root}/Info.plist", SlicePlist("ios-arm64", "Kit.framework", "ios", null, "arm64")),
                ($"native/net10.0-ios26.0/{root}/ios-arm64/Kit.framework/Kit", kit),
                ($"native/net10.0-ios26.0/{root}/ios-arm64/Old.framework/Old", Dylib("old", "int old_a(void){return 1;}"))]),
            Contribution("x27", ["net10.0-ios27.2"], extra: [($"native/net10.0-ios27.2/{root}/Info.plist", SlicePlist("ios-arm64", "Kit.framework", "ios", null, "arm64")),
                ($"native/net10.0-ios27.2/{root}/ios-arm64/Kit.framework/Kit", kit)]),
        ], "net10.0-ios26.0", "net10.0-ios27.2");
        Assert.True(stray.Problems.IsEmpty, string.Join("\n", stray.Problems));
    }

    [Fact]
    public void Merge_ASliceWhoseLibraryPathIsNested_IsCompared()
    {
        if (!OperatingSystem.IsMacOS())
            return;
        const string root = "Kit.xcframework";
        (string, byte[])[] Copy(string tfm, byte[] image) =>
            [($"native/{tfm}/{root}/Info.plist", SlicePlist("ios-arm64", "Libraries/Kit.framework", "ios", null, "arm64")),
             ($"native/{tfm}/{root}/ios-arm64/Libraries/Kit.framework/Kit", image)];
        var result = Merge([
            Contribution("x26", ["net10.0-ios26.0"], extra: Copy("net10.0-ios26.0", Dylib("nested-older", "int kit_a(void){return 1;} int kit_b(void){return 2;}"))),
            Contribution("x27", ["net10.0-ios27.2"], extra: Copy("net10.0-ios27.2", Dylib("nested-newer", "int kit_a(void){return 1;}"))),
        ], "net10.0-ios26.0", "net10.0-ios27.2");
        Assert.Contains("kit_b", Assert.Single(result.Problems));
    }

    [Fact]
    public void Merge_ASliceWhoseInfoPlistDropsAnArchitecture_IsRefused_ThoughItsBinaryKeepsIt()
    {
        // A build for x86_64 chooses its slice by the plist's SupportedArchitectures.
        if (!OperatingSystem.IsMacOS())
            return;
        const string root = "Kit.xcframework";
        const string id = "ios-arm64_x86_64-simulator";
        var universal = Dylib("universal", "int kit_a(void){return 1;}", "arm64", "x86_64");
        (string, byte[])[] Copy(string tfm, params string[] architectures) =>
            [($"native/{tfm}/{root}/Info.plist", SlicePlist(id, "Kit.framework", "ios", "simulator", architectures)), ($"native/{tfm}/{root}/{id}/Kit.framework/Kit", universal)];
        var refused = Merge([
            Contribution("x26", ["net10.0-ios26.0"], extra: Copy("net10.0-ios26.0", "arm64", "x86_64")),
            Contribution("x27", ["net10.0-ios27.2"], extra: Copy("net10.0-ios27.2", "arm64")),
        ], "net10.0-ios26.0", "net10.0-ios27.2");
        var problem = Assert.Single(refused.Problems);
        Assert.Contains("the newer copy's Info.plist does not declare x86_64 for its slice", problem);
        var kept = Merge([
            Contribution("x26", ["net10.0-ios26.0"], extra: Copy("net10.0-ios26.0", "arm64", "x86_64")),
            Contribution("x27", ["net10.0-ios27.2"], extra: Copy("net10.0-ios27.2", "arm64", "x86_64")),
        ], "net10.0-ios26.0", "net10.0-ios27.2");
        Assert.True(kept.Problems.IsEmpty, string.Join("\n", kept.Problems));
    }

    [Fact]
    public void Merge_ASymbolThatMovesIntoBitcode_AndIsLostInALaterCopy_IsRefused()
    {
        // Each copy against the previous passes (the middle one's table of contents still names
        // kit_b); the newest must still define what the oldest copy's Mach-O members did.
        if (!OperatingSystem.IsMacOS())
            return;
        const string binary = "Kit.xcframework/ios-arm64/Kit.framework/Kit";
        var older = StaticArchive("three-older", ["int kit_a(void){return 1;}", "int kit_b(void){return 2;}"]);
        var moved = StaticArchive("three-moved", ["int kit_a(void){return 1;}", "//flto\nint kit_b(void){return 2;}"]);
        var lost = StaticArchive("three-lost", ["int kit_a(void){return 1;}", "//flto\nint kit_c(void){return 3;}"]);
        var result = Merge([
            Contribution("x26", ["net10.0-ios26.0"], extra: [($"native/net10.0-ios26.0/{binary}", older)]),
            Contribution("x27", ["net10.0-ios27.2"], extra: [($"native/net10.0-ios27.2/{binary}", moved)]),
            Contribution("n11", ["net11.0-ios27.0"], extra: [($"native/net11.0-ios27.0/{binary}", lost)]),
        ], "net10.0-ios26.0", "net10.0-ios27.2", "net11.0-ios27.0");
        var problem = Assert.Single(result.Problems);
        Assert.Contains("net11.0-ios27.0", problem);
        Assert.Contains("lacks 1 linkable symbol(s) of the older copy net10.0-ios26.0 (kit_b)", problem);
    }

    [Fact]
    public void Merge_AThinArchiveRebuiltAsBitcodeAlone_IsComparedByItsTableOfContents()
    {
        // No member of an archive built entirely with -flto reads as Mach-O, so it names no
        // architecture; its table of contents still says what it defines.
        if (!OperatingSystem.IsMacOS())
            return;
        const string binary = "Kit.xcframework/ios-arm64/Kit.framework/Kit";
        var older = StaticArchive("thin-older", ["int kit_a(void){return 1;}", "int kit_b(void){return 2;}"]);
        var kept = StaticArchive("thin-kept", ["//flto\nint kit_a(void){return 1;}", "//flto\nint kit_b(void){return 2;}"]);
        var lost = StaticArchive("thin-lost", ["//flto\nint kit_a(void){return 1;}"]);
        var accepted = Merge([
            Contribution("x26", ["net10.0-ios26.0"], extra: [($"native/net10.0-ios26.0/{binary}", older)]),
            Contribution("x27", ["net10.0-ios27.2"], extra: [($"native/net10.0-ios27.2/{binary}", kept)]),
        ], "net10.0-ios26.0", "net10.0-ios27.2");
        Assert.True(accepted.Problems.IsEmpty, string.Join("\n", accepted.Problems));
        var refused = Merge([
            Contribution("x26", ["net10.0-ios26.0"], extra: [($"native/net10.0-ios26.0/{binary}", older)]),
            Contribution("x27", ["net10.0-ios27.2"], extra: [($"native/net10.0-ios27.2/{binary}", lost)]),
        ], "net10.0-ios26.0", "net10.0-ios27.2");
        Assert.Contains("lacks 1 linkable symbol(s) of the older copy net10.0-ios26.0 (kit_b)", Assert.Single(refused.Problems));

        // A bitcode-only archive requires no symbol, whatever architectures the newer archive has.
        var grown = Merge([
            Contribution("x26", ["net10.0-ios26.0"], extra: [($"native/net10.0-ios26.0/{binary}", kept)]),
            Contribution("x27", ["net10.0-ios27.2"], extra: [($"native/net10.0-ios27.2/{binary}", StaticArchive("thin-grown", ["int kit_a(void){return 1;} int kit_b(void){return 2;}"], "arm64", "x86_64"))]),
        ], "net10.0-ios26.0", "net10.0-ios27.2");
        Assert.True(grown.Problems.IsEmpty, string.Join("\n", grown.Problems));
    }

    [Fact]
    public void Merge_AFatArchiveWhoseSliceLiesPastItsEnd_IsRefused_NotThrown()
    {
        const string binary = "Kit.xcframework/ios-arm64/Kit.framework/Kit";
        // A 64-bit fat header of two slices: the first an empty archive, the second at long.MaxValue.
        var fat = new byte[8 + 2 * 32 + 8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(fat, 0xCAFEBABF);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(fat.AsSpan(4), 2);
        void Slice(int i, uint cpu, ulong offset, ulong size)
        {
            var entry = fat.AsSpan(8 + i * 32);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(entry, cpu);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(entry[8..], offset);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(entry[16..], size);
        }
        Slice(0, 0x0100000C, 72, 8);
        Slice(1, 0x01000007, long.MaxValue, 8);
        "!<arch>\n"u8.CopyTo(fat.AsSpan(72));
        var result = Merge([
            Contribution("x26", ["net10.0-ios26.0"], extra: [($"native/net10.0-ios26.0/{binary}", fat)]),
            Contribution("x27", ["net10.0-ios27.2"], extra: [($"native/net10.0-ios27.2/{binary}", fat)]),
        ], "net10.0-ios26.0", "net10.0-ios27.2");
        Assert.Contains(result.Problems, p => p.Contains($"{binary} does not read as a static archive", StringComparison.Ordinal));
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

    [Theory]
    // A member signature: the newer copy's F returns its own System.Uri, not the framework's.
    [InlineData("public class Kit { public static System.Uri F() => null; }",
        "namespace System { public class Uri { } } public class Kit { public static System.Uri F() => null; }", "Kit::F")]
    // A field's type.
    [InlineData("public class Kit { public System.Uri U; }",
        "namespace System { public class Uri { } } public class Kit { public System.Uri U; }", "Kit::U")]
    // A base: the newer Kit derives from a System.Exception of its own.
    [InlineData("public class Kit : System.Exception { }",
        "namespace System { public class Exception { } } public class Kit : System.Exception { }", "Kit : ")]
    // An interface.
    [InlineData("public class Kit : System.IDisposable { public void Dispose() { } }",
        "namespace System { public interface IDisposable { void Dispose(); } } public class Kit : System.IDisposable { public void Dispose() { } }", "Kit : ")]
    public void Merge_ATypeOfTheSameNameFromAnotherAssembly_IsAnotherType(string olderApi, string newerApi, string key)
    {
        // A library compiled against the older copy binds to the type in the assembly the older copy
        // names, so the newer copy's same-named type of another assembly does not keep that API.
        var older = Contribution("x26", ["net10.0-ios26.0"], api: olderApi);
        var newer = Contribution("x27", ["net10.0-ios27.2"], api: newerApi);
        var result = Merge([older, newer], "net10.0-ios26.0", "net10.0-ios27.2");
        Assert.Contains(result.Problems, p => p.Contains("lacks", StringComparison.Ordinal) && p.Contains(key, StringComparison.Ordinal));
        // The same API from the same assembly is still the same API.
        Assert.Empty(Merge([Contribution("x26b", ["net10.0-ios26.0"], api: olderApi), Contribution("x27b", ["net10.0-ios27.2"], api: olderApi)],
            "net10.0-ios26.0", "net10.0-ios27.2").Problems);
    }

    [Theory]
    // An enum member's value: a library compiled against the older copy compiled `Mode.B` as 1.
    [InlineData("public enum Mode : uint { A = 0, B = 1 }", "public enum Mode : uint { A = 0, Inserted = 1, B = 2 }", "Mode::B", "1", "2")]
    // A nested generated discriminator, as a case-tag enum is emitted.
    [InlineData("public class Kit { public enum CaseTag : uint { X = 0, Y = 1 } }", "public class Kit { public enum CaseTag : uint { Y = 0, X = 1 } }", "Kit+CaseTag::X", "0", "1")]
    // A const field of a class.
    [InlineData("public static class Kit { public const long Flag = 65536; }", "public static class Kit { public const long Flag = 1048576; }", "Kit::Flag", "65536", "1048576")]
    // A string constant.
    [InlineData("public static class Kit { public const string Name = \"a\"; }", "public static class Kit { public const string Name = \"b\"; }", "Kit::Name", "\"a\"", "\"b\"")]
    // A parameter's default value, which the caller compiles in.
    [InlineData("public static class Kit { public static int F(int x = 5) => x; }", "public static class Kit { public static int F(int x = 6) => x; }", "Kit::F`0(Int32):Int32 parameter 1", "5", "6")]
    // A parameter's position, not its name, is what the caller binds, so a renamed parameter keeps the value's identity.
    [InlineData("public static class Kit { public static int F(int x = 5) => x; }", "public static class Kit { public static int F(int y = 6) => y; }", "Kit::F`0(Int32):Int32 parameter 1", "5", "6")]
    // A decimal constant and a decimal default, which C# records in an attribute rather than a metadata constant.
    [InlineData("public static class Kit { public const decimal Rate = 1.5m; }", "public static class Kit { public const decimal Rate = 2.5m; }", "Kit::Rate", "1.5m", "2.5m")]
    [InlineData("public static class Kit { public static decimal F(decimal r = 1.5m) => r; }", "public static class Kit { public static decimal F(decimal r = 2.5m) => r; }",
        // (The key's type names carry the reference assembly the test compiles against, so match its end.)
        "System.Decimal parameter 1", "1.5m", "2.5m")]
    // A DateTime default, likewise an attribute.
    [InlineData("public static class Kit { public static long F([System.Runtime.InteropServices.Optional, System.Runtime.CompilerServices.DateTimeConstant(5)] System.DateTime d) => d.Ticks; }",
        "public static class Kit { public static long F([System.Runtime.InteropServices.Optional, System.Runtime.CompilerServices.DateTimeConstant(6)] System.DateTime d) => d.Ticks; }",
        "System.DateTime):Int64 parameter 1", "0001-01-01T00:00:00.0000005", "0001-01-01T00:00:00.0000006")]
    // A decimal default of a nullable or object parameter is the same attribute.
    [InlineData("public static class Kit { public static decimal? F(decimal? r = 1m) => r; }", "public static class Kit { public static decimal? F(decimal? r = 2m) => r; }",
        "parameter 1", "1m", "2m")]
    [InlineData("public static class Kit { public static object F([System.Runtime.InteropServices.Optional, System.Runtime.CompilerServices.DecimalConstant(0, 0, 0u, 0u, 1u)] object r) => r; }",
        "public static class Kit { public static object F([System.Runtime.InteropServices.Optional, System.Runtime.CompilerServices.DecimalConstant(0, 0, 0u, 0u, 2u)] object r) => r; }",
        "Kit::F`0(Object):Object parameter 1", "1m", "2m")]
    // A decimal zero whose sign changes: the text of both is 0.0, the bits differ.
    [InlineData("public static class Kit { public const decimal Z = 0.0m; }", "public static class Kit { public const decimal Z = -0.0m; }", "Kit::Z", "0.0m", "-0.0m")]
    // A boxed default of an object parameter is a value of its own type: Int32 1 and Int64 1 are different objects.
    [InlineData("public static class Kit { public static object F([System.Runtime.InteropServices.Optional, System.Runtime.InteropServices.DefaultParameterValue(1)] object o) => o; }",
        "public static class Kit { public static object F([System.Runtime.InteropServices.Optional, System.Runtime.InteropServices.DefaultParameterValue(1L)] object o) => o; }",
        "Kit::F`0(Object):Object parameter 1", "1 (Int32)", "1 (Int64)")]
    // A DateTime field constant out of DateTime's range is still a value, not a failed merge.
    [InlineData("public static class Kit { [System.Runtime.CompilerServices.DateTimeConstant(-1)] public static readonly System.DateTime D; }",
        "public static class Kit { [System.Runtime.CompilerServices.DateTimeConstant(-2)] public static readonly System.DateTime D; }",
        "Kit::D", "DateTime(ticks -1)", "DateTime(ticks -2)")]
    public void Merge_ANewerCopyThatChangesACompiledInValue_IsRefused_NamingTheMember(string olderApi, string newerApi, string member, string before, string after)
    {
        // A literal is copied into the assembly that uses it: a library compiled against the older copy
        // keeps the older value when the app runs the newer copy, so a changed value is a different API
        // even though the member's name and signature are the same.
        var older = Contribution("x26", ["net10.0-ios26.0"], api: olderApi);
        var newer = Contribution("x27", ["net10.0-ios27.2"], api: newerApi);
        var result = Merge([older, newer], "net10.0-ios26.0", "net10.0-ios27.2");
        Assert.Null(result.Package);
        var problem = Assert.Single(result.Problems, p => p.Contains("compiled-in value", StringComparison.Ordinal));
        Assert.Contains($"{member} is {before} in net10.0-ios26.0 and {after} in net10.0-ios27.2", problem);
    }

    [Theory]
    // A class sealed: a library's class deriving from it no longer loads.
    [InlineData("public class Kit { public int A() => 1; }", "public sealed class Kit { public int A() => 1; }", "Kit can no longer be derived from")]
    // A virtual method made non-virtual, or a override sealed: a library's override no longer loads.
    [InlineData("public class Kit { public virtual int A() => 1; }", "public class Kit { public int A() => 1; }", "A`0():Int32 of .Kit can no longer be overridden")]
    [InlineData("public class Base { public virtual int A() => 1; } public class Kit : Base { public override int A() => 2; }",
        "public class Base { public virtual int A() => 1; } public class Kit : Base { public sealed override int A() => 2; }", "A`0():Int32 of .Kit can no longer be overridden")]
    [InlineData("public class Kit { protected virtual int A() => 1; }", "public class Kit { protected int A() => 1; }", "A`0():Int32 of .Kit can no longer be overridden")]
    // One a class a library may derive from inherits from a base no library may derive from.
    [InlineData("public class Base { internal Base() { } public virtual int A() => 1; } public class Kit : Base { }",
        "public class Base { internal Base() { } public int A() => 1; } public class Kit : Base { }", "A`0():Int32 of .Kit can no longer be overridden")]
    // A new virtual method that hides it: a library's override binds to the new slot, and a call through Base no longer reaches it.
    [InlineData("public class Base { internal Base() { } public virtual int A() => 1; } public class Kit : Base { }",
        "public class Base { internal Base() { } public int A() => 1; } public class Kit : Base { public new virtual int A() => 2; }", ".Base::A`0():Int32 of .Kit can no longer be overridden")]
    // A class made abstract: a library's `new Kit()` no longer runs.
    [InlineData("public class Kit { public int A() => 1; }", "public abstract class Kit { public int A() => 1; }", "Kit can no longer be constructed")]
    // An abstract method added to a class a library may derive from: the library's class does not implement it.
    [InlineData("public abstract class Kit { public int A() => 1; }", "public abstract class Kit { public int A() => 1; public abstract int B(); }", "leaves .Kit with the abstract method B`0():Int32 to implement")]
    [InlineData("public abstract class Kit { protected Kit() { } }", "public abstract class Kit { protected Kit() { } protected abstract void B(); }", "leaves .Kit with the abstract method B`0():Void to implement")]
    // One no library can implement is no less an obligation.
    [InlineData("public abstract class Kit { }", "public abstract class Kit { private protected abstract void B(); }", "leaves .Kit with the abstract method B`0():Void to implement")]
    [InlineData("public abstract class Kit { }", "public abstract class Kit { internal abstract void B(); }", "leaves .Kit with the abstract method B`0():Void to implement")]
    // One added to a base no library may derive from, which a class it may derive from inherits.
    [InlineData("public abstract class Base { internal Base() { } } public abstract class Kit : Base { protected Kit() { } }",
        "public abstract class Base { internal Base() { } public abstract int B(); } public abstract class Kit : Base { protected Kit() { } }", "leaves .Kit with the abstract method B`0():Int32 to implement")]
    [InlineData("public abstract class Base<T> { internal Base() { } } public abstract class Kit : Base<int> { protected Kit() { } }",
        "public abstract class Base<T> { internal Base() { } public abstract T B(); } public abstract class Kit : Base<int> { protected Kit() { } }", "leaves .Kit with the abstract method B`0():Int32 to implement")]
    // A constraint added to a generic method or type: a call or instantiation the older copy allowed no longer loads.
    [InlineData("public static class Kit { public static int A<T>(T t) => 1; }", "public static class Kit { public static int A<T>(T t) where T : class => 1; }",
        "adds the constraint method M static .Kit::A`1(!!0):Int32 parameter 0 ReferenceTypeConstraint")]
    [InlineData("public static class Kit { public static int A<T>(T t) => 1; }", "public static class Kit { public static int A<T>(T t) where T : new() => 1; }",
        "adds the constraint method M static .Kit::A`1(!!0):Int32 parameter 0 DefaultConstructorConstraint")]
    [InlineData("public class Box<T> { }", "public class Box<T> where T : System.IDisposable { }", "adds the constraint type .Box`1 parameter 0 : ")]
    [InlineData("public class Box<T> where T : System.IDisposable { }", "public class Box<T> where T : System.IDisposable, System.IComparable { }", "adds the constraint type .Box`1 parameter 0 : ")]
    public void Merge_ANewerCopyThatNarrowsWhatALibraryMayDeriveOverrideOrPass_IsRefused(string olderApi, string newerApi, string narrowed)
    {
        var older = Contribution("x26", ["net10.0-ios26.0"], api: olderApi);
        var newer = Contribution("x27", ["net10.0-ios27.2"], api: newerApi);
        var result = Merge([older, newer], "net10.0-ios26.0", "net10.0-ios27.2");
        Assert.Null(result.Package);
        var problem = Assert.Single(result.Problems, p => p.Contains("inheritance or constraint contract", StringComparison.Ordinal));
        Assert.Contains(narrowed, problem);
    }

    [Theory]
    // The reverse of each: unsealing, a method made virtual, a class made concrete, a constraint dropped.
    [InlineData("public sealed class Kit { public int A() => 1; }", "public class Kit { public virtual int A() => 1; }")]
    [InlineData("public abstract class Kit { public abstract int A(); }", "public class Kit { public virtual int A() => 1; }")]
    [InlineData("public static class Kit { public static int A<T>(T t) where T : class => 1; }", "public static class Kit { public static int A<T>(T t) => 1; }")]
    [InlineData("public class Box<T> where T : System.IDisposable, System.IComparable { }", "public class Box<T> where T : System.IDisposable { }")]
    // A constraint loosened to a type the older constraint converts to.
    [InlineData("public interface IParent { } public interface IChild : IParent { } public class Box<T> where T : IChild { }",
        "public interface IParent { } public interface IChild : IParent { } public class Box<T> where T : IParent { }")]
    [InlineData("public class Animal { } public class Dog : Animal { } public static class Kit { public static int A<T>(T t) where T : Dog => 1; }",
        "public class Animal { } public class Dog : Animal { } public static class Kit { public static int A<T>(T t) where T : Animal => 1; }")]
    [InlineData("public interface IParent<T> { } public interface IChild<T> : IParent<T> { } public class Box<T> where T : IChild<int> { }",
        "public interface IParent<T> { } public interface IChild<T> : IParent<T> { } public class Box<T> where T : IParent<int> { }")]
    [InlineData("public interface IParent<T, U> { } public interface IChild<T, U> : IParent<T, U> { } public class Box<T> where T : IChild<int[,], long> { }",
        "public interface IParent<T, U> { } public interface IChild<T, U> : IParent<T, U> { } public class Box<T> where T : IParent<int[,], long> { }")]
    // An override added below the base that introduces the slot keeps it overridable.
    [InlineData("public class Base { internal Base() { } public virtual int A() => 1; } public class Kit : Base { }",
        "public class Base { internal Base() { } public virtual int A() => 1; } public class Kit : Base { public override int A() => 2; }")]
    // A constrained generic member or type the older copy does not have: no library calls it.
    [InlineData("public static class Kit { public static int A() => 1; }", "public static class Kit { public static int A() => 1; public static int B<T>(T t) where T : class => 1; }")]
    [InlineData("public static class Kit { public static int A() => 1; }", "public static class Kit { public static int A() => 1; } public class Box<T> where T : System.IDisposable { }")]
    // An abstract method added to a base that the class a library derives from implements.
    [InlineData("public abstract class Base { internal Base() { } } public abstract class Kit : Base { protected Kit() { } }",
        "public abstract class Base { internal Base() { } public abstract int B(); } public abstract class Kit : Base { protected Kit() { } public override int B() => 1; }")]
    [InlineData("public abstract class Base<T> { internal Base() { } } public abstract class Kit : Base<int> { protected Kit() { } }",
        "public abstract class Base<T> { internal Base() { } public abstract T B(); } public abstract class Kit : Base<int> { protected Kit() { } public override int B() => 1; }")]
    [InlineData("public abstract class Base { internal Base() { } } public abstract class Kit : Base { protected Kit() { } }",
        "public abstract class Base { internal Base() { } public abstract Base B(); } public abstract class Kit : Base { protected Kit() { } public override Kit B() => this; }")]
    [InlineData("public abstract class Base<T> { internal Base() { } } public abstract class Kit : Base<int> { protected Kit() { } }",
        "public abstract class Base<T> { internal Base() { } public abstract object B(T t); } public abstract class Kit : Base<int> { protected Kit() { } public override string B(int t) => \"\"; }")]
    // A class no library can derive from (no constructor it can call) may gain an abstract method or seal its methods.
    [InlineData("public abstract class Kit { internal Kit() { } public virtual int A() => 1; }", "public abstract class Kit { internal Kit() { } public int A() => 1; public abstract int B(); }")]
    // A static class, a struct and an interface's own members are not derivation contracts.
    [InlineData("public static class Kit { public static int A() => 1; }", "public static class Kit { public static int A() => 1; }")]
    [InlineData("public struct Kit { public int A() => 1; }", "public struct Kit { public int A() => 1; public override string ToString() => \"\"; }")]
    // A protected member made public is still reachable from every subclass.
    [InlineData("public class Kit { protected virtual int A() => 1; }", "public class Kit { public virtual int A() => 1; }")]
    public void Merge_ANewerCopyThatKeepsOrWidensWhatALibraryMayDeriveOverrideOrPass_IsAccepted(string olderApi, string newerApi)
    {
        var older = Contribution("x26", ["net10.0-ios26.0"], api: olderApi);
        var newer = Contribution("x27", ["net10.0-ios27.2"], api: newerApi);
        var result = Merge([older, newer], "net10.0-ios26.0", "net10.0-ios27.2");
        Assert.True(result.Problems.IsEmpty, string.Join("\n", result.Problems));
    }

    [Fact]
    public void Merge_ANewerCopyThatNarrowsAMembersAccess_IsRefused()
    {
        // A public member made protected (or internal) is gone for a library that calls it from outside a subclass.
        var older = Contribution("x26", ["net10.0-ios26.0"], api: "public class Kit { public int A() => 1; }");
        var newer = Contribution("x27", ["net10.0-ios27.2"], api: "public class Kit { protected int A() => 1; }");
        var problem = Assert.Single(Merge([older, newer], "net10.0-ios26.0", "net10.0-ios27.2").Problems);
        Assert.Contains("lacks 1 public API member(s)", problem);
        Assert.Contains("public M instance .Kit::A`0():Int32", problem);
    }

    [Theory]
    // A default the newer copy drops: the signature is the same, but the older library's call site still passes 5.
    [InlineData("public static class Kit { public static int F(int x = 5) => x; }", "public static class Kit { public static int F(int x) => x; }", "Kit::F`0(Int32):Int32 parameter 1", "5")]
    // A constant the newer copy turns into a field of the same type: the field's signature is the same, but
    // a library compiled against the older copy holds the constant's value and never reads the field.
    [InlineData("public static class Kit { public const long Flag = 1; }", "public static class Kit { public static readonly long Flag = 1; }", "Kit::Flag", "1")]
    public void Merge_ANewerCopyThatDropsACompiledInValue_IsRefused_NamingTheMember(string olderApi, string newerApi, string member, string before)
    {
        var older = Contribution("x26", ["net10.0-ios26.0"], api: olderApi);
        var newer = Contribution("x27", ["net10.0-ios27.2"], api: newerApi);
        var result = Merge([older, newer], "net10.0-ios26.0", "net10.0-ios27.2");
        Assert.Null(result.Package);
        var problem = Assert.Single(result.Problems, p => p.Contains("compiled-in value", StringComparison.Ordinal));
        Assert.Contains($"{member} is {before} in net10.0-ios26.0 and has no compiled-in value in net10.0-ios27.2", problem);
    }

    [Theory]
    [InlineData("public enum Mode : uint { A = 0, B = 1 }", "public enum Mode : uint { A = 0, B = 1, Added = 2 }")]
    [InlineData("public static class Kit { public const long Flag = 65536; public static int F(int x = 5) => x; }",
        "public static class Kit { public const long Flag = 65536; public const long Other = 2; public static int F(int x = 5) => x; public static int G(int y = 9) => y; }")]
    // Two encodings of one DateTime default pass the same value.
    [InlineData("public static class Kit { public static long F(System.DateTime d = default) => d.Ticks; }",
        "public static class Kit { public static long F([System.Runtime.InteropServices.Optional, System.Runtime.CompilerServices.DateTimeConstant(0)] System.DateTime d) => d.Ticks; }")]
    // A constant attribute on a field of another type is not a constant: compilers read the field.
    [InlineData("public static class Kit { [System.Runtime.CompilerServices.DateTimeConstant(5)] public static readonly int X = 30; }",
        "public static class Kit { [System.Runtime.CompilerServices.DateTimeConstant(6)] public static readonly int X = 30; }")]
    [InlineData("public static class Kit { [System.Runtime.CompilerServices.DecimalConstant(0, 0, 0u, 0u, 1u)] public static readonly object X = 30; }",
        "public static class Kit { [System.Runtime.CompilerServices.DecimalConstant(0, 0, 0u, 0u, 2u)] public static readonly object X = 30; }")]
    public void Merge_ANewerCopyThatKeepsEveryCompiledInValue_AndAddsNew_IsAccepted(string olderApi, string newerApi)
    {
        var older = Contribution("x26", ["net10.0-ios26.0"], api: olderApi);
        var newer = Contribution("x27", ["net10.0-ios27.2"], api: newerApi);
        Assert.Empty(Merge([older, newer], "net10.0-ios26.0", "net10.0-ios27.2").Problems);
    }

    [Fact]
    public void Merge_MoreThanTenChangedValues_TheRefusalNamesTen_AndTheLogNamesEvery()
    {
        var names = Enumerable.Range(0, 12).Select(i => ((char)('A' + i)).ToString()).ToList();
        var older = Contribution("x26", ["net10.0-ios26.0"], api: $"public enum Mode : uint {{ {string.Join(", ", names.Select((n, i) => $"{n} = {i}"))} }}");
        var newer = Contribution("x27", ["net10.0-ios27.2"], api: $"public enum Mode : uint {{ {string.Join(", ", names.Select((n, i) => $"{n} = {i + 1}"))} }}");
        var result = Merge([older, newer], "net10.0-ios26.0", "net10.0-ios27.2");
        var problem = Assert.Single(result.Problems);
        Assert.Contains("12 compiled-in value(s)", problem);
        Assert.Contains("Mode::J is 9 in net10.0-ios26.0 and 10 in net10.0-ios27.2; …", problem);
        Assert.DoesNotContain("Mode::K", problem);
        var logged = result.Notes.Where(n => n.Contains("changed value", StringComparison.Ordinal)).ToList();
        Assert.Equal(12, logged.Count);
        Assert.Contains(logged, n => n.Contains("Mode::L is 11 in net10.0-ios26.0 and 12 in net10.0-ios27.2", StringComparison.Ordinal));
    }

    [Fact]
    public void Merge_AnEntryPastTheWindowsPathBudget_IsAWarning_AndARefusalWhenRequired()
    {
        var deep = $"native/net10.0-ios26.0/Kit.xcframework/{new string('a', 200)}/Info.plist";
        var result = Merge([Contribution("x26", ["net10.0-ios26.0"], extra: [(deep, [1])])], "net10.0-ios26.0");
        Assert.True(result.Problems.IsEmpty, string.Join("\n", result.Problems));
        Assert.NotNull(result.Package);
        var warning = Assert.Single(result.PathBudget);
        Assert.Contains("Windows restore budget", warning);
        Assert.Contains("fails for every user profile", warning);
        Assert.Contains("SwiftBindingsMergeRequireWindowsPathBudget=true", warning);

        var required = SwiftBindingPackageMerge.Merge([Contribution("x26r", ["net10.0-ios26.0"], extra: [(deep, [1])])], ["net10.0-ios26.0"],
            Path.Combine(tempDir, "out-required"), requireWindowsPathBudget: true);
        Assert.Null(required.Package);
        Assert.Empty(required.Problems);
        Assert.Contains("Windows restore budget", Assert.Single(required.PathBudget));
    }

    [Fact]
    public void Merge_TheLongestAppleFrameworkId_Merges_AndNamesTheUserProfilesAVisualStudioRestoreFits()
    {
        // The longest id of an Apple framework binding, with a wrapper binary at the depth the copy layout
        // puts it (the same depth in a one-copy package).
        const string id = "SwiftBindings.Apple.LiveCommunicationKit";
        Assert.Equal(40, id.Length);
        const string wrapper = "LiveCommunicationKitSwiftBindings";
        var binary = $"native/net10.0-ios27.2/{wrapper}.xcframework/ios-arm64_x86_64-simulator/{wrapper}.framework/{wrapper}";
        var result = Merge([Contribution("x26", ["net10.0-ios26.0"], id: id, version: "27.2.0"),
            Contribution("x27", ["net10.0-ios27.2"], id: id, version: "27.2.0", extra: [(binary, [1])])], "net10.0-ios26.0", "net10.0-ios27.2");
        Assert.True(result.Problems.IsEmpty, string.Join("\n", result.Problems));
        Assert.NotNull(result.Package);
        var budget = SwiftBindingPackageMerge.WindowsRestoreEntryBudget($"{id}.27.2.0".Length, 40);
        Assert.Equal(145, budget);
        var warning = Assert.Single(result.PathBudget);
        Assert.Contains($"{binary} is {binary.Length} characters, past the Windows restore budget of 145", warning);
        Assert.Contains($"longer than {40 - (binary.Length - budget)} characters", warning);
    }

    // ---- fixtures -------------------------------------------------------------------------------

    private static (string Path, byte[] Data) Declarations(string tfm, (string Name, string Swift)[] types, (string Name, string Swift)[] members) =>
        ($"buildTransitive/{tfm}/{SwiftDeclarationRecordFormat.FileName("Kit")}", Encoding.UTF8.GetBytes(SwiftDeclarationMapEmitter.Serialize(new SwiftDeclarationMapDocument
        {
            SchemaVersion = SwiftDeclarationRecordFormat.SchemaVersion,
            Module = "Kit",
            Types = new(types.ToDictionary(t => t.Name, t => t.Swift), StringComparer.Ordinal),
            Members = new(members.ToDictionary(m => m.Name, m => m.Swift), StringComparer.Ordinal),
        })));

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
        Func<string, string>? declares = null, bool duplicateGroup = false, bool noDependencies = false,
        string payload = PackageCopyDeclarationEmitter.NativePayloadNative, string? declaredPayload = null, bool? declaresTargets = null)
    {
        var assembly = SwiftTransitionManifestTests.Compile("Acme.Kit", api);
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var groups = new StringBuilder();
        // A Swift or mixed copy: natives under native/<tfm>/, which its targets reference. A pure
        // Objective-C copy of an Apple framework: targets with no natives. A pure Objective-C copy of a
        // library: natives in lib/<tfm>/ as the binding's resource package, no consumer targets.
        var shipsTargets = targetsFor is not null || payload != PackageCopyDeclarationEmitter.NativePayloadResources;
        foreach (var tfm in tfms)
        {
            files[$"lib/{tfm}/Acme.Kit.dll"] = assembly;
            if (shipsTargets)
                files[$"buildTransitive/{tfm}/{id}.targets"] = Encoding.UTF8.GetBytes(targetsFor?.Invoke(tfm)
                    ?? (payload == PackageCopyDeclarationEmitter.NativePayloadNative ? Targets(tfm) : "<Project />"));
            files[$"buildTransitive/{tfm}/{id}.props"] = Encoding.UTF8.GetBytes(
                PackageCopyDeclarationEmitter.Compose(id, declares?.Invoke(tfm) ?? tfm, $"toolchain of {name}", "26.2", generator,
                    declaredPayload ?? payload, declaresTargets ?? shipsTargets));
            if (payload == PackageCopyDeclarationEmitter.NativePayloadNative)
                files[$"native/{tfm}/Kit.xcframework/Info.plist"] = Encoding.UTF8.GetBytes("<plist/>");
            if (payload == PackageCopyDeclarationEmitter.NativePayloadResources && !(extra ?? []).Any(e => e.Path.StartsWith($"lib/{tfm}/Acme.Kit.resources", StringComparison.Ordinal)))
                files[$"lib/{tfm}/Acme.Kit.resources.zip"] = Zip(("Kit.xcframework/Info.plist", Encoding.UTF8.GetBytes("<plist/>")));
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

    /// <summary>A zip archive of <paramref name="entries"/>, as a binding project's resource package.</summary>
    private static byte[] Zip(params (string Path, byte[] Data)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (path, data) in entries)
            {
                using var stream = zip.CreateEntry(path).Open();
                stream.Write(data);
            }
        return buffer.ToArray();
    }

    /// <summary>A dylib exporting the C functions of <paramref name="source"/>, for <paramref name="architectures"/> (arm64 when none).</summary>
    private byte[] Dylib(string name, string source, params string[] architectures)
    {
        var c = Path.Combine(tempDir, name + ".c");
        var output = Path.Combine(tempDir, name + ".dylib");
        File.WriteAllText(c, source);
        Xcrun(["clang", .. Arches(architectures), "-dynamiclib", c, "-o", output]);
        return File.ReadAllBytes(output);
    }

    /// <summary>
    /// A static archive as <c>libtool -static</c> writes one, of one object per entry of
    /// <paramref name="members"/> (each compiled for <paramref name="architectures"/>, arm64 when none):
    /// fat when there is more than one architecture.
    /// </summary>
    private byte[] StaticArchive(string name, string[] members, params string[] architectures)
    {
        var objects = new List<string>();
        for (var i = 0; i < members.Length; i++)
        {
            // A long member name, which the archive stores in the BSD #1/<length> form.
            var c = Path.Combine(tempDir, $"{name}_member_with_a_long_object_name_{i}.c");
            File.WriteAllText(c, members[i]);
            var o = Path.ChangeExtension(c, ".o");
            // A member whose source starts //flto is compiled to LLVM bitcode.
            Xcrun(["clang", .. Arches(architectures), .. members[i].StartsWith("//flto", StringComparison.Ordinal) ? ["-flto"] : Array.Empty<string>(), "-c", c, "-o", o]);
            objects.Add(o);
        }
        var output = Path.Combine(tempDir, $"lib{name}.a");
        Xcrun(["libtool", "-static", "-o", output, .. objects]);
        return File.ReadAllBytes(output);
    }

    private static string[] Arches(string[] architectures) =>
        (architectures.Length == 0 ? ["arm64"] : architectures).SelectMany(a => new[] { "-arch", a }).ToArray();

    private static void Xcrun(string[] arguments)
    {
        var start = new ProcessStartInfo("xcrun", arguments)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
    }

    /// <summary>A thin <c>ar</c> archive of <paramref name="members"/>, written by hand (short names, even padding).</summary>
    private static byte[] Ar(params (string Name, byte[] Data)[] members)
    {
        var buffer = new MemoryStream();
        buffer.Write("!<arch>\n"u8);
        foreach (var (name, data) in members)
        {
            buffer.Write(Encoding.ASCII.GetBytes($"{name,-16}{0,-12}{0,-6}{0,-6}{644,-8}{data.Length,-10}`\n"));
            buffer.Write(data);
            if (data.Length % 2 == 1)
                buffer.WriteByte((byte)'\n');
        }
        return buffer.ToArray();
    }

    /// <summary>The Mac Catalyst slice of <c>Kit.xcframework</c> that <see cref="VersionedFrameworkZip"/> lays out.</summary>
    private const string CatalystFramework = "Kit.xcframework/ios-arm64_x86_64-maccatalyst/Kit.framework/";

    /// <summary>
    /// A resource package holding a versioned (Mac Catalyst) framework whose binary is
    /// <paramref name="image"/>, zipped by the system zip tool: with <c>-y</c>, as a binding project
    /// zips it, <c>Versions/Current</c>, the root binary and <c>Resources</c> are stored as symbolic
    /// links; without, every link is stored as the file or folder it names.
    /// </summary>
    private byte[] VersionedFrameworkZip(byte[] image, bool links)
    {
        var dir = Path.Combine(tempDir, "framework-" + Guid.NewGuid().ToString("N")[..8]);
        var framework = Path.Combine(dir, CatalystFramework);
        var versionA = Path.Combine(framework, "Versions", "A");
        Directory.CreateDirectory(Path.Combine(versionA, "Resources"));
        File.WriteAllBytes(Path.Combine(versionA, "Kit"), image);
        File.WriteAllText(Path.Combine(versionA, "Resources", "Info.plist"), "<plist/>");
        Directory.CreateSymbolicLink(Path.Combine(framework, "Versions", "Current"), "A");
        File.CreateSymbolicLink(Path.Combine(framework, "Kit"), "Versions/Current/Kit");
        Directory.CreateSymbolicLink(Path.Combine(framework, "Resources"), "Versions/Current/Resources");
        File.WriteAllText(Path.Combine(dir, "Kit.xcframework", "Info.plist"), "<plist/>");
        var zip = Path.Combine(dir, "Acme.Kit.resources.zip");
        var start = new ProcessStartInfo("zip", links ? ["-q", "-r", "-y", zip, "Kit.xcframework"] : ["-q", "-r", zip, "Kit.xcframework"])
        {
            WorkingDirectory = dir,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
        var data = File.ReadAllBytes(zip);
        using var archive = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read);
        Assert.Equal(links, archive.Entries.Any(e => (e.ExternalAttributes >> 16 & 0xF000) == 0xA000));
        return data;
    }

    /// <summary>
    /// A zip archive of <paramref name="entries"/>, each a file, a symbolic link to <c>Link</c> as
    /// <c>zip -y</c> stores one, or (a path ending in a slash) a folder.
    /// </summary>
    private static byte[] ZipWithLinks(params (string Path, byte[]? Data, string? Link)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (path, data, link) in entries)
            {
                var entry = zip.CreateEntry(path);
                if (path.EndsWith('/'))
                    continue;
                if (link is not null)
                    entry.ExternalAttributes = unchecked((int)(0xA1EDu << 16)); // S_IFLNK | 0755
                using var stream = entry.Open();
                stream.Write(link is null ? data! : Encoding.UTF8.GetBytes(link));
            }
        return buffer.ToArray();
    }
}
