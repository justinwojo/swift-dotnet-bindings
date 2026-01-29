// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for ModuleHandler functionality.
/// </summary>
public class ModuleHandlerTests
{
    #region Swift Import Emission Tests

    [Fact]
    public void EmitSwiftImports_AlwaysImportsModuleName()
    {
        var (_, swiftOutput) = EmitModuleWithDependencies("TestModule", new List<string>());

        Assert.Contains("import TestModule", swiftOutput);
    }

    [Fact]
    public void EmitSwiftImports_AlwaysImportsFoundation()
    {
        var (_, swiftOutput) = EmitModuleWithDependencies("TestModule", new List<string>());

        Assert.Contains("import Foundation", swiftOutput);
    }

    [Fact]
    public void EmitSwiftImports_ImportsUIKitWhenInDependencies()
    {
        var (_, swiftOutput) = EmitModuleWithDependencies("TestModule", new List<string> { "UIKit" });

        Assert.Contains("import UIKit", swiftOutput);
    }

    [Fact]
    public void EmitSwiftImports_ImportsAppKitWhenInDependencies()
    {
        var (_, swiftOutput) = EmitModuleWithDependencies("TestModule", new List<string> { "AppKit" });

        Assert.Contains("import AppKit", swiftOutput);
    }

    [Fact]
    public void EmitSwiftImports_DoesNotImportOtherDependencies()
    {
        var (_, swiftOutput) = EmitModuleWithDependencies("TestModule", new List<string> { "CoreGraphics", "AVFoundation" });

        Assert.DoesNotContain("import CoreGraphics", swiftOutput);
        Assert.DoesNotContain("import AVFoundation", swiftOutput);
    }

    [Fact]
    public void EmitSwiftImports_ImportsAreAtTopOfFile()
    {
        var (_, swiftOutput) = EmitModuleWithDependencies("TestModule", new List<string> { "UIKit" });

        // Imports should be near the beginning of the output
        var importTestModuleIndex = swiftOutput.IndexOf("import TestModule");
        var importFoundationIndex = swiftOutput.IndexOf("import Foundation");
        var importUIKitIndex = swiftOutput.IndexOf("import UIKit");

        Assert.True(importTestModuleIndex >= 0, "import TestModule not found");
        Assert.True(importFoundationIndex >= 0, "import Foundation not found");
        Assert.True(importUIKitIndex >= 0, "import UIKit not found");

        // All imports should be near the top (first 200 characters)
        Assert.True(importTestModuleIndex < 200, "import TestModule should be near top of file");
        Assert.True(importFoundationIndex < 200, "import Foundation should be near top of file");
        Assert.True(importUIKitIndex < 200, "import UIKit should be near top of file");
    }

    #endregion

    #region Helper Methods

    private static (string csOutput, string swiftOutput) EmitModuleWithDependencies(string moduleName, List<string> dependencies)
    {
        var moduleDecl = new ModuleDecl
        {
            Name = moduleName,
            Dependencies = dependencies,
            Types = new List<TypeDecl>(),
            Methods = new List<MethodDecl>(),
            Properties = new List<PropertyDecl>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };

        var typeDatabase = new TypeDatabase();
        var module = new ModuleTypeDatabase(moduleName, "/fake/path");
        typeDatabase.AddModuleDatabase(module);

        var csStringWriter = new StringWriter();
        var swiftStringWriter = new StringWriter();
        var csWriter = new CSharpWriter(csStringWriter);
        var swiftWriter = new SwiftWriter(swiftStringWriter);

        var handler = new ModuleHandler(new Microsoft.Extensions.Logging.Abstractions.NullLogger<ModuleHandler>());
        var env = handler.Marshal(moduleDecl, typeDatabase);

        // Create a minimal conductor for the test
        var loggerFactory = new Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory();
        var conductor = new Conductor(loggerFactory);

        handler.Emit(csWriter, swiftWriter, env, conductor);

        return (csStringWriter.ToString(), swiftStringWriter.ToString());
    }

    #endregion
}
