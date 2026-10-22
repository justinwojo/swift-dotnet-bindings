// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests;

public class SwiftCompilerSettingsTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ModuleCompilation_DefaultsToExplicitSwift5(bool emitInterface, bool interfacePath)
    {
        var settings = new SwiftCompilerSettings().SetEmitModule();
        if (emitInterface)
            settings.SetEmitModuleInterface();
        if (interfacePath)
            settings.SetModuleInterfacePath("Fixture.swiftinterface");

        Assert.Equal("5", settings.SwiftVersion);
        Assert.Contains("-swift-version 5", settings.BuildArguments());
    }

    [Fact]
    public void LibraryCompilation_PreservesSwift5WithMinimalConcurrencyChecking()
    {
        var settings = new SwiftCompilerSettings()
            .SetEmitLibrary()
            .SetStrictConcurrency("minimal")
            .AddSourceFile("Fixture.swift");

        var arguments = settings.BuildArguments();
        Assert.Contains("-swift-version 5", arguments);
        Assert.Contains("-strict-concurrency=minimal", arguments);
    }

    [Fact]
    public void ExplicitLanguageVersion_OverridesDefaultWithoutConflictingMode()
    {
        var settings = new SwiftCompilerSettings()
            .SetEmitModule()
            .SetEmitModuleInterface()
            .SetSwiftVersion("6");

        var arguments = settings.BuildArguments();
        Assert.Equal("6", settings.SwiftVersion);
        Assert.Contains("-swift-version 6", arguments);
        Assert.DoesNotContain("-swift-version 5", arguments);
        Assert.Equal(arguments.IndexOf("-swift-version", System.StringComparison.Ordinal),
            arguments.LastIndexOf("-swift-version", System.StringComparison.Ordinal));
    }
}
