// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace BindingsGeneration.Tests;

public class Net11PackageHygieneTests
{
    private delegate string[] FixtureWriter(string framework, string directory);
    private static readonly Lazy<FixtureWriter> WriteFixture = new(CompileFixtureWriter);

    [Theory]
    [InlineData("net10.0", "appstore-hygiene", "0.0.0-appstorehygiene")]
    [InlineData("net11.0", "appstore-hygiene-net11", "0.0.0-appstorehygiene-net11")]
    public void SelectedFramework_DrivesEveryConsumerAndIsolatesPackageIdentity(
        string framework, string scratchName, string version)
    {
        InScratch(directory =>
        {
            var result = WriteFixture.Value(framework, directory);
            var scratch = Path.Combine(directory, "artifacts", scratchName);
            Assert.Equal(scratch, result[0]);
            Assert.Equal(version, result[1]);

            var ios = XDocument.Load(Path.Combine(scratch, "consumer", "AppStoreHygieneApp.csproj"));
            Assert.Equal(framework + "-ios", Value(ios, "TargetFramework"));
            Assert.Equal("ios-arm64", Value(ios, "RuntimeIdentifier"));
            Assert.Equal("15.0", Value(ios, "SupportedOSPlatformVersion"));
            Assert.Equal("true", Value(ios, "BuildIpa"));
            Assert.Equal("Apple Development: Justin Wojciechowski (2YJ2TQ6Q7Q)", Value(ios, "CodesignKey"));
            Assert.Equal("Swift Bindings Dev", Value(ios, "CodesignProvision"));
            AssertRuntimeReference(ios, version);

            foreach (var (platform, rid, floor) in new[]
                     {
                         ("macos", "osx-arm64", "14.0"),
                         ("maccatalyst", "maccatalyst-arm64", "17.0"),
                     })
            {
                var project = XDocument.Load(Path.Combine(scratch, "mac-anatomy-" + framework + "-" + platform,
                    "MacAnatomyApp.csproj"));
                Assert.Equal(framework + "-" + platform, Value(project, "TargetFramework"));
                Assert.Equal(rid, Value(project, "RuntimeIdentifier"));
                Assert.Equal(floor, Value(project, "SupportedOSPlatformVersion"));
                AssertRuntimeReference(project, version);
                Assert.Equal("Framework", project.Descendants("NativeReference").Single().Attribute("Kind")?.Value);
            }

            var universal = XDocument.Load(Path.Combine(scratch, "mac-anatomy-" + framework + "-macos-universal",
                "MacAnatomyApp.csproj"));
            Assert.Equal(framework + "-macos", Value(universal, "TargetFramework"));
            Assert.Equal("osx-arm64;osx-x64", Value(universal, "RuntimeIdentifiers"));
            Assert.Empty(universal.Descendants("RuntimeIdentifier"));
            AssertRuntimeReference(universal, version);
        });
    }

    [Theory]
    [InlineData("net10.0")]
    [InlineData("net11.0")]
    public void MacGate_PreservesBuildPublishRecopyAndUniversalFlows(string framework)
    {
        InScratch(directory =>
        {
            var result = WriteFixture.Value(framework, directory);
            Assert.Equal(new[]
            {
                "macOS:osx-arm64:False:False", "macOS:osx-arm64:True:False", "macOS:osx-arm64:True:True",
                "Mac Catalyst:maccatalyst-arm64:False:False", "Mac Catalyst:maccatalyst-arm64:True:False",
                "Mac Catalyst:maccatalyst-arm64:True:True", "macOS universal:osx-arm64;osx-x64:True:False",
                "macOS universal:osx-arm64;osx-x64:True:True",
            }, result.Skip(2));
        });
    }

    private static string Value(XDocument project, string name) => project.Descendants(name).Single().Value;

    private static void AssertRuntimeReference(XDocument project, string version)
    {
        var reference = project.Descendants("PackageReference").Single();
        Assert.Equal("SwiftBindings.Runtime", reference.Attribute("Include")?.Value);
        Assert.Equal(version, reference.Attribute("Version")?.Value);
    }

