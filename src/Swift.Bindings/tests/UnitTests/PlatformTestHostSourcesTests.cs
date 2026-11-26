// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// The tvOS, Mac Catalyst and macOS test hosts compile the shared runtime tests from
/// <c>RuntimeTestsApp</c> by listing them, and list each shared helper under
/// <c>Infrastructure/</c> one by one. A helper added for a shared test but not listed in a host
/// breaks only that host's app build, which the iOS lanes never see. These checks fail when a host
/// compiles a test that uses a helper type the host does not compile.
/// </summary>
public class PlatformTestHostSourcesTests
{
    private static readonly string[] PlatformHosts =
    {
        Path.Combine("BindingTests", "RuntimeTestsApp.MacCatalyst", "RuntimeTestsApp.MacCatalyst.csproj"),
        Path.Combine("BindingTests", "RuntimeTestsApp.tvOS", "RuntimeTestsApp.tvOS.csproj"),
        Path.Combine("BindingTests", "RuntimeTestsApp.Mac", "RuntimeTestsApp.Mac.csproj"),
    };

    public static IEnumerable<object[]> Hosts() => PlatformHosts.Select(host => new object[] { host });

    [Theory]
    [MemberData(nameof(Hosts))]
    public void Host_CompilesEverySharedHelperItsTestsUse(string relativeProject)
    {
        var compiled = CompiledSources(Path.Combine(RepoRoot, relativeProject));
        Assert.NotEmpty(compiled);

        var missing = MissingHelpers(compiled);
        Assert.True(missing.Count == 0,
            $"{relativeProject} compiles tests that use shared helpers it does not compile:\n" +
            string.Join("\n", missing.Select(m => $"  {m.Helper} (type {m.Type}), used by {m.UsedBy}")));
    }

    [Fact]
    public void MissingHelpers_ReportsAHelperLeftOutOfAHost()
    {
        // Negative control: drop one helper from a real host's compile set; the check must name it.
        var compiled = CompiledSources(Path.Combine(RepoRoot, PlatformHosts[0]));
        var helper = Path.Combine(InfrastructureDirectory, "NameKeyedCase.cs");
        Assert.Contains(helper, compiled);

        var missing = MissingHelpers(compiled.Where(path => path != helper).ToHashSet(StringComparer.Ordinal));
        Assert.Contains(missing, m => m.Helper == "NameKeyedCase.cs" && m.Type == "NameKeyedCase");
    }

    private sealed record MissingHelper(string Helper, string Type, string UsedBy);

    private static List<MissingHelper> MissingHelpers(IReadOnlySet<string> compiled)
    {
        var compiledText = compiled
            .Where(path => !path.StartsWith(InfrastructureDirectory + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .Select(path => (Path: path, Names: IdentifierNames(path)))
            .ToList();

        var missing = new List<MissingHelper>();
        foreach (var helper in Directory.GetFiles(InfrastructureDirectory, "*.cs").OrderBy(p => p, StringComparer.Ordinal))
        {
            if (compiled.Contains(helper))
                continue;
            foreach (var type in TopLevelTypeNames(helper))
            {
                var user = compiledText.FirstOrDefault(file => file.Names.Contains(type));
                if (user.Path != null)
                    missing.Add(new MissingHelper(Path.GetFileName(helper), type, Path.GetRelativePath(RepoRoot, user.Path)));
            }
        }
        return missing;
    }

    private static HashSet<string> CompiledSources(string project)
    {
        var directory = Path.GetDirectoryName(project)!;
        var files = new HashSet<string>(StringComparer.Ordinal);
        foreach (var include in XDocument.Load(project).Descendants("Compile").Select(e => (string?)e.Attribute("Include")))
        {
            if (string.IsNullOrEmpty(include))
                continue;
            var pattern = include.Replace('\\', '/');
            var star = pattern.IndexOf('*');
            if (star < 0)
            {
                var file = Path.GetFullPath(Path.Combine(directory, pattern));
                if (File.Exists(file))
                    files.Add(file);
                continue;
            }
            // The hosts use "<dir>/**/*.cs" and "<dir>/*.cs"; anything else must be taught here, not guessed.
            var root = Path.GetFullPath(Path.Combine(directory, pattern[..star].TrimEnd('/')));
            var rest = pattern[star..];
            var option = rest switch
            {
                "**/*.cs" => SearchOption.AllDirectories,
                "*.cs" => SearchOption.TopDirectoryOnly,
                _ => throw new InvalidOperationException($"{project}: unrecognized Compile glob '{include}'."),
            };
            if (Directory.Exists(root))
                foreach (var file in Directory.GetFiles(root, "*.cs", option))
                    files.Add(Path.GetFullPath(file));
        }
        return files;
    }

    private static HashSet<string> TopLevelTypeNames(string file) =>
        Parse(file).GetCompilationUnitRoot().DescendantNodes()
            .OfType<BaseTypeDeclarationSyntax>()
            .Where(type => type.Parent is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax)
            .Select(type => type.Identifier.ValueText)
            .ToHashSet(StringComparer.Ordinal);

    private static HashSet<string> IdentifierNames(string file) =>
        Parse(file).GetRoot().DescendantTokens()
            .Where(token => token.IsKind(SyntaxKind.IdentifierToken))
            .Select(token => token.ValueText)
            .ToHashSet(StringComparer.Ordinal);

    private static SyntaxTree Parse(string file) => CSharpSyntaxTree.ParseText(File.ReadAllText(file), path: file);

    private static string InfrastructureDirectory =>
        Path.GetFullPath(Path.Combine(RepoRoot, "BindingTests", "RuntimeTestsApp", "Infrastructure"));

    private static string RepoRoot { get; } = LocateRepoRoot();

    private static string LocateRepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "BindingTests", "RuntimeTestsApp", "RuntimeTestsApp.csproj")))
                return directory.FullName;
        throw new InvalidOperationException("Could not locate the repository root from the test output directory.");
    }
}
