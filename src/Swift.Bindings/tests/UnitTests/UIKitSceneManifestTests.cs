// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Apps built with the iOS or Mac Catalyst 27 SDK must adopt the UIKit scene lifecycle: the
/// Apple workloads fail the build with MT7199 when the app's Info.plist has no
/// <c>UIApplicationSceneManifest</c>, and the OS refuses to launch such an app on 27.0 or later.
/// The Xcode 26 lanes do not validate this, so these checks hold every checked-in UIKit-family
/// test host to the same contract on every toolchain: a manifest is declared, every scene
/// delegate it names is registered with Objective-C, and a host that declares no scene
/// configuration never starts UIKit's application lifecycle.
/// </summary>
public class UIKitSceneManifestTests
{
    public static IEnumerable<object[]> UIKitAppProjects()
    {
        var data = new List<object[]>();
        foreach (var project in Directory.GetFiles(Path.Combine(RepoRoot, "BindingTests"), "*.csproj", SearchOption.AllDirectories)
                     .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                                    !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") &&
                                    !path.Contains($"{Path.DirectorySeparatorChar}output{Path.DirectorySeparatorChar}"))
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            var document = XDocument.Load(project);
            var isApp = document.Descendants("OutputType").Any(e => e.Value == "Exe");
            var isUIKit = document.Descendants("TargetFramework")
                .Any(e => Regex.IsMatch(e.Value, @"-(ios|tvos|maccatalyst)[0-9.]*$", RegexOptions.IgnoreCase));
            if (isApp && isUIKit)
                data.Add(new object[] { Path.GetRelativePath(RepoRoot, project) });
        }
        return data;
    }

    [Fact]
    public void Discovery_FindsEveryUIKitFamilyHost()
    {
        // Positive control: an empty discovery would pass the theory below vacuously.
        var projects = UIKitAppProjects().Select(row => (string)row[0]).ToArray();
        Assert.Contains(Path.Combine("BindingTests", "RuntimeTestsApp", "RuntimeTestsApp.csproj"), projects);
        Assert.Contains(Path.Combine("BindingTests", "RuntimeTestsApp.tvOS", "RuntimeTestsApp.tvOS.csproj"), projects);
        Assert.Contains(Path.Combine("BindingTests", "RuntimeTestsApp.MacCatalyst", "RuntimeTestsApp.MacCatalyst.csproj"), projects);
    }

    [Theory]
    [MemberData(nameof(UIKitAppProjects))]
    public void UIKitFamilyHost_DeclaresASceneManifestMatchingItsLifecycle(string relativeProject)
    {
        var project = Path.Combine(RepoRoot, relativeProject);
        var directory = Path.GetDirectoryName(project)!;
        var plistPath = Path.Combine(directory, "Info.plist");
        Assert.True(File.Exists(plistPath), $"{relativeProject} has no Info.plist to carry UIApplicationSceneManifest.");
        Assert.Contains(XDocument.Load(project).Descendants("None"),
            item => (string?)item.Attribute("Include") == "Info.plist");

        var manifest = DictionaryValue(RootDictionary(plistPath), "UIApplicationSceneManifest");
        Assert.True(manifest != null, $"{plistPath} does not declare a UIApplicationSceneManifest dictionary.");

        var source = string.Join("\n", Directory.GetFiles(directory, "*.cs", SearchOption.TopDirectoryOnly).Select(File.ReadAllText));
        var delegates = manifest!.Descendants("key").Where(key => key.Value == "UISceneDelegateClassName")
            .Select(key => key.ElementsAfterSelf().First().Value).ToArray();
        foreach (var name in delegates)
        {
            Assert.True(Regex.IsMatch(source, $@"\[Register\(""{Regex.Escape(name)}""\)\]\s*public\s+(sealed\s+)?class\s+\w+\s*:\s*UIWindowSceneDelegate"),
                $"{relativeProject} names scene delegate '{name}' but registers no UIWindowSceneDelegate under that name.");
        }

        var startsUIKit = source.Contains("UIApplication.Main(", StringComparison.Ordinal);
        if (startsUIKit)
            Assert.True(delegates.Length > 0, $"{relativeProject} starts UIApplication.Main but its manifest names no scene delegate.");
        else
            Assert.True(delegates.Length == 0, $"{relativeProject} never starts UIApplication.Main, so a declared scene delegate would never run.");
    }

    [Fact]
    public void StoreKitSandboxControl_UsesTheSceneLifecycle()
    {
        // The pure-Swift control is compiled with swiftc and bundled by hand, so no workload
        // validates it; it must still launch on an iOS 27 simulator.
        var directory = Path.Combine(RepoRoot, "BindingTests", "StoreKitSandboxControl");
        var manifest = DictionaryValue(RootDictionary(Path.Combine(directory, "Info.plist")), "UIApplicationSceneManifest");
        Assert.NotNull(manifest);
        var delegates = manifest!.Descendants("key").Where(key => key.Value == "UISceneDelegateClassName")
            .Select(key => key.ElementsAfterSelf().First().Value).ToArray();
        Assert.Equal(new[] { "SceneDelegate" }, delegates);
        var source = File.ReadAllText(Path.Combine(directory, "App.swift"));
        Assert.Matches(@"@objc\(SceneDelegate\)\s*final class \w+: UIResponder, UIWindowSceneDelegate", source);
    }

    private static XElement RootDictionary(string plistPath)
    {
        // Apple plists carry a DOCTYPE; ignore it rather than resolving the remote DTD.
        using var reader = XmlReader.Create(plistPath, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore });
        return XDocument.Load(reader).Root!.Element("dict")!;
    }

    private static XElement? DictionaryValue(XElement dictionary, string key)
    {
        var keyElement = dictionary.Elements("key").FirstOrDefault(element => element.Value == key);
        return keyElement?.ElementsAfterSelf().FirstOrDefault() is { Name.LocalName: "dict" } value ? value : null;
    }

    private static string RepoRoot { get; } = LocateRepoRoot();

    private static string LocateRepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "BindingTests", "RuntimeTestsApp", "RuntimeTestsApp.csproj")))
                return directory.FullName;
        throw new InvalidOperationException("Could not locate the repository root from the test output directory.");
    }
}
