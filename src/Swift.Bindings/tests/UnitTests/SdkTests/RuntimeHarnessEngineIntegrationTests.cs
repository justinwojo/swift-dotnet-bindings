// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>Guards the ProjectReference hosts' explicit production-target integration.</summary>
public class RuntimeHarnessEngineIntegrationTests
{
    [Theory]
    [InlineData("RuntimeTestsApp")]
    [InlineData("RuntimeTestsApp.Mac")]
    [InlineData("RuntimeTestsApp.MacCatalyst")]
    [InlineData("RuntimeTestsApp.tvOS")]
    public void RuntimeHost_UsesProductionEngineHookAndHarnessNativeInjection(string app)
    {
        string root = FindRepoRoot();
        string appDir = Path.Combine(root, "BindingTests", app);
        var project = XDocument.Load(Path.Combine(appDir, app + ".csproj"));
        string target = Path.GetFullPath(Path.Combine(root, "src/Swift.Runtime/src/build/SwiftBindings.Runtime.targets"));
        var import = Assert.Single(project.Descendants("Import"), element =>
            ((string)element.Attribute("Project") ?? "").EndsWith("/SwiftBindings.Runtime.targets", StringComparison.Ordinal));
        Assert.Equal(target, Path.GetFullPath(Path.Combine(appDir, (string)import.Attribute("Project"))));
        Assert.Null(import.Attribute("Condition"));
        Assert.Contains(project.Descendants("IncludeSwiftBindingsRuntimeNative"), element => element.Value == "false");
        Assert.DoesNotContain(project.Descendants("Target"), element =>
            ((string)element.Attribute("Name") ?? "").Contains("InjectRuntimeFlavorSwitch", StringComparison.Ordinal));
        Assert.DoesNotContain(project.Descendants("RuntimeHostConfigurationOption"), element =>
            ((string)element.Attribute("Include") ?? "").StartsWith("Swift.Runtime.", StringComparison.Ordinal));
        // Each app must compile the live configuration and skip-selection controls.
        if (app != "RuntimeTestsApp")
            Assert.Contains(project.Descendants("Compile"), element =>
                (string)element.Attribute("Include") == "../RuntimeTestsApp/Metadata/**/*.cs");
    }

    private static string FindRepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "BindingTests/RuntimeTestsApp/RuntimeTestsApp.csproj")))
                return directory.FullName;
        throw new InvalidOperationException("Could not locate runtime test apps.");
    }
}