    private static void InScratch(Action<string> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), "package-hygiene-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { action(directory); }
        finally { Directory.Delete(directory, true); }
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Test-only production fixture methods, retained in full.")]
    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "Test-only dynamically compiled fixture entry, retained in full.")]
    private static FixtureWriter CompileFixtureWriter()
    {
        var root = LocateRepoRoot();
        var members = new[] { "Build.BindingTests.AppStoreHygiene.cs", "Build.BindingTests.AppStoreHygiene.Mac.cs" }
            .SelectMany(name => CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root, "build", name)))
                .GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>().Single(c => c.Identifier.Text == "Build")
                .Members).ToList();
        var methods = new[] { "WriteAppStoreHygieneConsumerApp", "WriteMacAnatomyConsumerApp", "RunMacFrameworkAnatomyLegs" };
        var properties = new[] { "AppStoreHygieneVersion", "AppStoreHygieneScratch" };
        var production = string.Join("\n", members.Where(m =>
            m is FieldDeclarationSyntax field && field.Modifiers.Any(SyntaxKind.ConstKeyword) ||
            m is MethodDeclarationSyntax method && methods.Contains(method.Identifier.Text) ||
            m is PropertyDeclarationSyntax property && properties.Contains(property.Identifier.Text)));
        // Execute the actual writers and Mac orchestration. Only external build/process boundaries
        // are replaced; generated project XML, package identities and the eight-flow grid are real.
        var source = $$"""
            #nullable enable
            using System;
            using System.IO;
            using System.Linq;
            using System.Collections.Generic;
            public readonly record struct AbsolutePath(string Path) {
                public static implicit operator string(AbsolutePath path) => path.Path;
                public static implicit operator AbsolutePath(string path) => new(path);
                public static AbsolutePath operator /(AbsolutePath path, string child) => new(System.IO.Path.Combine(path.Path, child));
                public void CreateDirectory() => Directory.CreateDirectory(Path);
                public void DeleteDirectory() => Directory.Delete(Path, true);
                public override string ToString() => Path;
            }
            public class HygieneFixture {
                const string AppleSupplementModuleName = "SBApple";
                string BtFramework = "";
                sealed record Lane(bool IsNet11);
                Lane RuntimeLane => new(BtFramework == "net11.0");
                AbsolutePath RootDirectory;
                static string MacAnatomyHostArch => "arm64";
                List<string> flows = new();
                static class Assert {
                    public static void True(bool value, string message) { if (!value) throw new Exception(message); }
                }
                static class Log { public static void Information(string message, params object[] args) {} }
                static string MixedPackNuGetConfig(AbsolutePath feed, AbsolutePath? fixtureNupkgDir) => "<configuration/>";
                AbsolutePath PrepareMacAnatomyDirectoryEntryFramework(AbsolutePath scratch, string contents) {
                    var path = scratch / "SBApple.xcframework";
                    path.CreateDirectory();
                    return path;
                }
                static void WriteMacAnatomyDirectoryEntryMarkers(AbsolutePath path, string contents) {}
                static void ForceMacFrameworkRecopy(AbsolutePath path) {}
                void RunMacAnatomyFlow(string platform, AbsolutePath appDir, string rid, string contents, bool publish, bool recopy = false)
                    => flows.Add($"{platform}:{rid}:{publish}:{recopy}");
                {{production}}
                public static string[] Run(string framework, string directory) {
                    var fixture = new HygieneFixture { BtFramework = framework, RootDirectory = directory };
                    var scratch = fixture.AppStoreHygieneScratch;
                    var consumer = scratch / "consumer";
                    consumer.CreateDirectory();
                    fixture.WriteAppStoreHygieneConsumerApp(consumer);
                    fixture.RunMacFrameworkAnatomyLegs(scratch, scratch / "packages");
                    return new[] { (string)scratch, fixture.AppStoreHygieneVersion }.Concat(fixture.flows).ToArray();
                }
            }
            """;
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))
            .Split(Path.PathSeparator).Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create("PackageHygiene" + Guid.NewGuid().ToString("N"),
            new[] { CSharpSyntaxTree.ParseText(source) }, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        return Assembly.Load(stream.ToArray()).GetType("HygieneFixture").GetMethod("Run").CreateDelegate<FixtureWriter>();
    }

    private static string LocateRepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "build", "Build.BindingTests.AppStoreHygiene.cs"))) return directory.FullName;
        throw new InvalidOperationException("Could not locate the production package hygiene source.");
    }
}
