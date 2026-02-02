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
    public void EmitSwiftImports_ImportsAppleFrameworksWhenInDependencies()
    {
        var (_, swiftOutput) = EmitModuleWithDependencies("TestModule", new List<string> { "CoreGraphics", "AVFoundation" });

        Assert.Contains("import CoreGraphics", swiftOutput);
        Assert.Contains("import AVFoundation", swiftOutput);
    }

    [Fact]
    public void EmitSwiftImports_DoesNotImportUnknownDependencies()
    {
        // Dependencies not in the known Apple frameworks list should not be imported
        var (_, swiftOutput) = EmitModuleWithDependencies("TestModule", new List<string> { "SomePrivateFramework", "ThirdPartyLib" });

        Assert.DoesNotContain("import SomePrivateFramework", swiftOutput);
        Assert.DoesNotContain("import ThirdPartyLib", swiftOutput);
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

    [Fact]
    public void EmitSwiftImports_ImportsFrameworkForAsyncMethodReturnType()
    {
        var (_, swiftOutput) = EmitModuleWithDependencies(
            "TestModule",
            new List<string>(),
            moduleDecl =>
            {
                var asyncType = new ClassDecl
                {
                    Name = "ImageLoader",
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.ImageLoader"),
                    MangledName = "$s10TestModule11ImageLoaderCN",
                    Properties = new List<PropertyDecl>(),
                    Methods = new List<MethodDecl>(),
                    Types = new List<TypeDecl>(),
                    Operators = new List<OperatorDecl>(),
                    Subscripts = new List<SubscriptDecl>(),
                    GenericParameters = new List<GenericArgumentDecl>(),
                    Conformances = new List<TypeConformance>(),
                    ParentDecl = moduleDecl,
                    ModuleDecl = moduleDecl
                };
                asyncType.Methods.Add(new MethodDecl
                {
                    Name = "fetchImage",
                    MangledName = "$s10TestModule11ImageLoaderC10fetchImageSo7UIImageCyYaF",
                    MethodType = MethodType.Instance,
                    IsConstructor = false,
                    CSSignature = new List<ArgumentDecl>
                    {
                        new()
                        {
                            SwiftTypeSpec = new NamedTypeSpec("UIKit.UIImage"),
                            Name = string.Empty,
                            PrivateName = string.Empty,
                            IsInOut = false,
                            IsGeneric = false,
                            ParentDecl = null,
                            ModuleDecl = moduleDecl
                        }
                    },
                    Throws = false,
                    IsAsync = true,
                    GenericParameters = new List<GenericArgumentDecl>(),
                    Visibility = Visibility.Public,
                    ParentDecl = asyncType,
                    ModuleDecl = moduleDecl
                });
                moduleDecl.Types.Add(asyncType);
            });

        Assert.Contains("import UIKit", swiftOutput);
    }

    [Fact]
    public void EmitSwiftImports_ImportsFrameworkForProtocolSignatures()
    {
        var (_, swiftOutput) = EmitModuleWithDependencies(
            "TestModule",
            new List<string>(),
            moduleDecl =>
            {
                moduleDecl.Protocols.Add(new ProtocolDecl
                {
                    Name = "Renderer",
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Renderer"),
                    MangledName = "$s10TestModule8RendererP",
                    Types = new List<TypeDecl>(),
                    Operators = new List<OperatorDecl>(),
                    GenericSignature = null,
                    AssociatedTypes = new List<AssociatedTypeDecl>(),
                    InheritedProtocols = new List<NamedTypeSpec>(),
                    IsClassBound = false,
                    HasSelfRequirement = false,
                    Properties = new List<PropertyDecl>
                    {
                        new()
                        {
                            Name = "layer",
                            SwiftTypeSpec = new NamedTypeSpec("QuartzCore.CALayer"),
                            IsStatic = false,
                            HasStorage = false,
                            Accessors = new List<AccessorDecl>(),
                            ParentDecl = null,
                            ModuleDecl = moduleDecl
                        }
                    },
                    Methods = new List<MethodDecl>(),
                    Subscripts = new List<SubscriptDecl>(),
                    ParentDecl = moduleDecl,
                    ModuleDecl = moduleDecl
                });
            });

        Assert.Contains("import QuartzCore", swiftOutput);
    }

    [Fact]
    public void EmitSwiftImports_DeduplicatesAndSortsFrameworkImports()
    {
        var (_, swiftOutput) = EmitModuleWithDependencies(
            "TestModule",
            new List<string> { "UIKit", "CoreGraphics", "UIKit" },
            moduleDecl =>
            {
                var protocol = new ProtocolDecl
                {
                    Name = "Painter",
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Painter"),
                    MangledName = "$s10TestModule7PainterP",
                    Types = new List<TypeDecl>(),
                    Operators = new List<OperatorDecl>(),
                    GenericSignature = null,
                    AssociatedTypes = new List<AssociatedTypeDecl>(),
                    InheritedProtocols = new List<NamedTypeSpec>(),
                    IsClassBound = false,
                    HasSelfRequirement = false,
                    Properties = new List<PropertyDecl>(),
                    Methods = new List<MethodDecl>
                    {
                        new()
                        {
                            Name = "draw",
                            MangledName = "$s10TestModule7PainterP4drawyy10CoreImage7CIImageVF",
                            MethodType = MethodType.Instance,
                            IsConstructor = false,
                            CSSignature = new List<ArgumentDecl>
                            {
                                new()
                                {
                                    SwiftTypeSpec = new TupleTypeSpec(new List<TypeSpec>()),
                                    Name = string.Empty,
                                    PrivateName = string.Empty,
                                    IsInOut = false,
                                    IsGeneric = false,
                                    ParentDecl = null,
                                    ModuleDecl = moduleDecl
                                },
                                new()
                                {
                                    SwiftTypeSpec = new NamedTypeSpec("CoreImage.CIImage"),
                                    Name = "image",
                                    PrivateName = "image",
                                    IsInOut = false,
                                    IsGeneric = false,
                                    ParentDecl = null,
                                    ModuleDecl = moduleDecl
                                }
                            },
                            Throws = false,
                            IsAsync = false,
                            GenericParameters = new List<GenericArgumentDecl>(),
                            Visibility = Visibility.Public,
                            ParentDecl = null,
                            ModuleDecl = moduleDecl
                        }
                    },
                    Subscripts = new List<SubscriptDecl>(),
                    ParentDecl = moduleDecl,
                    ModuleDecl = moduleDecl
                };
                moduleDecl.Protocols.Add(protocol);
            });

        Assert.Equal(1, EmitterTestHelpers.CountOccurrences(swiftOutput, "import UIKit"));
        Assert.Equal(1, EmitterTestHelpers.CountOccurrences(swiftOutput, "import CoreGraphics"));
        Assert.Equal(1, EmitterTestHelpers.CountOccurrences(swiftOutput, "import CoreImage"));
        Assert.True(swiftOutput.IndexOf("import CoreGraphics") < swiftOutput.IndexOf("import CoreImage"));
        Assert.True(swiftOutput.IndexOf("import CoreImage") < swiftOutput.IndexOf("import UIKit"));
    }

    #endregion

    #region Helper Methods

    private static (string csOutput, string swiftOutput) EmitModuleWithDependencies(
        string moduleName,
        List<string> dependencies,
        Action<ModuleDecl> customizeModule = null)
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
        customizeModule?.Invoke(moduleDecl);

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
